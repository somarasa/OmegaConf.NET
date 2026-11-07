// Ported from Hydra: patterns/configuring_experiments
// Lesson: define reusable server configs as a config group for experiments.
using OmegaConf.NET;
using OmegaConf.NET.Serialization;

    var node = OmegaConfBuilder.Create()
        .SetRootDirectory(Path.Combine(AppContext.BaseDirectory, "conf"))
        .AddCliArgs(args)
        .BuildNode();
    Console.WriteLine(node.ToYaml());
    return 0;
