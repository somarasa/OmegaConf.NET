namespace OmegaConf.NET.Common;

/// <summary>
/// Tracks the source origin (file path, line number, and column number) of a configuration node for error reporting and diagnostics.
/// </summary>
public sealed record SourceInfo(string? FilePath, long Line, long Column)
{
    /// <summary>Represents an unknown or dynamically synthesized configuration origin (e.g., CLI override or in-memory object).</summary>
    public static readonly SourceInfo Unknown = new(null, 0, 0);

    /// <summary>Returns a human-readable string representation of the source location (<c>file:line:col</c>).</summary>
    public override string ToString()
    {
        if (string.IsNullOrEmpty(FilePath))
        {
            return Line > 0 ? $"Line {Line}, Col {Column}" : "(dynamic/cli)";
        }

        return Line > 0 ? $"{FilePath}:{Line}:{Column}" : FilePath;
    }
}
