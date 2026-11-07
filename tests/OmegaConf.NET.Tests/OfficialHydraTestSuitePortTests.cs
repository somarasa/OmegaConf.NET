using OmegaConf.NET.Ast;
using OmegaConf.NET.Binding;
using OmegaConf.NET.Common;
using OmegaConf.NET.Serialization;
using Xunit;

namespace OmegaConf.NET.Tests;

/// <summary>
/// Ported test suite modeled directly on the official Python Hydra repository:
/// - <c>tests/test_missing_literals.py</c>
/// - <c>tests/test_override_key_paths.py</c>
/// - <c>tests/test_overrides_parser.py</c>
/// - <c>tests/defaults_list/test_defaults_list.py</c>
/// - <c>tests/defaults_list/test_group_package.py</c>
/// - <c>tests/test_compose.py</c>
/// - <c>tests/instantiate/test_instantiate.py</c>
/// </summary>
public class OfficialHydraTestSuitePortTests : IDisposable
{
    private readonly string _tempDir;

    public OfficialHydraTestSuitePortTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "OmegaConfOfficialHydraPort_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);
    }

    public void Dispose()
    {
        if (Directory.Exists(_tempDir))
        {
            Directory.Delete(_tempDir, recursive: true);
        }
    }

    // =========================================================================
    // 1. Ported from Hydra tests/test_missing_literals.py
    // =========================================================================

    [Fact]
    public void MissingLiterals_EscapedAndMandatoryMissing_BehaveLikeOfficialHydra()
    {
        File.WriteAllText(Path.Combine(_tempDir, "config.yaml"), """
            normal: hello
            mandatory_missing: ???
            escaped_literal: \???
            double_escaped: \\???
            """);

        var node = OmegaConfBuilder.Create()
            .SetRootDirectory(_tempDir)
            .BuildNode();

        // 1. Mandatory missing marker (???) has IsMissingMarker = true and throws on Bind
        var mandatoryVn = (ConfigValueNode)node.GetChild("mandatory_missing")!;
        Assert.True(mandatoryVn.IsMissingMarker);

        // 2. Escaped literal (\???) unescapes to literal string "???" and is NOT a missing marker
        var escapedVn = (ConfigValueNode)node.GetChild("escaped_literal")!;
        Assert.False(escapedVn.IsMissingMarker);
        Assert.Equal("???", escapedVn.Value);

        // 3. Double escaped (\\???) unescapes one leading backslash to "\???" and is NOT a missing marker
        var doubleEscapedVn = (ConfigValueNode)node.GetChild("double_escaped")!;
        Assert.False(doubleEscapedVn.IsMissingMarker);
        Assert.Equal(@"\???", doubleEscapedVn.Value);
    }

    [Fact]
    public void MissingLiterals_CliOverrideAndConditionalDeletion_MatchesOfficialHydra()
    {
        File.WriteAllText(Path.Combine(_tempDir, "config.yaml"), """
            a: ???
            b: \???
            c: value
            d: value
            """);

        // ~a=??? deletes 'a' because 'a' is a missing marker
        // ~b=\??? deletes 'b' because 'b' is the literal string "???"
        // c=??? turns 'c' into a missing marker
        // d=\??? sets 'd' to the literal string "???"
        var node = OmegaConfBuilder.Create()
            .SetRootDirectory(_tempDir)
            .AddCliArgs("~a=???", @"~b=\???", "c=???", @"d=\???")
            .BuildNode();

        Assert.Null(node.GetChild("a"));
        Assert.Null(node.GetChild("b"));

        var cNode = (ConfigValueNode)node.GetChild("c")!;
        Assert.True(cNode.IsMissingMarker);

        var dNode = (ConfigValueNode)node.GetChild("d")!;
        Assert.False(dNode.IsMissingMarker);
        Assert.Equal("???", dNode.Value);
    }

    [Fact]
    public void MissingLiterals_ConditionalDeleteMismatch_ThrowsConfigStructException()
    {
        File.WriteAllText(Path.Combine(_tempDir, "config.yaml"), """
            a: ???
            b: \???
            """);

        // Trying to delete mandatory missing 'a' with literal '\???' should fail
        Assert.Throws<ConfigStructException>(() =>
            OmegaConfBuilder.Create()
                .SetRootDirectory(_tempDir)
                .AddCliArgs(@"~a=\???")
                .BuildNode());

        // Trying to delete literal 'b' (\???) with mandatory missing '???' should fail
        Assert.Throws<ConfigStructException>(() =>
            OmegaConfBuilder.Create()
                .SetRootDirectory(_tempDir)
                .AddCliArgs("~b=???")
                .BuildNode());
    }

    // =========================================================================
    // 2. Ported from Hydra tests/test_override_key_paths.py
    // =========================================================================

    [Theory]
    [InlineData("a[0]=3", new long[] { 3, 2 })]
    [InlineData("a.0=3", new long[] { 3, 2 })]
    [InlineData("a[-1]=3", new long[] { 1, 3 })]
    [InlineData("a.-1=3", new long[] { 1, 3 })]
    [InlineData("~a[0]", new long[] { 2 })]
    [InlineData("~a.0", new long[] { 2 })]
    [InlineData("~a[-1]", new long[] { 1 })]
    [InlineData("~a[0]=1", new long[] { 2 })]
    [InlineData("~a[-1]=2", new long[] { 1 })]
    public void OverrideKeyPaths_ListIndexingAndDeletion_MatchesHydra(string cliOverride, long[] expectedItems)
    {
        File.WriteAllText(Path.Combine(_tempDir, "config.yaml"), """
            a:
              - 1
              - 2
            """);

        var node = OmegaConfBuilder.Create()
            .SetRootDirectory(_tempDir)
            .AddCliArgs(cliOverride)
            .BuildNode();

        var list = ConfigBinder.Bind<long[]>(node.GetChild("a")!);
        Assert.Equal(expectedItems, list);
    }

    [Fact]
    public void OverrideKeyPaths_NestedListAndBracketDictKeys_MatchesHydra()
    {
        File.WriteAllText(Path.Combine(_tempDir, "config.yaml"), """
            matrix:
              - items: [10, 20, 30]
              - items: [40, 50, 60]
            metrics:
              20s_labels: 1
              normal_key: 5
            """);

        var node = OmegaConfBuilder.Create()
            .SetRootDirectory(_tempDir)
            .AddCliArgs(
                "matrix[0].items[-1]=99",
                "~matrix[1].items[0]=40",
                "metrics[20s_labels]=42")
            .BuildNode();

        Assert.Equal(99L, node.Select<long>("matrix[0].items[-1]"));
        Assert.Equal(99L, node.Select<long>("matrix.0.items.2"));

        var secondRow = ConfigBinder.Bind<long[]>(node.Select("matrix[1].items") as ConfigNode ?? throw new Exception());
        Assert.Equal([50L, 60L], secondRow);

        Assert.Equal(42L, node.Select<long>("metrics[20s_labels]"));
    }

    [Fact]
    public void OverrideKeyPaths_ConditionalDeleteValueMismatch_Throws()
    {
        File.WriteAllText(Path.Combine(_tempDir, "config.yaml"), """
            a: [1, 2]
            b: 10
            """);

        // ~a[0]=99 should fail because a[0] is 1, not 99
        Assert.Throws<ConfigStructException>(() =>
            OmegaConfBuilder.Create()
                .SetRootDirectory(_tempDir)
                .AddCliArgs("~a[0]=99")
                .BuildNode());

        // ~b=20 should fail because b is 10, not 20
        Assert.Throws<ConfigStructException>(() =>
            OmegaConfBuilder.Create()
                .SetRootDirectory(_tempDir)
                .AddCliArgs("~b=20")
                .BuildNode());
    }

    // =========================================================================
    // 3. Ported from Hydra tests/test_overrides_parser.py & test_compose.py
    // =========================================================================

    [Fact]
    public void CliGrammarFunctions_TypeCastsSortAndExtendList_MatchHydra()
    {
        File.WriteAllText(Path.Combine(_tempDir, "config.yaml"), """
            as_str: 10
            as_int: "42"
            as_float: 3
            as_bool: "true"
            sorted_asc: [3, 1, 2]
            sorted_desc: [1, 3, 2]
            tags:
              - alpha
              - beta
            """);

        var node = OmegaConfBuilder.Create()
            .SetRootDirectory(_tempDir)
            .AddCliArgs(
                "as_str=str(12345)",
                "as_int=int(99)",
                "as_float=float(2.5)",
                "as_bool=bool(false)",
                "sorted_asc=sort([30, 10, 20])",
                "sorted_desc=sort([10, 30, 20], reverse=true)",
                "tags=extend_list(gamma, delta)")
            .BuildNode();

        Assert.IsType<string>(((ConfigValueNode)node.GetChild("as_str")!).Value);
        Assert.Equal("12345", ((ConfigValueNode)node.GetChild("as_str")!).Value);
        Assert.Equal(99L, ((ConfigValueNode)node.GetChild("as_int")!).Value);
        Assert.Equal(2.5, ((ConfigValueNode)node.GetChild("as_float")!).Value);
        Assert.Equal(false, ((ConfigValueNode)node.GetChild("as_bool")!).Value);

        Assert.Equal([10L, 20L, 30L], ConfigBinder.Bind<long[]>(node.GetChild("sorted_asc")!));
        Assert.Equal([30L, 20L, 10L], ConfigBinder.Bind<long[]>(node.GetChild("sorted_desc")!));
        Assert.Equal(["alpha", "beta", "gamma", "delta"], ConfigBinder.Bind<string[]>(node.GetChild("tags")!));
    }

    [Fact]
    public void CliGrammarFunctions_SortAndShuffleInMultirunSweep_Work()
    {
        File.WriteAllText(Path.Combine(_tempDir, "config.yaml"), """
            lr: 0.01
            seed: 1
            """);

        var sweeps = OmegaConfBuilder.Create()
            .SetRootDirectory(_tempDir)
            .AddCliArgs("-m", "seed=sort(30, 10, 20, reverse=true)", "lr=shuffle(0.1, 0.2)")
            .BuildSweepNodes();

        Assert.Equal(6, sweeps.Count);
        // First 2 jobs have seed=30, next 2 have seed=20, last 2 have seed=10
        Assert.Equal(30L, sweeps[0].Select<long>("seed"));
        Assert.Equal(20L, sweeps[2].Select<long>("seed"));
        Assert.Equal(10L, sweeps[4].Select<long>("seed"));
    }

    // =========================================================================
    // 4. Ported from Hydra tests/defaults_list/test_defaults_list.py & test_group_package.py
    // =========================================================================

    [Fact]
    public void DefaultsList_OverrideKeywordAndGroupPackagePlaceholders_MatchHydra()
    {
        string dbDir = Path.Combine(_tempDir, "db");
        Directory.CreateDirectory(dbDir);

        File.WriteAllText(Path.Combine(dbDir, "postgres.yaml"), """
            driver: pgsql
            port: 5432
            """);

        File.WriteAllText(Path.Combine(dbDir, "mysql.yaml"), """
            driver: mysql
            port: 3306
            """);

        // Test 'override db: mysql' and '@_group_.primary'
        File.WriteAllText(Path.Combine(_tempDir, "config.yaml"), """
            defaults:
              - /db: postgres
              - override db: mysql
              - db@_group_.replica: postgres
              - _self_
            app: demo
            """);

        var node = OmegaConfBuilder.Create()
            .SetRootDirectory(_tempDir)
            .BuildNode();

        // 'db' was overridden in-place from postgres to mysql
        Assert.Equal("mysql", node.Select<string>("db.driver"));
        Assert.Equal(3306, node.Select<int>("db.port"));

        // 'db@_group_.replica' expands _group_ -> db, mounting under db.replica
        Assert.Equal("pgsql", node.Select<string>("db.replica.driver"));
        Assert.Equal(5432, node.Select<int>("db.replica.port"));
    }

    [Fact]
    public void DefaultsList_NestedGroupDefaultsAndInvalidKeywords_MatchHydra()
    {
        string cloudDir = Path.Combine(_tempDir, "cloud");
        string dbDir = Path.Combine(_tempDir, "db");
        Directory.CreateDirectory(cloudDir);
        Directory.CreateDirectory(dbDir);

        // Nested defaults list inside a config group file!
        File.WriteAllText(Path.Combine(cloudDir, "aws.yaml"), """
            # @package _global_
            defaults:
              - db: postgres
              - _self_
            region: us-east-1
            """);

        File.WriteAllText(Path.Combine(dbDir, "postgres.yaml"), """
            host: rds.amazonaws.com
            port: 5432
            """);

        File.WriteAllText(Path.Combine(_tempDir, "config.yaml"), """
            defaults:
              - cloud: aws
              - _self_
            """);

        var node = OmegaConfBuilder.Create()
            .SetRootDirectory(_tempDir)
            .BuildNode();

        Assert.Equal("us-east-1", node.Select<string>("region"));
        Assert.Equal("rds.amazonaws.com", node.Select<string>("db.host"));

        // Conflicting 'optional override' in defaults list must throw ConfigException
        File.WriteAllText(Path.Combine(_tempDir, "bad_config.yaml"), """
            defaults:
              - optional override db: mysql
            """);

        Assert.Throws<ConfigSyntaxException>(() =>
            OmegaConfBuilder.Create()
                .SetRootDirectory(_tempDir)
                .SetEntryFile("bad_config.yaml")
                .BuildNode());
    }

    // =========================================================================
    // 5. Ported from Hydra tests/test_compose.py (Multi-Directory Search Paths)
    // =========================================================================

    [Fact]
    public void SearchPaths_MultiDirectoryComposition_FirstMatchWinsAndSharesGroups()
    {
        string primaryDir = Path.Combine(_tempDir, "primary");
        string secondaryDir = Path.Combine(_tempDir, "secondary_plugin");
        Directory.CreateDirectory(Path.Combine(primaryDir, "db"));
        Directory.CreateDirectory(Path.Combine(secondaryDir, "db"));
        Directory.CreateDirectory(Path.Combine(secondaryDir, "cache"));

        // Entry config in primaryDir references 'db: postgres' (in primary), 'db@analytics: clickhouse' (in secondary), and 'cache: redis' (in secondary)
        File.WriteAllText(Path.Combine(primaryDir, "config.yaml"), """
            defaults:
              - db: postgres
              - db@analytics: clickhouse
              - cache: redis
              - _self_
            """);

        // Both primary and secondary have db/postgres.yaml — primary must win!
        File.WriteAllText(Path.Combine(primaryDir, "db", "postgres.yaml"), "host: primary-pg.internal");
        File.WriteAllText(Path.Combine(secondaryDir, "db", "postgres.yaml"), "host: secondary-pg.internal");

        // Only secondary has db/clickhouse.yaml and cache/redis.yaml
        File.WriteAllText(Path.Combine(secondaryDir, "db", "clickhouse.yaml"), "host: ch.analytics.internal");
        File.WriteAllText(Path.Combine(secondaryDir, "cache", "redis.yaml"), "endpoint: redis:6379");

        var node = OmegaConfBuilder.Create()
            .SetRootDirectory(primaryDir)
            .AddSearchPath(secondaryDir)
            .BuildNode();

        Assert.Equal("primary-pg.internal", node.Select<string>("db.host"));
        Assert.Equal("ch.analytics.internal", node.Select<string>("analytics.host"));
        Assert.Equal("redis:6379", node.Select<string>("cache.endpoint"));
    }

    // =========================================================================
    // 6. Ported from Hydra tests/instantiate/test_instantiate.py
    // =========================================================================

    public class OptimizerStub
    {
        public string Name { get; set; } = "";
        public double Lr { get; set; }
        public OptimizerStub() { }
        public OptimizerStub(string name, double lr)
        {
            Name = name;
            Lr = lr;
        }
    }

    public class TrainerStub
    {
        public string ModelName { get; set; } = "";
        public object? Optimizer { get; set; }
        public List<object> Callbacks { get; set; } = [];
    }

    [Fact]
    public void Instantiate_RecursiveUntypedTargetAndCallSiteOverrides_MatchHydra()
    {
        string trainerType = typeof(TrainerStub).AssemblyQualifiedName!;
        string optType = typeof(OptimizerStub).AssemblyQualifiedName!;

        var cfg = OmegaConfig.ParseYaml($$"""
            _target_: "{{trainerType}}"
            model_name: resnet50
            optimizer:
              _target_: "{{optType}}"
              _args_: ["AdamW"]
              lr: 0.001
            callbacks:
              - _target_: "{{optType}}"
                name: WarmupScheduler
                lr: 0.0001
            """);

        // 1. Recursive instantiation into 'object'-typed properties and List<object> + call-site override via anonymous object
        var trainer = OmegaConfig.Instantiate<TrainerStub>(cfg, new { model_name = "vit_large" });

        Assert.Equal("vit_large", trainer.ModelName);
        var opt = Assert.IsType<OptimizerStub>(trainer.Optimizer);
        Assert.Equal("AdamW", opt.Name);
        Assert.Equal(0.001, opt.Lr);

        Assert.Single(trainer.Callbacks);
        var cb = Assert.IsType<OptimizerStub>(trainer.Callbacks[0]);
        Assert.Equal("WarmupScheduler", cb.Name);

        // 2. Non-recursive instantiation (_recursive_: false) keeps nested _target_ as a dictionary instead of instantiating OptimizerStub
        var nonRecCfg = OmegaConfig.ParseYaml($$"""
            _target_: "{{trainerType}}"
            _recursive_: false
            model_name: bert
            optimizer:
              _target_: "{{optType}}"
              name: SGD
              lr: 0.05
            """);

        var nonRecTrainer = OmegaConfig.Instantiate<TrainerStub>(nonRecCfg);
        Assert.Equal("bert", nonRecTrainer.ModelName);
        var rawDict = Assert.IsType<Dictionary<string, object>>(nonRecTrainer.Optimizer);
        Assert.Equal("SGD", rawDict["name"]);

        // 3. Nested call-site override merges into nested _target_ config without losing _target_ or _args_
        var nestedOverrideTrainer = OmegaConfig.Instantiate<TrainerStub>(
            cfg,
            new { optimizer = new { lr = 0.09 } });
        var mergedOpt = Assert.IsType<OptimizerStub>(nestedOverrideTrainer.Optimizer);
        Assert.Equal("AdamW", mergedOpt.Name);
        Assert.Equal(0.09, mergedOpt.Lr);
    }

    // =========================================================================
    // 7. Deep Python-vs-.NET Parity Sweep: Edge Cases from defaults_list.py & grammar_functions.py
    // =========================================================================

    [Fact]
    public void DefaultsList_NullGroup_MissingGroup_MultiListGroup_AndConditionalGroupDelete_MatchHydra()
    {
        string dbDir = Path.Combine(_tempDir, "db");
        string cbDir = Path.Combine(_tempDir, "callbacks");
        Directory.CreateDirectory(dbDir);
        Directory.CreateDirectory(cbDir);

        File.WriteAllText(Path.Combine(dbDir, "postgres.yaml"), "host: pg.local\nport: 5432");
        File.WriteAllText(Path.Combine(dbDir, "mysql.yaml"), "host: mysql.local\nport: 3306");
        File.WriteAllText(Path.Combine(cbDir, "checkpoint.yaml"), "save_top_k: 3");
        File.WriteAllText(Path.Combine(cbDir, "early_stop.yaml"), "patience: 5");

        // 1. Multi-config list in defaults (- callbacks: [checkpoint, early_stop]) + disabled group (- cache: null)
        File.WriteAllText(Path.Combine(_tempDir, "config.yaml"), """
            defaults:
              - db: postgres
              - callbacks: [checkpoint, early_stop]
              - cache: null
              - _self_
            """);

        var node = OmegaConfBuilder.Create()
            .SetRootDirectory(_tempDir)
            .BuildNode();

        Assert.Equal("pg.local", node.Select<string>("db.host"));
        Assert.Equal(3, node.Select<int>("callbacks.save_top_k"));
        Assert.Equal(5, node.Select<int>("callbacks.patience"));
        Assert.Null(node.GetChild("cache"));

        // 2. Conditional group deletion (~db=postgres succeeds; ~db=mysql fails because active choice is postgres)
        var droppedNode = OmegaConfBuilder.Create()
            .SetRootDirectory(_tempDir)
            .AddCliArgs("~db=postgres")
            .BuildNode();
        Assert.Null(droppedNode.GetChild("db"));

        Assert.Throws<ConfigSyntaxException>(() =>
            OmegaConfBuilder.Create()
                .SetRootDirectory(_tempDir)
                .AddCliArgs("~db=mysql")
                .BuildNode());

        // 3. Disabling a group via CLI (db=null) skips loading db
        var cliNullNode = OmegaConfBuilder.Create()
            .SetRootDirectory(_tempDir)
            .AddCliArgs("db=null")
            .BuildNode();
        Assert.Null(cliNullNode.GetChild("db"));

        // 4. Mandatory missing group (- db: ???) throws MissingValueMarkerException unless overridden on CLI
        File.WriteAllText(Path.Combine(_tempDir, "missing_group.yaml"), """
            defaults:
              - db: ???
            """);

        Assert.Throws<MissingValueMarkerException>(() =>
            OmegaConfBuilder.Create()
                .SetRootDirectory(_tempDir)
                .SetEntryFile("missing_group.yaml")
                .BuildNode());

        var resolvedMissingGroup = OmegaConfBuilder.Create()
            .SetRootDirectory(_tempDir)
            .SetEntryFile("missing_group.yaml")
            .AddCliArgs("db=mysql")
            .BuildNode();
        Assert.Equal("mysql.local", resolvedMissingGroup.Select<string>("db.host"));
    }

    [Fact]
    public void DefaultsList_DuplicateSelf_ConflictingChoicesWithoutOverride_AndForceAddGroup_Throw()
    {
        string dbDir = Path.Combine(_tempDir, "db");
        Directory.CreateDirectory(dbDir);
        File.WriteAllText(Path.Combine(dbDir, "postgres.yaml"), "port: 5432");
        File.WriteAllText(Path.Combine(dbDir, "mysql.yaml"), "port: 3306");

        // 1. Duplicate _self_ in defaults list throws ConfigSyntaxException
        File.WriteAllText(Path.Combine(_tempDir, "dup_self.yaml"), """
            defaults:
              - _self_
              - db: postgres
              - _self_
            """);

        Assert.Throws<ConfigSyntaxException>(() =>
            OmegaConfBuilder.Create()
                .SetRootDirectory(_tempDir)
                .SetEntryFile("dup_self.yaml")
                .BuildNode());

        // 2. Duplicate group choices without 'override' keyword throws ConfigSyntaxException
        File.WriteAllText(Path.Combine(_tempDir, "conflict_group.yaml"), """
            defaults:
              - db: postgres
              - db: mysql
            """);

        Assert.Throws<ConfigSyntaxException>(() =>
            OmegaConfBuilder.Create()
                .SetRootDirectory(_tempDir)
                .SetEntryFile("conflict_group.yaml")
                .BuildNode());

        // 3. Force-add (++db=mysql) on a config group throws ConfigSyntaxException
        File.WriteAllText(Path.Combine(_tempDir, "valid.yaml"), "app: test");
        Assert.Throws<ConfigSyntaxException>(() =>
            OmegaConfBuilder.Create()
                .SetRootDirectory(_tempDir)
                .SetEntryFile("valid.yaml")
                .AddCliArgs("++db=mysql")
                .BuildNode());
    }

    [Fact]
    public void GrammarFunctions_GlobExclude_SingleArgRange_TagSweep_AndRecursiveCollectionCasts_MatchHydra()
    {
        string dbDir = Path.Combine(_tempDir, "db");
        Directory.CreateDirectory(dbDir);
        File.WriteAllText(Path.Combine(dbDir, "postgres.yaml"), "driver: pg");
        File.WriteAllText(Path.Combine(dbDir, "mysql.yaml"), "driver: mysql");
        File.WriteAllText(Path.Combine(dbDir, "sqlite.yaml"), "driver: sqlite");

        File.WriteAllText(Path.Combine(_tempDir, "config.yaml"), """
            defaults:
              - db: postgres
              - _self_
            idx: 0
            lr: 0.1
            items: []
            flag_str: ""
            """);

        // 1. glob(*, exclude=sqlite), single-arg range(2), and tag(exp1, sort(choice(0.2, 0.1)))
        var sweeps = OmegaConfBuilder.Create()
            .SetRootDirectory(_tempDir)
            .AddCliArgs(
                "-m",
                "db=glob(*, exclude=sqlite)",
                "idx=range(2)",
                "lr=tag(fast_sweep, sort(choice(0.2, 0.1)))")
            .BuildSweepNodes();

        // 2 dbs (mysql, postgres) * 2 idx (0, 1) * 2 lr (0.1, 0.2) = 8 combinations
        Assert.Equal(8, sweeps.Count);
        Assert.All(sweeps, s => Assert.NotEqual("sqlite", s.Select<string>("db.driver")));
        Assert.Equal(0L, sweeps[0].Select<long>("idx"));
        Assert.Equal(0.1, sweeps[0].Select<double>("lr"));
        Assert.Equal(0.2, sweeps[1].Select<double>("lr"));

        // 2. Recursive int([...]) on list, lowercase str(true), and strict bool(invalid)
        var castedNode = OmegaConfBuilder.Create()
            .SetRootDirectory(_tempDir)
            .AddCliArgs(
                "items=int([1.0, '2', true])",
                "flag_str=str(true)")
            .BuildNode();

        Assert.Equal([1L, 2L, 1L], ConfigBinder.Bind<long[]>(castedNode.GetChild("items")!));
        Assert.Equal("true", castedNode.Select<string>("flag_str"));

        Assert.Throws<ConfigSyntaxException>(() =>
            OmegaConfBuilder.Create()
                .SetRootDirectory(_tempDir)
                .AddCliArgs("flag_str=bool(not_a_boolean)")
                .BuildNode());
    }

    // =========================================================================
    // 8. Ported from Hydra tests/defaults_list/test_group_package.py, test_slash_normalization.py & tests/instantiate/test_positional.py
    // =========================================================================

    [Fact]
    public void GroupPackage_CliDeleteAndOverrideWithSymbolicGroupKeyword_AndRootBareSingleGroupSuffix_MatchHydra()
    {
        string prepDir = Path.Combine(_tempDir, "preprocess");
        string deepDir = Path.Combine(_tempDir, "cloud", "aws");
        Directory.CreateDirectory(prepDir);
        Directory.CreateDirectory(deepDir);

        File.WriteAllText(Path.Combine(prepDir, "v1.yaml"), "step: normalize");
        File.WriteAllText(Path.Combine(prepDir, "v2.yaml"), "step: tokenize");
        File.WriteAllText(Path.Combine(deepDir, "rds.yaml"), "engine: aurora");
        File.WriteAllText(Path.Combine(_tempDir, "item.yaml"), "x: 42");

        File.WriteAllText(Path.Combine(_tempDir, "config.yaml"), """
            defaults:
              - /preprocess@_group_: v1
              - /cloud/aws@_group_.primary: rds
              - /item@_group_.suffix
              - _self_
            """);

        // 1. Default composition:
        //    - /preprocess@_group_: v1 -> mounts under preprocess
        //    - /cloud/aws@_group_.primary: rds -> expands 'cloud/aws' to 'cloud.aws.primary' (not literal key 'cloud/aws')
        //    - /item@_group_.suffix -> bare root config has empty group, so mounts under 'suffix'
        var baseNode = OmegaConfBuilder.Create()
            .SetRootDirectory(_tempDir)
            .BuildNode();

        Assert.Equal("normalize", baseNode.Select<string>("preprocess.step"));
        Assert.Equal("aurora", baseNode.Select<string>("cloud.aws.primary.engine"));
        Assert.Equal(42, baseNode.Select<int>("suffix.x"));

        // 2. CLI override using symbolic @_group_ (preprocess@_group_=v2 or plain preprocess=v2)
        var overriddenBySymbolic = OmegaConfBuilder.Create()
            .SetRootDirectory(_tempDir)
            .AddCliArgs("preprocess@_group_=v2")
            .BuildNode();
        Assert.Equal("tokenize", overriddenBySymbolic.Select<string>("preprocess.step"));

        var overriddenByGroup = OmegaConfBuilder.Create()
            .SetRootDirectory(_tempDir)
            .AddCliArgs("preprocess=v2")
            .BuildNode();
        Assert.Equal("tokenize", overriddenByGroup.Select<string>("preprocess.step"));

        // 3. CLI deletion using symbolic ~preprocess@_group_ (with StructMode enabled)
        var deletedBySymbolic = OmegaConfBuilder.Create()
            .SetRootDirectory(_tempDir)
            .WithStructMode()
            .AddCliArgs("~preprocess@_group_")
            .BuildNode();
        Assert.Null(deletedBySymbolic.GetChild("preprocess"));
    }

    [Fact]
    public void SlashNormalization_SubgroupChoiceWithRelativeDefaults_ResolvesInsideSubgroupDirectory()
    {
        // Modeled on Hydra tests/defaults_list/test_slash_normalization.py:
        // config.yaml selects "- group_a: sub_group/item"
        // and group_a/sub_group/item.yaml has a relative default "- sub_sub: leaf" (located at group_a/sub_group/sub_sub/leaf.yaml)
        string subGroupDir = Path.Combine(_tempDir, "group_a", "sub_group");
        string subSubDir = Path.Combine(subGroupDir, "sub_sub");
        Directory.CreateDirectory(subSubDir);

        File.WriteAllText(Path.Combine(subSubDir, "leaf.yaml"), "leaf_val: 99");
        File.WriteAllText(Path.Combine(subGroupDir, "item.yaml"), """
            defaults:
              - sub_sub: leaf
              - _self_
            item_val: 10
            """);
        File.WriteAllText(Path.Combine(_tempDir, "config.yaml"), """
            defaults:
              - group_a: sub_group/item
              - _self_
            """);

        var node = OmegaConfBuilder.Create()
            .SetRootDirectory(_tempDir)
            .BuildNode();

        Assert.Equal(10, node.Select<int>("group_a.item_val"));
        Assert.Equal(99, node.Select<int>("group_a.sub_sub.leaf_val"));
    }

    [Fact]
    public void Instantiate_PositionalArgsOverrideAndRelativeListElementInterpolation_MatchHydra()
    {
        // 1. Modeled on Hydra tests/instantiate/test_positional.py (direct_args+interpolation & positional override precedence)
        string optType = typeof(OptimizerStub).AssemblyQualifiedName!;
        var cfg = OmegaConfig.ParseYaml($$"""
            _target_: "{{optType}}"
            _args_:
              - SGD
              - 0.01
            name: ConfigKeywordName
            lr: "${._args_.1}"
            """);

        // Call-site _args_ override updates _args_[0] and _args_[1], and ${._args_.1} resolves against the overridden _args_!
        // Also verifies that positional _args_ takes precedence over conflicting config property 'name: ConfigKeywordName'.
        var inst = OmegaConfig.Instantiate<OptimizerStub>(cfg, new { _args_ = new object[] { "AdamW", 0.05 } });
        Assert.Equal("AdamW", inst.Name);
        Assert.Equal(0.05, inst.Lr);

        // 2. Relative dot navigation (${.0}, ${..sibling_tag}, and ${...parent_prefix}) from directly inside a list element (items[0])
        var listInterpCfg = OmegaConfig.ParseYaml("""
            parent_prefix: global_v1
            container:
              sibling_tag: local_tag
              items:
                - first_elem
                - "${.0}:${..sibling_tag}:${...parent_prefix}"
            """);
        var merged = OmegaConfig.Merge(listInterpCfg);
        Assert.Equal("first_elem:local_tag:global_v1", merged.Select<string>("container.items[1]"));
    }
}
