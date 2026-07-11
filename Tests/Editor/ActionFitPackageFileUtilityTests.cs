#if UNITY_EDITOR
using System;
using System.IO;
using NUnit.Framework;

public sealed class ActionFitPackageFileUtilityTests
{
    private const string PackageId = "com.actionfit.testpackage";
    private string _testRoot;

    [SetUp]
    public void SetUp()
    {
        _testRoot = Path.Combine(
            ActionFitPackagePaths.ProjectRoot,
            "Temp",
            "ActionFitPackageManagerTests",
            Guid.NewGuid().ToString("N"),
            PackageId);
        Directory.CreateDirectory(_testRoot);
    }

    [TearDown]
    public void TearDown()
    {
        string operationRoot = Directory.GetParent(_testRoot)?.FullName;
        if (!string.IsNullOrWhiteSpace(operationRoot) && Directory.Exists(operationRoot))
            Directory.Delete(operationRoot, true);
    }

    [Test]
    public void RemovePackageCacheMetadata_RemovesFingerprintAndKeepsPackageValid()
    {
        string packageJsonPath = Path.Combine(_testRoot, "package.json");
        File.WriteAllText(
            packageJsonPath,
            "{\n" +
            $"  \"name\": \"{PackageId}\",\n" +
            "  \"version\": \"1.0.0\",\n" +
            "  \"dependencies\": {},\n" +
            "  \"_fingerprint\": \"abcdef123456\"\n" +
            "}\n");

        ActionFitPackageFileUtility.RemovePackageCacheMetadata(PackageId, _testRoot);

        string updated = File.ReadAllText(packageJsonPath);
        Assert.That(updated, Does.Not.Contain("_fingerprint"));
        Assert.That(ActionFitPackageFileUtility.IsValidLocalPackageFolder(PackageId, _testRoot, out string error), Is.True, error);
    }

    [Test]
    public void SetDependency_WritesExpectedLocalPackageDependency()
    {
        const string manifest = "{\n  \"dependencies\": {\n    \"com.actionfit.testpackage\": \"https://example.invalid/repo.git#1.0.0\"\n  }\n}";

        string updated = ActionFitPackageManifestUtility.SetDependency(manifest, PackageId, $"file:{PackageId}");

        Assert.That(ActionFitPackageManifestUtility.GetDependency(updated, PackageId), Is.EqualTo($"file:{PackageId}"));
    }
}
#endif
