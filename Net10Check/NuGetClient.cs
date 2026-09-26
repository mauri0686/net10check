using System.Collections.Concurrent;
using System.Net;
using System.Text.Json.Nodes;

namespace Net10Check;

public record PkgVersion(string Version, bool Listed, List<string> Frameworks, string? Deprecation, List<string> Vulnerabilities,
                         int DependencyCount = 0, List<string>? CappedPlatformDeps = null);

// Reads https://api.nuget.org/v3/registration5-gz-semver2/{id}/index.json: versions, dependency groups per TFM,
// deprecation and vulnerabilities in one place. Returns null when the package is not on nuget.org.
public sealed class NuGetClient
{
    static readonly HttpClient Http = new(new HttpClientHandler { AutomaticDecompression = DecompressionMethods.All })
    {
        Timeout = TimeSpan.FromSeconds(15),
        DefaultRequestHeaders = { { "User-Agent", "net10-check" } },
    };
    readonly ConcurrentDictionary<string, Task<JsonNode?>> cache = new(StringComparer.OrdinalIgnoreCase);
    readonly SemaphoreSlim gate = new(16);

    // Loads only the registration pages needed: the one holding the current version, and pages from the newest
    // backwards until a stable version that works on net10.0 shows up (usually just the last page).
    public async Task<List<PkgVersion>?> GetVersions(string id, string? current)
    {
        var index = await GetJson($"https://api.nuget.org/v3/registration5-gz-semver2/{id.ToLowerInvariant()}/index.json");
        if (index is null) return null;
        var pages = index["items"]!.AsArray().Select(p => p!).ToList();

        async Task<List<PkgVersion>> Load(JsonNode page) =>
            ((page["items"] ?? (await GetJson((string)page["@id"]!))?["items"])?.AsArray() ?? [])
                .Select(i => Parse(i!["catalogEntry"]!)).ToList();

        var result = new List<PkgVersion>();
        var holder = current == null ? null : pages.FirstOrDefault(p =>
            NuGetVersion.Compare((string)p["lower"]!, current) <= 0 && NuGetVersion.Compare(current, (string)p["upper"]!) <= 0);
        if (holder != null) result.AddRange(await Load(holder));
        for (int i = pages.Count - 1; i >= 0; i--)
        {
            var items = await Load(pages[i]);
            result.AddRange(items);
            if (items.Any(v => v.Listed && !v.Version.Contains('-') && Packages.SupportsNet10(v.Frameworks))) break;
        }
        return result.DistinctBy(v => v.Version.ToLowerInvariant()).ToList();
    }

    Task<JsonNode?> GetJson(string url) => cache.GetOrAdd(url, Download);

    async Task<JsonNode?> Download(string url)
    {
        await gate.WaitAsync();
        try
        {
            using var response = await Http.GetAsync(url);
            if (response.StatusCode == HttpStatusCode.NotFound) return null;
            response.EnsureSuccessStatusCode();
            return JsonNode.Parse(await response.Content.ReadAsStreamAsync());
        }
        finally { gate.Release(); }
    }

    static readonly string[] Severity = ["low", "moderate", "high", "critical"];

    public static PkgVersion Parse(JsonNode entry)
    {
        var groups = entry["dependencyGroups"]?.AsArray() ?? [];
        var frameworks = groups.Select(g => (string?)g!["targetFramework"] ?? "any").ToList();
        var dependencyCount = groups.Sum(g => g!["dependencies"]?.AsArray().Count ?? 0);
        // Only the group NuGet would pick for net10.0 matters (a netstandard2.0 group often pins old ASP.NET Core 2.x packages).
        // e.g. Pomelo.EntityFrameworkCore.MySql 9.0.0 (net8.0) -> Microsoft.EntityFrameworkCore.Relational [9.0.0, 9.0.999]
        var nearest = groups.Where(g => Net10Rank((string?)g!["targetFramework"]) >= 0).MaxBy(g => Net10Rank((string?)g!["targetFramework"]));
        var capped = (nearest?["dependencies"]?.AsArray() ?? [])
            .Select(x => $"{(string)x!["id"]!} {(string?)x["range"]}")
            .Where(x => Packages.IsPlatform(x.Split(' ')[0]) && CapsBelow10(x[(x.IndexOf(' ') + 1)..]))
            .Distinct().ToList();
        string? deprecation = null;
        if (entry["deprecation"] is { } d)
        {
            var reasons = string.Join(", ", d["reasons"]?.AsArray().Select(r => (string?)r) ?? []);
            var alt = (string?)d["alternatePackage"]?["id"];
            deprecation = $"deprecated ({reasons})" + (alt != null ? $", use {alt}" : "");
        }
        var vulns = entry["vulnerabilities"]?.AsArray().Select(v =>
        {
            var sev = int.TryParse((string?)v!["severity"], out var s) && s is >= 0 and <= 3 ? Severity[s] : "unknown";
            return $"{sev}: {(string?)v["advisoryUrl"]}";
        }).ToList() ?? [];
        return new PkgVersion((string)entry["version"]!, (bool?)entry["listed"] ?? true, frameworks, deprecation, vulns, dependencyCount, capped);
    }

    // How well a dependency group's TFM fits a net10.0 consumer (higher is nearer), -1 if it does not apply.
    public static int Net10Rank(string? tfm)
    {
        var f = (tfm ?? "any").ToLowerInvariant().TrimStart('.');
        if (f is "any" or "") return 0;
        var m = System.Text.RegularExpressions.Regex.Match(f, @"^(net|netcoreapp|netstandard)(\d+)\.(\d+)");
        if (!m.Success) return -1;
        int major = int.Parse(m.Groups[2].Value), minor = int.Parse(m.Groups[3].Value);
        return m.Groups[1].Value switch
        {
            "net" when major is >= 5 and <= 10 => 3000 + major * 10 + minor,
            "netcoreapp" => 2000 + major * 10 + minor,
            "netstandard" => 1000 + major * 10 + minor,
            _ => -1,
        };
    }

    // A dependency on the runtime line (lower bound 5.x+) capped below 10: "[9.0.0, 9.0.999]", "[8.0.0, 10.0.0)", "[9.0.1]".
    // "[8.0.0, )", "8.0.0" and old 2.x lines like "[2.1.1, 6.0.0)" (no 10.x will ever exist) do not count.
    public static bool CapsBelow10(string range)
    {
        var r = range.Trim();
        if (NuGetVersion.Major(r) is not >= 5) return false;                 // lower bound
        var comma = r.IndexOf(',');
        if (comma < 0) return r.StartsWith('[') && NuGetVersion.Major(r) < 10; // exact pin
        var upper = r[(comma + 1)..].Trim().TrimEnd(']', ')').Trim();
        if (upper == "") return false;
        return NuGetVersion.Major(upper) < 10 || (r.EndsWith(')') && NuGetVersion.Compare(upper, "10.0.0") <= 0);
    }
}

// NuGet/SemVer ordering: numeric release parts, then release > prerelease, then prerelease labels.
public static class NuGetVersion
{
    public static int Compare(string a, string b)
    {
        var (ra, pa) = SplitVersion(a);
        var (rb, pb) = SplitVersion(b);
        for (int i = 0; i < 4; i++)
            if (ra[i] != rb[i]) return ra[i].CompareTo(rb[i]);
        if (pa == pb) return 0;
        if (pa == "") return 1;
        if (pb == "") return -1;
        var la = pa.Split('.');
        var lb = pb.Split('.');
        for (int i = 0; i < Math.Min(la.Length, lb.Length); i++)
        {
            bool na = long.TryParse(la[i], out var xa), nb = long.TryParse(lb[i], out var xb);
            int c = na && nb ? xa.CompareTo(xb) : na ? -1 : nb ? 1 : string.Compare(la[i], lb[i], StringComparison.OrdinalIgnoreCase);
            if (c != 0) return c;
        }
        return la.Length.CompareTo(lb.Length);
    }

    public static int? Major(string? version) =>
        version != null && int.TryParse(Normalize(version).Split('.', '-')[0], out var m) ? m : null;

    // "[8.0.1, )" -> "8.0.1", "9.*" -> "9.0", "1.2.3+meta" -> "1.2.3"
    public static string Normalize(string version)
    {
        var v = version.Trim().TrimStart('[', '(').Split(',')[0].Trim().TrimEnd(']', ')');
        return v.Split('+')[0].Replace("*", "0");
    }

    static (long[] Release, string Pre) SplitVersion(string version)
    {
        var v = Normalize(version);
        var dash = v.IndexOf('-');
        var pre = dash >= 0 ? v[(dash + 1)..] : "";
        var nums = (dash >= 0 ? v[..dash] : v).Split('.');
        var release = new long[4];
        for (int i = 0; i < Math.Min(4, nums.Length); i++) long.TryParse(nums[i], out release[i]);
        return (release, pre);
    }
}
