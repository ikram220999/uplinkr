using WebSocketBridge;

Logger.Init();
Logger.Info("bridge starting");

var listenPrefix = Environment.GetEnvironmentVariable("BRIDGE_LISTEN_PREFIX") ?? "http://127.0.0.1:4001/";
var server = new BridgeServer(listenPrefix);
await server.RunAsync();