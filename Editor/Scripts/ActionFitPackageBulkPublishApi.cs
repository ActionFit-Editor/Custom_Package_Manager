#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using UnityEngine;

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
    {
        var plan = new ActionFitPackageBulkPublishPlan();
        try
        {
            request ??= new ActionFitPackageBulkPublishPrepareRequest();
            if (!request.CheckRemoteState)
                return Fail(plan, "REMOTE_CHECK_REQUIRED", "GitHub remote state must be checked before a bulk publish plan can be approved.");

            if (request.RefreshCatalog)
            {
                ActionFitPackageCatalogSettings_SO settings = ActionFitPackageCatalogSettingsProvider.FindOrCreate();
                if (!ActionFitPackageCatalogUpdater.UpdateCatalog(settings, out string refreshError, false))
                    return Fail(plan, "CATALOG_REFRESH_FAILED", $"Catalog refresh failed; bulk publishing was not prepared: {refreshError}");
            }

            string[] packageIds = NormalizePackageIds(request.PackageIds);
            if (packageIds.Length == 0)
                packageIds = DiscoverChangedPackageIds();
            if (packageIds.Length == 0)
                return Fail(plan, "NO_CHANGED_PACKAGES", "No changed embedded ActionFit packages are ready to publish.");

            var packages = new List<ActionFitPackagePublishPlan>();
            foreach (string packageId in packageIds)
            {
                ActionFitPackagePublishPlan packagePlan = ActionFitPackagePublishApi.Prepare(new ActionFitPackagePublishPrepareRequest
                {
                    PackageId = packageId,
                    RefreshCatalog = false,
                    CheckRemoteState = true,
                });
                packages.Add(packagePlan);
            }

            plan.PackageIds = packageIds;
            plan.Packages = packages.ToArray();
            ActionFitPackagePublishPlan[] failed = packages.Where(item => !item.Success || !item.ReadyToPublish).ToArray();
            if (failed.Length > 0)
            {
                string details = string.Join("\n", failed.Select(item => $"- {item.PackageId}: {item.Code} - {item.Message}"));
                return Fail(plan, "PACKAGE_PREFLIGHT_FAILED", $"Bulk publish preflight failed. No external state was changed.\n{details}");
            }

            plan.RepositoryCreationPackageIds = packages
                .Where(item => item.WillCreateRepository)
                .Select(item => item.PackageId)
                .OrderBy(value => value, StringComparer.Ordinal)
                .ToArray();
            plan.PlanId = ComputePlanId(packages);
            plan.RequiredApprovalText = $"PUBLISH ALL {packages.Count} PACKAGES PLAN {plan.PlanId}";
            plan.Warnings = packages.SelectMany(item => item.Warnings ?? Array.Empty<string>()).Distinct().ToArray();
            plan.Success = true;
            plan.ReadyToPublish = true;
            plan.Code = "READY_TO_PUBLISH";
            plan.Message = $"Bulk publish plan is ready for {packages.Count} package(s). No external state was changed.";
            return plan;
        }
        catch (Exception ex)
        {
            return Fail(plan, "PREPARE_FAILED", ex.Message);
        }
    }

    public static ActionFitPackageBulkPublishExecutionResult ExecuteAll(ActionFitPackageBulkPublishExecuteRequest request)
    {
        if (request == null)
            return ExecutionFailure(null, "INVALID_REQUEST", "Bulk publish execute request is missing.");
        if (string.IsNullOrWhiteSpace(request.ExpectedPlanId) || string.IsNullOrWhiteSpace(request.ApprovalText))
            return ExecutionFailure(request, "APPROVAL_REQUIRED", "ExpectedPlanId and exact ApprovalText from PrepareAllChanged are required.");

        ActionFitPackageBulkPublishPlan current = PrepareAllChanged(new ActionFitPackageBulkPublishPrepareRequest
        {
            PackageIds = request.PackageIds,
            RefreshCatalog = true,
            CheckRemoteState = true,
        });
        if (!current.Success || !current.ReadyToPublish)
            return ExecutionFailure(request, "PREFLIGHT_FAILED", current.Message, current);
        if (!string.Equals(current.PlanId, request.ExpectedPlanId, StringComparison.Ordinal))
            return ExecutionFailure(request, "PLAN_CHANGED", $"Bulk publish state changed after approval. Expected plan {request.ExpectedPlanId}, current plan {current.PlanId}. Prepare and approve again.", current);
        if (!string.Equals(current.RequiredApprovalText, request.ApprovalText, StringComparison.Ordinal))
            return ExecutionFailure(request, "APPROVAL_TEXT_MISMATCH", "ApprovalText does not exactly match the prepared bulk publish plan.", current);

        string[] approvedCreates = NormalizePackageIds(request.ApprovedRepositoryCreationPackageIds);
        if (!new HashSet<string>(current.RepositoryCreationPackageIds, StringComparer.Ordinal).SetEquals(approvedCreates))
        {
            return ExecutionFailure(
                request,
                "REPOSITORY_CREATION_APPROVAL_REQUIRED",
                "ApprovedRepositoryCreationPackageIds must exactly match the repositories marked for creation by PrepareAllChanged.",
                current);
        }

        try
        {
            ActionFitPackageCatalogSettings_SO settings = ActionFitPackageCatalogSettingsProvider.FindOrCreate();
            var publishRequests = new List<ActionFitPackagePublisher.PublishRequest>();
            foreach (ActionFitPackagePublishPlan packagePlan in current.Packages)
            {
                ActionFitPackageInfo_SO info = ActionFitPackagePublishApi.FindPackageInfo(packagePlan.PackageId);
                if (!ActionFitPackagePublisher.TryCreatePublishRequest(
                        settings,
                        info,
                        info.RepositoryVisibility,
                        out ActionFitPackagePublisher.PublishRequest publishRequest,
                        out string requestError))
                {
                    return ExecutionFailure(request, "PUBLISH_REQUEST_INVALID", $"{packagePlan.PackageId}: {requestError}", current);
                }
                publishRequests.Add(publishRequest);
            }

            ActionFitPackagePublisher.PublishResult[] repositoryResults = PublishRepositoriesParallel(publishRequests);
            ActionFitPackagePublisher.PublishResult[] succeeded = repositoryResults.Where(item => item.Success).ToArray();
            ActionFitPackagePublisher.PublishResult[] failed = repositoryResults.Where(item => !item.Success).ToArray();
            ActionFitPackageBulkPublishPackageResult[] packageResults = repositoryResults.Select(ToPackageResult).ToArray();
            ActionFitPackagePublisher.CatalogAppendItem[] successfulCatalogItems = succeeded.Select(item => item.Request.CatalogItem).ToArray();

            if (failed.Length > 0)
            {
                string details = string.Join("\n", failed.Select(item => $"- {item.Request?.PackageId ?? "<unknown>"}: {item.Message}"));
                return new ActionFitPackageBulkPublishExecutionResult
                {
                    Success = false,
                    AllRepositoriesPublished = false,
                    CatalogAppended = false,
                    RetryCatalogAppendAvailable = successfulCatalogItems.Length > 0,
                    Code = "REPOSITORY_PUBLISH_FAILED",
                    Message = $"One or more repository publishes failed. Catalog rows were not appended.\n{details}",
                    PlanId = current.PlanId,
                    Packages = packageResults,
                    RetryCatalogPackageIds = successfulCatalogItems.Select(item => item.PackageId).ToArray(),
                    RetryCatalogItems = successfulCatalogItems,
                };
            }

            bool catalogAppended = TryAppendCatalogWithFallback(successfulCatalogItems, out string catalogMessage);
            if (!catalogAppended)
            {
                return new ActionFitPackageBulkPublishExecutionResult
                {
                    Success = false,
                    AllRepositoriesPublished = true,
                    CatalogAppended = false,
                    RetryCatalogAppendAvailable = true,
                    Code = "CATALOG_APPEND_FAILED",
                    Message = catalogMessage,
                    PlanId = current.PlanId,
                    Packages = packageResults,
                    RetryCatalogPackageIds = successfulCatalogItems.Select(item => item.PackageId).ToArray(),
                    RetryCatalogItems = successfulCatalogItems,
                };
            }

            string[] warnings = Array.Empty<string>();
            if (!ActionFitPackageCatalogUpdater.UpdateCatalog(settings, out string refreshMessage, false))
                warnings = new[] { $"Bulk publish succeeded, but catalog refresh failed: {refreshMessage}" };

            return new ActionFitPackageBulkPublishExecutionResult
            {
                Success = true,
                AllRepositoriesPublished = true,
                CatalogAppended = true,
                Code = "PUBLISHED",
                Message = $"{publishRequests.Count} package repository(s) were published and registered in the catalog.",
                PlanId = current.PlanId,
                Packages = packageResults,
                Warnings = warnings,
            };
        }
        catch (Exception ex)
        {
            return ExecutionFailure(request, "EXECUTION_FAILED", ex.Message, current);
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

    private static ActionFitPackagePublisher.PublishResult[] PublishRepositoriesParallel(IReadOnlyList<ActionFitPackagePublisher.PublishRequest> requests)
    {
        var results = new ActionFitPackagePublisher.PublishResult[requests.Count];
        if (requests.Count == 0) return results;

        int nextIndex = -1;
        int workerCount = Math.Min(ActionFitPackagePublisher.DefaultMaxParallelPublishes, requests.Count);
        Task[] workers = Enumerable.Range(0, workerCount).Select(_ => Task.Run(() =>
        {
            while (true)
            {
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
            }
        })).ToArray();
        Task.WhenAll(workers).GetAwaiter().GetResult();
        return results;
    }

    private static bool TryAppendCatalogWithFallback(
        IReadOnlyList<ActionFitPackagePublisher.CatalogAppendItem> items,
        out string message)
    {
        if (ActionFitPackagePublisher.TryAppendCatalogBatch(items, out message)) return true;
        string batchMessage = message;
        bool success = ActionFitPackagePublisher.TryAppendCatalogSerial(items, out string serialMessage);
        message = success
            ? $"Catalog batch append failed, but serial fallback succeeded. Batch: {batchMessage} Serial: {serialMessage}"
            : $"Catalog batch append failed: {batchMessage} Serial fallback failed: {serialMessage}";
        return success;
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
