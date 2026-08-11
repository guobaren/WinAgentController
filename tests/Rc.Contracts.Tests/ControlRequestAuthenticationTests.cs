using System.Security.Cryptography;
using Rc.Contracts;
using Xunit;

namespace Rc.Contracts.Tests;

public sealed class ControlRequestAuthenticationTests
{
    [Fact]
    public void ExecuteOnceSignatureVerifiesOnlyForTheOriginalAgentControllerAndCommand()
    {
        using var privateKey = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var execution = ExecRequest.ForShell(ShellKind.PowerShell, "Write-Output hello", "C:\\Temp");
        var signature = ControlRequestAuthentication.SignExecuteOnce("agent-1", "controller-1", execution, privateKey);

        try
        {
            Assert.True(ControlRequestAuthentication.VerifyExecuteOnce("agent-1", "controller-1", execution, signature, privateKey));
            Assert.False(ControlRequestAuthentication.VerifyExecuteOnce("agent-2", "controller-1", execution, signature, privateKey));
            Assert.False(ControlRequestAuthentication.VerifyExecuteOnce(
                "agent-1",
                "controller-1",
                ExecRequest.ForShell(ShellKind.PowerShell, "Write-Output altered", "C:\\Temp"),
                signature,
                privateKey));
        }
        finally
        {
            CryptographicOperations.ZeroMemory(signature);
        }
    }

    [Fact]
    public void JobSignaturesAreBoundToTheirOperationAndPayload()
    {
        using var privateKey = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var statusSignature = ControlRequestAuthentication.SignJobStatus("agent-1", "controller-1", "job-1", privateKey);
        var startSignature = ControlRequestAuthentication.SignJobStart(
            "agent-1",
            "controller-1",
            ExecRequest.ForShell(ShellKind.Cmd, "echo hello"),
            privateKey);

        try
        {
            Assert.True(ControlRequestAuthentication.VerifyJobStatus("agent-1", "controller-1", "job-1", statusSignature, privateKey));
            Assert.False(ControlRequestAuthentication.VerifyJobStatus("agent-1", "controller-1", "job-2", statusSignature, privateKey));
            Assert.False(ControlRequestAuthentication.VerifyJobStart(
                "agent-1",
                "controller-1",
                ExecRequest.ForShell(ShellKind.Cmd, "echo hello"),
                statusSignature,
                privateKey));
            Assert.True(ControlRequestAuthentication.VerifyJobStart(
                "agent-1",
                "controller-1",
                ExecRequest.ForShell(ShellKind.Cmd, "echo hello"),
                startSignature,
                privateKey));
        }
        finally
        {
            CryptographicOperations.ZeroMemory(statusSignature);
            CryptographicOperations.ZeroMemory(startSignature);
        }
    }

    [Fact]
    public void UpdateSignaturesAreBoundToTheManifestAndChunkHash()
    {
        using var privateKey = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var manifest = new UpdatePackageManifest("RemoteController", "1.2.3", [
            new UpdatePackageFile("Rc.Agent.exe", 12, new string('A', 64)),
        ]);
        var start = new UpdateStartRequest(Guid.NewGuid(), manifest);
        var chunk = new UpdateWriteChunkRequest(start.UpdateId, "Rc.Agent.exe", 0, [1, 2, 3], Convert.ToHexString(SHA256.HashData([1, 2, 3])));
        var startSignature = ControlRequestAuthentication.SignUpdateStart("agent-1", "controller-1", start, privateKey);
        var chunkSignature = ControlRequestAuthentication.SignUpdateWriteChunk("agent-1", "controller-1", chunk, privateKey);
        var binary = new UpdateBinaryWriteRequest(start.UpdateId, "Rc.Agent.exe", 0, 3, chunk.Sha256);
        var binarySignature = ControlRequestAuthentication.SignUpdateWriteBinary("agent-1", "controller-1", binary, privateKey);

        try
        {
            Assert.True(ControlRequestAuthentication.VerifyUpdateStart("agent-1", "controller-1", start, startSignature, privateKey));
            Assert.False(ControlRequestAuthentication.VerifyUpdateStart("agent-1", "controller-1", start with { UpdateId = Guid.NewGuid() }, startSignature, privateKey));
            Assert.True(ControlRequestAuthentication.VerifyUpdateWriteChunk("agent-1", "controller-1", chunk, chunkSignature, privateKey));
            Assert.False(ControlRequestAuthentication.VerifyUpdateWriteChunk("agent-1", "controller-1", new UpdateWriteChunkRequest(start.UpdateId, "Rc.Agent.exe", 0, [1, 2, 3], new string('B', 64)), chunkSignature, privateKey));
            Assert.True(ControlRequestAuthentication.VerifyUpdateWriteBinary("agent-1", "controller-1", binary, binarySignature, privateKey));
            Assert.False(ControlRequestAuthentication.VerifyUpdateWriteBinary("agent-1", "controller-1", binary with { Length = 4 }, binarySignature, privateKey));
        }
        finally
        {
            CryptographicOperations.ZeroMemory(startSignature);
            CryptographicOperations.ZeroMemory(chunkSignature);
            CryptographicOperations.ZeroMemory(binarySignature);
        }
    }
}
