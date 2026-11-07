// Ported from Hydra: tutorials/basic/your_first_hydra_app/5_defaults
// Lesson: the defaults list controls the order config groups are composed in.
using OmegaConf.NET;
using OmegaConf.NET.Serialization;

    var node = OmegaConfBuilder.Create()
        .SetRootDirectory(Path.Combine(AppContext.BaseDirectory, "conf"))
        .AddCliArgs(args)
        .BuildNode();
    Console.WriteLine(node.ToYaml());
    return 0;
