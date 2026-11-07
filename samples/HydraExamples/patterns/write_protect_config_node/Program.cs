// Ported from Hydra: patterns/write_protect_config_node
// Lesson: struct mode protects the config schema against accidental writes.
using OmegaConf.NET;
using OmegaConf.NET.Ast;
using OmegaConf.NET.Composition;
using OmegaConf.NET.Common;
using OmegaConf.NET.Serialization;

public class SerialPort
{
    public int BaudRate { get; set; } = 19200;
    public int DataBits { get; set; } = 8;
    public int StopBits { get; set; } = 1;
}

public static class Entry
{
    public static int Main(string[] args)
    {
        var cs = new ConfigStore();
        cs.Store(name: "config", node: typeof(SerialPort));
        var node = OmegaConfBuilder.Create()
            .SetRootDirectory(AppContext.BaseDirectory)
            .WithConfigStore(cs)
            .SetEntryFile("config")
            .AddCliArgs(args)
            .BuildNode();

        if (args.Length == 0)
        {
            Console.WriteLine(node);
            return 0;
        }

        Console.WriteLine(node.ToYaml());

        try
        {
            var root = (ConfigObjectNode)node;
            root.SetStruct(true);
            root.SetChild("parity_bits", new ConfigValueNode(2));
            Console.WriteLine("Added parity_bits=2");
        }
        catch (ConfigStructException)
        {
            Console.WriteLine("Struct mode: the locked config node cannot be modified");
        }
        return 0;
    }
}
