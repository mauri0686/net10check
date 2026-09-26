using System.Text.RegularExpressions;

namespace Net10Check;

public enum TfmStatus { Supported, EolNov2026, AlreadyEol, NetFramework, NetStandard, Unknown }

public record TfmInfo(string Tfm, TfmStatus Status, DateOnly? EndOfSupport, string Note);

// Dates from https://dotnet.microsoft.com/platform/support/policy/dotnet-core
public static class Frameworks
{
    public static readonly DateOnly Nov2026 = new(2026, 11, 10);

    static readonly Dictionary<string, DateOnly> EndOfSupport = new()
    {
        ["net10.0"] = new(2028, 11, 14),
        ["net9.0"] = Nov2026,
        ["net8.0"] = Nov2026,
        ["net7.0"] = new(2024, 5, 14),
        ["net6.0"] = new(2024, 11, 12),
        ["net5.0"] = new(2022, 5, 10),
        ["netcoreapp3.1"] = new(2022, 12, 13),
        ["netcoreapp3.0"] = new(2020, 3, 3),
        ["netcoreapp2.2"] = new(2019, 12, 23),
        ["netcoreapp2.1"] = new(2021, 8, 21),
        ["netcoreapp2.0"] = new(2018, 10, 1),
        ["netcoreapp1.1"] = new(2019, 6, 27),
        ["netcoreapp1.0"] = new(2019, 6, 27),
    };

    public static TfmInfo Classify(string tfm)
    {
        var core = tfm.Trim().ToLowerInvariant().Split('-')[0]; // net8.0-windows -> net8.0

        if (EndOfSupport.TryGetValue(core, out var end))
        {
            if (end > Nov2026) return new(tfm, TfmStatus.Supported, end, $"supported until {end:yyyy-MM-dd}");
            if (end == Nov2026) return new(tfm, TfmStatus.EolNov2026, end, $"end of support {end:yyyy-MM-dd}");
            return new(tfm, TfmStatus.AlreadyEol, end, $"out of support since {end:yyyy-MM-dd}");
        }
        if (core.StartsWith("netstandard"))
            return new(tfm, TfmStatus.NetStandard, null, "library standard, usable from net10.0 as is");

        var m = Regex.Match(core, @"^net(\d+)\.\d+$");
        if (m.Success && int.Parse(m.Groups[1].Value) > 10)
            return new(tfm, TfmStatus.Supported, null, "newer than .NET 10");

        // net48, net472, net20 ... (old-style TargetFrameworkVersion v4.7.2 is mapped to net472 by the scanner)
        if (Regex.IsMatch(core, @"^net[1-4]\d{1,2}$"))
            return new(tfm, TfmStatus.NetFramework, null, ".NET Framework: a port to modern .NET, not a TFM bump");

        return new(tfm, TfmStatus.Unknown, null, "could not evaluate (MSBuild property or custom TFM)");
    }
}
