using OmegaConf.NET.Ast;
using OmegaConf.NET.Binding;

namespace OmegaConf.NET.MultiRun;

public sealed record SweepCombination(IReadOnlyDictionary<string, string> Parameters);

public static class SweepEngine
{
    /// <summary>
    /// Checks if CLI args include the multi-run flag (-m or --multirun).
    /// </summary>
    public static bool IsMultiRun(IEnumerable<string> args)
    {
        return args.Any(a => a == "-m" || a == "--multirun");
    }

    /// <summary>
    /// Filters out -m / --multirun and parses sweep parameters containing commas (e.g. "workers=2,4,8", "db=staging,prod").
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
            // If in multi-run mode and argument has comma-separated values (e.g. workers=2,4 or db=stage,prod)
            if (isMultiRunMode && eqIdx > 0 && arg.Contains(','))
            {
                string key = arg[..eqIdx].Trim();
                string rawValues = arg[(eqIdx + 1)..].Trim();

                var values = rawValues.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();
                if (values.Count > 1)
                {
                    sweepMatrix[key] = values;
                    continue;
                }
            }

            nonSweepArgs.Add(arg);
        }

        return (nonSweepArgs, sweepMatrix);
    }

    /// <summary>
    /// Generates the Cartesian product of all sweep parameters.
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
