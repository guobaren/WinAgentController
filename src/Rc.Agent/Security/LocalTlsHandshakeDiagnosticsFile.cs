using System.Text.Json;

namespace Rc.Agent.Security;

/// <summary>
/// Keeps the last TLS listener failure in the Agent's access-controlled data
/// directory so a local administrator can diagnose service-only Schannel
/// failures without exposing implementation details on the network endpoint.
/// </summary>
public static class LocalTlsHandshakeDiagnosticsFile
{
    private const string FileName = "tls-handshake-diagnostics.json";

    public static string GetPath(string dataRoot)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(dataRoot);
        return Path.Combine(Path.GetFullPath(dataRoot), FileName);
    }

    public static void Write(string dataRoot, string stage, Exception exception)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(stage);
        ArgumentNullException.ThrowIfNull(exception);

        var message = exception.Message.Trim();
        if (message.Length > 512)
        {
            message = message[..512];
        }

        var diagnostic = new LocalTlsHandshakeDiagnostic(
            DateTimeOffset.UtcNow,
            stage,
            exception.GetType().FullName ?? exception.GetType().Name,
            exception.HResult,
            message);
        WriteAtomic(GetPath(dataRoot), diagnostic);
    }

    public static bool TryRead(string dataRoot, out LocalTlsHandshakeDiagnostic? diagnostic)
    {
        diagnostic = null;
        var path = GetPath(dataRoot);
        if (!File.Exists(path))
        {
            return false;
        }

        try
        {
            var saved = JsonSerializer.Deserialize<LocalTlsHandshakeDiagnostic>(File.ReadAllText(path));
            if (saved is null || string.IsNullOrWhiteSpace(saved.Stage) ||
                string.IsNullOrWhiteSpace(saved.ExceptionType) || saved.Message is null)
            {
                return false;
            }

            diagnostic = saved;
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    public static void Clear(string dataRoot)
    {
        try
        {
            File.Delete(GetPath(dataRoot));
        }
        catch (IOException)
        {
            // Diagnostics must never disrupt a successful control connection.
        }
        catch (UnauthorizedAccessException)
        {
            // The Agent can still serve the request if local diagnostics cannot be removed.
        }
    }

    private static readonly object writeGate = new();

    private static void WriteAtomic(string path, LocalTlsHandshakeDiagnostic diagnostic)
    {
        var temporaryPath = Path.Combine(Path.GetDirectoryName(path)!, $".{FileName}.{Guid.NewGuid():N}.tmp");
        // 串行化：并发 TLS 握手失败会并发覆盖同一诊断文件，两个 File.Move(overwrite)
        // 同时执行可能互相占用目标而抛 IOException（云端 CI 已复现）。
        lock (writeGate)
        {
            try
            {
                File.WriteAllText(temporaryPath, JsonSerializer.Serialize(diagnostic));
                File.Move(temporaryPath, path, overwrite: true);
            }
            finally
            {
                try
                {
                    File.Delete(temporaryPath);
                }
                catch (IOException)
                {
                    // 诊断写入失败不得影响 TLS 握手结果。
                }
                catch (UnauthorizedAccessException)
                {
                }
            }
        }
    }
}

public sealed record LocalTlsHandshakeDiagnostic(
    DateTimeOffset RecordedAtUtc,
    string Stage,
    string ExceptionType,
    int HResult,
    string Message);
