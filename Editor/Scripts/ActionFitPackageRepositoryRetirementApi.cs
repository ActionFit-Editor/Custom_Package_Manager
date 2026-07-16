#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using UnityEngine;
using Debug = UnityEngine.Debug;

public enum ActionFitPackageRepositoryRetirementMode
{
    Keep = 0,
    Archive = 1,
    Delete = 2,
}

[Serializable]
public sealed class ActionFitPackageRepositoryRetirementPrepareRequest
{
    public string PackageId;
    public string Version;
    public string SourceRepositoryUrl;
    public string TargetRepositoryUrl;
    public ActionFitPackageRepositoryRetirementMode Mode = ActionFitPackageRepositoryRetirementMode.Keep;
    public bool RefreshCatalog = true;
}

[Serializable]
public sealed class ActionFitPackageRepositoryRetirementPlan
{
    public bool Success;
    public bool ReadyToExecute;
    public string Code;
    public string Message;
    public string PackageId;
    public string Version;
    public ActionFitPackageRepositoryRetirementMode Mode;
    public string SourceRepositoryUrl;
    public string TargetRepositoryUrl;
    public string SourceRepositoryVisibility;
    public string TargetRepositoryVisibility;
    public bool SourceRepositoryArchived;
    public string SourceDefaultBranch;
    public string TargetDefaultBranch;
    public int SourceBranchCount;
    public int SourceTagCount;
    public int TargetBranchCount;
    public int TargetTagCount;
    public string[] RemainingLocalReferences = Array.Empty<string>();
    public string StateFingerprint;
    public string PlanId;
    public string RequiredApprovalText;
    public string[] PlannedActions = Array.Empty<string>();
    public string[] Warnings = Array.Empty<string>();
}

[Serializable]
public sealed class ActionFitPackageRepositoryRetirementExecuteRequest
{
    public string PackageId;
    public string Version;
    public string SourceRepositoryUrl;
    public string TargetRepositoryUrl;
    public ActionFitPackageRepositoryRetirementMode Mode = ActionFitPackageRepositoryRetirementMode.Keep;
    public string ExpectedPlanId;
    public string ApprovalText;
    public ActionFitPackageRepositoryRetirementPlan ApprovedPlan;
}

[Serializable]
public sealed class ActionFitPackageRepositoryRetirementResult
{
    public bool Success;
    public string Code;
    public string Message;
    public string PackageId;
    public string Version;
    public ActionFitPackageRepositoryRetirementMode Mode;
    public string SourceRepositoryUrl;
    public string TargetRepositoryUrl;
    public string PlanId;
    public bool SourceRepositoryArchived;
    public bool SourceRepositoryDeleted;
}

[Serializable]
public sealed class ActionFitPackageRepositoryRetirementBatchPrepareRequest
{
    public ActionFitPackageRepositoryRetirementPrepareRequest[] Items =
        Array.Empty<ActionFitPackageRepositoryRetirementPrepareRequest>();
    public bool RefreshCatalog = true;
}

[Serializable]
public sealed class ActionFitPackageRepositoryRetirementBatchPlan
{
    public bool Success;
    public bool ReadyToExecute;
    public string Code;
    public string Message;
    public string PlanId;
    public string RequiredApprovalText;
    public ActionFitPackageRepositoryRetirementPlan[] Items =
        Array.Empty<ActionFitPackageRepositoryRetirementPlan>();
    public string[] Warnings = Array.Empty<string>();
}

[Serializable]
public sealed class ActionFitPackageRepositoryRetirementBatchExecuteRequest
{
    public ActionFitPackageRepositoryRetirementPrepareRequest[] Items =
        Array.Empty<ActionFitPackageRepositoryRetirementPrepareRequest>();
    public string ExpectedPlanId;
    public string ApprovalText;
    public ActionFitPackageRepositoryRetirementBatchPlan ApprovedPlan;
}

[Serializable]
public sealed class ActionFitPackageRepositoryRetirementBatchResult
{
    public bool Success;
    public string Code;
    public string Message;
    public string PlanId;
    public ActionFitPackageRepositoryRetirementResult[] Items =
        Array.Empty<ActionFitPackageRepositoryRetirementResult>();
}

/// <summary>
/// Prepares and executes separately approved source-repository Archive or Delete operations after publication.
/// </summary>
public static class ActionFitPackageRepositoryRetirementApi
{
    private const string ExternalConsumerWarning =
        "The current Catalog and checkout cannot prove that every external consumer stopped using the Private source.";

    /// <summary>Prepares a read-only, content-bound retirement plan.</summary>
    public static ActionFitPackageRepositoryRetirementPlan Prepare(
        ActionFitPackageRepositoryRetirementPrepareRequest request)
        => PrepareCore(request, request?.RefreshCatalog == true);

    /// <summary>Revalidates and executes one exact Archive or Delete approval.</summary>
    public static ActionFitPackageRepositoryRetirementResult Execute(
        ActionFitPackageRepositoryRetirementExecuteRequest request)
    {
        if (request == null)
            return Failure(null, "INVALID_REQUEST", "Repository retirement execute request is missing.");
        if (!ApprovedPlanMatches(
                request.ApprovedPlan,
                request.PackageId,
                request.Version,
                request.SourceRepositoryUrl,
                request.TargetRepositoryUrl,
                request.Mode,
                request.ExpectedPlanId,
                request.ApprovalText))
        {
            return Failure(
                request,
                "APPROVAL_REQUIRED",
                "ApprovedPlan, ExpectedPlanId, and exact ApprovalText from Prepare are required.");
        }

        ActionFitPackageRepositoryRetirementPlan current = PrepareCore(
            new ActionFitPackageRepositoryRetirementPrepareRequest
            {
                PackageId = request.PackageId,
                Version = request.Version,
                SourceRepositoryUrl = request.SourceRepositoryUrl,
                TargetRepositoryUrl = request.TargetRepositoryUrl,
                Mode = request.Mode,
                RefreshCatalog = true,
            },
            true);
        if (!current.Success || !current.ReadyToExecute)
            return Failure(request, "PREFLIGHT_FAILED", current.Message, current);
        if (!string.Equals(current.PlanId, request.ExpectedPlanId, StringComparison.Ordinal))
        {
            return Failure(
                request,
                "PLAN_CHANGED",
                $"Repository retirement state changed after approval. Expected {request.ExpectedPlanId}, current {current.PlanId}.",
                current);
        }
        if (!string.Equals(current.RequiredApprovalText, request.ApprovalText, StringComparison.Ordinal))
            return Failure(request, "APPROVAL_TEXT_MISMATCH", "ApprovalText does not exactly match the current retirement plan.", current);

        return ExecuteApprovedPlan(current);
    }

    /// <summary>Prepares an exact package/repository set for serial retirement.</summary>
    public static ActionFitPackageRepositoryRetirementBatchPlan PrepareBatch(
        ActionFitPackageRepositoryRetirementBatchPrepareRequest request)
    {
        var batch = new ActionFitPackageRepositoryRetirementBatchPlan();
        try
        {
            ActionFitPackageRepositoryRetirementPrepareRequest[] items = NormalizeItems(request?.Items);
            if (items.Length == 0)
                return BatchFailure(batch, "NO_RETIREMENT_ITEMS", "At least one Archive or Delete item is required.");

            if (request?.RefreshCatalog != false)
            {
                ActionFitPackageCatalogSettings_SO settings = ActionFitPackageCatalogSettingsProvider.FindOrCreate();
                if (!ActionFitPackageCatalogUpdater.UpdateCatalog(settings, out string refreshError, false))
                    return BatchFailure(batch, "CATALOG_REFRESH_FAILED", refreshError);
            }

            var plans = new List<ActionFitPackageRepositoryRetirementPlan>();
            foreach (ActionFitPackageRepositoryRetirementPrepareRequest item in items)
            {
                var preparedItem = new ActionFitPackageRepositoryRetirementPrepareRequest
                {
                    PackageId = item.PackageId,
                    Version = item.Version,
                    SourceRepositoryUrl = item.SourceRepositoryUrl,
                    TargetRepositoryUrl = item.TargetRepositoryUrl,
                    Mode = item.Mode,
                    RefreshCatalog = false,
                };
                plans.Add(PrepareCore(preparedItem, false));
            }

            ActionFitPackageRepositoryRetirementPlan[] failed = plans
                .Where(item => !item.Success || !item.ReadyToExecute)
                .ToArray();
            if (failed.Length > 0)
            {
                return BatchFailure(
                    batch,
                    "ITEM_PREFLIGHT_FAILED",
                    "Repository retirement preflight failed. No source repository was changed.\n" +
                    string.Join("\n", failed.Select(item => $"- {item.PackageId}: {item.Code} - {item.Message}")));
            }

            batch.Items = plans
                .OrderBy(item => item.PackageId, StringComparer.Ordinal)
                .ToArray();
            batch.PlanId = ComputeBatchPlanId(batch.Items);
            batch.RequiredApprovalText =
                $"RETIRE SOURCES {string.Join(",", batch.Items.Select(item => $"{item.PackageId}:{item.Mode}"))} PLAN {batch.PlanId}";
            batch.Warnings = batch.Items
                .SelectMany(item => item.Warnings ?? Array.Empty<string>())
                .Distinct(StringComparer.Ordinal)
                .ToArray();
            batch.Success = true;
            batch.ReadyToExecute = true;
            batch.Code = "READY_TO_RETIRE_REPOSITORIES";
            batch.Message = $"Repository retirement plan is ready for {batch.Items.Length} source repository/repositories.";
            return batch;
        }
        catch (Exception ex)
        {
            return BatchFailure(batch, "PREPARE_FAILED", ex.Message);
        }
    }

    /// <summary>Revalidates every item, then retires sources serially and stops on the first failure.</summary>
    public static ActionFitPackageRepositoryRetirementBatchResult ExecuteBatch(
        ActionFitPackageRepositoryRetirementBatchExecuteRequest request)
    {
        if (request == null)
            return BatchExecutionFailure(null, "INVALID_REQUEST", "Repository retirement batch request is missing.");
        if (!BatchApprovedPlanMatches(request.ApprovedPlan, request.ExpectedPlanId, request.ApprovalText))
            return BatchExecutionFailure(request, "APPROVAL_REQUIRED", "The exact approved batch plan and approval text are required.");

        ActionFitPackageRepositoryRetirementBatchPlan current = PrepareBatch(
            new ActionFitPackageRepositoryRetirementBatchPrepareRequest
            {
                Items = request.Items,
                RefreshCatalog = true,
            });
        if (!current.Success || !current.ReadyToExecute)
            return BatchExecutionFailure(request, "PREFLIGHT_FAILED", current.Message, current);
        if (!string.Equals(current.PlanId, request.ExpectedPlanId, StringComparison.Ordinal) ||
            !string.Equals(current.RequiredApprovalText, request.ApprovalText, StringComparison.Ordinal))
        {
            return BatchExecutionFailure(
                request,
                "PLAN_CHANGED",
                $"Repository retirement batch changed after approval. Expected {request.ExpectedPlanId}, current {current.PlanId}.",
                current);
        }

        var results = new List<ActionFitPackageRepositoryRetirementResult>();
        foreach (ActionFitPackageRepositoryRetirementPlan item in current.Items)
        {
            ActionFitPackageRepositoryRetirementPlan freshItem = PrepareCore(
                new ActionFitPackageRepositoryRetirementPrepareRequest
                {
                    PackageId = item.PackageId,
                    Version = item.Version,
                    SourceRepositoryUrl = item.SourceRepositoryUrl,
                    TargetRepositoryUrl = item.TargetRepositoryUrl,
                    Mode = item.Mode,
                    RefreshCatalog = true,
                },
                true);
            if (!freshItem.Success ||
                !freshItem.ReadyToExecute ||
                !string.Equals(freshItem.PlanId, item.PlanId, StringComparison.Ordinal))
            {
                return new ActionFitPackageRepositoryRetirementBatchResult
                {
                    Success = false,
                    Code = "ITEM_PLAN_CHANGED",
                    Message =
                        $"Repository retirement stopped before {item.PackageId} because its state changed. " +
                        $"Later sources were preserved. {freshItem.Code}: {freshItem.Message}",
                    PlanId = current.PlanId,
                    Items = results.ToArray(),
                };
            }

            ActionFitPackageRepositoryRetirementResult result = ExecuteApprovedPlan(freshItem);
            results.Add(result);
            if (!result.Success)
            {
                return new ActionFitPackageRepositoryRetirementBatchResult
                {
                    Success = false,
                    Code = "ITEM_EXECUTION_FAILED",
                    Message = $"Repository retirement stopped at {item.PackageId}. Later sources were preserved. {result.Message}",
                    PlanId = current.PlanId,
                    Items = results.ToArray(),
                };
            }
        }

        return new ActionFitPackageRepositoryRetirementBatchResult
        {
            Success = true,
            Code = "REPOSITORIES_RETIRED",
            Message = $"{results.Count} source repository/repositories were retired with fresh verification.",
            PlanId = current.PlanId,
            Items = results.ToArray(),
        };
    }

    public static string PrepareJson(string requestJson)
    {
        try
        {
            return JsonUtility.ToJson(
                Prepare(JsonUtility.FromJson<ActionFitPackageRepositoryRetirementPrepareRequest>(requestJson ?? "")),
                true);
        }
        catch (Exception ex)
        {
            return JsonUtility.ToJson(Fail(new ActionFitPackageRepositoryRetirementPlan(), "INVALID_REQUEST_JSON", ex.Message), true);
        }
    }

    public static string ExecuteJson(string requestJson)
    {
        try
        {
            return JsonUtility.ToJson(
                Execute(JsonUtility.FromJson<ActionFitPackageRepositoryRetirementExecuteRequest>(requestJson ?? "")),
                true);
        }
        catch (Exception ex)
        {
            return JsonUtility.ToJson(Failure(null, "INVALID_REQUEST_JSON", ex.Message), true);
        }
    }

    public static string PrepareBatchJson(string requestJson)
    {
        try
        {
            return JsonUtility.ToJson(
                PrepareBatch(JsonUtility.FromJson<ActionFitPackageRepositoryRetirementBatchPrepareRequest>(requestJson ?? "")),
                true);
        }
        catch (Exception ex)
        {
            return JsonUtility.ToJson(BatchFailure(new ActionFitPackageRepositoryRetirementBatchPlan(), "INVALID_REQUEST_JSON", ex.Message), true);
        }
    }

    public static string ExecuteBatchJson(string requestJson)
    {
        try
        {
            return JsonUtility.ToJson(
                ExecuteBatch(JsonUtility.FromJson<ActionFitPackageRepositoryRetirementBatchExecuteRequest>(requestJson ?? "")),
                true);
        }
        catch (Exception ex)
        {
            return JsonUtility.ToJson(BatchExecutionFailure(null, "INVALID_REQUEST_JSON", ex.Message), true);
        }
    }

    internal static bool ApprovedPlanMatches(
        ActionFitPackageRepositoryRetirementPlan plan,
        string packageId,
        string version,
        string sourceRepositoryUrl,
        string targetRepositoryUrl,
        ActionFitPackageRepositoryRetirementMode mode,
        string expectedPlanId,
        string approvalText)
    {
        return plan != null &&
               plan.Success &&
               plan.ReadyToExecute &&
               string.Equals(plan.PackageId, packageId, StringComparison.Ordinal) &&
               string.Equals(plan.Version, version, StringComparison.Ordinal) &&
               RepositoryUrlsEqual(plan.SourceRepositoryUrl, sourceRepositoryUrl) &&
               RepositoryUrlsEqual(plan.TargetRepositoryUrl, targetRepositoryUrl) &&
               plan.Mode == mode &&
               string.Equals(plan.PlanId, expectedPlanId, StringComparison.Ordinal) &&
               string.Equals(plan.RequiredApprovalText, approvalText, StringComparison.Ordinal);
    }

    internal static bool BatchApprovedPlanMatches(
        ActionFitPackageRepositoryRetirementBatchPlan plan,
        string expectedPlanId,
        string approvalText)
    {
        return plan != null &&
               plan.Success &&
               plan.ReadyToExecute &&
               string.Equals(plan.PlanId, expectedPlanId, StringComparison.Ordinal) &&
               string.Equals(plan.RequiredApprovalText, approvalText, StringComparison.Ordinal);
    }

    internal static string[] FindLocalReferencePaths(
        ActionFitPackageRepositoryMigration.RepositoryIdentity source)
    {
        string marker = $"github.com/{source.Organization}/{source.Name}";
        var paths = new List<string>
        {
            Path.Combine(ActionFitPackagePaths.ProjectRoot, "Packages", "manifest.json"),
            Path.Combine(ActionFitPackagePaths.ProjectRoot, "Packages", "packages-lock.json"),
        };
        if (Directory.Exists(ActionFitPackagePaths.PackagesRoot))
        {
            paths.AddRange(Directory
                .GetDirectories(ActionFitPackagePaths.PackagesRoot, "com.actionfit.*", SearchOption.TopDirectoryOnly)
                .Select(directory => Path.Combine(directory, "package.json")));
        }

        return paths
            .Where(File.Exists)
            .Where(path => File.ReadAllText(path).Replace("\\", "/")
                .IndexOf(marker, StringComparison.OrdinalIgnoreCase) >= 0)
            .Select(ActionFitPackagePaths.ToProjectRelativePath)
            .Distinct(StringComparer.Ordinal)
            .OrderBy(path => path, StringComparer.Ordinal)
            .ToArray();
    }

    private static ActionFitPackageRepositoryRetirementPlan PrepareCore(
        ActionFitPackageRepositoryRetirementPrepareRequest request,
        bool refreshCatalog)
    {
        var plan = new ActionFitPackageRepositoryRetirementPlan
        {
            PackageId = request?.PackageId ?? "",
            Version = request?.Version ?? "",
            Mode = request?.Mode ?? ActionFitPackageRepositoryRetirementMode.Keep,
            SourceRepositoryUrl = request?.SourceRepositoryUrl ?? "",
            TargetRepositoryUrl = request?.TargetRepositoryUrl ?? "",
        };

        try
        {
            if (request == null) throw new ArgumentNullException(nameof(request));
            ActionFitPackagePaths.ValidatePackageId(request.PackageId);
            if (!request.PackageId.StartsWith("com.actionfit.", StringComparison.Ordinal))
                return Fail(plan, "PACKAGE_ID_UNSUPPORTED", "Repository retirement is limited to com.actionfit.* packages.");
            if (string.IsNullOrWhiteSpace(request.Version))
                return Fail(plan, "VERSION_REQUIRED", "The published package version is required.");
            if (request.Mode == ActionFitPackageRepositoryRetirementMode.Keep)
            {
                plan.Success = true;
                plan.ReadyToExecute = false;
                plan.Code = "SOURCE_REPOSITORY_KEPT";
                plan.Message = "Keep requires no repository mutation. The source remains unchanged.";
                return plan;
            }
            if (request.Mode != ActionFitPackageRepositoryRetirementMode.Archive &&
                request.Mode != ActionFitPackageRepositoryRetirementMode.Delete)
            {
                return Fail(plan, "RETIREMENT_MODE_INVALID", $"Unsupported repository retirement mode: {request.Mode}");
            }
            if (!ActionFitPackageRepositoryMigration.TryParseRepositoryUrl(
                    request.SourceRepositoryUrl,
                    out ActionFitPackageRepositoryMigration.RepositoryIdentity source))
            {
                return Fail(plan, "SOURCE_REPOSITORY_URL_INVALID", "SourceRepositoryUrl is not a supported GitHub repository URL.");
            }
            if (!ActionFitPackageRepositoryMigration.TryParseRepositoryUrl(
                    request.TargetRepositoryUrl,
                    out ActionFitPackageRepositoryMigration.RepositoryIdentity target))
            {
                return Fail(plan, "TARGET_REPOSITORY_URL_INVALID", "TargetRepositoryUrl is not a supported GitHub repository URL.");
            }
            if (source.Equals(target))
                return Fail(plan, "SOURCE_TARGET_SAME", "The source and target repositories must be different.");

            ActionFitPackageCatalogSettings_SO settings = ActionFitPackageCatalogSettingsProvider.FindOrCreate();
            if (refreshCatalog &&
                !ActionFitPackageCatalogUpdater.UpdateCatalog(settings, out string refreshError, false))
            {
                return Fail(plan, "CATALOG_REFRESH_FAILED", refreshError);
            }

            ActionFitPackageInspectionResult inspection = ActionFitPackageWorkflowApi.Inspect(
                new ActionFitPackageInspectionRequest
                {
                    PackageId = request.PackageId,
                    RefreshCatalog = false,
                });
            if (!inspection.Success)
                return Fail(plan, "INSPECTION_FAILED", inspection.Message);
            if (!inspection.CatalogPackageFound ||
                !string.Equals(inspection.LatestVersion, request.Version, StringComparison.Ordinal) ||
                !RepositoryUrlsEqual(inspection.RepositoryUrl, target.Url))
            {
                return Fail(
                    plan,
                    "CATALOG_TARGET_NOT_CURRENT",
                    $"Catalog latest must be {request.PackageId}@{request.Version} at {target.Url} before source retirement.");
            }
            string[] catalogSourceVersions =
                ActionFitPackageWorkflowApi.FindCatalogRepositoryReferenceVersions(
                    request.PackageId,
                    source.Url);
            if (catalogSourceVersions.Length > 0)
            {
                return Fail(
                    plan,
                    "CATALOG_SOURCE_REFERENCES_REMAIN",
                    $"Catalog versions still reference the Private source: {string.Join(", ", catalogSourceVersions)}. " +
                    "Refresh or migrate those rows to the Public target before retirement.");
            }

            ActionFitPackageInfo_SO info = ActionFitPackagePublishApi.FindPackageInfo(request.PackageId);
            if (info == null)
                return Fail(plan, "PACKAGE_INFO_MISSING", "ActionFitPackageInfo_SO was not found.");
            if (info.RepositoryVisibility != ActionFitPackageRepositoryVisibility.Public)
                return Fail(plan, "PUBLIC_TARGET_REQUIRED", "Archive and Delete require a selected Public package target.");
            if (!ActionFitPackagePublisher.TryCreatePublishRequest(
                    settings,
                    info,
                    info.RepositoryVisibility,
                    out ActionFitPackagePublisher.PublishRequest publishRequest,
                    out string requestError))
            {
                return Fail(plan, "PUBLISH_REQUEST_INVALID", requestError);
            }
            var configuredTarget = new ActionFitPackageRepositoryMigration.RepositoryIdentity(
                publishRequest.GitHubOrganization,
                publishRequest.RepoName);
            if (!configuredTarget.Equals(target) || !string.Equals(publishRequest.Version, request.Version, StringComparison.Ordinal))
                return Fail(plan, "TARGET_CONFIGURATION_CHANGED", "The current PackageInfo target or package version no longer matches the approved target.");

            if (!ActionFitPackagePublisher.TryGetRemoteState(
                    publishRequest,
                    out ActionFitPackagePublisher.RemoteState targetRemote,
                    out string remoteError))
            {
                return Fail(plan, "TARGET_REPOSITORY_CHECK_FAILED", remoteError);
            }
            if (!targetRemote.RepositoryExists || targetRemote.RepositoryIsPrivate || !targetRemote.TagExists)
            {
                return Fail(
                    plan,
                    "PUBLIC_TARGET_NOT_VERIFIED",
                    "The Public target repository and immutable package tag must exist before source retirement.");
            }
            if (!ActionFitPackagePublisher.TryGetRepositoryMetadata(
                    target.Organization,
                    target.Name,
                    publishRequest.GitHubToken,
                    out bool targetExists,
                    out bool targetIsPrivate,
                    out _,
                    out bool targetArchived,
                    out string targetError))
            {
                return Fail(plan, "TARGET_REPOSITORY_CHECK_FAILED", targetError);
            }
            if (!targetExists || targetIsPrivate || targetArchived)
                return Fail(plan, "PUBLIC_TARGET_UNAVAILABLE", "The Public replacement must exist, remain Public, and not be archived.");

            publishRequest.SourceRepositoryUrl = source.Url;
            if (!ActionFitPackageRepositoryMigration.TryInspect(
                    publishRequest,
                    targetRemote,
                    out ActionFitPackageRepositoryMigration.RepositoryMigrationState migration,
                    out string migrationCode,
                    out string migrationMessage))
            {
                return Fail(plan, migrationCode, migrationMessage);
            }
            if (!migration.Required || !migration.SourceRepositoryExists || !migration.SourceRepositoryIsPrivate)
                return Fail(plan, "PRIVATE_SOURCE_REQUIRED", "Archive and Delete require an existing Private source and a different Public target.");
            if (migration.WillMirrorRepository)
                return Fail(plan, "TARGET_REFS_INCOMPLETE", "The Public target does not yet contain every required source branch and tag.");

            if (!ActionFitPackagePublisher.TryGetRepositoryMetadata(
                    source.Organization,
                    source.Name,
                    publishRequest.GitHubToken,
                    out bool sourceExists,
                    out bool sourceIsPrivate,
                    out _,
                    out bool sourceArchived,
                    out string sourceError))
            {
                return Fail(plan, "SOURCE_REPOSITORY_CHECK_FAILED", sourceError);
            }
            if (!sourceExists || !sourceIsPrivate)
                return Fail(plan, "PRIVATE_SOURCE_REQUIRED", "The source must still exist as Private immediately before retirement.");
            if (request.Mode == ActionFitPackageRepositoryRetirementMode.Archive && sourceArchived)
            {
                plan.Success = true;
                plan.ReadyToExecute = false;
                plan.Code = "SOURCE_ALREADY_ARCHIVED";
                plan.Message = $"Private source {source.DisplayName} is already archived.";
                plan.SourceRepositoryArchived = true;
                return plan;
            }

            string[] remainingReferences = FindLocalReferencePaths(source);
            if (remainingReferences.Length > 0)
            {
                return Fail(
                    plan,
                    "SOURCE_REFERENCES_REMAIN",
                    "The Private source is still referenced by current project dependency files: " +
                    string.Join(", ", remainingReferences));
            }

            plan.SourceRepositoryUrl = source.Url;
            plan.TargetRepositoryUrl = target.Url;
            plan.SourceRepositoryVisibility = "Private";
            plan.TargetRepositoryVisibility = "Public";
            plan.SourceRepositoryArchived = sourceArchived;
            plan.SourceDefaultBranch = migration.SourceDefaultBranch;
            plan.TargetDefaultBranch = migration.TargetDefaultBranch;
            plan.SourceBranchCount = migration.SourceBranchCount;
            plan.SourceTagCount = migration.SourceTagCount;
            plan.TargetBranchCount = migration.TargetBranchCount;
            plan.TargetTagCount = migration.TargetTagCount;
            plan.RemainingLocalReferences = remainingReferences;
            plan.StateFingerprint = migration.Fingerprint;
            plan.Warnings = request.Mode == ActionFitPackageRepositoryRetirementMode.Delete
                ? new[]
                {
                    ExternalConsumerWarning,
                    "Delete is irreversible and does not migrate Issues, pull requests, settings, secrets, Actions configuration, stars, forks, or other GitHub metadata.",
                }
                : new[] { ExternalConsumerWarning, "Archive is reversible and is the recommended retirement mode." };
            plan.PlannedActions = new[]
            {
                $"Revalidate Public target {target.DisplayName} and immutable tag {request.Version}",
                $"Revalidate Catalog latest URL and current-project dependency references for {request.PackageId}",
                $"{request.Mode} Private source {source.DisplayName}",
                "Leave the Public target and Catalog unchanged",
            };
            plan.PlanId = ComputePlanId(plan);
            plan.RequiredApprovalText =
                $"{request.Mode.ToString().ToUpperInvariant()} SOURCE {source.DisplayName} FOR {request.PackageId}@{request.Version} PLAN {plan.PlanId}";
            plan.Success = true;
            plan.ReadyToExecute = true;
            plan.Code = "READY_TO_RETIRE_REPOSITORY";
            plan.Message = $"{request.Mode} plan is ready for Private source {source.DisplayName}. No repository was changed.";
            return plan;
        }
        catch (Exception ex)
        {
            return Fail(plan, "PREPARE_FAILED", ex.Message);
        }
    }

    private static ActionFitPackageRepositoryRetirementResult ExecuteApprovedPlan(
        ActionFitPackageRepositoryRetirementPlan plan)
    {
        var stopwatch = Stopwatch.StartNew();
        try
        {
            if (!ActionFitPackageRepositoryMigration.TryParseRepositoryUrl(
                    plan.SourceRepositoryUrl,
                    out ActionFitPackageRepositoryMigration.RepositoryIdentity source))
            {
                return Failure(null, "SOURCE_REPOSITORY_URL_INVALID", "Approved source repository URL is invalid.", plan);
            }

            ActionFitPackageCatalogSettings_SO settings = ActionFitPackageCatalogSettingsProvider.FindOrCreate();
            string method = plan.Mode == ActionFitPackageRepositoryRetirementMode.Archive ? "PATCH" : "DELETE";
            HttpWebRequest request = ActionFitPackagePublisher.CreateGitHubRequest(
                $"https://api.github.com/repos/{source.Organization}/{source.Name}",
                settings.GitHubToken,
                method);
            if (plan.Mode == ActionFitPackageRepositoryRetirementMode.Archive)
                ActionFitPackagePublisher.WriteBody(request, "{\"archived\":true}");
            using (HttpWebResponse response = (HttpWebResponse)request.GetResponse())
            {
                if ((int)response.StatusCode < 200 || (int)response.StatusCode >= 300)
                    return Failure(null, "GITHUB_RETIREMENT_FAILED", $"GitHub returned {response.StatusCode}.", plan);
            }

            if (!ActionFitPackagePublisher.TryGetRepositoryMetadata(
                    source.Organization,
                    source.Name,
                    settings.GitHubToken,
                    out bool exists,
                    out _,
                    out _,
                    out bool archived,
                    out string verificationError))
            {
                return Failure(null, "RETIREMENT_VERIFICATION_FAILED", verificationError, plan);
            }

            bool verified = plan.Mode == ActionFitPackageRepositoryRetirementMode.Archive
                ? exists && archived
                : !exists;
            if (!verified)
                return Failure(null, "RETIREMENT_VERIFICATION_FAILED", "GitHub source state did not match the approved retirement mode.", plan);

            string message =
                $"Private source {source.DisplayName} was {plan.Mode.ToString().ToLowerInvariant()}d and verified " +
                $"in {stopwatch.ElapsedMilliseconds} ms.";
            Debug.Log($"[ActionFitPackageManager] Repository retirement complete: {plan.PackageId} ({plan.Mode}, {source.DisplayName})");
            return new ActionFitPackageRepositoryRetirementResult
            {
                Success = true,
                Code = plan.Mode == ActionFitPackageRepositoryRetirementMode.Archive
                    ? "SOURCE_REPOSITORY_ARCHIVED"
                    : "SOURCE_REPOSITORY_DELETED",
                Message = message,
                PackageId = plan.PackageId,
                Version = plan.Version,
                Mode = plan.Mode,
                SourceRepositoryUrl = plan.SourceRepositoryUrl,
                TargetRepositoryUrl = plan.TargetRepositoryUrl,
                PlanId = plan.PlanId,
                SourceRepositoryArchived = plan.Mode == ActionFitPackageRepositoryRetirementMode.Archive,
                SourceRepositoryDeleted = plan.Mode == ActionFitPackageRepositoryRetirementMode.Delete,
            };
        }
        catch (WebException ex)
        {
            ex.Response?.Dispose();
            return Failure(
                null,
                "GITHUB_RETIREMENT_FAILED",
                $"GitHub rejected {plan.Mode} for the Private source. Verify repository administration permission. {ex.Message}",
                plan);
        }
        catch (Exception ex)
        {
            return Failure(null, "RETIREMENT_FAILED", ex.Message, plan);
        }
    }

    private static string ComputePlanId(ActionFitPackageRepositoryRetirementPlan plan)
    {
        string canonical = string.Join("\n", new[]
        {
            plan.PackageId,
            plan.Version,
            plan.Mode.ToString(),
            plan.SourceRepositoryUrl,
            plan.TargetRepositoryUrl,
            plan.SourceRepositoryVisibility,
            plan.TargetRepositoryVisibility,
            plan.SourceRepositoryArchived.ToString(),
            plan.SourceDefaultBranch,
            plan.TargetDefaultBranch,
            plan.SourceBranchCount.ToString(),
            plan.SourceTagCount.ToString(),
            plan.TargetBranchCount.ToString(),
            plan.TargetTagCount.ToString(),
            plan.StateFingerprint,
            string.Join(";", plan.RemainingLocalReferences ?? Array.Empty<string>()),
        });
        return Sha256(canonical).Substring(0, 20);
    }

    private static string ComputeBatchPlanId(IEnumerable<ActionFitPackageRepositoryRetirementPlan> plans)
    {
        string canonical = string.Join("\n", plans
            .OrderBy(item => item.PackageId, StringComparer.Ordinal)
            .Select(item => $"{item.PackageId}|{item.Mode}|{item.SourceRepositoryUrl}|{item.TargetRepositoryUrl}|{item.PlanId}"));
        return Sha256(canonical).Substring(0, 20);
    }

    private static ActionFitPackageRepositoryRetirementPrepareRequest[] NormalizeItems(
        IEnumerable<ActionFitPackageRepositoryRetirementPrepareRequest> items)
    {
        ActionFitPackageRepositoryRetirementPrepareRequest[] normalized = (items ?? Array.Empty<ActionFitPackageRepositoryRetirementPrepareRequest>())
            .Where(item => item != null && item.Mode != ActionFitPackageRepositoryRetirementMode.Keep)
            .OrderBy(item => item.PackageId, StringComparer.Ordinal)
            .ToArray();
        string duplicate = normalized
            .GroupBy(item => item.PackageId ?? "", StringComparer.Ordinal)
            .Where(group => group.Count() > 1)
            .Select(group => group.Key)
            .FirstOrDefault();
        if (!string.IsNullOrEmpty(duplicate))
            throw new InvalidOperationException($"Duplicate retirement package ID: {duplicate}");
        return normalized;
    }

    private static bool RepositoryUrlsEqual(string left, string right)
    {
        return ActionFitPackageRepositoryMigration.TryParseRepositoryUrl(left, out var leftIdentity) &&
               ActionFitPackageRepositoryMigration.TryParseRepositoryUrl(right, out var rightIdentity) &&
               leftIdentity.Equals(rightIdentity);
    }

    private static string Sha256(string value)
    {
        using SHA256 sha = SHA256.Create();
        return BitConverter.ToString(sha.ComputeHash(Encoding.UTF8.GetBytes(value ?? ""))).Replace("-", "");
    }

    private static ActionFitPackageRepositoryRetirementPlan Fail(
        ActionFitPackageRepositoryRetirementPlan plan,
        string code,
        string message)
    {
        plan ??= new ActionFitPackageRepositoryRetirementPlan();
        plan.Success = false;
        plan.ReadyToExecute = false;
        plan.Code = code;
        plan.Message = message;
        return plan;
    }

    private static ActionFitPackageRepositoryRetirementResult Failure(
        ActionFitPackageRepositoryRetirementExecuteRequest request,
        string code,
        string message,
        ActionFitPackageRepositoryRetirementPlan plan = null)
    {
        return new ActionFitPackageRepositoryRetirementResult
        {
            Success = false,
            Code = code,
            Message = message,
            PackageId = plan?.PackageId ?? request?.PackageId ?? "",
            Version = plan?.Version ?? request?.Version ?? "",
            Mode = plan?.Mode ?? request?.Mode ?? ActionFitPackageRepositoryRetirementMode.Keep,
            SourceRepositoryUrl = plan?.SourceRepositoryUrl ?? request?.SourceRepositoryUrl ?? "",
            TargetRepositoryUrl = plan?.TargetRepositoryUrl ?? request?.TargetRepositoryUrl ?? "",
            PlanId = plan?.PlanId ?? request?.ExpectedPlanId ?? "",
        };
    }

    private static ActionFitPackageRepositoryRetirementBatchPlan BatchFailure(
        ActionFitPackageRepositoryRetirementBatchPlan plan,
        string code,
        string message)
    {
        plan ??= new ActionFitPackageRepositoryRetirementBatchPlan();
        plan.Success = false;
        plan.ReadyToExecute = false;
        plan.Code = code;
        plan.Message = message;
        return plan;
    }

    private static ActionFitPackageRepositoryRetirementBatchResult BatchExecutionFailure(
        ActionFitPackageRepositoryRetirementBatchExecuteRequest request,
        string code,
        string message,
        ActionFitPackageRepositoryRetirementBatchPlan plan = null)
    {
        return new ActionFitPackageRepositoryRetirementBatchResult
        {
            Success = false,
            Code = code,
            Message = message,
            PlanId = plan?.PlanId ?? request?.ExpectedPlanId ?? "",
        };
    }
}
#endif
