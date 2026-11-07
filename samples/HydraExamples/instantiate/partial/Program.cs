// Ported from Hydra: instantiate/partial
// Lesson: _partial_ produces a factory (Func<T>) that is invoked at construction time.
using OmegaConf.NET;
using OmegaConf.NET.Serialization;
using OmegaConf.NET.Composition;

namespace my_app;

public class Optimizer
{
    public string? Algo { get; set; }
    public float Lr { get; set; }
    public Optimizer(string? algo = null, float lr = 0f)
    {
        Algo = algo;
        Lr = lr;
    }
    public override string ToString() => $"Optimizer(algo={Algo},lr={Lr})";
}

public class Model
{
    public Optimizer Optim { get; set; } = null!;
    public Model(Func<Optimizer> optimPartial)
    {
        Optim = optimPartial();
    }
    public override string ToString() => $"Model(Optimizer={Optim})";
}

public static class Entry
{
    public static int Main(string[] args)
    {
        var node = OmegaConfBuilder.Create()
            .SetRootDirectory(AppContext.BaseDirectory)
            .AddCliArgs(args)
            .BuildNode();
        var model = OmegaConfig.Instantiate<Model>(node.SelectNode("model")!, allowedTargets: ["my_app.*"]);
        Console.WriteLine(model);
        return 0;
    }
}
