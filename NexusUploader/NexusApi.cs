using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Xml.Linq;

namespace NexusUploader;

internal sealed class NexusApi : IDisposable
{
    private readonly HttpClient api;
    private readonly HttpClient storage;
    private readonly string key;
    private readonly bool readOnly;

    public NexusApi(string key, bool readOnly, HttpMessageHandler? apiHandler = null, HttpMessageHandler? storageHandler = null)
    {
        this.key = key;
        this.readOnly = readOnly;
        api = new HttpClient(apiHandler ?? Handler()) { Timeout = TimeSpan.FromMinutes(2), MaxResponseContentBufferSize = 2 * 1024 * 1024 };
        api.DefaultRequestHeaders.Add("apikey", key);
        api.DefaultRequestHeaders.UserAgent.ParseAdd("MyUploader-NexusUploader/1.0");
        storage = new HttpClient(storageHandler ?? Handler()) { Timeout = TimeSpan.FromHours(1), MaxResponseContentBufferSize = 2 * 1024 * 1024 };
    }

    private static HttpClientHandler Handler() => new() { AllowAutoRedirect = false, UseCookies = false };
    public void Dispose() { api.Dispose(); storage.Dispose(); }

    internal static JsonElement Property(JsonElement value, string name)
    {
        Guard.Require(value.ValueKind == JsonValueKind.Object && value.TryGetProperty(name, out _), "API_SCHEMA", "Nexus 响应结构变化或字段缺失；已停止。");
        return value.GetProperty(name);
    }
    internal static string Text(JsonElement value, string name)
    {
        var field = Property(value, name);
        var text = field.ValueKind switch { JsonValueKind.String => field.GetString(), JsonValueKind.Number => field.GetRawText(), _ => null };
        Guard.Require(text is not null, "API_SCHEMA", "Nexus 响应字段类型错误；已停止。");
        return text!;
    }
    internal static bool Boolean(JsonElement value, string name)
    {
        var field = Property(value, name);
        Guard.Require(field.ValueKind is JsonValueKind.True or JsonValueKind.False, "API_SCHEMA", "Nexus 响应布尔字段类型错误。");
        return field.GetBoolean();
    }
    private static JsonElement[] Array(JsonElement value, string name)
    {
        var array = Property(value, name);
        Guard.Require(array.ValueKind == JsonValueKind.Array, "API_SCHEMA", "Nexus 响应数组类型错误。");
        return array.EnumerateArray().ToArray();
    }

    internal async Task<JsonElement> SendAsync(HttpMethod method, string path, object? body, int expected, CancellationToken ct)
    {
        Guard.Require(!readOnly || method == HttpMethod.Get, "DRY_RUN_WRITE", "dry-run 传输层禁止所有写请求。");
        Guard.Require(!path.Contains("..") && !path.Contains(':') && !path.StartsWith('/'), "API_ROUTE", "拒绝非固定 Nexus API 路由。");
        using var request = new HttpRequestMessage(method, "https://api.nexusmods.com/v3/" + path);
        if (body is not null)
        {
            var json = Json.Serialize(body);
            Guard.PublicText(json, key);
            request.Content = new StringContent(json, Encoding.UTF8, "application/json");
        }
        using var response = await api.SendAsync(request, ct);
        // No reason phrase, response body, request headers, URLs, or raw exceptions reach output.
        if ((int)response.StatusCode != expected)
            throw new SafeException("API_HTTP_" + (int)response.StatusCode, "Nexus API 返回非预期 HTTP 状态；响应正文已隐藏。", 3);
        if (expected == 204) return JsonDocument.Parse("{}").RootElement.Clone();
        var raw = await response.Content.ReadAsStringAsync(ct);
        using var doc = JsonDocument.Parse(raw);
        Json.NoDuplicates(doc.RootElement);
        // These documented endpoints return complete lists. Refuse pagination instead of validating only page one.
        if (doc.RootElement.TryGetProperty("links", out var links) && links.TryGetProperty("next", out var next))
            Guard.Require(next.ValueKind == JsonValueKind.Null, "PAGINATION", "Nexus 开始分页返回结果；完整性无法确认，已停止。");
        if (doc.RootElement.TryGetProperty("meta", out var meta) && meta.TryGetProperty("pagination", out _))
            throw new SafeException("PAGINATION", "Nexus 返回分页信息；需要更新客户端后再发布。");
        return Property(doc.RootElement, "data").Clone();
    }

    public async Task<Target> InspectAsync(string modId, CancellationToken ct)
    {
        var target = new Target(modId, await FilesAsync(modId, ct));
        Guard.PublicText(Json.Serialize(target), key);
        return target;
    }

    public async Task<ModResolution> ResolveAsync(ModPageReference page, CancellationToken ct)
    {
        Guard.Require(Regex.IsMatch(page.GameDomain, @"\A[a-z0-9_-]{1,100}\z", RegexOptions.CultureInvariant),
            "MOD_URL", "游戏域名格式无效。");
        Guard.Id(page.GameScopedId);
        var data = await SendAsync(HttpMethod.Get,
            $"games/{page.GameDomain}/mods/{page.GameScopedId}", null, 200, ct);
        var modId = Text(data, "id");
        var scopedId = Text(data, "game_scoped_id");
        var gameId = Text(data, "game_id");
        Guard.Id(modId);
        Guard.Id(scopedId);
        Guard.Id(gameId);
        Guard.Require(scopedId == page.GameScopedId, "MOD_RESOLVE_MISMATCH",
            "Nexus 返回的页面 ID 与 URL 不一致；已停止。");
        var resolution = new ModResolution(page.GameDomain, scopedId, modId, gameId);
        Guard.PublicText(Json.Serialize(resolution), key);
        return resolution;
    }
    public async Task<RemoteFile[]> FilesAsync(string modId, CancellationToken ct)
    {
        Guard.Id(modId);
        var data = await SendAsync(HttpMethod.Get, $"mods/{modId}/files", null, 200, ct);
        var files = new List<RemoteFile>();
        foreach (var file in Array(data, "mod_files"))
        {
            var id = Text(file, "id"); Guard.Id(id);
            Guard.Require(files.All(f => f.Id != id), "API_SCHEMA", "Nexus 返回重复文件 ID。");
            var versions = await VersionsAsync(id, ct);
            var count = Property(file, "versions_count");
            Guard.Require(count.ValueKind == JsonValueKind.Number && count.TryGetInt32(out var length) && length == versions.Length,
                "INCOMPLETE_HISTORY", "文件历史数量不符，可能发生并发更新或响应不完整。");
            files.Add(new(id, Text(file, "name"), Boolean(file, "is_active"), versions));
        }
        return files.OrderBy(f => f.Id, StringComparer.Ordinal).ToArray();
    }

    private async Task<FileVersion[]> VersionsAsync(string fileId, CancellationToken ct)
    {
        var data = await SendAsync(HttpMethod.Get, $"mod-files/{fileId}/versions", null, 200, ct);
        var versions = Array(data, "versions").Select(v =>
        {
            var id = Text(v, "id"); Guard.Id(id);
            Guard.Require(Text(Property(v, "file"), "id") == fileId, "FILE_MISMATCH", "历史版本属于其他文件。");
            var category = Text(v, "category");
            Guard.Require(category is "main" or "update" or "optional" or "miscellaneous" or "old_version" or "removed" or "archived" or "unknown", "API_SCHEMA", "出现未知文件分类。");
            return new FileVersion(id, Text(v, "name"), Text(v, "version"), category,
                v.TryGetProperty("is_primary", out _) && Boolean(v, "is_primary"));
        }).OrderBy(v => v.Id, StringComparer.Ordinal).ToArray();
        Guard.Require(versions.Select(v => v.Id).Distinct().Count() == versions.Length, "API_SCHEMA", "Nexus 返回重复版本 ID。");
        return versions;
    }

    public async Task<string> UploadAsync(FileStream archive, string filename, Action<string> recordUpload, CancellationToken ct)
    {
        var upload = await SendAsync(HttpMethod.Post, "uploads/multipart", new { filename, size_bytes = archive.Length }, 201, ct);
        var uploadId = Text(upload, "id");
        Guard.Require(Guid.TryParse(uploadId, out _), "API_SCHEMA", "上传 ID 格式异常。");
        recordUpload(uploadId);
        var sizeValue = Property(upload, "part_size_bytes");
        Guard.Require(sizeValue.TryGetInt64(out var partSize) && partSize > 0, "PART_SIZE", "上传分片大小无效。");
        var parts = Array(upload, "part_presigned_urls");
        Guard.Require(parts.Length is > 0 and <= 10000 && (archive.Length - 1) / partSize + 1 == parts.Length, "PART_COUNT", "分片数量与压缩包大小不符。");
        var urls = parts.Select(p => StorageUrl(p.GetString() ?? "")).ToArray();
        var complete = StorageUrl(Text(upload, "complete_presigned_url"));
        var etags = new List<XElement>();
        archive.Position = 0;
        for (var i = 0; i < urls.Length; i++)
        {
            using var request = new HttpRequestMessage(HttpMethod.Put, urls[i]);
            request.Content = new SliceContent(archive, Math.Min(partSize, archive.Length - archive.Position));
            using var response = await storage.SendAsync(request, ct);
            Guard.Require(response.StatusCode == HttpStatusCode.OK, "STORAGE_HTTP", "存储分片上传失败；签名地址和响应已隐藏。");
            var etag = response.Headers.ETag?.Tag;
            Guard.Require(!string.IsNullOrWhiteSpace(etag), "ETAG", "存储未返回分片 ETag。");
            etags.Add(new XElement("Part", new XElement("PartNumber", i + 1), new XElement("ETag", etag)));
        }
        using (var request = new HttpRequestMessage(HttpMethod.Post, complete))
        {
            request.Content = new StringContent(new XElement("CompleteMultipartUpload", etags).ToString(), Encoding.UTF8, "application/xml");
            using var response = await storage.SendAsync(request, ct);
            Guard.Require(response.StatusCode == HttpStatusCode.OK, "COMPLETE_HTTP", "合并分片请求失败。");
            // S3 can return HTTP 200 with an <Error> document: validate the XML result too.
            var result = XDocument.Parse(await response.Content.ReadAsStringAsync(ct));
            Guard.Require(result.Root?.Name.LocalName == "CompleteMultipartUploadResult", "COMPLETE_XML", "存储返回合并失败或未知结果。");
        }
        await SendAsync(HttpMethod.Post, $"uploads/{uploadId}/finalise", null, 200, ct);
        for (var attempt = 0; attempt < 60; attempt++)
        {
            var status = await SendAsync(HttpMethod.Get, $"uploads/{uploadId}", null, 200, ct);
            Guard.Require(Text(status, "id") == uploadId, "UPLOAD_MISMATCH", "上传状态 ID 错误。");
            var state = Text(status, "state");
            if (state == "available") return uploadId;
            Guard.Require(state == "created", "UPLOAD_STATE", "上传进入未知或失败状态。");
            await Task.Delay(TimeSpan.FromSeconds(5), ct);
        }
        throw new SafeException("UPLOAD_TIMEOUT", "等待 Nexus 处理上传超时；请核对回执后处理。", 3);
    }

    internal static Uri StorageUrl(string value)
    {
        Guard.Require(Uri.TryCreate(value, UriKind.Absolute, out var url) && url.Scheme == "https" && url.IsDefaultPort &&
            url.UserInfo == "" && url.Fragment == "" && !url.IsLoopback &&
            (url.Host.EndsWith(".amazonaws.com", StringComparison.OrdinalIgnoreCase) ||
             url.Host.EndsWith(".r2.cloudflarestorage.com", StringComparison.OrdinalIgnoreCase)),
            "STORAGE_HOST", "Nexus 返回未受信任的存储地址；需核实并更新允许列表，地址不会输出。");
        return url!;
    }

    public async Task<PublishResult> PublishAsync(Request request, PackageInfo package, FileIntent intent, string uploadId, CancellationToken ct)
    {
        var body = new Dictionary<string, object>
        {
            ["upload_id"] = uploadId, ["name"] = intent.Name, ["version"] = package.Version,
            ["file_category"] = "main", ["primary_mod_manager_download"] = true, ["update_mod_version"] = true
        };
        if (request.Description is not null) body["description"] = request.Description;
        if (intent.Action == "create") body["mod_id"] = request.ModId;
        var path = intent.Action == "create" ? "mod-files" : $"mod-files/{intent.FileId}/versions";
        var result = await SendAsync(HttpMethod.Post, path, body, 201, ct);
        var id = intent.Action == "create" ? Text(result, "id") : Text(Property(result, "version"), "id");
        Guard.Id(id);
        var responseFileId = intent.Action == "create" ? id : Text(Property(result, "file"), "id");
        Guard.Id(responseFileId);
        // The write response's UploadModFile.id is not a reliable update-chain identity.
        // Keep it for diagnosis; Publisher must find the returned version.id in the target mod's
        // complete history and verify its owning file group before allowing changelog writes.
        var published = new PublishResult(id, responseFileId);
        Guard.PublicText(Json.Serialize(published), key);
        return published;
    }

    public async Task ChangelogAsync(Request request, string version, CancellationToken ct)
    {
        if (request.Changelog is not null)
            await SendAsync(HttpMethod.Post, $"mods/{request.ModId}/changelogs", new { version, changelog = request.Changelog }, 201, ct);
    }
}

// Streams a bounded part without buffering the whole part/archive or closing the locked archive.
internal sealed class SliceContent(Stream source, long length) : HttpContent
{
    protected override bool TryComputeLength(out long value) { value = length; return true; }
    protected override Task SerializeToStreamAsync(Stream destination, TransportContext? context) => SerializeToStreamAsync(destination, context, CancellationToken.None);
    protected override async Task SerializeToStreamAsync(Stream destination, TransportContext? context, CancellationToken ct)
    {
        var remaining = length;
        var buffer = new byte[128 * 1024];
        while (remaining > 0)
        {
            var read = await source.ReadAsync(buffer.AsMemory(0, (int)Math.Min(buffer.Length, remaining)), ct);
            if (read == 0) throw new SafeException("ARCHIVE_CHANGED", "读取压缩包时提前遇到 EOF。");
            await destination.WriteAsync(buffer.AsMemory(0, read), ct);
            remaining -= read;
        }
    }
}
