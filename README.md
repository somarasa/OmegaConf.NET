# OmegaConf.NET

[![CI](https://github.com/somarasa/OmegaConf.NET/actions/workflows/ci.yml/badge.svg)](https://github.com/somarasa/OmegaConf.NET/actions/workflows/ci.yml)
[![License: MIT](https://img.shields.io/badge/License-MIT-blue.svg)](LICENSE)
[![.NET](https://img.shields.io/badge/.NET-8.0%20%7C%209.0%20%7C%2010.0-purple.svg)](https://dotnet.microsoft.com/)

OmegaConf.NET is a hierarchical, composable configuration framework for C# and .NET inspired by Python's [Hydra](https://hydra.cc/) and [OmegaConf](https://omegaconf.readthedocs.io/).

It provides tree-based configuration composition, automatic inheritance cascading, in-config mutation operators (`+=`, `-=`, `*=`, `/=`, `&=`, `|=`, `!=`), strict structured typing, variable interpolation, parameter sweeps, and native `Microsoft.Extensions.Configuration` integration.

---

## Features

- **Hierarchical Composition**: Organize configurations into modular Config Groups (e.g., `db/`, `model/`, `env/`) and compose them via `defaults` lists.
- **Auto-Cascading & Explicit Inheritance**: Nested configurations automatically cascade on base files sharing the same name (`db/staging/postgres.yaml` inherits from `db/postgres.yaml`). Also supports explicit `_inherits_: "../base.yaml"`.
- **In-Config Mutation Operators**: Key suffixes modify inherited values (`+=`, `-=`, `*=`, `/=`, `&=`, `|=`, `!=`).
- **Format Agnostic**: Unified AST parsing and serialization across YAML, JSON / JSONC, and TOML.
- **Variable Interpolation**:
  - `${path.to.key}` with native type preservation (numbers remain numbers, booleans remain booleans).
  - List indexing: `${servers[0]}` or `${servers.0}`.
  - Sibling and parent navigation: `${.sibling}` and `${..parent}`.
  - Built-in math and logic evaluation via `${eval:...}`.
  - Environment variables: `${env:VAR, fallback}` and `${oc.env:VAR, fallback}`.
  - Extensible custom C# resolvers.
  - Cycle detection and circular dependency prevention.
- **Mandatory Value Markers (`???`)**: Mark keys that must be supplied by child layers or command-line arguments.
- **Command-Line Overrides**: Override group selections (`db=production/postgres`), drop groups (`~db`), mutate values (`threads+=2`, `debug!=true`), or enforce strict mode (`+key=val`, `++key=val`).
- **Parameter Sweeps (Multi-Run)**: Run Cartesian product sweeps across comma-separated values or generator functions (`range()`, `linspace()`, `choice()`, `glob()`).
- **Structured Configs**: Bind AST nodes to C# POCOs and positional `record` primary constructors with automatic casing translation (`PascalCase` to `snake_case`, `kebab-case`, or exact match).
- **.NET Integration**: First-class `IConfigurationSource` and `ConfigurationProvider` for ASP.NET Core and Microsoft DI.

---

## Installation

```bash
dotnet add package OmegaConf.NET
dotnet add package OmegaConf.NET.Extensions
```

---

## Quick Start

### 1. Configuration Files Layout

```text
configs/
├── config.yaml               # Root entrypoint
└── db/
    ├── postgres.yaml         # Base defaults
    ├── sqlite.yaml
    ├── staging/
    │   └── postgres.yaml     # Staging overrides (cascades on db/postgres.yaml)
    └── production/
        └── postgres.yaml     # Production overrides with operators
```

#### `configs/config.yaml`
```yaml
defaults:
  - db: staging/postgres
  - _self_

app_name: "CatalogService"
active_workers: 4
```

#### `configs/db/postgres.yaml` (Base)
```yaml
driver: postgres
port: 5432
max_connections: 50
ssl: false
timeout_seconds: 30
```

#### `configs/db/staging/postgres.yaml` (Overrides)
```yaml
host: "staging-db.internal"
max_connections: 20
```

#### `configs/db/production/postgres.yaml` (Overrides with Operators)
```yaml
host: "prod-db.internal"
max_connections*=: 4          # 50 * 4 = 200
ssl: true
timeout_seconds+=: 30         # 30 + 30 = 60
```

---

### 2. Define Strongly Typed C# Models

```csharp
using OmegaConf.NET.Attributes;

public class DatabaseConfig
{
    public string Driver { get; set; } = "postgres";
    public string Host { get; set; } = "localhost";
    public int Port { get; set; } = 5432;
    public int MaxConnections { get; set; }
    public bool Ssl { get; set; }
    public int TimeoutSeconds { get; set; }
}

public class AppConfig
{
    [ConfigRequired]
    public string AppName { get; set; } = "";

    public DatabaseConfig? Db { get; set; }

    public int ActiveWorkers { get; set; } = 4;
}

// Positional records are also supported:
public record ServiceRecordConfig(
    [property: ConfigRequired] string AppName,
    DatabaseConfig? Db,
    int ActiveWorkers = 4);
```

---

### 3. Load & Bind

```csharp
using OmegaConf.NET;

AppConfig config = OmegaConfBuilder.Create<AppConfig>()
    .SetRoot("configs")
    .AddCliArgs(args) // CLI overrides, e.g. db=production/postgres active_workers+=2
    .Build();

Console.WriteLine($"App: {config.AppName}");
Console.WriteLine($"Database: {config.Db?.Host}:{config.Db?.Port} (Connections: {config.Db?.MaxConnections})");
```

---

## Operators Reference

Operators are declared as key suffixes in configuration files or passed via CLI overrides:

| Operator | Action | Target Types | Example |
| :---: | :--- | :--- | :--- |
| `=` | Assignment / replace | Any | `port=: 5432` |
| `+=` | Add / concatenate / append | Numbers, Strings, Lists, Objects | `timeout+=: 10`, `tags+=: ["gpu"]` |
| `-=` | Subtraction / item removal | Numbers, Lists | `retry_count-=: 1`, `items-=: ["old"]` |
| `*=` | Multiplication / repeat | Numbers, Strings | `workers*=: 2`, `"ha"*=: 3` |
| `/=` | Division | Numbers | `rate/=: 2` |
| `&=` | Logical / bitwise AND | Booleans, Integers | `strict_mode&=: false` |
| `|=` | Logical / bitwise OR | Booleans, Integers | `enable_feature|=: true` |
| `!=` | Boolean toggle / invert | Booleans | `debug!=: true` |

---

## Command-Line Overrides & Sweeps

### CLI Overrides
```bash
# Switch config group choice
dotnet run -- db=production/postgres

# Drop a config group
dotnet run -- ~db

# Override properties with mutation operators
dotnet run -- db.max_connections=500 db.timeout_seconds+=15 active_workers+=4 debug!=true

# Struct mode (safe add / upsert)
dotnet run -- +db.pool_size=20 ++db.port=5433
```

### Parameter Sweeps (`-m` / `--multirun`)
Generate and run combinations across parameters:
```bash
dotnet run -- -m db=staging/postgres,production/postgres active_workers=2,4,8
```

Programmatic execution:
```csharp
IReadOnlyList<AppConfig> configs = OmegaConfBuilder.Create<AppConfig>()
    .SetRoot("configs")
    .AddCliArgs(args)
    .BuildSweeps();
```

---

## Variable Interpolation & Resolvers

### Interpolation Syntax
```yaml
server:
  host: "10.0.0.1"
  port: 8080

# String interpolation
api_url: "http://${server.host}:${server.port}/api"

# Native type preservation (resolves to int 8080, not string)
target_port: "${server.port}"

# Fallback default syntax
cache_ttl: "${cache.timeout:300}"

# List element indexing
servers: ["alpha.local", "beta.local"]
primary_server: "${servers[0]}"
```

### Relative Navigation (Dot Syntax)
```yaml
db:
  host: "db.internal"
  staging:
    port: 5433
    # '.port' resolves to sibling (5433); '..host' resolves to parent db.host
    connection: "Server=${..host};Port=${.port}"
```

### Resolvers
```yaml
# Environment variables
database_url: "${env:DATABASE_URL, postgresql://localhost:5432/dev}"

# Math & boolean evaluation
total_batch: "${eval: ${batch_size} * ${num_gpus}}"

# Safe path selection & string decoding
optional_port: "${oc.select:db.port, 5432}"
decoded_port: "${oc.decode:${oc.env:PORT_STR, 8080}}"
run_dir: "outputs/${now:yyyy-MM-dd}"
```

Custom resolver registration:
```csharp
builder.RegisterResolver("slugify", raw => raw.ToLowerInvariant().Replace(' ', '-'));
```

---

## Configuration Diffing & Serialization

```csharp
using OmegaConf.NET.Diff;
using OmegaConf.NET.Serialization;

// Diff two configuration nodes
ConfigDiff diff = ConfigDiffer.Compare(stagingNode, productionNode);
Console.WriteLine(diff.ToString());

// Export back to YAML, JSON, or TOML
string yaml = productionNode.ToYaml();
string json = productionNode.ToJson(indented: true);
string toml = productionNode.ToToml();
```

---

## JSON Schema Generation

Generate standard Draft-07 schemas for editor autocompletion:
```csharp
using OmegaConf.NET.Schema;

JsonSchemaGenerator.GenerateSchemaFile<AppConfig>("configs/config.schema.json");
```

Add to `config.yaml` for IDE validation:
```yaml
# yaml-language-server: $schema=./config.schema.json
app_name: "CatalogService"
```

---

## Microsoft.Extensions.Configuration Integration

```csharp
using OmegaConf.NET.Extensions;

var builder = WebApplication.CreateBuilder(args);

// Register as IConfiguration provider
builder.Configuration.AddOmegaConf(omega =>
{
    omega.SetRoot("configs");
    omega.AddCliArgs(args);
});

// Or register strongly typed options in DI
builder.Services.ConfigureOmegaConf<AppConfig>(omega =>
{
    omega.SetRoot("configs");
    omega.AddCliArgs(args);
});
```

---

## Sample Projects

- **QuickStart**: Minimal runnable console application in [`samples/QuickStart`](samples/QuickStart).
- **HydraExamples**: Complete C# port of the official 34 Python Hydra examples in [`samples/HydraExamples`](samples/HydraExamples).

---

## Running Tests

```bash
dotnet test -m:1
```

---

## AI Agent Integration

- [**AGENTS.md**](AGENTS.md): Architecture manual and multi-targeting directives for AI coding agents.
- [**SKILL.md**](skills/omegaconf-net/SKILL.md): Authoritative agent skill guide with syntax tables, gotchas, and composition rules.

---

## License

This project is licensed under the [MIT License](LICENSE).
