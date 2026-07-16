#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using UnityEditor;
using UnityEngine;
using Debug = UnityEngine.Debug;

public static class ActionFitPackagePublisher
{
    private const string CatalogAppendAction = "upsertPackageVersion";
    private const string CatalogBatchAppendAction = "upsertPackageVersions";

    public const int DefaultMaxParallelPublishes = 4;
    public const int DefaultHttpTimeoutMilliseconds = 30000;

    public static bool Publish(ActionFitPackageCatalogSettings_SO settings, ActionFitPackageInfo_SO info, out string message)
    {
        if (settings == null) { message = "Package Manager settings asset is missing."; return false; }
        return Publish(settings, info, settings.DefaultGitHubProfile, out message);
    }

    public static bool Publish(ActionFitPackageCatalogSettings_SO settings, ActionFitPackageInfo_SO info, ActionFitPackageRepositoryVisibility repositoryVisibility, out string message)
    {
        if (settings == null) { message = "Package Manager settings asset is missing."; return false; }
        return Publish(settings, info, settings.GetRepositoryCreationProfile(repositoryVisibility), out message);
    }

    public static bool TryCreatePublishRequest(
        ActionFitPackageCatalogSettings_SO settings,
        ActionFitPackageInfo_SO info,
        ActionFitPackageRepositoryVisibility repositoryVisibility,
        out PublishRequest request,
        out string message)
    {
        request = null;
        message = "";
        if (settings == null) { message = "Package Manager settings asset is missing."; return false; }
        return TryCreatePublishRequest(settings, info, settings.GetRepositoryCreationProfile(repositoryVisibility), out request, out message);
    }

    public static PublishResult PublishRepository(PublishRequest request)
    {
        if (request == null) return PublishResult.Failed(null, "Publish request is missing.");

        var stopwatch = Stopwatch.StartNew();
        try
        {
            Debug.Log(
                $"[ActionFitPackageManager] Publish start: {request.PackageId}@{request.Version} " +
                $"-> {request.GitHubOrganization}/{request.RepoName}");
            EnsureRepository(request);
            Debug.Log($"[ActionFitPackageManager] Repository ready: {request.GitHubOrganization}/{request.RepoName}");

            PublishGitRepository(request);
            Debug.Log(
                $"[ActionFitPackageManager] Repository publish complete: {request.PackageId}@{request.Version} " +
                $"({stopwatch.ElapsedMilliseconds} ms)");

            return PublishResult.Succeeded(
                request,
                $"{request.PackageId}@{request.Version} package repository published.");
        }
        catch (Exception ex)
        {
            Debug.LogWarning(
                $"[ActionFitPackageManager] Repository publish failed: {request.PackageId}@{request.Version} " +
                $"({stopwatch.ElapsedMilliseconds} ms)\n{ex.Message}");
            return PublishResult.Failed(request, ex.Message);
        }
    }

    /// <summary>
    /// Checks whether the target repository and immutable version tag already exist without changing GitHub state.
    /// </summary>
    public static bool TryGetRemoteState(PublishRequest request, out RemoteState state, out string message)
    {
        state = null;
        message = "";
        if (request == null)
        {
            message = "Publish request is missing.";
            return false;
        }

        var stopwatch = Stopwatch.StartNew();
        try
        {
            string repoPath = $"{request.GitHubOrganization}/{request.RepoName}";
            if (!TryGetRepositoryMetadata(
                    request.GitHubOrganization,
                    request.RepoName,
                    request.GitHubToken,
                    out bool repositoryExists,
                    out bool repositoryIsPrivate,
                    out string defaultBranch,
                    out string repositoryError))
            {
                throw new InvalidOperationException(repositoryError);
            }
            bool tagExists = repositoryExists && GitHubResourceExists(
                $"https://api.github.com/repos/{repoPath}/git/ref/tags/{Uri.EscapeDataString(request.Version)}",
                request.GitHubToken,
                true);

            state = new RemoteState(repositoryExists, tagExists, repositoryIsPrivate, defaultBranch);
            message = repositoryExists
                ? tagExists
                    ? $"Repository and tag {request.Version} already exist."
                    : $"Repository exists and tag {request.Version} is available."
                : "Repository does not exist and would be created during publish.";
            Debug.Log(
                $"[ActionFitPackageManager] GitHub preflight complete: {request.PackageId}@{request.Version} " +
                $"({stopwatch.ElapsedMilliseconds} ms)");
            return true;
        }
        catch (Exception ex)
        {
            message = $"GitHub remote preflight failed: {ex.Message}";
            Debug.LogWarning(
                $"[ActionFitPackageManager] GitHub preflight failed: {request.PackageId}@{request.Version} " +
                $"({stopwatch.ElapsedMilliseconds} ms)\n{message}");
            return false;
        }
    }

    public static bool TryAppendCatalogBatch(IReadOnlyList<CatalogAppendItem> items, out string message)
        => TryAppendCatalogBatch(items, CancellationToken.None, out message);

    internal static bool TryAppendCatalogBatch(
        IReadOnlyList<CatalogAppendItem> items,
        CancellationToken cancellationToken,
        out string message)
    {
        message = "";
        if (items == null || items.Count == 0)
        {
            message = "No catalog rows to append.";
            return true;
        }

        var stopwatch = Stopwatch.StartNew();
        try
        {
            Debug.Log($"[ActionFitPackageManager] Catalog batch append start: {items.Count} row(s)");
            AppendCatalogBatch(items, cancellationToken);
            message = $"{items.Count} catalog row(s) appended by batch request in {stopwatch.ElapsedMilliseconds} ms.";
            Debug.Log($"[ActionFitPackageManager] Catalog batch append complete: {message}");
            return true;
        }
        catch (Exception ex)
        {
            message = ex.Message;
            Debug.LogWarning(
                $"[ActionFitPackageManager] Catalog batch append failed after {stopwatch.ElapsedMilliseconds} ms.\n{message}");
            return false;
        }
    }

    public static bool TryAppendCatalogSerial(IReadOnlyList<CatalogAppendItem> items, out string message)
        => TryAppendCatalogSerial(items, CancellationToken.None, out message);

    internal static bool TryAppendCatalogSerial(
        IReadOnlyList<CatalogAppendItem> items,
        CancellationToken cancellationToken,
        out string message)
    {
        message = "";
        if (items == null || items.Count == 0)
        {
            message = "No catalog rows to append.";
            return true;
        }

        var appended = new List<string>();
        var stopwatch = Stopwatch.StartNew();
        Debug.Log($"[ActionFitPackageManager] Catalog serial fallback start: {items.Count} row(s)");
        foreach (var item in items)
        {
            try
            {
                cancellationToken.ThrowIfCancellationRequested();
                AppendCatalog(item, cancellationToken);
                appended.Add(item.CatalogId);
            }
            catch (Exception ex)
            {
                message =
                    $"Catalog serial append stopped.\n\nSucceeded:\n{string.Join("\n", appended)}\n\nFailed:\n{item.CatalogId}\n{ex.Message}";
                Debug.LogWarning(
                    $"[ActionFitPackageManager] Catalog serial fallback failed after {stopwatch.ElapsedMilliseconds} ms.\n{message}");
                return false;
            }
        }

        message = $"{items.Count} catalog row(s) appended one by one in {stopwatch.ElapsedMilliseconds} ms.";
        Debug.Log($"[ActionFitPackageManager] Catalog serial fallback complete: {message}");
        return true;
    }

    private static bool Publish(ActionFitPackageCatalogSettings_SO settings, ActionFitPackageInfo_SO info, ActionFitPackageGitHubProfile github, out string message)
    {
        if (!TryCreatePublishRequest(settings, info, github, out var request, out message))
            return false;

        try
        {
            EditorUtility.DisplayProgressBar("ActionFit Package Publish", "Checking GitHub repository...", 0.1f);
            EnsureRepository(request);
            Debug.Log($"[ActionFitPackageManager] Repository ready: {request.GitHubOrganization}/{request.RepoName}");

            EditorUtility.DisplayProgressBar("ActionFit Package Publish", "Publishing package repository...", 0.45f);
            PublishGitRepository(request);

            EditorUtility.DisplayProgressBar("ActionFit Package Publish", "Appending catalog row...", 0.8f);
            AppendCatalog(request.CatalogItem);
            Debug.Log($"[ActionFitPackageManager] Catalog append complete: {request.PackageId}@{request.Version}");

            message = $"{request.PackageId}@{request.Version} published and catalog append requested.";
            return true;
        }
        catch (Exception ex)
        {
            message = ex.Message;
            return false;
        }
        finally
        {
            EditorUtility.ClearProgressBar();
        }
    }

    private static bool TryCreatePublishRequest(
        ActionFitPackageCatalogSettings_SO settings,
        ActionFitPackageInfo_SO info,
        ActionFitPackageGitHubProfile github,
        out PublishRequest request,
        out string message)
    {
        request = null;
        message = "";
        if (info == null) { message = "Select an ActionFitPackageInfo_SO asset first."; return false; }
        if (string.IsNullOrWhiteSpace(github.Organization)) { message = $"{github.Label} GitHub Org is empty."; return false; }
        if (string.IsNullOrWhiteSpace(github.Token)) { message = $"{github.Label} GitHub Token is empty."; return false; }

        string packageRoot = ActionFitPackageInfoUtility.FindPackageRoot(info);
        if (string.IsNullOrWhiteSpace(packageRoot)) { message = "Could not find package.json above selected PackageInfo asset."; return false; }

        string packageJsonPath = Path.Combine(packageRoot, "package.json").Replace("\\", "/");
        var manifest = ActionFitPackageManifest.Read(packageJsonPath);
        if (string.IsNullOrWhiteSpace(manifest.Name) || string.IsNullOrWhiteSpace(manifest.Version))
        {
            message = "package.json must contain name and version.";
            return false;
        }

        if (!string.Equals(info.PackageId, manifest.Name, StringComparison.Ordinal))
        {
            message = $"PackageInfo packageId({info.PackageId}) does not match package.json name({manifest.Name}).";
            return false;
        }

        if (string.IsNullOrWhiteSpace(info.RepoName))
        {
            message = "PackageInfo repoName is empty.";
            return false;
        }

        string spreadsheetId = ExtractSpreadsheetId(settings.SpreadSheetUrl);
        string repoUrl = $"https://github.com/{github.Organization}/{info.RepoName}.git";
        string dependencies = !string.IsNullOrWhiteSpace(info.DependenciesOverride)
            ? info.DependenciesOverride
            : manifest.Dependencies;

        var catalogItem = new CatalogAppendItem(
            settings.WebAppUrl,
            settings.FetchToken,
            settings.SpreadSheetUrl,
            spreadsheetId,
            manifest.Name,
            info.DisplayName,
            repoUrl,
            info.Owner,
            info.Status,
            info.Description,
            manifest.Version,
            manifest.Unity,
            info.ReleaseNote,
            dependencies);

        request = new PublishRequest(
            settings.PublishRoot,
            packageRoot,
            info.RepoName,
            github.Label,
            github.Organization,
            github.Token,
            github.IsPrivate,
            manifest.Name,
            manifest.Version,
            catalogItem);
        return true;
    }

    internal static void EnsureRepository(PublishRequest request)
    {
        string repoPath = $"{request.GitHubOrganization}/{request.RepoName}";
        var get = CreateGitHubRequest($"https://api.github.com/repos/{repoPath}", request.GitHubToken, "GET");
        try
        {
            using var response = (HttpWebResponse)get.GetResponse();
            if ((int)response.StatusCode >= 200 && (int)response.StatusCode < 300)
            {
                using var reader = new StreamReader(response.GetResponseStream(), Encoding.UTF8);
                GitHubRepositoryResponse repository = JsonUtility.FromJson<GitHubRepositoryResponse>(reader.ReadToEnd());
                if (repository == null || repository.@private != request.GitHubIsPrivate)
                {
                    throw new InvalidOperationException(
                        $"Existing repository visibility does not match the selected {request.GitHubLabel} profile. " +
                        "The publisher will not change repository visibility automatically.");
                }
                return;
            }
        }
        catch (WebException ex) when ((ex.Response as HttpWebResponse)?.StatusCode == HttpStatusCode.NotFound)
        {
            string visibility = request.GitHubIsPrivate ? "true" : "false";
            string body = $"{{\"name\":\"{EscapeJson(request.RepoName)}\",\"private\":{visibility},\"auto_init\":false}}";
            var create = CreateGitHubRequest($"https://api.github.com/orgs/{request.GitHubOrganization}/repos", request.GitHubToken, "POST");
            WriteBody(create, body);
            using var response = (HttpWebResponse)create.GetResponse();
            if ((int)response.StatusCode < 200 || (int)response.StatusCode >= 300)
                throw new InvalidOperationException($"GitHub repository create failed ({request.GitHubLabel}): {response.StatusCode}");
            return;
        }
    }

    internal static bool TryGetRepositoryMetadata(
        string organization,
        string repoName,
        string token,
        out bool exists,
        out bool isPrivate,
        out string defaultBranch,
        out string message)
    {
        return TryGetRepositoryMetadata(
            organization,
            repoName,
            token,
            out exists,
            out isPrivate,
            out defaultBranch,
            out _,
            out message);
    }

    internal static bool TryGetRepositoryMetadata(
        string organization,
        string repoName,
        string token,
        out bool exists,
        out bool isPrivate,
        out string defaultBranch,
        out bool archived,
        out string message)
    {
        exists = false;
        isPrivate = false;
        defaultBranch = "";
        archived = false;
        message = "";
        try
        {
            var get = CreateGitHubRequest(
                $"https://api.github.com/repos/{organization}/{repoName}",
                token,
                "GET");
            using var response = (HttpWebResponse)get.GetResponse();
            using var reader = new StreamReader(response.GetResponseStream(), Encoding.UTF8);
            GitHubRepositoryResponse repository = JsonUtility.FromJson<GitHubRepositoryResponse>(reader.ReadToEnd());
            if (repository == null)
            {
                message = $"GitHub returned an invalid repository response for {organization}/{repoName}.";
                return false;
            }

            exists = true;
            isPrivate = repository.@private;
            defaultBranch = repository.default_branch ?? "";
            archived = repository.archived;
            return true;
        }
        catch (WebException ex) when ((ex.Response as HttpWebResponse)?.StatusCode == HttpStatusCode.NotFound)
        {
            ex.Response?.Dispose();
            return true;
        }
        catch (Exception ex)
        {
            message = $"GitHub repository inspection failed for {organization}/{repoName}: {ex.Message}";
            return false;
        }
    }

    internal static void SetDefaultBranch(PublishRequest request, string defaultBranch)
    {
        if (request == null) throw new ArgumentNullException(nameof(request));
        if (string.IsNullOrWhiteSpace(defaultBranch)) return;

        var update = CreateGitHubRequest(
            $"https://api.github.com/repos/{request.GitHubOrganization}/{request.RepoName}",
            request.GitHubToken,
            "PATCH");
        WriteBody(update, $"{{\"default_branch\":\"{EscapeJson(defaultBranch)}\"}}");
        using var response = (HttpWebResponse)update.GetResponse();
        if ((int)response.StatusCode < 200 || (int)response.StatusCode >= 300)
            throw new InvalidOperationException($"GitHub default branch update failed: {response.StatusCode}");
    }

    private static bool GitHubResourceExists(string url, string token, bool conflictMeansMissing = false)
    {
        var get = CreateGitHubRequest(url, token, "GET");
        try
        {
            using var response = (HttpWebResponse)get.GetResponse();
            return (int)response.StatusCode >= 200 && (int)response.StatusCode < 300;
        }
        catch (WebException ex) when (
            ex.Response is HttpWebResponse response &&
            IsMissingGitHubResourceStatus(response.StatusCode, conflictMeansMissing))
        {
            ex.Response?.Dispose();
            return false;
        }
    }

    internal static bool IsMissingGitHubResourceStatus(HttpStatusCode statusCode, bool conflictMeansMissing)
        => statusCode == HttpStatusCode.NotFound ||
           conflictMeansMissing && statusCode == HttpStatusCode.Conflict;

    internal static HttpWebRequest CreateGitHubRequest(string url, string token, string method)
    {
        var request = (HttpWebRequest)WebRequest.Create(url);
        request.Method = method;
        request.Accept = "application/vnd.github+json";
        request.ContentType = "application/json";
        request.UserAgent = "ActionFitPackageManager";
        request.Headers["Authorization"] = $"Bearer {token}";
        request.Headers["X-GitHub-Api-Version"] = "2022-11-28";
        ConfigureHttpRequest(request);
        return request;
    }

    private static void PublishGitRepository(PublishRequest request)
    {
        string publishRoot = ExpandPath(request.PublishRoot);
        Directory.CreateDirectory(publishRoot);

        string dest = Path.Combine(publishRoot, request.RepoName);
        string remote = BuildTokenRemote(request);
        Debug.Log($"[ActionFitPackageManager] Publish clone path: {dest}");
        if (!Directory.Exists(Path.Combine(dest, ".git")))
        {
            Debug.Log($"[ActionFitPackageManager] Cloning package repository: {request.GitHubOrganization}/{request.RepoName}");
            RunGit(publishRoot, $"clone {Quote(remote)} {Quote(dest)}", request.GitHubToken);
        }

        RunGit(dest, $"remote set-url origin {Quote(remote)}", request.GitHubToken);
        RunGit(dest, "fetch --prune origin", request.GitHubToken);
        RunGit(dest, "reset --hard", request.GitHubToken, false);
        RunGit(dest, "clean -fdx", request.GitHubToken, false);

        if (RunGit(dest, "show-ref --verify --quiet refs/remotes/origin/main", request.GitHubToken, false))
        {
            RunGit(dest, "checkout -B main origin/main", request.GitHubToken);
            RunGit(dest, "reset --hard origin/main", request.GitHubToken);
        }
        else
        {
            RunGit(dest, "checkout -B main", request.GitHubToken);
        }

        ClearDirectoryExceptGit(dest);
        CopyDirectory(Path.GetFullPath(request.PackageRoot), dest);
        Debug.Log($"[ActionFitPackageManager] Copied package files: {request.PackageId}@{request.Version}");

        RunGit(dest, "add -A", request.GitHubToken);

        if (!RunGit(dest, "diff --cached --quiet", request.GitHubToken, false))
        {
            RunGit(dest, $"commit -m {Quote($"{request.PackageId} {request.Version}")}", request.GitHubToken);
            Debug.Log($"[ActionFitPackageManager] Package commit created: {request.PackageId}@{request.Version}");
        }
        else
        {
            Debug.Log($"[ActionFitPackageManager] No package file changes to commit: {request.PackageId}@{request.Version}");
        }

        string tagRef = $"refs/tags/{request.Version}";
        bool remoteTagExists = RunGit(dest, $"ls-remote --exit-code --tags origin {Quote(tagRef)}", request.GitHubToken, false);
        if (!remoteTagExists)
        {
            if (RunGit(dest, $"show-ref --verify --quiet {Quote(tagRef)}", request.GitHubToken, false))
                RunGit(dest, $"tag -d {Quote(request.Version)}", request.GitHubToken);

            RunGit(dest, $"tag {Quote(request.Version)}", request.GitHubToken);
            Debug.Log($"[ActionFitPackageManager] Package tag prepared: {request.Version}");
        }
        else
        {
            Debug.Log($"[ActionFitPackageManager] Remote tag already exists, tag push will be skipped: {request.Version}");
        }

        Debug.Log($"[ActionFitPackageManager] Pushing package main branch: {request.GitHubOrganization}/{request.RepoName}");
        RunGit(dest, "push -u origin main", request.GitHubToken);
        Debug.Log($"[ActionFitPackageManager] Package main branch pushed: {request.GitHubOrganization}/{request.RepoName}");
        if (!remoteTagExists)
        {
            Debug.Log($"[ActionFitPackageManager] Pushing package tag: {request.Version}");
            RunGit(dest, $"push origin {Quote(request.Version)}", request.GitHubToken);
            Debug.Log($"[ActionFitPackageManager] Package tag pushed: {request.Version}");
        }
    }

    private static void AppendCatalog(CatalogAppendItem item, CancellationToken cancellationToken = default)
    {
        ValidateCatalogSettings(item);

        string body =
            "{" +
            $"\"token\":\"{EscapeJson(item.FetchToken)}\"," +
            $"\"action\":\"{CatalogAppendAction}\"," +
            $"\"ssId\":\"{EscapeJson(item.SpreadsheetId)}\"," +
            BuildCatalogPayloadJson(item) +
            "}";

        string text = PostCatalogRequest(item, body, cancellationToken);
        var result = JsonUtility.FromJson<CatalogAppendResponse>(text);
        ValidateCatalogAppendResponse(result, item, text);
    }

    private static void AppendCatalogBatch(
        IReadOnlyList<CatalogAppendItem> items,
        CancellationToken cancellationToken = default)
    {
        if (items == null || items.Count == 0) return;
        var first = items[0];
        ValidateCatalogSettings(first);
        foreach (var item in items)
        {
            ValidateCatalogSettings(item);
            if (!string.Equals(first.WebAppUrl, item.WebAppUrl, StringComparison.Ordinal) ||
                !string.Equals(first.FetchToken, item.FetchToken, StringComparison.Ordinal) ||
                !string.Equals(first.SpreadsheetId, item.SpreadsheetId, StringComparison.Ordinal))
                throw new InvalidOperationException("All batch catalog rows must use the same Web App URL, token, and spreadsheet.");
        }

        string body =
            "{" +
            $"\"token\":\"{EscapeJson(first.FetchToken)}\"," +
            $"\"action\":\"{CatalogBatchAppendAction}\"," +
            $"\"ssId\":\"{EscapeJson(first.SpreadsheetId)}\"," +
            "\"items\":[" +
            string.Join(",", items.Select(item => "{" + BuildCatalogPayloadJson(item) + "}")) +
            "]" +
            "}";

        string text = PostCatalogRequest(first, body, cancellationToken);
        var result = JsonUtility.FromJson<CatalogBatchAppendResponse>(text);
        ValidateCatalogBatchAppendResponse(result, items, text);
    }

    private static string BuildCatalogPayloadJson(CatalogAppendItem item)
    {
        return
            "\"package\":{" +
            $"\"package_id\":\"{EscapeJson(item.PackageId)}\"," +
            $"\"display_name\":\"{EscapeJson(item.DisplayName)}\"," +
            $"\"repo_url\":\"{EscapeJson(item.RepoUrl)}\"," +
            $"\"owner\":\"{EscapeJson(item.Owner)}\"," +
            $"\"status\":\"{EscapeJson(item.Status)}\"," +
            $"\"description\":\"{EscapeJson(item.Description)}\"" +
            "}," +
            "\"version\":{" +
            $"\"catalog_id\":\"{EscapeJson(item.CatalogId)}\"," +
            $"\"version\":\"{EscapeJson(item.Version)}\"," +
            $"\"unity_min\":\"{EscapeJson(item.UnityMin)}\"," +
            $"\"changelog\":\"{EscapeJson(item.Changelog)}\"," +
            $"\"dependencies\":\"{EscapeJson(item.Dependencies)}\"" +
            "}";
    }

    private static string PostCatalogRequest(
        CatalogAppendItem item,
        string body,
        CancellationToken cancellationToken)
    {
        string url = $"{item.WebAppUrl}?token={Uri.EscapeDataString(item.FetchToken)}&ssId={Uri.EscapeDataString(item.SpreadsheetId)}";
        var request = (HttpWebRequest)WebRequest.Create(url);
        request.Method = "POST";
        request.ContentType = "application/json; charset=utf-8";
        ConfigureHttpRequest(request);

        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            using var registration = cancellationToken.Register(request.Abort);
            WriteBody(request, body);

            using var response = (HttpWebResponse)request.GetResponse();
            using var reader = new StreamReader(response.GetResponseStream(), Encoding.UTF8);
            string text = reader.ReadToEnd();
            if ((int)response.StatusCode < 200 || (int)response.StatusCode >= 300)
                throw new InvalidOperationException($"Catalog append failed: {text}");

            return text;
        }
        catch (WebException ex) when (cancellationToken.IsCancellationRequested)
        {
            throw new OperationCanceledException("Catalog append was canceled.", ex, cancellationToken);
        }
        catch (WebException ex) when (ex.Status == WebExceptionStatus.Timeout)
        {
            throw new TimeoutException(
                $"Catalog request timed out after {DefaultHttpTimeoutMilliseconds / 1000} seconds.",
                ex);
        }
    }

    private static void ValidateCatalogSettings(CatalogAppendItem item)
    {
        if (item == null)
            throw new InvalidOperationException("Catalog append item is missing.");
        if (string.IsNullOrWhiteSpace(item.WebAppUrl) ||
            string.IsNullOrWhiteSpace(item.FetchToken) ||
            string.IsNullOrWhiteSpace(item.SpreadsheetUrl))
            throw new InvalidOperationException("Spreadsheet URL, Web App URL, and Fetch Token are required for catalog append.");
        if (string.IsNullOrWhiteSpace(item.SpreadsheetId))
            throw new InvalidOperationException("Spreadsheet URL is invalid.");
    }

    private static void ValidateCatalogAppendResponse(CatalogAppendResponse result, CatalogAppendItem item, string responseText)
    {
        if (result == null || !result.success ||
            !string.Equals(result.package_id, item.PackageId, StringComparison.Ordinal) ||
            !string.Equals(result.version, item.Version, StringComparison.Ordinal) ||
            !string.Equals(result.catalog_id, item.CatalogId, StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                "Catalog append did not return package catalog confirmation. " +
                "Update the Apps Script Web App deployment and try again.\n" + responseText);
        }
    }

    private static void ValidateCatalogBatchAppendResponse(CatalogBatchAppendResponse result, IReadOnlyList<CatalogAppendItem> items, string responseText)
    {
        if (result == null || !result.success)
        {
            throw new InvalidOperationException(
                "Catalog batch append did not return success. " +
                "Update the Apps Script Web App deployment and try again.\n" + responseText);
        }

        if (result.items != null && result.items.Length > 0)
        {
            if (result.count > 0 && result.count != items.Count)
                throw new InvalidOperationException($"Catalog batch append count mismatch. Expected {items.Count}, got {result.count}.\n{responseText}");

            foreach (var item in items)
            {
                var responseItem = result.items.FirstOrDefault(r =>
                    r != null &&
                    string.Equals(r.package_id, item.PackageId, StringComparison.Ordinal) &&
                    string.Equals(r.version, item.Version, StringComparison.Ordinal));
                ValidateCatalogAppendResponse(responseItem, item, responseText);
            }

            return;
        }

        if (result.count == items.Count)
            return;

        throw new InvalidOperationException(
            "Catalog batch append did not return count or item confirmations. " +
            "Update the Apps Script Web App deployment and try again.\n" + responseText);
    }

    internal static bool RunGit(string workingDirectory, string arguments, string token, bool throwOnFailure = true)
    {
        var startInfo = new ProcessStartInfo("git", arguments)
        {
            WorkingDirectory = workingDirectory,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        };

        using var process = Process.Start(startInfo);
        if (process == null)
            throw new InvalidOperationException("Could not start git process.");

        Task<string> outputTask = process.StandardOutput.ReadToEndAsync();
        Task<string> errorTask = process.StandardError.ReadToEndAsync();
        process.WaitForExit();
        string output = outputTask.GetAwaiter().GetResult();
        string error = errorTask.GetAwaiter().GetResult();

        if (process.ExitCode == 0) return true;
        if (!throwOnFailure) return false;

        string sanitizedArguments = Sanitize(arguments, token);
        string sanitizedOutput = Sanitize(output + "\n" + error, token);
        throw new InvalidOperationException($"git {sanitizedArguments} failed:\n{sanitizedOutput}");
    }

    internal static bool RunGitCapture(
        string workingDirectory,
        string arguments,
        string token,
        out string output,
        out string error,
        bool throwOnFailure = true)
    {
        var startInfo = new ProcessStartInfo("git", arguments)
        {
            WorkingDirectory = workingDirectory,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        };

        using var process = Process.Start(startInfo);
        if (process == null)
            throw new InvalidOperationException("Could not start git process.");

        Task<string> outputTask = process.StandardOutput.ReadToEndAsync();
        Task<string> errorTask = process.StandardError.ReadToEndAsync();
        process.WaitForExit();
        output = outputTask.GetAwaiter().GetResult();
        error = errorTask.GetAwaiter().GetResult();

        if (process.ExitCode == 0) return true;
        if (!throwOnFailure) return false;

        throw new InvalidOperationException(
            $"git {Sanitize(arguments, token)} failed:\n{Sanitize(output + "\n" + error, token)}");
    }

    private static string BuildTokenRemote(PublishRequest request)
        => BuildTokenRemote(request.GitHubOrganization, request.RepoName, request.GitHubToken);

    internal static string BuildTokenRemote(string organization, string repoName, string token)
        => $"https://x-access-token:{token}@github.com/{organization}/{repoName}.git";

    internal static void WriteBody(HttpWebRequest request, string body)
    {
        byte[] bytes = Encoding.UTF8.GetBytes(body);
        request.ContentLength = bytes.Length;
        using var stream = request.GetRequestStream();
        stream.Write(bytes, 0, bytes.Length);
    }

    internal static void ConfigureHttpRequest(HttpWebRequest request)
    {
        if (request == null) throw new ArgumentNullException(nameof(request));
        request.Timeout = DefaultHttpTimeoutMilliseconds;
        request.ReadWriteTimeout = DefaultHttpTimeoutMilliseconds;
    }

    private static void ClearDirectoryExceptGit(string path)
    {
        foreach (string file in Directory.GetFiles(path))
            File.Delete(file);

        foreach (string dir in Directory.GetDirectories(path))
        {
            if (Path.GetFileName(dir) == ".git") continue;
            Directory.Delete(dir, true);
        }
    }

    private static void CopyDirectory(string source, string destination)
    {
        Directory.CreateDirectory(destination);
        foreach (string dir in Directory.GetDirectories(source, "*", SearchOption.AllDirectories))
        {
            if (dir.Contains($"{Path.DirectorySeparatorChar}.git{Path.DirectorySeparatorChar}")) continue;
            Directory.CreateDirectory(dir.Replace(source, destination));
        }

        foreach (string file in Directory.GetFiles(source, "*", SearchOption.AllDirectories))
        {
            if (file.Contains($"{Path.DirectorySeparatorChar}.git{Path.DirectorySeparatorChar}")) continue;
            File.Copy(file, file.Replace(source, destination), true);
        }
    }

    internal static string ExpandPath(string path)
    {
        if (string.IsNullOrWhiteSpace(path)) path = "~/upm-publish";
        if (path.StartsWith("~/", StringComparison.Ordinal))
            return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), path[2..]);
        return Path.GetFullPath(path);
    }

    private static string ExtractSpreadsheetId(string input)
    {
        if (string.IsNullOrWhiteSpace(input)) return "";
        string s = input.Trim();
        var match = Regex.Match(s, @"/spreadsheets/d/([a-zA-Z0-9-_]+)");
        return match.Success ? match.Groups[1].Value : s;
    }

    private static string Quote(string value) => "\"" + (value ?? "").Replace("\"", "\\\"") + "\"";
    private static string EscapeJson(string value) => (value ?? "").Replace("\\", "\\\\").Replace("\"", "\\\"").Replace("\n", "\\n").Replace("\r", "\\r");
    private static string Sanitize(string text, string token) => string.IsNullOrEmpty(token) ? text : text.Replace(token, "***");

    public sealed class PublishRequest
    {
        public PublishRequest(
            string publishRoot,
            string packageRoot,
            string repoName,
            string gitHubLabel,
            string gitHubOrganization,
            string gitHubToken,
            bool gitHubIsPrivate,
            string packageId,
            string version,
            CatalogAppendItem catalogItem)
        {
            PublishRoot = publishRoot;
            PackageRoot = packageRoot;
            RepoName = repoName;
            GitHubLabel = gitHubLabel;
            GitHubOrganization = gitHubOrganization;
            GitHubToken = gitHubToken;
            GitHubIsPrivate = gitHubIsPrivate;
            PackageId = packageId;
            Version = version;
            CatalogItem = catalogItem;
        }

        public string PublishRoot { get; }
        public string PackageRoot { get; }
        public string RepoName { get; }
        public string GitHubLabel { get; }
        public string GitHubOrganization { get; }
        public string GitHubToken { get; }
        public bool GitHubIsPrivate { get; }
        public string PackageId { get; }
        public string Version { get; }
        public CatalogAppendItem CatalogItem { get; }
        public string CatalogId => CatalogItem?.CatalogId ?? $"{PackageId}@{Version}";
        public string SourceRepositoryUrl { get; internal set; }
    }

    public sealed class CatalogAppendItem
    {
        public CatalogAppendItem(
            string webAppUrl,
            string fetchToken,
            string spreadsheetUrl,
            string spreadsheetId,
            string packageId,
            string displayName,
            string repoUrl,
            string owner,
            string status,
            string description,
            string version,
            string unityMin,
            string changelog,
            string dependencies)
        {
            WebAppUrl = webAppUrl;
            FetchToken = fetchToken;
            SpreadsheetUrl = spreadsheetUrl;
            SpreadsheetId = spreadsheetId;
            PackageId = packageId;
            DisplayName = displayName;
            RepoUrl = repoUrl;
            Owner = owner;
            Status = status;
            Description = description;
            Version = version;
            UnityMin = unityMin;
            Changelog = changelog;
            Dependencies = dependencies;
        }

        public string WebAppUrl { get; }
        public string FetchToken { get; }
        public string SpreadsheetUrl { get; }
        public string SpreadsheetId { get; }
        public string PackageId { get; }
        public string DisplayName { get; }
        public string RepoUrl { get; }
        public string Owner { get; }
        public string Status { get; }
        public string Description { get; }
        public string Version { get; }
        public string UnityMin { get; }
        public string Changelog { get; }
        public string Dependencies { get; }
        public string CatalogId => PackageId + "@" + Version;
    }

    public sealed class PublishResult
    {
        private PublishResult(PublishRequest request, bool success, string message)
        {
            Request = request;
            Success = success;
            Message = message;
        }

        public PublishRequest Request { get; }
        public bool Success { get; }
        public string Message { get; }

        public static PublishResult Succeeded(PublishRequest request, string message) => new(request, true, message);
        public static PublishResult Failed(PublishRequest request, string message) => new(request, false, message);
    }

    /// <summary>
    /// Read-only GitHub repository and immutable tag state used by publish preflight.
    /// </summary>
    public sealed class RemoteState
    {
        public RemoteState(
            bool repositoryExists,
            bool tagExists,
            bool repositoryIsPrivate = false,
            string defaultBranch = "")
        {
            RepositoryExists = repositoryExists;
            TagExists = tagExists;
            RepositoryIsPrivate = repositoryIsPrivate;
            DefaultBranch = defaultBranch ?? "";
        }

        public bool RepositoryExists { get; }
        public bool TagExists { get; }
        public bool RepositoryIsPrivate { get; }
        public string DefaultBranch { get; }
    }

    [Serializable]
    private sealed class GitHubRepositoryResponse
    {
        public bool @private;
        public string default_branch;
        public bool archived;
    }

    [Serializable]
    private sealed class CatalogAppendResponse
    {
        public bool success;
        public string package_id;
        public string version;
        public string catalog_id;
    }

    [Serializable]
    private sealed class CatalogBatchAppendResponse
    {
        public bool success;
        public int count;
        public CatalogAppendResponse[] items;
    }
}
#endif
