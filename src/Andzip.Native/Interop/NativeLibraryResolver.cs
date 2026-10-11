using System.Reflection;
using System.Runtime.InteropServices;

namespace Andzip.Native.Interop;

/// <summary>
/// Locates <c>andzip_core</c> at runtime and registers itself as the assembly's
/// <see cref="DllImportResolver"/>. Probe order:
/// <list type="number">
///   <item><description>the <c>ANDZIP_CORE_PATH</c> env var (full file path, or a directory)</description></item>
///   <item><description><c>AppContext.BaseDirectory/runtimes/&lt;rid&gt;/native/&lt;file&gt;</c></description></item>
///   <item><description><c>AppContext.BaseDirectory/&lt;file&gt;</c></description></item>
///   <item><description>the runtime's default probing (returned as <see cref="IntPtr.Zero"/> so the loader falls back)</description></item>
/// </list>
/// Every path considered is recorded so a failure can name them all.
/// </summary>
internal static class NativeLibraryResolver
{
    public const string LibraryName = "andzip_core";

    private static readonly object Gate = new();
    private static IntPtr _cachedHandle;
    private static bool _attempted;
    private static List<string> _tried = new();

    /// <summary>OS-correct bare file name for the engine (no directory).</summary>
    public static string FileName
    {
        get
        {
            if (OperatingSystem.IsWindows()) return "andzip_core.dll";
            if (OperatingSystem.IsMacOS()) return "libandzip_core.dylib";
            return "libandzip_core.so";
        }
    }

    /// <summary>Registers the resolver. Safe to call more than once; called from
    /// <see cref="NativeMethods"/>'s static constructor so it runs before the first P/Invoke.</summary>
    public static void Register(Assembly assembly)
    {
        NativeLibrary.SetDllImportResolver(assembly, Resolve);
    }

    private static IntPtr Resolve(string libraryName, Assembly assembly, DllImportSearchPath? searchPath)
    {
        if (!string.Equals(libraryName, LibraryName, StringComparison.Ordinal))
            return IntPtr.Zero;

        // Reuse an already-resolved handle; otherwise do the probe. A null return
        // hands control to the runtime's default probing.
        return LoadOrZero();
    }

    /// <summary>
    /// Forces resolution now and throws a descriptive
    /// <see cref="AndzipLibraryNotFoundException"/> if nothing loaded. Public entry
    /// points call this so callers get the full "paths tried" message instead of the
    /// runtime's generic <see cref="DllNotFoundException"/>.
    /// </summary>
    public static IntPtr EnsureLoaded()
    {
        var handle = LoadOrZero();
        if (handle != IntPtr.Zero)
            return handle;

        // Default probing may still succeed at the actual call site; treat a total
        // miss here as a hard error with the diagnostics the resolver collected.
        throw new AndzipLibraryNotFoundException(BuildTriedPaths());
    }

    private static IntPtr LoadOrZero()
    {
        lock (Gate)
        {
            if (_cachedHandle != IntPtr.Zero)
                return _cachedHandle;

            if (!_attempted)
            {
                _attempted = true;
                _tried = new List<string>();
                TryEachCandidate();
            }

            return _cachedHandle;
        }
    }

    private static void TryEachCandidate()
    {
        foreach (var candidate in EnumerateCandidates())
        {
            _tried.Add(candidate);
            if (File.Exists(candidate) && NativeLibrary.TryLoad(candidate, out var handle))
            {
                _cachedHandle = handle;
                return;
            }
        }
    }

    private static IEnumerable<string> EnumerateCandidates()
    {
        // (a) Explicit override from the environment. Accept either a full file
        // path or a directory containing the engine.
        var env = Environment.GetEnvironmentVariable("ANDZIP_CORE_PATH");
        if (!string.IsNullOrWhiteSpace(env))
        {
            if (Directory.Exists(env))
                yield return Path.Combine(env, FileName);
            else
                yield return env;
        }

        // (b) Rider-specific staged copy.
        string rid;
        try
        {
            rid = RuntimeInformation.RuntimeIdentifier;
        }
        catch
        {
            rid = RuntimeInformation.ProcessArchitecture switch
            {
                Architecture.X64 => OperatingSystem.IsWindows() ? "win-x64"
                                 : OperatingSystem.IsMacOS() ? "osx-x64" : "linux-x64",
                Architecture.Arm64 => OperatingSystem.IsWindows() ? "win-arm64"
                                   : OperatingSystem.IsMacOS() ? "osx-arm64" : "linux-arm64",
                _ => "native"
            };
        }

        yield return Path.Combine(AppContext.BaseDirectory, "runtimes", rid, "native", FileName);

        // (c) Sitting directly beside the entry assembly.
        yield return Path.Combine(AppContext.BaseDirectory, FileName);
    }

    private static IReadOnlyList<string> BuildTriedPaths()
    {
        var list = new List<string>(_tried);
        list.Add("(system default native-library probing)");
        return list;
    }
}
