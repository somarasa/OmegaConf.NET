---
name: omegaconf-net
description: Authoritative guide for AI coding assistants to design, generate, refactor, and integrate hierarchical configurations in .NET using OmegaConf.NET.
---

# OmegaConf.NET Skill Guide for AI Assistants

Use this skill when you need to create, modify, or integrate configuration trees in C# / .NET using **OmegaConf.NET**.

OmegaConf.NET brings the best features of Python's **Hydra** and **OmegaConf** to the .NET ecosystem:
- Directory-based **Config Groups** and **Defaults Lists**.
- **Multi-level Automatic Cascading** and cross-format explicit inheritance (`_inherits_`).
- In-config **mutation operators** (`+=`, `-=`, `*=`, `/=`, `&=`, `|=`, `!=`).
- **Variable interpolation** with list indexing (`${servers[0]}`), relative dot navigation, literal escaping, `${eval:...}`, and `${env:...}`.
- **Strict Structured Configs** binding to C# POCOs and positional `record` primary constructors.
- **Format Agnostic Parsing & Serialization** across YAML, JSON/JSONC, and TOML (`.ToYaml()`, `.ToJson()`, `.ToToml()`).
- Seamless **`Microsoft.Extensions.Configuration`** and DI integration.

---

## 1. Directory Tree & Config Composition

### Recommended Layout
Place configs in a folder (e.g. `configs/` or `conf/`):

```text
configs/
├── config.yaml               # Root entrypoint
├── db/                       # Config group: 'db'
│   ├── postgres.yaml         # Base postgres defaults
│   ├── sqlite.yaml
│   ├── staging/
│   │   └── postgres.yaml     # Staging overrides (auto-cascades on db/postgres.yaml)
│   └── production/
│       └── postgres.yaml     # Production overrides (uses operators)
└── model/                    # Config group: 'model'
    ├── fast.yaml
    └── accurate.yaml
```

### The Defaults List (`config.yaml`)
Always declare the `defaults:` list in your entrypoint file:

```yaml
defaults:
  - db: staging/postgres      # Resolves db/postgres.yaml -> db/staging/postgres.yaml
  - model: fast
  - optional secrets: local   # Gracefully ignored if local.yaml doesn't exist
  - _self_                    # Determines precedence of local settings below

app_name: "CatalogService"
active_workers: 4
```

### The `_self_` Keyword
- If `_self_` is placed at the **end** (or omitted), settings in `config.yaml` **override** the defaults.
- If `_self_` is placed at the **beginning**, the defaults **override** `config.yaml`.

### Automatic Cascading vs. Explicit Inheritance
- **Auto-Cascading (Default)**: If a child file like `db/staging/postgres.yaml` shares the same filename as its parent `db/postgres.yaml`, the base file is loaded automatically first. Only write diffs in the child file!
- **Explicit Inheritance**: To inherit from an arbitrary file, declare `_inherits_`:
  ```yaml
  _inherits_: "../base_db.yaml"
  # Or multiple bases:
  _inherits_: ["base1.yaml", "base2.yaml"]
  ```

---

## 2. In-Config Mutation Operators

OmegaConf.NET uses **key suffixes** to define mutation operators in YAML, JSON, and TOML.

### Operator Reference Table

| Operator | Key Syntax | Target Types | Behavior |
| :---: | :--- | :--- | :--- |
| `=` | `key=:` | Any | Direct assignment / replace |
| `+=` | `key+=:` | Numbers, Strings, Lists, Objects | Numeric add, string concat, append list items, merge object |
| `-=` | `key-=:` | Numbers, Lists | Numeric subtraction, remove matching list items |
| `*=` | `key*=:` | Numbers, Strings | Numeric multiplication, string repetition (`"ha"*=: 3` -> `"hahaha"`) |
| `/=` | `key/=:` | Numbers | Numeric division |
| `&=` | `key&=:` | Booleans, Integers | Logical AND, Bitwise AND |
| `|=` | `key|=:` | Booleans, Integers | Logical OR, Bitwise OR |
| `!=` | `key!=:` | Booleans | Logical toggle / invert current boolean value |

### Clean YAML Example
```yaml
# In child configuration:
max_connections*=: 4          # Multiply inherited 50 by 4 = 200
timeout_seconds+=: 30         # Add 30 to inherited timeout
tags+=: ["gpu", "cluster"]    # Append to inherited list
features-=: ["legacy_v1"]     # Remove item from inherited list
enable_ssl!=: true            # Invert inherited boolean
strict_mode&=: false          # Logical AND
```

---

## 3. Variable Interpolation & Resolvers

### Interpolation Syntax
- **Absolute Reference**: `${server.port}` or `${db.postgres.host}`.
- **List Indexing**: `${servers[0]}` or `${servers.0}`.
- **Native Type Preservation**: When a string is solely `${server.port}`, OmegaConf.NET preserves the native `int` type (rather than converting to `"8080"`).
- **Embedded Strings**: `"http://${server.host}:${server.port}/api"`.
- **Default Fallback**: `${cache.ttl:300}` (uses `300` if `cache.ttl` is not found).

### Relative & Sibling Navigation (Dot Syntax)
- **`.sibling`**: Reference a key in the **same enclosing object**:
  ```yaml
  db:
    port: 5432
    url: "postgresql://localhost:${.port}/app"
  ```
- **`..parent_sibling`**: Reference a key in the **parent object** (one level up):
  ```yaml
  db:
    host: "db.internal"
    staging:
      port: 5433
      connection: "Server=${..host};Port=${.port}"
  ```

### Literal Escaping
To output a literal `${...}` without resolving it (e.g. bash scripts, regex, or templates), prefix with a backslash:
```yaml
script: "echo \\${HOME} on port ${server.port}"
# Resolves to: "echo ${HOME} on port 8080"
```

### Expression & Math Evaluation (`${eval:...}`)
Allows embedded arithmetic, logic, and comparisons:
```yaml
batch_size: 32
num_gpus: 4
total_batch: "${eval: ${batch_size} * ${num_gpus}}"  # -> 128
timeout_calc: "${eval: 10 + 5 * 2}"                 # -> 20
is_scaled: "${eval: ${batch_size} >= 64}"           # -> False
```

### Environment Variables
```yaml
db_pass: "${env:DATABASE_PASSWORD, secret_dev_pw}"
```

### Mandatory Missing Marker (`???`)
Set a field to `???` to indicate that a value must be supplied by a child file or CLI argument. If left unresolved at runtime, throws `MissingValueMarkerException`.
```yaml
api_key: ???
```

---

## 4. Structured C# Configs (POCOs & Records)

OmegaConf.NET maps configurations into strongly typed C# classes and positional `record` types with automatic casing conversion:
- C# `PascalCase` properties and constructor parameters automatically match YAML `snake_case` (including acronyms like `SSLPort` ↔ `ssl_port`), `kebab-case`, `camelCase`, or exact names.

```csharp
using OmegaConf.NET.Attributes;

public class DatabaseConfig
{
    public string Driver { get; set; } = "postgres";
    public string Host { get; set; } = "localhost";
    public int Port { get; set; } = 5432;
    public int MaxConnections { get; set; }
    public TimeSpan Timeout { get; set; }
    public Uri? EndpointUri { get; set; }
}

public class AppConfig
{
    [ConfigRequired]
    public string AppName { get; set; } = "";

    public DatabaseConfig? Db { get; set; }

    [ConfigKey("active_workers")]
    public int Workers { get; set; } = 4;

    [ConfigIgnore]
    public string RuntimeState { get; set; } = "";
}

// Positional records with primary constructors are also supported:
public record ServiceRecordConfig(
    [property: ConfigRequired] string AppName,
    DatabaseConfig? Db,
    [property: ConfigKey("active_workers")] int Workers = 4);
```

### Attributes
- `[ConfigRequired]`: Throws `MissingRequiredConfigException` if property is absent or set to `???`.
- `[ConfigKey("custom_name")]`: Maps a custom or aliased configuration key.
- `[ConfigIgnore]`: Skips the property during binding.
- `_target_`: Supports dynamic instantiation of C# classes specified by assembly qualified name.

---

## 5. C# Builder & Framework Integration

### Fluent Builder
```csharp
using OmegaConf.NET;

AppConfig config = OmegaConfBuilder.Create<AppConfig>()
    .SetRoot("configs")
    .SetEntryFile("config.yaml")
    .SelectPath("db", "production/postgres")
    .AddCliArgs(args) // CLI overrides
    .Build();
```

### CLI Overrides Syntax
CLI arguments passed to `.AddCliArgs(args)` can override paths, drop groups, or mutate deep values:
```bash
# Select path
dotnet run -- db=production/postgres

# Drop default group (Hydra tilde syntax)
dotnet run -- ~db

# Deep property overrides with operators
dotnet run -- db.max_connections*=2 db.timeout+=10 tags+=[gpu,cluster] debug!=true
```

### ASP.NET Core & Microsoft.Extensions.Configuration
```csharp
using OmegaConf.NET.Extensions;

var builder = WebApplication.CreateBuilder(args);

// 1. Add as IConfiguration source
builder.Configuration.AddOmegaConf(omega =>
{
    omega.SetRoot("configs");
    omega.AddCliArgs(args);
});

// 2. Or register strongly typed options directly in DI
builder.Services.ConfigureOmegaConf<AppConfig>(omega =>
{
    omega.SetRoot("configs");
    omega.AddCliArgs(args);
});
```

### Multi-Run / Parameter Sweeps (Hydra `-m`)
Generate all combinations across comma-separated values:
```bash
dotnet run -- -m db=staging/postgres,production/postgres active_workers=2,4,8
```
In C#:
```csharp
IReadOnlyList<AppConfig> configs = OmegaConfBuilder.Create<AppConfig>()
    .SetRoot("configs")
    .AddCliArgs(args)
    .BuildSweeps();
```

### Config Differ & Serialization
Compare two configuration layers and inspect changes, or export nodes to YAML, JSON, or TOML:
```csharp
using OmegaConf.NET.Diff;
using OmegaConf.NET.Serialization;

ConfigDiff diff = ConfigDiffer.Compare(baseNode, overrideNode);
Console.WriteLine(diff.ToString());
// Outputs: ~ key: old -> new, + key: val, - key: val

string yaml = overrideNode.ToYaml();
string json = overrideNode.ToJson(indented: true);
string toml = overrideNode.ToToml();
```

### JSON Schema Generation for IDE Auto-Complete
Generate Draft-07 schemas for VS Code, Visual Studio, and Rider intellisense:
```csharp
using OmegaConf.NET.Schema;

JsonSchemaGenerator.GenerateSchemaFile<AppConfig>("configs/config.schema.json");
```
Add to `config.yaml`:
```yaml
# yaml-language-server: $schema=./config.schema.json
```

---

## 6. Antipatterns & Common AI Mistakes to Avoid

1. ❌ **Do NOT put operators inside scalar values**:
   - Wrong: `timeout: "+= 10"`
   - Right: `timeout+=: 10`
2. ❌ **Do NOT duplicate inherited base fields**:
   - If `db/postgres.yaml` has `port: 5432`, `db/staging/postgres.yaml` does not need to repeat `port: 5432`. It automatically cascades!
3. ❌ **Do NOT use string interpolation for pure numbers if you want strong typing**:
   - Writing `timeout: "${base_timeout}"` preserves the integer type `30`. Do not wrap with extra quotes or text unless string concatenation is intended.
4. ❌ **Do NOT forget `_self_` when local file overrides are needed**:
   - If `config.yaml` defines a parameter that should take precedence over `defaults:`, always place `_self_` at the end of the `defaults:` list.
