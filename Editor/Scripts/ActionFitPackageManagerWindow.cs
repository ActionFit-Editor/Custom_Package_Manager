#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using UnityEditor;
using UnityEditor.PackageManager;
using UnityEngine;

public class ActionFitPackageManagerWindow : EditorWindow
{
    private const string PackageName = "com.actionfit.custompackagemanager";
    private const string CatalogRelativePath = "Editor/Catalog/actionfit_package_catalog.csv";
    private const string ReadmePath = "Packages/com.actionfit.custompackagemanager/README.md";
    private const string ManifestPath = "Packages/manifest.json";
    private static readonly string PackageCatalogPath = Path.Combine("Packages", PackageName, CatalogRelativePath).Replace("\\", "/");

    private readonly Dictionary<string, int> _selectedVersionByPackage = new();
    private readonly HashSet<string> _expandedPackageIds = new();
    private readonly List<PackageGroup> _packages = new();
    private ActionFitPackageCatalogSettings_SO _settings;
    private Vector2 _scroll;
    private string _filter = "";

    [MenuItem("Tools/ActionFit/Package Manager", false, 0)]
    public static void Open()
    {
        GetWindow<ActionFitPackageManagerWindow>("ActionFit Packages");
    }

    private void OnEnable()
    {
        _settings = ActionFitPackageCatalogSettingsProvider.FindOrCreate();
        Reload();
    }

    private void OnGUI()
    {
        DrawToolbar();

        if (_packages.Count == 0)
        {
            EditorGUILayout.HelpBox($"Catalog not found or empty.\n{ActiveCatalogPath}", MessageType.Warning);
        }

        _filter = EditorGUILayout.TextField("Filter", _filter);
        _scroll = EditorGUILayout.BeginScrollView(_scroll);

        DrawPackageSections();

        EditorGUILayout.EndScrollView();
    }

    private void DrawToolbar()
    {
        using (new EditorGUILayout.HorizontalScope(EditorStyles.toolbar))
        {
            if (GUILayout.Button("Reload", EditorStyles.toolbarButton, GUILayout.Width(70))) Reload();
            if (GUILayout.Button("Update", EditorStyles.toolbarButton, GUILayout.Width(70))) UpdateCatalog();
            if (GUILayout.Button("Settings", EditorStyles.toolbarButton, GUILayout.Width(80)))
                Selection.activeObject = _settings = ActionFitPackageCatalogSettingsProvider.FindOrCreate();
            if (GUILayout.Button("1. Create Package", EditorStyles.toolbarButton, GUILayout.Width(130))) ActionFitPackageCreateWindow.Open();
            if (GUILayout.Button("2. Create Repo", EditorStyles.toolbarButton, GUILayout.Width(115))) ActionFitPackagePublishWindow.OpenCreate();
            if (GUILayout.Button("3. Publish Package", EditorStyles.toolbarButton, GUILayout.Width(135))) ActionFitPackagePublishWindow.OpenUpdate();
            if (GUILayout.Button("Publish Changed", EditorStyles.toolbarButton, GUILayout.Width(125))) ActionFitPackagePublishWindow.OpenChanged();
            if (GUILayout.Button("README", EditorStyles.toolbarButton, GUILayout.Width(80))) OpenReadme();
            if (GUILayout.Button("Open Catalog", EditorStyles.toolbarButton, GUILayout.Width(100)))
                Selection.activeObject = AssetDatabase.LoadAssetAtPath<TextAsset>(ActiveCatalogPath);
            if (GUILayout.Button("Open Manifest", EditorStyles.toolbarButton, GUILayout.Width(100)))
                AssetDatabase.OpenAsset(AssetDatabase.LoadAssetAtPath<UnityEngine.Object>(ManifestPath));
            GUILayout.FlexibleSpace();
        }
    }

    private void OpenReadme()
    {
        ActionFitPackageReadmeWindow.Open(ReadmePath);
    }

    private void DrawPackageSections()
    {
        var filtered = FilteredPackages().ToList();
        var manager = filtered.Where(p => p.Id == PackageName).ToList();
        var catalogPackages = filtered.Where(p => p.Id != PackageName).ToList();
        var downloaded = catalogPackages.Where(p => GetInstalledVersion(p.Id).IsInstalled).ToList();
        var available = catalogPackages.Where(p => !GetInstalledVersion(p.Id).IsInstalled).ToList();

        DrawPackageSection("Package Manager", manager);
        EditorGUILayout.Space(4);
        EditorGUILayout.LabelField("", GUI.skin.horizontalSlider);
        EditorGUILayout.Space(4);
        DrawPackageSection("Downloaded Packages", downloaded);
        EditorGUILayout.Space(4);
        EditorGUILayout.LabelField("", GUI.skin.horizontalSlider);
        EditorGUILayout.Space(4);
        DrawPackageSection("Available Packages", available);
    }

    private void DrawPackageSection(string title, List<PackageGroup> packages)
    {
        EditorGUILayout.LabelField($"{title} ({packages.Count})", EditorStyles.boldLabel);

        if (packages.Count == 0)
        {
            EditorGUILayout.HelpBox("No packages.", MessageType.None);
            return;
        }

        foreach (var package in packages)
        {
            DrawPackage(package);
            EditorGUILayout.Space(6);
        }
    }

    private IEnumerable<PackageGroup> FilteredPackages()
    {
        if (string.IsNullOrWhiteSpace(_filter)) return _packages;

        string filter = _filter.Trim();
        return _packages.Where(p =>
            p.Id.IndexOf(filter, StringComparison.OrdinalIgnoreCase) >= 0 ||
            p.DisplayName.IndexOf(filter, StringComparison.OrdinalIgnoreCase) >= 0 ||
            p.Owner.IndexOf(filter, StringComparison.OrdinalIgnoreCase) >= 0);
    }

    private void DrawPackage(PackageGroup package)
    {
        var installed = GetInstalledVersion(package.Id);
        var versions = package.Versions;
        int selected = GetSelectedVersionIndex(package, installed.Version);
        var selectedVersion = versions[selected];

        using (new EditorGUILayout.VerticalScope(EditorStyles.helpBox))
        {
            using (new EditorGUILayout.HorizontalScope())
            {
                bool expanded = _expandedPackageIds.Contains(package.Id);
                bool nextExpanded = EditorGUILayout.Foldout(expanded, package.DisplayName, true, EditorStyles.foldoutHeader);
                if (nextExpanded) _expandedPackageIds.Add(package.Id);
                else _expandedPackageIds.Remove(package.Id);

                GUILayout.FlexibleSpace();
                EditorGUILayout.LabelField($"Installed: {installed.Label}", EditorStyles.miniLabel, GUILayout.Width(150));
                EditorGUILayout.LabelField($"Latest: {package.LatestVersionLabel}", EditorStyles.miniLabel, GUILayout.Width(150));
                EditorGUILayout.LabelField($"Owner: {package.Owner}", EditorStyles.miniLabel, GUILayout.Width(120));
                EditorGUILayout.LabelField(package.Id, EditorStyles.miniLabel, GUILayout.Width(260));
            }

            if (!_expandedPackageIds.Contains(package.Id)) return;

            EditorGUILayout.LabelField("Installed", installed.Label);
            EditorGUILayout.LabelField("Owner", package.Owner);
            EditorGUILayout.LabelField("Description", selectedVersion.Description);
            EditorGUILayout.LabelField("Changelog", selectedVersion.Changelog);

            using (new EditorGUILayout.HorizontalScope())
            {
                EditorGUILayout.LabelField("Version", GUILayout.Width(80));
                selected = EditorGUILayout.Popup(selected, versions.Select(v => v.VersionLabel).ToArray());
                _selectedVersionByPackage[package.Id] = selected;

                string buttonText = installed.IsInstalled ? "Apply Version" : "Install";
                EditorGUI.BeginDisabledGroup(installed.IsEmbedded);
                if (GUILayout.Button(buttonText, GUILayout.Width(120)))
                    ApplyPackage(package, selectedVersion);
                EditorGUI.EndDisabledGroup();

                EditorGUI.BeginDisabledGroup(!installed.IsInstalled || installed.IsEmbedded);
                if (GUILayout.Button("Remove", GUILayout.Width(90)))
                    RemovePackage(package);
                EditorGUI.EndDisabledGroup();
            }

            if (installed.IsEmbedded)
                EditorGUILayout.HelpBox("Embedded package is already present under Packages/. Remove the embedded folder before installing this package as a Git UPM dependency.", MessageType.Info);

            if (!string.IsNullOrWhiteSpace(selectedVersion.Dependencies))
                EditorGUILayout.LabelField("Dependencies", selectedVersion.Dependencies, EditorStyles.miniLabel);
        }
    }

    private int GetSelectedVersionIndex(PackageGroup package, string installedVersion)
    {
        if (_selectedVersionByPackage.TryGetValue(package.Id, out int selected) &&
            selected >= 0 && selected < package.Versions.Count)
            return selected;

        int installed = package.Versions.FindIndex(v => v.Version == installedVersion);
        if (installed >= 0) return installed;

        int latest = package.Versions.FindIndex(v => v.IsLatest);
        return latest >= 0 ? latest : 0;
    }

    private void Reload()
    {
        _packages.Clear();
        _selectedVersionByPackage.Clear();

        string path = Path.GetFullPath(ActiveCatalogPath);
        if (File.Exists(path))
        {
            var rows = ReadCatalog(path);
            _packages.AddRange(rows
                .GroupBy(r => r.Id)
                .Select(g => new PackageGroup(g.Key, g.OrderByDescending(v => v.Version, PackageVersionComparer.Instance).ToList()))
                .OrderBy(g => g.DisplayName, StringComparer.OrdinalIgnoreCase));
        }
    }

    private static string ActiveCatalogPath
    {
        get
        {
            string local = ActionFitPackageCatalogSettingsProvider.LocalCatalogPath;
            return File.Exists(Path.GetFullPath(local)) ? local : PackageCatalogPath;
        }
    }

    private void UpdateCatalog()
    {
        _settings = ActionFitPackageCatalogSettingsProvider.FindOrCreate();
        if (!EditorUtility.DisplayDialog(
                "ActionFit Package Manager",
                "Spreadsheet -> local package catalog CSV\n\nUpdate will overwrite the local catalog CSV with spreadsheet data.",
                "Update",
                "Cancel"))
            return;

        bool ok = ActionFitPackageCatalogUpdater.UpdateCatalog(_settings, out string message);
        if (ok)
        {
            UnityEngine.Debug.Log($"[ActionFitPackageManager] {message}");
            Reload();
        }
        else
        {
            UnityEngine.Debug.LogError($"[ActionFitPackageManager] Update failed: {message}");
            EditorUtility.DisplayDialog("ActionFit Package Manager", message, "OK");
        }
    }

    private static List<PackageVersion> ReadCatalog(string path)
    {
        string[] lines = File.ReadAllLines(path);
        if (lines.Length < 2) return new List<PackageVersion>();

        var header = SplitCsvLine(lines[0]).Select(h => h.Trim()).ToArray();
        var index = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        for (int i = 0; i < header.Length; i++) index[header[i]] = i;

        var rows = new List<PackageVersion>();
        for (int i = 1; i < lines.Length; i++)
        {
            if (string.IsNullOrWhiteSpace(lines[i])) continue;
            if (lines[i].Contains("(string)", StringComparison.OrdinalIgnoreCase)) continue;

            string[] cols = SplitCsvLine(lines[i]);
            var row = new PackageVersion
            {
                Id = Get(cols, index, "package_id"),
                DisplayName = Get(cols, index, "display_name"),
                Owner = Get(cols, index, "owner"),
                RepoUrl = Get(cols, index, "repo_url"),
                Version = Get(cols, index, "version"),
                Status = Get(cols, index, "status"),
                IsLatest = IsTrue(Get(cols, index, "is_latest")),
                UnityMin = Get(cols, index, "unity_min"),
                Description = Get(cols, index, "description"),
                Changelog = Get(cols, index, "changelog"),
                Dependencies = Get(cols, index, "dependencies"),
            };

            if (!string.IsNullOrWhiteSpace(row.Id) &&
                !string.IsNullOrWhiteSpace(row.RepoUrl) &&
                !string.IsNullOrWhiteSpace(row.Version))
                rows.Add(row);
        }

        return rows;
    }

    private void ApplyPackage(PackageGroup package, PackageVersion version)
    {
        string manifestPath = Path.GetFullPath(ManifestPath);
        if (!File.Exists(manifestPath))
        {
            EditorUtility.DisplayDialog("ActionFit Package Manager", "Packages/manifest.json not found.", "OK");
            return;
        }

        string manifest = File.ReadAllText(manifestPath);
        var changes = new List<string>();

        foreach (var dep in ParseDependencies(version.Dependencies))
        {
            var depVersion = FindVersion(dep.Id, dep.Version);
            if (depVersion == null)
            {
                UnityEngine.Debug.LogWarning($"[ActionFitPackageManager] Dependency not found in catalog: {dep.Id}@{dep.Version}");
                continue;
            }

            manifest = SetDependency(manifest, depVersion.Id, depVersion.PackageUrl);
            changes.Add($"{depVersion.Id} -> {depVersion.PackageUrl}");
        }

        manifest = SetDependency(manifest, version.Id, version.PackageUrl);
        changes.Add($"{version.Id} -> {version.PackageUrl}");

        File.WriteAllText(manifestPath, manifest, new UTF8Encoding(false));
        UnityEngine.Debug.Log("[ActionFitPackageManager] Updated manifest:\n" + string.Join("\n", changes));

        AssetDatabase.Refresh();
        Client.Resolve();
    }

    private void RemovePackage(PackageGroup package)
    {
        if (!EditorUtility.DisplayDialog(
                "ActionFit Package Manager",
                $"Remove {package.Id} from Packages/manifest.json?\n\nEmbedded packages under Packages/ are not removed by this action.",
                "Remove",
                "Cancel"))
            return;

        string manifestPath = Path.GetFullPath(ManifestPath);
        if (!File.Exists(manifestPath))
        {
            EditorUtility.DisplayDialog("ActionFit Package Manager", "Packages/manifest.json not found.", "OK");
            return;
        }

        string manifest = File.ReadAllText(manifestPath);
        manifest = RemoveDependency(manifest, package.Id, out bool removed);
        if (!removed)
        {
            UnityEngine.Debug.LogWarning($"[ActionFitPackageManager] Package is not in manifest: {package.Id}");
            return;
        }

        File.WriteAllText(manifestPath, manifest, new UTF8Encoding(false));
        UnityEngine.Debug.Log($"[ActionFitPackageManager] Removed manifest dependency: {package.Id}");

        AssetDatabase.Refresh();
        Client.Resolve();
    }

    private PackageVersion FindVersion(string packageId, string version)
    {
        var group = _packages.FirstOrDefault(p => p.Id == packageId);
        if (group == null) return null;
        if (!string.IsNullOrWhiteSpace(version))
            return group.Versions.FirstOrDefault(v => v.Version == version);
        return group.Versions.FirstOrDefault(v => v.IsLatest) ?? group.Versions.FirstOrDefault();
    }

    private static string SetDependency(string manifest, string packageId, string value)
    {
        string escapedId = Regex.Escape(packageId);
        var existing = new Regex($"(\"{escapedId}\"\\s*:\\s*\")([^\"]*)(\"\\s*,?)");
        if (existing.IsMatch(manifest))
            return existing.Replace(manifest, $"$1{value}$3", 1);

        int dependenciesStart = manifest.IndexOf("\"dependencies\"", StringComparison.Ordinal);
        if (dependenciesStart < 0) throw new InvalidOperationException("manifest.json has no dependencies block.");

        int openBrace = manifest.IndexOf('{', dependenciesStart);
        int closeBrace = FindMatchingBrace(manifest, openBrace);
        string dependenciesBody = manifest.Substring(openBrace + 1, closeBrace - openBrace - 1);
        bool hasExistingDependencies = !string.IsNullOrWhiteSpace(dependenciesBody);
        string suffix = hasExistingDependencies ? ",\n" : "\n";
        string insert = $"        \"{packageId}\": \"{value}\"{suffix}";
        return manifest.Insert(openBrace + 1, "\n" + insert);
    }

    private static string RemoveDependency(string manifest, string packageId, out bool removed)
    {
        removed = false;
        string newline = manifest.Contains("\r\n") ? "\r\n" : "\n";
        var lines = manifest.Replace("\r\n", "\n").Split('\n').ToList();
        var pattern = new Regex($"^\\s*\"{Regex.Escape(packageId)}\"\\s*:");

        int index = lines.FindIndex(line => pattern.IsMatch(line));
        if (index < 0) return manifest;

        bool removedLineHadComma = lines[index].TrimEnd().EndsWith(",", StringComparison.Ordinal);
        lines.RemoveAt(index);

        if (!removedLineHadComma)
        {
            for (int i = index - 1; i >= 0; i--)
            {
                if (!lines[i].Contains(":")) continue;
                lines[i] = Regex.Replace(lines[i], @",\s*$", "");
                break;
            }
        }

        removed = true;
        return string.Join(newline, lines);
    }

    private InstalledPackage GetInstalledVersion(string packageId)
    {
        string embeddedPackageJson = Path.GetFullPath($"Packages/{packageId}/package.json");
        if (File.Exists(embeddedPackageJson))
        {
            string version = ExtractJsonString(File.ReadAllText(embeddedPackageJson), "version");
            return new InstalledPackage(true, true, version, $"embedded ({version})");
        }

        string manifestPath = Path.GetFullPath(ManifestPath);
        if (!File.Exists(manifestPath)) return InstalledPackage.NotInstalled;

        string manifestValue = ExtractDependencyValue(File.ReadAllText(manifestPath), packageId);
        if (string.IsNullOrWhiteSpace(manifestValue)) return InstalledPackage.NotInstalled;

        string versionFromUrl = ExtractVersionFromPackageUrl(manifestValue);
        string label = string.IsNullOrWhiteSpace(versionFromUrl) ? manifestValue : versionFromUrl;
        return new InstalledPackage(true, false, versionFromUrl, label);
    }

    private static List<(string Id, string Version)> ParseDependencies(string raw)
    {
        var result = new List<(string Id, string Version)>();
        if (string.IsNullOrWhiteSpace(raw)) return result;

        foreach (string token in raw.Split(new[] { ';', ',', '|' }, StringSplitOptions.RemoveEmptyEntries))
        {
            string trimmed = token.Trim();
            int at = trimmed.IndexOf('@');
            result.Add(at > 0
                ? (trimmed[..at].Trim(), trimmed[(at + 1)..].Trim())
                : (trimmed, ""));
        }

        return result;
    }

    private static string ExtractDependencyValue(string manifest, string packageId)
    {
        var match = Regex.Match(manifest, $"\"{Regex.Escape(packageId)}\"\\s*:\\s*\"([^\"]*)\"");
        return match.Success ? match.Groups[1].Value : "";
    }

    private static string ExtractJsonString(string json, string key)
    {
        var match = Regex.Match(json, $"\"{Regex.Escape(key)}\"\\s*:\\s*\"([^\"]*)\"");
        return match.Success ? match.Groups[1].Value : "";
    }

    private static string ExtractVersionFromPackageUrl(string packageUrl)
    {
        int hash = packageUrl.LastIndexOf('#');
        return hash >= 0 && hash < packageUrl.Length - 1 ? packageUrl[(hash + 1)..] : "";
    }

    private static int FindMatchingBrace(string text, int openBrace)
    {
        int depth = 0;
        for (int i = openBrace; i < text.Length; i++)
        {
            if (text[i] == '{') depth++;
            if (text[i] == '}')
            {
                depth--;
                if (depth == 0) return i;
            }
        }

        throw new InvalidOperationException("Could not find dependencies block end.");
    }

    private static string Get(string[] cols, Dictionary<string, int> index, string key)
    {
        return index.TryGetValue(key, out int i) && i < cols.Length ? cols[i].Trim() : "";
    }

    private static bool IsTrue(string value)
    {
        return value.Equals("true", StringComparison.OrdinalIgnoreCase) ||
               value.Equals("1", StringComparison.OrdinalIgnoreCase) ||
               value.Equals("yes", StringComparison.OrdinalIgnoreCase);
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

    private sealed class PackageGroup
    {
        public PackageGroup(string id, List<PackageVersion> versions)
        {
            Id = id;
            Versions = versions;
            DisplayName = versions.FirstOrDefault()?.DisplayName ?? id;
            Owner = versions.FirstOrDefault(v => !string.IsNullOrWhiteSpace(v.Owner))?.Owner ?? "ActionFit";
            LatestVersionLabel = versions.FirstOrDefault(v => v.IsLatest)?.Version ?? versions.FirstOrDefault()?.Version ?? "";
        }

        public string Id { get; }
        public string DisplayName { get; }
        public string Owner { get; }
        public string LatestVersionLabel { get; }
        public List<PackageVersion> Versions { get; }
    }

    private sealed class PackageVersionComparer : IComparer<string>
    {
        public static readonly PackageVersionComparer Instance = new();

        public int Compare(string x, string y)
        {
            var left = ParseVersion(x);
            var right = ParseVersion(y);
            for (int i = 0; i < Math.Max(left.Count, right.Count); i++)
            {
                int l = i < left.Count ? left[i] : 0;
                int r = i < right.Count ? right[i] : 0;
                int cmp = l.CompareTo(r);
                if (cmp != 0) return cmp;
            }

            return string.Compare(x, y, StringComparison.OrdinalIgnoreCase);
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
    }

    private sealed class PackageVersion
    {
        public string Id;
        public string DisplayName;
        public string Owner;
        public string RepoUrl;
        public string Version;
        public string Status;
        public bool IsLatest;
        public string UnityMin;
        public string Description;
        public string Changelog;
        public string Dependencies;
        public string PackageUrl => $"{RepoUrl}#{Version}";
        public string VersionLabel => IsLatest ? $"{Version} ({Status}, latest)" : $"{Version} ({Status})";
    }

    private readonly struct InstalledPackage
    {
        public static readonly InstalledPackage NotInstalled = new(false, false, "", "not installed");

        public InstalledPackage(bool isInstalled, bool isEmbedded, string version, string label)
        {
            IsInstalled = isInstalled;
            IsEmbedded = isEmbedded;
            Version = version;
            Label = label;
        }

        public bool IsInstalled { get; }
        public bool IsEmbedded { get; }
        public string Version { get; }
        public string Label { get; }
    }
}
#endif
