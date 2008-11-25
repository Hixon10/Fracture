﻿using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading;

namespace Squared.Util {
    // Thanks to Herb Sutter
    // http://www.ddj.com/cpp/211601363

    public class AtomicQueue<T> {
        internal class Node {
            public Node (T value) {
                Value = value;
                Next = null;
            }

            public T Value;
            public volatile Node Next;
        }

        Node _Head;
        Node _Tail;

        int _ConsumerLock;
        int _ProducerLock;

        int _Count;

        public AtomicQueue () {
            _Head = _Tail = new Node(default(T));
            _ConsumerLock = _ProducerLock = 0;
            _Count = 0;
        }

        public int GetCount () {
            return _Count;
        }

        public void Enqueue (T value) {
            var temp = new Node(value);

            int iterations = 1;
            while (Interlocked.CompareExchange(ref _ProducerLock, 1, 0) != 0) {
                SpinWait(iterations++);
            }

            _Tail.Next = temp;
            _Tail = temp;

            Interlocked.Increment(ref _Count);

            var x = Interlocked.Exchange(ref _ProducerLock, 0);
            if (x != 1)
                throw new ThreadStateException();
        }

        public bool Dequeue (out T result) {
            int iterations = 1;
            while (Interlocked.CompareExchange(ref _ConsumerLock, 1, 0) != 0) {
                SpinWait(iterations++);
            }

            bool success = false;

            var head = _Head;
            var next = head.Next;
            if (next != null) {
                result = next.Value;
                _Head = next;
                success = true;
                Interlocked.Decrement(ref _Count);
            } else {
                result = default(T);
            }

            var x = Interlocked.Exchange(ref _ConsumerLock, 0);
            if (x != 1)
                throw new ThreadStateException();

            return success;
        }

        private void SpinWait (int iterationCount) {
#if !XBOX
            if ((iterationCount < 5) && (Environment.ProcessorCount > 1)) {
                Thread.SpinWait(10 * iterationCount);
            } else if (iterationCount < 8) {
#else
            if (iterationCount < 3) {
#endif
                Thread.Sleep(0);
            } else {
                Thread.Sleep(1);
            }
        }
    }
}