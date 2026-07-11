# Contributing to TenantContextCache

First off, thank you for considering contributing to TenantContextCache! 🎉

This document provides guidelines and instructions for contributing to the project.

## Code of Conduct

We are committed to providing a welcoming and inspiring community for all. Please be respectful and constructive in your interactions with other contributors.

## How Can I Contribute?

### Reporting Bugs

Before creating a bug report, please check the [issue list](https://github.com/yourusername/TenantContextCache/issues) to see if the problem has already been reported.

When you create a bug report, include as many details as possible:

- **Description:** Clear and concise description of the bug
- **Steps to reproduce:** Numbered list of steps
- **Expected behavior:** What you expected to happen
- **Actual behavior:** What actually happened
- **Screenshots:** If applicable
- **Environment:** 
  - OS and .NET version
  - TenantContextCache version
  - Redis version (if applicable)

### Suggesting Enhancements

Enhancement suggestions are tracked as [GitHub Issues](https://github.com/yourusername/TenantContextCache/issues).

When creating an enhancement suggestion:

- **Use a clear title** describing the feature
- **Provide a step-by-step description** of the suggested feature
- **Provide specific examples** to demonstrate the feature
- **Describe the current behavior** and explain the expected behavior
- **Explain why this enhancement would be useful**

### Pull Requests

**Process:**

1. **Fork** the repository
2. **Create a branch** for your changes (`git checkout -b feature/amazing-feature`)
3. **Commit your changes** with clear messages (`git commit -m 'Add amazing feature'`)
4. **Push to the branch** (`git push origin feature/amazing-feature`)
5. **Open a Pull Request** with a clear title and description

**Requirements:**

- All tests must pass (`dotnet test`)
- Code must follow the project's style guidelines
- Add/update unit tests for new functionality
- Update documentation if needed
- Squash commits into logical units

## Development Setup

### Prerequisites

- .NET 8.0 SDK or later
- Git
- Visual Studio 2022, VS Code, or JetBrains Rider
- Docker (for Redis testing)

### Getting Started

```bash
# Clone the repository
git clone https://github.com/yourusername/TenantContextCache.git
cd TenantContextCache

# Restore dependencies
dotnet restore

# Build the project
dotnet build

# Run tests
dotnet test

# Run examples
cd examples/TenantContextCache.Examples
dotnet run
```

### Running Tests

```bash
# Run all tests
dotnet test

# Run with verbose output
dotnet test -v detailed

# Run specific test class
dotnet test --filter "FullyQualifiedName~TenantContextCache.Tests.CacheTests"

# Run with code coverage
dotnet test /p:CollectCoverage=true /p:CoverageFormat=opencover
```

## Code Style Guidelines

### C# Conventions

- **Naming:** Use PascalCase for public members, camelCase for private/local
- **Indentation:** 4 spaces (no tabs)
- **Line length:** Aim for 100 characters, max 120
- **Braces:** Opening brace on same line (Allman style)
- **Comments:** Use `///` for XML documentation, `//` for inline comments

### Example

```csharp
/// <summary>
/// Gets or sets the tenant ID.
/// </summary>
public string TenantId { get; set; }

/// <summary>
/// Resolves the tenant from the HTTP context.
/// </summary>
/// <param name="httpContext">The HTTP context</param>
/// <returns>The resolved tenant ID or null</returns>
public string ResolveTenant(HttpContext httpContext)
{
    // Implementation here
    return tenantId;
}
```

### Async/Await

- Always use `async`/`await` for I/O operations
- Name async methods with `Async` suffix
- Use `Task` instead of `void` for async methods

```csharp
public async Task<TenantInfo> GetTenantAsync(string tenantId)
{
    return await _cache.GetAsync<TenantInfo>(tenantId);
}
```

### Null Handling

- Use nullable reference types (`#nullable enable`)
- Check for null before use
- Use null-coalescing operator (`??`) where appropriate

```csharp
public string GetDisplayName(TenantInfo? tenant)
{
    return tenant?.Name ?? "Unknown";
}
```

## Documentation

### Code Comments

- Write comments that explain **why**, not **what**
- Keep comments up-to-date with code changes
- Use XML documentation for public APIs

### Documentation Files

- Update [docs/](./docs) folder for user-facing changes
- Update [CHANGELOG.md](./CHANGELOG.md) for new features/fixes
- Add examples for new features

## Testing Guidelines

### Unit Test Structure

```csharp
[Test]
public async Task MethodName_Scenario_ExpectedResult()
{
    // Arrange
    var cache = TestCacheFactory.CreateTenantContextCache();
    var testValue = "test-data";

    // Act
    await cache.SetAsync("acme", "key", testValue);
    var result = await cache.GetAsync<string>("acme", "key");

    // Assert
    result.Should().Be(testValue);
}
```

`TestCacheFactory` builds a real FusionCache-backed `TenantContextCache`; pass an
`InProcessDistributedCache` to exercise the L2 layer. See the existing tests for
examples.

### Test Requirements

- **Naming:** `MethodName_Scenario_ExpectedResult`
- **Arrange-Act-Assert:** Clear structure with comments
- **One assertion per test:** Multiple assertions only when testing related behavior
- **Use mocks:** For external dependencies
- **Coverage goal:** > 85% code coverage

### Running Tests

```bash
# Run all tests
dotnet test

# Run with coverage
dotnet test /p:CollectCoverage=true

# Watch mode (requires watchdog)
dotnet watch test
```

## Commit Messages

Use clear, concise commit messages:

```
feat: Add multi-pattern tenant resolution
fix: Resolve null reference exception in middleware
docs: Update installation guide
test: Add cache invalidation tests
chore: Update dependencies
refactor: Simplify cache key generation
```

Format: `<type>: <subject>`

**Types:**
- `feat:` New feature
- `fix:` Bug fix
- `docs:` Documentation
- `test:` Tests
- `refactor:` Code refactoring
- `chore:` Build, dependencies, tooling
- `perf:` Performance improvements

## Pull Request Process

1. **Ensure tests pass** locally
2. **Update documentation** if needed
3. **Add entry to CHANGELOG.md**
4. **Create clear PR description:**
   - What changes are made
   - Why they're needed
   - How to test them
5. **Address review feedback** promptly
6. **Maintain conversation** with reviewers

### PR Title Format

```
[Type] Brief description

Example:
[Feature] Add multi-pattern tenant resolution
[Fix] Resolve cache key collision bug
[Docs] Update installation guide
```

## Release Process

Releases follow [Semantic Versioning](https://semver.org/):

- **MAJOR:** Incompatible API changes
- **MINOR:** Backward-compatible features
- **PATCH:** Backward-compatible fixes

## Areas Needing Help

- **Cache backends:** Memcached, RavenDB implementations
- **Performance:** Optimization opportunities
- **Documentation:** More examples, tutorials
- **Integrations:** OpenTelemetry, Application Insights
- **Languages:** Java, Go, Node.js implementations

## Questions?

- **Documentation:** [/docs](./docs)
- **Issues:** [GitHub Issues](https://github.com/yourusername/TenantContextCache/issues)
- **Discussions:** [GitHub Discussions](https://github.com/yourusername/TenantContextCache/discussions)
- **Email:** support@example.com

## Recognition

Contributors will be recognized in:
- [CONTRIBUTORS.md](./CONTRIBUTORS.md)
- Release notes
- GitHub contributors page

Thank you for contributing! 🚀
