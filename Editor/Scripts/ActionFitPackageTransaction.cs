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

/// <summary>
/// Applies package-folder and manifest changes as one recoverable transaction.
/// </summary>
internal static class ActionFitPackageTransaction
{
    private const string TransactionRelativePath = "UserSettings/ActionFitPackageManager/Transactions";

    internal sealed class Request
    {
        public string Operation;
        public string PackageId;
        public string SourcePath;
        public string DestinationPath;
        public string OriginalManifest;
        public string UpdatedManifest;
        public string[] AffectedPackageIds;
        public bool UseExistingDestination;
        public Action<string> PrepareStagedPackage;
        public Action<string> PrepareDestination;
    }

    internal sealed class Result
    {
        public bool Success;
        public string Code;
        public string Message;
        public bool RolledBack;
        public bool RecoveryRequired;
        public string JournalPath;
    }

    /// <summary>
    /// Runs a local package conversion without exposing a broken file dependency.
    /// </summary>
    public static Result Execute(Request request)
    {
        ActionFitPackageTransactionJournal journal = null;
        string journalPath = "";

        try
        {
            ValidateRequest(request);

            string transactionId = Guid.NewGuid().ToString("N");
            string tempPath = Path.Combine(
                ActionFitPackagePaths.ProjectRoot,
                "Temp",
                "ActionFitPackageManager",
                "Transactions",
                transactionId,
                request.PackageId);

            journal = new ActionFitPackageTransactionJournal
            {
                TransactionId = transactionId,
                Operation = request.Operation,
                PackageId = request.PackageId,
                ManifestPath = ActionFitPackagePaths.ManifestPath,
                DestinationPath = ActionFitPackagePaths.NormalizePhysicalPath(request.DestinationPath),
                TempPath = ActionFitPackagePaths.NormalizePhysicalPath(tempPath),
                OriginalManifest = request.OriginalManifest,
                UpdatedManifest = request.UpdatedManifest,
                AffectedPackageIds = NormalizeAffectedPackageIds(request),
                DestinationCreated = false,
                Phase = ActionFitPackageTransactionPhase.Prepared.ToString(),
            };

            journalPath = GetJournalPath(request.PackageId, transactionId);
            SaveJournal(journalPath, journal);

            if (!request.UseExistingDestination)
            {
                ActionFitPackageFileUtility.CopyDirectory(request.SourcePath, journal.TempPath);
                request.PrepareStagedPackage?.Invoke(journal.TempPath);
                ActionFitPackageFileUtility.ValidateLocalPackageFolder(request.PackageId, journal.TempPath);

                if (ActionFitPackageFileUtility.PhysicalDirectoryExists(journal.DestinationPath))
                    throw new InvalidOperationException($"Destination package folder already exists: {ActionFitPackagePaths.ToProjectRelativePath(journal.DestinationPath)}");

                Directory.CreateDirectory(Path.GetDirectoryName(journal.DestinationPath));
                Directory.Move(journal.TempPath, journal.DestinationPath);
                journal.DestinationCreated = true;
                journal.Phase = ActionFitPackageTransactionPhase.PackageMoved.ToString();
                SaveJournal(journalPath, journal);
            }
            else
            {
                ActionFitPackageFileUtility.ValidateLocalPackageFolder(request.PackageId, journal.DestinationPath);
            }

            request.PrepareDestination?.Invoke(journal.DestinationPath);
            ActionFitPackageFileUtility.ValidateLocalPackageFolder(request.PackageId, journal.DestinationPath);

            ActionFitPackageManifestUtility.WriteAtomic(journal.ManifestPath, journal.UpdatedManifest);
            ValidateCommittedState(journal);

            journal.Phase = ActionFitPackageTransactionPhase.ManifestCommitted.ToString();
            SaveJournal(journalPath, journal);
            CleanupCompletedTransaction(journalPath, journal);

            return new Result
            {
                Success = true,
                Code = "COMPLETED",
                Message = $"{request.Operation} completed for {request.PackageId}.",
                JournalPath = journalPath,
            };
        }
        catch (Exception ex)
        {
            if (journal == null)
            {
                return new Result
                {
                    Success = false,
                    Code = "PREPARE_FAILED",
                    Message = ex.Message,
                    JournalPath = journalPath,
                };
            }

            ActionFitPackageRecoveryResult recovery = RecoverJournal(journalPath, journal, true);
            return new Result
            {
                Success = false,
                Code = recovery.RecoveryRequired ? "RECOVERY_REQUIRED" : "ROLLED_BACK",
                Message = recovery.RecoveryRequired
                    ? $"{ex.Message}\nAutomatic recovery could not finish: {recovery.Message}"
                    : $"{ex.Message}\n{recovery.Message}",
                RolledBack = recovery.RolledBack,
                RecoveryRequired = recovery.RecoveryRequired,
                JournalPath = journalPath,
            };
        }
    }

    /// <summary>
    /// Recovers every pending transaction journal after a domain reload or editor restart.
    /// </summary>
    public static ActionFitPackageRecoveryResult[] RecoverPendingTransactions()
    {
        string root = ActionFitPackagePaths.ProjectRelativeFullPath(TransactionRelativePath);
        if (!Directory.Exists(root)) return Array.Empty<ActionFitPackageRecoveryResult>();

        var results = new List<ActionFitPackageRecoveryResult>();
        foreach (string journalPath in Directory.GetFiles(root, "*.json", SearchOption.TopDirectoryOnly)
                     .OrderBy(path => path, StringComparer.Ordinal))
        {
            try
            {
                var journal = JsonUtility.FromJson<ActionFitPackageTransactionJournal>(File.ReadAllText(journalPath));
                if (journal == null || string.IsNullOrWhiteSpace(journal.PackageId))
                {
                    results.Add(ActionFitPackageRecoveryResult.Required(
                        "INVALID_JOURNAL",
                        "Transaction journal is empty or invalid.",
                        journalPath));
                    continue;
                }

                results.Add(RecoverJournal(journalPath, journal, false));
            }
            catch (Exception ex)
            {
                results.Add(ActionFitPackageRecoveryResult.Required("JOURNAL_READ_FAILED", ex.Message, journalPath));
            }
        }

        if (results.Any(result => result.Success))
            Client.Resolve();

        return results.ToArray();
    }

    private static ActionFitPackageRecoveryResult RecoverJournal(
        string journalPath,
        ActionFitPackageTransactionJournal journal,
        bool fromFailure)
    {
        try
        {
            ValidateJournalPaths(journal);
            if (!File.Exists(journal.ManifestPath))
                return ActionFitPackageRecoveryResult.Required("MANIFEST_MISSING", "Packages/manifest.json is missing.", journalPath);

            string currentManifest = File.ReadAllText(journal.ManifestPath);
            bool destinationValid = ActionFitPackageFileUtility.IsValidLocalPackageFolder(
                journal.PackageId,
                journal.DestinationPath,
                out _);
            bool affectedMatchUpdated = ActionFitPackageManifestUtility.DependenciesMatch(
                currentManifest,
                journal.UpdatedManifest,
                journal.AffectedPackageIds);
            bool affectedMatchOriginal = ActionFitPackageManifestUtility.DependenciesMatch(
                currentManifest,
                journal.OriginalManifest,
                journal.AffectedPackageIds);
            bool localDependencyPresent = string.Equals(
                ActionFitPackageManifestUtility.GetDependency(currentManifest, journal.PackageId),
                ActionFitPackageManifestUtility.LocalDependency(journal.PackageId),
                StringComparison.OrdinalIgnoreCase);

            if (destinationValid && (affectedMatchUpdated || localDependencyPresent))
            {
                if (!affectedMatchUpdated)
                {
                    string completedManifest = ActionFitPackageManifestUtility.ApplyDependenciesFrom(
                        currentManifest,
                        journal.UpdatedManifest,
                        journal.AffectedPackageIds);
                    ActionFitPackageManifestUtility.WriteAtomic(journal.ManifestPath, completedManifest);
                }

                ActionFitPackageBaseline.Save(journal.PackageId, journal.DestinationPath);
                CleanupCompletedTransaction(journalPath, journal);
                return ActionFitPackageRecoveryResult.Committed(
                    fromFailure ? "The filesystem operation committed before the reported failure and was finalized safely." : "Pending package transaction was finalized.",
                    journalPath);
            }

            string restoredManifest = affectedMatchOriginal
                ? currentManifest
                : ActionFitPackageManifestUtility.ApplyDependenciesFrom(
                    currentManifest,
                    journal.OriginalManifest,
                    journal.AffectedPackageIds);
            ActionFitPackageManifestUtility.WriteAtomic(journal.ManifestPath, restoredManifest);

            string verifiedManifest = File.ReadAllText(journal.ManifestPath);
            if (!ActionFitPackageManifestUtility.DependenciesMatch(
                    verifiedManifest,
                    journal.OriginalManifest,
                    journal.AffectedPackageIds))
            {
                return ActionFitPackageRecoveryResult.Required(
                    "MANIFEST_ROLLBACK_VERIFY_FAILED",
                    "The original package dependencies could not be verified after rollback. The package folder was preserved.",
                    journalPath);
            }

            // Never delete a package folder until the local file dependency has been removed successfully.
            if (journal.DestinationCreated && ActionFitPackageFileUtility.PhysicalDirectoryExists(journal.DestinationPath))
                ActionFitPackageFileUtility.DeletePackageDirectory(journal.DestinationPath);

            CleanupCompletedTransaction(journalPath, journal);
            return ActionFitPackageRecoveryResult.Rollback(
                fromFailure ? "The original manifest dependencies were restored and the incomplete package folder was removed." : "Pending package transaction was rolled back.",
                journalPath);
        }
        catch (Exception ex)
        {
            return ActionFitPackageRecoveryResult.Required(
                "RECOVERY_FAILED",
                $"{ex.Message} The package folder was preserved to avoid leaving a missing local dependency.",
                journalPath);
        }
    }

    private static void ValidateRequest(Request request)
    {
        if (request == null) throw new ArgumentNullException(nameof(request));
        ActionFitPackagePaths.ValidatePackageId(request.PackageId);
        if (!File.Exists(ActionFitPackagePaths.ManifestPath))
            throw new FileNotFoundException("Packages/manifest.json was not found.", ActionFitPackagePaths.ManifestPath);
        if (string.IsNullOrWhiteSpace(request.OriginalManifest))
            throw new InvalidOperationException("Original manifest content is required.");
        if (string.IsNullOrWhiteSpace(request.UpdatedManifest))
            throw new InvalidOperationException("Updated manifest content is required.");

        ActionFitPackagePaths.EnsurePackagePath(request.DestinationPath);
        ActionFitPackageManifestUtility.Validate(request.OriginalManifest);
        ActionFitPackageManifestUtility.Validate(request.UpdatedManifest);

        string expectedDependency = ActionFitPackageManifestUtility.LocalDependency(request.PackageId);
        string actualDependency = ActionFitPackageManifestUtility.GetDependency(request.UpdatedManifest, request.PackageId);
        if (!string.Equals(expectedDependency, actualDependency, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException($"Updated manifest must contain {request.PackageId}: {expectedDependency}.");

        if (request.UseExistingDestination)
        {
            ActionFitPackageFileUtility.ValidateLocalPackageFolder(request.PackageId, request.DestinationPath);
        }
        else
        {
            if (string.IsNullOrWhiteSpace(request.SourcePath) || !ActionFitPackageFileUtility.PhysicalDirectoryExists(request.SourcePath))
                throw new DirectoryNotFoundException($"Package source was not found: {request.SourcePath}");
            if (ActionFitPackageFileUtility.PhysicalDirectoryExists(request.DestinationPath))
                throw new InvalidOperationException($"Destination package folder already exists: {request.DestinationPath}");
        }
    }

    private static void ValidateCommittedState(ActionFitPackageTransactionJournal journal)
    {
        ActionFitPackageFileUtility.ValidateLocalPackageFolder(journal.PackageId, journal.DestinationPath);
        string currentManifest = File.ReadAllText(journal.ManifestPath);
        if (!ActionFitPackageManifestUtility.DependenciesMatch(
                currentManifest,
                journal.UpdatedManifest,
                journal.AffectedPackageIds))
            throw new InvalidOperationException("Manifest dependencies did not match the requested transaction after commit.");
    }

    private static string[] NormalizeAffectedPackageIds(Request request)
    {
        var ids = (request.AffectedPackageIds ?? Array.Empty<string>())
            .Where(id => !string.IsNullOrWhiteSpace(id))
            .Append(request.PackageId)
            .Distinct(StringComparer.Ordinal)
            .OrderBy(id => id, StringComparer.Ordinal)
            .ToArray();
        foreach (string id in ids) ActionFitPackagePaths.ValidatePackageId(id);
        return ids;
    }

    private static void ValidateJournalPaths(ActionFitPackageTransactionJournal journal)
    {
        if (!string.Equals(ActionFitPackagePaths.NormalizePhysicalPath(journal.ManifestPath), ActionFitPackagePaths.ManifestPath, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Transaction journal points to an unexpected manifest path.");
        ActionFitPackagePaths.EnsurePackagePath(journal.DestinationPath);
        ActionFitPackagePaths.EnsureTempPath(journal.TempPath);
    }

    private static string GetJournalPath(string packageId, string transactionId)
    {
        string root = ActionFitPackagePaths.ProjectRelativeFullPath(TransactionRelativePath);
        Directory.CreateDirectory(root);
        return Path.Combine(root, $"{packageId}.{transactionId}.json");
    }

    private static void SaveJournal(string journalPath, ActionFitPackageTransactionJournal journal)
    {
        ActionFitPackageManifestUtility.WriteAtomic(journalPath, JsonUtility.ToJson(journal, true), false);
    }

    private static void CleanupCompletedTransaction(string journalPath, ActionFitPackageTransactionJournal journal)
    {
        ActionFitPackageFileUtility.TryDeleteDirectory(journal.TempPath, ActionFitPackagePaths.TempRoot);
        string transactionTempRoot = Path.GetDirectoryName(journal.TempPath);
        ActionFitPackageFileUtility.TryDeleteDirectory(transactionTempRoot, ActionFitPackagePaths.TempRoot);
        if (File.Exists(journalPath)) File.Delete(journalPath);
    }
}

/// <summary>
/// Repairs transaction journals when Unity reloads during a package conversion.
/// </summary>
[InitializeOnLoad]
internal static class ActionFitPackageTransactionRecoveryBootstrap
{
    static ActionFitPackageTransactionRecoveryBootstrap()
    {
        EditorApplication.delayCall += Recover;
    }

    private static void Recover()
    {
        ActionFitPackageRecoveryResult[] results = ActionFitPackageTransaction.RecoverPendingTransactions();
        foreach (ActionFitPackageRecoveryResult result in results)
        {
            if (result.RecoveryRequired)
                Debug.LogError($"[ActionFitPackageManager] Package transaction recovery requires attention: {result.Code}\n{result.Message}\nJournal: {result.JournalPath}");
            else if (result.Success)
                Debug.Log($"[ActionFitPackageManager] {result.Message}\nJournal: {result.JournalPath}");
        }
    }
}

/// <summary>
/// Structured result returned by package transaction recovery.
/// </summary>
[Serializable]
public sealed class ActionFitPackageRecoveryResult
{
    public bool Success;
    public bool RolledBack;
    public bool RecoveryRequired;
    public string Code;
    public string Message;
    public string JournalPath;

    internal static ActionFitPackageRecoveryResult Committed(string message, string journalPath)
    {
        return new ActionFitPackageRecoveryResult
        {
            Success = true,
            Code = "RECOVERED_COMMIT",
            Message = message,
            JournalPath = journalPath,
        };
    }

    internal static ActionFitPackageRecoveryResult Rollback(string message, string journalPath)
    {
        return new ActionFitPackageRecoveryResult
        {
            Success = true,
            RolledBack = true,
            Code = "RECOVERED_ROLLBACK",
            Message = message,
            JournalPath = journalPath,
        };
    }

    internal static ActionFitPackageRecoveryResult Required(string code, string message, string journalPath)
    {
        return new ActionFitPackageRecoveryResult
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
internal sealed class ActionFitPackageTransactionJournal
{
    public string TransactionId;
    public string Operation;
    public string PackageId;
    public string ManifestPath;
    public string DestinationPath;
    public string TempPath;
    public string OriginalManifest;
    public string UpdatedManifest;
    public string[] AffectedPackageIds;
    public bool DestinationCreated;
    public string Phase;
}

internal enum ActionFitPackageTransactionPhase
{
    Prepared,
    PackageMoved,
    ManifestCommitted,
}

/// <summary>
/// Reads and writes the dependencies block in Packages/manifest.json.
/// </summary>
internal static class ActionFitPackageManifestUtility
{
    public static string LocalDependency(string packageId)
    {
        return $"file:{packageId}";
    }

    public static string GetDependency(string manifest, string packageId)
    {
        List<(string Id, string Value)> dependencies = ReadDependencies(manifest, out _, out _);
        return dependencies.FirstOrDefault(dependency => string.Equals(dependency.Id, packageId, StringComparison.Ordinal)).Value ?? "";
    }

    public static string SetDependency(string manifest, string packageId, string value)
    {
        List<(string Id, string Value)> dependencies = ReadDependencies(manifest, out int openBrace, out int closeBrace);
        int index = dependencies.FindIndex(dependency => string.Equals(dependency.Id, packageId, StringComparison.Ordinal));
        if (index >= 0) dependencies[index] = (packageId, value);
        else dependencies.Add((packageId, value));
        return WriteDependencies(manifest, openBrace, closeBrace, dependencies);
    }

    public static string RemoveDependency(string manifest, string packageId, out bool removed)
    {
        List<(string Id, string Value)> dependencies = ReadDependencies(manifest, out int openBrace, out int closeBrace);
        removed = dependencies.RemoveAll(dependency => string.Equals(dependency.Id, packageId, StringComparison.Ordinal)) > 0;
        return removed ? WriteDependencies(manifest, openBrace, closeBrace, dependencies) : manifest;
    }

    public static string ApplyDependenciesFrom(string targetManifest, string sourceManifest, IEnumerable<string> packageIds)
    {
        string result = targetManifest;
        foreach (string packageId in packageIds ?? Array.Empty<string>())
        {
            string sourceValue = GetDependency(sourceManifest, packageId);
            result = string.IsNullOrEmpty(sourceValue)
                ? RemoveDependency(result, packageId, out _)
                : SetDependency(result, packageId, sourceValue);
        }

        return result;
    }

    public static bool DependenciesMatch(string left, string right, IEnumerable<string> packageIds)
    {
        return (packageIds ?? Array.Empty<string>()).All(packageId =>
            string.Equals(GetDependency(left, packageId), GetDependency(right, packageId), StringComparison.Ordinal));
    }

    public static void Validate(string manifest)
    {
        ReadDependencies(manifest, out _, out _);
    }

    public static void WriteAtomic(string path, string content, bool validateManifest = true)
    {
        string fullPath = ActionFitPackagePaths.NormalizePhysicalPath(path);
        string directory = Path.GetDirectoryName(fullPath);
        Directory.CreateDirectory(directory);
        if (validateManifest) Validate(content);

        string tempPath = fullPath + ".actionfit." + Guid.NewGuid().ToString("N") + ".tmp";
        string backupPath = fullPath + ".actionfit." + Guid.NewGuid().ToString("N") + ".bak";
        try
        {
            File.WriteAllText(tempPath, content, new UTF8Encoding(false));
            if (validateManifest) Validate(File.ReadAllText(tempPath));

            if (File.Exists(fullPath))
                File.Replace(tempPath, fullPath, backupPath, true);
            else
                File.Move(tempPath, fullPath);

            string written = File.ReadAllText(fullPath);
            if (!string.Equals(NormalizeNewlines(written), NormalizeNewlines(content), StringComparison.Ordinal))
                throw new IOException($"Atomic write verification failed: {fullPath}");
        }
        finally
        {
            TryDeleteFile(tempPath);
            TryDeleteFile(backupPath);
        }
    }

    private static List<(string Id, string Value)> ReadDependencies(string manifest, out int openBrace, out int closeBrace)
    {
        if (string.IsNullOrWhiteSpace(manifest))
            throw new InvalidOperationException("manifest.json is empty.");

        int dependenciesStart = manifest.IndexOf("\"dependencies\"", StringComparison.Ordinal);
        if (dependenciesStart < 0) throw new InvalidOperationException("manifest.json has no dependencies block.");

        openBrace = manifest.IndexOf('{', dependenciesStart);
        if (openBrace < 0) throw new InvalidOperationException("manifest.json dependencies block has no opening brace.");
        closeBrace = FindMatchingBrace(manifest, openBrace);
        if (closeBrace < 0) throw new InvalidOperationException("manifest.json dependencies block has no closing brace.");

        string dependenciesBody = manifest.Substring(openBrace + 1, closeBrace - openBrace - 1);
        var dependencies = new List<(string Id, string Value)>();
        var pattern = new Regex("^\\s*\"([^\"]+)\"\\s*:\\s*\"([^\"]*)\"\\s*,?\\s*$");
        foreach (string line in dependenciesBody.Replace("\r\n", "\n").Split('\n'))
        {
            if (string.IsNullOrWhiteSpace(line)) continue;
            Match match = pattern.Match(line);
            if (!match.Success)
                throw new InvalidOperationException($"Unsupported manifest dependency line: {line.Trim()}");
            dependencies.Add((match.Groups[1].Value, match.Groups[2].Value));
        }

        return dependencies;
    }

    private static string WriteDependencies(
        string manifest,
        int openBrace,
        int closeBrace,
        IReadOnlyList<(string Id, string Value)> dependencies)
    {
        string newline = manifest.Contains("\r\n") ? "\r\n" : "\n";
        var sb = new StringBuilder();
        sb.Append('{');
        if (dependencies.Count > 0)
        {
            sb.Append(newline);
            for (int i = 0; i < dependencies.Count; i++)
            {
                (string id, string value) = dependencies[i];
                sb.Append("    \"").Append(id).Append("\": \"").Append(value).Append('"');
                if (i < dependencies.Count - 1) sb.Append(',');
                sb.Append(newline);
            }

            sb.Append("  ");
        }

        sb.Append('}');
        return manifest.Substring(0, openBrace) + sb + manifest[(closeBrace + 1)..];
    }

    private static int FindMatchingBrace(string text, int openBrace)
    {
        int depth = 0;
        bool inString = false;
        bool escaped = false;
        for (int i = openBrace; i < text.Length; i++)
        {
            char c = text[i];
            if (inString)
            {
                if (escaped) escaped = false;
                else if (c == '\\') escaped = true;
                else if (c == '"') inString = false;
                continue;
            }

            if (c == '"') inString = true;
            else if (c == '{') depth++;
            else if (c == '}' && --depth == 0) return i;
        }

        return -1;
    }

    private static string NormalizeNewlines(string value)
    {
        return (value ?? "").Replace("\r\n", "\n");
    }

    private static void TryDeleteFile(string path)
    {
        try
        {
            if (File.Exists(path)) File.Delete(path);
        }
        catch
        {
            // Temporary cleanup must not turn a committed atomic write into a failed transaction.
        }
    }
}

/// <summary>
/// Provides guarded package file operations used by conversion transactions.
/// </summary>
internal static class ActionFitPackageFileUtility
{
    public static bool PhysicalDirectoryExists(string path)
    {
        if (string.IsNullOrWhiteSpace(path)) return false;
        string fullPath = ActionFitPackagePaths.NormalizePhysicalPath(path);
        string parent = Path.GetDirectoryName(fullPath);
        if (string.IsNullOrWhiteSpace(parent) || !Directory.Exists(parent)) return false;

        string name = Path.GetFileName(fullPath);
        return Directory.GetDirectories(parent, name, SearchOption.TopDirectoryOnly)
            .Any(candidate => string.Equals(
                ActionFitPackagePaths.NormalizePhysicalPath(candidate),
                fullPath,
                StringComparison.OrdinalIgnoreCase));
    }

    public static void CopyDirectory(string source, string destination)
    {
        string fullSource = ActionFitPackagePaths.NormalizePhysicalPath(source);
        string fullDestination = ActionFitPackagePaths.NormalizePhysicalPath(destination);
        Directory.CreateDirectory(fullDestination);

        foreach (string directory in Directory.GetDirectories(fullSource, "*", SearchOption.AllDirectories))
        {
            string relative = directory[(fullSource.Length + 1)..];
            if (IsGitMetadataPath(relative)) continue;
            Directory.CreateDirectory(Path.Combine(fullDestination, relative));
        }

        foreach (string file in Directory.GetFiles(fullSource, "*", SearchOption.AllDirectories))
        {
            string relative = file[(fullSource.Length + 1)..];
            if (IsGitMetadataPath(relative)) continue;
            string target = Path.Combine(fullDestination, relative);
            Directory.CreateDirectory(Path.GetDirectoryName(target));
            File.Copy(file, target, true);
        }
    }

    public static bool IsValidLocalPackageFolder(string packageId, string packagePath, out string error)
    {
        try
        {
            ValidateLocalPackageFolder(packageId, packagePath);
            error = "";
            return true;
        }
        catch (Exception ex)
        {
            error = ex.Message;
            return false;
        }
    }

    public static void ValidateLocalPackageFolder(string packageId, string packagePath)
    {
        if (string.IsNullOrWhiteSpace(packagePath) || !PhysicalDirectoryExists(packagePath))
            throw new DirectoryNotFoundException($"Local package folder does not exist: {packagePath}");

        string packageJsonPath = Path.Combine(packagePath, "package.json");
        if (!File.Exists(packageJsonPath))
            throw new FileNotFoundException("Local package folder is missing package.json.", packageJsonPath);

        ActionFitPackageManifest manifest = ActionFitPackageManifest.Read(packageJsonPath);
        if (!string.Equals(manifest.Name, packageId, StringComparison.Ordinal))
            throw new InvalidOperationException($"Local package ID mismatch. Expected: {packageId}, Found: {manifest.Name}");
        if (string.IsNullOrWhiteSpace(manifest.Version))
            throw new InvalidOperationException($"Local package version is missing: {packageJsonPath}");
    }

    public static void DeletePackageDirectory(string packagePath)
    {
        ActionFitPackagePaths.EnsurePackagePath(packagePath);
        if (PhysicalDirectoryExists(packagePath)) Directory.Delete(packagePath, true);
    }

    public static void TryDeleteDirectory(string path, string allowedRoot)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(path) || !PhysicalDirectoryExists(path)) return;
            string fullPath = ActionFitPackagePaths.NormalizePhysicalPath(path);
            string fullRoot = ActionFitPackagePaths.NormalizePhysicalPath(allowedRoot);
            if (!fullPath.StartsWith(fullRoot + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)) return;
            Directory.Delete(fullPath, true);
        }
        catch
        {
            // Stale temporary directories can be retried on the next transaction.
        }
    }

    private static bool IsGitMetadataPath(string relativePath)
    {
        string normalized = relativePath.Replace("\\", "/");
        return normalized.Equals(".git", StringComparison.OrdinalIgnoreCase) ||
               normalized.StartsWith(".git/", StringComparison.OrdinalIgnoreCase) ||
               normalized.Contains("/.git/", StringComparison.OrdinalIgnoreCase);
    }
}

/// <summary>
/// Records a content hash used to detect edits to embedded packages.
/// </summary>
internal static class ActionFitPackageBaseline
{
    private const string BaselineRelativePath = "UserSettings/ActionFitPackageManager/EmbeddedBaselines";

    public static void Save(string packageId, string packagePath)
    {
        string baselineRoot = ActionFitPackagePaths.ProjectRelativeFullPath(BaselineRelativePath);
        Directory.CreateDirectory(baselineRoot);
        string baselinePath = Path.Combine(baselineRoot, packageId + ".sha256");
        File.WriteAllText(baselinePath, ComputeContentHash(packagePath), new UTF8Encoding(false));
    }

    public static string GetChangeState(string packageId, string packagePath)
    {
        try
        {
            string baselinePath = Path.Combine(
                ActionFitPackagePaths.ProjectRelativeFullPath(BaselineRelativePath),
                packageId + ".sha256");
            if (!File.Exists(baselinePath) || !ActionFitPackageFileUtility.PhysicalDirectoryExists(packagePath)) return "UNKNOWN";
            string baseline = File.ReadAllText(baselinePath).Trim();
            string current = ComputeContentHash(packagePath);
            return string.Equals(baseline, current, StringComparison.OrdinalIgnoreCase) ? "UNCHANGED" : "MODIFIED";
        }
        catch
        {
            return "UNKNOWN";
        }
    }

    public static string ComputeContentHash(string directoryPath)
    {
        string fullRoot = ActionFitPackagePaths.NormalizePhysicalPath(directoryPath);
        var rows = new StringBuilder();
        using var sha256 = SHA256.Create();

        foreach (string file in Directory.GetFiles(fullRoot, "*", SearchOption.AllDirectories)
                     .OrderBy(path => path, StringComparer.Ordinal))
        {
            string relative = file[(fullRoot.Length + 1)..].Replace("\\", "/");
            if (relative.Equals(".git", StringComparison.OrdinalIgnoreCase) ||
                relative.StartsWith(".git/", StringComparison.OrdinalIgnoreCase) ||
                relative.Contains("/.git/", StringComparison.OrdinalIgnoreCase))
                continue;

            using var stream = File.OpenRead(file);
            string fileHash = BitConverter.ToString(sha256.ComputeHash(stream)).Replace("-", "");
            rows.Append(relative).Append('|').Append(fileHash).Append('\n');
        }

        return BitConverter.ToString(sha256.ComputeHash(Encoding.UTF8.GetBytes(rows.ToString()))).Replace("-", "");
    }
}

/// <summary>
/// Centralizes project paths and path traversal guards for package transactions.
/// </summary>
internal static class ActionFitPackagePaths
{
    public static string ProjectRoot => Path.GetFullPath(Path.Combine(Application.dataPath, ".."));
    public static string PackagesRoot => Path.Combine(ProjectRoot, "Packages");
    public static string TempRoot => Path.Combine(ProjectRoot, "Temp", "ActionFitPackageManager");
    public static string ManifestPath => Path.Combine(PackagesRoot, "manifest.json");

    public static string ProjectRelativeFullPath(string relativePath)
    {
        return Path.Combine(ProjectRoot, relativePath.Replace("/", Path.DirectorySeparatorChar.ToString()));
    }

    public static string PackagePath(string packageId)
    {
        ValidatePackageId(packageId);
        return Path.Combine(PackagesRoot, packageId);
    }

    public static string ToProjectRelativePath(string fullPath)
    {
        string root = ProjectRoot.Replace("\\", "/").TrimEnd('/');
        string normalized = NormalizePhysicalPath(fullPath).Replace("\\", "/");
        return normalized.StartsWith(root + "/", StringComparison.OrdinalIgnoreCase)
            ? normalized[(root.Length + 1)..]
            : normalized;
    }

    public static string NormalizePhysicalPath(string path)
    {
        if (string.IsNullOrWhiteSpace(path)) throw new ArgumentException("Path is required.", nameof(path));
        string rooted = Path.IsPathRooted(path) ? path : Path.Combine(ProjectRoot, path);
        return new Uri(rooted).LocalPath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
    }

    public static void ValidatePackageId(string packageId)
    {
        if (string.IsNullOrWhiteSpace(packageId) ||
            !Regex.IsMatch(packageId, "^[a-z0-9][a-z0-9._-]*$", RegexOptions.CultureInvariant))
            throw new ArgumentException($"Invalid package ID: {packageId}", nameof(packageId));
    }

    public static void EnsurePackagePath(string path)
    {
        EnsureChildPath(path, PackagesRoot, "package");
    }

    public static void EnsureTempPath(string path)
    {
        EnsureChildPath(path, TempRoot, "temporary");
    }

    private static void EnsureChildPath(string path, string root, string label)
    {
        string fullPath = NormalizePhysicalPath(path);
        string fullRoot = NormalizePhysicalPath(root);
        if (!fullPath.StartsWith(fullRoot + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException($"Unexpected {label} path: {fullPath}");
    }
}
#endif
