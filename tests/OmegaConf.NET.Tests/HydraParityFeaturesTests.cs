using FluentAssertions;
using OmegaConf.NET.Ast;
using OmegaConf.NET.Common;
using OmegaConf.NET.MultiRun;
using Xunit;

namespace OmegaConf.NET.Tests;

public class HydraParityFeaturesTests : IDisposable
{
    private readonly string _testDir;

    public HydraParityFeaturesTests()
    {
        _testDir = Path.Combine(Path.GetTempPath(), "omegaconf_hydra_parity_" + Guid.NewGuid().ToString("N"));
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
    public void PackageDirectives_ShouldMountMultipleGroupsAtCustomPackages_AndSupportGlobalPackage_AndFileHeaders()
    {
        Directory.CreateDirectory(Path.Combine(_testDir, "db"));
        Directory.CreateDirectory(Path.Combine(_testDir, "experiment"));

        File.WriteAllText(Path.Combine(_testDir, "db", "postgres.yaml"), @"
driver: postgresql
port: 5432
");
        File.WriteAllText(Path.Combine(_testDir, "db", "sqlite.yaml"), @"
driver: sqlite
port: 0
");
        // File with # @package _global_ header
        File.WriteAllText(Path.Combine(_testDir, "experiment", "fast_train.yaml"), @"# @package _global_
workers: 16
primary_db:
  port: 6432
");

        File.WriteAllText(Path.Combine(_testDir, "config.yaml"), @"
defaults:
  - db@primary_db: postgres
  - db@replica_db: sqlite
  - _self_

workers: 4
");

        // Also dynamically inject experiment/fast_train via CLI +experiment=fast_train (which uses its # @package _global_ header)
        var root = OmegaConfBuilder.Create()
            .SetRoot(_testDir)
            .AddCliArgs("+experiment=fast_train")
            .BuildNode();

        root.Select<string>("primary_db.driver").Should().Be("postgresql");
        root.Select<int>("primary_db.port").Should().Be(6432); // Overridden by fast_train @_global_!
        root.Select<string>("replica_db.driver").Should().Be("sqlite");
        root.Select<int>("workers").Should().Be(16);
    }

    [Fact]
    public void SweepFunctions_Range_Linspace_Choice_AndGlob_ShouldExpandCorrectly()
    {
        Directory.CreateDirectory(Path.Combine(_testDir, "db"));
        File.WriteAllText(Path.Combine(_testDir, "db", "postgres.yaml"), "driver: pg\nport: 5432");
        File.WriteAllText(Path.Combine(_testDir, "db", "sqlite.yaml"), "driver: sqlite\nport: 0");
        File.WriteAllText(Path.Combine(_testDir, "db", "mysql.yaml"), "driver: mysql\nport: 3306");

        File.WriteAllText(Path.Combine(_testDir, "config.yaml"), @"
defaults:
  - db: sqlite
  - _self_
workers: 1
lr: 0.1
");

        // Verify wildcard filtering with glob(*sql*) -> matches mysql and sqlite (excludes postgres)
        var (_, filteredMatrix) = SweepEngine.ParseSweepArgs(["-m", "db=glob(*sql*)"], _testDir);
        filteredMatrix["db"].Should().Equal("mysql", "sqlite");

        string[] args =
        [
            "-m",
            "db=glob(*)",             // matches mysql, postgres, sqlite (3)
            "workers=range(2, 6, 2)", // 2, 4 (2)
            "lr=linspace(0.1, 0.2, 2)" // 0.1, 0.2 (2)
        ];

        var (nonSweep, matrix) = SweepEngine.ParseSweepArgs(args, _testDir);
        nonSweep.Should().BeEmpty();
        matrix["db"].Should().Equal("mysql", "postgres", "sqlite");
        matrix["workers"].Should().Equal("2", "4");
        matrix["lr"].Should().Equal("0.1", "0.2");

        var sweepNodes = OmegaConfBuilder.Create()
            .SetRoot(_testDir)
            .AddCliArgs(args)
            .BuildSweepNodes();

        // 3 * 2 * 2 = 12 combinations
        sweepNodes.Should().HaveCount(12);
    }

    [Fact]
    public void CliDeepKeyDeletion_AndStrictStructMode_WithPlusAndPlusPlusModifiers_ShouldWork()
    {
        File.WriteAllText(Path.Combine(_testDir, "config.yaml"), @"
app_name: StrictApp
db:
  host: localhost
  port: 5432
  secret_token: s3cr3t
");

        // 1. Typo on undeclared key in Struct Mode should throw ConfigStructException
        Action typoAct = () => OmegaConfBuilder.Create()
            .SetRoot(_testDir)
            .WithStructMode()
            .AddCliArgs("db.prot=5433")
            .BuildNode();
        typoAct.Should().Throw<ConfigStructException>().Where(e => e.ConfigPath == "db.prot");

        // 2. +key on an ALREADY existing key should throw ConfigStructException
        Action duplicateAppendAct = () => OmegaConfBuilder.Create()
            .SetRoot(_testDir)
            .WithStructMode()
            .AddCliArgs("+db.port=5433")
            .BuildNode();
        duplicateAppendAct.Should().Throw<ConfigStructException>().Where(e => e.ConfigPath == "db.port");

        // 3. ~db.secret_token (deep deletion), +db.pool_size=20 (strict append), and ++db.port=5433 (force upsert) should succeed!
        var node = OmegaConfBuilder.Create()
            .SetRoot(_testDir)
            .WithStructMode()
            .AddCliArgs("~db.secret_token", "+db.pool_size=20", "++db.port=5433", "++db.ssl=true")
            .BuildNode();

        node.Select("db.secret_token").Should().BeNull();
        node.Select<int>("db.pool_size").Should().Be(20);
        node.Select<int>("db.port").Should().Be(5433);
        node.Select<bool>("db.ssl").Should().BeTrue();
    }

    [Fact]
    public void BuiltInOcResolvers_OcSelect_OcDecode_OcDictKeys_OcEnv_AndNow_ShouldWork()
    {
        Environment.SetEnvironmentVariable("OC_TEST_DECODE_PORT", "9090");
        try
        {
            File.WriteAllText(Path.Combine(_testDir, "config.yaml"), @"
db:
  host: localhost
  port: 5432
selected_existing: ""${oc.select:db.port, 3306}""
selected_missing_with_fallback: ""${oc.select:db.missing_timeout, 45}""
selected_missing_null: ""${oc.select:db.non_existent}""
decoded_env_port: ""${oc.decode:${oc.env:OC_TEST_DECODE_PORT}}""
decoded_list: ""${oc.decode:[10, 20, 30]}""
db_keys: ""${oc.dict.keys:db}""
current_year: ""${now:yyyy}""
");

            var root = OmegaConfBuilder.Create()
                .SetRoot(_testDir)
                .BuildNode();

            root.Select<long>("selected_existing").Should().Be(5432L);
            root.Select<long>("selected_missing_with_fallback").Should().Be(45L);
            root.Select("selected_missing_null").Should().BeNull();
            root.Select<long>("decoded_env_port").Should().Be(9090L);
            root.Select<int[]>("decoded_list").Should().Equal(10, 20, 30);
            root.Select<string[]>("db_keys").Should().Equal("host", "port");
            root.Select<string>("current_year").Should().Be(DateTime.Now.Year.ToString());
        }
        finally
        {
            Environment.SetEnvironmentVariable("OC_TEST_DECODE_PORT", null);
        }
    }

    [Fact]
    public void ProgrammaticOmegaConfFacade_Select_Merge_SetReadOnly_AndSetStruct_ShouldWork()
    {
        var baseCfg = OmegaConfig.ParseYaml(@"
server:
  host: 127.0.0.1
  port: 8080
  endpoints:
    - /health
    - /metrics
");
        var overrideCfg = OmegaConfig.ParseJson(@"{
  ""server"": {
    ""port+="": 10,
    ""url"": ""http://${server.host}:${server.port}${server.endpoints[0]}""
  }
}");

        var merged = OmegaConfig.Merge(baseCfg, overrideCfg);

        OmegaConfig.Select<int>(merged, "server.port").Should().Be(8090);
        OmegaConfig.Select<string>(merged, "server.url").Should().Be("http://127.0.0.1:8090/health");
        OmegaConfig.Select(merged, "server.missing", "default_val").Should().Be("default_val");

        // Test SetStruct
        OmegaConfig.SetStruct(merged, true);
        Action addInStruct = () => merged.SetChild("undeclared", new ConfigValueNode(1));
        addInStruct.Should().Throw<ConfigStructException>();

        // Test SetReadOnly / Freeze
        OmegaConfig.SetReadOnly(merged, true);
        merged.IsReadOnly.Should().BeTrue();
        Action mutateReadOnly = () => ((ConfigValueNode)merged.SelectNode("server.port")!).Value = 9999L;
        mutateReadOnly.Should().Throw<ConfigReadOnlyException>();
    }

    public interface IEndpointClient
    {
        string Endpoint { get; }
        int Retries { get; }
        int TimeoutSeconds { get; set; }
    }

    public class CustomEndpointClient : IEndpointClient
    {
        public string Endpoint { get; }
        public int Retries { get; }
        public int TimeoutSeconds { get; set; }

        public CustomEndpointClient(string endpoint, int retries = 3)
        {
            Endpoint = endpoint;
            Retries = retries;
        }
    }

    [Fact]
    public void DynamicInstantiation_WithPositionalArgs_AndPartialFactoryDelegate_ShouldWork()
    {
        string targetTypeName = typeof(CustomEndpointClient).AssemblyQualifiedName!;
        var node = OmegaConfig.ParseYaml($@"
client:
  _target_: ""{targetTypeName}""
  _args_:
    - ""https://api.internal""
    - 5
  timeout_seconds: 30
");

        var clientNode = node.SelectNode("client")!;

        // 1. Direct instantiation via OmegaConfig.Instantiate<T>
        var instance = OmegaConfig.Instantiate<IEndpointClient>(clientNode);
        instance.Should().BeOfType<CustomEndpointClient>();
        instance.Endpoint.Should().Be("https://api.internal");
        instance.Retries.Should().Be(5);
        instance.TimeoutSeconds.Should().Be(30);

        // 2. Partial / Factory delegate binding via Func<T>
        var factory = OmegaConfig.Instantiate<Func<IEndpointClient>>(clientNode);
        var instance1 = factory();
        var instance2 = factory();
        instance1.Should().NotBeSameAs(instance2);
        instance1.Endpoint.Should().Be("https://api.internal");
        instance1.Retries.Should().Be(5);
    }

    [Fact]
    public void OutputDirectorySnapshot_ShouldWriteReproducibleOmegaConfArtifacts_ForSingleRunAndMultiRun()
    {
        File.WriteAllText(Path.Combine(_testDir, "config.yaml"), @"
app_name: SnapshotApp
workers: 2
");

        string singleRunOutDir = Path.Combine(_testDir, "outputs", "single");
        var singleNode = OmegaConfBuilder.Create()
            .SetRoot(_testDir)
            .AddCliArgs("workers=8")
            .WithOutputDirectory(singleRunOutDir)
            .BuildNode();

        File.Exists(Path.Combine(singleRunOutDir, ".omegaconf", "config.yaml")).Should().BeTrue();
        File.Exists(Path.Combine(singleRunOutDir, ".omegaconf", "overrides.yaml")).Should().BeTrue();
        File.ReadAllText(Path.Combine(singleRunOutDir, ".omegaconf", "config.yaml")).Should().Contain("workers: 8");
        File.ReadAllText(Path.Combine(singleRunOutDir, ".omegaconf", "overrides.yaml")).Should().Contain("workers=8");

        string sweepOutDir = Path.Combine(_testDir, "outputs", "multirun");
        var sweepNodes = OmegaConfBuilder.Create()
            .SetRoot(_testDir)
            .AddCliArgs("-m", "workers=range(2, 5)")
            .WithOutputDirectory(sweepOutDir)
            .BuildSweepNodes();

        sweepNodes.Should().HaveCount(3);
        File.Exists(Path.Combine(sweepOutDir, "multirun.yaml")).Should().BeTrue();
        File.Exists(Path.Combine(sweepOutDir, "0", ".omegaconf", "config.yaml")).Should().BeTrue();
        File.Exists(Path.Combine(sweepOutDir, "1", ".omegaconf", "config.yaml")).Should().BeTrue();
        File.Exists(Path.Combine(sweepOutDir, "2", ".omegaconf", "config.yaml")).Should().BeTrue();
    }
}
