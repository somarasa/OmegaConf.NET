# Changelog

All notable changes to **OmegaConf.NET** will be documented in this file.

The format is based on [Keep a Changelog](https://keepachangelog.com/en/1.1.0/),
and this project adheres to [Semantic Versioning](https://semver.org/spec/v2.0.0.html).

## [0.1.0] - 2026-10-31

### Added
- **Hierarchical Tree Composition, Interpolated Defaults, `ConfigStore` & Package Directives**: Hydra-style `defaults:` lists (in both entrypoint and nested config group files), interpolated defaults (`- db: ${default_db}`, `- group1/group2/${group1}`), in-memory `ConfigStore` (`ConfigStore.Instance` & `WithConfigStore`), Config Groups, multi-directory search paths (`AddSearchPath` / `WithSearchPaths` / `hydra.searchpath`), `@package`, `@_global_`, `@_group_` / `@_group_.suffix` mounting directives, `# @package` file headers, `override group: file` and `optional group: file` keywords, and `_self_` precedence control.
- **Multi-Level Auto-Cascading & Explicit Inheritance**: Automatic parent cascading across directory hierarchies (e.g. `db/postgres.yaml` -> `db/cloud/postgres.yaml` -> `db/cloud/aws/postgres.yaml`) and explicit `_inherits_` / `_base_` support across formats.
- **In-Config & CLI Mutation Operators, Override Aliases, Grammar Functions & Struct Mode**:
  - Key-suffix and CLI operators `=`, `+=`, `-=`, `*=`, `/=`, `&=`, `|=`, and `!=` across numbers, strings, booleans, lists, and objects.
  - CLI Override Aliases (`hydra.aliases` / `omegaconf.aliases` and `builder.AddAlias`) supporting key aliases (`bs=32` -> `trainer.batch_size=32`) and value aliases (`v` -> `verbose=true`).
  - Strict Struct Mode (`WithStructMode()` / `SetStruct()`), `+key=val` strict append, `++key=val` safe upsert, `~key.path` / `~items[0]` / `~items[-1]` deep property and list element deletion, and conditional value-checked deletion (`~key=expected`, `~key=???`, `~key=\???`).
  - Hydra CLI grammar functions: `int()`, `float()`, `str()`, `bool()`, `sort([...][, reverse=true])`, `shuffle(...)`, `tag(...)`, and `extend_list(...)`.
  - CLI Inspection & Help flags (`--cfg [all|job|hydra]`, `--info [all|defaults|aliases|searchpath]`, `--help` / `-h` via `TryHandleCliInspection`).
  - Escaped missing literal support (`\???` -> literal `"???"` vs `???` mandatory missing value marker).
- **Multi-Format Parsing, Serialization & Programmatic AST API**:
  - Built-in support for YAML (`.yaml`, `.yml`), JSON / JSONC (`.json`, `.jsonc` with comments and trailing commas), and TOML (`.toml`), plus `.ToYaml()`, `.ToJson()`, and `.ToToml()` serialization.
  - `OmegaConfig` static facade (`Merge`, `Select`, `SetReadOnly` / `Freeze`, `SetStruct`, `Instantiate`, `ParseYaml`, `ParseJson`, `ParseToml`).
- **Variable Interpolation & Resolvers**:
  - Native type preservation for single-variable interpolations (`${server.port}`) and AST subtree/node replacement.
  - List element indexing (`${servers[0]}` and `${servers.0}`).
  - Relative and sibling dot navigation (`${.sibling}` and `${..parent}`).
  - Literal escaping (`\${...}`) and default fallbacks (`${key:default}`).
  - Built-in `${eval:...}`, `${env:VAR, fallback}`, `${oc.env:...}`, `${oc.select:path, fallback}`, `${oc.decode:...}`, `${oc.dict.keys:...}`, and `${now:format}` resolvers.
  - Extensible custom C# resolvers (`RegisterResolver`) with nested interpolation support.
- **Structured Configs, Records, Dynamic Instantiation, Security Whitelists & Lifecycle Callbacks**:
  - Automatic `PascalCase` to `snake_case` (including acronym handling), `kebab-case`, and `camelCase` mapping for C# POCOs and positional `record` primary constructors.
  - Support for primitives, enums, `TimeSpan`, `DateTime`, `DateTimeOffset`, `DateOnly`, `TimeOnly`, `Guid`, `Uri`, arrays, lists, dictionaries, and recursive dynamic `_target_` instantiation with positional `_args_`, `_recursive_: false`, lazy `Func<T>` (`_partial_`) factories, and `ExecutionWhitelist` (`allowedTargets` / `ConfigSecurityException`) security policies.
  - Lifecycle callbacks (`IConfigCallback` / `ConfigCallback` / `hydra.callbacks`) for `OnComposeConfig`, `OnRunStart`/`OnRunEnd`, `OnMultiRunStart`/`OnMultiRunEnd`, and `OnJobStart`/`OnJobEnd`.
  - `[ConfigRequired]`, `[ConfigKey]`, `[ConfigIgnore]`, and `???` mandatory value marker validation with source file/line diagnostics.
- **Multi-Run Sweep Generators, Reproducibility Snapshots, Diffing & JSON Schema**:
  - `SweepEngine` (`-m` / `--multirun`) supporting Cartesian sweeps across comma-separated lists, `range()`, `linspace()` / `interval()`, `choice()`, and `glob()`.
  - `.WithOutputDirectory(...)` reproducibility manager writing `.omegaconf/config.yaml`, `.omegaconf/overrides.yaml`, and `multirun.yaml`.
  - `ConfigDiffer` for hierarchical configuration comparison (`+`, `-`, `~`).
  - `JsonSchemaGenerator` for Draft-07 IDE auto-complete schemas.
- **Microsoft.Extensions.Configuration & DI Integration**: `AddOmegaConf` and `ConfigureOmegaConf<T>` extension methods in `OmegaConf.NET.Extensions`.
- **Official Hydra Examples Suite (`samples/HydraExamples`)**: Complete C# port of the 34 official Python Hydra example projects (`tutorials/basic`, `tutorials/structured_configs`, `instantiate`, `patterns`, and `advanced`) verified for 100% output parity with Python Hydra.
