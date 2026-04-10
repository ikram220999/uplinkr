using System.Text;
using System.Threading.Channels;

namespace WebSocketBridge;

internal static class Logger
{
    private static readonly Channel<string> Channel = System.Threading.Channels.Channel.CreateUnbounded<string>(
        new UnboundedChannelOptions { SingleReader = true, SingleWriter = false });

    private static int _started;

    public static void Init()
    {
        if (Interlocked.Exchange(ref _started, 1) == 1)
            return;

        var dir = Environment.GetEnvironmentVariable("BRIDGE_LOG_DIR");
        if (string.IsNullOrWhiteSpace(dir))
            dir = Path.Combine(AppContext.BaseDirectory, "logs");

        Directory.CreateDirectory(dir);
        var file = Environment.GetEnvironmentVariable("BRIDGE_LOG_FILE");
        if (string.IsNullOrWhiteSpace(file))
            file = $"bridge-{DateTime.Now:yyyyMMdd-HHmmss}.log";

        var logPath = Path.Combine(dir, file);

        _ = Task.Run(async () =>
        {
            try
            {
                await using var fs = new FileStream(logPath, FileMode.Create, FileAccess.Write, FileShare.ReadWrite);
                await using var sw = new StreamWriter(fs, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false))
                {
                    AutoFlush = true
                };

                await sw.WriteLineAsync($"{DateTimeOffset.Now:O} INFO log started path={logPath}");

                while (await Channel.Reader.WaitToReadAsync())
                {
                    while (Channel.Reader.TryRead(out var line))
                        await sw.WriteLineAsync(line);
                }
            }
            catch
            {
                // If logging fails, we avoid crashing the bridge.
            }
        });
    }

    public static void Info(string msg) => Write("INFO", msg);
    public static void Warn(string msg) => Write("WARN", msg);
    public static void Error(string msg) => Write("ERROR", msg);

    private static void Write(string level, string msg)
    {
        if (Volatile.Read(ref _started) == 0)
            return;

        Channel.Writer.TryWrite($"{DateTimeOffset.Now:O} {level} {msg}");
    }
}
