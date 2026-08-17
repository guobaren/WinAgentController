using Rc.Agent.Security;
using Rc.Agent.Tests.Persistence;
using Xunit;

namespace Rc.Agent.Tests.Security;

public sealed class LocalTlsHandshakeDiagnosticsFileTests
{
    [Fact]
    public void MostRecentTlsFailureCanBeReadAndCleared()
    {
        using var directory = new TemporaryDirectory();
        var exception = new IOException("The TLS server could not acquire a credential.");

        LocalTlsHandshakeDiagnosticsFile.Write(directory.Path, "authenticating the TLS server", exception);

        Assert.True(LocalTlsHandshakeDiagnosticsFile.TryRead(directory.Path, out var diagnostic));
        Assert.NotNull(diagnostic);
        Assert.Equal("authenticating the TLS server", diagnostic!.Stage);
        Assert.Equal(typeof(IOException).FullName, diagnostic.ExceptionType);
        Assert.Equal(exception.HResult, diagnostic.HResult);
        Assert.Equal(exception.Message, diagnostic.Message);

        LocalTlsHandshakeDiagnosticsFile.Clear(directory.Path);

        Assert.False(LocalTlsHandshakeDiagnosticsFile.TryRead(directory.Path, out _));
    }

    [Fact]
    public async Task ConcurrentWritesNeverThrowAndLeaveReadableDiagnostics()
    {
        using var directory = new TemporaryDirectory();

        // 并发 TLS 握手失败会同时写诊断文件；此前无锁的 File.Move(overwrite)
        // 并发覆盖同一目标会偶发 IOException（云端 CI 已复现）。
        var writes = Enumerable.Range(0, 32).Select(index => Task.Run(() =>
            LocalTlsHandshakeDiagnosticsFile.Write(directory.Path, $"stage-{index}", new IOException($"failure-{index}"))));
        await Task.WhenAll(writes);

        Assert.True(LocalTlsHandshakeDiagnosticsFile.TryRead(directory.Path, out var diagnostic));
        Assert.NotNull(diagnostic);
        Assert.Empty(Directory.EnumerateFiles(directory.Path, "*.tmp"));
    }
}
