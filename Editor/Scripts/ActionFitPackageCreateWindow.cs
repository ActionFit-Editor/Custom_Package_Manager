#if UNITY_EDITOR
using System;
using System.Text.RegularExpressions;
using UnityEditor;
using UnityEngine;

public class ActionFitPackageCreateWindow : EditorWindow
{
    private string _packageId = "com.actionfit.";
    private string _displayName = "";
    private string _repoName = "";
    private int _repositoryVisibilitySelection;
    private static readonly string[] RepositoryVisibilityOptions = { "Public (Default)", "Private (Explicit Exception)" };
    private string _version = "1.0.0";
    private string _unityVersion = "6000.2";
    private string _owner = "ActionFit";
    private string _status = "verified";
    private string _description = "";
    private string _releaseNote = "검증된 최초 버전";
    private Vector2 _scroll;

    public static void Open()
    {
        var window = GetWindow<ActionFitPackageCreateWindow>("1. Create Package");
        window.minSize = new Vector2(460, 420);
    }

    private void OnGUI()
    {
        _scroll = EditorGUILayout.BeginScrollView(_scroll);

        EditorGUILayout.LabelField("Package", EditorStyles.boldLabel);
        _packageId = EditorGUILayout.TextField("Package Id", _packageId);
        _displayName = EditorGUILayout.TextField("Display Name", _displayName);
        _repoName = EditorGUILayout.TextField("Repo Name", _repoName);
        _repositoryVisibilitySelection = EditorGUILayout.Popup("Repository Visibility", _repositoryVisibilitySelection, RepositoryVisibilityOptions);
        EditorGUILayout.HelpBox(
            _repositoryVisibilitySelection == 1
                ? "Private is an explicit exception for approved distribution restrictions. Never place tokens, credentials, or other secrets in package content."
                : "Public is the default for new package repositories. Keep tokens, credentials, and other secrets outside package content.",
            _repositoryVisibilitySelection == 1 ? MessageType.Warning : MessageType.Info);
        _version = EditorGUILayout.TextField("Version", _version);
        _unityVersion = EditorGUILayout.TextField("Unity", _unityVersion);

        EditorGUILayout.Space(8);
        EditorGUILayout.LabelField("Catalog", EditorStyles.boldLabel);
        _owner = EditorGUILayout.TextField("Owner", _owner);
        _status = EditorGUILayout.TextField("Status", _status);
        EditorGUILayout.LabelField("Description");
        _description = EditorGUILayout.TextArea(_description, GUILayout.MinHeight(52));
        EditorGUILayout.LabelField("Release Note");
        _releaseNote = EditorGUILayout.TextArea(_releaseNote, GUILayout.MinHeight(52));

        EditorGUILayout.Space(12);
        using (new EditorGUILayout.HorizontalScope())
        {
            if (GUILayout.Button("Fill From Display Name", GUILayout.Width(160)))
                FillDerivedNames();

            GUILayout.FlexibleSpace();

            if (GUILayout.Button("1. Create Package", GUILayout.Width(140)))
                CreatePackage();
        }

        EditorGUILayout.EndScrollView();
    }

    private void FillDerivedNames()
    {
        if (string.IsNullOrWhiteSpace(_displayName)) return;

        string suffix = Regex.Replace(_displayName.Trim().ToLowerInvariant(), @"[^a-z0-9._-]+", "");
        _packageId = $"com.actionfit.{suffix}";
        _repoName = Regex.Replace(_displayName.Trim(), @"[^A-Za-z0-9._-]+", "_").Trim('_', '.', '-');
    }

    private void CreatePackage()
    {
        try
        {
            var request = new ActionFitPackageCreateRequest
            {
                PackageId = _packageId,
                DisplayName = _displayName,
                RepoName = _repoName,
                RepositoryVisibility = GetSelectedRepositoryVisibility(),
                RepositoryVisibilitySpecified = true,
                Version = _version,
                UnityVersion = _unityVersion,
                Owner = _owner,
                Status = _status,
                Description = _description,
                ReleaseNote = _releaseNote,
            };

            var info = ActionFitPackageInfoUtility.CreatePackage(request);
            string packageRoot = $"Packages/{request.PackageId}";
            if (info != null)
            {
                Selection.activeObject = info;
                EditorGUIUtility.PingObject(info);
                packageRoot = ActionFitPackageInfoUtility.FindPackageRoot(info);
            }
            else
            {
                Selection.activeObject = AssetDatabase.LoadAssetAtPath<UnityEngine.Object>(packageRoot);
            }

            Debug.Log($"[ActionFitPackageManager] Package created: {packageRoot}");
            Close();
        }
        catch (Exception ex)
        {
            Debug.LogError($"[ActionFitPackageManager] Package create failed: {ex.Message}");
            EditorUtility.DisplayDialog("ActionFit Package Manager", ex.Message, "OK");
        }
    }

    private ActionFitPackageRepositoryVisibility GetSelectedRepositoryVisibility()
        => _repositoryVisibilitySelection == 1
            ? ActionFitPackageRepositoryVisibility.Private
            : ActionFitPackageRepositoryVisibility.Public;
}
#endif
