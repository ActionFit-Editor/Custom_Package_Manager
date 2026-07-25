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
    private const int CoreProfilePriority = 4;
    private const int AllProfilePriority = 5;
    private const int InspectProfilePriority = 6;
    private const int RollbackProfilePriority = 7;
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

    [MenuItem(MenuRoot + "Agent Skill Profile/Preview and Apply Core", false, CoreProfilePriority)]
    private static void ApplyCoreProfile() => PreviewAndApplyProfile("core");

    [MenuItem(MenuRoot + "Agent Skill Profile/Preview and Apply All", false, AllProfilePriority)]
    private static void ApplyAllProfile() => PreviewAndApplyProfile("all");

    [MenuItem(MenuRoot + "Agent Skill Profile/Inspect Active", false, InspectProfilePriority)]
    private static void InspectActiveProfile()
    {
        try
        {
            string projectRoot = System.IO.Path.GetFullPath(
                System.IO.Path.Combine(Application.dataPath, ".."));
            EditorUtility.DisplayDialog(
                "ActionFit Agent Skill Profile Inspection",
                ActionFitAgentSkillProfileService.Inspect(projectRoot).ExactPreview,
                "OK");
        }
        catch (System.Exception exception)
        {
            Debug.LogException(exception);
            EditorUtility.DisplayDialog("ActionFit Agent Skill Profile", exception.Message, "OK");
        }
    }

    [MenuItem(MenuRoot + "Agent Skill Profile/Rollback Last Apply", false, RollbackProfilePriority)]
    private static void RollbackLastProfile()
    {
        if (!EditorUtility.DisplayDialog(
                "Rollback Agent Skill Profile",
                "Rollback the last journaled profile move only when every moved target is unchanged?",
                "Rollback",
                "Cancel"))
            return;
        try
        {
            string projectRoot = System.IO.Path.GetFullPath(
                System.IO.Path.Combine(Application.dataPath, ".."));
            ActionFitAgentSkillProfileService.RollbackLast(projectRoot);
            ActionFitPackageSkillInstallResult refresh = ActionFitPackageSkillBootstrap.InstallOrRefresh();
            ActionFitPackageSkillBootstrap.LogResult("profile rollback refresh", refresh);
            EditorUtility.DisplayDialog(
                "ActionFit Agent Skill Profile",
                "The last eligible profile transaction was rolled back. Start a new AI session.",
                "OK");
        }
        catch (System.Exception exception)
        {
            Debug.LogException(exception);
            EditorUtility.DisplayDialog("ActionFit Agent Skill Profile", exception.Message, "OK");
        }
    }

    private static void PreviewAndApplyProfile(string profileName)
    {
        try
        {
            string projectRoot = System.IO.Path.GetFullPath(
                System.IO.Path.Combine(Application.dataPath, ".."));
            ActionFitAgentSkillProfileResult preview =
                ActionFitAgentSkillProfileService.Preview(projectRoot, profileName);
            if (!EditorUtility.DisplayDialog(
                    "ActionFit Agent Skill Profile Preview",
                    preview.ExactPreview + "\n\nApply this exact move plan?",
                    "Apply",
                    "Cancel"))
                return;

            ActionFitAgentSkillProfileService.Apply(projectRoot, profileName, preview.ExactPreview);
            ActionFitPackageSkillInstallResult refresh = ActionFitPackageSkillBootstrap.InstallOrRefresh();
            ActionFitPackageSkillBootstrap.LogResult("profile refresh", refresh);
            EditorUtility.DisplayDialog(
                "ActionFit Agent Skill Profile",
                preview.Summary + "\n\nStart a new AI session before measuring context.",
                "OK");
        }
        catch (System.Exception exception)
        {
            Debug.LogException(exception);
            EditorUtility.DisplayDialog("ActionFit Agent Skill Profile", exception.Message, "OK");
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
