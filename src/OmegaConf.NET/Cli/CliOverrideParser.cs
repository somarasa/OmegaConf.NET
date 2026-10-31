using System.Globalization;
using OmegaConf.NET.Ast;
using OmegaConf.NET.Common;
using OmegaConf.NET.Operators;

namespace OmegaConf.NET.Cli;

/// <summary>
/// Parses command-line arguments (<c>args[]</c>) into two categories:
/// 1. Pre-composition Config Group selections (<c>db=production/postgres</c>, <c>db@primary=postgres</c>, <c>+experiment@_global_=exp1</c>) and group exclusions (<c>~db</c>).
/// 2. Post-composition deep property overrides, key deletions (<c>~db.password</c>), struct-mode additions (<c>+new_key=1</c>, <c>++upsert_key=2</c>), and mutation operators (<c>db.port=5433</c>, <c>workers+=4</c>).
/// </summary>
public static class CliOverrideParser
{
    /// <summary>
    /// Scans CLI arguments before tree composition to extract group path selections (<c>group=path</c>, <c>group@pkg=path</c>)
    /// and group exclusions (<c>~group</c>), placing remaining value overrides and deep deletions into <paramref name="remainingValueOverrides"/>.
    /// </summary>
    public static void ExtractGroupSelections(
        IEnumerable<string> args,
        IDictionary<string, string> selectedPaths,
        ISet<string> droppedGroups,
        List<string> remainingValueOverrides,
        string? rootDir = null,
        IEnumerable<string>? searchPaths = null)
    {
        var allSearchDirs = new List<string>();
        if (!string.IsNullOrEmpty(rootDir)) allSearchDirs.Add(rootDir);
        if (searchPaths != null)
        {
            foreach (var sp in searchPaths)
            {
                if (!string.IsNullOrWhiteSpace(sp)) allSearchDirs.Add(sp);
            }
        }

        foreach (var arg in args)
        {
            if (string.IsNullOrWhiteSpace(arg)) continue;

            string trimmed = arg.Trim();

            // 1. Check Hydra tilde syntax (~group, ~deep.key.path, ~items[0], or conditional ~key=expected_val)
            if (trimmed.StartsWith('~'))
            {
                string target = trimmed[1..].Trim();
                if (!target.Contains('.') && !target.Contains('[') && !target.Contains('='))
                {
                    droppedGroups.Add(target);
                }
                // Also forward to remainingValueOverrides so deep properties, list elements, or conditional deletions are processed
                remainingValueOverrides.Add(trimmed);
                continue;
            }

            // 2. Parse the operator to distinguish plain '=' assignments from mutation operators (+=, *=, !=, etc.)
            var (rawPath, op, val) = OperatorParser.ParseAssignment(trimmed);
            string cleanPath = rawPath.TrimStart('+');
            int atIdx = cleanPath.IndexOf('@');
            string groupCandidate = atIdx > 0 ? cleanPath[..atIdx] : cleanPath;

            if (op == ConfigOperator.Assign && !groupCandidate.Contains('.') && !groupCandidate.Contains('['))
            {
                // Explicit @package redirection (e.g. db@primary_db=postgres or +experiment@_global_=fast)
                if (atIdx > 0)
                {
                    selectedPaths[cleanPath] = val;
                    continue;
                }

                // Explicit subfolder path selection (e.g. db=staging/postgres), excluding escaped literals like \???
                if ((val.Contains('/') || val.Contains('\\')) && !val.StartsWith('\\'))
                {
                    selectedPaths[cleanPath] = val;
                    continue;
                }

                // If any search path contains a subdirectory matching 'groupCandidate' (e.g. configs/db/), treat as a config group selection
                if (allSearchDirs.Any(d => Directory.Exists(Path.Combine(d, groupCandidate))))
                {
                    selectedPaths[cleanPath] = val;
                    continue;
                }

                // Otherwise record as a candidate group selection (if not prefixed with +/++) AND keep in remainingValueOverrides
                // in case it is a top-level scalar property (e.g. app_name=MyService)
                if (!rawPath.StartsWith('+'))
                {
                    selectedPaths[cleanPath] = val;
                }
            }

            remainingValueOverrides.Add(trimmed);
        }
    }

    /// <summary>
    /// Applies scalar, list, and deep dot/bracket-path property overrides, key deletions (<c>~key.path</c>, <c>~items[0]</c>, <c>~key=expected</c>),
    /// struct-mode modifiers (<c>+key</c>, <c>++key</c>), and mutation operators to the composed configuration tree.
    /// </summary>
    /// <param name="root">The composed configuration root object.</param>
    /// <param name="overrideArgs">The CLI value override arguments.</param>
    /// <param name="structMode">Whether strict Struct Mode is active (forbidding creation of undeclared keys without <c>+</c> or <c>++</c>).</param>
    /// <param name="droppedGroups">Optional set of config groups that were already dropped during composition.</param>
    public static void ApplyValueOverrides(
        ConfigObjectNode root,
        IEnumerable<string> overrideArgs,
        bool structMode = false,
        ISet<string>? droppedGroups = null)
    {
        bool effectiveStructMode = structMode || root.IsStruct;

        foreach (var arg in overrideArgs)
        {
            if (string.IsNullOrWhiteSpace(arg)) continue;

            string trimmed = arg.Trim();

            // Handle key/subtree/list-element deletion (~key, ~db.password, ~items[-1], or ~key=expected_value)
            if (trimmed.StartsWith('~'))
            {
                int eqIdx = trimmed.IndexOf('=');
                string pathToDelete = eqIdx > 0 ? trimmed[1..eqIdx].Trim() : trimmed[1..].Trim();
                string? expectedRawValue = eqIdx > 0 ? trimmed[(eqIdx + 1)..].Trim() : null;

                bool wasDroppedGroup = expectedRawValue == null && droppedGroups != null && droppedGroups.Contains(pathToDelete);
                bool removed = RemoveDeepProperty(root, pathToDelete, expectedRawValue);

                if (!removed && (!wasDroppedGroup || expectedRawValue != null))
                {
                    if (expectedRawValue != null || effectiveStructMode)
                    {
                        throw new ConfigStructException(
                            pathToDelete,
                            expectedRawValue != null
                                ? $"Could not delete '~{pathToDelete}={expectedRawValue}': key does not exist or its value does not match '{expectedRawValue}'."
                                : $"Cannot delete '~{pathToDelete}': key does not exist in struct-locked configuration.",
                            new SourceInfo("(cli)", 0, 0));
                    }
                }
                continue;
            }

            var (rawPath, op, rawVal) = OperatorParser.ParseAssignment(trimmed);
            bool isForceUpsert = rawPath.StartsWith("++", StringComparison.Ordinal);
            bool isStrictAppend = !isForceUpsert && rawPath.StartsWith('+');
            string cleanPath = rawPath.TrimStart('+');

            // Hydra forbids combining +key with extend_list(...)
            if (isStrictAppend && rawVal.StartsWith("extend_list(", StringComparison.OrdinalIgnoreCase))
            {
                throw new ConfigSyntaxException($"Cannot combine append prefix '+' with 'extend_list(...)' in '{trimmed}'.", new SourceInfo("(cli)", 0, 0));
            }

            var overrideNode = CreateNodeFromCliValue(rawVal, op);
            SetDeepProperty(root, cleanPath, overrideNode, isStrictAppend, isForceUpsert, effectiveStructMode);
        }
    }

    /// <summary>
    /// Removes a nested property, dictionary entry, or list element at <paramref name="path"/> from <paramref name="root"/>.
    /// When <paramref name="expectedRawValue"/> is provided (<c>~key=expected</c>), verifies that the target's value matches before removing.
    /// </summary>
    private static bool RemoveDeepProperty(ConfigObjectNode root, string path, string? expectedRawValue = null)
    {
        var steps = ParsePathSteps(path);
        if (steps.Count == 0) return false;

        ConfigNode current = root;
        for (int i = 0; i < steps.Count - 1; i++)
        {
            var next = StepInto(current, steps[i]);
            if (next == null) return false;
            current = next;
        }

        var lastStep = steps[^1];
        if (current is ConfigListNode listNode && int.TryParse(lastStep.Key, out int listIdx))
        {
            int actualIdx = listIdx < 0 ? listNode.Items.Count + listIdx : listIdx;
            if (actualIdx < 0 || actualIdx >= listNode.Items.Count) return false;

            if (expectedRawValue != null && !NodeMatchesExpectedCliValue(listNode.Items[actualIdx], expectedRawValue))
            {
                return false;
            }

            listNode.Items.RemoveAt(actualIdx);
            return true;
        }

        if (current is ConfigObjectNode objNode)
        {
            if (!objNode.TryGetChild(lastStep.Key, out var existingChild) || existingChild == null)
            {
                return false;
            }

            if (expectedRawValue != null && !NodeMatchesExpectedCliValue(existingChild, expectedRawValue))
            {
                return false;
            }

            return objNode.RemoveChild(lastStep.Key);
        }

        return false;
    }

    /// <summary>
    /// Checks whether <paramref name="node"/> matches the expected CLI value in a conditional delete expression (<c>~key=expected</c>).
    /// Distinguishes unescaped <c>???</c> (missing marker) from escaped <c>\???</c> (literal string <c>"???"</c>).
    /// </summary>
    private static bool NodeMatchesExpectedCliValue(ConfigNode node, string expectedRawValue)
    {
        var expectedNode = CreateNodeFromCliValue(expectedRawValue, ConfigOperator.Assign);
        if (node is ConfigValueNode actualVal && expectedNode is ConfigValueNode expVal)
        {
            if (actualVal.IsMissingMarker != expVal.IsMissingMarker)
            {
                return false;
            }
            if (actualVal.IsMissingMarker && expVal.IsMissingMarker)
            {
                return true;
            }
            if (actualVal.Value == null && expVal.Value == null)
            {
                return true;
            }
            return string.Equals(actualVal.Value?.ToString(), expVal.Value?.ToString(), StringComparison.OrdinalIgnoreCase);
        }

        return string.Equals(node.ToString(), expectedNode.ToString(), StringComparison.Ordinal);
    }

    private readonly record struct PathStep(string Key, bool IsBracketIndex);

    /// <summary>
    /// Tokenizes a dot/bracket path such as <c>"items[0].nested[-1]"</c> or <c>"obj[20s_labels]"</c> into navigation steps.
    /// </summary>
    private static List<PathStep> ParsePathSteps(string path)
    {
        var steps = new List<PathStep>();
        string[] dotParts = path.Split('.', StringSplitOptions.RemoveEmptyEntries);

        foreach (var part in dotParts)
        {
            int bracketIdx = part.IndexOf('[');
            if (bracketIdx < 0)
            {
                steps.Add(new PathStep(part, IsBracketIndex: false));
                continue;
            }

            if (bracketIdx > 0)
            {
                steps.Add(new PathStep(part[..bracketIdx], IsBracketIndex: false));
            }

            string rest = part[bracketIdx..];
            while (rest.StartsWith('['))
            {
                int closeIdx = rest.IndexOf(']');
                if (closeIdx < 0) break;
                string inner = rest[1..closeIdx].Trim();
                steps.Add(new PathStep(inner, IsBracketIndex: true));
                rest = rest[(closeIdx + 1)..];
            }
        }

        return steps;
    }

    private static ConfigNode? StepInto(ConfigNode current, PathStep step)
    {
        if (current is ConfigListNode listNode && int.TryParse(step.Key, out int idx))
        {
            int actualIdx = idx < 0 ? listNode.Items.Count + idx : idx;
            return actualIdx >= 0 && actualIdx < listNode.Items.Count ? listNode.Items[actualIdx] : null;
        }

        if (current is ConfigObjectNode objNode && objNode.TryGetChild(step.Key, out var next))
        {
            return next;
        }

        return null;
    }

    /// <summary>
    /// Navigates (or creates) intermediate containers along <paramref name="path"/> (supporting both dot and bracket list/object paths)
    /// and merges <paramref name="overrideNode"/> onto the target leaf property or list element using <see cref="OperatorEngine"/>,
    /// enforcing Hydra <c>+key</c>, <c>++key</c>, and Struct Mode rules.
    /// </summary>
    private static void SetDeepProperty(
        ConfigObjectNode root,
        string path,
        ConfigNode overrideNode,
        bool isStrictAppend,
        bool isForceUpsert,
        bool structMode)
    {
        var steps = ParsePathSteps(path);
        if (steps.Count == 0) return;

        ConfigNode current = root;

        for (int i = 0; i < steps.Count - 1; i++)
        {
            var step = steps[i];
            var next = StepInto(current, step);
            if (next == null)
            {
                if (current is ConfigObjectNode currObj)
                {
                    if ((structMode || currObj.IsStruct) && !isStrictAppend && !isForceUpsert)
                    {
                        throw new ConfigStructException(
                            path,
                            $"Cannot set '{path}': parent container '{step.Key}' does not exist in struct-locked configuration. Use '+{path}=...' or '++{path}=...' to add new keys.",
                            overrideNode.Source);
                    }

                    var newObj = new ConfigObjectNode();
                    if (structMode || currObj.IsStruct)
                    {
                        newObj.SetStruct(true);
                    }
                    currObj.SetChild(step.Key, newObj, allowNewKeyInStruct: true);
                    next = newObj;
                }
                else
                {
                    throw new ConfigStructException(path, $"Cannot navigate through '{step.Key}' in path '{path}'.", overrideNode.Source);
                }
            }
            current = next;
        }

        var lastStep = steps[^1];

        // Case 1: Target container is a ConfigListNode (e.g. items[0]=3, items[-1]=3, or top_level_list.0=b)
        if (current is ConfigListNode targetList && int.TryParse(lastStep.Key, out int listIdx))
        {
            int actualIdx = listIdx < 0 ? targetList.Items.Count + listIdx : listIdx;
            if (actualIdx < 0 || actualIdx >= targetList.Items.Count)
            {
                throw new ConfigStructException(path, $"List index {listIdx} is out of range for '{path}' (count: {targetList.Items.Count}).", overrideNode.Source);
            }

            var existingElem = targetList.Items[actualIdx];
            if (isStrictAppend && !(existingElem is ConfigValueNode { Value: null }))
            {
                throw new ConfigStructException(path, $"Cannot append '+{path}': list element at index {listIdx} is already set.", overrideNode.Source);
            }

            targetList.Items[actualIdx] = OperatorEngine.Apply(existingElem, overrideNode);
            return;
        }

        // Case 2: Target container is a ConfigObjectNode
        if (current is not ConfigObjectNode currentObj)
        {
            throw new ConfigStructException(path, $"Target parent of '{path}' is not an object or list.", overrideNode.Source);
        }

        if (currentObj.TryGetChild(lastStep.Key, out var existing) && existing != null)
        {
            // Guard: Do not overwrite an already-composed config group object with a plain scalar CLI group name
            if (steps.Count == 1 && !lastStep.IsBracketIndex && existing is ConfigObjectNode && overrideNode is ConfigValueNode { Operator: ConfigOperator.Assign })
            {
                return;
            }

            if (isStrictAppend && !(existing is ConfigValueNode { Value: null }))
            {
                throw new ConfigStructException(
                    path,
                    $"Cannot append '+{path}': key already exists in configuration. Use '{path}=...' to override or '++{path}=...' to upsert.",
                    overrideNode.Source);
            }

            currentObj.SetChild(lastStep.Key, OperatorEngine.Apply(existing, overrideNode), allowNewKeyInStruct: true);
        }
        else
        {
            if ((structMode || currentObj.IsStruct) && !isStrictAppend && !isForceUpsert)
            {
                throw new ConfigStructException(
                    path,
                    $"Cannot override '{path}': key does not exist in struct-locked configuration. Use '+{path}=...' to append a new key or '++{path}=...' to upsert.",
                    overrideNode.Source);
            }

            currentObj.SetChild(lastStep.Key, overrideNode, allowNewKeyInStruct: true);
        }
    }

    /// <summary>
    /// Parses a raw CLI value string into a typed <see cref="ConfigNode"/> (supporting <c>???</c>, escaped <c>\???</c>,
    /// <c>null</c>, booleans, 64-bit integers, floating-point numbers, bracketed lists <c>[a,b,c]</c>,
    /// inline objects <c>{a:1}</c>, and Hydra grammar functions <c>int()</c>, <c>float()</c>, <c>str()</c>, <c>bool()</c>, <c>sort()</c>, <c>extend_list()</c>).
    /// </summary>
    private static ConfigNode CreateNodeFromCliValue(string raw, ConfigOperator op)
    {
        var source = new SourceInfo("(cli)", 0, 0);
        string trimmed = raw.Trim();

        // Strip enclosing single or double quotes if present
        if (trimmed.Length >= 2 &&
            ((trimmed.StartsWith('\'') && trimmed.EndsWith('\'')) || (trimmed.StartsWith('"') && trimmed.EndsWith('"'))))
        {
            string unquoted = trimmed[1..^1];
            if (unquoted is @"\???" or @"\\???" or ConfigValueNode.MissingMarker)
            {
                return new ConfigValueNode(unquoted, op, source);
            }
            return new ConfigValueNode(unquoted, op, source) { IsEscapedLiteral = true };
        }

        if (trimmed is ConfigValueNode.MissingMarker or @"\???" or @"\\???")
        {
            return new ConfigValueNode(trimmed, op, source);
        }

        if (string.Equals(trimmed, "null", StringComparison.OrdinalIgnoreCase))
        {
            return new ConfigValueNode(null, op, source);
        }

        // Check Hydra CLI grammar functions: int(...), float(...), str(...), bool(...), sort(...), extend_list(...)
        if (TryEvaluateCliGrammarFunction(trimmed, op, source, out var funcNode) && funcNode != null)
        {
            return funcNode;
        }

        if (bool.TryParse(trimmed, out bool b))
        {
            return new ConfigValueNode(b, op, source);
        }

        if (long.TryParse(trimmed, NumberStyles.Integer, CultureInfo.InvariantCulture, out long l))
        {
            return new ConfigValueNode(l, op, source);
        }

        if (trimmed.Contains('.') && double.TryParse(trimmed, NumberStyles.Float, CultureInfo.InvariantCulture, out double d))
        {
            return new ConfigValueNode(d, op, source);
        }

        // Parse bracketed CLI list literal: e.g. tags=[gpu,fast,cluster]
        if (trimmed.StartsWith('[') && trimmed.EndsWith(']'))
        {
            string inner = trimmed[1..^1];
            var items = SplitTopLevelCliCommas(inner)
                .Select(item => CreateNodeFromCliValue(item, ConfigOperator.Assign));
            return new ConfigListNode(items, op, source);
        }

        // Parse inline CLI object literal: e.g. +items[0]={b:2}
        if (trimmed.StartsWith('{') && trimmed.EndsWith('}'))
        {
            var parsedObj = OmegaConfig.ParseYaml($"_inline_: {trimmed}");
            if (parsedObj.TryGetChild("_inline_", out var inlineChild) && inlineChild is ConfigObjectNode childObj)
            {
                childObj.Operator = op;
                return childObj;
            }
        }

        return new ConfigValueNode(trimmed, op, source);
    }

    /// <summary>
    /// Evaluates Hydra CLI grammar functions: <c>int(x)</c>, <c>float(x)</c>, <c>str(x)</c>, <c>bool(x)</c>,
    /// <c>sort([...][, reverse=true])</c>, and <c>extend_list(a, b, ...)</c>.
    /// </summary>
    private static bool TryEvaluateCliGrammarFunction(string trimmed, ConfigOperator op, SourceInfo source, out ConfigNode? resultNode)
    {
        resultNode = null;
        if (!trimmed.EndsWith(')')) return false;
        int openParen = trimmed.IndexOf('(');
        if (openParen <= 0) return false;

        string funcName = trimmed[..openParen].Trim().ToLowerInvariant();
        string inner = trimmed[(openParen + 1)..^1].Trim();

        switch (funcName)
        {
            case "int":
            {
                var innerVal = CreateNodeFromCliValue(inner, ConfigOperator.Assign) as ConfigValueNode;
                double d = Convert.ToDouble(innerVal?.Value ?? inner, CultureInfo.InvariantCulture);
                resultNode = new ConfigValueNode((long)d, op, source);
                return true;
            }
            case "float":
            {
                var innerVal = CreateNodeFromCliValue(inner, ConfigOperator.Assign) as ConfigValueNode;
                double d = Convert.ToDouble(innerVal?.Value ?? inner, CultureInfo.InvariantCulture);
                resultNode = new ConfigValueNode(d, op, source);
                return true;
            }
            case "str":
            {
                var innerVal = CreateNodeFromCliValue(inner, ConfigOperator.Assign) as ConfigValueNode;
                string s = innerVal?.Value is double dv
                    ? (dv % 1 == 0 ? dv.ToString("0.0", CultureInfo.InvariantCulture) : dv.ToString(CultureInfo.InvariantCulture))
                    : innerVal?.Value?.ToString() ?? inner;
                resultNode = new ConfigValueNode(s, op, source) { IsEscapedLiteral = true };
                return true;
            }
            case "bool":
            {
                var innerVal = CreateNodeFromCliValue(inner, ConfigOperator.Assign) as ConfigValueNode;
                bool b = innerVal?.Value switch
                {
                    bool bv => bv,
                    long lv => lv != 0,
                    double dv => Math.Abs(dv) > 1e-15,
                    string sv => !string.IsNullOrEmpty(sv) && !string.Equals(sv, "false", StringComparison.OrdinalIgnoreCase) && sv != "0",
                    null => false,
                    _ => true
                };
                resultNode = new ConfigValueNode(b, op, source);
                return true;
            }
            case "extend_list":
            {
                var args = SplitTopLevelCliCommas(inner)
                    .Select(item => CreateNodeFromCliValue(item, ConfigOperator.Assign));
                resultNode = new ConfigListNode(args, ConfigOperator.Add, source);
                return true;
            }
            case "sort":
            {
                var args = SplitTopLevelCliCommas(inner);
                bool reverse = false;
                if (args.Count > 0 && args[^1].Replace(" ", "").Equals("reverse=true", StringComparison.OrdinalIgnoreCase))
                {
                    reverse = true;
                    args.RemoveAt(args.Count - 1);
                }
                else if (args.Count > 0 && args[^1].Replace(" ", "").Equals("reverse=false", StringComparison.OrdinalIgnoreCase))
                {
                    args.RemoveAt(args.Count - 1);
                }

                List<ConfigNode> itemsToSort;
                if (args.Count == 1 && args[0].StartsWith('[') && args[0].EndsWith(']'))
                {
                    var listNode = (ConfigListNode)CreateNodeFromCliValue(args[0], ConfigOperator.Assign);
                    itemsToSort = listNode.Items;
                }
                else
                {
                    itemsToSort = args.Select(a => CreateNodeFromCliValue(a, ConfigOperator.Assign)).ToList();
                }

                var sorted = itemsToSort.OrderBy(n => n is ConfigValueNode vn && vn.Value is IComparable cmp ? cmp : n.ToString()).ToList();
                if (reverse) sorted.Reverse();
                resultNode = new ConfigListNode(sorted, op, source);
                return true;
            }
            default:
                return false;
        }
    }

    private static List<string> SplitTopLevelCliCommas(string input)
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
}
