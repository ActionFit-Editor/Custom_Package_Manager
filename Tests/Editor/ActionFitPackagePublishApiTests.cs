#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Text;
using NUnit.Framework;

public sealed class ActionFitPackagePublishApiTests
{
    [Test]
    public void ValidateCreateRequest_RejectsMissingExplicitRepositoryVisibility()
    {
        ActionFitPackageCreateRequest request = CreateValidRequest();
        request.RepositoryVisibilitySpecified = false;

        InvalidOperationException exception = Assert.Throws<InvalidOperationException>(
            () => ActionFitPackageInfoUtility.ValidateCreateRequest(request));

        Assert.That(exception.Message, Does.Contain("explicitly selected"));
    }

    [TestCase(ActionFitPackageRepositoryVisibility.Public)]
    [TestCase(ActionFitPackageRepositoryVisibility.Private)]
    public void ValidateCreateRequest_AcceptsExplicitRepositoryVisibility(ActionFitPackageRepositoryVisibility visibility)
    {
        ActionFitPackageCreateRequest request = CreateValidRequest();
        request.RepositoryVisibility = visibility;
        request.RepositoryVisibilitySpecified = true;

        Assert.DoesNotThrow(() => ActionFitPackageInfoUtility.ValidateCreateRequest(request));
    }

    [Test]
    public void ComputeBulkPlanId_IsStableRegardlessOfInputOrder()
    {
        var first = new ActionFitPackagePublishPlan { PackageId = "com.actionfit.alpha", Version = "1.0.1", PlanId = "AAA" };
        var second = new ActionFitPackagePublishPlan { PackageId = "com.actionfit.beta", Version = "2.0.3", PlanId = "BBB" };

        string forward = ActionFitPackageBulkPublishApi.ComputePlanId(new[] { first, second });
        string reverse = ActionFitPackageBulkPublishApi.ComputePlanId(new[] { second, first });

        Assert.That(forward, Is.EqualTo(reverse));
        Assert.That(forward, Has.Length.EqualTo(20));
    }

    [TestCase(0, 0)]
    [TestCase(1, 1)]
    [TestCase(4, 4)]
    [TestCase(5, 4)]
    [TestCase(20, 4)]
    public void GetWorkerCount_BoundsPublishAndRemotePreflightConcurrency(int requestCount, int expected)
    {
        Assert.That(ActionFitPackageBulkPublishApi.GetWorkerCount(requestCount), Is.EqualTo(expected));
    }

    [Test]
    public void ApprovedBulkPlanMatches_RequiresUntamperedPlanAndApproval()
    {
        var package = new ActionFitPackagePublishPlan
        {
            Success = true,
            ReadyToPublish = true,
            PackageId = "com.actionfit.alpha",
            Version = "1.0.1",
            PlanId = "PACKAGE_PLAN",
        };
        string planId = ActionFitPackageBulkPublishApi.ComputePlanId(new[] { package });
        var plan = new ActionFitPackageBulkPublishPlan
        {
            Success = true,
            ReadyToPublish = true,
            PackageIds = new[] { package.PackageId },
            PublishPackageIds = new[] { package.PackageId },
            Packages = new[] { package },
            PlanId = planId,
            RequiredApprovalText = $"PUBLISH ALL 1 PACKAGES PLAN {planId}",
        };
        var request = new ActionFitPackageBulkPublishExecuteRequest
        {
            PackageIds = plan.PackageIds,
            ExpectedPlanId = plan.PlanId,
            ApprovalText = plan.RequiredApprovalText,
            ApprovedPlan = plan,
        };

        Assert.That(ActionFitPackageBulkPublishApi.ApprovedPlanMatches(request), Is.True);

        package.PlanId = "TAMPERED";
        Assert.That(ActionFitPackageBulkPublishApi.ApprovedPlanMatches(request), Is.False);
    }

    [Test]
    public void CompleteBulkRemotePreflight_MatchingTagBecomesCatalogRecoveryCandidate()
    {
        var plan = new ActionFitPackagePublishPlan
        {
            Success = true,
            PackageId = "com.actionfit.alpha",
            Version = "1.0.1",
            CatalogLatestVersion = "1.0.0",
            RepositoryVisibility = "Public",
            GitHubOrganization = "ActionFit",
            RepositoryName = "Alpha",
        };
        bool verifierCalled = false;

        ActionFitPackagePublishPlan result = ActionFitPackageBulkPublishApi.CompleteBulkRemotePreflight(
            plan,
            new ActionFitPackagePublisher.RemoteState(true, true, false, "main"),
            null,
            _ =>
            {
                verifierCalled = true;
                return new ActionFitPackageCatalogRecoveryVerification(
                    true,
                    true,
                    "0123456789012345678901234567890123456789",
                    "matched");
            });

        Assert.That(verifierCalled, Is.True);
        Assert.That(result.Success, Is.True);
        Assert.That(result.ReadyToPublish, Is.False);
        Assert.That(result.ReadyToRecoverCatalog, Is.True);
        Assert.That(result.Code, Is.EqualTo("READY_TO_RECOVER_CATALOG"));
    }

    [Test]
    public void CompleteBulkRemotePreflight_ChangedTagContentBlocksAndSuggestsPatch()
    {
        var plan = new ActionFitPackagePublishPlan
        {
            Success = true,
            PackageId = "com.actionfit.alpha",
            Version = "1.0.1",
            RepositoryVisibility = "Public",
        };

        ActionFitPackagePublishPlan result = ActionFitPackageBulkPublishApi.CompleteBulkRemotePreflight(
            plan,
            new ActionFitPackagePublisher.RemoteState(true, true, false, "main"),
            null,
            _ => new ActionFitPackageCatalogRecoveryVerification(
                true,
                false,
                "0123456789012345678901234567890123456789",
                "Package content differs at Runtime/Changed.cs."));

        Assert.That(result.Success, Is.False);
        Assert.That(result.Code, Is.EqualTo("REMOTE_TAG_CONTENT_MISMATCH"));
        Assert.That(result.SuggestedNextVersion, Is.EqualTo("1.0.2"));
        Assert.That(result.Message, Does.Contain("Bump package.json to 1.0.2"));
    }

    [Test]
    public void CompleteBulkRemotePreflight_VisibilityMismatchBlocksWithoutTagCheckout()
    {
        var plan = new ActionFitPackagePublishPlan
        {
            Success = true,
            PackageId = "com.actionfit.alpha",
            Version = "1.0.1",
            RepositoryVisibility = "Private",
        };
        bool verifierCalled = false;

        ActionFitPackagePublishPlan result = ActionFitPackageBulkPublishApi.CompleteBulkRemotePreflight(
            plan,
            new ActionFitPackagePublisher.RemoteState(true, true, false, "main"),
            null,
            _ =>
            {
                verifierCalled = true;
                return new ActionFitPackageCatalogRecoveryVerification(true, true, "commit", "matched");
            });

        Assert.That(verifierCalled, Is.False);
        Assert.That(result.Success, Is.False);
        Assert.That(result.Code, Is.EqualTo("RECOVERY_REPOSITORY_VISIBILITY_MISMATCH"));
    }

    [Test]
    public void CompleteBulkRemotePreflight_PreRegisteredCatalogFailureNeverBecomesRecovery()
    {
        var plan = new ActionFitPackagePublishPlan
        {
            Success = false,
            Code = "VERSION_ALREADY_IN_CATALOG",
            PackageId = "com.actionfit.alpha",
            Version = "1.0.1",
            CatalogContainsVersion = true,
            RepositoryVisibility = "Public",
        };
        bool verifierCalled = false;

        ActionFitPackagePublishPlan result = ActionFitPackageBulkPublishApi.CompleteBulkRemotePreflight(
            plan,
            new ActionFitPackagePublisher.RemoteState(true, true, false, "main"),
            null,
            _ =>
            {
                verifierCalled = true;
                return new ActionFitPackageCatalogRecoveryVerification(true, true, "commit", "matched");
            });

        Assert.That(result, Is.SameAs(plan));
        Assert.That(verifierCalled, Is.False);
        Assert.That(result.ReadyToRecoverCatalog, Is.False);
        Assert.That(result.Code, Is.EqualTo("VERSION_ALREADY_IN_CATALOG"));
    }

    [Test]
    public void ApprovedBulkPlanMatches_MixedPlanRequiresSeparateRecoveryApproval()
    {
        var publish = new ActionFitPackagePublishPlan
        {
            Success = true,
            ReadyToPublish = true,
            PackageId = "com.actionfit.alpha",
            Version = "1.0.2",
            PlanId = "PUBLISH_PLAN",
        };
        var recovery = new ActionFitPackagePublishPlan
        {
            Success = true,
            ReadyToRecoverCatalog = true,
            PackageId = "com.actionfit.beta",
            Version = "2.0.1",
            PlanId = "RECOVERY_PLAN",
        };
        string planId = ActionFitPackageBulkPublishApi.ComputePlanId(new[] { publish, recovery });
        var plan = new ActionFitPackageBulkPublishPlan
        {
            Success = true,
            ReadyToPublish = true,
            PackageIds = new[] { publish.PackageId, recovery.PackageId },
            PublishPackageIds = new[] { publish.PackageId },
            CatalogRecoveryPackageIds = new[] { recovery.PackageId },
            Packages = new[] { publish, recovery },
            PlanId = planId,
            RequiredApprovalText = $"PUBLISH ALL 1 PACKAGES PLAN {planId}",
            RequiredCatalogRecoveryApprovalText = $"RECOVER CATALOG {recovery.PackageId} PLAN {planId}",
        };
        var request = new ActionFitPackageBulkPublishExecuteRequest
        {
            PackageIds = plan.PackageIds,
            ExpectedPlanId = plan.PlanId,
            ApprovalText = plan.RequiredApprovalText,
            CatalogRecoveryApprovalText = plan.RequiredCatalogRecoveryApprovalText,
            ApprovedPlan = plan,
        };

        Assert.That(ActionFitPackageBulkPublishApi.ApprovedPlanMatches(request), Is.True);

        request.CatalogRecoveryApprovalText = "OTHER";
        Assert.That(ActionFitPackageBulkPublishApi.ApprovedPlanMatches(request), Is.False);
    }

    [Test]
    public void CreatePackageResults_MixedPlanNeverMarksRecoveryAsRepositoryPublished()
    {
        var publishPlan = new ActionFitPackagePublishPlan
        {
            Success = true,
            ReadyToPublish = true,
            PackageId = "com.actionfit.alpha",
            Version = "1.0.2",
        };
        var recoveryPlan = new ActionFitPackagePublishPlan
        {
            Success = true,
            ReadyToRecoverCatalog = true,
            PackageId = "com.actionfit.beta",
            Version = "2.0.1",
        };
        ActionFitPackagePublisher.PublishRequest publishRequest = CreatePublishRequest(publishPlan);
        ActionFitPackagePublisher.PublishRequest recoveryRequest = CreatePublishRequest(recoveryPlan);
        var requests = new Dictionary<string, ActionFitPackagePublisher.PublishRequest>
        {
            [publishPlan.PackageId] = publishRequest,
            [recoveryPlan.PackageId] = recoveryRequest,
        };

        ActionFitPackagePublisher.PublishRequest[] repositoryRequests =
            ActionFitPackageBulkPublishApi.SelectRepositoryPublishRequests(
                new[] { publishPlan, recoveryPlan },
                requests);
        ActionFitPackagePublisher.PublishRequest[] recoveryRequests =
            ActionFitPackageBulkPublishApi.SelectCatalogRecoveryRequests(
                new[] { publishPlan, recoveryPlan },
                requests);

        ActionFitPackageBulkPublishPackageResult[] results = ActionFitPackageBulkPublishApi.CreatePackageResults(
            new[] { publishPlan, recoveryPlan },
            requests,
            new[] { ActionFitPackagePublisher.PublishResult.Succeeded(publishRequest, "published") });

        Assert.That(repositoryRequests.Select(item => item.PackageId), Is.EqualTo(new[] { publishPlan.PackageId }));
        Assert.That(recoveryRequests.Select(item => item.PackageId), Is.EqualTo(new[] { recoveryPlan.PackageId }));
        Assert.That(results, Has.Length.EqualTo(2));
        Assert.That(results[0].RepositoryPublished, Is.True);
        Assert.That(results[1].RepositoryPublished, Is.False);
        Assert.That(results[1].CatalogRecovered, Is.False);
        Assert.That(results[1].Message, Does.Contain("waiting for catalog append"));
    }

    [Test]
    public void ApprovedSinglePlanMatches_RequiresExactIdentityAndApproval()
    {
        var plan = new ActionFitPackagePublishPlan
        {
            Success = true,
            ReadyToPublish = true,
            PackageId = "com.actionfit.alpha",
            PlanId = "PLAN",
            RequiredApprovalText = "APPROVE",
        };

        Assert.That(
            ActionFitPackagePublishApi.ApprovedPlanMatches(plan, plan.PackageId, plan.PlanId, plan.RequiredApprovalText),
            Is.True);
        Assert.That(
            ActionFitPackagePublishApi.ApprovedPlanMatches(plan, plan.PackageId, "OTHER", plan.RequiredApprovalText),
            Is.False);
    }

    [Test]
    public void MigrationApprovalMatches_RequiresSeparateExactApproval()
    {
        var plan = new ActionFitPackagePublishPlan
        {
            RepositoryMigrationRequired = true,
            RequiredMigrationApprovalText = "MIGRATE EXACTLY",
        };

        Assert.That(ActionFitPackagePublishApi.MigrationApprovalMatches(plan, false, "MIGRATE EXACTLY"), Is.False);
        Assert.That(ActionFitPackagePublishApi.MigrationApprovalMatches(plan, true, "OTHER"), Is.False);
        Assert.That(ActionFitPackagePublishApi.MigrationApprovalMatches(plan, true, "MIGRATE EXACTLY"), Is.True);
    }

    [Test]
    public void MigrationApprovalsMatch_RequiresExactBulkPackageSetAndText()
    {
        var plan = new ActionFitPackageBulkPublishPlan
        {
            RepositoryMigrationPackageIds = new[] { "com.actionfit.alpha", "com.actionfit.beta" },
            RequiredMigrationApprovalText = "MIGRATE BULK",
        };

        Assert.That(
            ActionFitPackageBulkPublishApi.MigrationApprovalsMatch(
                plan,
                new[] { "com.actionfit.beta", "com.actionfit.alpha" },
                "MIGRATE BULK"),
            Is.True);
        Assert.That(
            ActionFitPackageBulkPublishApi.MigrationApprovalsMatch(
                plan,
                new[] { "com.actionfit.alpha" },
                "MIGRATE BULK"),
            Is.False);
        Assert.That(
            ActionFitPackageBulkPublishApi.MigrationApprovalsMatch(
                plan,
                plan.RepositoryMigrationPackageIds,
                "OTHER"),
            Is.False);
    }

    [TestCase("https://github.com/ActionFit/PublicRepo.git", "ActionFit", "PublicRepo")]
    [TestCase("https://github.com/ActionFit/PublicRepo", "ActionFit", "PublicRepo")]
    [TestCase("git@github.com:ActionFit/PublicRepo.git", "ActionFit", "PublicRepo")]
    public void TryParseRepositoryUrl_AcceptsSupportedGitHubFormats(
        string url,
        string expectedOrganization,
        string expectedName)
    {
        Assert.That(
            ActionFitPackageRepositoryMigration.TryParseRepositoryUrl(
                url,
                out ActionFitPackageRepositoryMigration.RepositoryIdentity identity),
            Is.True);
        Assert.That(identity.Organization, Is.EqualTo(expectedOrganization));
        Assert.That(identity.Name, Is.EqualTo(expectedName));
    }

    [TestCase("https://gitlab.com/ActionFit/PublicRepo.git")]
    [TestCase("https://github.com/ActionFit")]
    [TestCase("")]
    public void TryParseRepositoryUrl_RejectsUnsupportedUrls(string url)
    {
        Assert.That(
            ActionFitPackageRepositoryMigration.TryParseRepositoryUrl(url, out _),
            Is.False);
    }

    [Test]
    public void RefComparison_AllowsIdempotentRetryAndRejectsConflicts()
    {
        var source = new Dictionary<string, string>
        {
            ["refs/heads/main"] = "aaa",
            ["refs/heads/release"] = "bbb",
            ["refs/tags/1.0.0"] = "ccc",
        };
        var partialTarget = new Dictionary<string, string>
        {
            ["refs/heads/main"] = "aaa",
        };

        Assert.That(
            ActionFitPackageRepositoryMigration.FindMissingRefs(source, partialTarget),
            Is.EqualTo(new[] { "refs/heads/release", "refs/tags/1.0.0" }));
        Assert.That(
            ActionFitPackageRepositoryMigration.FindConflictingRefs(source, partialTarget),
            Is.Empty);

        partialTarget["refs/heads/release"] = "different";
        Assert.That(
            ActionFitPackageRepositoryMigration.FindConflictingRefs(source, partialTarget),
            Is.EqualTo(new[] { "refs/heads/release" }));
    }

    [Test]
    public void ValidateDocumentation_RequiresTargetUrlInReadmeAndAiGuide()
    {
        string operationRoot = Path.Combine(
            ActionFitPackagePaths.ProjectRoot,
            "Temp",
            "ActionFitPackageManagerTests",
            Guid.NewGuid().ToString("N"),
            "repository-docs");
        Directory.CreateDirectory(operationRoot);
        var target = new ActionFitPackageRepositoryMigration.RepositoryIdentity("PrivateOrg", "PrivateRepo");

        try
        {
            File.WriteAllText(Path.Combine(operationRoot, "README.md"), $"Install from {target.Url}#1.0.0");
            File.WriteAllText(Path.Combine(operationRoot, "AI_GUIDE.md"), "Old repository only");
            Assert.That(
                ActionFitPackageRepositoryMigration.ValidateDocumentation(operationRoot, target, out string failure),
                Is.False,
                failure);

            File.WriteAllText(Path.Combine(operationRoot, "AI_GUIDE.md"), $"Repository: {target.Url}");
            Assert.That(
                ActionFitPackageRepositoryMigration.ValidateDocumentation(operationRoot, target, out string success),
                Is.True,
                success);
        }
        finally
        {
            string parent = Directory.GetParent(operationRoot)?.FullName;
            if (!string.IsNullOrWhiteSpace(parent) && Directory.Exists(parent))
                DeleteDirectory(parent);
        }
    }

    [Test]
    public void CompleteRemotePreflight_BlocksVisibilityMismatchWithoutChangingRepository()
    {
        var plan = new ActionFitPackagePublishPlan
        {
            Success = true,
            PackageId = "com.actionfit.alpha",
            Version = "1.0.1",
            RepositoryVisibility = "Private",
        };

        ActionFitPackagePublishPlan result = ActionFitPackagePublishApi.CompleteRemotePreflight(
            plan,
            new ActionFitPackagePublisher.RemoteState(true, false, false, "main"));

        Assert.That(result.Success, Is.False);
        Assert.That(result.Code, Is.EqualTo("TARGET_REPOSITORY_VISIBILITY_MISMATCH"));
    }

    [Test]
    public void CompleteRemotePreflight_BlocksPrivateTargetWhenPublicWasSelected()
    {
        var plan = new ActionFitPackagePublishPlan
        {
            Success = true,
            PackageId = "com.actionfit.alpha",
            Version = "1.0.1",
            RepositoryVisibility = "Public",
        };

        ActionFitPackagePublishPlan result = ActionFitPackagePublishApi.CompleteRemotePreflight(
            plan,
            new ActionFitPackagePublisher.RemoteState(true, false, true, "main"));

        Assert.That(result.Success, Is.False);
        Assert.That(result.Code, Is.EqualTo("TARGET_REPOSITORY_VISIBILITY_MISMATCH"));
        Assert.That(result.Message, Does.Contain("selected Public"));
    }

    [Test]
    public void CompleteRemotePreflight_BlocksSamePublicSourceInsteadOfChangingVisibility()
    {
        var plan = new ActionFitPackagePublishPlan
        {
            Success = true,
            PackageId = "com.actionfit.alpha",
            Version = "1.0.1",
            RepositoryVisibility = "Private",
            GitHubOrganization = "SharedOrg",
            RepositoryName = "AlphaRepo",
        };
        var request = new ActionFitPackagePublisher.PublishRequest(
            "~/upm-publish",
            "Packages/com.actionfit.alpha",
            "AlphaRepo",
            "Private",
            "SharedOrg",
            "token-not-used",
            true,
            plan.PackageId,
            plan.Version,
            null)
        {
            SourceRepositoryUrl = "https://github.com/SharedOrg/AlphaRepo.git",
        };

        ActionFitPackagePublishPlan result = ActionFitPackagePublishApi.CompleteRemotePreflight(
            plan,
            new ActionFitPackagePublisher.RemoteState(true, false, false, "main"),
            request);

        Assert.That(result.Success, Is.False);
        Assert.That(result.Code, Is.EqualTo("SOURCE_REPOSITORY_VISIBILITY_CHANGE_BLOCKED"));
        Assert.That(result.Message, Does.Contain("different Private organization or repository name"));
    }

    [Test]
    public void CompleteRemotePreflight_BlocksSamePrivateSourceInsteadOfChangingVisibility()
    {
        var plan = new ActionFitPackagePublishPlan
        {
            Success = true,
            PackageId = "com.actionfit.alpha",
            Version = "1.0.1",
            RepositoryVisibility = "Public",
            GitHubOrganization = "SharedOrg",
            RepositoryName = "AlphaRepo",
        };
        var request = new ActionFitPackagePublisher.PublishRequest(
            "~/upm-publish",
            "Packages/com.actionfit.alpha",
            "AlphaRepo",
            "Public",
            "SharedOrg",
            "token-not-used",
            false,
            plan.PackageId,
            plan.Version,
            null)
        {
            SourceRepositoryUrl = "https://github.com/SharedOrg/AlphaRepo.git",
        };

        ActionFitPackagePublishPlan result = ActionFitPackagePublishApi.CompleteRemotePreflight(
            plan,
            new ActionFitPackagePublisher.RemoteState(true, false, true, "main"),
            request);

        Assert.That(result.Success, Is.False);
        Assert.That(result.Code, Is.EqualTo("SOURCE_REPOSITORY_VISIBILITY_CHANGE_BLOCKED"));
        Assert.That(result.Message, Does.Contain("different Public organization or repository name"));
    }

    [Test]
    public void RepositoryRetirementPrepare_KeepRequiresNoExternalState()
    {
        ActionFitPackageRepositoryRetirementPlan plan = ActionFitPackageRepositoryRetirementApi.Prepare(
            new ActionFitPackageRepositoryRetirementPrepareRequest
            {
                PackageId = "com.actionfit.alpha",
                Version = "1.0.1",
                SourceRepositoryUrl = "https://github.com/PrivateOrg/AlphaRepo.git",
                TargetRepositoryUrl = "https://github.com/ActionFit/AlphaRepo.git",
                Mode = ActionFitPackageRepositoryRetirementMode.Keep,
            });

        Assert.That(plan.Success, Is.True);
        Assert.That(plan.ReadyToExecute, Is.False);
        Assert.That(plan.Code, Is.EqualTo("SOURCE_REPOSITORY_KEPT"));
    }

    [Test]
    public void RepositoryRetirementApprovedPlanMatches_RequiresExactBoundFields()
    {
        var plan = new ActionFitPackageRepositoryRetirementPlan
        {
            Success = true,
            ReadyToExecute = true,
            PackageId = "com.actionfit.alpha",
            Version = "1.0.1",
            SourceRepositoryUrl = "https://github.com/PrivateOrg/AlphaRepo.git",
            TargetRepositoryUrl = "https://github.com/ActionFit/AlphaRepo.git",
            Mode = ActionFitPackageRepositoryRetirementMode.Archive,
            PlanId = "EXACT_PLAN",
            RequiredApprovalText = "ARCHIVE SOURCE PrivateOrg/AlphaRepo FOR com.actionfit.alpha@1.0.1 PLAN EXACT_PLAN",
        };

        Assert.That(
            ActionFitPackageRepositoryRetirementApi.ApprovedPlanMatches(
                plan,
                plan.PackageId,
                plan.Version,
                plan.SourceRepositoryUrl,
                plan.TargetRepositoryUrl,
                plan.Mode,
                plan.PlanId,
                plan.RequiredApprovalText),
            Is.True);
        Assert.That(
            ActionFitPackageRepositoryRetirementApi.ApprovedPlanMatches(
                plan,
                plan.PackageId,
                plan.Version,
                plan.SourceRepositoryUrl,
                plan.TargetRepositoryUrl,
                ActionFitPackageRepositoryRetirementMode.Delete,
                plan.PlanId,
                plan.RequiredApprovalText),
            Is.False);
    }

    [Test]
    public void RepositoryRetirementBatchApprovedPlanMatches_RejectsChangedApproval()
    {
        var plan = new ActionFitPackageRepositoryRetirementBatchPlan
        {
            Success = true,
            ReadyToExecute = true,
            PlanId = "BATCH_PLAN",
            RequiredApprovalText = "RETIRE SOURCES com.actionfit.alpha:Archive PLAN BATCH_PLAN",
        };

        Assert.That(
            ActionFitPackageRepositoryRetirementApi.BatchApprovedPlanMatches(
                plan,
                plan.PlanId,
                plan.RequiredApprovalText),
            Is.True);
        Assert.That(
            ActionFitPackageRepositoryRetirementApi.BatchApprovedPlanMatches(
                plan,
                plan.PlanId,
                plan.RequiredApprovalText + " changed"),
            Is.False);
    }

    [Test]
    public void CompleteRemotePreflight_SameCatalogTargetNeedsNoMigration()
    {
        var plan = new ActionFitPackagePublishPlan
        {
            Success = true,
            PackageId = "com.actionfit.alpha",
            Version = "1.0.1",
            ContentHash = "content",
            RepositoryVisibility = "Private",
            GitHubOrganization = "PrivateOrg",
            RepositoryName = "AlphaRepo",
            RepositoryUrl = "https://github.com/PrivateOrg/AlphaRepo.git",
        };
        var request = new ActionFitPackagePublisher.PublishRequest(
            "~/upm-publish",
            "Packages/com.actionfit.alpha",
            "AlphaRepo",
            "Private",
            "PrivateOrg",
            "token-not-used",
            true,
            plan.PackageId,
            plan.Version,
            null)
        {
            SourceRepositoryUrl = "https://github.com/privateorg/alpharepo.git",
        };

        ActionFitPackagePublishPlan result = ActionFitPackagePublishApi.CompleteRemotePreflight(
            plan,
            new ActionFitPackagePublisher.RemoteState(true, false, true, "main"),
            request);

        Assert.That(result.Success, Is.True);
        Assert.That(result.ReadyToPublish, Is.True);
        Assert.That(result.RepositoryMigrationRequired, Is.False);
        Assert.That(result.RequiredMigrationApprovalText, Is.Empty);
    }

    [Test]
    public void CompleteCatalogRecoveryPreflight_AllowsMatchingImmutableTagWithoutRepositoryActions()
    {
        var plan = new ActionFitPackagePublishPlan
        {
            Success = true,
            PackageId = "com.actionfit.alpha",
            Version = "1.0.1",
            CatalogLatestVersion = "1.0.0",
            ContentHash = "content",
            RepositoryVisibility = "Public",
            GitHubOrganization = "ActionFit",
            RepositoryName = "Alpha",
            RepositoryUrl = "https://github.com/ActionFit/Alpha.git",
        };

        ActionFitPackagePublishPlan result = ActionFitPackagePublishApi.CompleteCatalogRecoveryPreflight(
            plan,
            new ActionFitPackagePublisher.RemoteState(true, true, false, "main"),
            true,
            "0123456789012345678901234567890123456789",
            "matched");

        Assert.That(result.Success, Is.True);
        Assert.That(result.ReadyToPublish, Is.False);
        Assert.That(result.ReadyToRecoverCatalog, Is.True);
        Assert.That(result.Code, Is.EqualTo("READY_TO_RECOVER_CATALOG"));
        Assert.That(result.RequiredCatalogRecoveryApprovalText, Does.StartWith("RECOVER CATALOG com.actionfit.alpha@1.0.1"));
        Assert.That(result.PlannedActions, Has.None.Contains("Push"));
    }

    [Test]
    public void CompleteCatalogRecoveryPreflight_BlocksChangedContentAndSuggestsNextPatch()
    {
        var plan = new ActionFitPackagePublishPlan
        {
            Success = true,
            PackageId = "com.actionfit.alpha",
            Version = "1.0.1",
            RepositoryVisibility = "Public",
        };

        ActionFitPackagePublishPlan result = ActionFitPackagePublishApi.CompleteCatalogRecoveryPreflight(
            plan,
            new ActionFitPackagePublisher.RemoteState(true, true, false, "main"),
            false,
            "0123456789012345678901234567890123456789",
            "Package content differs at Runtime/Changed.cs.");

        Assert.That(result.Success, Is.False);
        Assert.That(result.Code, Is.EqualTo("REMOTE_TAG_CONTENT_MISMATCH"));
        Assert.That(result.SuggestedNextVersion, Is.EqualTo("1.0.2"));
    }

    [Test]
    public void CatalogRecoveryApprovedPlanMatches_RequiresRecoveryPlanAndExactApproval()
    {
        var plan = new ActionFitPackagePublishPlan
        {
            Success = true,
            ReadyToRecoverCatalog = true,
            PackageId = "com.actionfit.alpha",
            PlanId = "RECOVERY_PLAN",
            RequiredCatalogRecoveryApprovalText = "RECOVER EXACTLY",
        };

        Assert.That(
            ActionFitPackagePublishApi.CatalogRecoveryApprovedPlanMatches(
                plan,
                plan.PackageId,
                plan.PlanId,
                plan.RequiredCatalogRecoveryApprovalText),
            Is.True);
        Assert.That(
            ActionFitPackagePublishApi.CatalogRecoveryApprovedPlanMatches(
                plan,
                plan.PackageId,
                plan.PlanId,
                "OTHER"),
            Is.False);
    }

    [Test]
    public void CatalogRecoveryVerifier_NormalizesFingerprintJsonAndUnityYamlButRejectsCodeChanges()
    {
        string operationRoot = Path.Combine(
            ActionFitPackagePaths.ProjectRoot,
            "Temp",
            "ActionFitPackageManagerTests",
            Guid.NewGuid().ToString("N"),
            "catalog-recovery-compare");
        string localRoot = Path.Combine(operationRoot, "local");
        string tagRoot = Path.Combine(operationRoot, "tag");
        string packageInfoPath = Path.Combine("Editor", "PackageInfo", "ActionFitPackageInfo_SO.asset");

        try
        {
            Directory.CreateDirectory(Path.Combine(localRoot, "Runtime"));
            Directory.CreateDirectory(Path.Combine(tagRoot, "Runtime"));
            Directory.CreateDirectory(Path.Combine(localRoot, "Editor", "PackageInfo"));
            Directory.CreateDirectory(Path.Combine(tagRoot, "Editor", "PackageInfo"));
            File.WriteAllText(
                Path.Combine(localRoot, "package.json"),
                "{\n  \"name\": \"com.actionfit.alpha\",\n  \"version\": \"1.0.1\",\n  \"_fingerprint\": \"012345\"\n}");
            File.WriteAllText(
                Path.Combine(tagRoot, "package.json"),
                "{ \"name\": \"com.actionfit.alpha\", \"version\": \"1.0.1\" }");
            File.WriteAllText(Path.Combine(localRoot, "Runtime", "Same.cs"), "public class Same {}\n");
            File.WriteAllText(Path.Combine(tagRoot, "Runtime", "Same.cs"), "public class Same {}\n");
            File.WriteAllText(
                Path.Combine(localRoot, packageInfoPath),
                "MonoBehaviour:\n  m_Name: ActionFitPackageInfo_SO\n  _packageId: com.actionfit.alpha\n  _releaseNote: \"\\uC548\\uC804\n    \\uBCF5\\uAD6C\"");
            File.WriteAllText(
                Path.Combine(tagRoot, packageInfoPath),
                "MonoBehaviour:\n  m_Name: ActionFitPackageInfo_SO\n  _packageId: com.actionfit.alpha\n  _releaseNote: \"안전 복구\"");

            Assert.That(
                ActionFitPackageCatalogRecoveryVerifier.AreEquivalent(localRoot, tagRoot, out string matched),
                Is.True,
                matched);

            File.WriteAllText(Path.Combine(localRoot, "Runtime", "Same.cs"), "public class Changed {}\n");
            Assert.That(
                ActionFitPackageCatalogRecoveryVerifier.AreEquivalent(localRoot, tagRoot, out string changed),
                Is.False);
            Assert.That(changed, Does.Contain("Runtime/Same.cs"));
        }
        finally
        {
            if (Directory.Exists(operationRoot))
                DeleteDirectory(operationRoot);
        }
    }

    [Test]
    public void ManagerConsole_OrdersCreateThenPublishChangedThenAddAgentSkill()
    {
        string path = Path.Combine(
            ActionFitPackagePaths.ProjectRoot,
            "Packages/com.actionfit.custompackagemanager/Editor/Scripts/ActionFitPackageManagerConsoleWindow.cs");
        string source = File.ReadAllText(path);

        int create = source.IndexOf("1. Create Package", StringComparison.Ordinal);
        int publish = source.IndexOf("2. Publish Changed", StringComparison.Ordinal);
        int skill = source.IndexOf("Add Agent Skill", StringComparison.Ordinal);
        Assert.That(create, Is.GreaterThanOrEqualTo(0));
        Assert.That(publish, Is.GreaterThan(create));
        Assert.That(skill, Is.GreaterThan(publish));
    }

    [Test]
    public void ContractValidationReceipt_IsNotSerializedWithApprovedPlan()
    {
        var plan = new ActionFitPackagePublishPlan
        {
            Success = true,
            ReadyToPublish = true,
            ContractValidatedInProcess = true,
        };

        string json = UnityEngine.JsonUtility.ToJson(plan);
        ActionFitPackagePublishPlan deserialized =
            UnityEngine.JsonUtility.FromJson<ActionFitPackagePublishPlan>(json);

        Assert.That(deserialized.ContractValidatedInProcess, Is.False);
    }

    [TestCase("Catalog request timed out after 30 seconds.", false)]
    [TestCase("Catalog append was canceled.", false)]
    [TestCase("Catalog batch append did not return success.", true)]
    public void ShouldAttemptSerialFallback_SkipsAmbiguousTimeoutsAndCancellation(string message, bool expected)
    {
        Assert.That(ActionFitPackageBulkPublishApi.ShouldAttemptSerialFallback(message), Is.EqualTo(expected));
    }

    [Test]
    public void ConfigureHttpRequest_AppliesBoundedTimeouts()
    {
        var request = (HttpWebRequest)WebRequest.Create("https://example.invalid/");

        ActionFitPackagePublisher.ConfigureHttpRequest(request);

        Assert.That(request.Timeout, Is.EqualTo(ActionFitPackagePublisher.DefaultHttpTimeoutMilliseconds));
        Assert.That(request.ReadWriteTimeout, Is.EqualTo(ActionFitPackagePublisher.DefaultHttpTimeoutMilliseconds));
    }

    [TestCase(HttpStatusCode.NotFound, false, true)]
    [TestCase(HttpStatusCode.NotFound, true, true)]
    [TestCase(HttpStatusCode.Conflict, true, true)]
    [TestCase(HttpStatusCode.Conflict, false, false)]
    [TestCase(HttpStatusCode.Unauthorized, true, false)]
    public void IsMissingGitHubResourceStatus_OnlyAllowsConflictForTagChecks(
        HttpStatusCode statusCode,
        bool conflictMeansMissing,
        bool expected)
    {
        Assert.That(
            ActionFitPackagePublisher.IsMissingGitHubResourceStatus(statusCode, conflictMeansMissing),
            Is.EqualTo(expected));
    }

    [Test]
    [Timeout(30000)]
    public void RunGit_DrainsLargeLineEndingWarningOutputWithoutDeadlock()
    {
        string testRoot = Path.Combine(
            ActionFitPackagePaths.ProjectRoot,
            "Temp",
            "ActionFitPackageManagerTests",
            Guid.NewGuid().ToString("N"),
            "git-output-drain");
        Directory.CreateDirectory(testRoot);

        try
        {
            Assert.That(ActionFitPackagePublisher.RunGit(testRoot, "init", ""), Is.True);

            var utf8WithoutBom = new UTF8Encoding(false);
            for (int i = 0; i < 512; i++)
            {
                string path = Path.Combine(testRoot, $"line-ending-{i:D4}.txt");
                File.WriteAllText(path, "first line\nsecond line\n", utf8WithoutBom);
            }

            Assert.That(
                ActionFitPackagePublisher.RunGit(
                    testRoot,
                    "-c core.autocrlf=true -c core.safecrlf=warn add -A",
                    ""),
                Is.True);
            Assert.That(File.Exists(Path.Combine(testRoot, ".git", "index.lock")), Is.False);
        }
        finally
        {
            string operationRoot = Directory.GetParent(testRoot)?.FullName;
            if (!string.IsNullOrWhiteSpace(operationRoot) && Directory.Exists(operationRoot))
                DeleteDirectory(operationRoot);
        }
    }

    [Test]
    public void FindTopLevelActionFitPackageJsonPaths_IgnoresNestedFixturePackages()
    {
        string operationRoot = Path.Combine(
            ActionFitPackagePaths.ProjectRoot,
            "Temp",
            "ActionFitPackageManagerTests",
            Guid.NewGuid().ToString("N"));
        string packagesRoot = Path.Combine(operationRoot, "Packages");
        string realPackageJson = Path.Combine(packagesRoot, "com.actionfit.real", "package.json");
        string fixturePackageJson = Path.Combine(
            packagesRoot,
            "com.actionfit.real",
            "Tests",
            "Shell",
            "Fixtures~",
            "valid-package",
            "package.json");

        Directory.CreateDirectory(Path.GetDirectoryName(realPackageJson));
        Directory.CreateDirectory(Path.GetDirectoryName(fixturePackageJson));
        File.WriteAllText(realPackageJson, "{}");
        File.WriteAllText(fixturePackageJson, "{}");

        try
        {
            string[] results = ActionFitPackagePublishWindow.FindTopLevelActionFitPackageJsonPaths(packagesRoot);

            Assert.That(results, Is.EqualTo(new[] { realPackageJson }));
        }
        finally
        {
            if (Directory.Exists(operationRoot))
                DeleteDirectory(operationRoot);
        }
    }

    private static ActionFitPackagePublisher.PublishRequest CreatePublishRequest(ActionFitPackagePublishPlan plan)
    {
        return new ActionFitPackagePublisher.PublishRequest(
            "~/upm-publish",
            $"Packages/{plan.PackageId}",
            plan.PackageId.Replace("com.actionfit.", ""),
            "Public",
            "ActionFit",
            "token-not-used",
            false,
            plan.PackageId,
            plan.Version,
            null);
    }

    private static void DeleteDirectory(string path)
    {
        foreach (string file in Directory.GetFiles(path, "*", SearchOption.AllDirectories))
            File.SetAttributes(file, FileAttributes.Normal);
        foreach (string directory in Directory.GetDirectories(path, "*", SearchOption.AllDirectories))
            File.SetAttributes(directory, FileAttributes.Directory);
        File.SetAttributes(path, FileAttributes.Directory);
        Directory.Delete(path, true);
    }

    private static ActionFitPackageCreateRequest CreateValidRequest()
    {
        return new ActionFitPackageCreateRequest
        {
            PackageId = "com.actionfit.visibilitytest",
            DisplayName = "Visibility Test",
            RepoName = "Visibility_Test",
            RepositoryVisibility = ActionFitPackageRepositoryVisibility.Public,
            RepositoryVisibilitySpecified = true,
            Version = "1.0.0",
            UnityVersion = "6000.2",
            Description = "Test package",
            Owner = "ActionFit",
            Status = "verified",
            ReleaseNote = "Test",
        };
    }
}
#endif
