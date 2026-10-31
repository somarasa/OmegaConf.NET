# OmegaConf.NET 🚀

[![License: MIT](https://img.shields.io/badge/License-MIT-blue.svg)](LICENSE)
[![.NET](https://img.shields.io/badge/.NET-8.0%20%7C%209.0%20%7C%2010.0-purple.svg)](https://dotnet.microsoft.com/)

**OmegaConf.NET** is a powerful, hierarchical, and composable configuration framework for C# and .NET, inspired by Python's [Hydra](https://hydra.cc/) and [OmegaConf](https://omegaconf.readthedocs.io/).

It bridges the gap in the .NET configuration ecosystem by bringing **tree-based config composition**, **automatic inheritance cascading**, **mutation operators** (`+=`, `-=`, `*=`, `/=`, `&=`, `|=`, `!=`), **strict structured typing**, **variable interpolation**, and seamless **`Microsoft.Extensions.Configuration`** integration.

---

## ✨ Features

- 🌳 **Hierarchical Tree Composition**: Organize configs into reusable Config Groups (e.g. `db/`, `model/`, `env/`) and compose them using Hydra-style `defaults` lists.
- ⚡ **Auto-Cascading & Explicit Inheritance**: Sub-configs (e.g. `db/staging/postgres.yaml`) automatically inherit from base files (`db/postgres.yaml`), keeping configurations DRY. Also supports explicit `_inherits_: "../base.yaml"`.
- 🧮 **In-Config Mutation Operators**:
  - Direct assignment: `=`
  - Arithmetic & append: `+=`, `-=`, `*=`, `/=`
  - Boolean logic & toggle: `&=`, `|=`, `!=`
- 🧩 **Format Agnostic**: Built-in support for **YAML**, **TOML**, and **JSON / JSONC** (JSON with comments and trailing commas).
- 🔗 **Rich Variable Interpolation**:
  - `${path.to.key}` with **native type preservation** (numbers remain numbers, booleans remain booleans).
  - Default fallback syntax: `${path.to.key:default_value}`.
  - Built-in environment variable resolver: `${env:VAR_NAME, fallback}`.
  - Extensible custom resolvers in C# (`${upper:...}`, `${now:...}`).
  - Circular dependency detection and cycle prevention.
- ❓ **Mandatory Value Marker (`???`)**: Mark keys that must be supplied by lower layers or CLI overrides.
- 💻 **CLI Overrides**: Seamlessly switch paths (`db=staging/postgres`), drop branches (`~db`), or mutate values (`threads+=2`, `debug!=true`) from `args[]`.
- 🛡️ **Structured Configs & Strict Typing**: Bind to POCOs and records with automatic `PascalCase` ↔ `snake_case` mapping, `[ConfigRequired]`, `[ConfigKey]`, and `[ConfigIgnore]` attributes.
- 🔌 **Standard .NET Integration**: Native `IConfigurationSource` and `ConfigurationProvider` for ASP.NET Core and `Microsoft.Extensions.DependencyInjection`.

---

## 📦 Installation

```bash
dotnet add package OmegaConf.NET
dotnet add package OmegaConf.NET.Extensions
```

---

## 🚀 Quick Start

### 1. Organize Your Configurations

```text
configs/
├── config.yaml               # Root entrypoint
└── db/
    ├── postgres.yaml         # Base PostgreSQL defaults
    ├── sqlite.yaml
    ├── staging/
    │   └── postgres.yaml     # Staging overrides
    └── production/
        └── postgres.yaml     # Production overrides (uses operators)
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
user: "db_admin"
max_connections: 50
ssl: false
timeout_seconds: 30
```

#### `configs/db/staging/postgres.yaml` (Automatically cascades on `db/postgres.yaml`!)
```yaml
host: "staging-db.internal"
max_connections: 20
```

#### `configs/db/production/postgres.yaml`
```yaml
host: "prod-db.internal"
max_connections*=: 4          # 50 * 4 = 200 connections
ssl: true
timeout_seconds+=: 30         # 30 + 30 = 60s timeout
```

---

### 2. Define Strongly Typed C# Classes

```csharp
using OmegaConf.NET.Attributes;

public class DatabaseConfig
{
    public string Driver { get; set; } = "";
    public string Host { get; set; } = "";
    public int Port { get; set; }
    public int MaxConnections { get; set; }
    public bool Ssl { get; set; }
    public int TimeoutSeconds { get; set; }
}

public class AppConfig
{
    [ConfigRequired]
    public string AppName { get; set; } = "";

    public DatabaseConfig? Db { get; set; }

    public int ActiveWorkers { get; set; }
}
```

---

### 3. Load & Compose

```csharp
using OmegaConf.NET;

// Build strongly typed configuration
AppConfig config = OmegaConfBuilder.Create<AppConfig>()
    .SetRoot("configs")
    .AddCliArgs(args) // Supports: db=production/postgres active_workers+=2
    .Build();

Console.WriteLine($"Service: {config.AppName}");
Console.WriteLine($"DB Host: {config.Db?.Host}, Max Connections: {config.Db?.MaxConnections}");
```

---

## 🧮 Operators Reference

OmegaConf.NET supports operators as key suffixes in YAML, JSON, and TOML, as well as in CLI arguments:

| Operator | Action | Applicable Data Types | Example |
| :---: | :--- | :--- | :--- |
| `=` | Assignment / Replace | Any | `port=: 5432` |
| `+=` | Addition / Concat / Append | Numbers, Strings, Lists, Objects | `timeout+=: 10`, `tags+=: ["gpu"]` |
| `-=` | Subtraction / Item Removal | Numbers, Lists | `retry_count-=: 1`, `items-=: ["old"]` |
| `*=` | Multiplication / Repeat | Numbers, Strings | `workers*=: 2`, `"ha"*=: 3` |
| `/=` | Division | Numbers | `rate/=: 2` |
| `&=` | Logical / Bitwise AND | Booleans, Integers | `strict_mode&=: false` |
| `|=` | Logical / Bitwise OR | Booleans, Integers | `enable_feature|=: true` |
| `!=` | Logical Toggle / Invert | Booleans | `debug!=: true` *(flips current bool)* |

---

## 💻 CLI Overrides

OmegaConf.NET translates command-line arguments dynamically:

```bash
# 1. Switch config group paths
dotnet run -- db=production/postgres

# 2. Exclude a config group completely (Hydra tilde syntax)
dotnet run -- ~db

# 3. Override deep properties with operators
dotnet run -- db.max_connections=500 db.timeout_seconds+=15 active_workers+=4 debug!=true
```

---

## 🔗 Interpolation & Resolvers

### Path Interpolation & Type Preservation
```yaml
server:
  host: "10.0.0.1"
  port: 8080

# String interpolation
api_url: "http://${server.host}:${server.port}/api"

# Native type preservation (target_port resolves to int 8080, not string "8080")
target_port: "${server.port}"

# Fallback default syntax
cache_ttl: "${cache.timeout:300}"
```

### Relative & Sibling Navigation (Dot Syntax)
Refer to keys in the same object or parent objects without writing full paths:
```yaml
db:
  host: "db.internal"
  port: 5432
  staging:
    port: 5433
    # '.port' resolves to sibling (5433)
    # '..host' resolves to parent db.host ("db.internal")
    connection: "Server=${..host};Port=${.port}"
```

### Literal Escaping (`\${...}`)
Escape interpolations so they aren't evaluated (e.g. for shell scripts, regex, or templates):
```yaml
command: "echo \\${HOME} on port ${server.port}"  # Resolves to: "echo ${HOME} on port 8080"
```

### Expression & Math Evaluation (`${eval:...}`)
Perform arithmetic, logic, and comparisons inside configs:
```yaml
batch_size: 32
num_gpus: 4
total_batch: "${eval: ${batch_size} * ${num_gpus}}"  # Resolves to 128
timeout_calc: "${eval: 10 + 5 * 2}"                 # Resolves to 20
```

### Environment Variables & Custom Resolvers
```yaml
# Environment variable with fallback
database_url: "${env:DATABASE_URL, postgresql://localhost:5432/dev}"

# Custom registered resolver
log_dir: "/var/log/apps/${slugify:${app_name}}"
```

Register custom resolvers in C#:
```csharp
builder.RegisterResolver("slugify", raw => raw.ToLowerInvariant().Replace(' ', '-'));
```

---

## 🔌 Microsoft.Extensions.Configuration Integration

Plug into ASP.NET Core `IConfiguration` and `WebApplicationBuilder`:

```csharp
using OmegaConf.NET.Extensions;

var builder = WebApplication.CreateBuilder(args);

// Register OmegaConf.NET as configuration source
builder.Configuration.AddOmegaConf(omega =>
{
    omega.SetRoot("configs");
    omega.AddCliArgs(args);
});

// Access via standard IConfiguration
string? dbHost = builder.Configuration["db:host"];

// Or register strongly typed options in DI
builder.Services.ConfigureOmegaConf<AppConfig>(omega =>
{
    omega.SetRoot("configs");
    omega.AddCliArgs(args);
});
```

---

## 🧪 Running Tests

```bash
dotnet test -m:1
```

---

## 🤖 AI Coding Assistants & Skills

OmegaConf.NET includes a dedicated [SKILL.md](skills/omegaconf-net/SKILL.md) guide tailored for AI coding assistants (Antigravity, Cursor, Claude Code, Copilot, etc.). It provides explicit prompt instructions, operator rules, syntax tables, and best practices to guide AI when generating or refactoring configs in your projects.

---

## 📄 License

This project is open-source under the [MIT License](LICENSE). Contributions, issues, and pull requests are warmly welcome!
