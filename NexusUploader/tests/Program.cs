using System.IO.Compression;
using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using NexusUploader;
using App = NexusUploader.Program;

internal static partial class Tests
{
    private const string Key = "test-only-fake-key-never-real-0123456789";
    private static string root = "";
    private static int passed;
    public static async Task<int> Main(string[] args)
    {
        root = Path.Combine(Path.GetTempPath(), "NexusUploader-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            await Run("strict CLI modes and duplicate flags", () =>
            {
                Reject(() => App.Parse(["update-file", "--request", "r"]), "CLI_MODE");
                Reject(() => App.Parse(["inspect", "--mod", "1", "--mod", "2"]), "CLI_DUPLICATE");
                Reject(() => App.Parse(["inspect", "--apikey", Key]), "CLI");
                Reject(() => App.Parse(["inspect", "--mod", "1", "--config", "x"]), "CLI");
                var inspect = App.Parse(["inspect", "--mod", "12345"]);
                Check(!inspect.Flags.ContainsKey("--config"));
                var inspectUrl = App.Parse(["inspect", "--mod", "https://www.nexusmods.com/game/mods/1"]);
                Check(inspectUrl.Flags.ContainsKey("--mod"));
                Reject(() => App.Parse(["target", "add", "--mod-directory", @"E:\Games\Game\FixtureMod", "--mod", "https://www.nexusmods.com/game/mods/1"]), "CLI");
                Check(App.Parse(["target", "candidates"]).Command == "target-candidates");
                Reject(() => App.Parse(["target", "list"]), "CLI");
                Reject(() => App.Parse(["target-add", "--mod-directory", @"E:\Games\Game\FixtureMod", "--mod", "1"]), "CLI");
                Reject(() => App.Parse(["target-candidates"]), "CLI");
                Reject(() => App.Parse(["target-list"]), "CLI");
                Check(App.Parse(["inspect", "--mod-directory", @"E:\Games\Game\FixtureMod"]).Flags.ContainsKey("--mod-directory"));
                Reject(() => App.Parse(["resolve", "--mod-url", "https://www.nexusmods.com/game/mods/1"]), "CLI");
                Reject(() => App.Parse(["target", "show", "--name", "fixture-mod"]), "CLI");
                Reject(() => App.Parse(["create-mod", "--dry-run"]), "CLI");
                Reject(() => App.Parse(["inspect", "--mod-id", "1"]), "CLI");
                Reject(() => App.Parse(["inspect", "--mod-url", "https://www.nexusmods.com/game/mods/1"]), "CLI");
                Reject(() => App.Parse(["inspect", "--mod", "1", "--mod-directory", @"E:\Games\Game\FixtureMod"]), "CLI");
                Reject(() => App.Parse(["inspect"]), "CLI");
                var update = App.Parse(["update-file", "--request", "r", "--dry-run"]);
                Check(!update.Flags.ContainsKey("--config") && !update.Flags.ContainsKey("--plan"));
                var updateInfo = App.Parse(["update-info", "--request", "r", "--dry-run"]);
                Check(updateInfo.Command == "update-info");
                Check(App.Parse(["update-file", "--request", "r", "--dry-run", "--mod", "20000"]).Flags["--mod"] == "20000");
                Reject(() => App.Parse(["update-file", "--request", "r", "--confirm", new string('a', 64), "--mod", "20000"]), "CLI_MODE");
                Reject(() => App.Parse(["update-info", "--request", "r", "--dry-run", "--mod", "20000"]), "CLI");
                foreach (var command in new[] { "update-file", "update-info" })
                {
                    Reject(() => App.Parse([command, "--request", "r", "--dry-run", "--plan", "p"]), "CLI");
                    Reject(() => App.Parse([command, "--request", "r"]), "CLI_MODE");
                    Reject(() => App.Parse([command, "--request", "r", "--execute"]), "CLI");
                    Reject(() => App.Parse([command, "--request", "r", "--execute", "--confirm", new string('a', 64)]), "CLI");
                    Reject(() => App.Parse([command, "--request", "r", "--dry-run", "--confirm", "hash"]), "CLI_MODE");
                    Reject(() => App.Parse([command, "--request", "r", "--confirm"]), "CLI");
                    Reject(() => App.Parse([command, "--request", "r", "--confirm", " "]), "CLI");
                    Reject(() => App.Parse([command, "--request", "r", "--confirm", "hash", "--confirm", "hash"]), "CLI_DUPLICATE");
                    var confirmed = App.Parse([command, "--request", "r", "--confirm", new string('a', 64)]);
                    Check(confirmed.Flags.ContainsKey("--confirm") && !confirmed.Flags.ContainsKey("--dry-run") &&
                        !confirmed.Flags.ContainsKey("--execute"));
                }
                Check(Path.GetFileName(App.DefaultConfigPath()) == "config.json");
                Reject(() => Json.Parse<UpdateFileInput>("{\"modDirectory\":\"x\",\"ModDirectory\":\"y\"}"), "DUPLICATE_JSON");
                try { Json.Parse<UpdateFileInput>("{\"typo\":1}"); throw new Exception(); } catch (JsonException) { }
                return Task.CompletedTask;
            });
            await Run("global ID, relative folder and public secret validation", () =>
            {
                foreach (var value in new[] { "0", "-1", "1/2", "https://www.nexusmods.com/game/mods/1", " 1", "1?x" })
                    Reject(() => Guard.Id(value), "ID");
                var (request, _) = Setup();
                var input = new UpdateFileInput { RequestId = request.RequestId.ToUpperInvariant(),
                    ModDirectory = Path.GetFileName(request.ModDirectory), Description = request.Description, Changelog = request.Changelog };
                var normalized = Guard.Validate(input, root, Key);
                Check(normalized.ModDirectory == request.ModDirectory && normalized.RequestId == request.RequestId);
                Reject(() => Guard.Validate(input with { ModDirectory = Path.Combine(root, "missing") }, root, Key), "MOD_DIRECTORY");
                Reject(() => Guard.PublicText(Key, Key), "SECRET_IN_INPUT");
                Reject(() => NexusApi.StorageUrl("https://localhost/file?signature=secret"), "STORAGE_HOST");
                Reject(() => NexusApi.StorageUrl("https://bucket.s3.amazonaws.com.evil.test/a"), "STORAGE_HOST");
                Reject(() => NexusApi.StorageUrl("https://bucket.s3.amazonaws.com:8443/a"), "STORAGE_HOST");
                return Task.CompletedTask;
            });
            await Run("official mod URLs resolve to global IDs with read-only API calls", async () =>
            {
                var page = Guard.ModUrl("https://www.nexusmods.com/SkyrimSpecialEdition/mods/12604?tab=files#content");
                Check(page.GameDomain == "skyrimspecialedition" && page.GameScopedId == "12604");
                foreach (var value in new[] {
                    "http://www.nexusmods.com/game/mods/1",
                    "https://nexusmods.com.evil.test/game/mods/1",
                    "https://user@nexusmods.com/game/mods/1",
                    "https://nexusmods.com:8443/game/mods/1",
                    "https://nexusmods.com/game/mods/0",
                    "https://nexusmods.com/game/files/1",
                    "https://nexusmods.com/game/mods/%31" })
                    Reject(() => Guard.ModUrl(value), "MOD_URL");
                var mock = new ApiMock();
                using var api = new NexusApi(Key, true, mock, new StorageMock());
                var result = await api.ResolveAsync(page, default);
                Check(result.ModId == "12345" && result.GameId == "1704" && result.GameScopedId == "12604");
                Check(mock.Calls.Single() == ("GET", "/v3/games/skyrimspecialedition/mods/12604"));
            });
            await Run("unregistered private empty team mods are returned as candidates", async () =>
            {
                var mock = new ApiMock();
                using var api = new NexusApi(Key, true, mock, new StorageMock());
                var candidates = await api.CandidatesAsync(new HashSet<string>(StringComparer.Ordinal) { "30000" }, default);
                Check(candidates.Length == 1);
                var candidate = candidates[0];
                Check(candidate.ModId == "20000" && candidate.GameDomain == "skyrimspecialedition" &&
                    candidate.GameScopedId == "200" && candidate.Status == "not_published" &&
                    candidate.Affiliation == "owner" && candidate.FileCount == 0);
                Check((await api.CandidateAsync("20000", new HashSet<string>(StringComparer.Ordinal), default)).Resolution.GameId == "1704");
                await RejectAsync(() => api.CandidateAsync("12345", new HashSet<string>(StringComparer.Ordinal), default),
                    "TARGET_NOT_CANDIDATE");
                Check(mock.Calls.All(x => x.Method == "GET"));
            });
            await Run("first-upload dry-run proposes candidate or URL bindings without saving the registry", async () =>
            {
                foreach (var mod in new[] { "20000", "https://www.nexusmods.com/skyrimspecialedition/mods/12604?tab=files#content" })
                {
                    var (request, mock) = Setup(); var input = FileInput(request);
                    var registry = Path.Combine(State(), "modconfig.json");
                    using var api = new NexusApi(Key, true, mock, new StorageMock());
                    var plan = await FileWorkflow.PrepareAsync(input, mod, registry, api, Key, default);
                    var proposed = plan.Registration!;
                    Check(proposed.Source == (mod == "20000" ? "candidate" : "url") &&
                        proposed.Target.ModId == plan.Request.ModId && proposed.Target.DirectoryKey == request.DirectoryKey);
                    Check(plan.Intent.Action == (mod == "20000" ? "create" : "update") && !File.Exists(registry) &&
                        !File.Exists(registry + ".lock") && mock.Calls.All(x => x.Method == "GET"));
                    Check(!Json.Serialize(plan).Contains("tab=files"));
                    var state = State(); var hash = PlanStore.Save(state, plan);
                    var stored = PlanStore.Read<Plan>(state, input.RequestId, hash);
                    Check(stored.Registration == proposed && FileWorkflow.BindForConfirmation(input, stored, registry).ModId == proposed.Target.ModId);
                    var altered = plan with { Registration = proposed with { Source = proposed.Source == "url" ? "candidate" : "url" } };
                    Reject(() => Planner.CheckPlan(altered, plan.Request, Json.Hash(altered)), "PLAN_CONFIRMATION");
                }
            });
            await Run("confirmed first upload saves its verified binding before remote writes and later reuses it", async () =>
            {
                foreach (var candidate in new[] { false, true })
                {
                    var (request, mock) = Setup(); var input = FileInput(request);
                    mock.FixtureIsCandidate = candidate; mock.Empty = candidate;
                    var registry = Path.Combine(State(), "modconfig.json"); var state = State();
                    var mod = candidate ? "12345" : "https://www.nexusmods.com/skyrimspecialedition/mods/12604";
                    using var readOnly = new NexusApi(Key, true, mock, new StorageMock());
                    var plan = await FileWorkflow.PrepareAsync(input, mod, registry, readOnly, Key, default);
                    var hash = PlanStore.Save(state, plan);
                    mock.BeforeUpload = () => Check(File.Exists(registry) &&
                        ModRegistry.Find(registry, input.ModDirectory).ModId == "12345");
                    using var api = new NexusApi(Key, false, mock, new StorageMock());
                    var receipt = await FileWorkflow.ExecuteAsync(input, PlanStore.Read<Plan>(state, input.RequestId, hash),
                        hash, registry, api, Key, state, default);
                    Check(receipt.Status == "success" && receipt.ModId == "12345" &&
                        ModRegistry.Find(registry, input.ModDirectory).PageName == "Fixture Mod");
                    var calls = mock.Calls.Count;
                    await RejectAsync(() => FileWorkflow.PrepareAsync(input, mod, registry, readOnly, Key, default), "TARGET_EXISTS");
                    Check(mock.Calls.Count == calls);
                    File.WriteAllText(Path.Combine(input.ModDirectory, "modinfo.ini"), "name=Test Mod\nversion=3.0");
                    var next = await FileWorkflow.PrepareAsync(input with { RequestId = Guid.NewGuid().ToString("D") },
                        null, registry, readOnly, Key, default);
                    Check(next.Registration is null && next.Request.ModId == "12345" && next.Intent.Action == "update" &&
                        mock.Calls.Skip(calls).All(x => x.Path != "/v3/team-mods" && !x.Path.StartsWith("/v3/games/")));
                }
            });
            await Run("registration conflicts after dry-run never overwrite another binding", async () =>
            {
                foreach (var sameDirectory in new[] { true, false })
                {
                    var (request, mock) = Setup(); var input = FileInput(request);
                    var registry = Path.Combine(State(), "modconfig.json");
                    using var api = new NexusApi(Key, false, mock, new StorageMock());
                    var plan = await FileWorkflow.PrepareAsync(input,
                        "https://www.nexusmods.com/skyrimspecialedition/mods/12604", registry, api, Key, default);
                    var (other, _) = Setup();
                    ModRegistry.Add(registry, sameDirectory ? request.ModDirectory : other.ModDirectory,
                        new("skyrimspecialedition", "12604", "12345", "1704", "Other binding"));
                    var snapshot = File.ReadAllText(registry);
                    await RejectAsync(() => FileWorkflow.ExecuteAsync(input, plan, Json.Hash(plan), registry, api, Key, State(), default), "TARGET_EXISTS");
                    Check(File.ReadAllText(registry) == snapshot && mock.Calls.All(x => x.Method == "GET"));
                }
            });
            await Run("first-binding confirmation rechecks content eligibility identity expiration and hash", async () =>
            {
                foreach (var change in new[] { "content", "page", "candidate", "expired", "hash" })
                {
                    var (request, mock) = Setup(); var input = FileInput(request);
                    mock.FixtureIsCandidate = change == "candidate"; mock.Empty = mock.FixtureIsCandidate;
                    var registry = Path.Combine(State(), "modconfig.json"); var state = State();
                    using var api = new NexusApi(Key, false, mock, new StorageMock());
                    var plan = await FileWorkflow.PrepareAsync(input, mock.FixtureIsCandidate ? "12345" :
                        "https://www.nexusmods.com/skyrimspecialedition/mods/12604", registry, api, Key, default);
                    switch (change)
                    {
                        case "content": File.WriteAllText(Path.Combine(input.ModDirectory, "mod.lua"), "changed source"); break;
                        case "page": mock.ResolvedPageName = "Changed page"; break;
                        case "candidate": mock.CandidateStatus = "published"; break;
                        case "expired": plan = plan with { CreatedAt = DateTimeOffset.UtcNow.AddMinutes(-20), ExpiresAt = DateTimeOffset.UtcNow.AddMinutes(-5) }; break;
                    }
                    var code = change switch { "content" => "PACKAGE_CHANGED", "page" => "BINDING_CHANGED",
                        "candidate" => "TARGET_NOT_CANDIDATE", "expired" => "PLAN_EXPIRED", _ => "PLAN_CONFIRMATION" };
                    await RejectAsync(() => FileWorkflow.ExecuteAsync(input, plan, change == "hash" ? new string('0', 64) : Json.Hash(plan),
                        registry, api, Key, state, default), code);
                    Check(!File.Exists(registry) && !File.Exists(Path.Combine(state, input.RequestId + ".receipt.json")) &&
                        mock.Calls.All(x => x.Method == "GET"));
                }
            });
            await Run("failed first upload retains its binding and receipt and cannot be retried", async () =>
            {
                var (request, mock) = Setup(); var input = FileInput(request); mock.CancelUpload = true;
                var registry = Path.Combine(State(), "modconfig.json"); var state = State();
                using var api = new NexusApi(Key, false, mock, new StorageMock());
                var plan = await FileWorkflow.PrepareAsync(input,
                    "https://www.nexusmods.com/skyrimspecialedition/mods/12604", registry, api, Key, default);
                await RejectAsync(() => FileWorkflow.ExecuteAsync(input, plan, Json.Hash(plan), registry, api, Key, state, default), "PARTIAL_OR_UNCERTAIN");
                Check(ModRegistry.Find(registry, input.ModDirectory).ModId == "12345" &&
                    Json.Read<Receipt>(Path.Combine(state, input.RequestId + ".receipt.json")).Status == "uncertain");
                var calls = mock.Calls.Count;
                await RejectAsync(() => FileWorkflow.ExecuteAsync(input, plan, Json.Hash(plan), registry, api, Key, state, default), "ALREADY_ATTEMPTED");
                Check(mock.Calls.Count == calls);
            });
            await Run("directory identity uses only the final three segments", () =>
            {
                var parent = State();
                var first = Path.Combine(parent, "old-root", "Game", "Project", "FixtureMod");
                var second = Path.Combine(parent, "new-root", "Game", "Project", "FixtureMod");
                Directory.CreateDirectory(first); Directory.CreateDirectory(second);
                File.WriteAllText(Path.Combine(first, "modinfo.ini"), "name=Fixture Mod\nversion=1.0");
                File.WriteAllText(Path.Combine(second, "modinfo.ini"), "name=Fixture Mod\nversion=1.0");
                var firstIdentity = ModDirectoryIdentity.Read(first);
                var secondIdentity = ModDirectoryIdentity.Read(second);
                Check(firstIdentity.DirectoryKey == "game/project/fixturemod" &&
                    firstIdentity.DirectoryKey == secondIdentity.DirectoryKey);
                return Task.CompletedTask;
            });
            await Run("central modconfig stores and binds verified publishing targets", async () =>
            {
                var directory = State();
                var configPath = Path.Combine(directory, "config.json");
                var registryPath = ModRegistry.PathForConfig(configPath);
                Check(registryPath == Path.Combine(directory, "modconfig.json"));
                var (baseRequest, mock) = Setup();
                var resolution = new ModResolution("skyrimspecialedition", "12604", "12345", "1704", "Fixture Page");
                var added = ModRegistry.Add(registryPath, baseRequest.ModDirectory, resolution);
                Check(added.ModId == "12345" && File.Exists(registryPath));
                var stored = ModRegistry.Find(registryPath, baseRequest.ModDirectory);
                Check(stored.GameScopedId == "12604" && stored.GameDomain == "skyrimspecialedition");
                var raw = new UpdateFileInput { RequestId = baseRequest.RequestId,
                    ModDirectory = baseRequest.ModDirectory, Description = baseRequest.Description, Changelog = baseRequest.Changelog };
                var bound = ModRegistry.Bind(raw, registryPath);
                Check(bound.DirectoryKey == stored.DirectoryKey && bound.ModId == "12345" && bound.Operation == "update-file");
                var info = new UpdateInfoInput { RequestId = Guid.NewGuid().ToString(),
                    ModDirectory = baseRequest.ModDirectory, Description = "[b]Page description[/b]" };
                var boundInfo = ModRegistry.Bind(info, registryPath);
                Check(boundInfo.DirectoryKey == stored.DirectoryKey && boundInfo.ModId == "12345" && boundInfo.Operation == "update-info");
                Reject(() => ModRegistry.Add(registryPath, baseRequest.ModDirectory, resolution), "TARGET_EXISTS");
                using var api = new NexusApi(Key, true, mock, new StorageMock());
                var plan = await Planner.PrepareAsync(bound, api, Key, default);
                var changed = new ModConfig { Targets = [stored with { ModId = "54321", VerifiedAt = DateTimeOffset.UtcNow }] };
                File.WriteAllText(registryPath, Json.Serialize(changed));
                var rebound = ModRegistry.Bind(raw, registryPath);
                Reject(() => Planner.CheckPlan(plan, rebound, Json.Hash(plan)), "PLAN_MISMATCH");
                Check(!File.ReadAllText(registryPath).Contains(Key) && !File.ReadAllText(registryPath).Contains("tab=files"));
            });
            await Run("directory inspection returns only its binding and read-only history", async () =>
            {
                var (request, mock) = Setup();
                var registryPath = Path.Combine(State(), "modconfig.json");
                var binding = ModRegistry.Add(registryPath, request.ModDirectory,
                    new("skyrimspecialedition", "12604", "12345", "1704", "Fixture Page"));
                using var api = new NexusApi(Key, true, mock, new StorageMock());
                var flags = App.Parse(["inspect", "--mod-directory", request.ModDirectory]).Flags;
                var result = await App.InspectAsync(flags, registryPath, api, Key, default);
                Check(result.Status == "inspected" && result.Binding == binding && result.Resolution is null &&
                    result.Target.ModId == binding.ModId && result.Target.Files.Length == 1);
                using var doc = JsonDocument.Parse(Json.Serialize(result));
                Check(doc.RootElement.GetProperty("binding").GetProperty("pageName").GetString() == "Fixture Page");
                var byId = await App.InspectAsync(App.Parse(["inspect", "--mod", "12345"]).Flags,
                    registryPath, api, Key, default);
                Check(byId.Binding is null && byId.Resolution is null && byId.Target.ModId == "12345");
                var byUrl = await App.InspectAsync(App.Parse(["inspect", "--mod", "https://www.nexusmods.com/skyrimspecialedition/mods/12604"]).Flags,
                    registryPath, api, Key, default);
                Check(byUrl.Binding is null && byUrl.Resolution?.PageName == "Fixture Mod" && byUrl.Target.ModId == "12345");
                Check(mock.Calls.All(x => x.Method == "GET"));
            });
            await Run("sensitive paths are rejected before reading and keys across buffers are rejected", async () =>
            {
                var (request, _) = Setup();
                var forbidden = Path.Combine(request.ModDirectory, "UPDATER.JSON");
                using (var locked = new FileStream(forbidden, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                    await RejectAsync(() => Packaging.BuildAsync(request.ModDirectory, Key, default), "PACKAGE_SECRET");
                File.Delete(forbidden);
                foreach (var encoding in new[] { Encoding.UTF8, Encoding.Unicode, Encoding.BigEndianUnicode })
                {
                    File.WriteAllBytes(Path.Combine(request.ModDirectory, "mod.lua"), encoding.GetBytes(new string('x', 128 * 1024 - 10) + Key));
                    await RejectAsync(() => Packaging.BuildAsync(request.ModDirectory, Key, default), "PACKAGE_SECRET");
                }
                foreach (var name in new[] { "config.json", "modconfig.json", "credentials.json", "nested/.env", "nested/storage-state.json", "nested/token.secret.json", "private.pem", "private.key" })
                    Reject(() => Packaging.CheckName(name), "PACKAGE_SECRET");
                Reject(() => Packaging.CheckName("../mod.lua"), "PACKAGE_PATH");
                Reject(() => Packaging.CheckName(".GIT/config"), "PACKAGE_PATH");
            });
            await Run("deterministic ZIP layout, metadata, file name and temporary cleanup", async () =>
            {
                var (request, _) = Setup();
                Directory.CreateDirectory(Path.Combine(request.ModDirectory, "empty"));
                Directory.CreateDirectory(Path.Combine(request.ModDirectory, "nested"));
                File.WriteAllText(Path.Combine(request.ModDirectory, "nested", "data.txt"), "fixture");
                PackageInfo first;
                string temporary;
                var rootName = Path.GetFileName(request.ModDirectory);
                using (var package = await Packaging.BuildAsync(request.ModDirectory, Key, default))
                {
                    first = package.Info; temporary = package.Stream.Name;
                    Check(first.Name == "Test Mod" && first.Version == "2.0" && first.ArchiveName == "Test_Mod-2.0.zip" &&
                        first.FileCount == 3 && first.RootDirectory == rootName);
                    using var zip = new ZipArchive(package.Stream, ZipArchiveMode.Read, leaveOpen: true);
                    Check(zip.Entries.Select(e => e.FullName).SequenceEqual(new[] { rootName + "/", rootName + "/empty/",
                        rootName + "/mod.lua", rootName + "/modinfo.ini", rootName + "/nested/", rootName + "/nested/data.txt" }));
                    using var reader = new StreamReader(zip.GetEntry(rootName + "/modinfo.ini")!.Open());
                    Check(reader.ReadToEnd().Contains("version=2.0"));
                }
                Check(!File.Exists(temporary));
                using (var flat = await Packaging.BuildAsync(request.ModDirectory, Key, includeRootDirectory: false, default))
                {
                    Check(flat.Info.RootDirectory is null && flat.Info.Sha256 != first.Sha256);
                    using var zip = new ZipArchive(flat.Stream, ZipArchiveMode.Read, leaveOpen: true);
                    Check(zip.Entries.Select(e => e.FullName).SequenceEqual(new[] { "empty/", "mod.lua", "modinfo.ini", "nested/", "nested/data.txt" }));
                }
                File.SetLastWriteTimeUtc(Path.Combine(request.ModDirectory, "mod.lua"), DateTime.UtcNow.AddDays(-10));
                using var second = await Packaging.BuildAsync(request.ModDirectory, Key, default);
                Check(Json.Hash(first) == Json.Hash(second.Info));
            });
            await Run("INI BOM, section and strict metadata validation", async () =>
            {
                var (request, _) = Setup(); var ini = Path.Combine(request.ModDirectory, "modinfo.ini");
                File.WriteAllText(ini, "; comment\n[ModInfo]\nNAME = Test Mod\nVERSION = 2.0\n[Other]\nversion=unused", new UTF8Encoding(true));
                using (var package = await Packaging.BuildAsync(request.ModDirectory, Key, default)) Check(package.Info.Version == "2.0");
                foreach (var (content, code) in new[] {
                    ("name=Test Mod\nversion=2\nVERSION=3", "MODINFO_DUPLICATE"),
                    ("name=Test Mod", "MODINFO_INVALID"), ("name=Test Mod\nversion=2/3", "VERSION"),
                    ("name=Test Mod\nversion=", "VERSION"), ("name=Bad/Name\nversion=2", "FILE_NAME"),
                    ("name=Test Mod\nversion=" + new string('1', 51), "VERSION") })
                {
                    File.WriteAllText(ini, content);
                    await RejectAsync(() => Packaging.BuildAsync(request.ModDirectory, Key, default), code);
                }
                File.WriteAllBytes(ini, [0xFF, 0xFF]);
                try { using var package = await Packaging.BuildAsync(request.ModDirectory, Key, default); throw new Exception(); } catch (DecoderFallbackException) { }
                File.Delete(ini);
                await RejectAsync(() => Packaging.BuildAsync(request.ModDirectory, Key, default), "MODINFO_MISSING");
            });
            await Run("dry-run uses only GET and preserves remote state", async () =>
            {
                var (request, mock) = Setup();
                using var api = new NexusApi(Key, true, mock, new StorageMock());
                var plan = await Planner.PrepareAsync(request, api, Key, default);
                Check(plan.Intent.Action == "update" && plan.Intent.FileId == "100" && plan.Target.ModId == "12345");
                Check(mock.Calls.All(c => c.Method == "GET") && !mock.Published);
                Check(mock.Calls.Select(c => c.Path).SequenceEqual(new[] { "/v3/mods/12345/files", "/v3/mod-files/100/versions" }));
                await RejectAsync(() => api.SendAsync(HttpMethod.Post, "uploads/multipart", new { }, 201, default), "DRY_RUN_WRITE");
                Check(!Json.Serialize(plan).Contains(Key) && !Json.Serialize(plan).Contains("schemaVersion"));
            });
            await Run("automatic main selection and duplicate history guards", async () =>
            {
                var (request, mock) = Setup();
                using var api = new NexusApi(Key, true, mock, new StorageMock());
                var plan = await Planner.PrepareAsync(request, api, Key, default);
                Reject(() => Planner.Resolve(request with { ModId = "42" }, plan.Target, plan.Package), "TARGET_MISMATCH");
                Reject(() => Planner.Resolve(request, plan.Target, plan.Package with { Version = "1.0" }), "DUPLICATE_VERSION");
                var duplicateMain = plan.Target with { Files = [plan.Target.Files[0], plan.Target.Files[0] with { Id = "200", Versions = [plan.Target.Files[0].Versions[0] with { IsPrimary = false }] }] };
                Reject(() => Planner.Resolve(request, duplicateMain, plan.Package), "MAIN_AMBIGUOUS");
                var optional = plan.Target.Files[0] with { Id = "200", Versions = [new("201", "Optional", "0.8", "optional", true)] };
                var main = plan.Target.Files[0] with { Name = "Existing Main", Versions = [plan.Target.Files[0].Versions[0] with { IsPrimary = false }] };
                var intent = Planner.Resolve(request, plan.Target with { Files = [main, optional] }, plan.Package);
                Check(intent.Action == "update" && intent.FileId == "100" && intent.Name == "Existing Main");
                Check(Planner.Resolve(request, plan.Target with { Files = [optional] }, plan.Package).Action == "create");
                Check(Planner.Resolve(request, plan.Target with { Files = [main with { IsActive = false }, optional] }, plan.Package).Action == "create");
                var old = optional with { IsActive = false, Versions = [new("202", "Old", "2.0", "old_version", false)] };
                Reject(() => Planner.Resolve(request, plan.Target with { Files = [main, old] }, plan.Package), "DUPLICATE_VERSION");
            });
            await DescriptionTests();
            await Run("description-only dry-run is read-only and strictly validated", async () =>
            {
                var (request, mock) = SetupInfo();
                var input = new UpdateInfoInput { RequestId = request.RequestId,
                    ModDirectory = request.ModDirectory, Description = request.Description };
                var normalized = Guard.Validate(input, root, Key);
                Check(normalized.Description == "[b]Full mod page[/b]\nSecond line");
                Reject(() => Guard.Validate(input with { Description = " " }, root, Key), "DESCRIPTION");
                try { Json.Parse<UpdateInfoInput>("{\"summary\":\"not supported\"}"); throw new Exception(); } catch (JsonException) { }
                using var api = new NexusApi(Key, true, mock, new StorageMock());
                var plan = await InfoPlanner.PrepareAsync(request, api, default);
                Check(plan.Request.Description == normalized.Description && plan.Fingerprint == InfoPlanner.Fingerprint(request, plan.PageDescription));
                Check(mock.Calls.All(c => c.Method == "GET") && !mock.InfoUpdated);
                await RejectAsync(() => api.UpdateDescriptionAsync(request.ModId, request.Description!, default), "DRY_RUN_WRITE");
                Reject(() => InfoPlanner.CheckPlan(plan, request with { Description = "changed" }, Json.Hash(plan)), "PLAN_MISMATCH");
            });
            await Run("description-only execute patches no file or changelog state", async () =>
            {
                var (request, mock) = SetupInfo();
                using var api = new NexusApi(Key, false, mock, new StorageMock());
                var plan = await InfoPlanner.PrepareAsync(request, api, default);
                var state = State();
                var confirmation = PlanStore.Save(state, plan);
                var stored = PlanStore.Read<UpdateInfoPlan>(state, request.RequestId, confirmation);
                var receipt = await InfoPublisher.ExecuteAsync(stored, request, confirmation, api, state, default);
                var old = SaveCleanupPlan(state, DateTimeOffset.UtcNow.AddMonths(-2));
                var completed = App.CompletePublication(receipt, state);
                Check(completed.Status == "success" && completed.Receipt == receipt &&
                    completed.Cleanup.DeletedPlans == 1 && !File.Exists(old.Path));
                Check(receipt.Status == "success" && receipt.Stage == "complete" && receipt.ModId == "12345");
                Check(mock.InfoUpdated && mock.PageDescription == request.Description && !mock.Published && mock.ChangelogCalls == 0);
                Check(mock.Calls.Count(c => c.Method == "PATCH") == 1 &&
                    mock.Calls.Single(c => c.Method == "PATCH").Path == "/v3/mods/12345");
                var calls = mock.Calls.Count;
                await RejectAsync(() => InfoPublisher.ExecuteAsync(stored, request, confirmation, api, state, default), "ALREADY_ATTEMPTED");
                Check(mock.Calls.Count == calls);
                Check(File.Exists(PlanStore.PathFor(state, request.RequestId, confirmation)));
            });
            await Run("description update failure records uncertain receipt", async () =>
            {
                var (request, mock) = SetupInfo(); mock.FailInfoUpdate = true;
                using var api = new NexusApi(Key, false, mock, new StorageMock());
                var plan = await InfoPlanner.PrepareAsync(request, api, default); var state = State();
                await RejectAsync(() => InfoPublisher.ExecuteAsync(plan, request, Json.Hash(plan), api, state, default), "PARTIAL_OR_UNCERTAIN");
                var receipt = Json.Read<Receipt>(Path.Combine(state, request.RequestId + ".receipt.json"));
                Check(receipt.Status == "uncertain" && receipt.Stage == "update-description" &&
                    receipt.ErrorCode == "API_HTTP_500" && !Json.Serialize(receipt).Contains(Key));
            });
            await Run("missing Main File automatically selects creation", async () =>
            {
                var (request, mock) = Setup(); mock.Empty = true;
                using var api = new NexusApi(Key, true, mock, new StorageMock());
                var plan = await Planner.PrepareAsync(request, api, Key, default);
                Check(plan.Intent.FileId is null && plan.Intent.Action == "create" && plan.Intent.Name == "Test Mod");
            });
            await Run("full multipart publish and read-back", async () =>
            {
                var (request, mock) = Setup(); var storage = new StorageMock();
                using var api = new NexusApi(Key, false, mock, storage);
                var plan = await Planner.PrepareAsync(request, api, Key, default);
                var state = State();
                var confirmation = PlanStore.Save(state, plan);
                var stored = PlanStore.Read<Plan>(state, request.RequestId, confirmation);
                var receipt = await Publisher.ExecuteAsync(stored, request, confirmation, api, Key, state, default);
                var old = SaveCleanupPlan(state, DateTimeOffset.UtcNow.AddMonths(-2));
                var damaged = SaveCleanupPlan(state, DateTimeOffset.UtcNow.AddMonths(-2));
                File.WriteAllText(damaged.Path, "invalid synthetic plan");
                var completed = App.CompletePublication(receipt, state);
                Check(completed.Status == "success" && completed.Receipt == receipt &&
                    completed.Cleanup.DeletedPlans == 1 && completed.Cleanup.WarningCode == "CLEANUP_INCOMPLETE" &&
                    !File.Exists(old.Path) && File.Exists(damaged.Path));
                Check(receipt.Status == "success" && receipt.PublishedId == "102");
                Check(Json.HashBytes(storage.Bytes.ToArray()) == plan.Package.Sha256);
                Check(mock.UploadFilename == plan.Package.ArchiveName && mock.PublishedVersion == plan.Package.Version && mock.PublishedName == plan.Intent.Name);
                Check(!storage.SawApiKey && mock.ChangelogCalls == 1);
                var calls = mock.Calls.Count;
                await RejectAsync(() => Publisher.ExecuteAsync(stored, request, confirmation, api, Key, state, default), "ALREADY_ATTEMPTED");
                Check(mock.Calls.Count == calls);
                Check(File.Exists(PlanStore.PathFor(state, request.RequestId, confirmation)) &&
                    File.Exists(Path.Combine(state, request.RequestId + ".receipt.json")));
            });
            await Run("automatic plan storage selects the exact request and digest without overwrites", async () =>
            {
                var (request, mock) = Setup();
                using var api = new NexusApi(Key, true, mock, new StorageMock());
                var state = State();
                var first = await Planner.PrepareAsync(request, api, Key, default);
                var firstHash = PlanStore.Save(state, first);
                var secondRequest = request with { Description = "Second review" };
                var second = await Planner.PrepareAsync(secondRequest, api, Key, default);
                var secondHash = PlanStore.Save(state, second);
                Check(firstHash == Json.Hash(first) && secondHash != firstHash);
                Check(PlanStore.PathFor(state, request.RequestId, firstHash) ==
                    Path.Combine(state, "plans", request.RequestId, firstHash + ".plan.json"));
                var selected = PlanStore.Read<Plan>(state, request.RequestId, firstHash);
                Check(Json.Hash(selected) == firstHash && selected.Request.Description == request.Description);
                Reject(() => PlanStore.Save(state, first), "PLAN_EXISTS");
                Reject(() => PlanStore.Read<Plan>(state, Guid.NewGuid().ToString("D"), firstHash), "PLAN_NOT_FOUND");
                Reject(() => PlanStore.Read<Plan>(state, request.RequestId, new string('0', 64)), "PLAN_NOT_FOUND");
                Reject(() => Planner.CheckPlan(selected, secondRequest, firstHash), "PLAN_MISMATCH");
                Check(Directory.GetFiles(Path.Combine(state, "plans", request.RequestId)).Length == 2 &&
                    mock.Calls.All(x => x.Method == "GET"));
            });
            await Run("stored plans reject tampering and path-like confirmation values", async () =>
            {
                var (request, mock) = Setup();
                using var api = new NexusApi(Key, true, mock, new StorageMock());
                var state = State();
                var plan = await Planner.PrepareAsync(request, api, Key, default);
                var hash = PlanStore.Save(state, plan);
                File.WriteAllText(PlanStore.PathFor(state, request.RequestId, hash),
                    Json.Serialize(plan with { Request = request with { Description = "Tampered" } }));
                Reject(() => PlanStore.Read<Plan>(state, request.RequestId, hash), "PLAN_CONFIRMATION");
                foreach (var value in new[] { "../escape", "", new string('A', 64), new string('a', 63), new string('a', 65) })
                    Reject(() => PlanStore.Read<Plan>(state, request.RequestId, value), "PLAN_CONFIRMATION");
                Reject(() => PlanStore.Read<Plan>(state, "../escape", hash), "REQUEST_ID");
                Check(mock.Calls.All(x => x.Method == "GET"));
            });
            await Run("expired plans are refused but retained for audit", async () =>
            {
                var (request, mock) = SetupInfo();
                using var api = new NexusApi(Key, true, mock, new StorageMock());
                var state = State();
                var expired = (await InfoPlanner.PrepareAsync(request, api, default)) with
                { CreatedAt = DateTimeOffset.UtcNow.AddMinutes(-20), ExpiresAt = DateTimeOffset.UtcNow.AddMinutes(-5) };
                var hash = PlanStore.Save(state, expired);
                var stored = PlanStore.Read<UpdateInfoPlan>(state, request.RequestId, hash);
                Reject(() => InfoPlanner.CheckPlan(stored, request, hash), "PLAN_EXPIRED");
                Check(File.Exists(PlanStore.PathFor(state, request.RequestId, hash)) &&
                    !File.Exists(Path.Combine(state, request.RequestId + ".receipt.json")) &&
                    mock.Calls.All(x => x.Method == "GET"));
            });
            await Run("cleanup uses a calendar-month expiry boundary and preserves unresolved receipts", () =>
            {
                var state = State();
                var now = new DateTimeOffset(2026, 11, 4, 12, 0, 0, TimeSpan.Zero);
                var cutoff = now.AddMonths(-1);
                var old = SaveCleanupPlan(state, cutoff.AddTicks(-1));
                var successful = SaveCleanupPlan(state, cutoff.AddDays(-1));
                var boundary = SaveCleanupPlan(state, cutoff);
                var recent = SaveCleanupPlan(state, cutoff.AddTicks(1));
                var valid = SaveCleanupPlan(state, now.AddMinutes(15));
                var partial = SaveCleanupPlan(state, cutoff.AddDays(-1));
                var uncertain = SaveCleanupPlan(state, cutoff.AddDays(-1));
                foreach (var item in new[] { (successful.Plan, "success", "complete"),
                    (partial.Plan, "partial", "changelog"), (uncertain.Plan, "uncertain", "publish-file") })
                    File.WriteAllText(Path.Combine(state, item.Plan.Request.RequestId + ".receipt.json"),
                        Json.Serialize(new Receipt(item.Plan.Request.RequestId, item.Item2, item.Item3, ModId: item.Plan.Request.ModId)));
                var notes = Path.Combine(Path.GetDirectoryName(boundary.Path)!, "notes.plan.json");
                File.WriteAllText(notes, "not a tool-generated plan");
                var config = Path.Combine(state, "config.json");
                var sentinelLock = Path.Combine(state, "keep.lock");
                var legacyPlan = Path.Combine(state, "old.plan.json");
                File.WriteAllText(config, "synthetic configuration must not be opened by cleanup");
                File.WriteAllText(sentinelLock, "keep"); File.WriteAllText(legacyPlan, "keep");
                var report = PlanCleanup.Run(state, now);
                Check(report.DeletedPlans == 2 && report.DeletedDirectories == 2 && report.RetainedPlans == 5 &&
                    report.FailedItems == 0 && report.WarningCode is null);
                Check(!File.Exists(old.Path) && !Directory.Exists(Path.GetDirectoryName(old.Path)) &&
                    !File.Exists(successful.Path) && !Directory.Exists(Path.GetDirectoryName(successful.Path)));
                Check(new[] { boundary.Path, recent.Path, valid.Path, partial.Path, uncertain.Path, notes, config,
                    sentinelLock, legacyPlan }.All(File.Exists));
                foreach (var plan in new[] { successful.Plan, partial.Plan, uncertain.Plan })
                    Check(File.Exists(Path.Combine(state, plan.Request.RequestId + ".receipt.json")));
                return Task.CompletedTask;
            });
            await Run("cleanup keeps damaged plans and inconsistent or pending receipts", () =>
            {
                var state = State(); var now = DateTimeOffset.UtcNow; var expiry = now.AddMonths(-2);
                var damaged = SaveCleanupPlan(state, expiry);
                File.WriteAllText(damaged.Path, "{invalid synthetic " + Key);
                var tampered = SaveCleanupPlan(state, expiry);
                File.WriteAllText(tampered.Path, Json.Serialize(tampered.Plan with
                    { Request = tampered.Plan.Request with { Description = "changed" } }));
                var badReceipt = SaveCleanupPlan(state, expiry);
                File.WriteAllText(Path.Combine(state, badReceipt.Plan.Request.RequestId + ".receipt.json"), "invalid " + Key);
                var mismatched = SaveCleanupPlan(state, expiry);
                File.WriteAllText(Path.Combine(state, mismatched.Plan.Request.RequestId + ".receipt.json"),
                    Json.Serialize(new Receipt(mismatched.Plan.Request.RequestId, "success", "complete", ModId: "99999")));
                var pending = SaveCleanupPlan(state, expiry);
                File.WriteAllText(Path.Combine(state, pending.Plan.Request.RequestId + ".receipt.json.tmp"), "pending");
                var report = PlanCleanup.Run(state, now);
                Check(report.DeletedPlans == 0 && report.DeletedDirectories == 0 && report.RetainedPlans == 5 &&
                    report.FailedItems == 3 && report.WarningCode == "CLEANUP_INCOMPLETE");
                Check(new[] { damaged.Path, tampered.Path, badReceipt.Path, mismatched.Path, pending.Path }.All(File.Exists));
                Check(!Json.Serialize(report).Contains(Key));
                return Task.CompletedTask;
            });
            await Run("cleanup respects publishing locks and never changes an unsuccessful result", () =>
            {
                var state = State(); var now = DateTimeOffset.UtcNow;
                var old = SaveCleanupPlan(state, now.AddMonths(-2));
                using (var journal = new Journal(state, old.Plan.Request))
                {
                    var report = PlanCleanup.Run(state, now);
                    Check(report.DeletedPlans == 0 && report.WarningCode == "CLEANUP_INCOMPLETE" && File.Exists(old.Path));
                }
                var receipt = new Receipt(old.Plan.Request.RequestId, "partial", "changelog", ModId: old.Plan.Request.ModId);
                var completed = App.CompletePublication(receipt, state);
                Check(completed.Status == "partial" && completed.Receipt == receipt && completed.Cleanup.DeletedPlans == 0 && File.Exists(old.Path));
                Check(PlanCleanup.Run(state, now).DeletedPlans == 1 && !File.Exists(old.Path));
                return Task.CompletedTask;
            });
            await Run("flat ZIP option survives dry-run and execute", async () =>
            {
                var (request, mock) = Setup();
                request = request with { IncludeRootDirectory = false };
                var storage = new StorageMock();
                using var api = new NexusApi(Key, false, mock, storage);
                var plan = await Planner.PrepareAsync(request, api, Key, default);
                Check(!plan.Request.IncludeRootDirectory && plan.Package.RootDirectory is null);
                var receipt = await Publisher.ExecuteAsync(plan, request, Json.Hash(plan), api, Key, State(), default);
                Check(receipt.Status == "success");
                using var zip = new ZipArchive(new MemoryStream(storage.Bytes.ToArray()), ZipArchiveMode.Read);
                Check(zip.GetEntry("modinfo.ini") is not null && zip.Entries.All(e => !e.FullName.Contains('/')));
            });
            await Run("first Main File publish path", async () =>
            {
                var (request, mock) = Setup(); mock.Empty = true;
                using var api = new NexusApi(Key, false, mock, new StorageMock());
                var plan = await Planner.PrepareAsync(request, api, Key, default);
                var receipt = await Publisher.ExecuteAsync(plan, request, Json.Hash(plan), api, Key, State(), default);
                Check(receipt.Status == "success" && receipt.PublishedId == "102" && receipt.TargetFileId == "100");
                Check(receipt.ResponseFileId == "102" && mock.ChangelogCalls == 1);
            });
            await Run("different write-response file ID is reconciled by the returned version ID", async () =>
            {
                var (request, mock) = Setup(); mock.ResponseFileId = "900";
                using var api = new NexusApi(Key, false, mock, new StorageMock());
                var plan = await Planner.PrepareAsync(request, api, Key, default); var state = State();
                var receipt = await Publisher.ExecuteAsync(plan, request, Json.Hash(plan), api, Key, state, default);
                Check(receipt.Status == "success" && receipt.PublishedId == "102" && receipt.ResponseFileId == "900");
                Check(receipt.TargetFileId == "100" && receipt.ModId == "12345" && mock.ChangelogCalls == 1);
                Check(Json.Hash(receipt) == Json.Hash(Json.Read<Receipt>(Path.Combine(state, request.RequestId + ".receipt.json"))));
                var publishIndex = mock.Calls.FindIndex(c => c == ("POST", "/v3/mod-files/100/versions"));
                var verifyIndex = mock.Calls.FindIndex(publishIndex + 1, c => c == ("GET", "/v3/mod-files/100/versions"));
                var changelogIndex = mock.Calls.FindIndex(c => c == ("POST", "/v3/mods/12345/changelogs"));
                Check(publishIndex >= 0 && verifyIndex > publishIndex && changelogIndex > verifyIndex);
            });
            await Run("matching response file ID cannot hide a version attached to the wrong group", async () =>
            {
                var (request, mock) = Setup(); mock.PublishedOwnerFileId = "200";
                using var api = new NexusApi(Key, false, mock, new StorageMock());
                var plan = await Planner.PrepareAsync(request, api, Key, default); var state = State();
                await RejectAsync(() => Publisher.ExecuteAsync(plan, request, Json.Hash(plan), api, Key, state, default), "PARTIAL_OR_UNCERTAIN");
                var receipt = Json.Read<Receipt>(Path.Combine(state, request.RequestId + ".receipt.json"));
                Check(receipt.Status == "partial" && receipt.Stage == "verify-file" && receipt.ErrorCode == "PUBLISH_MISMATCH");
                Check(receipt.PublishedId == "102" && receipt.TargetFileId == "100" && receipt.ResponseFileId == "100" && mock.ChangelogCalls == 0);
            });
            await Run("failed read-back preserves response IDs and prevents changelog writes", async () =>
            {
                var (request, mock) = Setup(); mock.FailReadback = true; mock.ResponseFileId = "900";
                using var api = new NexusApi(Key, false, mock, new StorageMock());
                var plan = await Planner.PrepareAsync(request, api, Key, default); var state = State();
                await RejectAsync(() => Publisher.ExecuteAsync(plan, request, Json.Hash(plan), api, Key, state, default), "PARTIAL_OR_UNCERTAIN");
                var receipt = Json.Read<Receipt>(Path.Combine(state, request.RequestId + ".receipt.json"));
                Check(receipt.Status == "partial" && receipt.Stage == "verify-file" && receipt.ErrorCode == "API_HTTP_503");
                Check(receipt.PublishedId == "102" && receipt.ResponseFileId == "900" && receipt.TargetFileId == "100" && mock.ChangelogCalls == 0);
                Check(!Json.Serialize(receipt).Contains(Key));
            });
            await Run("read-back requires exact version identity, metadata and active primary state", async () =>
            {
                var (request, mock) = Setup();
                using var api = new NexusApi(Key, true, mock, new StorageMock());
                var plan = await Planner.PrepareAsync(request, api, Key, default);
                var version = new FileVersion("102", plan.Intent.Name, plan.Package.Version, "main", true);
                var file = new RemoteFile("100", plan.Intent.Name, true, [version]);
                var published = new PublishResult("102", "900");
                foreach (var invalid in new[] {
                    version with { Id = "103" }, version with { Name = "Different" }, version with { Version = "3.0" },
                    version with { Category = "optional" }, version with { IsPrimary = false } })
                    Reject(() => Publisher.VerifyPublished([file with { Versions = [invalid] }], plan.Package, plan.Intent, published), "VERIFY_FAILED");
                Reject(() => Publisher.VerifyPublished([file with { IsActive = false }], plan.Package, plan.Intent, published), "VERIFY_FAILED");
                Reject(() => Publisher.VerifyPublished([file with { Versions = [version, version] }], plan.Package, plan.Intent, published), "VERIFY_FAILED");
                var create = plan.Intent with { Action = "create", FileId = null };
                Check(Publisher.VerifyPublished([file], plan.Package, create, new("102", "102")) == new VerifiedPublish("102", "100"));
                Check(Publisher.VerifyPublished([file], plan.Package, create, new("100", "100")) == new VerifiedPublish("102", "100"));
                Reject(() => Publisher.VerifyPublished([file], plan.Package, create, new("900", "900")), "VERIFY_FAILED");
            });
            await Run("modified plan and expiration rejected", async () =>
            {
                var (request, mock) = Setup();
                using var api = new NexusApi(Key, true, mock, new StorageMock());
                var plan = await Planner.PrepareAsync(request, api, Key, default);
                Reject(() => Planner.CheckPlan(plan, request, "wrong"), "PLAN_CONFIRMATION");
                Reject(() => Planner.CheckPlan(plan with { ExpiresAt = DateTimeOffset.UtcNow.AddMinutes(-1) }, request, Json.Hash(plan)), "PLAN_EXPIRED");
                Reject(() => Planner.CheckPlan(plan, request with { Description = "changed" }, Json.Hash(plan)), "PLAN_MISMATCH");
            });
            await Run("folder edits, additions, deletions and INI version changes invalidate the plan", async () =>
            {
                foreach (var mutation in new[] { "edit", "add", "delete", "version", "rename" })
                {
                    var (request, mock) = Setup();
                    using var api = new NexusApi(Key, false, mock, new StorageMock());
                    var plan = await Planner.PrepareAsync(request, api, Key, default);
                    var source = Path.Combine(request.ModDirectory, "mod.lua");
                    switch (mutation)
                    {
                        case "edit": File.WriteAllText(source, "-- edited fixture"); break;
                        case "add": File.WriteAllText(Path.Combine(request.ModDirectory, "new.lua"), "-- new fixture"); break;
                        case "delete": File.Delete(source); break;
                        case "version": File.WriteAllText(Path.Combine(request.ModDirectory, "modinfo.ini"), "name=Test Mod\nversion=2.1"); break;
                        case "rename": File.Move(source, Path.Combine(request.ModDirectory, "renamed.lua")); break;
                    }
                    var state = State();
                    await RejectAsync(() => Publisher.ExecuteAsync(plan, request, Json.Hash(plan), api, Key, state, default), "PACKAGE_CHANGED");
                    Check(mock.Calls.All(c => c.Method == "GET") && !File.Exists(Path.Combine(state, request.RequestId + ".receipt.json")));
                }
            });
            await Run("remote create/update selection cannot switch after dry-run", async () =>
            {
                foreach (var empty in new[] { false, true })
                {
                    var (request, mock) = Setup(); mock.Empty = empty;
                    using var api = new NexusApi(Key, false, mock, new StorageMock());
                    var plan = await Planner.PrepareAsync(request, api, Key, default);
                    mock.Empty = !empty;
                    await RejectAsync(() => Publisher.ExecuteAsync(plan, request, Json.Hash(plan), api, Key, State(), default), "REMOTE_CHANGED");
                    Check(mock.Calls.All(c => c.Method == "GET"));
                }
            });
            await Run("omitted release text and INI-derived version reach the expected API fields", async () =>
            {
                var (request, mock) = Setup(); request = request with { Description = null, Changelog = null };
                File.WriteAllText(Path.Combine(request.ModDirectory, "modinfo.ini"), "name=Local Name\nversion=3.1-rc2");
                using var api = new NexusApi(Key, false, mock, new StorageMock());
                var plan = await Planner.PrepareAsync(request, api, Key, default);
                Check(plan.Intent.Name == "Test Mod" && plan.Package.ArchiveName == "Local_Name-3.1-rc2.zip");
                var receipt = await Publisher.ExecuteAsync(plan, request, Json.Hash(plan), api, Key, State(), default);
                Check(receipt.Status == "success" && mock.ChangelogCalls == 0 && mock.PublishedVersion == "3.1-rc2" && mock.Description is null);
            });
            await Run("directory junctions cannot escape the package or plan store", async () =>
            {
                if (!OperatingSystem.IsWindows()) return;
                var (request, _) = Setup(); var external = State();
                File.WriteAllText(Path.Combine(external, "fixture.txt"), "outside source");
                var junction = Path.Combine(request.ModDirectory, "linked");
                var info = new ProcessStartInfo("cmd.exe") { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
                info.Arguments = $"/d /c mklink /J \"{junction}\" \"{external}\"";
                using var process = Process.Start(info) ?? throw new Exception();
                process.OutputDataReceived += (_, _) => { }; process.ErrorDataReceived += (_, _) => { };
                process.BeginOutputReadLine(); process.BeginErrorReadLine();
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
                try
                {
                    await process.WaitForExitAsync(timeout.Token); Check(process.ExitCode == 0);
                    await RejectAsync(() => Packaging.BuildAsync(request.ModDirectory, Key, default), "PACKAGE_LINK");
                    await RejectAsync(() => Packaging.BuildAsync(junction, Key, default), "PACKAGE_LINK");
                    Reject(() => PlanStore.Read<Plan>(junction, request.RequestId, new string('a', 64)), "PLAN_PATH_LINK");
                    var old = SaveCleanupPlan(external, DateTimeOffset.UtcNow.AddMonths(-2));
                    var cleanup = PlanCleanup.Run(junction);
                    Check(cleanup.DeletedPlans == 0 && cleanup.WarningCode == "CLEANUP_INCOMPLETE" && File.Exists(old.Path));
                }
                finally
                {
                    if (!process.HasExited) { process.Kill(entireProcessTree: true); await process.WaitForExitAsync(); }
                    // Remove only the fixed junction under this newly created test source, never its target.
                    Check(Path.GetFullPath(junction).StartsWith(Path.GetFullPath(root) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase));
                    if (Directory.Exists(junction)) Directory.Delete(junction, recursive: false);
                }
                Check(File.Exists(Path.Combine(external, "fixture.txt")));
            });
            await Run("remote drift between dry-run and execute blocks writes", async () =>
            {
                var (request, mock) = Setup();
                using var api = new NexusApi(Key, false, mock, new StorageMock());
                var plan = await Planner.PrepareAsync(request, api, Key, default);
                mock.OldVersion = "0.9";
                await RejectAsync(() => Publisher.ExecuteAsync(plan, request, Json.Hash(plan), api, Key, State(), default), "REMOTE_CHANGED");
                Check(mock.Calls.All(c => c.Method == "GET"));
            });
            await Run("remote drift during upload prevents file creation", async () =>
            {
                var (request, mock) = Setup(); mock.DriftAfterUpload = true;
                using var api = new NexusApi(Key, false, mock, new StorageMock());
                var plan = await Planner.PrepareAsync(request, api, Key, default);
                await RejectAsync(() => Publisher.ExecuteAsync(plan, request, Json.Hash(plan), api, Key, State(), default), "PARTIAL_OR_UNCERTAIN");
                Check(!mock.Published && mock.ChangelogCalls == 0);
            });
            await Run("changelog failure records partial and never retries", async () =>
            {
                var (request, mock) = Setup(); mock.FailChangelog = true;
                using var api = new NexusApi(Key, false, mock, new StorageMock());
                var plan = await Planner.PrepareAsync(request, api, Key, default); var state = State();
                await RejectAsync(() => Publisher.ExecuteAsync(plan, request, Json.Hash(plan), api, Key, state, default), "PARTIAL_OR_UNCERTAIN");
                var receipt = Json.Read<Receipt>(Path.Combine(state, request.RequestId + ".receipt.json"));
                Check(receipt.Status == "partial" && receipt.Stage == "changelog" && receipt.PublishedId == "102" && mock.ChangelogCalls == 1);
                Check(!Json.Serialize(receipt).Contains(Key));
            });
            await Run("HTTP 200 storage Error XML stops publication", async () =>
            {
                var (request, mock) = Setup(); var storage = new StorageMock { ErrorXml = true };
                using var api = new NexusApi(Key, false, mock, storage);
                var plan = await Planner.PrepareAsync(request, api, Key, default);
                await RejectAsync(() => Publisher.ExecuteAsync(plan, request, Json.Hash(plan), api, Key, State(), default), "PARTIAL_OR_UNCERTAIN");
                Check(!mock.Published && mock.ChangelogCalls == 0);
            });
            await Run("cancellation after write attempt records uncertainty", async () =>
            {
                var (request, mock) = Setup(); mock.CancelUpload = true;
                using var api = new NexusApi(Key, false, mock, new StorageMock());
                var plan = await Planner.PrepareAsync(request, api, Key, default); var state = State();
                await RejectAsync(() => Publisher.ExecuteAsync(plan, request, Json.Hash(plan), api, Key, state, default), "PARTIAL_OR_UNCERTAIN");
                var receipt = Json.Read<Receipt>(Path.Combine(state, request.RequestId + ".receipt.json"));
                Check(receipt.Status == "uncertain" && receipt.Stage == "create-upload" && !mock.Published);
            });
            await Run("API error body is never echoed", async () =>
            {
                using var api = new NexusApi(Key, true, new ErrorMock(), new StorageMock());
                try { await api.InspectAsync("12345", default); throw new Exception(); }
                catch (SafeException e) { Check(!e.Message.Contains(Key) && !e.Message.Contains("signature")); }
            });
            await Run("concurrent tasks cannot lock the same target", () =>
            {
                var (request, _) = Setup(); var state = State();
                using var one = new Journal(state, request);
                try { using var two = new Journal(state, request with { RequestId = Guid.NewGuid().ToString() }); throw new Exception(); } catch (IOException) { }
                return Task.CompletedTask;
            });
            await Run("credential JSON errors do not expose credential contents", () =>
            {
                var secretPath = Path.Combine(root, "invalid.secret.json");
                File.WriteAllText(secretPath, "{\"NEXUSMODS_API_KEY\":\"" + Key);
                try { Credentials.Load(secretPath); throw new Exception(); }
                catch (JsonException exception) { Check(!exception.ToString().Contains(Key)); }
                return Task.CompletedTask;
            });
            await Run("removed request fields and browser arguments are rejected", () =>
            {
                Reject(() => App.Parse(["update-file", "--session", "unused"]), "CLI");
                Reject(() => App.Parse(["update-file", "--site-profile", "unused"]), "CLI");
                Reject(() => App.Parse(["update-file", "--mod-url", "unused"]), "CLI");
                Reject(() => App.Parse(["inspect", "--expected-user-id", "unused"]), "CLI");
                foreach (var field in new[] { "schemaVersion", "expectedUserId", "expectedModName", "expectedModId", "modUrl", "fileAction", "fileId", "archive", "archiveSha256", "fileName", "version", "sha256" })
                {
                    try { Json.Parse<UpdateFileInput>("{\"" + field + "\":\"obsolete\"}"); throw new Exception(); } catch (JsonException) { }
                }
                foreach (var field in new[] { "target", "modId", "operation" })
                {
                    try { Json.Parse<UpdateFileInput>("{\"" + field + "\":\"obsolete\"}"); throw new Exception(); } catch (JsonException) { }
                }
                try { Json.Parse<UpdateFileInput>("{\"changes\":{}}"); throw new Exception(); } catch (JsonException) { }
                foreach (var field in new[] { "summary", "changelog", "includeRootDirectory", "target", "modId", "operation" })
                {
                    try { Json.Parse<UpdateInfoInput>("{\"" + field + "\":\"obsolete\"}"); throw new Exception(); } catch (JsonException) { }
                }
                return Task.CompletedTask;
            });
            Console.WriteLine($"PASS: {passed} test groups; no Nexus network traffic or real credentials used.");
            return 0;
        }
        catch (Exception exception)
        {
            Console.WriteLine("FAIL: " + exception.GetType().Name + (exception is SafeException safe ? " / " + safe.Code : "") + "; completed groups: " + passed);
            Console.WriteLine(exception.StackTrace); // Synthetic fixtures only; never print external error messages.
            return 1;
        }
        finally
        {
            // A fixed, newly created temp subtree only; never delete workspace or user files.
            try
            {
                if (root.StartsWith(Path.Combine(Path.GetTempPath(), "NexusUploader-tests-"), StringComparison.OrdinalIgnoreCase)) Directory.Delete(root, true);
            }
            catch { Console.WriteLine("Temporary fixture cleanup failed; no external error details printed."); }
        }
    }

    private static async Task Run(string name, Func<Task> test) { await test(); passed++; Console.WriteLine("PASS " + name); }
    private static void Check(bool value) { if (!value) throw new Exception("Test assertion failed."); }
    private static void Reject(Action action, string code)
    {
        try { action(); throw new Exception(); } catch (SafeException e) { Check(e.Code == code); }
    }
    private static async Task RejectAsync(Func<Task> action, string code)
    {
        try { await action(); throw new Exception(); } catch (SafeException e) { Check(e.Code == code); }
    }
    private static string State() { var path = Path.Combine(root, Guid.NewGuid().ToString()); Directory.CreateDirectory(path); return path; }
    private static UpdateFileInput FileInput(Request request) => new()
    {
        RequestId = request.RequestId, ModDirectory = request.ModDirectory, IncludeRootDirectory = request.IncludeRootDirectory,
        Description = request.Description, Changelog = request.Changelog
    };
    private static (UpdateInfoPlan Plan, string Path) SaveCleanupPlan(string state, DateTimeOffset expiry)
    {
        var (request, _) = SetupInfo();
        var plan = new UpdateInfoPlan(InfoPlanner.Version, expiry.AddMinutes(-15), expiry,
            request, InfoPlanner.Fingerprint(request));
        var hash = PlanStore.Save(state, plan);
        return (plan, PlanStore.PathFor(state, request.RequestId, hash));
    }
    private static (Request, ApiMock) Setup()
    {
        var directory = State();
        File.WriteAllText(Path.Combine(directory, "modinfo.ini"), "name=Test Mod\nversion=2.0\nauthor=Fixture");
        File.WriteAllText(Path.Combine(directory, "mod.lua"), "-- synthetic fixture\nreturn true");
        var request = new Request { RequestId = Guid.NewGuid().ToString(), Operation = "update-file", ModId = "12345",
            DirectoryKey = ModDirectoryIdentity.Read(directory).DirectoryKey, ModDirectory = directory,
            Description = "Example description", Changelog = "Example change" };
        return (request, new ApiMock());
    }
    private static (UpdateInfoRequest, ApiMock) SetupInfo() =>
        CreateInfo();
    private static (UpdateInfoRequest, ApiMock) CreateInfo()
    {
        var directory = State();
        File.WriteAllText(Path.Combine(directory, "modinfo.ini"), "name=Test Mod\nversion=2.0");
        return (new UpdateInfoRequest { RequestId = Guid.NewGuid().ToString(), Operation = "update-info", ModId = "12345",
            DirectoryKey = ModDirectoryIdentity.Read(directory).DirectoryKey, ModDirectory = directory,
            Description = "[b]Full mod page[/b]\nSecond line" }, new ApiMock());
    }
    private sealed class ApiMock : HttpMessageHandler
    {
        public List<(string Method, string Path)> Calls { get; } = [];
        public string OldVersion = "1.0", UploadFilename = "", PublishedVersion = "", PublishedName = "";
        public string ResponseFileId = "100", PublishedOwnerFileId = "100";
        public string ResolvedPageName = "Fixture Mod", CandidateStatus = "not_published";
        public bool FixtureIsCandidate;
        public Action? BeforeUpload;
        public string? Description, PageDescription;
        public bool Empty, Published, FailChangelog, CancelUpload, DriftAfterUpload, FailReadback, InfoUpdated, FailInfoUpdate;
        public bool IgnoreInfoUpdate;
        public int ChangelogCalls;
        private long size;
        private const string UploadId = "11111111-2222-3333-4444-555555555555";
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Check(request.RequestUri!.Host == "api.nexusmods.com" && request.Headers.GetValues("apikey").Single() == Key);
            var path = request.RequestUri.AbsolutePath;
            Calls.Add((request.Method.Method, path));
            if (path == "/v3/mods/12345" && request.Method == HttpMethod.Get)
                return Data(new { id = "12345", description = JsonSerializer.SerializeToElement(PageDescription, Json.Options) });
            if (path == "/v3/team-mods" && request.Method == HttpMethod.Get)
                return Response(new
                {
                    data = new
                    {
                        mods = FixtureIsCandidate ? new object[] { TeamMod("12345", "12604", ResolvedPageName, CandidateStatus, "owner") } : new object[]
                        {
                            TeamMod("12345", "12604", "Published Mod", "published", "owner"),
                            TeamMod("20000", "200", "New Private Page", CandidateStatus, "owner"),
                            TeamMod("30000", "300", "Hidden Existing Mod", "hidden", "contributor"),
                            TeamMod("40000", "400", "Moderated Draft", "under_moderation", "owner")
                        }
                    },
                    meta = new { page = 1, page_size = 100, total_count = FixtureIsCandidate ? 1 : 4 }
                });
            if (path == "/v3/mods/20000/files" && request.Method == HttpMethod.Get)
                return Data(new { mod_files = Array.Empty<object>() });
            if (path == "/v3/mods/30000/files" && request.Method == HttpMethod.Get)
                return Data(new { mod_files = new[] { new { id = "301", name = "Existing", is_active = true, versions_count = 1 } } });
            if (path == "/v3/games/skyrimspecialedition/mods/12604" && request.Method == HttpMethod.Get)
                return Data(new { id = "12345", game_scoped_id = "12604", game_id = "1704", name = ResolvedPageName });
            if (path == "/v3/mods/12345/files")
            {
                if (Published && FailReadback) return Response(new { error = Key }, 503);
                var files = new List<object>();
                if (!Empty || Published)
                    files.Add(new { id = "100", name = "Test Mod", is_active = true, versions_count = (Empty ? 0 : 1) + (Published && PublishedOwnerFileId == "100" ? 1 : 0) });
                if (Published && PublishedOwnerFileId != "100")
                    files.Add(new { id = PublishedOwnerFileId, name = "Other File", is_active = true, versions_count = 1 });
                return Data(new { mod_files = files });
            }
            if (path == "/v3/mod-files/100/versions" && request.Method == HttpMethod.Get)
            {
                var versions = new List<object>();
                if (!Empty) versions.Add(new { id = "101", file = new { id = "100" }, name = "Test Mod", version = OldVersion, category = "main", is_primary = !Published });
                if (Published && PublishedOwnerFileId == "100") versions.Add(new { id = "102", file = new { id = "100" }, name = PublishedName, version = PublishedVersion, category = "main", is_primary = true });
                return Data(new { versions });
            }
            if (Published && PublishedOwnerFileId != "100" && path == $"/v3/mod-files/{PublishedOwnerFileId}/versions" && request.Method == HttpMethod.Get)
                return Data(new { versions = new[] { new { id = "102", file = new { id = PublishedOwnerFileId }, name = PublishedName, version = PublishedVersion, category = "main", is_primary = true } } });
            if (path == "/v3/uploads/multipart")
            {
                BeforeUpload?.Invoke();
                if (CancelUpload) throw new OperationCanceledException("fake exception with " + Key);
                using var body = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(ct));
                size = body.RootElement.GetProperty("size_bytes").GetInt64();
                UploadFilename = body.RootElement.GetProperty("filename").GetString()!;
                return Data(new { id = UploadId, state = "created", part_size_bytes = size / 2 + 1, part_presigned_urls = new[] { "https://test.s3.amazonaws.com/part1?signature=SECRET", "https://test.s3.amazonaws.com/part2?signature=SECRET" }, complete_presigned_url = "https://test.s3.amazonaws.com/complete?signature=SECRET" }, 201);
            }
            if (path.StartsWith("/v3/uploads/"))
            {
                if (DriftAfterUpload && path.EndsWith("/finalise")) OldVersion = "0.9";
                return Data(new { id = UploadId, state = "available" });
            }
            if (path is "/v3/mod-files/100/versions" or "/v3/mod-files")
            {
                using var body = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(ct));
                Check(body.RootElement.GetProperty("primary_mod_manager_download").GetBoolean() && body.RootElement.GetProperty("update_mod_version").GetBoolean());
                Check(!body.RootElement.TryGetProperty("archive_existing_file", out _));
                Check(body.RootElement.GetProperty("file_category").GetString() == "main");
                PublishedName = body.RootElement.GetProperty("name").GetString()!;
                PublishedVersion = body.RootElement.GetProperty("version").GetString()!;
                Description = body.RootElement.TryGetProperty("description", out var description) ? description.GetString() : null;
                if (path == "/v3/mod-files") Check(Empty && body.RootElement.GetProperty("mod_id").GetString() == "12345");
                else Check(!Empty && !body.RootElement.TryGetProperty("mod_id", out _));
                Published = true;
                return path == "/v3/mod-files" ? Data(new { id = "102" }, 201) : Data(new { file = new { id = ResponseFileId }, version = new { id = "102" } }, 201);
            }
            if (path == "/v3/mods/12345/changelogs")
            {
                using var body = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(ct));
                Check(Published && body.RootElement.GetProperty("version").GetString() == PublishedVersion && body.RootElement.GetProperty("changelog").GetString() == "Example change");
                ChangelogCalls++; return FailChangelog ? Response(new { error = Key }, 500) : Data(new { }, 201);
            }
            if (path == "/v3/mods/12345" && request.Method == HttpMethod.Patch)
            {
                using var body = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(ct));
                Check(body.RootElement.EnumerateObject().Count() == 1);
                if (FailInfoUpdate) return Response(new { error = Key }, 500);
                if (!IgnoreInfoUpdate) PageDescription = body.RootElement.GetProperty("description").GetString();
                InfoUpdated = true;
                return new HttpResponseMessage(HttpStatusCode.NoContent);
            }
            throw new Exception("Unexpected fake API route.");
        }

        private static object TeamMod(string id, string scopedId, string name, string status, string affiliation) => new
        {
            mod = new
            {
                id,
                game_scoped_id = scopedId,
                name,
                status,
                game = new { id = "1704", name = "Skyrim Special Edition", domain_name = "skyrimspecialedition" }
            },
            affiliation,
            bug_tracker = "disabled",
            open_bug_count = 0
        };
    }
    private sealed class StorageMock : HttpMessageHandler
    {
        public List<byte> Bytes = []; public bool SawApiKey, ErrorXml;
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            SawApiKey |= request.Headers.Contains("apikey");
            if (request.Method == HttpMethod.Put)
            {
                Bytes.AddRange(await request.Content!.ReadAsByteArrayAsync(ct));
                var response = new HttpResponseMessage(HttpStatusCode.OK); response.Headers.ETag = new EntityTagHeaderValue("\"abc\""); return response;
            }
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(ErrorXml ? "<Error><Message>secret</Message></Error>" : "<CompleteMultipartUploadResult />") };
        }
    }
    private sealed class ErrorMock : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) => Task.FromResult(Response(new { key = Key, url = "https://test.s3.amazonaws.com/?signature=secret" }, 403));
    }
    private static HttpResponseMessage Data(object value, int status = 200) => Response(new { data = value }, status);
    private static HttpResponseMessage Response(object value, int status = 200) => new((HttpStatusCode)status) { Content = new StringContent(Json.Serialize(value), Encoding.UTF8, "application/json") };
}
