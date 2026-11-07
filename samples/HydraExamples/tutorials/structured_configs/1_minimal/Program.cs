// Ported from Hydra: tutorials/structured_configs/1_minimal
// Lesson: the simplest structured config is a single dataclass registered in the ConfigStore.
using OmegaConf.NET;
using OmegaConf.NET.Serialization;
using OmegaConf.NET.Composition;

public class MySQLConfig
{
    public string Host { get; set; } = "localhost";
    public int Port { get; set; } = 3306;
}

public static class Entry
{
    public static int Main(string[] args)
    {
        var cs = new ConfigStore();
        cs.Store(name: "config", node: typeof(MySQLConfig));
        var cfg = OmegaConfBuilder.Create<MySQLConfig>()
            .SetRootDirectory(AppContext.BaseDirectory)
            .WithConfigStore(cs)
            .SetEntryFile("config")
            .Build();
        Console.WriteLine($"Host: {cfg.Host}, port: {cfg.Port}");
        return 0;
    }
}
