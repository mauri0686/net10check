using System.Text.RegularExpressions;

namespace Net10Check;

public record PackageStatus(
    string Id,
    string? Current,
    string? Target,              // newest stable version whose dependency groups work on net10.0
    bool UpdateAvailable,
    bool BumpWithTfm,            // Microsoft.*/System.* on 8.x/9.x that ships a 10.x line
    bool NoNet10Version,         // no stable version works on net10.0: blocker
    string? Deprecation,
    List<string> Vulnerabilities,
    string? Note,
    List<string> Projects,
    string? CappedBy = null)     // target still pins Microsoft.* below 10.x (e.g. EF provider without an EF Core 10 release): blocker
{
    public bool NeedsAction => UpdateAvailable || BumpWithTfm || NoNet10Version || CappedBy != null || Deprecation != null || Vulnerabilities.Count > 0 || Current == null;
}

public static class Packages
{
    // Packages versioned with the runtime (8.x for net8.0, 10.x for net10.0).
    static readonly string[] PlatformPrefixes = ["Microsoft.AspNetCore.", "Microsoft.EntityFrameworkCore", "Microsoft.Extensions.", "System."];

    public static bool IsPlatform(string id) => PlatformPrefixes.Any(p => id.StartsWith(p, StringComparison.OrdinalIgnoreCase));

    // A dependency group works on net10.0 if it is netstandard, netcoreapp, net5.0..net10.0 (any platform suffix) or has no TFM.
    public static bool SupportsNet10(List<string> frameworks)
    {
        if (frameworks.Count == 0) return true;
        foreach (var raw in frameworks)
        {
            var f = raw.ToLowerInvariant().TrimStart('.');
            if (f is "any" or "" || f.StartsWith("netstandard") || f.StartsWith("netcoreapp")) return true;
            var m = Regex.Match(f, @"^net(\d+)\.\d+");
            if (m.Success && int.Parse(m.Groups[1].Value) is >= 5 and <= 10) return true;
        }
        return false;
    }

    // versions == null: offline or not found; note says which.
    public static PackageStatus Classify(string id, string? current, List<PkgVersion>? versions, string? note, List<string> projects)
    {
        int? curMajor = NuGetVersion.Major(current);
        bool platformOnOldLine = IsPlatform(id) && curMajor is 8 or 9;

        if (versions is null)
            return new(id, current, null, false, platformOnOldLine, false, null, [], note ?? "not checked", projects);

        var stable = versions.Where(v => v.Listed && !NuGetVersion.Normalize(v.Version).Contains('-'))
                             .OrderByDescending(v => v.Version, Comparer<string>.Create(NuGetVersion.Compare)).ToList();
        var best = stable.FirstOrDefault(v => SupportsNet10(v.Frameworks));
        // Dependency groups describe dependencies, not assets. Only .NET Framework groups with no dependencies at all is
        // typical of build-time packages (MSBuild targets, tools): not enough to call it a blocker.
        if (best == null && stable.Count > 0 && stable[0].DependencyCount == 0)
        {
            best = stable[0];
            note ??= "declares only .NET Framework (no dependencies): fine for build-time packages, verify otherwise";
        }
        bool capped = best?.CappedPlatformDeps is { Count: > 0 };
        if (capped)
        {
            var pre = versions.Where(v => v.Listed && NuGetVersion.Normalize(v.Version).Contains('-') && NuGetVersion.Compare(v.Version, best!.Version) > 0
                                          && SupportsNet10(v.Frameworks) && v.CappedPlatformDeps is not { Count: > 0 })
                              .OrderByDescending(v => v.Version, Comparer<string>.Create(NuGetVersion.Compare)).FirstOrDefault();
            if (pre != null) note = $"prerelease {pre.Version} lifts the cap";
        }
        var mine = current == null ? null : versions.FirstOrDefault(v => NuGetVersion.Compare(v.Version, current) == 0);
        var target = best?.Version;

        return new(
            id, current, target,
            UpdateAvailable: target != null && current != null && NuGetVersion.Compare(current, target) < 0,
            BumpWithTfm: platformOnOldLine && NuGetVersion.Major(target) >= 10,
            NoNet10Version: stable.Count > 0 && best == null,
            Deprecation: mine?.Deprecation ?? best?.Deprecation,
            Vulnerabilities: mine?.Vulnerabilities ?? [],
            Note: stable.Count == 0 ? "no stable release on nuget.org" : note,
            Projects: projects,
            CappedBy: capped ? string.Join(", ", best!.CappedPlatformDeps!) : null);
    }
}
