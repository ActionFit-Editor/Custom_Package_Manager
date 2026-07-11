#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using UnityEngine;

/// <summary>
/// Request for catalog refresh, installed-version comparison, and workflow recommendation.
/// </summary>
[Serializable]
public sealed class ActionFitPackageInspectionRequest
{
    public string PackageId;
    public bool RefreshCatalog = true;
}

/// <summary>
/// One safe workflow that an AI can present to the user before making package changes.
/// </summary>
[Serializable]
public sealed class ActionFitPackageWorkflowOption
{
    public string Code;
    public bool Recommended;
    public bool CreatesNewRepository;
    public bool ReplacesInstalledVersion;
    public bool RequiresExplicitPublishApproval;
    public string Description;
}

/// <summary>
/// Structured package state derived from the shared sheet, manifest, and installed package.
/// </summary>
[Serializable]
public sealed class ActionFitPackageInspectionResult
{
    public bool Success;
    public string Code;
    public string Message;
    public string PackageId;
    public bool CatalogRefreshRequested;
    public bool CatalogRefreshed;
    public string CatalogMessage;
    public string CatalogPath;
    public string CatalogUpdatedUtc;
    public bool Installed;
    public bool Embedded;
    public string InstalledVersion;
    public string InstalledDependency;
    public string LocalChangeState;
    public bool CatalogPackageFound;
    public bool CatalogContainsInstalledVersion;
    public string[] CatalogVersions = Array.Empty<string>();
    public string LatestVersion;
    public string LatestPackageUrl;
    public string RepositoryUrl;
    public string LatestChangelog;
    public string LatestDependencies;
    public string VersionRelation;
    public bool InstalledVersionIsLatest;
    public string RecommendedAction;
    public ActionFitPackageWorkflowOption[] Options = Array.Empty<ActionFitPackageWorkflowOption>();
    public string[] Warnings = Array.Empty<string>();
}

/// <summary>
/// Dialog-free AI API for refreshing the shared catalog and deciding a package edit workflow.
/// </summary>
public static class ActionFitPackageWorkflowApi
{
    private const string PackageCatalogFallbackPath = "Packages/com.actionfit.custompackagemanager/Editor/Catalog/package_catalog.csv";

    /// <summary>
    /// Refreshes the shared sheet when requested and compares its latest version with the installed package.
    /// </summary>
    public static ActionFitPackageInspectionResult Inspect(ActionFitPackageInspectionRequest request)
    {
        var result = new ActionFitPackageInspectionResult
        {
            PackageId = request?.PackageId ?? "",
            CatalogRefreshRequested = request?.RefreshCatalog ?? false,
        };

        try
        {
            if (request == null) throw new ArgumentNullException(nameof(request));
            ActionFitPackagePaths.ValidatePackageId(request.PackageId);
            if (!request.PackageId.StartsWith("com.actionfit.", StringComparison.Ordinal))
                throw new InvalidOperationException("Package workflow API is limited to com.actionfit.* packages.");

            var warnings = new List<string>();
            if (request.RefreshCatalog)
            {
                ActionFitPackageCatalogSettings_SO settings = ActionFitPackageCatalogSettingsProvider.FindOrCreate();
                result.CatalogRefreshed = ActionFitPackageCatalogUpdater.UpdateCatalog(settings, out string refreshMessage, false);
                result.CatalogMessage = refreshMessage;
                if (!result.CatalogRefreshed)
                    warnings.Add($"Shared catalog refresh failed; inspection used the current local catalog when available: {refreshMessage}");
            }

            string catalogPath = ResolveCatalogPath();
            result.CatalogPath = catalogPath;
            string fullCatalogPath = ActionFitPackagePaths.ProjectRelativeFullPath(catalogPath);
            if (!File.Exists(fullCatalogPath))
                throw new FileNotFoundException("Package catalog CSV was not found. Configure the catalog settings and refresh the shared sheet.", fullCatalogPath);
            result.CatalogUpdatedUtc = File.GetLastWriteTimeUtc(fullCatalogPath).ToString("O");

            List<CatalogVersion> catalogVersions = ReadCatalog(fullCatalogPath)
                .Where(version => string.Equals(version.PackageId, request.PackageId, StringComparison.Ordinal))
                .OrderByDescending(version => version.Version, ActionFitPackageVersionComparer.Instance)
                .ToList();
            CatalogVersion latest = catalogVersions.FirstOrDefault(version => version.IsLatest) ?? catalogVersions.FirstOrDefault();
            result.CatalogPackageFound = latest != null;
            result.CatalogVersions = catalogVersions.Select(version => version.Version).Distinct(StringComparer.Ordinal).ToArray();
            if (latest != null)
            {
                result.LatestVersion = latest.Version;
                result.RepositoryUrl = latest.RepositoryUrl;
                result.LatestPackageUrl = string.IsNullOrWhiteSpace(latest.RepositoryUrl)
                    ? ""
                    : $"{latest.RepositoryUrl}#{latest.Version}";
                result.LatestChangelog = latest.Changelog;
                result.LatestDependencies = latest.Dependencies;
            }

            ReadInstalledState(request.PackageId, result);
            result.CatalogContainsInstalledVersion = result.CatalogVersions.Contains(result.InstalledVersion, StringComparer.Ordinal);
            BuildRecommendation(result, warnings);
            result.Warnings = warnings.ToArray();
            result.Success = true;
            result.Code = "INSPECTED";
            result.Message = BuildSummary(result);
            return result;
        }
        catch (Exception ex)
        {
            result.Success = false;
            result.Code = "INSPECTION_FAILED";
            result.Message = ex.Message;
            return result;
        }
    }

    /// <summary>
    /// Executes an inspection request from JSON and returns a JSON result for AI connectors.
    /// </summary>
    public static string InspectJson(string requestJson)
    {
        try
        {
            var request = JsonUtility.FromJson<ActionFitPackageInspectionRequest>(requestJson ?? "");
            return JsonUtility.ToJson(Inspect(request), true);
        }
        catch (Exception ex)
        {
            return JsonUtility.ToJson(new ActionFitPackageInspectionResult
            {
                Success = false,
                Code = "INVALID_REQUEST_JSON",
                Message = ex.Message,
            }, true);
        }
    }

    private static void ReadInstalledState(string packageId, ActionFitPackageInspectionResult result)
    {
        string manifest = File.ReadAllText(ActionFitPackagePaths.ManifestPath);
        result.InstalledDependency = ActionFitPackageManifestUtility.GetDependency(manifest, packageId);

        UnityEditor.PackageManager.PackageInfo registered = null;
        try
        {
            registered = UnityEditor.PackageManager.PackageInfo.GetAllRegisteredPackages()
                .FirstOrDefault(package => string.Equals(package.name, packageId, StringComparison.Ordinal));
        }
        catch (Exception ex)
        {
            Debug.LogWarning($"[ActionFitPackageManager] Installed package lookup failed for {packageId}: {ex.Message}");
        }

        string localPackagePath = ActionFitPackagePaths.PackagePath(packageId);
        bool localFolderValid = ActionFitPackageFileUtility.IsValidLocalPackageFolder(packageId, localPackagePath, out _);
        bool localDependency = result.InstalledDependency.Trim().StartsWith("file:", StringComparison.OrdinalIgnoreCase);
        result.Installed = registered != null || !string.IsNullOrWhiteSpace(result.InstalledDependency) || localFolderValid;
        result.Embedded = localDependency || localFolderValid;

        if (localFolderValid)
            result.InstalledVersion = ActionFitPackageManifest.Read(Path.Combine(localPackagePath, "package.json")).Version;
        else
            result.InstalledVersion = registered?.version ?? "";

        result.LocalChangeState = result.Embedded
            ? ActionFitPackageBaseline.GetChangeState(packageId, localPackagePath)
            : "NOT_EMBEDDED";
    }

    private static void BuildRecommendation(ActionFitPackageInspectionResult result, List<string> warnings)
    {
        var options = new List<ActionFitPackageWorkflowOption>();
        if (!result.Installed)
        {
            result.VersionRelation = "NOT_INSTALLED";
            result.RecommendedAction = result.CatalogPackageFound ? "INSTALL_LATEST" : "CONFIGURE_CATALOG";
            if (result.CatalogPackageFound)
                options.Add(Option("INSTALL_LATEST", true, false, true, false, "Install the catalog latest version before editing."));
            result.Options = options.ToArray();
            return;
        }

        options.Add(Option(
            "FORK_CURRENT_AS_NEW_REPOSITORY",
            false,
            true,
            false,
            true,
            "Fork the currently installed source into a new package ID and publish only after explicit user approval."));

        if (!result.CatalogPackageFound)
        {
            result.VersionRelation = "CATALOG_MISSING";
            result.RecommendedAction = result.Embedded ? "EDIT_CURRENT_WITHOUT_CATALOG_BASELINE" : "EMBED_CURRENT_WITHOUT_CATALOG_BASELINE";
            options.Add(Option(
                result.Embedded ? "EDIT_CURRENT_WITHOUT_CATALOG_BASELINE" : "EMBED_CURRENT_WITHOUT_CATALOG_BASELINE",
                true,
                false,
                false,
                true,
                "Proceed only after confirming the package repository and next unused version manually."));
            warnings.Add("The package does not have a catalog row, so latest-version safety cannot be guaranteed.");
            result.Options = options.ToArray();
            return;
        }

        int comparison = ActionFitPackageVersionComparer.Instance.Compare(result.InstalledVersion, result.LatestVersion);
        result.VersionRelation = comparison < 0 ? "BEHIND_LATEST" : comparison > 0 ? "AHEAD_OF_CATALOG" : "MATCHES_LATEST";
        result.InstalledVersionIsLatest = comparison == 0;

        if (comparison < 0)
        {
            bool modifiedEmbedded = result.Embedded && string.Equals(result.LocalChangeState, "MODIFIED", StringComparison.Ordinal);
            result.RecommendedAction = modifiedEmbedded
                ? "FORK_CURRENT_AS_NEW_REPOSITORY"
                : "UPDATE_TO_LATEST_THEN_EMBED";
            options.Add(Option(
                "UPDATE_TO_LATEST_THEN_EMBED",
                !modifiedEmbedded,
                false,
                true,
                true,
                "Update to the shared-sheet latest version, embed it, bump to the next unused version, then update the existing repository after approval."));
            options.Add(Option(
                "UPDATE_LATEST_THEN_FORK_NEW_REPOSITORY",
                false,
                true,
                true,
                true,
                "Update to the latest source first, then fork it under a new package ID and repository."));
            if (modifiedEmbedded)
                warnings.Add("The embedded package has local modifications. Updating will not merge them; preserve them with a backup or fork first.");
        }
        else if (comparison == 0)
        {
            result.RecommendedAction = result.Embedded ? "EDIT_EMBEDDED_AND_BUMP_VERSION" : "EMBED_LATEST_AND_EDIT";
            options.Add(Option(
                result.Embedded ? "EDIT_EMBEDDED_AND_BUMP_VERSION" : "EMBED_LATEST_AND_EDIT",
                true,
                false,
                false,
                true,
                "Edit the catalog latest source, bump above the latest catalog version, and update the existing repository only after approval."));
        }
        else
        {
            result.RecommendedAction = result.Embedded ? "VERIFY_UNPUBLISHED_VERSION_THEN_CONTINUE" : "EMBED_CURRENT_AHEAD_OF_CATALOG";
            options.Add(Option(
                result.RecommendedAction,
                true,
                false,
                false,
                true,
                "The installed version is newer than the shared catalog. Verify remote tags and catalog state before publishing or changing versions."));
            warnings.Add("Installed version is ahead of the shared catalog; do not overwrite or reuse an existing remote tag.");
        }

        options[0].Recommended = string.Equals(result.RecommendedAction, "FORK_CURRENT_AS_NEW_REPOSITORY", StringComparison.Ordinal);
        result.Options = options.ToArray();
    }

    private static ActionFitPackageWorkflowOption Option(
        string code,
        bool recommended,
        bool createsNewRepository,
        bool replacesInstalledVersion,
        bool requiresPublishApproval,
        string description)
    {
        return new ActionFitPackageWorkflowOption
        {
            Code = code,
            Recommended = recommended,
            CreatesNewRepository = createsNewRepository,
            ReplacesInstalledVersion = replacesInstalledVersion,
            RequiresExplicitPublishApproval = requiresPublishApproval,
            Description = description,
        };
    }

    private static string BuildSummary(ActionFitPackageInspectionResult result)
    {
        string installed = result.Installed ? result.InstalledVersion : "not installed";
        string latest = result.CatalogPackageFound ? result.LatestVersion : "not found";
        return $"{result.PackageId}: installed={installed}, catalogLatest={latest}, relation={result.VersionRelation}, recommended={result.RecommendedAction}";
    }

    private static string ResolveCatalogPath()
    {
        string local = ActionFitPackageCatalogSettingsProvider.LocalCatalogPath;
        return File.Exists(ActionFitPackagePaths.ProjectRelativeFullPath(local)) ? local : PackageCatalogFallbackPath;
    }

    private static List<CatalogVersion> ReadCatalog(string path)
    {
        List<string> records = ReadCsvRecords(File.ReadAllText(path));
        if (records.Count < 2) return new List<CatalogVersion>();

        string[] header = SplitCsvLine(records[0]).Select(value => value.Trim()).ToArray();
        var index = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        for (int i = 0; i < header.Length; i++) index[header[i]] = i;

        var rows = new List<CatalogVersion>();
        for (int i = 1; i < records.Count; i++)
        {
            if (string.IsNullOrWhiteSpace(records[i]) || records[i].Contains("(string)", StringComparison.OrdinalIgnoreCase)) continue;
            string[] columns = SplitCsvLine(records[i]);
            var row = new CatalogVersion
            {
                PackageId = Get(columns, index, "package_id"),
                Version = Get(columns, index, "version"),
                RepositoryUrl = Get(columns, index, "repo_url"),
                IsLatest = IsTrue(Get(columns, index, "is_latest")),
                Changelog = Get(columns, index, "changelog"),
                Dependencies = Get(columns, index, "dependencies"),
            };
            if (!string.IsNullOrWhiteSpace(row.PackageId) && !string.IsNullOrWhiteSpace(row.Version)) rows.Add(row);
        }

        return rows;
    }

    private static List<string> ReadCsvRecords(string text)
    {
        var result = new List<string>();
        var row = new StringBuilder();
        bool inQuotes = false;
        text = (text ?? "").TrimStart('\uFEFF');
        for (int i = 0; i < text.Length; i++)
        {
            char c = text[i];
            if (c == '"')
            {
                if (inQuotes && i + 1 < text.Length && text[i + 1] == '"')
                {
                    row.Append(c).Append(text[++i]);
                    continue;
                }
                inQuotes = !inQuotes;
            }

            if ((c == '\n' || c == '\r') && !inQuotes)
            {
                if (c == '\r' && i + 1 < text.Length && text[i + 1] == '\n') i++;
                result.Add(row.ToString());
                row.Clear();
                continue;
            }
            row.Append(c);
        }
        if (row.Length > 0) result.Add(row.ToString());
        return result;
    }

    private static string[] SplitCsvLine(string line)
    {
        var result = new List<string>();
        var value = new StringBuilder();
        bool inQuotes = false;
        for (int i = 0; i < (line ?? "").Length; i++)
        {
            char c = line[i];
            if (c == '"')
            {
                if (inQuotes && i + 1 < line.Length && line[i + 1] == '"')
                {
                    value.Append('"');
                    i++;
                    continue;
                }
                inQuotes = !inQuotes;
                continue;
            }
            if (c == ',' && !inQuotes)
            {
                result.Add(value.ToString());
                value.Clear();
                continue;
            }
            value.Append(c);
        }
        result.Add(value.ToString());
        return result.ToArray();
    }

    private static string Get(string[] columns, IReadOnlyDictionary<string, int> index, string key)
    {
        return index.TryGetValue(key, out int position) && position >= 0 && position < columns.Length
            ? columns[position].Trim()
            : "";
    }

    private static bool IsTrue(string value)
    {
        return string.Equals(value, "true", StringComparison.OrdinalIgnoreCase) || value == "1";
    }

    private sealed class CatalogVersion
    {
        public string PackageId;
        public string Version;
        public string RepositoryUrl;
        public bool IsLatest;
        public string Changelog;
        public string Dependencies;
    }
}

internal sealed class ActionFitPackageVersionComparer : IComparer<string>
{
    public static readonly ActionFitPackageVersionComparer Instance = new();

    public int Compare(string left, string right)
    {
        List<int> leftParts = Parse(left);
        List<int> rightParts = Parse(right);
        for (int i = 0; i < Math.Max(leftParts.Count, rightParts.Count); i++)
        {
            int leftValue = i < leftParts.Count ? leftParts[i] : 0;
            int rightValue = i < rightParts.Count ? rightParts[i] : 0;
            int comparison = leftValue.CompareTo(rightValue);
            if (comparison != 0) return comparison;
        }
        return string.Compare(left, right, StringComparison.OrdinalIgnoreCase);
    }

    private static List<int> Parse(string version)
    {
        var result = new List<int>();
        foreach (string part in (version ?? "").Split('.'))
        {
            string digits = new string(part.TakeWhile(char.IsDigit).ToArray());
            result.Add(int.TryParse(digits, out int value) ? value : 0);
        }
        return result;
    }
}

/// <summary>
/// Batchmode entry point for refreshing and inspecting package workflow state.
/// </summary>
public static class ActionFitPackageWorkflowCli
{
    /// <summary>
    /// Runs with -actionFitInspectRequest &lt;path&gt; and -actionFitInspectResult &lt;path&gt;.
    /// </summary>
    public static void Run()
    {
        string requestPath = GetArgument("-actionFitInspectRequest");
        string resultPath = GetArgument("-actionFitInspectResult");
        ActionFitPackageInspectionResult result;
        try
        {
            if (string.IsNullOrWhiteSpace(requestPath))
                throw new InvalidOperationException("-actionFitInspectRequest is required.");
            if (string.IsNullOrWhiteSpace(resultPath))
                throw new InvalidOperationException("-actionFitInspectResult is required.");

            var request = JsonUtility.FromJson<ActionFitPackageInspectionRequest>(File.ReadAllText(Path.GetFullPath(requestPath)));
            result = ActionFitPackageWorkflowApi.Inspect(request);
        }
        catch (Exception ex)
        {
            result = new ActionFitPackageInspectionResult
            {
                Success = false,
                Code = "CLI_FAILED",
                Message = ex.Message,
            };
        }

        if (!string.IsNullOrWhiteSpace(resultPath))
        {
            string fullResultPath = Path.GetFullPath(resultPath);
            Directory.CreateDirectory(Path.GetDirectoryName(fullResultPath));
            File.WriteAllText(fullResultPath, JsonUtility.ToJson(result, true), new UTF8Encoding(false));
        }

        if (result.Success) Debug.Log($"[ActionFitPackageManager] Workflow inspection succeeded: {result.Message}");
        else Debug.LogError($"[ActionFitPackageManager] Workflow inspection failed: {result.Code} - {result.Message}");
    }

    private static string GetArgument(string name)
    {
        string[] args = Environment.GetCommandLineArgs();
        for (int i = 0; i < args.Length - 1; i++)
            if (string.Equals(args[i], name, StringComparison.OrdinalIgnoreCase))
                return args[i + 1];
        return "";
    }
}
#endif
