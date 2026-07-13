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
