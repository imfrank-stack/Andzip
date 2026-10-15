namespace Andzip.App;

/// <summary>
/// "这是哪一个 Andzip" 的那一个来源。
/// <para>版本号以前在三个地方各拼一遍（主窗口标题、关于窗口、界面试验里的对照值）。
/// 拼法一旦分叉，屏幕上就会出现两个版本号 —— 与 <c>AndzipLinks</c> 那条"地址只有一个来源"同一条理由。</para>
/// <para><strong>读的是 <c>AssemblyName.Version</c> 而不是 <c>InformationalVersion</c></strong>：
/// 后者会被 SourceLink 追加提交号（实测标题一度长成 "v1.9.1+44.3e846cf039…"），
/// 那是流水线要看的东西，印在窗口上只是噪音。反过来绕过 <c>build.ps1</c> 直接
/// <c>dotnet build</c> 会看到 csproj 里那个兜底版本 —— 那正说明手上这份不是发布产物。</para>
/// </summary>
internal static class AppIdentity
{
    /// <summary>写在窗口右下角与关于框里的那一截，例如 <c>v3.1.0</c>。</summary>
    public static string Version { get; } =
        typeof(AppIdentity).Assembly.GetName().Version is { } v
            ? $"v{v.Major}.{v.Minor}.{v.Build}"
            : "v0.0.0";
}
