#if UNITY_EDITOR
using System;
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
