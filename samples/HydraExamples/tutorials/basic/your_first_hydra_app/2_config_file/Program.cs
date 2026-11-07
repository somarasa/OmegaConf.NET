// Ported from Hydra: tutorials/basic/your_first_hydra_app/2_config_file
// Lesson: load configuration from a YAML file located in the config path.
using OmegaConf.NET;
using OmegaConf.NET.Serialization;

    var node = OmegaConfBuilder.Create()
        .SetRootDirectory(AppContext.BaseDirectory)
        .AddCliArgs(args)
        .BuildNode();
    Console.WriteLine(node.ToYaml());
    return 0;
