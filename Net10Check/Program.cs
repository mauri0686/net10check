using Net10Check;

const string Usage = """
    net10-check: what blocks this solution from moving to .NET 10?

    Usage: net10-check [path] [options]

      path               .sln, .slnx, .csproj/.fsproj/.vbproj or folder (default: current folder)
      --format <f>       console (default), md or json
      -o, --output <f>   write the md/json report to a file (console summary still printed)
      --offline          skip nuget.org lookups
      --fail-on-eol      exit code 1 if any project targets a framework out of support by 2026-11-10 (for CI)
      -h, --help         show this help
    """;

string path = ".", format = "console";
string? output = null;
bool offline = false, failOnEol = false;
for (int i = 0; i < args.Length; i++)
{
    switch (args[i])
    {
        case "-h" or "--help" or "-?": Console.WriteLine(Usage); return 0;
        case "--offline": offline = true; break;
        case "--fail-on-eol": failOnEol = true; break;
        case "--format" or "-f" when i + 1 < args.Length: format = args[++i].ToLowerInvariant(); break;
        case "--output" or "-o" when i + 1 < args.Length: output = args[++i]; break;
        case var a when a.StartsWith('-'): Console.Error.WriteLine($"Unknown option: {a}\n\n{Usage}"); return 2;
        default: path = args[i]; break;
    }
}
if (format is not ("console" or "md" or "json")) { Console.Error.WriteLine($"Unknown format: {format} (use console, md or json)"); return 2; }
if (output != null && format == "console") format = Path.GetExtension(output).Equals(".json", StringComparison.OrdinalIgnoreCase) ? "json" : "md";

Report report;
try
{
    var (root, projectPaths) = ProjectScanner.Discover(path);
    if (projectPaths.Count == 0) { Console.Error.WriteLine($"No projects found in {Path.GetFullPath(path)}"); return 2; }
    var projects = projectPaths.Select(ProjectScanner.Load).ToList();
    report = await Analyzer.Build(Path.GetFullPath(path), root, projects, offline ? null : new NuGetClient().GetVersions);
}
catch (Exception e) when (e is FileNotFoundException or ArgumentException)
{
    Console.Error.WriteLine(e.Message);
    return 2;
}

var text = format switch { "md" => Render.Markdown(report), "json" => Render.Json(report), _ => null };
if (output != null)
{
    File.WriteAllText(output, text);
    Render.Console(report);
    Console.WriteLine($"\nReport written to {Path.GetFullPath(output)}");
}
else if (text != null) Console.WriteLine(text);
else Render.Console(report);

bool eol = report.Projects.Any(p => p.Frameworks.Any(f => f.Status is TfmStatus.EolNov2026 or TfmStatus.AlreadyEol));
return failOnEol && eol ? 1 : 0;
