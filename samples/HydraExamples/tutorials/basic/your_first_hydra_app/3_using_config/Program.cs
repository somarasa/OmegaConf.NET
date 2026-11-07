// Ported from Hydra: tutorials/basic/your_first_hydra_app/3_using_config
// Lesson: read values from the composed config, including interpolations and missing markers.
using System.Diagnostics;
using OmegaConf.NET;
using OmegaConf.NET.Ast;
using OmegaConf.NET.Binding;
using OmegaConf.NET.Common;

var node = OmegaConfBuilder.Create()
    .SetRootDirectory(AppContext.BaseDirectory)
    .AddCliArgs(args)
    .BuildNode();

var n = (ConfigObjectNode)node.SelectNode("node")!;
if (n.Select<long>("loompa") != 10)
    throw new InvalidOperationException("Assertion failed: cfg.node.loompa == 10");
if (n.Select<long>("zippity") != 10)
    throw new InvalidOperationException("Assertion failed: cfg.node.zippity == 10");
if (n.Select<string>("do") != "oompa 10")
    throw new InvalidOperationException("Assertion failed: cfg.node.do == 'oompa 10'");

bool threwMissing = false;
try
{
    _ = ConfigBinder.Bind<long>(n.SelectNode("waldo")!);
}
catch (MissingValueMarkerException)
{
    threwMissing = true;
}

if (!threwMissing)
    throw new InvalidOperationException("Assertion failed: expected MissingValueMarkerException on cfg.node.waldo");

return 0;
