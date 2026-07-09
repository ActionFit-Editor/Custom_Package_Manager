#if UNITY_EDITOR
using System.IO;
using UnityEditor;
using UnityEngine;

public class ActionFitPackageManagerConsoleWindow : EditorWindow
{
    private const string ManifestPath = "Packages/manifest.json";
    private const string FallbackCatalogPath = "Packages/com.actionfit.custompackagemanager/Editor/Catalog/package_catalog.csv";

    [MenuItem("Tools/ActionFit/Package Manager/Manager Console", false, 1)]
    public static void Open()
    {
        GetWindow<ActionFitPackageManagerConsoleWindow>("Package Manager Console");
    }

    private void OnGUI()
    {
        EditorGUILayout.LabelField("Package Operations", EditorStyles.boldLabel);
        using (new EditorGUILayout.VerticalScope(EditorStyles.helpBox))
        {
            if (GUILayout.Button("1. Create Package"))
                ActionFitPackageCreateWindow.Open();

            if (GUILayout.Button("2. Publish Changed"))
                ActionFitPackagePublishWindow.OpenChanged();

            if (GUILayout.Button("Publish Package"))
                ActionFitPackagePublishWindow.OpenUpdate();
        }

        EditorGUILayout.Space(8);
        EditorGUILayout.LabelField("Project Files", EditorStyles.boldLabel);
        using (new EditorGUILayout.VerticalScope(EditorStyles.helpBox))
        {
            if (GUILayout.Button("Open Catalog"))
            {
                var catalog = AssetDatabase.LoadAssetAtPath<TextAsset>(ActionFitPackageCatalogSettingsProvider.LocalCatalogPath);
                Selection.activeObject = catalog != null
                    ? catalog
                    : AssetDatabase.LoadAssetAtPath<TextAsset>(FallbackCatalogPath);
            }

            if (GUILayout.Button("Open Manifest"))
            {
                var manifest = AssetDatabase.LoadAssetAtPath<UnityEngine.Object>(ManifestPath);
                if (manifest != null)
                    AssetDatabase.OpenAsset(manifest);
                else if (File.Exists(ManifestPath))
                    EditorUtility.RevealInFinder(ManifestPath);
            }

            if (GUILayout.Button("Refresh AI Guide Router"))
                ActionFitPackageAiGuideRouter.EnsureProjectRouter();
        }
    }
}
#endif
