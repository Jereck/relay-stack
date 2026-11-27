using RelayStack.Worker;

var host = WorkerApp.Build(args);
await host.RunAsync();
