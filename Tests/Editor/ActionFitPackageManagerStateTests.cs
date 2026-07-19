#if UNITY_EDITOR
using System;
using System.IO;
using NUnit.Framework;

public sealed class ActionFitPackageManagerStateTests
{
    private string _projectRoot;

    [SetUp]
    public void SetUp()
    {
        _projectRoot = Path.Combine(
            Path.GetTempPath(),
            "ActionFitPackageManagerStateTests",
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(_projectRoot, "Packages"));
    }

    [TearDown]
    public void TearDown()
    {
        ActionFitPackageManagerRefreshSignal.FlushForTests();
        if (Directory.Exists(_projectRoot)) Directory.Delete(_projectRoot, true);
    }

    [Test]
    public void Capture_ReadsManifestAndPackageRootOnce_AndLookupsStayInMemory()
    {
        WritePackage("Packages/com.actionfit.embedded", "com.actionfit.embedded", "1.2.3");
        WritePackage("LocalPackages/com.actionfit.external", "com.actionfit.external", "3.4.5");
        File.WriteAllText(
            Path.Combine(_projectRoot, "Packages", "manifest.json"),
            "{\"dependencies\":{" +
            "\"com.actionfit.downloaded\":\"https://github.com/actionfit/downloaded.git#2.0.0\"," +
            "\"com.actionfit.external\":\"file:../LocalPackages/com.actionfit.external\"" +
            "}}");

        ActionFitPackageManagerInstalledStateSnapshot snapshot =
            ActionFitPackageManagerInstalledStateLoader.Capture(
                new[]
                {
                    "com.actionfit.embedded",
                    "com.actionfit.downloaded",
                    "com.actionfit.external",
                    "com.actionfit.available",
                },
                _projectRoot);

        Assert.That(snapshot.Statistics.ManifestReads, Is.EqualTo(1));
        Assert.That(snapshot.Statistics.PackageRootEnumerations, Is.EqualTo(1));
        Assert.That(snapshot.Statistics.PackageJsonReads, Is.EqualTo(2));

        Assert.That(snapshot.Get("com.actionfit.embedded").IsEmbedded, Is.True);
        Assert.That(snapshot.Get("com.actionfit.embedded").Version, Is.EqualTo("1.2.3"));
        Assert.That(snapshot.Get("com.actionfit.downloaded").IsEmbedded, Is.False);
        Assert.That(snapshot.Get("com.actionfit.downloaded").Version, Is.EqualTo("2.0.0"));
        Assert.That(snapshot.Get("com.actionfit.external").IsEmbedded, Is.True);
        Assert.That(snapshot.Get("com.actionfit.external").Version, Is.EqualTo("3.4.5"));
        Assert.That(snapshot.Get("com.actionfit.available").IsInstalled, Is.False);

        for (int i = 0; i < 500; i++)
            _ = snapshot.Get("com.actionfit.embedded");

        Assert.That(snapshot.Statistics.ManifestReads, Is.EqualTo(1));
        Assert.That(snapshot.Statistics.PackageRootEnumerations, Is.EqualTo(1));
        Assert.That(snapshot.Statistics.PackageJsonReads, Is.EqualTo(2));
    }

    [Test]
    public void RefreshSignal_CoalescesRepeatedRequestsUntilDispatch()
    {
        int dispatchCount = 0;
        void CountDispatch() => dispatchCount++;
        ActionFitPackageManagerRefreshSignal.RefreshRequested += CountDispatch;
        try
        {
            ActionFitPackageManagerRefreshSignal.Request();
            ActionFitPackageManagerRefreshSignal.Request();
            ActionFitPackageManagerRefreshSignal.Request();

            Assert.That(ActionFitPackageManagerRefreshSignal.IsPending, Is.True);
            Assert.That(dispatchCount, Is.Zero);

            ActionFitPackageManagerRefreshSignal.FlushForTests();

            Assert.That(ActionFitPackageManagerRefreshSignal.IsPending, Is.False);
            Assert.That(dispatchCount, Is.EqualTo(1));
        }
        finally
        {
            ActionFitPackageManagerRefreshSignal.RefreshRequested -= CountDispatch;
        }
    }

    [TestCase("Packages/com.actionfit.sample/package.json", true)]
    [TestCase("Packages\\com.actionfit.sample\\package.json", true)]
    [TestCase("Assets/_Data/_CustomPackageManager/package_catalog.csv", true)]
    [TestCase("Packages/com.actionfit.sample/README.md", false)]
    [TestCase("Packages/com.actionfit.sample/Tests/Fixtures~/package.json", false)]
    [TestCase("Assets/Unrelated/package.json", false)]
    public void AssetFilter_OnlyAcceptsCatalogAndPackageManifestChanges(string path, bool expected)
    {
        Assert.That(ActionFitPackageManagerAssetPostprocessor.ShouldRefresh(new[] { path }), Is.EqualTo(expected));
    }

    private void WritePackage(string relativePath, string packageId, string version)
    {
        string directory = Path.Combine(_projectRoot, relativePath.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(directory);
        File.WriteAllText(
            Path.Combine(directory, "package.json"),
            $"{{\"name\":\"{packageId}\",\"version\":\"{version}\"}}");
    }
}
#endif
