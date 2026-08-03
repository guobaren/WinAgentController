using System.Net;
using Rc.Cli.Targets;
using Xunit;

namespace Rc.Agent.Tests.Cli;

public sealed class ControllerTargetStoreTests : IDisposable
{
    private const string Fingerprint = "AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA";
    private readonly string root = Path.Combine(Path.GetTempPath(), "rc-cli-target-tests", Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task AddCreatesCurrentTargetAndPersistsProfile()
    {
        var store = new ControllerTargetStore(root);
        await store.AddAsync("lab", "device-1", IPEndPoint.Parse("192.168.10.5:43001"), Fingerprint);

        var snapshot = await new ControllerTargetStore(root).GetSnapshotAsync();

        Assert.Equal("lab", snapshot.CurrentTarget);
        var target = Assert.Single(snapshot.Targets);
        Assert.Equal("device-1", target.DeviceId);
        Assert.Equal("192.168.10.5:43001", target.Endpoint);
        Assert.Equal(Fingerprint, target.CertificateSha256Fingerprint);
    }

    [Fact]
    public async Task AddRejectsRebindingNameToDifferentIdentity()
    {
        var store = new ControllerTargetStore(root);
        await store.AddAsync("lab", "device-1", IPEndPoint.Parse("192.168.10.5:43001"), Fingerprint);

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            store.AddAsync("lab", "device-2", IPEndPoint.Parse("192.168.10.6:43001"), Fingerprint));

        Assert.Contains("different device", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task RefreshChangesOnlyEndpoint()
    {
        var store = new ControllerTargetStore(root);
        await store.AddAsync("lab", "device-1", IPEndPoint.Parse("192.168.10.5:43001"), Fingerprint);

        var refreshed = await store.RefreshEndpointAsync("lab", IPEndPoint.Parse("192.168.10.9:43002"));

        Assert.Equal("192.168.10.9:43002", refreshed.Endpoint);
        Assert.Equal("device-1", refreshed.DeviceId);
        Assert.Equal(Fingerprint, refreshed.CertificateSha256Fingerprint);
    }

    [Fact]
    public async Task ResolverExpandsNamedAndCurrentTargets()
    {
        var store = new ControllerTargetStore(root);
        await store.AddAsync("lab", "device-1", IPEndPoint.Parse("192.168.10.5:43001"), Fingerprint);
        var resolver = new TargetArgumentResolver(store);

        var named = await resolver.ResolveAsync(["exec", "lab", "--command", "hostname"]);
        var current = await resolver.ResolveAsync(["fs", "list", ".", "--text"]);

        Assert.True(named.Success);
        Assert.Equal(["exec", "192.168.10.5:43001", "--command", "hostname", "--fingerprint", Fingerprint], named.Arguments);
        Assert.True(current.Success);
        Assert.Equal(["fs", "list", "192.168.10.5:43001", ".", "--text", "--fingerprint", Fingerprint], current.Arguments);
    }

    [Fact]
    public async Task ResolverPreservesExplicitEndpointAndRejectsFingerprintMismatchForNamedTarget()
    {
        var store = new ControllerTargetStore(root);
        await store.AddAsync("lab", "device-1", IPEndPoint.Parse("192.168.10.5:43001"), Fingerprint);
        var resolver = new TargetArgumentResolver(store);

        var explicitEndpoint = await resolver.ResolveAsync(["exec", "192.168.10.6:43001", "--fingerprint", new string('B', 64), "--command", "hostname"]);
        var mismatch = await resolver.ResolveAsync(["exec", "lab", "--fingerprint", new string('B', 64), "--command", "hostname"]);

        Assert.True(explicitEndpoint.Success);
        Assert.Equal("192.168.10.6:43001", explicitEndpoint.Arguments[1]);
        Assert.False(mismatch.Success);
        Assert.Contains("does not match", mismatch.Error, StringComparison.Ordinal);
    }

    public static TheoryData<string[], int> RemoteCommandArguments => new()
    {
        { ["probe", "lab"], 1 },
        { ["pair", "lab", "--code", "unused-in-parser-test"], 1 },
        { ["exec", "lab", "--command", "hostname"], 1 },
        { ["job", "start", "lab", "--command", "hostname"], 2 },
        { ["fs", "list", "lab", "."], 2 },
        { ["copy", "status", "lab", "session-1"], 2 },
        { ["ui", "status", "lab"], 2 },
        { ["update", "status", "lab", "--update", Guid.Empty.ToString()], 2 },
    };

    [Theory]
    [MemberData(nameof(RemoteCommandArguments))]
    public async Task ResolverSupportsEveryRemoteCommandEndpointPosition(string[] arguments, int endpointIndex)
    {
        var store = new ControllerTargetStore(root);
        await store.AddAsync("lab", "device-1", IPEndPoint.Parse("192.168.10.5:43001"), Fingerprint);

        var resolved = await new TargetArgumentResolver(store).ResolveAsync(arguments);

        Assert.True(resolved.Success);
        Assert.Equal("192.168.10.5:43001", resolved.Arguments[endpointIndex]);
        var fingerprintIndex = Array.IndexOf(resolved.Arguments, "--fingerprint");
        Assert.True(fingerprintIndex >= 0);
        Assert.Equal(Fingerprint, resolved.Arguments[fingerprintIndex + 1]);
    }

    [Fact]
    public async Task InvalidJsonIsReportedAsInvalidProfileData()
    {
        Directory.CreateDirectory(root);
        await File.WriteAllTextAsync(Path.Combine(root, "targets.json"), "{not-json");

        var exception = await Assert.ThrowsAsync<InvalidDataException>(() =>
            new ControllerTargetStore(root).GetSnapshotAsync());

        Assert.Contains("invalid JSON", exception.Message, StringComparison.Ordinal);
    }

    public void Dispose()
    {
        if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
    }
}
