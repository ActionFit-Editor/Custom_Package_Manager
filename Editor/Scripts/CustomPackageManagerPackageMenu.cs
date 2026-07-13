#if UNITY_EDITOR
using UnityEditor;
using UnityEngine;

public static class CustomPackageManagerPackageMenu
{
    private const string PackageId = "com.actionfit.custompackagemanager";
    private const string MenuRoot = "Tools/Package/Custom Package Manager/";
    private const string ReadmePath = "Packages/com.actionfit.custompackagemanager/README.md";
    private const int InstallSkillsPriority = 2;
    private const int RemoveSkillsPriority = 3;
    private const int SettingPriority = 900;
    private const int ReadmePriority = 901;

    [MenuItem(MenuRoot + "Install or Refresh Agent Skills", false, InstallSkillsPriority)]
    private static void InstallOrRefreshAgentSkills()
    {
        try
        {
            ActionFitPackageSkillInstallResult result = ActionFitPackageSkillBootstrap.InstallOrRefresh();
            ActionFitPackageSkillBootstrap.LogResult("install or refresh", result);
            EditorUtility.DisplayDialog(
                "ActionFit Package Agent Skills",
                $"Installed: {result.Installed}\nUpdated: {result.Updated}\n"
                + $"Unchanged: {result.Unchanged}\nPreserved: {result.Warnings.Count}",
                "OK");
        }
        catch (System.Exception exception)
        {
            Debug.LogException(exception);
            EditorUtility.DisplayDialog("ActionFit Package Agent Skills", exception.Message, "OK");
        }
    }

    [MenuItem(MenuRoot + "Remove Managed Agent Skills", false, RemoveSkillsPriority)]
    private static void RemoveManagedAgentSkills()
    {
        if (!EditorUtility.DisplayDialog(
                "Remove ActionFit Package Agent Skills",
                "Remove only unchanged skills managed by installed ActionFit packages? "
                + "Modified skills will be preserved and automatic installation will be disabled.",
                "Remove Managed Skills",
                "Cancel"))
        {
            return;
        }

        try
        {
            ActionFitPackageSkillInstallResult result = ActionFitPackageSkillBootstrap.RemoveManaged();
            ActionFitPackageSkillBootstrap.LogResult("removal", result);
            EditorUtility.DisplayDialog(
                "ActionFit Package Agent Skills",
                $"Removed: {result.Removed}\nPreserved: {result.Warnings.Count}",
                "OK");
        }
        catch (System.Exception exception)
        {
            Debug.LogException(exception);
            EditorUtility.DisplayDialog("ActionFit Package Agent Skills", exception.Message, "OK");
        }
    }

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
