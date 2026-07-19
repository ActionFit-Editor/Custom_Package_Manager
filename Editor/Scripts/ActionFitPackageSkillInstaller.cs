#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using UnityEditor;
using UnityEditor.PackageManager;
using UnityEngine;

public sealed class ActionFitPackageSkillInstallResult
{
    public int Installed { get; internal set; }
    public int Updated { get; internal set; }
    public int Removed { get; internal set; }
    public int Unchanged { get; internal set; }
    public List<string> Warnings { get; } = new List<string>();
}

public sealed class ActionFitPackageSkillStatus
{
    public string PackageId { get; internal set; }
    public int Registered { get; internal set; }
    public int Current { get; internal set; }
    public int UpdateAvailable { get; internal set; }
    public int Missing { get; internal set; }
    public int Preserved { get; internal set; }
    public int Conflicts { get; internal set; }

    public string Summary
    {
        get
        {
            var parts = new List<string> { $"registered {Registered}" };
            if (Current > 0) parts.Add($"current {Current}");
            if (UpdateAvailable > 0) parts.Add($"update available {UpdateAvailable}");
            if (Missing > 0) parts.Add($"missing {Missing}");
            if (Preserved > 0) parts.Add($"preserved {Preserved}");
            if (Conflicts > 0) parts.Add($"conflict {Conflicts}");
            return string.Join(" · ", parts);
        }
    }
}

public sealed class ActionFitPackageSkillInspectionResult
{
    public List<ActionFitPackageSkillStatus> Packages { get; } = new List<ActionFitPackageSkillStatus>();
    public List<string> Warnings { get; } = new List<string>();
}

public static class ActionFitPackageSkillInstallService
{
    private const int LegacyManifestSchemaVersion = 1;
    private const int CurrentManifestSchemaVersion = 2;
    private const int StateSchemaVersion = 1;
    private const string GeneratedInventoryFileName = "PACKAGE_SKILLS.md";
    private static readonly Regex PackageIdPattern = new Regex(
        @"^com\.actionfit\.[a-z0-9]+(?:[._-][a-z0-9]+)*$",
        RegexOptions.CultureInvariant);
    private static readonly Regex SkillNamePattern = new Regex(
        @"^[a-z0-9]+(?:-[a-z0-9]+)*$",
        RegexOptions.CultureInvariant);

    public static ActionFitPackageSkillInstallResult InstallOrRefresh(
        IEnumerable<string> packageRoots,
        string projectRoot,
        string statePath,
        string tempRoot,
        string legacyStatePath = null)
    {
        if (packageRoots == null) throw new ArgumentNullException(nameof(packageRoots));
        ValidateProjectRoot(projectRoot);
        ValidateManagedPath(projectRoot, statePath, "Skill state");
        ValidateManagedPath(projectRoot, tempRoot, "Skill staging");
        if (!string.IsNullOrWhiteSpace(legacyStatePath))
            ValidateManagedPath(projectRoot, legacyStatePath, "Legacy skill state");
        Directory.CreateDirectory(tempRoot);

        var result = new ActionFitPackageSkillInstallResult();
        SkillInstallState state = LoadState(statePath, result);
        ImportLegacyState(legacyStatePath, state, result);
        state.autoInstallEnabled = 1;

        try
        {
            List<SkillCandidate> candidates = ReadCandidates(packageRoots, result);
            foreach (IGrouping<string, SkillCandidate> group in candidates
                         .GroupBy(candidate => candidate.TargetRelativePath, StringComparer.Ordinal)
                         .OrderBy(group => group.Key, StringComparer.Ordinal))
            {
                List<SkillCandidate> claims = group.ToList();
                if (claims.Count > 1)
                {
                    string owners = string.Join(", ", claims.Select(candidate => candidate.PackageId)
                        .OrderBy(value => value, StringComparer.Ordinal));
                    result.Warnings.Add($"Skill target conflict was preserved: {group.Key} is claimed by {owners}");
                    continue;
                }

                InstallCandidate(claims[0], projectRoot, statePath, tempRoot, state, result);
            }

            SaveState(statePath, state);
            return result;
        }
        finally
        {
            DeleteStagingDirectories(tempRoot);
            DeleteDirectoryIfEmpty(tempRoot);
        }
    }

    public static ActionFitPackageSkillInstallResult RemoveManaged(
        string projectRoot,
        string statePath,
        string tempRoot)
    {
        ValidateProjectRoot(projectRoot);
        ValidateManagedPath(projectRoot, statePath, "Skill state");
        ValidateManagedPath(projectRoot, tempRoot, "Skill staging");
        SkillInstallState state = LoadState(statePath, null);
        state.autoInstallEnabled = 0;
        var result = new ActionFitPackageSkillInstallResult();

        foreach (SkillInstallEntry entry in state.EntriesSnapshot())
        {
            string targetPath = Path.Combine(projectRoot, ToNativePath(entry.targetPath));
            if (TryFindLinkedPath(projectRoot, targetPath, out string linkedPath))
            {
                result.Warnings.Add($"Preserved linked skill path during removal: {entry.targetPath} ({linkedPath})");
                continue;
            }
            if (File.Exists(targetPath))
            {
                result.Warnings.Add($"Preserved file at managed skill target: {entry.targetPath}");
                continue;
            }

            if (!Directory.Exists(targetPath))
            {
                state.Remove(entry.targetPath);
                continue;
            }

            if (IsReparsePoint(targetPath))
            {
                result.Warnings.Add($"Preserved linked skill directory during removal: {entry.targetPath}");
                continue;
            }

            if (!IsSafeTree(targetPath, out linkedPath))
            {
                result.Warnings.Add($"Preserved linked or unreadable skill during removal: {entry.targetPath} ({linkedPath})");
                continue;
            }

            string currentHash = ComputeDirectoryHash(targetPath);
            if (!string.Equals(currentHash, entry.installedHash, StringComparison.Ordinal))
            {
                result.Warnings.Add($"Preserved modified skill during removal: {entry.targetPath}");
                continue;
            }

            Directory.Delete(targetPath, true);
            state.Remove(entry.targetPath);
            result.Removed++;
            DeleteDirectoryIfEmpty(Path.GetDirectoryName(targetPath));
        }

        SaveState(statePath, state);
        DeleteDirectoryIfEmpty(tempRoot);
        return result;
    }

    public static ActionFitPackageSkillInspectionResult Inspect(
        IEnumerable<string> packageRoots,
        string projectRoot,
        string statePath,
        string legacyStatePath = null)
    {
        if (packageRoots == null) throw new ArgumentNullException(nameof(packageRoots));
        ValidateProjectRoot(projectRoot);
        ValidateManagedPath(projectRoot, statePath, "Skill state");
        if (!string.IsNullOrWhiteSpace(legacyStatePath))
            ValidateManagedPath(projectRoot, legacyStatePath, "Legacy skill state");

        var discovery = new ActionFitPackageSkillInstallResult();
        SkillInstallState state = LoadState(statePath, discovery);
        ImportLegacyState(legacyStatePath, state, discovery);
        List<SkillCandidate> candidates = ReadCandidates(packageRoots, discovery);
        var result = new ActionFitPackageSkillInspectionResult();
        result.Warnings.AddRange(discovery.Warnings);
        var statusByPackage = new Dictionary<string, ActionFitPackageSkillStatus>(StringComparer.OrdinalIgnoreCase);

        foreach (SkillCandidate candidate in candidates)
        {
            if (!statusByPackage.TryGetValue(candidate.PackageId, out ActionFitPackageSkillStatus status))
            {
                status = new ActionFitPackageSkillStatus { PackageId = candidate.PackageId };
                statusByPackage.Add(candidate.PackageId, status);
            }
            status.Registered++;
        }

        foreach (IGrouping<string, SkillCandidate> group in candidates
                     .GroupBy(candidate => candidate.TargetRelativePath, StringComparer.Ordinal))
        {
            List<SkillCandidate> claims = group.ToList();
            if (claims.Count > 1)
            {
                foreach (SkillCandidate candidate in claims) statusByPackage[candidate.PackageId].Conflicts++;
                continue;
            }

            InspectCandidate(claims[0], projectRoot, state, statusByPackage[claims[0].PackageId]);
        }

        result.Packages.AddRange(statusByPackage.Values.OrderBy(status => status.PackageId, StringComparer.OrdinalIgnoreCase));
        return result;
    }

    public static bool IsAutoInstallEnabled(string statePath, string legacyStatePath = null)
    {
        if (File.Exists(statePath)) return LoadState(statePath, null).autoInstallEnabled != 0;
        return !File.Exists(legacyStatePath) || LoadState(legacyStatePath, null).autoInstallEnabled != 0;
    }

    public static bool IsAutoInstallEnabled(
        string projectRoot,
        string statePath,
        string legacyStatePath)
    {
        ValidateProjectRoot(projectRoot);
        ValidateManagedPath(projectRoot, statePath, "Skill state");
        ValidateManagedPath(projectRoot, legacyStatePath, "Legacy skill state");
        return IsAutoInstallEnabled(statePath, legacyStatePath);
    }

    public static string ComputeDirectoryHash(string directory)
    {
        if (!Directory.Exists(directory)) return string.Empty;
        var files = EnumerateFilesWithoutLinks(directory).ToDictionary(
            path => path.Substring(directory.Length).TrimStart(Path.DirectorySeparatorChar)
                .Replace(Path.DirectorySeparatorChar, '/'),
            path => path,
            StringComparer.Ordinal);
        return ComputeFileMapHash(files);
    }

    private static string ComputeFileMapHash(
        IReadOnlyDictionary<string, string> files,
        IReadOnlyDictionary<string, byte[]> generatedFiles = null)
    {
        using var payload = new MemoryStream();
        var relativePaths = new HashSet<string>(files.Keys, StringComparer.Ordinal);
        if (generatedFiles != null) relativePaths.UnionWith(generatedFiles.Keys);
        foreach (string relativePath in relativePaths.OrderBy(value => value, StringComparer.Ordinal))
        {
            byte[] nameBytes = Encoding.UTF8.GetBytes(relativePath + "\n");
            payload.Write(nameBytes, 0, nameBytes.Length);
            byte[] contentBytes = generatedFiles != null && generatedFiles.TryGetValue(relativePath, out byte[] generated)
                ? generated
                : File.ReadAllBytes(files[relativePath]);
            byte[] lengthBytes = BitConverter.GetBytes((long)contentBytes.Length);
            payload.Write(lengthBytes, 0, lengthBytes.Length);
            payload.Write(contentBytes, 0, contentBytes.Length);
        }

        using SHA256 hash = SHA256.Create();
        return BitConverter.ToString(hash.ComputeHash(payload.ToArray()))
            .Replace("-", string.Empty).ToLowerInvariant();
    }

    private static string ComputeCandidateHash(SkillCandidate candidate)
    {
        var files = EnumerateFilesWithoutLinks(candidate.SourcePath).ToDictionary(
            path => path.Substring(candidate.SourcePath.Length).TrimStart(Path.DirectorySeparatorChar)
                .Replace(Path.DirectorySeparatorChar, '/'),
            path => path,
            StringComparer.Ordinal);
        if (!string.IsNullOrEmpty(candidate.SharedPath))
        {
            foreach (string path in EnumerateFilesWithoutLinks(candidate.SharedPath))
            {
                string relative = path.Substring(candidate.SharedPath.Length).TrimStart(Path.DirectorySeparatorChar)
                    .Replace(Path.DirectorySeparatorChar, '/');
                files.Add(relative, path);
            }
        }
        IReadOnlyDictionary<string, byte[]> generatedFiles = string.IsNullOrEmpty(candidate.InventoryMarkdown)
            ? null
            : new Dictionary<string, byte[]>(StringComparer.Ordinal)
            {
                [GeneratedInventoryFileName] = new UTF8Encoding(false).GetBytes(candidate.InventoryMarkdown),
            };
        return ComputeFileMapHash(files, generatedFiles);
    }

    private static List<SkillCandidate> ReadCandidates(
        IEnumerable<string> packageRoots,
        ActionFitPackageSkillInstallResult result)
    {
        var candidates = new List<SkillCandidate>();
        var packageIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (string rootValue in packageRoots.Where(value => !string.IsNullOrWhiteSpace(value))
                     .Select(Path.GetFullPath).Distinct(StringComparer.OrdinalIgnoreCase)
                     .OrderBy(value => value, StringComparer.OrdinalIgnoreCase))
        {
            if (!Directory.Exists(rootValue))
            {
                result.Warnings.Add($"Registered skill package root was not found: {rootValue}");
                continue;
            }

            if (IsReparsePoint(rootValue))
            {
                result.Warnings.Add($"Linked skill package root was rejected: {rootValue}");
                continue;
            }

            PackageManifest package = ReadPackageManifest(rootValue);
            string packageId = package?.name;
            if (!PackageIdPattern.IsMatch(packageId ?? string.Empty))
            {
                result.Warnings.Add($"Skill package has an invalid ActionFit package ID: {rootValue}");
                continue;
            }

            if (!packageIds.Add(packageId))
            {
                result.Warnings.Add($"Duplicate installed skill package was ignored: {packageId}");
                continue;
            }

            string skillsRoot = Path.Combine(rootValue, "Skills~");
            string manifestPath = Path.Combine(skillsRoot, "manifest.json");
            if (!File.Exists(manifestPath)) continue;
            if (!IsSafeTree(skillsRoot, out string unsafeSkillPath))
            {
                result.Warnings.Add($"Linked or unreadable skill package content was rejected: {unsafeSkillPath}");
                continue;
            }

            SkillManifest manifest;
            try
            {
                manifest = JsonUtility.FromJson<SkillManifest>(File.ReadAllText(manifestPath, Encoding.UTF8));
            }
            catch (Exception exception)
            {
                result.Warnings.Add($"Invalid skill manifest for {packageId}: {exception.Message}");
                continue;
            }

            if (manifest == null
                || (manifest.schemaVersion != LegacyManifestSchemaVersion
                    && manifest.schemaVersion != CurrentManifestSchemaVersion)
                || manifest.skills == null || manifest.skills.Length == 0)
            {
                result.Warnings.Add($"Unsupported or incomplete skill manifest for {packageId}");
                continue;
            }

            if (manifest.schemaVersion == CurrentManifestSchemaVersion
                && !ValidateSchemaV2Manifest(packageId, manifest, result))
            {
                continue;
            }

            var registeredTargets = new HashSet<string>(StringComparer.Ordinal);
            var packageCandidates = new List<SkillCandidate>();
            foreach (SkillManifestEntry skill in manifest.skills)
            {
                if (skill == null || !SkillNamePattern.IsMatch(skill.name ?? string.Empty))
                {
                    result.Warnings.Add($"Invalid skill name was rejected for {packageId}: {skill?.name ?? "<missing>"}");
                    continue;
                }

                if (skill.agents == null || skill.agents.Length == 0)
                {
                    result.Warnings.Add($"Skill has no registered agents: {packageId}/{skill.name}");
                    continue;
                }

                foreach (string agent in skill.agents)
                {
                    if (!TryResolveAgent(agent, out string sourceDirectory, out string targetDirectory))
                    {
                        result.Warnings.Add($"Unsupported skill agent was rejected: {packageId}/{skill.name}/{agent}");
                        continue;
                    }

                    string targetRelativePath = $"{targetDirectory}/skills/{skill.name}";
                    if (!registeredTargets.Add(targetRelativePath))
                    {
                        result.Warnings.Add($"Duplicate skill registration was rejected: {packageId}/{targetRelativePath}");
                        continue;
                    }

                    string sourcePath = Path.Combine(skillsRoot, sourceDirectory, skill.name);
                    string sharedPath = skill.includeShared ? Path.Combine(skillsRoot, "Shared") : null;
                    if (!ValidateSkillSource(
                            packageId,
                            skill.name,
                            sourcePath,
                            sharedPath,
                            result,
                            out string description))
                    {
                        continue;
                    }

                    packageCandidates.Add(new SkillCandidate(
                        packageId,
                        package?.displayName,
                        package?.description,
                        agent,
                        skill.name,
                        description,
                        skill.access,
                        sourcePath,
                        sharedPath,
                        targetRelativePath,
                        manifest.schemaVersion == CurrentManifestSchemaVersion
                        && string.Equals(skill.name, manifest.helpSkill, StringComparison.Ordinal)));
                }
            }

            if (manifest.schemaVersion == CurrentManifestSchemaVersion)
            {
                SkillCandidate reservedInventory = packageCandidates.FirstOrDefault(candidate => candidate.IsHelp
                    && (File.Exists(Path.Combine(candidate.SourcePath, GeneratedInventoryFileName))
                        || (!string.IsNullOrEmpty(candidate.SharedPath)
                            && File.Exists(Path.Combine(candidate.SharedPath, GeneratedInventoryFileName)))));
                if (reservedInventory != null)
                {
                    result.Warnings.Add($"Schema v2 package source uses reserved {GeneratedInventoryFileName}: {packageId}");
                    continue;
                }
                int registeredCandidateCount = manifest.skills.Sum(skill => skill?.agents?.Length ?? 0);
                if (packageCandidates.Count != registeredCandidateCount)
                {
                    result.Warnings.Add($"Schema v2 package was rejected because one or more skill sources are invalid: {packageId}");
                    continue;
                }
                PopulateHelpInventories(packageCandidates);
            }
            candidates.AddRange(packageCandidates);
        }

        return candidates;
    }

    private static bool ValidateSkillSource(
        string packageId,
        string skillName,
        string sourcePath,
        string sharedPath,
        ActionFitPackageSkillInstallResult result,
        out string description)
    {
        description = null;
        if (!Directory.Exists(sourcePath))
        {
            result.Warnings.Add($"Registered skill source was not found: {packageId}/{skillName} at {sourcePath}");
            return false;
        }

        if (!IsSafeTree(sourcePath, out string unsafePath))
        {
            result.Warnings.Add($"Linked skill source entry was rejected: {unsafePath}");
            return false;
        }

        if (sharedPath != null)
        {
            if (!Directory.Exists(sharedPath))
            {
                result.Warnings.Add($"Registered shared skill source was not found: {packageId}/{skillName}");
                return false;
            }
            if (!IsSafeTree(sharedPath, out unsafePath))
            {
                result.Warnings.Add($"Linked shared skill source entry was rejected: {unsafePath}");
                return false;
            }

            var sourceFiles = new HashSet<string>(RelativeFilePaths(sourcePath), StringComparer.Ordinal);
            string collision = RelativeFilePaths(sharedPath).FirstOrDefault(sourceFiles.Contains);
            if (collision != null)
            {
                result.Warnings.Add(
                    $"Shared skill source collides with agent source for {packageId}/{skillName}: {collision}");
                return false;
            }
        }

        string skillPath = Path.Combine(sourcePath, "SKILL.md");
        if (!File.Exists(skillPath))
        {
            result.Warnings.Add($"Registered skill is missing SKILL.md: {packageId}/{skillName}");
            return false;
        }

        string text = File.ReadAllText(skillPath, Encoding.UTF8).Replace("\r\n", "\n");
        if (!TryReadSkillFrontmatter(text, out string declaredName, out description)
            || !string.Equals(declaredName, skillName, StringComparison.Ordinal)
            || string.IsNullOrWhiteSpace(description))
        {
            result.Warnings.Add($"SKILL.md frontmatter is invalid for {packageId}/{skillName}");
            return false;
        }

        return true;
    }

    private static bool ValidateSchemaV2Manifest(
        string packageId,
        SkillManifest manifest,
        ActionFitPackageSkillInstallResult result)
    {
        bool valid = true;
        if (!SkillNamePattern.IsMatch(manifest.skillPrefix ?? string.Empty))
        {
            result.Warnings.Add($"Schema v2 skillPrefix is invalid for {packageId}");
            valid = false;
        }

        string expectedHelp = string.IsNullOrWhiteSpace(manifest.skillPrefix)
            ? null
            : manifest.skillPrefix + "-help";
        if (!string.Equals(manifest.helpSkill, expectedHelp, StringComparison.Ordinal))
        {
            result.Warnings.Add($"Schema v2 helpSkill must be {expectedHelp ?? "<skillPrefix>-help"} for {packageId}");
            valid = false;
        }

        var names = new HashSet<string>(StringComparer.Ordinal);
        var relatedAgents = new HashSet<string>(StringComparer.Ordinal);
        SkillManifestEntry help = null;
        int helpCount = 0;
        foreach (SkillManifestEntry skill in manifest.skills)
        {
            if (skill == null)
            {
                result.Warnings.Add($"Schema v2 contains an empty skill registration for {packageId}");
                valid = false;
                continue;
            }
            if (!SkillNamePattern.IsMatch(skill.name ?? string.Empty))
            {
                result.Warnings.Add($"Schema v2 skill name is invalid: {packageId}/{skill.name ?? "<missing>"}");
                valid = false;
            }
            if (!names.Add(skill.name ?? string.Empty))
            {
                result.Warnings.Add($"Schema v2 skill name is registered more than once: {packageId}/{skill.name}");
                valid = false;
            }
            if (!string.IsNullOrEmpty(manifest.skillPrefix)
                && !(skill.name ?? string.Empty).StartsWith(manifest.skillPrefix + "-", StringComparison.Ordinal))
            {
                result.Warnings.Add($"Schema v2 skill must use prefix {manifest.skillPrefix}: {packageId}/{skill.name}");
                valid = false;
            }
            if (skill.access != "read-only" && skill.access != "write-capable")
            {
                result.Warnings.Add($"Schema v2 skill access is invalid: {packageId}/{skill.name}");
                valid = false;
            }

            var agents = new HashSet<string>(StringComparer.Ordinal);
            if (skill.agents == null || skill.agents.Length == 0)
            {
                result.Warnings.Add($"Schema v2 skill has no registered agents: {packageId}/{skill.name}");
                valid = false;
            }
            foreach (string agent in skill.agents ?? Array.Empty<string>())
            {
                if (!agents.Add(agent))
                {
                    result.Warnings.Add($"Schema v2 skill agent is registered more than once: {packageId}/{skill.name}/{agent}");
                    valid = false;
                }
                if (agent != "codex" && agent != "claude")
                {
                    result.Warnings.Add($"Schema v2 skill agent is unsupported: {packageId}/{skill.name}/{agent}");
                    valid = false;
                }
                if (!string.Equals(skill.name, manifest.helpSkill, StringComparison.Ordinal)) relatedAgents.Add(agent);
            }

            if (string.Equals(skill.name, manifest.helpSkill, StringComparison.Ordinal))
            {
                help = skill;
                helpCount++;
            }
        }

        if (help == null || helpCount != 1)
        {
            result.Warnings.Add($"Schema v2 help skill is not registered for {packageId}: {manifest.helpSkill}");
            return false;
        }
        if (help.access != "read-only")
        {
            result.Warnings.Add($"Schema v2 help skill must be read-only for {packageId}: {manifest.helpSkill}");
            valid = false;
        }

        var helpAgents = new HashSet<string>(help.agents ?? Array.Empty<string>(), StringComparer.Ordinal);
        if (!relatedAgents.IsSubsetOf(helpAgents))
        {
            result.Warnings.Add($"Schema v2 help skill must cover every registered agent for {packageId}");
            valid = false;
        }
        return valid;
    }

    private static void PopulateHelpInventories(IEnumerable<SkillCandidate> packageCandidates)
    {
        List<SkillCandidate> candidates = packageCandidates.ToList();
        foreach (SkillCandidate help in candidates.Where(candidate => candidate.IsHelp))
        {
            List<SkillCandidate> related = candidates
                .Where(candidate => string.Equals(candidate.Agent, help.Agent, StringComparison.Ordinal))
                .OrderBy(candidate => candidate.SkillName, StringComparer.Ordinal)
                .ToList();
            help.InventoryMarkdown = BuildPackageSkillsMarkdown(help, related);
        }
    }

    private static string BuildPackageSkillsMarkdown(SkillCandidate help, IEnumerable<SkillCandidate> related)
    {
        string displayName = string.IsNullOrWhiteSpace(help.PackageDisplayName)
            ? help.PackageId
            : help.PackageDisplayName.Trim();
        string summary = string.IsNullOrWhiteSpace(help.PackageDescription)
            ? "No package description was provided."
            : help.PackageDescription.Trim();
        var builder = new StringBuilder();
        builder.AppendLine("# Package Skills");
        builder.AppendLine();
        builder.AppendLine("Generated by Custom Package Manager from the installed package manifest and SKILL.md frontmatter. Do not edit this managed file.");
        builder.AppendLine();
        builder.AppendLine("## Package");
        builder.AppendLine();
        builder.AppendLine($"- Package ID: `{help.PackageId}`");
        builder.AppendLine($"- Display name: {EscapeMarkdown(displayName)}");
        builder.AppendLine($"- Summary: {EscapeMarkdown(summary)}");
        builder.AppendLine();
        builder.AppendLine("## Related Skills");
        builder.AppendLine();
        builder.AppendLine("| Invocation | Access | Description and when to use |");
        builder.AppendLine("| --- | --- | --- |");
        foreach (SkillCandidate skill in related)
        {
            builder.AppendLine($"| `${skill.SkillName}` | {skill.Access} | {EscapeMarkdown(skill.Description)} |");
        }
        builder.AppendLine();
        builder.AppendLine("Invoke a skill with its exact `$name`. Read that skill's SKILL.md for its full workflow and safety boundaries.");
        return builder.ToString();
    }

    private static string EscapeMarkdown(string value)
    {
        return (value ?? string.Empty)
            .Replace("\r", " ")
            .Replace("\n", " ")
            .Replace("|", "\\|");
    }

    private static bool TryReadSkillFrontmatter(string text, out string name, out string description)
    {
        name = null;
        description = null;
        string[] lines = text.Split('\n');
        if (lines.Length < 3 || lines[0].Trim() != "---") return false;

        for (int index = 1; index < lines.Length; index++)
        {
            string line = lines[index];
            if (line.Trim() == "---") return !string.IsNullOrWhiteSpace(name) && !string.IsNullOrWhiteSpace(description);
            int separator = line.IndexOf(':');
            if (separator <= 0) continue;
            string key = line.Substring(0, separator).Trim();
            string value = line.Substring(separator + 1).Trim().Trim('"', '\'');
            if (key == "name") name = value;
            if (key == "description") description = value;
        }

        return false;
    }

    private static void InspectCandidate(
        SkillCandidate candidate,
        string projectRoot,
        SkillInstallState state,
        ActionFitPackageSkillStatus status)
    {
        string targetPath = Path.Combine(projectRoot, ToNativePath(candidate.TargetRelativePath));
        if (TryFindLinkedPath(projectRoot, targetPath, out _)
            || File.Exists(targetPath))
        {
            status.Preserved++;
            return;
        }

        if (!Directory.Exists(targetPath))
        {
            status.Missing++;
            return;
        }

        if (!IsSafeTree(targetPath, out _))
        {
            status.Preserved++;
            return;
        }

        SkillInstallEntry entry = state.Find(candidate.TargetRelativePath);
        string currentHash = ComputeDirectoryHash(targetPath);
        if (entry != null && string.IsNullOrWhiteSpace(entry.packageId)
                          && string.Equals(currentHash, entry.installedHash, StringComparison.Ordinal))
        {
            entry.SetOwner(candidate);
        }

        if (entry == null || !entry.HasOwner(candidate))
        {
            status.Preserved++;
            return;
        }

        string sourceHash = ComputeCandidateHash(candidate);
        if (string.Equals(currentHash, sourceHash, StringComparison.Ordinal))
        {
            status.Current++;
            return;
        }

        if (string.Equals(currentHash, entry.installedHash, StringComparison.Ordinal))
        {
            status.UpdateAvailable++;
            return;
        }

        status.Preserved++;
    }

    private static void InstallCandidate(
        SkillCandidate candidate,
        string projectRoot,
        string statePath,
        string tempRoot,
        SkillInstallState state,
        ActionFitPackageSkillInstallResult result)
    {
        string stagedPath = Path.Combine(tempRoot, "stage-" + Guid.NewGuid().ToString("N"));
        try
        {
            CopyDirectory(candidate.SourcePath, stagedPath);
            if (!string.IsNullOrEmpty(candidate.SharedPath)) CopyDirectory(candidate.SharedPath, stagedPath);
            if (!string.IsNullOrEmpty(candidate.InventoryMarkdown))
            {
                File.WriteAllText(
                    Path.Combine(stagedPath, GeneratedInventoryFileName),
                    candidate.InventoryMarkdown,
                    new UTF8Encoding(false));
            }
            string stagedHash = ComputeDirectoryHash(stagedPath);
            string targetPath = Path.Combine(projectRoot, ToNativePath(candidate.TargetRelativePath));
            SkillInstallEntry entry = state.Find(candidate.TargetRelativePath);

            if (TryFindLinkedPath(projectRoot, targetPath, out string linkedPath))
            {
                result.Warnings.Add($"Preserved linked skill path: {candidate.TargetRelativePath} ({linkedPath})");
                return;
            }

            if (entry != null && !string.IsNullOrWhiteSpace(entry.packageId) && !entry.HasOwner(candidate))
            {
                result.Warnings.Add($"Managed skill ownership conflict was preserved: {candidate.TargetRelativePath}");
                return;
            }

            if (File.Exists(targetPath))
            {
                result.Warnings.Add($"Preserved file at skill target path: {candidate.TargetRelativePath}");
                return;
            }

            if (Directory.Exists(targetPath) && IsReparsePoint(targetPath))
            {
                result.Warnings.Add($"Preserved linked skill directory: {candidate.TargetRelativePath}");
                return;
            }

            if (Directory.Exists(targetPath) && !IsSafeTree(targetPath, out linkedPath))
            {
                result.Warnings.Add($"Preserved linked or unreadable skill: {candidate.TargetRelativePath} ({linkedPath})");
                return;
            }

            if (entry != null && string.IsNullOrWhiteSpace(entry.packageId))
            {
                if (!Directory.Exists(targetPath)
                    || string.Equals(ComputeDirectoryHash(targetPath), entry.installedHash, StringComparison.Ordinal))
                {
                    entry.SetOwner(candidate);
                }
            }

            if (!Directory.Exists(targetPath))
            {
                state.Set(candidate, stagedHash);
                SaveState(statePath, state);
                ReplaceDirectory(stagedPath, targetPath, tempRoot);
                result.Installed++;
                return;
            }

            string currentHash = ComputeDirectoryHash(targetPath);
            if (entry == null || string.IsNullOrWhiteSpace(entry.packageId))
            {
                result.Warnings.Add($"Preserved user-managed or modified skill: {candidate.TargetRelativePath}");
                return;
            }

            if (!string.Equals(currentHash, entry.installedHash, StringComparison.Ordinal))
            {
                if (string.Equals(currentHash, stagedHash, StringComparison.Ordinal))
                {
                    state.Set(candidate, stagedHash);
                    result.Unchanged++;
                    return;
                }

                result.Warnings.Add($"Preserved user-managed or modified skill: {candidate.TargetRelativePath}");
                return;
            }

            if (string.Equals(currentHash, stagedHash, StringComparison.Ordinal))
            {
                state.Set(candidate, stagedHash);
                result.Unchanged++;
                return;
            }

            ReplaceDirectory(stagedPath, targetPath, tempRoot);
            state.Set(candidate, stagedHash);
            result.Updated++;
        }
        finally
        {
            if (Directory.Exists(stagedPath)) Directory.Delete(stagedPath, true);
        }
    }

    private static bool TryResolveAgent(string agent, out string sourceDirectory, out string targetDirectory)
    {
        sourceDirectory = null;
        targetDirectory = null;
        if (agent == "codex")
        {
            sourceDirectory = "Codex";
            targetDirectory = ".agents";
            return true;
        }
        if (agent == "claude")
        {
            sourceDirectory = "Claude";
            targetDirectory = ".claude";
            return true;
        }
        return false;
    }

    private static PackageManifest ReadPackageManifest(string packageRoot)
    {
        string path = Path.Combine(packageRoot, "package.json");
        if (!File.Exists(path) || IsReparsePoint(path)) return null;
        try
        {
            return JsonUtility.FromJson<PackageManifest>(File.ReadAllText(path, Encoding.UTF8));
        }
        catch
        {
            return null;
        }
    }

    private static bool IsSafeTree(string root, out string unsafePath)
    {
        unsafePath = null;
        try
        {
            if (IsReparsePoint(root))
            {
                unsafePath = root;
                return false;
            }
            EnumerateFilesWithoutLinks(root);
            return true;
        }
        catch (Exception exception)
        {
            unsafePath = $"{root} ({exception.Message})";
            return false;
        }
    }

    private static List<string> EnumerateFilesWithoutLinks(string root)
    {
        var files = new List<string>();
        var pending = new Stack<string>();
        pending.Push(root);
        while (pending.Count > 0)
        {
            string directory = pending.Pop();
            foreach (string path in Directory.EnumerateFileSystemEntries(directory))
            {
                FileAttributes attributes = File.GetAttributes(path);
                if ((attributes & FileAttributes.ReparsePoint) != 0)
                    throw new InvalidOperationException(path);
                if ((attributes & FileAttributes.Directory) != 0) pending.Push(path);
                else files.Add(path);
            }
        }
        return files;
    }

    private static IEnumerable<string> RelativeFilePaths(string root)
    {
        return EnumerateFilesWithoutLinks(root).Select(path =>
            path.Substring(root.Length).TrimStart(Path.DirectorySeparatorChar)
                .Replace(Path.DirectorySeparatorChar, '/'));
    }

    private static void CopyDirectory(string source, string destination)
    {
        Directory.CreateDirectory(destination);
        foreach (string path in Directory.EnumerateFileSystemEntries(source))
        {
            FileAttributes attributes = File.GetAttributes(path);
            if ((attributes & FileAttributes.ReparsePoint) != 0)
                throw new InvalidOperationException($"Linked skill source entry was rejected: {path}");
            string target = Path.Combine(destination, Path.GetFileName(path));
            if ((attributes & FileAttributes.Directory) != 0)
            {
                CopyDirectory(path, target);
                continue;
            }
            if (File.Exists(target))
                throw new InvalidOperationException($"Shared skill source collides with agent source: {target}");
            File.Copy(path, target);
        }
    }

    private static void ReplaceDirectory(string stagedPath, string targetPath, string tempRoot)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(targetPath) ?? throw new InvalidOperationException());
        if (!Directory.Exists(targetPath))
        {
            Directory.Move(stagedPath, targetPath);
            return;
        }

        string backupPath = Path.Combine(tempRoot, "backup-" + Guid.NewGuid().ToString("N"));
        Directory.Move(targetPath, backupPath);
        try
        {
            Directory.Move(stagedPath, targetPath);
            Directory.Delete(backupPath, true);
        }
        catch
        {
            if (Directory.Exists(targetPath)) Directory.Delete(targetPath, true);
            if (Directory.Exists(backupPath)) Directory.Move(backupPath, targetPath);
            throw;
        }
    }

    private static void ValidateProjectRoot(string projectRoot)
    {
        if (string.IsNullOrWhiteSpace(projectRoot) || !Directory.Exists(projectRoot))
            throw new DirectoryNotFoundException($"Project root was not found: {projectRoot}");
    }

    private static void ValidateManagedPath(string projectRoot, string path, string label)
    {
        if (string.IsNullOrWhiteSpace(path)) throw new ArgumentException($"{label} path is required.");
        string root = Path.GetFullPath(projectRoot).TrimEnd(Path.DirectorySeparatorChar);
        string fullPath = Path.GetFullPath(path);
        if (!fullPath.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.Ordinal))
            throw new InvalidOperationException($"{label} path must stay inside the project: {fullPath}");
        if (TryFindLinkedPath(root, fullPath, out string linkedPath))
            throw new InvalidOperationException($"{label} path contains a linked entry: {linkedPath}");
    }

    private static bool TryFindLinkedPath(string root, string target, out string linkedPath)
    {
        linkedPath = null;
        string fullRoot = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar);
        string fullTarget = Path.GetFullPath(target);
        if (!fullTarget.StartsWith(fullRoot + Path.DirectorySeparatorChar, StringComparison.Ordinal))
        {
            linkedPath = fullTarget;
            return true;
        }

        string relative = fullTarget.Substring(fullRoot.Length).TrimStart(Path.DirectorySeparatorChar);
        string current = fullRoot;
        foreach (string part in relative.Split(Path.DirectorySeparatorChar))
        {
            if (string.IsNullOrEmpty(part)) continue;
            current = Path.Combine(current, part);
            if (!File.Exists(current) && !Directory.Exists(current)) break;
            if (IsReparsePoint(current))
            {
                linkedPath = current;
                return true;
            }
        }
        return false;
    }

    private static bool IsReparsePoint(string path)
    {
        return (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0;
    }

    private static string ToNativePath(string path)
    {
        return path.Replace('/', Path.DirectorySeparatorChar);
    }

    private static void ImportLegacyState(
        string legacyStatePath,
        SkillInstallState state,
        ActionFitPackageSkillInstallResult result)
    {
        if (string.IsNullOrWhiteSpace(legacyStatePath) || !File.Exists(legacyStatePath)) return;
        SkillInstallState legacy = LoadState(legacyStatePath, result);
        foreach (SkillInstallEntry entry in legacy.EntriesSnapshot())
        {
            if (state.Find(entry.targetPath) == null) state.entries.Add(entry.Clone());
        }
    }

    private static SkillInstallState LoadState(
        string statePath,
        ActionFitPackageSkillInstallResult result)
    {
        if (string.IsNullOrWhiteSpace(statePath) || !File.Exists(statePath)) return new SkillInstallState();
        try
        {
            return JsonUtility.FromJson<SkillInstallState>(File.ReadAllText(statePath, Encoding.UTF8))
                   ?? new SkillInstallState();
        }
        catch (Exception exception)
        {
            result?.Warnings.Add($"Invalid managed-skill state was ignored: {exception.Message}");
            return new SkillInstallState();
        }
    }

    private static void SaveState(string statePath, SkillInstallState state)
    {
        string directory = Path.GetDirectoryName(statePath) ?? throw new InvalidOperationException();
        Directory.CreateDirectory(directory);
        string temporaryPath = statePath + ".tmp";
        File.WriteAllText(temporaryPath, JsonUtility.ToJson(state, true), new UTF8Encoding(false));
        if (File.Exists(statePath)) File.Delete(statePath);
        File.Move(temporaryPath, statePath);
    }

    private static void DeleteDirectoryIfEmpty(string path)
    {
        if (!string.IsNullOrWhiteSpace(path) && Directory.Exists(path)
            && !Directory.EnumerateFileSystemEntries(path).Any())
        {
            Directory.Delete(path);
        }
    }

    private static void DeleteStagingDirectories(string tempRoot)
    {
        if (!Directory.Exists(tempRoot)) return;
        foreach (string path in Directory.GetDirectories(tempRoot, "stage-*", SearchOption.TopDirectoryOnly))
            Directory.Delete(path, true);
    }

    [Serializable]
    private sealed class PackageManifest
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
        public SkillManifestEntry[] skills;
    }

    [Serializable]
    private sealed class SkillManifestEntry
    {
        public string name;
        public string[] agents;
        public bool includeShared;
        public string access;
    }

    [Serializable]
    private sealed class SkillInstallState
    {
        public int schemaVersion = StateSchemaVersion;
        public int autoInstallEnabled = 1;
        public List<SkillInstallEntry> entries = new List<SkillInstallEntry>();

        public SkillInstallEntry Find(string targetPath)
        {
            if (entries == null) entries = new List<SkillInstallEntry>();
            return entries.FirstOrDefault(entry => entry.targetPath == targetPath);
        }

        public void Set(SkillCandidate candidate, string installedHash)
        {
            SkillInstallEntry entry = Find(candidate.TargetRelativePath);
            if (entry == null)
            {
                entry = new SkillInstallEntry { targetPath = candidate.TargetRelativePath };
                entries.Add(entry);
            }
            entry.SetOwner(candidate);
            entry.installedHash = installedHash;
        }

        public void Remove(string targetPath)
        {
            if (entries == null) entries = new List<SkillInstallEntry>();
            entries.RemoveAll(entry => entry.targetPath == targetPath);
        }

        public List<SkillInstallEntry> EntriesSnapshot()
        {
            if (entries == null) entries = new List<SkillInstallEntry>();
            return entries.ToList();
        }
    }

    [Serializable]
    private sealed class SkillInstallEntry
    {
        public string packageId;
        public string agent;
        public string skillName;
        public string targetPath;
        public string installedHash;

        public void SetOwner(SkillCandidate candidate)
        {
            packageId = candidate.PackageId;
            agent = candidate.Agent;
            skillName = candidate.SkillName;
        }

        public bool HasOwner(SkillCandidate candidate)
        {
            return string.Equals(packageId, candidate.PackageId, StringComparison.Ordinal)
                   && string.Equals(agent, candidate.Agent, StringComparison.Ordinal)
                   && string.Equals(skillName, candidate.SkillName, StringComparison.Ordinal);
        }

        public SkillInstallEntry Clone()
        {
            return new SkillInstallEntry
            {
                packageId = packageId,
                agent = agent,
                skillName = skillName,
                targetPath = targetPath,
                installedHash = installedHash,
            };
        }
    }

    private sealed class SkillCandidate
    {
        public SkillCandidate(
            string packageId,
            string packageDisplayName,
            string packageDescription,
            string agent,
            string skillName,
            string description,
            string access,
            string sourcePath,
            string sharedPath,
            string targetRelativePath,
            bool isHelp)
        {
            PackageId = packageId;
            PackageDisplayName = packageDisplayName;
            PackageDescription = packageDescription;
            Agent = agent;
            SkillName = skillName;
            Description = description;
            Access = string.IsNullOrWhiteSpace(access) ? "unspecified" : access;
            SourcePath = sourcePath;
            SharedPath = sharedPath;
            TargetRelativePath = targetRelativePath;
            IsHelp = isHelp;
        }

        public string PackageId { get; }
        public string PackageDisplayName { get; }
        public string PackageDescription { get; }
        public string Agent { get; }
        public string SkillName { get; }
        public string Description { get; }
        public string Access { get; }
        public string SourcePath { get; }
        public string SharedPath { get; }
        public string TargetRelativePath { get; }
        public bool IsHelp { get; }
        public string InventoryMarkdown { get; set; }
    }
}

[InitializeOnLoad]
public static class ActionFitPackageSkillBootstrap
{
    private const string StateRelativePath = "UserSettings/ActionFitPackageManager/skill-install-state.json";
    private const string LegacyStateRelativePath = "UserSettings/AIJira/skill-install-state.json";
    private const string TempRelativePath = "Temp/ActionFitPackageSkills";

    static ActionFitPackageSkillBootstrap()
    {
        Events.registeredPackages += OnRegisteredPackages;
        if (!Application.isBatchMode) EditorApplication.delayCall += InstallAutomatically;
    }

    public static ActionFitPackageSkillInstallResult InstallOrRefresh()
    {
        string projectRoot = ProjectRootPath;
        ActionFitPackageSkillInstallResult result = ActionFitPackageSkillInstallService.InstallOrRefresh(
            FindRegisteredPackageRoots(projectRoot),
            projectRoot,
            Path.Combine(projectRoot, StateRelativePath),
            Path.Combine(projectRoot, TempRelativePath),
            Path.Combine(projectRoot, LegacyStateRelativePath));
        ActionFitPackageManagerRefreshSignal.Request();
        return result;
    }

    public static ActionFitPackageSkillInstallResult RemoveManaged()
    {
        string projectRoot = ProjectRootPath;
        ActionFitPackageSkillInstallResult result = ActionFitPackageSkillInstallService.RemoveManaged(
            projectRoot,
            Path.Combine(projectRoot, StateRelativePath),
            Path.Combine(projectRoot, TempRelativePath));
        ActionFitPackageManagerRefreshSignal.Request();
        return result;
    }

    public static ActionFitPackageSkillInspectionResult InspectRegisteredSkills()
    {
        string projectRoot = ProjectRootPath;
        return ActionFitPackageSkillInstallService.Inspect(
            FindRegisteredPackageRoots(projectRoot),
            projectRoot,
            Path.Combine(projectRoot, StateRelativePath),
            Path.Combine(projectRoot, LegacyStateRelativePath));
    }

    public static List<string> FindRegisteredPackageRoots(string projectRoot)
    {
        var byPackageId = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        AddPackageRoots(byPackageId, Path.Combine(projectRoot, "Packages"), "com.actionfit.*", true);
        AddPackageRoots(byPackageId, Path.Combine(projectRoot, "Library", "PackageCache"), "com.actionfit.*@*", false);
        return byPackageId.OrderBy(pair => pair.Key, StringComparer.OrdinalIgnoreCase)
            .Select(pair => pair.Value).ToList();
    }

    public static void LogResult(string operation, ActionFitPackageSkillInstallResult result)
    {
        if (result.Installed + result.Updated + result.Removed > 0)
        {
            Debug.Log($"[ActionFitPackageManager] Agent skill {operation}: installed={result.Installed}, "
                      + $"updated={result.Updated}, removed={result.Removed}, unchanged={result.Unchanged}");
        }
        foreach (string warning in result.Warnings) Debug.LogWarning("[ActionFitPackageManager] " + warning);
    }

    private static void OnRegisteredPackages(PackageRegistrationEventArgs _)
    {
        if (Application.isBatchMode) return;
        EditorApplication.delayCall -= InstallAutomatically;
        EditorApplication.delayCall += InstallAutomatically;
    }

    private static void InstallAutomatically()
    {
        try
        {
            string projectRoot = ProjectRootPath;
            string statePath = Path.Combine(projectRoot, StateRelativePath);
            string legacyStatePath = Path.Combine(projectRoot, LegacyStateRelativePath);
            if (!ActionFitPackageSkillInstallService.IsAutoInstallEnabled(projectRoot, statePath, legacyStatePath))
                return;
            LogResult("automatic install", InstallOrRefresh());
        }
        catch (Exception exception)
        {
            Debug.LogWarning($"[ActionFitPackageManager] Agent skill installation failed: {exception}");
        }
    }

    private static void AddPackageRoots(
        IDictionary<string, string> byPackageId,
        string root,
        string pattern,
        bool replaceExisting)
    {
        if (!Directory.Exists(root)) return;
        foreach (string path in Directory.GetDirectories(root, pattern, SearchOption.TopDirectoryOnly))
        {
            string manifestPath = Path.Combine(path, "Skills~", "manifest.json");
            if (!File.Exists(manifestPath)) continue;
            string packageId = Path.GetFileName(path).Split('@')[0];
            if (replaceExisting || !byPackageId.ContainsKey(packageId)) byPackageId[packageId] = path;
        }
    }

    private static string ProjectRootPath => Path.GetFullPath(Path.Combine(Application.dataPath, ".."));
}
#endif
