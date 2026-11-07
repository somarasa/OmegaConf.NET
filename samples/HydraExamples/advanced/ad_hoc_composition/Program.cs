// Ported from Hydra: advanced/ad_hoc_composition
// Lesson: compose a config without the standard @hydra.main entry point.
using OmegaConf.NET;
using OmegaConf.NET.Serialization;

var node = OmegaConfBuilder.Create()
    .SetRootDirectory(Path.Combine(AppContext.BaseDirectory, "conf"))
    .AddCliArgs("db=mysql", $"db.user={Environment.GetEnvironmentVariable("USER") ?? "me"}")
    .BuildNode();

Console.WriteLine(node.ToYaml());
return 0;
