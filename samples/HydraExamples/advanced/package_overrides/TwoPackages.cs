// Ported from Hydra: advanced/package_overrides/two_packages.py
// Lesson: combine two independent package-override groups into one config.
using OmegaConf.NET;
using OmegaConf.NET.Serialization;

var node = OmegaConfBuilder.Create()
    .SetRootDirectory(Path.Combine(AppContext.BaseDirectory, "conf"))
    .SetEntryFile("two_packages")
    .AddCliArgs(args)
    .BuildNode();
Console.WriteLine(node.ToYaml());
return 0;
