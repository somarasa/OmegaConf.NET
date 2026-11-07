// Ported from Hydra: tutorials/structured_configs/3_config_groups
// Lesson: a structured config selects a config group member via its defaults list.
using OmegaConf.NET;
using OmegaConf.NET.Serialization;
using OmegaConf.NET.Composition;

public class MySQLConfig
{
    public string Driver { get; set; } = "mysql";
    public string Host { get; set; } = "localhost";
    public int Port { get; set; } = 3306;
}

public class PostGreSQLConfig
{
    public string Driver { get; set; } = "postgresql";
    public string Host { get; set; } = "localhost";
    public int Port { get; set; } = 5432;
    public int Timeout { get; set; } = 10;
}

public class Config
{
    public object Db { get; set; } = null!;
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
        Console.WriteLine(node.ToYaml());
        return 0;
    }
}
