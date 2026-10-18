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
//
// The two Deflate rows changed in 5.7.0 because the encoder behind method 0x40108 became
// zlib-ng's (docs/perf-p2c-deflate-swap.md): UltraFast/Fast are one package still, Max is
// now the smallest of the ladder, and 7z's own Deflate member is listed too because the
// same id serves both containers -- measuring only the ZIP side would leave half the swap
// unwatched.
//
// The two 7z/LZMA2 rows (Fast and Normal) moved in 5.8.0: those two tiers now send a block
// size ("c=4m" from TierExtras). The reason is that LZMA2 parallelises per *block* while the
// AUTO block at x5 is 128 MiB -- exactly one block for a 128 MiB input, so no thread count
// could help. Normal prints 1.57 s for 36 713 343 bytes, +3.0% over the 35 641 550 that
// docs/perf-tiers.tsv recorded; the command-line witness is docs/perf-mt.tsv, sweep lzma2bs,
// "mixed-128 LZMA2 x5 mt=16": 9.05 s -> 1.28 s at the same 36 713 343 bytes. Same bytes from
// the two entries (product path and azcore) is the point of this file, and the 0.29 s between
// 1.28 and 1.57 is TestAfterCreation reading the package back.
// Fast is the row worth looking at: there the block equals the tier's own dictionary (4 MiB),
// so nothing is given up -- 37 107 043 is *smaller* than the 37 144 537 that same tier made
// without it (docs/perf-mt.tsv, x3 auto vs x3 c=4m).
// Max (x9) stays untouched on purpose: that tier promises the smallest package, and the same
// block costs 3.0% of it (docs/perf-mt.tsv, level 9: 0.26557 -> 0.27355 at c=4m).
var cells = new (string Label, string Format, string Method, CompressionLevelOption Level, long Expect)[]
{
    ("zip-ultrafast-deflate", "zip", "Deflate", CompressionLevelOption.UltraFast, 40_421_017),
    ("zip-max-deflate", "zip", "Deflate", CompressionLevelOption.Max, 39_768_264),
    ("zip-normal-deflate", "zip", "Deflate", CompressionLevelOption.Normal, 39_783_086),
    ("zip-max-deflate64", "zip", "Deflate64", CompressionLevelOption.Max, 40_105_064),
    ("zip-max-lzma", "zip", "LZMA", CompressionLevelOption.Max, -1),
    ("7z-max-deflate", "7z", "Deflate", CompressionLevelOption.Max, 39_768_234),
    ("7z-max-lzma2", "7z", "LZMA2", CompressionLevelOption.Max, 35_636_412),
    ("7z-normal-lzma2", "7z", "LZMA2", CompressionLevelOption.Normal, 36_713_343),
    ("7z-fast-lzma2", "7z", "LZMA2", CompressionLevelOption.Fast, 37_107_043),
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
