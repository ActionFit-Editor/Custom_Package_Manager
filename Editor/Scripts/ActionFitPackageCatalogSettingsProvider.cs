#if UNITY_EDITOR
using System.IO;
using ActionFit.SOSingleton.Editor;
using UnityEditor;

public static class ActionFitPackageCatalogSettingsProvider
{
    public const string SettingsAssetPath = "Assets/_Data/_CustomPackageManager/ActionFitPackageCatalogSettings_SO.asset";
    public const string LocalCatalogPath = "Assets/_Data/_CustomPackageManager/package_catalog.csv";

    public static ActionFitPackageCatalogSettings_SO EnsureAsset()
    {
        return ActionFitSettingsAssetProvider.GetOrCreate<ActionFitPackageCatalogSettings_SO>();
    }

    public static ActionFitPackageCatalogSettings_SO FindOrCreate()
    {
        return ActionFitSettingsAssetProvider.GetOrCreate<ActionFitPackageCatalogSettings_SO>();
    }

    public static void EnsureLocalCatalogFolder()
    {
        EnsureFolder(Path.GetDirectoryName(LocalCatalogPath)?.Replace("\\", "/"));
    }

    private static void EnsureFolder(string folder)
    {
        if (string.IsNullOrWhiteSpace(folder) || AssetDatabase.IsValidFolder(folder)) return;

        string parent = Path.GetDirectoryName(folder)?.Replace("\\", "/");
        if (!string.IsNullOrWhiteSpace(parent)) EnsureFolder(parent);

        string name = Path.GetFileName(folder);
        AssetDatabase.CreateFolder(parent, name);
    }
}
#endif
