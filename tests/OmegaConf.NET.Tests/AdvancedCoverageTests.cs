using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using OmegaConf.NET.Ast;
using OmegaConf.NET.Attributes;
using OmegaConf.NET.Binding;
using OmegaConf.NET.Common;
using OmegaConf.NET.Extensions;
using OmegaConf.NET.Interpolation;
using OmegaConf.NET.MultiRun;
using OmegaConf.NET.Operators;
using OmegaConf.NET.Parsers;
using OmegaConf.NET.Schema;
using OmegaConf.NET.Serialization;
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

public record PositionalRecordConfig(
    [property: ConfigRequired] string ServiceName,
    int MaxRetries = 3,
    [property: ConfigKey("ssl_port")] int SSLPort = 443);

public class ExtendedTypesConfig
{
    public Uri? EndpointUri { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateOnly ReleaseDate { get; set; }
    public TimeOnly MaintenanceWindow { get; set; }
    public int SSLPort { get; set; }
    public string HTTPSEndpoint { get; set; } = "";
}

public class DynamicTargetConfig
{
    public MemoryCacheService? Cache { get; set; }
}

public static class TestRunnerProgram
{
    public static int Main()
    {
        var asm = System.Reflection.Assembly.GetExecutingAssembly();
        int passed = 0, failed = 0;

        foreach (var type in asm.GetExportedTypes().Where(t => !t.IsAbstract && t != typeof(TestRunnerProgram)))
        {
            var methods = type.GetMethods(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance)
                              .Where(m => m.GetCustomAttributes(typeof(FactAttribute), false).Length > 0);
            foreach (var method in methods)
            {
                object? instance = Activator.CreateInstance(type);
                try
                {
                    method.Invoke(instance, null);
                    Console.WriteLine($"  [PASS] {type.Name}.{method.Name}");
                    passed++;
                }
                catch (System.Reflection.TargetInvocationException ex)
                {
                    Console.WriteLine($"  [FAIL] {type.Name}.{method.Name}: {ex.InnerException?.GetType().Name} - {ex.InnerException?.Message}");
                    failed++;
                }
                finally
                {
                    if (instance is IDisposable d) d.Dispose();
                }
            }
        }

        Console.WriteLine($"\nSummary ({System.Runtime.InteropServices.RuntimeInformation.FrameworkDescription}): {passed} passed, {failed} failed, {passed + failed} total.");
        return failed > 0 ? 1 : 0;
    }
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

    [Fact]
    public void PositionalRecordBinding_AndAcronymSnakeCase_ShouldWork()
    {
        File.WriteAllText(Path.Combine(_testDir, "config.yaml"), @"
service_name: PaymentGateway
max_retries: 5
ssl_port: 8443
");

        var cfg = OmegaConfBuilder.Create<PositionalRecordConfig>()
            .SetRoot(_testDir)
            .Build();

        cfg.ServiceName.Should().Be("PaymentGateway");
        cfg.MaxRetries.Should().Be(5);
        cfg.SSLPort.Should().Be(8443);
    }

    [Fact]
    public void CliGroupSwitchWithoutSlash_ShouldNotOverwriteGroupObjectWithScalar()
    {
        string dbDir = Path.Combine(_testDir, "db");
        Directory.CreateDirectory(dbDir);

        File.WriteAllText(Path.Combine(_testDir, "config.yaml"), @"
defaults:
  - db: sqlite
app_name: SwitchApp
");

        File.WriteAllText(Path.Combine(dbDir, "sqlite.yaml"), @"
driver: sqlite
port: 0
");

        File.WriteAllText(Path.Combine(dbDir, "postgres.yaml"), @"
driver: postgres
port: 5432
");

        var cfg = OmegaConfBuilder.Create<AppConfig>()
            .SetRoot(_testDir)
            .AddCliArgs("db=postgres")
            .Build();

        cfg.Db.Should().NotBeNull();
        cfg.Db!.Driver.Should().Be("postgres");
        cfg.Db!.Port.Should().Be(5432);
    }

    [Fact]
    public void MultiLevelCascading_ShouldCascadeAcrossThreeDirectoryLevels()
    {
        string dbDir = Path.Combine(_testDir, "db");
        string cloudDir = Path.Combine(dbDir, "cloud");
        string awsDir = Path.Combine(cloudDir, "aws");
        Directory.CreateDirectory(awsDir);

        File.WriteAllText(Path.Combine(_testDir, "config.yaml"), @"
defaults:
  - db: cloud/aws/postgres
");

        File.WriteAllText(Path.Combine(dbDir, "postgres.yaml"), @"
driver: postgres
port: 5432
max_connections: 50
");

        File.WriteAllText(Path.Combine(cloudDir, "postgres.yaml"), @"
ssl: true
max_connections*=: 2
");

        File.WriteAllText(Path.Combine(awsDir, "postgres.yaml"), @"
host: rds.amazonaws.com
max_connections+=: 25
");

        var node = OmegaConfBuilder.Create()
            .SetRoot(_testDir)
            .BuildNode();

        var db = (ConfigObjectNode)node.GetChild("db")!;
        ((ConfigValueNode)db.GetChild("driver")!).Value.Should().Be("postgres");
        ((ConfigValueNode)db.GetChild("ssl")!).Value.Should().Be(true);
        ((ConfigValueNode)db.GetChild("host")!).Value.Should().Be("rds.amazonaws.com");
        ((ConfigValueNode)db.GetChild("max_connections")!).Value.Should().Be(125L); // (50 * 2) + 25
    }

    [Fact]
    public void YamlQuotedScalars_ShouldPreserveStringTypes_AndParseScientificNotation()
    {
        var yamlParser = new YamlFormatParser();
        var node = yamlParser.Parse(@"
quoted_number: ""12345""
single_quoted_bool: 'true'
quoted_null: ""null""
scientific_float: 1e5
unquoted_number: 12345
");

        ((ConfigValueNode)node.GetChild("quoted_number")!).Value.Should().BeOfType<string>().Which.Should().Be("12345");
        ((ConfigValueNode)node.GetChild("single_quoted_bool")!).Value.Should().BeOfType<string>().Which.Should().Be("true");
        ((ConfigValueNode)node.GetChild("quoted_null")!).Value.Should().BeOfType<string>().Which.Should().Be("null");
        ((ConfigValueNode)node.GetChild("scientific_float")!).Value.Should().BeOfType<double>().Which.Should().Be(100000.0);
        ((ConfigValueNode)node.GetChild("unquoted_number")!).Value.Should().BeOfType<long>().Which.Should().Be(12345L);
    }

    [Fact]
    public void SweepEngine_ShouldNotSplitBracketedListLiteralsOnCommas()
    {
        string[] args =
        [
            "-m",
            "workers=2,4",
            "tags=[gpu,fast,cluster]"
        ];

        var (nonSweep, matrix) = SweepEngine.ParseSweepArgs(args);

        matrix.Should().ContainKey("workers").WhoseValue.Should().Equal("2", "4");
        matrix.Should().NotContainKey("tags");
        nonSweep.Should().ContainSingle().Which.Should().Be("tags=[gpu,fast,cluster]");
    }

    [Fact]
    public void ExtendedScalarTypes_AndAcronymSnakeCase_ShouldBindAndGenerateSchema()
    {
        File.WriteAllText(Path.Combine(_testDir, "config.yaml"), @"
endpoint_uri: https://api.omegaconf.net/v1
created_at: 2026-10-31T12:00:00+00:00
release_date: 2026-10-31
maintenance_window: 02:30:00
ssl_port: 8443
https_endpoint: https://secure.local
");

        var cfg = OmegaConfBuilder.Create<ExtendedTypesConfig>()
            .SetRoot(_testDir)
            .Build();

        cfg.EndpointUri.Should().Be(new Uri("https://api.omegaconf.net/v1"));
        cfg.CreatedAt.Should().Be(DateTimeOffset.Parse("2026-10-31T12:00:00+00:00", System.Globalization.CultureInfo.InvariantCulture));
        cfg.ReleaseDate.Should().Be(new DateOnly(2026, 10, 31));
        cfg.MaintenanceWindow.Should().Be(new TimeOnly(2, 30, 0));
        cfg.SSLPort.Should().Be(8443);
        cfg.HTTPSEndpoint.Should().Be("https://secure.local");

        string schema = JsonSchemaGenerator.GenerateSchema<PositionalRecordConfig>();
        schema.Should().Contain("\"service_name\"");
        schema.Should().Contain("\"max_retries\"");
        schema.Should().Contain("\"ssl_port\"");
    }

    [Fact]
    public void NestedCustomResolverInterpolation_AndTomlSerialization_ShouldWork()
    {
        var root = new ConfigObjectNode();
        root.SetChild("app_name", new ConfigValueNode("My Awesome Service"));
        root.SetChild("slug", new ConfigValueNode("${slugify:${app_name}}"));
        root.SetChild("port", new ConfigValueNode(8080L));

        var engine = new InterpolationEngine(root, [new CustomResolver("slugify", s => s.ToLowerInvariant().Replace(' ', '-'))]);
        engine.ResolveAll();

        ((ConfigValueNode)root.GetChild("slug")!).Value.Should().Be("my-awesome-service");

        string toml = root.ToToml();
        toml.Should().Contain("slug = \"my-awesome-service\"");
        toml.Should().Contain("port = 8080");
    }

    [Fact]
    public void CrossFormatTreeComposition_YamlJsoncToml_ShouldMergeAndApplyOperators()
    {
        string dbDir = Path.Combine(_testDir, "db");
        string cacheDir = Path.Combine(_testDir, "cache");
        Directory.CreateDirectory(dbDir);
        Directory.CreateDirectory(cacheDir);

        // Base file in TOML
        File.WriteAllText(Path.Combine(_testDir, "base.toml"), @"
app_name = ""CrossFormatService""
workers = 4
");

        // Entrypoint in YAML inheriting from TOML and composing JSONC + TOML groups
        File.WriteAllText(Path.Combine(_testDir, "config.yaml"), @"
_inherits_: base.toml
defaults:
  - db: postgres
  - cache: redis
  - _self_
workers*=: 3
");

        // db group in JSONC (with comments, trailing commas, and += operator)
        File.WriteAllText(Path.Combine(dbDir, "postgres.jsonc"), @"
{
  // JSONC database config
  ""driver"": ""postgres"",
  ""port"": 5432,
  ""tags"": [""primary"", ""sql"",],
}
");

        // cache group in TOML
        File.WriteAllText(Path.Combine(cacheDir, "redis.toml"), @"
provider = ""redis""
ttl_seconds = 120
");

        var node = OmegaConfBuilder.Create()
            .SetRoot(_testDir)
            .BuildNode();

        ((ConfigValueNode)node.GetChild("app_name")!).Value.Should().Be("CrossFormatService");
        ((ConfigValueNode)node.GetChild("workers")!).Value.Should().Be(12L); // 4 * 3

        var db = (ConfigObjectNode)node.GetChild("db")!;
        ((ConfigValueNode)db.GetChild("driver")!).Value.Should().Be("postgres");
        ((ConfigValueNode)db.GetChild("port")!).Value.Should().Be(5432L);
        ((ConfigListNode)db.GetChild("tags")!).Items.Should().HaveCount(2);

        var cache = (ConfigObjectNode)node.GetChild("cache")!;
        ((ConfigValueNode)cache.GetChild("provider")!).Value.Should().Be("redis");
        ((ConfigValueNode)cache.GetChild("ttl_seconds")!).Value.Should().Be(120L);
    }

    [Fact]
    public void DeepMultiHopAndDiamondInterpolation_ShouldResolveWithoutFalseCycleDetection()
    {
        // Diamond & multi-hop graph:
        // env_name: "prod"
        // region: "eu-west"
        // cluster: "${env_name}-${region}"          (hop 1)
        // host_prefix: "${cluster}.internal"        (hop 2)
        // primary_url: "https://${host_prefix}/v1"  (hop 3)
        // backup_url: "https://${host_prefix}/v2"   (shared diamond reference to host_prefix & cluster)
        // summary: "${primary_url} | ${backup_url}" (diamond convergence)

        var root = new ConfigObjectNode();
        root.SetChild("env_name", new ConfigValueNode("prod"));
        root.SetChild("region", new ConfigValueNode("eu-west"));
        root.SetChild("cluster", new ConfigValueNode("${env_name}-${region}"));
        root.SetChild("host_prefix", new ConfigValueNode("${cluster}.internal"));
        root.SetChild("primary_url", new ConfigValueNode("https://${host_prefix}/v1"));
        root.SetChild("backup_url", new ConfigValueNode("https://${host_prefix}/v2"));
        root.SetChild("summary", new ConfigValueNode("${primary_url} | ${backup_url}"));

        var engine = new InterpolationEngine(root);
        engine.ResolveAll();

        ((ConfigValueNode)root.GetChild("cluster")!).Value.Should().Be("prod-eu-west");
        ((ConfigValueNode)root.GetChild("host_prefix")!).Value.Should().Be("prod-eu-west.internal");
        ((ConfigValueNode)root.GetChild("primary_url")!).Value.Should().Be("https://prod-eu-west.internal/v1");
        ((ConfigValueNode)root.GetChild("backup_url")!).Value.Should().Be("https://prod-eu-west.internal/v2");
        ((ConfigValueNode)root.GetChild("summary")!).Value.Should().Be("https://prod-eu-west.internal/v1 | https://prod-eu-west.internal/v2");
    }

    [Fact]
    public void ListIndexingInInterpolation_AndCliListMutationOperators_ShouldWork()
    {
        File.WriteAllText(Path.Combine(_testDir, "config.yaml"), @"
servers:
  - alpha.local
  - beta.local
  - gamma.local
primary_server: ""${servers[0]}""
secondary_server: ""${servers.1}""
");

        var node = OmegaConfBuilder.Create()
            .SetRoot(_testDir)
            .AddCliArgs("servers+=[delta.local]", "servers-=[beta.local]")
            .BuildNode();

        var servers = (ConfigListNode)node.GetChild("servers")!;
        servers.Items.Select(x => x.ToString()).Should().Equal("alpha.local", "gamma.local", "delta.local");

        ((ConfigValueNode)node.GetChild("primary_server")!).Value.Should().Be("alpha.local");
        ((ConfigValueNode)node.GetChild("secondary_server")!).Value.Should().Be("gamma.local");
    }

    [Fact]
    public void ErrorDiagnostics_ShouldReportAccurateSourceInfoLineAndFile()
    {
        string badYamlPath = Path.Combine(_testDir, "config.yaml");
        File.WriteAllText(badYamlPath, @"
app_name: DiagnosticApp
db:
  port: 5432
  secret_token: ???
");

        // 1. MissingValueMarkerException should carry SourceLocation with FilePath and Line 5
        var actMissing = () => OmegaConfBuilder.Create()
            .SetRoot(_testDir)
            .AddCliArgs("auth=${db.secret_token}")
            .BuildNode();

        var missingEx = actMissing.Should().Throw<MissingValueMarkerException>().Which;
        missingEx.Key.Should().Be("db.secret_token");
        missingEx.SourceLocation.FilePath.Should().EndWith("config.yaml");
        missingEx.SourceLocation.Line.Should().Be(5);

        // 2. ConfigTypeMismatchException should report property path, expected type, and SourceLocation
        File.WriteAllText(badYamlPath, @"
app_name: DiagnosticApp
active_workers: invalid_int_value
");

        var actType = () => OmegaConfBuilder.Create<AppConfig>()
            .SetRoot(_testDir)
            .Build();

        var typeEx = actType.Should().Throw<ConfigTypeMismatchException>().Which;
        typeEx.ConfigPath.Should().Be("Workers");
        typeEx.ExpectedType.Should().Be(typeof(int));
        typeEx.SourceLocation.FilePath.Should().EndWith("config.yaml");
        typeEx.SourceLocation.Line.Should().Be(3);

        // 3. InvalidOperatorException should report Operator, TargetType, and SourceLocation
        var baseNode = new ConfigValueNode("text_value");
        var badOverride = new ConfigValueNode(true, ConfigOperator.Toggle, new SourceInfo("override.yaml", 12, 4));
        var actOp = () => OperatorEngine.Apply(baseNode, badOverride);

        var opEx = actOp.Should().Throw<InvalidOperatorException>().Which;
        opEx.Operator.Should().Be("!=");
        opEx.SourceLocation.FilePath.Should().Be("override.yaml");
        opEx.SourceLocation.Line.Should().Be(12);
        opEx.SourceLocation.Column.Should().Be(4);
    }

    [Fact]
    public void MicrosoftConfigurationProvider_ShouldFlattenLists_AndSupportSectionBinding()
    {
        File.WriteAllText(Path.Combine(_testDir, "config.yaml"), @"
app_name: SectionBindApp
endpoints:
  - https://node1.internal
  - https://node2.internal
db:
  driver: postgres
  host: db.production.local
  port: 5432
  max_connections: 100
  ssl: true
  timeout_seconds: 45
");

        var configRoot = new Microsoft.Extensions.Configuration.ConfigurationBuilder()
            .AddOmegaConf(omega =>
            {
                omega.SetRoot(_testDir);
                omega.AddCliArgs("db.max_connections+=50", "endpoints+=[https://node3.internal]");
            })
            .Build();

        // Verify list flattening with standard :0, :1, :2 keys
        configRoot["endpoints:0"].Should().Be("https://node1.internal");
        configRoot["endpoints:1"].Should().Be("https://node2.internal");
        configRoot["endpoints:2"].Should().Be("https://node3.internal");

        // Verify Microsoft.Extensions.Configuration.Binder section binding
        var dbSection = Microsoft.Extensions.Configuration.ConfigurationBinder.Get<AppDatabaseConfig>(configRoot.GetSection("db"));
        dbSection.Should().NotBeNull();
        dbSection!.Driver.Should().Be("postgres");
        dbSection.Host.Should().Be("db.production.local");
        dbSection.Port.Should().Be(5432);
        dbSection.Ssl.Should().BeTrue();
    }
}
