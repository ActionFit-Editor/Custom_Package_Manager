#if UNITY_EDITOR
using System;
using System.IO;
using System.Linq;
using System.Reflection;
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

    [Test]
    public void ResolveSelection_SchemaOneKeepsLegacyAllPackageBehavior()
    {
        ActionFitContentBundleProfile profile = CreateProfile();

        ActionFitContentBundleSelection selection = ActionFitContentBundlePlanner.ResolveSelection(
            profile,
            Array.Empty<string>());

        Assert.That(selection.profile.packages.Select(package => package.packageId),
            Is.EquivalentTo(profile.packages.Select(package => package.packageId)));
        Assert.That(selection.selectedModuleIds, Is.Empty);
        Assert.That(selection.profile.packages.All(package => package.required), Is.True);
    }

    [Test]
    public void ResolveSelection_SchemaTwoUsesDefaultsAndKeepsRequiredModule()
    {
        ActionFitContentBundleProfile profile = CreateModularProfile();

        ActionFitContentBundleSelection defaults = ActionFitContentBundlePlanner.ResolveSelection(profile, null);
        ActionFitContentBundleSelection explicitEmpty = ActionFitContentBundlePlanner.ResolveSelection(
            profile,
            Array.Empty<string>());

        Assert.That(defaults.selectedModuleIds, Is.EqualTo(new[] { "core", "ui" }));
        Assert.That(defaults.profile.packages.Select(package => package.packageId),
            Is.EquivalentTo(new[]
            {
                "com.actionfit.content-core",
                "com.actionfit.ui.foundation",
                "com.actionfit.match-rival.ui",
            }));
        Assert.That(explicitEmpty.selectedModuleIds, Is.EqualTo(new[] { "core" }));
        Assert.That(explicitEmpty.profile.packages.Select(package => package.packageId),
            Is.EqualTo(new[] { "com.actionfit.content-core" }));
        Assert.That(explicitEmpty.profile.packages.Single().required, Is.True);
    }

    [Test]
    public void ResolveSelection_SharedLeafPackageIsInstalledOnce()
    {
        ActionFitContentBundleProfile profile = CreateModularProfile();

        ActionFitContentBundleSelection selection = ActionFitContentBundlePlanner.ResolveSelection(
            profile,
            new[] { "ui", "animation" });

        Assert.That(selection.selectedModuleIds, Is.EqualTo(new[] { "core", "ui", "animation" }));
        Assert.That(selection.profile.packages.Count(package => package.packageId == "com.actionfit.ui.foundation"), Is.EqualTo(1));
        Assert.That(selection.profile.packages.Any(package => package.packageId == "com.actionfit.match-rival.animation.dotween"), Is.True);
    }

    [Test]
    public void ValidateProfile_SchemaTwoRejectsUnassignedPackagesAndUnknownSelection()
    {
        ActionFitContentBundleProfile profile = CreateModularProfile();
        profile.modules[1].packageIds = new[] { "com.actionfit.ui.foundation" };

        InvalidOperationException unassigned = Assert.Throws<InvalidOperationException>(
            () => ActionFitContentBundlePlanner.ValidateProfile(profile));
        Assert.That(unassigned.Message, Does.Contain("not assigned"));

        profile = CreateModularProfile();
        InvalidOperationException unknown = Assert.Throws<InvalidOperationException>(
            () => ActionFitContentBundlePlanner.ResolveSelection(profile, new[] { "missing" }));
        Assert.That(unknown.Message, Does.Contain("Unknown content bundle modules"));
    }

    [Test]
    public void LegacyInstallerReflectionEntryPointsRemainUnambiguous()
    {
        MethodInfo install = typeof(ActionFitContentBundleApi).GetMethod(
            "InstallJson",
            BindingFlags.Public | BindingFlags.Static);
        MethodInfo repair = typeof(ActionFitContentBundleApi).GetMethod(
            "RepairJson",
            BindingFlags.Public | BindingFlags.Static);

        Assert.That(install, Is.Not.Null);
        Assert.That(repair, Is.Not.Null);
        Assert.That(install.GetParameters().Select(parameter => parameter.ParameterType),
            Is.EqualTo(new[] { typeof(string) }));
        Assert.That(repair.GetParameters().Select(parameter => parameter.ParameterType),
            Is.EqualTo(new[] { typeof(string) }));
    }

    [Test]
    public void ProjectOverrideStateKeepsPublicBaseRepositoryAndNoAbsolutePathField()
    {
        string[] fields = typeof(ActionFitPackageProjectOverrideRecord)
            .GetFields(BindingFlags.Public | BindingFlags.Instance)
            .Select(field => field.Name)
            .ToArray();

        Assert.That(fields, Does.Contain("embeddedPath"));
        Assert.That(fields, Does.Contain("baseRepositoryUrl"));
        Assert.That(fields.Any(field => field.Contains("absolute", StringComparison.OrdinalIgnoreCase)), Is.False);
    }

    [Test]
    public void ProjectOverrideRepositoryUrlAllowsOnlyCredentialFreeGitPathQuery()
    {
        Assert.That(
            ActionFitPackageProjectOverrideApi.NormalizePublicRepositoryUrl(
                "https://github.com/ActionFit-Editor/Content.git?path=/Packages/Core"),
            Is.EqualTo("https://github.com/ActionFit-Editor/Content.git?path=/Packages/Core"));
        Assert.Throws<InvalidOperationException>(() =>
            ActionFitPackageProjectOverrideApi.NormalizePublicRepositoryUrl(
                "https://github.com/ActionFit-Editor/Content.git?token=secret"));
        Assert.Throws<InvalidOperationException>(() =>
            ActionFitPackageProjectOverrideApi.NormalizePublicRepositoryUrl(
                "https://user:secret@github.com/ActionFit-Editor/Content.git"));
    }

    [Test]
    public void PublishPreflightRejectsRegisteredProjectOverrideBeforeUpstreamChecks()
    {
        string statePath = ActionFitPackageProjectOverrideStateStore.Path;
        bool existed = File.Exists(statePath);
        string original = existed ? File.ReadAllText(statePath) : "";
        try
        {
            ActionFitPackageProjectOverrideStateStore.Save(new ActionFitPackageProjectOverrideStateFile
            {
                overrides = new[]
                {
                    new ActionFitPackageProjectOverrideRecord
                    {
                        packageId = "com.actionfit.custompackagemanager",
                        baseRepositoryUrl = "https://github.com/ActionFit-Editor/Custom_Package_Manager.git",
                        baseVersion = "1.1.99",
                        baseRevision = "1.1.99",
                        baseContentHash = "ABC",
                        embeddedPath = "Packages/com.actionfit.custompackagemanager",
                    },
                },
            });

            ActionFitPackagePublishPlan plan = ActionFitPackagePublishApi.PrepareLocal(
                new ActionFitPackagePublishPrepareRequest
                {
                    PackageId = "com.actionfit.custompackagemanager",
                    RefreshCatalog = false,
                    CheckRemoteState = false,
                },
                false,
                out _);

            Assert.That(plan.Success, Is.False);
            Assert.That(plan.Code, Is.EqualTo("PROJECT_OVERRIDE_NOT_PUBLISHABLE"));
        }
        finally
        {
            if (existed)
                ActionFitPackageManifestUtility.WriteAtomic(statePath, original, false);
            else if (File.Exists(statePath))
                File.Delete(statePath);
        }
    }

    [Test]
    public void InstallerTemplateDeclaresSchemaTwoAndCanonicalManagerBootstrap()
    {
        const string root = "Packages/com.actionfit.custompackagemanager/Editor/Templates~/ContentBundleInstaller";
        string profile = File.ReadAllText(Path.Combine(root, "ContentBundleProfile.json.template"));
        string bootstrap = File.ReadAllText(Path.Combine(root, "InstallerBootstrap.cs.template"));

        StringAssert.Contains("\"schemaVersion\": 2", profile);
        StringAssert.Contains("\"modules\"", profile);
        StringAssert.Contains("ActionFitContentBundleApi", bootstrap);
        StringAssert.Contains("Client.Add(ManagerGitUrl)", bootstrap);
        StringAssert.Contains("IsDevelopmentEmbedded", bootstrap);
    }

    [Test]
    public void GeneratedAiStateIsDeterministicAndOmitsRemoteAndMachinePath()
    {
        var bundles = new[]
        {
            new ActionFitContentBundleStatus
            {
                bundleId = "feature",
                bundleVersion = "1.0.0",
                state = "active",
                selectedModuleIds = new[] { "ui", "core" },
                requiredModuleIds = new[] { "core" },
                requiredPackageIds = new[] { "com.actionfit.feature" },
                modules = new[]
                {
                    new ActionFitContentBundleModuleSpec
                    {
                        moduleId = "core",
                        required = true,
                        defaultSelected = true,
                        packageIds = new[] { "com.actionfit.feature" },
                    },
                    new ActionFitContentBundleModuleSpec
                    {
                        moduleId = "ui",
                        defaultSelected = true,
                        packageIds = new[] { "com.actionfit.feature.ui" },
                    },
                },
            },
        };
        var overrides = new[]
        {
            new ActionFitPackageProjectOverrideStatus
            {
                PackageId = "com.actionfit.feature.ui",
                BaseRepositoryUrl = "https://github.com/private/example.git",
                BaseVersion = "1.0.0",
                BaseRevision = "1.0.0",
                BaseContentHash = "1234567890ABCDEF",
                CurrentContentHash = "FEDCBA0987654321",
                EmbeddedPath = "/Users/example/private/project/Packages/com.actionfit.feature.ui",
                Modified = true,
                UpstreamVersion = "1.1.0",
                UpstreamUpdateAvailable = true,
            },
        };

        string first = ActionFitPackageAiGuideRouter.BuildPrivacySafePackageState(bundles, overrides);
        string second = ActionFitPackageAiGuideRouter.BuildPrivacySafePackageState(bundles, overrides);

        Assert.That(second, Is.EqualTo(first));
        StringAssert.Contains("selected modules: `core`, `ui`", first);
        StringAssert.Contains("default modules: `core`, `ui`", first);
        StringAssert.Contains("1234567890AB", first);
        StringAssert.DoesNotContain(overrides[0].BaseRepositoryUrl, first);
        StringAssert.DoesNotContain(overrides[0].EmbeddedPath, first);
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

    private static ActionFitContentBundleProfile CreateModularProfile()
    {
        return new ActionFitContentBundleProfile
        {
            schemaVersion = 2,
            bundleId = "match-rival",
            bundleVersion = "0.2.0",
            displayName = "ActionFit Match Rival",
            bootstrapPackageId = "com.actionfit.match-rival.installer",
            packages = new[]
            {
                Package("com.actionfit.content-core", "0.2.0", "ContentCore"),
                Package("com.actionfit.ui.foundation", "1.0.0", "UIFoundation"),
                Package("com.actionfit.match-rival.ui", "0.2.0", "MatchRivalUI"),
                Package("com.actionfit.match-rival.animation.dotween", "0.2.0", "MatchRivalAnimationDotween"),
            },
            modules = new[]
            {
                new ActionFitContentBundleModuleSpec
                {
                    moduleId = "core",
                    displayName = "Origin / Core",
                    required = true,
                    defaultSelected = true,
                    packageIds = new[] { "com.actionfit.content-core" },
                },
                new ActionFitContentBundleModuleSpec
                {
                    moduleId = "ui",
                    displayName = "UI Foundation Binding",
                    defaultSelected = true,
                    packageIds = new[]
                    {
                        "com.actionfit.ui.foundation",
                        "com.actionfit.match-rival.ui",
                    },
                },
                new ActionFitContentBundleModuleSpec
                {
                    moduleId = "animation",
                    displayName = "DOTween Animation",
                    defaultSelected = false,
                    packageIds = new[]
                    {
                        "com.actionfit.ui.foundation",
                        "com.actionfit.match-rival.animation.dotween",
                    },
                },
            },
        };
    }

    private static ActionFitContentBundlePackageSpec Package(string packageId, string version, string repository)
    {
        return new ActionFitContentBundlePackageSpec
        {
            packageId = packageId,
            version = version,
            gitUrl = $"https://github.com/ActionFit-Editor/{repository}.git#{version}",
            removeOnRelease = true,
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
