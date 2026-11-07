// Ported from Hydra: patterns/extending_configs
// Lesson: use += to append to a list key defined by a group.
using OmegaConf.NET;
using OmegaConf.NET.Serialization;

    var node = OmegaConfBuilder.Create()
        .SetRootDirectory(Path.Combine(AppContext.BaseDirectory, "conf"))
        .AddCliArgs(args)
        .BuildNode();
    Console.WriteLine(node.ToYaml());
    return 0;
