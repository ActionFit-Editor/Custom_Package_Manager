#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

internal static class ActionFitPackageCatalogRecoveryVerifier
{
    private const string PackageInfoRelativePath = "Editor/PackageInfo/ActionFitPackageInfo_SO.asset";

    public static bool TryVerify(
        ActionFitPackagePublisher.PublishRequest request,
        out bool matches,
        out string tagCommit,
        out string message)
    {
        matches = false;
        tagCommit = "";
        message = "";
        if (request == null)
        {
            message = "Catalog recovery publish request is missing.";
            return false;
        }

        string operationRoot = Path.Combine(
            ActionFitPackagePaths.TempRoot,
            "CatalogRecovery",
            Guid.NewGuid().ToString("N"));
        string tagRoot = Path.Combine(operationRoot, "tagged-package");

        try
        {
            Directory.CreateDirectory(operationRoot);
            string remote = ActionFitPackagePublisher.BuildTokenRemote(
                request.GitHubOrganization,
                request.RepoName,
                request.GitHubToken);
            ActionFitPackagePublisher.RunGit(
                operationRoot,
                $"clone --quiet --depth 1 --single-branch --branch {Quote(request.Version)} {Quote(remote)} {Quote(tagRoot)}",
                request.GitHubToken);
            ActionFitPackagePublisher.RunGitCapture(
                tagRoot,
                "rev-parse HEAD",
                request.GitHubToken,
                out tagCommit,
                out _);
            tagCommit = (tagCommit ?? "").Trim();

            string taggedManifestPath = Path.Combine(tagRoot, "package.json");
            if (!File.Exists(taggedManifestPath))
            {
                message = $"Remote tag {request.Version} does not contain package.json.";
                return true;
            }

            ActionFitPackageManifest taggedManifest = ActionFitPackageManifest.Read(taggedManifestPath);
            if (!string.Equals(taggedManifest.Name, request.PackageId, StringComparison.Ordinal) ||
                !string.Equals(taggedManifest.Version, request.Version, StringComparison.Ordinal))
            {
                message =
                    $"Remote tag package identity mismatch. Expected {request.PackageId}@{request.Version}, " +
                    $"found {taggedManifest.Name}@{taggedManifest.Version}.";
                return true;
            }

            matches = AreEquivalent(request.PackageRoot, tagRoot, out message);
            return true;
        }
        catch (Exception ex)
        {
            message = $"Remote tag content verification failed: {ex.Message}";
            return false;
        }
        finally
        {
            ActionFitPackageFileUtility.TryDeleteDirectory(operationRoot, ActionFitPackagePaths.TempRoot);
        }
    }

    internal static bool AreEquivalent(string localRoot, string tagRoot, out string difference)
    {
        difference = "";
        if (!Directory.Exists(localRoot) || !Directory.Exists(tagRoot))
        {
            difference = "Local package or remote tag checkout is missing.";
            return false;
        }

        Dictionary<string, string> localFiles = EnumerateFiles(localRoot);
        Dictionary<string, string> tagFiles = EnumerateFiles(tagRoot);
        string[] localOnly = localFiles.Keys.Except(tagFiles.Keys, StringComparer.Ordinal).OrderBy(path => path, StringComparer.Ordinal).ToArray();
        string[] tagOnly = tagFiles.Keys.Except(localFiles.Keys, StringComparer.Ordinal).OrderBy(path => path, StringComparer.Ordinal).ToArray();
        if (localOnly.Length > 0 || tagOnly.Length > 0)
        {
            difference =
                $"Package file set differs. Local-only: {FormatPaths(localOnly)}; tag-only: {FormatPaths(tagOnly)}.";
            return false;
        }

        foreach (string relativePath in localFiles.Keys.OrderBy(path => path, StringComparer.Ordinal))
        {
            string localPath = localFiles[relativePath];
            string tagPath = tagFiles[relativePath];
            bool equal;
            if (string.Equals(relativePath, "package.json", StringComparison.Ordinal))
            {
                equal = string.Equals(
                    NormalizePackageJson(File.ReadAllText(localPath)),
                    NormalizePackageJson(File.ReadAllText(tagPath)),
                    StringComparison.Ordinal);
            }
            else if (string.Equals(relativePath, PackageInfoRelativePath, StringComparison.Ordinal))
            {
                equal = string.Equals(
                    NormalizeUnityYaml(File.ReadAllText(localPath)),
                    NormalizeUnityYaml(File.ReadAllText(tagPath)),
                    StringComparison.Ordinal);
            }
            else
            {
                equal = File.ReadAllBytes(localPath).SequenceEqual(File.ReadAllBytes(tagPath));
            }

            if (equal) continue;
            difference = $"Package content differs from remote tag at {relativePath}.";
            return false;
        }

        difference = "Local package content matches the immutable remote tag after safe metadata normalization.";
        return true;
    }

    private static Dictionary<string, string> EnumerateFiles(string root)
    {
        string fullRoot = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        return Directory.GetFiles(fullRoot, "*", SearchOption.AllDirectories)
            .Select(path => new
            {
                FullPath = path,
                RelativePath = path[(fullRoot.Length + 1)..].Replace("\\", "/"),
            })
            .Where(item => !item.RelativePath.Equals(".git", StringComparison.OrdinalIgnoreCase) &&
                           !item.RelativePath.StartsWith(".git/", StringComparison.OrdinalIgnoreCase) &&
                           !item.RelativePath.Contains("/.git/", StringComparison.OrdinalIgnoreCase))
            .ToDictionary(item => item.RelativePath, item => item.FullPath, StringComparer.Ordinal);
    }

    private static string NormalizePackageJson(string json)
    {
        string withoutFingerprint = Regex.Replace(
            json ?? "",
            @"\s*,\s*""_fingerprint""\s*:\s*""[^""]*""",
            "",
            RegexOptions.CultureInvariant);
        withoutFingerprint = Regex.Replace(
            withoutFingerprint,
            @"""_fingerprint""\s*:\s*""[^""]*""\s*,\s*",
            "",
            RegexOptions.CultureInvariant);

        var normalized = new StringBuilder(withoutFingerprint.Length);
        bool inString = false;
        bool escaped = false;
        foreach (char c in withoutFingerprint)
        {
            if (inString)
            {
                normalized.Append(c);
                if (escaped) escaped = false;
                else if (c == '\\') escaped = true;
                else if (c == '"') inString = false;
                continue;
            }

            if (c == '"')
            {
                inString = true;
                normalized.Append(c);
            }
            else if (!char.IsWhiteSpace(c))
            {
                normalized.Append(c);
            }
        }
        return normalized.ToString();
    }

    private static string NormalizeUnityYaml(string yaml)
    {
        string[] lines = (yaml ?? "").Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');
        var values = new SortedDictionary<string, string>(StringComparer.Ordinal);
        for (int i = 0; i < lines.Length; i++)
        {
            string line = lines[i];
            if (!line.StartsWith("  ", StringComparison.Ordinal) || line.StartsWith("    ", StringComparison.Ordinal))
                continue;

            int colon = line.IndexOf(':', 2);
            if (colon <= 2) continue;
            string key = line.Substring(2, colon - 2);
            var scalarParts = new List<string> { line[(colon + 1)..].Trim() };
            while (i + 1 < lines.Length && lines[i + 1].StartsWith("    ", StringComparison.Ordinal))
            {
                scalarParts.Add(lines[++i].Trim());
            }
            values[key] = DecodeYamlScalar(string.Join(" ", scalarParts));
        }

        return string.Join("\n", values.Select(pair => pair.Key + "=" + pair.Value));
    }

    private static string DecodeYamlScalar(string scalar)
    {
        string value = (scalar ?? "").Trim();
        if (value.Length >= 2 && value[0] == '"' && value[^1] == '"')
        {
            value = value.Substring(1, value.Length - 2);
            try
            {
                value = Regex.Unescape(value);
            }
            catch (ArgumentException)
            {
                // Keep the original escaped value so malformed or unsupported escapes never compare as equal accidentally.
            }
        }
        return value;
    }

    private static string FormatPaths(IReadOnlyList<string> paths)
        => paths == null || paths.Count == 0 ? "none" : string.Join(", ", paths.Take(5)) + (paths.Count > 5 ? ", ..." : "");

    private static string Quote(string value) => "\"" + (value ?? "").Replace("\"", "\\\"") + "\"";
}
#endif
