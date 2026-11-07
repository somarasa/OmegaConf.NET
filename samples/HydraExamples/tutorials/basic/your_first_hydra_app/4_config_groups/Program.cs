// Ported from Hydra: tutorials/basic/your_first_hydra_app/4_config_groups
// Lesson: config groups let you select which member of a group is composed.
using OmegaConf.NET;
using OmegaConf.NET.Serialization;

    var node = OmegaConfBuilder.Create()
        .SetRootDirectory(Path.Combine(AppContext.BaseDirectory, "conf"))
        .AddCliArgs(args)
        .BuildNode();
    Console.WriteLine(node.ToYaml());
    return 0;
