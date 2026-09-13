using System.Text.Json;

namespace NexusUploader;

internal static class Program
{
    private static readonly HashSet<string> ValueFlags = new(["--request", "--config", "--plan", "--confirm", "--mod-id"]);
    public static async Task<int> Main(string[] args)
    {
        using var cancellation = new CancellationTokenSource();
        Console.CancelKeyPress += (_, e) => { e.Cancel = true; cancellation.Cancel(); };
        try { return await RunAsync(args, cancellation.Token); }
        catch (Exception exception)
        {
            // Deliberately use an allowlist of diagnostic messages. Never print exception.ToString/Message
            // from JSON, filesystem, HTTP, archive tools, or third-party response bodies.
            var safe = exception switch
            {
                SafeException known => known,
                OperationCanceledException => new SafeException("CANCELLED", "操作取消或超时。", 130),
                JsonException => new SafeException("JSON_INVALID", "JSON 语法、类型或字段无效；未知字段不被接受。"),
                System.Text.DecoderFallbackException => new SafeException("TEXT_ENCODING", "输入文本必须使用有效 UTF-8 编码。"),
                HttpRequestException => new SafeException("TRANSPORT_ERROR", "网络/TLS 请求失败；原始错误已隐藏。沙箱 Schannel 错误需在沙箱外核实。", 3),
                IOException => new SafeException("LOCAL_IO", "本地文件读写失败或目标被其他任务锁定；路径与文件内容已隐藏。"),
                UnauthorizedAccessException => new SafeException("LOCAL_ACCESS", "本地访问权限不足。"),
                _ => new SafeException("INTERNAL_ERROR", "操作失败；原始异常已隐藏以保护凭据。请运行离线测试检查环境。", 3)
            };
            Console.WriteLine(Json.Serialize(new { status = "error", code = safe.Code, message = safe.Message, exitCode = safe.ExitCode }));
            return safe.ExitCode;
        }
    }

    internal static (string Command, Dictionary<string, string> Flags) Parse(string[] args)
    {
        Guard.Require(args.Length > 0, "CLI", "缺少命令，请运行 --help。");
        var command = args[0];
        Guard.Require(command is "update-file" or "create-mod" or "update-info" or "inspect", "CLI", "未知命令，请运行 --help；参数内容不会回显。");
        Guard.Require(command is not ("create-mod" or "update-info"), "UNSUPPORTED_OPERATION",
            "当前未接入创建 mod 或修改页面信息的可靠 API。此操作不受支持，且不会读取凭据、访问网络或使用浏览器替代。");
        var flags = new Dictionary<string, string>();
        for (var i = 1; i < args.Length; i++)
        {
            var name = args[i];
            Guard.Require(!flags.ContainsKey(name), "CLI_DUPLICATE", "命令行含重复参数。");
            Guard.Require(ValueFlags.Contains(name) || name is "--dry-run" or "--execute", "CLI", "未知参数；不接受 API key、--yes 或 --force。");
            var value = "true";
            if (ValueFlags.Contains(name))
            {
                Guard.Require(++i < args.Length && !args[i].StartsWith("--", StringComparison.Ordinal), "CLI", "参数缺少值。");
                value = args[i];
            }
            flags.Add(name, value);
        }
        var allowed = command == "inspect" ? new[] { "--mod-id", "--config" } :
            new[] { "--request", "--config", "--plan", "--confirm", "--dry-run", "--execute" };
        Guard.Require(flags.Keys.All(allowed.Contains), "CLI", "命令包含不适用的参数。");
        Required(flags, "--config");
        if (command != "inspect")
        {
            Required(flags, "--request"); Required(flags, "--plan");
            Guard.Require(flags.ContainsKey("--dry-run") ^ flags.ContainsKey("--execute"), "CLI_MODE", "必须且只能选择 --dry-run 或 --execute。");
            Guard.Require(flags.ContainsKey("--execute") == flags.ContainsKey("--confirm"), "CLI_MODE", "执行必须提供 --confirm；dry-run 不接受确认参数。");
        }
        else { Required(flags, "--mod-id"); }
        return (command, flags);
    }

    private static string Required(Dictionary<string, string> flags, string name)
    {
        Guard.Require(flags.TryGetValue(name, out var value) && !string.IsNullOrWhiteSpace(value), "CLI", "缺少必需参数，请运行 --help。");
        return value!;
    }

    private static string StateDirectory()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "NexusUploader.csproj"))) return Path.Combine(directory.FullName, ".nexus-state");
            directory = directory.Parent;
        }
        return Path.Combine(AppContext.BaseDirectory, ".nexus-state");
    }

    internal static async Task<int> RunAsync(string[] args, CancellationToken ct)
    {
        if (args.Length == 0 || args is ["--help"] or ["-h"])
        {
            Console.WriteLine("""
                NexusUploader 1.0 — automation CLI (.NET 8, JSON stdout)
                Read AGENTS.md and README.md before publishing.

                inspect --mod-id <global Nexus v3 ID> --config <secret JSON>
                update-file --request <JSON> --config <secret JSON> --dry-run --plan <new *.plan.json>
                update-file --request <same JSON> --config <secret JSON> --execute --plan <plan> --confirm <planSha256>
                create-mod / update-info: unsupported until a reliable API integration is available (exit 2).

                Request: requestId, operation=update-file, modId, modDirectory; optional description/changelog.
                Package: reads modinfo.ini name/version, creates ZIP, computes hash automatically.
                Main Files: none -> create; one -> update; multiple -> error.
                Dry-run: temporary packaging, read-only remote checks, local plan creation, no upload.
                Execute: plan expires after 15 minutes; repackages and rechecks content and remote state.
                Only reliable APIs/tools are allowed. No browser/computer-use implementation or fallback.
                No interactive prompts, auto-login, API key CLI args, --yes, --force, or automatic retries.
                Exit codes: 0 success; 2 input/guard; 3 service/environment; 4 partial/uncertain; 130 cancelled.
                """);
            return 0;
        }
        var (command, flags) = Parse(args);
        var credentials = Credentials.Load(flags["--config"]);
        if (command == "inspect")
        {
            Guard.Id(flags["--mod-id"]);
            Guard.PublicText(flags["--mod-id"], credentials.ApiKey);
            using var api = new NexusApi(credentials.ApiKey, readOnly: true);
            var target = await api.InspectAsync(flags["--mod-id"], ct);
            Console.WriteLine(Json.Serialize(new { status = "inspected", target }));
            return 0;
        }
        var requestPath = Path.GetFullPath(flags["--request"]);
        var request = Guard.Validate(Json.Read<Request>(requestPath), command, Path.GetDirectoryName(requestPath)!, credentials.ApiKey);
        var planPath = Path.GetFullPath(flags["--plan"]);
        Guard.Require(planPath.EndsWith(".plan.json", StringComparison.OrdinalIgnoreCase), "PLAN_PATH", "计划文件必须使用 .plan.json 扩展名。");
        var dryRun = flags.ContainsKey("--dry-run");
        if (dryRun) Guard.Require(!File.Exists(planPath), "PLAN_EXISTS", "计划已存在；使用新的计划路径以保留审查记录。");
        using var client = new NexusApi(credentials.ApiKey, readOnly: dryRun);
        if (dryRun)
        {
            var plan = await Planner.PrepareAsync(request, client, credentials.ApiKey, ct);
            Json.WriteNew(planPath, plan);
            Console.WriteLine(Json.Serialize(new { status = "dry_run_ok", planSha256 = Json.Hash(plan), plan }));
        }
        else
        {
            var receipt = await Publisher.ExecuteAsync(Json.Read<Plan>(planPath), request, flags["--confirm"], client, credentials.ApiKey, StateDirectory(), ct);
            Console.WriteLine(Json.Serialize(new { status = receipt.Status, receipt }));
        }
        return 0;
    }
}
