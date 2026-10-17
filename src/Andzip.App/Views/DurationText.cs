namespace Andzip.App.Views;

/// <summary>
/// 时长文案：<c>03:12</c>、<c>1:02:03</c> 这种钟点写法。
/// <para>
/// 刻意不用单位（秒 / 分 / 小时）：那要为一套时长文案做本地化，而这种写法不需要翻译，
/// 也和参考软件一致。进度窗口的「剩余时间」与新建压缩对话框的「预计」共用这一份 ——
/// 同一个窗口里两种时长说法是自己打自己的脸。
/// </para>
/// </summary>
internal static class DurationText
{
    public static string Format(TimeSpan span)
        => span.TotalHours >= 1
            ? $"{(int)span.TotalHours}:{span.Minutes:D2}:{span.Seconds:D2}"
            : $"{span.Minutes}:{span.Seconds:D2}";
}
