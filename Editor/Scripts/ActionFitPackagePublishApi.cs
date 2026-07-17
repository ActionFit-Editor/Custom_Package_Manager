#if UNITY_EDITOR
using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using UnityEditor;
using UnityEngine;
using Debug = UnityEngine.Debug;

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
    public bool ReadyToRecoverCatalog;
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
    public bool RepositoryIsPrivate;
    public bool RemoteTagExists;
    public bool CatalogRecoveryContentMatches;
    public string RemoteTagCommit;
    public bool WillCreateRepository;
    public string GitHubOrganization;
    public string RepositoryName;
    public string RepositoryVisibility;
    public string RepositoryUrl;
    public bool RepositoryMigrationRequired;
    public bool WillMirrorRepository;
    public string SourceRepositoryUrl;
    public string SourceRepositoryVisibility;
    public string SourceDefaultBranch;
    public string TargetDefaultBranch;
    public int SourceBranchCount;
    public int SourceTagCount;
    public int TargetBranchCount;
    public int TargetTagCount;
    public int MissingRepositoryRefCount;
    public string RepositoryMigrationFingerprint;
    public bool SourceRepositoryRetirementCandidate;
    public string SourceRepositoryRetirementWarning;
    public string PlanId;
    public string RequiredApprovalText;
    public string RequiredMigrationApprovalText;
    public string RequiredCatalogRecoveryApprovalText;
    public ActionFitPackageContractDiagnostic[] ContractDiagnostics = Array.Empty<ActionFitPackageContractDiagnostic>();
    public string[] PlannedActions = Array.Empty<string>();
    public string[] Warnings = Array.Empty<string>();

    [NonSerialized]
    internal bool ContractValidatedInProcess;
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
    public bool ApproveRepositoryMigration;
    public string MigrationApprovalText;
    public ActionFitPackagePublishPlan ApprovedPlan;
}

/// <summary>
/// Explicitly approved request that registers an already published immutable tag in the catalog.
/// </summary>
[Serializable]
public sealed class ActionFitPackageCatalogRecoveryExecuteRequest
{
    public string PackageId;
    public string ExpectedPlanId;
    public string ApprovalText;
    public ActionFitPackagePublishPlan ApprovedPlan;
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
        var stopwatch = Stopwatch.StartNew();
        ActionFitPackagePublishPlan plan = PrepareLocal(request, true, out ActionFitPackagePublisher.PublishRequest publishRequest);
        if (!plan.Success)
            return LogPreparedPlan(plan, stopwatch);
        if (!request.CheckRemoteState)
            return LogPreparedPlan(
                Fail(plan, "REMOTE_CHECK_REQUIRED", "GitHub remote state must be checked before a publish plan can be approved."),
                stopwatch);
        if (!ActionFitPackagePublisher.TryGetRemoteState(
                publishRequest,
                out ActionFitPackagePublisher.RemoteState remote,
                out string remoteError))
        {
            return LogPreparedPlan(Fail(plan, "REMOTE_CHECK_FAILED", remoteError), stopwatch);
        }

        return LogPreparedPlan(CompleteRemotePreflight(plan, remote, publishRequest), stopwatch);
    }

    /// <summary>
    /// Prepares a catalog-only recovery plan after verifying that local package content matches the immutable remote tag.
    /// </summary>
    public static ActionFitPackagePublishPlan PrepareCatalogRecovery(ActionFitPackagePublishPrepareRequest request)
    {
        var stopwatch = Stopwatch.StartNew();
        ActionFitPackagePublishPlan plan = PrepareLocal(request, true, out ActionFitPackagePublisher.PublishRequest publishRequest);
        if (!plan.Success)
            return LogPreparedPlan(plan, stopwatch);
        if (!request.CheckRemoteState)
            return LogPreparedPlan(
                Fail(plan, "REMOTE_CHECK_REQUIRED", "GitHub remote state must be checked before catalog recovery can be approved."),
                stopwatch);
        if (!ActionFitPackagePublisher.TryGetRemoteState(
                publishRequest,
                out ActionFitPackagePublisher.RemoteState remote,
                out string remoteError))
        {
            return LogPreparedPlan(Fail(plan, "REMOTE_CHECK_FAILED", remoteError), stopwatch);
        }

        if (!remote.TagExists)
        {
            return LogPreparedPlan(
                CompleteCatalogRecoveryPreflight(plan, remote, false, "", $"Remote tag {plan.Version} does not exist."),
                stopwatch);
        }

        if (!ActionFitPackageCatalogRecoveryVerifier.TryVerify(
                publishRequest,
                out bool matches,
                out string tagCommit,
                out string verificationMessage))
        {
            return LogPreparedPlan(Fail(plan, "REMOTE_TAG_VERIFICATION_FAILED", verificationMessage), stopwatch);
        }

        return LogPreparedPlan(
            CompleteCatalogRecoveryPreflight(plan, remote, matches, tagCommit, verificationMessage),
            stopwatch);
    }

    internal static ActionFitPackagePublishPlan PrepareLocal(
        ActionFitPackagePublishPrepareRequest request,
        bool validateContract,
        out ActionFitPackagePublisher.PublishRequest publishRequest)
    {
        publishRequest = null;
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
            if (ActionFitPackageProjectOverrideApi.IsProjectOverride(request.PackageId))
            {
                return Fail(
                    plan,
                    "PROJECT_OVERRIDE_NOT_PUBLISHABLE",
                    "Project-owned overrides are excluded from upstream publishing. Restore the base package or fork it under a new package ID and repository.");
            }

            if (validateContract)
            {
                ActionFitPackageContractValidationResult contract =
                    ActionFitPackageContractValidator.ValidatePackage(request.PackageId);
                plan.ContractDiagnostics = contract.Diagnostics;
                if (!contract.Success)
                    return Fail(plan, contract.Code, contract.Message);
                plan.ContractValidatedInProcess = true;
            }

            string packagePath = ActionFitPackagePaths.PackagePath(request.PackageId);
            ActionFitPackageFileUtility.ValidateLocalPackageFolder(request.PackageId, packagePath);
            plan.PackagePath = ActionFitPackagePaths.ToProjectRelativePath(packagePath);

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
            if (inspection.CatalogPackageFound && string.IsNullOrWhiteSpace(inspection.RepositoryUrl))
            {
                return Fail(
                    plan,
                    "CATALOG_REPOSITORY_URL_MISSING",
                    "The registered package has no catalog repository URL. Publishing was blocked because existing version history cannot be verified or migrated safely.");
            }

            ActionFitPackageInfo_SO info = FindPackageInfo(request.PackageId);
            if (info == null)
                return Fail(plan, "PACKAGE_INFO_MISSING", "ActionFitPackageInfo_SO was not found inside the package.");

            if (!ActionFitPackagePublisher.TryCreatePublishRequest(
                    settings,
                    info,
                    info.RepositoryVisibility,
                    out publishRequest,
                    out string requestError))
            {
                return Fail(plan, "PUBLISH_REQUEST_INVALID", requestError);
            }

            plan.GitHubOrganization = publishRequest.GitHubOrganization;
            plan.RepositoryName = publishRequest.RepoName;
            plan.RepositoryVisibility = publishRequest.GitHubIsPrivate ? "Private" : "Public";
            plan.RepositoryUrl = $"https://github.com/{publishRequest.GitHubOrganization}/{publishRequest.RepoName}.git";
            plan.SourceRepositoryUrl = inspection.RepositoryUrl ?? "";
            publishRequest.SourceRepositoryUrl = plan.SourceRepositoryUrl;
            if (string.IsNullOrWhiteSpace(info.ReleaseNote))
                plan.Warnings = new[] { "Package release note is empty." };
            plan.Success = true;
            plan.Code = "LOCAL_PREFLIGHT_READY";
            plan.Message = $"Local publish preflight is ready for {plan.PackageId}@{plan.Version}.";
            return plan;
        }
        catch (Exception ex)
        {
            return Fail(plan, "PREPARE_FAILED", ex.Message);
        }
    }

    internal static ActionFitPackagePublishPlan CompleteRemotePreflight(
        ActionFitPackagePublishPlan plan,
        ActionFitPackagePublisher.RemoteState remote,
        ActionFitPackagePublisher.PublishRequest publishRequest = null)
    {
        if (plan == null || !plan.Success)
            return plan ?? Fail(new ActionFitPackagePublishPlan(), "PREPARE_FAILED", "Local publish preflight is missing.");
        if (remote == null)
            return Fail(plan, "REMOTE_CHECK_FAILED", "GitHub remote state is missing.");

        plan.RepositoryExists = remote.RepositoryExists;
        plan.RepositoryIsPrivate = remote.RepositoryIsPrivate;
        plan.RemoteTagExists = remote.TagExists;
        plan.WillCreateRepository = !remote.RepositoryExists;
        if (remote.RepositoryExists && remote.RepositoryIsPrivate !=
            string.Equals(plan.RepositoryVisibility, "Private", StringComparison.Ordinal))
        {
            bool sourceIsTarget = publishRequest != null &&
                                  ActionFitPackageRepositoryMigration.TryParseRepositoryUrl(
                                      publishRequest.SourceRepositoryUrl,
                                      out ActionFitPackageRepositoryMigration.RepositoryIdentity source) &&
                                  source.Equals(new ActionFitPackageRepositoryMigration.RepositoryIdentity(
                                      plan.GitHubOrganization,
                                      plan.RepositoryName));
            return Fail(
                plan,
                sourceIsTarget ? "SOURCE_REPOSITORY_VISIBILITY_CHANGE_BLOCKED" : "TARGET_REPOSITORY_VISIBILITY_MISMATCH",
                sourceIsTarget
                    ? $"The selected visibility change resolves to the same existing " +
                      $"{(remote.RepositoryIsPrivate ? "Private" : "Public")} repository. " +
                      $"Configure a different {(string.Equals(plan.RepositoryVisibility, "Private", StringComparison.Ordinal) ? "Private" : "Public")} " +
                      "organization or repository name; source visibility is never changed in place."
                    : $"Existing target repository visibility does not match selected {plan.RepositoryVisibility}. " +
                      "Repository visibility is never changed automatically.");
        }
        if (plan.RemoteTagExists)
            return Fail(plan, "REMOTE_TAG_ALREADY_EXISTS", $"Remote tag {plan.Version} already exists. Published tags are immutable; bump package.json.");

        if (publishRequest != null)
        {
            if (!ActionFitPackageRepositoryMigration.TryInspect(
                    publishRequest,
                    remote,
                    out ActionFitPackageRepositoryMigration.RepositoryMigrationState migration,
                    out string migrationCode,
                    out string migrationMessage))
            {
                return Fail(plan, migrationCode, migrationMessage);
            }

            plan.RepositoryMigrationRequired = migration.Required;
            plan.WillMirrorRepository = migration.WillMirrorRepository;
            plan.SourceRepositoryUrl = migration.SourceRepositoryUrl;
            plan.SourceRepositoryVisibility = migration.SourceRepositoryExists
                ? migration.SourceRepositoryIsPrivate ? "Private" : "Public"
                : "";
            plan.SourceDefaultBranch = migration.SourceDefaultBranch;
            plan.TargetDefaultBranch = migration.TargetDefaultBranch;
            plan.SourceBranchCount = migration.SourceBranchCount;
            plan.SourceTagCount = migration.SourceTagCount;
            plan.TargetBranchCount = migration.TargetBranchCount;
            plan.TargetTagCount = migration.TargetTagCount;
            plan.MissingRepositoryRefCount = migration.MissingRefCount;
            plan.RepositoryMigrationFingerprint = migration.Fingerprint;
            plan.SourceRepositoryRetirementCandidate =
                migration.Required &&
                migration.SourceRepositoryIsPrivate &&
                !publishRequest.GitHubIsPrivate;
            plan.SourceRepositoryRetirementWarning = plan.SourceRepositoryRetirementCandidate
                ? "The Private source can be prepared for Keep, Archive, or Delete only after publication and Catalog verification. Keep is the default."
                : "";
        }

        var actions = new System.Collections.Generic.List<string>
        {
            plan.WillCreateRepository ? $"Create {plan.RepositoryVisibility} repository {plan.GitHubOrganization}/{plan.RepositoryName}" : $"Use existing repository {plan.GitHubOrganization}/{plan.RepositoryName}",
        };
        if (plan.RepositoryMigrationRequired)
        {
            actions.Add(plan.WillMirrorRepository
                ? $"Mirror all source branches and tags from {plan.SourceRepositoryUrl} and verify the {plan.RepositoryVisibility} target"
                : $"Verify the existing {plan.RepositoryVisibility} target contains every source branch and tag");
            actions.Add(plan.SourceRepositoryRetirementCandidate
                ? "Keep the Private source unless a separate post-publish Archive or Delete plan is explicitly approved"
                : "Keep the source repository unchanged");
        }
        actions.Add($"Push package content to main for {plan.PackageId}@{plan.Version}");
        actions.Add($"Create and push immutable tag {plan.Version}");
        actions.Add($"Upsert catalog row {plan.PackageId}@{plan.Version}");
        actions.Add("Refresh the shared package catalog");
        plan.PlannedActions = actions.ToArray();
        plan.PlanId = ComputePlanId(plan);
        plan.RequiredApprovalText = $"PUBLISH {plan.PackageId}@{plan.Version} PLAN {plan.PlanId}";
        plan.RequiredMigrationApprovalText = plan.RepositoryMigrationRequired
            ? $"MIGRATE {plan.PackageId} FROM {plan.SourceRepositoryUrl} TO {plan.RepositoryUrl} PLAN {plan.PlanId}"
            : "";
        plan.ReadyToPublish = true;
        plan.Success = true;
        plan.Code = "READY_TO_PUBLISH";
        plan.Message = $"Publish plan is ready for {plan.PackageId}@{plan.Version}. No external state was changed.";
        return plan;
    }

    internal static ActionFitPackagePublishPlan CompleteCatalogRecoveryPreflight(
        ActionFitPackagePublishPlan plan,
        ActionFitPackagePublisher.RemoteState remote,
        bool contentMatches,
        string tagCommit,
        string verificationMessage)
    {
        if (plan == null || !plan.Success)
            return plan ?? Fail(new ActionFitPackagePublishPlan(), "PREPARE_FAILED", "Local catalog recovery preflight is missing.");
        if (remote == null)
            return Fail(plan, "REMOTE_CHECK_FAILED", "GitHub remote state is missing.");

        plan.RepositoryExists = remote.RepositoryExists;
        plan.RepositoryIsPrivate = remote.RepositoryIsPrivate;
        plan.RemoteTagExists = remote.TagExists;
        plan.CatalogRecoveryContentMatches = contentMatches;
        plan.RemoteTagCommit = tagCommit ?? "";
        if (!remote.RepositoryExists)
            return Fail(plan, "RECOVERY_REPOSITORY_MISSING", "Catalog recovery requires the already published repository.");
        if (remote.RepositoryIsPrivate != string.Equals(plan.RepositoryVisibility, "Private", StringComparison.Ordinal))
        {
            return Fail(
                plan,
                "RECOVERY_REPOSITORY_VISIBILITY_MISMATCH",
                $"Existing repository visibility does not match selected {plan.RepositoryVisibility}. Catalog recovery was blocked.");
        }
        if (!remote.TagExists)
            return Fail(plan, "RECOVERY_REMOTE_TAG_MISSING", verificationMessage);
        if (!contentMatches)
        {
            plan.SuggestedNextVersion = IncrementPatchVersion(plan.Version);
            return Fail(
                plan,
                "REMOTE_TAG_CONTENT_MISMATCH",
                $"Remote tag {plan.Version} exists, but its package content does not match the local publish candidate. " +
                $"Catalog recovery was blocked. {verificationMessage} Bump package.json to {plan.SuggestedNextVersion} for new changes.");
        }

        plan.PlannedActions = new[]
        {
            $"Use immutable tag {plan.Version} at {plan.RemoteTagCommit}",
            $"Upsert catalog row {plan.PackageId}@{plan.Version}",
            "Refresh the shared package catalog",
            "Do not push or modify any repository branch or tag",
        };
        plan.PlanId = ComputePlanId(plan);
        plan.RequiredCatalogRecoveryApprovalText =
            $"RECOVER CATALOG {plan.PackageId}@{plan.Version} TAG {plan.RemoteTagCommit} PLAN {plan.PlanId}";
        plan.ReadyToPublish = false;
        plan.ReadyToRecoverCatalog = true;
        plan.Success = true;
        plan.Code = "READY_TO_RECOVER_CATALOG";
        plan.Message =
            $"Catalog-only recovery is ready for {plan.PackageId}@{plan.Version}. " +
            "The repository and immutable tag will not be changed.";
        return plan;
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

        ActionFitPackagePublishPlan current;
        if (request.ApprovedPlan != null &&
            !ApprovedPlanMatches(
                request.ApprovedPlan,
                request.PackageId,
                request.ExpectedPlanId,
                request.ApprovalText))
        {
            return ExecutionFailure(
                request,
                "APPROVED_PLAN_MISMATCH",
                "ApprovedPlan must match PackageId, ExpectedPlanId, and ApprovalText from Prepare.");
        }

        if (request.ApprovedPlan?.ContractValidatedInProcess == true)
        {
            current = PrepareLocal(
                new ActionFitPackagePublishPrepareRequest
                {
                    PackageId = request.PackageId,
                    RefreshCatalog = true,
                    CheckRemoteState = true,
                },
                false,
                out ActionFitPackagePublisher.PublishRequest revalidatedRequest);
            if (current.Success)
            {
                current = ActionFitPackagePublisher.TryGetRemoteState(
                    revalidatedRequest,
                    out ActionFitPackagePublisher.RemoteState remote,
                    out string remoteError)
                    ? CompleteRemotePreflight(current, remote, revalidatedRequest)
                    : Fail(current, "REMOTE_CHECK_FAILED", remoteError);
            }
        }
        else
        {
            current = Prepare(new ActionFitPackagePublishPrepareRequest
            {
                PackageId = request.PackageId,
                RefreshCatalog = true,
                CheckRemoteState = true,
            });
        }
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
        if (!MigrationApprovalMatches(
                current,
                request.ApproveRepositoryMigration,
                request.MigrationApprovalText))
        {
            return ExecutionFailure(
                request,
                "REPOSITORY_MIGRATION_APPROVAL_REQUIRED",
                "Repository migration requires a separate approval flag and the exact MigrationApprovalText returned by Prepare.",
                current);
        }

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
            publishRequest.SourceRepositoryUrl = current.SourceRepositoryUrl;

            if (current.RepositoryMigrationRequired)
            {
                ActionFitPackageRepositoryMigration.MigrationResult migration =
                    ActionFitPackageRepositoryMigration.Execute(
                        publishRequest,
                        current.RepositoryMigrationFingerprint);
                if (!migration.Success)
                    return ExecutionFailure(request, "REPOSITORY_MIGRATION_FAILED", migration.Message, current);
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
    /// Revalidates and appends only the catalog row for a matching immutable remote tag.
    /// </summary>
    public static ActionFitPackagePublishExecutionResult ExecuteCatalogRecovery(
        ActionFitPackageCatalogRecoveryExecuteRequest request)
    {
        if (request == null)
            return CatalogRecoveryFailure(null, "INVALID_REQUEST", "Catalog recovery execute request is missing.");
        if (string.IsNullOrWhiteSpace(request.ExpectedPlanId) || string.IsNullOrWhiteSpace(request.ApprovalText))
            return CatalogRecoveryFailure(request, "APPROVAL_REQUIRED", "ExpectedPlanId and exact ApprovalText from catalog recovery preparation are required.");
        if (request.ApprovedPlan != null &&
            !CatalogRecoveryApprovedPlanMatches(
                request.ApprovedPlan,
                request.PackageId,
                request.ExpectedPlanId,
                request.ApprovalText))
        {
            return CatalogRecoveryFailure(
                request,
                "APPROVED_PLAN_MISMATCH",
                "ApprovedPlan must match PackageId, ExpectedPlanId, and ApprovalText from catalog recovery preparation.");
        }

        ActionFitPackagePublishPlan current = PrepareCatalogRecovery(new ActionFitPackagePublishPrepareRequest
        {
            PackageId = request.PackageId,
            RefreshCatalog = true,
            CheckRemoteState = true,
        });
        if (!current.Success || !current.ReadyToRecoverCatalog)
        {
            if (string.Equals(current.Code, "VERSION_ALREADY_IN_CATALOG", StringComparison.Ordinal))
            {
                return new ActionFitPackagePublishExecutionResult
                {
                    Success = true,
                    RepositoryPublished = false,
                    CatalogAppended = false,
                    Code = "CATALOG_ALREADY_RECOVERED",
                    Message = $"Catalog already contains {current.PackageId}@{current.Version}; no duplicate row was appended.",
                    PackageId = current.PackageId,
                    Version = current.Version,
                    PlanId = request.ExpectedPlanId,
                };
            }
            return CatalogRecoveryFailure(request, "PREFLIGHT_FAILED", current.Message, current);
        }
        if (!string.Equals(current.PlanId, request.ExpectedPlanId, StringComparison.Ordinal))
        {
            return CatalogRecoveryFailure(
                request,
                "PLAN_CHANGED",
                $"Catalog recovery state changed after approval. Expected plan {request.ExpectedPlanId}, current plan {current.PlanId}. Prepare and approve again.",
                current);
        }
        if (!string.Equals(current.RequiredCatalogRecoveryApprovalText, request.ApprovalText, StringComparison.Ordinal))
            return CatalogRecoveryFailure(request, "APPROVAL_TEXT_MISMATCH", "ApprovalText does not exactly match the prepared catalog recovery plan.", current);

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
                return CatalogRecoveryFailure(request, "PUBLISH_REQUEST_INVALID", requestError, current);
            }

            bool catalogAppended = TryAppendCatalogWithSafeFallback(
                new[] { publishRequest.CatalogItem },
                out string catalogMessage);
            if (!catalogAppended)
            {
                return new ActionFitPackagePublishExecutionResult
                {
                    Success = false,
                    RepositoryPublished = false,
                    CatalogAppended = false,
                    RetryCatalogAppendAvailable = true,
                    Code = "CATALOG_RECOVERY_APPEND_FAILED",
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
                warnings = new[] { $"Catalog row recovery succeeded, but catalog refresh failed: {refreshMessage}" };

            return new ActionFitPackagePublishExecutionResult
            {
                Success = true,
                RepositoryPublished = false,
                CatalogAppended = true,
                Code = "CATALOG_RECOVERED",
                Message =
                    $"{current.PackageId}@{current.Version} was registered in the catalog without changing the repository or tag.",
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
            return CatalogRecoveryFailure(request, "CATALOG_RECOVERY_FAILED", ex.Message, current);
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

    public static string PrepareCatalogRecoveryJson(string requestJson)
    {
        try
        {
            return JsonUtility.ToJson(
                PrepareCatalogRecovery(JsonUtility.FromJson<ActionFitPackagePublishPrepareRequest>(requestJson ?? "")),
                true);
        }
        catch (Exception ex)
        {
            return JsonUtility.ToJson(Fail(new ActionFitPackagePublishPlan(), "INVALID_REQUEST_JSON", ex.Message), true);
        }
    }

    public static string ExecuteCatalogRecoveryJson(string requestJson)
    {
        try
        {
            return JsonUtility.ToJson(
                ExecuteCatalogRecovery(JsonUtility.FromJson<ActionFitPackageCatalogRecoveryExecuteRequest>(requestJson ?? "")),
                true);
        }
        catch (Exception ex)
        {
            return JsonUtility.ToJson(CatalogRecoveryFailure(null, "INVALID_REQUEST_JSON", ex.Message), true);
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

    internal static bool ApprovedPlanMatches(
        ActionFitPackagePublishPlan plan,
        string packageId,
        string expectedPlanId,
        string approvalText)
    {
        return plan != null &&
               plan.Success &&
               plan.ReadyToPublish &&
               string.Equals(plan.PackageId, packageId, StringComparison.Ordinal) &&
               string.Equals(plan.PlanId, expectedPlanId, StringComparison.Ordinal) &&
               string.Equals(plan.RequiredApprovalText, approvalText, StringComparison.Ordinal);
    }

    internal static bool CatalogRecoveryApprovedPlanMatches(
        ActionFitPackagePublishPlan plan,
        string packageId,
        string expectedPlanId,
        string approvalText)
    {
        return plan != null &&
               plan.Success &&
               plan.ReadyToRecoverCatalog &&
               string.Equals(plan.PackageId, packageId, StringComparison.Ordinal) &&
               string.Equals(plan.PlanId, expectedPlanId, StringComparison.Ordinal) &&
               string.Equals(plan.RequiredCatalogRecoveryApprovalText, approvalText, StringComparison.Ordinal);
    }

    internal static bool MigrationApprovalMatches(
        ActionFitPackagePublishPlan plan,
        bool approved,
        string approvalText)
    {
        return plan != null &&
               (!plan.RepositoryMigrationRequired ||
                approved && string.Equals(
                    plan.RequiredMigrationApprovalText,
                    approvalText,
                    StringComparison.Ordinal));
    }

    private static ActionFitPackagePublishPlan LogPreparedPlan(
        ActionFitPackagePublishPlan plan,
        Stopwatch stopwatch)
    {
        string outcome = plan != null && plan.Success ? "complete" : "failed";
        Debug.Log(
            $"[ActionFitPackageManager] Publish preflight {outcome}: {plan?.PackageId ?? "<unknown>"} " +
            $"({stopwatch.ElapsedMilliseconds} ms, code={plan?.Code ?? "UNKNOWN"})");
        return plan;
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
            plan.RepositoryIsPrivate.ToString(),
            plan.RemoteTagExists.ToString(),
            plan.CatalogRecoveryContentMatches.ToString(),
            plan.RemoteTagCommit ?? "",
            plan.SourceRepositoryUrl ?? "",
            plan.RepositoryMigrationRequired.ToString(),
            plan.WillMirrorRepository.ToString(),
            plan.RepositoryMigrationFingerprint ?? "",
            plan.SourceRepositoryRetirementCandidate.ToString(),
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

    private static string IncrementPatchVersion(string version)
        => SuggestNextVersion("", version);

    private static bool TryAppendCatalogWithSafeFallback(
        ActionFitPackagePublisher.CatalogAppendItem[] items,
        out string message)
    {
        if (ActionFitPackagePublisher.TryAppendCatalogBatch(items, out message)) return true;

        string batchMessage = message;
        if (!ActionFitPackageBulkPublishApi.ShouldAttemptSerialFallback(batchMessage))
        {
            message =
                $"Catalog batch append failed and serial fallback was skipped because the outcome was ambiguous. {batchMessage}";
            return false;
        }

        bool serialSucceeded = ActionFitPackagePublisher.TryAppendCatalogSerial(items, out string serialMessage);
        message = serialSucceeded
            ? $"Batch append failed, serial upsert succeeded. Batch: {batchMessage} Serial: {serialMessage}"
            : $"Batch append failed: {batchMessage} Serial upsert failed: {serialMessage}";
        return serialSucceeded;
    }

    private static ActionFitPackagePublishPlan Fail(ActionFitPackagePublishPlan plan, string code, string message)
    {
        plan ??= new ActionFitPackagePublishPlan();
        plan.Success = false;
        plan.ReadyToPublish = false;
        plan.ReadyToRecoverCatalog = false;
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

    private static ActionFitPackagePublishExecutionResult CatalogRecoveryFailure(
        ActionFitPackageCatalogRecoveryExecuteRequest request,
        string code,
        string message,
        ActionFitPackagePublishPlan plan = null)
    {
        return new ActionFitPackagePublishExecutionResult
        {
            Success = false,
            RepositoryPublished = false,
            CatalogAppended = false,
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
