using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;

namespace NexusUploader;

// A private, immutable snapshot. The temporary ZIP is deleted when the handle closes.
internal sealed class PreparedPackage(FileStream stream, PackageInfo info) : IDisposable
{
    public FileStream Stream { get; } = stream;
    public PackageInfo Info { get; } = info;
    public void Dispose() => Stream.Dispose();
}

internal static class Packaging
{
    private const long MaxBytes = 8L * 1024 * 1024 * 1024;
    private sealed record Entry(string Path, string Name, bool Directory);

    public static async Task<PreparedPackage> BuildAsync(string directory, string key, CancellationToken ct)
    {
        directory = Path.TrimEndingDirectorySeparator(Path.GetFullPath(directory));
        Guard.Require(Directory.Exists(directory), "MOD_DIRECTORY", "modDirectory 必须是存在的文件夹。");
        var entries = Inventory(directory, key, ct); // Reject sensitive names before opening any source file.
        var ini = entries.SingleOrDefault(e => e.Name.Equals("modinfo.ini", StringComparison.OrdinalIgnoreCase) && !e.Directory);
        Guard.Require(ini is not null, "MODINFO_MISSING", "mod 文件夹根目录缺少 modinfo.ini。");
        var sources = new Dictionary<string, FileStream>(StringComparer.Ordinal);
        FileStream? output = null;
        try
        {
            long total = 0;
            foreach (var entry in entries.Where(e => !e.Directory))
            {
                ct.ThrowIfCancellationRequested();
                CheckAncestors(entry.Path);
                var source = new FileStream(entry.Path, FileMode.Open, FileAccess.Read, FileShare.Read, bufferSize: 1, FileOptions.SequentialScan);
                sources.Add(entry.Name, source); // Hold all source handles through packaging to prevent Windows writes/deletes.
                total = checked(total + source.Length);
                Guard.Require(total <= MaxBytes, "PACKAGE_LIMIT", "文件夹内容超过 8 GiB 打包上限。");
                await ScanAsync(source, key, ct);
                source.Position = 0;
            }
            var metadata = ReadMetadata(sources[ini.Name], key);
            var stem = metadata.Name.Replace(' ', '_').TrimStart('.').TrimEnd('.');
            if (System.Text.RegularExpressions.Regex.IsMatch(stem, @"(?i)\A(CON|PRN|AUX|NUL|COM[1-9]|LPT[1-9])(?:\.|\z)")) stem = "mod-" + stem;
            var archiveName = stem + "-" + metadata.Version + ".zip";
            // A random local storage name avoids collisions; archiveName is the public download filename.
            var temporary = Path.Combine(Path.GetTempPath(), "NexusUploader-" + Guid.NewGuid().ToString("N") + ".zip");
            var sourcePrefix = Path.EndsInDirectorySeparator(directory) ? directory : directory + Path.DirectorySeparatorChar;
            Guard.Require(!temporary.StartsWith(sourcePrefix, StringComparison.OrdinalIgnoreCase),
                "MOD_DIRECTORY", "源文件夹不能包含工具的临时打包目录。");
            output = new FileStream(temporary, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None, 128 * 1024,
                FileOptions.DeleteOnClose | FileOptions.SequentialScan);
            using (var zip = new ZipArchive(output, ZipArchiveMode.Create, leaveOpen: true, entryNameEncoding: Encoding.UTF8))
            {
                foreach (var entry in entries)
                {
                    ct.ThrowIfCancellationRequested();
                    var item = zip.CreateEntry(entry.Name, CompressionLevel.Optimal);
                    // Fixed metadata + sorted paths make content-identical folders produce identical ZIP bytes.
                    item.LastWriteTime = new DateTimeOffset(1980, 1, 1, 0, 0, 0, TimeSpan.Zero);
                    item.ExternalAttributes = entry.Directory ? 0x10 : 0;
                    if (!entry.Directory)
                    {
                        using var destination = item.Open();
                        var source = sources[entry.Name];
                        source.Position = 0;
                        await source.CopyToAsync(destination, 128 * 1024, ct);
                    }
                }
            }
            Guard.Require(entries.SequenceEqual(Inventory(directory, key, ct)), "SOURCE_CHANGED", "打包期间文件夹结构变化；请停止源文件修改后重新 dry-run。");
            output.Position = 0;
            var hash = Convert.ToHexString(await SHA256.HashDataAsync(output, ct)).ToLowerInvariant();
            output.Position = 0;
            var package = new PreparedPackage(output, new(metadata.Name, metadata.Version, archiveName, output.Length, hash, sources.Count));
            output = null; // Ownership transfers only after all validation succeeds.
            return package;
        }
        finally
        {
            output?.Dispose();
            foreach (var source in sources.Values) source.Dispose();
        }
    }

    private static Entry[] Inventory(string directory, string key, CancellationToken ct)
    {
        CheckAncestors(directory);
        var entries = new List<Entry>();
        var pending = new Stack<string>();
        pending.Push(directory);
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        while (pending.TryPop(out var parent))
        {
            ct.ThrowIfCancellationRequested();
            foreach (var path in Directory.EnumerateFileSystemEntries(parent))
            {
                var relative = Path.GetRelativePath(directory, path).Replace('\\', '/');
                CheckName(relative);
                Guard.PublicText(relative, key);
                var attributes = File.GetAttributes(path);
                Guard.Require(!attributes.HasFlag(FileAttributes.ReparsePoint), "PACKAGE_LINK", "拒绝符号链接、目录联接及其他重解析点。");
                var isDirectory = attributes.HasFlag(FileAttributes.Directory);
                Guard.Require(names.Add(relative), "PACKAGE_PATH", "文件夹包含大小写冲突的路径。");
                entries.Add(new(path, relative + (isDirectory ? "/" : ""), isDirectory));
                Guard.Require(entries.Count <= 10000, "PACKAGE_LIMIT", "文件夹条目数超过 10,000。");
                if (isDirectory) pending.Push(path);
            }
        }
        return entries.OrderBy(e => e.Name, StringComparer.Ordinal).ToArray();
    }

    private static void CheckAncestors(string path)
    {
        for (string? current = path; current is not null; current = Path.GetDirectoryName(current))
            Guard.Require(!File.GetAttributes(current).HasFlag(FileAttributes.ReparsePoint), "PACKAGE_LINK", "源路径及其父目录不能包含符号链接或目录联接。");
    }

    internal static void CheckName(string name)
    {
        var pieces = name.Replace('\\', '/').Split('/');
        Guard.Require(!name.StartsWith('/') && !name.Contains(':') && pieces.All(p => p.Length > 0 && p is not "." and not ".." && !p.Any(char.IsControl)),
            "PACKAGE_PATH", "文件夹包含不安全的路径。");
        foreach (var piece in pieces)
        {
            var value = piece.ToLowerInvariant();
            Guard.Require(value is not ".git" and not ".nexus-state", "PACKAGE_PATH", "文件夹包含 Git 或发布状态元数据。");
            Guard.Require(!(value is "updater.json" or "credentials.json" or "cookies.json" ||
                value.StartsWith(".env") || value.Contains("storage-state") || value.EndsWith(".secret.json") ||
                value.EndsWith(".pem") || value.EndsWith(".key")), "PACKAGE_SECRET", "文件夹包含敏感配置文件名；未读取该文件，禁止发布。");
        }
    }

    private static (string Name, string Version) ReadMetadata(FileStream stream, string key)
    {
        Guard.Require(stream.Length <= 1024 * 1024, "MODINFO_INVALID", "modinfo.ini 超过 1 MiB 上限。");
        // UTF-8 only, optional BOM. Never silently replace invalid bytes or echo INI text.
        using var reader = new StreamReader(stream, new UTF8Encoding(false, true), detectEncodingFromByteOrderMarks: false, leaveOpen: true);
        var raw = reader.ReadToEnd().TrimStart('\uFEFF');
        Guard.PublicText(raw, key);
        var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var selectedSection = true;
        foreach (var rawLine in raw.Split('\n'))
        {
            var line = rawLine.Trim();
            if (line.Length == 0 || line.StartsWith(';') || line.StartsWith('#')) continue;
            if (line.StartsWith('['))
            {
                Guard.Require(line.EndsWith(']') && line.Length > 2, "MODINFO_INVALID", "modinfo.ini 的节标题无效。");
                selectedSection = line[1..^1].Trim().Equals("ModInfo", StringComparison.OrdinalIgnoreCase);
                continue;
            }
            var separator = line.IndexOf('=');
            Guard.Require(separator > 0, "MODINFO_INVALID", "modinfo.ini 必须使用 key=value 格式。");
            if (!selectedSection) continue;
            var name = line[..separator].Trim();
            if (!name.Equals("name", StringComparison.OrdinalIgnoreCase) && !name.Equals("version", StringComparison.OrdinalIgnoreCase)) continue;
            Guard.Require(values.TryAdd(name, line[(separator + 1)..].Trim()), "MODINFO_DUPLICATE", "modinfo.ini 包含重复的 name 或 version。");
        }
        Guard.Require(values.TryGetValue("name", out var modName) && values.TryGetValue("version", out _), "MODINFO_INVALID", "modinfo.ini 必须提供 name 和 version（无节或 [ModInfo] 节）。");
        Guard.FileName(modName!);
        Guard.Version(values["version"]);
        stream.Position = 0;
        return (modName!, values["version"]);
    }

    private static async Task ScanAsync(Stream stream, string key, CancellationToken ct)
    {
        var needles = new[] { Encoding.UTF8.GetBytes(key), Encoding.Unicode.GetBytes(key), Encoding.BigEndianUnicode.GetBytes(key),
            Encoding.UTF8.GetBytes("NEXUSMODS_API_KEY"), Encoding.Unicode.GetBytes("NEXUSMODS_API_KEY"), Encoding.BigEndianUnicode.GetBytes("NEXUSMODS_API_KEY") };
        var overlap = needles.Max(n => n.Length) - 1;
        var buffer = new byte[128 * 1024 + overlap];
        var carry = 0;
        long total = 0;
        while (true)
        {
            var read = await stream.ReadAsync(buffer.AsMemory(carry, 128 * 1024), ct);
            if (read == 0) break;
            total += read;
            Guard.Require(total <= MaxBytes, "PACKAGE_LIMIT", "文件内容超过 8 GiB 校验上限。");
            var count = carry + read;
            foreach (var needle in needles)
                Guard.Require(buffer.AsSpan(0, count).IndexOf(needle) < 0, "PACKAGE_SECRET", "文件内容包含 Nexus 凭据或配置特征；已禁止发布。");
            carry = Math.Min(overlap, count);
            Buffer.BlockCopy(buffer, count - carry, buffer, 0, carry);
        }
    }
}
