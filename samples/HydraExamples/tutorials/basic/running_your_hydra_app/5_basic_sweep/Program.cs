// Ported from Hydra: tutorials/basic/running_your_hydra_app/5_basic_sweep
// Lesson: register a sweep parameter and let Hydra enumerate the grid.
using OmegaConf.NET;
using OmegaConf.NET.Ast;
using OmegaConf.NET.MultiRun;
using OmegaConf.NET.Serialization;

var rootDir = Path.Combine(AppContext.BaseDirectory, "conf");

if (SweepEngine.IsMultiRun(args))
{
    var sweepNodes = OmegaConfBuilder.Create()
        .SetRootDirectory(rootDir)
        .AddCliArgs(args)
        .BuildSweepNodes();

    foreach (var sNode in sweepNodes)
    {
        var sDb = (ConfigObjectNode)sNode.SelectNode("db")!;
        Console.WriteLine($"driver={sDb.Select<string>("driver")}, timeout={sDb.Select<long>("timeout")}");
    }
    return 0;
}

var node = OmegaConfBuilder.Create()
    .SetRootDirectory(rootDir)
    .AddCliArgs(args)
    .BuildNode();
var db = (ConfigObjectNode)node.SelectNode("db")!;
Console.WriteLine($"driver={db.Select<string>("driver")}, timeout={db.Select<long>("timeout")}");
return 0;
