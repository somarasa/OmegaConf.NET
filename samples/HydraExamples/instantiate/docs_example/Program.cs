// Ported from Hydra: instantiate/docs_example
// Lesson: recursive instantiation, call-site overrides, and _recursive_=False.
using OmegaConf.NET;
using OmegaConf.NET.Ast;
using OmegaConf.NET.Serialization;
using OmegaConf.NET.Composition;

namespace my_app;

public class Optimizer
{
    public string? Algo { get; set; }
    public float Lr { get; set; }
    public override string ToString() => $"Optimizer(algo={Algo},lr={Lr})";
}

public class Dataset
{
    public string? Name { get; set; }
    public string? Path { get; set; }
    public override string ToString() => $"Dataset(name={Name}, path={Path})";
}

public class Trainer
{
    public object Optimizer { get; set; } = null!;
    public object Dataset { get; set; } = null!;
    public Trainer(object optimizer, object dataset)
    {
        Optimizer = optimizer;
        Dataset = dataset;
    }
    public override string ToString() => "Trainer(\n  optimizer=" + Optimizer + ",\n  dataset=" + Dataset + "\n)";
}

public static class Entry
{
    public static int Main(string[] args)
    {
        var node = OmegaConfBuilder.Create()
            .SetRootDirectory(AppContext.BaseDirectory)
            .AddCliArgs(args)
            .BuildNode();

        var optimizer = OmegaConfig.Instantiate<Optimizer>(node.SelectNode("trainer.optimizer")!, allowedTargets: ["my_app.*"]);
        Console.WriteLine(optimizer);

        var overridden = OmegaConfig.Instantiate<Optimizer>(node.SelectNode("trainer.optimizer")!,
            new Dictionary<string, object?> { ["lr"] = 0.2 }, allowedTargets: ["my_app.*"]);
        Console.WriteLine(overridden);

        var trainer = OmegaConfig.Instantiate<Trainer>(node.SelectNode("trainer")!, allowedTargets: ["my_app.*"]);
        Console.WriteLine(trainer);

        var overriddenTrainer = OmegaConfig.Instantiate<Trainer>(node.SelectNode("trainer")!,
            new Dictionary<string, object?>
            {
                ["optimizer"] = new Dictionary<string, object?> { ["lr"] = 0.3 },
                ["dataset"] = new Dictionary<string, object?> { ["name"] = "cifar10", ["path"] = "/datasets/cifar10" }
            },
            allowedTargets: ["my_app.*"]);
        Console.WriteLine(overriddenTrainer);

        var nonRecursiveNode = (ConfigObjectNode)node.SelectNode("trainer")!;
        var nonRecursive = new Trainer(nonRecursiveNode.GetChild("optimizer")!, nonRecursiveNode.GetChild("dataset")!);
        Console.WriteLine(nonRecursive);
        return 0;
    }
}
