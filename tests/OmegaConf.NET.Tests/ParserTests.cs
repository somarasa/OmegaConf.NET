using FluentAssertions;
using OmegaConf.NET.Ast;
using OmegaConf.NET.Parsers;
using Xunit;

namespace OmegaConf.NET.Tests;

public class ParserTests
{
    [Fact]
    public void YamlParser_ShouldParseNestedMappingsAndOperators()
    {
        string yaml = @"
app_name: MyAwesomeApp
timeout+=: 10
features:
  - auth
  - logging
db:
  host: localhost
  port: 5432
  password: ???
";
        var parser = new YamlFormatParser();
        var node = parser.Parse(yaml, "app.yaml");

        node.Children["app_name"].Should().BeOfType<ConfigValueNode>()
            .Which.Value.Should().Be("MyAwesomeApp");

        var timeoutNode = node.Children["timeout"].Should().BeOfType<ConfigValueNode>().Subject;
        timeoutNode.Value.Should().Be(10L);
        timeoutNode.Operator.Should().Be(ConfigOperator.Add);

        var dbObj = node.Children["db"].Should().BeOfType<ConfigObjectNode>().Subject;
        dbObj.Children["port"].Should().BeOfType<ConfigValueNode>()
            .Which.Value.Should().Be(5432L);

        var pwdNode = dbObj.Children["password"].Should().BeOfType<ConfigValueNode>().Subject;
        pwdNode.IsMissingMarker.Should().BeTrue();
    }

    [Fact]
    public void JsonParser_ShouldSupportJsoncAndTrailingCommas()
    {
        string jsonc = @"
{
  // Application configuration
  ""service_name"": ""WorkerService"",
  ""threads*="": 2,
  ""tags"": [""worker"", ""high-priority"",],
}
";
        var parser = new JsonFormatParser();
        var node = parser.Parse(jsonc, "config.jsonc");

        node.Children["service_name"].Should().BeOfType<ConfigValueNode>()
            .Which.Value.Should().Be("WorkerService");

        var threadsNode = node.Children["threads"].Should().BeOfType<ConfigValueNode>().Subject;
        threadsNode.Value.Should().Be(2L);
        threadsNode.Operator.Should().Be(ConfigOperator.Multiply);

        var tagsList = node.Children["tags"].Should().BeOfType<ConfigListNode>().Subject;
        tagsList.Items.Should().HaveCount(2);
    }

    [Fact]
    public void TomlParser_ShouldParseTablesAndPrimitives()
    {
        string toml = @"
title = ""TOML Config""
workers = 8

[database]
server = ""192.168.1.1""
enabled = true
";
        var parser = new TomlFormatParser();
        var node = parser.Parse(toml, "config.toml");

        node.Children["title"].Should().BeOfType<ConfigValueNode>()
            .Which.Value.Should().Be("TOML Config");

        var dbNode = node.Children["database"].Should().BeOfType<ConfigObjectNode>().Subject;
        dbNode.Children["server"].Should().BeOfType<ConfigValueNode>()
            .Which.Value.Should().Be("192.168.1.1");
        dbNode.Children["enabled"].Should().BeOfType<ConfigValueNode>()
            .Which.Value.Should().Be(true);
    }
}
