using Rc.Agent.Jobs;
using Rc.Agent.Persistence;
using Rc.Agent.Tests.Persistence;
using Rc.Contracts;
using Rc.TaskHost;
using Xunit;

namespace Rc.Agent.Tests.Jobs;

public sealed class TaskHostRegistrationTests
{
    [Fact]
    public async Task RegistrationControlsTaskHostAndPersistsItsStatus()
    {
        using var directory = new TemporaryDirectory();
        var pipeName = "rc-agent-registration-" + Guid.NewGuid().ToString("N");
        var request = new TaskLaunchRequest("job-registration", ExecRequest.ForDirectArgv(["cmd.exe", "/d", "/c", "more"]), ExecutionIdentity.CurrentUser, directory.Path, pipeName, TimeSpan.FromSeconds(1));
        await using var runner = new TaskHostRunner(request);
        await using var store = new AgentStateStore(directory.Path);
        await store.InitializeAsync();
        var registration = new TaskHostRegistration(store);
        var completion = runner.RunAsync();
        await runner.Started.WaitAsync(TimeSpan.FromSeconds(20));

        await registration.WriteStandardInputAsync(pipeName, new byte[] { (byte)'o', (byte)'k', (byte)'\r', (byte)'\n' }, TimeSpan.FromSeconds(20));
        var status = await registration.RefreshAsync(pipeName, TimeSpan.FromSeconds(20));
        await registration.CloseStandardInputAsync(pipeName, TimeSpan.FromSeconds(20));
        await completion;

        Assert.Equal(JobState.Running, status.Job.State);
        Assert.Equal(status.Job, await store.GetJobSnapshotAsync("job-registration"));
    }
}
