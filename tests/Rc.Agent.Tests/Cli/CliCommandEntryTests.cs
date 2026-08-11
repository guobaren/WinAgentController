using System.Text;
using Rc.Cli.Commands;
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

    public static TheoryData<string, int> InvalidCommandArguments => new()
    {
        { "discover", 2 },
        { "target", 2 },
        { "probe", 2 },
        { "pair", 2 },
        { "exec", 2 },
        { "job", 2 },
        { "fs", 1 },
        { "copy", 1 },
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
}
