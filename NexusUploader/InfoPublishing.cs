namespace NexusUploader;

internal static class InfoPlanner
{
    public const string Version = "1.0";

    public static string Fingerprint(UpdateInfoRequest request) =>
        Json.Hash(new { toolVersion = Version, request });

    public static async Task<UpdateInfoPlan> PrepareAsync(UpdateInfoRequest request, NexusApi api,
        CancellationToken ct)
    {
        // The official edit endpoint does not expose the current description. A read-only file query
        // still verifies that the global mod ID is readable before a plan can be approved.
        await api.FilesAsync(request.ModId, ct);
        var now = DateTimeOffset.UtcNow;
        return new(Version, now, now.AddMinutes(15), request, Fingerprint(request));
    }

    public static void CheckPlan(UpdateInfoPlan plan, UpdateInfoRequest request, string confirmation)
    {
        Guard.Require(plan.ToolVersion == Version && Json.Hash(request) == Json.Hash(plan.Request),
            "PLAN_MISMATCH", "工具版本或请求已变化，必须重新 dry-run。");
        Guard.Require(plan.CreatedAt <= DateTimeOffset.UtcNow && plan.ExpiresAt > DateTimeOffset.UtcNow &&
            plan.ExpiresAt - plan.CreatedAt == TimeSpan.FromMinutes(15),
            "PLAN_EXPIRED", "计划已过期或时间无效，必须重新 dry-run。");
        Guard.Require(plan.Fingerprint == Fingerprint(request) && confirmation == Json.Hash(plan),
            "PLAN_CONFIRMATION", "计划被更改或 --confirm 不匹配；必须确认 dry-run 返回的完整 planSha256。");
    }
}

internal static class InfoPublisher
{
    public static async Task<Receipt> ExecuteAsync(UpdateInfoPlan plan, UpdateInfoRequest request,
        string confirmation, NexusApi api, string stateDirectory, CancellationToken ct)
    {
        InfoPlanner.CheckPlan(plan, request, confirmation);
        using var journal = new Journal(stateDirectory, request);
        await api.FilesAsync(request.ModId, ct);
        ct.ThrowIfCancellationRequested();
        journal.Set("uncertain", "update-description");
        try
        {
            await api.UpdateDescriptionAsync(request.ModId, request.Description, ct);
            journal.Set("success", "complete");
            return journal.Current;
        }
        catch (Exception exception)
        {
            try
            {
                journal.Set("uncertain", journal.Current.Stage,
                    errorCode: exception is SafeException safe ? safe.Code :
                        exception is OperationCanceledException ? "CANCELLED" : "TRANSPORT_ERROR");
            }
            catch { /* The durable pre-write journal remains authoritative. */ }
            throw new SafeException("PARTIAL_OR_UNCERTAIN",
                "已发送描述修改请求，结果不确定；查看 receipt 回执并核对 Nexus，禁止盲目重试。", 4);
        }
    }
}
