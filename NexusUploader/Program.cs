using System.Text.Json;

namespace NexusUploader;

internal static class Program
{
    private static readonly HashSet<string> ValueFlags = new(["--request", "--plan", "--confirm", "--mod", "--mod-directory"]);
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
            Guard.Require(args.Length > 1 && args[1] is "add" or "candidates" or "list", "CLI", "target 必须指定 add、candidates 或 list。");
            command = "target-" + args[1];
            firstFlag = 2;
        }
        Guard.Require(command is "update-file" or "update-info" or "inspect" or
            "target-add" or "target-candidates" or "target-list", "CLI", "未知命令，请运行 --help；参数内容不会回显。");
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
            "inspect" => new[] { "--mod", "--mod-directory" },
            "target-add" => new[] { "--mod-directory", "--mod" },
            "target-candidates" or "target-list" => Array.Empty<string>(),
            _ => new[] { "--request", "--plan", "--confirm", "--dry-run", "--execute" }
        };
        Guard.Require(flags.Keys.All(allowed.Contains), "CLI", "命令包含不适用的参数。");
        if (command is "update-file" or "update-info")
        {
            Required(flags, "--request"); Required(flags, "--plan");
            Guard.Require(flags.ContainsKey("--dry-run") ^ flags.ContainsKey("--execute"), "CLI_MODE", "必须且只能选择 --dry-run 或 --execute。");
            Guard.Require(flags.ContainsKey("--execute") == flags.ContainsKey("--confirm"), "CLI_MODE", "执行必须提供 --confirm；dry-run 不接受确认参数。");
        }
        else if (command == "inspect")
            Guard.Require(new[] { "--mod", "--mod-directory" }.Count(flags.ContainsKey) == 1,
                "CLI", "inspect 必须且只能提供 --mod 或 --mod-directory。");
        else if (command == "target-add") { Required(flags, "--mod-directory"); Required(flags, "--mod"); }
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
                NexusUploader 1.4 — automation CLI (.NET 8, JSON stdout)
                Read AGENTS.md and README.md before publishing.

                target candidates
                target add --mod-directory <full local mod directory> --mod <global Nexus v3 ID | Nexus mod page URL>
                target list
                inspect (--mod <global Nexus v3 ID | Nexus mod page URL> | --mod-directory <full local mod directory>)
                update-file --request <JSON> --dry-run --plan <new *.plan.json>
                update-file --request <same JSON> --execute --plan <plan> --confirm <planSha256>
                update-info --request <JSON> --dry-run --plan <new *.plan.json>
                update-info --request <same JSON> --execute --plan <plan> --confirm <planSha256>

                update-file request: requestId, operation, modDirectory; optional includeRootDirectory (default true), description/changelog.
                update-info request: requestId, operation, modDirectory, description (full mod page BBCode).
                Package: reads modinfo.ini name/version, creates ZIP, computes hash automatically.
                Main Files: none -> create; one -> update; multiple -> error.
                Dry-run: read-only remote checks and local plan creation; update-file also builds a temporary package.
                Execute: plan expires after 15 minutes; update-file repackages and rechecks content and remote state.
                Only reliable APIs/tools are allowed. No browser/computer-use implementation or fallback.
                No interactive prompts, auto-login, API key CLI args, --yes, --force, or automatic retries.
                Exit codes: 0 success; 2 input/guard; 3 service/environment; 4 partial/uncertain; 130 cancelled.
                """);
            return 0;
        }
        var (command, flags) = Parse(args);
        var configPath = DefaultConfigPath();
        var modConfigPath = ModRegistry.PathForConfig(configPath);
        if (command == "target-list")
        {
            Console.WriteLine(Json.Serialize(new { status = "listed", targets = ModRegistry.Load(modConfigPath, allowMissing: true).Targets }));
            return 0;
        }
        var credentials = Credentials.Load(configPath);
        if (command == "target-candidates")
        {
            var registered = ModRegistry.Load(modConfigPath, allowMissing: true).Targets
                .Select(x => x.ModId).ToHashSet(StringComparer.Ordinal);
            using var api = new NexusApi(credentials.ApiKey, readOnly: true);
            var candidates = await api.CandidatesAsync(registered, ct);
            Console.WriteLine(Json.Serialize(new { status = "candidates", candidates }));
            return 0;
        }
        if (command == "target-add")
        {
            var mod = flags["--mod"];
            Guard.PublicText(mod, credentials.ApiKey);
            using var api = new NexusApi(credentials.ApiKey, readOnly: true);
            ModResolution resolution;
            if (IsNumeric(mod))
            {
                Guard.Id(mod);
                var registered = ModRegistry.Load(modConfigPath, allowMissing: true).Targets
                    .Select(x => x.ModId).ToHashSet(StringComparer.Ordinal);
                Guard.Require(!registered.Contains(mod), "TARGET_EXISTS", "该全局 modId 已登记；禁止建立重复别名。");
                resolution = (await api.CandidateAsync(mod, registered, ct)).Resolution;
            }
            else resolution = await api.ResolveAsync(Guard.ModUrl(mod), ct);
            var target = ModRegistry.Add(modConfigPath, flags["--mod-directory"], resolution);
            Console.WriteLine(Json.Serialize(new { status = "added", target }));
            return 0;
        }
        if (command == "inspect")
        {
            using var api = new NexusApi(credentials.ApiKey, readOnly: true);
            ModResolution? resolution = null;
            string? modId = null;
            if (flags.TryGetValue("--mod-directory", out var modDirectory))
            {
                modId = ModRegistry.Find(modConfigPath, modDirectory).ModId;
            }
            else
            {
                var mod = flags["--mod"];
                Guard.PublicText(mod, credentials.ApiKey);
                if (IsNumeric(mod))
                {
                    Guard.Id(mod);
                    modId = mod;
                }
                else
                {
                    resolution = await api.ResolveAsync(Guard.ModUrl(mod), ct);
                    modId = resolution.ModId;
                }
            }
            var target = await api.InspectAsync(modId, ct);
            Console.WriteLine(Json.Serialize(new { status = "inspected", resolution, target }));
            return 0;
        }
        var requestPath = Path.GetFullPath(flags["--request"]);
        var planPath = Path.GetFullPath(flags["--plan"]);
        Guard.Require(planPath.EndsWith(".plan.json", StringComparison.OrdinalIgnoreCase), "PLAN_PATH", "计划文件必须使用 .plan.json 扩展名。");
        var dryRun = flags.ContainsKey("--dry-run");
        if (dryRun) Guard.Require(!File.Exists(planPath), "PLAN_EXISTS", "计划已存在；使用新的计划路径以保留审查记录。");
        using var client = new NexusApi(credentials.ApiKey, readOnly: dryRun);
        if (command == "update-info")
        {
            var input = Guard.Validate(Json.Read<UpdateInfoInput>(requestPath), command,
                Path.GetDirectoryName(requestPath)!, credentials.ApiKey);
            var request = ModRegistry.Bind(input, modConfigPath);
            if (dryRun)
            {
                var plan = await InfoPlanner.PrepareAsync(request, client, ct);
                Json.WriteNew(planPath, plan);
                Console.WriteLine(Json.Serialize(new { status = "dry_run_ok", planSha256 = Json.Hash(plan), plan }));
            }
            else
            {
                var receipt = await InfoPublisher.ExecuteAsync(Json.Read<UpdateInfoPlan>(planPath), request,
                    flags["--confirm"], client, StateDirectory(), ct);
                Console.WriteLine(Json.Serialize(new { status = receipt.Status, receipt }));
            }
        }
        else
        {
            var input = Guard.Validate(Json.Read<UpdateFileInput>(requestPath), command,
                Path.GetDirectoryName(requestPath)!, credentials.ApiKey);
            var request = ModRegistry.Bind(input, modConfigPath);
            if (dryRun)
            {
                var plan = await Planner.PrepareAsync(request, client, credentials.ApiKey, ct);
                Json.WriteNew(planPath, plan);
                Console.WriteLine(Json.Serialize(new { status = "dry_run_ok", planSha256 = Json.Hash(plan), plan }));
            }
            else
            {
                var receipt = await Publisher.ExecuteAsync(Json.Read<Plan>(planPath), request, flags["--confirm"], client,
                    credentials.ApiKey, StateDirectory(), ct);
                Console.WriteLine(Json.Serialize(new { status = receipt.Status, receipt }));
            }
        }
        return 0;
    }

    private static bool IsNumeric(string value) => value.Length > 0 && value.All(c => c is >= '0' and <= '9');
}
