namespace Rc.Cli.Targets;

using System.Net;

internal sealed record TargetArgumentResolution(bool Success, string[] Arguments, string? Error)
{
    public static TargetArgumentResolution Unchanged(string[] arguments) => new(true, arguments, null);
    public static TargetArgumentResolution Failed(string error) => new(false, [], error);
}

internal sealed class TargetArgumentResolver(ControllerTargetStore store)
{
    private static readonly HashSet<string> CommandsWithDirectEndpoint = new(StringComparer.Ordinal)
    {
        "probe", "pair", "exec",
    };

    private static readonly HashSet<string> CommandsWithOperationThenEndpoint = new(StringComparer.Ordinal)
    {
        "job", "fs", "copy", "ui", "update",
    };

    public async Task<TargetArgumentResolution> ResolveAsync(string[] arguments, CancellationToken cancellationToken = default)
    {
        if (arguments.Length == 0 || arguments[0] is "target" or "discover")
        {
            return TargetArgumentResolution.Unchanged(arguments);
        }

        var endpointIndex = CommandsWithDirectEndpoint.Contains(arguments[0])
            ? 1
            : CommandsWithOperationThenEndpoint.Contains(arguments[0]) ? 2 : -1;
        if (endpointIndex < 0)
        {
            return TargetArgumentResolution.Unchanged(arguments);
        }

        if (arguments.Length < endpointIndex)
        {
            return TargetArgumentResolution.Unchanged(arguments);
        }

        if (arguments.Length > endpointIndex && IPEndPoint.TryParse(arguments[endpointIndex], out _))
        {
            return TargetArgumentResolution.Unchanged(arguments);
        }

        var snapshot = await store.GetSnapshotAsync(cancellationToken).ConfigureAwait(false);
        ControllerTargetProfile? profile;
        var replaceEndpoint = false;
        if (arguments.Length > endpointIndex && snapshot.Targets.FirstOrDefault(target =>
                string.Equals(target.Name, arguments[endpointIndex], StringComparison.OrdinalIgnoreCase)) is { } named)
        {
            profile = named;
            replaceEndpoint = true;
        }
        else
        {
            profile = snapshot.CurrentTarget is null
                ? null
                : snapshot.Targets.FirstOrDefault(target =>
                    string.Equals(target.Name, snapshot.CurrentTarget, StringComparison.OrdinalIgnoreCase));
        }

        if (profile is null)
        {
            return TargetArgumentResolution.Unchanged(arguments);
        }

        var expanded = arguments.ToList();
        if (replaceEndpoint)
        {
            expanded[endpointIndex] = profile.Endpoint;
        }
        else
        {
            expanded.Insert(endpointIndex, profile.Endpoint);
        }

        var fingerprintIndex = -1;
        for (var index = 0; index < expanded.Count; index++)
        {
            if (!string.Equals(expanded[index], "--fingerprint", StringComparison.Ordinal))
            {
                continue;
            }

            if (fingerprintIndex >= 0)
            {
                return TargetArgumentResolution.Failed("Only one --fingerprint option is allowed for a target profile.");
            }

            fingerprintIndex = index;
        }

        if (fingerprintIndex >= 0)
        {
            if (fingerprintIndex + 1 >= expanded.Count)
            {
                return TargetArgumentResolution.Failed("--fingerprint requires a value.");
            }
            var explicitFingerprint = TargetValueParser.NormalizeFingerprint(expanded[fingerprintIndex + 1]);
            if (!string.Equals(explicitFingerprint, profile.CertificateSha256Fingerprint, StringComparison.Ordinal))
            {
                return TargetArgumentResolution.Failed($"The explicit TLS fingerprint does not match target '{profile.Name}'.");
            }
        }
        else
        {
            expanded.Add("--fingerprint");
            expanded.Add(profile.CertificateSha256Fingerprint);
        }

        return new TargetArgumentResolution(true, expanded.ToArray(), null);
    }
}
