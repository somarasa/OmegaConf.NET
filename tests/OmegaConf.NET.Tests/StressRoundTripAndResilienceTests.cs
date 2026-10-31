using FluentAssertions;
using OmegaConf.NET.Ast;
using OmegaConf.NET.Binding;
using OmegaConf.NET.Common;
using OmegaConf.NET.Diff;
using OmegaConf.NET.Interpolation;
using OmegaConf.NET.Operators;
using OmegaConf.NET.Parsers;
using OmegaConf.NET.Serialization;
using Xunit;

namespace OmegaConf.NET.Tests;

/// <summary>
/// Covers Stress/Concurrency, Round-Trip Serialization, Parameterized Theories,
/// AST Immutability/Cloning, and Malformed Syntax Resilience.
/// </summary>
public class StressRoundTripAndResilienceTests : IDisposable
{
    private readonly string _testDir;

    public StressRoundTripAndResilienceTests()
    {
        _testDir = Path.Combine(Path.GetTempPath(), "omegaconf_stress_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_testDir);
    }

    public void Dispose()
    {
        if (Directory.Exists(_testDir))
        {
            Directory.Delete(_testDir, recursive: true);
        }
    }

    [Theory]
    [InlineData("AppName", "app_name")]
    [InlineData("SSLPort", "ssl_port")]
    [InlineData("HTTPSEndpoint", "https_endpoint")]
    [InlineData("MaxDBConnections", "max_db_connections")]
    [InlineData("Simple", "simple")]
    [InlineData("Already_Snake_Case", "already_snake_case")]
    [InlineData("OAuth2ClientID", "o_auth2_client_id")]
    public void ParameterizedTheory_ToSnakeCase_ShouldConvertAcronymsAndPascalCaseAccurately(string input, string expected)
    {
        ConfigBinder.ToSnakeCase(input).Should().Be(expected);
    }

    [Theory]
    [InlineData("${eval: 10 + 20 * 3}", "70")]
    [InlineData("${eval: (10 + 20) * 3}", "90")]
    [InlineData("${eval: 100 / 4 - 5}", "20")]
    [InlineData("${eval: 15 % 4}", "3")]
    [InlineData("${eval: -10 + 25}", "15")]
    [InlineData("${eval: true && !false}", "True")]
    [InlineData("${eval: 5 == 5 && 3 != 4}", "True")]
    [InlineData("${eval: 10 > 20 || 5 <= 5}", "True")]
    [InlineData("${eval: 'hello' == 'hello'}", "True")]
    public void ParameterizedTheory_EvalExpressions_ShouldEvaluateAllOperatorsCorrectly(string expression, string expected)
    {
        var root = new ConfigObjectNode();
        root.SetChild("result", new ConfigValueNode(expression));

        var engine = new InterpolationEngine(root);
        engine.ResolveAll();

        ((ConfigValueNode)root.GetChild("result")!).Value.Should().Be(expected);
    }

    [Fact]
    public void RoundTripSerialization_YamlJsonToml_ShouldPreserveTreeEquivalence()
    {
        var original = new ConfigObjectNode();
        original.SetChild("service_name", new ConfigValueNode("OrderProcessor"));
        original.SetChild("max_workers", new ConfigValueNode(16L));
        original.SetChild("enable_metrics", new ConfigValueNode(true));
        original.SetChild("threshold", new ConfigValueNode(0.85));

        var tags = new ConfigListNode([
            new ConfigValueNode("production"),
            new ConfigValueNode("eu-central"),
            new ConfigValueNode("critical")
        ]);
        original.SetChild("tags", tags);

        var db = new ConfigObjectNode();
        db.SetChild("host", new ConfigValueNode("orders-db.internal"));
        db.SetChild("port", new ConfigValueNode(5432L));
        original.SetChild("database", db);

        // 1. Round-trip through YAML
        string yaml = original.ToYaml();
        var fromYaml = new YamlFormatParser().Parse(yaml);
        ConfigDiffer.Compare(original, fromYaml).HasChanges.Should().BeFalse();

        // 2. Round-trip through JSON
        string json = original.ToJson(indented: true);
        var fromJson = new JsonFormatParser().Parse(json);
        ConfigDiffer.Compare(original, fromJson).HasChanges.Should().BeFalse();

        // 3. Round-trip through TOML
        string toml = original.ToToml();
        var fromToml = new TomlFormatParser().Parse(toml);
        ConfigDiffer.Compare(original, fromToml).HasChanges.Should().BeFalse();
    }

    [Fact]
    public void AstDeepClone_AndOperatorMerge_ShouldNeverMutateOriginalNodes()
    {
        var baseTree = new ConfigObjectNode();
        var baseDb = new ConfigObjectNode();
        baseDb.SetChild("port", new ConfigValueNode(5432L));
        baseDb.SetChild("roles", new ConfigListNode([new ConfigValueNode("reader")]));
        baseTree.SetChild("db", baseDb);

        var overrideTree = new ConfigObjectNode();
        var overrideDb = new ConfigObjectNode();
        overrideDb.SetChild("port", new ConfigValueNode(1000L, ConfigOperator.Add));
        overrideDb.SetChild("roles", new ConfigListNode([new ConfigValueNode("writer")], ConfigOperator.Add));
        overrideTree.SetChild("db", overrideDb);

        // Apply merge
        var merged = (ConfigObjectNode)OperatorEngine.Apply(baseTree, overrideTree);

        // Mutate merged tree afterwards
        var mergedDb = (ConfigObjectNode)merged.GetChild("db")!;
        mergedDb.SetChild("port", new ConfigValueNode(9999L));
        ((ConfigListNode)mergedDb.GetChild("roles")!).Items.Add(new ConfigValueNode("admin"));

        // Verify baseTree and overrideTree were completely untouched
        ((ConfigValueNode)baseDb.GetChild("port")!).Value.Should().Be(5432L);
        ((ConfigListNode)baseDb.GetChild("roles")!).Items.Should().ContainSingle().Which.ToString().Should().Be("reader");

        ((ConfigValueNode)overrideDb.GetChild("port")!).Value.Should().Be(1000L);
        ((ConfigListNode)overrideDb.GetChild("roles")!).Items.Should().ContainSingle().Which.ToString().Should().Be("writer");
    }

    [Fact]
    public void ConcurrentParallelBuilds_ShouldBeThreadSafeWithoutRaceConditions()
    {
        string dbDir = Path.Combine(_testDir, "db");
        Directory.CreateDirectory(dbDir);

        File.WriteAllText(Path.Combine(_testDir, "config.yaml"), @"
defaults:
  - db: postgres
  - _self_
app_name: ConcurrentService
active_workers: 4
");

        File.WriteAllText(Path.Combine(dbDir, "postgres.yaml"), @"
driver: postgres
host: db.local
port: 5432
max_connections: 25
timeout_seconds: 10
");

        // Run 50 concurrent builds across thread pool threads with different CLI overrides
        var results = Enumerable.Range(1, 50)
            .AsParallel()
            .WithDegreeOfParallelism(8)
            .Select(i =>
            {
                var cfg = OmegaConfBuilder.Create<AppConfig>()
                    .SetRoot(_testDir)
                    .AddCliArgs($"active_workers+={i}", $"db.max_connections*={i}")
                    .Build();
                return (Iteration: i, Config: cfg);
            })
            .ToList();

        results.Should().HaveCount(50);
        foreach (var (i, cfg) in results)
        {
            cfg.AppName.Should().Be("ConcurrentService");
            cfg.Workers.Should().Be(4 + i);
            cfg.Db.Should().NotBeNull();
            cfg.Db!.MaxConnections.Should().Be(25 * i);
        }
    }

    [Fact]
    public void MalformedSyntaxAndEdgeCaseFuzzing_ShouldThrowStructuredOmegaConfExceptions()
    {
        // 1. Malformed YAML (unclosed bracket)
        var yamlParser = new YamlFormatParser();
        var actBadYaml = () => yamlParser.Parse("key: [unclosed, list", "broken.yaml");
        actBadYaml.Should().Throw<ConfigSyntaxException>()
            .Which.SourceLocation.FilePath.Should().Be("broken.yaml");

        // 2. Malformed JSON (non-object root)
        var jsonParser = new JsonFormatParser();
        var actJsonArrayRoot = () => jsonParser.Parse("[1, 2, 3]", "array_root.json");
        actJsonArrayRoot.Should().Throw<ConfigSyntaxException>()
            .WithMessage("*Root of JSON configuration must be an object*");

        // 3. Malformed TOML
        var tomlParser = new TomlFormatParser();
        var actBadToml = () => tomlParser.Parse("invalid_toml = = 123", "broken.toml");
        actBadToml.Should().Throw<ConfigSyntaxException>()
            .Which.SourceLocation.FilePath.Should().Be("broken.toml");

        // 4. Unregistered file extension
        var actBadExt = () => ConfigFormatRegistry.Default.GetParserForExtension(".xml");
        actBadExt.Should().Throw<ConfigSyntaxException>()
            .WithMessage("*No configuration parser registered for extension '.xml'*");

        // 5. Malformed eval expression (unclosed parenthesis)
        var root = new ConfigObjectNode();
        root.SetChild("broken_eval", new ConfigValueNode("${eval: (10 + 5}"));
        var engine = new InterpolationEngine(root);
        var actBadEval = () => engine.ResolveAll();
        actBadEval.Should().Throw<ConfigSyntaxException>()
            .WithMessage("*Expected closing ')'*");
    }
}
