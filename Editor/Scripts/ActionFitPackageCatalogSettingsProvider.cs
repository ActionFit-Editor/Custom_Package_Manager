#if UNITY_EDITOR
using System.IO;
using UnityEditor;
using UnityEngine;

[InitializeOnLoad]
public static class ActionFitPackageCatalogSettingsProvider
{
    public const string SettingsAssetPath = "Assets/_Data/_CustomPackageManager/ActionFitPackageCatalogSettings_SO.asset";
    public const string LocalCatalogPath = "Assets/_Data/_CustomPackageManager/package_catalog.csv";

    static ActionFitPackageCatalogSettingsProvider()
    {
        EditorApplication.delayCall += () => EnsureAsset();
    }

    public static ActionFitPackageCatalogSettings_SO EnsureAsset()
    {
        var settings = AssetDatabase.LoadAssetAtPath<ActionFitPackageCatalogSettings_SO>(SettingsAssetPath);
        if (settings != null) return settings;

        EnsureFolder(Path.GetDirectoryName(SettingsAssetPath)?.Replace("\\", "/"));
        settings = ScriptableObject.CreateInstance<ActionFitPackageCatalogSettings_SO>();
        AssetDatabase.CreateAsset(settings, SettingsAssetPath);
        AssetDatabase.SaveAssets();
        Debug.Log($"[ActionFitPackageManager] Catalog settings created: {SettingsAssetPath}");
        return settings;
    }

    public static ActionFitPackageCatalogSettings_SO FindOrCreate()
    {
        var settings = AssetDatabase.LoadAssetAtPath<ActionFitPackageCatalogSettings_SO>(SettingsAssetPath);
        if (settings != null) return settings;

        var guids = AssetDatabase.FindAssets($"t:{nameof(ActionFitPackageCatalogSettings_SO)}");
        if (guids.Length > 0)
        {
            if (guids.Length > 1)
                Debug.LogWarning($"[ActionFitPackageManager] Multiple catalog settings found. Using first: {AssetDatabase.GUIDToAssetPath(guids[0])}");
            return AssetDatabase.LoadAssetAtPath<ActionFitPackageCatalogSettings_SO>(AssetDatabase.GUIDToAssetPath(guids[0]));
        }

        return EnsureAsset();
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
