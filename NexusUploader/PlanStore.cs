using System.Text;
using System.Text.RegularExpressions;

namespace NexusUploader;

// Plans are immutable review records. Receipts and locks live outside this subtree.
internal static class PlanStore
{
    public static string Save(string stateDirectory, Plan plan) =>
        SavePlan(stateDirectory, plan.Request.RequestId, plan);

    public static string Save(string stateDirectory, UpdateInfoPlan plan) =>
        SavePlan(stateDirectory, plan.Request.RequestId, plan);

    public static T Read<T>(string stateDirectory, string requestId, string confirmation)
    {
        var path = PathFor(stateDirectory, requestId, confirmation);
        CheckLinks(path);
        Guard.Require(File.Exists(path), "PLAN_NOT_FOUND",
            "没有找到此 requestId 和确认摘要对应的计划；请核对 dry-run 输出，必要时重新 dry-run。");
        var plan = Json.Read<T>(path);
        Guard.Require(Json.Hash(plan) == confirmation, "PLAN_CONFIRMATION",
            "已保存计划被更改或确认摘要不匹配；必须重新 dry-run。");
        return plan;
    }

    internal static string PathFor(string stateDirectory, string requestId, string confirmation)
    {
        Guard.Require(Guid.TryParseExact(requestId, "D", out var id) && id != Guid.Empty &&
            requestId == id.ToString("D"), "REQUEST_ID", "计划索引的 requestId 必须是规范化的非空 UUID。");
        Guard.Require(Regex.IsMatch(confirmation, @"\A[0-9a-f]{64}\z", RegexOptions.CultureInvariant),
            "PLAN_CONFIRMATION", "--confirm 必须为 dry-run 返回的完整小写 SHA-256 摘要。");
        return Path.Combine(Path.GetFullPath(stateDirectory), "plans", requestId, confirmation + ".plan.json");
    }

    private static string SavePlan<T>(string stateDirectory, string requestId, T plan)
    {
        var bytes = Encoding.UTF8.GetBytes(Json.Serialize(plan));
        Guard.Require(bytes.Length <= 2 * 1024 * 1024, "PLAN_LIMIT", "计划超过 2 MiB 存储及读取上限；未保存计划。");
        var confirmation = Json.HashBytes(bytes);
        var path = PathFor(stateDirectory, requestId, confirmation);
        CheckLinks(path);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        CheckLinks(path);
        Guard.Require(!File.Exists(path), "PLAN_EXISTS", "同一计划已经保存；不会覆盖已有审查记录。");
        using var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None);
        stream.Write(bytes);
        stream.Flush(true);
        return confirmation;
    }

    internal static void CheckLinks(string path)
    {
        for (string? current = path; current is not null; current = Path.GetDirectoryName(current))
            if (File.Exists(current) || Directory.Exists(current))
                Guard.Require(!File.GetAttributes(current).HasFlag(FileAttributes.ReparsePoint), "PLAN_PATH_LINK",
                    "计划存储路径及其父目录不能包含符号链接或目录联接。");
    }
}
