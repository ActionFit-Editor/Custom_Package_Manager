#if UNITY_EDITOR
using UnityEditor;
using UnityEngine;

public static class CustomPackageManagerPackageMenu
{
    private const string PackageId = "com.actionfit.custompackagemanager";
    private const string MenuRoot = "Tools/Package/Custom Package Manager/";
    private const string ReadmePath = "Packages/com.actionfit.custompackagemanager/README.md";
    private const int SettingPriority = 900;
    private const int ReadmePriority = 901;

    [MenuItem(MenuRoot + "Setting SO", false, SettingPriority)]
    private static void FocusSettingSo() => FocusObject(ActionFitPackageCatalogSettingsProvider.FindOrCreate(), PackageId);

    [MenuItem(MenuRoot + "README", false, ReadmePriority)]
    private static void OpenReadme()
    {
        var readme = AssetDatabase.LoadAssetAtPath<TextAsset>(ReadmePath);
        if (readme == null)
        {
            EditorUtility.DisplayDialog("Package README", $"README was not found.\n{ReadmePath}", "OK");
            return;
        }

        Selection.activeObject = readme;
        AssetDatabase.OpenAsset(readme);
    }

    private static void FocusObject(Object target, string packageId)
    {
        if (target == null)
        {
            EditorUtility.DisplayDialog(
                "Setting SO",
                $"Setting SO was not found for {packageId}.\nOpen the package setup window or create the settings asset first.",
                "OK");
            return;
        }

        Selection.activeObject = target;
        EditorUtility.FocusProjectWindow();
        EditorGUIUtility.PingObject(target);
    }
}
#endif
