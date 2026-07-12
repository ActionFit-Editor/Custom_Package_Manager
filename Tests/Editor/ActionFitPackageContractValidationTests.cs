#if UNITY_EDITOR
using System;
using NUnit.Framework;

public sealed class ActionFitPackageContractValidationTests
{
    [TearDown]
    public void TearDown()
    {
        ActionFitPackageContractValidator.ValidatePackageOverride = null;
    }

    [Test]
    public void ValidatePackage_CurrentPackagePassesPackageOwnedCli()
    {
        ActionFitPackageContractValidationResult result =
            ActionFitPackageContractValidator.ValidatePackage("com.actionfit.custompackagemanager");

        Assert.That(result.Success, Is.True, result.Message);
        Assert.That(result.ExitCode, Is.EqualTo(0));
        Assert.That(result.Diagnostics, Is.Empty);
    }

    [Test]
    public void ValidatePackageContract_InvalidGeneratedPackageThrowsDiagnostic()
    {
        ActionFitPackageContractValidator.ValidatePackageOverride = _ =>
            ActionFitPackageContractValidationResult.Failed(
                "PACKAGE_CONTRACT_INVALID",
                "Package contract validation failed.",
                1,
                new ActionFitPackageContractDiagnostic
                {
                    Code = "PACKAGE_INFO_FIELD_MISMATCH",
                    Severity = "error",
                    Path = "Packages/com.actionfit.example/Editor/PackageInfo/ActionFitPackageInfo_SO.asset",
                    Line = 14,
                    Message = "PackageInfo package ID does not match package.json.",
                    SuggestedFix = "Align PackageInfo with package.json.",
                });

        InvalidOperationException exception = Assert.Throws<InvalidOperationException>(
            () => ActionFitPackageInfoUtility.ValidatePackageContract("com.actionfit.example"));

        Assert.That(exception.Message, Does.Contain("PACKAGE_CONTRACT_INVALID"));
        Assert.That(exception.Message, Does.Contain("PACKAGE_INFO_FIELD_MISMATCH"));
    }

    [Test]
    public void Prepare_InvalidContractStopsBeforeCatalogAndCredentialChecks()
    {
        var expectedDiagnostic = new ActionFitPackageContractDiagnostic
        {
            Code = "README_INSTALL_VERSION_MISMATCH",
            Severity = "error",
            Path = "Packages/com.actionfit.custompackagemanager/README.md",
            Line = 12,
            Message = "README install tag does not match package.json version.",
            SuggestedFix = "Update the README install tag.",
        };
        ActionFitPackageContractValidator.ValidatePackageOverride = _ =>
            ActionFitPackageContractValidationResult.Failed(
                "PACKAGE_CONTRACT_INVALID",
                "Package contract validation failed.",
                1,
                expectedDiagnostic);

        ActionFitPackagePublishPlan plan = ActionFitPackagePublishApi.Prepare(
            new ActionFitPackagePublishPrepareRequest
            {
                PackageId = "com.actionfit.custompackagemanager",
                RefreshCatalog = true,
                CheckRemoteState = true,
            });

        Assert.That(plan.Success, Is.False);
        Assert.That(plan.ReadyToPublish, Is.False);
        Assert.That(plan.Code, Is.EqualTo("PACKAGE_CONTRACT_INVALID"));
        Assert.That(plan.ContractDiagnostics, Has.Length.EqualTo(1));
        Assert.That(plan.ContractDiagnostics[0].Code, Is.EqualTo(expectedDiagnostic.Code));
        Assert.That(plan.ContractDiagnostics[0].SuggestedFix, Is.EqualTo(expectedDiagnostic.SuggestedFix));
    }
}
#endif
