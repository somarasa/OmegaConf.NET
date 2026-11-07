// Ported from Hydra: advanced/hydra_app_example
// Lesson: a realistic app reads values from the config and uses them in logic.
using OmegaConf.NET;
using OmegaConf.NET.Ast;

var node = OmegaConfBuilder.Create()
    .SetRootDirectory(Path.Combine(AppContext.BaseDirectory, "conf"))
    .AddCliArgs(args)
    .BuildNode();

var app = (ConfigObjectNode)node.SelectNode("app")!;
long num1 = app.Select<long>("num1")!;
long num2 = app.Select<long>("num2")!;
string user = app.Select<string>("user")!;
long sum = num1 + num2;
Console.WriteLine($"Hello {user}, {num1} + {num2} = {sum}");
return 0;
