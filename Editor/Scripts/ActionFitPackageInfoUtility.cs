#if UNITY_EDITOR
using System;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using UnityEditor;
using UnityEngine;

public sealed class ActionFitPackageCreateRequest
{
    public string PackageId;
    public string DisplayName;
    public string RepoName;
    public string Version;
    public string UnityVersion;
    public string Description;
    public string Owner;
    public string Status;
    public string ReleaseNote;
}

public static class ActionFitPackageInfoUtility
{
    public const string PackageInfoFolder = "Editor/PackageInfo";
    public const string PackageInfoAssetName = "ActionFitPackageInfo_SO.asset";
    private const string PackageInfoScriptPath = "Packages/com.actionfit.custompackagemanager/Editor/Scripts/ActionFitPackageInfo_SO.cs";

    public static ActionFitPackageInfo_SO CreateOrUpdateFromSelection()
    {
        string packageRoot = FindSelectedPackageRoot();
        if (string.IsNullOrWhiteSpace(packageRoot))
            throw new InvalidOperationException("Select a folder or asset under Packages/com.actionfit.* first.");

        return CreateOrUpdate(packageRoot);
    }

    public static ActionFitPackageInfo_SO CreateOrUpdate(string packageRoot)
    {
        string packageJsonPath = Path.Combine(packageRoot, "package.json").Replace("\\", "/");
        if (!File.Exists(Path.GetFullPath(packageJsonPath)))
            throw new FileNotFoundException("package.json not found.", packageJsonPath);

        var manifest = ActionFitPackageManifest.Read(packageJsonPath);
        string infoFolder = Path.Combine(packageRoot, PackageInfoFolder).Replace("\\", "/");
        EnsureFolder(infoFolder);

        string assetPath = Path.Combine(infoFolder, PackageInfoAssetName).Replace("\\", "/");
        var info = AssetDatabase.LoadAssetAtPath<ActionFitPackageInfo_SO>(assetPath);
        if (info == null)
        {
            if (assetPath.StartsWith("Packages/", StringComparison.OrdinalIgnoreCase))
            {
                info = CreatePackageInfoAssetFile(assetPath);
            }
            else
            {
                info = ScriptableObject.CreateInstance<ActionFitPackageInfo_SO>();
                if (!TryCreateAsset(info, assetPath))
                    info = CreatePackageInfoAssetFile(assetPath);
            }
        }

        if (info == null)
            return null;

        var serialized = new SerializedObject(info);
        SetIfEmpty(serialized, "_packageId", manifest.Name);
        SetIfEmpty(serialized, "_displayName", manifest.DisplayName);
        SetIfEmpty(serialized, "_repoName", ToRepoName(manifest.DisplayName, manifest.Name));
        SetIfEmpty(serialized, "_description", manifest.Description);
        SetIfEmpty(serialized, "_releaseNote", "package_catalog/package_versions 카탈로그 마이그레이션을 위한 재배포 버전");
        serialized.ApplyModifiedPropertiesWithoutUndo();

        EditorUtility.SetDirty(info);
        AssetDatabase.SaveAssets();
        AssetDatabase.ImportAsset(assetPath, ImportAssetOptions.ForceUpdate);
        return info;
    }

    public static ActionFitPackageInfo_SO CreatePackage(ActionFitPackageCreateRequest request)
    {
        ValidateCreateRequest(request);

        string packageRoot = Path.Combine("Packages", request.PackageId).Replace("\\", "/");
        string fullPackageRoot = Path.GetFullPath(packageRoot);
        if (Directory.Exists(fullPackageRoot))
        {
            string existingPackageJson = Path.Combine(fullPackageRoot, "package.json");
            if (!File.Exists(existingPackageJson))
                throw new InvalidOperationException($"Package already exists without package.json: {packageRoot}");

            return CreateOrUpdatePackageInfo(packageRoot, request);
        }

        Directory.CreateDirectory(fullPackageRoot);
        Directory.CreateDirectory(Path.Combine(fullPackageRoot, "Editor", "Scripts"));
        Directory.CreateDirectory(Path.Combine(fullPackageRoot, PackageInfoFolder));

        File.WriteAllText(Path.Combine(fullPackageRoot, "package.json"), BuildPackageJson(request), new UTF8Encoding(false));
        File.WriteAllText(Path.Combine(fullPackageRoot, "README.md"), BuildReadme(request), new UTF8Encoding(false));
        WritePackageInfoAssetFile(GetPackageInfoAssetPath(packageRoot), request);
        AssetDatabase.Refresh();

        File.WriteAllText(
            Path.Combine(fullPackageRoot, "Editor", $"{request.PackageId}.Editor.asmdef"),
            BuildEditorAsmdef(request.PackageId),
            new UTF8Encoding(false));

        AssetDatabase.SaveAssets();
        AssetDatabase.Refresh();
        return AssetDatabase.LoadAssetAtPath<ActionFitPackageInfo_SO>(GetPackageInfoAssetPath(packageRoot));
    }

    private static ActionFitPackageInfo_SO CreateOrUpdatePackageInfo(string packageRoot, ActionFitPackageCreateRequest request)
    {
        string assetPath = GetPackageInfoAssetPath(packageRoot);
        var existingInfo = AssetDatabase.LoadAssetAtPath<ActionFitPackageInfo_SO>(assetPath);
        if (existingInfo == null && packageRoot.StartsWith("Packages/", StringComparison.OrdinalIgnoreCase))
        {
            WritePackageInfoAssetFile(assetPath, request);
            AssetDatabase.ImportAsset(assetPath, ImportAssetOptions.ForceSynchronousImport | ImportAssetOptions.ForceUpdate);
            existingInfo = AssetDatabase.LoadAssetAtPath<ActionFitPackageInfo_SO>(assetPath);
            if (existingInfo == null) return null;
        }

        var info = existingInfo != null ? existingInfo : CreateOrUpdate(packageRoot);
        var serialized = new SerializedObject(info);
        SetString(serialized, "_packageId", request.PackageId);
        SetString(serialized, "_displayName", request.DisplayName);
        SetString(serialized, "_repoName", request.RepoName);
        SetString(serialized, "_owner", request.Owner);
        SetString(serialized, "_status", request.Status);
        SetString(serialized, "_description", request.Description);
        SetString(serialized, "_releaseNote", request.ReleaseNote);
        serialized.ApplyModifiedPropertiesWithoutUndo();

        EditorUtility.SetDirty(info);
        AssetDatabase.SaveAssets();
        return info;
    }

    private static string GetPackageInfoAssetPath(string packageRoot)
        => Path.Combine(packageRoot, PackageInfoFolder, PackageInfoAssetName).Replace("\\", "/");

    private static bool TryCreateAsset(ActionFitPackageInfo_SO info, string assetPath)
    {
        try
        {
            AssetDatabase.CreateAsset(info, assetPath);
            return AssetDatabase.LoadAssetAtPath<ActionFitPackageInfo_SO>(assetPath) != null;
        }
        catch (Exception ex)
        {
            Debug.LogWarning($"[ActionFitPackageManager] CreateAsset failed. Falling back to asset file write: {ex.Message}");
            return false;
        }
    }

    private static ActionFitPackageInfo_SO CreatePackageInfoAssetFile(string assetPath)
    {
        WritePackageInfoAssetFile(assetPath, null);
        AssetDatabase.ImportAsset(assetPath, ImportAssetOptions.ForceSynchronousImport | ImportAssetOptions.ForceUpdate);
        return AssetDatabase.LoadAssetAtPath<ActionFitPackageInfo_SO>(assetPath);
    }

    private static void WritePackageInfoAssetFile(string assetPath, ActionFitPackageCreateRequest request)
    {
        string scriptGuid = AssetDatabase.AssetPathToGUID(PackageInfoScriptPath);
        if (string.IsNullOrWhiteSpace(scriptGuid))
            throw new InvalidOperationException($"ActionFitPackageInfo_SO script guid not found: {PackageInfoScriptPath}");

        string fullAssetPath = Path.GetFullPath(assetPath);
        Directory.CreateDirectory(Path.GetDirectoryName(fullAssetPath));
        File.WriteAllText(fullAssetPath, BuildPackageInfoAssetYaml(scriptGuid, request), new UTF8Encoding(false));
    }

    public static string FindPackageRoot(ActionFitPackageInfo_SO info)
    {
        if (info == null) return "";
        string path = AssetDatabase.GetAssetPath(info);
        string full = Path.GetFullPath(path);
        string current = File.Exists(full) ? Path.GetDirectoryName(full) : full;

        while (!string.IsNullOrWhiteSpace(current))
        {
            string packageJson = Path.Combine(current, "package.json");
            if (File.Exists(packageJson))
                return ToProjectRelativePath(current);

            var parent = Directory.GetParent(current);
            if (parent == null) break;
            current = parent.FullName;
        }

        return "";
    }

    private static string FindSelectedPackageRoot()
    {
        string path = AssetDatabase.GetAssetPath(Selection.activeObject);
        if (string.IsNullOrWhiteSpace(path)) return "";

        string full = Path.GetFullPath(path);
        string current = File.Exists(full) ? Path.GetDirectoryName(full) : full;
        while (!string.IsNullOrWhiteSpace(current))
        {
            string relative = ToProjectRelativePath(current);
            if (!relative.StartsWith("Packages/com.actionfit.", StringComparison.OrdinalIgnoreCase))
                return "";

            if (File.Exists(Path.Combine(current, "package.json")))
                return relative;

            var parent = Directory.GetParent(current);
            if (parent == null) break;
            current = parent.FullName;
        }

        return "";
    }

    private static void SetIfEmpty(SerializedObject serialized, string propertyName, string value)
    {
        var prop = serialized.FindProperty(propertyName);
        if (prop != null && string.IsNullOrWhiteSpace(prop.stringValue))
            prop.stringValue = value ?? "";
    }

    private static void SetString(SerializedObject serialized, string propertyName, string value)
    {
        var prop = serialized.FindProperty(propertyName);
        if (prop != null) prop.stringValue = value ?? "";
    }

    private static void ValidateCreateRequest(ActionFitPackageCreateRequest request)
    {
        if (request == null) throw new ArgumentNullException(nameof(request));
        request.PackageId = NormalizePackageId(request.PackageId);
        request.DisplayName = NormalizeRequired(request.DisplayName, "Display Name");
        request.RepoName = NormalizeRepoName(request.RepoName);
        request.Version = string.IsNullOrWhiteSpace(request.Version) ? "1.0.0" : request.Version.Trim();
        request.UnityVersion = string.IsNullOrWhiteSpace(request.UnityVersion) ? "6000.2" : request.UnityVersion.Trim();
        request.Description = (request.Description ?? "").Trim();
        request.Owner = string.IsNullOrWhiteSpace(request.Owner) ? "ActionFit" : request.Owner.Trim();
        request.Status = string.IsNullOrWhiteSpace(request.Status) ? "verified" : request.Status.Trim();
        request.ReleaseNote = string.IsNullOrWhiteSpace(request.ReleaseNote) ? "검증된 최초 버전" : request.ReleaseNote.Trim();
    }

    private static string NormalizePackageId(string value)
    {
        value = (value ?? "").Trim().ToLowerInvariant();
        if (string.IsNullOrWhiteSpace(value)) throw new InvalidOperationException("Package Id is required.");
        if (!value.StartsWith("com.actionfit.", StringComparison.Ordinal))
            value = "com.actionfit." + value.TrimStart('.');

        if (!Regex.IsMatch(value, @"^com\.actionfit\.[a-z0-9][a-z0-9._-]*$"))
            throw new InvalidOperationException("Package Id must look like com.actionfit.mytool.");

        return value;
    }

    private static string NormalizeRequired(string value, string label)
    {
        value = (value ?? "").Trim();
        if (string.IsNullOrWhiteSpace(value)) throw new InvalidOperationException($"{label} is required.");
        return value;
    }

    private static string NormalizeRepoName(string value)
    {
        value = NormalizeRequired(value, "Repo Name");
        value = Regex.Replace(value, @"[^A-Za-z0-9._-]+", "_").Trim('_', '.', '-');
        if (string.IsNullOrWhiteSpace(value)) throw new InvalidOperationException("Repo Name is invalid.");
        return value;
    }

    private static string BuildPackageJson(ActionFitPackageCreateRequest request)
    {
        return "{\n" +
               $"  \"name\": \"{EscapeJson(request.PackageId)}\",\n" +
               $"  \"version\": \"{EscapeJson(request.Version)}\",\n" +
               $"  \"displayName\": \"{EscapeJson(request.DisplayName)}\",\n" +
               $"  \"description\": \"{EscapeJson(request.Description)}\",\n" +
               $"  \"unity\": \"{EscapeJson(request.UnityVersion)}\",\n" +
               $"  \"author\": {{ \"name\": \"{EscapeJson(request.Owner)}\" }},\n" +
               "  \"dependencies\": {}\n" +
               "}\n";
    }

    private static string BuildReadme(ActionFitPackageCreateRequest request)
    {
        return $"# {request.DisplayName} ({request.PackageId})\n\n" +
               $"{request.Description}\n\n" +
               "## 설치 (manifest.json, Git URL)\n\n" +
               "```json\n" +
               "{\n" +
               "  \"dependencies\": {\n" +
               $"    \"{request.PackageId}\": \"https://github.com/ActionFit-Editor/{request.RepoName}.git#{request.Version}\"\n" +
               "  }\n" +
               "}\n" +
               "```\n\n" +
               "## 구성\n\n" +
               $"- **Editor** (`{request.PackageId}.Editor`): 에디터 전용 코드\n";
    }

    private static string BuildEditorAsmdef(string packageId)
    {
        return "{\n" +
               $"    \"name\": \"{EscapeJson(packageId)}.Editor\",\n" +
               "    \"rootNamespace\": \"\",\n" +
               "    \"references\": [],\n" +
               "    \"includePlatforms\": [\n" +
               "        \"Editor\"\n" +
               "    ],\n" +
               "    \"excludePlatforms\": [],\n" +
               "    \"allowUnsafeCode\": false,\n" +
               "    \"overrideReferences\": false,\n" +
               "    \"precompiledReferences\": [],\n" +
               "    \"autoReferenced\": true,\n" +
               "    \"defineConstraints\": [],\n" +
               "    \"versionDefines\": [],\n" +
               "    \"noEngineReferences\": false\n" +
               "}\n";
    }

    private static string BuildPackageInfoAssetYaml(string scriptGuid, ActionFitPackageCreateRequest request)
    {
        return "%YAML 1.1\n" +
               "%TAG !u! tag:unity3d.com,2011:\n" +
               "--- !u!114 &11400000\n" +
               "MonoBehaviour:\n" +
               "  m_ObjectHideFlags: 0\n" +
               "  m_CorrespondingSourceObject: {fileID: 0}\n" +
               "  m_PrefabInstance: {fileID: 0}\n" +
               "  m_PrefabAsset: {fileID: 0}\n" +
               "  m_GameObject: {fileID: 0}\n" +
               "  m_Enabled: 1\n" +
               "  m_EditorHideFlags: 0\n" +
               $"  m_Script: {{fileID: 11500000, guid: {scriptGuid}, type: 3}}\n" +
               "  m_Name: ActionFitPackageInfo_SO\n" +
               "  m_EditorClassIdentifier: \n" +
               $"  _packageId: {EscapeYaml(request?.PackageId)}\n" +
               $"  _displayName: {EscapeYaml(request?.DisplayName)}\n" +
               $"  _repoName: {EscapeYaml(request?.RepoName)}\n" +
               $"  _owner: {EscapeYaml(request?.Owner)}\n" +
               $"  _status: {EscapeYaml(request?.Status ?? "verified")}\n" +
               $"  _description: {EscapeYaml(request?.Description)}\n" +
               $"  _releaseNote: {EscapeYaml(request?.ReleaseNote)}\n" +
               "  _dependenciesOverride: \n";
    }

    private static string EscapeYaml(string value)
        => string.IsNullOrEmpty(value) ? "" : "\"" + value.Replace("\\", "\\\\").Replace("\"", "\\\"").Replace("\n", "\\n").Replace("\r", "\\r") + "\"";

    private static string EscapeJson(string value)
        => (value ?? "").Replace("\\", "\\\\").Replace("\"", "\\\"").Replace("\n", "\\n").Replace("\r", "\\r");

    private static string ToRepoName(string displayName, string packageId)
    {
        string source = !string.IsNullOrWhiteSpace(displayName)
            ? displayName
            : Regex.Replace(packageId ?? "", @"^com\.actionfit\.", "", RegexOptions.IgnoreCase);

        string repo = Regex.Replace(source.Trim(), @"[^A-Za-z0-9]+", "_").Trim('_');
        return string.IsNullOrWhiteSpace(repo) ? "ActionFit_Package" : repo;
    }

    private static void EnsureFolder(string folder)
    {
        if (AssetDatabase.IsValidFolder(folder)) return;
        string parent = Path.GetDirectoryName(folder)?.Replace("\\", "/");
        if (!string.IsNullOrWhiteSpace(parent)) EnsureFolder(parent);
        AssetDatabase.CreateFolder(parent, Path.GetFileName(folder));
    }

    private static string ToProjectRelativePath(string fullPath)
    {
        string root = Path.GetFullPath(".").Replace("\\", "/").TrimEnd('/');
        string normalized = Path.GetFullPath(fullPath).Replace("\\", "/");
        return normalized.StartsWith(root + "/", StringComparison.OrdinalIgnoreCase)
            ? normalized[(root.Length + 1)..]
            : normalized;
    }
}
#endif
