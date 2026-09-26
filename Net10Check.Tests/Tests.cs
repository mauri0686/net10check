using System.Runtime.CompilerServices;
using System.Text.Json.Nodes;
using Net10Check;
using Xunit;

public class ScannerTests
{
    static string Fixture(string rel, [CallerFilePath] string self = "") => Path.Combine(Path.GetDirectoryName(self)!, "fixtures", rel);

    [Fact]
    public void Sln_lists_existing_projects_only()
    {
        var (_, projects) = ProjectScanner.Discover(Fixture("Fixtures.sln"));
        Assert.Equal(["App.csproj", "Legacy.csproj", "Lib.csproj"], projects.Select(Path.GetFileName).Order());
    }

    [Fact]
    public void Slnx_and_folder_discovery()
    {
        Assert.Single(ProjectScanner.Discover(Fixture("cpm/Cpm.slnx")).Projects);
        // folder with exactly one solution uses it
        Assert.Single(ProjectScanner.Discover(Fixture("cpm")).Projects);
        // folder without a solution: recursive search
        Assert.Single(ProjectScanner.Discover(Fixture("multi")).Projects);
    }

    [Fact]
    public void Net8_project_versions_from_attribute_and_element()
    {
        var p = ProjectScanner.Load(Fixture("net8/App.csproj"));
        Assert.Equal(["net8.0"], p.Tfms);
        Assert.False(p.CentralPackageManagement);
        Assert.Equal("8.0.1", p.Packages.Single(x => x.Id == "Microsoft.EntityFrameworkCore").Version);
        Assert.Equal("13.0.1", p.Packages.Single(x => x.Id == "Newtonsoft.Json").Version);
        Assert.Null(p.Packages.Single(x => x.Id == "Serilog").Version);
    }

    [Fact]
    public void Multi_target()
    {
        var p = ProjectScanner.Load(Fixture("multi/Lib.csproj"));
        Assert.Equal(["net8.0", "net9.0", "netstandard2.0"], p.Tfms);
    }

    [Fact]
    public void Central_package_management_and_directory_build_props()
    {
        var p = ProjectScanner.Load(Fixture("cpm/src/Api/Api.csproj"));
        Assert.True(p.CentralPackageManagement);
        Assert.Equal(["net9.0"], p.Tfms);                       // $(AppTfm) from Directory.Build.props
        Assert.Equal("9.0.4", p.Packages.Single(x => x.Id == "Microsoft.AspNetCore.OpenApi").Version); // $(AspNetVersion)
        Assert.Equal("8.4.0", p.Packages.Single(x => x.Id == "Polly").Version);                        // VersionOverride
        Assert.Equal("3.7.115", p.Packages.Single(x => x.Id == "Nerdbank.GitVersioning").Version);     // GlobalPackageReference
        Assert.True(p.Prop("TreatWarningsAsErrors"));
    }

    [Fact]
    public void Legacy_netfx_project_with_packages_config()
    {
        var p = ProjectScanner.Load(Fixture("netfx/Legacy.csproj"));
        Assert.Equal(["net472"], p.Tfms);
        Assert.Equal(TfmStatus.NetFramework, Frameworks.Classify(p.Tfms[0]).Status);
        Assert.Equal("12.0.3", p.Packages.Single(x => x.Id == "Newtonsoft.Json").Version);
    }

    [Fact]
    public void Rules_on_fixtures()
    {
        var cpmRoot = Fixture("cpm");
        var cpm = Rules.Scan(cpmRoot, [ProjectScanner.Load(Fixture("cpm/src/Api/Api.csproj"))]);
        // TreatWarningsAsErrors turns obsoletions into blockers
        Assert.Equal(Severity.Blocker, cpm.Single(h => h.Id == "ASPDEPR004").Severity);
        Assert.Equal(["src/Api/Program.cs:2"], cpm.Single(h => h.Id == "ASPDEPR005").Locations); // commented line ignored
        Assert.Equal(["src/Api/Dockerfile:1"], cpm.Single(h => h.Id == "DOCKER-DEBIAN").Locations);
        Assert.Equal(["src/Api/Dockerfile:2"], cpm.Single(h => h.Id == "DOCKER-UBUNTU").Locations);
        Assert.Contains(cpm, h => h.Id == "GLOBAL-JSON");
        Assert.DoesNotContain(cpm, h => h.Id == "NU1015");     // CPM: no Version is correct

        var app = Rules.Scan(Fixture("net8"), [ProjectScanner.Load(Fixture("net8/App.csproj"))]);
        Assert.Contains("App.csproj (Serilog)", app.Single(h => h.Id == "NU1015").Locations);
        Assert.Contains(app, h => h.Id == "LINQ-ASYNC");
        Assert.Equal(Severity.Warning, app.Single(h => h.Id == "ASPDEPR002").Severity);
        Assert.DoesNotContain(app, h => h.Id == "ASPDEPR004"); // only in a comment

        var lib = Rules.Scan(Fixture("multi"), [ProjectScanner.Load(Fixture("multi/Lib.csproj"))]);
        Assert.Equal("EF10-TOOLS", Assert.Single(lib).Id);
    }

    [Fact]
    public void Azure_and_serialization_rules()
    {
        var hits = Rules.Scan(Fixture("azure"), [ProjectScanner.Load(Fixture("azure/InProc/InProc.csproj")),
                                                 ProjectScanner.Load(Fixture("azure/Isolated/Isolated.csproj"))]);
        // isolated worker project (AzureFunctionsVersion + Worker packages) is not flagged
        Assert.Equal(["InProc/InProc.csproj"], hits.Single(h => h.Id == "FUNCTIONS-INPROC").Locations);
        Assert.Equal(["InProc/InProc.csproj (Microsoft.Azure.ServiceBus)"], hits.Single(h => h.Id == "AZURE-SB-LEGACY").Locations);
        Assert.Equal(["InProc/InProc.csproj (Microsoft.Azure.EventHubs)"], hits.Single(h => h.Id == "AZURE-EH-LEGACY").Locations);
        Assert.Equal(["InProc/InProc.csproj"], hits.Single(h => h.Id == "SQLCLIENT").Locations);
        // the using line and the commented line do not count
        Assert.Equal(["InProc/Cache.cs:5"], hits.Single(h => h.Id == "BINARYFORMATTER").Locations);
        Assert.All(hits.Where(h => h.Id != "SQLCLIENT"), h => Assert.Equal(Severity.Blocker, h.Severity));
    }

    [Fact]
    public async Task End_to_end_offline_summary()
    {
        var (root, paths) = ProjectScanner.Discover(Fixture("Fixtures.sln"));
        var report = await Analyzer.Build("Fixtures.sln", root, paths.Select(ProjectScanner.Load).ToList(), fetch: null);
        var s = report.Summary;
        Assert.Equal(3, s.Projects);
        Assert.Equal(2, s.EolNov2026);        // App (net8.0) and Lib (net8.0;net9.0)
        Assert.Equal(1, s.NetFramework);
        Assert.Equal("high", s.Effort);
        Assert.Contains(report.Blockers, b => b.StartsWith("Legacy"));
        // offline: platform packages on 8.x/9.x still flagged
        Assert.True(report.Packages.Single(p => p.Id == "Microsoft.EntityFrameworkCore").BumpWithTfm);
        Assert.Contains("# .NET 10 readiness", Render.Markdown(report));
        Assert.Contains("\"effort\": \"high\"", Render.Json(report));
    }
}

public class FrameworkTests
{
    [Theory]
    [InlineData("net8.0", TfmStatus.EolNov2026)]
    [InlineData("net9.0", TfmStatus.EolNov2026)]
    [InlineData("net9.0-windows", TfmStatus.EolNov2026)]
    [InlineData("net6.0", TfmStatus.AlreadyEol)]
    [InlineData("netcoreapp3.1", TfmStatus.AlreadyEol)]
    [InlineData("net10.0", TfmStatus.Supported)]
    [InlineData("net11.0", TfmStatus.Supported)]
    [InlineData("net48", TfmStatus.NetFramework)]
    [InlineData("net472", TfmStatus.NetFramework)]
    [InlineData("netstandard2.0", TfmStatus.NetStandard)]
    [InlineData("$(Unresolved)", TfmStatus.Unknown)]
    public void Classify(string tfm, TfmStatus expected) => Assert.Equal(expected, Frameworks.Classify(tfm).Status);

    [Fact]
    public void Dates() => Assert.Equal(new DateOnly(2028, 11, 14), Frameworks.Classify("net10.0").EndOfSupport);
}

public class PackageTests
{
    static PkgVersion V(string v, params string[] tfms) => new(v, true, [.. tfms], null, []);

    [Fact]
    public void Platform_package_moves_to_10_ignoring_prerelease()
    {
        var s = Packages.Classify("Microsoft.EntityFrameworkCore", "9.0.0",
            [V("9.0.0", "net8.0"), V("10.0.3", "net10.0"), V("11.0.0-rc.1", "net11.0")], null, ["A"]);
        Assert.Equal("10.0.3", s.Target);
        Assert.True(s.BumpWithTfm);
        Assert.True(s.UpdateAvailable);
    }

    [Fact]
    public void Newer_line_that_drops_net10_is_skipped()
    {
        var s = Packages.Classify("Microsoft.Extensions.Http", "8.0.1",
            [V("8.0.1", "net8.0"), V("10.0.5", "net10.0"), V("11.0.0", "net11.0")], null, []);
        Assert.Equal("10.0.5", s.Target);
    }

    [Fact]
    public void Third_party_major_8_is_not_a_platform_bump()
    {
        var s = Packages.Classify("System.IdentityModel.Tokens.Jwt", "8.3.1", [V("8.3.1", "net8.0"), V("8.23.0", "net9.0")], null, []);
        Assert.False(s.BumpWithTfm);
        Assert.True(s.UpdateAvailable);
    }

    [Fact]
    public void Netfx_only_package_is_a_blocker()
    {
        var s = Packages.Classify("Old.Lib", "1.0.0",
            [V("1.0.0", ".NETFramework4.5") with { DependencyCount = 1 }, V("1.1.0", ".NETFramework4.6.1") with { DependencyCount = 2 }], null, []);
        Assert.True(s.NoNet10Version);
        Assert.Null(s.Target);
    }

    [Theory]
    [InlineData("[9.0.0, 9.0.999]", true)]
    [InlineData("[8.0.0, 10.0.0)", true)]
    [InlineData("[9.0.1]", true)]
    [InlineData("[10.0.0, 11.0.0)", false)]
    [InlineData("[8.0.0, )", false)]
    [InlineData("8.0.0", false)]
    [InlineData("9.0.0", false)]
    [InlineData("[10.0.0, )", false)]
    [InlineData("[2.1.1, 6.0.0)", false)] // old ASP.NET Core 2.x line, never gets a 10.x
    public void Upper_bound_below_10(string range, bool capped) => Assert.Equal(capped, NuGetClient.CapsBelow10(range));

    [Fact]
    public void Only_the_group_net10_picks_counts()
    {
        // OpenTelemetry.Instrumentation.AspNetCore style: netstandard2.0 group pins ASP.NET Core 2.x/8.x, net8.0 group does not
        var node = JsonNode.Parse("""
            {"version":"1.19.0","dependencyGroups":[
              {"targetFramework":".NETStandard2.0","dependencies":[{"id":"Microsoft.Extensions.Options","range":"[8.0.0, 9.0.0)"}]},
              {"targetFramework":"net8.0","dependencies":[{"id":"OpenTelemetry","range":"[1.19.0, 2.0.0)"}]}]}
            """)!;
        Assert.Empty(NuGetClient.Parse(node).CappedPlatformDeps!);
        Assert.True(NuGetClient.Net10Rank("net10.0") > NuGetClient.Net10Rank("net8.0"));
        Assert.True(NuGetClient.Net10Rank("net8.0") > NuGetClient.Net10Rank(".NETStandard2.1"));
        Assert.Equal(-1, NuGetClient.Net10Rank(".NETFramework4.7.2"));
        Assert.Equal(-1, NuGetClient.Net10Rank("net11.0"));
    }

    [Fact]
    public void Prerelease_that_lifts_the_cap_is_hinted()
    {
        var stable = V("9.0.0", "net8.0") with { CappedPlatformDeps = ["Microsoft.EntityFrameworkCore.Relational [9.0.0, 9.0.999]"] };
        var pre = V("10.0.0-rc.1", "net10.0") with { CappedPlatformDeps = [] };
        var s = Packages.Classify("Pomelo.EntityFrameworkCore.MySql", "9.0.0", [stable, pre], null, []);
        Assert.NotNull(s.CappedBy);
        Assert.Equal("prerelease 10.0.0-rc.1 lifts the cap", s.Note);
    }

    [Fact]
    public void Provider_pinned_to_ef9_is_a_blocker()
    {
        // Pomelo.EntityFrameworkCore.MySql 9.0.0 -> Microsoft.EntityFrameworkCore.Relational [9.0.0, 9.0.999]
        var node = JsonNode.Parse("""
            {"version":"9.0.0","dependencyGroups":[{"targetFramework":"net8.0","dependencies":[
              {"id":"Microsoft.EntityFrameworkCore.Relational","range":"[9.0.0, 9.0.999]"},{"id":"MySqlConnector","range":"[2.4.0, )"}]}]}
            """)!;
        var s = Packages.Classify("Pomelo.EntityFrameworkCore.MySql", "9.0.0", [NuGetClient.Parse(node)], null, []);
        Assert.Equal("Microsoft.EntityFrameworkCore.Relational [9.0.0, 9.0.999]", s.CappedBy);
        Assert.True(s.NeedsAction);
    }

    [Fact]
    public void Empty_netfx_group_is_only_a_note()
    {
        // e.g. Microsoft.VisualStudio.Azure.Containers.Tools.Targets: MSBuild targets, empty .NETFramework4.7.2 group
        var s = Packages.Classify("Some.Build.Targets", "1.19.6", [V("1.19.6", ".NETFramework4.7.2"), V("1.23.0", ".NETFramework4.7.2")], null, []);
        Assert.False(s.NoNet10Version);
        Assert.Equal("1.23.0", s.Target);
        Assert.Contains("build-time", s.Note);
    }

    [Fact]
    public void No_dependency_groups_and_netstandard_are_fine()
    {
        Assert.True(Packages.SupportsNet10([]));
        Assert.True(Packages.SupportsNet10([".NETStandard2.0"]));
        Assert.True(Packages.SupportsNet10(["net8.0-windows7.0"]));
        Assert.False(Packages.SupportsNet10([".NETFramework4.8", "net11.0"]));
    }

    [Fact]
    public void Unlisted_versions_ignored_and_current_version_metadata_used()
    {
        var current = new PkgVersion("2.0.0", true, [], "deprecated (Legacy), use New.Lib", ["high: https://github.com/advisories/GHSA-x"]);
        var s = Packages.Classify("Some.Lib", "2.0.0", [current, new PkgVersion("3.0.0", false, [], null, []), V("2.1.0")], null, []);
        Assert.Equal("2.1.0", s.Target);
        Assert.Equal("deprecated (Legacy), use New.Lib", s.Deprecation);
        Assert.Single(s.Vulnerabilities);
    }

    [Fact]
    public void Offline_flags_platform_packages_only()
    {
        Assert.True(Packages.Classify("Microsoft.AspNetCore.OpenApi", "9.0.4", null, "offline", []).BumpWithTfm);
        Assert.False(Packages.Classify("Newtonsoft.Json", "13.0.1", null, "offline", []).BumpWithTfm);
    }

    [Fact]
    public void Parse_registration_entry()
    {
        var node = JsonNode.Parse("""
            {"version":"8.0.4","listed":true,
             "dependencyGroups":[{"targetFramework":"net8.0"},{"targetFramework":".NETStandard2.0"}],
             "deprecation":{"reasons":["Legacy"],"alternatePackage":{"id":"New.Lib","range":"*"}},
             "vulnerabilities":[{"advisoryUrl":"https://github.com/advisories/GHSA-8g4q-xg66-9fp4","severity":"2"}]}
            """)!;
        var v = NuGetClient.Parse(node);
        Assert.Equal(["net8.0", ".NETStandard2.0"], v.Frameworks);
        Assert.Equal("deprecated (Legacy), use New.Lib", v.Deprecation);
        Assert.Equal("high: https://github.com/advisories/GHSA-8g4q-xg66-9fp4", Assert.Single(v.Vulnerabilities));
    }

    [Theory]
    [InlineData("1.0.0-rc.1", "1.0.0", -1)]
    [InlineData("10.0.0", "9.0.0", 1)]
    [InlineData("1.0.0-rc.10", "1.0.0-rc.2", 1)]
    [InlineData("8.0", "8.0.0", 0)]
    [InlineData("[8.0.1, )", "8.0.1", 0)]
    [InlineData("9.*", "9.0.0", 0)]
    public void Version_compare(string a, string b, int sign) => Assert.Equal(sign, Math.Sign(NuGetVersion.Compare(a, b)));
}

public class EffortTests
{
    [Fact]
    public void Levels()
    {
        Assert.Equal("none", Analyzer.Effort(9, toUpgrade: 0, 0, 0, 0, 28).Level);
        Assert.Equal("high", Analyzer.Effort(1, 1, netfx: 1, 0, 0, 0).Level);
        Assert.Equal("high", Analyzer.Effort(1, 1, 0, blockers: 3, 0, 0).Level);
        Assert.Equal("medium", Analyzer.Effort(1, 1, 0, blockers: 1, 0, 0).Level);
        Assert.Equal("medium", Analyzer.Effort(1, 1, 0, 0, codeChanges: 3, 0).Level);
        Assert.Equal("medium", Analyzer.Effort(20, 20, 0, 0, 0, 0).Level);
        Assert.Equal("low", Analyzer.Effort(5, 5, 0, 0, 2, 20).Level);
        Assert.Equal("high", Analyzer.Effort(5, 5, 0, 1, 0, 0, efProviderBlocked: true).Level);
    }
}
