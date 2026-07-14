#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using UnityEngine;
using Debug = UnityEngine.Debug;

[Serializable]
public sealed class ActionFitPackageBulkPublishPrepareRequest
{
    public string[] PackageIds = Array.Empty<string>();
    public bool RefreshCatalog = true;
    public bool CheckRemoteState = true;
}

[Serializable]
public sealed class ActionFitPackageBulkPublishPlan
{
    public bool Success;
    public bool ReadyToPublish;
    public string Code;
    public string Message;
    public string PlanId;
    public string RequiredApprovalText;
    public string[] PackageIds = Array.Empty<string>();
    public string[] RepositoryCreationPackageIds = Array.Empty<string>();
    public string[] RepositoryMigrationPackageIds = Array.Empty<string>();
    public string RequiredMigrationApprovalText;
    public ActionFitPackagePublishPlan[] Packages = Array.Empty<ActionFitPackagePublishPlan>();
    public int MaxParallelPublishes = ActionFitPackagePublisher.DefaultMaxParallelPublishes;
    public string[] Warnings = Array.Empty<string>();
}

[Serializable]
public sealed class ActionFitPackageBulkPublishExecuteRequest
{
    public string[] PackageIds = Array.Empty<string>();
    public string ExpectedPlanId;
    public string ApprovalText;
    public string[] ApprovedRepositoryCreationPackageIds = Array.Empty<string>();
    public string[] ApprovedRepositoryMigrationPackageIds = Array.Empty<string>();
    public string MigrationApprovalText;
    public ActionFitPackageBulkPublishPlan ApprovedPlan;
}

public sealed class ActionFitPackageBulkPublishProgress
{
    public string Stage;
    public string Message;
    public int Completed;
    public int Total;
    public float Fraction;
}

[Serializable]
public sealed class ActionFitPackageBulkPublishPackageResult
{
    public string PackageId;
    public string Version;
    public bool RepositoryPublished;
    public string RepositoryUrl;
    public string CatalogId;
    public string Message;
}

[Serializable]
public sealed class ActionFitPackageBulkPublishExecutionResult
{
    public bool Success;
    public bool AllRepositoriesPublished;
    public bool CatalogAppended;
    public bool RetryCatalogAppendAvailable;
    public string Code;
    public string Message;
    public string PlanId;
    public ActionFitPackageBulkPublishPackageResult[] Packages = Array.Empty<ActionFitPackageBulkPublishPackageResult>();
    public string[] RetryCatalogPackageIds = Array.Empty<string>();
    public string[] Warnings = Array.Empty<string>();

    [NonSerialized]
    public ActionFitPackagePublisher.CatalogAppendItem[] RetryCatalogItems = Array.Empty<ActionFitPackagePublisher.CatalogAppendItem>();
}

/// <summary>
/// Dialog-free, approval-gated API shared by AI callers and Publish All Changed.
/// </summary>
public static class ActionFitPackageBulkPublishApi
{
    public static ActionFitPackageBulkPublishPlan PrepareAllChanged(ActionFitPackageBulkPublishPrepareRequest request)
        => PrepareAllChanged(request, null, CancellationToken.None);

    public static ActionFitPackageBulkPublishPlan PrepareAllChanged(
        ActionFitPackageBulkPublishPrepareRequest request,
        Action<ActionFitPackageBulkPublishProgress> onProgress,
        CancellationToken cancellationToken)
        => PrepareAllChangedCore(request, true, onProgress, cancellationToken);

    private static ActionFitPackageBulkPublishPlan PrepareAllChangedCore(
        ActionFitPackageBulkPublishPrepareRequest request,
        bool validateContract,
        Action<ActionFitPackageBulkPublishProgress> onProgress,
        CancellationToken cancellationToken)
    {
        var plan = new ActionFitPackageBulkPublishPlan();
        var stopwatch = Stopwatch.StartNew();
        try
        {
            request ??= new ActionFitPackageBulkPublishPrepareRequest();
            if (!request.CheckRemoteState)
                return Fail(plan, "REMOTE_CHECK_REQUIRED", "GitHub remote state must be checked before a bulk publish plan can be approved.");

            if (request.RefreshCatalog)
            {
                ReportProgress(onProgress, "catalog-refresh", "Refreshing catalog before preflight...", 0, 1, 0.05f);
                cancellationToken.ThrowIfCancellationRequested();
                ActionFitPackageCatalogSettings_SO settings = ActionFitPackageCatalogSettingsProvider.FindOrCreate();
                if (!ActionFitPackageCatalogUpdater.UpdateCatalog(settings, out string refreshError, false))
                    return LogPreparedPlan(
                        Fail(plan, "CATALOG_REFRESH_FAILED", $"Catalog refresh failed; bulk publishing was not prepared: {refreshError}"),
                        stopwatch);
            }

            string[] packageIds = NormalizePackageIds(request.PackageIds);
            if (packageIds.Length == 0)
                packageIds = DiscoverChangedPackageIds();
            if (packageIds.Length == 0)
                return LogPreparedPlan(
                    Fail(plan, "NO_CHANGED_PACKAGES", "No changed embedded ActionFit packages are ready to publish."),
                    stopwatch);

            var packages = new List<ActionFitPackagePublishPlan>();
            var publishRequests = new List<ActionFitPackagePublisher.PublishRequest>();
            for (int index = 0; index < packageIds.Length; index++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                string packageId = packageIds[index];
                ReportProgress(
                    onProgress,
                    "local-preflight",
                    $"Validating local package {index + 1}/{packageIds.Length}: {packageId}",
                    index,
                    packageIds.Length,
                    0.1f + 0.25f * index / packageIds.Length);
                ActionFitPackagePublishPlan packagePlan = ActionFitPackagePublishApi.PrepareLocal(
                    new ActionFitPackagePublishPrepareRequest
                    {
                        PackageId = packageId,
                        RefreshCatalog = false,
                        CheckRemoteState = true,
                    },
                    validateContract,
                    out ActionFitPackagePublisher.PublishRequest publishRequest);
                packages.Add(packagePlan);
                publishRequests.Add(publishRequest);
            }

            plan.PackageIds = packageIds;
            plan.Packages = packages.ToArray();
            ActionFitPackagePublishPlan[] failed = packages.Where(item => !item.Success).ToArray();
            if (failed.Length > 0)
            {
                string details = string.Join("\n", failed.Select(item => $"- {item.PackageId}: {item.Code} - {item.Message}"));
                return LogPreparedPlan(
                    Fail(plan, "PACKAGE_PREFLIGHT_FAILED", $"Bulk publish preflight failed. No external state was changed.\n{details}"),
                    stopwatch);
            }

            RemotePreflightResult[] remoteResults = CheckRemoteStatesParallel(
                publishRequests,
                onProgress,
                cancellationToken);
            for (int index = 0; index < packages.Count; index++)
            {
                RemotePreflightResult remoteResult = remoteResults[index];
                packages[index] = remoteResult.Success
                    ? ActionFitPackagePublishApi.CompleteRemotePreflight(
                        packages[index],
                        remoteResult.State,
                        publishRequests[index])
                    : FailPackage(packages[index], "REMOTE_CHECK_FAILED", remoteResult.Message);
            }

            plan.Packages = packages.ToArray();
            failed = packages.Where(item => !item.Success || !item.ReadyToPublish).ToArray();
            if (failed.Length > 0)
            {
                string details = string.Join("\n", failed.Select(item => $"- {item.PackageId}: {item.Code} - {item.Message}"));
                return LogPreparedPlan(
                    Fail(plan, "PACKAGE_PREFLIGHT_FAILED", $"Bulk publish preflight failed. No external state was changed.\n{details}"),
                    stopwatch);
            }

            plan.RepositoryCreationPackageIds = packages
                .Where(item => item.WillCreateRepository)
                .Select(item => item.PackageId)
                .OrderBy(value => value, StringComparer.Ordinal)
                .ToArray();
            plan.RepositoryMigrationPackageIds = packages
                .Where(item => item.RepositoryMigrationRequired)
                .Select(item => item.PackageId)
                .OrderBy(value => value, StringComparer.Ordinal)
                .ToArray();
            plan.PlanId = ComputePlanId(packages);
            plan.RequiredApprovalText = $"PUBLISH ALL {packages.Count} PACKAGES PLAN {plan.PlanId}";
            plan.RequiredMigrationApprovalText = plan.RepositoryMigrationPackageIds.Length == 0
                ? ""
                : $"MIGRATE REPOSITORIES {string.Join(",", plan.RepositoryMigrationPackageIds)} PLAN {plan.PlanId}";
            plan.Warnings = packages.SelectMany(item => item.Warnings ?? Array.Empty<string>()).Distinct().ToArray();
            plan.Success = true;
            plan.ReadyToPublish = true;
            plan.Code = "READY_TO_PUBLISH";
            plan.Message = $"Bulk publish plan is ready for {packages.Count} package(s). No external state was changed.";
            ReportProgress(onProgress, "preflight-complete", plan.Message, packages.Count, packages.Count, 0.5f);
            return LogPreparedPlan(plan, stopwatch);
        }
        catch (OperationCanceledException)
        {
            return LogPreparedPlan(Fail(plan, "CANCELED", "Bulk publish preflight was canceled."), stopwatch);
        }
        catch (Exception ex)
        {
            return LogPreparedPlan(Fail(plan, "PREPARE_FAILED", ex.Message), stopwatch);
        }
    }

    public static ActionFitPackageBulkPublishExecutionResult ExecuteAll(ActionFitPackageBulkPublishExecuteRequest request)
        => ExecuteAll(request, null, CancellationToken.None);

    public static ActionFitPackageBulkPublishExecutionResult ExecuteAll(
        ActionFitPackageBulkPublishExecuteRequest request,
        Action<ActionFitPackageBulkPublishProgress> onProgress,
        CancellationToken cancellationToken)
    {
        var stopwatch = Stopwatch.StartNew();
        if (request == null)
            return LogExecutionResult(
                ExecutionFailure(null, "INVALID_REQUEST", "Bulk publish execute request is missing."),
                stopwatch);
        if (string.IsNullOrWhiteSpace(request.ExpectedPlanId) || string.IsNullOrWhiteSpace(request.ApprovalText))
            return LogExecutionResult(
                ExecutionFailure(request, "APPROVAL_REQUIRED", "ExpectedPlanId and exact ApprovalText from PrepareAllChanged are required."),
                stopwatch);

        bool hasApprovedPlan = request.ApprovedPlan != null;
        if (hasApprovedPlan && !ApprovedPlanMatches(request))
        {
            return LogExecutionResult(
                ExecutionFailure(
                    request,
                    "APPROVED_PLAN_MISMATCH",
                    "ApprovedPlan must exactly match PackageIds, ExpectedPlanId, ApprovalText, and the package plans returned by PrepareAllChanged."),
                stopwatch);
        }
        bool reuseApprovedPlan = hasApprovedPlan &&
                                 request.ApprovedPlan.Packages.All(
                                     item => item.ContractValidatedInProcess);

        ActionFitPackageBulkPublishPlan current = PrepareAllChangedCore(
            new ActionFitPackageBulkPublishPrepareRequest
            {
                PackageIds = request.PackageIds,
                RefreshCatalog = true,
                CheckRemoteState = true,
            },
            !reuseApprovedPlan,
            onProgress,
            cancellationToken);
        if (!current.Success || !current.ReadyToPublish)
            return LogExecutionResult(ExecutionFailure(request, "PREFLIGHT_FAILED", current.Message, current), stopwatch);
        if (!string.Equals(current.PlanId, request.ExpectedPlanId, StringComparison.Ordinal))
            return LogExecutionResult(
                ExecutionFailure(request, "PLAN_CHANGED", $"Bulk publish state changed after approval. Expected plan {request.ExpectedPlanId}, current plan {current.PlanId}. Prepare and approve again.", current),
                stopwatch);
        if (!string.Equals(current.RequiredApprovalText, request.ApprovalText, StringComparison.Ordinal))
            return LogExecutionResult(
                ExecutionFailure(request, "APPROVAL_TEXT_MISMATCH", "ApprovalText does not exactly match the prepared bulk publish plan.", current),
                stopwatch);

        string[] approvedCreates = NormalizePackageIds(request.ApprovedRepositoryCreationPackageIds);
        if (!new HashSet<string>(current.RepositoryCreationPackageIds, StringComparer.Ordinal).SetEquals(approvedCreates))
        {
            return LogExecutionResult(
                ExecutionFailure(
                    request,
                    "REPOSITORY_CREATION_APPROVAL_REQUIRED",
                    "ApprovedRepositoryCreationPackageIds must exactly match the repositories marked for creation by PrepareAllChanged.",
                    current),
                stopwatch);
        }

        if (!MigrationApprovalsMatch(
                current,
                request.ApprovedRepositoryMigrationPackageIds,
                request.MigrationApprovalText))
        {
            return LogExecutionResult(
                ExecutionFailure(
                    request,
                    "REPOSITORY_MIGRATION_APPROVAL_REQUIRED",
                    "ApprovedRepositoryMigrationPackageIds and MigrationApprovalText must exactly match the separate migration approval returned by PrepareAllChanged.",
                    current),
                stopwatch);
        }

        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            ReportProgress(onProgress, "snapshot", "Creating immutable publish snapshots...", 0, current.Packages.Length, 0.52f);
            ActionFitPackageCatalogSettings_SO settings = ActionFitPackageCatalogSettingsProvider.FindOrCreate();
            var publishRequests = new List<ActionFitPackagePublisher.PublishRequest>();
            for (int index = 0; index < current.Packages.Length; index++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                ActionFitPackagePublishPlan packagePlan = current.Packages[index];
                ActionFitPackageInfo_SO info = ActionFitPackagePublishApi.FindPackageInfo(packagePlan.PackageId);
                if (!ActionFitPackagePublisher.TryCreatePublishRequest(
                        settings,
                        info,
                        info.RepositoryVisibility,
                        out ActionFitPackagePublisher.PublishRequest publishRequest,
                        out string requestError))
                {
                    return LogExecutionResult(
                        ExecutionFailure(request, "PUBLISH_REQUEST_INVALID", $"{packagePlan.PackageId}: {requestError}", current),
                        stopwatch);
                }
                publishRequest.SourceRepositoryUrl = packagePlan.SourceRepositoryUrl;
                publishRequests.Add(publishRequest);
            }

            for (int index = 0; index < current.Packages.Length; index++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                ActionFitPackagePublishPlan packagePlan = current.Packages[index];
                if (!packagePlan.RepositoryMigrationRequired) continue;
                ReportProgress(
                    onProgress,
                    "repository-migration",
                    $"Migrating repository {index + 1}/{current.Packages.Length}: {packagePlan.PackageId}",
                    index,
                    current.Packages.Length,
                    0.54f);
                ActionFitPackageRepositoryMigration.MigrationResult migration =
                    ActionFitPackageRepositoryMigration.Execute(
                        publishRequests[index],
                        packagePlan.RepositoryMigrationFingerprint);
                if (!migration.Success)
                {
                    return LogExecutionResult(
                        ExecutionFailure(
                            request,
                            "REPOSITORY_MIGRATION_FAILED",
                            $"{packagePlan.PackageId}: {migration.Message} Catalog rows were not changed.",
                            current),
                        stopwatch);
                }
            }

            ActionFitPackagePublisher.PublishResult[] repositoryResults = PublishRepositoriesParallel(
                publishRequests,
                onProgress,
                cancellationToken);
            ActionFitPackagePublisher.PublishResult[] succeeded = repositoryResults.Where(item => item.Success).ToArray();
            ActionFitPackagePublisher.PublishResult[] failed = repositoryResults.Where(item => !item.Success).ToArray();
            ActionFitPackageBulkPublishPackageResult[] packageResults = repositoryResults.Select(ToPackageResult).ToArray();
            ActionFitPackagePublisher.CatalogAppendItem[] successfulCatalogItems = succeeded.Select(item => item.Request.CatalogItem).ToArray();

            if (failed.Length > 0)
            {
                string details = string.Join("\n", failed.Select(item => $"- {item.Request?.PackageId ?? "<unknown>"}: {item.Message}"));
                return LogExecutionResult(new ActionFitPackageBulkPublishExecutionResult
                {
                    Success = false,
                    AllRepositoriesPublished = false,
                    CatalogAppended = false,
                    RetryCatalogAppendAvailable = successfulCatalogItems.Length > 0,
                    Code = cancellationToken.IsCancellationRequested
                        ? "REPOSITORY_PUBLISH_CANCELED"
                        : "REPOSITORY_PUBLISH_FAILED",
                    Message = cancellationToken.IsCancellationRequested
                        ? $"Repository publishing was canceled. Active Git operations finished safely; queued packages were not started. Catalog rows were not appended.\n{details}"
                        : $"One or more repository publishes failed. Catalog rows were not appended.\n{details}",
                    PlanId = current.PlanId,
                    Packages = packageResults,
                    RetryCatalogPackageIds = successfulCatalogItems.Select(item => item.PackageId).ToArray(),
                    RetryCatalogItems = successfulCatalogItems,
                }, stopwatch);
            }

            if (cancellationToken.IsCancellationRequested)
            {
                return LogExecutionResult(new ActionFitPackageBulkPublishExecutionResult
                {
                    Success = false,
                    AllRepositoriesPublished = true,
                    CatalogAppended = false,
                    RetryCatalogAppendAvailable = true,
                    Code = "CANCELED_AFTER_REPOSITORY_PUBLISH",
                    Message = "Repository publishing completed, but catalog append was canceled. Retry the retained catalog rows without publishing repositories again.",
                    PlanId = current.PlanId,
                    Packages = packageResults,
                    RetryCatalogPackageIds = successfulCatalogItems.Select(item => item.PackageId).ToArray(),
                    RetryCatalogItems = successfulCatalogItems,
                }, stopwatch);
            }

            CatalogAppendOutcome catalogOutcome = AppendCatalogWithProgress(
                successfulCatalogItems,
                onProgress,
                cancellationToken);
            if (!catalogOutcome.Success)
            {
                return LogExecutionResult(new ActionFitPackageBulkPublishExecutionResult
                {
                    Success = false,
                    AllRepositoriesPublished = true,
                    CatalogAppended = false,
                    RetryCatalogAppendAvailable = true,
                    Code = cancellationToken.IsCancellationRequested ? "CATALOG_APPEND_CANCELED" : "CATALOG_APPEND_FAILED",
                    Message = catalogOutcome.Message,
                    PlanId = current.PlanId,
                    Packages = packageResults,
                    RetryCatalogPackageIds = successfulCatalogItems.Select(item => item.PackageId).ToArray(),
                    RetryCatalogItems = successfulCatalogItems,
                }, stopwatch);
            }

            string[] warnings = Array.Empty<string>();
            ReportProgress(onProgress, "catalog-refresh", "Refreshing catalog after publish...", 0, 1, 0.96f);
            if (!ActionFitPackageCatalogUpdater.UpdateCatalog(settings, out string refreshMessage, false))
                warnings = new[] { $"Bulk publish succeeded, but catalog refresh failed: {refreshMessage}" };

            ReportProgress(onProgress, "complete", "Bulk publish complete.", publishRequests.Count, publishRequests.Count, 1f);
            return LogExecutionResult(new ActionFitPackageBulkPublishExecutionResult
            {
                Success = true,
                AllRepositoriesPublished = true,
                CatalogAppended = true,
                Code = "PUBLISHED",
                Message = $"{publishRequests.Count} package repository(s) were published and registered in the catalog.",
                PlanId = current.PlanId,
                Packages = packageResults,
                Warnings = warnings,
            }, stopwatch);
        }
        catch (OperationCanceledException)
        {
            return LogExecutionResult(
                ExecutionFailure(request, "CANCELED", "Bulk publish execution was canceled before repository changes completed.", current),
                stopwatch);
        }
        catch (Exception ex)
        {
            return LogExecutionResult(ExecutionFailure(request, "EXECUTION_FAILED", ex.Message, current), stopwatch);
        }
    }

    public static string PrepareAllChangedJson(string requestJson)
    {
        try
        {
            ActionFitPackageBulkPublishPrepareRequest request = string.IsNullOrWhiteSpace(requestJson)
                ? new ActionFitPackageBulkPublishPrepareRequest()
                : JsonUtility.FromJson<ActionFitPackageBulkPublishPrepareRequest>(requestJson);
            return JsonUtility.ToJson(PrepareAllChanged(request), true);
        }
        catch (Exception ex)
        {
            return JsonUtility.ToJson(Fail(new ActionFitPackageBulkPublishPlan(), "INVALID_REQUEST_JSON", ex.Message), true);
        }
    }

    public static string ExecuteAllJson(string requestJson)
    {
        try
        {
            return JsonUtility.ToJson(ExecuteAll(JsonUtility.FromJson<ActionFitPackageBulkPublishExecuteRequest>(requestJson ?? "")), true);
        }
        catch (Exception ex)
        {
            return JsonUtility.ToJson(ExecutionFailure(null, "INVALID_REQUEST_JSON", ex.Message), true);
        }
    }

    internal static string ComputePlanId(IEnumerable<ActionFitPackagePublishPlan> packages)
    {
        string canonical = string.Join("\n", packages
            .OrderBy(item => item.PackageId, StringComparer.Ordinal)
            .Select(item => $"{item.PackageId}|{item.Version}|{item.PlanId}"));
        using SHA256 sha = SHA256.Create();
        return BitConverter.ToString(sha.ComputeHash(Encoding.UTF8.GetBytes(canonical))).Replace("-", "").Substring(0, 20);
    }

    private static string[] DiscoverChangedPackageIds()
    {
        var result = new List<string>();
        if (!Directory.Exists(ActionFitPackagePaths.PackagesRoot)) return result.ToArray();

        foreach (string packagePath in Directory.EnumerateDirectories(ActionFitPackagePaths.PackagesRoot, "com.actionfit.*", SearchOption.TopDirectoryOnly))
        {
            if (!ActionFitPackageFileUtility.PhysicalDirectoryExists(packagePath)) continue;
            string packageJson = Path.Combine(packagePath, "package.json");
            if (!File.Exists(packageJson)) continue;

            ActionFitPackageManifest manifest;
            try { manifest = ActionFitPackageManifest.Read(packageJson); }
            catch { continue; }
            if (string.IsNullOrWhiteSpace(manifest.Name) || string.IsNullOrWhiteSpace(manifest.Version)) continue;

            ActionFitPackageInspectionResult inspection = ActionFitPackageWorkflowApi.Inspect(new ActionFitPackageInspectionRequest
            {
                PackageId = manifest.Name,
                RefreshCatalog = false,
            });
            if (!inspection.Success || !inspection.Embedded || inspection.CatalogContainsInstalledVersion) continue;
            if (inspection.CatalogPackageFound &&
                ActionFitPackageVersionComparer.Instance.Compare(inspection.InstalledVersion, inspection.LatestVersion) <= 0) continue;
            result.Add(manifest.Name);
        }

        return result.Distinct(StringComparer.Ordinal).OrderBy(value => value, StringComparer.Ordinal).ToArray();
    }

    private static RemotePreflightResult[] CheckRemoteStatesParallel(
        IReadOnlyList<ActionFitPackagePublisher.PublishRequest> requests,
        Action<ActionFitPackageBulkPublishProgress> onProgress,
        CancellationToken cancellationToken)
    {
        var results = new RemotePreflightResult[requests.Count];
        if (requests.Count == 0) return results;

        int nextIndex = -1;
        int completed = 0;
        int workerCount = GetWorkerCount(requests.Count);
        Task[] workers = Enumerable.Range(0, workerCount).Select(_ => Task.Run(() =>
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                int index = Interlocked.Increment(ref nextIndex);
                if (index >= requests.Count) break;

                bool success = ActionFitPackagePublisher.TryGetRemoteState(
                    requests[index],
                    out ActionFitPackagePublisher.RemoteState state,
                    out string message);
                results[index] = new RemotePreflightResult(success, state, message);
                Interlocked.Increment(ref completed);
            }
        })).ToArray();

        Task allWorkers = Task.WhenAll(workers);
        while (!allWorkers.Wait(100))
        {
            ReportProgress(
                onProgress,
                "remote-preflight",
                $"Checking GitHub state... {completed}/{requests.Count}",
                completed,
                requests.Count,
                0.35f + 0.15f * completed / requests.Count);
        }
        cancellationToken.ThrowIfCancellationRequested();
        ReportProgress(
            onProgress,
            "remote-preflight",
            $"GitHub state checked: {requests.Count}/{requests.Count}",
            requests.Count,
            requests.Count,
            0.5f);
        return results;
    }

    private static ActionFitPackagePublisher.PublishResult[] PublishRepositoriesParallel(
        IReadOnlyList<ActionFitPackagePublisher.PublishRequest> requests,
        Action<ActionFitPackageBulkPublishProgress> onProgress,
        CancellationToken cancellationToken)
    {
        var results = new ActionFitPackagePublisher.PublishResult[requests.Count];
        if (requests.Count == 0) return results;

        int nextIndex = -1;
        int completed = 0;
        int workerCount = GetWorkerCount(requests.Count);
        Task[] workers = Enumerable.Range(0, workerCount).Select(_ => Task.Run(() =>
        {
            while (true)
            {
                if (cancellationToken.IsCancellationRequested) break;
                int index = Interlocked.Increment(ref nextIndex);
                if (index >= requests.Count) break;
                try
                {
                    results[index] = ActionFitPackagePublisher.PublishRepository(requests[index]);
                }
                catch (Exception ex)
                {
                    results[index] = ActionFitPackagePublisher.PublishResult.Failed(requests[index], ex.Message);
                }
                finally
                {
                    Interlocked.Increment(ref completed);
                }
            }
        })).ToArray();
        Task allWorkers = Task.WhenAll(workers);
        while (!allWorkers.Wait(100))
        {
            ReportProgress(
                onProgress,
                "repository-publish",
                $"Publishing repositories... {completed}/{requests.Count}",
                completed,
                requests.Count,
                0.55f + 0.25f * completed / requests.Count);
        }

        for (int index = 0; index < results.Length; index++)
        {
            results[index] ??= ActionFitPackagePublisher.PublishResult.Failed(
                requests[index],
                "Canceled before repository publish started.");
        }
        ReportProgress(
            onProgress,
            "repository-publish",
            cancellationToken.IsCancellationRequested
                ? $"Repository publishing canceled: {completed}/{requests.Count} completed"
                : $"Repository publishing complete: {requests.Count}/{requests.Count}",
            completed,
            requests.Count,
            0.8f);
        return results;
    }

    private static bool TryAppendCatalogWithFallback(
        IReadOnlyList<ActionFitPackagePublisher.CatalogAppendItem> items,
        CancellationToken cancellationToken,
        out string message)
    {
        if (ActionFitPackagePublisher.TryAppendCatalogBatch(items, cancellationToken, out message)) return true;
        string batchMessage = message;
        if (cancellationToken.IsCancellationRequested)
        {
            message = $"Catalog batch append was canceled. {batchMessage}";
            return false;
        }
        if (!ShouldAttemptSerialFallback(batchMessage))
        {
            message =
                $"Catalog batch append failed: {batchMessage} " +
                "Serial fallback was skipped because the batch request timed out or was canceled; retry the retained catalog rows after checking the Web App.";
            return false;
        }

        Debug.LogWarning(
            $"[ActionFitPackageManager] Catalog batch action was unavailable or failed; starting serial fallback.\n{batchMessage}");
        bool success = ActionFitPackagePublisher.TryAppendCatalogSerial(items, cancellationToken, out string serialMessage);
        message = success
            ? $"Catalog batch append failed, but serial fallback succeeded. Batch: {batchMessage} Serial: {serialMessage}"
            : $"Catalog batch append failed: {batchMessage} Serial fallback failed: {serialMessage}";
        return success;
    }

    private static CatalogAppendOutcome AppendCatalogWithProgress(
        IReadOnlyList<ActionFitPackagePublisher.CatalogAppendItem> items,
        Action<ActionFitPackageBulkPublishProgress> onProgress,
        CancellationToken cancellationToken)
    {
        ReportProgress(onProgress, "catalog-batch", "Appending catalog rows by batch request...", 0, items.Count, 0.82f);
        Task<CatalogAppendOutcome> task = Task.Run(() =>
        {
            bool success = TryAppendCatalogWithFallback(items, cancellationToken, out string message);
            return new CatalogAppendOutcome(success, message);
        });

        while (!task.Wait(100))
        {
            string stage = cancellationToken.IsCancellationRequested ? "catalog-cancel" : "catalog-batch";
            string message = cancellationToken.IsCancellationRequested
                ? "Canceling catalog request..."
                : "Waiting for catalog batch/fallback response...";
            ReportProgress(onProgress, stage, message, 0, items.Count, 0.9f);
        }

        CatalogAppendOutcome outcome = task.GetAwaiter().GetResult();
        ReportProgress(
            onProgress,
            outcome.Success ? "catalog-complete" : "catalog-failed",
            outcome.Message,
            outcome.Success ? items.Count : 0,
            items.Count,
            0.95f);
        return outcome;
    }

    private static ActionFitPackageBulkPublishPackageResult ToPackageResult(ActionFitPackagePublisher.PublishResult result)
    {
        return new ActionFitPackageBulkPublishPackageResult
        {
            PackageId = result.Request?.PackageId ?? "",
            Version = result.Request?.Version ?? "",
            RepositoryPublished = result.Success,
            RepositoryUrl = result.Request == null ? "" : $"https://github.com/{result.Request.GitHubOrganization}/{result.Request.RepoName}.git",
            CatalogId = result.Request?.CatalogId ?? "",
            Message = result.Message,
        };
    }

    private static string[] NormalizePackageIds(IEnumerable<string> packageIds)
    {
        return (packageIds ?? Array.Empty<string>())
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Select(value => value.Trim())
            .Distinct(StringComparer.Ordinal)
            .OrderBy(value => value, StringComparer.Ordinal)
            .ToArray();
    }

    internal static int GetWorkerCount(int requestCount)
    {
        return Math.Min(ActionFitPackagePublisher.DefaultMaxParallelPublishes, Math.Max(0, requestCount));
    }

    internal static bool ShouldAttemptSerialFallback(string batchMessage)
    {
        string value = batchMessage ?? "";
        return value.IndexOf("timed out", StringComparison.OrdinalIgnoreCase) < 0 &&
               value.IndexOf("canceled", StringComparison.OrdinalIgnoreCase) < 0 &&
               value.IndexOf("cancelled", StringComparison.OrdinalIgnoreCase) < 0;
    }

    internal static bool ApprovedPlanMatches(ActionFitPackageBulkPublishExecuteRequest request)
    {
        ActionFitPackageBulkPublishPlan plan = request?.ApprovedPlan;
        if (plan == null || !plan.Success || !plan.ReadyToPublish || plan.Packages == null)
            return false;
        if (!string.Equals(plan.PlanId, request.ExpectedPlanId, StringComparison.Ordinal) ||
            !string.Equals(plan.RequiredApprovalText, request.ApprovalText, StringComparison.Ordinal))
            return false;

        string[] requestIds = NormalizePackageIds(request.PackageIds);
        string[] planIds = NormalizePackageIds(plan.PackageIds);
        string[] packageIds = NormalizePackageIds(plan.Packages.Select(item => item?.PackageId));
        if (!requestIds.SequenceEqual(planIds, StringComparer.Ordinal) ||
            !planIds.SequenceEqual(packageIds, StringComparer.Ordinal))
        {
            return false;
        }

        if (plan.Packages.Any(item =>
                item == null ||
                !item.Success ||
                !item.ReadyToPublish ||
                string.IsNullOrWhiteSpace(item.PlanId)))
        {
            return false;
        }

        return string.Equals(ComputePlanId(plan.Packages), plan.PlanId, StringComparison.Ordinal);
    }

    internal static bool MigrationApprovalsMatch(
        ActionFitPackageBulkPublishPlan plan,
        IEnumerable<string> approvedPackageIds,
        string approvalText)
    {
        if (plan == null) return false;
        string[] expected = NormalizePackageIds(plan.RepositoryMigrationPackageIds);
        string[] approved = NormalizePackageIds(approvedPackageIds);
        return expected.SequenceEqual(approved, StringComparer.Ordinal) &&
               (expected.Length == 0 ||
                string.Equals(plan.RequiredMigrationApprovalText, approvalText, StringComparison.Ordinal));
    }

    private static void ReportProgress(
        Action<ActionFitPackageBulkPublishProgress> onProgress,
        string stage,
        string message,
        int completed,
        int total,
        float fraction)
    {
        onProgress?.Invoke(new ActionFitPackageBulkPublishProgress
        {
            Stage = stage,
            Message = message,
            Completed = completed,
            Total = total,
            Fraction = Math.Max(0f, Math.Min(1f, fraction)),
        });
    }

    private static ActionFitPackageBulkPublishPlan LogPreparedPlan(
        ActionFitPackageBulkPublishPlan plan,
        Stopwatch stopwatch)
    {
        string outcome = plan != null && plan.Success ? "complete" : "failed";
        Debug.Log(
            $"[ActionFitPackageManager] Bulk publish preflight {outcome} " +
            $"({stopwatch.ElapsedMilliseconds} ms, code={plan?.Code ?? "UNKNOWN"})");
        return plan;
    }

    private static ActionFitPackageBulkPublishExecutionResult LogExecutionResult(
        ActionFitPackageBulkPublishExecutionResult result,
        Stopwatch stopwatch)
    {
        string outcome = result != null && result.Success ? "complete" : "failed";
        Debug.Log(
            $"[ActionFitPackageManager] Bulk publish execution {outcome} " +
            $"({stopwatch.ElapsedMilliseconds} ms, code={result?.Code ?? "UNKNOWN"})");
        return result;
    }

    private static ActionFitPackagePublishPlan FailPackage(
        ActionFitPackagePublishPlan plan,
        string code,
        string message)
    {
        plan ??= new ActionFitPackagePublishPlan();
        plan.Success = false;
        plan.ReadyToPublish = false;
        plan.Code = code;
        plan.Message = message;
        return plan;
    }

    private static ActionFitPackageBulkPublishPlan Fail(ActionFitPackageBulkPublishPlan plan, string code, string message)
    {
        plan ??= new ActionFitPackageBulkPublishPlan();
        plan.Success = false;
        plan.ReadyToPublish = false;
        plan.Code = code;
        plan.Message = message;
        return plan;
    }

    private static ActionFitPackageBulkPublishExecutionResult ExecutionFailure(
        ActionFitPackageBulkPublishExecuteRequest request,
        string code,
        string message,
        ActionFitPackageBulkPublishPlan plan = null)
    {
        return new ActionFitPackageBulkPublishExecutionResult
        {
            Success = false,
            Code = code,
            Message = message,
            PlanId = plan?.PlanId ?? request?.ExpectedPlanId ?? "",
        };
    }

    private sealed class RemotePreflightResult
    {
        public RemotePreflightResult(
            bool success,
            ActionFitPackagePublisher.RemoteState state,
            string message)
        {
            Success = success;
            State = state;
            Message = message;
        }

        public bool Success { get; }
        public ActionFitPackagePublisher.RemoteState State { get; }
        public string Message { get; }
    }

    private sealed class CatalogAppendOutcome
    {
        public CatalogAppendOutcome(bool success, string message)
        {
            Success = success;
            Message = message;
        }

        public bool Success { get; }
        public string Message { get; }
    }
}

public static class ActionFitPackageBulkPublishCli
{
    public static void PrepareAllChanged() => Run(false);
    public static void ExecuteAll() => Run(true);

    private static void Run(bool execute)
    {
        string requestPath = GetArgument("-actionFitBulkPublishRequest");
        string resultPath = GetArgument("-actionFitBulkPublishResult");
        string resultJson;
        try
        {
            if (string.IsNullOrWhiteSpace(requestPath)) throw new InvalidOperationException("-actionFitBulkPublishRequest is required.");
            if (string.IsNullOrWhiteSpace(resultPath)) throw new InvalidOperationException("-actionFitBulkPublishResult is required.");
            string requestJson = File.ReadAllText(Path.GetFullPath(requestPath));
            resultJson = execute
                ? ActionFitPackageBulkPublishApi.ExecuteAllJson(requestJson)
                : ActionFitPackageBulkPublishApi.PrepareAllChangedJson(requestJson);
        }
        catch (Exception ex)
        {
            resultJson = JsonUtility.ToJson(new ActionFitPackageBulkPublishExecutionResult
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
        Debug.Log($"[ActionFitPackageManager] Bulk publish CLI {(execute ? "execute" : "prepare")} completed. Result: {resultPath}");
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
