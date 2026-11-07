// Ported from Hydra: instantiate/schema
// Lesson: structured configs can carry a _target_ so instantiate() builds a typed object.
using OmegaConf.NET;
using OmegaConf.NET.Serialization;
using OmegaConf.NET.Composition;
using OmegaConf.NET.Attributes;

namespace my_app;

public abstract class DBConnection
{
    public string? Driver { get; set; }
    public string? Host { get; set; }
    public int Port { get; set; }
    public abstract void Connect();
}

public class MySQLConnection : DBConnection
{
    public override void Connect() => Console.WriteLine($"{Driver} connecting to {Host}");
}

public class PostgreSQLConnection : DBConnection
{
    public int Timeout { get; set; }
    public override void Connect() => Console.WriteLine($"{Driver} connecting to {Host}");
}

public class DBConfig
{
    [ConfigRequired]
    public string Driver { get; set; } = null!;
    public string Host { get; set; } = "localhost";
    [ConfigRequired]
    public int Port { get; set; }
}

public class MySQLConfig : DBConfig
{
    [ConfigKey("_target_")]
    public string Target { get; set; } = "my_app.MySQLConnection";
    public new string Driver { get; set; } = "MySQL";
    public new int Port { get; set; } = 1234;
}

public class PostGreSQLConfig : DBConfig
{
    [ConfigKey("_target_")]
    public string Target { get; set; } = "my_app.PostgreSQLConnection";
    public new string Driver { get; set; } = "PostgreSQL";
    public new int Port { get; set; } = 5678;
    public int Timeout { get; set; } = 10;
}

public class Config
{
    public List<object> Defaults { get; set; } = new()
    {
        "_self_",
        new Dictionary<string, object>(System.StringComparer.Ordinal) { ["db"] = "mysql" },
    };
    public DBConfig Db { get; set; } = null!;
}

public static class Entry
{
    public static int Main(string[] args)
    {
        var cs = new ConfigStore();
        cs.Store(name: "config", node: typeof(Config));
        cs.Store(group: "db", name: "mysql", node: typeof(MySQLConfig));
        cs.Store(group: "db", name: "postgresql", node: typeof(PostGreSQLConfig));
        var node = OmegaConfBuilder.Create()
            .SetRootDirectory(AppContext.BaseDirectory)
            .WithConfigStore(cs)
            .SetEntryFile("config")
            .AddCliArgs(args)
            .BuildNode();
        var connection = OmegaConfig.Instantiate<DBConnection>(node.SelectNode("db")!, allowedTargets: ["my_app.*"]);
        connection.Connect();
        return 0;
    }
}
