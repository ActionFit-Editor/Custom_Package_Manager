#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using UnityEditor;
using UnityEngine;

[Serializable]
public sealed class ActionFitPackageSkillScaffoldRequest
{
    public string PackageId;
    public string SkillPrefix;
    public string SkillName;
    public string Description;
    public string[] Agents = { "codex", "claude" };
    public bool IncludeShared;
    public string Access = "read-only";
}

[Serializable]
public sealed class ActionFitPackageSkillScaffoldResult
{
    public bool Success;
    public string Code;
    public string Message;
    public string PackageId;
    public string HelpSkill;
    public string[] CreatedPaths = Array.Empty<string>();
}

/// <summary>
/// Adds schema v2 package-owned agent skills without overwriting existing skill sources.
/// </summary>
public static class ActionFitPackageSkillScaffoldApi
{
    private const int SchemaVersion = 2;
    private static readonly Regex PackageIdPattern = new Regex(
        @"^com\.actionfit\.[a-z0-9]+(?:[._-][a-z0-9]+)*$",
        RegexOptions.CultureInvariant);
    private static readonly Regex SkillNamePattern = new Regex(
        @"^[a-z0-9]+(?:-[a-z0-9]+)*$",
        RegexOptions.CultureInvariant);
    private static readonly string[] SupportedAgents = { "codex", "claude" };

    public static ActionFitPackageSkillScaffoldResult Add(ActionFitPackageSkillScaffoldRequest request)
    {
        if (request == null) return Failed("REQUEST_REQUIRED", "A skill scaffold request is required.");
        string packageId = (request.PackageId ?? string.Empty).Trim();
        if (!PackageIdPattern.IsMatch(packageId))
            return Failed("PACKAGE_ID_INVALID", $"Invalid embedded ActionFit package ID: {packageId}");

        string packageRoot = Path.Combine(ActionFitPackagePaths.PackagesRoot, packageId);
        ActionFitPackageSkillScaffoldResult result = AddAtPackageRoot(packageRoot, request);
        if (result.Success) AssetDatabase.Refresh();
        return result;
    }

    public static string AddJson(string requestJson)
    {
        ActionFitPackageSkillScaffoldRequest request;
        try
        {
            request = JsonUtility.FromJson<ActionFitPackageSkillScaffoldRequest>(requestJson);
        }
        catch (Exception exception)
        {
            return JsonUtility.ToJson(Failed("REQUEST_JSON_INVALID", exception.Message), true);
        }
        return JsonUtility.ToJson(Add(request), true);
    }

    internal static ActionFitPackageSkillScaffoldResult AddAtPackageRoot(
        string packageRoot,
        ActionFitPackageSkillScaffoldRequest request)
    {
        try
        {
            if (request == null) return Failed("REQUEST_REQUIRED", "A skill scaffold request is required.");
            string fullPackageRoot = Path.GetFullPath(packageRoot ?? string.Empty);
            string packageJsonPath = Path.Combine(fullPackageRoot, "package.json");
            if (!ActionFitPackageFileUtility.PhysicalDirectoryExists(fullPackageRoot) || !File.Exists(packageJsonPath))
                return Failed("PACKAGE_NOT_EMBEDDED", $"Editable package root was not found: {fullPackageRoot}");
            if (IsLinked(fullPackageRoot) || IsLinked(packageJsonPath))
                return Failed("PACKAGE_LINK_REJECTED", "Linked package roots and manifests cannot be scaffolded.");

            PackageJson package = JsonUtility.FromJson<PackageJson>(File.ReadAllText(packageJsonPath, Encoding.UTF8));
            string packageId = (package?.name ?? string.Empty).Trim();
            if (!PackageIdPattern.IsMatch(packageId))
                return Failed("PACKAGE_ID_INVALID", $"package.json contains an invalid ActionFit package ID: {packageId}");
            string requestedPackageId = (request.PackageId ?? string.Empty).Trim();
            if (!string.IsNullOrEmpty(requestedPackageId)
                && !string.Equals(requestedPackageId, packageId, StringComparison.Ordinal))
            {
                return Failed("PACKAGE_ID_MISMATCH", $"Requested package {requestedPackageId} does not match package.json {packageId}.");
            }

            string prefix = NormalizeSkillName(request.SkillPrefix);
            string skillName = NormalizeSkillName(request.SkillName);
            string description = (request.Description ?? string.Empty).Trim();
            string access = (request.Access ?? string.Empty).Trim();
            if (!SkillNamePattern.IsMatch(prefix))
                return Failed("SKILL_PREFIX_INVALID", "Skill Prefix must use lowercase letters, digits, and single hyphens.");
            if (!SkillNamePattern.IsMatch(skillName) || !skillName.StartsWith(prefix + "-", StringComparison.Ordinal))
                return Failed("SKILL_NAME_INVALID", $"Skill Name must start with {prefix}- and use lowercase letters, digits, and single hyphens.");
            if (string.IsNullOrWhiteSpace(description))
                return Failed("SKILL_DESCRIPTION_REQUIRED", "Description must explain what the skill does and when to use it.");
            if (access != "read-only" && access != "write-capable")
                return Failed("SKILL_ACCESS_INVALID", "Access must be read-only or write-capable.");

            string[] requestedAgents = request.Agents ?? Array.Empty<string>();
            if (requestedAgents.Length == 0)
                return Failed("SKILL_AGENTS_REQUIRED", "Select at least one supported agent: codex or claude.");
            string unsupportedAgent = requestedAgents.FirstOrDefault(
                agent => !SupportedAgents.Contains(agent, StringComparer.Ordinal));
            if (unsupportedAgent != null)
                return Failed("SKILL_AGENT_UNSUPPORTED", $"Unsupported agent: {unsupportedAgent}");
            string[] agents = NormalizeAgents(requestedAgents);

            string skillsRoot = Path.Combine(fullPackageRoot, "Skills~");
            string manifestPath = Path.Combine(skillsRoot, "manifest.json");
            if (Directory.Exists(skillsRoot) && ContainsLinkedEntry(skillsRoot, out string linkedSkillPath))
                return Failed("SKILL_ROOT_LINK_REJECTED", $"Skills~ contains a linked entry: {linkedSkillPath}");
            SkillManifest manifest = null;
            bool firstSkill = !File.Exists(manifestPath);
            if (firstSkill)
            {
                if (Directory.Exists(skillsRoot)
                    && Directory.EnumerateFileSystemEntries(skillsRoot).Any())
                {
                    return Failed(
                        "SKILL_MANIFEST_MISSING",
                        "Skills~ already contains files. Add or migrate manifest.json before scaffolding to avoid claiming unknown sources.");
                }
                manifest = new SkillManifest
                {
                    schemaVersion = SchemaVersion,
                    skillPrefix = prefix,
                    helpSkill = prefix + "-help",
                    skills = Array.Empty<SkillEntry>(),
                };
            }
            else
            {
                if (IsLinked(manifestPath))
                    return Failed("SKILL_MANIFEST_LINK_REJECTED", "Skills~/manifest.json must be a regular package-owned file.");
                manifest = JsonUtility.FromJson<SkillManifest>(File.ReadAllText(manifestPath, Encoding.UTF8));
                if (manifest == null || manifest.schemaVersion != SchemaVersion)
                    return Failed("SCHEMA_UPGRADE_REQUIRED", "Add Agent Skill requires a schemaVersion 2 manifest.");
                if (!string.Equals(manifest.skillPrefix, prefix, StringComparison.Ordinal)
                    || !string.Equals(manifest.helpSkill, prefix + "-help", StringComparison.Ordinal))
                {
                    return Failed("SKILL_PREFIX_CONFLICT", $"Existing manifest uses {manifest.skillPrefix}/{manifest.helpSkill}.");
                }
            }

            var entries = (manifest.skills ?? Array.Empty<SkillEntry>()).Where(item => item != null).ToList();
            if (entries.Any(item => !SkillNamePattern.IsMatch(item.name ?? string.Empty)
                                    || !(item.name ?? string.Empty).StartsWith(prefix + "-", StringComparison.Ordinal)
                                    || item.agents == null || item.agents.Length == 0
                                    || item.agents.Any(agent => !SupportedAgents.Contains(agent, StringComparer.Ordinal))
                                    || item.agents.Distinct(StringComparer.Ordinal).Count() != item.agents.Length
                                    || (item.access != "read-only" && item.access != "write-capable")))
            {
                return Failed("SKILL_MANIFEST_INVALID", "Existing schema v2 registrations must be valid before adding another skill.");
            }
            if (entries.GroupBy(item => item.name, StringComparer.Ordinal).Any(group => group.Count() > 1))
                return Failed("SKILL_MANIFEST_INVALID", "Existing schema v2 skill names must be unique before adding another skill.");
            if (entries.Any(item => string.Equals(item.name, skillName, StringComparison.Ordinal)))
                return Failed("SKILL_ALREADY_EXISTS", $"Skill {skillName} is already registered. Existing sources were not changed.");

            SkillEntry help = entries.FirstOrDefault(item => string.Equals(item.name, manifest.helpSkill, StringComparison.Ordinal));
            if (!firstSkill && help == null)
                return Failed("HELP_SKILL_REQUIRED", $"Existing schema v2 manifest does not register {manifest.helpSkill}.");
            if (!firstSkill && help.access != "read-only")
                return Failed("SKILL_MANIFEST_INVALID", $"Existing help skill {manifest.helpSkill} must be read-only.");
            if (!firstSkill && entries.Any(item => item.agents.Any(agent => !help.agents.Contains(agent, StringComparer.Ordinal))))
                return Failed("SKILL_MANIFEST_INVALID", $"Existing help skill {manifest.helpSkill} must cover every registered agent.");
            if (!firstSkill && entries.Any(item => item.agents.Any(agent =>
                    !File.Exists(Path.Combine(SkillSourceRoot(skillsRoot, agent, item.name), "SKILL.md")))))
            {
                return Failed("SKILL_MANIFEST_INVALID", "Every existing registration must have a package-owned SKILL.md before adding another skill.");
            }
            if (!firstSkill && entries.Any(item => item.includeShared)
                            && !Directory.Exists(Path.Combine(skillsRoot, "Shared")))
            {
                return Failed("SKILL_MANIFEST_INVALID", "Existing registrations that include Shared require Skills~/Shared.");
            }
            if (string.Equals(skillName, manifest.helpSkill, StringComparison.Ordinal))
                return Failed("HELP_SKILL_RESERVED", $"{manifest.helpSkill} is managed as the mandatory package help skill.");

            var createdFiles = new List<string>();
            var createdDirectories = new List<string>();
            string previousManifest = File.Exists(manifestPath) ? File.ReadAllText(manifestPath, Encoding.UTF8) : null;
            try
            {
                EnsureDirectory(skillsRoot, createdDirectories);
                if (firstSkill)
                {
                    help = new SkillEntry
                    {
                        name = manifest.helpSkill,
                        agents = SupportedAgents.ToArray(),
                        includeShared = false,
                        access = "read-only",
                    };
                    entries.Add(help);
                }

                string[] requiredHelpAgents = firstSkill
                    ? SupportedAgents
                    : entries.SelectMany(item => item.agents ?? Array.Empty<string>())
                        .Union(agents, StringComparer.Ordinal)
                        .OrderBy(AgentOrder).ToArray();
                foreach (string agent in requiredHelpAgents)
                {
                    string helpSkillPath = SkillSourceRoot(skillsRoot, agent, manifest.helpSkill);
                    string helpMarkdownPath = Path.Combine(helpSkillPath, "SKILL.md");
                    if (!File.Exists(helpMarkdownPath))
                    {
                        EnsureDirectory(helpSkillPath, createdDirectories);
                        WriteNewFile(
                            helpMarkdownPath,
                            BuildHelpSkill(packageId, package?.displayName, manifest.helpSkill),
                            createdFiles);
                        if (agent == "codex")
                            WriteCodexMetadata(helpSkillPath, manifest.helpSkill, HelpDescription(package?.displayName), createdDirectories, createdFiles);
                    }
                }
                help.agents = requiredHelpAgents;
                help.access = "read-only";

                string[] targetAgents = agents.OrderBy(AgentOrder).ToArray();
                foreach (string agent in targetAgents)
                {
                    string skillPath = SkillSourceRoot(skillsRoot, agent, skillName);
                    if (Directory.Exists(skillPath) || File.Exists(skillPath))
                        throw new InvalidOperationException($"Skill source already exists and was preserved: {skillPath}");
                    EnsureDirectory(skillPath, createdDirectories);
                    WriteNewFile(Path.Combine(skillPath, "SKILL.md"), BuildSkill(skillName, description), createdFiles);
                    if (agent == "codex")
                        WriteCodexMetadata(skillPath, skillName, description, createdDirectories, createdFiles);
                }

                if (request.IncludeShared)
                {
                    string sharedPath = Path.Combine(skillsRoot, "Shared");
                    EnsureDirectory(sharedPath, createdDirectories);
                    if (!Directory.EnumerateFileSystemEntries(sharedPath).Any())
                        WriteNewFile(Path.Combine(sharedPath, ".gitkeep"), string.Empty, createdFiles);
                }

                entries.Add(new SkillEntry
                {
                    name = skillName,
                    agents = targetAgents,
                    includeShared = request.IncludeShared,
                    access = access,
                });
                manifest.skills = entries
                    .OrderBy(item => string.Equals(item.name, manifest.helpSkill, StringComparison.Ordinal) ? 0 : 1)
                    .ThenBy(item => item.name, StringComparer.Ordinal)
                    .ToArray();
                WriteManifestAtomic(manifestPath, manifest);

                ActionFitPackageManagerRefreshSignal.Request();
                return new ActionFitPackageSkillScaffoldResult
                {
                    Success = true,
                    Code = firstSkill ? "SKILL_SYSTEM_CREATED" : "SKILL_ADDED",
                    Message = firstSkill
                        ? $"Created schema v2 package skills with mandatory help skill {manifest.helpSkill}."
                        : $"Added package skill {skillName} without overwriting existing sources.",
                    PackageId = packageId,
                    HelpSkill = manifest.helpSkill,
                    CreatedPaths = createdFiles.Select(path => RelativeTo(fullPackageRoot, path)).ToArray(),
                };
            }
            catch (Exception exception)
            {
                RestoreManifest(manifestPath, previousManifest);
                RollbackCreated(createdFiles, createdDirectories);
                return Failed("SKILL_SCAFFOLD_FAILED", exception.Message, packageId, manifest.helpSkill);
            }
        }
        catch (Exception exception)
        {
            return Failed("SKILL_SCAFFOLD_FAILED", exception.Message);
        }
    }

    internal static string ReadExistingPrefix(string packageId)
    {
        try
        {
            if (!PackageIdPattern.IsMatch(packageId ?? string.Empty)) return string.Empty;
            string path = Path.Combine(ActionFitPackagePaths.PackagesRoot, packageId, "Skills~", "manifest.json");
            if (!File.Exists(path)) return string.Empty;
            return JsonUtility.FromJson<SkillManifest>(File.ReadAllText(path, Encoding.UTF8))?.skillPrefix ?? string.Empty;
        }
        catch
        {
            return string.Empty;
        }
    }

    private static string[] NormalizeAgents(IEnumerable<string> agents)
    {
        return (agents ?? Array.Empty<string>())
            .Where(agent => SupportedAgents.Contains(agent, StringComparer.Ordinal))
            .Distinct(StringComparer.Ordinal)
            .OrderBy(AgentOrder)
            .ToArray();
    }

    private static int AgentOrder(string agent) => agent == "codex" ? 0 : 1;
    private static string NormalizeSkillName(string value) => (value ?? string.Empty).Trim();
    private static string SkillSourceRoot(string skillsRoot, string agent, string name)
        => Path.Combine(skillsRoot, agent == "codex" ? "Codex" : "Claude", name);

    private static void WriteCodexMetadata(
        string skillPath,
        string skillName,
        string description,
        ICollection<string> createdDirectories,
        ICollection<string> createdFiles)
    {
        string agentsPath = Path.Combine(skillPath, "agents");
        EnsureDirectory(agentsPath, createdDirectories);
        string shortDescription = BuildShortDescription(description);
        string yaml = "interface:\n"
                      + $"  display_name: {QuoteYaml(ToDisplayName(skillName))}\n"
                      + $"  short_description: {QuoteYaml(shortDescription)}\n"
                      + $"  default_prompt: {QuoteYaml($"Use ${skillName} when this package skill matches the user's request.")}\n";
        WriteNewFile(Path.Combine(agentsPath, "openai.yaml"), yaml, createdFiles);
    }

    private static string BuildHelpSkill(string packageId, string displayName, string helpSkill)
    {
        string packageName = string.IsNullOrWhiteSpace(displayName) ? packageId : displayName.Trim();
        string description = $"Explain the {packageName} package and its installed related skills. Use when the user asks for package help, available skills, invocation, setup, configuration, menus, or safety boundaries.";
        return $"---\nname: {helpSkill}\ndescription: {QuoteYaml(description)}\n---\n\n"
               + $"# {packageName} Help\n\n"
               + "1. Read `PACKAGE_SKILLS.md` first. Treat its generated package identity and related-skill table as authoritative.\n"
               + "2. Answer in the user's language and include the package ID, display name, summary, every related skill, each `$skill-name` invocation, description/when-to-use guidance, and access boundary.\n"
               + $"3. For package-specific setup, configuration, or Unity menus, read `Packages/{packageId}/README.md` and `Packages/{packageId}/AI_GUIDE.md` when present.\n"
               + "4. Explain commands or state changes without executing them unless the user separately requests that operation and repository safety rules permit it.\n";
    }

    private static string BuildSkill(string skillName, string description)
    {
        return $"---\nname: {skillName}\ndescription: {QuoteYaml(description)}\n---\n\n"
               + $"# {ToDisplayName(skillName)}\n\n"
               + "Follow the consuming repository's instructions and package documentation. Define the concrete workflow, required inputs, safety boundaries, and validation steps for this skill here.\n";
    }

    private static string HelpDescription(string displayName)
    {
        string name = string.IsNullOrWhiteSpace(displayName) ? "ActionFit package" : displayName.Trim();
        return $"Explain {name}, its related skills, setup, and safety boundaries.";
    }

    private static string BuildShortDescription(string description)
    {
        string value = Regex.Replace((description ?? string.Empty).Trim(), @"\s+", " ");
        if (value.Length < 25) value = (value + " package workflow and usage guidance").Trim();
        if (value.Length > 64) value = value.Substring(0, 61).TrimEnd() + "...";
        return value;
    }

    private static string ToDisplayName(string skillName)
    {
        return string.Join(" ", (skillName ?? string.Empty).Split('-')
            .Where(part => part.Length > 0)
            .Select(part => char.ToUpperInvariant(part[0]) + part.Substring(1)));
    }

    private static string QuoteYaml(string value)
    {
        return "\"" + (value ?? string.Empty)
            .Replace("\\", "\\\\")
            .Replace("\"", "\\\"")
            .Replace("\r", " ")
            .Replace("\n", " ") + "\"";
    }

    private static void EnsureDirectory(string path, ICollection<string> createdDirectories)
    {
        if (Directory.Exists(path)) return;
        string parent = Path.GetDirectoryName(path);
        if (!string.IsNullOrWhiteSpace(parent) && !Directory.Exists(parent))
            EnsureDirectory(parent, createdDirectories);
        Directory.CreateDirectory(path);
        createdDirectories.Add(path);
    }

    private static void WriteNewFile(string path, string text, ICollection<string> createdFiles)
    {
        if (File.Exists(path)) throw new IOException($"Existing file was preserved: {path}");
        using var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None);
        using var writer = new StreamWriter(stream, new UTF8Encoding(false));
        writer.Write(text);
        createdFiles.Add(path);
    }

    private static void WriteManifestAtomic(string path, SkillManifest manifest)
    {
        string directory = Path.GetDirectoryName(path) ?? throw new InvalidOperationException();
        Directory.CreateDirectory(directory);
        string temporaryPath = path + ".tmp-" + Guid.NewGuid().ToString("N");
        string backupPath = path + ".bak-" + Guid.NewGuid().ToString("N");
        File.WriteAllText(temporaryPath, JsonUtility.ToJson(manifest, true) + "\n", new UTF8Encoding(false));
        bool hadManifest = File.Exists(path);
        try
        {
            if (hadManifest) File.Move(path, backupPath);
            File.Move(temporaryPath, path);
            if (File.Exists(backupPath)) File.Delete(backupPath);
        }
        catch
        {
            if (File.Exists(path)) File.Delete(path);
            if (File.Exists(backupPath)) File.Move(backupPath, path);
            throw;
        }
        finally
        {
            if (File.Exists(temporaryPath)) File.Delete(temporaryPath);
            if (File.Exists(backupPath)) File.Delete(backupPath);
        }
    }

    private static void RestoreManifest(string path, string previousManifest)
    {
        if (previousManifest == null)
        {
            if (File.Exists(path)) File.Delete(path);
            return;
        }
        File.WriteAllText(path, previousManifest, new UTF8Encoding(false));
    }

    private static void RollbackCreated(IEnumerable<string> files, IEnumerable<string> directories)
    {
        foreach (string file in files.Reverse())
        {
            if (File.Exists(file)) File.Delete(file);
        }
        foreach (string directory in directories.Reverse())
        {
            if (Directory.Exists(directory) && !Directory.EnumerateFileSystemEntries(directory).Any())
                Directory.Delete(directory);
        }
    }

    private static bool IsLinked(string path)
        => (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0;

    private static bool ContainsLinkedEntry(string root, out string linkedPath)
    {
        linkedPath = null;
        var pending = new Stack<string>();
        pending.Push(root);
        while (pending.Count > 0)
        {
            string directory = pending.Pop();
            if (IsLinked(directory))
            {
                linkedPath = directory;
                return true;
            }
            foreach (string path in Directory.EnumerateFileSystemEntries(directory))
            {
                if (IsLinked(path))
                {
                    linkedPath = path;
                    return true;
                }
                if (Directory.Exists(path)) pending.Push(path);
            }
        }
        return false;
    }

    private static string RelativeTo(string root, string path)
        => path.Substring(root.TrimEnd(Path.DirectorySeparatorChar).Length)
            .TrimStart(Path.DirectorySeparatorChar).Replace(Path.DirectorySeparatorChar, '/');

    private static ActionFitPackageSkillScaffoldResult Failed(
        string code,
        string message,
        string packageId = "",
        string helpSkill = "")
    {
        return new ActionFitPackageSkillScaffoldResult
        {
            Success = false,
            Code = code,
            Message = message,
            PackageId = packageId,
            HelpSkill = helpSkill,
        };
    }

    [Serializable]
    private sealed class PackageJson
    {
        public string name;
        public string displayName;
        public string description;
    }

    [Serializable]
    private sealed class SkillManifest
    {
        public int schemaVersion;
        public string skillPrefix;
        public string helpSkill;
        public SkillEntry[] skills;
    }

    [Serializable]
    private sealed class SkillEntry
    {
        public string name;
        public string[] agents;
        public bool includeShared;
        public string access;
    }
}

public sealed class ActionFitPackageSkillScaffoldWindow : EditorWindow
{
    private static readonly string[] AccessOptions = { "read-only", "write-capable" };
    private string _packageId = "com.actionfit.";
    private string _skillPrefix = "";
    private string _skillName = "";
    private string _description = "";
    private bool _codex = true;
    private bool _claude = true;
    private bool _includeShared;
    private int _accessIndex;
    private Vector2 _scroll;
    private ActionFitPackageSkillScaffoldResult _lastResult;

    [MenuItem("Tools/Package/Custom Package Manager/Add Agent Skill", false, 3)]
    public static void Open()
    {
        Open(string.Empty);
    }

    public static void Open(string packageId)
    {
        var window = GetWindow<ActionFitPackageSkillScaffoldWindow>("Add Agent Skill");
        window.minSize = new Vector2(500, 380);
        if (!string.IsNullOrWhiteSpace(packageId))
        {
            window._packageId = packageId;
            string existingPrefix = ActionFitPackageSkillScaffoldApi.ReadExistingPrefix(packageId);
            if (!string.IsNullOrWhiteSpace(existingPrefix)) window._skillPrefix = existingPrefix;
        }
        window.Show();
    }

    private void OnGUI()
    {
        _scroll = EditorGUILayout.BeginScrollView(_scroll);
        EditorGUILayout.LabelField("Schema v2 Package Skill", EditorStyles.boldLabel);
        EditorGUILayout.HelpBox(
            "The first skill creates <prefix>-help for Codex and Claude. Existing skill sources are never overwritten.",
            MessageType.Info);
        _packageId = EditorGUILayout.TextField("Embedded Package Id", _packageId);
        _skillPrefix = EditorGUILayout.TextField("Skill Prefix", _skillPrefix);
        _skillName = EditorGUILayout.TextField("Skill Name", _skillName);
        _accessIndex = EditorGUILayout.Popup("Access", _accessIndex, AccessOptions);
        _includeShared = EditorGUILayout.Toggle("Include Shared", _includeShared);

        EditorGUILayout.LabelField("Agents");
        using (new EditorGUILayout.HorizontalScope())
        {
            _codex = EditorGUILayout.ToggleLeft("Codex", _codex, GUILayout.Width(100));
            _claude = EditorGUILayout.ToggleLeft("Claude", _claude, GUILayout.Width(100));
        }

        EditorGUILayout.LabelField("Description / When To Use");
        _description = EditorGUILayout.TextArea(_description, GUILayout.MinHeight(80));
        EditorGUILayout.Space(12);

        using (new EditorGUILayout.HorizontalScope())
        {
            GUILayout.FlexibleSpace();
            if (GUILayout.Button("Add Agent Skill", GUILayout.Width(150))) AddSkill();
        }

        if (_lastResult != null)
        {
            EditorGUILayout.Space(8);
            EditorGUILayout.HelpBox(
                $"{_lastResult.Code}: {_lastResult.Message}",
                _lastResult.Success ? MessageType.Info : MessageType.Error);
            foreach (string path in _lastResult.CreatedPaths ?? Array.Empty<string>())
                EditorGUILayout.LabelField(path, EditorStyles.miniLabel);
        }
        EditorGUILayout.EndScrollView();
    }

    private void AddSkill()
    {
        var agents = new List<string>();
        if (_codex) agents.Add("codex");
        if (_claude) agents.Add("claude");
        _lastResult = ActionFitPackageSkillScaffoldApi.Add(new ActionFitPackageSkillScaffoldRequest
        {
            PackageId = _packageId,
            SkillPrefix = _skillPrefix,
            SkillName = _skillName,
            Description = _description,
            Agents = agents.ToArray(),
            IncludeShared = _includeShared,
            Access = AccessOptions[_accessIndex],
        });
        if (_lastResult.Success)
            Debug.Log($"[ActionFitPackageManager] {_lastResult.Code}: {_lastResult.Message}");
        else
            Debug.LogError($"[ActionFitPackageManager] {_lastResult.Code}: {_lastResult.Message}");
    }
}
#endif
