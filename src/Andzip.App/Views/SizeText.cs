namespace Andzip.App.Views;

/// <summary>体积文案格式化（列表与状态栏共用；B/KB/MB/GB）。</summary>
internal static class SizeText
{
    public static string Format(long bytes) => bytes switch
    {
        < 0 => string.Empty,
        < 1024 => bytes + " B",
        < 1024 * 1024 => (bytes / 1024.0).ToString("0.#") + " KB",
        < 1024L * 1024 * 1024 => (bytes / (1024.0 * 1024)).ToString("0.#") + " MB",
        _ => (bytes / (1024.0 * 1024 * 1024)).ToString("0.##") + " GB",
    };
}