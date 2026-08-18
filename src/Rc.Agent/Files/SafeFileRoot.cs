namespace Rc.Agent.Files;

public sealed class SafeFileRoot
{
    private static readonly HashSet<string> ReservedNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "CON", "PRN", "AUX", "NUL", "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9",
        "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9",
    };
    private readonly string rootPrefix;

    public SafeFileRoot(string root)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(root);
        Root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
        Directory.CreateDirectory(Root);
        rootPrefix = Path.EndsInDirectorySeparator(Root) ? Root : Root + Path.DirectorySeparatorChar;
    }

    public string Root { get; }

    public string Resolve(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        var validationPath = Path.IsPathFullyQualified(path) ? path[(Path.GetPathRoot(path)?.Length ?? 0)..] : path;
        ValidateSegments(validationPath);
        var full = Path.GetFullPath(Path.IsPathFullyQualified(path) ? path : Path.Combine(Root, path));
        if (!string.Equals(full, Root, StringComparison.OrdinalIgnoreCase) && !full.StartsWith(rootPrefix, StringComparison.OrdinalIgnoreCase))
        {
            throw new UnauthorizedAccessException(
                $"The path '{path}' is outside the configured file root ('{Root}', RC_AGENT_FILE_ROOT). fs/copy only serve paths under the root; for other locations use exec/job or an out-of-band transport (e.g. SCP).");
        }
        RejectReparsePoints(full);
        return full;
    }

    public string ResolveRelative(string rootPath, string relativePath)
    {
        var basePath = Resolve(rootPath);
        if (string.IsNullOrEmpty(relativePath))
        {
            return basePath;
        }
        if (Path.IsPathFullyQualified(relativePath))
        {
            throw new UnauthorizedAccessException("Manifest paths must be relative.");
        }
        ValidateSegments(relativePath);
        var combined = Path.GetFullPath(Path.Combine(basePath, relativePath.Replace('/', Path.DirectorySeparatorChar)));
        var prefix = Path.EndsInDirectorySeparator(basePath) ? basePath : basePath + Path.DirectorySeparatorChar;
        if (!combined.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
        {
            throw new UnauthorizedAccessException("The manifest path escapes its transfer root.");
        }
        RejectReparsePoints(combined);
        return combined;
    }

    public string ToDisplayPath(string fullPath) => Path.GetRelativePath(Root, fullPath).Replace('\\', '/');

    public IReadOnlyList<string> Enumerate(string rootPath, bool recursive)
    {
        var root = Resolve(rootPath);
        if (!Directory.Exists(root))
        {
            throw new DirectoryNotFoundException(rootPath);
        }

        var entries = new List<string>();
        EnumerateDirectory(root, recursive, entries);
        return entries;
    }

    private static void EnumerateDirectory(string directory, bool recursive, List<string> entries)
    {
        foreach (var entry in Directory.EnumerateFileSystemEntries(directory))
        {
            if ((File.GetAttributes(entry) & FileAttributes.ReparsePoint) != 0)
            {
                throw new UnauthorizedAccessException("Reparse points are not allowed in file paths.");
            }

            entries.Add(entry);
            if (recursive && Directory.Exists(entry))
            {
                EnumerateDirectory(entry, true, entries);
            }
        }
    }

    private static void ValidateSegments(string path)
    {
        var trimmed = path.Replace('\\', '/');
        foreach (var segment in trimmed.Split('/', StringSplitOptions.RemoveEmptyEntries))
        {
            if (segment == "..") throw new UnauthorizedAccessException("Parent traversal is not allowed.");
            if (segment == ".") continue;
            if (segment.Contains(':', StringComparison.Ordinal) || segment.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
            {
                throw new ArgumentException("The path contains an unsafe segment.", nameof(path));
            }
            var stem = segment.TrimEnd('.', ' ').Split('.')[0];
            if (ReservedNames.Contains(stem))
            {
                throw new ArgumentException("Windows device names are not valid file paths.", nameof(path));
            }
        }
    }

    private void RejectReparsePoints(string fullPath)
    {
        var current = fullPath;
        while (current.Length >= Root.Length && !string.Equals(current, Root, StringComparison.OrdinalIgnoreCase))
        {
            if (File.Exists(current) || Directory.Exists(current))
            {
                if ((File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                {
                    throw new UnauthorizedAccessException("Reparse points are not allowed in file paths.");
                }
            }
            current = Path.GetDirectoryName(current) ?? Root;
        }
    }
}
