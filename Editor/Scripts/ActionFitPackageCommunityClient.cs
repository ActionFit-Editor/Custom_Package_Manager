#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Text;
using System.Text.RegularExpressions;
using UnityEngine;

public static class ActionFitPackageCommunityClient
{
    public const string VoteLike = "like";
    public const string VoteDislike = "dislike";

    private const string VoteAction = "votePackage";
    private const string UpsertCommentAction = "upsertPackageComment";

    private static readonly string StateFolder = Path.Combine("UserSettings", "ActionFitPackageManager");
    private static readonly string VoteIdPath = Path.Combine(StateFolder, "community_id.txt");
    private static readonly string LocalVotesPath = Path.Combine(StateFolder, "package_votes.tsv");
    private static readonly string CommentCachePath = Path.Combine(StateFolder, "package_comments.csv");
    private static Dictionary<string, string> _localVotes;

    public static bool HasCommentCache => File.Exists(ProjectRelativeFullPath(CommentCachePath));

    public static bool SaveCommentCache(string csv)
    {
        string path = ProjectRelativeFullPath(CommentCachePath);
        string normalized = Normalize(csv);
        if (string.IsNullOrWhiteSpace(normalized))
        {
            if (!File.Exists(path)) return false;
            File.Delete(path);
            return true;
        }

        string previous = File.Exists(path) ? File.ReadAllText(path) : "";
        if (Normalize(previous) == normalized) return false;

        Directory.CreateDirectory(Path.GetDirectoryName(path));
        File.WriteAllText(path, normalized + "\n", new UTF8Encoding(false));
        return true;
    }

    public static Dictionary<string, CommentFetchResult> LoadCachedCommentsByPackage()
    {
        var result = new Dictionary<string, CommentFetchResult>(StringComparer.Ordinal);
        string path = ProjectRelativeFullPath(CommentCachePath);
        if (!File.Exists(path)) return result;

        string localVoteId = GetOrCreateVoteId();
        foreach (var row in ReadCsv(File.ReadAllText(path)))
        {
            if (IsTrue(Get(row, "hidden"))) continue;

            string packageId = GetAny(row, "package_id", "packageId");
            if (string.IsNullOrWhiteSpace(packageId)) continue;

            if (!result.TryGetValue(packageId, out var packageComments))
            {
                packageComments = new CommentFetchResult();
                result[packageId] = packageComments;
            }

            string voteId = GetAny(row, "vote_id", "voteId");
            packageComments.Comments.Add(new Comment
            {
                comment_id = FirstNonEmpty(GetAny(row, "comment_id", "commentId"), $"{packageId}:{voteId}"),
                package_id = packageId,
                title = Get(row, "title"),
                body = Get(row, "body"),
                created_at = GetAny(row, "created_at", "createdAt"),
                updated_at = GetAny(row, "updated_at", "updatedAt"),
                is_mine = string.Equals(voteId, localVoteId, StringComparison.Ordinal)
            });
        }

        foreach (var pair in result)
        {
            pair.Value.Comments = pair.Value.Comments
                .OrderByDescending(c => FirstNonEmpty(c.updated_at, c.created_at))
                .ToList();
            pair.Value.MyComment = pair.Value.Comments.Find(c => c.is_mine);
            pair.Value.Summary = new Summary(0, 0, pair.Value.Comments.Count);
        }

        return result;
    }

    public static string GetLocalVote(string packageId)
    {
        EnsureLocalVotesLoaded();
        return _localVotes.TryGetValue(packageId, out string vote) ? vote : "";
    }

    public static bool SubmitVote(ActionFitPackageCatalogSettings_SO settings, string packageId, string vote, out Summary summary, out string message)
    {
        summary = Summary.Empty;
        message = "";

        if (!ValidateSettings(settings, out message)) return false;
        if (!IsSupportedVote(vote))
        {
            message = "Vote must be like or dislike.";
            return false;
        }

        string localVote = GetLocalVote(packageId);
        if (!string.IsNullOrWhiteSpace(localVote))
        {
            message = $"This project already used its vote for this package: {localVote}.";
            return false;
        }

        string body =
            "{" +
            $"\"token\":\"{EscapeJson(settings.FetchToken)}\"," +
            $"\"action\":\"{VoteAction}\"," +
            $"\"ssId\":\"{EscapeJson(ExtractSpreadsheetId(settings.SpreadSheetUrl))}\"," +
            $"\"package_id\":\"{EscapeJson(packageId)}\"," +
            $"\"vote_id\":\"{EscapeJson(GetOrCreateVoteId())}\"," +
            $"\"vote\":\"{EscapeJson(vote)}\"" +
            "}";

        if (!TryPost(settings, body, out string responseText, out message)) return false;

        var response = JsonUtility.FromJson<VoteResponse>(responseText);
        if (response == null || !response.success)
        {
            message = $"Vote failed. Update the Apps Script Web App deployment and try again.\n{responseText}";
            return false;
        }

        string responseVote = string.IsNullOrWhiteSpace(response.my_vote) ? vote : response.my_vote;
        if (IsSupportedVote(responseVote))
            SaveLocalVote(packageId, responseVote);

        summary = new Summary(response.likes, response.dislikes, response.comment_count);
        message = $"Vote saved: {packageId}";
        return true;
    }

    public static bool UpsertComment(ActionFitPackageCatalogSettings_SO settings, string packageId, string title, string bodyText, out Summary summary, out string message)
    {
        summary = Summary.Empty;
        message = "";

        if (!ValidateSettings(settings, out message)) return false;

        title = (title ?? "").Trim();
        bodyText = (bodyText ?? "").Trim();
        if (string.IsNullOrWhiteSpace(title))
        {
            message = "Comment title is required.";
            return false;
        }

        if (string.IsNullOrWhiteSpace(bodyText))
        {
            message = "Comment description is required.";
            return false;
        }

        if (title.Length > 80)
        {
            message = "Comment title must be 80 characters or shorter.";
            return false;
        }

        if (bodyText.Length > 2000)
        {
            message = "Comment description must be 2000 characters or shorter.";
            return false;
        }

        string body =
            "{" +
            $"\"token\":\"{EscapeJson(settings.FetchToken)}\"," +
            $"\"action\":\"{UpsertCommentAction}\"," +
            $"\"ssId\":\"{EscapeJson(ExtractSpreadsheetId(settings.SpreadSheetUrl))}\"," +
            $"\"package_id\":\"{EscapeJson(packageId)}\"," +
            $"\"vote_id\":\"{EscapeJson(GetOrCreateVoteId())}\"," +
            $"\"title\":\"{EscapeJson(title)}\"," +
            $"\"body\":\"{EscapeJson(bodyText)}\"" +
            "}";

        if (!TryPost(settings, body, out string responseText, out message)) return false;

        var response = JsonUtility.FromJson<CommentUpsertResponse>(responseText);
        if (response == null || !response.success)
        {
            message = $"Comment save failed. Update the Apps Script Web App deployment and try again.\n{responseText}";
            return false;
        }

        summary = new Summary(response.likes, response.dislikes, response.comment_count);
        message = $"Comment saved: {packageId}";
        return true;
    }

    private static bool ValidateSettings(ActionFitPackageCatalogSettings_SO settings, out string message)
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

        if (string.IsNullOrWhiteSpace(ExtractSpreadsheetId(settings.SpreadSheetUrl)))
        {
            message = "Spreadsheet URL is invalid.";
            return false;
        }

        return true;
    }

    private static bool TryPost(ActionFitPackageCatalogSettings_SO settings, string body, out string responseText, out string message)
    {
        responseText = "";
        message = "";

        string ssId = ExtractSpreadsheetId(settings.SpreadSheetUrl);
        string url = $"{settings.WebAppUrl}?token={Uri.EscapeDataString(settings.FetchToken)}&ssId={Uri.EscapeDataString(ssId)}";
        try
        {
            var request = (HttpWebRequest)WebRequest.Create(url);
            request.Method = "POST";
            request.ContentType = "application/json; charset=utf-8";
            request.AllowAutoRedirect = true;

            byte[] bytes = Encoding.UTF8.GetBytes(body);
            request.ContentLength = bytes.Length;
            using (var stream = request.GetRequestStream())
                stream.Write(bytes, 0, bytes.Length);

            using var response = (HttpWebResponse)request.GetResponse();
            using var reader = new StreamReader(response.GetResponseStream(), Encoding.UTF8);
            responseText = reader.ReadToEnd();
            if ((int)response.StatusCode >= 200 && (int)response.StatusCode < 300)
                return true;

            message = $"Request failed: {(int)response.StatusCode} {response.StatusCode}\n{responseText}";
            return false;
        }
        catch (WebException ex)
        {
            string errorText = ReadErrorResponse(ex);
            message = string.IsNullOrWhiteSpace(errorText)
                ? $"Request failed: {ex.Message}"
                : $"Request failed: {ex.Message}\n{errorText}";
            return false;
        }
        catch (Exception ex)
        {
            message = $"Request failed: {ex.Message}";
            return false;
        }
    }

    private static string ReadErrorResponse(WebException ex)
    {
        if (ex.Response == null) return "";
        try
        {
            using var stream = ex.Response.GetResponseStream();
            if (stream == null) return "";
            using var reader = new StreamReader(stream, Encoding.UTF8);
            return reader.ReadToEnd();
        }
        catch
        {
            return "";
        }
    }

    private static bool IsSupportedVote(string vote)
    {
        return string.Equals(vote, VoteLike, StringComparison.OrdinalIgnoreCase) ||
               string.Equals(vote, VoteDislike, StringComparison.OrdinalIgnoreCase);
    }

    private static string GetOrCreateVoteId()
    {
        string path = ProjectRelativeFullPath(VoteIdPath);
        if (File.Exists(path))
        {
            string existing = File.ReadAllText(path).Trim();
            if (!string.IsNullOrWhiteSpace(existing)) return existing;
        }

        Directory.CreateDirectory(Path.GetDirectoryName(path));
        string voteId = Guid.NewGuid().ToString("N");
        File.WriteAllText(path, voteId + "\n", new UTF8Encoding(false));
        return voteId;
    }

    private static void EnsureLocalVotesLoaded()
    {
        if (_localVotes != null) return;

        _localVotes = new Dictionary<string, string>(StringComparer.Ordinal);
        string path = ProjectRelativeFullPath(LocalVotesPath);
        if (!File.Exists(path)) return;

        foreach (string line in File.ReadAllLines(path))
        {
            if (string.IsNullOrWhiteSpace(line)) continue;
            string[] parts = line.Split('\t');
            if (parts.Length < 2 || !IsSupportedVote(parts[1])) continue;
            _localVotes[parts[0]] = parts[1];
        }
    }

    private static void SaveLocalVote(string packageId, string vote)
    {
        EnsureLocalVotesLoaded();
        _localVotes[packageId] = vote;

        string path = ProjectRelativeFullPath(LocalVotesPath);
        Directory.CreateDirectory(Path.GetDirectoryName(path));
        var sb = new StringBuilder();
        foreach (var pair in _localVotes)
            sb.Append(pair.Key).Append('\t').Append(pair.Value).Append('\n');
        File.WriteAllText(path, sb.ToString(), new UTF8Encoding(false));
    }

    private static List<Dictionary<string, string>> ReadCsv(string csv)
    {
        var lines = ReadCsvRecords(Normalize(csv));
        var result = new List<Dictionary<string, string>>();
        if (lines.Count == 0 || string.IsNullOrWhiteSpace(lines[0])) return result;

        string[] headers = SplitCsvLine(lines[0]).Select(h => h.Trim()).ToArray();
        for (int i = 1; i < lines.Count; i++)
        {
            if (string.IsNullOrWhiteSpace(lines[i])) continue;
            if (lines[i].Contains("(string)", StringComparison.OrdinalIgnoreCase)) continue;

            string[] values = SplitCsvLine(lines[i]);
            var row = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            for (int j = 0; j < headers.Length && j < values.Length; j++)
                row[headers[j]] = values[j].Trim();
            result.Add(row);
        }

        return result;
    }

    private static List<string> ReadCsvRecords(string text)
    {
        var records = new List<string>();
        var sb = new StringBuilder();
        bool inQuotes = false;

        for (int i = 0; i < text.Length; i++)
        {
            char c = text[i];
            if (c == '"')
            {
                if (inQuotes && i + 1 < text.Length && text[i + 1] == '"')
                {
                    sb.Append(c);
                    sb.Append(text[i + 1]);
                    i++;
                    continue;
                }

                inQuotes = !inQuotes;
            }

            if ((c == '\n' || c == '\r') && !inQuotes)
            {
                if (c == '\r' && i + 1 < text.Length && text[i + 1] == '\n')
                    i++;

                records.Add(sb.ToString());
                sb.Clear();
                continue;
            }

            sb.Append(c);
        }

        if (sb.Length > 0)
            records.Add(sb.ToString());

        return records;
    }

    private static string[] SplitCsvLine(string line)
    {
        var result = new List<string>();
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

    private static string Get(Dictionary<string, string> row, string key)
    {
        return row != null && row.TryGetValue(key, out string value) ? value : "";
    }

    private static string GetAny(Dictionary<string, string> row, params string[] keys)
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

    private static bool IsTrue(string value)
    {
        return string.Equals(value, "true", StringComparison.OrdinalIgnoreCase) ||
               string.Equals(value, "1", StringComparison.OrdinalIgnoreCase) ||
               string.Equals(value, "yes", StringComparison.OrdinalIgnoreCase);
    }

    private static string Normalize(string text)
    {
        return (text ?? "").TrimStart('\uFEFF').Replace("\r\n", "\n").Replace("\r", "\n").TrimEnd('\n');
    }

    private static string ProjectRelativeFullPath(string relativePath)
    {
        string root = Path.GetFullPath(Path.Combine(Application.dataPath, ".."));
        return Path.Combine(root, relativePath.Replace("/", Path.DirectorySeparatorChar.ToString()));
    }

    private static string ExtractSpreadsheetId(string input)
    {
        if (string.IsNullOrWhiteSpace(input)) return "";
        string s = input.Trim();
        var match = Regex.Match(s, @"/spreadsheets/d/([a-zA-Z0-9-_]+)");
        return match.Success ? match.Groups[1].Value : s;
    }

    private static string EscapeJson(string value)
    {
        return (value ?? "")
            .Replace("\\", "\\\\")
            .Replace("\"", "\\\"")
            .Replace("\n", "\\n")
            .Replace("\r", "\\r")
            .Replace("\t", "\\t");
    }

    [Serializable]
    private sealed class VoteResponse
    {
        public bool success;
        public string package_id;
        public string my_vote;
        public int likes;
        public int dislikes;
        public int comment_count;
    }

    [Serializable]
    private sealed class CommentUpsertResponse
    {
        public bool success;
        public string package_id;
        public int likes;
        public int dislikes;
        public int comment_count;
    }

    [Serializable]
    public sealed class Comment
    {
        public string comment_id;
        public string package_id;
        public string title;
        public string body;
        public string created_at;
        public string updated_at;
        public bool is_mine;
    }

    public sealed class CommentFetchResult
    {
        public Summary Summary = Summary.Empty;
        public List<Comment> Comments = new();
        public Comment MyComment;
    }

    public readonly struct Summary
    {
        public static readonly Summary Empty = new(0, 0, 0);

        public Summary(int likes, int dislikes, int commentCount)
        {
            Likes = Math.Max(0, likes);
            Dislikes = Math.Max(0, dislikes);
            CommentCount = Math.Max(0, commentCount);
        }

        public int Likes { get; }
        public int Dislikes { get; }
        public int Score => Likes - Dislikes;
        public int CommentCount { get; }
    }
}
#endif
