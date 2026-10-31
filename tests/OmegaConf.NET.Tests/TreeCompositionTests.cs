using FluentAssertions;
using OmegaConf.NET.Ast;
using OmegaConf.NET.Serialization;
using Xunit;

namespace OmegaConf.NET.Tests;

public class TreeCompositionTests : IDisposable
{
    private readonly string _testDir;

    public TreeCompositionTests()
    {
        _testDir = Path.Combine(Path.GetTempPath(), "omegaconf_tests_" + Guid.NewGuid().ToString("N"));
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
    public void TreeResolver_ShouldSupportUserTreeWithCascadingAndOperators()
    {
        // 1. Setup User's exact Tree Structure:
        // configs/
        // ├── config.yaml
        // └── db/
        //     ├── postgres.yaml
        //     ├── sqlite.yaml
        //     ├── staging/
        //     │   └── postgres.yaml
        //     └── production/
        //         └── postgres.yaml

        string dbDir = Path.Combine(_testDir, "db");
        string stagingDir = Path.Combine(dbDir, "staging");
        string prodDir = Path.Combine(dbDir, "production");
        Directory.CreateDirectory(stagingDir);
        Directory.CreateDirectory(prodDir);

        // Root entrypoint
        File.WriteAllText(Path.Combine(_testDir, "config.yaml"), @"
defaults:
  - db: staging/postgres
  - _self_

app_name: MyService
");

        // Base postgres
        File.WriteAllText(Path.Combine(dbDir, "postgres.yaml"), @"
driver: postgres
port: 5432
user: db_admin
max_connections: 50
ssl: false
timeout_seconds: 30
");

        // Staging postgres (overrides host, reduces connections)
        File.WriteAllText(Path.Combine(stagingDir, "postgres.yaml"), @"
host: staging-db.internal
max_connections: 20
");

        // Production postgres (uses operators: *= and +=)
        File.WriteAllText(Path.Combine(prodDir, "postgres.yaml"), @"
host: prod-db.internal
max_connections*=: 4
ssl: true
timeout_seconds+=: 30
");

        // TEST 1: Default selection (staging/postgres)
        var stagingConfig = OmegaConfBuilder.Create()
            .SetRoot(_testDir)
            .BuildNode();

        stagingConfig.GetChild("app_name").Should().BeOfType<ConfigValueNode>()
            .Which.Value.Should().Be("MyService");

        var dbNode = stagingConfig.GetChild("db").Should().BeOfType<ConfigObjectNode>().Subject;
        dbNode.GetChild("driver").Should().BeOfType<ConfigValueNode>().Which.Value.Should().Be("postgres"); // from base
        dbNode.GetChild("port").Should().BeOfType<ConfigValueNode>().Which.Value.Should().Be(5432L); // from base
        dbNode.GetChild("user").Should().BeOfType<ConfigValueNode>().Which.Value.Should().Be("db_admin"); // from base
        dbNode.GetChild("host").Should().BeOfType<ConfigValueNode>().Which.Value.Should().Be("staging-db.internal"); // from staging
        dbNode.GetChild("max_connections").Should().BeOfType<ConfigValueNode>().Which.Value.Should().Be(20L); // overridden
        dbNode.GetChild("ssl").Should().BeOfType<ConfigValueNode>().Which.Value.Should().Be(false); // from base
        dbNode.GetChild("timeout_seconds").Should().BeOfType<ConfigValueNode>().Which.Value.Should().Be(30L); // from base

        // TEST 2: Select production path (production/postgres)
        var prodConfig = OmegaConfBuilder.Create()
            .SetRoot(_testDir)
            .SelectPath("db", "production/postgres")
            .BuildNode();

        var prodDb = prodConfig.GetChild("db").Should().BeOfType<ConfigObjectNode>().Subject;
        prodDb.GetChild("host").Should().BeOfType<ConfigValueNode>().Which.Value.Should().Be("prod-db.internal");
        prodDb.GetChild("max_connections").Should().BeOfType<ConfigValueNode>().Which.Value.Should().Be(200L); // 50 * 4
        prodDb.GetChild("ssl").Should().BeOfType<ConfigValueNode>().Which.Value.Should().Be(true);
        prodDb.GetChild("timeout_seconds").Should().BeOfType<ConfigValueNode>().Which.Value.Should().Be(60L); // 30 + 30
    }

    [Fact]
    public void ExplicitInheritance_ShouldLoadBaseFile()
    {
        string configsDir = Path.Combine(_testDir, "explicit");
        Directory.CreateDirectory(configsDir);

        File.WriteAllText(Path.Combine(configsDir, "base.yaml"), @"
base_param: original
shared_metric: 100
");

        File.WriteAllText(Path.Combine(configsDir, "child.yaml"), @"
_inherits_: base.yaml
child_param: custom
shared_metric+=: 50
");

        var result = OmegaConfBuilder.Create()
            .SetRoot(configsDir)
            .SetEntryFile("child.yaml")
            .BuildNode();

        result.GetChild("base_param").Should().BeOfType<ConfigValueNode>().Which.Value.Should().Be("original");
        result.GetChild("child_param").Should().BeOfType<ConfigValueNode>().Which.Value.Should().Be("custom");
        result.GetChild("shared_metric").Should().BeOfType<ConfigValueNode>().Which.Value.Should().Be(150L);
    }
}
