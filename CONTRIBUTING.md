# Contributing to OmegaConf.NET

Thank you for your interest in contributing to **OmegaConf.NET**!

## Development Prerequisites

- **.NET SDK**: .NET 8.0, .NET 9.0, and .NET 10.0 SDKs installed.
- **IDE**: Visual Studio 2022+, JetBrains Rider, or VS Code with C# Dev Kit.

## Building & Running Tests

Because the project multi-targets `net8.0`, `net9.0`, and `net10.0`, always verify builds and tests across all target frameworks:

```bash
# Restore dependencies
dotnet restore

# Build with 0 warnings and 0 errors
dotnet build -m:1 --no-restore

# Run unit tests
dotnet test -m:1 --no-restore
```

## Coding Standards

1. **Zero Warnings**: `<Nullable>enable</Nullable>` is enforced across all projects. New code must compile with 0 warnings.
2. **Formatting**: Follow the repository's [`.editorconfig`](.editorconfig) (4 spaces for C#, 2 spaces for YAML/JSON, Allman braces).
3. **Tests Required**: Every bug fix, operator, resolver, or binding enhancement must include unit tests in `tests/OmegaConf.NET.Tests/`.
4. **Documentation**: Keep `README.md`, `AGENTS.md`, and `skills/omegaconf-net/SKILL.md` updated when adding or modifying user-facing features.
5. **Commit Messages**: Use Conventional Commits (`feat:`, `fix:`, `test:`, `docs:`, `refactor:`).
