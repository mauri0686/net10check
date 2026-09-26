using System.Text.RegularExpressions;
using System.Xml.Linq;

namespace Net10Check;

public record PackageRef(string Id, string? Version);

public record ProjectInfo(
    string Path,
    string Name,
    List<string> Tfms,
    List<PackageRef> Packages,
    bool CentralPackageManagement,
    Dictionary<string, string> Properties)
{
    public bool Prop(string name) => Properties.TryGetValue(name, out var v) && v.Trim().Equals("true", StringComparison.OrdinalIgnoreCase);
}

// ponytail: static read of the XML (props + csproj + targets), no MSBuild evaluation.
// Conditions are ignored except that conditional TFMs are added to the list. Good enough for a scan.
public static class ProjectScanner
{
    static readonly string[] ProjectExts = [".csproj", ".fsproj", ".vbproj"];
    static readonly HashSet<string> SkipDirs = new(StringComparer.OrdinalIgnoreCase) { "bin", "obj", "node_modules" };

    public static (string Root, List<string> Projects) Discover(string input)
    {
        var full = Path.GetFullPath(input);
        if (Directory.Exists(full))
        {
            // Like the dotnet CLI: a folder with exactly one solution means that solution.
            var solutions = Directory.GetFiles(full, "*.sln").Concat(Directory.GetFiles(full, "*.slnx")).ToList();
            if (solutions.Count == 1) return Discover(solutions[0]);
            return (full, EnumerateFiles(full, "*.*proj").Where(f => ProjectExts.Contains(Path.GetExtension(f).ToLowerInvariant())).OrderBy(f => f).ToList());
        }
        if (!File.Exists(full)) throw new FileNotFoundException($"Path not found: {input}");

        var dir = Path.GetDirectoryName(full)!;
        var ext = Path.GetExtension(full).ToLowerInvariant();
        IEnumerable<string> rel = ext switch
        {
            ".sln" => Regex.Matches(File.ReadAllText(full), @"^Project\(""\{[^}]+\}""\)\s*=\s*""[^""]*""\s*,\s*""([^""]+)""", RegexOptions.Multiline)
                          .Select(m => m.Groups[1].Value),
            ".slnx" => XDocument.Load(full).Descendants().Where(e => e.Name.LocalName == "Project")
                          .Select(e => (string?)e.Attribute("Path") ?? ""),
            _ when ProjectExts.Contains(ext) => [full],
            _ => throw new ArgumentException($"Unsupported file: {input} (expected .sln, .slnx, .csproj, .fsproj, .vbproj or a folder)"),
        };
        var projects = rel
            .Where(p => ProjectExts.Contains(Path.GetExtension(p).ToLowerInvariant()))
            .Select(p => Path.GetFullPath(Path.Combine(dir, p.Replace('\\', Path.DirectorySeparatorChar))))
            .Where(File.Exists)
            .Distinct()
            .OrderBy(p => p)
            .ToList();
        return (dir, projects);
    }

    public static IEnumerable<string> EnumerateFiles(string root, string pattern)
    {
        var stack = new Stack<string>([root]);
        while (stack.Count > 0)
        {
            var dir = stack.Pop();
            string[] files, dirs;
            try { files = Directory.GetFiles(dir, pattern); dirs = Directory.GetDirectories(dir); }
            catch (UnauthorizedAccessException) { continue; }
            foreach (var f in files) yield return f;
            foreach (var d in dirs)
            {
                var name = Path.GetFileName(d);
                if (!SkipDirs.Contains(name) && !name.StartsWith('.')) stack.Push(d); // .git, .vs, .claude worktrees...
            }
        }
    }

    public static ProjectInfo Load(string projectPath)
    {
        var dir = Path.GetDirectoryName(projectPath)!;
        var name = Path.GetFileNameWithoutExtension(projectPath);
        var props = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["MSBuildProjectName"] = name,
            ["MSBuildProjectDirectory"] = dir,
            ["MSBuildThisFileDirectory"] = dir + Path.DirectorySeparatorChar,
        };

        // MSBuild import order: Directory.Build.props, Directory.Packages.props (via NuGet props), project, Directory.Build.targets
        var buildProps = FindUp(dir, "Directory.Build.props");
        var packagesProps = FindUp(dir, "Directory.Packages.props");
        var docs = new[] { buildProps, packagesProps, projectPath, FindUp(dir, "Directory.Build.targets") }
            .Where(p => p != null).Select(p => (Path: p!, Doc: TryLoad(p!))).Where(d => d.Doc != null).ToList();

        string? tfm = null, tfms = null, tfv = null;
        var conditionalTfms = new List<string>();
        foreach (var (_, doc) in docs)
            foreach (var el in doc!.Descendants().Where(e => e.Parent?.Name.LocalName == "PropertyGroup"))
            {
                var key = el.Name.LocalName;
                var value = Expand(el.Value.Trim(), props);
                props[key] = value;
                bool conditional = el.Attribute("Condition") != null || el.Parent!.Attribute("Condition") != null;
                switch (key)
                {
                    case "TargetFramework" when conditional:
                    case "TargetFrameworks" when conditional: conditionalTfms.AddRange(Split(value)); break;
                    case "TargetFramework": tfm = value; break;
                    case "TargetFrameworks": tfms = value; break;
                    case "TargetFrameworkVersion": tfv = value; break;
                }
            }

        var frameworks = Split(tfms ?? tfm ?? "").ToList();
        if (frameworks.Count == 0 && tfv != null) frameworks.Add("net" + tfv.TrimStart('v', 'V').Replace(".", "")); // v4.7.2 -> net472
        frameworks.AddRange(conditionalTfms);
        frameworks = frameworks.Where(f => f.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase).ToList();

        bool cpm = props.TryGetValue("ManagePackageVersionsCentrally", out var c) && c.Equals("true", StringComparison.OrdinalIgnoreCase);

        var central = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var updates = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var refs = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
        foreach (var (path, doc) in docs)
            foreach (var el in doc!.Descendants())
            {
                var kind = el.Name.LocalName;
                if (kind is not ("PackageReference" or "PackageVersion" or "GlobalPackageReference")) continue;
                var version = Attr(el, "VersionOverride") ?? Attr(el, "Version");
                version = version == null ? null : Expand(version, props);
                var include = Attr(el, "Include");
                var update = Attr(el, "Update");

                if (kind == "PackageVersion" && include != null && version != null)
                    foreach (var id in Split(include)) central[id] = version;
                else if (kind == "PackageReference" && update != null && version != null)
                    foreach (var id in Split(update)) updates[id] = version;
                else if (include != null)
                    foreach (var id in Split(Expand(include, props)))
                        refs[id] = version ?? refs.GetValueOrDefault(id);
            }

        var packages = refs
            .Where(r => !r.Key.Contains('$') && r.Key is not ("Microsoft.AspNetCore.App" or "Microsoft.NETCore.App" or "Microsoft.AspNetCore.All"))
            .Select(r => new PackageRef(r.Key, r.Value ?? updates.GetValueOrDefault(r.Key) ?? (cpm ? central.GetValueOrDefault(r.Key) : null)))
            .ToList();

        // Classic projects: packages.config
        var packagesConfig = Path.Combine(dir, "packages.config");
        if (File.Exists(packagesConfig) && TryLoad(packagesConfig) is { } pc)
            packages.AddRange(pc.Descendants("package").Select(p => new PackageRef((string?)p.Attribute("id") ?? "", (string?)p.Attribute("version"))).Where(p => p.Id != ""));

        return new ProjectInfo(projectPath, name, frameworks, packages, cpm, props);
    }

    static string? Attr(XElement el, string name) =>
        (string?)el.Attribute(name) ?? el.Elements().FirstOrDefault(e => e.Name.LocalName == name)?.Value.Trim();

    static IEnumerable<string> Split(string value) =>
        value.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    static string Expand(string value, Dictionary<string, string> props)
    {
        for (int i = 0; i < 5 && value.Contains("$("); i++)
            value = Regex.Replace(value, @"\$\(([\w.]+)\)", m => props.TryGetValue(m.Groups[1].Value, out var v) ? v : m.Value);
        return value;
    }

    static string? FindUp(string dir, string file)
    {
        for (var d = new DirectoryInfo(dir); d != null; d = d.Parent)
        {
            var candidate = Path.Combine(d.FullName, file);
            if (File.Exists(candidate)) return candidate;
        }
        return null;
    }

    static XDocument? TryLoad(string path)
    {
        try { return XDocument.Load(path); }
        catch (Exception) { return null; }
    }
}
