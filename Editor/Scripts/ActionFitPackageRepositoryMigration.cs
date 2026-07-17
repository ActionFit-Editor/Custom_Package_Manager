#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using UnityEngine;
using Debug = UnityEngine.Debug;

/// <summary>
/// Read-only preflight and explicitly approved execution for catalog repository relocation.
/// The source repository is never mutated by this workflow. Source retirement is a separate approval-gated phase.
/// </summary>
internal static class ActionFitPackageRepositoryMigration
{
    internal const string NoMigrationCode = "REPOSITORY_MIGRATION_NOT_REQUIRED";

    internal static bool TryInspect(
        ActionFitPackagePublisher.PublishRequest request,
        ActionFitPackagePublisher.RemoteState targetRemote,
        out RepositoryMigrationState state,
        out string code,
        out string message)
    {
        state = new RepositoryMigrationState();
        code = NoMigrationCode;
        message = "Catalog repository already matches the publish target.";
        if (request == null)
        {
            code = "PUBLISH_REQUEST_MISSING";
            message = "Publish request is missing.";
            return false;
        }

        string targetUrl = BuildRepositoryUrl(request.GitHubOrganization, request.RepoName);
        state.TargetRepositoryUrl = targetUrl;
        state.TargetRepositoryExists = targetRemote?.RepositoryExists == true;
        state.TargetRepositoryIsPrivate = targetRemote?.RepositoryIsPrivate == true;
        state.TargetDefaultBranch = targetRemote?.DefaultBranch ?? "";

        if (string.IsNullOrWhiteSpace(request.SourceRepositoryUrl))
            return true;

        if (!TryParseRepositoryUrl(request.SourceRepositoryUrl, out RepositoryIdentity source))
        {
            code = "CATALOG_REPOSITORY_URL_INVALID";
            message = $"Catalog repository URL is not a supported GitHub repository URL: {request.SourceRepositoryUrl}";
            return false;
        }

        var target = new RepositoryIdentity(request.GitHubOrganization, request.RepoName);
        state.SourceRepositoryUrl = source.Url;
        if (source.Equals(target))
            return true;

        state.Required = true;
        if (targetRemote == null)
        {
            code = "TARGET_REPOSITORY_STATE_MISSING";
            message = "Target repository state is missing.";
            return false;
        }
        if (targetRemote.RepositoryExists && targetRemote.RepositoryIsPrivate != request.GitHubIsPrivate)
        {
            code = "TARGET_REPOSITORY_VISIBILITY_MISMATCH";
            message =
                $"Target repository {target.DisplayName} exists as " +
                $"{(targetRemote.RepositoryIsPrivate ? "Private" : "Public")}, but the selected target is " +
                $"{(request.GitHubIsPrivate ? "Private" : "Public")}. " +
                "The publisher will not change its visibility or overwrite it.";
            return false;
        }

        if (!ActionFitPackagePublisher.TryGetRepositoryMetadata(
                source.Organization,
                source.Name,
                request.GitHubToken,
                out bool sourceExists,
                out bool sourceIsPrivate,
                out string sourceDefaultBranch,
                out string sourceError))
        {
            code = "SOURCE_REPOSITORY_CHECK_FAILED";
            message = sourceError;
            return false;
        }
        if (!sourceExists)
        {
            code = "SOURCE_REPOSITORY_MISSING";
            message = $"Catalog source repository {source.DisplayName} does not exist or is not accessible.";
            return false;
        }

        state.SourceRepositoryExists = true;
        state.SourceRepositoryIsPrivate = sourceIsPrivate;
        state.SourceDefaultBranch = sourceDefaultBranch;

        try
        {
            Dictionary<string, string> sourceRefs = ReadRemoteRefs(source, request.GitHubToken);
            Dictionary<string, string> targetRefs = targetRemote.RepositoryExists
                ? ReadRemoteRefs(target, request.GitHubToken)
                : new Dictionary<string, string>(StringComparer.Ordinal);
            if (!sourceRefs.Keys.Any(value => value.StartsWith("refs/heads/", StringComparison.Ordinal)))
            {
                code = "SOURCE_REPOSITORY_HAS_NO_BRANCHES";
                message = $"Source repository {source.DisplayName} has no branches to migrate.";
                return false;
            }
            if (!string.IsNullOrWhiteSpace(sourceDefaultBranch) &&
                !sourceRefs.ContainsKey($"refs/heads/{sourceDefaultBranch}"))
            {
                code = "SOURCE_DEFAULT_BRANCH_MISSING";
                message =
                    $"Source default branch {sourceDefaultBranch} was not found in {source.DisplayName}. " +
                    "Repository migration cannot preserve an invalid default branch.";
                return false;
            }
            if (sourceRefs.ContainsKey($"refs/tags/{request.Version}"))
            {
                code = "SOURCE_VERSION_TAG_ALREADY_EXISTS";
                message =
                    $"Source repository already contains immutable tag {request.Version}. " +
                    "Bump package.json before repository migration and publish.";
                return false;
            }

            string ancestryCheckError = "";
            string[] conflictingRefs = FindConflictingRefs(
                sourceRefs,
                targetRefs,
                (sourceSha, targetSha) =>
                {
                    if (TryIsTargetDescendant(
                            target,
                            sourceSha,
                            targetSha,
                            request.GitHubToken,
                            out bool isDescendant,
                            out string compareError))
                    {
                        return isDescendant;
                    }

                    ancestryCheckError = compareError;
                    return false;
                });
            if (!string.IsNullOrWhiteSpace(ancestryCheckError))
            {
                code = "TARGET_REPOSITORY_ANCESTRY_CHECK_FAILED";
                message = ancestryCheckError;
                return false;
            }
            if (conflictingRefs.Length > 0)
            {
                code = "TARGET_REPOSITORY_REF_CONFLICT";
                message =
                    $"Target repository {target.DisplayName} contains {conflictingRefs.Length} conflicting branch/tag ref(s): " +
                    string.Join(", ", conflictingRefs.Take(8)) +
                    (conflictingRefs.Length > 8 ? ", ..." : "") +
                    ". Existing target history will not be force-updated.";
                return false;
            }

            string[] missingRefs = FindMissingRefs(sourceRefs, targetRefs);
            state.SourceBranchCount = CountRefs(sourceRefs, "refs/heads/");
            state.SourceTagCount = CountRefs(sourceRefs, "refs/tags/");
            state.TargetBranchCount = CountRefs(targetRefs, "refs/heads/");
            state.TargetTagCount = CountRefs(targetRefs, "refs/tags/");
            state.MissingRefCount = missingRefs.Length;
            state.WillCreateTarget = !targetRemote.RepositoryExists;
            state.WillMirrorRepository = state.WillCreateTarget ||
                                         missingRefs.Length > 0 ||
                                         !string.Equals(sourceDefaultBranch, targetRemote.DefaultBranch, StringComparison.Ordinal);

            if (!ValidateDocumentation(request.PackageRoot, target, out string documentationError))
            {
                code = "TARGET_REPOSITORY_DOCUMENTATION_MISSING";
                message = documentationError;
                return false;
            }

            state.Fingerprint = ComputeFingerprint(state, sourceRefs, targetRefs);
            code = "REPOSITORY_MIGRATION_READY";
            message = state.WillMirrorRepository
                ? $"Repository migration is ready: {source.DisplayName} -> {target.DisplayName}. " +
                  $"Source refs: {state.SourceBranchCount} branch(es), {state.SourceTagCount} tag(s); missing target refs: {state.MissingRefCount}."
                : $"{(request.GitHubIsPrivate ? "Private" : "Public")} target {target.DisplayName} already contains all source refs. " +
                  "Catalog relocation still requires explicit migration approval.";
            return true;
        }
        catch (Exception ex)
        {
            code = "REPOSITORY_MIGRATION_CHECK_FAILED";
            message = $"Repository migration preflight failed: {ex.Message}";
            return false;
        }
    }

    internal static MigrationResult Execute(
        ActionFitPackagePublisher.PublishRequest request,
        string expectedFingerprint)
    {
        if (request == null)
            return MigrationResult.Failed("Publish request is missing.");

        var stopwatch = Stopwatch.StartNew();
        try
        {
            if (!ActionFitPackagePublisher.TryGetRemoteState(request, out ActionFitPackagePublisher.RemoteState targetRemote, out string remoteError))
                return MigrationResult.Failed(remoteError);
            if (!TryInspect(request, targetRemote, out RepositoryMigrationState before, out string code, out string inspectionMessage))
                return MigrationResult.Failed($"{code}: {inspectionMessage}");
            if (!before.Required)
                return MigrationResult.Succeeded("Repository migration was not required.");
            if (!string.Equals(before.Fingerprint, expectedFingerprint, StringComparison.Ordinal))
            {
                return MigrationResult.Failed(
                    "Repository migration state changed after approval. Prepare and approve the publish plan again.");
            }

            Debug.Log(
                $"[ActionFitPackageManager] Repository migration start: {request.PackageId} " +
                $"({before.SourceRepositoryUrl} -> {before.TargetRepositoryUrl})");
            ActionFitPackagePublisher.EnsureRepository(request);

            if (before.WillMirrorRepository)
            {
                if (!TryParseRepositoryUrl(before.SourceRepositoryUrl, out RepositoryIdentity source))
                    return MigrationResult.Failed("Approved source repository URL is invalid.");
                var target = new RepositoryIdentity(request.GitHubOrganization, request.RepoName);
                MirrorRepository(request, source, target);
                if (!string.IsNullOrWhiteSpace(before.SourceDefaultBranch))
                    ActionFitPackagePublisher.SetDefaultBranch(request, before.SourceDefaultBranch);
            }

            if (!ActionFitPackagePublisher.TryGetRemoteState(request, out targetRemote, out remoteError))
                return MigrationResult.Failed(remoteError);
            if (!TryInspect(request, targetRemote, out RepositoryMigrationState after, out code, out inspectionMessage))
                return MigrationResult.Failed($"Post-migration verification failed: {code}: {inspectionMessage}");
            if (!after.TargetRepositoryExists ||
                after.TargetRepositoryIsPrivate != request.GitHubIsPrivate ||
                after.WillMirrorRepository)
            {
                return MigrationResult.Failed(
                    "Post-migration verification found missing refs, a default-branch mismatch, or an unexpected target visibility.");
            }

            string resultMessage =
                $"Repository migration verified in {stopwatch.ElapsedMilliseconds} ms: " +
                $"{after.SourceBranchCount} branch(es), {after.SourceTagCount} tag(s), default branch {after.SourceDefaultBranch}.";
            Debug.Log($"[ActionFitPackageManager] {resultMessage}");
            return MigrationResult.Succeeded(resultMessage);
        }
        catch (Exception ex)
        {
            string message = $"Repository migration failed after {stopwatch.ElapsedMilliseconds} ms: {ex.Message}";
            Debug.LogWarning($"[ActionFitPackageManager] {message}");
            return MigrationResult.Failed(message);
        }
    }

    internal static bool TryParseRepositoryUrl(string repositoryUrl, out RepositoryIdentity identity)
    {
        identity = default;
        string value = (repositoryUrl ?? "").Trim();
        if (value.StartsWith("git@github.com:", StringComparison.OrdinalIgnoreCase))
            value = "https://github.com/" + value.Substring("git@github.com:".Length);
        if (!Uri.TryCreate(value, UriKind.Absolute, out Uri uri) ||
            !string.Equals(uri.Host, "github.com", StringComparison.OrdinalIgnoreCase))
            return false;

        string[] segments = uri.AbsolutePath.Trim('/').Split('/');
        if (segments.Length != 2 || string.IsNullOrWhiteSpace(segments[0]) || string.IsNullOrWhiteSpace(segments[1]))
            return false;
        string name = segments[1].EndsWith(".git", StringComparison.OrdinalIgnoreCase)
            ? segments[1].Substring(0, segments[1].Length - 4)
            : segments[1];
        if (string.IsNullOrWhiteSpace(name)) return false;
        identity = new RepositoryIdentity(segments[0], name);
        return true;
    }

    internal static bool ValidateDocumentation(
        string packageRoot,
        RepositoryIdentity target,
        out string message)
    {
        string repositoryMarker = $"github.com/{target.Organization}/{target.Name}".ToLowerInvariant();
        foreach (string fileName in new[] { "README.md", "AI_GUIDE.md" })
        {
            string path = Path.Combine(packageRoot ?? "", fileName);
            if (!File.Exists(path))
            {
                message = $"{fileName} is required before repository migration.";
                return false;
            }

            string content = File.ReadAllText(path).Replace("\\", "/").ToLowerInvariant();
            if (!content.Contains(repositoryMarker))
            {
                message =
                    $"{fileName} must reference the target repository {target.Url} before migration. " +
                    "The catalog URL will not change until documentation and repository verification both pass.";
                return false;
            }
        }

        message = "Repository documentation points to the target URL.";
        return true;
    }

    internal static string[] FindConflictingRefs(
        IReadOnlyDictionary<string, string> sourceRefs,
        IReadOnlyDictionary<string, string> targetRefs)
        => FindConflictingRefs(sourceRefs, targetRefs, null);

    internal static string[] FindConflictingRefs(
        IReadOnlyDictionary<string, string> sourceRefs,
        IReadOnlyDictionary<string, string> targetRefs,
        Func<string, string, bool> branchTargetContainsSource)
    {
        return (sourceRefs ?? new Dictionary<string, string>())
            .Where(pair => targetRefs != null &&
                           targetRefs.TryGetValue(pair.Key, out string targetSha) &&
                           !string.Equals(pair.Value, targetSha, StringComparison.Ordinal) &&
                           (!pair.Key.StartsWith("refs/heads/", StringComparison.Ordinal) ||
                            branchTargetContainsSource == null ||
                            !branchTargetContainsSource(pair.Value, targetSha)))
            .Select(pair => pair.Key)
            .OrderBy(value => value, StringComparer.Ordinal)
            .ToArray();
    }

    internal static bool IsTargetCompareStatusCompatible(string status)
        => string.Equals(status, "ahead", StringComparison.OrdinalIgnoreCase) ||
           string.Equals(status, "identical", StringComparison.OrdinalIgnoreCase);

    private static bool TryIsTargetDescendant(
        RepositoryIdentity target,
        string sourceSha,
        string targetSha,
        string token,
        out bool isDescendant,
        out string error)
    {
        isDescendant = false;
        error = "";
        string url =
            $"https://api.github.com/repos/{target.Organization}/{target.Name}/compare/" +
            $"{Uri.EscapeDataString(sourceSha ?? "")}...{Uri.EscapeDataString(targetSha ?? "")}";
        try
        {
            HttpWebRequest request = ActionFitPackagePublisher.CreateGitHubRequest(url, token, "GET");
            using var response = (HttpWebResponse)request.GetResponse();
            using var reader = new StreamReader(response.GetResponseStream(), Encoding.UTF8);
            string responseJson = reader.ReadToEnd();
            Match statusMatch = Regex.Match(
                responseJson,
                "\"status\"\\s*:\\s*\"([^\"]+)\"",
                RegexOptions.IgnoreCase);
            if (!statusMatch.Success)
            {
                error = $"GitHub compare response did not include status for {target.DisplayName}.";
                return false;
            }

            isDescendant = IsTargetCompareStatusCompatible(statusMatch.Groups[1].Value);
            return true;
        }
        catch (WebException ex) when (
            ex.Response is HttpWebResponse response &&
            (response.StatusCode == HttpStatusCode.NotFound ||
             response.StatusCode == HttpStatusCode.UnprocessableEntity))
        {
            ex.Response?.Dispose();
            return true;
        }
        catch (Exception ex)
        {
            error = $"GitHub compare failed for {target.DisplayName}: {ex.Message}";
            return false;
        }
    }

    internal static string[] FindMissingRefs(
        IReadOnlyDictionary<string, string> sourceRefs,
        IReadOnlyDictionary<string, string> targetRefs)
    {
        return (sourceRefs?.Keys ?? Array.Empty<string>())
            .Where(value => targetRefs == null || !targetRefs.ContainsKey(value))
            .OrderBy(value => value, StringComparer.Ordinal)
            .ToArray();
    }

    private static void MirrorRepository(
        ActionFitPackagePublisher.PublishRequest request,
        RepositoryIdentity source,
        RepositoryIdentity target)
    {
        string publishRoot = ActionFitPackagePublisher.ExpandPath(request.PublishRoot);
        string migrationRoot = Path.Combine(publishRoot, ".repository-migration");
        Directory.CreateDirectory(migrationRoot);
        string cacheName = Sha256($"{source.DisplayName}->{target.DisplayName}").Substring(0, 20) + ".git";
        string mirrorPath = Path.Combine(migrationRoot, cacheName);
        string sourceRemote = ActionFitPackagePublisher.BuildTokenRemote(source.Organization, source.Name, request.GitHubToken);
        string targetRemote = ActionFitPackagePublisher.BuildTokenRemote(target.Organization, target.Name, request.GitHubToken);

        if (Directory.Exists(mirrorPath))
        {
            bool validMirror = ActionFitPackagePublisher.RunGitCapture(
                mirrorPath,
                "rev-parse --is-bare-repository",
                request.GitHubToken,
                out string bareOutput,
                out _,
                false) && string.Equals(bareOutput.Trim(), "true", StringComparison.Ordinal);
            if (!validMirror)
                Directory.Delete(mirrorPath, true);
        }

        if (!Directory.Exists(mirrorPath))
        {
            ActionFitPackagePublisher.RunGit(
                migrationRoot,
                $"clone --mirror {Quote(sourceRemote)} {Quote(mirrorPath)}",
                request.GitHubToken);
        }
        else
        {
            ActionFitPackagePublisher.RunGit(
                mirrorPath,
                $"remote set-url origin {Quote(sourceRemote)}",
                request.GitHubToken);
            ActionFitPackagePublisher.RunGit(mirrorPath, "fetch --prune origin", request.GitHubToken);
        }

        ActionFitPackagePublisher.RunGit(mirrorPath, $"push {Quote(targetRemote)} --all", request.GitHubToken);
        ActionFitPackagePublisher.RunGit(mirrorPath, $"push {Quote(targetRemote)} --tags", request.GitHubToken);
    }

    private static Dictionary<string, string> ReadRemoteRefs(RepositoryIdentity repository, string token)
    {
        string remote = ActionFitPackagePublisher.BuildTokenRemote(repository.Organization, repository.Name, token);
        ActionFitPackagePublisher.RunGitCapture(
            ActionFitPackagePaths.ProjectRoot,
            $"ls-remote --heads --tags {Quote(remote)}",
            token,
            out string output,
            out _);
        var refs = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (string line in output.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries))
        {
            string[] columns = line.Split(new[] { '\t', ' ' }, StringSplitOptions.RemoveEmptyEntries);
            if (columns.Length < 2 || columns[1].EndsWith("^{}", StringComparison.Ordinal)) continue;
            if (columns[1].StartsWith("refs/heads/", StringComparison.Ordinal) ||
                columns[1].StartsWith("refs/tags/", StringComparison.Ordinal))
                refs[columns[1]] = columns[0];
        }
        return refs;
    }

    private static int CountRefs(IReadOnlyDictionary<string, string> refs, string prefix)
        => refs.Keys.Count(value => value.StartsWith(prefix, StringComparison.Ordinal));

    private static string ComputeFingerprint(
        RepositoryMigrationState state,
        IReadOnlyDictionary<string, string> sourceRefs,
        IReadOnlyDictionary<string, string> targetRefs)
    {
        string canonical = string.Join("\n", new[]
        {
            state.SourceRepositoryUrl,
            state.TargetRepositoryUrl,
            state.SourceRepositoryIsPrivate.ToString(),
            state.TargetRepositoryExists.ToString(),
            state.TargetRepositoryIsPrivate.ToString(),
            state.SourceDefaultBranch,
            state.TargetDefaultBranch,
            string.Join(";", sourceRefs.OrderBy(pair => pair.Key).Select(pair => $"{pair.Key}={pair.Value}")),
            string.Join(";", targetRefs.OrderBy(pair => pair.Key).Select(pair => $"{pair.Key}={pair.Value}")),
        });
        return Sha256(canonical).Substring(0, 20);
    }

    private static string Sha256(string value)
    {
        using SHA256 sha = SHA256.Create();
        return BitConverter.ToString(sha.ComputeHash(Encoding.UTF8.GetBytes(value ?? ""))).Replace("-", "");
    }

    private static string BuildRepositoryUrl(string organization, string name)
        => $"https://github.com/{organization}/{name}.git";

    private static string Quote(string value) => "\"" + (value ?? "").Replace("\"", "\\\"") + "\"";

    internal readonly struct RepositoryIdentity : IEquatable<RepositoryIdentity>
    {
        internal RepositoryIdentity(string organization, string name)
        {
            Organization = organization ?? "";
            Name = name ?? "";
        }

        internal string Organization { get; }
        internal string Name { get; }
        internal string DisplayName => $"{Organization}/{Name}";
        internal string Url => BuildRepositoryUrl(Organization, Name);

        public bool Equals(RepositoryIdentity other)
            => string.Equals(Organization, other.Organization, StringComparison.OrdinalIgnoreCase) &&
               string.Equals(Name, other.Name, StringComparison.OrdinalIgnoreCase);
    }

    internal sealed class RepositoryMigrationState
    {
        internal bool Required;
        internal bool SourceRepositoryExists;
        internal bool SourceRepositoryIsPrivate;
        internal bool TargetRepositoryExists;
        internal bool TargetRepositoryIsPrivate;
        internal bool WillCreateTarget;
        internal bool WillMirrorRepository;
        internal string SourceRepositoryUrl = "";
        internal string TargetRepositoryUrl = "";
        internal string SourceDefaultBranch = "";
        internal string TargetDefaultBranch = "";
        internal int SourceBranchCount;
        internal int SourceTagCount;
        internal int TargetBranchCount;
        internal int TargetTagCount;
        internal int MissingRefCount;
        internal string Fingerprint = "";
    }

    internal sealed class MigrationResult
    {
        private MigrationResult(bool success, string message)
        {
            Success = success;
            Message = message ?? "";
        }

        internal bool Success { get; }
        internal string Message { get; }
        internal static MigrationResult Succeeded(string message) => new MigrationResult(true, message);
        internal static MigrationResult Failed(string message) => new MigrationResult(false, message);
    }
}
#endif
