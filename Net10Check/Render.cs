using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Net10Check;

public static class Render
{
    const int MaxLocations = 5;

    static string Count(int n, string noun) => $"{n} {noun}{(n == 1 ? "" : "s")}";

    static string EolLine(Summary s)
    {
        var days = Frameworks.Nov2026.DayNumber - DateOnly.FromDateTime(DateTime.Today).DayNumber;
        var when = days > 0 ? $"in {days} days" : "already passed";
        return $"{s.EolNov2026} reach end of support on 2026-11-10 ({when})";
    }

    static string Flags(PackageStatus p)
    {
        var f = new List<string>();
        if (p.NoNet10Version) f.Add("BLOCKER: no version supports .NET 10");
        if (p.CappedBy != null) f.Add($"BLOCKER: requires {p.CappedBy}");
        if (p.Current == null) f.Add("no version found");
        if (p.BumpWithTfm) f.Add("moves to 10.x with the TFM");
        else if (p.UpdateAvailable) f.Add("update available");
        if (p.Deprecation != null) f.Add(p.Deprecation);
        f.AddRange(p.Vulnerabilities.Select(v => "vulnerable " + v));
        if (p.Note != null) f.Add(p.Note);
        return string.Join("; ", f);
    }

    static IEnumerable<string> Locations(RuleHit h) =>
        h.Locations.Count <= MaxLocations ? h.Locations : h.Locations.Take(MaxLocations).Append($"and {h.Locations.Count - MaxLocations} more");

    static string Cta => "Need this done before 2026-11-10? Fixed-price .NET 10 upgrades, you keep your tests green" +
                         (Analyzer.ServiceUrl.Contains(".example") ? "." : $": {Analyzer.ServiceUrl}");

    public static void Console(Report r)
    {
        bool color = !System.Console.IsOutputRedirected && Environment.GetEnvironmentVariable("NO_COLOR") == null;
        void W(string text, ConsoleColor? c = null)
        {
            if (color && c != null) System.Console.ForegroundColor = c.Value;
            System.Console.WriteLine(text);
            if (color && c != null) System.Console.ResetColor();
        }
        var s = r.Summary;

        W($"net10-check: {r.Target}", ConsoleColor.White);
        W("");
        W($"  {s.Projects} projects, {EolLine(s)}" + (s.AlreadyEol > 0 ? $", {s.AlreadyEol} already out of support" : "") +
          (s.NetFramework > 0 ? $", {s.NetFramework} on .NET Framework" : ""), s.EolNov2026 + s.AlreadyEol > 0 ? ConsoleColor.Yellow : ConsoleColor.Green);
        W($"  {Count(s.PackagesToUpdate, "package")} to update, {Count(s.Blockers, "blocker")}" + (r.Offline ? " (offline: NuGet not checked)" : ""), s.Blockers > 0 ? ConsoleColor.Red : null);
        W($"  Estimated effort: {s.Effort.ToUpperInvariant()} ({s.EffortReason})", s.Effort switch { "high" => ConsoleColor.Red, "medium" => ConsoleColor.Yellow, _ => ConsoleColor.Green });

        W("");
        W("Projects", ConsoleColor.White);
        var width = r.Projects.Select(p => p.Name.Length).DefaultIfEmpty(10).Max() + 2;
        foreach (var p in r.Projects)
        {
            if (p.Frameworks.Count == 0) { W($"  {p.Name.PadRight(width)}no TargetFramework found", ConsoleColor.DarkGray); continue; }
            foreach (var f in p.Frameworks)
                W($"  {p.Name.PadRight(width)}{f.Tfm,-16}{f.Note}", f.Status switch
                {
                    TfmStatus.AlreadyEol or TfmStatus.NetFramework => ConsoleColor.Red,
                    TfmStatus.EolNov2026 => ConsoleColor.Yellow,
                    TfmStatus.Unknown => ConsoleColor.DarkGray,
                    _ => ConsoleColor.Green,
                });
        }

        if (r.Blockers.Count > 0)
        {
            W("");
            W("Blockers", ConsoleColor.Red);
            foreach (var b in r.Blockers) W($"  - {b}", ConsoleColor.Red);
        }

        var attention = r.Packages.Where(p => p.NeedsAction).ToList();
        if (attention.Count > 0)
        {
            W("");
            W("Packages", ConsoleColor.White);
            var idWidth = attention.Max(p => p.Id.Length) + 2;
            foreach (var p in attention)
                W($"  {p.Id.PadRight(idWidth)}{p.Current ?? "?",-14}-> {p.Target ?? "?",-14}{Flags(p)}",
                  p.NoNet10Version || p.CappedBy != null || p.Vulnerabilities.Count > 0 ? ConsoleColor.Red : p.Deprecation != null ? ConsoleColor.Yellow : null);
        }
        var ok = r.Packages.Count - attention.Count;
        if (ok > 0) W($"  {ok} package reference(s) already fine for .NET 10 (see --format md for the full list)", ConsoleColor.DarkGray);

        if (r.Findings.Count > 0)
        {
            W("");
            W(".NET 10 breaking changes found in code and project files", ConsoleColor.White);
            foreach (var h in r.Findings)
            {
                W($"  [{h.Severity.ToString().ToLowerInvariant()}] {h.Id}: {h.Title}", h.Severity switch
                {
                    Severity.Blocker => ConsoleColor.Red,
                    Severity.Warning => ConsoleColor.Yellow,
                    _ => ConsoleColor.Cyan,
                });
                W($"      {h.Fix}");
                W($"      at {string.Join(", ", Locations(h))}", ConsoleColor.DarkGray);
                W($"      {h.Doc}", ConsoleColor.DarkGray);
            }
        }

        W("");
        W(Cta, ConsoleColor.Cyan);
    }

    public static string Markdown(Report r)
    {
        var s = r.Summary;
        var sb = new StringBuilder();
        sb.AppendLine($"# .NET 10 readiness: {Path.GetFileName(Path.TrimEndingDirectorySeparator(r.Target))}");
        sb.AppendLine();
        sb.AppendLine($"Generated by `net10-check` on {r.GeneratedUtc:yyyy-MM-dd}" + (r.Offline ? " (offline, NuGet not checked)." : "."));
        sb.AppendLine();
        sb.AppendLine("## Summary");
        sb.AppendLine();
        sb.AppendLine($"- **{s.Projects} projects**, {EolLine(s)}" + (s.AlreadyEol > 0 ? $", {s.AlreadyEol} already out of support" : "") +
                      (s.NetFramework > 0 ? $", {s.NetFramework} on .NET Framework" : ""));
        sb.AppendLine($"- **{Count(s.PackagesToUpdate, "package")} to update**, **{Count(s.Blockers, "blocker")}**");
        sb.AppendLine($"- **Estimated effort: {s.Effort}** ({s.EffortReason})");
        sb.AppendLine();
        sb.AppendLine("Effort levels: *none* = nothing targets an old framework; *high* = any .NET Framework project, an EF Core provider with no EF Core 10 release, or 3+ blockers; *medium* = 1-2 blockers, 3+ kinds of code changes, more than 25 packages to update or more than 15 projects; *low* = everything else.");
        sb.AppendLine();

        sb.AppendLine("## Projects");
        sb.AppendLine();
        sb.AppendLine("| Project | Target framework | Status |");
        sb.AppendLine("|---|---|---|");
        foreach (var p in r.Projects)
            foreach (var f in p.Frameworks.DefaultIfEmpty(new TfmInfo("?", TfmStatus.Unknown, null, "no TargetFramework found")))
                sb.AppendLine($"| {p.Name} | {f.Tfm} | {f.Note} |");
        sb.AppendLine();

        if (r.Blockers.Count > 0)
        {
            sb.AppendLine("## Blockers");
            sb.AppendLine();
            foreach (var b in r.Blockers) sb.AppendLine($"- {b}");
            sb.AppendLine();
        }

        sb.AppendLine("## Packages");
        sb.AppendLine();
        sb.AppendLine("| Package | Current | Target for net10.0 | Notes | Used by |");
        sb.AppendLine("|---|---|---|---|---|");
        foreach (var p in r.Packages.OrderByDescending(p => p.NeedsAction).ThenBy(p => p.Id, StringComparer.OrdinalIgnoreCase))
            sb.AppendLine($"| {p.Id} | {p.Current ?? "?"} | {p.Target ?? "?"} | {(p.NeedsAction ? Flags(p) : "ok")} | {string.Join(", ", p.Projects)} |");
        sb.AppendLine();

        if (r.Findings.Count > 0)
        {
            sb.AppendLine("## .NET 10 breaking changes found");
            sb.AppendLine();
            foreach (var h in r.Findings)
            {
                sb.AppendLine($"### [{h.Severity.ToString().ToLowerInvariant()}] {h.Id}: {h.Title}");
                sb.AppendLine();
                sb.AppendLine(h.Fix + $" ([docs]({h.Doc}))");
                sb.AppendLine();
                foreach (var l in Locations(h)) sb.AppendLine($"- `{l}`");
                sb.AppendLine();
            }
        }

        sb.AppendLine("---");
        sb.AppendLine();
        sb.AppendLine(Cta);
        return sb.ToString();
    }

    public static string Json(Report r) => JsonSerializer.Serialize(r, new JsonSerializerOptions
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
    });
}
