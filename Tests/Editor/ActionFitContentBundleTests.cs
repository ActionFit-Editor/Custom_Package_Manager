#if UNITY_EDITOR
using System;
using System.Linq;
using NUnit.Framework;

public sealed class ActionFitContentBundleTests
{
    private const string EmptyManifest = "{\n  \"dependencies\": {\n  }\n}\n";

    [Test]
    public void PlanInstall_AddsEveryMissingPinnedPackage()
    {
        ActionFitContentBundleProfile profile = CreateProfile();

        ActionFitContentBundlePlan plan = ActionFitContentBundlePlanner.PlanInstall(
            profile,
            EmptyManifest,
            new ActionFitContentBundleStateFile(),
            _ => default);

        Assert.That(plan.success, Is.True);
        Assert.That(plan.changes.All(change => change.kind == ActionFitContentBundleChangeKind.Add), Is.True);
        Assert.That(plan.requiredPackageIds, Is.EquivalentTo(new[] { "com.actionfit.content-core", "com.actionfit.lava-rush.ui" }));
        string updated = ActionFitContentBundlePlanner.ApplyPlan(EmptyManifest, plan);
        Assert.That(ActionFitPackageManifestUtility.GetDependency(updated, "com.actionfit.lava-rush.ui"),
            Is.EqualTo("https://github.com/ActionFitGames/LavaRushUI.git#0.1.1"));
    }

    [Test]
    public void PlanInstall_PreservesCompatibleEmbeddedPackage()
    {
        ActionFitContentBundleProfile profile = CreateProfile();

        ActionFitContentBundlePlan plan = ActionFitContentBundlePlanner.PlanInstall(
            profile,
            EmptyManifest,
            new ActionFitContentBundleStateFile(),
            packageId => packageId == "com.actionfit.lava-rush.ui"
                ? new ActionFitContentBundleEmbeddedPackage(true, "0.1.2")
                : default);

        ActionFitContentBundleChange ui = plan.changes.Single(change => change.packageId == "com.actionfit.lava-rush.ui");
        Assert.That(plan.success, Is.True);
        Assert.That(ui.kind, Is.EqualTo(ActionFitContentBundleChangeKind.PreserveEmbedded));
    }

    [Test]
    public void PlanInstall_RejectsOlderEmbeddedPackage()
    {
        ActionFitContentBundlePlan plan = ActionFitContentBundlePlanner.PlanInstall(
            CreateProfile(),
            EmptyManifest,
            new ActionFitContentBundleStateFile(),
            packageId => packageId == "com.actionfit.lava-rush.ui"
                ? new ActionFitContentBundleEmbeddedPackage(true, "0.1.0")
                : default);

        Assert.That(plan.success, Is.False);
        Assert.That(plan.code, Is.EqualTo("INSTALL_CONFLICT"));
        Assert.That(plan.conflicts.Single(), Does.Contain("older than required"));
    }

    [Test]
    public void PlanInstall_UpdatesOlderCanonicalTagAndPreservesNewerTag()
    {
        string manifest = Manifest(
            ("com.actionfit.content-core", "https://github.com/ActionFitGames/ContentCore.git#0.1.9"),
            ("com.actionfit.lava-rush.ui", "https://github.com/ActionFitGames/LavaRushUI.git#0.1.2"));

        ActionFitContentBundlePlan plan = ActionFitContentBundlePlanner.PlanInstall(
            CreateProfile(), manifest, new ActionFitContentBundleStateFile(), _ => default);

        Assert.That(plan.success, Is.True);
        Assert.That(plan.changes.Single(change => change.packageId == "com.actionfit.content-core").kind,
            Is.EqualTo(ActionFitContentBundleChangeKind.Update));
        Assert.That(plan.changes.Single(change => change.packageId == "com.actionfit.lava-rush.ui").kind,
            Is.EqualTo(ActionFitContentBundleChangeKind.Preserve));
    }

    [Test]
    public void PlanInstall_PreservesForkAndReportsConflict()
    {
        string manifest = Manifest(
            ("com.actionfit.lava-rush.ui", "https://github.com/SomeoneElse/LavaRushUI.git#0.1.1"));

        ActionFitContentBundlePlan plan = ActionFitContentBundlePlanner.PlanInstall(
            CreateProfile(), manifest, new ActionFitContentBundleStateFile(), _ => default);

        Assert.That(plan.success, Is.False);
        Assert.That(plan.conflicts.Single(), Does.Contain("Non-canonical"));
        Assert.That(ActionFitContentBundlePlanner.ApplyPlan(manifest, plan), Is.EqualTo(manifest));
    }

    [Test]
    public void PlanInstall_RejectsDifferentPinOwnedByAnotherBundle()
    {
        var state = new ActionFitContentBundleStateFile
        {
            bundles = new[]
            {
                new ActionFitContentBundleRecord
                {
                    bundleId = "other",
                    packages = new[]
                    {
                        new ActionFitContentBundlePackageOwnership
                        {
                            packageId = "com.actionfit.content-core",
                            targetValue = "https://github.com/ActionFitGames/ContentCore.git#0.3.0",
                        },
                    },
                },
            },
        };

        ActionFitContentBundlePlan plan = ActionFitContentBundlePlanner.PlanInstall(
            CreateProfile(), EmptyManifest, state, _ => default);

        Assert.That(plan.success, Is.False);
        Assert.That(plan.conflicts.Single(), Does.Contain("another bundle"));
    }

    [Test]
    public void PlanRelease_RemovesExclusiveDependencyAndPreservesSharedDependency()
    {
        ActionFitContentBundleRecord current = CreateRecord();
        var state = new ActionFitContentBundleStateFile
        {
            bundles = new[]
            {
                current,
                new ActionFitContentBundleRecord
                {
                    bundleId = "other",
                    packages = new[]
                    {
                        new ActionFitContentBundlePackageOwnership { packageId = "com.actionfit.content-core" },
                    },
                },
            },
        };
        string manifest = Manifest(
            ("com.actionfit.content-core", current.packages[0].targetValue),
            ("com.actionfit.lava-rush.ui", current.packages[1].targetValue));

        ActionFitContentBundlePlan plan = ActionFitContentBundlePlanner.PlanRelease(current, state, manifest, _ => default);

        Assert.That(plan.changes.Single(change => change.packageId == "com.actionfit.content-core").kind,
            Is.EqualTo(ActionFitContentBundleChangeKind.Preserve));
        Assert.That(plan.changes.Single(change => change.packageId == "com.actionfit.lava-rush.ui").kind,
            Is.EqualTo(ActionFitContentBundleChangeKind.Remove));
        string updated = ActionFitContentBundlePlanner.ApplyPlan(manifest, plan);
        Assert.That(ActionFitPackageManifestUtility.GetDependency(updated, "com.actionfit.content-core"), Is.Not.Empty);
        Assert.That(ActionFitPackageManifestUtility.GetDependency(updated, "com.actionfit.lava-rush.ui"), Is.Empty);
    }

    [Test]
    public void PlanRelease_PreservesUserModifiedDependency()
    {
        ActionFitContentBundleRecord record = CreateRecord();
        string manifest = Manifest(
            ("com.actionfit.content-core", record.packages[0].targetValue),
            ("com.actionfit.lava-rush.ui", "https://github.com/SomeoneElse/LavaRushUI.git#custom"));

        ActionFitContentBundlePlan plan = ActionFitContentBundlePlanner.PlanRelease(
            record,
            new ActionFitContentBundleStateFile { bundles = new[] { record } },
            manifest,
            _ => default);

        ActionFitContentBundleChange ui = plan.changes.Single(change => change.packageId == "com.actionfit.lava-rush.ui");
        Assert.That(ui.kind, Is.EqualTo(ActionFitContentBundleChangeKind.Preserve));
        Assert.That(ActionFitContentBundlePlanner.ApplyPlan(manifest, plan), Does.Contain("SomeoneElse"));
    }

    [Test]
    public void PlanReconcile_RestoresMissingRequiredDependencyWithoutOverwritingConflict()
    {
        ActionFitContentBundleRecord record = CreateRecord();
        string manifest = Manifest(
            ("com.actionfit.lava-rush.ui", "https://github.com/SomeoneElse/LavaRushUI.git#custom"));

        ActionFitContentBundlePlan plan = ActionFitContentBundlePlanner.PlanReconcile(record, manifest, _ => default);

        Assert.That(plan.success, Is.False);
        Assert.That(plan.changes.Single(change => change.packageId == "com.actionfit.content-core").kind,
            Is.EqualTo(ActionFitContentBundleChangeKind.Add));
        Assert.That(plan.changes.Single(change => change.packageId == "com.actionfit.lava-rush.ui").kind,
            Is.EqualTo(ActionFitContentBundleChangeKind.Conflict));
        Assert.That(ActionFitContentBundlePlanner.ApplyPlan(manifest, plan), Is.EqualTo(manifest));
    }

    [Test]
    public void PlanReconcile_RestoresPreviouslyPreservedNewerCanonicalValue()
    {
        ActionFitContentBundleRecord record = CreateRecord();
        ActionFitContentBundlePackageOwnership core = record.packages.Single(package =>
            package.packageId == "com.actionfit.content-core");
        core.appliedValue = "https://github.com/ActionFitGames/ContentCore.git#0.3.0";

        ActionFitContentBundlePlan plan = ActionFitContentBundlePlanner.PlanReconcile(
            record,
            EmptyManifest,
            _ => default);

        ActionFitContentBundleChange change = plan.changes.Single(item =>
            item.packageId == "com.actionfit.content-core");
        Assert.That(change.kind, Is.EqualTo(ActionFitContentBundleChangeKind.Add));
        Assert.That(change.to, Is.EqualTo(core.appliedValue));
    }

    [Test]
    public void ReleaseAuthorization_UsesCaseInsensitiveExactGitHubLogin()
    {
        ActionFitContentBundleProfile profile = CreateProfile();
        profile.allowedReleaseGitHubLogins = new[] { "JewooSong" };

        Assert.That(ActionFitContentBundlePlanner.IsReleaseAuthorized(profile, "jewoosong"), Is.True);
        Assert.That(ActionFitContentBundlePlanner.IsReleaseAuthorized(profile, "Jewoo"), Is.False);
        Assert.That(ActionFitContentBundlePlanner.IsReleaseAuthorized(profile, ""), Is.False);
    }

    [Test]
    public void ValidateProfile_RejectsBranchRevision()
    {
        ActionFitContentBundleProfile profile = CreateProfile();
        profile.packages[0].gitUrl = "https://github.com/ActionFitGames/ContentCore.git#main";

        InvalidOperationException exception = Assert.Throws<InvalidOperationException>(
            () => ActionFitContentBundlePlanner.ValidateProfile(profile));

        Assert.That(exception.Message, Does.Contain("exact version tag"));
    }

    private static ActionFitContentBundleProfile CreateProfile()
    {
        return new ActionFitContentBundleProfile
        {
            bundleId = "lava-rush",
            bundleVersion = "0.1.0",
            displayName = "ActionFit Lava Rush",
            bootstrapPackageId = "com.actionfit.lava-rush.installer",
            packages = new[]
            {
                new ActionFitContentBundlePackageSpec
                {
                    packageId = "com.actionfit.content-core",
                    version = "0.2.0",
                    gitUrl = "https://github.com/ActionFitGames/ContentCore.git#0.2.0",
                    required = true,
                    removeOnRelease = true,
                },
                new ActionFitContentBundlePackageSpec
                {
                    packageId = "com.actionfit.lava-rush.ui",
                    version = "0.1.1",
                    gitUrl = "https://github.com/ActionFitGames/LavaRushUI.git#0.1.1",
                    required = true,
                    removeOnRelease = true,
                },
            },
        };
    }

    private static ActionFitContentBundleRecord CreateRecord()
    {
        ActionFitContentBundleProfile profile = CreateProfile();
        return new ActionFitContentBundleRecord
        {
            bundleId = profile.bundleId,
            bundleVersion = profile.bundleVersion,
            displayName = profile.displayName,
            packages = profile.packages.Select(package => new ActionFitContentBundlePackageOwnership
            {
                packageId = package.packageId,
                version = package.version,
                targetValue = package.gitUrl,
                appliedValue = package.gitUrl,
                originalValue = "",
                required = package.required,
                removeOnRelease = package.removeOnRelease,
            }).ToArray(),
        };
    }

    private static string Manifest(params (string Id, string Value)[] dependencies)
    {
        string entries = string.Join(",\n", dependencies.Select(dependency =>
            $"    \"{dependency.Id}\": \"{dependency.Value}\""));
        return $"{{\n  \"dependencies\": {{\n{entries}\n  }}\n}}\n";
    }
}
#endif
