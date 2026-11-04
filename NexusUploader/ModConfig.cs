using System.Text;
using System.Text.RegularExpressions;

namespace NexusUploader;

internal sealed record ModConfig
{
    public ModTarget[] Targets { get; init; } = [];
}

internal sealed record ModTarget(string DirectoryKey, string LocalModName, string? PageName,
    string GameDomain, string GameScopedId, string ModId, string GameId, DateTimeOffset VerifiedAt);

internal static class ModDirectoryIdentity
{
    public static (string FullPath, string DirectoryKey, string LocalModName) Read(string path)
    {
        Guard.Text(path, 4096, "MOD_DIRECTORY");
        Guard.Require(Path.IsPathFullyQualified(path), "MOD_DIRECTORY", "modDirectory 必须使用完整本地路径。");
        var full = Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
        Guard.Require(!full.StartsWith(@"\\", StringComparison.Ordinal) && Directory.Exists(full),
            "MOD_DIRECTORY", "modDirectory 必须是存在的本地文件夹。");
        var root = Path.GetPathRoot(full);
        Guard.Require(!string.IsNullOrEmpty(root), "MOD_DIRECTORY", "modDirectory 必须是带盘符的本地路径。");
        var relative = full[root.Length..].Split([Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar],
            StringSplitOptions.RemoveEmptyEntries);
        Guard.Require(relative.Length > 0, "MOD_DIRECTORY", "modDirectory 不能是磁盘根目录。");
        var key = string.Join('/', relative.TakeLast(3).Select(NormalizeSegment));
        Guard.Require(key.Length is > 0 and <= 1000, "DIRECTORY_KEY", "无法生成安全的 mod 目录标识。");
        var metadata = ReadMetadata(Path.Combine(full, "modinfo.ini"));
        return (full, key, metadata.Name);
    }

    internal static (string Name, string Version) ReadMetadata(string path)
    {
        Guard.Require(File.Exists(path), "MODINFO_MISSING", "mod 文件夹根目录缺少 modinfo.ini。");
        var info = new FileInfo(path);
        Guard.Require(info.Length <= 1024 * 1024, "MODINFO_INVALID", "modinfo.ini 超过 1 MiB 上限。");
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        using var reader = new StreamReader(stream, new UTF8Encoding(false, true), detectEncodingFromByteOrderMarks: false);
        var raw = reader.ReadToEnd().TrimStart('\uFEFF');
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
        Guard.Require(values.TryGetValue("name", out var modName),
            "MODINFO_INVALID", "modinfo.ini 必须提供 name 和 version（无节或 [ModInfo] 节）。");
        Guard.Require(values.TryGetValue("version", out var version),
            "MODINFO_INVALID", "modinfo.ini 必须提供 name 和 version（无节或 [ModInfo] 节）。");
        Guard.FileName(modName!);
        Guard.Version(version!);
        return (modName!, version!);
    }

    private static string NormalizeSegment(string value)
    {
        var normalized = value.Normalize(NormalizationForm.FormC).Trim().ToLowerInvariant();
        Guard.Require(normalized.Length > 0 && normalized.Length <= 255 && normalized is not "." and not ".." &&
            !normalized.Any(char.IsControl) && !normalized.Contains('/'), "DIRECTORY_KEY", "modDirectory 含无效目录名。");
        return normalized;
    }
}

internal static class ModRegistry
{
    public static string PathForConfig(string configPath)
    {
        var fullConfig = Path.GetFullPath(configPath);
        return Path.Combine(Path.GetDirectoryName(fullConfig)!, "modconfig.json");
    }

    public static ModConfig Load(string path, bool allowMissing = false)
    {
        if (!File.Exists(path))
        {
            Guard.Require(allowMissing, "MODCONFIG_MISSING", "modconfig.json 不存在；请运行 target candidates，并在首次 update-file --dry-run 时传入 --mod。");
            return new();
        }
        var config = Json.Read<ModConfig>(path);
        Guard.Require(config.Targets is not null && config.Targets.Length <= 1000,
            "MODCONFIG_INVALID", "modconfig.json 的 targets 无效或超过 1000 个目标。");
        var keys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var modIds = new HashSet<string>(StringComparer.Ordinal);
        foreach (var target in config.Targets)
        {
            Validate(target);
            Guard.Require(keys.Add(target.DirectoryKey), "MODCONFIG_DUPLICATE", "modconfig.json 含重复目录标识。");
            Guard.Require(modIds.Add(target.ModId), "MODCONFIG_DUPLICATE", "同一个全局 modId 不能登记到多个目录。");
        }
        return config with { Targets = config.Targets.OrderBy(t => t.DirectoryKey, StringComparer.Ordinal).ToArray() };
    }

    public static ModTarget Find(string path, string modDirectory)
    {
        var identity = ModDirectoryIdentity.Read(modDirectory);
        var matches = Load(path).Targets.Where(t => t.DirectoryKey.Equals(identity.DirectoryKey, StringComparison.OrdinalIgnoreCase)).ToArray();
        Guard.Require(matches.Length == 1, "TARGET_NOT_FOUND", "modconfig.json 中没有该 modDirectory 的绑定；请运行 target candidates，并在首次 update-file --dry-run 时传入 --mod。");
        Guard.Require(matches[0].LocalModName.Equals(identity.LocalModName, StringComparison.OrdinalIgnoreCase),
            "TARGET_LOCAL_NAME_CHANGED", "modinfo.ini 名称与已登记绑定不一致；禁止猜测目标。");
        return matches[0];
    }

    public static Request Bind(UpdateFileInput request, string path)
    {
        var target = Find(path, request.ModDirectory);
        return Bind(request, target);
    }

    internal static Request Bind(UpdateFileInput request, ModTarget target)
    {
        return new() { RequestId = request.RequestId, Operation = "update-file", ModId = target.ModId,
            DirectoryKey = target.DirectoryKey, ModDirectory = request.ModDirectory,
            IncludeRootDirectory = request.IncludeRootDirectory, Description = request.Description, Changelog = request.Changelog };
    }

    public static UpdateInfoRequest Bind(UpdateInfoInput request, string path)
    {
        var target = Find(path, request.ModDirectory);
        return new() { RequestId = request.RequestId, Operation = "update-info", ModId = target.ModId,
            DirectoryKey = target.DirectoryKey, ModDirectory = request.ModDirectory, Description = request.Description };
    }

    public static ModTarget Add(string path, string modDirectory, ModResolution resolution)
    {
        var identity = ModDirectoryIdentity.Read(modDirectory);
        var target = new ModTarget(identity.DirectoryKey, identity.LocalModName, resolution.PageName,
            resolution.GameDomain, resolution.GameScopedId, resolution.ModId, resolution.GameId, DateTimeOffset.UtcNow);
        return WriteAdded(path, target);
    }

    internal static ModConfig RequireAvailable(string path, string directoryKey, string? modId = null)
    {
        var config = Load(path, allowMissing: true);
        Guard.Require(!config.Targets.Any(t => t.DirectoryKey.Equals(directoryKey, StringComparison.OrdinalIgnoreCase)),
            "TARGET_EXISTS", "该 modDirectory 已登记；禁止传入 --mod 或覆盖绑定，请重新 dry-run。");
        if (modId is not null)
            Guard.Require(!config.Targets.Any(t => t.ModId == modId), "TARGET_EXISTS", "该全局 modId 已登记；禁止重复绑定。");
        return config;
    }

    internal static void CheckLocalTarget(string modDirectory, ModTarget target)
    {
        Validate(target);
        var identity = ModDirectoryIdentity.Read(modDirectory);
        Guard.Require(identity.DirectoryKey == target.DirectoryKey && identity.LocalModName == target.LocalModName,
            "BINDING_CHANGED", "本地目录或 modinfo.ini 名称与首次绑定计划不一致；请重新 dry-run。");
    }

    internal static ModTarget ConfirmRegistration(string path, string modDirectory, ModTarget target)
    {
        CheckLocalTarget(modDirectory, target);
        return WriteAdded(path, target with { VerifiedAt = DateTimeOffset.UtcNow });
    }

    private static ModTarget WriteAdded(string path, ModTarget target)
    {
        var directory = Path.GetDirectoryName(Path.GetFullPath(path))!;
        Guard.Require(Directory.Exists(directory), "MODCONFIG_PATH", "modconfig.json 所在目录不存在。");
        using var gate = new FileStream(path + ".lock", FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        var config = RequireAvailable(path, target.DirectoryKey, target.ModId);
        Guard.Require(config.Targets.Length < 1000, "MODCONFIG_LIMIT", "modconfig.json 已达到 1000 个绑定的上限。");
        WriteReplacement(path, config with { Targets = config.Targets.Append(target).OrderBy(t => t.DirectoryKey, StringComparer.Ordinal).ToArray() });
        return target;
    }

    private static void WriteReplacement(string path, ModConfig config)
    {
        foreach (var target in config.Targets) Validate(target);
        var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                var bytes = Encoding.UTF8.GetBytes(Json.Serialize(config));
                stream.Write(bytes);
                stream.Flush(true);
            }
            File.Move(temporary, path, overwrite: true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }

    private static void Validate(ModTarget target)
    {
        Guard.Require(target.DirectoryKey.Length is > 0 and <= 1000 && target.DirectoryKey == target.DirectoryKey.ToLowerInvariant() &&
            !target.DirectoryKey.StartsWith('/') && !target.DirectoryKey.EndsWith('/') && !target.DirectoryKey.Any(char.IsControl),
            "MODCONFIG_INVALID", "modconfig.json 含无效目录标识。");
        Guard.FileName(target.LocalModName);
        if (target.PageName is not null)
            Guard.Require(target.PageName.Length is > 0 and <= 500 && !target.PageName.Any(char.IsControl),
                "MODCONFIG_INVALID", "modconfig.json 含无效页面名称。");
        Guard.Require(Regex.IsMatch(target.GameDomain, @"\A[a-z0-9_-]{1,100}\z", RegexOptions.CultureInvariant),
            "MODCONFIG_INVALID", "modconfig.json 含无效游戏域名。");
        Guard.Id(target.GameScopedId); Guard.Id(target.ModId); Guard.Id(target.GameId);
        Guard.Require(target.VerifiedAt > DateTimeOffset.UnixEpoch && target.VerifiedAt <= DateTimeOffset.UtcNow.AddMinutes(5),
            "MODCONFIG_INVALID", "modconfig.json 含无效验证时间。");
    }
}
