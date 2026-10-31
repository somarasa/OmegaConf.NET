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
