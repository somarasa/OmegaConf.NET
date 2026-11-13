---
name: omegaconf-net
description: Authoritative guide for AI coding assistants to design, generate, refactor, and integrate hierarchical configurations in .NET using OmegaConf.NET.
---

# OmegaConf.NET Skill Guide for AI Assistants

Use this skill when you need to create, modify, or integrate configuration trees in C# / .NET using **OmegaConf.NET**.

OmegaConf.NET brings the best features of Python's **Hydra** and **OmegaConf** to the .NET ecosystem:
- Directory-based **Config Groups** and **Defaults Lists**.
- **Multi-Config Selection**: selecting several configs from one group **merges** them into a keyed object at the group package — namespace each with `@package group.<name>` to keep them distinct.
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
  - db: staging/postgres        # Resolves db/postgres.yaml -> db/staging/postgres.yaml
  - db@replica_db: sqlite       # Mounts db/sqlite.yaml under 'replica_db' (@package directive)
  - experiment@_global_: fast   # Merges experiment/fast.yaml directly into root (@_global_)
  - model: ${default_model}     # Interpolated default entry (supports properties, env vars, or earlier group choices)
  - optional secrets: local     # Gracefully ignored if local.yaml doesn't exist
  - _self_                      # Determines precedence of local settings below

app_name: "CatalogService"
active_workers: 4
```

### The `_self_` Keyword
- If `_self_` is placed at the **end** (or omitted), settings in `config.yaml` **override** the defaults.
- If `_self_` is placed at the **beginning**, the defaults **override** `config.yaml`.
- `_self_` can appear at most **once** in any defaults list (duplicates throw `ConfigSyntaxException`).

### Defaults List Directives & Syntax Rules
- **No Parent Directory Traversal (`..`)**: Defaults lists strictly forbid `..` (`- ../base` or `- db: ../sqlite` is rejected). Paths must be relative to the group directory or absolute from the configuration root starting with `/` (e.g. `- /shared/base` or `- /cloud/aws@_group_.primary: rds`). To inherit from parent files outside the tree, use `_inherits_: "../base.yaml"` or an absolute rooted path `- /path`.
- **Overriding in Child Configs (`override`)**: If a child configuration changes a default choice selected by a parent base, prefix it with `override`:
  ```yaml
  defaults:
    - override db: mysql
  ```
  Specifying conflicting choices for the same group without `override` throws `ConfigSyntaxException`.
- **Disabling a Group (`null`)**: Set `- group: null` (or CLI `group=null`) to skip loading any configuration from that group.
- **Mandatory Defaults (`???`)**: Mark a group as `- group: ???` when a choice must be supplied by the caller via CLI (`group=choice`). If left unresolved at runtime, throws `MissingValueMarkerException`.
- **Optional Defaults (`optional`)**: Prefix with `optional` (`- optional secrets: local`) to gracefully ignore missing configuration files without error.
- **Slash Normalization in Subgroups**: If a choice selects a subgroup like `- group_a: sub_group/item`, any relative defaults declared inside `item.yaml` resolve inside `group_a/sub_group/`.

### Package Directive Precedence
When determining where a configuration file mounts in the output object tree, OmegaConf.NET follows Hydra's strict precedence order:
1. **Defaults List Package Directive**: `- group@custom_pkg: choice` (or CLI `+group@pkg=choice`) has highest precedence.
2. **File Header Directive**: `# @package custom_pkg` in the target file.
   > **Note**: Never put inline comments on the `# @package` line (e.g. `# @package foo # comment` will corrupt the package name). Keep comments on separate lines.
3. **Config Group Name**: Default fallback derived from the group folder name (e.g. `db/` mounts under `db`).

Special package keywords:
- `@_global_`: Mounts keys directly at the root object level.
- `@_group_`: Refers to the config group name (e.g. `- /cloud/aws@_group_.primary: rds` mounts at `cloud.aws.primary`).

### Automatic Cascading vs. Explicit Inheritance
- **Auto-Cascading (Default)**: If a child file like `db/staging/postgres.yaml` shares the same filename as its parent `db/postgres.yaml`, the base file is loaded automatically first. Only write diffs in the child file!
- **Explicit Inheritance**: To inherit from an arbitrary file, declare `_inherits_`:
  ```yaml
  _inherits_: "../base_db.yaml"
  # Or multiple bases:
  _inherits_: ["base1.yaml", "base2.yaml"]
  ```

### Selecting Multiple Configs from a Group (Multi-Config)

You can select **several** configs from one group at once by passing a list in the Defaults List (or on the command line):

```yaml
defaults:
  - services: [postgres, temporal]   # two files from the services/ group
```

**Composition MERGES — it does not build a list.** OmegaConf.NET mounts every selected file under the group's package and **merges** them into a single object. It never concatenates them into an array (this matches upstream Hydra):

```yaml
# services/postgres.yaml   -> mounted at: services
name: postgres
type: _postgres._tcp
port: 5432
# services/temporal.yaml   -> also mounted at: services
name: temporal
type: _temporal._tcp
port: 7233
# =>
services:
  name: temporal       # temporal clobbered postgres (same key)
  type: _temporal._tcp
  port: 7233
```

**Keep each file distinct by giving it its own namespace** with a `# @package group.<name>` header — the documented Hydra pattern ("use an explicit nesting level to prevent them stepping over one another"). Each file then mounts under its own key, so nothing is clobbered:

```yaml
# services/postgres.yaml
# @package services.postgres
name: postgres
type: _postgres._tcp
port: 5432
# services/temporal.yaml
# @package services.temporal
name: temporal
type: _temporal._tcp
port: 7233
# =>
services:
  postgres:
    name: postgres
    type: _postgres._tcp
    port: 5432
  temporal:
    name: temporal
    type: _temporal._tcp
    port: 7233
```

Bind the resulting keyed object to a dictionary, then index by name:

```csharp
public record AppConfig(
    [property: ConfigRequired] string AppName,
    IReadOnlyDictionary<string, ServiceDiscoveryConfig>? Services = null);
```

```csharp
var postgres = cfg.Services!["postgres"];   // port 5432
```

> Notes:
> - A true C# `List<T>` / `T[]` comes from a **structured-config field** typed as a list; multi-config composition itself never yields an array.
> - A config file's **root must be a mapping/object** — OmegaConf.NET rejects a bare list as the root of a config file.

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
- **Inside List Elements (`.` / `..` / `...`)**:
  Within list elements (e.g. `container.items[1]`):
  - **`${.0}`**: References sibling index `0` of the *current list*.
  - **`${..sibling_key}`**: Escapes the list to reference a field in the enclosing container (`container.sibling_key`).
  - **`${...root_key}`**: Escapes the enclosing container up to the grandparent/root level.
  ```yaml
  parent_prefix: global_v1
  container:
    sibling_tag: local_tag
    items:
      - first_elem
      - "${.0}:${..sibling_tag}:${...parent_prefix}"  # -> "first_elem:local_tag:global_v1"
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

### Environment Variables & Built-In `oc.*` / `now` Resolvers
```yaml
db_pass: "${env:DATABASE_PASSWORD, secret_dev_pw}"
safe_port: "${oc.select:db.port, 5432}"
decoded_port: "${oc.decode:${oc.env:PORT_STR, 8080}}"
db_keys: "${oc.dict.keys:db}"
run_dir: "outputs/${now:yyyy-MM-dd}"
```

### Mandatory Missing Marker (`???`) & Escaped Literal (`\???`)
Set a field to `???` to indicate that a value must be supplied by a child file or CLI argument. If left unresolved at runtime, throws `MissingValueMarkerException`. Use `\???` to store the literal string `"???"`.
```yaml
api_key: ???
literal_questions: \???
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

### CLI Overrides, Struct Mode & Multi-Run Sweep Generators
CLI arguments passed to `.AddCliArgs(args)` can override paths, redirect `@package`, drop groups/keys (`~`), enforce Struct Mode (`+` / `++`), or run parameter sweeps (`-m`):

| Prefix / Pattern | Example | Purpose / Behavior |
| :--- | :--- | :--- |
| `key=val` | `server.port=8080` | Normal override (throws in Struct Mode if key is not defined). |
| `+key=val` | `+server.ssl=true` | Strict add — throws if `server.ssl` already exists. |
| `++key=val` | `++server.ssl=true` | Upsert — safely adds if missing or updates if already present. |
| `~key` | `~server.ssl` | Deletes the key from configuration. |
| `~key=expected` | `~legacy_flag=true` | Conditional delete — only removes if current value is `true` (throws on mismatch). |
| `~group` / `~group=choice` | `~db` or `~db=postgres` | Drops default group selection entirely (conditionally checks choice). |
| `tags=[a,b]` | `tags=[gpu,bench]` | Passes an explicit literal list of strings. |
| `param=v1,v2` (with `-m`) | `-m lr=0.01,0.1` | Triggers a Cartesian product sweep across 2 separate runs. |

> **Gotcha**: `++group=choice` is invalid for config groups and throws `ConfigSyntaxException`. `++` is strictly for keys/properties; config groups are selected with `group=choice` or `+group=choice`.

```bash
# Select path or @package target
dotnet run -- db=production/postgres +experiment@_global_=fast

# Drop default group, delete a deep key, delete a list item, or conditionally delete when value matches
dotnet run -- ~db ~server.secret_token ~items[-1] ~legacy_flag=true

# Struct mode additions (+key fails if key exists; ++key upserts safely)
dotnet run -- +db.pool_size=20 ++db.port=5433

# List element indexing, type casts, sort(), and extend_list()
dotnet run -- items[-1]=99 port=int(8080) tags=extend_list(gpu,cluster) ranks=sort([3,1,2])

# Deep property overrides with operators
dotnet run -- db.max_connections*=2 db.timeout+=10 tags+=[gpu,cluster] debug!=true

# Multi-run sweeps with range(), linspace(), choice(), glob(), sort(), and shuffle()
dotnet run -- -m db=glob(*) active_workers=range(2,8,2) lr=linspace(0.01,0.1,5) seed=sort(30,10,20)
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

5. ❌ **Do NOT assume `group: [a, b]` produces a list** — multi-config **merges** the selected files into a single object at the group package (matching upstream Hydra); it never builds an array. To keep each file distinct, namespace them with a `# @package group.<name>` header so each mounts under its own key, and bind the result to an `IReadOnlyDictionary<string, T>` indexed by name.
6. ❌ **Do NOT use `..` parent traversal in `defaults:` lists**:
   - Wrong: `defaults: [ - ../base, - db: ../sqlite ]`
   - Right: Use absolute rooted paths prefixed with `/` (`- /shared/base`) or explicit `_inherits_: "../base.yaml"`. Hydra and OmegaConf.NET reject `..` in defaults lists.
7. ❌ **Do NOT redefine a default group choice in a child file without `override`**:
   - Wrong: `defaults: [ - db: mysql ]` in a child configuration when a parent base already specified a choice for `db`.
   - Right: `defaults: [ - override db: mysql ]`. Conflicting choices without `override` throw `ConfigSyntaxException`.
8. ❌ **Do NOT put `_self_` more than once in a defaults list**:
   - Duplicate `_self_` entries are invalid and throw `ConfigSyntaxException`.
9. ❌ **Do NOT confuse sweep commas with list literals in CLI overrides**:
   - In `-m` multi-run, `tags=gpu,cluster` runs 2 separate sweep runs (`tags=gpu` and `tags=cluster`).
   - To set an explicit list literal of multiple items in a single run or sweep, enclose it in brackets: `tags=[gpu,cluster]`.
10. ❌ **Do NOT use `++` on config groups in CLI**:
    - `++` is strictly for config keys/properties (e.g. `++server.timeout=30`).
    - Using `++db=mysql` throws `ConfigSyntaxException`. Use `db=mysql` or `+db=mysql` to select a group.
11. ❌ **Do NOT leave unescaped `???` when literal text is intended**:
    - `???` is the mandatory missing marker that halts execution with `MissingValueMarkerException` if not overridden before binding.
    - If you need the literal string `"???"`, escape it as `\???`.
12. ❌ **Do NOT put inline comments on the same line as `# @package`**:
    - Wrong: `# @package _global_ # load at root`
    - Right: Keep comments on separate lines above or below `# @package _global_` so the package name is parsed cleanly.
