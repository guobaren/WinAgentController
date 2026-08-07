using System.Text.Json;
using Xunit;

namespace Rc.Contracts.Tests;

public sealed class JobAndOperationContractTests
{
    [Fact]
    public void JobLogRequestsValidateBoundsAndSerializeStableOffsets()
    {
        var request = new JobLogReadRequest("job-7", JobOutputKind.Stderr, 16, 4096);
        var json = JsonSerializer.Serialize(request, ContractJson.Options);

        Assert.Equal("{\"jobId\":\"job-7\",\"stream\":\"stderr\",\"afterOffset\":16,\"maximumBytes\":4096}", json);
        Assert.Throws<ArgumentOutOfRangeException>(() => new JobLogReadRequest("job-7", JobOutputKind.Stdout, -1, 1));
        Assert.Throws<ArgumentOutOfRangeException>(() => new JobLogReadRequest("job-7", JobOutputKind.Stdout, 0, 0));
        Assert.Throws<ArgumentOutOfRangeException>(() => new JobLogFollowRequest("job-7", JobOutputKind.Stdout, 0, 1, TimeSpan.FromMinutes(11)));
    }

    [Fact]
    public void FileRangeAndAtomicWriteUsePathSpecificPayloadsAndDefensiveCopies()
    {
        var read = new FileReadRequest("reports/out.bin", 8, 1024);
        var source = new byte[] { 1, 2, 3 };
        var write = new FileWriteRequest("reports/out.bin", source, overwrite: true);
        source[0] = 9;

        Assert.Equal("{\"path\":\"reports/out.bin\",\"offset\":8,\"maximumBytes\":1024}", JsonSerializer.Serialize(read, ContractJson.Options));
        Assert.Equal("{\"path\":\"reports/out.bin\",\"data\":\"AQID\",\"overwrite\":true}", JsonSerializer.Serialize(write, ContractJson.Options));
        Assert.Equal(new byte[] { 1, 2, 3 }, write.Data);
        Assert.Throws<ArgumentOutOfRangeException>(() => new FileReadRequest("x", -1, 1));
        Assert.Throws<ArgumentOutOfRangeException>(() => new FileReadRequest("x", 0, 0));
    }

    [Fact]
    public void TransferSessionCopiesCompletionStateAndRequiresKnownEnums()
    {
        var completed = new List<string> { "one.txt" };
        var manifest = new FileManifest("source", []);
        var session = new TransferSessionSnapshot(
            "transfer-1",
            TransferDirection.Upload,
            TransferSessionState.Transferring,
            "source",
            "destination",
            manifest,
            1024,
            DateTimeOffset.UnixEpoch,
            DateTimeOffset.UnixEpoch.AddHours(1),
            completed);
        completed.Add("injected.txt");

        var json = JsonSerializer.Serialize(session, ContractJson.Options);
        Assert.Contains("one.txt", json, StringComparison.Ordinal);
        Assert.DoesNotContain("injected.txt", json, StringComparison.Ordinal);
        Assert.Throws<ArgumentOutOfRangeException>(() => new TransferSessionSnapshot(
            "transfer-1",
            (TransferDirection)99,
            TransferSessionState.Preparing,
            "source",
            "destination",
            manifest,
            1024,
            DateTimeOffset.UnixEpoch,
            DateTimeOffset.UnixEpoch));
    }

    [Fact]
    public void UiTargetsAreExplicitAndBinaryClipboardPayloadsAreDefensivelyCopied()
    {
        var display = new DisplayTarget(0);
        var window = new WindowTarget(123);
        var screenshot = new UiScreenshotRequest(window);
        var source = new byte[] { 4, 5 };
        var clipboard = new UiClipboardWriteRequest(source);
        var clearedClipboard = new UiClipboardWriteRequest(Array.Empty<byte>());
        source[0] = 9;

        Assert.Equal("{\"target\":{\"kind\":\"window\",\"windowHandle\":123}}", JsonSerializer.Serialize(screenshot, ContractJson.Options));
        Assert.Equal("{\"data\":\"BAU=\",\"format\":\"text/plain\"}", JsonSerializer.Serialize(clipboard, ContractJson.Options));
        var clearedClipboardJson = JsonSerializer.Serialize(clearedClipboard, ContractJson.Options);
        var clearedClipboardRoundTrip = JsonSerializer.Deserialize<UiClipboardWriteRequest>(clearedClipboardJson, ContractJson.Options);
        Assert.Equal("{\"data\":\"\",\"format\":\"text/plain\"}", clearedClipboardJson);
        Assert.NotNull(clearedClipboardRoundTrip);
        Assert.Empty(clearedClipboardRoundTrip.Data);
        Assert.Equal(new byte[] { 4, 5 }, clipboard.Data);
        Assert.Empty(clearedClipboard.Data);
        Assert.Equal(0, display.DisplayIndex);
        Assert.Throws<ArgumentOutOfRangeException>(() => new DisplayTarget(-1));
        Assert.Throws<ArgumentOutOfRangeException>(() => new WindowTarget(0));
    }
}
