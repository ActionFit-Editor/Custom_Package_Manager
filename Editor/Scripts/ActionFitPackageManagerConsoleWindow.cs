#if UNITY_EDITOR
using System.IO;
using UnityEditor;
using UnityEngine;

public class ActionFitPackageManagerConsoleWindow : EditorWindow
{
    private const string ReadmePath = "Packages/com.actionfit.custompackagemanager/README.md";
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

            if (GUILayout.Button("2. Create Repo"))
                ActionFitPackagePublishWindow.OpenCreate();

            if (GUILayout.Button("3. Publish Package"))
                ActionFitPackagePublishWindow.OpenUpdate();

            if (GUILayout.Button("Publish Changed"))
                ActionFitPackagePublishWindow.OpenChanged();
        }

        EditorGUILayout.Space(8);
        EditorGUILayout.LabelField("Project Files", EditorStyles.boldLabel);
        using (new EditorGUILayout.VerticalScope(EditorStyles.helpBox))
        {
            if (GUILayout.Button("README"))
                ActionFitPackageReadmeWindow.Open(ReadmePath);

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

            if (GUILayout.Button("Settings"))
                Selection.activeObject = ActionFitPackageCatalogSettingsProvider.FindOrCreate();
        }
    }
}
#endif
