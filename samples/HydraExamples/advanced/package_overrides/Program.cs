// Ported from Hydra: advanced/package_overrides/simple.py
// Lesson: @package directives relocate a group into a subkey during composition.
using OmegaConf.NET;
using OmegaConf.NET.Serialization;

    var node = OmegaConfBuilder.Create()
        .SetRootDirectory(Path.Combine(AppContext.BaseDirectory, "conf"))
        .SetEntryFile("simple")
        .AddCliArgs(args)
        .BuildNode();
    Console.WriteLine(node.ToYaml());
    return 0;
