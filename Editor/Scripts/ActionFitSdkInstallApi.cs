#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using UnityEditor;
using UnityEditor.PackageManager;
using UnityEngine;

[Serializable]
public sealed class ActionFitSdkExecutionResult
{
    public bool Success;
    public bool RolledBack;
    public bool RecoveryRequired;
    public string Code = "";
    public string Message = "";
    public string PlanId = "";
    public string JournalPath = "";
}

[Serializable]
public sealed class ActionFitSdkRecoveryResult
{
    public bool Success;
    public bool RolledBack;
    public bool RecoveryRequired;
    public string Code = "";
    public string Message = "";
    public string JournalPath = "";
}

/// <summary>Public read, plan, and explicit execution API for external SDK install profiles.</summary>
public static class ActionFitSdkInstallApi
{
    /// <summary>Reads one validated SDK profile without changing project state.</summary>
    public static ActionFitSdkInstallProfile ReadProfile(string path)
    {
        return ActionFitSdkInstallProfile.Read(path);
    }

    /// <summary>Inspects the current project without changing files or package resolution.</summary>
    public static ActionFitSdkInspectionResult Inspect(
        ActionFitSdkInstallProfile profile,
        string[] selectedModuleIds = null,
        ActionFitSdkProjectContext context = null)
    {
        return ActionFitSdkInstallPlanner.Inspect(profile, selectedModuleIds, context);
    }

    /// <summary>Builds a content-bound, read-only plan for explicit review.</summary>
    public static ActionFitSdkInstallPlan Plan(
        ActionFitSdkInstallProfile profile,
        ActionFitSdkPlanRequest request,
        ActionFitSdkProjectContext context = null)
    {
        return ActionFitSdkInstallPlanner.Plan(profile, request, context);
    }

    /// <summary>Resolves installed packages or immutable latest stable fallbacks without changing project state.</summary>
    public static Task<ActionFitSdkResolutionResult> ResolveAsync(
        ActionFitSdkInstallProfile profile,
        string[] selectedModuleIds = null,
        ActionFitSdkProjectContext context = null,
        CancellationToken cancellationToken = default)
    {
        return ActionFitSdkLatestResolver.ResolveAsync(
            profile,
            selectedModuleIds,
            context ?? ActionFitSdkProjectContext.ForCurrentProject(),
            cancellationToken);
    }

    /// <summary>Resolves a schema-v2 policy and produces one immutable, content-bound plan for review.</summary>
    public static async Task<ActionFitSdkInstallPlan> PreparePlanAsync(
        ActionFitSdkInstallProfile profile,
        ActionFitSdkPlanRequest request,
        ActionFitSdkProjectContext context = null,
        CancellationToken cancellationToken = default)
    {
        context ??= ActionFitSdkProjectContext.ForCurrentProject();
        request ??= new ActionFitSdkPlanRequest();
        if (profile == null || !profile.RequiresAsyncResolution())
            return ActionFitSdkInstallPlanner.Plan(profile, request, context);

        ActionFitSdkResolutionResult resolution = await ActionFitSdkLatestResolver.ResolveAsync(
            profile,
            request.SelectedModuleIds,
            context,
            cancellationToken);
        if (!resolution.Success)
        {
            return new ActionFitSdkInstallPlan
            {
                Success = false,
                Code = resolution.Code,
                Message = resolution.Message,
                ProfileId = profile.ProfileId,
                ProfileVersion = profile.ProfileVersion,
                Operation = request.Operation,
                Findings = resolution.Findings,
            };
        }

        return ActionFitSdkInstallPlanner.PlanResolved(
            resolution.ResolvedProfile,
            request,
            context,
            resolution.Snapshot);
    }

    /// <summary>Executes a reviewed Apply plan and rejects every other operation.</summary>
    public static Task<ActionFitSdkExecutionResult> ApplyAsync(
        ActionFitSdkInstallPlan plan,
        string approvedPlanId,
        CancellationToken cancellationToken = default)
    {
        return ExecuteExpectedAsync(plan, approvedPlanId, ActionFitSdkInstallOperation.Apply, cancellationToken);
    }

    /// <summary>Executes a reviewed Repair plan and rejects every other operation.</summary>
    public static Task<ActionFitSdkExecutionResult> RepairAsync(
        ActionFitSdkInstallPlan plan,
        string approvedPlanId,
        CancellationToken cancellationToken = default)
    {
        return ExecuteExpectedAsync(plan, approvedPlanId, ActionFitSdkInstallOperation.Repair, cancellationToken);
    }

    /// <summary>Executes a reviewed Update plan and rejects every other operation.</summary>
    public static Task<ActionFitSdkExecutionResult> UpdateAsync(
        ActionFitSdkInstallPlan plan,
        string approvedPlanId,
        CancellationToken cancellationToken = default)
    {
        return ExecuteExpectedAsync(plan, approvedPlanId, ActionFitSdkInstallOperation.Update, cancellationToken);
    }

    /// <summary>Executes a reviewed Remove plan and rejects every other operation.</summary>
    public static Task<ActionFitSdkExecutionResult> RemoveAsync(
        ActionFitSdkInstallPlan plan,
        string approvedPlanId,
        CancellationToken cancellationToken = default)
    {
        return ExecuteExpectedAsync(plan, approvedPlanId, ActionFitSdkInstallOperation.Remove, cancellationToken);
    }

    /// <summary>Rolls back pending SDK transactions only when called explicitly.</summary>
    public static ActionFitSdkRecoveryResult[] RecoverPendingTransactions(ActionFitSdkProjectContext context = null)
    {
        return ActionFitSdkInstallTransaction.RecoverPending(context ?? ActionFitSdkProjectContext.ForCurrentProject());
    }

    /// <summary>Lists pending transaction journals without changing project state.</summary>
    public static string[] InspectPendingTransactions(ActionFitSdkProjectContext context = null)
    {
        return ActionFitSdkInstallTransaction.FindPendingJournals(context ?? ActionFitSdkProjectContext.ForCurrentProject());
    }

    private static Task<ActionFitSdkExecutionResult> ExecuteExpectedAsync(
        ActionFitSdkInstallPlan plan,
        string approvedPlanId,
        ActionFitSdkInstallOperation expected,
        CancellationToken cancellationToken)
    {
        if (plan == null)
            return Task.FromResult(Failure("PLAN_MISSING", "SDK install plan is required.", approvedPlanId));
        if (plan.Operation != expected)
            return Task.FromResult(Failure("OPERATION_MISMATCH", $"Expected {expected} plan, found {plan.Operation}.", approvedPlanId));
        return ActionFitSdkInstallTransaction.ExecuteAsync(plan, approvedPlanId, cancellationToken);
    }

    private static ActionFitSdkExecutionResult Failure(string code, string message, string planId)
    {
        return new ActionFitSdkExecutionResult
        {
            Success = false,
            Code = code,
            Message = message,
            PlanId = planId ?? "",
        };
    }
}

internal static class ActionFitSdkInstallTransaction
{
    public static async Task<ActionFitSdkExecutionResult> ExecuteAsync(
        ActionFitSdkInstallPlan plan,
        string approvedPlanId,
        CancellationToken cancellationToken)
    {
        ActionFitSdkTransactionJournal journal = null;
        string journalPath = "";
        string preparedTransactionDirectory = "";
        try
        {
            ValidatePlan(plan, approvedPlanId);
            cancellationToken.ThrowIfCancellationRequested();
            await ActionFitSdkLatestResolver.RevalidateAsync(plan.Profile, plan.ResolutionSnapshot, cancellationToken);
            RevalidateProjectSnapshots(plan);
            if (plan.Changes.Length == 0)
            {
                return new ActionFitSdkExecutionResult
                {
                    Success = true,
                    Code = "NO_CHANGES",
                    Message = $"{plan.ProfileId} already matched the reviewed project state.",
                    PlanId = plan.PlanId,
                };
            }

            string transactionId = Guid.NewGuid().ToString("N");
            string transactionDirectory = Path.Combine(plan.Context.TransactionRoot, transactionId);
            preparedTransactionDirectory = transactionDirectory;
            string downloadDirectory = Path.Combine(transactionDirectory, "downloads");
            string backupDirectory = Path.Combine(transactionDirectory, "backups");
            Directory.CreateDirectory(downloadDirectory);
            Directory.CreateDirectory(backupDirectory);

            ActionFitSdkArtifactJournalEntry[] artifactEntries = await PrepareArtifactsAsync(
                plan,
                downloadDirectory,
                backupDirectory,
                cancellationToken);
            ActionFitSdkAssetJournalEntry[] assetEntries = await PrepareAssetsAsync(
                plan,
                downloadDirectory,
                backupDirectory,
                cancellationToken);
            RevalidateProjectSnapshots(plan);

            journal = new ActionFitSdkTransactionJournal
            {
                SchemaVersion = 1,
                TransactionId = transactionId,
                PlanId = plan.PlanId,
                ProfileId = plan.ProfileId,
                ProjectRoot = plan.Context.ProjectRoot,
                ManifestPath = plan.Context.ManifestPath,
                OwnershipPath = plan.Context.OwnershipPath,
                OwnershipOriginallyExisted = plan.OriginalOwnershipExisted,
                OriginalManifest = plan.OriginalManifest,
                UpdatedManifest = plan.UpdatedManifest,
                OriginalOwnership = plan.OriginalOwnership,
                UpdatedOwnership = plan.UpdatedOwnership,
                ArtifactEntries = artifactEntries,
                AssetEntries = assetEntries,
                Phase = ActionFitSdkTransactionPhase.Prepared.ToString(),
            };
            journalPath = Path.Combine(plan.Context.TransactionRoot, transactionId + ".json");
            SaveJournal(journalPath, journal);

            RevalidateProjectSnapshots(plan);
            ApplyArtifactChanges(journalPath, journal);
            journal.Phase = ActionFitSdkTransactionPhase.ArtifactsPrepared.ToString();
            SaveJournal(journalPath, journal);

            ApplyAssetChanges(journalPath, journal);
            journal.Phase = ActionFitSdkTransactionPhase.AssetsCommitted.ToString();
            SaveJournal(journalPath, journal);

            ActionFitPackageManifestUtility.WriteAtomic(plan.Context.ManifestPath, plan.UpdatedManifest);
            journal.Phase = ActionFitSdkTransactionPhase.ManifestCommitted.ToString();
            SaveJournal(journalPath, journal);

            ActionFitPackageManifestUtility.WriteAtomic(plan.Context.OwnershipPath, plan.UpdatedOwnership, false);
            journal.Phase = ActionFitSdkTransactionPhase.OwnershipCommitted.ToString();
            SaveJournal(journalPath, journal);

            VerifyCommitted(plan, journal);
            journal.Phase = ActionFitSdkTransactionPhase.Completed.ToString();
            SaveJournal(journalPath, journal);
            CleanupCompleted(journalPath, journal);

            string resolveWarning = "";
            if (IsCurrentProject(plan.Context))
            {
                try
                {
                    Client.Resolve();
                }
                catch (Exception ex)
                {
                    resolveWarning = $" Project files were committed, but Unity Package Manager resolve could not be requested: {ex.Message}";
                }
            }
            return new ActionFitSdkExecutionResult
            {
                Success = true,
                Code = !string.IsNullOrEmpty(resolveWarning) ? "COMPLETED_RESOLVE_PENDING" : plan.Changes.Length == 0 ? "NO_CHANGES" : "COMPLETED",
                Message = (plan.Changes.Length == 0
                    ? $"{plan.ProfileId} already matched the reviewed project state."
                    : $"{plan.Operation} completed for {plan.ProfileId}.") + resolveWarning,
                PlanId = plan.PlanId,
                JournalPath = journalPath,
            };
        }
        catch (OperationCanceledException)
        {
            if (journal == null)
            {
                TryDeletePreparedDirectory(preparedTransactionDirectory);
                return Failure("CANCELLED", "SDK install execution was cancelled before project mutation.", approvedPlanId, journalPath);
            }
            return RecoverFailure(journalPath, journal, "SDK install execution was cancelled.", approvedPlanId);
        }
        catch (Exception ex)
        {
            if (journal == null)
            {
                TryDeletePreparedDirectory(preparedTransactionDirectory);
                return Failure("PREPARE_FAILED", ex.Message, approvedPlanId, journalPath);
            }
            return RecoverFailure(journalPath, journal, ex.Message, approvedPlanId);
        }
    }

    public static string[] FindPendingJournals(ActionFitSdkProjectContext context)
    {
        context.Validate();
        if (!Directory.Exists(context.TransactionRoot)) return Array.Empty<string>();
        return Directory.GetFiles(context.TransactionRoot, "*.json", SearchOption.TopDirectoryOnly)
            .OrderBy(path => path, StringComparer.Ordinal)
            .ToArray();
    }

    public static ActionFitSdkRecoveryResult[] RecoverPending(ActionFitSdkProjectContext context)
    {
        var results = new List<ActionFitSdkRecoveryResult>();
        foreach (string journalPath in FindPendingJournals(context))
        {
            try
            {
                ActionFitSdkTransactionJournal journal = JsonUtility.FromJson<ActionFitSdkTransactionJournal>(File.ReadAllText(journalPath));
                if (journal == null || string.IsNullOrWhiteSpace(journal.TransactionId))
                {
                    results.Add(RecoveryRequired("JOURNAL_INVALID", "SDK transaction journal is invalid.", journalPath));
                    continue;
                }
                if (!PathEquals(journal.ProjectRoot, context.ProjectRoot))
                {
                    results.Add(RecoveryRequired("JOURNAL_PROJECT_MISMATCH", "SDK transaction journal does not belong to the selected project context.", journalPath));
                    continue;
                }
                if (string.Equals(journal.Phase, ActionFitSdkTransactionPhase.Completed.ToString(), StringComparison.Ordinal))
                {
                    ValidateJournal(journal);
                    CleanupCompleted(journalPath, journal);
                    results.Add(new ActionFitSdkRecoveryResult
                    {
                        Success = true,
                        RolledBack = false,
                        Code = "COMPLETED_FINALIZED",
                        Message = "Verified completed SDK transaction cleanup was finalized without rollback.",
                        JournalPath = journalPath,
                    });
                    continue;
                }
                results.Add(Rollback(journalPath, journal));
            }
            catch (Exception ex)
            {
                results.Add(RecoveryRequired("JOURNAL_READ_FAILED", ex.Message, journalPath));
            }
        }
        if (results.Any(item => item.Success) && IsCurrentProject(context))
        {
            try
            {
                Client.Resolve();
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[ActionFitSdkInstall] Recovery completed, but Unity Package Manager resolve could not be requested: {ex.Message}");
            }
        }
        return results.ToArray();
    }

    private static void ValidatePlan(ActionFitSdkInstallPlan plan, string approvedPlanId)
    {
        if (plan == null) throw new ArgumentNullException(nameof(plan));
        if (!plan.Success) throw new InvalidOperationException($"SDK install plan is not executable: {plan.Code} {plan.Message}");
        if (string.IsNullOrWhiteSpace(plan.PlanId) || !string.Equals(plan.PlanId, approvedPlanId, StringComparison.Ordinal))
            throw new InvalidOperationException("Approved SDK plan ID does not match the prepared plan.");
        if (string.IsNullOrWhiteSpace(plan.PreparedPlanId) ||
            !string.Equals(plan.PlanId, plan.PreparedPlanId, StringComparison.Ordinal) ||
            !string.Equals(plan.PlanId, ActionFitSdkInstallPlanner.ComputePlanId(plan), StringComparison.Ordinal))
        {
            throw new InvalidOperationException("SDK install plan content changed after review. Prepare a new plan.");
        }
        if (plan.Context == null || plan.Profile == null)
            throw new InvalidOperationException("SDK install plan lost its in-process project or profile snapshot.");
        plan.Context.Validate();

        ActionFitSdkProfileValidationResult validation = ActionFitSdkInstallProfileValidator.Validate(plan.Profile);
        if (!validation.Success) throw new InvalidOperationException(validation.FormatMessage());
        if (!string.Equals(plan.ProfileSnapshot, plan.Profile.ToJson(), StringComparison.Ordinal))
            throw new InvalidOperationException("SDK profile changed after plan preparation.");
        RevalidateProjectSnapshots(plan);
    }

    private static void RevalidateProjectSnapshots(ActionFitSdkInstallPlan plan)
    {
        if (!File.Exists(plan.Context.ManifestPath))
            throw new FileNotFoundException("Packages/manifest.json disappeared after plan preparation.", plan.Context.ManifestPath);
        string manifest = File.ReadAllText(plan.Context.ManifestPath);
        if (!string.Equals(ActionFitSdkInstallPlanner.Hash(manifest), plan.OriginalManifestHash, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Packages/manifest.json changed after plan preparation. Prepare a new plan.");

        ActionFitSdkOwnershipStore.Read(plan.Context, out string ownership);
        if (!string.Equals(ActionFitSdkInstallPlanner.Hash(ownership), plan.OriginalOwnershipHash, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("SDK ownership state changed after plan preparation. Prepare a new plan.");

        if (plan.ResolutionSnapshot != null)
        {
            ActionFitSdkProjectStateSnapshot current = ActionFitSdkProjectStateSnapshot.Read(plan.Context, manifest);
            if (!string.Equals(current.PackagesLockHash, plan.ResolutionSnapshot.PackagesLockHash, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("Packages/packages-lock.json changed after SDK resolution. Prepare a new plan.");
            if (!string.Equals(current.RegisteredPackagesHash, plan.ResolutionSnapshot.RegisteredPackagesHash, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("Unity registered package state changed after SDK resolution. Prepare a new plan.");
        }
    }

    private static async Task<ActionFitSdkArtifactJournalEntry[]> PrepareArtifactsAsync(
        ActionFitSdkInstallPlan plan,
        string downloadDirectory,
        string backupDirectory,
        CancellationToken cancellationToken)
    {
        var entries = new List<ActionFitSdkArtifactJournalEntry>();
        foreach (ActionFitSdkArtifactPlan artifact in plan.ArtifactPlans)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var entry = new ActionFitSdkArtifactJournalEntry
            {
                TargetPath = artifact.TargetPath,
                BackupPath = Path.Combine(backupDirectory, entries.Count.ToString("D4") + ".backup"),
                ExpectedSha256 = artifact.Sha256,
                PackageId = artifact.PackageId,
                PackageVersion = artifact.PackageVersion,
                Remove = artifact.Remove,
            };

            EnsureProjectPath(plan.Context, entry.TargetPath);
            if (artifact.DownloadRequired)
            {
                string extension = artifact.TargetPath.EndsWith(".tar.gz", StringComparison.OrdinalIgnoreCase) ? ".tar.gz" : Path.GetExtension(artifact.TargetPath);
                entry.StagedPath = Path.Combine(downloadDirectory, entries.Count.ToString("D4") + extension);
                await DownloadArtifactAsync(plan.Profile, artifact.Url, entry.StagedPath, cancellationToken);
                VerifyArtifact(entry.StagedPath, artifact.Sha256, artifact.PackageId, artifact.PackageVersion);
            }
            entries.Add(entry);
        }
        return entries.ToArray();
    }

    /// <summary>
    /// Downloads, checksum-verifies, and fully validates every unitypackage source, then stages the
    /// exact bytes for each planned asset. Nothing is written into the project by this method.
    /// </summary>
    private static async Task<ActionFitSdkAssetJournalEntry[]> PrepareAssetsAsync(
        ActionFitSdkInstallPlan plan,
        string downloadDirectory,
        string backupDirectory,
        CancellationToken cancellationToken)
    {
        if (plan.AssetPlans.Length == 0)
            return Array.Empty<ActionFitSdkAssetJournalEntry>();

        string stageDirectory = Path.Combine(downloadDirectory, "assets");
        var archives = new Dictionary<string, Dictionary<string, ActionFitSdkUnityPackageEntry>>(StringComparer.Ordinal);

        foreach (string sourceId in plan.AssetPlans
                     .Where(item => item.WriteContent && !item.Remove)
                     .Select(item => item.SourceId)
                     .Distinct(StringComparer.Ordinal))
        {
            cancellationToken.ThrowIfCancellationRequested();
            ActionFitSdkSourceDefinition source = plan.Profile.Sources.FirstOrDefault(item =>
                item != null && string.Equals(item.Id, sourceId, StringComparison.Ordinal));
            if (source == null)
                throw new InvalidOperationException($"Planned asset source {sourceId} is missing from the profile.");

            string staged = Path.Combine(downloadDirectory, sourceId + ".unitypackage");
            await DownloadArtifactAsync(plan.Profile, source.Url, staged, cancellationToken);

            string actual = ActionFitSdkArtifactVerifier.ComputeSha256(staged);
            if (!string.Equals(actual, source.Sha256, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException($"Unity package checksum mismatch for source {sourceId}.");

            ActionFitSdkUnityPackageReadResult read = ActionFitSdkUnityPackageArchive.Read(staged);
            if (!read.Success)
                throw new InvalidOperationException(read.FormatMessage());

            ActionFitSdkProfileDiagnostic[] drift = ActionFitSdkUnityPackageArchive.CompareWithInventory(read.Entries, source);
            if (drift.Length > 0)
            {
                throw new InvalidOperationException(
                    $"Unity package inventory drift for source {sourceId}:\n" +
                    string.Join("\n", drift.Select(item => $"- {item.Code} ({item.Path}): {item.Message}")));
            }

            var index = new Dictionary<string, ActionFitSdkUnityPackageEntry>(StringComparer.Ordinal);
            foreach (ActionFitSdkUnityPackageEntry entry in read.Entries)
                index[entry.Path] = entry;
            archives[sourceId] = index;
        }

        var entries = new List<ActionFitSdkAssetJournalEntry>();
        foreach (ActionFitSdkAssetPlan asset in plan.AssetPlans)
        {
            cancellationToken.ThrowIfCancellationRequested();
            EnsureProjectPath(plan.Context, asset.TargetPath);

            string slot = entries.Count.ToString("D4");
            var entry = new ActionFitSdkAssetJournalEntry
            {
                ProjectRelativePath = asset.ProjectRelativePath,
                TargetPath = asset.TargetPath,
                MetaTargetPath = asset.TargetPath + ".meta",
                BackupPath = Path.Combine(backupDirectory, "asset-" + slot + ".backup"),
                BackupMetaPath = Path.Combine(backupDirectory, "asset-" + slot + ".meta.backup"),
                ExpectedSha256 = asset.ExpectedSha256,
                IsFolder = asset.IsFolder,
                Remove = asset.Remove,
            };

            if (asset.WriteContent && !asset.Remove)
            {
                if (!archives.TryGetValue(asset.SourceId, out Dictionary<string, ActionFitSdkUnityPackageEntry> index) ||
                    !index.TryGetValue(asset.ProjectRelativePath, out ActionFitSdkUnityPackageEntry archived))
                {
                    throw new InvalidOperationException($"Validated archive has no entry for {asset.ProjectRelativePath}.");
                }

                Directory.CreateDirectory(stageDirectory);
                if (!asset.IsFolder)
                {
                    if (archived.AssetData == null)
                        throw new InvalidOperationException($"Archive entry {asset.ProjectRelativePath} has no content.");
                    entry.StagedPath = Path.Combine(stageDirectory, slot + ".asset");
                    File.WriteAllBytes(entry.StagedPath, archived.AssetData);
                    entry.WriteContent = true;
                }

                // Existing .meta files are never replaced: their GUID is already verified and
                // resolvers own the importer labels inside them.
                if (archived.MetaData != null && !File.Exists(entry.MetaTargetPath))
                {
                    entry.StagedMetaPath = Path.Combine(stageDirectory, slot + ".meta");
                    File.WriteAllBytes(entry.StagedMetaPath, archived.MetaData);
                    entry.WriteMeta = true;
                }
            }

            entries.Add(entry);
        }

        return entries.ToArray();
    }

    private static async Task DownloadArtifactAsync(
        ActionFitSdkInstallProfile profile,
        string url,
        string destination,
        CancellationToken cancellationToken)
    {
        if (!ActionFitSdkInstallProfileValidator.IsAllowedHttpsUrl(url, profile.AllowedDomains, out string error))
            throw new InvalidOperationException($"Artifact download URL was rejected: {error}");

        using var handler = new HttpClientHandler { AllowAutoRedirect = false };
        using var client = new HttpClient(handler) { Timeout = TimeSpan.FromMinutes(5) };
        var current = new Uri(url, UriKind.Absolute);
        for (int redirectCount = 0; redirectCount <= 5; redirectCount++)
        {
            using HttpResponseMessage response = await client.GetAsync(current, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
            int statusCode = (int)response.StatusCode;
            if (statusCode >= 300 && statusCode <= 399)
            {
                if (redirectCount == 5 || response.Headers.Location == null)
                    throw new InvalidOperationException("Artifact download exceeded the safe redirect limit or returned no redirect location.");
                Uri redirected = response.Headers.Location.IsAbsoluteUri
                    ? response.Headers.Location
                    : new Uri(current, response.Headers.Location);
                if (!ActionFitSdkInstallProfileValidator.IsAllowedRedirectUrl(redirected.AbsoluteUri, profile.AllowedDomains, out string redirectError))
                    throw new InvalidOperationException($"Artifact download redirect was rejected: {redirectError}");
                current = redirected;
                continue;
            }

            response.EnsureSuccessStatusCode();
            Directory.CreateDirectory(Path.GetDirectoryName(destination));
            using Stream source = await response.Content.ReadAsStreamAsync();
            using var target = new FileStream(destination, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, true);
            await source.CopyToAsync(target, 81920, cancellationToken);
            return;
        }

        throw new InvalidOperationException("Artifact download did not reach a final response.");
    }

    private static void ApplyArtifactChanges(string journalPath, ActionFitSdkTransactionJournal journal)
    {
        for (int i = 0; i < journal.ArtifactEntries.Length; i++)
        {
            ActionFitSdkArtifactJournalEntry entry = journal.ArtifactEntries[i];
            if (entry.Remove)
            {
                if (File.Exists(entry.TargetPath))
                {
                    VerifyArtifact(entry.TargetPath, entry.ExpectedSha256, entry.PackageId, entry.PackageVersion);
                    Directory.CreateDirectory(Path.GetDirectoryName(entry.BackupPath));
                    entry.BackupIntended = true;
                    SaveJournal(journalPath, journal);
                    File.Move(entry.TargetPath, entry.BackupPath);
                    entry.BackedUp = true;
                    SaveJournal(journalPath, journal);
                }
                continue;
            }

            if (File.Exists(entry.TargetPath))
            {
                VerifyArtifact(entry.TargetPath, entry.ExpectedSha256, entry.PackageId, entry.PackageVersion);
                continue;
            }
            if (string.IsNullOrWhiteSpace(entry.StagedPath) || !File.Exists(entry.StagedPath))
                throw new FileNotFoundException("Verified staged SDK artifact is missing.", entry.StagedPath);

            Directory.CreateDirectory(Path.GetDirectoryName(entry.TargetPath));
            entry.CreateIntended = true;
            SaveJournal(journalPath, journal);
            File.Move(entry.StagedPath, entry.TargetPath);
            entry.Created = true;
            SaveJournal(journalPath, journal);
        }
    }

    /// <summary>
    /// Commits planned Unity Asset changes. Adopted and preserved entries are intentionally no-ops,
    /// so an adoption never rewrites project content or an existing .meta file.
    /// </summary>
    private static void ApplyAssetChanges(string journalPath, ActionFitSdkTransactionJournal journal)
    {
        foreach (ActionFitSdkAssetJournalEntry entry in journal.AssetEntries)
        {
            if (entry.Remove)
            {
                RemoveAssetEntry(journalPath, journal, entry);
                continue;
            }

            if (entry.IsFolder)
            {
                if (!Directory.Exists(entry.TargetPath))
                {
                    entry.CreateIntended = true;
                    SaveJournal(journalPath, journal);
                    Directory.CreateDirectory(entry.TargetPath);
                    entry.Created = true;
                    SaveJournal(journalPath, journal);
                }
            }
            else if (entry.WriteContent)
            {
                if (File.Exists(entry.TargetPath))
                    throw new IOException($"Cannot install asset because the target already exists: {entry.TargetPath}");
                if (string.IsNullOrWhiteSpace(entry.StagedPath) || !File.Exists(entry.StagedPath))
                    throw new FileNotFoundException("Verified staged asset is missing.", entry.StagedPath);

                Directory.CreateDirectory(Path.GetDirectoryName(entry.TargetPath));
                entry.CreateIntended = true;
                SaveJournal(journalPath, journal);
                File.Move(entry.StagedPath, entry.TargetPath);
                entry.Created = true;
                SaveJournal(journalPath, journal);
            }

            if (entry.WriteMeta && !File.Exists(entry.MetaTargetPath) &&
                !string.IsNullOrWhiteSpace(entry.StagedMetaPath) && File.Exists(entry.StagedMetaPath))
            {
                Directory.CreateDirectory(Path.GetDirectoryName(entry.MetaTargetPath));
                File.Move(entry.StagedMetaPath, entry.MetaTargetPath);
                entry.MetaCreated = true;
                SaveJournal(journalPath, journal);
            }
        }
    }

    private static void RemoveAssetEntry(
        string journalPath,
        ActionFitSdkTransactionJournal journal,
        ActionFitSdkAssetJournalEntry entry)
    {
        bool contentExists = entry.IsFolder ? Directory.Exists(entry.TargetPath) : File.Exists(entry.TargetPath);
        bool metaExists = File.Exists(entry.MetaTargetPath);
        if (!contentExists && !metaExists)
            return;

        // A folder that still holds unmanaged content is preserved rather than deleted.
        if (entry.IsFolder && contentExists &&
            (Directory.EnumerateFileSystemEntries(entry.TargetPath).Any()))
        {
            return;
        }

        Directory.CreateDirectory(Path.GetDirectoryName(entry.BackupPath));
        entry.BackupIntended = true;
        SaveJournal(journalPath, journal);

        if (metaExists)
        {
            File.Move(entry.MetaTargetPath, entry.BackupMetaPath);
            entry.MetaBackedUp = true;
            SaveJournal(journalPath, journal);
        }

        if (contentExists)
        {
            if (entry.IsFolder)
                Directory.Delete(entry.TargetPath, false);
            else
                File.Move(entry.TargetPath, entry.BackupPath);
            entry.BackedUp = true;
            SaveJournal(journalPath, journal);
        }
    }

    private static void RollbackAssetEntry(ActionFitSdkAssetJournalEntry entry)
    {
        if (entry.MetaCreated && File.Exists(entry.MetaTargetPath))
            File.Delete(entry.MetaTargetPath);

        if (entry.CreateIntended)
        {
            if (entry.IsFolder)
            {
                if (entry.Created && Directory.Exists(entry.TargetPath) &&
                    !Directory.EnumerateFileSystemEntries(entry.TargetPath).Any())
                {
                    Directory.Delete(entry.TargetPath, false);
                }
            }
            else if (File.Exists(entry.TargetPath))
            {
                File.Delete(entry.TargetPath);
            }
        }

        if (!entry.BackupIntended)
            return;

        if (entry.BackedUp && !entry.IsFolder && File.Exists(entry.BackupPath))
        {
            if (File.Exists(entry.TargetPath))
                throw new IOException($"Cannot restore asset because target exists: {entry.TargetPath}");
            Directory.CreateDirectory(Path.GetDirectoryName(entry.TargetPath));
            File.Move(entry.BackupPath, entry.TargetPath);
        }
        else if (entry.BackedUp && entry.IsFolder && !Directory.Exists(entry.TargetPath))
        {
            Directory.CreateDirectory(entry.TargetPath);
        }

        if (entry.MetaBackedUp && File.Exists(entry.BackupMetaPath))
        {
            if (File.Exists(entry.MetaTargetPath))
                throw new IOException($"Cannot restore asset meta because target exists: {entry.MetaTargetPath}");
            Directory.CreateDirectory(Path.GetDirectoryName(entry.MetaTargetPath));
            File.Move(entry.BackupMetaPath, entry.MetaTargetPath);
        }
    }

    private static void VerifyCommitted(ActionFitSdkInstallPlan plan, ActionFitSdkTransactionJournal journal)
    {
        string manifest = File.ReadAllText(plan.Context.ManifestPath);
        if (!string.Equals(ActionFitSdkInstallPlanner.Hash(manifest), ActionFitSdkInstallPlanner.Hash(plan.UpdatedManifest), StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Committed manifest does not match the reviewed SDK plan.");
        string ownership = File.ReadAllText(plan.Context.OwnershipPath);
        if (!string.Equals(ActionFitSdkInstallPlanner.Hash(ownership), ActionFitSdkInstallPlanner.Hash(plan.UpdatedOwnership), StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Committed ownership state does not match the reviewed SDK plan.");

        foreach (ActionFitSdkArtifactJournalEntry entry in journal.ArtifactEntries)
        {
            if (entry.Remove)
            {
                if (File.Exists(entry.TargetPath))
                    throw new InvalidOperationException($"Removed SDK artifact still exists: {entry.TargetPath}");
            }
            else
            {
                VerifyArtifact(entry.TargetPath, entry.ExpectedSha256, entry.PackageId, entry.PackageVersion);
            }
        }

        foreach (ActionFitSdkAssetJournalEntry entry in journal.AssetEntries)
        {
            if (entry.Remove)
            {
                if (entry.BackedUp && !entry.IsFolder && File.Exists(entry.TargetPath))
                    throw new InvalidOperationException($"Removed asset still exists: {entry.TargetPath}");
                if (entry.MetaBackedUp && File.Exists(entry.MetaTargetPath))
                    throw new InvalidOperationException($"Removed asset meta still exists: {entry.MetaTargetPath}");
                continue;
            }

            if (entry.IsFolder)
            {
                if (!Directory.Exists(entry.TargetPath))
                    throw new InvalidOperationException($"Installed asset folder is missing: {entry.TargetPath}");
                continue;
            }

            if (!File.Exists(entry.TargetPath))
                throw new InvalidOperationException($"Installed asset is missing: {entry.TargetPath}");

            // Preserved paths record their current value as the expected value, so this check still
            // proves the commit left them untouched. A missing hash can only be a folder-shaped entry.
            if (string.IsNullOrEmpty(entry.ExpectedSha256))
                continue;

            string actual = ActionFitSdkArtifactVerifier.ComputeSha256(entry.TargetPath);
            if (!string.Equals(actual, entry.ExpectedSha256, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException($"Committed asset content does not match the reviewed plan: {entry.TargetPath}");
        }
    }

    private static ActionFitSdkExecutionResult RecoverFailure(
        string journalPath,
        ActionFitSdkTransactionJournal journal,
        string failure,
        string planId)
    {
        ActionFitSdkRecoveryResult recovery = Rollback(journalPath, journal);
        return new ActionFitSdkExecutionResult
        {
            Success = false,
            RolledBack = recovery.RolledBack,
            RecoveryRequired = recovery.RecoveryRequired,
            Code = recovery.RecoveryRequired ? "RECOVERY_REQUIRED" : "ROLLED_BACK",
            Message = recovery.RecoveryRequired
                ? $"{failure}\nAutomatic rollback failed: {recovery.Message}"
                : $"{failure}\n{recovery.Message}",
            PlanId = planId ?? "",
            JournalPath = journalPath,
        };
    }

    private static ActionFitSdkRecoveryResult Rollback(string journalPath, ActionFitSdkTransactionJournal journal)
    {
        try
        {
            ValidateJournal(journal);
            ActionFitPackageManifestUtility.WriteAtomic(journal.ManifestPath, journal.OriginalManifest);
            if (journal.OwnershipOriginallyExisted)
                ActionFitPackageManifestUtility.WriteAtomic(journal.OwnershipPath, journal.OriginalOwnership, false);
            else if (File.Exists(journal.OwnershipPath))
                File.Delete(journal.OwnershipPath);

            // Assets roll back before artifacts so the project tree is restored in reverse order.
            foreach (ActionFitSdkAssetJournalEntry entry in journal.AssetEntries.Reverse())
                RollbackAssetEntry(entry);

            foreach (ActionFitSdkArtifactJournalEntry entry in journal.ArtifactEntries.Reverse())
            {
                bool moveCreatedTarget = entry.CreateIntended && File.Exists(entry.TargetPath) &&
                                         (entry.Created || string.IsNullOrWhiteSpace(entry.StagedPath) || !File.Exists(entry.StagedPath));
                if (moveCreatedTarget)
                {
                    VerifyArtifact(entry.TargetPath, entry.ExpectedSha256, entry.PackageId, entry.PackageVersion);
                    File.Delete(entry.TargetPath);
                }
                if (entry.BackupIntended && File.Exists(entry.BackupPath))
                {
                    VerifyArtifact(entry.BackupPath, entry.ExpectedSha256, entry.PackageId, entry.PackageVersion);
                    Directory.CreateDirectory(Path.GetDirectoryName(entry.TargetPath));
                    if (File.Exists(entry.TargetPath))
                        throw new IOException($"Cannot restore SDK artifact because target exists: {entry.TargetPath}");
                    File.Move(entry.BackupPath, entry.TargetPath);
                }
            }

            if (!string.Equals(ActionFitSdkInstallPlanner.Hash(File.ReadAllText(journal.ManifestPath)), ActionFitSdkInstallPlanner.Hash(journal.OriginalManifest), StringComparison.OrdinalIgnoreCase))
                throw new IOException("SDK manifest rollback verification failed.");
            if (journal.OwnershipOriginallyExisted &&
                !string.Equals(ActionFitSdkInstallPlanner.Hash(File.ReadAllText(journal.OwnershipPath)), ActionFitSdkInstallPlanner.Hash(journal.OriginalOwnership), StringComparison.OrdinalIgnoreCase))
                throw new IOException("SDK ownership rollback verification failed.");

            CleanupCompleted(journalPath, journal);
            return new ActionFitSdkRecoveryResult
            {
                Success = true,
                RolledBack = true,
                Code = "ROLLED_BACK",
                Message = "Original manifest, ownership, and artifact state were restored.",
                JournalPath = journalPath,
            };
        }
        catch (Exception ex)
        {
            return RecoveryRequired("RECOVERY_REQUIRED", ex.Message, journalPath);
        }
    }

    private static void ValidateJournal(ActionFitSdkTransactionJournal journal)
    {
        if (journal.SchemaVersion != 1) throw new InvalidOperationException("Unsupported SDK transaction journal schema.");
        if (string.IsNullOrWhiteSpace(journal.TransactionId) ||
            !Regex.IsMatch(journal.TransactionId, "^[A-Za-z0-9][A-Za-z0-9._-]*$", RegexOptions.CultureInvariant) ||
            journal.TransactionId.Contains(".."))
        {
            throw new InvalidOperationException("SDK transaction journal has an unsafe transaction ID.");
        }
        var context = ActionFitSdkProjectContext.ForProjectRoot(journal.ProjectRoot);
        context.Validate();
        if (!PathEquals(context.ManifestPath, journal.ManifestPath) || !PathEquals(context.OwnershipPath, journal.OwnershipPath))
            throw new InvalidOperationException("SDK transaction journal points outside its declared project paths.");
        foreach (ActionFitSdkArtifactJournalEntry entry in journal.ArtifactEntries ?? Array.Empty<ActionFitSdkArtifactJournalEntry>())
        {
            EnsureProjectPath(context, entry.TargetPath);
            EnsureTransactionPath(context, entry.BackupPath);
            if (!string.IsNullOrWhiteSpace(entry.StagedPath)) EnsureTransactionPath(context, entry.StagedPath);
        }

        // A journal is replayed from disk during recovery, so asset paths are bounded here too.
        foreach (ActionFitSdkAssetJournalEntry entry in journal.AssetEntries ?? Array.Empty<ActionFitSdkAssetJournalEntry>())
        {
            EnsureProjectPath(context, entry.TargetPath);
            EnsureProjectPath(context, entry.MetaTargetPath);
            EnsureTransactionPath(context, entry.BackupPath);
            EnsureTransactionPath(context, entry.BackupMetaPath);
            if (!string.IsNullOrWhiteSpace(entry.StagedPath)) EnsureTransactionPath(context, entry.StagedPath);
            if (!string.IsNullOrWhiteSpace(entry.StagedMetaPath)) EnsureTransactionPath(context, entry.StagedMetaPath);
        }
    }

    private static void VerifyArtifact(string path, string sha256, string packageId, string packageVersion)
    {
        if (!File.Exists(path)) throw new FileNotFoundException("SDK artifact is missing.", path);
        string actual = ActionFitSdkArtifactVerifier.ComputeSha256(path);
        if (!string.Equals(actual, sha256, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException($"SDK artifact SHA-256 mismatch for {path}.");
        if (!string.IsNullOrWhiteSpace(packageId) &&
            !ActionFitSdkArtifactVerifier.TryValidatePackageIdentity(path, packageId, packageVersion, out string error))
            throw new InvalidOperationException(error);
    }

    private static void SaveJournal(string journalPath, ActionFitSdkTransactionJournal journal)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(journalPath));
        ActionFitPackageManifestUtility.WriteAtomic(journalPath, JsonUtility.ToJson(journal, true) + "\n", false);
    }

    private static void CleanupCompleted(string journalPath, ActionFitSdkTransactionJournal journal)
    {
        string transactionDirectory = Path.Combine(Path.GetDirectoryName(journalPath), journal.TransactionId);
        try
        {
            if (File.Exists(journalPath)) File.Delete(journalPath);
        }
        catch (Exception ex)
        {
            Debug.LogWarning($"[ActionFitSdkInstall] Could not delete a completed transaction journal: {ex.Message}");
        }
        try
        {
            if (Directory.Exists(transactionDirectory)) Directory.Delete(transactionDirectory, true);
        }
        catch (Exception ex)
        {
            Debug.LogWarning($"[ActionFitSdkInstall] Could not delete completed transaction files: {ex.Message}");
        }
    }

    private static void TryDeletePreparedDirectory(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || !Directory.Exists(path)) return;
        try
        {
            Directory.Delete(path, true);
        }
        catch (Exception ex)
        {
            Debug.LogWarning($"[ActionFitSdkInstall] Could not clean an uncommitted transaction directory: {ex.Message}");
        }
    }

    private static void EnsureProjectPath(ActionFitSdkProjectContext context, string path)
    {
        string root = Path.GetFullPath(context.ProjectRoot).TrimEnd(Path.DirectorySeparatorChar);
        string fullPath = Path.GetFullPath(path);
        if (!fullPath.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException($"SDK artifact path is outside the project: {fullPath}");
        if (fullPath.StartsWith(Path.Combine(root, "Assets") + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) ||
            fullPath.StartsWith(Path.Combine(root, "ProjectSettings") + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) ||
            fullPath.StartsWith(Path.Combine(root, "UserSettings") + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException($"SDK artifact cache cannot target project assets or settings: {fullPath}");
    }

    private static void EnsureTransactionPath(ActionFitSdkProjectContext context, string path)
    {
        string root = Path.GetFullPath(context.TransactionRoot).TrimEnd(Path.DirectorySeparatorChar);
        string fullPath = Path.GetFullPath(path);
        if (!fullPath.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException($"SDK transaction path is outside the transaction root: {fullPath}");
    }

    private static bool IsCurrentProject(ActionFitSdkProjectContext context)
    {
        return PathEquals(context.ProjectRoot, ActionFitPackagePaths.ProjectRoot);
    }

    private static bool PathEquals(string left, string right)
    {
        return string.Equals(Path.GetFullPath(left).TrimEnd(Path.DirectorySeparatorChar), Path.GetFullPath(right).TrimEnd(Path.DirectorySeparatorChar), StringComparison.OrdinalIgnoreCase);
    }

    private static ActionFitSdkExecutionResult Failure(string code, string message, string planId, string journalPath)
    {
        return new ActionFitSdkExecutionResult
        {
            Success = false,
            Code = code,
            Message = message,
            PlanId = planId ?? "",
            JournalPath = journalPath ?? "",
        };
    }

    private static ActionFitSdkRecoveryResult RecoveryRequired(string code, string message, string journalPath)
    {
        return new ActionFitSdkRecoveryResult
        {
            Success = false,
            RecoveryRequired = true,
            Code = code,
            Message = message,
            JournalPath = journalPath,
        };
    }
}

[Serializable]
internal sealed class ActionFitSdkTransactionJournal
{
    public int SchemaVersion;
    public string TransactionId = "";
    public string PlanId = "";
    public string ProfileId = "";
    public string ProjectRoot = "";
    public string ManifestPath = "";
    public string OwnershipPath = "";
    public bool OwnershipOriginallyExisted;
    public string OriginalManifest = "";
    public string UpdatedManifest = "";
    public string OriginalOwnership = "";
    public string UpdatedOwnership = "";
    public ActionFitSdkArtifactJournalEntry[] ArtifactEntries = Array.Empty<ActionFitSdkArtifactJournalEntry>();
    public ActionFitSdkAssetJournalEntry[] AssetEntries = Array.Empty<ActionFitSdkAssetJournalEntry>();
    public string Phase = "";
}

/// <summary>
/// One Unity Asset mutation inside a transaction. Content and .meta are tracked together so a
/// rollback restores both, never leaving an asset without its GUID.
/// </summary>
[Serializable]
internal sealed class ActionFitSdkAssetJournalEntry
{
    public string ProjectRelativePath = "";
    public string TargetPath = "";
    public string MetaTargetPath = "";
    public string StagedPath = "";
    public string StagedMetaPath = "";
    public string BackupPath = "";
    public string BackupMetaPath = "";
    public string ExpectedSha256 = "";
    public bool IsFolder;
    public bool Remove;
    public bool WriteContent;
    public bool WriteMeta;
    public bool CreateIntended;
    public bool Created;
    public bool MetaCreated;
    public bool BackupIntended;
    public bool BackedUp;
    public bool MetaBackedUp;
}

[Serializable]
internal sealed class ActionFitSdkArtifactJournalEntry
{
    public string TargetPath = "";
    public string StagedPath = "";
    public string BackupPath = "";
    public string ExpectedSha256 = "";
    public string PackageId = "";
    public string PackageVersion = "";
    public bool Remove;
    public bool CreateIntended;
    public bool Created;
    public bool BackupIntended;
    public bool BackedUp;
}

internal enum ActionFitSdkTransactionPhase
{
    Prepared,
    ArtifactsPrepared,
    AssetsCommitted,
    ManifestCommitted,
    OwnershipCommitted,
    Completed,
}

internal static class ActionFitSdkArtifactVerifier
{
    private const long MaxPackageJsonEntrySize = 8 * 1024 * 1024;

    private static readonly Regex PackageNamePattern = new("\"name\"\\s*:\\s*\"(?<value>[^\"]+)\"", RegexOptions.CultureInvariant);
    private static readonly Regex PackageVersionPattern = new("\"version\"\\s*:\\s*\"(?<value>[^\"]+)\"", RegexOptions.CultureInvariant);

    public static string ComputeSha256(string path)
    {
        using SHA256 sha256 = SHA256.Create();
        using FileStream stream = File.OpenRead(path);
        return BitConverter.ToString(sha256.ComputeHash(stream)).Replace("-", "").ToLowerInvariant();
    }

    public static bool TryValidatePackageIdentity(string path, string expectedPackageId, string expectedVersion, out string error)
    {
        try
        {
            string packageJson = ReadPackageJsonFromTgz(path);
            string packageId = PackageNamePattern.Match(packageJson).Groups["value"].Value;
            string version = PackageVersionPattern.Match(packageJson).Groups["value"].Value;
            if (!string.Equals(packageId, expectedPackageId, StringComparison.Ordinal))
            {
                error = $"SDK artifact package ID mismatch. Expected {expectedPackageId}, found {packageId}.";
                return false;
            }
            if (!string.Equals(version, expectedVersion, StringComparison.Ordinal))
            {
                error = $"SDK artifact package version mismatch. Expected {expectedVersion}, found {version}.";
                return false;
            }
            error = "";
            return true;
        }
        catch (Exception ex)
        {
            error = $"SDK artifact identity validation failed: {ex.Message}";
            return false;
        }
    }

    private static string ReadPackageJsonFromTgz(string path)
    {
        using FileStream file = File.OpenRead(path);
        using var gzip = new GZipStream(file, CompressionMode.Decompress, false);
        var header = new byte[512];
        while (ReadBlock(gzip, header, header.Length) == header.Length)
        {
            if (header.All(value => value == 0)) break;
            string name = ReadNullTerminatedAscii(header, 0, 100);
            long size = ReadOctal(header, 124, 12);
            if (size < 0)
                throw new InvalidDataException($"Unexpected tar entry size for {name}: {size}");

            long padding = (512 - (size % 512)) % 512;
            string normalized = name.Replace('\\', '/').TrimStart('.', '/');
            if (!string.Equals(normalized, "package/package.json", StringComparison.OrdinalIgnoreCase) &&
                !string.Equals(normalized, "package.json", StringComparison.OrdinalIgnoreCase))
            {
                SkipEntry(gzip, size + padding, name);
                continue;
            }

            if (size > MaxPackageJsonEntrySize)
                throw new InvalidDataException($"Unexpected tar entry size for {name}: {size}");

            byte[] content = new byte[checked((int)size)];
            if (ReadBlock(gzip, content, content.Length) != content.Length)
                throw new EndOfStreamException($"Incomplete tar entry: {name}");
            return Encoding.UTF8.GetString(content);
        }
        throw new InvalidDataException("Artifact does not contain package/package.json.");
    }

    private static void SkipEntry(Stream stream, long count, string name)
    {
        byte[] buffer = new byte[81920];
        while (count > 0)
        {
            int chunk = (int)Math.Min(buffer.Length, count);
            if (ReadBlock(stream, buffer, chunk) != chunk)
                throw new EndOfStreamException($"Incomplete tar entry: {name}");
            count -= chunk;
        }
    }

    private static int ReadBlock(Stream stream, byte[] buffer, int count)
    {
        int offset = 0;
        while (offset < count)
        {
            int read = stream.Read(buffer, offset, count - offset);
            if (read <= 0) break;
            offset += read;
        }
        return offset;
    }

    private static string ReadNullTerminatedAscii(byte[] buffer, int offset, int count)
    {
        int length = 0;
        while (length < count && buffer[offset + length] != 0) length++;
        return Encoding.ASCII.GetString(buffer, offset, length);
    }

    private static long ReadOctal(byte[] buffer, int offset, int count)
    {
        string value = Encoding.ASCII.GetString(buffer, offset, count).Trim('\0', ' ');
        return string.IsNullOrWhiteSpace(value) ? 0 : Convert.ToInt64(value, 8);
    }
}

[InitializeOnLoad]
internal static class ActionFitSdkPendingTransactionReporter
{
    static ActionFitSdkPendingTransactionReporter()
    {
        EditorApplication.delayCall += Report;
    }

    private static void Report()
    {
        string[] pending = ActionFitSdkInstallApi.InspectPendingTransactions();
        if (pending.Length == 0) return;
        Debug.LogError($"[ActionFitSdkInstall] RecoveryRequired: {pending.Length} pending transaction journal(s). Review the SDK Profiles window and run explicit recovery. No automatic rollback was performed.");
    }
}
#endif
