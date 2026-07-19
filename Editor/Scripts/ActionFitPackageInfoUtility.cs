#if UNITY_EDITOR
using System;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using UnityEditor;
using UnityEngine;

public enum ActionFitPackageSettingsMode
{
    None,
    EditorOnly,
    RuntimeSingleton
}

public sealed class ActionFitPackageCreateRequest
{
    public string PackageId;
    public string DisplayName;
    public string RepoName;
    public ActionFitPackageRepositoryVisibility RepositoryVisibility;
    public bool RepositoryVisibilitySpecified;
    public string Version;
    public string UnityVersion;
    public string Description;
    public string Owner;
    public string Status;
    public string ReleaseNote;
    public ActionFitPackageSettingsMode SettingsMode;
    public string SettingsTypeName;
    public string SettingsOwner;
}

public static class ActionFitPackageInfoUtility
{
    public const string PackageInfoFolder = "Editor/PackageInfo";
    public const string PackageInfoAssetName = "ActionFitPackageInfo_SO.asset";
    public const string AiGuideFileName = "AI_GUIDE.md";
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
        SetIfEmpty(serialized, "_releaseNote", "카탈로그 메타데이터 갱신 릴리즈.");
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

            ActionFitPackageInfo_SO existingInfo = CreateOrUpdatePackageInfo(packageRoot, request);
            ValidatePackageContract(request.PackageId);
            return existingInfo;
        }

        Directory.CreateDirectory(fullPackageRoot);
        Directory.CreateDirectory(Path.Combine(fullPackageRoot, "Editor", "Scripts"));
        Directory.CreateDirectory(Path.Combine(fullPackageRoot, PackageInfoFolder));
        if (request.SettingsMode == ActionFitPackageSettingsMode.RuntimeSingleton)
            Directory.CreateDirectory(Path.Combine(fullPackageRoot, "Runtime"));
        if (request.SettingsMode != ActionFitPackageSettingsMode.None)
            Directory.CreateDirectory(Path.Combine(fullPackageRoot, "Tests", "Editor"));

        File.WriteAllText(Path.Combine(fullPackageRoot, "package.json"), BuildPackageJson(request), new UTF8Encoding(false));
        File.WriteAllText(Path.Combine(fullPackageRoot, "README.md"), BuildReadme(request), new UTF8Encoding(false));
        File.WriteAllText(Path.Combine(fullPackageRoot, AiGuideFileName), BuildAiGuide(request), new UTF8Encoding(false));
        File.WriteAllText(
            Path.Combine(fullPackageRoot, "Editor", "Scripts", $"{ToPascalIdentifier(request.DisplayName, request.PackageId)}PackageMenu.cs"),
            BuildPackageMenu(request),
            new UTF8Encoding(false));
        if (request.SettingsMode == ActionFitPackageSettingsMode.EditorOnly)
        {
            File.WriteAllText(
                Path.Combine(fullPackageRoot, "Editor", "Scripts", $"{request.SettingsTypeName}.cs"),
                BuildSettingsSource(request),
                new UTF8Encoding(false));
        }
        else if (request.SettingsMode == ActionFitPackageSettingsMode.RuntimeSingleton)
        {
            File.WriteAllText(
                Path.Combine(fullPackageRoot, "Runtime", $"{request.SettingsTypeName}.cs"),
                BuildSettingsSource(request),
                new UTF8Encoding(false));
            File.WriteAllText(
                Path.Combine(fullPackageRoot, "Runtime", $"{request.PackageId}.asmdef"),
                BuildRuntimeAsmdef(request.PackageId),
                new UTF8Encoding(false));
        }
        if (request.SettingsMode != ActionFitPackageSettingsMode.None)
        {
            File.WriteAllText(
                Path.Combine(fullPackageRoot, "Tests", "Editor", $"{request.SettingsTypeName}LifecycleTests.cs"),
                BuildSettingsTests(request),
                new UTF8Encoding(false));
            File.WriteAllText(
                Path.Combine(fullPackageRoot, "Tests", "Editor", $"{request.PackageId}.Editor.Tests.asmdef"),
                BuildEditorTestsAsmdef(request),
                new UTF8Encoding(false));
        }
        WritePackageInfoAssetFile(GetPackageInfoAssetPath(packageRoot), request);
        AssetDatabase.Refresh();

        File.WriteAllText(
            Path.Combine(fullPackageRoot, "Editor", $"{request.PackageId}.Editor.asmdef"),
            BuildEditorAsmdef(request),
            new UTF8Encoding(false));

        AssetDatabase.SaveAssets();
        AssetDatabase.Refresh();
        ActionFitPackageInfo_SO createdInfo = AssetDatabase.LoadAssetAtPath<ActionFitPackageInfo_SO>(GetPackageInfoAssetPath(packageRoot));
        ValidatePackageContract(request.PackageId);
        return createdInfo;
    }

    internal static void ValidatePackageContract(string packageId)
    {
        ActionFitPackageContractValidationResult result = ActionFitPackageContractValidator.ValidatePackage(packageId);
        if (result.Success) return;

        string diagnostics = string.Join("\n", ActionFitPackageContractValidator.DiagnosticSummaries(result));
        string detail = string.IsNullOrWhiteSpace(diagnostics) ? result.Message : $"{result.Message}\n{diagnostics}";
        throw new InvalidOperationException($"{result.Code}: Generated package contract validation failed. {detail}");
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
        SetRepositoryVisibility(serialized, request.RepositoryVisibility);
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

    private static void SetRepositoryVisibility(SerializedObject serialized, ActionFitPackageRepositoryVisibility visibility)
    {
        var prop = serialized.FindProperty("_repositoryVisibility");
        if (prop != null) prop.enumValueIndex = (int)visibility;
    }

    internal static void ValidateCreateRequest(ActionFitPackageCreateRequest request)
    {
        if (request == null) throw new ArgumentNullException(nameof(request));
        if (request.RepositoryVisibility != ActionFitPackageRepositoryVisibility.Public &&
            request.RepositoryVisibility != ActionFitPackageRepositoryVisibility.Private)
        {
            throw new InvalidOperationException("Repository Visibility must be Public or Private.");
        }
        if (!request.RepositoryVisibilitySpecified)
        {
            if (request.RepositoryVisibility == ActionFitPackageRepositoryVisibility.Private)
                throw new InvalidOperationException("Private Repository Visibility must be explicitly selected before creating a package.");

            request.RepositoryVisibility = ActionFitPackageRepositoryVisibility.Public;
        }
        request.PackageId = NormalizePackageId(request.PackageId);
        request.DisplayName = NormalizeRequired(request.DisplayName, "Display Name");
        request.RepoName = NormalizeRepoName(request.RepoName);
        request.Version = string.IsNullOrWhiteSpace(request.Version) ? "1.0.0" : request.Version.Trim();
        request.UnityVersion = string.IsNullOrWhiteSpace(request.UnityVersion) ? "6000.2" : request.UnityVersion.Trim();
        request.Description = (request.Description ?? "").Trim();
        request.Owner = string.IsNullOrWhiteSpace(request.Owner) ? "ActionFit" : request.Owner.Trim();
        request.Status = string.IsNullOrWhiteSpace(request.Status) ? "verified" : request.Status.Trim();
        request.ReleaseNote = string.IsNullOrWhiteSpace(request.ReleaseNote) ? "초기 검증 릴리즈." : request.ReleaseNote.Trim();

        if (!Enum.IsDefined(typeof(ActionFitPackageSettingsMode), request.SettingsMode))
            throw new InvalidOperationException("Settings Mode must be None, EditorOnly, or RuntimeSingleton.");

        if (request.SettingsMode == ActionFitPackageSettingsMode.None)
        {
            request.SettingsTypeName = string.Empty;
            request.SettingsOwner = string.Empty;
            return;
        }

        request.SettingsTypeName = string.IsNullOrWhiteSpace(request.SettingsTypeName)
            ? $"{ToPascalIdentifier(request.DisplayName, request.PackageId)}SettingsSO"
            : request.SettingsTypeName.Trim();
        request.SettingsOwner = string.IsNullOrWhiteSpace(request.SettingsOwner)
            ? ToPascalIdentifier(request.DisplayName, request.PackageId)
            : request.SettingsOwner.Trim();

        if (!Regex.IsMatch(request.SettingsTypeName, @"^[A-Za-z_][A-Za-z0-9_]*$"))
            throw new InvalidOperationException("Settings Type Name must be a valid C# identifier.");
        if (request.SettingsOwner is "." or ".." ||
            request.SettingsOwner.IndexOfAny(new[] { '/', '\\', ':' }) >= 0)
            throw new InvalidOperationException("Settings Owner must be one safe _Data folder segment.");
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
        string dependencies = request.SettingsMode == ActionFitPackageSettingsMode.None
            ? "{}"
            : "{\n    \"com.actionfit.sosingleton\": \"1.0.6\"\n  }";
        return "{\n" +
               $"  \"name\": \"{EscapeJson(request.PackageId)}\",\n" +
               $"  \"version\": \"{EscapeJson(request.Version)}\",\n" +
               $"  \"displayName\": \"{EscapeJson(request.DisplayName)}\",\n" +
               $"  \"description\": \"{EscapeJson(request.Description)}\",\n" +
               $"  \"unity\": \"{EscapeJson(request.UnityVersion)}\",\n" +
               $"  \"author\": {{ \"name\": \"{EscapeJson(request.Owner)}\" }},\n" +
               $"  \"dependencies\": {dependencies}\n" +
               "}\n";
    }

    private static string BuildReadme(ActionFitPackageCreateRequest request)
    {
        string settingsSection = BuildReadmeSettingsSection(request);
        string settingMenu = request.SettingsMode == ActionFitPackageSettingsMode.None
            ? "- If this package later owns or bootstraps a settings ScriptableObject, add `Setting SO` under the same package root.\n"
            : "- Setting SO: `Tools > Package > " + request.DisplayName + " > Setting SO`.\n";
        string runtimeAssembly = request.SettingsMode == ActionFitPackageSettingsMode.RuntimeSingleton
            ? $"- **Runtime** (`{request.PackageId}`): runtime settings assembly.\n"
            : string.Empty;
        return $"# {request.DisplayName} ({request.PackageId})\n\n" +
               $"{request.Description}\n\n" +
               "## Install\n\n" +
               "```json\n" +
               "{\n" +
               "  \"dependencies\": {\n" +
               $"    \"{request.PackageId}\": \"https://github.com/ActionFit-Editor/{request.RepoName}.git#{request.Version}\"\n" +
               "  }\n" +
               "}\n" +
               "```\n\n" +
               settingsSection +
               "## Unity Menu\n\n" +
               $"- README: `Tools > Package > {request.DisplayName} > README`.\n" +
               settingMenu + "\n" +
               "## AI Guide\n\n" +
               $"- Read `{AiGuideFileName}` before modifying or diagnosing this package in a consuming project.\n\n" +
               "## Assembly\n\n" +
               runtimeAssembly +
               $"- **Editor** (`{request.PackageId}.Editor`): editor-only package assembly.\n";
    }

    private static string BuildAiGuide(ActionFitPackageCreateRequest request)
    {
        string settingsContract = BuildAiGuideSettingsContract(request);
        return $"# AI Guide - {request.DisplayName}\n\n" +
               "This file is shipped inside the UPM package so an AI assistant in a consuming Unity project can understand the package without access to the source project's `Docs/AI` folder.\n\n" +
               "## Package Identity\n\n" +
               $"- Package ID: `{request.PackageId}`\n" +
               $"- Display name: {request.DisplayName}\n" +
               $"- Repository: `https://github.com/ActionFit-Editor/{request.RepoName}.git`\n" +
               $"- Current package version at generation time: `{request.Version}`\n" +
               $"- Unity version: `{request.UnityVersion}`\n\n" +
               "## Purpose\n\n" +
               $"{request.Description}\n\n" +
               "## Project Router Registration\n\n" +
               "This package should be listed in `Packages/com.actionfit.custompackagemanager/PACKAGE_AI_GUIDE_ROUTER.md`.\n\n" +
               "Requested router entry:\n\n" +
               $"- `Packages/{request.PackageId}/AI_GUIDE.md` - {request.DisplayName} provides an ActionFit Unity editor workflow. Read when changing `{request.DisplayName}` package behavior, settings, metadata, or release flow.\n\n" +
               "If the router file is not already included in the AI assistant's default reading sequence, the router file is responsible for asking the user to link it from the project's primary AI markdown entry point. Prefer an existing `PROJECT.md` wherever the project keeps it, otherwise use `AGENTS.md`, `CLAUDE.md`, `GEMINI.md`, or another primary AI markdown entry point.\n\n" +
               "Read this file when:\n\n" +
               $"- changing files under `Packages/{request.PackageId}/`\n" +
               $"- diagnosing `{request.DisplayName}` behavior in a consuming project\n" +
               $"- preparing a release for `{request.PackageId}`\n" +
               "- editing package metadata, README, AI guide, package version, or release notes\n\n" +
               "## How To Work On This Package\n\n" +
               "- Treat `package.json` as the source for package ID, version, Unity version, and package dependencies.\n" +
               $"- Treat `Editor/PackageInfo/{PackageInfoAssetName}` as the source for catalog metadata, owner, status, description, release note, repository name, and dependency override.\n" +
               "- Read `Packages/com.actionfit.custompackagemanager/PACKAGE_AI_GUIDE_ROUTER.md` only when deciding which installed ActionFit package `AI_GUIDE.md` applies to a task.\n" +
               settingsContract +
               "- Keep `README.md` focused on human usage and setup.\n" +
               "- Keep this `AI_GUIDE.md` focused on AI-facing architecture, constraints, migration notes, and package-specific editing rules.\n" +
               "- When behavior changes, update `AI_GUIDE.md` in the same package before publishing so consuming projects receive the latest AI context.\n\n" +
               "## Package Tools Menu\n\n" +
               $"- Unity menu root: `Tools/Package/{request.DisplayName}/`.\n" +
               "- Keep package commands under this package root.\n" +
               "- `README`: required for every ActionFit package; opens this package README.\n" +
               "- `Setting SO`: required when this package owns or bootstraps a settings ScriptableObject.\n" +
               "- Keep README-only packages in the README-only priority band unless this package adds executable commands or settings access.\n" +
               "- Do not add README or Setting SO access back to Custom Package Manager package rows or Project Files.\n\n" +
               "## Release Note Rules\n\n" +
               "- `ActionFitPackageInfo_SO.ReleaseNote` must be written in Korean so planners and developers can read package patch notes directly. Keep code identifiers, package IDs, menu paths, config keys, and file paths in their original spelling.\n" +
               "- `ActionFitPackageInfo_SO.ReleaseNote` must contain only the single version being prepared.\n" +
               "- Do not copy older changelog entries into the newest release note.\n" +
               "- Version history and update-range summaries are composed by Custom Package Manager from separate catalog version rows.\n" +
               "- Do not add headings such as `## 1.0.0` inside ReleaseNote unless a specific package UI requires it; the catalog row already carries the version.\n\n" +
               "## Publish Notes\n\n" +
               "- Publishing is manual through Custom Package Manager.\n" +
               "- Before reusing a version, check the remote Git tags. Published tags are immutable.\n" +
               "- If this package is modified after a version was tagged, bump to the next unused patch version before publishing.\n";
    }

    private static string BuildReadmeSettingsSection(ActionFitPackageCreateRequest request)
    {
        if (request.SettingsMode == ActionFitPackageSettingsMode.None)
            return string.Empty;

        string path = request.SettingsMode == ActionFitPackageSettingsMode.RuntimeSingleton
            ? $"Assets/_Data/_{request.SettingsOwner}/Resources/SO/{request.SettingsTypeName}.asset"
            : $"Assets/_Data/_{request.SettingsOwner}/{request.SettingsTypeName}.asset";
        string runtime = request.SettingsMode == ActionFitPackageSettingsMode.RuntimeSingleton
            ? $" Runtime code reads it through `{request.SettingsTypeName}.Instance`; the lookup result is cached until Unity subsystem registration resets static state."
            : "";
        return "## Settings SO\n\n" +
               $"- Lifecycle: `{request.SettingsMode}`.\n" +
               $"- Canonical path: `{path}`.\n" +
               "- The shared SO Singleton provider reuses a canonical, registered legacy, or unique existing asset before creating a new one. Duplicate or invalid candidates block creation.\n" +
               runtime + "\n\n";
    }

    private static string BuildAiGuideSettingsContract(ActionFitPackageCreateRequest request)
    {
        if (request.SettingsMode == ActionFitPackageSettingsMode.None)
            return "- This package was generated without a settings ScriptableObject. Add one only through an explicit lifecycle decision.\n";

        string path = request.SettingsMode == ActionFitPackageSettingsMode.RuntimeSingleton
            ? $"Assets/_Data/_{request.SettingsOwner}/Resources/SO/{request.SettingsTypeName}.asset"
            : $"Assets/_Data/_{request.SettingsOwner}/{request.SettingsTypeName}.asset";
        string runtime = request.SettingsMode == ActionFitPackageSettingsMode.RuntimeSingleton
            ? $"- `{request.SettingsTypeName}` inherits `SO_Singleton<{request.SettingsTypeName}>`; preserve the `Resources/SO` location and subsystem-reset cache contract.\n"
            : $"- `{request.SettingsTypeName}` is Editor-only and must not move into a Runtime assembly.\n";
        return $"- Settings lifecycle is `{request.SettingsMode}` and is registered with `ActionFitSettingsAssetAttribute`.\n" +
               $"- Canonical project-owned asset path: `{path}`. Preserve any existing asset GUID and serialized values; do not move it automatically.\n" +
               "- Resolve through `ActionFitSettingsAssetProvider`; canonical, registered legacy, and unique project assets are reused, while duplicate or invalid candidates block creation.\n" +
               runtime;
    }

    private static string BuildPackageMenu(ActionFitPackageCreateRequest request)
    {
        string className = $"{ToPascalIdentifier(request.DisplayName, request.PackageId)}PackageMenu";
        string packageId = EscapeJson(request.PackageId);
        string displayName = EscapeJson(request.DisplayName);
        string settingsUsing = request.SettingsMode == ActionFitPackageSettingsMode.None
            ? string.Empty
            : "using ActionFit.SOSingleton.Editor;\n";
        string settingsMenu = request.SettingsMode == ActionFitPackageSettingsMode.None
            ? string.Empty
            : "    private const int SettingPriority = 600;\n" +
              "\n" +
              "    [MenuItem(MenuRoot + \"Setting SO\", false, SettingPriority)]\n" +
              "    private static void FocusSettings()\n" +
              "    {\n" +
              $"        var settings = ActionFitSettingsAssetProvider.GetOrCreate<{request.SettingsTypeName}>();\n" +
              "        if (settings == null)\n" +
              "        {\n" +
              "            EditorUtility.DisplayDialog(\"Setting SO\", \"The settings asset could not be resolved. Run the SO Singleton audit for details.\", \"OK\");\n" +
              "            return;\n" +
              "        }\n" +
              "\n" +
              "        Selection.activeObject = settings;\n" +
              "        EditorGUIUtility.PingObject(settings);\n" +
              "    }\n\n";

        return "#if UNITY_EDITOR\n" +
               settingsUsing +
               "using UnityEditor;\n" +
               "using UnityEngine;\n\n" +
               $"public static class {className}\n" +
               "{\n" +
               $"    private const string MenuRoot = \"Tools/Package/{displayName}/\";\n" +
               $"    private const string ReadmePath = \"Packages/{packageId}/README.md\";\n" +
               "    private const int ReadmePriority = 901;\n\n" +
               settingsMenu +
               "    [MenuItem(MenuRoot + \"README\", false, ReadmePriority)]\n" +
               "    private static void OpenReadme()\n" +
               "    {\n" +
               "        var readme = AssetDatabase.LoadAssetAtPath<TextAsset>(ReadmePath);\n" +
               "        if (readme == null)\n" +
               "        {\n" +
               "            EditorUtility.DisplayDialog(\"Package README\", $\"README was not found.\\n{ReadmePath}\", \"OK\");\n" +
               "            return;\n" +
               "        }\n\n" +
               "        Selection.activeObject = readme;\n" +
               "        AssetDatabase.OpenAsset(readme);\n" +
               "    }\n" +
               "}\n" +
               "#endif\n";
    }

    private static string BuildEditorAsmdef(ActionFitPackageCreateRequest request)
    {
        string references = request.SettingsMode switch
        {
            ActionFitPackageSettingsMode.EditorOnly =>
                "[\n        \"com.actionfit.sosingleton\",\n        \"com.actionfit.sosingleton.Editor\"\n    ]",
            ActionFitPackageSettingsMode.RuntimeSingleton =>
                $"[\n        \"{EscapeJson(request.PackageId)}\",\n        \"com.actionfit.sosingleton\",\n        \"com.actionfit.sosingleton.Editor\"\n    ]",
            _ => "[]"
        };
        return "{\n" +
               $"    \"name\": \"{EscapeJson(request.PackageId)}.Editor\",\n" +
               "    \"rootNamespace\": \"\",\n" +
               $"    \"references\": {references},\n" +
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

    private static string BuildRuntimeAsmdef(string packageId)
    {
        return "{\n" +
               $"    \"name\": \"{EscapeJson(packageId)}\",\n" +
               "    \"rootNamespace\": \"\",\n" +
               "    \"references\": [\n" +
               "        \"com.actionfit.sosingleton\"\n" +
               "    ],\n" +
               "    \"includePlatforms\": [],\n" +
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

    private static string BuildSettingsSource(ActionFitPackageCreateRequest request)
    {
        string lifetime = request.SettingsMode == ActionFitPackageSettingsMode.RuntimeSingleton
            ? "RuntimeSingleton"
            : "EditorOnly";
        string baseType = request.SettingsMode == ActionFitPackageSettingsMode.RuntimeSingleton
            ? $"SO_Singleton<{request.SettingsTypeName}>"
            : "ScriptableObject";
        return "using ActionFit.SOSingleton;\n" +
               "using UnityEngine;\n\n" +
               $"[ActionFitSettingsAsset(\"{EscapeJson(request.SettingsOwner)}\", ActionFitSettingsAssetLifetime.{lifetime})]\n" +
               $"public sealed class {request.SettingsTypeName} : {baseType}\n" +
               "{\n" +
               "}\n";
    }

    private static string BuildEditorTestsAsmdef(ActionFitPackageCreateRequest request)
    {
        string runtimeReference = request.SettingsMode == ActionFitPackageSettingsMode.RuntimeSingleton
            ? $"        \"{EscapeJson(request.PackageId)}\",\n"
            : string.Empty;
        return "{\n" +
               $"    \"name\": \"{EscapeJson(request.PackageId)}.Editor.Tests\",\n" +
               "    \"rootNamespace\": \"\",\n" +
               "    \"references\": [\n" +
               runtimeReference +
               $"        \"{EscapeJson(request.PackageId)}.Editor\",\n" +
               "        \"com.actionfit.sosingleton\",\n" +
               "        \"com.actionfit.sosingleton.Editor\"\n" +
               "    ],\n" +
               "    \"includePlatforms\": [\n" +
               "        \"Editor\"\n" +
               "    ],\n" +
               "    \"optionalUnityReferences\": [\n" +
               "        \"TestAssemblies\"\n" +
               "    ],\n" +
               "    \"autoReferenced\": false,\n" +
               "    \"noEngineReferences\": false\n" +
               "}\n";
    }

    private static string BuildSettingsTests(ActionFitPackageCreateRequest request)
    {
        string lifetime = request.SettingsMode == ActionFitPackageSettingsMode.RuntimeSingleton
            ? "RuntimeSingleton"
            : "EditorOnly";
        string expectedPath = request.SettingsMode == ActionFitPackageSettingsMode.RuntimeSingleton
            ? $"Assets/_Data/_{request.SettingsOwner}/Resources/SO/{request.SettingsTypeName}.asset"
            : $"Assets/_Data/_{request.SettingsOwner}/{request.SettingsTypeName}.asset";
        return "using ActionFit.SOSingleton;\n" +
               "using ActionFit.SOSingleton.Editor;\n" +
               "using NUnit.Framework;\n\n" +
               $"public sealed class {request.SettingsTypeName}LifecycleTests\n" +
               "{\n" +
               "    [Test]\n" +
               "    public void Registration_UsesGeneratedLifecycleAndCanonicalPath()\n" +
               "    {\n" +
               $"        var registration = (ActionFitSettingsAssetAttribute)System.Attribute.GetCustomAttribute(typeof({request.SettingsTypeName}), typeof(ActionFitSettingsAssetAttribute));\n" +
               $"        var resolution = ActionFitSettingsAssetProvider.Resolve(typeof({request.SettingsTypeName}), false);\n\n" +
               "        Assert.That(registration, Is.Not.Null);\n" +
               $"        Assert.That(registration.Lifetime, Is.EqualTo(ActionFitSettingsAssetLifetime.{lifetime}));\n" +
               $"        Assert.That(resolution.CanonicalPath, Is.EqualTo(\"{EscapeJson(expectedPath)}\"));\n" +
               "    }\n" +
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
               $"  _repositoryVisibility: {(int)(request?.RepositoryVisibility ?? ActionFitPackageRepositoryVisibility.Public)}\n" +
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

    private static string ToPascalIdentifier(string displayName, string packageId)
    {
        string source = !string.IsNullOrWhiteSpace(displayName)
            ? displayName
            : Regex.Replace(packageId ?? "", @"^com\.actionfit\.", "", RegexOptions.IgnoreCase);
        var matches = Regex.Matches(source, @"[A-Za-z0-9]+");
        var builder = new StringBuilder();

        foreach (Match match in matches)
        {
            string token = match.Value;
            if (token.Length == 0) continue;
            if (builder.Length == 0 && char.IsDigit(token[0]))
                builder.Append('_');

            builder.Append(char.ToUpperInvariant(token[0]));
            if (token.Length > 1)
                builder.Append(token[1..]);
        }

        return builder.Length == 0 ? "ActionFitPackage" : builder.ToString();
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
