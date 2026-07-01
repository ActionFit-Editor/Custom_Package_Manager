#if UNITY_EDITOR
using System;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Text;
using System.Text.RegularExpressions;
using UnityEditor;
using UnityEngine;
using Debug = UnityEngine.Debug;

public static class ActionFitPackagePublisher
{
    private const string CatalogAppendAction = "upsertPackageVersion";

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

    private static bool Publish(ActionFitPackageCatalogSettings_SO settings, ActionFitPackageInfo_SO info, ActionFitPackageGitHubProfile github, out string message)
    {
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

        try
        {
            EditorUtility.DisplayProgressBar("ActionFit Package Publish", "Checking GitHub repository...", 0.1f);
            EnsureRepository(github, info.RepoName);

            EditorUtility.DisplayProgressBar("ActionFit Package Publish", "Preparing package repository...", 0.45f);
            PublishGitRepository(settings, github, packageRoot, info.RepoName, manifest);

            message = $"{manifest.Name}@{manifest.Version} prepared locally. Git push, tag push, and catalog append are disabled.";
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

    private static void EnsureRepository(ActionFitPackageGitHubProfile github, string repoName)
    {
        string repoPath = $"{github.Organization}/{repoName}";
        var get = CreateGitHubRequest($"https://api.github.com/repos/{repoPath}", github.Token, "GET");
        try
        {
            using var response = (HttpWebResponse)get.GetResponse();
            if ((int)response.StatusCode >= 200 && (int)response.StatusCode < 300) return;
        }
        catch (WebException ex) when ((ex.Response as HttpWebResponse)?.StatusCode == HttpStatusCode.NotFound)
        {
            string visibility = github.IsPrivate ? "true" : "false";
            string body = $"{{\"name\":\"{EscapeJson(repoName)}\",\"private\":{visibility},\"auto_init\":false}}";
            var create = CreateGitHubRequest($"https://api.github.com/orgs/{github.Organization}/repos", github.Token, "POST");
            WriteBody(create, body);
            using var response = (HttpWebResponse)create.GetResponse();
            if ((int)response.StatusCode < 200 || (int)response.StatusCode >= 300)
                throw new InvalidOperationException($"GitHub repository create failed ({github.Label}): {response.StatusCode}");
            return;
        }
    }

    private static HttpWebRequest CreateGitHubRequest(string url, string token, string method)
    {
        var request = (HttpWebRequest)WebRequest.Create(url);
        request.Method = method;
        request.Accept = "application/vnd.github+json";
        request.ContentType = "application/json";
        request.UserAgent = "ActionFitPackageManager";
        request.Headers["Authorization"] = $"Bearer {token}";
        request.Headers["X-GitHub-Api-Version"] = "2022-11-28";
        return request;
    }

    private static void PublishGitRepository(ActionFitPackageCatalogSettings_SO settings, ActionFitPackageGitHubProfile github, string packageRoot, string repoName, ActionFitPackageManifest manifest)
    {
        string publishRoot = ExpandPath(settings.PublishRoot);
        Directory.CreateDirectory(publishRoot);

        string dest = Path.Combine(publishRoot, repoName);
        string remote = BuildTokenRemote(github, repoName);
        if (!Directory.Exists(Path.Combine(dest, ".git")))
            RunGit(publishRoot, $"clone {Quote(remote)} {Quote(dest)}", github.Token);

        RunGit(dest, $"remote set-url origin {Quote(remote)}", github.Token);
        RunGit(dest, "fetch --prune origin", github.Token);
        RunGit(dest, "reset --hard", github.Token, false);
        RunGit(dest, "clean -fdx", github.Token, false);

        if (RunGit(dest, "show-ref --verify --quiet refs/remotes/origin/main", github.Token, false))
        {
            RunGit(dest, "checkout -B main origin/main", github.Token);
            RunGit(dest, "reset --hard origin/main", github.Token);
        }
        else
        {
            RunGit(dest, "checkout -B main", github.Token);
        }

        ClearDirectoryExceptGit(dest);
        CopyDirectory(Path.GetFullPath(packageRoot), dest);

        RunGit(dest, "add -A", github.Token);

        if (!RunGit(dest, "diff --cached --quiet", github.Token, false))
            RunGit(dest, $"commit -m {Quote($"{manifest.Name} {manifest.Version}")}", github.Token);

        string tagRef = $"refs/tags/{manifest.Version}";
        bool remoteTagExists = RunGit(dest, $"ls-remote --exit-code --tags origin {Quote(tagRef)}", github.Token, false);
        if (!remoteTagExists)
        {
            if (RunGit(dest, $"show-ref --verify --quiet {Quote(tagRef)}", github.Token, false))
                RunGit(dest, $"tag -d {Quote(manifest.Version)}", github.Token);

            RunGit(dest, $"tag {Quote(manifest.Version)}", github.Token);
        }

        Debug.Log(
            $"[ActionFitPackageManager] Prepared local package publish clone without remote push: " +
            $"{dest} ({manifest.Name}@{manifest.Version})");
    }

    private static void AppendCatalog(ActionFitPackageCatalogSettings_SO settings, ActionFitPackageGitHubProfile github, ActionFitPackageInfo_SO info, ActionFitPackageManifest manifest)
    {
        if (string.IsNullOrWhiteSpace(settings.WebAppUrl) ||
            string.IsNullOrWhiteSpace(settings.FetchToken) ||
            string.IsNullOrWhiteSpace(settings.SpreadSheetUrl))
            throw new InvalidOperationException("Spreadsheet URL, Web App URL, and Fetch Token are required for catalog append.");

        string ssId = ExtractSpreadsheetId(settings.SpreadSheetUrl);
        string repoUrl = $"https://github.com/{github.Organization}/{info.RepoName}.git";
        string dependencies = !string.IsNullOrWhiteSpace(info.DependenciesOverride) ? info.DependenciesOverride : manifest.Dependencies;
        string body =
            "{" +
            $"\"token\":\"{EscapeJson(settings.FetchToken)}\"," +
            $"\"action\":\"{CatalogAppendAction}\"," +
            $"\"ssId\":\"{EscapeJson(ssId)}\"," +
            "\"package\":{" +
            $"\"package_id\":\"{EscapeJson(manifest.Name)}\"," +
            $"\"display_name\":\"{EscapeJson(info.DisplayName)}\"," +
            $"\"repo_url\":\"{EscapeJson(repoUrl)}\"," +
            $"\"owner\":\"{EscapeJson(info.Owner)}\"," +
            $"\"status\":\"{EscapeJson(info.Status)}\"," +
            $"\"description\":\"{EscapeJson(info.Description)}\"" +
            "}," +
            "\"version\":{" +
            $"\"catalog_id\":\"{EscapeJson(manifest.Name + "@" + manifest.Version)}\"," +
            $"\"version\":\"{EscapeJson(manifest.Version)}\"," +
            $"\"unity_min\":\"{EscapeJson(manifest.Unity)}\"," +
            $"\"changelog\":\"{EscapeJson(info.ReleaseNote)}\"," +
            $"\"dependencies\":\"{EscapeJson(dependencies)}\"" +
            "}" +
            "}";

        string url = $"{settings.WebAppUrl}?token={Uri.EscapeDataString(settings.FetchToken)}&ssId={Uri.EscapeDataString(ssId)}";
        var request = (HttpWebRequest)WebRequest.Create(url);
        request.Method = "POST";
        request.ContentType = "application/json; charset=utf-8";
        WriteBody(request, body);

        using var response = (HttpWebResponse)request.GetResponse();
        using var reader = new StreamReader(response.GetResponseStream(), Encoding.UTF8);
        string text = reader.ReadToEnd();
        if ((int)response.StatusCode < 200 || (int)response.StatusCode >= 300)
            throw new InvalidOperationException($"Catalog append failed: {text}");

        var result = JsonUtility.FromJson<CatalogAppendResponse>(text);
        string expectedCatalogId = manifest.Name + "@" + manifest.Version;
        if (result == null || !result.success ||
            !string.Equals(result.package_id, manifest.Name, StringComparison.Ordinal) ||
            !string.Equals(result.version, manifest.Version, StringComparison.Ordinal) ||
            !string.Equals(result.catalog_id, expectedCatalogId, StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                "Catalog append did not return package catalog confirmation. " +
                "Update the Apps Script Web App deployment and try again.\n" + text);
        }
    }

    private static bool RunGit(string workingDirectory, string arguments, string token, bool throwOnFailure = true)
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
        string output = process.StandardOutput.ReadToEnd();
        string error = process.StandardError.ReadToEnd();
        process.WaitForExit();

        if (process.ExitCode == 0) return true;
        if (!throwOnFailure) return false;

        string sanitizedArguments = Sanitize(arguments, token);
        string sanitizedOutput = Sanitize(output + "\n" + error, token);
        throw new InvalidOperationException($"git {sanitizedArguments} failed:\n{sanitizedOutput}");
    }

    private static string BuildTokenRemote(ActionFitPackageGitHubProfile github, string repoName)
        => $"https://x-access-token:{github.Token}@github.com/{github.Organization}/{repoName}.git";

    private static void WriteBody(HttpWebRequest request, string body)
    {
        byte[] bytes = Encoding.UTF8.GetBytes(body);
        request.ContentLength = bytes.Length;
        using var stream = request.GetRequestStream();
        stream.Write(bytes, 0, bytes.Length);
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

    private static string ExpandPath(string path)
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

    [Serializable]
    private sealed class CatalogAppendResponse
    {
        public bool success;
        public string package_id;
        public string version;
        public string catalog_id;
    }
}
#endif
