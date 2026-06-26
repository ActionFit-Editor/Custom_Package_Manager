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
    private const string CatalogSheetName = "actionfit_package_catalog";

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
        string fullPath = Path.GetFullPath(path);
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

            var sheet = response.sheets.FirstOrDefault(s =>
                string.Equals(s.name, CatalogSheetName, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(s.name, CatalogSheetName + ".csv", StringComparison.OrdinalIgnoreCase));

            if (sheet == null)
            {
                message = $"Catalog sheet not found. Expected '{CatalogSheetName}'.";
                return false;
            }

            csv = StripBom(sheet.csv ?? "");
            return true;
        }
        catch (Exception ex)
        {
            message = $"JSON parse failed: {ex.Message}";
            return false;
        }
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
