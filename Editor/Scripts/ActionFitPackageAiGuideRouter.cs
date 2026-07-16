#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using UnityEditor;
using UnityEngine;

[InitializeOnLoad]
public static class ActionFitPackageAiGuideRouter
{
    private const string PackageGuideRouterPath = "Packages/com.actionfit.custompackagemanager/PACKAGE_AI_GUIDE_ROUTER.md";
    private const string PackageRoot = "Packages";
    private const string PackageCacheRoot = "Library/PackageCache";
    private const string GuideFileName = "AI_GUIDE.md";
    private const string StartMarker = "<!-- ACTIONFIT_PACKAGE_AI_GUIDES_START -->";
    private const string EndMarker = "<!-- ACTIONFIT_PACKAGE_AI_GUIDES_END -->";
    private const string EntriesHeading = "## Package Guide Entries";
    private const string GeneratedIndexHeading = "## Generated Project Index";

    static ActionFitPackageAiGuideRouter()
    {
        if (!Application.isBatchMode)
            EditorApplication.delayCall += EnsureProjectRouter;
    }

    public static void EnsureProjectRouter()
    {
        try
        {
            var guides = FindGuides();
            WritePackageGuideRouter(guides);

            foreach (var entryPoint in FindAiEntryPoints())
            {
                if (entryPoint.WritePackageIndex)
                    WritePackageIndex(entryPoint.FullPath, guides);

                EnsureProjectRouterLink(entryPoint.FullPath, guides.Count);
            }
        }
        catch (Exception ex)
        {
            Debug.LogWarning($"[ActionFitPackageManager] AI guide router refresh failed: {ex.Message}");
        }
    }

    private static List<PackageGuide> FindGuides()
    {
        var byPackageId = new Dictionary<string, PackageGuide>(StringComparer.OrdinalIgnoreCase);
        foreach (string dir in EnumerateInstalledPackageDirectories())
        {
            if (!TryReadGuide(dir, out var guide)) continue;

            // Prefer embedded package paths because they are editable and stable in the project tree.
            if (byPackageId.TryGetValue(guide.PackageId, out var existing) && existing.IsEmbedded)
                continue;

            byPackageId[guide.PackageId] = guide;
        }

        return byPackageId.Values.OrderBy(g => g.PackageId, StringComparer.OrdinalIgnoreCase).ToList();
    }

    private static IEnumerable<string> EnumerateInstalledPackageDirectories()
    {
        foreach (string dir in EnumeratePackageDirectories(PackageRoot, "com.actionfit.*"))
            yield return dir;

        foreach (string dir in EnumeratePackageDirectories(PackageCacheRoot, "com.actionfit.*@*"))
            yield return dir;
    }

    private static IEnumerable<string> EnumeratePackageDirectories(string relativeRoot, string pattern)
    {
        string fullRoot = ProjectRelativeFullPath(relativeRoot);
        if (!Directory.Exists(fullRoot)) yield break;

        foreach (string dir in Directory.GetDirectories(fullRoot, pattern, SearchOption.TopDirectoryOnly))
            yield return dir;
    }

    private static bool TryReadGuide(string dir, out PackageGuide guide)
    {
        guide = default;

        string guideFullPath = Path.Combine(dir, GuideFileName);
        if (!File.Exists(guideFullPath)) return false;

        string packageJsonFullPath = Path.Combine(dir, "package.json");
        string folderName = Path.GetFileName(dir);
        string packageId = folderName.Split('@')[0];
        string displayName = packageId;
        string version = "";
        if (File.Exists(packageJsonFullPath))
        {
            string json = File.ReadAllText(packageJsonFullPath);
            packageId = ExtractJsonString(json, "name", packageId);
            displayName = ExtractJsonString(json, "displayName", displayName);
            version = ExtractJsonString(json, "version", version);
        }

        string guidePath = ToProjectRelativePath(guideFullPath);
        string packagePath = ToProjectRelativePath(dir);
        string guideText = File.ReadAllText(guideFullPath);
        string routerEntry = ExtractRequestedRouterEntry(guideText, guidePath, displayName);
        routerEntry = RewriteRouterEntryPath(routerEntry, guidePath);

        guide = new PackageGuide(packageId, displayName, version, packagePath, guidePath, ExtractReadWhen(guideText), routerEntry);
        return true;
    }

    private static string RewriteRouterEntryPath(string routerEntry, string guidePath)
    {
        return Regex.Replace(routerEntry, @"^- `[^`]+`", $"- `{guidePath}`", RegexOptions.CultureInvariant);
    }

    private static void WritePackageGuideRouter(List<PackageGuide> guides)
    {
        string fullPath = ProjectRelativeFullPath(PackageGuideRouterPath);
        if (!File.Exists(fullPath)) return;

        string text = File.ReadAllText(fullPath).Replace("\r\n", "\n");
        int entriesStart = text.IndexOf(EntriesHeading, StringComparison.Ordinal);
        int nextHeading = text.IndexOf("\n" + GeneratedIndexHeading, StringComparison.Ordinal);
        if (entriesStart < 0 || nextHeading < 0 || nextHeading <= entriesStart) return;

        var sb = new StringBuilder();
        sb.AppendLine(EntriesHeading);
        sb.AppendLine();
        foreach (var guide in guides)
            sb.AppendLine(guide.RouterEntry);
        sb.AppendLine();

        string updated = text[..entriesStart] + sb + text[(nextHeading + 1)..];
        WriteIfChanged(fullPath, updated);
    }

    private static void WritePackageIndex(string aiEntryPointFullPath, List<PackageGuide> guides)
    {
        string entryDirectory = Path.GetDirectoryName(aiEntryPointFullPath);
        if (string.IsNullOrEmpty(entryDirectory)) return;

        string fullPath = Path.Combine(entryDirectory, "packages", "actionfit-packages.md");
        Directory.CreateDirectory(Path.GetDirectoryName(fullPath));

        var sb = new StringBuilder();
        sb.AppendLine("# Installed ActionFit Package AI Guides");
        sb.AppendLine();
        sb.AppendLine("This file is auto-generated by Custom Package Manager as a compatibility pointer.");
        sb.AppendLine();
        sb.AppendLine("Direct package `AI_GUIDE.md` pointers are intentionally centralized in the package-shipped router:");
        sb.AppendLine();
        sb.AppendLine($"- `{PackageGuideRouterPath}`");
        sb.AppendLine();
        sb.AppendLine($"Installed ActionFit package guides found: {guides.Count}.");
        sb.AppendLine();
        sb.AppendLine("Read the router above to decide which package-local `AI_GUIDE.md` applies to the current task.");

        WriteIfChanged(fullPath, sb.ToString());
    }

    private static List<AiEntryPoint> FindAiEntryPoints()
    {
        var result = new List<AiEntryPoint>();
        string primaryEntryPoint = FindPrimaryProjectRouter();
        bool hasProjectRouter = !string.IsNullOrEmpty(primaryEntryPoint);
        if (!hasProjectRouter)
            primaryEntryPoint = FindFirstFallbackAiEntryPoint();

        if (!string.IsNullOrEmpty(primaryEntryPoint))
            AddAiEntryPoint(result, primaryEntryPoint, true);

        // Tool-specific entry points should stay lightweight. In projects that
        // already use a central PROJECT.md, they normally point there instead
        // of receiving a duplicated package-router block.
        if (!hasProjectRouter)
        {
            foreach (string relativePath in GetFallbackAiEntryPointCandidates())
            {
                string fullPath = ProjectRelativeFullPath(relativePath);
                if (File.Exists(fullPath))
                    AddAiEntryPoint(result, fullPath, false);
            }
        }

        return result;
    }

    private static void AddAiEntryPoint(List<AiEntryPoint> entryPoints, string fullPath, bool writePackageIndex)
    {
        if (entryPoints.Any(entry => string.Equals(entry.FullPath, fullPath, StringComparison.OrdinalIgnoreCase)))
            return;

        entryPoints.Add(new AiEntryPoint(fullPath, writePackageIndex));
    }

    private static string FindPrimaryProjectRouter()
    {
        foreach (string relativePath in GetKnownProjectRouterCandidates())
        {
            string fullPath = ProjectRelativeFullPath(relativePath);
            if (File.Exists(fullPath)) return fullPath;
        }

        var projectRouters = FindProjectMdFiles();
        if (projectRouters.Count == 1) return projectRouters[0];
        return null;
    }

    private static string FindFirstFallbackAiEntryPoint()
    {
        foreach (string relativePath in GetFallbackAiEntryPointCandidates())
        {
            string fullPath = ProjectRelativeFullPath(relativePath);
            if (File.Exists(fullPath)) return fullPath;
        }

        return null;
    }

    private static IEnumerable<string> GetKnownProjectRouterCandidates()
    {
        yield return "Docs/AI/PROJECT.md";
        yield return "PROJECT.md";
    }

    private static IEnumerable<string> GetFallbackAiEntryPointCandidates()
    {
        yield return "AGENTS.md";
        yield return "CLAUDE.md";
        yield return "GEMINI.md";
    }

    private static List<string> FindProjectMdFiles()
    {
        string root = ProjectRootPath;
        if (!Directory.Exists(root)) return new List<string>();

        var result = new List<string>();
        var pending = new Stack<string>();
        pending.Push(root);

        while (pending.Count > 0)
        {
            string current = pending.Pop();
            if (!string.Equals(current, root, StringComparison.OrdinalIgnoreCase) && IsIgnoredPath(current))
                continue;

            string projectFile = Path.Combine(current, "PROJECT.md");
            if (File.Exists(projectFile))
                result.Add(projectFile);

            foreach (string child in EnumerateDirectoriesSafe(current))
                pending.Push(child);
        }

        return result.OrderBy(path => path, StringComparer.OrdinalIgnoreCase).ToList();
    }

    private static IEnumerable<string> EnumerateDirectoriesSafe(string fullPath)
    {
        try
        {
            return Directory.EnumerateDirectories(fullPath).ToArray();
        }
        catch
        {
            return Enumerable.Empty<string>();
        }
    }

    private static bool IsIgnoredPath(string fullPath)
    {
        string relativePath = ToProjectRelativePath(fullPath).Replace("\\", "/");
        return relativePath.StartsWith("Library/", StringComparison.OrdinalIgnoreCase)
            || relativePath.StartsWith("Logs/", StringComparison.OrdinalIgnoreCase)
            || relativePath.StartsWith("Packages/", StringComparison.OrdinalIgnoreCase)
            || relativePath.StartsWith("ProjectSettings/", StringComparison.OrdinalIgnoreCase)
            || relativePath.StartsWith("Temp/", StringComparison.OrdinalIgnoreCase)
            || relativePath.StartsWith("UserSettings/", StringComparison.OrdinalIgnoreCase)
            || relativePath.StartsWith("obj/", StringComparison.OrdinalIgnoreCase);
    }

    private static void EnsureProjectRouterLink(string fullPath, int guideCount)
    {
        string text = File.ReadAllText(fullPath);
        string block =
            StartMarker + "\n" +
            "\n" +
            "## Installed ActionFit Package AI Guides\n" +
            "\n" +
            $"- `{PackageGuideRouterPath}` - package-shipped AI router for installed ActionFit package guides ({guideCount} found); read when working with files under `Packages/com.actionfit.*` or `Library/PackageCache/com.actionfit.*@*`, or diagnosing ActionFit package install/update/publish behavior.\n" +
            "\n" +
            EndMarker;

        string updated;
        string pattern = Regex.Escape(StartMarker) + ".*?" + Regex.Escape(EndMarker);
        if (Regex.IsMatch(text, pattern, RegexOptions.Singleline))
            updated = Regex.Replace(text, pattern, block, RegexOptions.Singleline);
        else
            updated = text.TrimEnd() + "\n\n" + block + "\n";

        WriteIfChanged(fullPath, updated);
    }

    private static List<string> ExtractReadWhen(string guideText)
    {
        var result = new List<string>();
        var match = Regex.Match(guideText, @"(?ms)^## Project Router Registration\s+.*?^Read this file when:\s*(?<body>.*?)(?:\n## |\z)");
        if (!match.Success) return result;

        foreach (string line in match.Groups["body"].Value.Replace("\r\n", "\n").Split('\n'))
        {
            string trimmed = line.Trim();
            if (!trimmed.StartsWith("- ", StringComparison.Ordinal)) continue;
            result.Add(trimmed[2..].Trim());
        }

        return result;
    }

    private static string ExtractRequestedRouterEntry(string guideText, string guidePath, string displayName)
    {
        var match = Regex.Match(guideText, @"(?ms)^Requested router entry:\s*(?<body>.*?)(?:\n\n|\n## |\z)");
        if (match.Success)
        {
            foreach (string line in match.Groups["body"].Value.Replace("\r\n", "\n").Split('\n'))
            {
                string trimmed = line.Trim();
                if (trimmed.StartsWith("- ", StringComparison.Ordinal))
                    return trimmed;
            }
        }

        return $"- `{guidePath}` - {displayName}; read when changing or diagnosing this package.";
    }

    private static string ExtractJsonString(string json, string key, string fallback)
    {
        var match = Regex.Match(json, $"\"{Regex.Escape(key)}\"\\s*:\\s*\"([^\"]*)\"");
        return match.Success ? match.Groups[1].Value : fallback;
    }

    private static string ProjectRelativeFullPath(string relativePath)
    {
        return Path.Combine(ProjectRootPath, relativePath.Replace("/", Path.DirectorySeparatorChar.ToString()));
    }

    private static string ToProjectRelativePath(string fullPath)
    {
        string normalized = Path.GetFullPath(fullPath).Replace("\\", "/");
        string root = ProjectRootPath.Replace("\\", "/").TrimEnd('/');
        return normalized.StartsWith(root + "/", StringComparison.OrdinalIgnoreCase)
            ? normalized[(root.Length + 1)..]
            : normalized;
    }

    private static string ProjectRootPath => Path.GetFullPath(Path.Combine(Application.dataPath, ".."));

    private static void WriteIfChanged(string fullPath, string content)
    {
        content = content.Replace("\r\n", "\n");
        if (File.Exists(fullPath) && File.ReadAllText(fullPath).Replace("\r\n", "\n") == content)
            return;

        File.WriteAllText(fullPath, content, new UTF8Encoding(false));
        AssetDatabase.Refresh();
    }

    private readonly struct PackageGuide
    {
        public PackageGuide(string packageId, string displayName, string version, string packagePath, string guidePath, List<string> readWhen, string routerEntry)
        {
            PackageId = packageId;
            DisplayName = string.IsNullOrWhiteSpace(displayName) ? packageId : displayName;
            Version = version;
            PackagePath = packagePath;
            GuidePath = guidePath;
            ReadWhen = readWhen ?? new List<string>();
            RouterEntry = string.IsNullOrWhiteSpace(routerEntry)
                ? $"- `{guidePath}` - {DisplayName}; read when changing or diagnosing this package."
                : routerEntry;
        }

        public string PackageId { get; }
        public string DisplayName { get; }
        public string Version { get; }
        public string PackagePath { get; }
        public string GuidePath { get; }
        public List<string> ReadWhen { get; }
        public string RouterEntry { get; }
        public bool IsEmbedded => PackagePath.StartsWith("Packages/", StringComparison.OrdinalIgnoreCase);
    }

    private readonly struct AiEntryPoint
    {
        public AiEntryPoint(string fullPath, bool writePackageIndex)
        {
            FullPath = fullPath;
            WritePackageIndex = writePackageIndex;
        }

        public string FullPath { get; }
        public bool WritePackageIndex { get; }
    }
}
#endif
