using System.Text;
using System.Text.RegularExpressions;

namespace NexusUploader;

internal sealed record ModConfig
{
    public ModTarget[] Targets { get; init; } = [];
}

internal sealed record ModTarget(string Name, string GameDomain, string GameScopedId,
    string ModId, string GameId, DateTimeOffset VerifiedAt);

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
            Guard.Require(allowMissing, "MODCONFIG_MISSING", "modconfig.json 不存在；请先用 target add 登记发布目标。");
            return new();
        }
        var config = Json.Read<ModConfig>(path);
        Guard.Require(config.Targets is not null && config.Targets.Length <= 1000,
            "MODCONFIG_INVALID", "modconfig.json 的 targets 无效或超过 1000 个目标。");
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var modIds = new HashSet<string>(StringComparer.Ordinal);
        foreach (var target in config.Targets)
        {
            Guard.TargetName(target.Name);
            Guard.Require(target.Name == target.Name.ToLowerInvariant() && names.Add(target.Name),
                "MODCONFIG_DUPLICATE", "modconfig.json 含重复 target 名称。");
            Guard.Require(Regex.IsMatch(target.GameDomain, @"\A[a-z0-9_-]{1,100}\z", RegexOptions.CultureInvariant),
                "MODCONFIG_INVALID", "modconfig.json 含无效游戏域名。");
            Guard.Id(target.GameScopedId);
            Guard.Id(target.ModId);
            Guard.Id(target.GameId);
            Guard.Require(modIds.Add(target.ModId), "MODCONFIG_DUPLICATE", "同一个全局 modId 不能登记到多个 target。");
            Guard.Require(target.VerifiedAt > DateTimeOffset.UnixEpoch && target.VerifiedAt <= DateTimeOffset.UtcNow.AddMinutes(5),
                "MODCONFIG_INVALID", "modconfig.json 含无效验证时间。");
        }
        return config with { Targets = config.Targets.OrderBy(t => t.Name, StringComparer.Ordinal).ToArray() };
    }

    public static ModTarget Find(string path, string name)
    {
        Guard.TargetName(name);
        var matches = Load(path).Targets.Where(t => t.Name.Equals(name, StringComparison.OrdinalIgnoreCase)).ToArray();
        Guard.Require(matches.Length == 1, "TARGET_NOT_FOUND", "modconfig.json 中没有指定 target。");
        return matches[0];
    }

    public static Request Bind(Request request, string path)
    {
        var hasId = !string.IsNullOrWhiteSpace(request.ModId);
        var hasTarget = !string.IsNullOrWhiteSpace(request.Target);
        Guard.Require(hasId ^ hasTarget, "REQUEST_TARGET", "update-file 请求必须且只能提供 modId 或 target。");
        if (hasId) return request;
        var target = Find(path, request.Target!);
        return request with { Target = target.Name, ModId = target.ModId };
    }

    public static ModTarget Add(string path, string name, ModResolution resolution)
    {
        Guard.TargetName(name);
        var directory = Path.GetDirectoryName(Path.GetFullPath(path))!;
        Guard.Require(Directory.Exists(directory), "MODCONFIG_PATH", "modconfig.json 所在目录不存在。");
        var lockPath = path + ".lock";
        using var gate = new FileStream(lockPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        var config = Load(path, allowMissing: true);
        Guard.Require(!config.Targets.Any(t => t.Name.Equals(name, StringComparison.OrdinalIgnoreCase)),
            "TARGET_EXISTS", "target 名称已存在；禁止自动覆盖。");
        Guard.Require(!config.Targets.Any(t => t.ModId == resolution.ModId),
            "TARGET_EXISTS", "该全局 modId 已登记；禁止建立重复别名。");
        var target = new ModTarget(name, resolution.GameDomain, resolution.GameScopedId,
            resolution.ModId, resolution.GameId, DateTimeOffset.UtcNow);
        var updated = config with { Targets = config.Targets.Append(target).OrderBy(t => t.Name, StringComparer.Ordinal).ToArray() };
        var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                var bytes = Encoding.UTF8.GetBytes(Json.Serialize(updated));
                stream.Write(bytes);
                stream.Flush(true);
            }
            File.Move(temporary, path, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporary)) File.Delete(temporary);
        }
        return target;
    }
}
