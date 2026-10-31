using FluentAssertions;
using OmegaConf.NET.Ast;
using OmegaConf.NET.Common;
using OmegaConf.NET.Interpolation;
using Xunit;

namespace OmegaConf.NET.Tests;

public class InterpolationTests
{
    [Fact]
    public void Interpolation_ShouldResolvePaths_AndPreserveTypes()
    {
        var root = new ConfigObjectNode();
        var serverObj = new ConfigObjectNode();
        serverObj.SetChild("host", new ConfigValueNode("10.0.0.1"));
        serverObj.SetChild("port", new ConfigValueNode(8080L));
        root.SetChild("server", serverObj);

        root.SetChild("target_port", new ConfigValueNode("${server.port}"));
        root.SetChild("url", new ConfigValueNode("http://${server.host}:${server.port}/api"));

        var engine = new InterpolationEngine(root);
        engine.ResolveAll();

        // Native type preservation
        var portNode = (ConfigValueNode)root.GetChild("target_port")!;
        portNode.Value.Should().Be(8080L);

        // String interpolation
        var urlNode = (ConfigValueNode)root.GetChild("url")!;
        urlNode.Value.Should().Be("http://10.0.0.1:8080/api");
    }

    [Fact]
    public void Interpolation_ShouldSupportRelativeAndSiblingDotSyntax()
    {
        // Structure:
        // db:
        //   host: "prod-db"
        //   port: 5432
        //   staging:
        //     port: 5433
        //     connection_string: "Server=${..host};Port=${.port}"   # ..host -> db.host; .port -> db.staging.port

        var root = new ConfigObjectNode();
        var db = new ConfigObjectNode();
        db.SetChild("host", new ConfigValueNode("prod-db"));
        db.SetChild("port", new ConfigValueNode(5432L));

        var staging = new ConfigObjectNode();
        staging.SetChild("port", new ConfigValueNode(5433L));
        staging.SetChild("connection_string", new ConfigValueNode("Server=${..host};Port=${.port}"));

        db.SetChild("staging", staging);
        root.SetChild("db", db);

        var engine = new InterpolationEngine(root);
        engine.ResolveAll();

        var connNode = (ConfigValueNode)staging.GetChild("connection_string")!;
        connNode.Value.Should().Be("Server=prod-db;Port=5433");
    }

    [Fact]
    public void Interpolation_ShouldSupportLiteralEscaping()
    {
        var root = new ConfigObjectNode();
        root.SetChild("server_port", new ConfigValueNode(8080L));
        root.SetChild("bash_command", new ConfigValueNode(@"echo \${HOME} on port ${server_port}"));
        root.SetChild("raw_template", new ConfigValueNode(@"\${server_port}"));

        var engine = new InterpolationEngine(root);
        engine.ResolveAll();

        var bashNode = (ConfigValueNode)root.GetChild("bash_command")!;
        bashNode.Value.Should().Be("echo ${HOME} on port 8080");

        var rawNode = (ConfigValueNode)root.GetChild("raw_template")!;
        rawNode.Value.Should().Be("${server_port}");
    }

    [Fact]
    public void Interpolation_ShouldEvaluateMathAndExpressions()
    {
        var root = new ConfigObjectNode();
        root.SetChild("batch_size", new ConfigValueNode(32L));
        root.SetChild("num_gpus", new ConfigValueNode(4L));
        root.SetChild("total_batch", new ConfigValueNode("${eval: ${batch_size} * ${num_gpus}}"));
        root.SetChild("order_of_ops", new ConfigValueNode("${eval: 10 + 5 * 2}"));
        root.SetChild("division", new ConfigValueNode("${eval: 100 / 4}"));
        root.SetChild("comparison", new ConfigValueNode("${eval: 5 > 2}"));

        var engine = new InterpolationEngine(root);
        engine.ResolveAll();

        ((ConfigValueNode)root.GetChild("total_batch")!).Value.Should().Be("128");
        ((ConfigValueNode)root.GetChild("order_of_ops")!).Value.Should().Be("20");
        ((ConfigValueNode)root.GetChild("division")!).Value.Should().Be("25");
        ((ConfigValueNode)root.GetChild("comparison")!).Value.Should().Be("True");
    }

    [Fact]
    public void Interpolation_ShouldPreserveRawTemplateFormula()
    {
        var root = new ConfigObjectNode();
        root.SetChild("port", new ConfigValueNode(5432L));
        var templateNode = new ConfigValueNode("http://localhost:${port}/");
        root.SetChild("url", templateNode);

        var engine = new InterpolationEngine(root);
        engine.ResolveAll();

        templateNode.Value.Should().Be("http://localhost:5432/");
        templateNode.RawTemplate.Should().Be("http://localhost:${port}/");
    }

    [Fact]
    public void Interpolation_ShouldUseDefaultFallback_WhenPathMissing()
    {
        var root = new ConfigObjectNode();
        root.SetChild("timeout", new ConfigValueNode("${missing.timeout:30}"));

        var engine = new InterpolationEngine(root);
        engine.ResolveAll();

        var timeoutNode = (ConfigValueNode)root.GetChild("timeout")!;
        timeoutNode.Value.Should().Be("30");
    }

    [Fact]
    public void Interpolation_ShouldDetectCircularReferences()
    {
        var root = new ConfigObjectNode();
        root.SetChild("a", new ConfigValueNode("${b}"));
        root.SetChild("b", new ConfigValueNode("${a}"));

        var engine = new InterpolationEngine(root);
        var act = () => engine.ResolveAll();

        act.Should().Throw<ConfigInterpolationException>()
            .WithMessage("*Circular reference*");
    }

    [Fact]
    public void Interpolation_ShouldResolveEnvironmentVariables_AndCustomResolvers()
    {
        Environment.SetEnvironmentVariable("OMEGA_TEST_VAR", "Secret123");

        try
        {
            var root = new ConfigObjectNode();
            root.SetChild("api_key", new ConfigValueNode("${env:OMEGA_TEST_VAR}"));
            root.SetChild("fallback_key", new ConfigValueNode("${env:NON_EXISTENT_VAR, default_secret}"));
            root.SetChild("upper_text", new ConfigValueNode("${upper:hello}"));

            var engine = new InterpolationEngine(root, [new CustomResolver("upper", s => s.ToUpperInvariant())]);
            engine.ResolveAll();

            ((ConfigValueNode)root.GetChild("api_key")!).Value.Should().Be("Secret123");
            ((ConfigValueNode)root.GetChild("fallback_key")!).Value.Should().Be("default_secret");
            ((ConfigValueNode)root.GetChild("upper_text")!).Value.Should().Be("HELLO");
        }
        finally
        {
            Environment.SetEnvironmentVariable("OMEGA_TEST_VAR", null);
        }
    }

    [Fact]
    public void Interpolation_ReferencingMissingMarker_ShouldThrow()
    {
        var root = new ConfigObjectNode();
        root.SetChild("token", new ConfigValueNode(ConfigValueNode.MissingMarker));
        root.SetChild("auth_header", new ConfigValueNode("Bearer ${token}"));

        var engine = new InterpolationEngine(root);
        var act = () => engine.ResolveAll();

        act.Should().Throw<MissingValueMarkerException>();
    }
}
