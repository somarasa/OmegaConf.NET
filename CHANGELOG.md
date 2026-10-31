# Changelog

All notable changes to **OmegaConf.NET** will be documented in this file.

The format is based on [Keep a Changelog](https://keepachangelog.com/en/1.1.0/),
and this project adheres to [Semantic Versioning](https://semver.org/spec/v2.0.0.html).

## [0.1.0] - 2026-10-31

### Added
- **Hierarchical Tree Composition**: Hydra-style `defaults:` lists, Config Groups, optional defaults (`optional group: file`), and `_self_` precedence control.
- **Multi-Level Auto-Cascading & Explicit Inheritance**: Automatic parent cascading across directory hierarchies (e.g. `db/postgres.yaml` -> `db/cloud/postgres.yaml` -> `db/cloud/aws/postgres.yaml`) and explicit `_inherits_` / `_base_` support across formats.
- **In-Config & CLI Mutation Operators**: Key-suffix and CLI operators `=`, `+=`, `-=`, `*=`, `/=`, `&=`, `|=`, and `!=` across numbers, strings, booleans, lists, and objects.
- **Multi-Format Parsing & Serialization**: Built-in support for YAML (`.yaml`, `.yml`), JSON / JSONC (`.json`, `.jsonc` with comments and trailing commas), and TOML (`.toml`), plus `.ToYaml()`, `.ToJson()`, and `.ToToml()` serialization.
- **Variable Interpolation & Resolvers**:
  - Native type preservation for single-variable interpolations (`${server.port}`).
  - List element indexing (`${servers[0]}` and `${servers.0}`).
  - Relative and sibling dot navigation (`${.sibling}` and `${..parent}`).
  - Literal escaping (`\${...}`) and default fallbacks (`${key:default}`).
  - Built-in `${eval:...}` math/logic expression evaluator and `${env:VAR, fallback}` resolver.
  - Extensible custom C# resolvers (`RegisterResolver`) with nested interpolation support.
- **Structured Configs & Strict Binding**:
  - Automatic `PascalCase` to `snake_case` (including acronym handling), `kebab-case`, and `camelCase` mapping for C# POCOs and positional `record` primary constructors.
  - Support for primitives, enums, `TimeSpan`, `DateTime`, `DateTimeOffset`, `DateOnly`, `TimeOnly`, `Guid`, `Uri`, arrays, lists, dictionaries, and dynamic `_target_` instantiation.
  - `[ConfigRequired]`, `[ConfigKey]`, `[ConfigIgnore]`, and `???` mandatory value marker validation with source file/line diagnostics.
- **Multi-Run Sweeps, Diffing & JSON Schema**:
  - `SweepEngine` (`-m` / `--multirun`) for Cartesian parameter sweeps.
  - `ConfigDiffer` for hierarchical configuration comparison (`+`, `-`, `~`).
  - `JsonSchemaGenerator` for Draft-07 IDE auto-complete schemas.
- **Microsoft.Extensions.Configuration & DI Integration**: `AddOmegaConf` and `ConfigureOmegaConf<T>` extension methods in `OmegaConf.NET.Extensions`.
