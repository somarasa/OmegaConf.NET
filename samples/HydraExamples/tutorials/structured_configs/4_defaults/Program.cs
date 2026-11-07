// Ported from Hydra: tutorials/structured_configs/4_defaults
// Lesson: defaults lists map config group names to concrete choices (here: mysql).
using OmegaConf.NET;
using OmegaConf.NET.Serialization;
using OmegaConf.NET.Composition;

public class MySQLConfig
{
    public string Driver { get; set; } = "mysql";
    public string Host { get; set; } = "localhost";
    public int Port { get; set; } = 3306;
    public string User { get; set; } = "omry";
    public string Password { get; set; } = "secret";
}

public class PostGreSQLConfig
{
    public string Driver { get; set; } = "postgresql";
    public string Host { get; set; } = "localhost";
    public int Port { get; set; } = 5432;
    public int Timeout { get; set; } = 10;
    public string User { get; set; } = "postgres_user";
    public string Password { get; set; } = "drowssap";
}

public class Config
{
    public List<object> Defaults { get; set; } = new()
    {
        new Dictionary<string, object>(System.StringComparer.Ordinal) { ["db"] = "mysql" },
    };
    public object Db { get; set; } = null!;
}

public static class Entry
{
    public static int Main(string[] args)
    {
        var cs = new ConfigStore();
        cs.Store(group: "db", name: "mysql", node: typeof(MySQLConfig));
        cs.Store(group: "db", name: "postgresql", node: typeof(PostGreSQLConfig));
        cs.Store(name: "config", node: typeof(Config));
        var node = OmegaConfBuilder.Create()
            .SetRootDirectory(AppContext.BaseDirectory)
            .WithConfigStore(cs)
            .SetEntryFile("config")
            .AddCliArgs(args)
            .BuildNode();
        Console.WriteLine(node.ToYaml());
        return 0;
    }
}
