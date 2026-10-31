using System.Globalization;
using System.Text.RegularExpressions;
using OmegaConf.NET.Ast;
using OmegaConf.NET.Binding;

namespace OmegaConf.NET.MultiRun;

/// <summary>
/// Represents a single parameter combination in a multi-run sweep matrix.
/// </summary>
public sealed record SweepCombination(IReadOnlyDictionary<string, string> Parameters);

/// <summary>
/// Implements Hydra-style multi-run parameter sweeps (<c>-m</c> / <c>--multirun</c>),
/// computing the Cartesian product across:
/// - Comma-separated CLI overrides (e.g. <c>db=staging,prod workers=2,4,8</c>)
/// - <c>range(start, stop[, step])</c> numeric sequences
/// - <c>linspace(start, end, count)</c> and <c>interval(start, end[, count])</c> evenly-spaced intervals
/// - <c>choice(a, b, c)</c> explicit option sets (including nested lists <c>choice([1,2], [3,4])</c>)
/// - <c>glob(pattern)</c> dynamic config group file discovery on disk
/// </summary>
public static class SweepEngine
{
    private static readonly string[] SupportedExtensions = [".yaml", ".yml", ".json", ".jsonc", ".toml"];

    /// <summary>
    /// Checks if the CLI arguments include the multi-run flag (<c>-m</c> or <c>--multirun</c>).
    /// </summary>
    public static bool IsMultiRun(IEnumerable<string> args)
    {
        return args.Any(a => a == "-m" || a == "--multirun");
    }

    /// <summary>
    /// Filters out <c>-m</c> / <c>--multirun</c> and separates static arguments from sweep parameters
    /// (comma-separated choices or sweep generator functions <c>range()</c>, <c>linspace()</c>, <c>interval()</c>, <c>choice()</c>, <c>glob()</c>).
    /// Bracketed list literals (e.g. <c>tags=[a,b,c]</c>) are preserved as single non-sweep values.
    /// </summary>
    /// <param name="args">Raw command-line arguments.</param>
    /// <param name="rootDir">Optional root configuration directory used to resolve <c>glob()</c> sweeps against config group folders.</param>
    public static (List<string> NonSweepArgs, Dictionary<string, List<string>> SweepMatrix) ParseSweepArgs(
        IEnumerable<string> args,
        string? rootDir = null)
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
            if (isMultiRunMode && eqIdx > 0)
            {
                string key = arg[..eqIdx].Trim();
                string rawValues = arg[(eqIdx + 1)..].Trim();

                // 1. Check for explicit Hydra sweep functions: range(), linspace(), interval(), choice(), glob()
                if (TryExpandSweepFunction(key, rawValues, rootDir, out var funcValues) && funcValues.Count > 0)
                {
                    sweepMatrix[key] = funcValues;
                    continue;
                }

                // 2. Check for comma-separated sweep choices (excluding bracketed list literals like tags=[a,b,c])
                if (rawValues.Contains(',') && !(rawValues.StartsWith('[') && rawValues.EndsWith(']')))
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
    /// Attempts to evaluate a Hydra sweep generator function (<c>range</c>, <c>linspace</c>, <c>interval</c>, <c>choice</c>, or <c>glob</c>).
    /// </summary>
    private static bool TryExpandSweepFunction(
        string key,
        string rawValues,
        string? rootDir,
        out List<string> expandedValues)
    {
        expandedValues = [];
        if (!rawValues.EndsWith(')')) return false;

        int openParen = rawValues.IndexOf('(');
        if (openParen <= 0) return false;

        string funcName = rawValues[..openParen].Trim().ToLowerInvariant();
        string inner = rawValues[(openParen + 1)..^1].Trim();

        switch (funcName)
        {
            case "choice":
                expandedValues = SplitTopLevelCommas(inner);
                return true;

            case "range":
                expandedValues = ExpandRange(inner);
                return true;

            case "linspace":
            case "interval":
                expandedValues = ExpandLinspace(inner);
                return true;

            case "glob":
                expandedValues = ExpandGlob(key, inner, rootDir);
                return true;

            case "sort":
                expandedValues = ExpandSortSweep(inner);
                return true;

            case "shuffle":
                expandedValues = ExpandShuffleSweep(inner);
                return true;

            default:
                return false;
        }
    }

    private static List<string> ExpandSortSweep(string inner)
    {
        var parts = SplitTopLevelCommas(inner);
        bool reverse = false;
        if (parts.Count > 0 && parts[^1].Replace(" ", "").Equals("reverse=true", StringComparison.OrdinalIgnoreCase))
        {
            reverse = true;
            parts.RemoveAt(parts.Count - 1);
        }
        else if (parts.Count > 0 && parts[^1].Replace(" ", "").Equals("reverse=false", StringComparison.OrdinalIgnoreCase))
        {
            parts.RemoveAt(parts.Count - 1);
        }

        if (parts.All(p => double.TryParse(p, NumberStyles.Float, CultureInfo.InvariantCulture, out _)))
        {
            parts.Sort((a, b) => double.Parse(a, CultureInfo.InvariantCulture).CompareTo(double.Parse(b, CultureInfo.InvariantCulture)));
        }
        else
        {
            parts.Sort(StringComparer.Ordinal);
        }

        if (reverse) parts.Reverse();
        return parts;
    }

    private static List<string> ExpandShuffleSweep(string inner)
    {
        if (inner.StartsWith("choice(", StringComparison.OrdinalIgnoreCase) && inner.EndsWith(')'))
        {
            inner = inner["choice(".Length..^1].Trim();
        }
        var parts = SplitTopLevelCommas(inner);
        return parts.OrderBy(_ => Random.Shared.Next()).ToList();
    }

    /// <summary>
    /// Expands <c>range(stop)</c>, <c>range(start, stop)</c>, or <c>range(start, stop, step)</c> into a sequence of numeric strings.
    /// Supports both integer and floating-point step increments.
    /// </summary>
    private static List<string> ExpandRange(string inner)
    {
        var parts = SplitTopLevelCommas(inner);
        if (parts.Count is < 1 or > 3)
        {
            throw new ArgumentException($"Invalid range() syntax 'range({inner})'. Expected 1 to 3 numeric arguments.");
        }

        // Check if all arguments are integers
        if (parts.All(p => long.TryParse(p, NumberStyles.Integer, CultureInfo.InvariantCulture, out _)))
        {
            long start = parts.Count == 1 ? 0 : long.Parse(parts[0], CultureInfo.InvariantCulture);
            long stop = parts.Count == 1 ? long.Parse(parts[0], CultureInfo.InvariantCulture) : long.Parse(parts[1], CultureInfo.InvariantCulture);
            long step = parts.Count == 3 ? long.Parse(parts[2], CultureInfo.InvariantCulture) : (start <= stop ? 1 : -1);

            if (step == 0)
            {
                throw new ArgumentException("range() step argument cannot be zero.");
            }

            var intResults = new List<string>();
            if (step > 0)
            {
                for (long v = start; v < stop; v += step)
                {
                    intResults.Add(v.ToString(CultureInfo.InvariantCulture));
                }
            }
            else
            {
                for (long v = start; v > stop; v += step)
                {
                    intResults.Add(v.ToString(CultureInfo.InvariantCulture));
                }
            }
            return intResults;
        }
        else
        {
            double start = parts.Count == 1 ? 0.0 : double.Parse(parts[0], CultureInfo.InvariantCulture);
            double stop = parts.Count == 1 ? double.Parse(parts[0], CultureInfo.InvariantCulture) : double.Parse(parts[1], CultureInfo.InvariantCulture);
            double step = parts.Count == 3 ? double.Parse(parts[2], CultureInfo.InvariantCulture) : (start <= stop ? 1.0 : -1.0);

            if (Math.Abs(step) < 1e-15)
            {
                throw new ArgumentException("range() step argument cannot be zero.");
            }

            var floatResults = new List<string>();
            if (step > 0)
            {
                for (double v = start; v < stop - 1e-12; v += step)
                {
                    floatResults.Add(Math.Round(v, 10).ToString(CultureInfo.InvariantCulture));
                }
            }
            else
            {
                for (double v = start; v > stop + 1e-12; v += step)
                {
                    floatResults.Add(Math.Round(v, 10).ToString(CultureInfo.InvariantCulture));
                }
            }
            return floatResults;
        }
    }

    /// <summary>
    /// Expands <c>linspace(start, end, count)</c> or <c>interval(start, end[, count])</c> into evenly-spaced values (inclusive of endpoints).
    /// </summary>
    private static List<string> ExpandLinspace(string inner)
    {
        var parts = SplitTopLevelCommas(inner);
        if (parts.Count is < 2 or > 3)
        {
            throw new ArgumentException($"Invalid linspace/interval syntax '({inner})'. Expected (start, end[, count]).");
        }

        double start = double.Parse(parts[0], CultureInfo.InvariantCulture);
        double end = double.Parse(parts[1], CultureInfo.InvariantCulture);
        int count = parts.Count == 3 ? int.Parse(parts[2], CultureInfo.InvariantCulture) : 5;

        if (count <= 0)
        {
            throw new ArgumentException("linspace/interval count must be greater than zero.");
        }

        var results = new List<string>(count);
        for (int i = 0; i < count; i++)
        {
            double val = count == 1 ? start : start + (end - start) * i / (count - 1);
            results.Add(Math.Round(val, 10).ToString(CultureInfo.InvariantCulture));
        }

        return results;
    }

    /// <summary>
    /// Expands <c>glob(pattern)</c> by discovering matching configuration files in the config group directory on disk.
    /// </summary>
    private static List<string> ExpandGlob(string rawKey, string inner, string? rootDir)
    {
        string cleanKey = rawKey.TrimStart('+');
        int atIdx = cleanKey.IndexOf('@');
        string groupFolder = atIdx > 0 ? cleanKey[..atIdx] : cleanKey;

        string baseRoot = string.IsNullOrEmpty(rootDir) ? Directory.GetCurrentDirectory() : rootDir;
        string groupDir = Path.Combine(baseRoot, groupFolder);

        if (!Directory.Exists(groupDir))
        {
            return [];
        }

        var args = SplitTopLevelCommas(inner);
        string pattern = args.Count > 0 ? args[0].Trim('\'', '"') : "*";
        if (string.IsNullOrEmpty(pattern)) pattern = "*";

        // Convert wildcard pattern (* and ?) into a regular expression
        string regexPattern = "^" + Regex.Escape(pattern).Replace(@"\*", ".*").Replace(@"\?", ".") + "$";
        var regex = new Regex(regexPattern, RegexOptions.IgnoreCase);

        var matchedNames = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var file in Directory.GetFiles(groupDir))
        {
            string ext = Path.GetExtension(file);
            if (!SupportedExtensions.Contains(ext, StringComparer.OrdinalIgnoreCase))
            {
                continue;
            }

            string nameWithoutExt = Path.GetFileNameWithoutExtension(file);
            if (regex.IsMatch(nameWithoutExt))
            {
                matchedNames.Add(nameWithoutExt);
            }
        }

        return matchedNames.ToList();
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
