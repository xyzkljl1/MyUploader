using System.Text.Json;
using System.Text.RegularExpressions;

namespace NexusUploader;

internal static class PlanCleanup
{
    private sealed record StoredPlan(string RequestId, string ModId, DateTimeOffset CreatedAt, DateTimeOffset ExpiresAt);

    // Best effort only: cleanup must never turn an already successful publication into a failure.
    public static CleanupReport Run(string stateDirectory, DateTimeOffset? now = null)
    {
        var deleted = 0;
        var directories = 0;
        var retained = 0;
        var failures = 0;
        try
        {
            var state = Path.GetFullPath(stateDirectory);
            var plansRoot = Path.Combine(state, "plans");
            PlanStore.CheckLinks(plansRoot);
            if (!Directory.Exists(plansRoot)) return new();
            var gatePath = Path.Combine(state, "plans.cleanup.lock");
            PlanStore.CheckLinks(gatePath);
            using var cleanupGate = new FileStream(gatePath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
            var cutoff = (now ?? DateTimeOffset.UtcNow).ToUniversalTime().AddMonths(-1);
            foreach (var directory in Directory.EnumerateDirectories(plansRoot))
            {
                var requestId = Path.GetFileName(directory);
                if (!Guid.TryParseExact(requestId, "D", out var id) || id == Guid.Empty || requestId != id.ToString("D")) continue;
                var before = deleted;
                try
                {
                    PlanStore.CheckLinks(directory);
                    foreach (var path in Directory.EnumerateFiles(directory, "*.plan.json"))
                    {
                        var filename = Path.GetFileName(path);
                        if (!Regex.IsMatch(filename, @"\A[0-9a-f]{64}\.plan\.json\z", RegexOptions.CultureInvariant)) continue;
                        try
                        {
                            var hash = filename[..64];
                            Guard.Require(Path.GetFullPath(path).Equals(PlanStore.PathFor(state, requestId, hash),
                                StringComparison.OrdinalIgnoreCase), "CLEANUP_PATH", "清理路径不属于工具生成的计划位置。");
                            var plan = ReadPlan(state, requestId, hash);
                            Guard.Require(plan.CreatedAt > DateTimeOffset.UnixEpoch &&
                                plan.ExpiresAt - plan.CreatedAt == TimeSpan.FromMinutes(15),
                                "CLEANUP_PLAN", "计划的保留时间信息无效；保留该文件。");
                            if (plan.ExpiresAt >= cutoff) { retained++; continue; }

                            // Use the same mod lock as publishing so a missing receipt cannot race a new write.
                            var modLock = Journal.LockPath(state, plan.ModId);
                            PlanStore.CheckLinks(modLock);
                            using var targetGate = new FileStream(modLock, FileMode.OpenOrCreate,
                                FileAccess.ReadWrite, FileShare.None);
                            if (!CanRemove(state, plan)) { retained++; continue; }
                            // Revalidate both containment and content immediately before a non-recursive deletion.
                            var current = ReadPlan(state, requestId, hash);
                            Guard.Require(current == plan, "CLEANUP_PLAN", "计划在清理检查期间变化；保留该文件。");
                            File.Delete(path);
                            deleted++;
                        }
                        catch (Exception)
                        {
                            retained++;
                            failures++; // Never emit paths, content, or raw exceptions from cleanup.
                        }
                    }
                    if (deleted > before && !Directory.EnumerateFileSystemEntries(directory).Any())
                    {
                        PlanStore.CheckLinks(directory);
                        Directory.Delete(directory, recursive: false);
                        directories++;
                    }
                }
                catch (Exception) { failures++; }
            }
        }
        catch (Exception) { failures++; }
        return new(deleted, directories, retained, failures, failures > 0 ? "CLEANUP_INCOMPLETE" : null);
    }

    private static StoredPlan ReadPlan(string state, string requestId, string hash)
    {
        var path = PlanStore.PathFor(state, requestId, hash);
        PlanStore.CheckLinks(path);
        using var doc = JsonDocument.Parse(Json.ReadText(path));
        Json.NoDuplicates(doc.RootElement);
        var root = doc.RootElement;
        var request = root.GetProperty("request");
        var operation = request.GetProperty("operation").GetString();
        StoredPlan result;
        string actualHash;
        if (operation == "update-file")
        {
            var plan = root.Deserialize<Plan>(Json.Options) ?? throw new SafeException("CLEANUP_PLAN", "计划为空。");
            actualHash = Json.Hash(plan);
            result = new(plan.Request.RequestId, plan.Request.ModId, plan.CreatedAt, plan.ExpiresAt);
        }
        else if (operation == "update-info")
        {
            var plan = root.Deserialize<UpdateInfoPlan>(Json.Options) ?? throw new SafeException("CLEANUP_PLAN", "计划为空。");
            actualHash = Json.Hash(plan);
            result = new(plan.Request.RequestId, plan.Request.ModId, plan.CreatedAt, plan.ExpiresAt);
        }
        else throw new SafeException("CLEANUP_PLAN", "计划类型未知；保留该文件。");
        Guard.Require(actualHash == hash && result.RequestId == requestId,
            "CLEANUP_PLAN", "计划内容与文件标识不一致；保留该文件。");
        Guard.Id(result.ModId);
        return result;
    }

    private static bool CanRemove(string state, StoredPlan plan)
    {
        var receiptPath = Path.Combine(state, plan.RequestId + ".receipt.json");
        PlanStore.CheckLinks(receiptPath);
        // A journal interrupted during atomic replacement is also kept for reconciliation.
        var pendingReceipt = receiptPath + ".tmp";
        PlanStore.CheckLinks(pendingReceipt);
        if (File.Exists(pendingReceipt) || Directory.Exists(pendingReceipt)) return false;
        if (Directory.Exists(receiptPath)) return false;
        if (!File.Exists(receiptPath)) return true;
        var receipt = Json.Read<Receipt>(receiptPath);
        return receipt.RequestId == plan.RequestId && receipt.ModId == plan.ModId &&
            receipt.Status == "success" && receipt.Stage == "complete";
    }
}
