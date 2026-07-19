#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using UnityEditor;
using UnityEngine;

internal readonly struct ActionFitPackageManagerInstalledState
{
    public static readonly ActionFitPackageManagerInstalledState NotInstalled =
        new(false, false, "", "not installed");

    public ActionFitPackageManagerInstalledState(
        bool isInstalled,
        bool isEmbedded,
        string version,
        string label)
    {
        IsInstalled = isInstalled;
        IsEmbedded = isEmbedded;
        Version = version ?? "";
        Label = label ?? "";
    }

    public bool IsInstalled { get; }
    public bool IsEmbedded { get; }
    public string Version { get; }
    public string Label { get; }
}

internal sealed class ActionFitPackageManagerReadStatistics
{
    public int ManifestReads { get; internal set; }
    public int PackageRootEnumerations { get; internal set; }
    public int PackageJsonReads { get; internal set; }
}

internal sealed class ActionFitPackageManagerInstalledStateSnapshot
{
    private readonly Dictionary<string, ActionFitPackageManagerInstalledState> _states;

    public ActionFitPackageManagerInstalledStateSnapshot(
        Dictionary<string, ActionFitPackageManagerInstalledState> states,
        ActionFitPackageManagerReadStatistics statistics,
        string manifestText = "",
        bool manifestExists = false)
    {
        _states = states ?? new Dictionary<string, ActionFitPackageManagerInstalledState>(StringComparer.Ordinal);
        Statistics = statistics ?? new ActionFitPackageManagerReadStatistics();
        ManifestText = manifestText ?? "";
        ManifestExists = manifestExists;
    }

    public ActionFitPackageManagerReadStatistics Statistics { get; }
    public string ManifestText { get; }
    public bool ManifestExists { get; }
    public IEnumerable<KeyValuePair<string, ActionFitPackageManagerInstalledState>> States => _states;

    public ActionFitPackageManagerInstalledState Get(string packageId)
    {
        return !string.IsNullOrWhiteSpace(packageId) && _states.TryGetValue(packageId, out var state)
            ? state
            : ActionFitPackageManagerInstalledState.NotInstalled;
    }
}

internal static class ActionFitPackageManagerInstalledStateLoader
{
    public static ActionFitPackageManagerInstalledStateSnapshot Capture(
        IEnumerable<string> packageIds,
        string projectRoot)
    {
        if (string.IsNullOrWhiteSpace(projectRoot))
            throw new ArgumentException("Project root is required.", nameof(projectRoot));

        string fullProjectRoot = Path.GetFullPath(projectRoot);
        string packagesRoot = Path.Combine(fullProjectRoot, "Packages");
        string manifestPath = Path.Combine(packagesRoot, "manifest.json");
        var statistics = new ActionFitPackageManagerReadStatistics();
        var requestedIds = (packageIds ?? Array.Empty<string>())
            .Where(id => !string.IsNullOrWhiteSpace(id))
            .Distinct(StringComparer.Ordinal)
            .ToArray();

        bool manifestExists = File.Exists(manifestPath);
        string manifest = "";
        if (manifestExists)
        {
            manifest = File.ReadAllText(manifestPath);
            statistics.ManifestReads++;
        }

        var packageDirectories = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (Directory.Exists(packagesRoot))
        {
            statistics.PackageRootEnumerations++;
            foreach (string directory in Directory.GetDirectories(packagesRoot, "*", SearchOption.TopDirectoryOnly))
                packageDirectories[Path.GetFileName(directory)] = directory;
        }

        var manifestDependencies = requestedIds.ToDictionary(
            id => id,
            id => ExtractDependencyValue(manifest, id),
            StringComparer.Ordinal);
        var packageManifestCache = new Dictionary<string, (string Name, string Version)>(StringComparer.OrdinalIgnoreCase);
        var states = new Dictionary<string, ActionFitPackageManagerInstalledState>(StringComparer.Ordinal);

        foreach (string packageId in requestedIds)
        {
            if (packageDirectories.TryGetValue(packageId, out string embeddedPath) &&
                TryReadPackageManifest(embeddedPath, packageManifestCache, statistics, out string embeddedName, out string embeddedVersion) &&
                string.Equals(embeddedName, packageId, StringComparison.Ordinal) &&
                !string.IsNullOrWhiteSpace(embeddedVersion))
            {
                states[packageId] = new ActionFitPackageManagerInstalledState(
                    true,
                    true,
                    embeddedVersion,
                    $"embedded ({embeddedVersion})");
                continue;
            }

            string dependency = manifestDependencies[packageId];
            if (string.IsNullOrWhiteSpace(dependency))
            {
                states[packageId] = ActionFitPackageManagerInstalledState.NotInstalled;
                continue;
            }

            if (!IsLocalPackageDependency(dependency))
            {
                string version = ExtractVersionFromPackageUrl(dependency);
                states[packageId] = new ActionFitPackageManagerInstalledState(
                    true,
                    false,
                    version,
                    string.IsNullOrWhiteSpace(version) ? dependency : version);
                continue;
            }

            string relative = dependency.Trim()[5..];
            string localPath = Path.GetFullPath(Path.Combine(packagesRoot, relative));
            if (!TryReadPackageManifest(localPath, packageManifestCache, statistics, out _, out string localVersion))
            {
                localVersion = "";
            }

            states[packageId] = new ActionFitPackageManagerInstalledState(
                true,
                true,
                localVersion,
                string.IsNullOrWhiteSpace(localVersion) ? dependency : $"embedded ({localVersion})");
        }

        return new ActionFitPackageManagerInstalledStateSnapshot(
            states,
            statistics,
            manifest,
            manifestExists);
    }

    private static bool TryReadPackageManifest(
        string packagePath,
        IDictionary<string, (string Name, string Version)> cache,
        ActionFitPackageManagerReadStatistics statistics,
        out string name,
        out string version)
    {
        string packageJsonPath = Path.GetFullPath(Path.Combine(packagePath, "package.json"));
        if (cache.TryGetValue(packageJsonPath, out var cached))
        {
            name = cached.Name;
            version = cached.Version;
            return !string.IsNullOrWhiteSpace(name);
        }

        if (!File.Exists(packageJsonPath))
        {
            cache[packageJsonPath] = ("", "");
            name = "";
            version = "";
            return false;
        }

        string json = File.ReadAllText(packageJsonPath);
        statistics.PackageJsonReads++;
        name = ExtractJsonString(json, "name");
        version = ExtractJsonString(json, "version");
        cache[packageJsonPath] = (name, version);
        return !string.IsNullOrWhiteSpace(name);
    }

    private static string ExtractDependencyValue(string manifest, string packageId)
    {
        if (string.IsNullOrWhiteSpace(manifest)) return "";
        Match match = Regex.Match(manifest, $"\"{Regex.Escape(packageId)}\"\\s*:\\s*\"([^\"]*)\"");
        return match.Success ? match.Groups[1].Value : "";
    }

    private static string ExtractJsonString(string json, string key)
    {
        Match match = Regex.Match(json ?? "", $"\"{Regex.Escape(key)}\"\\s*:\\s*\"([^\"]*)\"");
        return match.Success ? match.Groups[1].Value : "";
    }

    private static bool IsLocalPackageDependency(string dependency)
    {
        return dependency.Trim().StartsWith("file:", StringComparison.OrdinalIgnoreCase);
    }

    private static string ExtractVersionFromPackageUrl(string packageUrl)
    {
        int hash = packageUrl.LastIndexOf('#');
        return hash >= 0 && hash < packageUrl.Length - 1 ? packageUrl[(hash + 1)..] : "";
    }
}

internal sealed class ActionFitContentBundleManagerInspection
{
    public ActionFitContentBundleManagerInspection(
        ActionFitContentBundleStatus[] statuses,
        Dictionary<string, string> requiredBundleByPackage,
        Dictionary<string, string> managedBundleByPackage)
    {
        Statuses = statuses ?? Array.Empty<ActionFitContentBundleStatus>();
        RequiredBundleByPackage = requiredBundleByPackage ?? new Dictionary<string, string>(StringComparer.Ordinal);
        ManagedBundleByPackage = managedBundleByPackage ?? new Dictionary<string, string>(StringComparer.Ordinal);
    }

    public ActionFitContentBundleStatus[] Statuses { get; }
    public Dictionary<string, string> RequiredBundleByPackage { get; }
    public Dictionary<string, string> ManagedBundleByPackage { get; }
}

internal sealed class ActionFitPackageManagerSnapshot
{
    private static readonly ActionFitPackageManagerInstalledStateSnapshot EmptyInstalledStates =
        new(new Dictionary<string, ActionFitPackageManagerInstalledState>(StringComparer.Ordinal),
            new ActionFitPackageManagerReadStatistics());

    private readonly Dictionary<string, string> _requiredBundleByPackage;
    private readonly Dictionary<string, string> _managedBundleByPackage;
    private readonly HashSet<string> _projectOverridePackageIds;
    private readonly Dictionary<string, ActionFitPackageSkillStatus> _skillStatuses;
    private readonly Dictionary<string, string> _localVotes;

    private ActionFitPackageManagerSnapshot(
        ActionFitPackageManagerInstalledStateSnapshot installedStates,
        ActionFitContentBundleStatus[] contentBundleStatuses,
        Dictionary<string, string> requiredBundleByPackage,
        Dictionary<string, string> managedBundleByPackage,
        HashSet<string> projectOverridePackageIds,
        Dictionary<string, ActionFitPackageSkillStatus> skillStatuses,
        Dictionary<string, string> localVotes)
    {
        InstalledStates = installedStates ?? EmptyInstalledStates;
        ContentBundleStatuses = contentBundleStatuses ?? Array.Empty<ActionFitContentBundleStatus>();
        _requiredBundleByPackage = requiredBundleByPackage ?? new Dictionary<string, string>(StringComparer.Ordinal);
        _managedBundleByPackage = managedBundleByPackage ?? new Dictionary<string, string>(StringComparer.Ordinal);
        _projectOverridePackageIds = projectOverridePackageIds ?? new HashSet<string>(StringComparer.Ordinal);
        _skillStatuses = skillStatuses ?? new Dictionary<string, ActionFitPackageSkillStatus>(StringComparer.OrdinalIgnoreCase);
        _localVotes = localVotes ?? new Dictionary<string, string>(StringComparer.Ordinal);
    }

    public static ActionFitPackageManagerSnapshot Empty { get; } = new(
        EmptyInstalledStates,
        Array.Empty<ActionFitContentBundleStatus>(),
        new Dictionary<string, string>(StringComparer.Ordinal),
        new Dictionary<string, string>(StringComparer.Ordinal),
        new HashSet<string>(StringComparer.Ordinal),
        new Dictionary<string, ActionFitPackageSkillStatus>(StringComparer.OrdinalIgnoreCase),
        new Dictionary<string, string>(StringComparer.Ordinal));

    public ActionFitPackageManagerInstalledStateSnapshot InstalledStates { get; }
    public ActionFitContentBundleStatus[] ContentBundleStatuses { get; }

    public static ActionFitPackageManagerSnapshot Capture(IEnumerable<string> packageIds, string projectRoot)
    {
        string[] ids = (packageIds ?? Array.Empty<string>())
            .Where(id => !string.IsNullOrWhiteSpace(id))
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        ActionFitPackageManagerInstalledStateSnapshot installedStates =
            ActionFitPackageManagerInstalledStateLoader.Capture(ids, projectRoot);

        ActionFitContentBundleManagerInspection contentInspection =
            ActionFitContentBundleApi.InspectForPackageManager(
                installedStates.ManifestText,
                installedStates.ManifestExists);
        var projectOverrides = new HashSet<string>(
            ActionFitPackageProjectOverrideApi.GetRegisteredPackageIdsForPackageManager(),
            StringComparer.Ordinal);
        var skillStatuses = new Dictionary<string, ActionFitPackageSkillStatus>(StringComparer.OrdinalIgnoreCase);
        try
        {
            foreach (ActionFitPackageSkillStatus status in ActionFitPackageSkillBootstrap.InspectRegisteredSkills().Packages)
                skillStatuses[status.PackageId] = status;
        }
        catch (Exception exception)
        {
            Debug.LogWarning($"[ActionFitPackageManager] Agent skill status inspection failed: {exception.Message}");
        }

        var localVotes = ids.ToDictionary(
            id => id,
            ActionFitPackageCommunityClient.GetLocalVote,
            StringComparer.Ordinal);
        return new ActionFitPackageManagerSnapshot(
            installedStates,
            contentInspection.Statuses,
            contentInspection.RequiredBundleByPackage,
            contentInspection.ManagedBundleByPackage,
            projectOverrides,
            skillStatuses,
            localVotes);
    }

    public bool TryGetRequiredBundle(string packageId, out string bundleDisplayName)
    {
        return _requiredBundleByPackage.TryGetValue(packageId, out bundleDisplayName);
    }

    public bool TryGetManagedBundle(string packageId, out string bundleDisplayName)
    {
        return _managedBundleByPackage.TryGetValue(packageId, out bundleDisplayName);
    }

    public bool IsProjectOverride(string packageId)
    {
        return _projectOverridePackageIds.Contains(packageId);
    }

    public bool TryGetSkillStatus(string packageId, out ActionFitPackageSkillStatus status)
    {
        return _skillStatuses.TryGetValue(packageId, out status);
    }

    public string GetLocalVote(string packageId)
    {
        return _localVotes.TryGetValue(packageId, out string vote) ? vote : "";
    }
}

internal static class ActionFitPackageManagerRefreshSignal
{
    private static bool _pending;

    public static event Action RefreshRequested;
    internal static bool IsPending => _pending;

    public static void Request()
    {
        if (_pending) return;
        _pending = true;
        EditorApplication.delayCall += DispatchPending;
    }

    internal static void FlushForTests()
    {
        if (!_pending) return;
        EditorApplication.delayCall -= DispatchPending;
        DispatchPending();
    }

    private static void DispatchPending()
    {
        if (!_pending) return;
        _pending = false;
        RefreshRequested?.Invoke();
    }
}

internal sealed class ActionFitPackageManagerAssetPostprocessor : AssetPostprocessor
{
    private static void OnPostprocessAllAssets(
        string[] importedAssets,
        string[] deletedAssets,
        string[] movedAssets,
        string[] movedFromAssetPaths)
    {
        if (ShouldRefresh(importedAssets) ||
            ShouldRefresh(deletedAssets) ||
            ShouldRefresh(movedAssets) ||
            ShouldRefresh(movedFromAssetPaths))
        {
            ActionFitPackageManagerRefreshSignal.Request();
        }
    }

    internal static bool ShouldRefresh(IEnumerable<string> paths)
    {
        foreach (string pathValue in paths ?? Array.Empty<string>())
        {
            string path = (pathValue ?? "").Replace("\\", "/");
            if (string.Equals(path, ActionFitPackageCatalogSettingsProvider.LocalCatalogPath, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(path, "Packages/com.actionfit.custompackagemanager/Editor/Catalog/package_catalog.csv", StringComparison.OrdinalIgnoreCase) ||
                IsTopLevelPackageManifest(path))
            {
                return true;
            }
        }

        return false;
    }

    private static bool IsTopLevelPackageManifest(string path)
    {
        string[] segments = path.Split('/');
        return segments.Length == 3 &&
               string.Equals(segments[0], "Packages", StringComparison.OrdinalIgnoreCase) &&
               !string.IsNullOrWhiteSpace(segments[1]) &&
               string.Equals(segments[2], "package.json", StringComparison.OrdinalIgnoreCase);
    }
}
#endif
