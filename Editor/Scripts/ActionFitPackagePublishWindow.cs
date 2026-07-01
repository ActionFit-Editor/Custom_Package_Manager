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
        Changed,
    }

    private const string PackageCatalogPath = "Packages/com.actionfit.custompackagemanager/Editor/Catalog/package_catalog.csv";
    private static readonly string[] RepositoryVisibilityLabels = { "Public", "Private" };
    private static string ProjectRootPath => Path.GetFullPath(Path.Combine(Application.dataPath, ".."));
    private static string ProjectRootNormalized => ProjectRootPath.Replace("\\", "/").TrimEnd('/');

    private static string ProjectRelativeFullPath(string relativePath)
    {
        return Path.Combine(ProjectRootPath, relativePath.Replace("/", Path.DirectorySeparatorChar.ToString()));
    }

    private readonly Dictionary<string, string> _versionByAssetPath = new();
    private readonly HashSet<string> _expandedPackageIds = new();
    private readonly List<Entry> _entries = new();
    private Mode _mode;
    private ActionFitPackageRepositoryVisibility _createRepoVisibility = ActionFitPackageRepositoryVisibility.Public;
    private Vector2 _scroll;

    public static void OpenCreate()
    {
        Open(Mode.Create);
    }

    public static void OpenUpdate()
    {
        Open(Mode.Update);
    }

    public static void OpenChanged()
    {
        Open(Mode.Changed);
    }

    private static void Open(Mode mode)
    {
        string title = mode switch
        {
            Mode.Create => "2. Create Repo",
            Mode.Changed => "Publish Changed",
            _ => "3. Publish Package"
        };
        var window = GetWindow<ActionFitPackagePublishWindow>(title);
        window.minSize = new Vector2(720, 520);
        window._mode = mode;
        window.Reload();
        window.Show();
    }

    private void OnGUI()
    {
        DrawToolbar();

        string empty = _mode switch
        {
            Mode.Create => "No unregistered ActionFit package infos found.",
            Mode.Changed => "No changed package versions found. Local package.json versions are not higher than catalog latest versions.",
            _ => "No registered ActionFit package infos found."
        };

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
            if (_mode == Mode.Create)
            {
                GUILayout.Space(8);
                EditorGUILayout.LabelField("Repo", EditorStyles.miniLabel, GUILayout.Width(34));
                _createRepoVisibility = (ActionFitPackageRepositoryVisibility)GUILayout.Toolbar(
                    (int)_createRepoVisibility,
                    RepositoryVisibilityLabels,
                    EditorStyles.toolbarButton,
                    GUILayout.Width(150));
            }

            if (_mode == Mode.Changed)
            {
                EditorGUI.BeginDisabledGroup(_entries.Count == 0);
                if (GUILayout.Button("Publish All Changed", EditorStyles.toolbarButton, GUILayout.Width(145))) PublishAllChanged();
                EditorGUI.EndDisabledGroup();
            }
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
            if (_mode == Mode.Changed)
                EditorGUILayout.LabelField("Catalog Latest", entry.CatalogLatestVersion);

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
                    string label = _mode == Mode.Changed
                        ? $"Publish Version: {entry.Version} (catalog latest: {entry.CatalogLatestVersion})"
                        : $"Initial Version: {entry.Version}";
                    EditorGUILayout.LabelField(label, EditorStyles.miniLabel);
                }

                GUILayout.FlexibleSpace();
                if (GUILayout.Button("Select", GUILayout.Width(70)))
                {
                    Selection.activeObject = entry.Info;
                    EditorGUIUtility.PingObject(entry.Info);
                }

                string button = _mode switch
                {
                    Mode.Create => "2. Create Repo",
                    Mode.Changed => "Publish Changed",
                    _ => "3. Publish Package"
                };
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
        string visibilityLine = _mode == Mode.Create
            ? $"\nRepository visibility: {GetRepositoryVisibilityLabel(_createRepoVisibility)}"
            : "";
        if (!EditorUtility.DisplayDialog(
                "ActionFit Package Manager",
                $"{action}: {entry.PackageId}@{version}?{visibilityLine}\n\nThis will create/check the GitHub repository and prepare the local publish clone. It will not push package contents, push tags, or append the catalog spreadsheet.",
                _mode == Mode.Create ? "2. Create Repo" : "Publish",
                "Cancel"))
            return;

        if (!PublishInternal(entry, version, true, out string error))
            EditorUtility.DisplayDialog("ActionFit Package Manager", error, "OK");
    }

    private void PublishAllChanged()
    {
        var targets = _entries.ToArray();
        if (targets.Length == 0) return;

        string list = string.Join("\n", targets.Select(e => $"- {e.PackageId}: {e.CatalogLatestVersion} -> {e.Version}"));
        if (!EditorUtility.DisplayDialog(
                "ActionFit Package Manager",
                $"Publish all changed packages?\n\n{list}\n\nThis will prepare local publish clones only. It will not push package contents, push tags, or append catalog rows.",
                "Publish All Changed",
                "Cancel"))
            return;

        var succeeded = new List<string>();
        foreach (var entry in targets)
        {
            if (!PublishInternal(entry, entry.Version, false, out string error))
            {
                string message = $"Bulk publish stopped.\n\nSucceeded:\n{string.Join("\n", succeeded)}\n\nFailed:\n{entry.PackageId}@{entry.Version}\n{error}";
                Debug.LogError($"[ActionFitPackageManager] {message}");
                EditorUtility.DisplayDialog("ActionFit Package Manager", message, "OK");
                ScheduleReload();
                return;
            }

            succeeded.Add($"{entry.PackageId}@{entry.Version}");
        }

        var settings = ActionFitPackageCatalogSettingsProvider.FindOrCreate();
        if (ActionFitPackageCatalogUpdater.UpdateCatalog(settings, out string updateMessage))
            Debug.Log($"[ActionFitPackageManager] {updateMessage}");
        else
            Debug.LogWarning($"[ActionFitPackageManager] Catalog refresh failed after bulk publish: {updateMessage}");

        Debug.Log($"[ActionFitPackageManager] Bulk publish preparation complete:\n{string.Join("\n", succeeded)}");
        ScheduleReload();
    }

    private bool PublishInternal(Entry entry, string version, bool refreshCatalog, out string error)
    {
        error = "";
        if (_mode == Mode.Update && !SetPackageJsonVersion(entry.PackageJsonPath, version, out error))
        {
            return false;
        }

        AssetDatabase.ImportAsset(entry.PackageJsonPath, ImportAssetOptions.ForceUpdate);
        var settings = ActionFitPackageCatalogSettingsProvider.FindOrCreate();
        bool ok;
        string message;
        if (_mode == Mode.Create)
            ok = ActionFitPackagePublisher.Publish(settings, entry.Info, _createRepoVisibility, out message);
        else
            ok = ActionFitPackagePublisher.Publish(settings, entry.Info, out message);
        if (ok)
        {
            Debug.Log($"[ActionFitPackageManager] {message}");
            if (refreshCatalog)
            {
                if (ActionFitPackageCatalogUpdater.UpdateCatalog(settings, out string updateMessage))
                    Debug.Log($"[ActionFitPackageManager] {updateMessage}");
                else
                    Debug.LogWarning($"[ActionFitPackageManager] Catalog refresh failed after publish: {updateMessage}");

                ScheduleReload();
            }

            return true;
        }

        error = message;
        Debug.LogError($"[ActionFitPackageManager] Publish failed: {message}");
        return false;
    }

    private static string GetRepositoryVisibilityLabel(ActionFitPackageRepositoryVisibility visibility)
        => visibility == ActionFitPackageRepositoryVisibility.Private ? "Private" : "Public";

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
        var registered = ReadRegisteredPackageVersions();
        foreach (string packageJsonPath in Directory.GetFiles(ProjectRelativeFullPath("Packages"), "package.json", SearchOption.AllDirectories))
        {
            string packageRoot = ToProjectRelativePath(Path.GetDirectoryName(packageJsonPath));
            if (!packageRoot.StartsWith("Packages/com.actionfit.", StringComparison.OrdinalIgnoreCase)) continue;

            string relativePackageJsonPath = Path.Combine(packageRoot, "package.json").Replace("\\", "/");
            var manifest = ActionFitPackageManifest.Read(relativePackageJsonPath);
            bool isRegistered = registered.ContainsKey(manifest.Name);
            if (_mode == Mode.Create && isRegistered) continue;
            if (_mode == Mode.Update && !isRegistered) continue;

            registered.TryGetValue(manifest.Name, out string catalogLatestVersion);
            if (_mode == Mode.Changed &&
                isRegistered &&
                CompareVersions(manifest.Version, catalogLatestVersion) <= 0)
                continue;

            var info = ActionFitPackageInfoUtility.CreateOrUpdate(packageRoot);
            if (info == null) continue;
            string assetPath = AssetDatabase.GetAssetPath(info);

            _entries.Add(new Entry
            {
                Info = info,
                AssetPath = assetPath,
                PackageRoot = packageRoot,
                PackageJsonPath = relativePackageJsonPath,
                PackageId = manifest.Name,
                DisplayName = string.IsNullOrWhiteSpace(manifest.DisplayName) ? info.DisplayName : manifest.DisplayName,
                Version = manifest.Version,
                CatalogLatestVersion = isRegistered ? catalogLatestVersion : "not registered",
            });
        }

        _entries.Sort((a, b) => string.Compare(a.DisplayName, b.DisplayName, StringComparison.OrdinalIgnoreCase));
    }

    private static string ToProjectRelativePath(string fullPath)
    {
        string normalized = Path.GetFullPath(fullPath).Replace("\\", "/");
        return normalized.StartsWith(ProjectRootNormalized + "/", StringComparison.Ordinal)
            ? normalized[(ProjectRootNormalized.Length + 1)..]
            : normalized;
    }

    private static Dictionary<string, string> ReadRegisteredPackageVersions()
    {
        var versions = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        string local = ActionFitPackageCatalogSettingsProvider.LocalCatalogPath;
        string catalogPath = File.Exists(ProjectRelativeFullPath(local)) ? local : PackageCatalogPath;
        string path = ProjectRelativeFullPath(catalogPath);
        if (!File.Exists(path)) return versions;

        string[] lines = File.ReadAllLines(path);
        if (lines.Length < 2) return versions;

        var header = SplitCsvLine(lines[0]).Select(h => h.Trim()).ToArray();
        int packageIdIndex = Array.FindIndex(header, h => string.Equals(h, "package_id", StringComparison.OrdinalIgnoreCase));
        int versionIndex = Array.FindIndex(header, h => string.Equals(h, "version", StringComparison.OrdinalIgnoreCase));
        int isLatestIndex = Array.FindIndex(header, h => string.Equals(h, "is_latest", StringComparison.OrdinalIgnoreCase));
        if (packageIdIndex < 0) return versions;

        for (int i = 1; i < lines.Length; i++)
        {
            if (string.IsNullOrWhiteSpace(lines[i])) continue;
            if (lines[i].Contains("(string)", StringComparison.OrdinalIgnoreCase)) continue;
            string[] cols = SplitCsvLine(lines[i]);
            if (packageIdIndex >= cols.Length || string.IsNullOrWhiteSpace(cols[packageIdIndex])) continue;

            string packageId = cols[packageIdIndex].Trim();
            string version = versionIndex >= 0 && versionIndex < cols.Length ? cols[versionIndex].Trim() : "";
            bool isLatest = isLatestIndex >= 0 && isLatestIndex < cols.Length && IsTrue(cols[isLatestIndex].Trim());
            if (!versions.TryGetValue(packageId, out string current) ||
                isLatest ||
                CompareVersions(version, current) > 0)
                versions[packageId] = version;
        }

        return versions;
    }

    private static int CompareVersions(string left, string right)
    {
        var leftParts = ParseVersion(left);
        var rightParts = ParseVersion(right);
        for (int i = 0; i < Math.Max(leftParts.Count, rightParts.Count); i++)
        {
            int l = i < leftParts.Count ? leftParts[i] : 0;
            int r = i < rightParts.Count ? rightParts[i] : 0;
            int cmp = l.CompareTo(r);
            if (cmp != 0) return cmp;
        }

        return string.Compare(left, right, StringComparison.OrdinalIgnoreCase);
    }

    private static List<int> ParseVersion(string version)
    {
        var result = new List<int>();
        foreach (string part in (version ?? "").Split('.'))
        {
            string digits = new(part.TakeWhile(char.IsDigit).ToArray());
            result.Add(int.TryParse(digits, out int value) ? value : 0);
        }
        return result;
    }

    private static bool IsTrue(string value)
    {
        return value.Equals("true", StringComparison.OrdinalIgnoreCase) ||
               value.Equals("1", StringComparison.OrdinalIgnoreCase) ||
               value.Equals("yes", StringComparison.OrdinalIgnoreCase);
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

        string updated = pattern.Replace(json, match => match.Groups[1].Value + version + match.Groups[3].Value, 1);
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
        public string CatalogLatestVersion;
    }
}
#endif
