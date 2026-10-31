using FluentAssertions;
using Microsoft.Extensions.Configuration;
using OmegaConf.NET.Ast;
using OmegaConf.NET.Attributes;
using OmegaConf.NET.Common;
using OmegaConf.NET.Extensions;
using OmegaConf.NET.Serialization;
using Xunit;

namespace OmegaConf.NET.Tests;

public class AppDatabaseConfig
{
    public string Driver { get; set; } = "";
    public string Host { get; set; } = "";
    public int Port { get; set; }
    public int MaxConnections { get; set; }
    public bool Ssl { get; set; }
    public int TimeoutSeconds { get; set; }
}

public class AppConfig
{
    [ConfigRequired]
    public string AppName { get; set; } = "";

    public AppDatabaseConfig? Db { get; set; }

    [ConfigKey("active_workers")]
    public int Workers { get; set; } = 4;

    [ConfigIgnore]
    public string InternalSecret { get; set; } = "do-not-overwrite";
}

public class StrictRequiredConfig
{
    [ConfigRequired]
    public string MandatorySecret { get; set; } = "";
}

public class CliAndBindingTests : IDisposable
{
    private readonly string _testDir;

    public CliAndBindingTests()
    {
        _testDir = Path.Combine(Path.GetTempPath(), "omegaconf_cli_" + Guid.NewGuid().ToString("N"));
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
    public void CliOverrides_ShouldModifyPathsAndDeepValuesWithOperators()
    {
        string dbDir = Path.Combine(_testDir, "db");
        Directory.CreateDirectory(dbDir);

        File.WriteAllText(Path.Combine(_testDir, "config.yaml"), @"
defaults:
  - db: default
app_name: MyBaseApp
active_workers: 2
debug_mode: false
");

        File.WriteAllText(Path.Combine(dbDir, "default.yaml"), @"
driver: sqlite
port: 3306
timeout_seconds: 10
");

        string[] args =
        [
            "app_name=OverrideAppName",
            "active_workers+=4",
            "db.timeout_seconds+=5",
            "debug_mode!=true"
        ];

        var config = OmegaConfBuilder.Create<AppConfig>()
            .SetRoot(_testDir)
            .AddCliArgs(args)
            .Build();

        config.AppName.Should().Be("OverrideAppName");
        config.Workers.Should().Be(6); // 2 + 4
        config.Db.Should().NotBeNull();
        config.Db!.TimeoutSeconds.Should().Be(15); // 10 + 5
    }

    [Fact]
    public void DropGroup_ShouldExcludeDefaultFromComposedTree()
    {
        string dbDir = Path.Combine(_testDir, "db");
        Directory.CreateDirectory(dbDir);

        File.WriteAllText(Path.Combine(_testDir, "config.yaml"), @"
defaults:
  - db: default
app_name: MyBaseApp
");

        File.WriteAllText(Path.Combine(dbDir, "default.yaml"), @"
driver: sqlite
");

        // CLI pass ~db
        var node = OmegaConfBuilder.Create()
            .SetRoot(_testDir)
            .AddCliArgs("~db")
            .BuildNode();

        node.ContainsKey("db").Should().BeFalse();
    }

    [Fact]
    public void ConfigRequired_ShouldThrowWhenMandatoryPropertyMissing()
    {
        var emptyNode = new ConfigObjectNode();

        var act = () => OmegaConfBuilder.Create<StrictRequiredConfig>()
            .SetRoot(_testDir)
            .Build();

        // Write a minimal config that misses MandatorySecret
        File.WriteAllText(Path.Combine(_testDir, "config.yaml"), "other_key: 123");

        var actWithConfig = () => OmegaConfBuilder.Create<StrictRequiredConfig>()
            .SetRoot(_testDir)
            .Build();

        actWithConfig.Should().Throw<MissingRequiredConfigException>()
            .WithMessage("*MandatorySecret*");
    }

    [Fact]
    public void StrictTypeCheck_ShouldThrowOnIncompatibleValue()
    {
        File.WriteAllText(Path.Combine(_testDir, "config.yaml"), @"
app_name: Test
active_workers: not_a_number
");

        var act = () => OmegaConfBuilder.Create<AppConfig>()
            .SetRoot(_testDir)
            .Build();

        act.Should().Throw<ConfigTypeMismatchException>()
            .WithMessage("*Workers*");
    }

    [Fact]
    public void Serialization_ShouldOutputValidYamlAndJson()
    {
        var node = new ConfigObjectNode();
        node.SetChild("service", new ConfigValueNode("payment-api"));
        node.SetChild("port", new ConfigValueNode(5000L));

        string yaml = node.ToYaml();
        yaml.Should().Contain("service: payment-api");
        yaml.Should().Contain("port: 5000");

        string json = node.ToJson();
        json.Should().Contain("\"service\": \"payment-api\"");
        json.Should().Contain("\"port\": 5000");
    }

    [Fact]
    public void MicrosoftConfigurationProvider_ShouldIntegrateSeamlessly()
    {
        File.WriteAllText(Path.Combine(_testDir, "config.yaml"), @"
app_name: MSConfigApp
db:
  host: 127.0.0.1
  port: 5432
");

        var configRoot = new ConfigurationBuilder()
            .AddOmegaConf(cfg => cfg.SetRoot(_testDir))
            .Build();

        configRoot["app_name"].Should().Be("MSConfigApp");
        configRoot["db:host"].Should().Be("127.0.0.1");
        configRoot["db:port"].Should().Be("5432");
    }
}
