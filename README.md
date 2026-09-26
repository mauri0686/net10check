# net10-check

[![NuGet](https://img.shields.io/nuget/v/Net10Check.svg)](https://www.nuget.org/packages/Net10Check) [![Downloads](https://img.shields.io/nuget/dt/Net10Check.svg)](https://www.nuget.org/packages/Net10Check)

.NET 8 and .NET 9 both reach end of support on **November 10, 2026**. `net10-check` scans your solution and tells you, in a few seconds, what stands between it and .NET 10.

```
dotnet tool install --global Net10Check
net10-check path/to/YourApp.sln
```

It runs on any machine with the .NET 8, 9 or 10 SDK. It never builds or changes your code.

## What it reports

- **Target frameworks** of every project and their end-of-support date (net8.0 and net9.0: 2026-11-10; net6.0/net7.0: already out of support; .NET Framework: flagged as a port, not a TFM bump; netstandard: fine as is).
- **NuGet packages** (PackageReference, Central Package Management, `Directory.Build.props`, `packages.config`), checked against nuget.org:
  - current version and the newest stable version that works on net10.0
  - `Microsoft.AspNetCore.*`, `Microsoft.EntityFrameworkCore.*`, `Microsoft.Extensions.*` and `System.*` packages on 8.x/9.x that must move to 10.x with the TFM
  - deprecated packages and versions with known vulnerabilities
  - packages with no version that supports modern .NET (blocker)
  - packages whose newest release still pins EF Core, ASP.NET Core or Microsoft.Extensions below 10.x, like an EF Core provider without a 10.x release (blocker)
- **A short list of .NET 10 breaking changes** that a text search finds reliably, each linked to the official Microsoft docs:

| Id | What |
|---|---|
| ASPDEPR002 | `WithOpenApi()` deprecated |
| ASPDEPR003 | Razor runtime compilation obsolete |
| ASPDEPR004 | `WebHostBuilder`, `IWebHost`, `WebHost` obsolete |
| ASPDEPR005 | `ForwardedHeadersOptions.KnownNetworks` obsolete |
| ASPDEPR006 | `IActionContextAccessor` obsolete |
| ASPDEPR007 | `IncludeOpenAPIAnalyzers` deprecated |
| SYSLIB0060 | `Rfc2898DeriveBytes` constructors obsolete |
| OPENAPI2 | OpenAPI.NET 2.0 (`Microsoft.OpenApi.Models`, `OpenApiAny`) used by Microsoft.AspNetCore.OpenApi 10 and Swashbuckle 10 |
| EF10-SETTERS | `ExecuteUpdate` setters built as expression trees no longer compile |
| EF10-TOOLS | `dotnet ef` needs `--framework` on multi-targeted projects |
| NU1015 | `PackageReference` without a version is now an error |
| LINQ-ASYNC | `System.Linq.Async` clashes with the new built-in `System.Linq.AsyncEnumerable` |
| APIDESC-CLIENT | `Microsoft.Extensions.ApiDescription.Client` deprecated |
| SQLITE-DATES | Microsoft.Data.Sqlite 10 handles dates as UTC |
| BLAZOR-CACHE | `BlazorCacheBootResources` removed |
| DOCKER-DEBIAN / DOCKER-UBUNTU | no Debian images for .NET 10; default tags are Ubuntu |
| GLOBAL-JSON | `global.json` pins an older SDK |

Obsoletion warnings are reported as blockers when the project sets `TreatWarningsAsErrors`.

The summary at the top gives the number of projects, how many reach end of support on 2026-11-10, packages to update, blockers, and an effort estimate:

- **none**: every project already targets .NET 10 or later
- **high**: any .NET Framework project, an EF Core provider or extension with no EF Core 10 release, or 3+ blockers
- **medium**: 1-2 blockers, 3+ kinds of code changes, more than 25 packages to update, or more than 15 projects
- **low**: everything else (TFM bump plus package updates)

## Options

```
net10-check [path] [--format console|md|json] [-o|--output file] [--offline] [--fail-on-eol]
```

- `path`: `.sln`, `.slnx`, project file or folder (default: current folder; a folder with one solution uses that solution).
- `--format md|json` with `--output report.md` writes a shareable report.
- `--offline` skips nuget.org.
- `--fail-on-eol` exits with code 1 when a project targets a framework that is out of support by 2026-11-10. Handy in CI.

## Use it in CI

Fail the build while any project still targets a framework that loses support on 2026-11-10 (GitHub Actions example):

```yaml
- uses: actions/setup-dotnet@v4
  with:
    dotnet-version: '8.0.x'
- run: dotnet tool install -g Net10Check
- run: net10-check --fail-on-eol --format md --output net10-report.md
```

## Limits

The scan reads project files as XML; it does not run MSBuild. Conditions are mostly ignored, private feeds are not queried, and transitive packages are not listed. Breaking changes that only show up at runtime are not detected: run your tests on .NET 10.

If the report is long and you would rather hand the upgrade off, fixed-price .NET 10 upgrades are available at [OnLTS](https://mauri0686.github.io/onlts/).

License: MIT
