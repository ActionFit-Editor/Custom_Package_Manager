#if UNITY_EDITOR
using System;
using System.IO;
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
