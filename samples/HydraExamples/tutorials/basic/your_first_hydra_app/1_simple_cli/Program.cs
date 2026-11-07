// Ported from Hydra: tutorials/basic/your_first_hydra_app/1_simple_cli
// Lesson: @hydra.main() parses CLI args and exposes an (empty) config.
using OmegaConf.NET;
using OmegaConf.NET.Serialization;
using OmegaConf.NET.Composition;
using OmegaConf.NET.Ast;

    var store = new ConfigStore();
    store.Store(name: "config", node: new ConfigObjectNode());
    var node = OmegaConfBuilder.Create()
        .SetRootDirectory(AppContext.BaseDirectory)
        .SetEntryFile("config")
        .WithConfigStore(store)
        .AddCliArgs(args)
        .BuildNode();
    Console.WriteLine(node.ToYaml());
    return 0;
