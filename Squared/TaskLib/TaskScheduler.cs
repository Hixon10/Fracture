﻿using System;
using System.Collections.Generic;
using System.Threading;
using Squared.Util;
using System.Collections.Concurrent;

namespace Squared.Task {
    public interface ISchedulable {
        void Schedule (TaskScheduler scheduler, IFuture future);
    }

    public enum TaskExecutionPolicy {
        RunWhileFutureLives,
        RunAsBackgroundTask,
        Default = RunWhileFutureLives
    }

    public interface ITaskResult {
        object Value {
            get;
        }
        void CompleteFuture (IFuture f);
    }

    public class Result : ITaskResult {
        private readonly object _Value;

        public Result (object value) {
            _Value = value;
        }

        public object Value {
            get {
                return _Value;
            }
        }

        public void CompleteFuture (IFuture future) {
            future.Complete(_Value);
        }

        public static Result New (object value) {
            return new Result(value);
        }

        public static TaskResult<T> New<T> (T value) {
            return new TaskResult<T>(value);
        }

        public static TaskResult<T> New<T> (ref T value) {
            return new TaskResult<T>(ref value);
        }
    }

    public class TaskResult<T> : ITaskResult {
        private readonly T _Value;

        public TaskResult (T value) {
            _Value = value;
        }

        public TaskResult (ref T value) {
            _Value = value;
        }

        public void CompleteFuture (Future<T> future) {
            future.Complete(_Value);
        }

        void ITaskResult.CompleteFuture (IFuture future) {
            var stronglyTyped = future as Future<T>;
            if (stronglyTyped != null)
                CompleteFuture(stronglyTyped);
            else
                future.Complete(_Value);
        }

        object ITaskResult.Value {
            get {
                return _Value;
            }
        }

        T Value {
            get {
                return _Value;
            }
        }
    }

    public class TaskException : Exception {
        public TaskException (string message, Exception innerException)
            : base(message, innerException) {
        }
    }

    public class TaskYieldedValueException : Exception {
        public IEnumerator<object> Task;

        public TaskYieldedValueException (IEnumerator<object> task)
            : base("A task directly yielded a value. To yield a result from a task, yield a Result() object containing the value.") {
            Task = task;
        }
    }

    struct SleepItem : IComparable<SleepItem> {
        public long Until;
        public IFuture Future;

        public bool Tick (long now) {
            long ticksLeft = Math.Max(Until - now, 0);
            if (ticksLeft == 0) {
                try {
                    Future.Complete();
                } catch (FutureAlreadyHasResultException ex) {
                    if (ex.Future != Future)
                        throw;
                }

                return true;
            } else {
                return false;
            }
        }

        public int CompareTo (SleepItem rhs) {
            if (Future == null) {
                if (rhs.Future == null)
                    return 0;
                else
                    return 1;
            } else if (rhs.Future == null)
                return -1;
            else
                return Until.CompareTo(rhs.Until);
        }
    }

    public delegate bool BackgroundTaskErrorHandler (Exception error);

    public class TaskScheduler : IDisposable {
        public static int SleepThreadTimeoutMs = 10000;

        const long SleepFudgeFactor = 100;
        const long SleepSpinThreshold = 1000;
        const long MinimumSleepLength = 10000;
        const long MaximumSleepLength = Time.SecondInTicks * 60;

        public BackgroundTaskErrorHandler ErrorHandler = null;

        private bool _IsDisposed = false;
        private IJobQueue _JobQueue = null;
        private ConcurrentQueue<Action> _StepListeners = new ConcurrentQueue<Action>();
        private Internal.WorkerThread<PriorityQueue<SleepItem>> _SleepWorker;

        public TaskScheduler (Func<IJobQueue> JobQueueFactory) {
            _JobQueue = JobQueueFactory();
            _SleepWorker = new Internal.WorkerThread<PriorityQueue<SleepItem>>(
                SleepWorkerThreadFunc, ThreadPriority.AboveNormal, "TaskScheduler Sleep Provider"
            );
        }

        public TaskScheduler ()
            : this(JobQueue.ThreadSafe) {
        }

        public bool WaitForWorkItems (double timeout = 0) {
            if (_IsDisposed)
                throw new ObjectDisposedException("TaskScheduler");

            return _JobQueue.WaitForWorkItems(timeout);
        }

        private void BackgroundTaskOnComplete (IFuture f) {
            var e = f.Error;
            if (e != null)
                OnTaskError(e);
        }

        public void OnTaskError (Exception exception) {
            if (_IsDisposed)
                throw new TaskException("Unhandled exception in background task", exception);

            this.QueueWorkItem(() => {
                if (ErrorHandler != null)
                    if (ErrorHandler(exception))
                        return;

                throw new TaskException("Unhandled exception in background task", exception);
            });
        }

        public void Start (IFuture future, ISchedulable task, TaskExecutionPolicy executionPolicy) {
            task.Schedule(this, future);

            switch (executionPolicy) {
                case TaskExecutionPolicy.RunAsBackgroundTask:
                    future.RegisterOnComplete(BackgroundTaskOnComplete);
                break;
                default:
                break;
            }
        }

        public IFuture Start (ISchedulable task, TaskExecutionPolicy executionPolicy = TaskExecutionPolicy.Default) {
            var future = new Future<object>();
            Start(future, task, executionPolicy);
            return future;
        }

        public IFuture Start (IEnumerator<object> task, TaskExecutionPolicy executionPolicy = TaskExecutionPolicy.Default) {
            return Start(new SchedulableGeneratorThunk(task), executionPolicy);
        }

        public void QueueWorkItem (Action workItem) {
            if (_IsDisposed)
                throw new ObjectDisposedException("TaskScheduler");

            _JobQueue.QueueWorkItem(workItem);
        }

        internal void AddStepListener (Action listener) {
            if (_IsDisposed)
                return;

            _StepListeners.Enqueue(listener);
        }

        internal static void SleepWorkerThreadFunc (PriorityQueue<SleepItem> pendingSleeps, ManualResetEventSlim newSleepEvent) {
            while (true) {
                long now = Time.Ticks;

                SleepItem currentSleep;
                Monitor.Enter(pendingSleeps);
                if (pendingSleeps.Peek(out currentSleep)) {
                    if (currentSleep.Tick(now)) {
                        pendingSleeps.Dequeue();
                        Monitor.Exit(pendingSleeps);
                        continue;
                    } else {
                        Monitor.Exit(pendingSleeps);
                    }
                } else {
                    Monitor.Exit(pendingSleeps);

                    if (!newSleepEvent.Wait(SleepThreadTimeoutMs))
                        return;

                    newSleepEvent.Reset();
                    continue;
                }

                long sleepUntil = currentSleep.Until;

                now = Time.Ticks;
                long timeToSleep = (sleepUntil - now) + SleepFudgeFactor;

#if !XBOX
                if (timeToSleep < SleepSpinThreshold) {
                    int iteration = 1;

                    while (Time.Ticks < sleepUntil) {
                        Thread.SpinWait(20 * iteration);
                        iteration += 1;
                    }

                    timeToSleep = 0;
                }
#endif

                if (timeToSleep > 0) {
                    if (timeToSleep > MaximumSleepLength)
                        timeToSleep = MaximumSleepLength;

                    int msToSleep = 0;
                    if (timeToSleep >= MinimumSleepLength) {
                        msToSleep = (int)(timeToSleep / Time.MillisecondInTicks);
                    }

                    if (newSleepEvent != null) {
                        newSleepEvent.Reset();
                        newSleepEvent.Wait(msToSleep);
                    }
                }
            }
        }

        internal void QueueSleep (long completeWhen, IFuture future) {
            if (_IsDisposed)
                return;

            long now = Time.Ticks;
            if (now > completeWhen) {
                future.Complete();
                return;
            }

            SleepItem sleep = new SleepItem { Until = completeWhen, Future = future };

            lock (_SleepWorker.WorkItems)
                _SleepWorker.WorkItems.Enqueue(sleep);
            _SleepWorker.Wake();
        }

        internal void BeforeStep () {
            Action item;

            while (_StepListeners.TryDequeue(out item))
                item();
        }

        public void Step () {
            if (_IsDisposed)
                return;

            BeforeStep();

            _JobQueue.Step();
        }

        public object WaitFor (ISchedulable schedulable) {
            var f = Start(schedulable, TaskExecutionPolicy.RunWhileFutureLives);
            return WaitFor(f);
        }

        public object WaitFor (IEnumerator<object> task) {
            var f = Start(task, TaskExecutionPolicy.RunWhileFutureLives);
            return WaitFor(f);
        }

        public T WaitFor<T> (Future<T> future) {
            if (_IsDisposed)
                throw new ObjectDisposedException("TaskScheduler");

            while (true) {
                BeforeStep();

                if (_JobQueue.WaitForFuture(future))
                    return future.Result;
            }
        }

        public object WaitFor (IFuture future) {
            if (_IsDisposed)
                throw new ObjectDisposedException("TaskScheduler");

            while (true) {
                BeforeStep();

                if (_JobQueue.WaitForFuture(future))
                    return future.Result;
            }
        }

        public bool HasPendingTasks {
            get {
                return (_StepListeners.Count > 0) || (_JobQueue.Count > 0);
            }
        }

        public bool IsDisposed {
            get {
                return _IsDisposed;
            }
        }

        public void Dispose () {
            if (_IsDisposed)
                return;

            _IsDisposed = true;
            Thread.MemoryBarrier();

            lock (_SleepWorker.WorkItems) {
                while (_SleepWorker.WorkItems.Count > 0) {
                    var item = _SleepWorker.WorkItems.Dequeue();

                    try {
                        item.Future.Dispose();
                    } catch (FutureHandlerException fhe) {
                        // FIXME: Maybe we should introduce two levels of disposed state, and in the first level,
                        //  queueing work items silently fails?
                        if (!(fhe.InnerException is ObjectDisposedException))
                            throw;
                    }
                }
            }
                
            _SleepWorker.Dispose();
            _JobQueue.Dispose();
        }
    }
}
