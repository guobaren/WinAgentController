using System.Diagnostics;
using System.Net;
using System.Text;
using System.Text.Json;
using Rc.Cli.Commands;
using Rc.Cli.Targets;
using Rc.Contracts;
using Xunit;

namespace Rc.Agent.Tests.Cli;

public sealed class CliCommandEntryTests
{
    [Theory]
    [InlineData("1.0.0.0", "1.0.0.0")]
    [InlineData("1.0.0+source", "1.0.0")]
    [InlineData("not-a-version", null)]
    [InlineData(null, null)]
    public void UpdatePackageVersionAcceptsFileVersionAndStripsInformationalMetadata(string? input, string? expected)
    {
        Assert.Equal(expected, UpdateCommand.NormalizePackageVersion(input));
    }

    [Fact]
    public void UpdateChunkNegotiationRequiresCurrentBinaryCapability()
    {
        UpdateCommand.EnsureCurrentAgentCapability(true);
        Assert.Throws<InvalidOperationException>(() => UpdateCommand.EnsureCurrentAgentCapability(false));
        Assert.Equal(64 * 1024 * 1024, UpdateCommand.ResolveChunkSize(64 * 1024 * 1024, null));
        Assert.Equal(8 * 1024 * 1024, UpdateCommand.ResolveChunkSize(8 * 1024 * 1024, null));
        Assert.Throws<ArgumentException>(() => UpdateCommand.ResolveChunkSize(256 * 1024, "262145"));
    }

    [Fact]
    public void CopyRequiresCurrentBinaryAndStreamingCapabilities()
    {
        FileCommand.EnsureCurrentAgentCapabilities(true, true);
        Assert.Throws<InvalidOperationException>(() => FileCommand.EnsureCurrentAgentCapabilities(false, true));
        Assert.Throws<InvalidOperationException>(() => FileCommand.EnsureCurrentAgentCapabilities(true, false));
    }

    public static TheoryData<string, int> InvalidCommandArguments => new()
    {
        { "discover", 2 },
        { "target", 2 },
        { "probe", 2 },
        { "pair", 2 },
        { "exec", 2 },
        { "job", 2 },
        { "fs", 2 },
        { "copy", 2 },
        { "ui", 2 },
        { "update", 2 },
    };

    [Theory]
    [MemberData(nameof(InvalidCommandArguments))]
    public async Task EveryCommandFamilyRejectsMalformedArguments(string command, int expectedExitCode)
    {
        using var output = new StringWriter(new StringBuilder());
        using var error = new StringWriter(new StringBuilder());

        var exitCode = command switch
        {
            "discover" => await DiscoverCommand.RunAsync(["--timeout-ms", "0"], output, error),
            "target" => await TargetCommand.RunAsync(["unknown"], output, error),
            "probe" => await ProbeCommand.RunAsync(["127.0.0.1:1"], output, error),
            "pair" => await PairCommand.RunAsync(["127.0.0.1:1"], new StringReader(string.Empty), output, error),
            "exec" => await ExecCommand.RunAsync([], output, error),
            "job" => await JobCommand.RunAsync([], output, error),
            "fs" => await FileCommand.RunFsAsync([], output, error),
            "copy" => await FileCommand.RunCopyAsync([], output, error),
            "ui" => await UiCommand.RunAsync([], output, error),
            "update" => await UpdateCommand.RunAsync([], output, error),
            _ => throw new ArgumentOutOfRangeException(nameof(command), command, null),
        };

        Assert.Equal(expectedExitCode, exitCode);
        var diagnostics = output.ToString() + error.ToString();
        Assert.False(string.IsNullOrWhiteSpace(diagnostics));
    }

    [Theory]
    [InlineData("job")]
    [InlineData("fs")]
    [InlineData("copy")]
    [InlineData("ui")]
    [InlineData("update")]
    public async Task IncompleteRemoteCommandWithCurrentTargetReturnsStructuredError(string command)
    {
        var root = Path.Combine(Path.GetTempPath(), "rc-cli-entry-tests", Guid.NewGuid().ToString("N"));
        try
        {
            await new ControllerTargetStore(root).AddAsync(
                "lab",
                "device-1",
                IPEndPoint.Parse("192.0.2.1:43001"),
                new string('A', 64));
            var startInfo = new ProcessStartInfo(Path.Combine(AppContext.BaseDirectory, "Rc.Cli.exe"))
            {
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
            };
            startInfo.ArgumentList.Add(command);
            startInfo.Environment["RC_CONTROLLER_DATA_ROOT"] = root;

            using var process = Process.Start(startInfo) ?? throw new InvalidOperationException("Unable to start Rc.Cli.exe.");
            var stdout = await process.StandardOutput.ReadToEndAsync();
            var stderr = await process.StandardError.ReadToEndAsync();
            await process.WaitForExitAsync();

            Assert.Equal(2, process.ExitCode);
            Assert.Equal(string.Empty, stderr);
            var envelope = JsonSerializer.Deserialize<ResultEnvelope<JsonElement>>(stdout, ContractJson.Options);
            Assert.NotNull(envelope);
            Assert.False(envelope!.Ok);
            Assert.Equal(ErrorCode.InvalidRequest, envelope.Error?.Code);
            Assert.False(string.IsNullOrWhiteSpace(envelope.Error?.Message));
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }
}
