namespace NexusUploader;

internal static class FileWorkflow
{
    public static async Task<Plan> PrepareAsync(UpdateFileInput input, string? mod, string registryPath,
        NexusApi api, string key, CancellationToken ct)
    {
        if (mod is null)
            return await Planner.PrepareAsync(ModRegistry.Bind(input, registryPath), api, key, ct,
                templateRoot: DescriptionComposer.RootFor(registryPath));

        Guard.PublicText(mod, key);
        var identity = ModDirectoryIdentity.Read(input.ModDirectory);
        var numeric = mod.Length > 0 && mod.All(c => c is >= '0' and <= '9');
        if (numeric) Guard.Id(mod);
        var config = ModRegistry.RequireAvailable(registryPath, identity.DirectoryKey, numeric ? mod : null);
        var registered = config.Targets.Select(t => t.ModId).ToHashSet(StringComparer.Ordinal);
        var resolution = numeric
            ? (await api.CandidateAsync(mod, registered, ct)).Resolution
            : await api.ResolveAsync(Guard.ModUrl(mod), ct);
        ModRegistry.RequireAvailable(registryPath, identity.DirectoryKey, resolution.ModId);
        var target = new ModTarget(identity.DirectoryKey, identity.LocalModName, resolution.PageName,
            resolution.GameDomain, resolution.GameScopedId, resolution.ModId, resolution.GameId, DateTimeOffset.UtcNow);
        var proposal = new BindingProposal(numeric ? "candidate" : "url", target);
        Guard.PublicText(Json.Serialize(proposal), key);
        return await Planner.PrepareAsync(ModRegistry.Bind(input, target), api, key, ct, proposal,
            DescriptionComposer.RootFor(registryPath));
    }

    public static Request BindForConfirmation(UpdateFileInput input, Plan plan, string registryPath)
    {
        if (plan.Registration is null) return ModRegistry.Bind(input, registryPath);
        ValidateProposal(input.ModDirectory, plan.Registration);
        var target = plan.Registration.Target;
        ModRegistry.RequireAvailable(registryPath, target.DirectoryKey, target.ModId);
        return ModRegistry.Bind(input, target);
    }

    public static Task<Receipt> ExecuteAsync(UpdateFileInput input, Plan plan, string confirmation,
        string registryPath, NexusApi api, string key, string stateDirectory, CancellationToken ct)
    {
        Journal.RequireUnattempted(stateDirectory, input.RequestId);
        var request = BindForConfirmation(input, plan, registryPath);
        return Publisher.ExecuteAsync(plan, request, confirmation, api, key, stateDirectory, ct, registryPath);
    }

    // Called inside the mod publishing lock, after package/remote/plan checks and before any remote write.
    internal static async Task ConfirmBindingAsync(BindingProposal proposal, string modDirectory, string registryPath,
        NexusApi api, string key, CancellationToken ct)
    {
        ValidateProposal(modDirectory, proposal);
        Guard.PublicText(Json.Serialize(proposal), key);
        var expected = proposal.Target;
        var config = ModRegistry.RequireAvailable(registryPath, expected.DirectoryKey, expected.ModId);
        var registered = config.Targets.Select(t => t.ModId).ToHashSet(StringComparer.Ordinal);
        var actual = proposal.Source == "candidate"
            ? (await api.CandidateAsync(expected.ModId, registered, ct)).Resolution
            : await api.ResolveAsync(new(expected.GameDomain, expected.GameScopedId), ct);
        Guard.Require(actual == new ModResolution(expected.GameDomain, expected.GameScopedId,
            expected.ModId, expected.GameId, expected.PageName!), "BINDING_CHANGED",
            "Nexus 页面身份或名称与首次绑定计划不一致；请重新 dry-run。");
        ct.ThrowIfCancellationRequested();
        ModRegistry.ConfirmRegistration(registryPath, modDirectory, expected);
    }

    private static void ValidateProposal(string modDirectory, BindingProposal proposal)
    {
        Guard.Require(proposal.Source is "candidate" or "url" && proposal.Target is not null &&
            proposal.Target.PageName is not null, "BINDING_PROPOSAL", "首次绑定计划的来源或页面信息无效。");
        ModRegistry.CheckLocalTarget(modDirectory, proposal.Target);
    }
}
