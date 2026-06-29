#if UNITY_EDITOR
using System;
using System.IO;
using System.Linq;
using System.Net;
using System.Text;
using System.Text.RegularExpressions;
using UnityEditor;
using UnityEngine;

public static class ActionFitPackageCatalogUpdater
{
    private const string PackageSheetName = "package_catalog";
    private const string VersionSheetName = "package_versions";
    private const string VoteSummarySheetName = "package_vote_summary";

    private static string ProjectRootPath => Path.GetFullPath(Path.Combine(Application.dataPath, ".."));

    private static string ProjectRelativeFullPath(string relativePath)
    {
        return Path.Combine(ProjectRootPath, relativePath.Replace("/", Path.DirectorySeparatorChar.ToString()));
    }

    public static bool UpdateCatalog(ActionFitPackageCatalogSettings_SO settings, out string message)
    {
        message = "";
        if (settings == null)
        {
            message = "Catalog settings asset is missing.";
            return false;
        }

        if (string.IsNullOrWhiteSpace(settings.SpreadSheetUrl) ||
            string.IsNullOrWhiteSpace(settings.WebAppUrl) ||
            string.IsNullOrWhiteSpace(settings.FetchToken))
        {
            message = "Spreadsheet URL, Web App URL, and Token must all be set.";
            return false;
        }

        string ssId = ExtractSpreadsheetId(settings.SpreadSheetUrl);
        if (string.IsNullOrWhiteSpace(ssId))
        {
            message = "Spreadsheet URL is invalid.";
            return false;
        }

        string json;
        try
        {
            EditorUtility.DisplayProgressBar("ActionFit Package Catalog", "Downloading catalog from spreadsheet...", 0.4f);
            json = HttpGet($"{settings.WebAppUrl}?token={Uri.EscapeDataString(settings.FetchToken)}&ssId={Uri.EscapeDataString(ssId)}");
        }
        catch (Exception ex)
        {
            message = $"Request failed: {ex.Message}";
            return false;
        }
        finally
        {
            EditorUtility.ClearProgressBar();
        }

        if (!TryFindCatalogCsv(json, out string csv, out message)) return false;

        ActionFitPackageCatalogSettingsProvider.EnsureLocalCatalogFolder();
        string path = ActionFitPackageCatalogSettingsProvider.LocalCatalogPath;
        string fullPath = ProjectRelativeFullPath(path);
        string previous = File.Exists(fullPath) ? File.ReadAllText(fullPath) : "";

        if (Normalize(previous) == Normalize(csv))
        {
            message = $"Catalog is already up to date: {path}";
            return true;
        }

        File.WriteAllText(fullPath, csv, new UTF8Encoding(true));
        AssetDatabase.ImportAsset(path, ImportAssetOptions.ForceUpdate);
        AssetDatabase.SaveAssets();
        message = $"Catalog updated: {path}";
        return true;
    }

    private static bool TryFindCatalogCsv(string json, out string csv, out string message)
    {
        csv = "";
        message = "";

        if (string.IsNullOrWhiteSpace(json))
        {
            message = "Empty response.";
            return false;
        }

        string trimmed = json.TrimStart();
        if (trimmed.StartsWith("UNAUTHORIZED", StringComparison.OrdinalIgnoreCase))
        {
            message = "Unauthorized. Check token.";
            return false;
        }

        if (trimmed.StartsWith("<", StringComparison.Ordinal))
        {
            message = "Got HTML, not JSON. Check Web App URL and access permission.";
            return false;
        }

        try
        {
            var response = JsonUtility.FromJson<FetchResponse>(json);
            if (response?.sheets == null || response.sheets.Length == 0)
            {
                message = "No sheets in response.";
                return false;
            }

            var packageSheet = response.sheets.FirstOrDefault(s =>
                string.Equals(s.name, PackageSheetName, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(s.name, PackageSheetName + ".csv", StringComparison.OrdinalIgnoreCase));
            var versionSheet = response.sheets.FirstOrDefault(s =>
                string.Equals(s.name, VersionSheetName, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(s.name, VersionSheetName + ".csv", StringComparison.OrdinalIgnoreCase));
            var voteSummarySheet = response.sheets.FirstOrDefault(s =>
                string.Equals(s.name, VoteSummarySheetName, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(s.name, VoteSummarySheetName + ".csv", StringComparison.OrdinalIgnoreCase));

            if (packageSheet == null || versionSheet == null)
            {
                message = $"Catalog sheets not found. Expected '{PackageSheetName}' and '{VersionSheetName}'.";
                return false;
            }

            csv = BuildCatalogCsv(packageSheet.csv, versionSheet.csv, voteSummarySheet?.csv);
            return true;
        }
        catch (Exception ex)
        {
            message = $"JSON parse failed: {ex.Message}";
            return false;
        }
    }

    private static string BuildCatalogCsv(string packageCsv, string versionCsv, string voteSummaryCsv)
    {
        var packages = ReadCsv(packageCsv)
            .Where(r => !string.IsNullOrWhiteSpace(GetAny(r, "package_id", "packageId")))
            .GroupBy(r => GetAny(r, "package_id", "packageId"), StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);

        var voteSummaries = ReadCsv(voteSummaryCsv)
            .Where(r => !string.IsNullOrWhiteSpace(GetAny(r, "package_id", "packageId")))
            .GroupBy(r => GetAny(r, "package_id", "packageId"), StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);

        var rows = new StringBuilder();
        rows.AppendLine("catalog_id,package_id,display_name,owner,repo_url,version,status,is_latest,unity_min,description,changelog,dependencies,likes,dislikes,vote_score,comment_count");
        rows.AppendLine("catalogId(string)[key],packageId(string),displayName(string),owner(string),repoUrl(string),version(string),status(string),isLatest(bool)[nondata],unityMin(string),description(string),changelog(string),dependencies(string),likes(int),dislikes(int),voteScore(int),commentCount(int)");

        foreach (var version in ReadCsv(versionCsv))
        {
            string packageId = GetAny(version, "package_id", "packageId");
            string packageVersion = Get(version, "version");
            if (string.IsNullOrWhiteSpace(packageId) || string.IsNullOrWhiteSpace(packageVersion)) continue;

            packages.TryGetValue(packageId, out var package);
            voteSummaries.TryGetValue(packageId, out var voteSummary);
            string catalogId = FirstNonEmpty(GetAny(version, "catalog_id", "catalogId"), $"{packageId}@{packageVersion}");
            string displayName = FirstNonEmpty(GetAny(package, "display_name", "displayName"), GetAny(version, "display_name", "displayName"), packageId);
            string owner = FirstNonEmpty(Get(package, "owner"), Get(version, "owner"), "ActionFit");
            string repoUrl = FirstNonEmpty(GetAny(package, "repo_url", "repoUrl"), GetAny(version, "repo_url", "repoUrl"));
            string status = FirstNonEmpty(Get(version, "status"), Get(package, "status"), "verified");
            string isLatest = FirstNonEmpty(Get(version, "is_latest"), Get(version, "isLatest"), "false");
            string unityMin = FirstNonEmpty(Get(version, "unity_min"), Get(version, "unityMin"), Get(package, "unity_min"), Get(package, "unityMin"));
            string description = FirstNonEmpty(Get(package, "description"), Get(version, "description"));
            string changelog = FirstNonEmpty(Get(version, "changelog"), Get(version, "release_note"), Get(version, "releaseNote"));
            string dependencies = FirstNonEmpty(Get(version, "dependencies"), Get(package, "dependencies"));
            string likes = FirstNonEmpty(GetAny(voteSummary, "likes", "like_count", "likeCount"), GetAny(package, "likes", "like_count", "likeCount"), GetAny(version, "likes", "like_count", "likeCount"), "0");
            string dislikes = FirstNonEmpty(GetAny(voteSummary, "dislikes", "dislike_count", "dislikeCount"), GetAny(package, "dislikes", "dislike_count", "dislikeCount"), GetAny(version, "dislikes", "dislike_count", "dislikeCount"), "0");
            string voteScore = FirstNonEmpty(GetAny(voteSummary, "vote_score", "voteScore", "score"), GetAny(package, "vote_score", "voteScore", "score"), GetAny(version, "vote_score", "voteScore", "score"));
            string commentCount = FirstNonEmpty(GetAny(voteSummary, "comment_count", "commentCount", "comments"), GetAny(package, "comment_count", "commentCount", "comments"), GetAny(version, "comment_count", "commentCount", "comments"), "0");
            if (string.IsNullOrWhiteSpace(voteScore))
                voteScore = (ParseInt(likes) - ParseInt(dislikes)).ToString();

            rows.AppendLine(string.Join(",", new[]
            {
                Csv(catalogId),
                Csv(packageId),
                Csv(displayName),
                Csv(owner),
                Csv(repoUrl),
                Csv(packageVersion),
                Csv(status),
                Csv(isLatest),
                Csv(unityMin),
                Csv(description),
                Csv(changelog),
                Csv(dependencies),
                Csv(likes),
                Csv(dislikes),
                Csv(voteScore),
                Csv(commentCount)
            }));
        }

        return rows.ToString();
    }

    private static System.Collections.Generic.List<System.Collections.Generic.Dictionary<string, string>> ReadCsv(string csv)
    {
        string[] lines = StripBom(csv ?? "").Replace("\r\n", "\n").Replace("\r", "\n").Split('\n');
        var result = new System.Collections.Generic.List<System.Collections.Generic.Dictionary<string, string>>();
        if (lines.Length == 0 || string.IsNullOrWhiteSpace(lines[0])) return result;

        string[] headers = SplitCsvLine(lines[0]).Select(h => h.Trim()).ToArray();
        for (int i = 1; i < lines.Length; i++)
        {
            if (string.IsNullOrWhiteSpace(lines[i])) continue;
            if (lines[i].Contains("(string)", StringComparison.OrdinalIgnoreCase)) continue;

            string[] values = SplitCsvLine(lines[i]);
            var row = new System.Collections.Generic.Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            for (int j = 0; j < headers.Length && j < values.Length; j++)
                row[headers[j]] = values[j].Trim();
            result.Add(row);
        }

        return result;
    }

    private static string[] SplitCsvLine(string line)
    {
        var result = new System.Collections.Generic.List<string>();
        var sb = new StringBuilder();
        bool inQuotes = false;

        for (int i = 0; i < line.Length; i++)
        {
            char c = line[i];
            if (c == '"')
            {
                if (inQuotes && i + 1 < line.Length && line[i + 1] == '"')
                {
                    sb.Append('"');
                    i++;
                    continue;
                }

                inQuotes = !inQuotes;
                continue;
            }

            if (c == ',' && !inQuotes)
            {
                result.Add(sb.ToString());
                sb.Clear();
                continue;
            }

            sb.Append(c);
        }

        result.Add(sb.ToString());
        return result.ToArray();
    }

    private static string Get(System.Collections.Generic.Dictionary<string, string> row, string key)
    {
        return row != null && row.TryGetValue(key, out string value) ? value : "";
    }

    private static string GetAny(System.Collections.Generic.Dictionary<string, string> row, params string[] keys)
    {
        foreach (string key in keys)
        {
            string value = Get(row, key);
            if (!string.IsNullOrWhiteSpace(value)) return value;
        }

        return "";
    }

    private static string FirstNonEmpty(params string[] values)
    {
        foreach (string value in values)
            if (!string.IsNullOrWhiteSpace(value))
                return value.Trim();
        return "";
    }

    private static int ParseInt(string value)
    {
        return int.TryParse((value ?? "").Trim(), out int result) ? result : 0;
    }

    private static string Csv(string value)
    {
        value ??= "";
        bool quote = value.Contains(',') || value.Contains('"') || value.Contains('\n') || value.Contains('\r');
        value = value.Replace("\"", "\"\"");
        return quote ? $"\"{value}\"" : value;
    }

    private static string HttpGet(string url)
    {
        var req = (HttpWebRequest)WebRequest.Create(url);
        req.Method = "GET";
        req.AllowAutoRedirect = true;
        using var resp = (HttpWebResponse)req.GetResponse();
        using var sr = new StreamReader(resp.GetResponseStream(), Encoding.UTF8);
        return sr.ReadToEnd();
    }

    private static string ExtractSpreadsheetId(string input)
    {
        if (string.IsNullOrWhiteSpace(input)) return "";
        string s = input.Trim();
        var match = Regex.Match(s, @"/spreadsheets/d/([a-zA-Z0-9-_]+)");
        return match.Success ? match.Groups[1].Value : s;
    }

    private static string Normalize(string text)
    {
        return StripBom(text ?? "").Replace("\r\n", "\n").Replace("\r", "\n").TrimEnd('\n');
    }

    private static string StripBom(string text)
    {
        return string.IsNullOrEmpty(text) ? "" : text.TrimStart('\uFEFF');
    }

    [Serializable] private class FetchResponse { public SheetEntry[] sheets; }
    [Serializable] private class SheetEntry { public string name; public string csv; }
}
#endif
