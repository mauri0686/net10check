namespace Net10Check;

public record ProjectReport(string Name, string Path, List<TfmInfo> Frameworks, int PackageCount);

public record Summary(int Projects, int EolNov2026, int AlreadyEol, int NetFramework, int PackagesToUpdate, int Blockers, string Effort, string EffortReason);

public record Report(string Target, DateTime GeneratedUtc, bool Offline, Summary Summary, List<ProjectReport> Projects,
                     List<PackageStatus> Packages, List<RuleHit> Findings, List<string> Blockers, string ServiceUrl);

public static class Analyzer
{
    // Landing page linked from the report footer and the NuGet package page.
    public const string ServiceUrl = "https://mauri0686.github.io/onlts/";

    public static async Task<Report> Build(string target, string root, List<ProjectInfo> projects, Func<string, string?, Task<List<PkgVersion>?>>? fetch)
    {
        var projectReports = projects.Select(p => new ProjectReport(
            p.Name, System.IO.Path.GetRelativePath(root, p.Path).Replace('\\', '/'),
            p.Tfms.Select(Frameworks.Classify).ToList(), p.Packages.Count)).ToList();

        // One row per (package, version) across projects.
        var groups = projects
            .SelectMany(p => p.Packages.Select(pkg => (pkg, project: p.Name)))
            .GroupBy(x => (Id: x.pkg.Id.ToLowerInvariant(), Version: x.pkg.Version))
            .ToList();
        var packages = await Task.WhenAll(groups.Select(async g =>
        {
            var id = g.First().pkg.Id;
            var projectsUsing = g.Select(x => x.project).Distinct().ToList();
            if (fetch is null) return Packages.Classify(id, g.Key.Version, null, "offline: not checked on nuget.org", projectsUsing);
            try
            {
                var versions = await fetch(id, g.Key.Version);
                return Packages.Classify(id, g.Key.Version, versions, versions is null ? "not on nuget.org (private feed?)" : null, projectsUsing);
            }
            catch (Exception)
            {
                return Packages.Classify(id, g.Key.Version, null, "nuget.org lookup failed (timeout or no network)", projectsUsing);
            }
        }));
        var packageList = packages.OrderBy(p => p.Id, StringComparer.OrdinalIgnoreCase).ToList();

        var findings = Rules.Scan(root, projects);

        var blockers = new List<string>();
        foreach (var p in projectReports.Where(p => p.Frameworks.Any(f => f.Status == TfmStatus.NetFramework)))
            blockers.Add($"{p.Name} targets .NET Framework: this is a port to modern .NET, not a TFM bump");
        foreach (var p in packageList.Where(p => p.NoNet10Version).DistinctBy(p => p.Id))
            blockers.Add($"{p.Id}: no stable version supports .NET Core / .NET 5+ (only .NET Framework dependency groups)");
        foreach (var p in packageList.Where(p => p.CappedBy != null).DistinctBy(p => p.Id))
            blockers.Add($"{p.Id}: no stable release works with the 10.x packages yet (latest {p.Target} depends on {p.CappedBy})"
                         + (p.Note?.StartsWith("prerelease") == true ? $"; {p.Note}" : ""));
        foreach (var f in findings.Where(f => f.Severity == Severity.Blocker))
            blockers.Add($"{f.Title} ({f.Locations.Count} place(s))");

        int eolNov = projectReports.Count(p => p.Frameworks.Any(f => f.Status == TfmStatus.EolNov2026));
        int already = projectReports.Count(p => p.Frameworks.Any(f => f.Status == TfmStatus.AlreadyEol));
        int netfx = projectReports.Count(p => p.Frameworks.Any(f => f.Status == TfmStatus.NetFramework));
        int toUpdate = packageList.Where(p => p.UpdateAvailable || p.BumpWithTfm).Select(p => p.Id.ToLowerInvariant()).Distinct().Count();
        int codeChanges = findings.Count(f => f.Severity == Severity.Warning);
        int toUpgrade = projectReports.Count(p => p.Frameworks.Any(f => f.Status is TfmStatus.EolNov2026 or TfmStatus.AlreadyEol or TfmStatus.NetFramework or TfmStatus.Unknown));
        bool efProviderBlocked = packageList.Any(p => p.CappedBy?.Contains("Microsoft.EntityFrameworkCore") == true);
        var (effort, reason) = Effort(projects.Count, toUpgrade, netfx, blockers.Count, codeChanges, toUpdate, efProviderBlocked);

        var summary = new Summary(projects.Count, eolNov, already, netfx, toUpdate, blockers.Count, effort, reason);
        return new Report(target, DateTime.UtcNow, fetch is null, summary, projectReports, packageList, findings, blockers, ServiceUrl);
    }

    // Simple, explainable thresholds. The reason string says which rule fired.
    public static (string Level, string Reason) Effort(int projects, int toUpgrade, int netfx, int blockers, int codeChanges, int packagesToUpdate,
                                                       bool efProviderBlocked = false)
    {
        if (toUpgrade == 0) return ("none", "no project targets an old framework; only routine package updates");
        if (netfx > 0) return ("high", $"{netfx} project(s) on .NET Framework need a port, not a TFM bump");
        if (efProviderBlocked) return ("high", "an EF Core provider/extension has no EF Core 10 release: switch it or wait for one");
        if (blockers >= 3) return ("high", $"{blockers} blockers to resolve before it builds on net10.0");
        var medium = new List<string>();
        if (blockers > 0) medium.Add($"{blockers} blocker(s)");
        if (codeChanges >= 3) medium.Add($"{codeChanges} kinds of code changes");
        if (packagesToUpdate > 25) medium.Add($"{packagesToUpdate} packages to update");
        if (projects > 15) medium.Add($"{projects} projects");
        if (medium.Count > 0) return ("medium", string.Join(", ", medium));
        return ("low", "TFM bump plus package updates, no known blockers");
    }
}
