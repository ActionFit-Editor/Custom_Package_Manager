#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEngine;

[Serializable]
public sealed class ActionFitPackageProjectOverrideStatus
{
    public string PackageId = "";
    public bool Active;
    public bool Modified;
    public string BaseVersion = "";
    public string BaseRepositoryUrl = "";
    public string BaseRevision = "";
    public string BaseContentHash = "";
    public string CurrentContentHash = "";
    public string EmbeddedPath = "";
    public string UpstreamVersion = "";
    public bool UpstreamUpdateAvailable;
    public string RestoreGuidance = "";
    public string ForkGuidance = "";
}

[Serializable]
public sealed class ActionFitPackageProjectOverrideResult
{
    public bool Success;
    public bool Changed;
    public string Code = "";
    public string Message = "";
    public string PackageId = "";
    public ActionFitPackageProjectOverrideStatus Status;
}

/// <summary>
/// Tracks project-owned embedded package overrides separately from upstream package editing.
/// The project state stores only a credential-free public base URL and project-relative path,
/// never a private remote or machine-specific absolute path.
/// </summary>
public static class ActionFitPackageProjectOverrideApi
{
    public static ActionFitPackageProjectOverrideStatus[] GetStatuses()
    {
        return ActionFitPackageProjectOverrideStateStore.Load().overrides
            .OrderBy(item => item.packageId, StringComparer.Ordinal)
            .Select(BuildStatus)
            .ToArray();
    }

    public static ActionFitPackageProjectOverrideStatus GetStatus(string packageId)
    {
        ActionFitPackagePaths.ValidatePackageId(packageId);
        ActionFitPackageProjectOverrideRecord record = ActionFitPackageProjectOverrideStateStore.Load().overrides
            .FirstOrDefault(item => string.Equals(item.packageId, packageId, StringComparison.Ordinal));
        return record == null
            ? new ActionFitPackageProjectOverrideStatus { PackageId = packageId }
            : BuildStatus(record);
    }

    public static bool IsProjectOverride(string packageId)
    {
        if (string.IsNullOrWhiteSpace(packageId)) return false;
        return ActionFitPackageProjectOverrideStateStore.Load().overrides.Any(item =>
            string.Equals(item.packageId, packageId, StringComparison.Ordinal));
    }

    public static ActionFitPackageProjectOverrideResult CompleteRestoreToBase(string packageId)
    {
        try
        {
            ActionFitPackagePaths.ValidatePackageId(packageId);
            string manifest = File.Exists(ActionFitPackagePaths.ManifestPath)
                ? File.ReadAllText(ActionFitPackagePaths.ManifestPath)
                : "";
            string dependency = string.IsNullOrWhiteSpace(manifest)
                ? ""
                : ActionFitPackageManifestUtility.GetDependency(manifest, packageId);
            bool localDependency = dependency.Trim().StartsWith("file:", StringComparison.OrdinalIgnoreCase);
            bool localFolder = ActionFitPackageFileUtility.PhysicalDirectoryExists(ActionFitPackagePaths.PackagePath(packageId));
            if (localDependency || localFolder)
            {
                return Failure(
                    packageId,
                    "RESTORE_NOT_COMPLETE",
                    "Restore the package to a downloaded Git/catalog dependency and remove its embedded folder before completing override restoration.");
            }

            ActionFitPackageProjectOverrideStateFile state = ActionFitPackageProjectOverrideStateStore.Load();
            int originalCount = state.overrides.Length;
            state.overrides = state.overrides
                .Where(item => !string.Equals(item.packageId, packageId, StringComparison.Ordinal))
                .ToArray();
            if (state.overrides.Length == originalCount)
            {
                return new ActionFitPackageProjectOverrideResult
                {
                    Success = true,
                    Changed = false,
                    Code = "OVERRIDE_NOT_REGISTERED",
                    Message = $"No project override is registered for {packageId}.",
                    PackageId = packageId,
                };
            }

            ActionFitPackageProjectOverrideStateStore.Save(state);
            ActionFitPackageAiGuideRouter.EnsureProjectRouter();
            return new ActionFitPackageProjectOverrideResult
            {
                Success = true,
                Changed = true,
                Code = "RESTORE_COMPLETED",
                Message = $"Project override restoration was recorded: {packageId}",
                PackageId = packageId,
            };
        }
        catch (Exception exception)
        {
            return Failure(packageId, "RESTORE_STATE_FAILED", exception.Message);
        }
    }

    internal static ActionFitPackageProjectOverrideResult RegisterEmbedded(
        string packageId,
        string sourceVersion,
        string baseRepositoryUrl,
        string previousDependency,
        string embeddedPath)
    {
        try
        {
            ActionFitPackagePaths.ValidatePackageId(packageId);
            baseRepositoryUrl = NormalizePublicRepositoryUrl(baseRepositoryUrl);
            string fullPath = ActionFitPackagePaths.NormalizePhysicalPath(embeddedPath);
            ActionFitPackageFileUtility.ValidateLocalPackageFolder(packageId, fullPath);
            string relativePath = ActionFitPackagePaths.ToProjectRelativePath(fullPath);
            if (!relativePath.StartsWith("Packages/", StringComparison.Ordinal))
                throw new InvalidOperationException("Project override path must remain inside Packages/.");

            string contentHash = ActionFitPackageBaseline.ComputeContentHash(fullPath);
            ActionFitPackageProjectOverrideStateFile state = ActionFitPackageProjectOverrideStateStore.Load();
            ActionFitPackageProjectOverrideRecord record = state.overrides.FirstOrDefault(item =>
                string.Equals(item.packageId, packageId, StringComparison.Ordinal));
            bool created = record == null;
            record ??= new ActionFitPackageProjectOverrideRecord { packageId = packageId };
            record.baseVersion = sourceVersion ?? "";
            record.baseRepositoryUrl = baseRepositoryUrl ?? "";
            record.baseRevision = RevisionPart(previousDependency);
            record.baseContentHash = contentHash;
            record.embeddedPath = relativePath;
            record.createdUtc = created ? DateTime.UtcNow.ToString("O") : record.createdUtc;
            record.updatedUtc = DateTime.UtcNow.ToString("O");
            state.overrides = state.overrides
                .Where(item => !string.Equals(item.packageId, packageId, StringComparison.Ordinal))
                .Append(record)
                .OrderBy(item => item.packageId, StringComparer.Ordinal)
                .ToArray();
            ActionFitPackageProjectOverrideStateStore.Save(state);
            ActionFitPackageProjectOverrideStatus status = BuildStatus(record);
            return new ActionFitPackageProjectOverrideResult
            {
                Success = true,
                Changed = true,
                Code = created ? "OVERRIDE_REGISTERED" : "OVERRIDE_BASE_REFRESHED",
                Message = created
                    ? $"Project override registered: {packageId}"
                    : $"Project override base refreshed: {packageId}",
                PackageId = packageId,
                Status = status,
            };
        }
        catch (Exception exception)
        {
            return Failure(packageId, "OVERRIDE_STATE_FAILED", exception.Message);
        }
    }

    internal static string NormalizePublicRepositoryUrl(string repositoryUrl)
    {
        if (!Uri.TryCreate(repositoryUrl, UriKind.Absolute, out Uri repositoryUri) ||
            !string.Equals(repositoryUri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase) ||
            !string.IsNullOrWhiteSpace(repositoryUri.UserInfo))
        {
            throw new InvalidOperationException("Project override base repository must be a credential-free public HTTPS URL.");
        }

        string query = repositoryUri.Query.TrimStart('?');
        if (!string.IsNullOrWhiteSpace(query))
        {
            string[] parts = query.Split('&', StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length != 1 ||
                !string.Equals(parts[0].Split('=')[0], "path", StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException("Project override base repository URL may contain only the public Git UPM path query.");
            }
        }

        return repositoryUri.GetLeftPart(UriPartial.Path) +
               (string.IsNullOrWhiteSpace(repositoryUri.Query) ? "" : repositoryUri.Query);
    }

    private static ActionFitPackageProjectOverrideStatus BuildStatus(ActionFitPackageProjectOverrideRecord record)
    {
        string packagePath = ActionFitPackagePaths.PackagePath(record.packageId);
        string currentHash = "";
        if (ActionFitPackageFileUtility.PhysicalDirectoryExists(packagePath))
        {
            try { currentHash = ActionFitPackageBaseline.ComputeContentHash(packagePath); }
            catch { currentHash = ""; }
        }

        string upstreamVersion = "";
        try
        {
            ActionFitPackageInspectionResult inspection = ActionFitPackageWorkflowApi.Inspect(
                new ActionFitPackageInspectionRequest
                {
                    PackageId = record.packageId,
                    RefreshCatalog = false,
                });
            if (inspection.Success) upstreamVersion = inspection.LatestVersion ?? "";
        }
        catch
        {
            upstreamVersion = "";
        }

        return new ActionFitPackageProjectOverrideStatus
        {
            PackageId = record.packageId,
            Active = !string.IsNullOrWhiteSpace(currentHash),
            Modified = !string.IsNullOrWhiteSpace(currentHash) &&
                       !string.Equals(currentHash, record.baseContentHash, StringComparison.OrdinalIgnoreCase),
            BaseVersion = record.baseVersion,
            BaseRepositoryUrl = record.baseRepositoryUrl,
            BaseRevision = record.baseRevision,
            BaseContentHash = record.baseContentHash,
            CurrentContentHash = currentHash,
            EmbeddedPath = record.embeddedPath,
            UpstreamVersion = upstreamVersion,
            UpstreamUpdateAvailable = !string.IsNullOrWhiteSpace(upstreamVersion) &&
                                      ActionFitPackageVersionComparer.Instance.Compare(upstreamVersion, record.baseVersion) > 0,
            RestoreGuidance = "Use Package Manager to restore a downloaded Git/catalog dependency, remove the embedded folder, then call CompleteRestoreToBase.",
            ForkGuidance = "Create a new package ID and repository before publishing; project overrides are never upstream publish candidates.",
        };
    }

    private static string RevisionPart(string dependency)
    {
        int hash = (dependency ?? "").LastIndexOf('#');
        return hash < 0 || hash == dependency.Length - 1 ? "" : dependency[(hash + 1)..];
    }

    private static ActionFitPackageProjectOverrideResult Failure(string packageId, string code, string message)
    {
        return new ActionFitPackageProjectOverrideResult
        {
            Success = false,
            Changed = false,
            Code = code,
            Message = message ?? "",
            PackageId = packageId ?? "",
        };
    }
}

[Serializable]
internal sealed class ActionFitPackageProjectOverrideStateFile
{
    public int schemaVersion = 1;
    public ActionFitPackageProjectOverrideRecord[] overrides = Array.Empty<ActionFitPackageProjectOverrideRecord>();
}

[Serializable]
internal sealed class ActionFitPackageProjectOverrideRecord
{
    public string packageId = "";
    public string baseRepositoryUrl = "";
    public string baseVersion = "";
    public string baseRevision = "";
    public string baseContentHash = "";
    public string embeddedPath = "";
    public string createdUtc = "";
    public string updatedUtc = "";
}

internal static class ActionFitPackageProjectOverrideStateStore
{
    private const string RelativePath = "ProjectSettings/ActionFitPackageOverrides.json";
    public static string Path => ActionFitPackagePaths.ProjectRelativeFullPath(RelativePath);

    public static ActionFitPackageProjectOverrideStateFile Load()
    {
        if (!File.Exists(Path)) return new ActionFitPackageProjectOverrideStateFile();
        string json = File.ReadAllText(Path);
        if (string.IsNullOrWhiteSpace(json)) return new ActionFitPackageProjectOverrideStateFile();
        ActionFitPackageProjectOverrideStateFile state = JsonUtility.FromJson<ActionFitPackageProjectOverrideStateFile>(json);
        if (state == null || state.schemaVersion != 1)
            throw new InvalidOperationException("ActionFitPackageOverrides.json has an unsupported schema.");
        state.overrides ??= Array.Empty<ActionFitPackageProjectOverrideRecord>();
        return state;
    }

    public static void Save(ActionFitPackageProjectOverrideStateFile state)
    {
        state ??= new ActionFitPackageProjectOverrideStateFile();
        state.overrides ??= Array.Empty<ActionFitPackageProjectOverrideRecord>();
        state.overrides = state.overrides.OrderBy(item => item.packageId, StringComparer.Ordinal).ToArray();
        ActionFitPackageManifestUtility.WriteAtomic(Path, JsonUtility.ToJson(state, true) + "\n", false);
    }
}
#endif
