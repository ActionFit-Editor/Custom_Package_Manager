#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using UnityEditor;
using UnityEngine;

public class ActionFitPackagePublishWindow : EditorWindow
{
    private enum Mode
    {
        Create,
        Update,
    }

    private const string PackageCatalogPath = "Packages/com.actionfit.custompackagemanager/Editor/Catalog/actionfit_package_catalog.csv";
    private readonly Dictionary<string, string> _versionByAssetPath = new();
    private readonly HashSet<string> _expandedPackageIds = new();
    private readonly List<Entry> _entries = new();
    private Mode _mode;
    private Vector2 _scroll;

    public static void OpenCreate()
    {
        Open(Mode.Create);
    }

    public static void OpenUpdate()
    {
        Open(Mode.Update);
    }

    private static void Open(Mode mode)
    {
        var window = GetWindow<ActionFitPackagePublishWindow>(mode == Mode.Create ? "2. Create Repo" : "3. Publish Package");
        window.minSize = new Vector2(720, 520);
        window._mode = mode;
        window.Reload();
        window.Show();
    }

    private void OnGUI()
    {
        DrawToolbar();

        string empty = _mode == Mode.Create
            ? "No unregistered ActionFit package infos found."
            : "No registered ActionFit package infos found.";

        if (_entries.Count == 0)
        {
            EditorGUILayout.HelpBox(empty, MessageType.Info);
            return;
        }

        var entries = _entries.ToArray();
        using var scroll = new EditorGUILayout.ScrollViewScope(_scroll);
        _scroll = scroll.scrollPosition;
        foreach (var entry in entries)
        {
            DrawEntry(entry);
            EditorGUILayout.Space(6);
        }
    }

    private void DrawToolbar()
    {
        using (new EditorGUILayout.HorizontalScope(EditorStyles.toolbar))
        {
            if (GUILayout.Button("Reload", EditorStyles.toolbarButton, GUILayout.Width(70))) Reload();
            GUILayout.FlexibleSpace();
        }
    }

    private void DrawEntry(Entry entry)
    {
        using (new EditorGUILayout.VerticalScope(EditorStyles.helpBox))
        {
            using (new EditorGUILayout.HorizontalScope())
            {
                bool expanded = _expandedPackageIds.Contains(entry.PackageId);
                bool nextExpanded = EditorGUILayout.Foldout(expanded, entry.DisplayName, true, EditorStyles.foldoutHeader);
                if (nextExpanded) _expandedPackageIds.Add(entry.PackageId);
                else _expandedPackageIds.Remove(entry.PackageId);

                GUILayout.FlexibleSpace();
                EditorGUILayout.LabelField(entry.PackageId, EditorStyles.miniLabel, GUILayout.Width(260));
            }

            if (!_expandedPackageIds.Contains(entry.PackageId)) return;

            EditorGUILayout.LabelField("Package Root", entry.PackageRoot);
            EditorGUILayout.LabelField("Current Version", entry.Version);

            var serialized = new SerializedObject(entry.Info);
            serialized.Update();
            EditorGUILayout.PropertyField(serialized.FindProperty("_displayName"));
            EditorGUILayout.PropertyField(serialized.FindProperty("_repoName"));
            EditorGUILayout.PropertyField(serialized.FindProperty("_description"));
            EditorGUILayout.PropertyField(serialized.FindProperty("_releaseNote"));
            EditorGUILayout.PropertyField(serialized.FindProperty("_dependenciesOverride"));
            serialized.ApplyModifiedProperties();

            using (new EditorGUILayout.HorizontalScope())
            {
                if (_mode == Mode.Update)
                {
                    EditorGUILayout.LabelField("Publish Version", GUILayout.Width(100));
                    string value = GetVersion(entry);
                    string next = EditorGUILayout.TextField(value);
                    if (next != value) _versionByAssetPath[entry.AssetPath] = next;
                }
                else
                {
                    EditorGUILayout.LabelField($"Initial Version: {entry.Version}", EditorStyles.miniLabel);
                }

                GUILayout.FlexibleSpace();
                if (GUILayout.Button("Select", GUILayout.Width(70)))
                {
                    Selection.activeObject = entry.Info;
                    EditorGUIUtility.PingObject(entry.Info);
                }

                string button = _mode == Mode.Create ? "2. Create Repo" : "3. Publish Package";
                if (GUILayout.Button(button, GUILayout.Width(140)))
                    Publish(entry);
            }
        }
    }

    private void Publish(Entry entry)
    {
        string version = _mode == Mode.Update ? GetVersion(entry).Trim() : entry.Version;
        if (string.IsNullOrWhiteSpace(version))
        {
            EditorUtility.DisplayDialog("ActionFit Package Manager", "Publish Version is empty.", "OK");
            return;
        }

        string action = _mode == Mode.Create ? "Create repo" : "Publish package";
        if (!EditorUtility.DisplayDialog(
                "ActionFit Package Manager",
                $"{action}: {entry.PackageId}@{version}?\n\nThis will create/check the GitHub repository, push package contents and tag, then append the catalog spreadsheet.",
                _mode == Mode.Create ? "2. Create Repo" : "3. Publish Package",
                "Cancel"))
            return;

        if (_mode == Mode.Update && !SetPackageJsonVersion(entry.PackageJsonPath, version, out string error))
        {
            EditorUtility.DisplayDialog("ActionFit Package Manager", error, "OK");
            return;
        }

        AssetDatabase.ImportAsset(entry.PackageJsonPath, ImportAssetOptions.ForceUpdate);
        var settings = ActionFitPackageCatalogSettingsProvider.FindOrCreate();
        bool ok = ActionFitPackagePublisher.Publish(settings, entry.Info, out string message);
        if (ok)
        {
            Debug.Log($"[ActionFitPackageManager] {message}");
            if (ActionFitPackageCatalogUpdater.UpdateCatalog(settings, out string updateMessage))
                Debug.Log($"[ActionFitPackageManager] {updateMessage}");
            else
                Debug.LogWarning($"[ActionFitPackageManager] Catalog refresh failed after publish: {updateMessage}");

            ScheduleReload();
        }
        else
        {
            Debug.LogError($"[ActionFitPackageManager] Publish failed: {message}");
            EditorUtility.DisplayDialog("ActionFit Package Manager", message, "OK");
        }
    }

    private string GetVersion(Entry entry)
    {
        if (_versionByAssetPath.TryGetValue(entry.AssetPath, out string version) && !string.IsNullOrWhiteSpace(version))
            return version;

        _versionByAssetPath[entry.AssetPath] = entry.Version;
        return entry.Version;
    }

    private void ScheduleReload()
    {
        EditorApplication.delayCall += () =>
        {
            if (this == null) return;
            Reload();
            Repaint();
        };
    }

    private void Reload()
    {
        _entries.Clear();
        var registered = ReadRegisteredPackageIds();
        foreach (string guid in AssetDatabase.FindAssets("t:ActionFitPackageInfo_SO", new[] { "Packages" }))
        {
            string assetPath = AssetDatabase.GUIDToAssetPath(guid);
            if (!assetPath.StartsWith("Packages/com.actionfit.", StringComparison.OrdinalIgnoreCase)) continue;

            var info = AssetDatabase.LoadAssetAtPath<ActionFitPackageInfo_SO>(assetPath);
            if (info == null) continue;

            string packageRoot = ActionFitPackageInfoUtility.FindPackageRoot(info);
            if (string.IsNullOrWhiteSpace(packageRoot)) continue;

            string packageJsonPath = Path.Combine(packageRoot, "package.json").Replace("\\", "/");
            if (!File.Exists(Path.GetFullPath(packageJsonPath))) continue;

            var manifest = ActionFitPackageManifest.Read(packageJsonPath);
            bool isRegistered = registered.Contains(manifest.Name);
            if (_mode == Mode.Create && isRegistered) continue;
            if (_mode == Mode.Update && !isRegistered) continue;

            _entries.Add(new Entry
            {
                Info = info,
                AssetPath = assetPath,
                PackageRoot = packageRoot,
                PackageJsonPath = packageJsonPath,
                PackageId = manifest.Name,
                DisplayName = string.IsNullOrWhiteSpace(manifest.DisplayName) ? info.DisplayName : manifest.DisplayName,
                Version = manifest.Version,
            });
        }

        _entries.Sort((a, b) => string.Compare(a.DisplayName, b.DisplayName, StringComparison.OrdinalIgnoreCase));
    }

    private static HashSet<string> ReadRegisteredPackageIds()
    {
        var ids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        string local = ActionFitPackageCatalogSettingsProvider.LocalCatalogPath;
        string catalogPath = File.Exists(Path.GetFullPath(local)) ? local : PackageCatalogPath;
        string path = Path.GetFullPath(catalogPath);
        if (!File.Exists(path)) return ids;

        string[] lines = File.ReadAllLines(path);
        if (lines.Length < 2) return ids;

        var header = SplitCsvLine(lines[0]).Select(h => h.Trim()).ToArray();
        int packageIdIndex = Array.FindIndex(header, h => string.Equals(h, "package_id", StringComparison.OrdinalIgnoreCase));
        if (packageIdIndex < 0) return ids;

        for (int i = 1; i < lines.Length; i++)
        {
            if (string.IsNullOrWhiteSpace(lines[i])) continue;
            if (lines[i].Contains("(string)", StringComparison.OrdinalIgnoreCase)) continue;
            string[] cols = SplitCsvLine(lines[i]);
            if (packageIdIndex < cols.Length && !string.IsNullOrWhiteSpace(cols[packageIdIndex]))
                ids.Add(cols[packageIdIndex].Trim());
        }

        return ids;
    }

    private static bool SetPackageJsonVersion(string packageJsonPath, string version, out string error)
    {
        error = "";
        if (!Regex.IsMatch(version, @"^\d+\.\d+\.\d+([-.][A-Za-z0-9._-]+)?$"))
        {
            error = "Publish Version must look like 1.0.0.";
            return false;
        }

        string fullPath = Path.GetFullPath(packageJsonPath);
        if (!File.Exists(fullPath))
        {
            error = $"package.json not found: {packageJsonPath}";
            return false;
        }

        string json = File.ReadAllText(fullPath);
        var pattern = new Regex("(\"version\"\\s*:\\s*\")([^\"]*)(\")");
        if (!pattern.IsMatch(json))
        {
            error = "package.json has no version field.";
            return false;
        }

        string updated = pattern.Replace(json, $"$1{version}$3", 1);
        if (updated != json) File.WriteAllText(fullPath, updated, new UTF8Encoding(false));
        return true;
    }

    private static string[] SplitCsvLine(string line)
    {
        var result = new List<string>();
        var sb = new StringBuilder();
        bool inQuotes = false;

        for (int i = 0; i < line.Length; i++)
        {
            char c = line[i];
            if (c == '"')
            {
                if (inQuotes && i + 1 < line.Length && line[i + 1] == '"')
                {
                    sb.Append('"');
                    i++;
                    continue;
                }

                inQuotes = !inQuotes;
                continue;
            }

            if (c == ',' && !inQuotes)
            {
                result.Add(sb.ToString());
                sb.Clear();
                continue;
            }

            sb.Append(c);
        }

        result.Add(sb.ToString());
        return result.ToArray();
    }

    private sealed class Entry
    {
        public ActionFitPackageInfo_SO Info;
        public string AssetPath;
        public string PackageRoot;
        public string PackageJsonPath;
        public string PackageId;
        public string DisplayName;
        public string Version;
    }
}
#endif
