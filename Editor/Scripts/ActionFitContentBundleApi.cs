#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using UnityEditor;
using UnityEditor.PackageManager;
using UnityEngine;
using Debug = UnityEngine.Debug;

/// <summary>
/// Plans and applies Git UPM content bundles while preserving project-owned package overrides.
/// </summary>
public static class ActionFitContentBundleApi
{
    private const int SupportedSchemaVersion = 1;

    public static ActionFitContentBundlePlan InspectJson(string profileJson)
    {
        return PlanJson(profileJson);
    }

    public static ActionFitContentBundlePlan PlanJson(string profileJson)
    {
        try
        {
            ActionFitContentBundleProfile profile = ParseProfile(profileJson);
            string manifest = ReadManifest();
            ActionFitContentBundleStateFile state = ActionFitContentBundleStateStore.Load();
            return ActionFitContentBundlePlanner.PlanInstall(
                profile,
                manifest,
                state,
                ActionFitContentBundleEnvironment.GetEmbeddedPackage);
        }
        catch (Exception exception)
        {
            return FailedPlan("PLAN_FAILED", exception.Message);
        }
    }

    public static ActionFitContentBundleResult InstallJson(string profileJson)
    {
        return InstallOrRepair(profileJson, false);
    }

    public static ActionFitContentBundleResult RepairJson(string profileJson)
    {
        return InstallOrRepair(profileJson, true);
    }

    public static ActionFitContentBundlePlan PlanRelease(string bundleId)
    {
        try
        {
            ActionFitContentBundleStateFile state = ActionFitContentBundleStateStore.Load();
            ActionFitContentBundleRecord record = state.bundles.FirstOrDefault(item =>
                string.Equals(item.bundleId, bundleId, StringComparison.Ordinal));
            if (record == null)
                return FailedPlan("BUNDLE_NOT_ACTIVE", $"Content bundle is not active: {bundleId}");

            ActionFitContentBundleProfile profile = ParseProfile(record.profileJson);
            string login = ActionFitContentBundleGitHubIdentity.GetCurrentLogin();
            bool authorized = ActionFitContentBundlePlanner.IsReleaseAuthorized(profile, login);
            if (!authorized)
            {
                ActionFitContentBundlePlan denied = FailedPlan(
                    "RELEASE_NOT_AUTHORIZED",
                    "The current GitHub login is not authorized to release this content bundle.");
                denied.bundleId = record.bundleId;
                denied.bundleVersion = record.bundleVersion;
                denied.authorized = false;
                return denied;
            }

            return ActionFitContentBundlePlanner.PlanRelease(
                record,
                state,
                ReadManifest(),
                ActionFitContentBundleEnvironment.GetEmbeddedPackage);
        }
        catch (Exception exception)
        {
            return FailedPlan("RELEASE_PLAN_FAILED", exception.Message);
        }
    }

    public static ActionFitContentBundleResult Release(string bundleId)
    {
        if (Application.isBatchMode)
            return BatchModeReadOnlyResult(bundleId);

        ActionFitContentBundlePlan plan = PlanRelease(bundleId);
        if (!plan.success)
            return FromPlan(plan);

        string journalPath = "";
        try
        {
            ActionFitContentBundleStateFile state = ActionFitContentBundleStateStore.Load();
            ActionFitContentBundleRecord record = state.bundles.First(item =>
                string.Equals(item.bundleId, bundleId, StringComparison.Ordinal));
            string originalManifest = ReadManifest();
            string updatedManifest = ActionFitContentBundlePlanner.ApplyPlan(originalManifest, plan);
            string originalStateJson = ActionFitContentBundleStateStore.Serialize(state);
            var updatedState = new ActionFitContentBundleStateFile
            {
                schemaVersion = state.schemaVersion,
                bundles = state.bundles
                    .Where(item => !string.Equals(item.bundleId, bundleId, StringComparison.Ordinal))
                    .ToArray(),
            };
            string updatedStateJson = ActionFitContentBundleStateStore.Serialize(updatedState);

            var journal = new ActionFitContentBundleTransactionJournal
            {
                schemaVersion = SupportedSchemaVersion,
                operation = ActionFitContentBundleOperation.Release.ToString(),
                bundleId = bundleId,
                profileJson = record.profileJson,
                originalManifest = originalManifest,
                updatedManifest = updatedManifest,
                originalStateJson = originalStateJson,
                updatedStateJson = updatedStateJson,
                affectedPackageIds = plan.changes.Select(change => change.packageId).Distinct().ToArray(),
                phase = ActionFitContentBundleTransactionPhase.Prepared.ToString(),
            };

            journalPath = ActionFitContentBundleJournalStore.Save(journal);
            ActionFitPackageManifestUtility.WriteAtomic(ActionFitPackagePaths.ManifestPath, updatedManifest);
            journal.phase = ActionFitContentBundleTransactionPhase.ManifestCommitted.ToString();
            ActionFitContentBundleJournalStore.Save(journal, journalPath);

            ActionFitContentBundleStateStore.Save(updatedState);
            journal.phase = ActionFitContentBundleTransactionPhase.StateCommitted.ToString();
            ActionFitContentBundleJournalStore.Save(journal, journalPath);
            VerifyReleaseCommit(journal);

            ActionFitContentBundleJournalStore.Delete(journalPath);
            if (!ActionFitPackageManifestUtility.DependenciesMatch(
                    originalManifest,
                    updatedManifest,
                    journal.affectedPackageIds))
            {
                Client.Resolve();
            }

            return new ActionFitContentBundleResult
            {
                success = true,
                changed = true,
                code = "RELEASED",
                message = $"Content bundle released: {bundleId}",
                bundleId = bundleId,
                journalPath = journalPath,
                plan = plan,
            };
        }
        catch (Exception exception)
        {
            return RecoverFailedRelease(bundleId, journalPath, exception, plan);
        }
    }

    public static ActionFitContentBundleResult Remove(string bundleId)
    {
        return Release(bundleId);
    }

    public static ActionFitContentBundleResult[] Recover()
    {
        if (Application.isBatchMode)
            return new[] { BatchModeReadOnlyResult("") };

        var results = new List<ActionFitContentBundleResult>();
        foreach ((string path, ActionFitContentBundleTransactionJournal journal) in ActionFitContentBundleJournalStore.LoadAll())
        {
            if (string.Equals(journal.operation, ActionFitContentBundleOperation.Install.ToString(), StringComparison.Ordinal))
            {
                results.Add(TryFinalizeInstall(path, journal));
                continue;
            }

            if (string.Equals(journal.operation, ActionFitContentBundleOperation.Release.ToString(), StringComparison.Ordinal))
            {
                results.Add(RecoverRelease(path, journal));
                continue;
            }

            results.Add(new ActionFitContentBundleResult
            {
                success = false,
                recoveryRequired = true,
                code = "UNKNOWN_JOURNAL_OPERATION",
                message = $"Unknown content bundle transaction operation: {journal.operation}",
                bundleId = journal.bundleId,
                journalPath = path,
            });
        }

        return results.ToArray();
    }

    public static ActionFitContentBundleStatus[] GetStatuses()
    {
        var statuses = new List<ActionFitContentBundleStatus>();
        ActionFitContentBundleStateFile state = ActionFitContentBundleStateStore.Load();
        string manifest = File.Exists(ActionFitPackagePaths.ManifestPath) ? ReadManifest() : "";

        foreach (ActionFitContentBundleRecord record in state.bundles.OrderBy(item => item.bundleId, StringComparer.Ordinal))
        {
            ActionFitContentBundleProfile profile = ParseProfile(record.profileJson);
            ActionFitContentBundlePlan drift = File.Exists(ActionFitPackagePaths.ManifestPath)
                ? ActionFitContentBundlePlanner.PlanReconcile(record, manifest, ActionFitContentBundleEnvironment.GetEmbeddedPackage)
                : FailedPlan("MANIFEST_MISSING", "Packages/manifest.json is missing.");
            statuses.Add(new ActionFitContentBundleStatus
            {
                bundleId = record.bundleId,
                bundleVersion = record.bundleVersion,
                displayName = record.displayName,
                state = drift.success && drift.changes.All(change =>
                    change.kind == ActionFitContentBundleChangeKind.Preserve ||
                    change.kind == ActionFitContentBundleChangeKind.PreserveEmbedded)
                    ? "active"
                    : "repair required",
                bootstrapPackageId = record.bootstrapPackageId,
                bootstrapInstalled = !string.IsNullOrWhiteSpace(manifest) &&
                                     !string.IsNullOrWhiteSpace(record.bootstrapPackageId) &&
                                     !string.IsNullOrWhiteSpace(ActionFitPackageManifestUtility.GetDependency(manifest, record.bootstrapPackageId)),
                releaseAuthorized = ActionFitContentBundlePlanner.IsReleaseAuthorized(
                    profile,
                    ActionFitContentBundleGitHubIdentity.GetCurrentLogin()),
                requiredPackageIds = (record.packages ?? Array.Empty<ActionFitContentBundlePackageOwnership>())
                    .Where(package => package.required)
                    .Select(package => package.packageId)
                    .ToArray(),
                conflicts = drift.conflicts,
            });
        }

        foreach ((string _, ActionFitContentBundleTransactionJournal journal) in ActionFitContentBundleJournalStore.LoadAll())
        {
            if (statuses.Any(status => string.Equals(status.bundleId, journal.bundleId, StringComparison.Ordinal)))
                continue;

            ActionFitContentBundleProfile profile;
            try
            {
                profile = ParseProfile(journal.profileJson);
            }
            catch
            {
                profile = new ActionFitContentBundleProfile { bundleId = journal.bundleId };
            }

            statuses.Add(new ActionFitContentBundleStatus
            {
                bundleId = journal.bundleId,
                bundleVersion = profile.bundleVersion,
                displayName = string.IsNullOrWhiteSpace(profile.displayName) ? journal.bundleId : profile.displayName,
                state = "recovery pending",
                bootstrapPackageId = profile.bootstrapPackageId,
                bootstrapInstalled = !string.IsNullOrWhiteSpace(manifest) &&
                                     !string.IsNullOrWhiteSpace(profile.bootstrapPackageId) &&
                                     !string.IsNullOrWhiteSpace(ActionFitPackageManifestUtility.GetDependency(manifest, profile.bootstrapPackageId)),
                releaseAuthorized = ActionFitContentBundlePlanner.IsReleaseAuthorized(
                    profile,
                    ActionFitContentBundleGitHubIdentity.GetCurrentLogin()),
                requiredPackageIds = (profile.packages ?? Array.Empty<ActionFitContentBundlePackageSpec>())
                    .Where(package => package.required)
                    .Select(package => package.packageId)
                    .ToArray(),
                conflicts = Array.Empty<string>(),
            });
        }

        return statuses.ToArray();
    }

    public static bool IsRequiredPackage(string packageId, out string bundleDisplayName)
    {
        ActionFitContentBundleStateFile state = ActionFitContentBundleStateStore.Load();
        foreach (ActionFitContentBundleRecord bundle in state.bundles)
        {
            if (!(bundle.packages ?? Array.Empty<ActionFitContentBundlePackageOwnership>()).Any(package =>
                    package.required && string.Equals(package.packageId, packageId, StringComparison.Ordinal)))
                continue;

            bundleDisplayName = string.IsNullOrWhiteSpace(bundle.displayName) ? bundle.bundleId : bundle.displayName;
            return true;
        }

        bundleDisplayName = "";
        return false;
    }

    internal static ActionFitContentBundleProfile ParseProfile(string profileJson)
    {
        if (string.IsNullOrWhiteSpace(profileJson))
            throw new InvalidOperationException("Content bundle profile JSON is required.");

        ActionFitContentBundleProfile profile = JsonUtility.FromJson<ActionFitContentBundleProfile>(profileJson);
        ActionFitContentBundlePlanner.ValidateProfile(profile);
        return profile;
    }

    internal static void ReconcileActiveBundles()
    {
        if (!File.Exists(ActionFitPackagePaths.ManifestPath)) return;

        ActionFitContentBundleStateFile state = ActionFitContentBundleStateStore.Load();
        string originalManifest = ReadManifest();
        string updatedManifest = originalManifest;
        var conflicts = new List<string>();

        foreach (ActionFitContentBundleRecord record in state.bundles)
        {
            ActionFitContentBundlePlan plan = ActionFitContentBundlePlanner.PlanReconcile(
                record,
                updatedManifest,
                ActionFitContentBundleEnvironment.GetEmbeddedPackage);
            conflicts.AddRange(plan.conflicts);
            if (plan.success)
            {
                updatedManifest = ActionFitContentBundlePlanner.ApplyPlan(updatedManifest, plan);
                continue;
            }

            var safePlan = new ActionFitContentBundlePlan
            {
                success = true,
                changes = plan.changes
                    .Where(change => change.kind != ActionFitContentBundleChangeKind.Conflict)
                    .ToArray(),
            };
            updatedManifest = ActionFitContentBundlePlanner.ApplyPlan(updatedManifest, safePlan);
        }

        foreach (string conflict in conflicts.Distinct(StringComparer.Ordinal))
            Debug.LogError($"[ActionFitContentBundle] Reconcile: {conflict}");

        if (string.Equals(originalManifest, updatedManifest, StringComparison.Ordinal)) return;
        if (Application.isBatchMode)
        {
            Debug.LogError("[ActionFitContentBundle] Reconcile: required package drift detected in batchmode; manifest was not changed.");
            return;
        }

        ActionFitPackageManifestUtility.WriteAtomic(ActionFitPackagePaths.ManifestPath, updatedManifest);
        Debug.Log("[ActionFitContentBundle] Reconcile: restored missing required package dependencies.");
        Client.Resolve();
    }

    private static ActionFitContentBundleResult InstallOrRepair(string profileJson, bool repair)
    {
        if (Application.isBatchMode)
            return BatchModeReadOnlyResult("");

        string journalPath = "";
        try
        {
            ActionFitContentBundleProfile profile = ParseProfile(profileJson);
            string existingJournal = ActionFitContentBundleJournalStore.PathFor(profile.bundleId);
            if (File.Exists(existingJournal))
            {
                ActionFitContentBundleTransactionJournal pending = ActionFitContentBundleJournalStore.Load(existingJournal);
                return TryFinalizeInstall(existingJournal, pending);
            }

            ActionFitContentBundleStateFile state = ActionFitContentBundleStateStore.Load();
            ActionFitContentBundleRecord active = state.bundles.FirstOrDefault(item =>
                string.Equals(item.bundleId, profile.bundleId, StringComparison.Ordinal));
            if (active != null && string.Equals(active.bundleVersion, profile.bundleVersion, StringComparison.Ordinal))
            {
                if (repair)
                    ReconcileActiveBundles();
                return new ActionFitContentBundleResult
                {
                    success = true,
                    changed = false,
                    code = "ALREADY_ACTIVE",
                    message = $"Content bundle is already active: {profile.bundleId}@{profile.bundleVersion}",
                    bundleId = profile.bundleId,
                };
            }

            if (active != null)
            {
                return new ActionFitContentBundleResult
                {
                    success = false,
                    code = "BUNDLE_VERSION_CONFLICT",
                    message = $"Release the active bundle before installing another version: {active.bundleVersion} -> {profile.bundleVersion}",
                    bundleId = profile.bundleId,
                };
            }

            string originalManifest = ReadManifest();
            ActionFitContentBundlePlan plan = ActionFitContentBundlePlanner.PlanInstall(
                profile,
                originalManifest,
                state,
                ActionFitContentBundleEnvironment.GetEmbeddedPackage);
            if (!plan.success) return FromPlan(plan);

            string updatedManifest = ActionFitContentBundlePlanner.ApplyPlan(originalManifest, plan);
            var journal = new ActionFitContentBundleTransactionJournal
            {
                schemaVersion = SupportedSchemaVersion,
                operation = ActionFitContentBundleOperation.Install.ToString(),
                bundleId = profile.bundleId,
                profileJson = profileJson,
                originalManifest = originalManifest,
                updatedManifest = updatedManifest,
                originalStateJson = ActionFitContentBundleStateStore.Serialize(state),
                updatedStateJson = "",
                affectedPackageIds = profile.packages.Select(package => package.packageId)
                    .Append(profile.bootstrapPackageId)
                    .Where(packageId => !string.IsNullOrWhiteSpace(packageId))
                    .Distinct(StringComparer.Ordinal)
                    .ToArray(),
                phase = ActionFitContentBundleTransactionPhase.Prepared.ToString(),
            };
            journalPath = ActionFitContentBundleJournalStore.Save(journal);

            if (!string.Equals(originalManifest, updatedManifest, StringComparison.Ordinal))
            {
                ActionFitPackageManifestUtility.WriteAtomic(ActionFitPackagePaths.ManifestPath, updatedManifest);
                journal.phase = ActionFitContentBundleTransactionPhase.ManifestCommitted.ToString();
                ActionFitContentBundleJournalStore.Save(journal, journalPath);
            }

            ActionFitContentBundleResult finalized = TryFinalizeInstall(journalPath, journal);
            finalized.plan = plan;
            if (finalized.pending && !string.Equals(originalManifest, updatedManifest, StringComparison.Ordinal))
                Client.Resolve();
            return finalized;
        }
        catch (Exception exception)
        {
            return new ActionFitContentBundleResult
            {
                success = false,
                recoveryRequired = !string.IsNullOrWhiteSpace(journalPath) && File.Exists(journalPath),
                code = "INSTALL_FAILED",
                message = exception.Message,
                journalPath = journalPath,
            };
        }
    }

    private static ActionFitContentBundleResult TryFinalizeInstall(
        string journalPath,
        ActionFitContentBundleTransactionJournal journal)
    {
        try
        {
            ActionFitContentBundleProfile profile = ParseProfile(journal.profileJson);
            string currentManifest = ReadManifest();
            string[] packageIds = profile.packages.Select(package => package.packageId).ToArray();
            bool manifestUpdated = ActionFitPackageManifestUtility.DependenciesMatch(
                currentManifest,
                journal.updatedManifest,
                packageIds);
            if (!manifestUpdated)
            {
                bool manifestOriginal = ActionFitPackageManifestUtility.DependenciesMatch(
                    currentManifest,
                    journal.originalManifest,
                    packageIds);
                if (!manifestOriginal)
                    throw new InvalidOperationException("The pending install manifest no longer matches its original or prepared dependencies.");

                ActionFitPackageManifestUtility.WriteAtomic(ActionFitPackagePaths.ManifestPath, journal.updatedManifest);
                journal.phase = ActionFitContentBundleTransactionPhase.ManifestCommitted.ToString();
                ActionFitContentBundleJournalStore.Save(journal, journalPath);
                Client.Resolve();
                return new ActionFitContentBundleResult
                {
                    success = true,
                    pending = true,
                    changed = true,
                    code = "INSTALL_PENDING",
                    message = "Prepared content bundle dependencies were restored; waiting for Unity Package Manager registration.",
                    bundleId = profile.bundleId,
                    journalPath = journalPath,
                };
            }

            if (!ActionFitContentBundleEnvironment.AreRequiredPackagesReady(profile, out string readiness))
            {
                return new ActionFitContentBundleResult
                {
                    success = true,
                    pending = true,
                    changed = true,
                    code = "INSTALL_PENDING",
                    message = readiness,
                    bundleId = profile.bundleId,
                    journalPath = journalPath,
                };
            }

            ActionFitContentBundleStateFile state = ActionFitContentBundleStateStore.Load();
            ActionFitContentBundleRecord record = BuildRecord(profile, journal, state);
            state.bundles = state.bundles
                .Where(item => !string.Equals(item.bundleId, profile.bundleId, StringComparison.Ordinal))
                .Append(record)
                .OrderBy(item => item.bundleId, StringComparer.Ordinal)
                .ToArray();
            ActionFitContentBundleStateStore.Save(state);
            ActionFitContentBundleStateFile verifiedState = ActionFitContentBundleStateStore.Load();
            if (!verifiedState.bundles.Any(item =>
                    string.Equals(item.bundleId, profile.bundleId, StringComparison.Ordinal) &&
                    string.Equals(item.bundleVersion, profile.bundleVersion, StringComparison.Ordinal)))
            {
                throw new InvalidOperationException("Bundle ownership state could not be verified after write.");
            }

            currentManifest = ReadManifest();
            string completedManifest = string.IsNullOrWhiteSpace(profile.bootstrapPackageId)
                ? currentManifest
                : ActionFitPackageManifestUtility.RemoveDependency(
                    currentManifest,
                    profile.bootstrapPackageId,
                    out _);
            bool removedBootstrap = !string.Equals(currentManifest, completedManifest, StringComparison.Ordinal);
            if (removedBootstrap)
                ActionFitPackageManifestUtility.WriteAtomic(ActionFitPackagePaths.ManifestPath, completedManifest);

            ActionFitContentBundleJournalStore.Delete(journalPath);
            if (removedBootstrap) Client.Resolve();

            return new ActionFitContentBundleResult
            {
                success = true,
                changed = true,
                code = "COMPLETED",
                message = $"Content bundle installed and verified: {profile.bundleId}@{profile.bundleVersion}",
                bundleId = profile.bundleId,
                journalPath = journalPath,
            };
        }
        catch (Exception exception)
        {
            return new ActionFitContentBundleResult
            {
                success = false,
                recoveryRequired = true,
                code = "FINALIZE_FAILED",
                message = exception.Message,
                bundleId = journal.bundleId,
                journalPath = journalPath,
            };
        }
    }

    private static ActionFitContentBundleRecord BuildRecord(
        ActionFitContentBundleProfile profile,
        ActionFitContentBundleTransactionJournal journal,
        ActionFitContentBundleStateFile state)
    {
        var ownership = new List<ActionFitContentBundlePackageOwnership>();
        foreach (ActionFitContentBundlePackageSpec package in profile.packages)
        {
            ActionFitContentBundlePackageOwnership shared = state.bundles
                .SelectMany(bundle => bundle.packages ?? Array.Empty<ActionFitContentBundlePackageOwnership>())
                .FirstOrDefault(item => string.Equals(item.packageId, package.packageId, StringComparison.Ordinal));
            string originalValue = shared?.originalValue ??
                                   ActionFitPackageManifestUtility.GetDependency(journal.originalManifest, package.packageId);
            string appliedValue = ActionFitPackageManifestUtility.GetDependency(journal.updatedManifest, package.packageId);
            ownership.Add(new ActionFitContentBundlePackageOwnership
            {
                packageId = package.packageId,
                version = package.version,
                targetValue = package.gitUrl,
                originalValue = originalValue,
                appliedValue = appliedValue,
                required = package.required,
                removeOnRelease = package.removeOnRelease,
            });
        }

        return new ActionFitContentBundleRecord
        {
            bundleId = profile.bundleId,
            bundleVersion = profile.bundleVersion,
            displayName = profile.displayName,
            bootstrapPackageId = profile.bootstrapPackageId,
            profileJson = journal.profileJson,
            installedUtc = DateTime.UtcNow.ToString("O"),
            packages = ownership.ToArray(),
        };
    }

    private static ActionFitContentBundleResult RecoverRelease(
        string journalPath,
        ActionFitContentBundleTransactionJournal journal)
    {
        try
        {
            string currentManifest = ReadManifest();
            ActionFitContentBundleStateFile currentState = ActionFitContentBundleStateStore.Load();
            bool manifestUpdated = ActionFitPackageManifestUtility.DependenciesMatch(
                currentManifest,
                journal.updatedManifest,
                journal.affectedPackageIds);
            bool manifestOriginal = ActionFitPackageManifestUtility.DependenciesMatch(
                currentManifest,
                journal.originalManifest,
                journal.affectedPackageIds);
            bool stateUpdated = !currentState.bundles.Any(item =>
                string.Equals(item.bundleId, journal.bundleId, StringComparison.Ordinal));
            bool stateOriginal = currentState.bundles.Any(item =>
                string.Equals(item.bundleId, journal.bundleId, StringComparison.Ordinal));

            if (manifestUpdated)
            {
                if (!stateUpdated)
                    ActionFitContentBundleStateStore.WriteJson(journal.updatedStateJson);
                ActionFitContentBundleJournalStore.Delete(journalPath);
                return new ActionFitContentBundleResult
                {
                    success = true,
                    changed = true,
                    code = "RELEASE_RECOVERED",
                    message = $"Completed interrupted content bundle release: {journal.bundleId}",
                    bundleId = journal.bundleId,
                    journalPath = journalPath,
                };
            }

            if (manifestOriginal && stateOriginal)
            {
                ActionFitContentBundleJournalStore.Delete(journalPath);
                return new ActionFitContentBundleResult
                {
                    success = true,
                    changed = false,
                    code = "RELEASE_ROLLED_BACK",
                    message = $"Interrupted content bundle release remained at the original state: {journal.bundleId}",
                    bundleId = journal.bundleId,
                    journalPath = journalPath,
                };
            }

            return new ActionFitContentBundleResult
            {
                success = false,
                recoveryRequired = true,
                code = "RELEASE_RECOVERY_REQUIRED",
                message = "Manifest and bundle ownership state do not match either side of the release transaction.",
                bundleId = journal.bundleId,
                journalPath = journalPath,
            };
        }
        catch (Exception exception)
        {
            return new ActionFitContentBundleResult
            {
                success = false,
                recoveryRequired = true,
                code = "RELEASE_RECOVERY_FAILED",
                message = exception.Message,
                bundleId = journal.bundleId,
                journalPath = journalPath,
            };
        }
    }

    private static ActionFitContentBundleResult RecoverFailedRelease(
        string bundleId,
        string journalPath,
        Exception exception,
        ActionFitContentBundlePlan plan)
    {
        try
        {
            if (!string.IsNullOrWhiteSpace(journalPath) && File.Exists(journalPath))
            {
                ActionFitContentBundleTransactionJournal journal = ActionFitContentBundleJournalStore.Load(journalPath);
                ActionFitPackageManifestUtility.WriteAtomic(ActionFitPackagePaths.ManifestPath, journal.originalManifest);
                ActionFitContentBundleStateStore.WriteJson(journal.originalStateJson);
                ActionFitContentBundleJournalStore.Delete(journalPath);
                return new ActionFitContentBundleResult
                {
                    success = false,
                    changed = false,
                    code = "RELEASE_ROLLED_BACK",
                    message = $"{exception.Message}\nThe original manifest and ownership state were restored.",
                    bundleId = bundleId,
                    journalPath = journalPath,
                    plan = plan,
                };
            }
        }
        catch (Exception recoveryException)
        {
            return new ActionFitContentBundleResult
            {
                success = false,
                recoveryRequired = true,
                code = "RELEASE_RECOVERY_REQUIRED",
                message = $"{exception.Message}\nAutomatic rollback failed: {recoveryException.Message}",
                bundleId = bundleId,
                journalPath = journalPath,
                plan = plan,
            };
        }

        return new ActionFitContentBundleResult
        {
            success = false,
            code = "RELEASE_FAILED",
            message = exception.Message,
            bundleId = bundleId,
            journalPath = journalPath,
            plan = plan,
        };
    }

    private static void VerifyReleaseCommit(ActionFitContentBundleTransactionJournal journal)
    {
        string manifest = ReadManifest();
        if (!ActionFitPackageManifestUtility.DependenciesMatch(
                manifest,
                journal.updatedManifest,
                journal.affectedPackageIds))
        {
            throw new InvalidOperationException("Released manifest dependencies could not be verified.");
        }

        ActionFitContentBundleStateFile state = ActionFitContentBundleStateStore.Load();
        if (state.bundles.Any(item => string.Equals(item.bundleId, journal.bundleId, StringComparison.Ordinal)))
            throw new InvalidOperationException("Released bundle ownership record is still active.");
    }

    private static string ReadManifest()
    {
        if (!File.Exists(ActionFitPackagePaths.ManifestPath))
            throw new FileNotFoundException("Packages/manifest.json is missing.", ActionFitPackagePaths.ManifestPath);
        return File.ReadAllText(ActionFitPackagePaths.ManifestPath);
    }

    private static ActionFitContentBundlePlan FailedPlan(string code, string message)
    {
        return new ActionFitContentBundlePlan
        {
            success = false,
            code = code,
            message = message,
            changes = Array.Empty<ActionFitContentBundleChange>(),
            conflicts = string.IsNullOrWhiteSpace(message) ? Array.Empty<string>() : new[] { message },
            requiredPackageIds = Array.Empty<string>(),
        };
    }

    private static ActionFitContentBundleResult FromPlan(ActionFitContentBundlePlan plan)
    {
        return new ActionFitContentBundleResult
        {
            success = plan.success,
            changed = false,
            code = plan.code,
            message = plan.message,
            bundleId = plan.bundleId,
            plan = plan,
        };
    }

    private static ActionFitContentBundleResult BatchModeReadOnlyResult(string bundleId)
    {
        return new ActionFitContentBundleResult
        {
            success = false,
            changed = false,
            recoveryRequired = false,
            code = "BATCHMODE_READ_ONLY",
            message = "Content bundle mutation is disabled in Unity batchmode. Run inspection or planning APIs instead.",
            bundleId = bundleId ?? "",
        };
    }
}

internal static class ActionFitContentBundlePlanner
{
    private static readonly Regex IdPattern = new("^[a-z0-9][a-z0-9._-]*$", RegexOptions.CultureInvariant);

    public static void ValidateProfile(ActionFitContentBundleProfile profile)
    {
        if (profile == null) throw new InvalidOperationException("Content bundle profile is invalid.");
        if (profile.schemaVersion != 1)
            throw new InvalidOperationException($"Unsupported content bundle profile schema: {profile.schemaVersion}");
        ValidateId(profile.bundleId, "bundle ID");
        if (string.IsNullOrWhiteSpace(profile.bundleVersion))
            throw new InvalidOperationException("Content bundle version is required.");
        ValidateId(profile.bootstrapPackageId, "bootstrap package ID");

        ActionFitContentBundlePackageSpec[] packages = profile.packages ?? Array.Empty<ActionFitContentBundlePackageSpec>();
        if (packages.Length == 0) throw new InvalidOperationException("Content bundle must contain at least one package.");
        if (packages.Any(package => package == null))
            throw new InvalidOperationException("Content bundle contains an empty package entry.");

        string[] duplicateIds = packages
            .GroupBy(package => package.packageId, StringComparer.Ordinal)
            .Where(group => group.Count() > 1)
            .Select(group => group.Key)
            .ToArray();
        if (duplicateIds.Length > 0)
            throw new InvalidOperationException($"Duplicate content bundle packages: {string.Join(", ", duplicateIds)}");

        foreach (ActionFitContentBundlePackageSpec package in packages)
        {
            ValidateId(package.packageId, "package ID");
            if (string.IsNullOrWhiteSpace(package.version))
                throw new InvalidOperationException($"Package version is required: {package.packageId}");
            if (!Uri.TryCreate(RepositoryPart(package.gitUrl), UriKind.Absolute, out Uri uri) ||
                !string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException($"Package Git URL must use HTTPS: {package.packageId}");
            }
            if (!string.Equals(RevisionPart(package.gitUrl), package.version, StringComparison.Ordinal))
                throw new InvalidOperationException($"Package Git URL must pin exact version tag {package.version}: {package.packageId}");
        }

        profile.allowedReleaseGitHubLogins ??= Array.Empty<string>();
        profile.packages = packages;
    }

    public static ActionFitContentBundlePlan PlanInstall(
        ActionFitContentBundleProfile profile,
        string manifest,
        ActionFitContentBundleStateFile state,
        Func<string, ActionFitContentBundleEmbeddedPackage> embeddedResolver)
    {
        ValidateProfile(profile);
        ActionFitPackageManifestUtility.Validate(manifest);
        state ??= new ActionFitContentBundleStateFile();
        state.bundles ??= Array.Empty<ActionFitContentBundleRecord>();
        var changes = new List<ActionFitContentBundleChange>();
        var conflicts = new List<string>();

        foreach (ActionFitContentBundlePackageSpec package in profile.packages)
        {
            ActionFitContentBundlePackageOwnership shared = state.bundles
                .Where(bundle => !string.Equals(bundle.bundleId, profile.bundleId, StringComparison.Ordinal))
                .SelectMany(bundle => bundle.packages ?? Array.Empty<ActionFitContentBundlePackageOwnership>())
                .FirstOrDefault(item => string.Equals(item.packageId, package.packageId, StringComparison.Ordinal));
            if (shared != null && !string.Equals(shared.targetValue, package.gitUrl, StringComparison.Ordinal))
            {
                conflicts.Add($"{package.packageId} is owned by another bundle with a different Git pin.");
                changes.Add(Change(package.packageId, shared.targetValue, package.gitUrl, ActionFitContentBundleChangeKind.Conflict, conflicts[^1]));
                continue;
            }

            ActionFitContentBundleEmbeddedPackage embedded = embeddedResolver(package.packageId);
            if (embedded.exists)
            {
                if (CompareVersions(embedded.version, package.version) < 0)
                {
                    string detail = $"Embedded {package.packageId}@{embedded.version} is older than required {package.version}.";
                    conflicts.Add(detail);
                    changes.Add(Change(package.packageId, $"embedded@{embedded.version}", package.gitUrl, ActionFitContentBundleChangeKind.Conflict, detail));
                }
                else
                {
                    changes.Add(Change(package.packageId, $"embedded@{embedded.version}", "preserve", ActionFitContentBundleChangeKind.PreserveEmbedded, "Compatible embedded package is preserved."));
                }
                continue;
            }

            string current = ActionFitPackageManifestUtility.GetDependency(manifest, package.packageId);
            if (string.IsNullOrWhiteSpace(current))
            {
                changes.Add(Change(package.packageId, "", package.gitUrl, ActionFitContentBundleChangeKind.Add, "Missing dependency will be added."));
                continue;
            }

            if (string.Equals(current, package.gitUrl, StringComparison.Ordinal))
            {
                changes.Add(Change(package.packageId, current, current, ActionFitContentBundleChangeKind.Preserve, "Exact Git pin is already installed."));
                continue;
            }

            if (current.StartsWith("file:", StringComparison.OrdinalIgnoreCase))
            {
                string detail = $"Local dependency has no valid physical embedded package: {package.packageId} -> {current}";
                conflicts.Add(detail);
                changes.Add(Change(package.packageId, current, package.gitUrl, ActionFitContentBundleChangeKind.Conflict, detail));
                continue;
            }

            if (SameRepository(current, package.gitUrl) &&
                TryVersionFromGitUrl(current, out string currentVersion))
            {
                if (CompareVersions(currentVersion, package.version) < 0)
                {
                    changes.Add(Change(package.packageId, current, package.gitUrl, ActionFitContentBundleChangeKind.Update, "Older canonical tag will be updated."));
                }
                else
                {
                    changes.Add(Change(package.packageId, current, current, ActionFitContentBundleChangeKind.Preserve, "Equal or newer canonical tag is preserved."));
                }
                continue;
            }

            string conflict = $"Non-canonical, forked, branch, or unparseable dependency is preserved and blocks installation: {package.packageId} -> {current}";
            conflicts.Add(conflict);
            changes.Add(Change(package.packageId, current, package.gitUrl, ActionFitContentBundleChangeKind.Conflict, conflict));
        }

        return new ActionFitContentBundlePlan
        {
            success = conflicts.Count == 0,
            code = conflicts.Count == 0 ? "INSTALL_READY" : "INSTALL_CONFLICT",
            message = conflicts.Count == 0
                ? $"Content bundle install is ready: {profile.bundleId}@{profile.bundleVersion}"
                : $"Content bundle install has {conflicts.Count} conflict(s).",
            bundleId = profile.bundleId,
            bundleVersion = profile.bundleVersion,
            authorized = false,
            changes = changes.ToArray(),
            conflicts = conflicts.ToArray(),
            requiredPackageIds = profile.packages.Where(package => package.required).Select(package => package.packageId).ToArray(),
        };
    }

    public static ActionFitContentBundlePlan PlanRelease(
        ActionFitContentBundleRecord record,
        ActionFitContentBundleStateFile state,
        string manifest,
        Func<string, ActionFitContentBundleEmbeddedPackage> embeddedResolver)
    {
        if (record == null) throw new ArgumentNullException(nameof(record));
        state ??= new ActionFitContentBundleStateFile();
        state.bundles ??= Array.Empty<ActionFitContentBundleRecord>();
        ActionFitPackageManifestUtility.Validate(manifest);
        var changes = new List<ActionFitContentBundleChange>();
        var conflicts = new List<string>();
        foreach (ActionFitContentBundlePackageOwnership package in
                 record.packages ?? Array.Empty<ActionFitContentBundlePackageOwnership>())
        {
            bool shared = state.bundles
                .Where(bundle => !string.Equals(bundle.bundleId, record.bundleId, StringComparison.Ordinal))
                .SelectMany(bundle => bundle.packages ?? Array.Empty<ActionFitContentBundlePackageOwnership>())
                .Any(item => string.Equals(item.packageId, package.packageId, StringComparison.Ordinal));
            if (!package.removeOnRelease || shared)
            {
                changes.Add(Change(package.packageId, package.appliedValue, package.appliedValue, ActionFitContentBundleChangeKind.Preserve,
                    !package.removeOnRelease ? "Package is configured to remain after release." : "Package remains owned by another bundle."));
                continue;
            }

            ActionFitContentBundleEmbeddedPackage embedded = embeddedResolver(package.packageId);
            if (embedded.exists)
            {
                changes.Add(Change(package.packageId, $"embedded@{embedded.version}", "preserve", ActionFitContentBundleChangeKind.PreserveEmbedded, "Release never deletes embedded package folders."));
                continue;
            }

            string current = ActionFitPackageManifestUtility.GetDependency(manifest, package.packageId);
            if (string.IsNullOrWhiteSpace(current))
            {
                changes.Add(Change(package.packageId, "", "", ActionFitContentBundleChangeKind.Preserve, "Dependency is already absent."));
                continue;
            }

            bool matchesOwnedValue = string.Equals(current, package.appliedValue, StringComparison.Ordinal) ||
                                     string.Equals(current, package.targetValue, StringComparison.Ordinal);
            if (!matchesOwnedValue)
            {
                changes.Add(Change(package.packageId, current, current, ActionFitContentBundleChangeKind.Preserve, "User-modified dependency is preserved."));
                continue;
            }

            if (string.IsNullOrWhiteSpace(package.originalValue))
            {
                changes.Add(Change(package.packageId, current, "", ActionFitContentBundleChangeKind.Remove, "Exclusively owned dependency will be removed."));
            }
            else
            {
                changes.Add(Change(package.packageId, current, package.originalValue, ActionFitContentBundleChangeKind.Restore, "Original dependency value will be restored."));
            }
        }

        return new ActionFitContentBundlePlan
        {
            success = conflicts.Count == 0,
            code = conflicts.Count == 0 ? "RELEASE_READY" : "RELEASE_CONFLICT",
            message = conflicts.Count == 0 ? $"Content bundle release is ready: {record.bundleId}" : "Content bundle release has conflicts.",
            bundleId = record.bundleId,
            bundleVersion = record.bundleVersion,
            authorized = true,
            changes = changes.ToArray(),
            conflicts = conflicts.ToArray(),
            requiredPackageIds = (record.packages ?? Array.Empty<ActionFitContentBundlePackageOwnership>())
                .Where(package => package.required).Select(package => package.packageId).ToArray(),
        };
    }

    public static ActionFitContentBundlePlan PlanReconcile(
        ActionFitContentBundleRecord record,
        string manifest,
        Func<string, ActionFitContentBundleEmbeddedPackage> embeddedResolver)
    {
        if (record == null) throw new ArgumentNullException(nameof(record));
        ActionFitPackageManifestUtility.Validate(manifest);
        var changes = new List<ActionFitContentBundleChange>();
        var conflicts = new List<string>();
        foreach (ActionFitContentBundlePackageOwnership package in
                 (record.packages ?? Array.Empty<ActionFitContentBundlePackageOwnership>()).Where(item => item.required))
        {
            ActionFitContentBundleEmbeddedPackage embedded = embeddedResolver(package.packageId);
            if (embedded.exists && CompareVersions(embedded.version, package.version) >= 0)
            {
                changes.Add(Change(package.packageId, $"embedded@{embedded.version}", "preserve", ActionFitContentBundleChangeKind.PreserveEmbedded, "Required embedded package is available."));
                continue;
            }

            string current = ActionFitPackageManifestUtility.GetDependency(manifest, package.packageId);
            if (string.IsNullOrWhiteSpace(current))
            {
                string restoreValue = string.IsNullOrWhiteSpace(package.appliedValue)
                    ? package.targetValue
                    : package.appliedValue;
                changes.Add(Change(package.packageId, "", restoreValue, ActionFitContentBundleChangeKind.Add, "Missing required dependency will be restored."));
                continue;
            }

            if (string.Equals(current, package.appliedValue, StringComparison.Ordinal) ||
                string.Equals(current, package.targetValue, StringComparison.Ordinal))
            {
                changes.Add(Change(package.packageId, current, current, ActionFitContentBundleChangeKind.Preserve, "Required dependency is present."));
                continue;
            }

            string conflict = $"Required package has a user-managed or conflicting value and was not overwritten: {package.packageId} -> {current}";
            conflicts.Add(conflict);
            changes.Add(Change(package.packageId, current, current, ActionFitContentBundleChangeKind.Conflict, conflict));
        }

        return new ActionFitContentBundlePlan
        {
            success = conflicts.Count == 0,
            code = conflicts.Count == 0 ? "RECONCILE_READY" : "RECONCILE_CONFLICT",
            message = conflicts.Count == 0 ? "Required package reconciliation is ready." : "Required package reconciliation has conflicts.",
            bundleId = record.bundleId,
            bundleVersion = record.bundleVersion,
            changes = changes.ToArray(),
            conflicts = conflicts.ToArray(),
            requiredPackageIds = (record.packages ?? Array.Empty<ActionFitContentBundlePackageOwnership>())
                .Where(package => package.required).Select(package => package.packageId).ToArray(),
        };
    }

    public static string ApplyPlan(string manifest, ActionFitContentBundlePlan plan)
    {
        if (plan == null || !plan.success) return manifest;
        string updated = manifest;
        foreach (ActionFitContentBundleChange change in plan.changes)
        {
            switch (change.kind)
            {
                case ActionFitContentBundleChangeKind.Add:
                case ActionFitContentBundleChangeKind.Update:
                case ActionFitContentBundleChangeKind.Restore:
                    updated = ActionFitPackageManifestUtility.SetDependency(updated, change.packageId, change.to);
                    break;
                case ActionFitContentBundleChangeKind.Remove:
                    updated = ActionFitPackageManifestUtility.RemoveDependency(updated, change.packageId, out _);
                    break;
            }
        }
        return updated;
    }

    public static bool IsReleaseAuthorized(ActionFitContentBundleProfile profile, string login)
    {
        if (profile == null || string.IsNullOrWhiteSpace(login)) return false;
        return (profile.allowedReleaseGitHubLogins ?? Array.Empty<string>()).Any(allowed =>
            string.Equals(allowed?.Trim(), login.Trim(), StringComparison.OrdinalIgnoreCase));
    }

    public static int CompareVersions(string left, string right)
    {
        int[] leftParts = ParseVersion(left);
        int[] rightParts = ParseVersion(right);
        for (int index = 0; index < Math.Max(leftParts.Length, rightParts.Length); index++)
        {
            int leftValue = index < leftParts.Length ? leftParts[index] : 0;
            int rightValue = index < rightParts.Length ? rightParts[index] : 0;
            int comparison = leftValue.CompareTo(rightValue);
            if (comparison != 0) return comparison;
        }
        return 0;
    }

    private static int[] ParseVersion(string version)
    {
        return (version ?? "").Split('.')
            .Select(part => new string(part.TakeWhile(char.IsDigit).ToArray()))
            .Select(part => int.TryParse(part, out int value) ? value : 0)
            .ToArray();
    }

    private static bool SameRepository(string left, string right)
    {
        return string.Equals(
            NormalizeRepository(RepositoryPart(left)),
            NormalizeRepository(RepositoryPart(right)),
            StringComparison.OrdinalIgnoreCase);
    }

    private static bool TryVersionFromGitUrl(string gitUrl, out string version)
    {
        version = RevisionPart(gitUrl);
        return !string.IsNullOrWhiteSpace(version) &&
               Regex.IsMatch(version, "^[0-9]+(?:\\.[0-9]+){1,3}(?:[-+][A-Za-z0-9.-]+)?$", RegexOptions.CultureInvariant);
    }

    private static string RepositoryPart(string gitUrl)
    {
        int hash = (gitUrl ?? "").LastIndexOf('#');
        return hash < 0 ? gitUrl ?? "" : gitUrl[..hash];
    }

    private static string RevisionPart(string gitUrl)
    {
        int hash = (gitUrl ?? "").LastIndexOf('#');
        return hash < 0 || hash == gitUrl.Length - 1 ? "" : gitUrl[(hash + 1)..];
    }

    private static string NormalizeRepository(string repository)
    {
        string normalized = (repository ?? "").Trim().TrimEnd('/');
        return normalized.EndsWith(".git", StringComparison.OrdinalIgnoreCase)
            ? normalized[..^4]
            : normalized;
    }

    private static void ValidateId(string value, string label)
    {
        if (string.IsNullOrWhiteSpace(value) || !IdPattern.IsMatch(value))
            throw new InvalidOperationException($"Invalid {label}: {value}");
    }

    private static ActionFitContentBundleChange Change(
        string packageId,
        string from,
        string to,
        ActionFitContentBundleChangeKind kind,
        string detail)
    {
        return new ActionFitContentBundleChange
        {
            packageId = packageId,
            from = from ?? "",
            to = to ?? "",
            kind = kind,
            detail = detail ?? "",
        };
    }
}

internal static class ActionFitContentBundleEnvironment
{
    public static ActionFitContentBundleEmbeddedPackage GetEmbeddedPackage(string packageId)
    {
        string packagePath = ActionFitPackagePaths.PackagePath(packageId);
        if (!ActionFitPackageFileUtility.PhysicalDirectoryExists(packagePath))
            return default;

        try
        {
            ActionFitPackageManifest manifest = ActionFitPackageManifest.Read(Path.Combine(packagePath, "package.json"));
            return new ActionFitContentBundleEmbeddedPackage(true, manifest.Version);
        }
        catch
        {
            return default;
        }
    }

    public static bool AreRequiredPackagesReady(ActionFitContentBundleProfile profile, out string message)
    {
        UnityEditor.PackageManager.PackageInfo[] registered;
        try
        {
            registered = UnityEditor.PackageManager.PackageInfo.GetAllRegisteredPackages();
        }
        catch (Exception exception)
        {
            message = $"Unity Package Manager registration is not ready: {exception.Message}";
            return false;
        }

        var missing = new List<string>();
        foreach (ActionFitContentBundlePackageSpec required in profile.packages.Where(package => package.required))
        {
            UnityEditor.PackageManager.PackageInfo package = registered.FirstOrDefault(item =>
                string.Equals(item.name, required.packageId, StringComparison.Ordinal));
            if (package == null || ActionFitContentBundlePlanner.CompareVersions(package.version, required.version) < 0)
                missing.Add($"{required.packageId}@{required.version}");
        }

        message = missing.Count == 0
            ? "Every required package is registered."
            : $"Waiting for required packages: {string.Join(", ", missing)}";
        return missing.Count == 0;
    }
}

internal readonly struct ActionFitContentBundleEmbeddedPackage
{
    public ActionFitContentBundleEmbeddedPackage(bool exists, string version)
    {
        this.exists = exists;
        this.version = version ?? "";
    }

    public readonly bool exists;
    public readonly string version;
}

internal static class ActionFitContentBundleGitHubIdentity
{
    private static bool _resolved;
    private static string _login = "";

    public static string GetCurrentLogin()
    {
        if (_resolved) return _login;
        _resolved = true;

        try
        {
            var startInfo = new ProcessStartInfo
            {
                FileName = "gh",
                Arguments = "api user --jq .login",
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
            };
            using Process process = Process.Start(startInfo);
            if (process == null || !process.WaitForExit(5000) || process.ExitCode != 0) return "";
            string output = process.StandardOutput.ReadToEnd().Trim();
            _login = Regex.IsMatch(output, "^[A-Za-z0-9-]+$", RegexOptions.CultureInvariant) ? output : "";
        }
        catch
        {
            _login = "";
        }

        return _login;
    }
}

[Serializable]
internal sealed class ActionFitContentBundleStateFile
{
    public int schemaVersion = 1;
    public ActionFitContentBundleRecord[] bundles = Array.Empty<ActionFitContentBundleRecord>();
}

[Serializable]
internal sealed class ActionFitContentBundleRecord
{
    public string bundleId = "";
    public string bundleVersion = "";
    public string displayName = "";
    public string bootstrapPackageId = "";
    public string profileJson = "";
    public string installedUtc = "";
    public ActionFitContentBundlePackageOwnership[] packages = Array.Empty<ActionFitContentBundlePackageOwnership>();
}

[Serializable]
internal sealed class ActionFitContentBundlePackageOwnership
{
    public string packageId = "";
    public string version = "";
    public string targetValue = "";
    public string originalValue = "";
    public string appliedValue = "";
    public bool required;
    public bool removeOnRelease;
}

internal static class ActionFitContentBundleStateStore
{
    private const string RelativePath = "ProjectSettings/ActionFitContentBundles.json";
    public static string Path => ActionFitPackagePaths.ProjectRelativeFullPath(RelativePath);

    public static ActionFitContentBundleStateFile Load()
    {
        if (!File.Exists(Path)) return new ActionFitContentBundleStateFile();
        string json = File.ReadAllText(Path);
        if (string.IsNullOrWhiteSpace(json)) return new ActionFitContentBundleStateFile();
        ActionFitContentBundleStateFile state = JsonUtility.FromJson<ActionFitContentBundleStateFile>(json);
        if (state == null || state.schemaVersion != 1)
            throw new InvalidOperationException("ActionFitContentBundles.json has an unsupported schema.");
        state.bundles ??= Array.Empty<ActionFitContentBundleRecord>();
        foreach (ActionFitContentBundleRecord record in state.bundles)
            record.packages ??= Array.Empty<ActionFitContentBundlePackageOwnership>();
        return state;
    }

    public static void Save(ActionFitContentBundleStateFile state)
    {
        WriteJson(Serialize(state));
    }

    public static string Serialize(ActionFitContentBundleStateFile state)
    {
        return JsonUtility.ToJson(state ?? new ActionFitContentBundleStateFile(), true) + "\n";
    }

    public static void WriteJson(string json)
    {
        ActionFitContentBundleStateFile parsed = JsonUtility.FromJson<ActionFitContentBundleStateFile>(json);
        if (parsed == null || parsed.schemaVersion != 1)
            throw new InvalidOperationException("Content bundle ownership state is invalid.");
        ActionFitPackageManifestUtility.WriteAtomic(Path, json, false);
    }
}

[Serializable]
internal sealed class ActionFitContentBundleTransactionJournal
{
    public int schemaVersion = 1;
    public string operation = "";
    public string bundleId = "";
    public string profileJson = "";
    public string originalManifest = "";
    public string updatedManifest = "";
    public string originalStateJson = "";
    public string updatedStateJson = "";
    public string[] affectedPackageIds = Array.Empty<string>();
    public string phase = "";
}

internal enum ActionFitContentBundleOperation
{
    Install,
    Release,
}

internal enum ActionFitContentBundleTransactionPhase
{
    Prepared,
    ManifestCommitted,
    StateCommitted,
}

internal static class ActionFitContentBundleJournalStore
{
    private const string RelativeRoot = "UserSettings/ActionFitPackageManager/ContentBundleTransactions";

    public static string PathFor(string bundleId)
    {
        string root = ActionFitPackagePaths.ProjectRelativeFullPath(RelativeRoot);
        Directory.CreateDirectory(root);
        return System.IO.Path.Combine(root, bundleId + ".json");
    }

    public static string Save(ActionFitContentBundleTransactionJournal journal)
    {
        string path = PathFor(journal.bundleId);
        Save(journal, path);
        return path;
    }

    public static void Save(ActionFitContentBundleTransactionJournal journal, string path)
    {
        ActionFitPackageManifestUtility.WriteAtomic(path, JsonUtility.ToJson(journal, true) + "\n", false);
    }

    public static ActionFitContentBundleTransactionJournal Load(string path)
    {
        ActionFitContentBundleTransactionJournal journal = JsonUtility.FromJson<ActionFitContentBundleTransactionJournal>(File.ReadAllText(path));
        if (journal == null || journal.schemaVersion != 1 || string.IsNullOrWhiteSpace(journal.bundleId))
            throw new InvalidOperationException($"Invalid content bundle transaction journal: {path}");
        journal.affectedPackageIds ??= Array.Empty<string>();
        return journal;
    }

    public static (string Path, ActionFitContentBundleTransactionJournal Journal)[] LoadAll()
    {
        string root = ActionFitPackagePaths.ProjectRelativeFullPath(RelativeRoot);
        if (!Directory.Exists(root)) return Array.Empty<(string, ActionFitContentBundleTransactionJournal)>();
        var results = new List<(string, ActionFitContentBundleTransactionJournal)>();
        foreach (string path in Directory.GetFiles(root, "*.json", SearchOption.TopDirectoryOnly).OrderBy(item => item, StringComparer.Ordinal))
        {
            try
            {
                results.Add((path, Load(path)));
            }
            catch (Exception exception)
            {
                Debug.LogError($"[ActionFitContentBundle] Journal: {exception.Message}");
            }
        }
        return results.ToArray();
    }

    public static void Delete(string path)
    {
        if (File.Exists(path)) File.Delete(path);
    }
}

[InitializeOnLoad]
internal static class ActionFitContentBundleRecoveryBootstrap
{
    private const string ReconcileSessionKey = "ActionFit.ContentBundle.Reconcile.1";

    static ActionFitContentBundleRecoveryBootstrap()
    {
        EditorApplication.delayCall += RecoverAndReconcile;
        Events.registeredPackages += OnRegisteredPackages;
    }

    private static void OnRegisteredPackages(PackageRegistrationEventArgs _)
    {
        EditorApplication.delayCall += RecoverAndReconcileAfterRegistration;
    }

    private static void RecoverAndReconcile()
    {
        if (!Application.isBatchMode) RecoverPending();
        if (SessionState.GetBool(ReconcileSessionKey, false)) return;
        SessionState.SetBool(ReconcileSessionKey, true);
        ActionFitContentBundleApi.ReconcileActiveBundles();
    }

    private static void RecoverAndReconcileAfterRegistration()
    {
        if (!Application.isBatchMode) RecoverPending();
        ActionFitContentBundleApi.ReconcileActiveBundles();
    }

    private static void RecoverPending()
    {
        foreach (ActionFitContentBundleResult result in ActionFitContentBundleApi.Recover())
        {
            if (result.recoveryRequired)
                Debug.LogError($"[ActionFitContentBundle] Recover: {result.code} - {result.message}\nJournal: {result.journalPath}");
            else if (!result.pending && result.success)
                Debug.Log($"[ActionFitContentBundle] Recover: {result.message}");
        }
    }
}
#endif
