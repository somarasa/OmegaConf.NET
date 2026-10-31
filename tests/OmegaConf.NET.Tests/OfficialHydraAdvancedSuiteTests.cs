using OmegaConf.NET.Ast;
using OmegaConf.NET.Binding;
using OmegaConf.NET.Callbacks;
using OmegaConf.NET.Cli;
using OmegaConf.NET.Common;
using OmegaConf.NET.Composition;
using Xunit;

namespace OmegaConf.NET.Tests;

/// <summary>
/// Ported test suite covering the 6 advanced Hydra capabilities:
/// 1. Interpolated Defaults List (<c>test_defaults_list.py</c> / <c>interpolation_*.yaml</c>)
/// 2. CLI Override Aliases (<c>test_override_aliases.py</c>)
/// 3. In-Memory <see cref="ConfigStore"/> (<c>test_config_store.py</c>)
/// 4. Execution Whitelist for <c>_target_</c> Instantiation (<c>execution_policy.py</c>)
/// 5. Lifecycle Callbacks (<c>test_callbacks.py</c>)
/// 6. CLI Inspection &amp; Help Generator (<c>--cfg</c>, <c>--info</c>, <c>--help</c>)
/// </summary>
public class OfficialHydraAdvancedSuiteTests : IDisposable
{
    private readonly string _tempDir;

    public OfficialHydraAdvancedSuiteTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "OmegaConfAdvancedTests_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);
    }

    public void Dispose()
    {
        ConfigStore.Instance.Clear();
        if (Directory.Exists(_tempDir))
        {
            Directory.Delete(_tempDir, recursive: true);
        }
    }

    private void WriteFile(string relativePath, string content)
    {
        string fullPath = Path.Combine(_tempDir, relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
        File.WriteAllText(fullPath, content);
    }

    // =========================================================================
    // 1. Interpolated Defaults List (modeled on tests/defaults_list/data/interpolation_*.yaml)
    // =========================================================================

    [Fact]
    public void InterpolatedDefaults_FromSelfPropertyAndCliOverride_MatchesHydra()
    {
        WriteFile("db/postgres.yaml", "driver: pg\nport: 5432");
        WriteFile("db/mysql.yaml", "driver: mysql\nport: 3306");
        WriteFile("config.yaml", """
            defaults:
              - db: ${default_db}
              - _self_
            default_db: postgres
            """);

        // 1. Default resolution uses default_db=postgres
        var node1 = OmegaConfBuilder.Create()
            .SetRootDirectory(_tempDir)
            .BuildNode();
        Assert.Equal("pg", node1.Select<string>("db.driver"));
        Assert.Equal(5432, node1.Select<int>("db.port"));

        // 2. Overriding default_db via CLI or SelectPath updates the interpolated default group choice
        var node2 = OmegaConfBuilder.Create()
            .SetRootDirectory(_tempDir)
            .SelectPath("default_db", "mysql")
            .BuildNode();
        Assert.Equal("mysql", node2.Select<string>("db.driver"));
        Assert.Equal(3306, node2.Select<int>("db.port"));
    }

    [Fact]
    public void InterpolatedDefaults_ReferencingEarlierGroupChoicesAndShorthandPaths_MatchesHydra()
    {
        // Modeled on Hydra's interpolation_1.yaml and interpolation_3.yaml:
        // - group1: file1
        // - group1_group2: ${group1}_file2
        // - group1/group2/${group1}
        WriteFile("group1/file1.yaml", "val: 10");
        WriteFile("group1/file2.yaml", "val: 20");
        WriteFile("group1_group2/file1_file2.yaml", "combined: from_file1_file2");
        WriteFile("group1_group2/file2_file2.yaml", "combined: from_file2_file2");
        WriteFile("group1/group2/file1.yaml", "nested: nested_file1");
        WriteFile("group1/group2/file2.yaml", "nested: nested_file2");

        WriteFile("config.yaml", """
            defaults:
              - group1: file1
              - group1_group2: ${group1}_file2
              - group1/group2/${group1}
              - _self_
            """);

        var node1 = OmegaConfBuilder.Create()
            .SetRootDirectory(_tempDir)
            .BuildNode();
        Assert.Equal(10, node1.Select<int>("group1.val"));
        Assert.Equal("from_file1_file2", node1.Select<string>("group1_group2.combined"));
        Assert.Equal("nested_file1", node1.Select<string>("group1.group2.nested"));

        // When group1=file2 is selected via CLI, both downstream interpolated defaults follow it automatically
        var node2 = OmegaConfBuilder.Create()
            .SetRootDirectory(_tempDir)
            .AddCliArgs("group1=file2")
            .BuildNode();
        Assert.Equal(20, node2.Select<int>("group1.val"));
        Assert.Equal("from_file2_file2", node2.Select<string>("group1_group2.combined"));
        Assert.Equal("nested_file2", node2.Select<string>("group1.group2.nested"));
    }

    [Fact]
    public void InterpolatedDefaults_EnvironmentVariableAndCircularDetection_MatchesHydra()
    {
        WriteFile("db/postgres.yaml", "driver: pg");
        WriteFile("db/sqlite.yaml", "driver: sqlite");
        WriteFile("config.yaml", """
            defaults:
              - db: ${oc.env:OMEGACONF_TEST_DEFAULT_DB, sqlite}
              - _self_
            """);

        var fallbackNode = OmegaConfBuilder.Create()
            .SetRootDirectory(_tempDir)
            .BuildNode();
        Assert.Equal("sqlite", fallbackNode.Select<string>("db.driver"));

        // Circular interpolation in defaults list throws ConfigInterpolationException
        WriteFile("circular.yaml", """
            defaults:
              - group1: ${group2}
              - group2: ${group1}
            """);

        Assert.Throws<ConfigInterpolationException>(() =>
            OmegaConfBuilder.Create()
                .SetRootDirectory(_tempDir)
                .SetEntryFile("circular.yaml")
                .BuildNode());
    }

    // =========================================================================
    // 2. CLI Override Aliases (modeled on tests/test_override_aliases.py)
    // =========================================================================

    [Fact]
    public void OverrideAliases_KeyAndValueExpansion_MatchesOfficialHydra()
    {
        var raw = OmegaConfig.ParseYaml("""
            hydra:
              aliases:
                "batch_size, bs": trainer.batch_size
                lr: optimizer.learning_rate
                config: config@_global_
                "verbose, v": verbose=true
                check: action=check
                seed: +trainer.seed=42
            """);

        var aliases = OverrideAliases.FromConfig(raw);
        Assert.False(aliases.IsEmpty);

        // Key aliases
        Assert.Equal("trainer.batch_size=32", aliases.Expand("bs=32"));
        Assert.Equal("trainer.batch_size=32", aliases.Expand("batch_size=32"));
        Assert.Equal("+trainer.batch_size=32", aliases.Expand("+bs=32"));
        Assert.Equal("++trainer.batch_size=32", aliases.Expand("++bs=32"));
        Assert.Equal("~trainer.batch_size", aliases.Expand("~bs"));
        Assert.Equal("~trainer.batch_size=8", aliases.Expand("~bs=8"));
        Assert.Equal("config@_global_=user", aliases.Expand("config=user"));
        Assert.Equal("other=1", aliases.Expand("other=1"));

        // Key alias without value throws ConfigSyntaxException (unless ~ delete)
        Assert.Throws<ConfigSyntaxException>(() => aliases.Expand("bs"));
        Assert.Throws<ConfigSyntaxException>(() => aliases.Expand("+bs"));
        Assert.Throws<ConfigSyntaxException>(() => aliases.Expand("++bs"));

        // Value aliases
        Assert.Equal("verbose=true", aliases.Expand("verbose"));
        Assert.Equal("verbose=true", aliases.Expand("v"));
        Assert.Equal("verbose=false", aliases.Expand("verbose=false"));
        Assert.Equal("v=false", aliases.Expand("v=false"));
        Assert.Equal("~verbose=true", aliases.Expand("~v"));
        Assert.Equal("+verbose=true", aliases.Expand("+v"));
        Assert.Equal("++verbose=true", aliases.Expand("++v"));
        Assert.Equal("action=check", aliases.Expand("check"));
        Assert.Equal("+trainer.seed=42", aliases.Expand("seed"));
        Assert.Equal("++trainer.seed=42", aliases.Expand("++seed"));
        Assert.Equal("~trainer.seed=42", aliases.Expand("~seed"));
    }

    [Fact]
    public void OverrideAliases_ValidationRules_MatchesOfficialHydra()
    {
        // 1. Escaped interpolation in target is allowed
        var escaped = OmegaConfig.ParseYaml("""
            hydra:
              aliases:
                a: \${not_interp}
            """);
        var escapedAliases = OverrideAliases.FromConfig(escaped);
        Assert.Equal(@"\${not_interp}=1", escapedAliases.Expand("a=1"));

        // 2. Invalid alias definitions throw ConfigSyntaxException
        string[] invalidYamls =
        [
            "hydra:\n  aliases:\n    - a\n    - b",                 // list instead of mapping
            "hydra:\n  aliases: ${some.ref}",                       // interpolated aliases mapping
            "hydra:\n  aliases:\n    \"\": target",                 // empty alias name
            "hydra:\n  aliases:\n    \",bs\": target",              // empty part in comma-separated
            "hydra:\n  aliases:\n    \"+bs\": target",              // prefix in alias name
            "hydra:\n  aliases:\n    \"~bs\": target",              // tilde in alias name
            "hydra:\n  aliases:\n    \"bs=16\": target",            // value in alias name
            "hydra:\n  aliases:\n    \"${interp}\": target",        // interpolation in alias name
            "hydra:\n  aliases:\n    bs: x\n    \"batch_size, bs\": y", // duplicate alias name
            "hydra:\n  aliases:\n    a: \"\"",                      // empty target
            "hydra:\n  aliases:\n    a: null",                      // null target
            "hydra:\n  aliases:\n    a: 42",                        // integer target
            "hydra:\n  aliases:\n    a: ${some.ref}",               // interpolation in target
            "hydra:\n  aliases:\n    a: +x",                        // prefix on key-only target
            "hydra:\n  aliases:\n    a: ~x"                         // tilde on key-only target
        ];

        foreach (var yaml in invalidYamls)
        {
            var node = OmegaConfig.ParseYaml(yaml);
            Assert.Throws<ConfigSyntaxException>(() => OverrideAliases.FromConfig(node));
        }

        // 3. Modifying hydra.aliases from CLI is rejected
        Assert.Throws<ConfigSyntaxException>(() =>
            OverrideAliases.ValidateCliDoesNotModifyAliases(["hydra.aliases.check=step"]));
        Assert.Throws<ConfigSyntaxException>(() =>
            OverrideAliases.ValidateCliDoesNotModifyAliases(["~hydra.aliases"]));
    }

    [Fact]
    public void OverrideAliases_EndToEndSingleRunMultiRunAndIncludedConfigGuard_MatchesHydra()
    {
        WriteFile("config.yaml", """
            trainer:
              batch_size: 8
              lr: 0.01
            verbose: false
            hydra:
              aliases:
                "batch_size, bs": trainer.batch_size
                lr: trainer.lr
                "verbose, v": verbose=true
            """);

        // 1. Single-run with aliases
        var single = OmegaConfBuilder.Create()
            .SetRootDirectory(_tempDir)
            .AddCliArgs("bs=64", "v", "+seed=123")
            .BuildNode();
        Assert.Equal(64, single.Select<int>("trainer.batch_size"));
        Assert.True(single.Select<bool>("verbose"));
        Assert.Equal(123, single.Select<int>("seed"));

        // 2. Multi-run sweep with aliases (bs=16,32 and v)
        var sweeps = OmegaConfBuilder.Create()
            .SetRootDirectory(_tempDir)
            .AddCliArgs("-m", "bs=16,32", "v")
            .BuildSweepNodes();
        Assert.Equal(2, sweeps.Count);
        Assert.Equal(16, sweeps[0].Select<int>("trainer.batch_size"));
        Assert.True(sweeps[0].Select<bool>("verbose"));
        Assert.Equal(32, sweeps[1].Select<int>("trainer.batch_size"));
        Assert.True(sweeps[1].Select<bool>("verbose"));

        // 3. Struct mode error on alias includes "Alias expansion: 'orig' -> 'expanded'"
        WriteFile("bad_alias.yaml", """
            trainer:
              batch_size: 8
            hydra:
              aliases:
                bad: trainer.nonexistent
            """);
        var ex = Assert.Throws<ConfigSyntaxException>(() =>
            OmegaConfBuilder.Create()
                .SetRootDirectory(_tempDir)
                .SetEntryFile("bad_alias.yaml")
                .WithStructMode()
                .AddCliArgs("bad=10")
                .BuildNode());
        Assert.Contains("Alias expansion: 'bad=10' -> 'trainer.nonexistent=10'", ex.Message);

        // 4. Defining hydra.aliases in an included config group file is rejected
        WriteFile("group/with_aliases.yaml", """
            x: 1
            hydra:
              aliases:
                a: x
            """);
        WriteFile("include_alias.yaml", """
            defaults:
              - group: with_aliases
            """);
        Assert.Throws<ConfigSyntaxException>(() =>
            OmegaConfBuilder.Create()
                .SetRootDirectory(_tempDir)
                .SetEntryFile("include_alias.yaml")
                .BuildNode());
    }

    // =========================================================================
    // 3. In-Memory ConfigStore (modeled on tests/test_config_store.py)
    // =========================================================================

    public sealed class InMemoryDbSchema
    {
        public string Host { get; set; } = "db.internal";
        public int Port { get; set; } = 5432;
    }

    [Fact]
    public void ConfigStore_StoreLoadListCollisionModesAndWithProvider_MatchesOfficialHydra()
    {
        var cs = new ConfigStore();

        // 1. Store and Load deep copy verification
        var rawDict = new Dictionary<string, object?> { ["name"] = "test1" };
        cs.Store(name: "config", node: rawDict);
        var loaded1 = cs.Load("config.yaml");
        Assert.Equal("test1", loaded1.Node.Select<string>("name"));
        loaded1.Node.SetChild("name", new ConfigValueNode("mutated"));
        Assert.Equal("test1", cs.Load("config").Node.Select<string>("name"));

        // 2. Empty group string "" is normalized to null (root)
        cs.Store(name: "root_cfg", node: new Dictionary<string, object?> { ["k"] = 1 }, group: "");
        Assert.Null(cs.Load("root_cfg").Group);

        // 3. Subgroup storage and List() / GetEntryType()
        cs.Store(name: "mysql", node: new InMemoryDbSchema { Host = "mysql.local", Port = 3306 }, group: "db", package: "primary_db");
        cs.Store(name: "postgres", node: typeof(InMemoryDbSchema), group: "db");
        Assert.Equal(ConfigStoreEntryType.Group, cs.GetEntryType("db"));
        Assert.Equal(ConfigStoreEntryType.Config, cs.GetEntryType("db/mysql"));
        Assert.Equal(ConfigStoreEntryType.NotFound, cs.GetEntryType("db/unknown"));
        Assert.Equal(["mysql.yaml", "postgres.yaml"], cs.List("db"));

        // 4. Collision behavior: replace=true, replace=false, replace=null (default), and .yaml normalization
        using (var scoped = cs.WithProvider("provider_a"))
        {
            scoped.Store(name: "collision_test", node: new Dictionary<string, object?> { ["v"] = 1 });
        }

        // replace: false throws ArgumentException with provider details and leaves entry untouched
        var colEx = Assert.Throws<ArgumentException>(() =>
            cs.Store(name: "collision_test.yaml", node: new Dictionary<string, object?> { ["v"] = 2 }, provider: "provider_b", replace: false));
        Assert.Contains("provider_a", colEx.Message);
        Assert.Contains("provider_b", colEx.Message);
        Assert.Equal(1, cs.Load("collision_test").Node.Select<int>("v"));
        Assert.Equal("provider_a", cs.Load("collision_test").Provider);

        // replace: null (default) overwrites and records warning
        Assert.Empty(cs.Warnings);
        cs.Store(name: "collision_test.yaml", node: new Dictionary<string, object?> { ["v"] = 3 }, provider: "provider_c", replace: null);
        Assert.Single(cs.Warnings);
        Assert.Contains("Overwriting 'collision_test.yaml'", cs.Warnings[0]);
        Assert.Equal(3, cs.Load("collision_test").Node.Select<int>("v"));
        Assert.Equal("provider_c", cs.Load("collision_test").Provider);

        // replace: true overwrites silently without adding a warning
        cs.Store(name: "collision_test", node: new Dictionary<string, object?> { ["v"] = 4 }, provider: "provider_d", replace: true);
        Assert.Single(cs.Warnings);
        Assert.Equal(4, cs.Load("collision_test").Node.Select<int>("v"));
        Assert.Equal("provider_d", cs.Load("collision_test").Provider);
    }

    [Fact]
    public void ConfigStore_EndToEndPureInMemoryAndHybridComposition_MatchesHydra()
    {
        var cs = new ConfigStore();
        cs.Store(
            name: "app_config",
            node: OmegaConfig.ParseYaml("""
                defaults:
                  - db: postgres
                  - _self_
                app_name: PureMemoryService
                """));
        cs.Store(name: "postgres", node: typeof(InMemoryDbSchema), group: "db");
        cs.Store(name: "mysql", node: new InMemoryDbSchema { Host = "mysql.mem", Port = 3306 }, group: "db");

        // Compose purely from ConfigStore with no YAML files in _tempDir
        var node = OmegaConfBuilder.Create()
            .SetRootDirectory(_tempDir)
            .SetEntryFile("app_config")
            .WithConfigStore(cs)
            .AddCliArgs("db=mysql", "db.port=3307")
            .BuildNode();

        Assert.Equal("PureMemoryService", node.Select<string>("app_name"));
        Assert.Equal("mysql.mem", node.Select<string>("db.host"));
        Assert.Equal(3307, node.Select<int>("db.port"));
    }

    // =========================================================================
    // 4. Execution Whitelist for _target_ Instantiation (modeled on execution_policy.py)
    // =========================================================================

    public interface IPlugin
    {
        string Describe();
    }

    public sealed class SafePlugin : IPlugin
    {
        public string Name { get; set; } = "safe";
        public IPlugin? Inner { get; set; }
        public string Describe() => Inner != null ? $"{Name}->{Inner.Describe()}" : Name;
    }

    public sealed class UntrustedPlugin : IPlugin
    {
        public string Describe() => "untrusted";
    }

    [Fact]
    public void ExecutionWhitelist_AllowsWhitelistedAndBlocksUnauthorizedOrSystemTargets_MatchesHydra()
    {
        string safeTarget = typeof(SafePlugin).FullName!;
        string untrustedTarget = typeof(UntrustedPlugin).FullName!;

        var safeNode = OmegaConfig.ParseYaml($"_target_: \"{safeTarget}\"\nname: alpha");
        var untrustedNode = OmegaConfig.ParseYaml($"_target_: \"{untrustedTarget}\"");

        // 1. Exact whitelist permits SafePlugin and blocks classUntrustedPlugin
        var plugin = OmegaConfig.Instantiate<IPlugin>(safeNode, allowedTargets: [safeTarget]);
        Assert.Equal("alpha", plugin.Describe());

        Assert.Throws<ConfigSecurityException>(() =>
            OmegaConfig.Instantiate<IPlugin>(untrustedNode, allowedTargets: [safeTarget]));

        // 2. Wildcard prefix pattern "OmegaConf.NET.Tests.*" permits both
        var wildcardPlugin = OmegaConfig.Instantiate<IPlugin>(untrustedNode, allowedTargets: ["OmegaConf.NET.Tests.*"]);
        Assert.Equal("untrusted", wildcardPlugin.Describe());

        // 3. Nested recursive _target_ and _partial_ factories also enforce the whitelist
        var nestedUntrusted = OmegaConfig.ParseYaml($$"""
            _target_: "{{safeTarget}}"
            name: outer
            inner:
              _target_: "{{untrustedTarget}}"
            """);
        Assert.Throws<ConfigSecurityException>(() =>
            OmegaConfig.Instantiate<IPlugin>(nestedUntrusted, allowedTargets: [safeTarget]));

        var partialUntrusted = OmegaConfig.ParseYaml($$"""
            _target_: "{{untrustedTarget}}"
            _partial_: true
            """);
        Assert.Throws<ConfigSecurityException>(() =>
            OmegaConfig.Instantiate<Func<IPlugin>>(partialUntrusted, allowedTargets: [safeTarget]));

        // 4. Dangerous built-in system types (System.Diagnostics.Process) are blocked by default even without an explicit whitelist
        var systemTargetNode = OmegaConfig.ParseYaml("_target_: \"System.Diagnostics.Process, System.Diagnostics.Process\"");
        Assert.Throws<ConfigSecurityException>(() =>
            OmegaConfig.Instantiate<object>(systemTargetNode));
    }

    // =========================================================================
    // 5. Lifecycle Callbacks (modeled on tests/test_callbacks.py)
    // =========================================================================

    public sealed class RecordingCallback : ConfigCallback
    {
        public string Name { get; set; } = "cb";
        public bool ThrowOnRunStart { get; set; }
        public List<string> Events { get; } = [];

        public override void OnComposeConfig(ConfigObjectNode config, string? configName = null, IReadOnlyList<string>? cliOverrides = null) =>
            Events.Add($"{Name}:OnComposeConfig");

        public override void OnRunStart(ConfigObjectNode config, string? configName = null)
        {
            Events.Add($"{Name}:OnRunStart");
            if (ThrowOnRunStart)
            {
                throw new InvalidOperationException($"Simulated failure in {Name}");
            }
        }

        public override void OnRunEnd(ConfigObjectNode config, string? configName = null) =>
            Events.Add($"{Name}:OnRunEnd");

        public override void OnMultiRunStart(ConfigObjectNode config, string? configName = null) =>
            Events.Add($"{Name}:OnMultiRunStart");

        public override void OnMultiRunEnd(IReadOnlyList<ConfigObjectNode> jobConfigs, string? configName = null) =>
            Events.Add($"{Name}:OnMultiRunEnd:{jobConfigs.Count}");

        public override void OnJobStart(ConfigObjectNode config, int jobId = 0) =>
            Events.Add($"{Name}:OnJobStart:{jobId}");

        public override void OnJobEnd(ConfigObjectNode config, int jobId = 0, Exception? error = null) =>
            Events.Add($"{Name}:OnJobEnd:{jobId}");
    }

    [Fact]
    public void LifecycleCallbacks_ForwardStartReverseEndAndFaultIsolation_MatchesOfficialHydra()
    {
        WriteFile("config.yaml", "lr: 0.01");

        var sharedLog = new List<string>();
        var cb1 = new DelegatingLogCallback("cb1", sharedLog, throwOnStart: true);
        var cb2 = new DelegatingLogCallback("cb2", sharedLog, throwOnStart: false);

        // 1. Single-run: Start hooks run in order (cb1, cb2); End hooks run in REVERSE order (cb2, cb1)!
        //    Also verifies cb1 throwing in OnRunStart is caught & logged to CallbackWarnings and cb2 still runs!
        var builder = OmegaConfBuilder.Create()
            .SetRootDirectory(_tempDir)
            .AddCallback(cb1)
            .AddCallback(cb2);

        var node = builder.BuildNode();
        Assert.Equal(0.01, node.Select<double>("lr"));
        Assert.Single(builder.CallbackWarnings);
        Assert.Contains("Simulated failure in cb1", builder.CallbackWarnings[0]);

        Assert.Equal(
            [
                "cb1:OnComposeConfig",
                "cb2:OnComposeConfig",
                "cb1:OnRunStart",
                "cb2:OnRunStart",
                "cb1:OnJobStart:0",
                "cb2:OnJobStart:0",
                "cb2:OnJobEnd:0",
                "cb1:OnJobEnd:0",
                "cb2:OnRunEnd",
                "cb1:OnRunEnd"
            ],
            sharedLog);

        // 2. Multi-run sweep: OnMultiRunStart (cb1, cb2) -> jobs -> OnMultiRunEnd (cb2, cb1)
        sharedLog.Clear();
        var sweepCb1 = new DelegatingLogCallback("cb1", sharedLog, throwOnStart: false);
        var sweepCb2 = new DelegatingLogCallback("cb2", sharedLog, throwOnStart: false);

        var sweeps = OmegaConfBuilder.Create()
            .SetRootDirectory(_tempDir)
            .AddCallback(sweepCb1)
            .AddCallback(sweepCb2)
            .AddCliArgs("-m", "lr=0.01,0.1")
            .BuildSweepNodes();

        Assert.Equal(2, sweeps.Count);
        Assert.Equal("cb1:OnMultiRunStart", sharedLog[0]);
        Assert.Equal("cb2:OnMultiRunStart", sharedLog[1]);
        Assert.Equal("cb2:OnMultiRunEnd:2", sharedLog[^2]);
        Assert.Equal("cb1:OnMultiRunEnd:2", sharedLog[^1]);

        // 3. Config-driven hydra.callbacks instantiated via _target_
        StaticCallbackRecorder.Events.Clear();
        string cbTarget = typeof(StaticCallbackRecorder).FullName!;
        WriteFile("with_cb.yaml", $$"""
            lr: 0.05
            hydra:
              callbacks:
                my_cb:
                  _target_: "{{cbTarget}}"
                  tag: yaml_cb
            """);

        OmegaConfBuilder.Create()
            .SetRootDirectory(_tempDir)
            .SetEntryFile("with_cb.yaml")
            .BuildNode();

        Assert.Equal(
            [
                "yaml_cb:OnComposeConfig",
                "yaml_cb:OnRunStart",
                "yaml_cb:OnJobStart:0",
                "yaml_cb:OnJobEnd:0",
                "yaml_cb:OnRunEnd"
            ],
            StaticCallbackRecorder.Events);
    }

    public sealed class StaticCallbackRecorder : ConfigCallback
    {
        public static List<string> Events { get; } = [];
        public string Tag { get; set; } = "cb";

        public override void OnComposeConfig(ConfigObjectNode config, string? configName = null, IReadOnlyList<string>? cliOverrides = null) =>
            Events.Add($"{Tag}:OnComposeConfig");

        public override void OnRunStart(ConfigObjectNode config, string? configName = null) =>
            Events.Add($"{Tag}:OnRunStart");

        public override void OnJobStart(ConfigObjectNode config, int jobId = 0) =>
            Events.Add($"{Tag}:OnJobStart:{jobId}");

        public override void OnJobEnd(ConfigObjectNode config, int jobId = 0, Exception? error = null) =>
            Events.Add($"{Tag}:OnJobEnd:{jobId}");

        public override void OnRunEnd(ConfigObjectNode config, string? configName = null) =>
            Events.Add($"{Tag}:OnRunEnd");
    }

    private sealed class DelegatingLogCallback(string name, List<string> sharedLog, bool throwOnStart) : ConfigCallback
    {
        public override void OnComposeConfig(ConfigObjectNode config, string? configName = null, IReadOnlyList<string>? cliOverrides = null) =>
            sharedLog.Add($"{name}:OnComposeConfig");

        public override void OnRunStart(ConfigObjectNode config, string? configName = null)
        {
            sharedLog.Add($"{name}:OnRunStart");
            if (throwOnStart)
            {
                throw new InvalidOperationException($"Simulated failure in {name}");
            }
        }

        public override void OnRunEnd(ConfigObjectNode config, string? configName = null) =>
            sharedLog.Add($"{name}:OnRunEnd");

        public override void OnMultiRunStart(ConfigObjectNode config, string? configName = null) =>
            sharedLog.Add($"{name}:OnMultiRunStart");

        public override void OnMultiRunEnd(IReadOnlyList<ConfigObjectNode> jobConfigs, string? configName = null) =>
            sharedLog.Add($"{name}:OnMultiRunEnd:{jobConfigs.Count}");

        public override void OnJobStart(ConfigObjectNode config, int jobId = 0) =>
            sharedLog.Add($"{name}:OnJobStart:{jobId}");

        public override void OnJobEnd(ConfigObjectNode config, int jobId = 0, Exception? error = null) =>
            sharedLog.Add($"{name}:OnJobEnd:{jobId}");
    }

    // =========================================================================
    // 6. CLI Inspection & Help Generator (--cfg, --info, --help)
    // =========================================================================

    [Fact]
    public void CliInspection_InfoAliasesCfgAndHelp_MatchesOfficialHydra()
    {
        WriteFile("db/postgres.yaml", "driver: pg");
        WriteFile("db/mysql.yaml", "driver: mysql");
        WriteFile("config.yaml", """
            defaults:
              - db: postgres
              - _self_
            trainer:
              batch_size: 8
            hydra:
              aliases:
                "batch_size, bs": trainer.batch_size
                check: action=check
            """);

        // 1. --info aliases (even with an invalid type value like bs=not_an_int)
        var infoAliasesBuilder = OmegaConfBuilder.Create()
            .SetRootDirectory(_tempDir)
            .AddCliArgs("bs=not_an_int", "--info", "aliases");
        Assert.True(infoAliasesBuilder.TryHandleCliInspection(out string aliasesOutput));
        Assert.Contains("== Override aliases ==", aliasesOutput);
        Assert.Contains("batch_size, bs -> trainer.batch_size", aliasesOutput);
        Assert.Contains("check -> action=check", aliasesOutput);
        Assert.Contains("Expanded overrides:", aliasesOutput);
        Assert.Contains("bs=not_an_int -> trainer.batch_size=not_an_int", aliasesOutput);

        // 2. --info aliases when no aliases are defined
        WriteFile("no_aliases.yaml", "x: 1");
        var noAliasesBuilder = OmegaConfBuilder.Create()
            .SetRootDirectory(_tempDir)
            .SetEntryFile("no_aliases.yaml")
            .AddCliArgs("--info", "aliases");
        Assert.True(noAliasesBuilder.TryHandleCliInspection(out string emptyAliasesOutput));
        Assert.Equal("No override aliases defined", emptyAliasesOutput);

        // 3. --cfg prints the composed & interpolated YAML
        var cfgBuilder = OmegaConfBuilder.Create()
            .SetRootDirectory(_tempDir)
            .AddCliArgs("bs=32", "--cfg", "job");
        Assert.True(cfgBuilder.TryHandleCliInspection(out string cfgOutput));
        Assert.Contains("batch_size: 32", cfgOutput);
        Assert.Contains("driver: pg", cfgOutput);

        // 4. --help prints discovered config groups & choices AND override aliases
        var helpBuilder = OmegaConfBuilder.Create()
            .SetRootDirectory(_tempDir)
            .AddCliArgs("--help");
        Assert.True(helpBuilder.TryHandleCliInspection(out string helpOutput));
        Assert.Contains("== Configuration Groups ==", helpOutput);
        Assert.Contains("db: mysql, postgres", helpOutput);
        Assert.Contains("== Override aliases ==", helpOutput);
        Assert.Contains("batch_size, bs -> trainer.batch_size", helpOutput);
    }
}
