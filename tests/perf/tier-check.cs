#:project ../../src/Andzip.Core/Andzip.Core.csproj
// The witness that the 5.6.0 tier changes go through the *product* path, not just azcore.
//
//   dotnet run tests/perf/tier-check.cs <input-file>
//
// It compresses one file five ways through ArchiveCreator (so: BuildCreateOptions ->
// TierExtras -> engine), each time with TestAfterCreation on -- the engine reads the
// package back in full -- and compares the resulting byte count with the row
// docs/perf-tiers.tsv recorded for the equivalent azcore command. Exit code is 1 if any
// cell differs, so this is a check, not a demo.
//
// The expected numbers are for the baseline corpus "mixed" at 128 MiB
// (tests/perf/bench.ps1 generates it; pareto.ps1 trims the prefix). Give it that file or
// pass --expect none to see the sizes without the comparison.
using System.Diagnostics;
using Andzip.Core.Archive;

var given = Environment.GetCommandLineArgs().Skip(1).Where(a => a != "none").ToList();
var data = given.FirstOrDefault()
    ?? Path.Combine(Path.GetTempPath(), "andzip-perf-tiers", "mixed-128.bin");
bool check = !Environment.GetCommandLineArgs().Contains("none");
if (!File.Exists(data))
{
    Console.Error.WriteLine($"input not found: {data}");
    return 2;
}
var outDir = Path.Combine(Path.GetTempPath(), "andzip-tier-check");
Directory.CreateDirectory(outDir);

Console.WriteLine($"input : {data} ({new FileInfo(data).Length} bytes)");
Console.WriteLine($"engine: {ArchiveEngine.Describe()}");

// (label, container, method, tier, expected archive bytes from docs/perf-tiers.tsv)
var cells = new (string Label, string Format, string Method, CompressionLevelOption Level, long Expect)[]
{
    ("zip-max-deflate", "zip", "Deflate", CompressionLevelOption.Max, 39_983_091),
    ("zip-normal-deflate", "zip", "Deflate", CompressionLevelOption.Normal, 40_710_824),
    ("zip-max-deflate64", "zip", "Deflate64", CompressionLevelOption.Max, 40_105_064),
    ("zip-max-lzma", "zip", "LZMA", CompressionLevelOption.Max, -1),
    ("7z-max-lzma2", "7z", "LZMA2", CompressionLevelOption.Max, 35_636_412),
    ("7z-normal-lzma2", "7z", "LZMA2", CompressionLevelOption.Normal, 35_641_550),
};

var failures = 0;
foreach (var cell in cells)
{
    var format = ArchiveFormatOption.All.Single(f =>
        string.Equals(f.CreateFormat, cell.Format, StringComparison.OrdinalIgnoreCase));
    var method = CompressionMethodOption.ForFormat(format).Single(m => m.EngineMethod == cell.Method);
    var target = Path.Combine(outDir, cell.Label + (cell.Format == "zip" ? ".zip" : ".7z"));
    if (File.Exists(target))
    {
        File.Delete(target);
    }

    var settings = new ArchiveCreationSettings(target, cell.Level, format,
        Method: method, TestAfterCreation: true);

    var sw = Stopwatch.StartNew();
    ArchiveCreator.Create(new[] { ArchiveInputItem.FromPath(data) }, settings);
    sw.Stop();

    var actual = new FileInfo(target).Length;
    var verdict = !check || cell.Expect < 0 ? "recorded" : (actual == cell.Expect ? "OK" : "MISMATCH");
    if (verdict == "MISMATCH")
    {
        failures++;
    }

    Console.WriteLine($"{cell.Label,-20} {sw.Elapsed.TotalSeconds,7:F2} s  out {actual,12}  " +
        $"tsv {cell.Expect,12}  {verdict}");
}

Console.WriteLine(failures == 0 ? "== all cells agree with docs/perf-tiers.tsv ==" : $"== {failures} cell(s) disagree ==");
return failures == 0 ? 0 : 1;
