// Ported from Hydra: instantiate/schema_recursive
// Lesson: instantiate() builds recursively-nested objects from a structured config.
using OmegaConf.NET;
using OmegaConf.NET.Serialization;
using OmegaConf.NET.Composition;
using OmegaConf.NET.Attributes;

namespace my_app;

public class Tree
{
    public int Value { get; set; }
    public Tree? Left { get; set; }
    public Tree? Right { get; set; }
}

public class TreeConf
{
    [ConfigKey("_target_")]
    public string Target { get; set; } = "my_app.Tree";
    [ConfigRequired]
    public int Value { get; set; }
    public TreeConf? Left { get; set; }
    public TreeConf? Right { get; set; }
}

public class Config
{
    public TreeConf Tree { get; set; } = null!;
}

public static class Entry
{
    public static int Main(string[] args)
    {
        var cs = new ConfigStore();
        cs.Store(name: "config_schema", node: typeof(Config));
        var node = OmegaConfBuilder.Create()
            .SetRootDirectory(AppContext.BaseDirectory)
            .WithConfigStore(cs)
            .AddCliArgs(args)
            .BuildNode();

        var tree = OmegaConfig.Instantiate<Tree>(node.SelectNode("tree")!, allowedTargets: ["my_app.*"]);
        PrettyPrint(tree);
        return 0;
    }

    private static void PrettyPrint(Tree tree, string name = "root", int depth = 0)
    {
        string pad = new(' ', depth * 2);
        Console.WriteLine($"{pad}{name}({tree.Value})");
        if (tree.Left is not null)
            PrettyPrint(tree.Left, "left", depth + 1);
        if (tree.Right is not null)
            PrettyPrint(tree.Right, "right", depth + 1);
    }
}
