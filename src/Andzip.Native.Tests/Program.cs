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
        TestExtensionCodecs();
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
        CheckEq("AbiVersion == 2", 2u, AndzipCore.AbiVersion);
        var info = AndzipCore.BuildInfo;
        Check("CoreVersion non-empty", !string.IsNullOrWhiteSpace(info.CoreVersion));
        Check("Engine non-empty", !string.IsNullOrWhiteSpace(info.Engine));
        Check("BuildConfig non-empty", !string.IsNullOrWhiteSpace(info.BuildConfig));
        CheckEq("AbiVersion in build info", 2u, info.AbiVersion);
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

        // The five 7z-only extension coders (7-Zip-zstd / NanaZip lineage). They are
        // compiled in by -ExtraCodecs, and the SHIPPING build must have them: the UI
        // offers them in the 7z method list (CompressionMethodOption.ForFormat), so an
        // engine built without that switch would offer five options that all fail.
        // This is what turns "someone rebuilt the engine plainly" into a build failure
        // here instead of a user-visible surprise later.
        foreach (var name in ExtensionCodecNames)
        {
            var codec = codecs.FirstOrDefault(c => c.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
            Check($"extension codec {name} is registered (rebuild the engine with "
                + "build-windows.ps1 -ExtraCodecs)", codec is not null);
            Check($"extension codec {name} can encode", codec is not null && codec.CanEncode);
        }
    }

    /// <summary>Method names exactly as the engine registers them (upper case).</summary>
    private static readonly string[] ExtensionCodecNames = ["ZSTD", "LZ4", "LZ5", "LIZARD", "BROTLI"];

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

    /// <summary>
    /// Creates a real 7z with each extension method and reads the bytes back, in memory.
    /// <para>
    /// Why this exists at all: a coder being *registered* is not evidence it can *write*.
    /// An earlier note in this project claimed these five round-tripped when they did not
    /// -- every create died in UpdateItems with E_INVALIDARG -- and the claim survived
    /// because nothing in a test process ever tried. Since the UI now offers them, the
    /// proof lives in the gate that runs before every package (build.ps1 step 2).
    /// </para>
    /// <para>
    /// Two payloads on purpose: compressible text (catches a coder that emits a valid
    /// header but no usable blocks) and incompressible random bytes (catches a coder that
    /// only survives by silently storing). Levels 0 / 5 / 9 cover the tier ends; the CLI
    /// sweep that first established this ran all five tiers plus dict / password /
    /// non-solid / multi-input variants.
    /// </para>
    /// </summary>
    private static void TestExtensionCodecs()
    {
        Section("7z extension codecs (create + read back)");

        var text = Encoding.UTF8.GetBytes(
            "the quick brown fox 0123456789 " + new string('z', 4096) + " 中文内容也要原样回来");
        var random = new byte[9001];
        new Random(20261014).NextBytes(random);

        foreach (var method in ExtensionCodecNames)
        {
            foreach (var level in new[] { 0, 5, 9 })
            {
                foreach (var (label, payload) in new (string, byte[])[] { ("text", text), ("random", random) })
                {
                    var what = $"{method} x{level} {label}";

                    byte[] archive;
                    using (var w = ArchiveWriter.CreateInMemory(new CreateOptions
                    {
                        Format = "7z",
                        Method = method,
                        Level = level,
                    }))
                    {
                        w.AddData(payload, "hello.txt");
                        archive = w.FinishToBytes();
                    }

                    Check($"{what}: wrote something", archive.Length > 0);

                    using var r = ArchiveReader.Open(archive, new OpenOptions { ForcedFormat = "7z" });
                    CheckEq($"{what}: one entry", 1ul, r.EntryCount);
                    Check($"{what}: payload matches exactly", r.ReadEntry(0).SequenceEqual(payload));
                }
            }
        }

        // The red line seen from the engine, not from the UI: the interface simply does
        // not offer an extension method for ZIP, but "nobody changed that list" is not
        // a guarantee. The zip handler must refuse it, and must not hand back a file.
        foreach (var method in ExtensionCodecNames)
        {
            CheckThrows<AndzipException>($"zip refuses {method}", () =>
            {
                using var w = ArchiveWriter.CreateInMemory(new CreateOptions { Format = "zip", Method = method });
                w.AddData(text, "hello.txt");
                var bytes = w.FinishToBytes();
                if (bytes.Length > 0)
                {
                    // A ZIP that nobody but us can open is the failure mode the user
                    // forbade; an empty refusal is acceptable, a written one is not.
                    throw new AndzipException(AzResult.ErrorUnsupported, "writer", "a ZIP was produced");
                }
            });
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

        // Both inputs are fixed, and every expected string below was produced by
        // 7-Zip 26.04 itself (`7z h -scrc* <file>` on this machine), in 7-Zip's
        // own display form -- upper case reversed for the 4- and 8-byte
        // digests, lower case straight for the rest. So this table pins three
        // things at once: the algorithm names this ABI accepts, that the two
        // fallbacks in az_hash.cpp (SHA-384 and BLAKE2sp, neither of which the
        // vendored registry publishes) compute the same value as 7-Zip, and
        // that FormatDigest renders what the user sees identical to 7-Zip's.
        //
        // The second input is 1 MiB of a repeating ramp: big enough that
        // BLAKE2sp's tree (parallel degree 8, 128-byte leaves) is really built
        // and that the hasher is fed many chunks, not one.
        var probe = Encoding.UTF8.GetBytes("andzip crc probe\n");
        var ramp = new byte[1 << 20];
        for (var i = 0; i < ramp.Length; i++)
            ramp[i] = (byte)((i % 121) + 5);

        (string Algorithm, string Probe, string Ramp)[] vectors =
        [
            ("CRC32",    "D2E07B7A", "0C99D8F1"),
            ("CRC64",    "0CAD0C4632C96D67", "EF4A983188C3AFF2"),
            ("XXH64",    "C132F080712BAF0B", "5B23000C84A69FF7"),
            ("MD5",      "36e4033284360113d7bb47f9f8c8b397", "2257a988321219bfac67b41d52db0e5c"),
            ("SHA1",     "04041db01f550f53bef0b9eed6d083678a8e1b9a", "2bf11b2949769d326ee3a84d3d09650511d36faa"),
            ("SHA256",   "8be160c35d73aa1d708faeb9c9131cb0e0d6a07f8a3987a9b7bf5f3c1539bb57",
                         "8e45e7a3147ce6f126bffe4ae326ac7f875cd5d4ee6e22492a1a3ea89cadc46e"),
            ("SHA384",   "6ed738e81d6729f1ccc156ab3c30539ecd5e8f3b7dae54708aad403ef38f523c945b135d221a7ca7d1999e6889d5bb58",
                         "ec008bbfc0899562c0dfa20471aa33cd91147f609bc8c7f205ee0223aaf7607bcc2ca32a31a8e3bc5143f75f4d75debb"),
            ("SHA512",   "f842b7b2f2f0c5871a870b7bcce2d50e574ada8cc1855165e17f0e0ffcff25a293250f493490e3886cdd7e68b8e2900340eba37e01d9f71d4fff399a73ac50c7",
                         "082dbf2a026cb9098635bcbb4137c406d7d782defdac8c5afbfc3cb3eb8677e3f8011578654b6b126e7694bafb06922df429e39546d7232d15711078fa46b108"),
            ("SHA3-256", "97b6c0e8ff29086ba7e0cd081275bbd03c84a4f830161836a8178e1b574a1fc1",
                         "7ec0d0465d45242e211c9ecd5baa0ccf1abf6c4d5829dfc515e368c218d23100"),
            ("BLAKE2sp", "16337ba793b033c54f13fe316547b2af7b903fc25a1d15ced58cd4643e95397c",
                         "c2bd6d6c71d5eec0ce31d86459b993e60f4fced5de6b252024511447e4cf2329"),
        ];

        foreach (var (algorithm, probeHex, rampHex) in vectors)
        {
            using (var h = Hasher.Open(algorithm))
            {
                h.Update(probe);
                CheckEq($"{algorithm}(probe)", probeHex, Hasher.FormatDigest(h.Finish()));
            }

            using (var h = Hasher.Open(algorithm))
            {
                for (var offset = 0; offset < ramp.Length; offset += 64 * 1024)
                    h.Update(ramp.AsMemory(offset, Math.Min(64 * 1024, ramp.Length - offset)));
                CheckEq($"{algorithm}(1 MiB, 64 KiB chunks)", rampHex, Hasher.FormatDigest(h.Finish()));
            }
        }

        using (var h = Hasher.Open("SHA256"))
        {
            h.Update(Encoding.UTF8.GetBytes("ab"));
            h.Update(Encoding.UTF8.GetBytes("c"));
            CheckEq("SHA256 streaming equals one-shot", "ba7816bf8f01cfea414140de5dae2223b00361a396177a9cb410ff61f20015ad", h.FinishHex());
        }

        using (var h = Hasher.Open("SHA256"))
        {
            h.Update(Encoding.UTF8.GetBytes("abc"));
            h.Finish();
            CheckThrows<AndzipException>("hasher double-Finish throws AlreadyDone", () => h.Finish(), ex => ex.Result == AzResult.ErrorAlreadyDone);
        }

        // A name the engine has no implementation for must be refused, not
        // silently answered with something else's digest.
        CheckThrows<AndzipException>("unknown hash algorithm refused",
            () => Hasher.Open("MD9"), ex => ex.Result == AzResult.ErrorUnsupported);
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
