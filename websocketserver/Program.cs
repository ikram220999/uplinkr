using WebSocketBridge;

Logger.Init();
Logger.Info("bridge starting");

var listenPrefix = Environment.GetEnvironmentVariable("BRIDGE_LISTEN_PREFIX") ?? "http://localhost:4001/";
var server = new BridgeServer(listenPrefix);
await server.RunAsync();