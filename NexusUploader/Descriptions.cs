using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace NexusUploader;

internal static class DescriptionComposer
{
    internal static string DefaultRoot => Path.Combine(Path.GetDirectoryName(Program.DefaultConfigPath())!, "descriptions");
    internal static string RootFor(string? registryPath) => registryPath is null ? DefaultRoot :
        Path.Combine(Path.GetDirectoryName(Path.GetFullPath(registryPath))!, "descriptions");
    internal static string Normalize(string value) => value.Replace("\r\n", "\n").Replace('\r', '\n').Trim();
    internal static string TextHash(string? text) => Json.Hash(text is null ? null : Normalize(text));

    internal static void ValidateSelection(string? mode, string? profile)
    {
        Guard.Require(mode is null or "manual" or "template", "DESCRIPTION_MODE", "descriptionMode 只能是 manual 或 template。");
        Guard.Require(profile is null || Regex.IsMatch(profile, @"\A[a-z0-9][a-z0-9_-]{0,63}\z"),
            "DESCRIPTION_PROFILE", "descriptionProfile 只能使用小写 ASCII 字母、数字、下划线和连字符。");
        Guard.Require(mode != "manual" || profile is null, "DESCRIPTION_PROFILE", "manual 模式不能同时指定模板。");
    }

    internal static DescriptionContext Context(ModTarget target, string? mode, string? profile)
    {
        ValidateSelection(mode, profile);
        var settings = target.DescriptionSettings;
        if (mode is not null || profile is not null)
        {
            mode ??= "template";
            profile = mode == "manual" ? null : profile ?? settings?.Profile;
            Guard.Require(mode != "template" || profile is not null, "DESCRIPTION_PROFILE", "首次启用模板模式必须选择 descriptionProfile。");
            settings = new(mode, profile);
        }
        return new(target.GameDomain, settings, target.DescriptionSettings, target.DescriptionSha256,
            mode is not null || profile is not null);
    }

    internal static (string Text, DescriptionSource Source)? ReadDoc(string directory, Action<string> protect, bool required)
    {
        PlanStore.CheckLinks(directory);
        var paths = Directory.EnumerateFileSystemEntries(directory)
            .Where(p => Path.GetFileName(p).Equals("doc.bbcode", StringComparison.OrdinalIgnoreCase)).ToArray();
        Guard.Require(paths.Length <= 1, "DOC_FILE", "根目录 doc.bbcode 存在大小写冲突。");
        if (paths.Length == 0)
        {
            Guard.Require(!required, "DOC_MISSING", "mod 根目录缺少 doc.bbcode；调用方必须维护当前 mod 的完整独立说明。");
            return null;
        }
        return ReadSource(paths[0], protect, "DOC_FILE");
    }

    private static (string Text, DescriptionSource Source) ReadSource(string path, Action<string> protect, string code)
    {
        PlanStore.CheckLinks(path);
        Guard.Require(File.Exists(path) && !Directory.Exists(path), code, "说明或模板文件不存在，或不是普通文件。");
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        PlanStore.CheckLinks(path);
        Guard.Require(stream.Length <= 2 * 1024 * 1024, code, "说明或模板文件超过 2 MiB。");
        var bytes = new byte[(int)stream.Length];
        stream.ReadExactly(bytes);
        var text = Normalize(new UTF8Encoding(false, true).GetString(bytes).TrimStart('\uFEFF'));
        Guard.Text(text, 1_000_000, code);
        protect(text);
        return (text, new(Path.GetFullPath(path), Json.HashBytes(bytes)));
    }

    internal static string[] Profiles(string root, string gameDomain)
    {
        GameDomain(gameDomain);
        var directory = Path.Combine(root, "games", gameDomain, "profiles");
        PlanStore.CheckLinks(directory);
        if (!Directory.Exists(directory)) return [];
        return Directory.EnumerateFiles(directory, "*.bbcode").Select(Path.GetFileNameWithoutExtension)
            .Where(p => p is not null && Regex.IsMatch(p, @"\A[a-z0-9][a-z0-9_-]{0,63}\z"))
            .Select(p => p!).Order(StringComparer.Ordinal).ToArray();
    }

    private static void GameDomain(string? gameDomain) => Guard.Require(gameDomain is not null &&
        Regex.IsMatch(gameDomain, @"\A[a-z0-9_-]{1,100}\z"), "DESCRIPTION_GAME", "模板模式需要已绑定游戏的有效域名。");

    internal static RenderedDescription Render(string directory, DescriptionContext? context, string root,
        Action<string> protect, string? inline = null)
    {
        var sources = new List<DescriptionSource>();
        string text;
        if (inline is not null)
        {
            Guard.Require(context?.Settings?.Mode != "template", "DESCRIPTION_SOURCE",
                "模板模式只读取 doc.bbcode；不能同时提供完整 description。切换 manual 模式须显式声明。");
            text = Normalize(inline);
            Guard.Text(text, 1_000_000, "DESCRIPTION");
            protect(text);
        }
        else
        {
            var doc = ReadDoc(directory, protect, required: true)!.Value;
            text = doc.Text;
            sources.Add(doc.Source);
        }
        Guard.Require(!text.Contains("{{", StringComparison.Ordinal) && !text.Contains("}}", StringComparison.Ordinal),
            "DESCRIPTION_PLACEHOLDER", "独立正文中不能含有模板占位符。");
        if (context?.Settings is { Mode: "template" } settings)
        {
            GameDomain(context.GameDomain);
            ValidateSelection(settings.Mode, settings.Profile);
            Guard.Require(settings.Profile is not null, "DESCRIPTION_PROFILE", "模板模式缺少类型模板。");
            var gameRoot = Path.Combine(root, "games", context.GameDomain);
            foreach (var path in new[] { Path.Combine(gameRoot, "profiles", settings.Profile + ".bbcode"),
                Path.Combine(gameRoot, "game.bbcode"), Path.Combine(root, "global.bbcode") })
            {
                var wrapper = ReadSource(path, protect, "TEMPLATE_MISSING");
                const string token = "{{content}}";
                var tokenIndex = wrapper.Text.IndexOf(token, StringComparison.Ordinal);
                var remainder = tokenIndex < 0 ? wrapper.Text : wrapper.Text.Remove(tokenIndex, token.Length);
                Guard.Require(tokenIndex >= 0 && !remainder.Contains("{{", StringComparison.Ordinal) &&
                    !remainder.Contains("}}", StringComparison.Ordinal),
                    "TEMPLATE_PLACEHOLDER", "每层模板必须恰好包含一个 {{content}}，且不能含其他占位符。");
                text = wrapper.Text.Replace("{{content}}", text, StringComparison.Ordinal);
                Guard.Text(text, 1_000_000, "DESCRIPTION");
                sources.Add(wrapper.Source);
            }
        }
        ValidateImages(text);
        protect(text);
        return new(text, sources.ToArray());
    }

    internal static void ValidateImages(string text)
    {
        // Scan once; malformed repeated opening tags must not cause quadratic regex backtracking.
        var starts = new Regex(@"\[(/?)(img|code|noparse)(?=[ =\]])", RegexOptions.IgnoreCase);
        var cursor = 0;
        while (cursor < text.Length)
        {
            var match = starts.Match(text, cursor);
            if (!match.Success) break;
            var tag = match.Groups[2].Value.ToLowerInvariant();
            var code = tag == "img" ? "DESCRIPTION_IMAGE" : "DESCRIPTION_BBCODE";
            var endOpen = text.IndexOf(']', match.Index);
            var nestedOpen = text.IndexOf('[', match.Index + 1);
            Guard.Require(endOpen >= 0 && (nestedOpen < 0 || nestedOpen > endOpen), code, "图片或文字代码块标签不完整。");
            if (match.Groups[1].Value == "/")
            {
                Guard.Require(tag != "img", code, "图片结束标签缺少对应开始标签。");
                cursor = endOpen + 1;
                continue;
            }
            var close = "[/" + tag + "]";
            var end = text.IndexOf(close, endOpen + 1, StringComparison.OrdinalIgnoreCase);
            Guard.Require(end >= 0, code, "图片或文字代码块缺少结束标签。");
            if (tag == "img") Guard.Require(Uri.TryCreate(text[(endOpen + 1)..end].Trim(), UriKind.Absolute, out var url) &&
                url.Scheme == Uri.UriSchemeHttps && url.IsDefaultPort && url.UserInfo.Length == 0,
                "DESCRIPTION_IMAGE", "图片必须使用公开 HTTPS 图片直链，不能使用本地路径、data URL 或带身份信息的地址。");
            cursor = end + close.Length;
        }
    }

    internal static async Task<DescriptionPlan> PrepareAsync(string directory, string modId, DescriptionContext? context,
        NexusApi api, string root, CancellationToken ct, string? inline = null)
    {
        var rendered = Render(directory, context, root, api.Protect, inline);
        var before = await api.DescriptionAsync(modId, ct);
        var beforeHash = TextHash(before);
        Guard.Require(context?.PreviousSha256 is null || context.ExplicitSettings || context.PreviousSha256 == beforeHash,
            "REMOTE_DESCRIPTION_CHANGED", "远端正文偏离上次同步结果；先 inspect 核对。确认接管后显式指定 descriptionMode，重新 dry-run 审核替换。");
        var afterHash = TextHash(rendered.Text);
        return new(before, rendered.Text, beforeHash, afterHash, rendered.Sources, beforeHash != afterHash);
    }

    internal static async Task CheckAsync(DescriptionPlan plan, string directory, string modId, DescriptionContext? context,
        NexusApi api, string root, CancellationToken ct, string? inline = null)
    {
        var current = await PrepareAsync(directory, modId, context, api, root, ct, inline);
        Guard.Require(Json.Hash(current) == Json.Hash(plan), "DESCRIPTION_CHANGED",
            "doc.bbcode、模板或远端正文已变化；必须重新 dry-run，不会采用未经审核的内容。");
    }

    internal static async Task PublishAsync(DescriptionPlan plan, string directory, string modId, DescriptionContext? context,
        NexusApi api, string root, Journal journal, CancellationToken ct, string? inline = null)
    {
        await CheckAsync(plan, directory, modId, context, api, root, ct, inline);
        if (plan.WriteRequired)
        {
            journal.Set("uncertain", "update-description");
            await api.UpdateDescriptionAsync(modId, plan.After, ct);
        }
        journal.Set("partial", "verify-description");
        Guard.Require(TextHash(await api.DescriptionAsync(modId, ct)) == plan.AfterSha256,
            "DESCRIPTION_MISMATCH", "页面正文读回与计划不一致；禁止重试。");
    }
}
