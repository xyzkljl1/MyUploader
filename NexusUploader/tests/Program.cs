using System.IO.Compression;
using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using NexusUploader;
using App = NexusUploader.Program;

internal static class Tests
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
                Reject(() => App.Parse(["update-file", "--config", "x", "--request", "r", "--plan", "p"]), "CLI_MODE");
                Reject(() => App.Parse(["update-file", "--config", "x", "--config", "y"]), "CLI_DUPLICATE");
                Reject(() => App.Parse(["inspect", "--apikey", Key]), "CLI");
                Reject(() => Json.Parse<Request>("{\"modId\":\"1\",\"ModId\":\"2\"}"), "DUPLICATE_JSON");
                try { Json.Parse<Request>("{\"typo\":1}"); throw new Exception(); } catch (JsonException) { }
                return Task.CompletedTask;
            });
            await Run("global ID, relative folder and public secret validation", () =>
            {
                foreach (var value in new[] { "0", "-1", "1/2", "https://www.nexusmods.com/game/mods/1", " 1", "1?x" })
                    Reject(() => Guard.Id(value), "ID");
                var (request, _) = Setup();
                var normalized = Guard.Validate(request with { ModDirectory = Path.GetFileName(request.ModDirectory), RequestId = request.RequestId.ToUpperInvariant() }, "update-file", root, Key);
                Check(normalized.ModDirectory == request.ModDirectory && normalized.RequestId == request.RequestId);
                Reject(() => Guard.Validate(request with { ModDirectory = Path.Combine(root, "missing") }, "update-file", root, Key), "MOD_DIRECTORY");
                Reject(() => Guard.PublicText(Key, Key), "SECRET_IN_INPUT");
                Reject(() => NexusApi.StorageUrl("https://localhost/file?signature=secret"), "STORAGE_HOST");
                Reject(() => NexusApi.StorageUrl("https://bucket.s3.amazonaws.com.evil.test/a"), "STORAGE_HOST");
                Reject(() => NexusApi.StorageUrl("https://bucket.s3.amazonaws.com:8443/a"), "STORAGE_HOST");
                return Task.CompletedTask;
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
                foreach (var name in new[] { "credentials.json", "nested/.env", "nested/storage-state.json", "nested/token.secret.json", "private.pem", "private.key" })
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
                using (var package = await Packaging.BuildAsync(request.ModDirectory, Key, default))
                {
                    first = package.Info; temporary = package.Stream.Name;
                    Check(first.Name == "Test Mod" && first.Version == "2.0" && first.ArchiveName == "Test_Mod-2.0.zip" && first.FileCount == 3);
                    using var zip = new ZipArchive(package.Stream, ZipArchiveMode.Read, leaveOpen: true);
                    Check(zip.Entries.Select(e => e.FullName).SequenceEqual(new[] { "empty/", "mod.lua", "modinfo.ini", "nested/", "nested/data.txt" }));
                    using var reader = new StreamReader(zip.GetEntry("modinfo.ini")!.Open());
                    Check(reader.ReadToEnd().Contains("version=2.0"));
                }
                Check(!File.Exists(temporary));
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
                var receipt = await Publisher.ExecuteAsync(plan, request, Json.Hash(plan), api, Key, state, default);
                Check(receipt.Status == "success" && receipt.PublishedId == "102");
                Check(Json.HashBytes(storage.Bytes.ToArray()) == plan.Package.Sha256);
                Check(mock.UploadFilename == plan.Package.ArchiveName && mock.PublishedVersion == plan.Package.Version && mock.PublishedName == plan.Intent.Name);
                Check(!storage.SawApiKey && mock.ChangelogCalls == 1);
                var calls = mock.Calls.Count;
                await RejectAsync(() => Publisher.ExecuteAsync(plan, request, Json.Hash(plan), api, Key, state, default), "ALREADY_ATTEMPTED");
                Check(mock.Calls.Count == calls);
            });
            await Run("first Main File publish path", async () =>
            {
                var (request, mock) = Setup(); mock.Empty = true;
                using var api = new NexusApi(Key, false, mock, new StorageMock());
                var plan = await Planner.PrepareAsync(request, api, Key, default);
                var receipt = await Publisher.ExecuteAsync(plan, request, Json.Hash(plan), api, Key, State(), default);
                Check(receipt.Status == "success" && receipt.PublishedId == "100");
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
            await Run("directory junctions cannot escape the package root", async () =>
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
            await Run("CLI JSON errors do not expose credential contents", async () =>
            {
                var secretPath = Path.Combine(root, "invalid.secret.json");
                File.WriteAllText(secretPath, "{\"NEXUSMODS_API_KEY\":\"" + Key);
                var original = Console.Out; using var output = new StringWriter();
                try { Console.SetOut(output); var result = await App.Main(["inspect", "--config", secretPath, "--mod-id", "12345"]); Check(result == 2); }
                finally { Console.SetOut(original); }
                Check(!output.ToString().Contains(Key) && output.ToString().Contains("JSON_INVALID"));
            });
            await Run("unsupported operations stop before credentials or network", async () =>
            {
                foreach (var command in new[] { "create-mod", "update-info" })
                foreach (var mode in new[] { "--dry-run", "--execute" })
                {
                    var original = Console.Out; using var output = new StringWriter();
                    try
                    {
                        Console.SetOut(output);
                        var result = await App.Main([command, "--config", Path.Combine(root, "does-not-exist.secret.json"), mode]);
                        Check(result == 2);
                    }
                    finally { Console.SetOut(original); }
                    using var response = JsonDocument.Parse(output.ToString());
                    Check(response.RootElement.GetProperty("code").GetString() == "UNSUPPORTED_OPERATION");
                    Check(!output.ToString().Contains("does-not-exist"));
                }
            });
            await Run("removed request fields and browser arguments are rejected", () =>
            {
                Reject(() => App.Parse(["update-file", "--session", "unused"]), "CLI");
                Reject(() => App.Parse(["update-file", "--site-profile", "unused"]), "CLI");
                Reject(() => App.Parse(["inspect", "--mod-url", "unused"]), "CLI");
                Reject(() => App.Parse(["inspect", "--expected-user-id", "unused"]), "CLI");
                foreach (var field in new[] { "schemaVersion", "expectedUserId", "expectedModName", "expectedModId", "modUrl", "fileAction", "fileId", "archive", "archiveSha256", "fileName", "version", "sha256" })
                {
                    try { Json.Parse<Request>("{\"" + field + "\":\"obsolete\"}"); throw new Exception(); } catch (JsonException) { }
                }
                try { Json.Parse<Request>("{\"changes\":{}}"); throw new Exception(); } catch (JsonException) { }
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
    private static (Request, ApiMock) Setup()
    {
        var directory = State();
        File.WriteAllText(Path.Combine(directory, "modinfo.ini"), "name=Test Mod\nversion=2.0\nauthor=Fixture");
        File.WriteAllText(Path.Combine(directory, "mod.lua"), "-- synthetic fixture\nreturn true");
        var request = new Request { RequestId = Guid.NewGuid().ToString(), Operation = "update-file", ModId = "12345", ModDirectory = directory, Description = "Example description", Changelog = "Example change" };
        return (request, new ApiMock());
    }
    private sealed class ApiMock : HttpMessageHandler
    {
        public List<(string Method, string Path)> Calls { get; } = [];
        public string OldVersion = "1.0", UploadFilename = "", PublishedVersion = "", PublishedName = "";
        public string? Description;
        public bool Empty, Published, FailChangelog, CancelUpload, DriftAfterUpload;
        public int ChangelogCalls;
        private long size;
        private const string UploadId = "11111111-2222-3333-4444-555555555555";
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Check(request.RequestUri!.Host == "api.nexusmods.com" && request.Headers.GetValues("apikey").Single() == Key);
            var path = request.RequestUri.AbsolutePath;
            Calls.Add((request.Method.Method, path));
            if (path == "/v3/mods/12345/files") return Data(new { mod_files = Empty && !Published ? System.Array.Empty<object>() : new object[] { new { id = "100", name = "Test Mod", is_active = true, versions_count = Published && !Empty ? 2 : 1 } } });
            if (path == "/v3/mod-files/100/versions" && request.Method == HttpMethod.Get)
            {
                var versions = new List<object>();
                if (!Empty) versions.Add(new { id = "101", file = new { id = "100" }, name = "Test Mod", version = OldVersion, category = "main", is_primary = !Published });
                if (Published) versions.Add(new { id = "102", file = new { id = "100" }, name = PublishedName, version = PublishedVersion, category = "main", is_primary = true });
                return Data(new { versions });
            }
            if (path == "/v3/uploads/multipart")
            {
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
                return path == "/v3/mod-files" ? Data(new { id = "100" }, 201) : Data(new { file = new { id = "100" }, version = new { id = "102" } }, 201);
            }
            if (path == "/v3/mods/12345/changelogs")
            {
                using var body = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(ct));
                Check(Published && body.RootElement.GetProperty("version").GetString() == PublishedVersion && body.RootElement.GetProperty("changelog").GetString() == "Example change");
                ChangelogCalls++; return FailChangelog ? Response(new { error = Key }, 500) : Data(new { }, 201);
            }
            throw new Exception("Unexpected fake API route.");
        }
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
