using OmegaConf.NET.Ast;
using OmegaConf.NET.Binding;

namespace OmegaConf.NET.MultiRun;

/// <summary>
/// Represents a single parameter combination in a multi-run sweep matrix.
/// </summary>
public sealed record SweepCombination(IReadOnlyDictionary<string, string> Parameters);

/// <summary>
/// Implements Hydra-style multi-run parameter sweeps (<c>-m</c> / <c>--multirun</c>),
/// computing the Cartesian product across comma-separated CLI overrides (e.g. <c>db=staging,prod workers=2,4,8</c>).
/// </summary>
public static class SweepEngine
{
    /// <summary>
    /// Checks if the CLI arguments include the multi-run flag (<c>-m</c> or <c>--multirun</c>).
    /// </summary>
    public static bool IsMultiRun(IEnumerable<string> args)
    {
        return args.Any(a => a == "-m" || a == "--multirun");
    }

    /// <summary>
    /// Filters out <c>-m</c> / <c>--multirun</c> and separates static arguments from sweep parameters containing comma-separated choices.
    /// Bracketed list literals (e.g. <c>tags=[a,b,c]</c>) are preserved as single non-sweep values.
    /// </summary>
    public static (List<string> NonSweepArgs, Dictionary<string, List<string>> SweepMatrix) ParseSweepArgs(IEnumerable<string> args)
    {
        var nonSweepArgs = new List<string>();
        var sweepMatrix = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);

        bool isMultiRunMode = false;
        var cleanArgs = new List<string>();

        foreach (var arg in args)
        {
            if (arg == "-m" || arg == "--multirun")
            {
                isMultiRunMode = true;
            }
            else
            {
                cleanArgs.Add(arg);
            }
        }

        foreach (var arg in cleanArgs)
        {
            int eqIdx = arg.IndexOf('=');
            if (isMultiRunMode && eqIdx > 0 && arg.Contains(','))
            {
                string key = arg[..eqIdx].Trim();
                string rawValues = arg[(eqIdx + 1)..].Trim();

                // Guard: Do not split bracketed list literals like tags=[a,b,c] into separate sweep runs
                if (!(rawValues.StartsWith('[') && rawValues.EndsWith(']')))
                {
                    var values = SplitTopLevelCommas(rawValues);
                    if (values.Count > 1)
                    {
                        sweepMatrix[key] = values;
                        continue;
                    }
                }
            }

            nonSweepArgs.Add(arg);
        }

        return (nonSweepArgs, sweepMatrix);
    }

    /// <summary>
    /// Splits a comma-separated string only at top-level commas (outside of any nested brackets, parentheses, or braces).
    /// </summary>
    private static List<string> SplitTopLevelCommas(string input)
    {
        var result = new List<string>();
        int depth = 0;
        int lastStart = 0;

        for (int i = 0; i < input.Length; i++)
        {
            char c = input[i];
            if (c is '[' or '(' or '{') depth++;
            else if (c is ']' or ')' or '}') depth--;
            else if (c == ',' && depth == 0)
            {
                string part = input[lastStart..i].Trim();
                if (!string.IsNullOrEmpty(part)) result.Add(part);
                lastStart = i + 1;
            }
        }

        string tail = input[lastStart..].Trim();
        if (!string.IsNullOrEmpty(tail)) result.Add(tail);

        return result;
    }

    /// <summary>
    /// Generates the Cartesian product of all parameter lists in <paramref name="sweepMatrix"/>.
    /// </summary>
    public static List<Dictionary<string, string>> GenerateCartesianProduct(Dictionary<string, List<string>> sweepMatrix)
    {
        var result = new List<Dictionary<string, string>>();
        if (sweepMatrix.Count == 0)
        {
            result.Add(new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase));
            return result;
        }

        var keys = sweepMatrix.Keys.ToList();
        GenerateCombinations(sweepMatrix, keys, 0, new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase), result);
        return result;
    }

    /// <summary>
    /// Recursive backtracking helper that populates <paramref name="accumulator"/> with every key-value permutation.
    /// </summary>
    private static void GenerateCombinations(
        Dictionary<string, List<string>> matrix,
        List<string> keys,
        int keyIndex,
        Dictionary<string, string> currentCombination,
        List<Dictionary<string, string>> accumulator)
    {
        if (keyIndex >= keys.Count)
        {
            accumulator.Add(new Dictionary<string, string>(currentCombination, StringComparer.OrdinalIgnoreCase));
            return;
        }

        string currentKey = keys[keyIndex];
        var values = matrix[currentKey];

        foreach (var val in values)
        {
            currentCombination[currentKey] = val;
            GenerateCombinations(matrix, keys, keyIndex + 1, currentCombination, accumulator);
            currentCombination.Remove(currentKey);
        }
    }
}
