using System.IO.Compression;
using System.Text;
using NexusUploader;
using App = NexusUploader.Program;

internal static partial class Tests
{
    private static (UpdateFileInput Input, ApiMock Mock, string Registry) TemplateFixture(bool empty = false)
    {
        var (request, mock) = Setup(); mock.Empty = empty; mock.PageDescription = "Old page";
        var registry = Path.Combine(State(), "modconfig.json");
        ModRegistry.Add(registry, request.ModDirectory, new("skyrimspecialedition", "12604", "12345", "1704", "Fixture Mod"));
        File.WriteAllText(Path.Combine(request.ModDirectory, "doc.bbcode"), "[b]Features[/b]\nCurrent mod features.\n", new UTF8Encoding(false));
        WriteTemplates(registry);
        return (FileInput(request) with { DescriptionMode = "template", DescriptionProfile = "lua" }, mock, registry);
    }

    private static void WriteTemplates(string registry)
    {
        var templates = DescriptionComposer.RootFor(registry);
        var game = Path.Combine(templates, "games", "skyrimspecialedition");
        Directory.CreateDirectory(Path.Combine(game, "profiles"));
        File.WriteAllText(Path.Combine(templates, "global.bbcode"), "Global header\n{{content}}\nGlobal footer");
        File.WriteAllText(Path.Combine(game, "game.bbcode"), "Game header\n{{content}}\nGame footer");
        File.WriteAllText(Path.Combine(game, "profiles", "lua.bbcode"), "Lua header\n{{content}}\nLua footer");
    }

    private static async Task DescriptionTests()
    {
        await Run("root doc.bbcode is reserved metadata but nested documents are packaged", async () =>
        {
            foreach (var includeRoot in new[] { true, false })
            {
                var (request, _) = Setup();
                using var before = await Packaging.BuildAsync(request.ModDirectory, Key, includeRoot, default);
                File.WriteAllText(Path.Combine(request.ModDirectory, "doc.bbcode"), "Description only");
                using var after = await Packaging.BuildAsync(request.ModDirectory, Key, includeRoot, default);
                Check(before.Info.Sha256 == after.Info.Sha256 && before.Info.FileCount == after.Info.FileCount);
                Directory.CreateDirectory(Path.Combine(request.ModDirectory, "nested"));
                File.WriteAllText(Path.Combine(request.ModDirectory, "nested", "doc.bbcode"), "Game data");
                using var nested = await Packaging.BuildAsync(request.ModDirectory, Key, includeRoot, default);
                using var zip = new ZipArchive(nested.Stream, ZipArchiveMode.Read, leaveOpen: true);
                Check(zip.Entries.Count(e => e.FullName.EndsWith("doc.bbcode", StringComparison.Ordinal)) == 1 &&
                    zip.Entries.Any(e => e.FullName.EndsWith("nested/doc.bbcode", StringComparison.Ordinal)));
            }
        });
        await Run("root documents reject secrets, invalid UTF8, empty data and directories", async () =>
        {
            var (request, _) = Setup(); var path = Path.Combine(request.ModDirectory, "doc.bbcode");
            File.WriteAllText(path, Key);
            await RejectAsync(() => Packaging.BuildAsync(request.ModDirectory, Key, default), "SECRET_IN_INPUT");
            File.WriteAllText(path, " \n");
            await RejectAsync(() => Packaging.BuildAsync(request.ModDirectory, Key, default), "DOC_FILE");
            File.WriteAllBytes(path, [0xff, 0xfe, 0x41]);
            try { using var package = await Packaging.BuildAsync(request.ModDirectory, Key, default); throw new Exception(); }
            catch (DecoderFallbackException) { }
            File.Delete(path); Directory.CreateDirectory(path);
            await RejectAsync(() => Packaging.BuildAsync(request.ModDirectory, Key, default), "DOC_FILE");
        });
        await Run("wrapper composition supports headers and footers at every level", () =>
        {
            var (input, _, registry) = TemplateFixture();
            var request = ModRegistry.Bind(input, registry);
            var result = DescriptionComposer.Render(input.ModDirectory, request.PageDescription,
                DescriptionComposer.RootFor(registry), text => Guard.PublicText(text, Key));
            Check(result.Text == "Global header\nGame header\nLua header\n[b]Features[/b]\nCurrent mod features.\nLua footer\nGame footer\nGlobal footer");
            Check(result.Sources.Length == 4 && result.Sources.All(s => s.Sha256.Length == 64));
            Check(DescriptionComposer.Profiles(DescriptionComposer.RootFor(registry), "skyrimspecialedition").SequenceEqual(new[] { "lua" }));
            return Task.CompletedTask;
        });
        await Run("template selections placeholders and image URLs fail closed", () =>
        {
            Reject(() => DescriptionComposer.ValidateSelection("auto", null), "DESCRIPTION_MODE");
            Reject(() => DescriptionComposer.ValidateSelection("template", "../lua"), "DESCRIPTION_PROFILE");
            Reject(() => DescriptionComposer.ValidateSelection("manual", "lua"), "DESCRIPTION_PROFILE");
            var (input, _, registry) = TemplateFixture();
            Reject(() => ModRegistry.Bind(input with { DescriptionProfile = null }, registry), "DESCRIPTION_PROFILE");
            var request = ModRegistry.Bind(input, registry);
            var template = Path.Combine(DescriptionComposer.RootFor(registry), "global.bbcode");
            foreach (var content in new[] { "Missing placeholder", "{{content}}{{content}}", "{{other}}", "{{content}}{{broken" })
            {
                File.WriteAllText(template, content);
                Reject(() => DescriptionComposer.Render(input.ModDirectory, request.PageDescription,
                    DescriptionComposer.RootFor(registry), text => Guard.PublicText(text, Key)), "TEMPLATE_PLACEHOLDER");
            }
            WriteTemplates(registry);
            Reject(() => DescriptionComposer.Render(input.ModDirectory, request.PageDescription,
                DescriptionComposer.RootFor(registry), text => Guard.PublicText(text, Key), "Full inline body"), "DESCRIPTION_SOURCE");
            foreach (var bad in new[] { "[img]C:\\local.png[/img]", "[img]data:image/png;base64,AA[/img]", "[img]http://example.com/a.png[/img]", "[img]https://name:secret@example.com/a.png[/img]", "[img]missing close" })
                Reject(() => DescriptionComposer.ValidateImages(bad), "DESCRIPTION_IMAGE");
            DescriptionComposer.ValidateImages("[img]https://example.com/a.png[/img]");
            DescriptionComposer.ValidateImages("[code][img]C:\\example.png[/img][/code][noparse][img]local[/img][/noparse]");
            return Task.CompletedTask;
        });
        await Run("template dry-run freezes full description without writes or settings persistence", async () =>
        {
            var (input, mock, registry) = TemplateFixture();
            using var api = new NexusApi(Key, true, mock, new StorageMock());
            var plan = await FileWorkflow.PrepareAsync(input, null, registry, api, Key, default);
            Check(plan.PageDescription is { Before: "Old page", WriteRequired: true } && plan.PageDescription.After.Contains("Global footer"));
            Check(ModRegistry.Find(registry, input.ModDirectory).DescriptionSettings is null && mock.Calls.All(c => c.Method == "GET"));
        });
        await Run("confirmed template upload syncs after changelog and persists reusable settings", async () =>
        {
            var (input, mock, registry) = TemplateFixture();
            using var api = new NexusApi(Key, false, mock, new StorageMock());
            var plan = await FileWorkflow.PrepareAsync(input, null, registry, api, Key, default);
            var receipt = await FileWorkflow.ExecuteAsync(input, plan, Json.Hash(plan), registry, api, Key, State(), default);
            Check(receipt.Status == "success" && mock.Published && mock.InfoUpdated && mock.ChangelogCalls == 1);
            Check(mock.Description == input.Description && mock.PageDescription == plan.PageDescription!.After);
            Check(mock.Calls.FindIndex(c => c.Method == "PATCH") > mock.Calls.FindIndex(c => c.Path.EndsWith("/changelogs")));
            var saved = ModRegistry.Find(registry, input.ModDirectory);
            Check(saved.DescriptionSettings == new DescriptionSettings("template", "lua") && saved.DescriptionSha256 == plan.PageDescription!.AfterSha256);
            var reused = ModRegistry.Bind(input with { DescriptionMode = null, DescriptionProfile = null }, registry);
            Check(reused.PageDescription!.Settings == saved.DescriptionSettings && !reused.PageDescription.ExplicitSettings);
        });
        await Run("first-upload template selection is saved only after verified publication", async () =>
        {
            var (input, mock, registry) = TemplateFixture(empty: true);
            // Use a distinct empty synthetic registry for first binding, with the same templates.
            var firstRegistry = Path.Combine(Path.GetDirectoryName(registry)!, "first-modconfig.json");
            mock.FixtureIsCandidate = true;
            using var api = new NexusApi(Key, false, mock, new StorageMock());
            var plan = await FileWorkflow.PrepareAsync(input, "12345", firstRegistry, api, Key, default);
            Check(!File.Exists(firstRegistry) && plan.Registration is not null);
            var receipt = await FileWorkflow.ExecuteAsync(input, plan, Json.Hash(plan), firstRegistry, api, Key, State(), default);
            Check(receipt.Status == "success" && ModRegistry.Find(firstRegistry, input.ModDirectory).DescriptionSettings?.Profile == "lua");
        });
        await Run("document template and remote changes invalidate confirmation before upload", async () =>
        {
            foreach (var kind in new[] { "doc", "template", "remote" })
            {
                var (input, mock, registry) = TemplateFixture();
                using var api = new NexusApi(Key, false, mock, new StorageMock());
                var plan = await FileWorkflow.PrepareAsync(input, null, registry, api, Key, default); var state = State();
                if (kind == "doc") File.AppendAllText(Path.Combine(input.ModDirectory, "doc.bbcode"), "Changed");
                if (kind == "template") File.AppendAllText(Path.Combine(DescriptionComposer.RootFor(registry), "global.bbcode"), "Changed");
                if (kind == "remote") mock.PageDescription = "Changed remotely";
                await RejectAsync(() => FileWorkflow.ExecuteAsync(input, plan, Json.Hash(plan), registry, api, Key, state, default), "DESCRIPTION_CHANGED");
                Check(mock.Calls.All(c => c.Method == "GET") && !File.Exists(Path.Combine(state, input.RequestId + ".receipt.json")));
            }
        });
        await Run("missing template documents stop publication and manual mode keeps file-only behavior", async () =>
        {
            var (input, mock, registry) = TemplateFixture();
            File.Delete(Path.Combine(input.ModDirectory, "doc.bbcode"));
            using var api = new NexusApi(Key, false, mock, new StorageMock());
            await RejectAsync(() => FileWorkflow.PrepareAsync(input, null, registry, api, Key, default), "DOC_MISSING");
            input = input with { DescriptionMode = "manual", DescriptionProfile = null };
            var plan = await FileWorkflow.PrepareAsync(input, null, registry, api, Key, default);
            await FileWorkflow.ExecuteAsync(input, plan, Json.Hash(plan), registry, api, Key, State(), default);
            Check(plan.PageDescription is null && !mock.InfoUpdated && mock.Published);
            Check(ModRegistry.Find(registry, input.ModDirectory).DescriptionSettings?.Mode == "manual");
        });
        await Run("remote edits after synchronization require explicit reviewed adoption", async () =>
        {
            var (input, mock, registry) = TemplateFixture();
            var binding = ModRegistry.Find(registry, input.ModDirectory);
            var context = DescriptionComposer.Context(binding, "template", "lua");
            ModRegistry.SaveDescription(registry, binding.DirectoryKey, binding.ModId, context, DescriptionComposer.TextHash("Old page"));
            mock.PageDescription = "Website edit";
            var infoInput = new UpdateInfoInput { RequestId = Guid.NewGuid().ToString(), ModDirectory = input.ModDirectory };
            using var api = new NexusApi(Key, true, mock, new StorageMock());
            var rootPath = DescriptionComposer.RootFor(registry);
            await RejectAsync(() => InfoPlanner.PrepareAsync(ModRegistry.Bind(infoInput, registry), api, default, rootPath), "REMOTE_DESCRIPTION_CHANGED");
            var adopted = ModRegistry.Bind(infoInput with { DescriptionMode = "template" }, registry);
            var plan = await InfoPlanner.PrepareAsync(adopted, api, default, rootPath);
            Check(plan.PageDescription!.Before == "Website edit" && mock.Calls.All(c => c.Method == "GET"));
        });
        await Run("description failure preserves uploaded file and blocks whole-operation retries", async () =>
        {
            var (input, mock, registry) = TemplateFixture(); mock.FailInfoUpdate = true;
            using var api = new NexusApi(Key, false, mock, new StorageMock());
            var plan = await FileWorkflow.PrepareAsync(input, null, registry, api, Key, default); var state = State();
            await RejectAsync(() => FileWorkflow.ExecuteAsync(input, plan, Json.Hash(plan), registry, api, Key, state, default), "PARTIAL_OR_UNCERTAIN");
            var receipt = Json.Read<Receipt>(Path.Combine(state, input.RequestId + ".receipt.json"));
            Check(receipt.Status == "partial" && receipt.Stage == "update-description" && mock.Published && mock.ChangelogCalls == 1);
            Check(ModRegistry.Find(registry, input.ModDirectory).DescriptionSettings is null);
            await RejectAsync(() => FileWorkflow.ExecuteAsync(input, plan, Json.Hash(plan), registry, api, Key, state, default), "ALREADY_ATTEMPTED");
        });
        await Run("changes during upload stop the description patch and readback detects mismatches", async () =>
        {
            foreach (var drift in new[] { true, false })
            {
                var (input, mock, registry) = TemplateFixture();
                if (drift) mock.BeforeUpload = () => File.AppendAllText(Path.Combine(input.ModDirectory, "doc.bbcode"), "Changed during upload");
                else mock.IgnoreInfoUpdate = true;
                using var api = new NexusApi(Key, false, mock, new StorageMock());
                var plan = await FileWorkflow.PrepareAsync(input, null, registry, api, Key, default); var state = State();
                await RejectAsync(() => FileWorkflow.ExecuteAsync(input, plan, Json.Hash(plan), registry, api, Key, state, default), "PARTIAL_OR_UNCERTAIN");
                var receipt = Json.Read<Receipt>(Path.Combine(state, input.RequestId + ".receipt.json"));
                Check(receipt.Status == "partial" && receipt.ErrorCode == (drift ? "DESCRIPTION_CHANGED" : "DESCRIPTION_MISMATCH"));
                Check(mock.Published && (!drift || !mock.InfoUpdated));
            }
        });
        await Run("update-info reads root doc without description arguments and skips identical patches", async () =>
        {
            var (input, mock, registry) = TemplateFixture();
            var info = ModRegistry.Bind(new UpdateInfoInput { RequestId = Guid.NewGuid().ToString(), ModDirectory = input.ModDirectory }, registry);
            using var api = new NexusApi(Key, false, mock, new StorageMock());
            var rootPath = DescriptionComposer.RootFor(registry);
            var plan = await InfoPlanner.PrepareAsync(info, api, default, rootPath);
            Check(plan.PageDescription!.After.StartsWith("[b]Features[/b]", StringComparison.Ordinal));
            await InfoPublisher.ExecuteAsync(plan, info, Json.Hash(plan), api, State(), default, registry);
            var again = ModRegistry.Bind(new UpdateInfoInput { RequestId = Guid.NewGuid().ToString(), ModDirectory = input.ModDirectory }, registry);
            plan = await InfoPlanner.PrepareAsync(again, api, default, rootPath);
            Check(!plan.PageDescription!.WriteRequired);
            var patches = mock.Calls.Count(c => c.Method == "PATCH");
            await InfoPublisher.ExecuteAsync(plan, again, Json.Hash(plan), api, State(), default, registry);
            Check(mock.Calls.Count(c => c.Method == "PATCH") == patches && !mock.Published);
        });
        await Run("removed preview command and flags are rejected before configuration access", () =>
        {
            Reject(() => App.Parse(["preview-description", "--mod-directory", "unused"]), "CLI");
            foreach (var flag in new[] { "--watch", "--game", "--profile" })
                Reject(() => App.Parse(["update-file", "--request", "x", "--dry-run", flag]), "CLI");
            return Task.CompletedTask;
        });
        await Run("settings changes after binding are rechecked inside the publishing lock", async () =>
        {
            var (input, mock, registry) = TemplateFixture();
            using var api = new NexusApi(Key, false, mock, new StorageMock());
            var plan = await FileWorkflow.PrepareAsync(input, null, registry, api, Key, default);
            var binding = ModRegistry.Find(registry, input.ModDirectory);
            ModRegistry.SaveDescription(registry, binding.DirectoryKey, binding.ModId,
                DescriptionComposer.Context(binding, "manual", null), null);
            await RejectAsync(() => Publisher.ExecuteAsync(plan, plan.Request, Json.Hash(plan), api, Key, State(), default, registry), "BINDING_CHANGED");
            Check(mock.Calls.All(c => c.Method == "GET"));
        });
        await Run("large malformed BBCode is rejected without repeated scanning", () =>
        {
            Reject(() => DescriptionComposer.ValidateImages(string.Concat(Enumerable.Repeat("[img]", 10000))), "DESCRIPTION_IMAGE");
            Reject(() => DescriptionComposer.ValidateImages(string.Concat(Enumerable.Repeat("[code]", 10000))), "DESCRIPTION_BBCODE");
            return Task.CompletedTask;
        });
    }
}
