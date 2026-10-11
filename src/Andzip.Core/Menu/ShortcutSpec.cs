namespace Andzip.Core.Menu;

/// <summary>
/// 快捷键描述（参考软件文案风格：Ctrl+O / F4 / Ins / Shift+Ctrl+Del）。
/// 解析结果供 UI 层转换为平台手势（Windows/macOS/Linux），解析规则本身与平台无关、可单元测试。
/// </summary>
public sealed record ShortcutSpec(string Text, IReadOnlySet<string> Modifiers, string Key)
{
    /// <summary>无法解析时返回 null（调用方保持菜单项可用但不绑定快捷键）。</summary>
    public static ShortcutSpec? Parse(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return null;
        }

        var parts = text.Split('+', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (parts.Length == 0)
        {
            return null;
        }

        var modifiers = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        for (var i = 0; i < parts.Length - 1; i++)
        {
            var part = parts[i];
            if (part.Equals("Ctrl", StringComparison.OrdinalIgnoreCase) || part.Equals("Control", StringComparison.OrdinalIgnoreCase))
            {
                modifiers.Add("Ctrl");
            }
            else if (part.Equals("Shift", StringComparison.OrdinalIgnoreCase))
            {
                modifiers.Add("Shift");
            }
            else if (part.Equals("Alt", StringComparison.OrdinalIgnoreCase))
            {
                modifiers.Add("Alt");
            }
            else
            {
                return null;
            }
        }

        var key = parts[^1];
        return key.Length == 0 ? null : new ShortcutSpec(text, modifiers, key);
    }

    public bool HasModifier(string modifier) => Modifiers.Contains(modifier);

    /// <summary>最后一段是否为功能键（F1–F24）。</summary>
    public bool IsFunctionKey =>
        Key.Length >= 2
        && (Key[0] == 'F' || Key[0] == 'f')
        && int.TryParse(Key[1..], out var n)
        && n is >= 1 and <= 24;

    public override string ToString() => Text;
}