namespace Andzip.Core.Settings;

/// <summary>
/// 接管某个扩展名前，它原本的默认程序 ProgID。
/// </summary>
/// <param name="Extension">小写、不含点的扩展名。</param>
/// <param name="ProgId">之前的默认 ProgID；空串表示当时根本没有设过默认值（撤销时要删掉而不是写回空）。</param>
public sealed record AssociationOriginal(string Extension, string ProgId);

/// <summary>
/// 「接管前的默认程序」映射的编解码，存进 <see cref="AppSettings"/> 的一个键里。
/// <para>
/// 存在的理由：设默认程序是可逆操作。没有这份记录，取消勾选时就不知道该把 .zip 还给谁 ——
/// 要么留着 Andzip 当默认（用户明确说了不要），要么瞎猜一个（更糟）。
/// </para>
/// <para>
/// 格式是 <c>扩展名=ProgID</c>，条目之间用 <c>;</c> 分隔，只按第一个 <c>=</c> 切分，
/// 所以 ProgID 里带 <c>=</c> 也能原样回来。扩展名本身不可能含 <c>=</c> 或 <c>;</c>。
/// </para>
/// </summary>
public static class AssociationOriginals
{
    public const string SettingKey = "file_association_defaults";

    public static string Encode(IEnumerable<AssociationOriginal> originals)
        => string.Join(';', originals
            .Where(item => !string.IsNullOrWhiteSpace(item.Extension))
            // 按扩展名去重：同一个扩展名被两个格式条目共用时，只保留第一次接管前的值。
            .GroupBy(item => item.Extension, StringComparer.OrdinalIgnoreCase)
            .Select(group => group.Key + "=" + group.First().ProgId));

    public static IReadOnlyList<AssociationOriginal> Decode(string? stored)
    {
        if (string.IsNullOrWhiteSpace(stored))
        {
            return [];
        }

        var items = new List<AssociationOriginal>();
        foreach (var record in stored.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var separator = record.IndexOf('=');
            if (separator <= 0)
            {
                // 缺分隔符的脏数据直接跳过：宁可少恢复一项，也不要把空 ProgID 写到别的扩展名上。
                continue;
            }

            items.Add(new AssociationOriginal(
                record[..separator].Trim().TrimStart('.').ToLowerInvariant(),
                record[(separator + 1)..].Trim()));
        }

        return items;
    }
}
