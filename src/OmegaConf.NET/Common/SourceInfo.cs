namespace OmegaConf.NET.Common;

/// <summary>
/// Tracks the source origin of a configuration node for error reporting and diagnostics.
/// </summary>
public sealed record SourceInfo(string? FilePath, long Line, long Column)
{
    public static readonly SourceInfo Unknown = new(null, 0, 0);

    public override string ToString()
    {
        if (string.IsNullOrEmpty(FilePath))
        {
            return Line > 0 ? $"Line {Line}, Col {Column}" : "(dynamic/cli)";
        }

        return Line > 0 ? $"{FilePath}:{Line}:{Column}" : FilePath;
    }
}
