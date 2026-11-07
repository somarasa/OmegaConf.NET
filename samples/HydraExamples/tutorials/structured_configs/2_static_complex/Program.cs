// Ported from Hydra: tutorials/structured_configs/2_static_complex
// Lesson: nested structured configs compose into a hierarchical config.
using OmegaConf.NET;
using OmegaConf.NET.Serialization;
using OmegaConf.NET.Composition;

public class MySQLConfig
{
    public string Host { get; set; } = "localhost";
    public int Port { get; set; } = 3306;
}

public class UserInterface
{
    public string Title { get; set; } = "My app";
    public int Width { get; set; } = 1024;
    public int Height { get; set; } = 768;
}

public class MyConfig
{
    public MySQLConfig Db { get; set; } = new();
    public UserInterface Ui { get; set; } = new();
}

public static class Entry
{
    public static int Main(string[] args)
    {
        var cs = new ConfigStore();
        cs.Store(name: "config", node: typeof(MyConfig));
        var cfg = OmegaConfBuilder.Create<MyConfig>()
            .SetRootDirectory(AppContext.BaseDirectory)
            .WithConfigStore(cs)
            .SetEntryFile("config")
            .Build();
        Console.WriteLine($"Title={cfg.Ui.Title}, size={cfg.Ui.Width}x{cfg.Ui.Height} pixels");
        return 0;
    }
}
