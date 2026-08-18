using Rc.Cli.Commands;
using Xunit;

namespace Rc.Agent.Tests.Cli;

public sealed class HealthCommandTests
{
    private const string BothRunning = """
        SERVICE_NAME: RemoteControllerAgent
                STATE              : 4  RUNNING
        SERVICE_NAME: RemoteControllerBroker
                STATE              : 4  RUNNING
        """;

    private const string BrokerStopped = """
        SERVICE_NAME: RemoteControllerAgent
                STATE              : 4  RUNNING
        SERVICE_NAME: RemoteControllerBroker
                STATE              : 1  STOPPED
        """;

    private const string Listening = """
        TCP    0.0.0.0:43001          0.0.0.0:0              LISTENING       3968
        """;

    private const string NotListening = """
        TCP    0.0.0.0:43001          0.0.0.0:0              TIME_WAIT       0
        """;

    [Fact]
    public void HealthyWhenBothServicesRunningAndPortListening()
    {
        var (running, listening, healthy) = HealthCommand.Evaluate(BothRunning, Listening);

        Assert.Equal(2, running);
        Assert.True(listening);
        Assert.True(healthy);
    }

    [Fact]
    public void FailsWhenOneServiceIsStopped()
    {
        var (running, listening, healthy) = HealthCommand.Evaluate(BrokerStopped, Listening);

        Assert.Equal(1, running);
        Assert.True(listening);
        Assert.False(healthy);
    }

    [Fact]
    public void FailsWhenPortIsNotListening()
    {
        var (running, listening, healthy) = HealthCommand.Evaluate(BothRunning, NotListening);

        Assert.Equal(2, running);
        Assert.False(listening);
        Assert.False(healthy);
    }

    [Fact]
    public void FailsOnEmptyOutputs()
    {
        var (running, listening, healthy) = HealthCommand.Evaluate("(no output)", "(no output)");

        Assert.Equal(0, running);
        Assert.False(listening);
        Assert.False(healthy);
    }
}
