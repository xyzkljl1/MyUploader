using System.Text;

namespace NexusUploader;

internal static class Planner
{
    public const string Version = "1.0";
    public static string Fingerprint(Request request, Target target, PackageInfo package, FileIntent intent) => Json.Hash(new { toolVersion = Version, request, target, package, intent });

    public static FileIntent Resolve(Request request, Target target, PackageInfo package)
    {
        Guard.Require(target.ModId == request.ModId, "TARGET_MISMATCH", "目标 modId 与请求不一致。");
        Guard.Require(!target.Files.SelectMany(f => f.Versions).Any(v => string.Equals(v.Version.Trim(), package.Version, StringComparison.OrdinalIgnoreCase)),
            "DUPLICATE_VERSION", "该版本已在目标 mod 的文件历史中使用，禁止重复上传。");
        var main = target.Files.Where(f => f.IsActive && f.Versions.Any(v => v.Category == "main")).ToArray();
        Guard.Require(main.Length <= 1, "MAIN_AMBIGUOUS", "存在多个有效 Main File，禁止自动选择。");
        if (main.Length == 0) return new("create", null, package.Name, "main");
        Guard.FileName(main[0].Name);
        return new("update", main[0].Id, main[0].Name, "main");
    }

    public static async Task<Plan> PrepareAsync(Request request, NexusApi api, string key, CancellationToken ct)
    {
        using var package = await Packaging.BuildAsync(request.ModDirectory, key, ct);
        var target = await api.InspectAsync(request.ModId, ct);
        var intent = Resolve(request, target, package.Info);
        Guard.PublicText(Json.Serialize(new { target, package = package.Info, intent }), key);
        var now = DateTimeOffset.UtcNow;
        return new(Version, now, now.AddMinutes(15), request, target, package.Info, intent, Fingerprint(request, target, package.Info, intent));
    }

    public static void CheckPlan(Plan plan, Request request, string confirmation)
    {
        Guard.Require(plan.ToolVersion == Version && Json.Hash(request) == Json.Hash(plan.Request), "PLAN_MISMATCH", "工具版本或请求已变化，必须重新 dry-run。");
        Guard.Require(plan.CreatedAt <= DateTimeOffset.UtcNow && plan.ExpiresAt > DateTimeOffset.UtcNow && plan.ExpiresAt - plan.CreatedAt == TimeSpan.FromMinutes(15), "PLAN_EXPIRED", "计划已过期或时间无效，必须重新 dry-run。");
        Guard.Require(plan.Fingerprint == Fingerprint(request, plan.Target, plan.Package, plan.Intent) && confirmation == Json.Hash(plan), "PLAN_CONFIRMATION", "计划被更改或 --confirm 不匹配；必须确认 dry-run 返回的完整 planSha256。");
    }
}

internal sealed class Journal : IDisposable
{
    private readonly FileStream gate;
    private readonly string path;
    public Receipt Current { get; private set; }

    public Journal(string stateDirectory, Request request)
    {
        Directory.CreateDirectory(stateDirectory);
        var targetLock = Json.Hash(new { request.ModId });
        gate = new FileStream(Path.Combine(stateDirectory, targetLock + ".lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        path = Path.Combine(stateDirectory, request.RequestId + ".receipt.json");
        Current = new(request.RequestId, "not_started", "validated");
        try
        {
            Guard.Require(!File.Exists(path), "ALREADY_ATTEMPTED", "此 requestId 已尝试执行。请检查回执及 Nexus，禁止自动重试或换 ID 绕过。");
        }
        catch { gate.Dispose(); throw; }
    }

    public void Set(string status, string stage, string? uploadId = null, string? publishedId = null, string? errorCode = null)
    {
        Current = Current with { Status = status, Stage = stage, UploadId = uploadId ?? Current.UploadId, PublishedId = publishedId ?? Current.PublishedId, ErrorCode = errorCode };
        var temporary = path + ".tmp";
        using (var stream = new FileStream(temporary, FileMode.Create, FileAccess.Write, FileShare.None))
        {
            stream.Write(Encoding.UTF8.GetBytes(Json.Serialize(Current)));
            stream.Flush(true);
        }
        File.Move(temporary, path, overwrite: true);
    }
    public void Dispose() => gate.Dispose();
}

internal static class Publisher
{
    public static async Task<Receipt> ExecuteAsync(Plan plan, Request request, string confirm, NexusApi api, string key, string stateDirectory, CancellationToken ct)
    {
        Planner.CheckPlan(plan, request, confirm);
        using var journal = new Journal(stateDirectory, request);
        using var package = await Packaging.BuildAsync(request.ModDirectory, key, ct);
        Guard.Require(Json.Hash(package.Info) == Json.Hash(plan.Package), "PACKAGE_CHANGED", "文件夹内容或 modinfo.ini 已变化，必须重新 dry-run。");
        var target = await api.InspectAsync(request.ModId, ct);
        var intent = Planner.Resolve(request, target, package.Info);
        Guard.Require(Planner.Fingerprint(request, target, package.Info, intent) == plan.Fingerprint, "REMOTE_CHANGED", "Nexus 文件状态已变化，必须重新 dry-run；不会静默切换创建/更新动作。");
        ct.ThrowIfCancellationRequested();
        journal.Set("uncertain", "create-upload"); // Persist before the first side effect, including timeouts/cancellation.
        try
        {
            var upload = await api.UploadAsync(package.Stream, package.Info.ArchiveName,
                id => journal.Set("uncertain", "upload-parts", uploadId: id), ct);
            journal.Set("uncertain", "recheck-before-publish");
            var latest = await api.InspectAsync(request.ModId, ct);
            Guard.Require(Json.Hash(latest) == Json.Hash(target), "REMOTE_CHANGED", "上传期间 Nexus 状态变化；压缩包已上传但未关联文件，请先核对。");
            journal.Set("uncertain", "publish-file");
            var published = await api.PublishAsync(request, package.Info, intent, upload, ct);
            journal.Set("partial", "verify-file", publishedId: published);
            var files = await api.FilesAsync(request.ModId, ct);
            var matches = files.SelectMany(f => f.Versions.Select(v => (File: f, Version: v)))
                .Where(x => x.Version.Version == package.Info.Version && x.Version.IsPrimary && x.Version.Category == "main" && x.Version.Name == intent.Name).ToArray();
            Guard.Require(matches.Length == 1 && (intent.Action == "create" ? matches[0].File.Id == published : matches[0].File.Id == intent.FileId && matches[0].Version.Id == published),
                "VERIFY_FAILED", "Nexus 已返回创建成功，但读回核验未通过；禁止再次上传。");
            journal.Set("partial", "changelog");
            await api.ChangelogAsync(request, package.Info.Version, ct);
            journal.Set("success", "complete");
            return journal.Current;
        }
        catch (Exception exception)
        {
            try
            {
                journal.Set(journal.Current.PublishedId is null ? "uncertain" : "partial", journal.Current.Stage,
                    errorCode: exception is SafeException safe ? safe.Code : exception is OperationCanceledException ? "CANCELLED" : "TRANSPORT_ERROR");
            }
            catch { /* The last durable pre-write journal remains authoritative. Always report exit 4. */ }
            throw new SafeException("PARTIAL_OR_UNCERTAIN", "已开始远程写入，结果部分完成或不确定；查看 receipt 回执并核对 Nexus，禁止盲目重试。", 4);
        }
    }
}
