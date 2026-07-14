#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using UnityEditor;
using UnityEngine;

public class ActionFitPackagePublishWindow : EditorWindow
{
    private enum Mode
    {
        Create,
        Update,
        Changed,
    }

    private const string PackageCatalogPath = "Packages/com.actionfit.custompackagemanager/Editor/Catalog/package_catalog.csv";
    private static string ProjectRootPath => Path.GetFullPath(Path.Combine(Application.dataPath, ".."));
    private static string ProjectRootNormalized => ProjectRootPath.Replace("\\", "/").TrimEnd('/');

    private static string ProjectRelativeFullPath(string relativePath)
    {
        return Path.Combine(ProjectRootPath, relativePath.Replace("/", Path.DirectorySeparatorChar.ToString()));
    }

    private readonly Dictionary<string, string> _versionByAssetPath = new();
    private readonly HashSet<string> _expandedPackageIds = new();
    private readonly List<Entry> _entries = new();
    private readonly List<ActionFitPackagePublisher.CatalogAppendItem> _pendingCatalogAppendItems = new();
    private Mode _mode;
    private Vector2 _scroll;
    private bool _delayedGuiActionQueued;

    public static void OpenCreate()
    {
        Open(Mode.Create);
    }

    public static void OpenUpdate()
    {
        Open(Mode.Update);
    }

    public static void OpenChanged()
    {
        Open(Mode.Changed);
    }

    private static void Open(Mode mode)
    {
        string title = mode switch
        {
            Mode.Create => "Initial Publish",
            Mode.Changed => "Publish Changed",
            _ => "Publish Package"
        };
        var window = GetWindow<ActionFitPackagePublishWindow>(title);
        window.minSize = new Vector2(720, 520);
        window._mode = mode;
        window.Reload();
        window.Show();
    }

    private void OnGUI()
    {
        DrawToolbar();

        string empty = _mode switch
        {
            Mode.Create => "No unregistered ActionFit package infos found.",
            Mode.Changed => "No changed package versions found. Local package.json versions are not higher than catalog latest versions.",
            _ => "No registered ActionFit package infos found."
        };

        if (_entries.Count == 0)
        {
            EditorGUILayout.HelpBox(empty, MessageType.Info);
            return;
        }

        var entries = _entries.ToArray();
        using var scroll = new EditorGUILayout.ScrollViewScope(_scroll);
        _scroll = scroll.scrollPosition;
        foreach (var entry in entries)
        {
            DrawEntry(entry);
            EditorGUILayout.Space(6);
        }
    }

    private void DrawToolbar()
    {
        using (new EditorGUILayout.HorizontalScope(EditorStyles.toolbar))
        {
            if (GUILayout.Button("Reload", EditorStyles.toolbarButton, GUILayout.Width(70))) Reload();
            if (_mode == Mode.Changed)
            {
                if (_pendingCatalogAppendItems.Count > 0)
                {
                    if (GUILayout.Button($"Retry Catalog Append ({_pendingCatalogAppendItems.Count})", EditorStyles.toolbarButton, GUILayout.Width(190)))
                        ScheduleGuiAction(RetryPendingCatalogAppend);
                }

                EditorGUI.BeginDisabledGroup(_entries.Count == 0);
                if (GUILayout.Button("Publish All Changed", EditorStyles.toolbarButton, GUILayout.Width(145)))
                    ScheduleGuiAction(PublishAllChanged);
                EditorGUI.EndDisabledGroup();
            }
            GUILayout.FlexibleSpace();
        }
    }

    private void DrawEntry(Entry entry)
    {
        using (new EditorGUILayout.VerticalScope(EditorStyles.helpBox))
        {
            using (new EditorGUILayout.HorizontalScope())
            {
                bool expanded = _expandedPackageIds.Contains(entry.PackageId);
                bool nextExpanded = EditorGUILayout.Foldout(expanded, entry.DisplayName, true, EditorStyles.foldoutHeader);
                if (nextExpanded) _expandedPackageIds.Add(entry.PackageId);
                else _expandedPackageIds.Remove(entry.PackageId);

                GUILayout.FlexibleSpace();
                EditorGUILayout.LabelField(entry.PackageId, EditorStyles.miniLabel, GUILayout.Width(260));
            }

            if (!_expandedPackageIds.Contains(entry.PackageId)) return;

            EditorGUILayout.LabelField("Package Root", entry.PackageRoot);
            EditorGUILayout.LabelField("Current Version", entry.Version);
            if (_mode == Mode.Changed)
                EditorGUILayout.LabelField("Catalog Latest", entry.CatalogLatestVersion);

            var serialized = new SerializedObject(entry.Info);
            serialized.Update();
            EditorGUILayout.PropertyField(serialized.FindProperty("_displayName"));
            EditorGUILayout.PropertyField(serialized.FindProperty("_repoName"));
            EditorGUILayout.PropertyField(serialized.FindProperty("_repositoryVisibility"));
            EditorGUILayout.PropertyField(serialized.FindProperty("_description"));
            EditorGUILayout.PropertyField(serialized.FindProperty("_releaseNote"));
            EditorGUILayout.PropertyField(serialized.FindProperty("_dependenciesOverride"));
            serialized.ApplyModifiedProperties();

            using (new EditorGUILayout.HorizontalScope())
            {
                if (_mode == Mode.Update)
                {
                    EditorGUILayout.LabelField("Publish Version", GUILayout.Width(100));
                    string value = GetVersion(entry);
                    string next = EditorGUILayout.TextField(value);
                    if (next != value) _versionByAssetPath[entry.AssetPath] = next;
                }
                else
                {
                    string label = _mode == Mode.Changed
                        ? entry.IsRegistered
                            ? $"Publish Version: {entry.Version} (catalog latest: {entry.CatalogLatestVersion})"
                            : $"Publish Version: {entry.Version} (new package)"
                        : $"Initial Version: {entry.Version}";
                    EditorGUILayout.LabelField(label, EditorStyles.miniLabel);
                }

                GUILayout.FlexibleSpace();
                if (GUILayout.Button("Select", GUILayout.Width(70)))
                {
                    Selection.activeObject = entry.Info;
                    EditorGUIUtility.PingObject(entry.Info);
                }

                string button = _mode switch
                {
                    Mode.Create => "Initial Publish",
                    Mode.Changed => "Publish Changed",
                    _ => "Publish Package"
                };
                if (GUILayout.Button(button, GUILayout.Width(140)))
                    ScheduleGuiAction(() => Publish(entry));
            }
        }
    }

    private void ScheduleGuiAction(Action action)
    {
        if (_delayedGuiActionQueued) return;
        _delayedGuiActionQueued = true;
        EditorApplication.delayCall += () =>
        {
            if (this == null) return;
            _delayedGuiActionQueued = false;
            action();
            Repaint();
        };
    }

    private void Publish(Entry entry)
    {
        if (_mode == Mode.Changed)
        {
            PublishChanged(entry);
            return;
        }

        string version = _mode == Mode.Update ? GetVersion(entry).Trim() : entry.Version;
        if (string.IsNullOrWhiteSpace(version))
        {
            EditorUtility.DisplayDialog("ActionFit Package Manager", "Publish Version is empty.", "OK");
            return;
        }

        if (!TryRefreshCatalogBeforePublish(out var settings, out var catalog, out string preflightError))
        {
            EditorUtility.DisplayDialog("ActionFit Package Manager", preflightError, "OK");
            ScheduleReload();
            return;
        }

        if (!ValidateVersionAgainstCatalog(entry, version, catalog, out string versionError))
        {
            EditorUtility.DisplayDialog("ActionFit Package Manager", versionError, "OK");
            ScheduleReload();
            return;
        }

        string action = _mode == Mode.Create ? "Create repo" : "Publish package";
        bool isRegisteredNow = catalog.IsRegistered(entry.PackageId);
        string visibilityLine = _mode == Mode.Create || (_mode == Mode.Changed && !isRegisteredNow)
            ? $"\nNew repository visibility: {GetRepositoryVisibilityLabel(entry.Info.RepositoryVisibility)}"
            : "";
        if (!EditorUtility.DisplayDialog(
                "ActionFit Package Manager",
                $"{action}: {entry.PackageId}@{version}?{visibilityLine}\n\nThe catalog was refreshed before this check. This will create/check the GitHub repository, prepare the local publish clone, push package contents, push the version tag, and append the catalog spreadsheet.",
                _mode == Mode.Create ? "Initial Publish" : "Publish",
                "Cancel"))
            return;

        if (_mode == Mode.Update)
        {
            if (!SetPackageJsonVersion(entry.PackageJsonPath, version, out string versionWriteError))
            {
                EditorUtility.DisplayDialog("ActionFit Package Manager", versionWriteError, "OK");
                return;
            }
            AssetDatabase.ImportAsset(entry.PackageJsonPath, ImportAssetOptions.ForceUpdate);
            PublishChanged(entry);
            return;
        }

        if (!PublishInternal(entry, version, true, settings, catalog, out string error))
            EditorUtility.DisplayDialog("ActionFit Package Manager", error, "OK");
    }

    private void PublishChanged(Entry entry)
    {
        ActionFitPackagePublishPlan plan = ActionFitPackagePublishApi.Prepare(
            new ActionFitPackagePublishPrepareRequest
            {
                PackageId = entry.PackageId,
                RefreshCatalog = true,
                CheckRemoteState = true,
            });
        if (!plan.Success || !plan.ReadyToPublish)
        {
            Debug.LogWarning($"[ActionFitPackageManager] Publish preflight failed: {plan.Message}");
            EditorUtility.DisplayDialog("ActionFit Package Manager", plan.Message, "OK");
            ScheduleReload();
            return;
        }

        if (plan.RepositoryMigrationRequired &&
            !EditorUtility.DisplayDialog(
                "Approve Repository Migration",
                $"Move catalog history to the selected Private repository?\n\n" +
                $"Source ({plan.SourceRepositoryVisibility}): {plan.SourceRepositoryUrl}\n" +
                $"Target (Private): {plan.RepositoryUrl}\n" +
                $"Branches: {plan.SourceBranchCount}, tags: {plan.SourceTagCount}, missing refs: {plan.MissingRepositoryRefCount}\n\n" +
                "All source branches and tags will be copied and verified. The source repository will remain unchanged. " +
                "The catalog URL changes only after migration verification and package publish both succeed.\n\n" +
                $"Migration approval: {plan.RequiredMigrationApprovalText}",
                "Approve Migration",
                "Cancel"))
        {
            return;
        }

        if (!EditorUtility.DisplayDialog(
                "ActionFit Package Manager",
                $"Publish changed package?\n\n- {plan.PackageId}@{plan.Version}\n- {plan.RepositoryUrl}\n\n" +
                $"Plan: {plan.PlanId}\nApproval: {plan.RequiredApprovalText}",
                "Publish Changed",
                "Cancel"))
        {
            return;
        }

        ActionFitPackagePublishExecutionResult result = ActionFitPackagePublishApi.Execute(
            new ActionFitPackagePublishExecuteRequest
            {
                PackageId = plan.PackageId,
                ExpectedPlanId = plan.PlanId,
                ApprovalText = plan.RequiredApprovalText,
                ApproveRepositoryCreation = plan.WillCreateRepository,
                ApproveRepositoryMigration = plan.RepositoryMigrationRequired,
                MigrationApprovalText = plan.RequiredMigrationApprovalText,
                ApprovedPlan = plan,
            });
        if (!result.Success)
        {
            Debug.LogError($"[ActionFitPackageManager] Publish failed: {result.Message}");
            EditorUtility.DisplayDialog("ActionFit Package Manager", result.Message, "OK");
            ScheduleReload();
            return;
        }

        Debug.Log($"[ActionFitPackageManager] Publish complete: {result.Message}");
        foreach (string warning in result.Warnings ?? Array.Empty<string>())
            Debug.LogWarning($"[ActionFitPackageManager] {warning}");
        ScheduleReload();
    }

    private void PublishAllChanged()
    {
        var targets = _entries.ToArray();
        if (targets.Length == 0) return;

        ActionFitPackageBulkPublishPlan plan;
        using (var preflightCancellation = new CancellationTokenSource())
        {
            try
            {
                plan = ActionFitPackageBulkPublishApi.PrepareAllChanged(
                    new ActionFitPackageBulkPublishPrepareRequest
                    {
                        PackageIds = targets.Select(entry => entry.PackageId).ToArray(),
                        RefreshCatalog = true,
                        CheckRemoteState = true,
                    },
                    CreateProgressReporter(preflightCancellation),
                    preflightCancellation.Token);
            }
            finally
            {
                EditorUtility.ClearProgressBar();
            }
        }
        if (!plan.Success || !plan.ReadyToPublish)
        {
            Debug.LogWarning($"[ActionFitPackageManager] Bulk publish preflight failed: {plan.Message}");
            EditorUtility.DisplayDialog("ActionFit Package Manager", plan.Message, "OK");
            ScheduleReload();
            return;
        }

        string list = string.Join("\n", plan.Packages.Select(item =>
            $"- {item.PackageId}@{item.Version}: " +
            (item.WillCreateRepository
                ? $"create {item.RepositoryVisibility} {item.GitHubOrganization}/{item.RepositoryName}"
                : $"publish to {item.GitHubOrganization}/{item.RepositoryName}") +
            (item.RepositoryMigrationRequired ? $" (migrate from {item.SourceRepositoryUrl})" : "")));
        if (plan.RepositoryMigrationPackageIds.Length > 0 &&
            !EditorUtility.DisplayDialog(
                "Approve Repository Migrations",
                $"Migrate {plan.RepositoryMigrationPackageIds.Length} package repository/repositories before publishing?\n\n" +
                string.Join("\n", plan.Packages
                    .Where(item => item.RepositoryMigrationRequired)
                    .Select(item => $"- {item.PackageId}: {item.SourceRepositoryUrl} -> {item.RepositoryUrl} " +
                                    $"({item.SourceBranchCount} branches, {item.SourceTagCount} tags)")) +
                "\n\nSource repositories remain unchanged. Catalog rows are not changed unless all migrations and publishes succeed.\n\n" +
                $"Migration approval: {plan.RequiredMigrationApprovalText}",
                "Approve Migrations",
                "Cancel"))
        {
            return;
        }
        if (!EditorUtility.DisplayDialog(
                "ActionFit Package Manager",
                $"Publish all changed packages?\n\n{list}\n\nPlan: {plan.PlanId}\nApproval: {plan.RequiredApprovalText}\n\nThe catalog and GitHub state were refreshed before this check. Repository publish runs up to {plan.MaxParallelPublishes} packages at once, then catalog rows are appended in one batch request.",
                "Publish All Changed",
                "Cancel"))
            return;

        ActionFitPackageBulkPublishExecutionResult result;
        using (var executionCancellation = new CancellationTokenSource())
        {
            try
            {
                result = ActionFitPackageBulkPublishApi.ExecuteAll(
                    new ActionFitPackageBulkPublishExecuteRequest
                    {
                        PackageIds = plan.PackageIds,
                        ExpectedPlanId = plan.PlanId,
                        ApprovalText = plan.RequiredApprovalText,
                        ApprovedRepositoryCreationPackageIds = plan.RepositoryCreationPackageIds,
                        ApprovedRepositoryMigrationPackageIds = plan.RepositoryMigrationPackageIds,
                        MigrationApprovalText = plan.RequiredMigrationApprovalText,
                        ApprovedPlan = plan,
                    },
                    CreateProgressReporter(executionCancellation),
                    executionCancellation.Token);
            }
            finally
            {
                EditorUtility.ClearProgressBar();
            }
        }

        _pendingCatalogAppendItems.Clear();
        if (result.RetryCatalogItems != null)
            _pendingCatalogAppendItems.AddRange(result.RetryCatalogItems);

        if (!result.Success)
        {
            string message = result.Message;
            if (result.RetryCatalogAppendAvailable)
                message += "\n\nRepository-published rows were kept in this window. Use Retry Catalog Append without publishing repositories again.";
            Debug.LogError($"[ActionFitPackageManager] {message}");
            EditorUtility.DisplayDialog("ActionFit Package Manager", message, "OK");
            ScheduleReload();
            return;
        }

        Debug.Log($"[ActionFitPackageManager] Bulk publish complete: {result.Message}");
        foreach (string warning in result.Warnings ?? Array.Empty<string>())
            Debug.LogWarning($"[ActionFitPackageManager] {warning}");
        ScheduleReload();
    }

    private static Action<ActionFitPackageBulkPublishProgress> CreateProgressReporter(
        CancellationTokenSource cancellation)
    {
        return progress =>
        {
            if (progress == null) return;
            if (EditorUtility.DisplayCancelableProgressBar(
                    "ActionFit Package Publish",
                    progress.Message,
                    Mathf.Clamp01(progress.Fraction)))
            {
                cancellation.Cancel();
            }
        };
    }

    private void RetryPendingCatalogAppend()
    {
        if (_pendingCatalogAppendItems.Count == 0) return;

        var items = _pendingCatalogAppendItems.ToList();
        string list = string.Join("\n", items.Select(item => $"- {item.CatalogId}"));
        if (!EditorUtility.DisplayDialog(
                "ActionFit Package Manager",
                $"Retry catalog append for repository-published packages?\n\n{list}",
                "Retry Catalog Append",
                "Cancel"))
            return;

        if (!AppendCatalogRowsWithFallback(items, out string appendMessage))
        {
            Debug.LogError($"[ActionFitPackageManager] Catalog append retry failed:\n{appendMessage}");
            EditorUtility.DisplayDialog("ActionFit Package Manager", appendMessage, "OK");
            return;
        }

        _pendingCatalogAppendItems.Clear();
        Debug.Log($"[ActionFitPackageManager] Catalog append retry complete: {appendMessage}");

        var settings = ActionFitPackageCatalogSettingsProvider.FindOrCreate();
        if (ActionFitPackageCatalogUpdater.UpdateCatalog(settings, out string updateMessage))
            Debug.Log($"[ActionFitPackageManager] {updateMessage}");
        else
            Debug.LogWarning($"[ActionFitPackageManager] Catalog refresh failed after catalog append retry: {updateMessage}");

        ScheduleReload();
    }

    private static List<ActionFitPackagePublisher.PublishResult> PublishRepositoriesParallel(IReadOnlyList<ActionFitPackagePublisher.PublishRequest> requests)
    {
        int completed = 0;
        int total = requests.Count;
        var task = StartRepositoryPublishTasks(requests, () => Interlocked.Increment(ref completed));

        try
        {
            EditorUtility.DisplayProgressBar("ActionFit Package Publish", $"Publishing repositories... 0/{total}", 0f);
            while (!task.Wait(100))
            {
                float progress = total == 0 ? 1f : Mathf.Clamp01(completed / (float)total);
                EditorUtility.DisplayProgressBar("ActionFit Package Publish", $"Publishing repositories... {completed}/{total}", progress);
            }

            EditorUtility.DisplayProgressBar("ActionFit Package Publish", $"Publishing repositories... {total}/{total}", 1f);
            return task.Result.ToList();
        }
        finally
        {
            EditorUtility.ClearProgressBar();
        }
    }

    private static Task<ActionFitPackagePublisher.PublishResult[]> StartRepositoryPublishTasks(
        IReadOnlyList<ActionFitPackagePublisher.PublishRequest> requests,
        Action onCompleted)
    {
        var results = new ActionFitPackagePublisher.PublishResult[requests.Count];
        if (requests.Count == 0) return Task.FromResult(results);

        int nextIndex = -1;
        int workerCount = Math.Min(ActionFitPackagePublisher.DefaultMaxParallelPublishes, requests.Count);
        var workers = Enumerable.Range(0, workerCount)
            .Select(_ => Task.Run(() =>
            {
                while (true)
                {
                    int index = Interlocked.Increment(ref nextIndex);
                    if (index >= requests.Count) break;

                    var request = requests[index];
                    try
                    {
                        results[index] = ActionFitPackagePublisher.PublishRepository(request);
                    }
                    catch (Exception ex)
                    {
                        results[index] = ActionFitPackagePublisher.PublishResult.Failed(request, ex.Message);
                    }
                    finally
                    {
                        onCompleted?.Invoke();
                    }
                }
            }))
            .ToArray();

        return Task.WhenAll(workers).ContinueWith(_ => results);
    }

    private static bool AppendCatalogRowsWithFallback(IReadOnlyList<ActionFitPackagePublisher.CatalogAppendItem> items, out string message)
    {
        try
        {
            EditorUtility.DisplayProgressBar("ActionFit Package Publish", "Appending catalog rows by batch request...", 0.85f);
            if (ActionFitPackagePublisher.TryAppendCatalogBatch(items, out message))
                return true;

            string batchMessage = message;
            Debug.LogWarning($"[ActionFitPackageManager] Catalog batch append failed. Falling back to serial append.\n{batchMessage}");

            EditorUtility.DisplayProgressBar("ActionFit Package Publish", "Appending catalog rows one by one...", 0.95f);
            if (ActionFitPackagePublisher.TryAppendCatalogSerial(items, out string serialMessage))
            {
                message =
                    "Catalog batch append failed, but serial fallback succeeded.\n\n" +
                    $"Batch:\n{batchMessage}\n\nSerial:\n{serialMessage}";
                return true;
            }

            message =
                "Catalog append failed.\n\n" +
                $"Batch:\n{batchMessage}\n\nSerial fallback:\n{serialMessage}";
            return false;
        }
        finally
        {
            EditorUtility.ClearProgressBar();
        }
    }

    private bool PublishInternal(Entry entry, string version, bool refreshCatalog, ActionFitPackageCatalogSettings_SO settings, CatalogSnapshot catalog, out string error)
    {
        error = "";
        if (_mode == Mode.Update && !SetPackageJsonVersion(entry.PackageJsonPath, version, out error))
        {
            return false;
        }

        AssetDatabase.ImportAsset(entry.PackageJsonPath, ImportAssetOptions.ForceUpdate);
        bool ok;
        string message;
        ok = ActionFitPackagePublisher.Publish(settings, entry.Info, entry.Info.RepositoryVisibility, out message);
        if (ok)
        {
            Debug.Log($"[ActionFitPackageManager] {message}");
            if (refreshCatalog)
            {
                if (ActionFitPackageCatalogUpdater.UpdateCatalog(settings, out string updateMessage))
                    Debug.Log($"[ActionFitPackageManager] {updateMessage}");
                else
                    Debug.LogWarning($"[ActionFitPackageManager] Catalog refresh failed after publish: {updateMessage}");

                ScheduleReload();
            }

            return true;
        }

        error = message;
        Debug.LogError($"[ActionFitPackageManager] Publish failed: {message}");
        return false;
    }

    private static bool TryRefreshCatalogBeforePublish(out ActionFitPackageCatalogSettings_SO settings, out CatalogSnapshot catalog, out string error)
    {
        settings = ActionFitPackageCatalogSettingsProvider.FindOrCreate();
        catalog = CatalogSnapshot.Empty;
        error = "";

        if (!ActionFitPackageCatalogUpdater.UpdateCatalog(settings, out string message))
        {
            error =
                "Catalog refresh failed before publish.\n\n" +
                "Publishing was stopped so the version check does not use stale catalog data.\n\n" +
                message;
            Debug.LogWarning($"[ActionFitPackageManager] {error}");
            return false;
        }

        Debug.Log($"[ActionFitPackageManager] Catalog refreshed before publish: {message}");
        catalog = ReadCatalogSnapshot();
        return true;
    }

    private bool ValidateVersionAgainstCatalog(Entry entry, string version, CatalogSnapshot catalog, out string error)
    {
        error = "";

        bool isRegisteredNow = catalog.IsRegistered(entry.PackageId);
        if (_mode == Mode.Create && isRegisteredNow)
        {
            error =
                $"Package is already registered in the refreshed catalog: {entry.PackageId}\n\n" +
                "Reload the publish window and use Publish Changed or Publish Package for this package.";
            return false;
        }

        if (_mode == Mode.Update && !isRegisteredNow)
        {
            error =
                $"Package is not registered in the refreshed catalog: {entry.PackageId}\n\n" +
                "Reload the publish window and use Initial Publish if this is a new package.";
            return false;
        }

        bool versionAlreadyPublished = catalog.ContainsVersion(entry.PackageId, version);
        string latest = catalog.GetLatestVersion(entry.PackageId);
        if (!versionAlreadyPublished && _mode == Mode.Changed && isRegisteredNow && CompareVersions(version, latest) <= 0)
        {
            error =
                $"Publish Changed target is no longer newer than the refreshed catalog latest version: {entry.PackageId}\n\n" +
                $"Publish version: {version}\n" +
                $"Catalog latest: {latest}\n\n" +
                "Reload the publish window and bump package.json to a newer version before publishing.";
            return false;
        }

        if (!versionAlreadyPublished)
            return true;

        error =
            $"이미 업로드되어 있는 버전입니다: {entry.PackageId}@{version}\n\n" +
            "기본 정책상 기존 Git tag와 catalog version row를 덮어쓰지 않습니다.\n\n" +
            "진행 방법:\n" +
            "- package.json version 또는 Publish Version을 새 값으로 변경한 뒤 다시 publish합니다.\n" +
            "- 기존 패키지와 분리해야 한다면 Package Manager에서 Fork as New로 새 package/repo를 만든 뒤 publish합니다.";
        if (!string.IsNullOrWhiteSpace(latest))
            error += $"\n\nCatalog latest: {latest}";

        Debug.LogWarning($"[ActionFitPackageManager] Publish blocked because catalog already contains {entry.PackageId}@{version}");
        return false;
    }

    private static string GetRepositoryVisibilityLabel(ActionFitPackageRepositoryVisibility visibility)
        => visibility == ActionFitPackageRepositoryVisibility.Private ? "Private" : "Public";

    private static string BuildNewRepositoryVisibilitySummary(IEnumerable<Entry> entries, CatalogSnapshot catalog)
    {
        var lines = entries
            .Where(e => !catalog.IsRegistered(e.PackageId))
            .Select(e => $"- {e.PackageId}: {GetRepositoryVisibilityLabel(e.Info.RepositoryVisibility)}")
            .ToList();
        return lines.Count == 0 ? "" : $"\n\nNew repository visibility:\n{string.Join("\n", lines)}";
    }

    private static string GetCatalogVersionLabel(Entry entry, CatalogSnapshot catalog)
    {
        string latest = catalog.GetLatestVersion(entry.PackageId);
        if (!string.IsNullOrWhiteSpace(latest)) return latest;
        return entry.IsRegistered ? entry.CatalogLatestVersion : "not registered";
    }

    private static string GetCatalogVersionLabel(string packageId, CatalogSnapshot catalog, IEnumerable<Entry> entries)
    {
        var entry = entries.FirstOrDefault(e => string.Equals(e.PackageId, packageId, StringComparison.OrdinalIgnoreCase));
        if (entry != null) return GetCatalogVersionLabel(entry, catalog);

        string latest = catalog.GetLatestVersion(packageId);
        return string.IsNullOrWhiteSpace(latest) ? "not registered" : latest;
    }

    private static string FormatPublishResultList(IEnumerable<ActionFitPackagePublisher.PublishResult> results)
    {
        var lines = results
            .Where(r => r != null)
            .Select(r =>
            {
                string label = r.Request?.CatalogId ?? "unknown";
                if (r.Success) return $"- {label}";
                return $"- {label}\n  {r.Message}";
            })
            .ToList();

        return lines.Count == 0 ? "- none" : string.Join("\n", lines);
    }

    private string GetVersion(Entry entry)
    {
        if (_versionByAssetPath.TryGetValue(entry.AssetPath, out string version) && !string.IsNullOrWhiteSpace(version))
            return version;

        _versionByAssetPath[entry.AssetPath] = entry.Version;
        return entry.Version;
    }

    private void ScheduleReload()
    {
        EditorApplication.delayCall += () =>
        {
            if (this == null) return;
            Reload();
            Repaint();
        };
    }

    private void Reload()
    {
        _entries.Clear();
        var catalog = ReadCatalogSnapshot();
        var registered = catalog.LatestVersions;
        foreach (string packageJsonPath in FindTopLevelActionFitPackageJsonPaths(ProjectRelativeFullPath("Packages")))
        {
            string packageRoot = ToProjectRelativePath(Path.GetDirectoryName(packageJsonPath));

            string relativePackageJsonPath = Path.Combine(packageRoot, "package.json").Replace("\\", "/");
            var manifest = ActionFitPackageManifest.Read(relativePackageJsonPath);
            bool isRegistered = registered.ContainsKey(manifest.Name);
            if (_mode == Mode.Create && isRegistered) continue;
            if (_mode == Mode.Update && !isRegistered) continue;

            registered.TryGetValue(manifest.Name, out string catalogLatestVersion);
            if (_mode == Mode.Changed &&
                isRegistered &&
                CompareVersions(manifest.Version, catalogLatestVersion) <= 0)
                continue;

            var info = ActionFitPackageInfoUtility.CreateOrUpdate(packageRoot);
            if (info == null) continue;
            string assetPath = AssetDatabase.GetAssetPath(info);

            _entries.Add(new Entry
            {
                Info = info,
                AssetPath = assetPath,
                PackageRoot = packageRoot,
                PackageJsonPath = relativePackageJsonPath,
                PackageId = manifest.Name,
                DisplayName = string.IsNullOrWhiteSpace(manifest.DisplayName) ? info.DisplayName : manifest.DisplayName,
                Version = manifest.Version,
                CatalogLatestVersion = isRegistered ? catalogLatestVersion : "not registered",
                IsRegistered = isRegistered,
            });
        }

        _entries.Sort((a, b) => string.Compare(a.DisplayName, b.DisplayName, StringComparison.OrdinalIgnoreCase));
    }

    internal static string[] FindTopLevelActionFitPackageJsonPaths(string packagesPath)
    {
        if (string.IsNullOrWhiteSpace(packagesPath) || !Directory.Exists(packagesPath))
            return Array.Empty<string>();

        return Directory.GetDirectories(packagesPath, "com.actionfit.*", SearchOption.TopDirectoryOnly)
            .Select(packageRoot => Path.Combine(packageRoot, "package.json"))
            .Where(File.Exists)
            .ToArray();
    }

    private static string ToProjectRelativePath(string fullPath)
    {
        string normalized = Path.GetFullPath(fullPath).Replace("\\", "/");
        return normalized.StartsWith(ProjectRootNormalized + "/", StringComparison.Ordinal)
            ? normalized[(ProjectRootNormalized.Length + 1)..]
            : normalized;
    }

    private static CatalogSnapshot ReadCatalogSnapshot()
    {
        var latestVersions = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var versionsByPackageId = new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);
        string local = ActionFitPackageCatalogSettingsProvider.LocalCatalogPath;
        string catalogPath = File.Exists(ProjectRelativeFullPath(local)) ? local : PackageCatalogPath;
        string path = ProjectRelativeFullPath(catalogPath);
        if (!File.Exists(path)) return new CatalogSnapshot(latestVersions, versionsByPackageId);

        var rows = ReadCsvRows(path).ToList();
        if (rows.Count < 2) return new CatalogSnapshot(latestVersions, versionsByPackageId);

        var header = rows[0].Select(h => h.Trim()).ToArray();
        int packageIdIndex = Array.FindIndex(header, h => string.Equals(h, "package_id", StringComparison.OrdinalIgnoreCase));
        int versionIndex = Array.FindIndex(header, h => string.Equals(h, "version", StringComparison.OrdinalIgnoreCase));
        int isLatestIndex = Array.FindIndex(header, h => string.Equals(h, "is_latest", StringComparison.OrdinalIgnoreCase));
        if (packageIdIndex < 0) return new CatalogSnapshot(latestVersions, versionsByPackageId);

        for (int i = 1; i < rows.Count; i++)
        {
            string[] cols = rows[i];
            if (cols.Length == 0 || cols.All(string.IsNullOrWhiteSpace)) continue;
            if (cols.Any(c => c.Contains("(string)", StringComparison.OrdinalIgnoreCase))) continue;
            if (packageIdIndex >= cols.Length || string.IsNullOrWhiteSpace(cols[packageIdIndex])) continue;

            string packageId = cols[packageIdIndex].Trim();
            string version = versionIndex >= 0 && versionIndex < cols.Length ? cols[versionIndex].Trim() : "";
            bool isLatest = isLatestIndex >= 0 && isLatestIndex < cols.Length && IsTrue(cols[isLatestIndex].Trim());
            if (!string.IsNullOrWhiteSpace(version))
            {
                if (!versionsByPackageId.TryGetValue(packageId, out var packageVersions))
                {
                    packageVersions = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                    versionsByPackageId[packageId] = packageVersions;
                }

                packageVersions.Add(version);
            }

            if (!latestVersions.TryGetValue(packageId, out string current) ||
                isLatest ||
                CompareVersions(version, current) > 0)
                latestVersions[packageId] = version;
        }

        return new CatalogSnapshot(latestVersions, versionsByPackageId);
    }

    private static int CompareVersions(string left, string right)
    {
        var leftParts = ParseVersion(left);
        var rightParts = ParseVersion(right);
        for (int i = 0; i < Math.Max(leftParts.Count, rightParts.Count); i++)
        {
            int l = i < leftParts.Count ? leftParts[i] : 0;
            int r = i < rightParts.Count ? rightParts[i] : 0;
            int cmp = l.CompareTo(r);
            if (cmp != 0) return cmp;
        }

        return string.Compare(left, right, StringComparison.OrdinalIgnoreCase);
    }

    private static List<int> ParseVersion(string version)
    {
        var result = new List<int>();
        foreach (string part in (version ?? "").Split('.'))
        {
            string digits = new(part.TakeWhile(char.IsDigit).ToArray());
            result.Add(int.TryParse(digits, out int value) ? value : 0);
        }
        return result;
    }

    private static bool IsTrue(string value)
    {
        return value.Equals("true", StringComparison.OrdinalIgnoreCase) ||
               value.Equals("1", StringComparison.OrdinalIgnoreCase) ||
               value.Equals("yes", StringComparison.OrdinalIgnoreCase);
    }

    private static bool SetPackageJsonVersion(string packageJsonPath, string version, out string error)
    {
        error = "";
        if (!Regex.IsMatch(version, @"^\d+\.\d+\.\d+([-.][A-Za-z0-9._-]+)?$"))
        {
            error = "Publish Version must look like 1.0.0.";
            return false;
        }

        string fullPath = Path.GetFullPath(packageJsonPath);
        if (!File.Exists(fullPath))
        {
            error = $"package.json not found: {packageJsonPath}";
            return false;
        }

        string json = File.ReadAllText(fullPath);
        var pattern = new Regex("(\"version\"\\s*:\\s*\")([^\"]*)(\")");
        if (!pattern.IsMatch(json))
        {
            error = "package.json has no version field.";
            return false;
        }

        string updated = pattern.Replace(json, match => match.Groups[1].Value + version + match.Groups[3].Value, 1);
        if (updated != json) File.WriteAllText(fullPath, updated, new UTF8Encoding(false));
        return true;
    }

    private static string[] SplitCsvLine(string line)
    {
        var result = new List<string>();
        var sb = new StringBuilder();
        bool inQuotes = false;

        for (int i = 0; i < line.Length; i++)
        {
            char c = line[i];
            if (c == '"')
            {
                if (inQuotes && i + 1 < line.Length && line[i + 1] == '"')
                {
                    sb.Append('"');
                    i++;
                    continue;
                }

                inQuotes = !inQuotes;
                continue;
            }

            if (c == ',' && !inQuotes)
            {
                result.Add(sb.ToString());
                sb.Clear();
                continue;
            }

            sb.Append(c);
        }

        result.Add(sb.ToString());
        return result.ToArray();
    }

    private static IEnumerable<string[]> ReadCsvRows(string path)
    {
        var record = new StringBuilder();
        foreach (string line in File.ReadLines(path))
        {
            if (record.Length > 0) record.Append('\n');
            record.Append(line);

            if (HasOpenCsvQuote(record.ToString()))
                continue;

            yield return SplitCsvLine(record.ToString());
            record.Clear();
        }

        if (record.Length > 0)
            yield return SplitCsvLine(record.ToString());
    }

    private static bool HasOpenCsvQuote(string value)
    {
        bool inQuotes = false;
        for (int i = 0; i < value.Length; i++)
        {
            if (value[i] != '"') continue;
            if (inQuotes && i + 1 < value.Length && value[i + 1] == '"')
            {
                i++;
                continue;
            }

            inQuotes = !inQuotes;
        }

        return inQuotes;
    }

    private sealed class CatalogSnapshot
    {
        public static readonly CatalogSnapshot Empty = new(new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase), new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase));

        public CatalogSnapshot(Dictionary<string, string> latestVersions, Dictionary<string, HashSet<string>> versionsByPackageId)
        {
            LatestVersions = latestVersions;
            VersionsByPackageId = versionsByPackageId;
        }

        public Dictionary<string, string> LatestVersions { get; }
        private Dictionary<string, HashSet<string>> VersionsByPackageId { get; }

        public bool IsRegistered(string packageId)
        {
            return LatestVersions.ContainsKey(packageId) || VersionsByPackageId.ContainsKey(packageId);
        }

        public bool ContainsVersion(string packageId, string version)
        {
            return VersionsByPackageId.TryGetValue(packageId, out var versions) && versions.Contains(version);
        }

        public string GetLatestVersion(string packageId)
        {
            return LatestVersions.TryGetValue(packageId, out string version) ? version : "";
        }
    }

    private sealed class Entry
    {
        public ActionFitPackageInfo_SO Info;
        public string AssetPath;
        public string PackageRoot;
        public string PackageJsonPath;
        public string PackageId;
        public string DisplayName;
        public string Version;
        public string CatalogLatestVersion;
        public bool IsRegistered;
    }
}
#endif
