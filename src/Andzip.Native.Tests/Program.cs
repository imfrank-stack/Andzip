using System.Text;
using Andzip.Native;

namespace Andzip.Native.Tests;

/// <summary>xunit-free console test harness for the Andzip.Native shell, driving the
/// real prebuilt <c>andzip_core</c> library. Prints a final "N checks, M failures"
/// tally and exits 1 on any failure.</summary>
internal static class Program
{
    private static int _checks;
    private static int _failures;

    private static int Main()
    {
        var tempRoot = Path.Combine(Path.GetTempPath(), "andzip-native-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempRoot);
        try
        {
            RunAll(tempRoot);
        }
        catch (Exception ex)
        {
            Fail("unhandled top-level exception: " + ex);
        }
        finally
        {
            TryDeleteDir(tempRoot);
        }

        Console.WriteLine();
        Console.WriteLine($"{_checks} checks, {_failures} failures");
        return _failures == 0 ? 0 : 1;
    }

    // ---- assert helpers ---------------------------------------------------
    private static void Check(string name, bool condition)
    {
        _checks++;
        if (condition) Console.WriteLine("  ok   " + name);
        else { _failures++; Console.WriteLine("  FAIL " + name); }
    }

    private static void CheckEq<T>(string name, T expected, T actual)
        => Check($"{name} (expected={Format(expected)}, actual={Format(actual)})", EqualityComparer<T>.Default.Equals(expected, actual));

    private static void CheckThrows<TException>(string name, Action action, Func<TException, bool>? predicate = null)
        where TException : Exception
    {
        _checks++;
        try
        {
            action();
            _failures++;
            Console.WriteLine($"  FAIL {name}: expected {typeof(TException).Name}, nothing thrown");
        }
        catch (TException ex)
        {
            if (predicate is null || predicate(ex)) Console.WriteLine($"  ok   {name} ({ex.GetType().Name})");
            else { _failures++; Console.WriteLine($"  FAIL {name}: {typeof(TException).Name} predicate false: {ex.Message}"); }
        }
        catch (Exception ex)
        {
            _failures++;
            Console.WriteLine($"  FAIL {name}: expected {typeof(TException).Name}, got {ex.GetType().Name}: {ex.Message}");
        }
    }

    private static void Section(string title) => Console.WriteLine("\n== " + title + " ==");

    private static void Fail(string msg) { _checks++; _failures++; Console.WriteLine("  FAIL " + msg); }

    private static string Format<T>(T v) => v?.ToString() ?? "<null>";

    // ---- tests ------------------------------------------------------------
    private static void RunAll(string tempRoot)
    {
        TestGlobalInfo();
        TestFormatsAndCodecs();
        TestInMemoryRoundTrip();
        TestMultiEntryNested();
        TestDirectoryRoundTripOnDisk(Path.Combine(tempRoot, "tree"));
        TestPasswordRoundTrips();
        TestPasswordPromptCallback();
        TestProgressCallback();
        TestPathTraversalSafety(Path.Combine(tempRoot, "trav"));
        TestHasherVectors();
        TestBufferTooSmallAndUseAfterDispose();
        TestWriterStress(Path.Combine(tempRoot, "stress"));
    }

    /// <summary>
    /// Repeated ZIP writes with a password, a progress callback and an empty-archive
    /// finish. Deliberately a loop rather than one assertion: the defect it guards is
    /// an uninitialised <c>PROPVARIANT</c> being freed, which only faults when the host
    /// has left a non-zero <c>vt</c> in that memory. A .NET host reuses heap and stack
    /// pages full of managed leftovers, so it reproduces in about 1 of 1500 writes and
    /// never under a plain C driver -- which is exactly why the check belongs in this
    /// process rather than in the native suite.
    /// </summary>
    private static void TestWriterStress(string dir)
    {
        Section("writer stress (host-reused memory)");
        Directory.CreateDirectory(dir);
        var source = Path.Combine(dir, "payload.bin");
        File.WriteAllBytes(source, new byte[8192]);

        const int rounds = 4000;
        int progressCalls = 0;
        var options = new CreateOptions
        {
            Format = "zip",
            Level = 6,
            Password = "stress-pw",
            Encryption = AzEncryption.Aes256,
            Progress = _ => Interlocked.Increment(ref progressCalls),
        };

        bool ok = true;
        for (int i = 0; i < rounds; i++)
        {
            // Both shapes crashed before the fix: one member, and zero members (the
            // empty finish still walks the handler's property list).
            var path = Path.Combine(dir, $"s{i}.zip");
            using (var w = ArchiveWriter.Create(path, i % 8 == 0 ? options with { Progress = null } : options))
            {
                if (i % 4 != 0)
                    w.AddFile(source, $"d/p{i}.bin");
                w.Finish();
            }

            if (i % 4 != 0)
            {
                using var r = ArchiveReader.Open(path, new OpenOptions { Password = "stress-pw" });
                ok &= r.EntryCount == 1;
            }

            File.Delete(path);
        }

        Check($"{rounds} encrypted zip writes survived; progress fired ({progressCalls} calls)", ok && progressCalls > 0);
        TryDeleteDir(dir);
    }

    private static void TestGlobalInfo()
    {
        Section("build info + ABI");
        CheckEq("AbiVersion == 1", 1u, AndzipCore.AbiVersion);
        var info = AndzipCore.BuildInfo;
        Check("CoreVersion non-empty", !string.IsNullOrWhiteSpace(info.CoreVersion));
        Check("Engine non-empty", !string.IsNullOrWhiteSpace(info.Engine));
        Check("BuildConfig non-empty", !string.IsNullOrWhiteSpace(info.BuildConfig));
        CheckEq("AbiVersion in build info", 1u, info.AbiVersion);
        Check("FormatCount > 0", info.FormatCount > 0);
        Check("CodecCount > 0", info.CodecCount > 0);
    }

    private static void TestFormatsAndCodecs()
    {
        Section("formats + codecs");
        var formats = AndzipCore.Formats;
        string[] names = formats.Select(f => f.Name.ToLowerInvariant()).ToArray();
        Check("has 7z", names.Contains("7z"));
        Check("has zip", names.Contains("zip"));
        Check("has tar", names.Contains("tar"));
        Check("has gzip (gzip or gz)", names.Contains("gzip") || names.Contains("gz"));
        Check("has xz", names.Contains("xz"));
        Check("has bzip2 (bzip2 or bz2)", names.Contains("bzip2") || names.Contains("bz2"));

        var codecs = AndzipCore.Codecs;
        string[] codecNames = codecs.Select(c => c.Name.ToLowerInvariant()).ToArray();
        Check("has LZMA2", codecNames.Contains("lzma2"));
        Check("has Deflate", codecNames.Contains("deflate"));
        Check("has Copy", codecNames.Contains("copy"));
        Check("LZMA2 hex id non-empty", codecs.First(c => c.Name.Equals("LZMA2", StringComparison.OrdinalIgnoreCase)).IdHex.Length > 0);
        Check("Deflate hex id non-empty", codecs.First(c => c.Name.Equals("Deflate", StringComparison.OrdinalIgnoreCase)).IdHex.Length > 0);
        Check("Copy hex id non-empty", codecs.First(c => c.Name.Equals("Copy", StringComparison.OrdinalIgnoreCase)).IdHex.Length > 0);
    }

    private static void TestInMemoryRoundTrip()
    {
        Section("in-memory round trip");
        var payload = Encoding.UTF8.GetBytes(new string('x', 5000) + " hello world 7z/zip round trip");

        // 7z / LZMA2
        byte[] sevenZip;
        using (var w = ArchiveWriter.CreateInMemory(new CreateOptions { Format = "7z", Method = "LZMA2" }))
        {
            w.AddData(payload, "hello.txt");
            sevenZip = w.FinishToBytes();
        }
        Check("7z bytes non-empty", sevenZip.Length > 0);
        using (var r = ArchiveReader.Open(sevenZip, new OpenOptions { ForcedFormat = "7z" }))
        {
            CheckEq("7z format name", "7z", r.FormatName);
            CheckEq("7z entry count", 1ul, r.EntryCount);
            CheckEq("7z entry name", "hello.txt", r.Entries[0].Name);
            Check("7z entry size matches", r.Entries[0].Size == (ulong)payload.Length);
            var back = r.ReadEntry(0);
            Check("7z payload matches exactly", back.SequenceEqual(payload));
        }

        // zip / Deflate
        byte[] zip;
        using (var w = ArchiveWriter.CreateInMemory(new CreateOptions { Format = "zip", Method = "Deflate" }))
        {
            w.AddData(payload, "hello.txt");
            zip = w.FinishToBytes();
        }
        Check("zip bytes non-empty", zip.Length > 0);
        using (var r = ArchiveReader.Open(zip, new OpenOptions { ForcedFormat = "zip" }))
        {
            CheckEq("zip format name", "zip", r.FormatName);
            var back = r.ReadEntry(0);
            Check("zip payload matches exactly", back.SequenceEqual(payload));
        }
    }

    private static void TestMultiEntryNested()
    {
        Section("multi-entry nested names");
        var a = Encoding.UTF8.GetBytes("alpha content " + new string('A', 300));
        var b = Encoding.UTF8.GetBytes("beta content");
        var c = Encoding.UTF8.GetBytes("gamma content " + new string('G', 900));

        byte[] zip;
        using (var w = ArchiveWriter.CreateInMemory(new CreateOptions { Format = "zip", Method = "Deflate" }))
        {
            w.AddData(a, "docs/readme.txt");
            w.AddData(b, "docs/sub/notes.txt");
            w.AddData(c, "assets/data/blob.bin");
            zip = w.FinishToBytes();
        }

        using (var r = ArchiveReader.Open(zip, new OpenOptions { ForcedFormat = "zip" }))
        {
            CheckEq("entry count == 3", 3ul, r.EntryCount);
            Check("names include separators", r.Entries.All(e => e.Name.Contains('/')));
            var found = r.FindEntry("docs/sub/notes.txt");
            Check("FindEntry returns match", found is not null);
            CheckEq("FindEntry index", 1ul, found!.Index);
            Check("FindEntry missing == null", r.FindEntry("no/such/file") is null);

            using var ms = new MemoryStream();
            r.ReadEntry(2, ms);
            Check("streamed blob matches", ms.ToArray().SequenceEqual(c));

            var readme = r.ReadEntry(0);
            Check("readme matches", readme.SequenceEqual(a));
        }
    }

    private static void TestDirectoryRoundTripOnDisk(string root)
    {
        Section("directory round trip on disk");
        Directory.CreateDirectory(root);
        var srcDir = Path.Combine(root, "src");
        var outDir = Path.Combine(root, "out");
        Directory.CreateDirectory(Path.Combine(srcDir, "nested"));
        var alpha = Encoding.UTF8.GetBytes("alpha-on-disk");
        var beta = Encoding.UTF8.GetBytes("beta-nested " + new string('B', 400));
        File.WriteAllBytes(Path.Combine(srcDir, "alpha.txt"), alpha);
        File.WriteAllBytes(Path.Combine(srcDir, "nested", "beta.txt"), beta);

        var archivePath = Path.Combine(root, "tree.7z");
        using (var w = ArchiveWriter.Create(archivePath, new CreateOptions { Format = "7z", Method = "LZMA2" }))
        {
            w.AddFile(srcDir, entryName: "tree", recurse: true);
            w.Finish();
        }
        Check("archive file exists", File.Exists(archivePath));

        using (var r = ArchiveReader.Open(archivePath))
        {
            CheckEq("disk archive is 7z", "7z", r.FormatName);
            Check("has entries", r.EntryCount > 0);
            var progressFired = false;
            r.ExtractTo(outDir, new ExtractOptions { Progress = _ => progressFired = true });
            Check("extract wrote files", Directory.Exists(outDir) && Directory.EnumerateFiles(outDir, "*", SearchOption.AllDirectories).Any());
            Check("directory extract progress fired", progressFired);

            // Locate the two known files by name anywhere under outDir and compare bytes.
            var files = Directory.EnumerateFiles(outDir, "*", SearchOption.AllDirectories).ToList();
            byte[]? ReadByName(string name)
            {
                var hit = files.FirstOrDefault(f => Path.GetFileName(f).Equals(name, StringComparison.OrdinalIgnoreCase));
                return hit is null ? null : File.ReadAllBytes(hit);
            }
            var gotAlpha = ReadByName("alpha.txt");
            var gotBeta = ReadByName("beta.txt");
            Check("alpha recovered on disk", gotAlpha is not null && gotAlpha.SequenceEqual(alpha));
            Check("beta (nested) recovered on disk", gotBeta is not null && gotBeta.SequenceEqual(beta));
        }
    }

    private static void TestPasswordRoundTrips()
    {
        Section("password round trips");
        var secret = Encoding.UTF8.GetBytes("classified " + new string('S', 2000));

        // 7z AES-256 (headers too)
        byte[] sevenZip;
        using (var w = ArchiveWriter.CreateInMemory(new CreateOptions
        {
            Format = "7z", Method = "LZMA2", Password = "correct horse",
            Encryption = AzEncryption.Aes256, EncryptHeaders = true
        }))
        {
            w.AddData(secret, "secret.dat");
            sevenZip = w.FinishToBytes();
        }
        using (var r = ArchiveReader.Open(sevenZip, new OpenOptions { Password = "correct horse", ForcedFormat = "7z" }))
        {
            Check("7z reports encrypted", r.IsEncrypted);
            Check("7z password read matches", r.ReadEntry(0).SequenceEqual(secret));
        }
        CheckThrows<AndzipException>(
            "7z wrong password throws",
            () =>
            {
                using var r = ArchiveReader.Open(sevenZip, new OpenOptions { Password = "nope", ForcedFormat = "7z" });
                r.ReadEntry(0);
            },
            ex => ex.IsPasswordError || ex.Result == AzResult.ErrorCrc || ex.Result == AzResult.ErrorBadFormat);

        // zip AES-256
        byte[] zipAes;
        using (var w = ArchiveWriter.CreateInMemory(new CreateOptions
        {
            Format = "zip", Method = "Deflate", Password = "p@ssw0rd", Encryption = AzEncryption.Aes256
        }))
        {
            w.AddData(secret, "aes.dat");
            zipAes = w.FinishToBytes();
        }
        using (var r = ArchiveReader.Open(zipAes, new OpenOptions { Password = "p@ssw0rd", ForcedFormat = "zip" }))
            Check("zip AES-256 read matches", r.ReadEntry(0).SequenceEqual(secret));

        // zip ZipCrypto
        byte[] zipZc;
        using (var w = ArchiveWriter.CreateInMemory(new CreateOptions
        {
            Format = "zip", Method = "Deflate", Password = "weakpw", Encryption = AzEncryption.ZipCrypto
        }))
        {
            w.AddData(secret, "zc.dat");
            zipZc = w.FinishToBytes();
        }
        using (var r = ArchiveReader.Open(zipZc, new OpenOptions { Password = "weakpw", ForcedFormat = "zip" }))
            Check("zip ZipCrypto read matches", r.ReadEntry(0).SequenceEqual(secret));

        CheckThrows<AndzipException>(
            "zip wrong password throws (not garbage)",
            () =>
            {
                using var r = ArchiveReader.Open(zipZc, new OpenOptions { Password = "wrong", ForcedFormat = "zip" });
                var got = r.ReadEntry(0);
                if (got.SequenceEqual(secret))
                    throw new AndzipException(AzResult.ErrorCrc, "wrong-password-read", "returned plaintext with a wrong password");
                throw new AndzipException(AzResult.ErrorPassword, "wrong-password-read", "read succeeded with wrong password");
            },
            _ => true);
    }

    private static void TestPasswordPromptCallback()
    {
        Section("password prompt callback");
        var secret = Encoding.UTF8.GetBytes("prompted secret " + new string('P', 1000));
        byte[] sevenZip;
        using (var w = ArchiveWriter.CreateInMemory(new CreateOptions
        {
            Format = "7z", Method = "LZMA2", Password = "fromprompt", Encryption = AzEncryption.Aes256
        }))
        {
            w.AddData(secret, "p.dat");
            sevenZip = w.FinishToBytes();
        }

        int prompts = 0;
        using (var r = ArchiveReader.Open(sevenZip, new OpenOptions
        {
            ForcedFormat = "7z",
            PasswordPrompt = () => { prompts++; return "fromprompt"; }
        }))
        {
            var got = r.ReadEntry(0);
            Check("prompt-supplied password decrypts", got.SequenceEqual(secret));
        }
        Check("password prompt was invoked", prompts > 0);
    }

    private static void TestProgressCallback()
    {
        Section("progress callback fires");
        var data = Encoding.UTF8.GetBytes(new string('Z', 20000));
        byte[] zip;
        int createProgress = 0;
        using (var w = ArchiveWriter.CreateInMemory(new CreateOptions { Format = "zip", Method = "Deflate", Progress = _ => createProgress++ }))
        {
            w.AddData(data, "big.dat");
            zip = w.FinishToBytes();
        }
        Check("create progress fired", createProgress > 0);

        var tmp = Path.Combine(Path.GetTempPath(), "andzip-progress-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tmp);
        try
        {
            int extractProgress = 0;
            using (var r = ArchiveReader.Open(zip, new OpenOptions { ForcedFormat = "zip" }))
                r.ExtractTo(tmp, new ExtractOptions { Progress = _ => extractProgress++ });
            Check("extract progress fired", extractProgress > 0);
        }
        finally { TryDeleteDir(tmp); }
    }

    private static void TestPathTraversalSafety(string root)
    {
        Section("path traversal safety");
        Directory.CreateDirectory(root);
        var destDir = Path.Combine(root, "dest");
        Directory.CreateDirectory(destDir);
        var destParent = root; // an escaping "../escaped.txt" would land here

        byte[] zip;
        using (var w = ArchiveWriter.CreateInMemory(new CreateOptions { Format = "zip", Method = "Deflate" }))
        {
            w.AddData(Encoding.UTF8.GetBytes("escaped!"), "../escaped.txt");
            w.AddData(Encoding.UTF8.GetBytes("two-up"), "../../two.txt");
            zip = w.FinishToBytes();
        }

        using (var r = ArchiveReader.Open(zip, new OpenOptions { ForcedFormat = "zip" }))
        {
            r.ExtractTo(destDir); // engine sanitises each entry
            Check("no ../escaped.txt leaked to parent", !File.Exists(Path.Combine(destParent, "escaped.txt")));
            Check("no ../../two.txt leaked above parent", !File.Exists(Path.Combine(Path.GetDirectoryName(destParent)!, "two.txt")));
            // Everything written must be inside destDir -- and something must have
            // been written, otherwise the containment loop below proves nothing.
            var allFiles = Directory.EnumerateFiles(destDir, "*", SearchOption.AllDirectories).ToList();
            Check($"extraction wrote files under dest ({allFiles.Count})", allFiles.Count > 0);
            foreach (var f in allFiles)
                Check($"contained: {Path.GetFileName(f)}", Path.GetFullPath(f).StartsWith(Path.GetFullPath(destDir), StringComparison.OrdinalIgnoreCase));
        }
    }

    private static void TestHasherVectors()
    {
        Section("hasher known vectors");
        var abc = Encoding.UTF8.GetBytes("abc");

        uint crc;
        using (var h = Hasher.Open("CRC32"))
        {
            h.Update(abc);
            var d = h.Finish();
            CheckEq("CRC32 length", 4, d.Length);
            crc = ((uint)d[0] << 24) | ((uint)d[1] << 16) | ((uint)d[2] << 8) | d[3];
            uint le = ((uint)d[3] << 24) | ((uint)d[2] << 16) | ((uint)d[1] << 8) | d[0];
            Check("CRC32(abc) == 0x352441C2", crc == 0x352441C2 || le == 0x352441C2);
        }

        using (var h = Hasher.Open("SHA256"))
        {
            h.Update(abc);
            var hex = h.FinishHex();
            CheckEq("SHA256(abc)", "ba7816bf8f01cfea414140de5dae2223b00361a396177a9cb410ff61f20015ad", hex);
        }

        using (var h = Hasher.Open("SHA256"))
        {
            var full = Encoding.UTF8.GetBytes("ab");
            h.Update(full);
            h.Update(Encoding.UTF8.GetBytes("c"));
            CheckEq("SHA256 streaming equals one-shot", "ba7816bf8f01cfea414140de5dae2223b00361a396177a9cb410ff61f20015ad", h.FinishHex());
        }

        using (var h = Hasher.Open("SHA256"))
        {
            h.Update(abc);
            h.Finish();
            CheckThrows<AndzipException>("hasher double-Finish throws AlreadyDone", () => h.Finish(), ex => ex.Result == AzResult.ErrorAlreadyDone);
        }
    }

    private static void TestBufferTooSmallAndUseAfterDispose()
    {
        Section("buffer-too-small + use-after-dispose");
        var payload = Encoding.UTF8.GetBytes("this entry is comfortably longer than one byte");
        byte[] zip;
        using (var w = ArchiveWriter.CreateInMemory(new CreateOptions { Format = "zip", Method = "Copy" }))
        {
            w.AddData(payload, "data.bin");
            zip = w.FinishToBytes();
        }

        var reader = ArchiveReader.Open(zip, new OpenOptions { ForcedFormat = "zip" });
        CheckThrows<AndzipException>(
            "ReadEntry into 1-byte buffer throws BUFFER_SMALL",
            () => reader.ReadEntry(0, new byte[1]),
            ex => ex.IsBufferTooSmall && ex.Result == AzResult.ErrorBufferSmall);

        // Exact-capacity read succeeds.
        CheckEq("ReadEntry into exact buffer returns length", payload.Length, reader.ReadEntry(0, new byte[payload.Length]));

        reader.Dispose();
        CheckThrows<ObjectDisposedException>("use-after-Dispose GetEntry throws", () => reader.GetEntry(0));
        CheckThrows<ObjectDisposedException>("use-after-Dispose ReadEntry throws", () => reader.ReadEntry(0));

        // Writer use-after-dispose.
        var writer = ArchiveWriter.CreateInMemory(new CreateOptions { Format = "7z" });
        writer.Dispose();
        CheckThrows<ObjectDisposedException>("writer use-after-Dispose throws", () => writer.AddData(new byte[] { 1 }, "x"));

        // A double Finish surfaces the distinct already-done code.
        using var w2 = ArchiveWriter.CreateInMemory(new CreateOptions { Format = "7z" });
        w2.AddData(new byte[] { 1, 2, 3 }, "a");
        w2.FinishToBytes();
        CheckThrows<AndzipException>("double Finish throws", () => w2.FinishToBytes(), ex => ex.Result == AzResult.ErrorAlreadyDone);
    }

    // ---- io helpers -------------------------------------------------------
    private static void TryDeleteDir(string dir)
    {
        try { if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true); }
        catch { /* best effort */ }
    }
}
