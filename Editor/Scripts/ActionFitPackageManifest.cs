#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.IO;
using System.Text.RegularExpressions;

public sealed class ActionFitPackageManifest
{
    public string Name;
    public string Version;
    public string DisplayName;
    public string Description;
    public string Unity;
    public string Dependencies;

    public static ActionFitPackageManifest Read(string packageJsonPath)
    {
        string json = File.ReadAllText(Path.GetFullPath(packageJsonPath));
        return new ActionFitPackageManifest
        {
            Name = ExtractJsonString(json, "name"),
            Version = ExtractJsonString(json, "version"),
            DisplayName = ExtractJsonString(json, "displayName"),
            Description = ExtractJsonString(json, "description"),
            Unity = ExtractJsonString(json, "unity"),
            Dependencies = ExtractDependencies(json),
        };
    }

    private static string ExtractJsonString(string json, string key)
    {
        var match = Regex.Match(json, $"\"{Regex.Escape(key)}\"\\s*:\\s*\"([^\"]*)\"");
        return match.Success ? match.Groups[1].Value : "";
    }

    private static string ExtractDependencies(string json)
    {
        int key = json.IndexOf("\"dependencies\"", StringComparison.Ordinal);
        if (key < 0) return "";

        int open = json.IndexOf('{', key);
        if (open < 0) return "";

        int close = FindMatchingBrace(json, open);
        if (close < 0) return "";

        string body = json.Substring(open + 1, close - open - 1);
        var deps = new List<string>();
        foreach (Match match in Regex.Matches(body, "\"([^\"]+)\"\\s*:\\s*\"([^\"]+)\""))
        {
            string id = match.Groups[1].Value;
            string version = match.Groups[2].Value;
            deps.Add($"{id}@{version}");
        }

        return string.Join(";", deps);
    }

    private static int FindMatchingBrace(string text, int openBrace)
    {
        int depth = 0;
        for (int i = openBrace; i < text.Length; i++)
        {
            if (text[i] == '{') depth++;
            else if (text[i] == '}')
            {
                depth--;
                if (depth == 0) return i;
            }
        }

        return -1;
    }
}
#endif
