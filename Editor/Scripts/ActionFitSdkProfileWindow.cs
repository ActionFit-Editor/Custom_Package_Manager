#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using UnityEditor;
using UnityEngine;

public sealed class ActionFitSdkProfileWindow : EditorWindow
{
    private string _profilePath = "";
    private ActionFitSdkInstallProfile _profile;
    private ActionFitSdkInspectionResult _inspection;
    private ActionFitSdkInstallPlan _plan;
    private ActionFitSdkExecutionResult _execution;
    private ActionFitSdkInstallOperation _operation;
    private readonly HashSet<string> _selectedModules = new(StringComparer.Ordinal);
    private Vector2 _scroll;
    private bool _executing;

    [MenuItem("Tools/Package/Custom Package Manager/SDK Profiles", false, 2)]
    public static void Open()
    {
        GetWindow<ActionFitSdkProfileWindow>("SDK Profiles");
    }

    private void OnGUI()
    {
        DrawProfilePicker();
        if (_profile == null)
        {
            DrawPendingRecovery();
            return;
        }

        _scroll = EditorGUILayout.BeginScrollView(_scroll);
        DrawProfileSummary();
        DrawModules();
        DrawPlanningActions();
        DrawInspection();
        DrawPlan();
        DrawExecution();
        DrawPendingRecovery();
        EditorGUILayout.EndScrollView();
    }

    private void DrawProfilePicker()
    {
        EditorGUILayout.LabelField("SDK Install Profile", EditorStyles.boldLabel);
        using (new EditorGUILayout.HorizontalScope())
        {
            _profilePath = EditorGUILayout.TextField(_profilePath);
            if (GUILayout.Button("Browse", GUILayout.Width(72)))
            {
                string path = EditorUtility.OpenFilePanel("Select SDKInstallProfile.json", ActionFitPackagePaths.ProjectRoot, "json");
                if (!string.IsNullOrWhiteSpace(path)) _profilePath = path;
            }
            using (new EditorGUI.DisabledScope(_executing || string.IsNullOrWhiteSpace(_profilePath)))
            {
                if (GUILayout.Button("Load", GUILayout.Width(64))) LoadProfile();
            }
        }
    }

    private void LoadProfile()
    {
        try
        {
            _profile = ActionFitSdkInstallApi.ReadProfile(_profilePath);
            _selectedModules.Clear();
            foreach (ActionFitSdkModuleDefinition module in _profile.Modules.Where(item => item != null && (item.Required || item.DefaultSelected)))
                _selectedModules.Add(module.Id);
            _inspection = null;
            _plan = null;
            _execution = null;
        }
        catch (Exception ex)
        {
            _profile = null;
            EditorUtility.DisplayDialog("SDK Profile", ex.Message, "OK");
        }
    }

    private void DrawProfileSummary()
    {
        EditorGUILayout.Space(8);
        EditorGUILayout.LabelField(_profile.DisplayName, EditorStyles.boldLabel);
        EditorGUILayout.LabelField("Vendor", _profile.Vendor);
        EditorGUILayout.LabelField("Profile", $"{_profile.ProfileId} @ {_profile.ProfileVersion}");
        EditorGUILayout.LabelField("Bridge package", _profile.BridgePackageId);
        EditorGUILayout.LabelField("Unity", string.IsNullOrWhiteSpace(_profile.MaximumUnityVersion)
            ? $">= {_profile.MinimumUnityVersion}"
            : $"{_profile.MinimumUnityVersion} - {_profile.MaximumUnityVersion}");
        EditorGUILayout.LabelField("Platforms", string.Join(", ", _profile.SupportedPlatforms));
        EditorGUILayout.LabelField("License", _profile.LicenseUrl);
        EditorGUILayout.LabelField("Official domains", string.Join(", ", _profile.AllowedDomains));
    }

    private void DrawModules()
    {
        EditorGUILayout.Space(8);
        EditorGUILayout.LabelField("Modules", EditorStyles.boldLabel);
        foreach (ActionFitSdkModuleDefinition module in _profile.Modules.Where(item => item != null))
        {
            bool selected = module.Required || _selectedModules.Contains(module.Id);
            using (new EditorGUI.DisabledScope(module.Required || _executing))
            {
                bool next = EditorGUILayout.ToggleLeft(
                    module.DisplayName + (module.Required ? " (required)" : ""),
                    selected);
                if (next) _selectedModules.Add(module.Id);
                else _selectedModules.Remove(module.Id);
            }
        }
    }

    private void DrawPlanningActions()
    {
        EditorGUILayout.Space(8);
        using (new EditorGUI.DisabledScope(_executing))
        {
            _operation = (ActionFitSdkInstallOperation)EditorGUILayout.EnumPopup("Operation", _operation);
            using (new EditorGUILayout.HorizontalScope())
            {
                if (GUILayout.Button("Inspect (read-only)"))
                {
                    _inspection = ActionFitSdkInstallApi.Inspect(_profile, SelectedModules());
                    _plan = null;
                    _execution = null;
                }
                if (GUILayout.Button("Prepare Plan (read-only)"))
                {
                    _plan = ActionFitSdkInstallApi.Plan(_profile, new ActionFitSdkPlanRequest
                    {
                        Operation = _operation,
                        SelectedModuleIds = SelectedModules(),
                        AdoptCompatible = true,
                        TakeOwnershipOfCompatibleEntries = false,
                    });
                    _execution = null;
                }
            }
        }
    }

    private void DrawInspection()
    {
        if (_inspection == null) return;
        EditorGUILayout.Space(8);
        EditorGUILayout.HelpBox($"{_inspection.Code}: {_inspection.Message}", _inspection.Success ? MessageType.Info : MessageType.Error);
        foreach (ActionFitSdkInstallationFinding finding in _inspection.Findings)
            EditorGUILayout.LabelField($"[{finding.Classification}] {finding.Kind}: {finding.Value}\n{finding.Message}", EditorStyles.wordWrappedLabel);
    }

    private void DrawPlan()
    {
        if (_plan == null) return;
        EditorGUILayout.Space(8);
        EditorGUILayout.LabelField("Reviewed Plan", EditorStyles.boldLabel);
        EditorGUILayout.HelpBox($"{_plan.Code}: {_plan.Message}\nPlan ID: {_plan.PlanId}", _plan.Success ? MessageType.Info : MessageType.Error);
        foreach (ActionFitSdkPlannedChange change in _plan.Changes)
        {
            EditorGUILayout.LabelField($"{change.Area} / {change.Action}: {change.Key}\n{change.Before} -> {change.After}", EditorStyles.wordWrappedLabel);
        }
        foreach (ActionFitSdkInstallationFinding finding in _plan.Findings)
            EditorGUILayout.LabelField($"[{finding.Classification}] {finding.Message}", EditorStyles.wordWrappedLabel);

        using (new EditorGUI.DisabledScope(_executing || !_plan.Success))
        {
            if (GUILayout.Button($"Review and Execute {_plan.Operation}")) ConfirmAndExecute();
        }
    }

    private async void ConfirmAndExecute()
    {
        if (_plan == null || !_plan.Success || _executing) return;
        string detail = $"Operation: {_plan.Operation}\nProfile: {_plan.ProfileId} @ {_plan.ProfileVersion}\nChanges: {_plan.Changes.Length}\nPlan ID: {_plan.PlanId}\n\nExecute this exact reviewed plan?";
        if (!EditorUtility.DisplayDialog("Confirm SDK Plan", detail, "Execute", "Cancel")) return;

        _executing = true;
        try
        {
            _execution = await ExecuteAsync(_plan);
        }
        catch (Exception ex)
        {
            _execution = new ActionFitSdkExecutionResult { Success = false, Code = "UI_EXECUTION_FAILED", Message = ex.Message };
        }
        finally
        {
            _executing = false;
            Repaint();
        }
    }

    private static Task<ActionFitSdkExecutionResult> ExecuteAsync(ActionFitSdkInstallPlan plan)
    {
        return plan.Operation switch
        {
            ActionFitSdkInstallOperation.Apply => ActionFitSdkInstallApi.ApplyAsync(plan, plan.PlanId),
            ActionFitSdkInstallOperation.Repair => ActionFitSdkInstallApi.RepairAsync(plan, plan.PlanId),
            ActionFitSdkInstallOperation.Update => ActionFitSdkInstallApi.UpdateAsync(plan, plan.PlanId),
            ActionFitSdkInstallOperation.Remove => ActionFitSdkInstallApi.RemoveAsync(plan, plan.PlanId),
            _ => throw new InvalidOperationException($"Unsupported SDK operation: {plan.Operation}"),
        };
    }

    private void DrawExecution()
    {
        if (_execution == null) return;
        EditorGUILayout.Space(8);
        EditorGUILayout.HelpBox(
            $"{_execution.Code}: {_execution.Message}",
            _execution.Success ? MessageType.Info : MessageType.Error);
    }

    private void DrawPendingRecovery()
    {
        string[] pending;
        try
        {
            pending = ActionFitSdkInstallApi.InspectPendingTransactions();
        }
        catch (Exception ex)
        {
            EditorGUILayout.HelpBox($"Pending transaction inspection failed: {ex.Message}", MessageType.Error);
            return;
        }
        if (pending.Length == 0) return;

        EditorGUILayout.Space(8);
        EditorGUILayout.HelpBox($"RecoveryRequired: {pending.Length} pending SDK transaction journal(s). No automatic rollback was performed.", MessageType.Error);
        using (new EditorGUI.DisabledScope(_executing))
        {
            if (GUILayout.Button("Review and Recover Pending Transactions") &&
                EditorUtility.DisplayDialog("Confirm SDK Recovery", "Restore project files recorded by every pending SDK transaction journal?", "Recover", "Cancel"))
            {
                ActionFitSdkRecoveryResult[] results = ActionFitSdkInstallApi.RecoverPendingTransactions();
                string message = string.Join("\n", results.Select(item => $"{item.Code}: {item.Message}"));
                EditorUtility.DisplayDialog("SDK Recovery", message, "OK");
            }
        }
    }

    private string[] SelectedModules()
    {
        return _selectedModules.OrderBy(item => item, StringComparer.Ordinal).ToArray();
    }
}
#endif
