// Ported from Hydra: instantiate/object
// Lesson: instantiate() builds objects from _target_ entries and supports method calls.
using OmegaConf.NET;
using OmegaConf.NET.Serialization;
using OmegaConf.NET.Composition;

namespace my_app;

public abstract class DBConnection
{
    public abstract void Connect();
}

public class MySQLConnection : DBConnection
{
    public string? Host { get; set; }
    public string? User { get; set; }
    public string? Password { get; set; }
    public override void Connect() => Console.WriteLine($"MySQL connecting to {Host}");
}

public static class Entry
{
    public static int Main(string[] args)
    {
        var node = OmegaConfBuilder.Create()
            .SetRootDirectory(Path.Combine(AppContext.BaseDirectory, "conf"))
            .AddCliArgs(args)
            .BuildNode();
        var connection = OmegaConfig.Instantiate<DBConnection>(node.SelectNode("db")!, allowedTargets: ["my_app.*"]);
        connection.Connect();
        return 0;
    }
}
