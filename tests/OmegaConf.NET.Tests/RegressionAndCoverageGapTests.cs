using FluentAssertions;
using OmegaConf.NET.Ast;
using OmegaConf.NET.Attributes;
using OmegaConf.NET.Common;
using OmegaConf.NET.Diff;
using OmegaConf.NET.Interpolation;
using OmegaConf.NET.MultiRun;
using OmegaConf.NET.Operators;
using OmegaConf.NET.Parsers;
using OmegaConf.NET.Schema;
using Xunit;

namespace OmegaConf.NET.Tests;

/// <summary>
/// Regression tests for the defects fixed during the Hydra-parity audit, plus coverage gaps
/// that previously had only a shallow (or no) test.
/// </summary>
public class RegressionAndCoverageGapTests
{
    // ------------------------------------------------------------------------
    // Regression tests for the five fixed source defects
    // ------------------------------------------------------------------------

    [Fact]
    public void Regression_YamlMalformedInput_ThrowsConfigSyntaxException_NotBle()
    {
        // Unclosed flow sequence inside a block used to leak a bare InvalidOperationException.
        string malformed = "a:\n  - 1\n  - [2, 3\nb: 1";

        var act = () => new YamlFormatParser().Parse(malformed);

        act.Should().Throw<ConfigSyntaxException>();
    }

    [Theory]
    [InlineData("flag: yes", true)]
    [InlineData("flag: no", false)]
    [InlineData("flag: on", true)]
    [InlineData("flag: off", false)]
    [InlineData("flag: YES", true)]
    [InlineData("flag: Off", false)]
    public void Regression_YamlBooleanSpellings_AreParsedAsBoolean(string yaml, bool expected)
    {
        // YAML 1.1 spellings must map to boolean, matching OmegaConf/PyYAML.
        var obj = (ConfigObjectNode)new YamlFormatParser().Parse(yaml);
        var valueNode = (ConfigValueNode)obj.GetChild("flag")!;

        valueNode.Value.Should().Be(expected);
    }

    [Fact]
    public void Regression_EvalModulo_PreservesFloatingPointOperands()
    {
        // ${eval: 5.5 % 2.0} must be 1.5, not the integer-rounding artifact 0.
        var root = new ConfigObjectNode();
        root.SetChild("result", new ConfigValueNode("${eval: 5.5 % 2.0}"));

        new InterpolationEngine(root).ResolveAll();

        // The eval resolver returns a string; verify the numeric result is 1.5 (the bug produced "0").
        ((ConfigValueNode)root.GetChild("result")!).Value.Should().Be("1.5");
    }

    [Theory]
    [InlineData("${eval: 5 / 0}")]
    [InlineData("${eval: 5 % 0}")]
    public void Regression_EvalDivisionByZero_ThrowsConfigSyntaxException(string expr)
    {
        var root = new ConfigObjectNode();
        root.SetChild("result", new ConfigValueNode(expr));

        var act = () => new InterpolationEngine(root).ResolveAll();

        act.Should().Throw<ConfigSyntaxException>();
    }

    [Theory]
    [InlineData(long.MaxValue, 1L, ConfigOperator.Add)]
    [InlineData(long.MaxValue, 2L, ConfigOperator.Multiply)]
    [InlineData(long.MaxValue, -1L, ConfigOperator.Subtract)]
    public void Regression_LongNumericOverflow_ThrowsOverflowException(object baseValue, object overrideValue, ConfigOperator op)
    {
        // Arithmetic must not silently wrap; checked context surfaces the overflow.
        var baseNode = new ConfigValueNode(baseValue);
        var overrideNode = new ConfigValueNode(overrideValue, op);

        var act = () => OperatorEngine.Apply(baseNode, overrideNode);

        act.Should().Throw<OverflowException>();
    }

    // ------------------------------------------------------------------------
    // Coverage gaps: ConfigDiffer branches
    // ------------------------------------------------------------------------

    [Fact]
    public void ConfigDiffer_SiblingAndIdenticalTrees_NoChangesReported()
    {
        var a = new ConfigObjectNode();
        a.SetChild("host", new ConfigValueNode("localhost"));
        a.SetChild("port", new ConfigValueNode(5432L));

        var b = new ConfigObjectNode();
        b.SetChild("host", new ConfigValueNode("localhost"));
        b.SetChild("port", new ConfigValueNode(5432L));

        var diff = ConfigDiffer.Compare(a, b);

        diff.HasChanges.Should().BeFalse();
        diff.ToString().Should().Be("No configuration differences.");
    }

    [Fact]
    public void ConfigDiffer_NestedListModification_IsReportedAsModified()
    {
        var baseObj = new ConfigObjectNode();
        baseObj.SetChild("items", new ConfigListNode([new ConfigValueNode(1L), new ConfigValueNode(2L), new ConfigValueNode(3L)]));

        var targetObj = new ConfigObjectNode();
        targetObj.SetChild("items", new ConfigListNode([new ConfigValueNode(1L), new ConfigValueNode(2L), new ConfigValueNode(4L)]));

        var diff = ConfigDiffer.Compare(baseObj, targetObj);

        diff.HasChanges.Should().BeTrue();
        diff.Entries.Should().ContainSingle().Which.ChangeType.Should().Be(ConfigDiffChangeType.Modified);
        diff.ToString().Should().Contain("items:");
        diff.ToString().Should().Contain("->");
    }

    [Fact]
    public void ConfigDiffer_ScalarToScalarTypeChange_IsReportedAsModified()
    {
        var baseObj = new ConfigObjectNode();
        baseObj.SetChild("value", new ConfigValueNode(123L));

        var targetObj = new ConfigObjectNode();
        targetObj.SetChild("value", new ConfigValueNode("abc"));

        var diff = ConfigDiffer.Compare(baseObj, targetObj);

        diff.Entries.Should().ContainSingle().Which.ChangeType.Should().Be(ConfigDiffChangeType.Modified);
        diff.ToString().Should().Contain("value: 123 -> \"abc\"");
    }

    [Fact]
    public void ConfigDiffer_MissingMarker_IsRenderedAsQuestionMarks()
    {
        var baseObj = new ConfigObjectNode();
        baseObj.SetChild("token", new ConfigValueNode(ConfigValueNode.MissingMarker));

        var targetObj = new ConfigObjectNode();
        targetObj.SetChild("token", new ConfigValueNode("settled"));

        var diff = ConfigDiffer.Compare(baseObj, targetObj);

        diff.ToString().Should().Contain("???");
    }

    // ------------------------------------------------------------------------
    // Coverage gap: JSON Schema type mapping (enum, collections, ignored, aliased)
    // ------------------------------------------------------------------------

    public enum TestColor
    {
        Red,
        Green,
        Blue
    }

    public class SchemaFixture
    {
        public string Name { get; set; } = "";

        public TestColor Color { get; set; }

        public List<int> Ports { get; set; } = [];

        public Dictionary<string, string> Headers { get; set; } = [];

        [ConfigIgnore]
        public string Secret { get; set; } = "";

        [ConfigKey("ssl_port")]
        public int SslPort { get; set; }
    }

    [Fact]
    public void JsonSchema_MapsCollections_Enums_IgnoredKeys_AndAliases()
    {
        string schema = JsonSchemaGenerator.GenerateSchema<SchemaFixture>();

        // enum members exposed as an allowed-values list (property keys are snake_case)
        schema.Should().Contain("\"color\":");
        schema.Should().Contain("\"enum\"");
        schema.Should().Contain("\"Red\"");
        schema.Should().Contain("\"Green\"");
        schema.Should().Contain("\"Blue\"");

        // List<T> -> array with items
        schema.Should().Contain("\"ports\":");
        schema.Should().Contain("\"type\": \"array\"");
        schema.Should().Contain("\"items\"");

        // Dictionary -> object with additionalProperties
        schema.Should().Contain("\"headers\":");
        schema.Should().Contain("\"additionalProperties\"");

        // [ConfigIgnore] key is absent
        schema.Should().NotContain("\"Secret\"");

        // [ConfigKey] alias wins over snake_case
        schema.Should().Contain("\"ssl_port\"");
        schema.Should().NotContain("\"ssl_port_lower\"");
    }

    // ------------------------------------------------------------------------
    // Coverage gap: SweepEngine range()/linspace() validation throws
    // ------------------------------------------------------------------------

    [Theory]
    [InlineData("workers=range(1, 10, 0)")]   // zero step
    [InlineData("lr=linspace(0, 1, 0)")]      // zero count
    public void SweepValidation_ThrowsOnZeroStepOrCount(string arg)
    {
        Action act = () => { SweepEngine.ParseSweepArgs(new[] { "-m", arg }); };

        act.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void SweepValidation_ThrowsOnNonNumericGenerator()
    {
        Action act = () => { SweepEngine.ParseSweepArgs(new[] { "-m", "workers=range(abc)" }); };

        act.Should().Throw<FormatException>();
    }

    // ------------------------------------------------------------------------
    // Coverage gap: ${env:VAR} with no default and unset var throws
    // ------------------------------------------------------------------------

    [Fact]
    public void Interpolation_UnsetEnvWithoutDefault_ThrowsKeyNotFoundException()
    {
        const string unset = "OMEGACONF_AUDIT_UNSET_VAR";
        Environment.SetEnvironmentVariable(unset, null);

        var root = new ConfigObjectNode();
        root.SetChild("value", new ConfigValueNode("${env:" + unset + "}"));

        var act = () => new InterpolationEngine(root).ResolveAll();

        act.Should().Throw<KeyNotFoundException>();
    }
}
