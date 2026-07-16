#if UNITY_EDITOR
using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using NUnit.Framework;
using UnityEngine;

public sealed class ActionFitSdkInstallProfileTests
{
    private string _temporaryRoot;

    [SetUp]
    public void SetUp()
    {
        _temporaryRoot = Path.Combine(Path.GetTempPath(), "ActionFitSdkProfileTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_temporaryRoot);
    }

    [TearDown]
    public void TearDown()
    {
        if (Directory.Exists(_temporaryRoot)) Directory.Delete(_temporaryRoot, true);
    }

    [Test]
    public void Validate_ValidRegistryProfilePasses()
    {
        ActionFitSdkProfileValidationResult result = ActionFitSdkInstallProfileValidator.Validate(CreateProfile());

        Assert.That(result.Success, Is.True, result.FormatMessage());
        Assert.That(result.Diagnostics, Is.Empty);
    }

    [Test]
    public void Validate_MutableGitCredentialAndArtifactChecksumReportStableCodes()
    {
        ActionFitSdkInstallProfile profile = CreateProfile();
        profile.Sources = new[]
        {
            new ActionFitSdkSourceDefinition
            {
                Id = "git",
                Kind = "git",
                Url = "https://token@example.com/vendor/sdk.git",
                ImmutableRevision = "main",
                PackageId = "com.vendor.sdk",
            },
            new ActionFitSdkSourceDefinition
            {
                Id = "artifact",
                Kind = "artifact",
                Url = "https://example.com/vendor/sdk.tgz",
                ImmutableVersion = "1.2.3",
                PackageId = "com.vendor.artifact",
                PackageVersion = "1.2.3",
                Sha256 = "short",
                CacheRelativePath = "ActionFitSdkArtifacts/vendor/sdk.tgz",
            },
        };
        profile.Dependencies = Array.Empty<ActionFitSdkDependencyDefinition>();

        ActionFitSdkProfileValidationResult result = ActionFitSdkInstallProfileValidator.Validate(profile);
        string[] codes = result.Diagnostics.Select(item => item.Code).ToArray();

        Assert.That(result.Success, Is.False);
        Assert.That(codes, Does.Contain("SOURCE_URL_INVALID"));
        Assert.That(codes, Does.Contain("SOURCE_GIT_REVISION_MUTABLE"));
        Assert.That(codes, Does.Contain("SOURCE_SHA256_INVALID"));
    }

    [Test]
    public void Validate_OfficialSourceQueryParametersAreRejected()
    {
        ActionFitSdkInstallProfile profile = CreateProfile();
        profile.Sources[0].Url = "https://registry.example.com?access_token=secret";

        ActionFitSdkProfileValidationResult result = ActionFitSdkInstallProfileValidator.Validate(profile);

        Assert.That(result.Success, Is.False);
        Assert.That(result.Diagnostics.Select(item => item.Code), Does.Contain("SOURCE_DOMAIN_NOT_ALLOWED"));
    }

    [Test]
    public void Validate_GitSubpathAcceptsSafeUpmPathAndRejectsTraversal()
    {
        ActionFitSdkInstallProfile profile = CreateGitProfile("Assets/VendorSdk");

        ActionFitSdkProfileValidationResult valid = ActionFitSdkInstallProfileValidator.Validate(profile);

        Assert.That(valid.Success, Is.True, valid.FormatMessage());

        profile.Sources[0].GitSubpath = "Assets/../VendorSdk";
        ActionFitSdkProfileValidationResult invalid = ActionFitSdkInstallProfileValidator.Validate(profile);

        Assert.That(invalid.Success, Is.False);
        Assert.That(invalid.Diagnostics.Select(item => item.Code), Does.Contain("SOURCE_GIT_SUBPATH_INVALID"));
    }

    [Test]
    public async Task Apply_GitSubpathBuildsPinnedUpmDependencyValue()
    {
        ActionFitSdkProjectContext context = CreateProject();
        ActionFitSdkInstallProfile profile = CreateGitProfile("Assets/VendorSdk");

        ActionFitSdkInstallPlan plan = ActionFitSdkInstallApi.Plan(profile, new ActionFitSdkPlanRequest(), context);
        ActionFitSdkExecutionResult result = await ActionFitSdkInstallApi.ApplyAsync(plan, plan.PlanId);

        Assert.That(plan.Success, Is.True, plan.Message);
        Assert.That(result.Success, Is.True, result.Message);
        Assert.That(
            ActionFitPackageManifestUtility.GetDependency(File.ReadAllText(context.ManifestPath), "com.vendor.sdk"),
            Is.EqualTo("https://example.com/vendor/sdk.git?path=Assets/VendorSdk#0123456789abcdef0123456789abcdef01234567"));
    }

    [Test]
    public void Inspect_SelectedModuleIncludesRequiredClosure()
    {
        ActionFitSdkProjectContext context = CreateProject();
        ActionFitSdkInstallProfile profile = CreateProfile();

        ActionFitSdkInspectionResult result = ActionFitSdkInstallApi.Inspect(profile, new[] { "extras" }, context);

        Assert.That(result.Success, Is.True, result.Message);
        Assert.That(result.SelectedModuleIds, Is.EqualTo(new[] { "core", "extras" }));
    }

    [Test]
    public void Plan_UnsupportedUnityVersionIsBlocked()
    {
        ActionFitSdkProjectContext context = CreateProject();
        ActionFitSdkInstallProfile profile = CreateProfile();
        profile.MinimumUnityVersion = "9999.1";

        ActionFitSdkInstallPlan plan = ActionFitSdkInstallApi.Plan(profile, new ActionFitSdkPlanRequest(), context);

        Assert.That(plan.Success, Is.False);
        Assert.That(plan.Findings.Any(item => item.RuleId == "unity-version-unsupported"), Is.True);
    }

    [Test]
    public void ScopedRegistries_RoundTripPreservesUnrelatedRegistryAndScopes()
    {
        const string manifest = "{\n  \"scopedRegistries\": [\n    {\n      \"name\": \"Existing\",\n      \"url\": \"https://existing.example.com\",\n      \"scopes\": [\"com.existing\"]\n    }\n  ],\n  \"dependencies\": {}\n}\n";
        var registries = ActionFitPackageManifestUtility.ReadScopedRegistries(manifest);
        registries.Add(new ActionFitSdkScopedRegistryValue
        {
            Name = "Vendor",
            Url = "https://registry.example.com",
            Scopes = new[] { "com.vendor", "com.vendor.sdk" },
        });

        string updated = ActionFitPackageManifestUtility.SetScopedRegistries(manifest, registries);
        var roundTrip = ActionFitPackageManifestUtility.ReadScopedRegistries(updated);

        Assert.That(roundTrip, Has.Count.EqualTo(2));
        Assert.That(roundTrip.Single(item => item.Name == "Existing").Scopes, Is.EqualTo(new[] { "com.existing" }));
        Assert.That(roundTrip.Single(item => item.Name == "Vendor").Scopes, Is.EqualTo(new[] { "com.vendor", "com.vendor.sdk" }));
        ActionFitPackageManifestUtility.Validate(updated);
    }

    [Test]
    public void Plan_CompatibleDependencyIsAdoptedWithoutTakingDestructiveOwnership()
    {
        ActionFitSdkProjectContext context = CreateProject("{\n  \"dependencies\": {\n    \"com.vendor.sdk\": \"1.2.3\"\n  }\n}\n");

        ActionFitSdkInstallPlan plan = ActionFitSdkInstallApi.Plan(CreateProfile(), new ActionFitSdkPlanRequest
        {
            Operation = ActionFitSdkInstallOperation.Apply,
            SelectedModuleIds = new[] { "core" },
            AdoptCompatible = true,
            TakeOwnershipOfCompatibleEntries = false,
        }, context);

        Assert.That(plan.Success, Is.True, plan.Message);
        ActionFitSdkPlannedChange adopted = plan.Changes.Single(item => item.Key == "com.vendor.sdk");
        Assert.That(adopted.Action, Is.EqualTo(ActionFitSdkPlannedChangeAction.Adopt));
        Assert.That(adopted.OwnedByProfile, Is.False);
    }

    [Test]
    public void Plan_ConflictingDependencyBlocksExecution()
    {
        ActionFitSdkProjectContext context = CreateProject("{\n  \"dependencies\": {\n    \"com.vendor.sdk\": \"9.9.9\"\n  }\n}\n");

        ActionFitSdkInstallPlan plan = ActionFitSdkInstallApi.Plan(CreateProfile(), new ActionFitSdkPlanRequest(), context);

        Assert.That(plan.Success, Is.False);
        Assert.That(plan.Code, Is.EqualTo("CONFLICT"));
        Assert.That(plan.Findings.Any(item => item.Classification == ActionFitSdkInstallationClassification.Conflicting), Is.True);
    }

    [Test]
    public async Task ApplyAndRemove_UseReviewedPlanAndOwnedStateOnly()
    {
        ActionFitSdkProjectContext context = CreateProject();
        ActionFitSdkInstallProfile profile = CreateProfile();
        var applyRequest = new ActionFitSdkPlanRequest
        {
            Operation = ActionFitSdkInstallOperation.Apply,
            SelectedModuleIds = new[] { "core" },
        };
        ActionFitSdkInstallPlan applyPlan = ActionFitSdkInstallApi.Plan(profile, applyRequest, context);

        ActionFitSdkExecutionResult apply = await ActionFitSdkInstallApi.ApplyAsync(applyPlan, applyPlan.PlanId);

        Assert.That(apply.Success, Is.True, apply.Message);
        Assert.That(ActionFitPackageManifestUtility.GetDependency(File.ReadAllText(context.ManifestPath), "com.vendor.sdk"), Is.EqualTo("1.2.3"));
        Assert.That(File.Exists(context.OwnershipPath), Is.True);

        ActionFitSdkInstallPlan removePlan = ActionFitSdkInstallApi.Plan(profile, new ActionFitSdkPlanRequest
        {
            Operation = ActionFitSdkInstallOperation.Remove,
        }, context);
        ActionFitSdkExecutionResult remove = await ActionFitSdkInstallApi.RemoveAsync(removePlan, removePlan.PlanId);

        Assert.That(remove.Success, Is.True, remove.Message);
        string removedManifest = File.ReadAllText(context.ManifestPath);
        Assert.That(ActionFitPackageManifestUtility.GetDependency(removedManifest, "com.vendor.sdk"), Is.Empty);
        Assert.That(ActionFitPackageManifestUtility.GetDependency(removedManifest, "com.unity.test-framework"), Is.EqualTo("1.1.33"));
        Assert.That(ActionFitPackageManifestUtility.ReadScopedRegistries(removedManifest), Is.Empty);
    }

    [Test]
    public async Task Remove_AdoptedDependencyPreservesItsRegistrySupport()
    {
        ActionFitSdkProjectContext context = CreateProject("{\n  \"dependencies\": {\n    \"com.vendor.sdk\": \"1.2.3\"\n  }\n}\n");
        ActionFitSdkInstallProfile profile = CreateProfile();
        ActionFitSdkInstallPlan applyPlan = ActionFitSdkInstallApi.Plan(profile, new ActionFitSdkPlanRequest
        {
            Operation = ActionFitSdkInstallOperation.Apply,
            AdoptCompatible = true,
            TakeOwnershipOfCompatibleEntries = false,
        }, context);
        ActionFitSdkExecutionResult apply = await ActionFitSdkInstallApi.ApplyAsync(applyPlan, applyPlan.PlanId);
        Assert.That(apply.Success, Is.True, apply.Message);

        ActionFitSdkInstallPlan removePlan = ActionFitSdkInstallApi.Plan(profile, new ActionFitSdkPlanRequest
        {
            Operation = ActionFitSdkInstallOperation.Remove,
        }, context);
        ActionFitSdkExecutionResult remove = await ActionFitSdkInstallApi.RemoveAsync(removePlan, removePlan.PlanId);

        Assert.That(remove.Success, Is.True, remove.Message);
        string manifest = File.ReadAllText(context.ManifestPath);
        Assert.That(ActionFitPackageManifestUtility.GetDependency(manifest, "com.vendor.sdk"), Is.EqualTo("1.2.3"));
        Assert.That(ActionFitPackageManifestUtility.ReadScopedRegistries(manifest).Single().Scopes, Does.Contain("com.vendor"));
    }

    [Test]
    public void RepairWithoutOwnershipIsBlocked()
    {
        ActionFitSdkProjectContext context = CreateProject();

        ActionFitSdkInstallPlan plan = ActionFitSdkInstallApi.Plan(CreateProfile(), new ActionFitSdkPlanRequest
        {
            Operation = ActionFitSdkInstallOperation.Repair,
        }, context);

        Assert.That(plan.Success, Is.False);
        Assert.That(plan.Findings.Any(item => item.RuleId == "ownership-required"), Is.True);
    }

    [Test]
    public async Task Apply_RejectsStaleManifestSnapshotBeforeMutation()
    {
        ActionFitSdkProjectContext context = CreateProject();
        ActionFitSdkInstallPlan plan = ActionFitSdkInstallApi.Plan(CreateProfile(), new ActionFitSdkPlanRequest(), context);
        File.AppendAllText(context.ManifestPath, "\n");

        ActionFitSdkExecutionResult result = await ActionFitSdkInstallApi.ApplyAsync(plan, plan.PlanId);

        Assert.That(result.Success, Is.False);
        Assert.That(result.Code, Is.EqualTo("PREPARE_FAILED"));
        Assert.That(File.Exists(context.OwnershipPath), Is.False);
    }

    [Test]
    public async Task Apply_RejectsPlanContentChangedAfterReview()
    {
        ActionFitSdkProjectContext context = CreateProject();
        ActionFitSdkInstallPlan plan = ActionFitSdkInstallApi.Plan(CreateProfile(), new ActionFitSdkPlanRequest(), context);
        plan.SelectedModuleIds = new[] { "extras" };

        ActionFitSdkExecutionResult result = await ActionFitSdkInstallApi.ApplyAsync(plan, plan.PlanId);

        Assert.That(result.Success, Is.False);
        Assert.That(result.Code, Is.EqualTo("PREPARE_FAILED"));
        Assert.That(File.Exists(context.OwnershipPath), Is.False);
    }

    [Test]
    public void RecoverPending_RestoresWriteAheadArtifactBackupExplicitly()
    {
        ActionFitSdkProjectContext context = CreateProject();
        string originalManifest = File.ReadAllText(context.ManifestPath);
        string changedManifest = ActionFitPackageManifestUtility.SetDependency(originalManifest, "com.vendor.sdk", "1.2.3");
        File.WriteAllText(context.ManifestPath, changedManifest);

        const string transactionId = "recoveryfixture";
        string transactionDirectory = Path.Combine(context.TransactionRoot, transactionId);
        string backupPath = Path.Combine(transactionDirectory, "backups", "0000.backup");
        string targetPath = Path.Combine(context.ProjectRoot, "ActionFitSdkArtifacts", "vendor", "package.tgz");
        Directory.CreateDirectory(Path.GetDirectoryName(backupPath));
        File.WriteAllText(backupPath, "owned artifact");
        var journal = new ActionFitSdkTransactionJournal
        {
            SchemaVersion = 1,
            TransactionId = transactionId,
            ProjectRoot = context.ProjectRoot,
            ManifestPath = context.ManifestPath,
            OwnershipPath = context.OwnershipPath,
            OriginalManifest = originalManifest,
            UpdatedManifest = changedManifest,
            OwnershipOriginallyExisted = false,
            ArtifactEntries = new[]
            {
                new ActionFitSdkArtifactJournalEntry
                {
                    TargetPath = targetPath,
                    BackupPath = backupPath,
                    ExpectedSha256 = ActionFitSdkArtifactVerifier.ComputeSha256(backupPath),
                    Remove = true,
                    BackupIntended = true,
                    BackedUp = false,
                },
            },
        };
        Directory.CreateDirectory(context.TransactionRoot);
        File.WriteAllText(Path.Combine(context.TransactionRoot, transactionId + ".json"), JsonUtility.ToJson(journal, true));

        ActionFitSdkRecoveryResult[] results = ActionFitSdkInstallApi.RecoverPendingTransactions(context);

        Assert.That(results, Has.Length.EqualTo(1));
        Assert.That(results[0].Success, Is.True, results[0].Message);
        Assert.That(File.ReadAllText(context.ManifestPath), Is.EqualTo(originalManifest));
        Assert.That(File.ReadAllText(targetPath), Is.EqualTo("owned artifact"));
        Assert.That(ActionFitSdkInstallApi.InspectPendingTransactions(context), Is.Empty);
    }

    [Test]
    public void BridgeTemplate_RequiresPublicVisibilityAndWritesSourceOnlyContract()
    {
        ActionFitSdkInstallProfile profile = CreateProfile();
        var request = new ActionFitPackageCreateRequest
        {
            PackageId = profile.BridgePackageId,
            RepositoryVisibilitySpecified = true,
            RepositoryVisibility = ActionFitPackageRepositoryVisibility.Public,
        };
        ActionFitSdkBridgePackageTemplate.ValidateRequest(request, profile);

        string packageRoot = Path.Combine(_temporaryRoot, profile.BridgePackageId);
        Directory.CreateDirectory(packageRoot);
        File.WriteAllText(Path.Combine(packageRoot, "package.json"), "{\n  \"dependencies\": {}\n}\n");
        ActionFitSdkBridgePackageTemplate.WriteManagerDependency(packageRoot);
        ActionFitSdkBridgePackageTemplate.WriteBridgeFiles(packageRoot, profile.BridgePackageId, profile);

        Assert.That(
            ActionFitPackageManifestUtility.GetDependency(File.ReadAllText(Path.Combine(packageRoot, "package.json")), ActionFitSdkBridgePackageTemplate.ManagerPackageId),
            Is.EqualTo(UnityEditor.PackageManager.PackageInfo.FindForAssembly(typeof(ActionFitSdkBridgePackageTemplate).Assembly).version));
        Assert.That(File.Exists(Path.Combine(packageRoot, ActionFitSdkBridgePackageTemplate.ProfileRelativePath)), Is.True);
        Assert.That(File.ReadAllText(Path.Combine(packageRoot, ActionFitSdkBridgePackageTemplate.ThirdPartyNoticesFileName)), Does.Contain("contains no redistributed"));
        Assert.That(Directory.GetFiles(packageRoot, "*.dll", SearchOption.AllDirectories), Is.Empty);

        request.RepositoryVisibility = ActionFitPackageRepositoryVisibility.Private;
        Assert.Throws<InvalidOperationException>(() => ActionFitSdkBridgePackageTemplate.ValidateRequest(request, profile));
    }

    private ActionFitSdkProjectContext CreateProject(string manifest = null)
    {
        string projectRoot = Path.Combine(_temporaryRoot, "Project");
        Directory.CreateDirectory(Path.Combine(projectRoot, "Packages"));
        Directory.CreateDirectory(Path.Combine(projectRoot, "ProjectSettings"));
        Directory.CreateDirectory(Path.Combine(projectRoot, "UserSettings"));
        File.WriteAllText(Path.Combine(projectRoot, "Packages", "manifest.json"), manifest ??
            "{\n  \"dependencies\": {\n    \"com.unity.test-framework\": \"1.1.33\"\n  }\n}\n");
        return ActionFitSdkProjectContext.ForProjectRoot(projectRoot);
    }

    private static ActionFitSdkInstallProfile CreateProfile()
    {
        return new ActionFitSdkInstallProfile
        {
            ProfileId = "vendor.sdk",
            ProfileVersion = "1.0.0",
            Vendor = "Vendor",
            DisplayName = "Vendor SDK",
            BridgePackageId = "com.actionfit.vendor.sdk",
            MinimumUnityVersion = "6000.2",
            LicenseUrl = "https://example.com/license",
            SupportUrl = "https://example.com/support",
            SupportedPlatforms = new[] { "Android", "iOS" },
            AllowedDomains = new[] { "example.com" },
            Sources = new[]
            {
                new ActionFitSdkSourceDefinition
                {
                    Id = "registry",
                    Kind = "registry",
                    Url = "https://registry.example.com",
                    ImmutableVersion = "1.2.3",
                    PackageId = "com.vendor.sdk",
                },
            },
            Modules = new[]
            {
                new ActionFitSdkModuleDefinition { Id = "core", DisplayName = "Core", Required = true, DefaultSelected = true },
                new ActionFitSdkModuleDefinition { Id = "extras", DisplayName = "Extras", Requires = new[] { "core" } },
            },
            Dependencies = new[]
            {
                new ActionFitSdkDependencyDefinition { PackageId = "com.vendor.sdk", SourceId = "registry", ModuleId = "core", Order = 0 },
            },
            ScopedRegistries = new[]
            {
                new ActionFitSdkScopedRegistryDefinition
                {
                    Name = "Vendor",
                    Url = "https://registry.example.com",
                    ModuleId = "core",
                    Scopes = new[] { "com.vendor" },
                },
            },
        };
    }

    private static ActionFitSdkInstallProfile CreateGitProfile(string gitSubpath)
    {
        ActionFitSdkInstallProfile profile = CreateProfile();
        profile.ScopedRegistries = Array.Empty<ActionFitSdkScopedRegistryDefinition>();
        profile.Sources = new[]
        {
            new ActionFitSdkSourceDefinition
            {
                Id = "git",
                Kind = "git",
                Url = "https://example.com/vendor/sdk.git",
                ImmutableRevision = "0123456789abcdef0123456789abcdef01234567",
                GitSubpath = gitSubpath,
                PackageId = "com.vendor.sdk",
            },
        };
        profile.Dependencies[0].SourceId = "git";
        return profile;
    }
}
#endif
