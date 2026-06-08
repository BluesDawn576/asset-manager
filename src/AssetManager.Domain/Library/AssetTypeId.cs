namespace AssetManager.Domain.Library;

public sealed record AssetTypeId
{
    public static readonly AssetTypeId Unknown = new("unknown");
    public static readonly AssetTypeId Image = new("image");
    public static readonly AssetTypeId Video = new("video");
    public static readonly AssetTypeId Audio = new("audio");
    public static readonly AssetTypeId Text = new("text");

    private AssetTypeId(string value)
    {
        Value = value;
    }

    public string Value { get; }

    public static AssetTypeId Create(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return Unknown;
        }

        return new AssetTypeId(value.Trim().ToLowerInvariant());
    }

    public override string ToString()
    {
        return Value;
    }
}
