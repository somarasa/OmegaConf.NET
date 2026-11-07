// Ported from Hydra: tutorials/structured_configs/5.2_structured_config_schema_different_config_group
// Lesson: a structured schema in one group and its members in a separate group (database_lib).
using OmegaConf.NET;
using OmegaConf.NET.Serialization;
using OmegaConf.NET.Composition;
using OmegaConf.NET.Attributes;

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
    public new string Driver { get; set; } = "mysql";
    public new int Port { get; set; } = 3306;
    public string User { get; set; } = null!;
    public string Password { get; set; } = null!;
}

public class PostGreSQLConfig : DBConfig
{
    public new string Driver { get; set; } = "postgresql";
    public new int Port { get; set; } = 5432;
    public string User { get; set; } = null!;
    public string Password { get; set; } = null!;
    public int Timeout { get; set; } = 10;
}

public class Config
{
    public DBConfig Db { get; set; } = null!;
    public bool Debug { get; set; }
}

public static class Entry
{
    public static int Main(string[] args)
    {
        var cs = new ConfigStore();
        cs.Store(name: "base_config", node: typeof(Config));
        cs.Store(group: "database_lib/db", name: "mysql", node: typeof(MySQLConfig));
        cs.Store(group: "database_lib/db", name: "postgresql", node: typeof(PostGreSQLConfig));
        var node = OmegaConfBuilder.Create()
            .SetRootDirectory(Path.Combine(AppContext.BaseDirectory, "conf"))
            .WithConfigStore(cs)
            .AddCliArgs(args)
            .BuildNode();
        Console.WriteLine(node.ToYaml());
        return 0;
    }
}
