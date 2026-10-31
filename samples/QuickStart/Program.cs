using OmegaConf.NET;
using OmegaConf.NET.Attributes;
using OmegaConf.NET.Diff;
using OmegaConf.NET.Schema;
using OmegaConf.NET.Serialization;

Console.WriteLine("==================================================");
Console.WriteLine("        OmegaConf.NET QuickStart Sample           ");
Console.WriteLine("==================================================");

string configsDir = Path.Combine(AppContext.BaseDirectory, "configs");

// 1. Build staging configuration (default)
Console.WriteLine("\n[1] Building Staging Configuration (Auto-Cascading)...");
var stagingNode = OmegaConfBuilder.Create()
    .SetRoot(configsDir)
    .AddCliArgs(args)
    .BuildNode();

Console.WriteLine("Composed Staging Config:\n" + stagingNode.ToYaml());

// 2. Build production configuration
Console.WriteLine("\n[2] Building Production Configuration (With *= and += Operators)...");
var prodNode = OmegaConfBuilder.Create()
    .SetRoot(configsDir)
    .SelectPath("db", "production/postgres")
    .BuildNode();

Console.WriteLine("Composed Production Config:\n" + prodNode.ToYaml());

// 3. Compare Staging vs Production using ConfigDiffer
Console.WriteLine("\n[3] Difference between Staging and Production:");
var diff = ConfigDiffer.Compare(stagingNode, prodNode);
Console.WriteLine(diff.ToString());

// 4. Strongly Typed POCO Binding
Console.WriteLine("\n[4] Binding to Strongly Typed C# Class:");
var config = OmegaConfBuilder.Create<ServiceConfig>()
    .SetRoot(configsDir)
    .AddCliArgs(args)
    .Build();

Console.WriteLine($"Service Name: {config.AppName}");
Console.WriteLine($"DB Host: {config.Db?.Host}, Port: {config.Db?.Port}, Max Connections: {config.Db?.MaxConnections}");

// 5. Generate JSON Schema for VS Code / Rider Auto-Complete
Console.WriteLine("\n[5] Generating JSON Schema for IDE Auto-Complete...");
string schemaPath = Path.Combine(configsDir, "config.schema.json");
JsonSchemaGenerator.GenerateSchemaFile<ServiceConfig>(schemaPath);
Console.WriteLine($"Schema written to: {schemaPath}");

Console.WriteLine("\nSample executed successfully!");

public class DatabaseOptions
{
    public string Driver { get; set; } = "";
    public string Host { get; set; } = "";
    public int Port { get; set; }
    public int MaxConnections { get; set; }
    public bool Ssl { get; set; }
    public int TimeoutSeconds { get; set; }
}

public class ServiceConfig
{
    [ConfigRequired]
    public string AppName { get; set; } = "";

    public DatabaseOptions? Db { get; set; }

    [ConfigKey("active_workers")]
    public int Workers { get; set; } = 4;

    public bool DebugMode { get; set; }
}
