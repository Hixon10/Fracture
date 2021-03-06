﻿using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using Squared.Task;

namespace MUDServer {
    public static class Program {
        public static Random RNG = new Random();
        public static TaskScheduler Scheduler;
        public static TelnetServer Server;

        static void Main (string[] args) {
            Scheduler = new TaskScheduler();

            World.Create();
            Event.Broadcast(new { Type = EventType.WorldConstructed });

            Server = new TelnetServer(Scheduler, System.Net.IPAddress.Any, 23);
            Scheduler.Start(HandleNewClients(), TaskExecutionPolicy.RunAsBackgroundTask);

            while (true) {
                Scheduler.Step();
                Scheduler.WaitForWorkItems();
            }
        }

        static IEnumerator<object> HandleNewClients () {
            while (true) {
                IFuture f = Server.AcceptNewClient();
                using (f)
                    yield return f;

                TelnetClient client = f.Result as TelnetClient;
                Player player = new Player(client, World.PlayerStartLocation);
            }
        }
    }
}
