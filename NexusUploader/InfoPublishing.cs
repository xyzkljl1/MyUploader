namespace NexusUploader;

internal static class InfoPlanner
{
    public const string Version = "1.2";

    public static string Fingerprint(UpdateInfoRequest request, DescriptionPlan? pageDescription = null) =>
        Json.Hash(new { toolVersion = Version, request, pageDescription });

    public static async Task<UpdateInfoPlan> PrepareAsync(UpdateInfoRequest request, NexusApi api,
        CancellationToken ct, string? templateRoot = null)
    {
        await api.FilesAsync(request.ModId, ct);
        var description = await DescriptionComposer.PrepareAsync(request.ModDirectory, request.ModId,
            request.PageDescription, api, templateRoot ?? DescriptionComposer.DefaultRoot, ct, request.Description);
        var now = DateTimeOffset.UtcNow;
        return new(Version, now, now.AddMinutes(15), request, Fingerprint(request, description), description);
    }

    public static void CheckPlan(UpdateInfoPlan plan, UpdateInfoRequest request, string confirmation)
    {
        Guard.Require(plan.ToolVersion == Version && Json.Hash(request) == Json.Hash(plan.Request),
            "PLAN_MISMATCH", "工具版本或请求已变化，必须重新 dry-run。");
        Guard.Require(plan.CreatedAt <= DateTimeOffset.UtcNow && plan.ExpiresAt > DateTimeOffset.UtcNow &&
            plan.ExpiresAt - plan.CreatedAt == TimeSpan.FromMinutes(15),
            "PLAN_EXPIRED", "计划已过期或时间无效，必须重新 dry-run。");
        Guard.Require(plan.PageDescription is not null && plan.Fingerprint == Fingerprint(request, plan.PageDescription) && confirmation == Json.Hash(plan),
            "PLAN_CONFIRMATION", "计划被更改或 --confirm 不匹配；必须确认 dry-run 返回的完整 planSha256。");
    }
}

internal static class InfoPublisher
{
    public static async Task<Receipt> ExecuteAsync(UpdateInfoPlan plan, UpdateInfoRequest request,
        string confirmation, NexusApi api, string stateDirectory, CancellationToken ct, string? registryPath = null)
    {
        InfoPlanner.CheckPlan(plan, request, confirmation);
        using var journal = new Journal(stateDirectory, request);
        if (registryPath is not null)
            ModRegistry.CheckDescriptionBinding(registryPath, request.ModDirectory, request.ModId, request.PageDescription);
        await api.FilesAsync(request.ModId, ct);
        var templateRoot = DescriptionComposer.RootFor(registryPath);
        await DescriptionComposer.CheckAsync(plan.PageDescription!, request.ModDirectory, request.ModId,
            request.PageDescription, api, templateRoot, ct, request.Description);
        ct.ThrowIfCancellationRequested();
        journal.Set("uncertain", "update-description");
        try
        {
            await DescriptionComposer.PublishAsync(plan.PageDescription!, request.ModDirectory, request.ModId,
                request.PageDescription, api, templateRoot, journal, ct, request.Description);
            if (registryPath is not null && request.PageDescription is { } context)
            {
                journal.Set("partial", "save-description-settings");
                ModRegistry.SaveDescription(registryPath, request.DirectoryKey, request.ModId,
                    context, plan.PageDescription!.AfterSha256);
            }
            journal.Set("success", "complete");
            return journal.Current;
        }
        catch (Exception exception)
        {
            try
            {
                journal.Set(journal.Current.Stage is "verify-description" or "save-description-settings" ? "partial" : "uncertain", journal.Current.Stage,
                    errorCode: exception is SafeException safe ? safe.Code :
                        exception is OperationCanceledException ? "CANCELLED" : "TRANSPORT_ERROR");
            }
            catch { /* The durable pre-write journal remains authoritative. */ }
            throw new SafeException("PARTIAL_OR_UNCERTAIN",
                "描述同步未全部确认完成；查看 receipt 回执并核对 Nexus，禁止盲目重试。", 4);
        }
    }
}
