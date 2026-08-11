using System.Net;
using System.Text.Json;

namespace Rc.Cli.Targets;

internal sealed record ControllerTargetProfile(
    string Name,
    string DeviceId,
    string Endpoint,
    string CertificateSha256Fingerprint,
    DateTimeOffset UpdatedAtUtc);

internal sealed record ControllerTargetSnapshot(
    string? CurrentTarget,
    IReadOnlyList<ControllerTargetProfile> Targets);

internal sealed class ControllerTargetStore
{
    private const int CurrentVersion = 1;
    private const string FileName = "targets.json";
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web) { WriteIndented = true };
    private readonly string path;

    public ControllerTargetStore(string? root = null)
    {
        root ??= Environment.GetEnvironmentVariable("RC_CONTROLLER_DATA_ROOT")
            ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "RemoteController");
        path = Path.Combine(Path.GetFullPath(root), FileName);
    }

    public async Task<ControllerTargetSnapshot> GetSnapshotAsync(CancellationToken cancellationToken = default)
    {
        var document = await LoadAsync(cancellationToken).ConfigureAwait(false);
        return new ControllerTargetSnapshot(
            document.CurrentTarget,
            document.Targets.OrderBy(target => target.Name, StringComparer.OrdinalIgnoreCase).ToArray());
    }

    public async Task<ControllerTargetProfile?> FindAsync(string name, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        var document = await LoadAsync(cancellationToken).ConfigureAwait(false);
        return document.Targets.FirstOrDefault(target => string.Equals(target.Name, name, StringComparison.OrdinalIgnoreCase));
    }

    public async Task<ControllerTargetProfile?> GetCurrentAsync(CancellationToken cancellationToken = default)
    {
        var document = await LoadAsync(cancellationToken).ConfigureAwait(false);
        return document.CurrentTarget is null
            ? null
            : document.Targets.FirstOrDefault(target => string.Equals(target.Name, document.CurrentTarget, StringComparison.OrdinalIgnoreCase));
    }

    public async Task<ControllerTargetProfile> AddAsync(
        string name,
        string deviceId,
        IPEndPoint endpoint,
        string fingerprint,
        CancellationToken cancellationToken = default)
    {
        ValidateName(name);
        ArgumentException.ThrowIfNullOrWhiteSpace(deviceId);
        ArgumentNullException.ThrowIfNull(endpoint);
        fingerprint = TargetValueParser.NormalizeFingerprint(fingerprint)
            ?? throw new ArgumentException("The certificate fingerprint must be a 64-character SHA-256 hexadecimal value.", nameof(fingerprint));

        var document = await LoadAsync(cancellationToken).ConfigureAwait(false);
        var existing = document.Targets.FirstOrDefault(target => string.Equals(target.Name, name, StringComparison.OrdinalIgnoreCase));
        if (existing is not null && !string.Equals(existing.DeviceId, deviceId, StringComparison.Ordinal))
        {
            throw new InvalidOperationException($"Target '{name}' is already pinned to a different device.");
        }

        var profile = new ControllerTargetProfile(name, deviceId, endpoint.ToString(), fingerprint, DateTimeOffset.UtcNow);
        if (existing is null)
        {
            document.Targets.Add(profile);
        }
        else
        {
            document.Targets[document.Targets.IndexOf(existing)] = profile;
        }

        RemoveSupersededFingerprints(document, profile);
        document.CurrentTarget ??= profile.Name;
        await SaveAsync(document, cancellationToken).ConfigureAwait(false);
        return profile;
    }

    public async Task<ControllerTargetProfile> RememberSuccessfulConnectionAsync(
        string deviceId,
        IPEndPoint endpoint,
        string fingerprint,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(deviceId);
        ArgumentNullException.ThrowIfNull(endpoint);
        fingerprint = TargetValueParser.NormalizeFingerprint(fingerprint)
            ?? throw new ArgumentException("The certificate fingerprint must be a 64-character SHA-256 hexadecimal value.", nameof(fingerprint));

        var document = await LoadAsync(cancellationToken).ConfigureAwait(false);
        var existing = document.Targets.FirstOrDefault(target =>
            string.Equals(target.DeviceId, deviceId, StringComparison.Ordinal) &&
            string.Equals(target.CertificateSha256Fingerprint, fingerprint, StringComparison.Ordinal));
        if (existing is not null)
        {
            var refreshed = existing with { Endpoint = endpoint.ToString(), UpdatedAtUtc = DateTimeOffset.UtcNow };
            document.Targets[document.Targets.IndexOf(existing)] = refreshed;
            RemoveSupersededFingerprints(document, refreshed);
            await SaveAsync(document, cancellationToken).ConfigureAwait(false);
            return refreshed;
        }

        var sameDeviceTargets = document.Targets
            .Where(target => string.Equals(target.DeviceId, deviceId, StringComparison.Ordinal))
            .ToArray();
        if (sameDeviceTargets.Length > 0)
        {
            var retained = sameDeviceTargets.FirstOrDefault(target =>
                    string.Equals(target.Name, document.CurrentTarget, StringComparison.OrdinalIgnoreCase))
                ?? sameDeviceTargets[0];
            var replaced = retained with
            {
                Endpoint = endpoint.ToString(),
                CertificateSha256Fingerprint = fingerprint,
                UpdatedAtUtc = DateTimeOffset.UtcNow,
            };
            document.Targets[document.Targets.IndexOf(retained)] = replaced;
            RemoveSupersededFingerprints(document, replaced);
            await SaveAsync(document, cancellationToken).ConfigureAwait(false);
            return replaced;
        }

        var name = CreateAutomaticName(deviceId, fingerprint, document.Targets);
        var profile = new ControllerTargetProfile(name, deviceId, endpoint.ToString(), fingerprint, DateTimeOffset.UtcNow);
        document.Targets.Add(profile);
        document.CurrentTarget ??= profile.Name;
        await SaveAsync(document, cancellationToken).ConfigureAwait(false);
        return profile;
    }

    public async Task<ControllerTargetProfile> SetCurrentAsync(string name, CancellationToken cancellationToken = default)
    {
        ValidateName(name);
        var document = await LoadAsync(cancellationToken).ConfigureAwait(false);
        var profile = document.Targets.FirstOrDefault(target => string.Equals(target.Name, name, StringComparison.OrdinalIgnoreCase))
            ?? throw new KeyNotFoundException($"Target '{name}' was not found.");
        document.CurrentTarget = profile.Name;
        await SaveAsync(document, cancellationToken).ConfigureAwait(false);
        return profile;
    }

    public async Task<ControllerTargetProfile> RefreshEndpointAsync(
        string name,
        IPEndPoint endpoint,
        CancellationToken cancellationToken = default)
    {
        ValidateName(name);
        ArgumentNullException.ThrowIfNull(endpoint);
        var document = await LoadAsync(cancellationToken).ConfigureAwait(false);
        var existing = document.Targets.FirstOrDefault(target => string.Equals(target.Name, name, StringComparison.OrdinalIgnoreCase))
            ?? throw new KeyNotFoundException($"Target '{name}' was not found.");
        var refreshed = existing with { Endpoint = endpoint.ToString(), UpdatedAtUtc = DateTimeOffset.UtcNow };
        document.Targets[document.Targets.IndexOf(existing)] = refreshed;
        await SaveAsync(document, cancellationToken).ConfigureAwait(false);
        return refreshed;
    }

    private async Task<TargetDocument> LoadAsync(CancellationToken cancellationToken)
    {
        if (!File.Exists(path))
        {
            return new TargetDocument();
        }

        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 16 * 1024, useAsync: true);
        TargetDocument document;
        try
        {
            document = await JsonSerializer.DeserializeAsync<TargetDocument>(stream, JsonOptions, cancellationToken).ConfigureAwait(false)
                ?? throw new InvalidDataException("The controller target profile is empty or invalid.");
        }
        catch (JsonException exception)
        {
            throw new InvalidDataException("The controller target profile contains invalid JSON.", exception);
        }
        if (document.Version != CurrentVersion)
        {
            throw new InvalidDataException($"Unsupported controller target profile version: {document.Version}.");
        }

        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var target in document.Targets)
        {
            ValidateName(target.Name);
            var normalizedFingerprint = TargetValueParser.NormalizeFingerprint(target.CertificateSha256Fingerprint);
            if (!names.Add(target.Name) || string.IsNullOrWhiteSpace(target.DeviceId) ||
                !IPEndPoint.TryParse(target.Endpoint, out _) || normalizedFingerprint is null ||
                !string.Equals(normalizedFingerprint, target.CertificateSha256Fingerprint, StringComparison.Ordinal))
            {
                throw new InvalidDataException("The controller target profile contains an invalid or duplicate target.");
            }
        }

        if (document.CurrentTarget is not null && !names.Contains(document.CurrentTarget))
        {
            throw new InvalidDataException("The current controller target does not exist in the target profile.");
        }

        return document;
    }

    private async Task SaveAsync(TargetDocument document, CancellationToken cancellationToken)
    {
        var directory = Path.GetDirectoryName(path)!;
        Directory.CreateDirectory(directory);
        var temporaryPath = Path.Combine(directory, $".{FileName}.{Guid.NewGuid():N}.tmp");
        try
        {
            await using (var stream = new FileStream(temporaryPath, FileMode.CreateNew, FileAccess.Write, FileShare.None, 16 * 1024, useAsync: true))
            {
                await JsonSerializer.SerializeAsync(stream, document, JsonOptions, cancellationToken).ConfigureAwait(false);
                await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
            }
            File.Move(temporaryPath, path, overwrite: true);
        }
        finally
        {
            File.Delete(temporaryPath);
        }
    }

    private static void ValidateName(string name)
    {
        if (string.IsNullOrWhiteSpace(name) || name.Length > 64 ||
            name.Any(character => !(char.IsAsciiLetterOrDigit(character) || character is '-' or '_' or '.')))
        {
            throw new ArgumentException("Target names must contain 1-64 ASCII letters, digits, dots, underscores, or hyphens.", nameof(name));
        }
    }

    private static void RemoveSupersededFingerprints(TargetDocument document, ControllerTargetProfile retained)
    {
        var superseded = document.Targets
            .Where(target =>
                !ReferenceEquals(target, retained) &&
                !string.Equals(target.Name, retained.Name, StringComparison.OrdinalIgnoreCase) &&
                string.Equals(target.DeviceId, retained.DeviceId, StringComparison.Ordinal) &&
                !string.Equals(target.CertificateSha256Fingerprint, retained.CertificateSha256Fingerprint, StringComparison.Ordinal))
            .ToArray();
        if (superseded.Length == 0)
        {
            return;
        }

        if (document.CurrentTarget is not null && superseded.Any(target =>
                string.Equals(target.Name, document.CurrentTarget, StringComparison.OrdinalIgnoreCase)))
        {
            document.CurrentTarget = retained.Name;
        }
        foreach (var target in superseded)
        {
            document.Targets.Remove(target);
        }
    }

    private static string CreateAutomaticName(
        string deviceId,
        string fingerprint,
        IReadOnlyCollection<ControllerTargetProfile> targets)
    {
        var normalizedDeviceId = new string(deviceId
            .Where(char.IsAsciiLetterOrDigit)
            .Take(16)
            .Select(char.ToLowerInvariant)
            .ToArray());
        var stem = $"agent-{(normalizedDeviceId.Length == 0 ? fingerprint[..12].ToLowerInvariant() : normalizedDeviceId)}";
        if (!targets.Any(target => string.Equals(target.Name, stem, StringComparison.OrdinalIgnoreCase)))
        {
            return stem;
        }

        var fingerprintSuffix = fingerprint[..8].ToLowerInvariant();
        var candidate = $"{stem}-{fingerprintSuffix}";
        if (!targets.Any(target => string.Equals(target.Name, candidate, StringComparison.OrdinalIgnoreCase)))
        {
            return candidate;
        }

        for (var suffix = 2; ; suffix++)
        {
            candidate = $"{stem}-{fingerprintSuffix}-{suffix}";
            if (!targets.Any(target => string.Equals(target.Name, candidate, StringComparison.OrdinalIgnoreCase)))
            {
                return candidate;
            }
        }
    }

    private sealed class TargetDocument
    {
        public int Version { get; set; } = CurrentVersion;
        public string? CurrentTarget { get; set; }
        public List<ControllerTargetProfile> Targets { get; set; } = [];
    }
}

internal static class TargetValueParser
{
    public static string? NormalizeFingerprint(string? value)
    {
        if (value is null) return null;
        var normalized = value.Replace(":", string.Empty, StringComparison.Ordinal).Trim();
        return normalized.Length == 64 && normalized.All(Uri.IsHexDigit)
            ? normalized.ToUpperInvariant()
            : null;
    }
}
