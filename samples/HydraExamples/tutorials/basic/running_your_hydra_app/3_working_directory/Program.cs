// Ported from Hydra: tutorials/basic/running_your_hydra_app/3_working_directory (my_app)
// Lesson: observe the current working directory and the Hydra output directory.
using OmegaConf.NET;
using OmegaConf.NET.Serialization;
using OmegaConf.NET.Composition;
using OmegaConf.NET.Ast;

    var store = new ConfigStore();
    store.Store(name: "config", node: new ConfigObjectNode());
    OmegaConfBuilder.Create()
        .SetRootDirectory(AppContext.BaseDirectory)
        .SetEntryFile("config")
        .WithConfigStore(store)
        .WithOutputDirectory(Path.Combine(AppContext.BaseDirectory, "outputs"))
        .AddCliArgs(args)
        .BuildNode();

    Console.WriteLine($"Working directory : {Directory.GetCurrentDirectory()}");
    Console.WriteLine($"Output directory  : {Path.Combine(AppContext.BaseDirectory, "outputs")}");
    return 0;
