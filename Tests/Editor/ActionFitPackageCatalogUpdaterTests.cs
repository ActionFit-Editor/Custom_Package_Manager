#if UNITY_EDITOR
using NUnit.Framework;

public sealed class ActionFitPackageCatalogUpdaterTests
{
    [Test]
    public void BuildCatalogCsv_PreservesOptionalCollectionPackageType()
    {
        const string packageCsv =
            "package_id,display_name,package_type,owner,repo_url\n" +
            "com.actionfit.collection,Sample Collection,collection,ActionFit,https://github.com/ActionFit/Collection.git\n";
        const string versionCsv =
            "catalog_id,package_id,version,status,is_latest\n" +
            "com.actionfit.collection@1.0.0,com.actionfit.collection,1.0.0,verified,true\n";

        string catalog = ActionFitPackageCatalogUpdater.BuildCatalogCsv(packageCsv, versionCsv, "");
        string[] rows = catalog.Split('\n');

        Assert.That(rows[0], Does.Contain("display_name,package_type,owner"));
        Assert.That(rows[1], Does.Contain("displayName(string),packageType(string),owner(string)"));
        Assert.That(rows[2], Does.Contain("com.actionfit.collection,Sample Collection,collection,ActionFit"));
    }

    [Test]
    public void BuildCatalogCsv_LeavesPackageTypeEmptyForLegacyCatalog()
    {
        const string packageCsv =
            "package_id,display_name,owner,repo_url\n" +
            "com.actionfit.regular,Regular Package,ActionFit,https://github.com/ActionFit/Regular.git\n";
        const string versionCsv =
            "package_id,version,status,is_latest\n" +
            "com.actionfit.regular,1.0.0,verified,true\n";

        string catalog = ActionFitPackageCatalogUpdater.BuildCatalogCsv(packageCsv, versionCsv, "");
        string[] values = catalog.Split('\n')[2].Split(',');

        Assert.That(values[3], Is.Empty);
        Assert.That(values[4], Is.EqualTo("ActionFit"));
    }
}
#endif
