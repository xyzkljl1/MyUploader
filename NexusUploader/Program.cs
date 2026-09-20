using System.Text.Json;

namespace NexusUploader;

internal static class Program
{
    private static readonly HashSet<string> ValueFlags = new(["--request", "--config", "--plan", "--confirm", "--mod-id", "--mod-url", "--name", "--target"]);
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
        var firstFlag = 1;
        if (command == "target")
        {
            Guard.Require(args.Length > 1 && args[1] is "add" or "list" or "show", "CLI", "target 必须指定 add、list 或 show。");
            command = "target-" + args[1];
            firstFlag = 2;
        }
        Guard.Require(command is "update-file" or "create-mod" or "update-info" or "inspect" or "resolve" or
            "target-add" or "target-list" or "target-show", "CLI", "未知命令，请运行 --help；参数内容不会回显。");
        Guard.Require(command is not ("create-mod" or "update-info"), "UNSUPPORTED_OPERATION",
            "当前未接入创建 mod 或修改页面信息的可靠 API。此操作不受支持，且不会读取凭据、访问网络或使用浏览器替代。");
        var flags = new Dictionary<string, string>();
        for (var i = firstFlag; i < args.Length; i++)
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
        var allowed = command switch
        {
            "inspect" => new[] { "--mod-id", "--mod-url", "--target", "--config" },
            "resolve" => new[] { "--mod-url", "--config" },
            "target-add" => new[] { "--name", "--mod-url", "--config" },
            "target-list" => new[] { "--config" },
            "target-show" => new[] { "--name", "--config" },
            _ => new[] { "--request", "--config", "--plan", "--confirm", "--dry-run", "--execute" }
        };
        Guard.Require(flags.Keys.All(allowed.Contains), "CLI", "命令包含不适用的参数。");
        if (command == "update-file")
        {
            Required(flags, "--request"); Required(flags, "--plan");
            Guard.Require(flags.ContainsKey("--dry-run") ^ flags.ContainsKey("--execute"), "CLI_MODE", "必须且只能选择 --dry-run 或 --execute。");
            Guard.Require(flags.ContainsKey("--execute") == flags.ContainsKey("--confirm"), "CLI_MODE", "执行必须提供 --confirm；dry-run 不接受确认参数。");
        }
        else if (command == "inspect")
            Guard.Require(new[] { "--mod-id", "--mod-url", "--target" }.Count(flags.ContainsKey) == 1,
                "CLI", "inspect 必须且只能提供 --mod-id、--mod-url 或 --target。");
        else if (command == "resolve") Required(flags, "--mod-url");
        else if (command == "target-add") { Required(flags, "--name"); Required(flags, "--mod-url"); }
        else if (command == "target-show") Required(flags, "--name");
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

    internal static string DefaultConfigPath()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "NexusUploader.csproj"))) return Path.Combine(directory.FullName, "config.json");
            directory = directory.Parent;
        }
        return Path.Combine(AppContext.BaseDirectory, "config.json");
    }

    internal static async Task<int> RunAsync(string[] args, CancellationToken ct)
    {
        if (args.Length == 0 || args is ["--help"] or ["-h"])
        {
            Console.WriteLine("""
                NexusUploader 1.0 — automation CLI (.NET 8, JSON stdout)
                Read AGENTS.md and README.md before publishing.

                resolve --mod-url <Nexus mod page URL>
                target add --name <stable-name> --mod-url <Nexus mod page URL>
                target list
                target show --name <stable-name>
                inspect (--mod-id <global Nexus v3 ID> | --mod-url <Nexus mod page URL> | --target <stable-name>)
                update-file --request <JSON> --dry-run --plan <new *.plan.json>
                update-file --request <same JSON> --execute --plan <plan> --confirm <planSha256>
                create-mod / update-info: unsupported until a reliable API integration is available (exit 2).

                Request: requestId, operation=update-file, exactly one of target/modId, modDirectory; optional description/changelog.
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
        var configPath = flags.GetValueOrDefault("--config") ?? DefaultConfigPath();
        var modConfigPath = ModRegistry.PathForConfig(configPath);
        if (command == "target-list")
        {
            Console.WriteLine(Json.Serialize(new { status = "listed", targets = ModRegistry.Load(modConfigPath, allowMissing: true).Targets }));
            return 0;
        }
        if (command == "target-show")
        {
            Console.WriteLine(Json.Serialize(new { status = "shown", target = ModRegistry.Find(modConfigPath, flags["--name"]) }));
            return 0;
        }
        var credentials = Credentials.Load(configPath);
        if (command == "target-add")
        {
            var modUrl = flags["--mod-url"];
            Guard.PublicText(modUrl, credentials.ApiKey);
            using var api = new NexusApi(credentials.ApiKey, readOnly: true);
            var resolution = await api.ResolveAsync(Guard.ModUrl(modUrl), ct);
            var target = ModRegistry.Add(modConfigPath, flags["--name"], resolution);
            Console.WriteLine(Json.Serialize(new { status = "added", target }));
            return 0;
        }
        if (command is "inspect" or "resolve")
        {
            using var api = new NexusApi(credentials.ApiKey, readOnly: true);
            ModResolution? resolution = null;
            var modId = flags.GetValueOrDefault("--mod-id");
            if (flags.TryGetValue("--target", out var targetName))
            {
                modId = ModRegistry.Find(modConfigPath, targetName).ModId;
            }
            else if (modId is null)
            {
                var modUrl = flags["--mod-url"];
                Guard.PublicText(modUrl, credentials.ApiKey);
                resolution = await api.ResolveAsync(Guard.ModUrl(modUrl), ct);
                modId = resolution.ModId;
            }
            else
            {
                Guard.Id(modId);
                Guard.PublicText(modId, credentials.ApiKey);
            }
            if (command == "resolve")
                Console.WriteLine(Json.Serialize(new { status = "resolved", resolution }));
            else
            {
                var target = await api.InspectAsync(modId, ct);
                Console.WriteLine(Json.Serialize(new { status = "inspected", resolution, target }));
            }
            return 0;
        }
        var requestPath = Path.GetFullPath(flags["--request"]);
        var request = Guard.Validate(ModRegistry.Bind(Json.Read<Request>(requestPath), modConfigPath),
            command, Path.GetDirectoryName(requestPath)!, credentials.ApiKey);
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
