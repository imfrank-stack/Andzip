using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text;
using Andzip.Core.Archive;
using Andzip.Core.Settings;

namespace Andzip.App.Services;

/// <summary>
/// 跨重启那一层口令存储的唯一入口。
/// <para>非 Windows 上 <see cref="Open"/> 直接回 null —— 界面对它只字不提，
/// 而不是摆一个按下去什么也不发生的开关（这条规矩在设置页上已经写过一次）。</para>
/// </summary>
internal static class PasswordVault
{
    /// <summary>打开这台机器上的凭据存储；不支持则为 null。<param name="settingsPath">索引写在哪个设置文件里。</param></summary>
    public static IPersistentPasswordStore? Open(string settingsPath)
        => OperatingSystem.IsWindows() ? new CredentialVault(settingsPath) : null;
}

/// <summary>
/// Windows 凭据管理器（<c>advapi32</c> 的 <c>Cred*</c> 那一组）。
/// <para><strong>为什么是它而不是 DPAPI 自己加密写文件</strong>：DPAPI（<c>CryptProtectData</c>）
/// 只解决"这段字节谁能解得开"，加密完还是要自己挑个地方放 —— 放 <c>settings.ini</c> 旁边
/// 就等于把"删掉设置文件里的口令"这件事变成"删掉设置文件里的密文"，而用户看得见的是前者。
/// 凭据管理器把存储、访问控制（默认 ACL 就是当前用户）、以及在系统面板里可见可删这三件事一起给了，
/// 用户想手工清的时候知道去哪儿清。</para>
/// <para><strong>目标名是路径的哈希，路径在索引里</strong>（<see cref="PasswordVaultIndex"/>）：
/// 口令进凭据，"哪个包存过口令"这条元数据不进凭据 —— 凭据列表是能被任何以本用户身份跑的东西枚举的，
/// 而"你的税单是加密的"本身就是一条信息。索引那份里本来就有 <c>recent_files</c>，不是新增暴露。</para>
/// <para><strong>能防什么、不能防什么要如实说</strong>：这条线挡住的是"明文躺在磁盘上"和"别的用户读走"，
/// 挡不住"以你的身份运行的程序"——那个层次上没有任何用户态 API 能保护你。界面上不夸口。</para>
/// </summary>
[SupportedOSPlatform("windows")]
internal sealed class CredentialVault(string settingsPath) : IPersistentPasswordStore
{
    private const int CredTypeGeneric = 1;
    private const int CredPersistLocalMachine = 2;

    /// <summary>
    /// 一条凭据的 blob 上限（<c>CRED_MAX_CREDENTIAL_BLOB_SIZE</c>，512 <strong>字节</strong>）。
    /// <para>是字节不是字符，所以中文口令的实际上限比 512 低得多 —— 按 UTF-8 算，
    /// 三个字节一个汉字。超了必须说，不能静默截断：截断之后"保存的口令"是个打不开包的东西。</para>
    /// </summary>
    private const int MaxBlobBytes = 512;

    public bool IsSupported => true;

    public string? Rejects(string password)
    {
        var bytes = Encoding.UTF8.GetByteCount(password);
        return bytes > MaxBlobBytes
            ? $"这条口令有 {bytes} 字节，超过系统凭据单条 {MaxBlobBytes} 字节的上限，没有保存（这次运行里仍然记得）。"
            : null;
    }

    public string? Save(string archivePath, string password)
    {
        if (Rejects(password) is { } refused)
        {
            return refused;
        }

        var full = PasswordVaultKey.Normalize(archivePath);
        if (!Write(PasswordVaultKey.For(full), archivePath, password))
        {
            return $"Windows 拒绝了这次写入（错误 {Marshal.GetLastWin32Error()}），口令没有保存。";
        }

        // 索引在凭据之后写：反过来会留下"界面上有、实际读不到"的假条目。
        // 索引里存<strong>原样路径</strong>（界面上要给人看），只有凭据的键才用归一化之后的大写形式。
        var settings = AppSettings.Load(settingsPath);
        PasswordVaultIndex.Add(settings, archivePath);
        settings.Save(settingsPath);
        return null;
    }

    public bool TryGet(string archivePath, out string password)
    {
        password = string.Empty;
        var full = PasswordVaultKey.Normalize(archivePath);
        var handle = IntPtr.Zero;
        try
        {
            if (!CredRead(PasswordVaultKey.For(full), CredTypeGeneric, 0, out handle))
            {
                return false;
            }

            var record = Marshal.PtrToStructure<Credential>(handle);
            if (record.CredentialBlobSize == 0 || record.CredentialBlob == IntPtr.Zero)
            {
                return false;
            }

            var bytes = new byte[record.CredentialBlobSize];
            Marshal.Copy(record.CredentialBlob, bytes, 0, bytes.Length);
            password = Encoding.UTF8.GetString(bytes);
            return password.Length > 0;
        }
        finally
        {
            if (handle != IntPtr.Zero)
            {
                CredFree(handle);
            }
        }
    }

    public void Remove(string archivePath)
    {
        var full = PasswordVaultKey.Normalize(archivePath);
        CredDelete(PasswordVaultKey.For(full), CredTypeGeneric, 0);
        // 删不掉也照样从索引里去掉：界面上留一条"删了还在"的记录，比留一条口令更让人不信任这个功能。

        var settings = AppSettings.Load(settingsPath);
        PasswordVaultIndex.Remove(settings, full);
        settings.Save(settingsPath);
    }

    public IReadOnlyList<PasswordVaultEntry> List()
    {
        var settings = AppSettings.Load(settingsPath);
        var entries = new List<PasswordVaultEntry>();
        var dropped = 0;

        foreach (var path in PasswordVaultIndex.Load(settings))
        {
            // 索引里有一条不等于凭据里有一条：用户可能直接在系统面板里删了那条凭据。
            // 对不上的就当没有 —— 报一条打不开的记录比不报更糟。
            if (ReadSavedAt(PasswordVaultKey.For(PasswordVaultKey.Normalize(path)), out var savedAt))
            {
                entries.Add(new PasswordVaultEntry(path, savedAt));
            }
            else
            {
                dropped++;
            }
        }

        if (dropped > 0)
        {
            // 索引里剩下的就是"确实还读得到"的那几条。
            PasswordVaultIndex.Clear(settings);
            foreach (var entry in entries)
            {
                PasswordVaultIndex.Add(settings, entry.ArchivePath);
            }

            settings.Save(settingsPath);
        }

        return [.. entries.OrderByDescending(e => e.SavedAt)];
    }

    /// <summary>
    /// 全部删掉。<strong>按前缀枚举凭据来删，不依赖索引</strong> ——
    /// 索引可能因为崩溃、手工改文件而少几条，而"关掉这个开关"的承诺是一个不剩。
    /// </summary>
    public void Clear()
    {
        foreach (var target in EnumerateTargets())
        {
            CredDelete(target, CredTypeGeneric, 0);
        }

        var settings = AppSettings.Load(settingsPath);
        PasswordVaultIndex.Clear(settings);
        settings.Save(settingsPath);
    }

    private static string? ReadTarget(IntPtr pointer)
    {
        var record = Marshal.PtrToStructure<Credential>(pointer);
        return record.TargetName;
    }

    internal static IEnumerable<string> EnumerateTargets()
    {
        // filter 只能传 null：CredEnumerate 的 filter 是**模式**匹配（唯一的通配符是 *），
        // 不是前缀匹配。传 "Andzip/archive/" 进去不会报错，只会一条都不回 —— 于是 Clear()
        // 变成一颗什么都没删的「全部删除」。宁可多枚举几条再按前缀筛，
        // 也不要一个静默返回空的过滤条件：这一层失败的样子和"本来就没有"一模一样。
        if (!CredEnumerate(null, 0, out var count, out var arrayPtr))
        {
            return [];
        }

        try
        {
            var targets = new List<string>();
            for (var i = 0; i < count; i++)
            {
                var entry = Marshal.ReadIntPtr(arrayPtr, i * IntPtr.Size);
                if (ReadTarget(entry) is { } target && PasswordVaultKey.BelongsToUs(target))
                {
                    targets.Add(target);
                }
            }

            return targets;
        }
        finally
        {
            CredFree(arrayPtr);
        }
    }

    private bool ReadSavedAt(string target, out DateTimeOffset savedAt)
    {
        savedAt = DateTimeOffset.MinValue;
        var handle = IntPtr.Zero;
        try
        {
            if (!CredRead(target, CredTypeGeneric, 0, out handle))
            {
                return false;
            }

            var record = Marshal.PtrToStructure<Credential>(handle);
            savedAt = DateTime.FromFileTimeUtc(record.LastWritten == 0 ? DateTime.UtcNow.ToFileTimeUtc() : record.LastWritten);
            return true;
        }
        finally
        {
            if (handle != IntPtr.Zero)
            {
                CredFree(handle);
            }
        }
    }

    private static bool Write(string target, string userName, string password)
    {
        var bytes = Encoding.UTF8.GetBytes(password);
        var blob = Marshal.AllocHGlobal(bytes.Length);
        try
        {
            Marshal.Copy(bytes, 0, blob, bytes.Length);
            var record = new Credential
            {
                Type = CredTypeGeneric,
                TargetName = target,
                UserName = userName,
                CredentialBlob = blob,
                CredentialBlobSize = bytes.Length,
                Persist = CredPersistLocalMachine,
                Comment = "Andzip 密码管理器",
                AttributeCount = 0,
                Attributes = IntPtr.Zero,
                TargetAlias = null,
                Flags = 0,
            };
            return CredWrite(ref record, 0);
        }
        finally
        {
            Marshal.FreeHGlobal(blob);
        }
    }

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool CredWrite([In] ref Credential userCredential, [In] int flags);

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool CredRead(
        string target,
        [In] int type,
        [In] int flags,
        [MarshalAs(UnmanagedType.SysInt)] out IntPtr credential);

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool CredDelete(string target, [In] int type, [In] int flags);

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool CredEnumerate(
        string? filter,
        [In] int flags,
        out int count,
        out IntPtr credentialArray);

    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern void CredFree([In] IntPtr credential);

    /// <summary>
    /// <c>CREDENTIALW</c>。<see cref="LayoutKind.Sequential"/> + <c>Pack = 8</c> 是这份头文件在 x64 上的实际布局；
    /// 字段顺序一个都不能动 —— 动一个，<c>CredWrite</c> 读到的就是错位的数据，而且不一定报错。
    /// </summary>
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode, Pack = 8)]
    private struct Credential
    {
        public int Flags;
        public int Type;

        [MarshalAs(UnmanagedType.LPWStr)]
        public string? TargetName;

        [MarshalAs(UnmanagedType.LPWStr)]
        public string? Comment;

        public long LastWritten;
        public int CredentialBlobSize;
        public IntPtr CredentialBlob;
        public int Persist;
        public int AttributeCount;
        public IntPtr Attributes;

        [MarshalAs(UnmanagedType.LPWStr)]
        public string? TargetAlias;

        [MarshalAs(UnmanagedType.LPWStr)]
        public string? UserName;
    }
}
