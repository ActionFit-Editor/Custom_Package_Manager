#if UNITY_EDITOR
using System;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using UnityEditor;
using UnityEngine;

/// <summary>
/// Read-only request that prepares an immutable package publish plan.
/// </summary>
[Serializable]
public sealed class ActionFitPackagePublishPrepareRequest
{
    public string PackageId;
    public bool RefreshCatalog = true;
    public bool CheckRemoteState = true;
}

/// <summary>
/// Publish plan returned before any repository, tag, or catalog mutation occurs.
/// </summary>
[Serializable]
public sealed class ActionFitPackagePublishPlan
{
    public bool Success;
    public bool ReadyToPublish;
    public string Code;
    public string Message;
    public string PackageId;
    public string Version;
    public string SuggestedNextVersion;
    public string PackagePath;
    public string ContentHash;
    public string CatalogLatestVersion;
    public bool CatalogContainsVersion;
    public bool RepositoryExists;
    public bool RemoteTagExists;
    public bool WillCreateRepository;
    public string GitHubOrganization;
    public string RepositoryName;
    public string RepositoryVisibility;
    public string RepositoryUrl;
    public string PlanId;
    public string RequiredApprovalText;
    public ActionFitPackageContractDiagnostic[] ContractDiagnostics = Array.Empty<ActionFitPackageContractDiagnostic>();
    public string[] PlannedActions = Array.Empty<string>();
    public string[] Warnings = Array.Empty<string>();
}

/// <summary>
/// Explicitly approved request that executes a previously prepared publish plan.
/// </summary>
[Serializable]
public sealed class ActionFitPackagePublishExecuteRequest
{
    public string PackageId;
    public string ExpectedPlanId;
    public string ApprovalText;
    public bool ApproveRepositoryCreation;
}

/// <summary>
/// Structured outcome of repository publication and catalog registration.
/// </summary>
[Serializable]
public sealed class ActionFitPackagePublishExecutionResult
{
    public bool Success;
    public bool RepositoryPublished;
    public bool CatalogAppended;
    public bool RetryCatalogAppendAvailable;
    public string Code;
    public string Message;
    public string PackageId;
    public string Version;
    public string PlanId;
    public string RepositoryUrl;
    public string CatalogId;
    public string[] Warnings = Array.Empty<string>();
}

/// <summary>
/// Public, dialog-free, approval-gated API for publishing modified ActionFit packages.
/// </summary>
public static class ActionFitPackagePublishApi
{
    /// <summary>
    /// Refreshes catalog state, validates local content and credentials, checks GitHub, and returns a publish plan.
    /// </summary>
    public static ActionFitPackagePublishPlan Prepare(ActionFitPackagePublishPrepareRequest request)
    {
        var plan = new ActionFitPackagePublishPlan
        {
            PackageId = request?.PackageId ?? "",
        };

        try
        {
            if (request == null) throw new ArgumentNullException(nameof(request));
            ActionFitPackagePaths.ValidatePackageId(request.PackageId);
            if (!request.PackageId.StartsWith("com.actionfit.", StringComparison.Ordinal))
                throw new InvalidOperationException("Publish API is limited to com.actionfit.* packages.");

            string packagePath = ActionFitPackagePaths.PackagePath(request.PackageId);
            ActionFitPackageFileUtility.ValidateLocalPackageFolder(request.PackageId, packagePath);
            plan.PackagePath = ActionFitPackagePaths.ToProjectRelativePath(packagePath);

            ActionFitPackageContractValidationResult contract =
                ActionFitPackageContractValidator.ValidatePackage(request.PackageId);
            plan.ContractDiagnostics = contract.Diagnostics;
            if (!contract.Success)
                return Fail(plan, contract.Code, contract.Message);

            ActionFitPackageCatalogSettings_SO settings = ActionFitPackageCatalogSettingsProvider.FindOrCreate();
            if (request.RefreshCatalog &&
                !ActionFitPackageCatalogUpdater.UpdateCatalog(settings, out string refreshError, false))
            {
                return Fail(plan, "CATALOG_REFRESH_FAILED", $"Catalog refresh failed; publishing was not prepared: {refreshError}");
            }

            ActionFitPackageInspectionResult inspection = ActionFitPackageWorkflowApi.Inspect(new ActionFitPackageInspectionRequest
            {
                PackageId = request.PackageId,
                RefreshCatalog = false,
            });
            if (!inspection.Success)
                return Fail(plan, "INSPECTION_FAILED", inspection.Message);

            plan.Version = inspection.InstalledVersion;
            plan.CatalogLatestVersion = inspection.LatestVersion;
            plan.CatalogContainsVersion = inspection.CatalogContainsInstalledVersion;
            plan.SuggestedNextVersion = SuggestNextVersion(inspection.LatestVersion, inspection.InstalledVersion);
            plan.ContentHash = ActionFitPackageBaseline.ComputeContentHash(packagePath);

            if (!inspection.Embedded)
                return Fail(plan, "PACKAGE_NOT_EMBEDDED", "Only a physical embedded package can be published.");
            if (plan.CatalogContainsVersion)
                return Fail(plan, "VERSION_ALREADY_IN_CATALOG", $"Catalog already contains {request.PackageId}@{plan.Version}. Bump package.json before publishing.");
            if (inspection.CatalogPackageFound &&
                ActionFitPackageVersionComparer.Instance.Compare(plan.Version, inspection.LatestVersion) <= 0)
            {
                return Fail(
                    plan,
                    "VERSION_BUMP_REQUIRED",
                    $"Local version {plan.Version} must be higher than catalog latest {inspection.LatestVersion}. Suggested: {plan.SuggestedNextVersion}");
            }

            ActionFitPackageInfo_SO info = FindPackageInfo(request.PackageId);
            if (info == null)
                return Fail(plan, "PACKAGE_INFO_MISSING", "ActionFitPackageInfo_SO was not found inside the package.");

            if (!ActionFitPackagePublisher.TryCreatePublishRequest(
                    settings,
                    info,
                    info.RepositoryVisibility,
                    out ActionFitPackagePublisher.PublishRequest publishRequest,
                    out string requestError))
            {
                return Fail(plan, "PUBLISH_REQUEST_INVALID", requestError);
            }

            plan.GitHubOrganization = publishRequest.GitHubOrganization;
            plan.RepositoryName = publishRequest.RepoName;
            plan.RepositoryVisibility = publishRequest.GitHubIsPrivate ? "Private" : "Public";
            plan.RepositoryUrl = $"https://github.com/{publishRequest.GitHubOrganization}/{publishRequest.RepoName}.git";

            if (!request.CheckRemoteState)
                return Fail(plan, "REMOTE_CHECK_REQUIRED", "GitHub remote state must be checked before a publish plan can be approved.");
            if (!ActionFitPackagePublisher.TryGetRemoteState(publishRequest, out ActionFitPackagePublisher.RemoteState remote, out string remoteError))
                return Fail(plan, "REMOTE_CHECK_FAILED", remoteError);

            plan.RepositoryExists = remote.RepositoryExists;
            plan.RemoteTagExists = remote.TagExists;
            plan.WillCreateRepository = !remote.RepositoryExists;
            if (plan.RemoteTagExists)
                return Fail(plan, "REMOTE_TAG_ALREADY_EXISTS", $"Remote tag {plan.Version} already exists. Published tags are immutable; bump package.json.");

            plan.PlannedActions = new[]
            {
                plan.WillCreateRepository ? $"Create {plan.RepositoryVisibility} repository {plan.GitHubOrganization}/{plan.RepositoryName}" : $"Use existing repository {plan.GitHubOrganization}/{plan.RepositoryName}",
                $"Push package content to main for {plan.PackageId}@{plan.Version}",
                $"Create and push immutable tag {plan.Version}",
                $"Upsert catalog row {plan.PackageId}@{plan.Version}",
                "Refresh the shared package catalog",
            };
            plan.PlanId = ComputePlanId(plan);
            plan.RequiredApprovalText = $"PUBLISH {plan.PackageId}@{plan.Version} PLAN {plan.PlanId}";
            plan.ReadyToPublish = true;
            plan.Success = true;
            plan.Code = "READY_TO_PUBLISH";
            plan.Message = $"Publish plan is ready for {plan.PackageId}@{plan.Version}. No external state was changed.";
            if (string.IsNullOrWhiteSpace(info.ReleaseNote))
                plan.Warnings = new[] { "Package release note is empty." };
            return plan;
        }
        catch (Exception ex)
        {
            return Fail(plan, "PREPARE_FAILED", ex.Message);
        }
    }

    /// <summary>
    /// Re-prepares and executes a plan only when the plan hash and exact approval text still match.
    /// </summary>
    public static ActionFitPackagePublishExecutionResult Execute(ActionFitPackagePublishExecuteRequest request)
    {
        if (request == null)
            return ExecutionFailure(null, "INVALID_REQUEST", "Publish execute request is missing.");
        if (string.IsNullOrWhiteSpace(request.ExpectedPlanId) || string.IsNullOrWhiteSpace(request.ApprovalText))
            return ExecutionFailure(request, "APPROVAL_REQUIRED", "ExpectedPlanId and exact ApprovalText from Prepare are required.");

        ActionFitPackagePublishPlan current = Prepare(new ActionFitPackagePublishPrepareRequest
        {
            PackageId = request.PackageId,
            RefreshCatalog = true,
            CheckRemoteState = true,
        });
        if (!current.Success || !current.ReadyToPublish)
            return ExecutionFailure(request, "PREFLIGHT_FAILED", current.Message, current);
        if (!string.Equals(current.PlanId, request.ExpectedPlanId, StringComparison.Ordinal))
            return ExecutionFailure(
                request,
                "PLAN_CHANGED",
                $"Publish state changed after approval. Expected plan {request.ExpectedPlanId}, current plan {current.PlanId}. Prepare and approve again.",
                current);
        if (!string.Equals(current.RequiredApprovalText, request.ApprovalText, StringComparison.Ordinal))
            return ExecutionFailure(request, "APPROVAL_TEXT_MISMATCH", "ApprovalText does not exactly match the prepared publish plan.", current);
        if (current.WillCreateRepository && !request.ApproveRepositoryCreation)
            return ExecutionFailure(request, "REPOSITORY_CREATION_APPROVAL_REQUIRED", "This plan creates a new repository. ApproveRepositoryCreation must be true.", current);

        try
        {
            ActionFitPackageCatalogSettings_SO settings = ActionFitPackageCatalogSettingsProvider.FindOrCreate();
            ActionFitPackageInfo_SO info = FindPackageInfo(request.PackageId);
            if (!ActionFitPackagePublisher.TryCreatePublishRequest(
                    settings,
                    info,
                    info.RepositoryVisibility,
                    out ActionFitPackagePublisher.PublishRequest publishRequest,
                    out string requestError))
            {
                return ExecutionFailure(request, "PUBLISH_REQUEST_INVALID", requestError, current);
            }

            ActionFitPackagePublisher.PublishResult repository = ActionFitPackagePublisher.PublishRepository(publishRequest);
            if (!repository.Success)
                return ExecutionFailure(request, "REPOSITORY_PUBLISH_FAILED", repository.Message, current);

            bool catalogAppended = ActionFitPackagePublisher.TryAppendCatalogBatch(
                new[] { publishRequest.CatalogItem },
                out string catalogMessage);
            if (!catalogAppended)
            {
                string batchMessage = catalogMessage;
                catalogAppended = ActionFitPackagePublisher.TryAppendCatalogSerial(
                    new[] { publishRequest.CatalogItem },
                    out string serialMessage);
                catalogMessage = catalogAppended
                    ? $"Batch append failed, serial upsert succeeded. Batch: {batchMessage} Serial: {serialMessage}"
                    : $"Batch append failed: {batchMessage} Serial upsert failed: {serialMessage}";
            }

            if (!catalogAppended)
            {
                return new ActionFitPackagePublishExecutionResult
                {
                    Success = false,
                    RepositoryPublished = true,
                    CatalogAppended = false,
                    RetryCatalogAppendAvailable = true,
                    Code = "CATALOG_APPEND_FAILED",
                    Message = catalogMessage,
                    PackageId = current.PackageId,
                    Version = current.Version,
                    PlanId = current.PlanId,
                    RepositoryUrl = current.RepositoryUrl,
                    CatalogId = publishRequest.CatalogId,
                };
            }

            string[] warnings = Array.Empty<string>();
            if (!ActionFitPackageCatalogUpdater.UpdateCatalog(settings, out string refreshMessage, false))
                warnings = new[] { $"Publish succeeded, but catalog refresh failed: {refreshMessage}" };

            return new ActionFitPackagePublishExecutionResult
            {
                Success = true,
                RepositoryPublished = true,
                CatalogAppended = true,
                Code = "PUBLISHED",
                Message = $"{current.PackageId}@{current.Version} was published and registered in the catalog.",
                PackageId = current.PackageId,
                Version = current.Version,
                PlanId = current.PlanId,
                RepositoryUrl = current.RepositoryUrl,
                CatalogId = publishRequest.CatalogId,
                Warnings = warnings,
            };
        }
        catch (Exception ex)
        {
            return ExecutionFailure(request, "EXECUTION_FAILED", ex.Message, current);
        }
    }

    /// <summary>
    /// JSON wrapper for preparing a publish plan through AI connectors.
    /// </summary>
    public static string PrepareJson(string requestJson)
    {
        try
        {
            return JsonUtility.ToJson(Prepare(JsonUtility.FromJson<ActionFitPackagePublishPrepareRequest>(requestJson ?? "")), true);
        }
        catch (Exception ex)
        {
            return JsonUtility.ToJson(Fail(new ActionFitPackagePublishPlan(), "INVALID_REQUEST_JSON", ex.Message), true);
        }
    }

    /// <summary>
    /// JSON wrapper for executing an explicitly approved publish plan.
    /// </summary>
    public static string ExecuteJson(string requestJson)
    {
        try
        {
            return JsonUtility.ToJson(Execute(JsonUtility.FromJson<ActionFitPackagePublishExecuteRequest>(requestJson ?? "")), true);
        }
        catch (Exception ex)
        {
            return JsonUtility.ToJson(ExecutionFailure(null, "INVALID_REQUEST_JSON", ex.Message), true);
        }
    }

    internal static ActionFitPackageInfo_SO FindPackageInfo(string packageId)
    {
        string assetPath = Path.Combine(
            "Packages",
            packageId,
            ActionFitPackageInfoUtility.PackageInfoFolder,
            ActionFitPackageInfoUtility.PackageInfoAssetName).Replace("\\", "/");
        ActionFitPackageInfo_SO info = AssetDatabase.LoadAssetAtPath<ActionFitPackageInfo_SO>(assetPath);
        if (info != null && string.Equals(info.PackageId, packageId, StringComparison.Ordinal)) return info;

        foreach (string guid in AssetDatabase.FindAssets($"t:{nameof(ActionFitPackageInfo_SO)}", new[] { $"Packages/{packageId}" }))
        {
            info = AssetDatabase.LoadAssetAtPath<ActionFitPackageInfo_SO>(AssetDatabase.GUIDToAssetPath(guid));
            if (info != null && string.Equals(info.PackageId, packageId, StringComparison.Ordinal)) return info;
        }
        return null;
    }

    private static string ComputePlanId(ActionFitPackagePublishPlan plan)
    {
        string canonical = string.Join("\n", new[]
        {
            plan.PackageId,
            plan.Version,
            plan.ContentHash,
            plan.CatalogLatestVersion ?? "",
            plan.CatalogContainsVersion.ToString(),
            plan.GitHubOrganization,
            plan.RepositoryName,
            plan.RepositoryVisibility,
            plan.RepositoryExists.ToString(),
            plan.RemoteTagExists.ToString(),
        });
        using SHA256 sha = SHA256.Create();
        return BitConverter.ToString(sha.ComputeHash(Encoding.UTF8.GetBytes(canonical))).Replace("-", "").Substring(0, 20);
    }

    private static string SuggestNextVersion(string catalogLatest, string localVersion)
    {
        string basis = string.IsNullOrWhiteSpace(catalogLatest) ? localVersion : catalogLatest;
        string[] parts = (basis ?? "1.0.0").Split('.');
        int major = parts.Length > 0 && int.TryParse(new string(parts[0].TakeWhile(char.IsDigit).ToArray()), out int parsedMajor) ? parsedMajor : 1;
        int minor = parts.Length > 1 && int.TryParse(new string(parts[1].TakeWhile(char.IsDigit).ToArray()), out int parsedMinor) ? parsedMinor : 0;
        int patch = parts.Length > 2 && int.TryParse(new string(parts[2].TakeWhile(char.IsDigit).ToArray()), out int parsedPatch) ? parsedPatch : 0;
        return $"{major}.{minor}.{patch + 1}";
    }

    private static ActionFitPackagePublishPlan Fail(ActionFitPackagePublishPlan plan, string code, string message)
    {
        plan ??= new ActionFitPackagePublishPlan();
        plan.Success = false;
        plan.ReadyToPublish = false;
        plan.Code = code;
        plan.Message = message;
        return plan;
    }

    private static ActionFitPackagePublishExecutionResult ExecutionFailure(
        ActionFitPackagePublishExecuteRequest request,
        string code,
        string message,
        ActionFitPackagePublishPlan plan = null)
    {
        return new ActionFitPackagePublishExecutionResult
        {
            Success = false,
            Code = code,
            Message = message,
            PackageId = plan?.PackageId ?? request?.PackageId ?? "",
            Version = plan?.Version ?? "",
            PlanId = plan?.PlanId ?? request?.ExpectedPlanId ?? "",
            RepositoryUrl = plan?.RepositoryUrl ?? "",
        };
    }
}

/// <summary>
/// Batchmode entry points for publish preparation and explicitly approved execution.
/// </summary>
public static class ActionFitPackagePublishCli
{
    /// <summary>
    /// Prepares a read-only publish plan from the configured request/result files.
    /// </summary>
    public static void Prepare()
    {
        Run(false);
    }

    /// <summary>
    /// Executes a publish request only when it contains valid explicit approval.
    /// </summary>
    public static void Execute()
    {
        Run(true);
    }

    private static void Run(bool execute)
    {
        string requestPath = GetArgument("-actionFitPublishRequest");
        string resultPath = GetArgument("-actionFitPublishResult");
        string resultJson;
        try
        {
            if (string.IsNullOrWhiteSpace(requestPath)) throw new InvalidOperationException("-actionFitPublishRequest is required.");
            if (string.IsNullOrWhiteSpace(resultPath)) throw new InvalidOperationException("-actionFitPublishResult is required.");
            string requestJson = File.ReadAllText(Path.GetFullPath(requestPath));
            resultJson = execute
                ? ActionFitPackagePublishApi.ExecuteJson(requestJson)
                : ActionFitPackagePublishApi.PrepareJson(requestJson);
        }
        catch (Exception ex)
        {
            resultJson = JsonUtility.ToJson(new ActionFitPackagePublishExecutionResult
            {
                Success = false,
                Code = "CLI_FAILED",
                Message = ex.Message,
            }, true);
        }

        if (!string.IsNullOrWhiteSpace(resultPath))
        {
            string fullResultPath = Path.GetFullPath(resultPath);
            Directory.CreateDirectory(Path.GetDirectoryName(fullResultPath));
            File.WriteAllText(fullResultPath, resultJson, new UTF8Encoding(false));
        }
        Debug.Log($"[ActionFitPackageManager] Publish CLI {(execute ? "execute" : "prepare")} completed. Result: {resultPath}");
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
