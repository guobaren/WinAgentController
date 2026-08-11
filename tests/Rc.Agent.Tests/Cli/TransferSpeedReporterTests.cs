using System.Text;
using Rc.Cli.Commands;
using Xunit;

namespace Rc.Agent.Tests.Cli;

public sealed class TransferSpeedReporterTests
{
    [Fact]
    public async Task ReportsLiveSamplesAndFinalMinimumMaximumAndAverageSpeeds()
    {
        var elapsed = TimeSpan.Zero;
        using var output = new StringWriter(new StringBuilder());
        var reporter = new TransferSpeedReporter(
            output,
            TimeSpan.FromSeconds(1),
            () => elapsed);

        elapsed = TimeSpan.FromSeconds(1);
        await reporter.RecordBytesAsync(1024 * 1024);
        elapsed = TimeSpan.FromSeconds(3);
        await reporter.RecordBytesAsync(3 * 1024 * 1024);
        elapsed = TimeSpan.FromSeconds(4);
        await reporter.CompleteAsync();

        var text = output.ToString();
        Assert.Contains("progress bytes=1048576 elapsed=1.000s currentMiB/s=1.00", text, StringComparison.Ordinal);
        Assert.Contains("progress bytes=4194304 elapsed=3.000s currentMiB/s=1.50", text, StringComparison.Ordinal);
        Assert.Contains("bytes=4194304 wireBytes=4194304 retransmittedBytes=0 elapsed=4.000s MiB/s=1.00 minMiB/s=1.00 maxMiB/s=1.50 avgMiB/s=1.00", text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ReportsRetransmittedWireBytesSeparatelyFromLogicalBytes()
    {
        var elapsed = TimeSpan.FromSeconds(2);
        using var output = new StringWriter(new StringBuilder());
        var reporter = new TransferSpeedReporter(output, elapsedProvider: () => elapsed);

        await reporter.RecordBytesAsync(6 * 1024 * 1024);
        await reporter.CompleteAsync(4 * 1024 * 1024);

        Assert.Contains("bytes=4194304 wireBytes=6291456 retransmittedBytes=2097152", output.ToString(), StringComparison.Ordinal);
    }
}
