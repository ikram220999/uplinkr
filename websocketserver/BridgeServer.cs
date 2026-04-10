using System.Collections.Concurrent;
using System.Net;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace WebSocketBridge;

internal sealed class BridgeServer
{
    private readonly ConcurrentDictionary<string, WebSocket> _socketClients = new();
    private readonly ConcurrentDictionary<string, TaskCompletionSource<BridgeResponse>> _pending = new();
    private readonly ConcurrentDictionary<string, long> _pendingStartedAtMs = new();
    private readonly SemaphoreSlim _wsSendLock = new(1, 1);

    private readonly JsonSerializerOptions _jsonOptions = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    public string ListenPrefix { get; }

    public BridgeServer(string listenPrefix)
    {
        ListenPrefix = listenPrefix;
    }

    public async Task RunAsync(CancellationToken cancellationToken = default)
    {
        var listener = new HttpListener();
        listener.Prefixes.Add(ListenPrefix);
        listener.Start();

        Console.WriteLine("Listening...");
        Logger.Info($"listening {ListenPrefix}");

        while (!cancellationToken.IsCancellationRequested)
        {
            var context = await listener.GetContextAsync();
            Console.WriteLine(context);

            if (context.Request.IsWebSocketRequest)
            {
                _ = Task.Run(() => HandleWebSocketClientAsync(context), CancellationToken.None);
                continue;
            }

            _ = Task.Run(() => HandleHttpRequestAsync(context), CancellationToken.None);
        }
    }

    private static Dictionary<string, string> ToHeaderDictionary(HttpListenerRequest request)
    {
        var dict = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var key in request.Headers.AllKeys)
        {
            if (string.IsNullOrWhiteSpace(key))
                continue;
            dict[key] = request.Headers[key] ?? "";
        }
        return dict;
    }

    private static async Task<(string body, bool isBase64)> ReadBodyAsync(HttpListenerRequest request)
    {
        if (!request.HasEntityBody)
            return ("", false);

        using var ms = new MemoryStream();
        await request.InputStream.CopyToAsync(ms);
        var bytes = ms.ToArray();
        if (bytes.Length == 0)
            return ("", false);

        // Prefer UTF-8 text if it roundtrips; otherwise base64.
        var text = Encoding.UTF8.GetString(bytes);
        var roundtrip = Encoding.UTF8.GetBytes(text);
        if (roundtrip.AsSpan().SequenceEqual(bytes))
            return (text, false);

        return (Convert.ToBase64String(bytes), true);
    }

    private WebSocket? PickSocketClient()
    {
        foreach (var kvp in _socketClients)
        {
            if (kvp.Value.State == WebSocketState.Open)
                return kvp.Value;
        }
        return null;
    }

    private static void TrySetResponseHeader(HttpListenerResponse response, string name, string value)
    {
        if (name.Equals("Content-Type", StringComparison.OrdinalIgnoreCase))
        {
            response.ContentType = value;
            return;
        }

        if (name.Equals("Content-Length", StringComparison.OrdinalIgnoreCase))
            return;

        try
        {
            response.Headers[name] = value;
        }
        catch
        {
            // Ignore restricted/invalid headers.
        }
    }

    private async Task HandleWebSocketClientAsync(HttpListenerContext context)
    {
        try
        {
            var wsContext = await context.AcceptWebSocketAsync(subProtocol: null);
            var ws = wsContext.WebSocket;

            var clientId = Guid.NewGuid().ToString("N");
            _socketClients[clientId] = ws;
            Console.WriteLine($"ws client connected id={clientId} remote={context.Request.RemoteEndPoint}");
            Logger.Info($"ws connected id={clientId} remote={context.Request.RemoteEndPoint}");

            var buffer = new byte[64 * 1024];
            var ms = new MemoryStream();

            try
            {
                while (ws.State == WebSocketState.Open)
                {
                    ms.SetLength(0);
                    WebSocketReceiveResult result;
                    do
                    {
                        result = await ws.ReceiveAsync(buffer, CancellationToken.None);
                        if (result.MessageType == WebSocketMessageType.Close)
                        {
                            try
                            {
                                await ws.CloseAsync(
                                    result.CloseStatus ?? WebSocketCloseStatus.NormalClosure,
                                    result.CloseStatusDescription,
                                    CancellationToken.None);
                            }
                            catch (Exception ex)
                            {
                                Console.WriteLine($"ws close handshake failed id={clientId} err={ex.GetType().Name}: {ex.Message}");
                                Logger.Error($"ws close handshake failed id={clientId} err={ex.GetType().Name}: {ex.Message}");
                            }
                            return;
                        }

                        ms.Write(buffer, 0, result.Count);
                    } while (!result.EndOfMessage);

                    if (result.MessageType != WebSocketMessageType.Text)
                        continue;

                    var json = Encoding.UTF8.GetString(ms.GetBuffer(), 0, (int)ms.Length);
                    Logger.Info($"ws recv bytes={(int)ms.Length} id={clientId}");
                    Logger.Info($"bridge ws<-client id={clientId} json={json}");

                    BridgeEnvelope? env;
                    try
                    {
                        env = JsonSerializer.Deserialize<BridgeEnvelope>(json, _jsonOptions);
                    }
                    catch
                    {
                        Logger.Warn($"ws recv invalid json id={clientId} bytes={(int)ms.Length}");
                        continue;
                    }

                    if (env?.Type != "response" || string.IsNullOrWhiteSpace(env.Id) || env.Response is null)
                        continue;

                    if (_pending.TryRemove(env.Id, out var tcs))
                    {
                        tcs.TrySetResult(env.Response);

                        var latencyMs = -1L;
                        if (_pendingStartedAtMs.TryRemove(env.Id, out var start))
                            latencyMs = Environment.TickCount64 - start;
                        Logger.Info($"bridge response id={env.Id} status={env.Response.Status} latencyMs={latencyMs}");
                    }
                }
            }
            catch (WebSocketException wse)
            {
                Console.WriteLine($"ws error id={clientId} err={wse.WebSocketErrorCode}: {wse.Message}");
                Logger.Error($"ws error id={clientId} err={wse.WebSocketErrorCode}: {wse.Message}");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"ws loop crashed id={clientId} err={ex.GetType().Name}: {ex.Message}");
                Logger.Error($"ws loop crashed id={clientId} err={ex.GetType().Name}: {ex.Message}");
            }
            finally
            {
                _socketClients.TryRemove(clientId, out _);
                try
                {
                    if (ws.State == WebSocketState.Open || ws.State == WebSocketState.CloseReceived)
                    {
                        ws.CloseAsync(WebSocketCloseStatus.NormalClosure, "bye", CancellationToken.None)
                            .GetAwaiter()
                            .GetResult();
                    }
                    else
                    {
                        ws.Abort();
                    }
                }
                catch { }
                try { ws.Dispose(); } catch { }
                Console.WriteLine($"ws client disconnected id={clientId}");
                Logger.Info($"ws disconnected id={clientId}");
            }
        }
        catch (Exception ex)
        {
            try { context.Response.StatusCode = 500; context.Response.Close(); } catch { }
            Logger.Error($"ws accept/handler failed err={ex.GetType().Name}: {ex.Message}");
        }
    }

    private async Task HandleHttpRequestAsync(HttpListenerContext context)
    {
        try
        {
            var response = context.Response;

            var ws = PickSocketClient();
            if (ws is null)
            {
                response.StatusCode = 503;
                var msg = Encoding.UTF8.GetBytes("No WebSocket client connected.");
                response.ContentType = "text/plain; charset=utf-8";
                response.ContentLength64 = msg.Length;
                await response.OutputStream.WriteAsync(msg);
                response.Close();
                Logger.Warn($"http 503 no ws client method={context.Request.HttpMethod} path={context.Request.RawUrl}");
                return;
            }

            var req = context.Request;
            var id = Guid.NewGuid().ToString("N");
            var path = req.RawUrl ?? (req.Url?.PathAndQuery ?? "/");
            Logger.Info($"http incoming id={id} method={req.HttpMethod} path={path} remote={req.RemoteEndPoint}");
            var headers = ToHeaderDictionary(req);
            var (body, isBase64) = await ReadBodyAsync(req);

            var envelope = new BridgeEnvelope
            {
                Type = "request",
                Id = id,
                Request = new BridgeRequest
                {
                    Method = req.HttpMethod,
                    Path = path,
                    Headers = headers,
                    Body = body,
                    IsBase64 = isBase64
                }
            };

            var json = JsonSerializer.Serialize(envelope, _jsonOptions);
            var payload = Encoding.UTF8.GetBytes(json);

            var tcs = new TaskCompletionSource<BridgeResponse>(TaskCreationOptions.RunContinuationsAsynchronously);
            _pending[id] = tcs;
            _pendingStartedAtMs[id] = Environment.TickCount64;

            try
            {
                await _wsSendLock.WaitAsync();
                try
                {
                    await ws.SendAsync(payload, WebSocketMessageType.Text, endOfMessage: true, CancellationToken.None);
                }
                finally
                {
                    _wsSendLock.Release();
                }
                Logger.Info($"bridge send request id={id} bytes={payload.Length}");
                Logger.Info($"bridge ws->client id={id} json={json}");
            }
            catch
            {
                _pending.TryRemove(id, out _);
                _pendingStartedAtMs.TryRemove(id, out _);
                response.StatusCode = 502;
                response.Close();
                Logger.Error($"http 502 send failed id={id} method={req.HttpMethod} path={path}");
                return;
            }

            BridgeResponse bridgeResp;
            try
            {
                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(60));
                await using (cts.Token.Register(() => tcs.TrySetCanceled(cts.Token)))
                {
                    bridgeResp = await tcs.Task;
                }
            }
            catch
            {
                _pending.TryRemove(id, out _);
                _pendingStartedAtMs.TryRemove(id, out _);
                response.StatusCode = 504;
                response.Close();
                Logger.Warn($"http 504 timeout id={id} method={req.HttpMethod} path={path}");
                return;
            }

            response.StatusCode = bridgeResp.Status;
            if (bridgeResp.Headers is not null)
            {
                foreach (var kvp in bridgeResp.Headers)
                    TrySetResponseHeader(response, kvp.Key, kvp.Value);
            }

            byte[] outBytes;
            if (string.IsNullOrEmpty(bridgeResp.Body))
            {
                outBytes = Array.Empty<byte>();
            }
            else if (bridgeResp.IsBase64)
            {
                try { outBytes = Convert.FromBase64String(bridgeResp.Body); }
                catch { outBytes = Array.Empty<byte>(); }
            }
            else
            {
                outBytes = Encoding.UTF8.GetBytes(bridgeResp.Body);
            }

            response.ContentLength64 = outBytes.Length;
            if (outBytes.Length > 0)
                await response.OutputStream.WriteAsync(outBytes);

            response.Close();
            Logger.Info($"http outgoing id={id} status={bridgeResp.Status} bytes={outBytes.Length}");
        }
        catch (Exception ex)
        {
            try { context.Response.StatusCode = 500; context.Response.Close(); } catch { }
            Logger.Error($"http handler failed err={ex.GetType().Name}: {ex.Message}");
        }
    }
}

