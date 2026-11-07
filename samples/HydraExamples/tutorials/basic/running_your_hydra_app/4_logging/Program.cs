// Ported from Hydra: tutorials/basic/running_your_hydra_app/4_logging
// Lesson: structured logging inside an app.
using OmegaConf.NET;
using OmegaConf.NET.Composition;
using OmegaConf.NET.Ast;
using OmegaConf.NET.Serialization;

var store = new ConfigStore();
store.Store(name: "config", node: new ConfigObjectNode());
OmegaConfBuilder.Create()
    .SetRootDirectory(AppContext.BaseDirectory)
    .SetEntryFile("config")
    .WithConfigStore(store)
    .AddCliArgs(args)
    .BuildNode();

Console.WriteLine($"[{DateTime.Now:yyyy-MM-dd HH:mm:ss,fff}][__main__][INFO] - Info level message");
return 0;
