using System.Text.Json;
using System.Text.RegularExpressions;

namespace Net10Check;

public enum Severity { Blocker, Warning, Review }

public record RuleHit(string Id, string Title, Severity Severity, string Fix, string Doc, List<string> Locations);

// Curated .NET 10 breaking changes that a text search finds with few false positives.
// Source: https://learn.microsoft.com/dotnet/core/compatibility/10 and the ASP.NET Core / EF Core 10 pages it links.
public static class Rules
{
    const string Core = "https://learn.microsoft.com/dotnet/core/compatibility/";
    const string Asp = "https://learn.microsoft.com/aspnet/core/breaking-changes/10/";

    record CodeRule(string Id, string Title, Severity Severity, string Fix, string Doc, Regex Pattern);

    static CodeRule Code(string id, string title, Severity sev, string fix, string doc, string pattern) =>
        new(id, title, sev, fix, doc, new Regex(pattern, RegexOptions.Compiled));

    static readonly CodeRule[] CodeRules =
    [
        Code("ASPDEPR004", "WebHostBuilder / IWebHost / WebHost are obsolete", Severity.Warning,
            "Move to WebApplication.CreateBuilder or HostBuilder.ConfigureWebHost.", Asp + "webhostbuilder-deprecated",
            @"\bnew\s+WebHostBuilder\s*\(|\bWebHost\s*\.\s*(CreateDefaultBuilder|Start|StartWith)\s*\(|\bIWebHost\b"),
        Code("ASPDEPR002", "WithOpenApi() is deprecated", Severity.Warning,
            "Remove .WithOpenApi(); use AddOpenApiOperationTransformer (or Swashbuckle/NSwag filters).", Asp + "withopenapi-deprecated",
            @"\.WithOpenApi\s*\("),
        Code("ASPDEPR003", "Razor runtime compilation is obsolete", Severity.Warning,
            "Remove AddRazorRuntimeCompilation; use Hot Reload in development.", Asp + "razor-runtime-compilation-obsolete",
            @"\bAddRazorRuntimeCompilation\s*\("),
        Code("ASPDEPR005", "ForwardedHeadersOptions.KnownNetworks is obsolete", Severity.Warning,
            "Use KnownIPNetworks with System.Net.IPNetwork.", Asp + "ipnetwork-knownnetworks-obsolete",
            @"\bKnownNetworks\b"),
        Code("ASPDEPR006", "IActionContextAccessor / ActionContextAccessor are obsolete", Severity.Warning,
            "Use IHttpContextAccessor and HttpContext.GetEndpoint().", Asp + "iactioncontextaccessor-obsolete",
            @"\bI?ActionContextAccessor\b"),
        Code("SYSLIB0060", "Rfc2898DeriveBytes constructors are obsolete", Severity.Warning,
            "Use the static Rfc2898DeriveBytes.Pbkdf2 method.", Core + "core-libraries/10.0/obsolete-apis",
            @"\bnew\s+Rfc2898DeriveBytes\s*\("),
        Code("OPENAPI2", "Code uses OpenAPI.NET 1.x types (Microsoft.OpenApi.Models / OpenApiAny)", Severity.Warning,
            "Microsoft.AspNetCore.OpenApi 10 and Swashbuckle 10 ship OpenAPI.NET 2.0: namespaces move to Microsoft.OpenApi, OpenApiAny becomes JsonNode, entities become interfaces.",
            "https://learn.microsoft.com/aspnet/core/release-notes/aspnetcore-10.0#openapi-31-breaking-changes",
            @"\bMicrosoft\.OpenApi\.(Models|Any)\b|\bnew\s+OpenApi(String|Integer|Long|Double|Boolean|Object|Array)\s*\("),
        Code("EF10-SETTERS", "ExecuteUpdate setters built as expression trees (SetPropertyCalls<T>)", Severity.Blocker,
            "EF Core 10 takes a plain lambda for ExecuteUpdate setters; code that builds Expression<Func<SetPropertyCalls<T>, ...>> no longer compiles.",
            "https://learn.microsoft.com/ef/core/what-is-new/ef-core-10.0/breaking-changes",
            @"\bSetPropertyCalls\s*<"),
    ];

    public static List<RuleHit> Scan(string root, List<ProjectInfo> projects)
    {
        var hits = new Dictionary<string, RuleHit>();
        void Add(string id, string title, Severity sev, string fix, string doc, string location)
        {
            var key = id + "|" + sev;
            if (!hits.TryGetValue(key, out var hit)) hits[key] = hit = new RuleHit(id, title, sev, fix, doc, []);
            hit.Locations.Add(location);
        }
        string Rel(string path) => Path.GetRelativePath(root, path).Replace('\\', '/');

        // Code: files under each project folder (and the root when scanning a folder).
        var dirs = projects.Select(p => Path.GetDirectoryName(p.Path)!).Append(root).Distinct().OrderBy(d => d.Length).ToList();
        dirs = dirs.Where((d, i) => !dirs.Take(i).Any(parent => d.StartsWith(parent + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))).ToList();
        var files = dirs.SelectMany(d => ProjectScanner.EnumerateFiles(d, "*.cs")).ToList();
        foreach (var file in files)
        {
            // TreatWarningsAsErrors turns the obsoletion warnings into build errors.
            var owner = projects.Where(p => file.StartsWith(Path.GetDirectoryName(p.Path)! + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                                .MaxBy(p => p.Path.Length);
            bool warnAsError = owner?.Prop("TreatWarningsAsErrors") == true;
            var lines = File.ReadAllLines(file);
            for (int i = 0; i < lines.Length; i++)
            {
                var line = lines[i].TrimStart();
                if (line.StartsWith("//") || line.StartsWith("*") || line.StartsWith("/*")) continue;
                foreach (var r in CodeRules)
                    if (r.Pattern.IsMatch(line))
                    {
                        var sev = warnAsError && r.Severity == Severity.Warning ? Severity.Blocker : r.Severity;
                        var title = sev != r.Severity ? r.Title + " (TreatWarningsAsErrors: build error)" : r.Title;
                        Add(r.Id, title, sev, r.Fix, r.Doc, $"{Rel(file)}:{i + 1}");
                    }
            }
        }

        // Project files
        foreach (var p in projects)
        {
            var where = Rel(p.Path);
            bool Has(string id) => p.Packages.Any(x => x.Id.Equals(id, StringComparison.OrdinalIgnoreCase));

            if (!p.CentralPackageManagement)
                foreach (var pkg in p.Packages.Where(x => x.Version == null))
                    Add("NU1015", "PackageReference without a version is an error", Severity.Blocker,
                        "The .NET 10 SDK raises error NU1015 (was warning NU1604). Add a Version.",
                        Core + "sdk/10.0/nu1015-packagereference-version", $"{where} ({pkg.Id})");

            var linqAsync = p.Packages.FirstOrDefault(x => x.Id.Equals("System.Linq.Async", StringComparison.OrdinalIgnoreCase));
            if (linqAsync != null && (NuGetVersion.Major(linqAsync.Version) ?? 0) < 7)
                Add("LINQ-ASYNC", "System.Linq.Async clashes with the built-in System.Linq.AsyncEnumerable", Severity.Warning,
                    "Remove the reference (or move to 7.0.0); expect ambiguous-call errors otherwise.",
                    Core + "core-libraries/10.0/asyncenumerable", where);

            if (Has("Microsoft.Extensions.ApiDescription.Client"))
                Add("APIDESC-CLIENT", "Microsoft.Extensions.ApiDescription.Client is deprecated", Severity.Warning,
                    "Replace OpenApiReference / dotnet openapi with NSwag or Kiota tooling.", Asp + "apidescription-client-deprecated", where);

            if (p.Prop("IncludeOpenAPIAnalyzers"))
                Add("ASPDEPR007", "IncludeOpenAPIAnalyzers is deprecated", Severity.Warning,
                    "Remove the property; prefer TypedResults.", Asp + "openapi-analyzers-deprecated", where);

            if (p.Properties.ContainsKey("BlazorCacheBootResources"))
                Add("BLAZOR-CACHE", "BlazorCacheBootResources no longer has any effect", Severity.Review,
                    "Remove the property.", "https://learn.microsoft.com/aspnet/core/migration/90-to-100", where);

            if (p.Tfms.Count > 1 && (Has("Microsoft.EntityFrameworkCore.Design") || Has("Microsoft.EntityFrameworkCore.Tools")))
                Add("EF10-TOOLS", "dotnet ef needs --framework on multi-targeted projects", Severity.Warning,
                    "Pass --framework to every dotnet ef command (scripts, CI).",
                    "https://learn.microsoft.com/ef/core/what-is-new/ef-core-10.0/breaking-changes", where);

            if (p.Packages.Any(x => (x.Id.StartsWith("Microsoft.Data.Sqlite", StringComparison.OrdinalIgnoreCase)
                                  || x.Id.StartsWith("Microsoft.EntityFrameworkCore.Sqlite", StringComparison.OrdinalIgnoreCase))
                                 && NuGetVersion.Major(x.Version) is null or < 10))
                Add("SQLITE-DATES", "Microsoft.Data.Sqlite 10 reads and writes DateTime/DateTimeOffset as UTC", Severity.Review,
                    "Check code that reads timestamps without offset; AppContext switch Microsoft.Data.Sqlite.Pre10TimeZoneHandling restores the old behavior.",
                    "https://learn.microsoft.com/ef/core/what-is-new/ef-core-10.0/breaking-changes#microsoftdatasqlite-breaking-changes", where);
        }

        // Dockerfiles
        var dockerFrom = new Regex(@"^\s*FROM\s+mcr\.microsoft\.com/dotnet/(sdk|aspnet|runtime|runtime-deps):(8|9)\.0(\S*)", RegexOptions.IgnoreCase);
        foreach (var file in ProjectScanner.EnumerateFiles(root, "*").Where(f => Path.GetFileName(f).StartsWith("Dockerfile", StringComparison.OrdinalIgnoreCase)
                                                                              || f.EndsWith(".dockerfile", StringComparison.OrdinalIgnoreCase)))
        {
            var lines = File.ReadAllLines(file);
            for (int i = 0; i < lines.Length; i++)
            {
                var m = dockerFrom.Match(lines[i]);
                if (!m.Success) continue;
                var tag = m.Groups[3].Value.ToLowerInvariant();
                if (tag.Contains("bookworm") || tag.Contains("bullseye"))
                    Add("DOCKER-DEBIAN", "Debian-based .NET images are not published for .NET 10", Severity.Blocker,
                        "Switch the base image to 10.0 (Ubuntu 24.04), 10.0-noble, 10.0-alpine or 10.0-azurelinux3.0 and check apt/package names.",
                        Core + "containers/10.0/default-images-use-ubuntu", $"{Rel(file)}:{i + 1}");
                else if (tag == "")
                    Add("DOCKER-UBUNTU", "Default .NET image tags move from Debian to Ubuntu", Severity.Review,
                        "When you change the tag to 10.0 the base OS becomes Ubuntu 24.04; check apt packages and users.",
                        Core + "containers/10.0/default-images-use-ubuntu", $"{Rel(file)}:{i + 1}");
            }
        }

        // global.json pinning an older SDK
        foreach (var file in ProjectScanner.EnumerateFiles(root, "global.json"))
        {
            try
            {
                using var doc = JsonDocument.Parse(File.ReadAllText(file), new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true });
                if (!doc.RootElement.TryGetProperty("sdk", out var sdk) || !sdk.TryGetProperty("version", out var v)) continue;
                var rollForward = sdk.TryGetProperty("rollForward", out var rf) ? rf.GetString() : null;
                if (NuGetVersion.Major(v.GetString()) < 10 && rollForward != "latestMajor")
                    Add("GLOBAL-JSON", $"global.json pins SDK {v.GetString()}", Severity.Warning,
                        "Building net10.0 needs the .NET 10 SDK: update sdk.version.", "https://learn.microsoft.com/aspnet/core/migration/90-to-100", Rel(file));
            }
            catch (JsonException) { }
        }

        return hits.Values.OrderBy(h => h.Severity).ThenBy(h => h.Id).ToList();
    }
}
