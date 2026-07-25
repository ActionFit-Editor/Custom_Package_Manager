#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using UnityEngine;

public sealed class ActionFitAgentSkillProfileOperation
{
    public string Action { get; internal set; }
    public string PackageId { get; internal set; }
    public string Agent { get; internal set; }
    public string SkillName { get; internal set; }
    public string SourcePath { get; internal set; }
    public string DestinationPath { get; internal set; }
    public string State { get; internal set; }
    public string Reason { get; internal set; }
    internal string ExpectedHash { get; set; }
}

public sealed class ActionFitAgentSkillProfileResult
{
    public string ProfileName { get; internal set; }
    public int Activate { get; internal set; }
    public int Deactivate { get; internal set; }
    public int Current { get; internal set; }
    public int Inactive { get; internal set; }
    public int Missing { get; internal set; }
    public int Preserved { get; internal set; }
    public List<ActionFitAgentSkillProfileOperation> Operations { get; } =
        new List<ActionFitAgentSkillProfileOperation>();

    public string Summary =>
        $"Profile: {ProfileName}\nActivate: {Activate}\nDeactivate: {Deactivate}\n"
        + $"Current: {Current}\nInactive: {Inactive}\nMissing: {Missing}\nPreserved: {Preserved}";

    public string ExactPreview
    {
        get
        {
            var lines = new List<string> { Summary };
            lines.AddRange(Operations.Select(operation =>
                $"{operation.Action}: {operation.SourcePath} -> {operation.DestinationPath}"
                + (string.IsNullOrWhiteSpace(operation.Reason) ? string.Empty : $" ({operation.Reason})")));
            return string.Join("\n", lines);
        }
    }
}

public static class ActionFitAgentSkillProfileService
{
    public const string ProfileRelativePath = "ProjectSettings/ActionFitAgentSkillProfile.json";
    public const string StateRelativePath =
        "UserSettings/ActionFitPackageManager/skill-install-state.json";
    public const string InactiveRelativePath =
        "UserSettings/ActionFitPackageManager/InactiveSkills";
    public const string JournalRelativePath =
        "UserSettings/ActionFitPackageManager/ProfileTransactions";

    public static bool IsPackageActive(string projectRoot, string packageId)
    {
        string profilePath = Path.Combine(projectRoot, ProfileRelativePath);
        if (!File.Exists(profilePath)) return true;
        ProfileConfig config = LoadProfile(profilePath);
        ProfileDefinition profile = ResolveProfile(config, config.activeProfile);
        return profile.all || (profile.packageIds ?? Array.Empty<string>())
            .Contains(packageId, StringComparer.OrdinalIgnoreCase);
    }

    public static ActionFitAgentSkillProfileResult Preview(
        string projectRoot,
        string profileName = null)
    {
        ValidateProjectRoot(projectRoot);
        string profilePath = ManagedPath(projectRoot, ProfileRelativePath);
        string statePath = ManagedPath(projectRoot, StateRelativePath);
        string inactiveRoot = ManagedPath(projectRoot, InactiveRelativePath);
        ProfileConfig config = LoadProfile(profilePath);
        ProfileDefinition profile = ResolveProfile(config, profileName ?? config.activeProfile);
        SkillState state = LoadJson<SkillState>(statePath, "managed skill state");
        var result = new ActionFitAgentSkillProfileResult { ProfileName = profile.name };

        foreach (SkillStateEntry entry in (state.entries ?? new List<SkillStateEntry>())
                     .Where(value => value != null && !string.IsNullOrWhiteSpace(value.targetPath))
                     .OrderBy(value => value.targetPath, StringComparer.Ordinal))
        {
            bool desiredActive = profile.all || (profile.packageIds ?? Array.Empty<string>())
                .Contains(entry.packageId, StringComparer.OrdinalIgnoreCase);
            string target = ManagedPath(projectRoot, entry.targetPath);
            string inactive = ManagedPath(inactiveRoot, entry.targetPath);
            AddPreviewOperation(projectRoot, entry, desiredActive, target, inactive, result);
        }
        return result;
    }

    public static ActionFitAgentSkillProfileResult Apply(
        string projectRoot,
        string profileName,
        string expectedExactPreview = null)
    {
        ActionFitAgentSkillProfileResult preview = Preview(projectRoot, profileName);
        if (!string.IsNullOrEmpty(expectedExactPreview)
            && !string.Equals(preview.ExactPreview, expectedExactPreview, StringComparison.Ordinal))
            throw new InvalidOperationException(
                "Agent skill profile state changed after preview. Preview and approve the exact plan again.");
        string profilePath = ManagedPath(projectRoot, ProfileRelativePath);
        string previousProfile = LoadProfile(profilePath).activeProfile;
        string journalRoot = ManagedPath(projectRoot, JournalRelativePath);
        Directory.CreateDirectory(journalRoot);
        string journalPath = Path.Combine(
            journalRoot,
            DateTime.UtcNow.ToString("yyyyMMddTHHmmssfff") + "-" + Guid.NewGuid().ToString("N") + ".json");
        var journal = new ProfileJournal
        {
            profileName = preview.ProfileName,
            previousProfileName = previousProfile,
            createdUtc = DateTime.UtcNow.ToString("O"),
            state = "prepared",
            operations = preview.Operations
                .Where(value => value.State == "ready")
                .Select(value => new ProfileJournalOperation
                {
                    action = value.Action,
                    sourcePath = Relative(projectRoot, value.SourcePath),
                    destinationPath = Relative(projectRoot, value.DestinationPath),
                    expectedHash = value.ExpectedHash,
                    completed = false,
                }).ToList(),
        };
        SaveJson(journalPath, journal);

        try
        {
            foreach (ProfileJournalOperation operation in journal.operations)
            {
                string source = ManagedPath(projectRoot, operation.sourcePath);
                string destination = ManagedPath(projectRoot, operation.destinationPath);
                Directory.CreateDirectory(Path.GetDirectoryName(destination)
                                          ?? throw new InvalidOperationException());
                Directory.Move(source, destination);
                operation.completed = true;
                SaveJson(journalPath, journal);
                DeleteDirectoryIfEmpty(Path.GetDirectoryName(source));
            }
            UpdateActiveProfile(profilePath, preview.ProfileName);
            journal.state = "applied";
            SaveJson(journalPath, journal);
            return preview;
        }
        catch
        {
            RollbackJournal(projectRoot, journal, journalPath);
            throw;
        }
    }

    public static ActionFitAgentSkillProfileResult Inspect(string projectRoot)
    {
        return Preview(projectRoot);
    }

    public static void ValidateReferencedPackages(
        string projectRoot,
        IEnumerable<string> registeredPackageIds)
    {
        string profilePath = ManagedPath(projectRoot, ProfileRelativePath);
        if (!File.Exists(profilePath)) return;
        ProfileConfig config = LoadProfile(profilePath);
        var registered = new HashSet<string>(
            registeredPackageIds ?? Array.Empty<string>(),
            StringComparer.OrdinalIgnoreCase);
        foreach (ProfileDefinition profile in config.profiles.Where(value => !value.all))
        {
            string[] missing = (profile.packageIds ?? Array.Empty<string>())
                .Where(value => !registered.Contains(value))
                .OrderBy(value => value, StringComparer.OrdinalIgnoreCase)
                .ToArray();
            if (missing.Length > 0)
                throw new InvalidOperationException(
                    $"Profile {profile.name} references packages without registered skills: "
                    + string.Join(", ", missing));
        }
    }

    public static void RollbackLast(string projectRoot)
    {
        string journalRoot = ManagedPath(projectRoot, JournalRelativePath);
        if (!Directory.Exists(journalRoot))
            throw new InvalidOperationException("No profile transaction journal exists.");
        string journalPath = Directory.GetFiles(journalRoot, "*.json", SearchOption.TopDirectoryOnly)
            .OrderByDescending(value => value, StringComparer.Ordinal)
            .FirstOrDefault();
        if (journalPath == null)
            throw new InvalidOperationException("No profile transaction journal exists.");
        ProfileJournal journal = LoadJson<ProfileJournal>(journalPath, "profile transaction journal");
        RollbackJournal(projectRoot, journal, journalPath);
    }

    private static void AddPreviewOperation(
        string projectRoot,
        SkillStateEntry entry,
        bool desiredActive,
        string target,
        string inactive,
        ActionFitAgentSkillProfileResult result)
    {
        string action = desiredActive ? "activate" : "deactivate";
        string source = desiredActive ? inactive : target;
        string destination = desiredActive ? target : inactive;
        var operation = new ActionFitAgentSkillProfileOperation
        {
            Action = action,
            PackageId = entry.packageId,
            Agent = entry.agent,
            SkillName = entry.skillName,
            SourcePath = source,
            DestinationPath = destination,
        };
        result.Operations.Add(operation);

        if (Directory.Exists(target) && Directory.Exists(inactive))
        {
            Preserve(operation, result, "active and inactive targets both exist");
            return;
        }
        if (File.Exists(target) || File.Exists(inactive))
        {
            Preserve(operation, result, "a file occupies a managed skill directory");
            return;
        }
        if (PathContainsLink(projectRoot, target) || PathContainsLink(projectRoot, inactive))
        {
            Preserve(operation, result, "linked or unreadable path");
            return;
        }

        if (!Directory.Exists(source))
        {
            if (Directory.Exists(destination))
            {
                operation.State = desiredActive ? "current" : "inactive";
                if (desiredActive) result.Current++;
                else result.Inactive++;
            }
            else
            {
                operation.State = "missing";
                operation.Reason = "automatic refresh can restore an active missing target";
                result.Missing++;
            }
            return;
        }

        string hash;
        try
        {
            hash = ActionFitPackageSkillInstallService.ComputeDirectoryHash(source);
        }
        catch (Exception exception)
        {
            Preserve(operation, result, "unreadable directory: " + exception.Message);
            return;
        }
        if (!string.Equals(hash, entry.installedHash, StringComparison.Ordinal))
        {
            Preserve(operation, result, "modified manager-owned target");
            return;
        }
        operation.State = "ready";
        operation.ExpectedHash = hash;
        if (desiredActive) result.Activate++;
        else result.Deactivate++;
    }

    private static void Preserve(
        ActionFitAgentSkillProfileOperation operation,
        ActionFitAgentSkillProfileResult result,
        string reason)
    {
        operation.State = "preserved";
        operation.Reason = reason;
        result.Preserved++;
    }

    private static void RollbackJournal(
        string projectRoot,
        ProfileJournal journal,
        string journalPath)
    {
        foreach (ProfileJournalOperation operation in (journal.operations ?? new List<ProfileJournalOperation>())
                     .Where(value => value.completed).Reverse())
        {
            string source = ManagedPath(projectRoot, operation.sourcePath);
            string destination = ManagedPath(projectRoot, operation.destinationPath);
            if (!Directory.Exists(destination) || Directory.Exists(source) || File.Exists(source))
                throw new InvalidOperationException(
                    $"Profile rollback is ambiguous: {operation.destinationPath} -> {operation.sourcePath}");
            string currentHash = ActionFitPackageSkillInstallService.ComputeDirectoryHash(destination);
            if (!string.Equals(currentHash, operation.expectedHash, StringComparison.Ordinal))
                throw new InvalidOperationException(
                    $"Profile rollback preserved a modified target: {operation.destinationPath}");
            Directory.CreateDirectory(Path.GetDirectoryName(source)
                                      ?? throw new InvalidOperationException());
            Directory.Move(destination, source);
            operation.completed = false;
            SaveJson(journalPath, journal);
            DeleteDirectoryIfEmpty(Path.GetDirectoryName(destination));
        }
        journal.state = "rolled-back";
        if (!string.IsNullOrWhiteSpace(journal.previousProfileName))
            UpdateActiveProfile(ManagedPath(projectRoot, ProfileRelativePath), journal.previousProfileName);
        SaveJson(journalPath, journal);
    }

    private static void UpdateActiveProfile(string path, string profileName)
    {
        ProfileConfig config = LoadProfile(path);
        ResolveProfile(config, profileName);
        config.activeProfile = profileName;
        SaveJson(path, config);
    }

    private static ProfileConfig LoadProfile(string path)
    {
        ProfileConfig config = LoadJson<ProfileConfig>(path, "agent skill profile");
        if (config.schemaVersion != 1)
            throw new InvalidOperationException("Agent skill profile schemaVersion must be 1.");
        if (config.profiles == null || config.profiles.Length == 0)
            throw new InvalidOperationException("Agent skill profile must define profiles.");
        var names = new HashSet<string>(StringComparer.Ordinal);
        foreach (ProfileDefinition profile in config.profiles)
        {
            if (profile == null || string.IsNullOrWhiteSpace(profile.name) || !names.Add(profile.name))
                throw new InvalidOperationException("Agent skill profile names must be non-empty and unique.");
            string[] packageIds = profile.packageIds ?? Array.Empty<string>();
            if (packageIds.Any(string.IsNullOrWhiteSpace)
                || packageIds.Distinct(StringComparer.OrdinalIgnoreCase).Count() != packageIds.Length)
                throw new InvalidOperationException($"Profile {profile.name} has invalid package IDs.");
        }
        ResolveProfile(config, config.activeProfile);
        return config;
    }

    private static ProfileDefinition ResolveProfile(ProfileConfig config, string profileName)
    {
        ProfileDefinition profile = (config.profiles ?? Array.Empty<ProfileDefinition>())
            .SingleOrDefault(value => string.Equals(value.name, profileName, StringComparison.Ordinal));
        return profile ?? throw new InvalidOperationException($"Unknown agent skill profile: {profileName}");
    }

    private static T LoadJson<T>(string path, string label) where T : class
    {
        if (!File.Exists(path)) throw new FileNotFoundException($"{label} was not found.", path);
        try
        {
            return JsonUtility.FromJson<T>(File.ReadAllText(path, Encoding.UTF8))
                   ?? throw new InvalidOperationException($"{label} is empty.");
        }
        catch (Exception exception) when (!(exception is InvalidOperationException))
        {
            throw new InvalidOperationException($"Invalid {label}: {exception.Message}", exception);
        }
    }

    private static void SaveJson<T>(string path, T value)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path) ?? throw new InvalidOperationException());
        string temporary = path + ".tmp";
        File.WriteAllText(temporary, JsonUtility.ToJson(value, true), new UTF8Encoding(false));
        if (File.Exists(path)) File.Delete(path);
        File.Move(temporary, path);
    }

    private static string ManagedPath(string root, string relative)
    {
        string fullRoot = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar);
        string fullPath = Path.GetFullPath(Path.Combine(fullRoot, relative.Replace('/', Path.DirectorySeparatorChar)));
        if (!fullPath.StartsWith(fullRoot + Path.DirectorySeparatorChar, StringComparison.Ordinal))
            throw new InvalidOperationException($"Managed profile path escapes its root: {relative}");
        return fullPath;
    }

    private static string Relative(string root, string path)
    {
        string fullRoot = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar);
        return Path.GetFullPath(path).Substring(fullRoot.Length).TrimStart(Path.DirectorySeparatorChar)
            .Replace(Path.DirectorySeparatorChar, '/');
    }

    private static bool PathContainsLink(string root, string path)
    {
        try
        {
            string fullRoot = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar);
            string relative = Path.GetFullPath(path).Substring(fullRoot.Length)
                .TrimStart(Path.DirectorySeparatorChar);
            string current = fullRoot;
            foreach (string part in relative.Split(Path.DirectorySeparatorChar))
            {
                if (string.IsNullOrEmpty(part)) continue;
                current = Path.Combine(current, part);
                if (!File.Exists(current) && !Directory.Exists(current)) break;
                if ((File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0) return true;
            }
            return false;
        }
        catch
        {
            return true;
        }
    }

    private static void ValidateProjectRoot(string projectRoot)
    {
        if (string.IsNullOrWhiteSpace(projectRoot) || !Directory.Exists(projectRoot))
            throw new DirectoryNotFoundException($"Project root was not found: {projectRoot}");
    }

    private static void DeleteDirectoryIfEmpty(string path)
    {
        if (!string.IsNullOrWhiteSpace(path) && Directory.Exists(path)
            && !Directory.EnumerateFileSystemEntries(path).Any())
            Directory.Delete(path);
    }

    [Serializable]
    private sealed class ProfileConfig
    {
        public int schemaVersion;
        public string activeProfile;
        public ProfileDefinition[] profiles;
    }

    [Serializable]
    private sealed class ProfileDefinition
    {
        public string name;
        public bool all;
        public string[] packageIds;
    }

    [Serializable]
    private sealed class SkillState
    {
        public List<SkillStateEntry> entries = new List<SkillStateEntry>();
    }

    [Serializable]
    private sealed class SkillStateEntry
    {
        public string packageId;
        public string agent;
        public string skillName;
        public string targetPath;
        public string installedHash;
    }

    [Serializable]
    private sealed class ProfileJournal
    {
        public int schemaVersion = 1;
        public string profileName;
        public string previousProfileName;
        public string createdUtc;
        public string state;
        public List<ProfileJournalOperation> operations = new List<ProfileJournalOperation>();
    }

    [Serializable]
    private sealed class ProfileJournalOperation
    {
        public string action;
        public string sourcePath;
        public string destinationPath;
        public string expectedHash;
        public bool completed;
    }
}
#endif
