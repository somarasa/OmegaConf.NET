# AGENTS.md — AI Agent Guidelines & Architecture Manual

> **Scope**: This document defines the operating rules, architectural standards, and workflow directives for autonomous AI coding agents (Antigravity, Cursor, Copilot, Claude Code, etc.) contributing to or working within the **OmegaConf.NET** codebase.

---

## 1. Project Overview & Repository Blueprint

**OmegaConf.NET** is an open-source, hierarchical, composable configuration framework for C# and .NET inspired by Python's Hydra and OmegaConf.

### Solution Structure
```text
c#omegaconf/
├── OmegaConf.NET.slnx                   # Solution file
├── Directory.Build.props               # Global build properties (LangVersion, UseSharedCompilation)
├── LICENSE                             # MIT License
├── README.md                           # Human-facing documentation
├── AGENTS.md                           # AI Agent operating guide (this file)
├── skills/
│   └── omegaconf-net/SKILL.md          # Agent skill for consuming the framework
├── .github/
│   └── workflows/
│       ├── ci.yml                      # Matrix CI (Ubuntu, Windows, macOS across .NET 8, 9, 10)
│       └── publish.yml                 # Automated NuGet publishing
├── src/
│   ├── OmegaConf.NET/                  # Core library (AST, Parsers, Operators, Resolvers, Binder)
│   └── OmegaConf.NET.Extensions/       # Microsoft.Extensions.Configuration & DI provider
├── tests/
│   └── OmegaConf.NET.Tests/            # Comprehensive unit and integration test suite
└── samples/
    └── QuickStart/                     # Runnable sample console application
```

---

## 2. Agent Operating Directives & Build Rules

1. **Multi-Targeting Mandate**:
   - Both `src/OmegaConf.NET` and `src/OmegaConf.NET.Extensions` target:
     ```xml
     <TargetFrameworks>net8.0;net9.0;net10.0</TargetFrameworks>
     ```
   - Any new code, API, or dependency must compile cleanly across all three frameworks without conditional compilation `#if` unless strictly required for runtime optimization.

2. **Single-Node MSBuild Rule (`-m:1`)**:
   - When running MSBuild, `dotnet build`, or `dotnet test`, **always pass `-m:1`**:
     ```bash
     dotnet build -m:1
     dotnet test -m:1 --no-restore
     ```
   - *Reason*: In sandboxed execution environments, inter-process named pipe communication used by Roslyn compiler servers (`VBCSCompiler`) or multi-proc node reuse can be restricted. Passing `-m:1` guarantees 100% deterministic, self-contained execution.

3. **Zero Warning Standard**:
   - `<Nullable>enable</Nullable>` is active.
   - All code must build with **0 errors and 0 warnings**. Treat compiler warnings as errors.

4. **Test-First Requirement**:
   - Any new operator, syntax format, resolver, or binding feature must include automated tests in `tests/OmegaConf.NET.Tests/`.
   - Never consider a task complete without running and passing the full test suite.

---

## 3. Core Architecture & Design Patterns

### AST Model (`src/OmegaConf.NET/Ast/`)
- All configurations parse into an immutable or state-preserving **Abstract Syntax Tree (AST)** before resolution:
  - `ConfigNode`: Base class tracking `SourceInfo` (`FilePath`, `Line`, `Column`) and `Operator`.
  - `ConfigValueNode`: Stores scalar values (`Value`), tracks `IsMissingMarker` (`???`), and preserves `RawTemplate` (e.g. `"${db.host}:${db.port}"`) for diagnostics and future write-back.
  - `ConfigListNode`: Represents sequences/arrays.
  - `ConfigObjectNode`: Case-insensitive (`OrdinalIgnoreCase`) dictionary representing mappings.

### Mutation Operators Engine (`src/OmegaConf.NET/Operators/`)
- Expressed as **key suffixes** in files (e.g. `timeout+=: 10`, `scale*=: 2`, `debug!=: true`):
  - `=`: Direct assignment / object merge.
  - `+=`: Numeric addition, string concatenation, list append, object merge.
  - `-=`: Numeric subtraction, list item removal by value.
  - `*=`: Numeric multiplication, string repetition (`"ha"*=: 3` -> `"hahaha"`).
  - `/=`: Numeric division (with zero-division checks).
  - `&=`: Logical AND (booleans and integers).
  - `|=`: Logical OR (booleans and integers).
  - `!=`: Boolean toggle / invert.
- Always use `OperatorParser.ParseKey(...)` to extract the operator and clean key.

### Composition & Cascading (`src/OmegaConf.NET/Composition/`)
- **Config Groups**: Subdirectories under the root config directory (e.g. `db/`, `model/`, `env/`).
- **Defaults List**: Top-level `defaults:` array in `config.yaml`.
- **Precedence with `_self_`**:
  - `_self_` at the end: Local file overrides defaults.
  - `_self_` at the start: Defaults override local file.
- **Auto-Cascading**: If a sub-config shares a filename with its parent folder (e.g. `db/staging/postgres.yaml` and `db/postgres.yaml`), the parent base is loaded automatically first.
- **Explicit Inheritance**: `_inherits_: "../base.yaml"` or lists of files.
- **Cycle Prevention**: Circular dependencies throw `ConfigInheritanceException`.

### Variable Interpolation (`src/OmegaConf.NET/Interpolation/`)
- **Native Type Preservation**: Pure single-variable interpolations `${port}` preserve `int`/`bool` types.
- **Relative Dot Navigation**:
  - `.sibling`: Resolves in the same enclosing object container.
  - `..parent`: Resolves one container level up.
- **Literal Escaping**: `\${...}` is preserved as unescaped `${...}` and not evaluated.
- **Resolvers**:
  - `${env:VAR, fallback}`: Environment variables.
  - `${eval: 10 * 20}`: Built-in math, logic, comparison, and parentheses evaluator.
  - Custom resolvers registered via `builder.RegisterResolver(...)`.
- **Cycle Prevention**: Circular references throw `ConfigInterpolationException`.

### Structured Configs & Binding (`src/OmegaConf.NET/Binding/`)
- **Name Resolution**: Automatic mapping between C# `PascalCase` and config `snake_case`, `kebab-case`, `camelCase`, or exact names.
- **Type Support**: Primitives, enums (case-insensitive), `TimeSpan`, `Guid`, `Nullable<T>`, arrays (`T[]`), `List<T>`, `Dictionary<string, T>`, and nested POCOs/records.
- **Attributes**:
  - `[ConfigRequired]`: Throws `MissingRequiredConfigException` if property is absent or `???`.
  - `[ConfigKey("custom")]`: Custom key alias.
  - `[ConfigIgnore]`: Skips property during binding.
- **Dynamic Instantiation**: `_target_: "Namespace.Type, Assembly"` allows dynamic instantiation via reflection.

---

## 4. Multi-Run & Tooling (`MultiRun/`, `Diff/`, `Schema/`)

1. **Multi-Run / Sweeps (`SweepEngine`)**:
   - Detects `-m` or `--multirun` in CLI args.
   - Computes Cartesian product of comma-separated arguments (e.g. `db=stage,prod workers=2,4`).
   - Generates all permutations via `.BuildSweeps<T>()` and `.BuildSweepNodes()`.

2. **Config Differ (`ConfigDiffer`)**:
   - Hierarchically compares two nodes and reports additions (`+`), removals (`-`), and modifications (`~`).

3. **JSON Schema Generator (`JsonSchemaGenerator`)**:
   - Generates Draft-07 JSON schemas for IDE auto-complete.

---

## 5. Documentation & Commit Protocol

When modifying this repository, AI agents must:
1. **Synchronize Documentation**:
   - Keep `README.md`, `skills/omegaconf-net/SKILL.md`, and `AGENTS.md` synchronized whenever features, syntax, or APIs change.
2. **Conventional Commits**:
   - Use standard prefixes:
     - `feat:` for new capabilities.
     - `fix:` for bug fixes.
     - `test:` for test additions or updates.
     - `docs:` for documentation updates.
     - `refactor:` for internal restructuring without behavior changes.
3. **Verify Everything**:
   - Run `dotnet test -m:1 --no-restore` across all targets before committing.
