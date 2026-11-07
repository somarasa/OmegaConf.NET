// Ported from Hydra: tutorials/basic/running_your_hydra_app/3_working_directory (original_cwd)
// Lesson: distinguish the directory the app was launched from.
using OmegaConf.NET;
using OmegaConf.NET.Serialization;
using OmegaConf.NET.Composition;
using OmegaConf.NET.Ast;

string origCwd = Directory.GetCurrentDirectory();
var store = new ConfigStore();
store.Store(name: "config", node: new ConfigObjectNode());
OmegaConfBuilder.Create()
    .SetRootDirectory(AppContext.BaseDirectory)
    .SetEntryFile("config")
    .WithConfigStore(store)
    .AddCliArgs(args)
    .BuildNode();

string ToAbsolutePath(string path) =>
    Path.IsPathRooted(path) ? Path.GetFullPath(path) : Path.GetFullPath(Path.Combine(origCwd, path));

Console.WriteLine($"Current working directory : {Directory.GetCurrentDirectory()}");
Console.WriteLine($"Orig working directory    : {origCwd}");
Console.WriteLine($"to_absolute_path('foo')   : {ToAbsolutePath("foo")}");
Console.WriteLine($"to_absolute_path('/foo')  : {ToAbsolutePath("/foo")}");
return 0;
