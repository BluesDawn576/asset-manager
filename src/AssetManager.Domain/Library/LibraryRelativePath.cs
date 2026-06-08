namespace AssetManager.Domain.Library;

public sealed record LibraryRelativePath
{
    public static readonly LibraryRelativePath Root = new(string.Empty);

    private LibraryRelativePath(string value)
    {
        Value = value;
    }

    public string Value { get; }

    public bool IsRoot => Value.Length == 0;

    public static LibraryRelativePath Create(string? value)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Trim() == ".")
        {
            return Root;
        }

        var normalized = value.Trim().Replace('\\', '/').Trim('/');
        if (normalized.Length == 0)
        {
            return Root;
        }

        if (Path.IsPathRooted(normalized))
        {
            throw new ArgumentException("Library relative path cannot be rooted.", nameof(value));
        }

        var segments = normalized.Split('/', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        foreach (var segment in segments)
        {
            if (segment is "." or "..")
            {
                throw new ArgumentException("Library relative path cannot contain traversal segments.", nameof(value));
            }
        }

        if (segments.Length > 0
            && string.Equals(segments[0], LibraryLocation.ManagementDirectoryName, StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException("Library relative path cannot point inside the management directory.", nameof(value));
        }

        return new LibraryRelativePath(string.Join('/', segments));
    }

    public LibraryRelativePath Combine(string childPath)
    {
        var child = Create(childPath);
        if (child.IsRoot)
        {
            return this;
        }

        return IsRoot ? child : Create(Value + "/" + child.Value);
    }

    public string ToFullPath(string rootPath)
    {
        var fullRoot = Path.GetFullPath(rootPath);
        var combined = IsRoot
            ? fullRoot
            : Path.GetFullPath(Path.Combine(new[] { fullRoot }.Concat(Value.Split('/')).ToArray()));

        if (!combined.Equals(fullRoot, StringComparison.OrdinalIgnoreCase)
            && !combined.StartsWith(fullRoot + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
            && !combined.StartsWith(fullRoot + Path.AltDirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("Library relative path escaped the library root.");
        }

        return combined;
    }

    public override string ToString()
    {
        return Value;
    }
}
