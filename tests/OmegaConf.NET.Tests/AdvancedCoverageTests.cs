using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using OmegaConf.NET.Ast;
using OmegaConf.NET.Attributes;
using OmegaConf.NET.Common;
using OmegaConf.NET.Extensions;
using OmegaConf.NET.Operators;
using OmegaConf.NET.Parsers;
using Xunit;

namespace OmegaConf.NET.Tests;

public enum EnvironmentTier
{
    Development,
    Staging,
    Production
}

public interface ICacheService
{
    string GetName();
}

public class MemoryCacheService : ICacheService
{
    public string Prefix { get; set; } = "default";
    public string GetName() => $"Memory:{Prefix}";
}

public class RichTypesConfig
{
    public EnvironmentTier Tier { get; set; }
    public int? NullableInt { get; set; }
    public TimeSpan CacheDuration { get; set; }
    public Guid CorrelationId { get; set; }
    public string[] Endpoints { get; set; } = [];
    public List<int> Ports { get; set; } = [];
    public Dictionary<string, string> Headers { get; set; } = [];
}

public class DynamicTargetConfig
{
    public MemoryCacheService? Cache { get; set; }
}

public class AdvancedCoverageTests : IDisposable
{
    private readonly string _testDir;

    public AdvancedCoverageTests()
    {
        _testDir = Path.Combine(Path.GetTempPath(), "omegaconf_adv_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_testDir);
    }

    public void Dispose()
    {
        if (Directory.Exists(_testDir))
        {
            Directory.Delete(_testDir, recursive: true);
        }
    }

    [Fact]
    public void StructuredBinding_ShouldSupportEnums_Nullables_TimeSpan_Guid_Collections_AndDictionaries()
    {
        File.WriteAllText(Path.Combine(_testDir, "config.yaml"), @"
tier: Staging
nullable_int: 42
cache_duration: 00:15:00
correlation_id: 11111111-2222-3333-4444-555555555555
endpoints:
  - https://api1.local
  - https://api2.local
ports:
  - 80
  - 443
headers:
  X-Custom-Env: Test
  Authorization: Bearer xyz
");

        var cfg = OmegaConfBuilder.Create<RichTypesConfig>()
            .SetRoot(_testDir)
            .Build();

        cfg.Tier.Should().Be(EnvironmentTier.Staging);
        cfg.NullableInt.Should().Be(42);
        cfg.CacheDuration.Should().Be(TimeSpan.FromMinutes(15));
        cfg.CorrelationId.Should().Be(Guid.Parse("11111111-2222-3333-4444-555555555555"));
        cfg.Endpoints.Should().Equal("https://api1.local", "https://api2.local");
        cfg.Ports.Should().Equal(80, 443);
        cfg.Headers.Should().ContainKey("X-Custom-Env").WhoseValue.Should().Be("Test");
        cfg.Headers.Should().ContainKey("Authorization").WhoseValue.Should().Be("Bearer xyz");
    }

    [Fact]
    public void StructuredBinding_ShouldInstantiateDynamicTarget()
    {
        string targetTypeName = typeof(MemoryCacheService).AssemblyQualifiedName!;

        File.WriteAllText(Path.Combine(_testDir, "config.yaml"), $@"
cache:
  _target_: ""{targetTypeName}""
  prefix: custom_cache
");

        var cfg = OmegaConfBuilder.Create<DynamicTargetConfig>()
            .SetRoot(_testDir)
            .Build();

        cfg.Cache.Should().NotBeNull();
        cfg.Cache!.GetName().Should().Be("Memory:custom_cache");
    }

    [Fact]
    public void OptionalDefault_ShouldNotThrow_WhenFileIsMissing()
    {
        File.WriteAllText(Path.Combine(_testDir, "config.yaml"), @"
defaults:
  - optional non_existent_group: missing_file
  - _self_
app_name: SafeApp
");

        var node = OmegaConfBuilder.Create()
            .SetRoot(_testDir)
            .BuildNode();

        node.GetChild("app_name").Should().NotBeNull();
    }

    [Fact]
    public void NonOptionalDefault_ShouldThrow_WhenFileIsMissing()
    {
        File.WriteAllText(Path.Combine(_testDir, "config.yaml"), @"
defaults:
  - mandatory_group: missing_file
");

        var act = () => OmegaConfBuilder.Create()
            .SetRoot(_testDir)
            .BuildNode();

        act.Should().Throw<FileNotFoundException>();
    }

    [Fact]
    public void CircularInheritance_ShouldThrowConfigInheritanceException()
    {
        File.WriteAllText(Path.Combine(_testDir, "fileA.yaml"), @"
_inherits_: fileB.yaml
a: 1
");

        File.WriteAllText(Path.Combine(_testDir, "fileB.yaml"), @"
_inherits_: fileA.yaml
b: 2
");

        var act = () => OmegaConfBuilder.Create()
            .SetRoot(_testDir)
            .SetEntryFile("fileA.yaml")
            .BuildNode();

        act.Should().Throw<ConfigInheritanceException>()
            .WithMessage("*Circular*");
    }

    [Fact]
    public void SelfPlacement_ShouldDeterminePrecedenceOverDefaults()
    {
        string grpDir = Path.Combine(_testDir, "sub");
        Directory.CreateDirectory(grpDir);

        File.WriteAllText(Path.Combine(grpDir, "setting.yaml"), "val: from_default");

        // Case 1: _self_ after defaults (local file wins)
        File.WriteAllText(Path.Combine(_testDir, "self_wins.yaml"), @"
defaults:
  - sub: setting
  - _self_
val: from_local
");

        var node1 = OmegaConfBuilder.Create()
            .SetRoot(_testDir)
            .SetEntryFile("self_wins.yaml")
            .BuildNode();

        ((ConfigValueNode)node1.GetChild("val")!).Value.Should().Be("from_local");

        // Case 2: _self_ before defaults (defaults win if at the root level)
        File.WriteAllText(Path.Combine(_testDir, "defaults_win.yaml"), @"
defaults:
  - _self_
  - sub: setting
val: from_local
");

        var node2 = OmegaConfBuilder.Create()
            .SetRoot(_testDir)
            .SetEntryFile("defaults_win.yaml")
            .BuildNode();

        node2.GetChild("sub").Should().NotBeNull();
    }

    [Fact]
    public void BitwiseOperatorsOnIntegers_AndDivideByZero()
    {
        // 12 (1100) & 10 (1010) = 8 (1000)
        var baseInt = new ConfigValueNode(12L);
        var andInt = new ConfigValueNode(10L, ConfigOperator.BitwiseAnd);
        var resAnd = (ConfigValueNode)OperatorEngine.Apply(baseInt, andInt);
        resAnd.Value.Should().Be(8L);

        // 12 (1100) | 3 (0011) = 15 (1111)
        var orInt = new ConfigValueNode(3L, ConfigOperator.BitwiseOr);
        var resOr = (ConfigValueNode)OperatorEngine.Apply(baseInt, orInt);
        resOr.Value.Should().Be(15L);

        // Divide by zero
        var zero = new ConfigValueNode(0L, ConfigOperator.Divide);
        var actDivZero = () => OperatorEngine.Apply(baseInt, zero);
        actDivZero.Should().Throw<DivideByZeroException>();
    }

    [Fact]
    public void AdvancedEvalExpressions_Parentheses_Modulo_Logic()
    {
        var root = new ConfigObjectNode();
        root.SetChild("complex_math", new ConfigValueNode("${eval: (10 + 2) * (5 - 2)}")); // 12 * 3 = 36
        root.SetChild("modulo", new ConfigValueNode("${eval: 23 % 5}")); // 3
        root.SetChild("logic_and", new ConfigValueNode("${eval: true && false}")); // False
        root.SetChild("logic_or", new ConfigValueNode("${eval: false || true}")); // True
        root.SetChild("unary_not", new ConfigValueNode("${eval: !false}")); // True
        root.SetChild("comparison_le", new ConfigValueNode("${eval: 10 <= 10}")); // True

        var engine = new OmegaConf.NET.Interpolation.InterpolationEngine(root);
        engine.ResolveAll();

        ((ConfigValueNode)root.GetChild("complex_math")!).Value.Should().Be("36");
        ((ConfigValueNode)root.GetChild("modulo")!).Value.Should().Be("3");
        ((ConfigValueNode)root.GetChild("logic_and")!).Value.Should().Be("False");
        ((ConfigValueNode)root.GetChild("logic_or")!).Value.Should().Be("True");
        ((ConfigValueNode)root.GetChild("unary_not")!).Value.Should().Be("True");
        ((ConfigValueNode)root.GetChild("comparison_le")!).Value.Should().Be("True");
    }

    [Fact]
    public void ServiceCollection_ConfigureOmegaConf_ShouldRegisterSingleton()
    {
        File.WriteAllText(Path.Combine(_testDir, "config.yaml"), @"
app_name: DIServiceApp
active_workers: 8
");

        var services = new ServiceCollection();
        services.ConfigureOmegaConf<AppConfig>(omega =>
        {
            omega.SetRoot(_testDir);
        });

        var descriptor = services.FirstOrDefault(sd => sd.ServiceType == typeof(AppConfig));
        descriptor.Should().NotBeNull();
        var cfg = descriptor!.ImplementationInstance.Should().BeOfType<AppConfig>().Subject;
        cfg.AppName.Should().Be("DIServiceApp");
        cfg.Workers.Should().Be(8);
    }

    [Fact]
    public void EmptyFiles_AcrossFormats_ShouldReturnEmptyObjectNode()
    {
        var yamlParser = new YamlFormatParser();
        yamlParser.Parse("").Children.Should().BeEmpty();
        yamlParser.Parse("   \n  \t  ").Children.Should().BeEmpty();

        var jsonParser = new JsonFormatParser();
        jsonParser.Parse("").Children.Should().BeEmpty();
        jsonParser.Parse("{}").Children.Should().BeEmpty();

        var tomlParser = new TomlFormatParser();
        tomlParser.Parse("").Children.Should().BeEmpty();
    }
}
