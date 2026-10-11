namespace Andzip.Core.Archive;

/// <summary>
/// 把一个“可能是分卷包”的归档路径变成引擎能直接打开的单个文件。
/// <para>
/// 7-Zip 风格的分卷（<c>archive.7z.001</c>）由引擎自己按 <c>IArchiveOpenVolumeCallback</c>
/// 取回兄弟卷，不需要这里插手。WinZip 风格（<c>name.z01 … name.zip</c>）是 ZIP 的私有做法，
/// 引擎不认，所以仍然拼接成临时文件。
/// </para>
/// <para>用完后必须 Dispose，临时文件才会被清掉。</para>
/// </summary>
internal sealed class SingleFileArchive : IDisposable
{
    private readonly string? _temporaryPath;

    private SingleFileArchive(string path, string? temporaryPath)
    {
        FilePath = path;
        _temporaryPath = temporaryPath;
    }

    /// <summary>引擎应当打开的路径。叫 FilePath 而不是 Path，避免遮蔽 System.IO.Path。</summary>
    public string FilePath { get; }

    public static SingleFileArchive Open(string archivePath, CancellationToken cancellationToken = default)
    {
        var parts = SplitVolumeSupport.EnumerateParts(archivePath);
        if (parts.Count <= 1)
        {
            return new SingleFileArchive(archivePath, null);
        }

        var temporary = Path.Combine(
            Path.GetTempPath(),
            $"andzip-{Guid.NewGuid():N}{Path.GetExtension(archivePath)}");
        try
        {
            SplitVolumeSupport.Concatenate(parts, temporary, cancellationToken);
        }
        catch
        {
            ArchiveOutputGuard.TryDelete(temporary);
            throw;
        }

        return new SingleFileArchive(temporary, temporary);
    }

    public void Dispose()
    {
        if (_temporaryPath is not null)
        {
            ArchiveOutputGuard.TryDelete(_temporaryPath);
        }
    }
}
