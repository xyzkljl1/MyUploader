using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using System.Diagnostics.CodeAnalysis;

namespace NexusUploader;

internal sealed class SafeException(string code, string message, int exitCode = 2) : Exception(message)
{
    public string Code { get; } = code;
    public int ExitCode { get; } = exitCode;
}

internal sealed record Request
{
    public string RequestId { get; init; } = "";
    public string Operation { get; init; } = "";
    public string ModId { get; init; } = "";
    public string ModDirectory { get; init; } = "";
    public string? Description { get; init; }
    public string? Changelog { get; init; }
}

internal sealed record FileVersion(string Id, string Name, string Version, string Category, bool IsPrimary);
internal sealed record RemoteFile(string Id, string Name, bool IsActive, FileVersion[] Versions);
internal sealed record Target(string ModId, RemoteFile[] Files);
internal sealed record PackageInfo(string Name, string Version, string ArchiveName, long SizeBytes, string Sha256, int FileCount);
internal sealed record FileIntent(string Action, string? FileId, string Name, string Category);
internal sealed record Plan(string ToolVersion, DateTimeOffset CreatedAt, DateTimeOffset ExpiresAt,
    Request Request, Target Target, PackageInfo Package, FileIntent Intent, string Fingerprint);
internal sealed record Receipt(string RequestId, string Status, string Stage, string? UploadId = null,
    string? PublishedId = null, string? ErrorCode = null);

internal static class Json
{
    public static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        WriteIndented = true
    };

    public static T Read<T>(string path)
    {
        var text = ReadText(path);
        return Parse<T>(text);
    }

    public static string ReadText(string path)
    {
        Guard.Require(File.Exists(path) && new FileInfo(path).Length <= 2 * 1024 * 1024,
            "INPUT_FILE", "JSON 输入文件不存在或超过 2 MiB 限制。");
        return File.ReadAllText(path, new UTF8Encoding(false, true));
    }

    public static T Parse<T>(string text)
    {
        using var doc = JsonDocument.Parse(text);
        NoDuplicates(doc.RootElement);
        return JsonSerializer.Deserialize<T>(text, Options) ?? throw new SafeException("JSON", "JSON 内容为空。");
    }

    public static void NoDuplicates(JsonElement value)
    {
        if (value.ValueKind == JsonValueKind.Object)
        {
            var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var property in value.EnumerateObject())
            {
                Guard.Require(names.Add(property.Name), "DUPLICATE_JSON", "JSON 含重复字段（包括大小写变体）。");
                NoDuplicates(property.Value);
            }
        }
        else if (value.ValueKind == JsonValueKind.Array)
            foreach (var item in value.EnumerateArray()) NoDuplicates(item);
    }

    public static string Serialize<T>(T value) => JsonSerializer.Serialize(value, Options);
    public static string Hash<T>(T value) => HashBytes(Encoding.UTF8.GetBytes(Serialize(value)));
    public static string HashBytes(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();

    public static void WriteNew<T>(string path, T value)
    {
        using var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None);
        var bytes = Encoding.UTF8.GetBytes(Serialize(value));
        stream.Write(bytes);
        stream.Flush(true);
    }
}

internal static class Guard
{
    public static void Require([DoesNotReturnIf(false)] bool condition, string code, string message)
    {
        if (!condition) throw new SafeException(code, message);
    }

    public static void Id(string? value) => Require(value is not null && Regex.IsMatch(value, @"\A[1-9][0-9]{0,24}\z"), "ID", "ID 必须是正整数字符串，不可使用占位符或从页面 ID 猜测全局 ID。");
    public static void FileName(string value) => Require(Regex.IsMatch(value, @"\A[a-zA-Z0-9 _'().-]{1,50}\z") && value.Trim() == value && value.Any(char.IsLetterOrDigit),
        "FILE_NAME", "文件显示名称必须含字母或数字，长度为 1–50，且仅含 ASCII 字母、数字、空格、下划线、单引号、括号、点和连字符。");
    public static void Version(string value) => Require(Regex.IsMatch(value, @"\A[a-zA-Z0-9.-]{1,50}\z") && value.Any(char.IsLetterOrDigit),
        "VERSION", "modinfo.ini 的 version 必须含字母或数字，长度为 1–50，且仅含 ASCII 字母、数字、点和连字符。");
    public static void Text(string? value, int max, string code) => Require(!string.IsNullOrWhiteSpace(value) && value.Length <= max && value == value.Trim() && !value.Any(c => char.IsControl(c) && c is not '\n' and not '\r' and not '\t'), code, "文本为空、含非法控制字符、首尾空白或超出长度限制。");
    public static void PublicText(string value, string apiKey)
    {
        var escapedKey = JsonSerializer.Serialize(apiKey)[1..^1];
        Require(!value.Contains(apiKey, StringComparison.Ordinal) && !value.Contains(escapedKey, StringComparison.Ordinal) && !Regex.IsMatch(value,
            @"(?i)(NEXUSMODS_API_KEY|[?&](x-amz-|x-goog-|signature=)|(?:apikey|authorization|cookie)\s*[:=])"),
            "SECRET_IN_INPUT", "公开内容包含凭据或签名 URL 特征；拒绝输出和上传。");
    }

    public static Request Validate(Request request, string operation, string baseDirectory, string apiKey)
    {
        Require(request.Operation == operation && operation == "update-file", "OPERATION", "operation 必须为受支持的 update-file 命令。");
        Require(Guid.TryParseExact(request.RequestId, "D", out var requestId) && requestId != Guid.Empty,
            "REQUEST_ID", "requestId 必须是非空 UUID；重试同一操作必须保留此 ID。");
        PublicText(Json.Serialize(request), apiKey);
        Id(request.ModId);
        if (request.Description is not null) Text(request.Description, 50000, "DESCRIPTION");
        if (request.Changelog is not null) Text(request.Changelog, 50000, "CHANGELOG");
        Text(request.ModDirectory, 4096, "MOD_DIRECTORY");
        var directory = Path.TrimEndingDirectorySeparator(Path.GetFullPath(request.ModDirectory, baseDirectory));
        Require(!directory.StartsWith(@"\\", StringComparison.Ordinal) && Directory.Exists(directory), "MOD_DIRECTORY", "modDirectory 必须是存在的本地文件夹。");
        var normalized = request with { RequestId = requestId.ToString("D"), ModDirectory = directory };
        PublicText(Json.Serialize(normalized), apiKey);
        return normalized;
    }
}

internal sealed class Credentials
{
    public string ApiKey { get; }
    private Credentials(string apiKey) => ApiKey = apiKey;
    public static Credentials Load(string path)
    {
        // Compatible with the reference updater.json. Never enumerate or serialize the other entries.
        using var doc = JsonDocument.Parse(Json.ReadText(path));
        Json.NoDuplicates(doc.RootElement);
        Guard.Require(doc.RootElement.TryGetProperty("NEXUSMODS_API_KEY", out var key) && key.ValueKind == JsonValueKind.String,
            "CREDENTIALS", "配置缺少 NEXUSMODS_API_KEY 字符串。不要打印配置。");
        var value = key.GetString()!;
        Guard.Require(value.Length >= 16 && value.Length <= 8192 && value.All(c => c is >= '!' and <= '~'),
            "CREDENTIALS", "API key 为空或格式无效。不要打印配置。");
        return new(value);
    }
}
