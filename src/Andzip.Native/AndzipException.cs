using System.Runtime.InteropServices;

namespace Andzip.Native;

/// <summary>
/// Thrown for every non-<see cref="AzResult.Ok"/> return from the native layer.
/// Carries the numeric <see cref="Result"/> and the thread-local message from
/// <c>az_last_error_message()</c> captured at the point of failure (the native
/// message is only valid until the next call on the same thread, so it is copied
/// eagerly here).
/// </summary>
public class AndzipException : Exception
{
    /// <summary>The native result code that triggered this exception.</summary>
    public AzResult Result { get; }

    /// <summary>The raw UTF-8 message from <c>az_last_error_message()</c>, if any.</summary>
    public string NativeMessage { get; }

    /// <summary>True when a callback aborted the operation.</summary>
    public bool IsCancelled => Result == AzResult.ErrorCancelled;

    /// <summary>True when the caller's buffer was too small — retry with more room.</summary>
    public bool IsBufferTooSmall => Result == AzResult.ErrorBufferSmall;

    /// <summary>True when the archive was encrypted and the password was missing/wrong.</summary>
    public bool IsPasswordError => Result == AzResult.ErrorPassword;

    public AndzipException(AzResult result, string operation, string? nativeMessage)
        : base(Describe(result, operation, nativeMessage))
    {
        Result = result;
        NativeMessage = nativeMessage ?? string.Empty;
    }

    private static string Describe(AzResult result, string operation, string? nativeMessage)
    {
        var detail = string.IsNullOrWhiteSpace(nativeMessage) ? "(no native message)" : nativeMessage;
        return $"{operation} failed: {result} — {detail}";
    }
}

/// <summary>
/// Thrown when <c>andzip_core</c> cannot be located. The message enumerates every
/// path the resolver tried, in probe order, so a deployment misconfiguration is
/// obvious rather than an opaque <see cref="DllNotFoundException"/>.
/// </summary>
public sealed class AndzipLibraryNotFoundException : DllNotFoundException
{
    /// <summary>Filesystem paths probed, in order. Includes the env-var path and each candidate.</summary>
    public IReadOnlyList<string> TriedPaths { get; }

    public AndzipLibraryNotFoundException(IReadOnlyList<string> triedPaths)
        : base(BuildMessage(triedPaths))
    {
        TriedPaths = triedPaths;
    }

    private static string BuildMessage(IReadOnlyList<string> triedPaths)
    {
        var sb = new System.Text.StringBuilder();
        sb.Append("Could not load the native andzip_core engine. Paths tried, in order:");
        foreach (var p in triedPaths)
            sb.Append("\n  - ").Append(p);
        sb.Append("\nSet the ANDZIP_CORE_PATH environment variable to the full path of the "
                + "andzip_core native library, or stage it under runtimes/<rid>/native/.");
        return sb.ToString();
    }
}
