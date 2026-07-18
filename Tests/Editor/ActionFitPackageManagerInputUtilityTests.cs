#if UNITY_EDITOR
using NUnit.Framework;

public sealed class ActionFitPackageManagerInputUtilityTests
{
    [TestCase("com.actionfit.collection", "collection", true)]
    [TestCase("com.actionfit.lava-rush.installer", "", true)]
    [TestCase("com.actionfit.regular", "package", false)]
    public void IsCollectionPackage_UsesCatalogTypeAndInstallerFallback(
        string packageId,
        string packageType,
        bool expected)
    {
        Assert.That(
            ActionFitPackageManagerInputUtility.IsCollectionPackage(packageId, packageType),
            Is.EqualTo(expected));
    }

    [TestCase("https://github.com/ActionFit/Test.git#1.2.3")]
    [TestCase("https://github.com/ActionFit/MonoRepo.git?path=/Packages/Test#main")]
    [TestCase("ssh://git@github.com/ActionFit/Test.git#abcdef1")]
    [TestCase("git@github.com:ActionFit/Test.git?path=/Packages/Test#1.2.3")]
    public void TryNormalizeGitUrl_AcceptsSupportedUpmGitForms(string input)
    {
        bool success = ActionFitPackageManagerInputUtility.TryNormalizeGitUrl(
            input,
            out string normalized,
            out string error);

        Assert.That(success, Is.True, error);
        Assert.That(normalized, Is.EqualTo(input));
    }

    [TestCase("")]
    [TestCase("http://github.com/ActionFit/Test.git#1.2.3")]
    [TestCase("https://token@github.com/ActionFit/Test.git#1.2.3")]
    [TestCase("ssh://git:password@github.com/ActionFit/Test.git#1.2.3")]
    [TestCase("git:password@github.com:ActionFit/Test.git#1.2.3")]
    [TestCase("https://github.com/ActionFit/Test.git?token=secret#1.2.3")]
    [TestCase("https://github.com/ActionFit/Test.git#")]
    public void TryNormalizeGitUrl_RejectsUnsupportedOrCredentialBearingForms(string input)
    {
        bool success = ActionFitPackageManagerInputUtility.TryNormalizeGitUrl(
            input,
            out string normalized,
            out string error);

        Assert.That(success, Is.False);
        Assert.That(normalized, Is.Empty);
        Assert.That(error, Is.Not.Empty);
        if (!string.IsNullOrEmpty(input))
            Assert.That(error, Does.Not.Contain(input));
    }

    [Test]
    public void MatchesContentBundle_SearchesBundleModuleAndPackageMetadata()
    {
        var status = new ActionFitContentBundleStatus
        {
            bundleId = "lava-rush",
            displayName = "Lava Rush",
            state = "active",
            requiredPackageIds = new[] { "com.actionfit.content-core" },
            modules = new[]
            {
                new ActionFitContentBundleModuleSpec
                {
                    moduleId = "cat-theme",
                    displayName = "Cat Theme",
                    packageIds = new[] { "com.actionfit.lava-rush.cat-merge-theme" },
                },
            },
        };

        Assert.That(ActionFitPackageManagerInputUtility.MatchesContentBundle("Lava", status), Is.True);
        Assert.That(ActionFitPackageManagerInputUtility.MatchesContentBundle("content-core", status), Is.True);
        Assert.That(ActionFitPackageManagerInputUtility.MatchesContentBundle("cat-theme", status), Is.True);
        Assert.That(ActionFitPackageManagerInputUtility.MatchesContentBundle("unrelated", status), Is.False);
    }

    [Test]
    public void MatchesSearch_IsCaseInsensitiveAcrossProvidedMetadata()
    {
        string[] values = { "com.actionfit.sample", "Package Owner", "Git URL installer" };

        Assert.That(ActionFitPackageManagerInputUtility.MatchesSearch("owner", values), Is.True);
        Assert.That(ActionFitPackageManagerInputUtility.MatchesSearch("INSTALLER", values), Is.True);
        Assert.That(ActionFitPackageManagerInputUtility.MatchesSearch("missing", values), Is.False);
    }
}
#endif
