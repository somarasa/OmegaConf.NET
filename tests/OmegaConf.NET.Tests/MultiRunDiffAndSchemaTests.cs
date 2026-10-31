using FluentAssertions;
using OmegaConf.NET.Ast;
using OmegaConf.NET.Diff;
using OmegaConf.NET.MultiRun;
using OmegaConf.NET.Schema;
using Xunit;

namespace OmegaConf.NET.Tests;

public class MultiRunDiffAndSchemaTests : IDisposable
{
    private readonly string _testDir;

    public MultiRunDiffAndSchemaTests()
    {
        _testDir = Path.Combine(Path.GetTempPath(), "omegaconf_multirun_" + Guid.NewGuid().ToString("N"));
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
    public void MultiRun_ShouldGenerateCartesianCombinations()
    {
        // 2 db choices x 3 worker choices = 6 runs!
        string[] cliArgs =
        [
            "-m",
            "db=sqlite,postgres",
            "workers=2,4,8",
            "log_level=Info"
        ];

        var (nonSweep, matrix) = SweepEngine.ParseSweepArgs(cliArgs);
        matrix.Should().ContainKey("db").WhoseValue.Should().Equal("sqlite", "postgres");
        matrix.Should().ContainKey("workers").WhoseValue.Should().Equal("2", "4", "8");
        nonSweep.Should().ContainSingle().Which.Should().Be("log_level=Info");

        var combinations = SweepEngine.GenerateCartesianProduct(matrix);
        combinations.Should().HaveCount(6);

        // Verify with builder
        File.WriteAllText(Path.Combine(_testDir, "config.yaml"), @"
app_name: MatrixApp
db: sqlite
workers: 1
log_level: Debug
");

        var sweepNodes = OmegaConfBuilder.Create()
            .SetRoot(_testDir)
            .AddCliArgs(cliArgs)
            .BuildSweepNodes();

        sweepNodes.Should().HaveCount(6);
    }

    [Fact]
    public void ConfigDiffer_ShouldIdentifyAdditions_Removals_AndModifications()
    {
        var baseNode = new ConfigObjectNode();
        baseNode.SetChild("host", new ConfigValueNode("localhost"));
        baseNode.SetChild("port", new ConfigValueNode(5432L));
        baseNode.SetChild("old_flag", new ConfigValueNode(true));

        var targetNode = new ConfigObjectNode();
        targetNode.SetChild("host", new ConfigValueNode("prod-db.internal")); // modified
        targetNode.SetChild("port", new ConfigValueNode(5432L)); // unchanged
        targetNode.SetChild("ssl", new ConfigValueNode(true)); // added
        // old_flag removed

        var diff = ConfigDiffer.Compare(baseNode, targetNode);
        diff.HasChanges.Should().BeTrue();
        diff.Entries.Should().HaveCount(3);

        string report = diff.ToString();
        report.Should().Contain("~ host: \"localhost\" -> \"prod-db.internal\"");
        report.Should().Contain("+ ssl: True");
        report.Should().Contain("- old_flag: True");
    }

    [Fact]
    public void JsonSchemaGenerator_ShouldProduceValidDraft07Schema()
    {
        string schemaJson = JsonSchemaGenerator.GenerateSchema<AppConfig>();

        schemaJson.Should().Contain("\"$schema\": \"http://json-schema.org/draft-07/schema#\"");
        schemaJson.Should().Contain("\"title\": \"AppConfig\"");
        schemaJson.Should().Contain("\"app_name\":");
        schemaJson.Should().Contain("\"type\": \"string\"");
        schemaJson.Should().Contain("\"required\":");
        schemaJson.Should().Contain("\"app_name\"");
    }
}
