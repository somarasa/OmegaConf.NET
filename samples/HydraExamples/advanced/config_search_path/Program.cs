// Ported from Hydra: advanced/config_search_path
// Lesson: extend the search path so config groups resolve from additional locations.
using OmegaConf.NET;
using OmegaConf.NET.Serialization;

var appDir = AppContext.BaseDirectory;
var node = OmegaConfBuilder.Create()
    .SetRootDirectory(Path.Combine(appDir, "conf"))
    .AddSearchPath(Path.Combine(appDir, "additional_conf"))
    .AddCliArgs(args)
    .BuildNode();
Console.WriteLine(node.ToYaml());
return 0;
