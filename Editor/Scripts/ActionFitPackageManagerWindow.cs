#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using UnityEditor;
using UnityEditor.PackageManager;
using UnityEngine;

public class ActionFitPackageManagerWindow : EditorWindow
{
    private const string PackageName = "com.actionfit.custompackagemanager";
    private const string CatalogRelativePath = "Editor/Catalog/package_catalog.csv";
    private static readonly string PackageCatalogPath = Path.Combine("Packages", PackageName, CatalogRelativePath).Replace("\\", "/");
    private static readonly string PackageCachePath = Path.Combine("Library", "PackageCache").Replace("\\", "/");
    private static string ProjectRootPath => Path.GetFullPath(Path.Combine(Application.dataPath, ".."));
    private static string ManifestFullPath => Path.Combine(ProjectRootPath, "Packages", "manifest.json");
    private static string ProjectRelativeFullPath(string relativePath)
    {
        return Path.Combine(ProjectRootPath, relativePath.Replace("/", Path.DirectorySeparatorChar.ToString()));
    }

    private static string ToProjectRelativePath(string fullPath)
    {
        string root = ProjectRootPath.Replace("\\", "/").TrimEnd('/');
        string normalized = Path.GetFullPath(fullPath).Replace("\\", "/");
        return normalized.StartsWith(root + "/", StringComparison.Ordinal)
            ? normalized[(root.Length + 1)..]
            : normalized;
    }

    private readonly Dictionary<string, int> _selectedVersionByPackage = new();
    private readonly HashSet<string> _expandedPackageIds = new();
    private readonly HashSet<string> _expandedCommunityPackageIds = new();
    private readonly HashSet<string> _expandedCommentKeys = new();
    private readonly HashSet<string> _selectedUpdatePackageIds = new();
    private readonly Dictionary<string, ActionFitPackageCommunityClient.Summary> _communitySummariesByPackage = new();
    private readonly Dictionary<string, CommentPanelState> _commentStatesByPackage = new();
    private readonly Dictionary<string, string> _communityMessagesByPackage = new();
    private readonly List<PackageGroup> _packages = new();
    private ActionFitPackageCatalogSettings_SO _settings;
    private Vector2 _scroll;
    private string _filter = "";
    private bool _showUpdateManager;
    private string _historyPackageId = "";
    private bool _historyRangeOnly;
    private string _historyFromVersion = "";
    private string _historyToVersion = "";

    [MenuItem("Tools/ActionFit/Package Manager/Package Manager", false, 0)]
    public static void Open()
    {
        GetWindow<ActionFitPackageManagerWindow>("ActionFit Packages");
    }

    private void OnEnable()
    {
        _settings = ActionFitPackageCatalogSettingsProvider.FindOrCreate();
        Events.registeredPackages += OnRegisteredPackages;
        Reload();
    }

    private void OnDisable()
    {
        Events.registeredPackages -= OnRegisteredPackages;
    }

    private void OnGUI()
    {
        DrawToolbar();

        if (_packages.Count == 0)
        {
            EditorGUILayout.HelpBox($"Catalog not found or empty.\n{ActiveCatalogPath}", MessageType.Warning);
        }

        _filter = EditorGUILayout.TextField("Filter", _filter);
        _scroll = EditorGUILayout.BeginScrollView(_scroll);

        DrawPackageSections();

        EditorGUILayout.EndScrollView();
    }

    private void DrawToolbar()
    {
        using (new EditorGUILayout.HorizontalScope(EditorStyles.toolbar))
        {
            if (GUILayout.Button("Reload", EditorStyles.toolbarButton, GUILayout.Width(70))) Reload();
            if (GUILayout.Button("Update Catalog", EditorStyles.toolbarButton, GUILayout.Width(105))) UpdateCatalog();
            if (GUILayout.Button("Settings", EditorStyles.toolbarButton, GUILayout.Width(80)))
                Selection.activeObject = _settings = ActionFitPackageCatalogSettingsProvider.FindOrCreate();
            if (GUILayout.Button(_showUpdateManager ? "Hide Updates" : "Updates", EditorStyles.toolbarButton, GUILayout.Width(95)))
                _showUpdateManager = !_showUpdateManager;
            if (GUILayout.Button("Console", EditorStyles.toolbarButton, GUILayout.Width(80))) ActionFitPackageManagerConsoleWindow.Open();
            GUILayout.FlexibleSpace();
        }
    }

    private void DrawPackageSections()
    {
        var filtered = FilteredPackages().ToList();
        var manager = filtered.Where(p => p.Id == PackageName).ToList();
        var catalogPackages = filtered.Where(p => p.Id != PackageName).ToList();
        var embedded = SortPackagesForDisplay(catalogPackages.Where(p => GetInstalledVersion(p.Id).IsEmbedded));
        var downloaded = SortPackagesForDisplay(catalogPackages.Where(p =>
        {
            var installed = GetInstalledVersion(p.Id);
            return installed.IsInstalled && !installed.IsEmbedded;
        }));
        var available = SortPackagesForDisplay(catalogPackages.Where(p => !GetInstalledVersion(p.Id).IsInstalled));

        DrawPackageSection("Package Manager", manager);
        EditorGUILayout.Space(4);
        EditorGUILayout.LabelField("", GUI.skin.horizontalSlider);
        EditorGUILayout.Space(4);
        if (_showUpdateManager)
        {
            DrawUpdateManager(filtered);
            EditorGUILayout.Space(4);
            EditorGUILayout.LabelField("", GUI.skin.horizontalSlider);
            EditorGUILayout.Space(4);
        }
        DrawPackageSection("Embedded Packages", embedded);
        EditorGUILayout.Space(4);
        EditorGUILayout.LabelField("", GUI.skin.horizontalSlider);
        EditorGUILayout.Space(4);
        DrawPackageSection("Downloaded Packages", downloaded);
        EditorGUILayout.Space(4);
        EditorGUILayout.LabelField("", GUI.skin.horizontalSlider);
        EditorGUILayout.Space(4);
        DrawPackageSection("Available Packages", available);
    }

    private void DrawPackageSection(string title, List<PackageGroup> packages)
    {
        EditorGUILayout.LabelField($"{title} ({packages.Count})", EditorStyles.boldLabel);

        if (packages.Count == 0)
        {
            EditorGUILayout.HelpBox("No packages.", MessageType.None);
            return;
        }

        foreach (var package in packages)
        {
            DrawPackage(package);
            EditorGUILayout.Space(6);
        }
    }

    private IEnumerable<PackageGroup> FilteredPackages()
    {
        if (string.IsNullOrWhiteSpace(_filter)) return _packages;

        string filter = _filter.Trim();
        return _packages.Where(p =>
            p.Id.IndexOf(filter, StringComparison.OrdinalIgnoreCase) >= 0 ||
            p.DisplayName.IndexOf(filter, StringComparison.OrdinalIgnoreCase) >= 0 ||
            p.Owner.IndexOf(filter, StringComparison.OrdinalIgnoreCase) >= 0);
    }

    private List<PackageGroup> SortPackagesForDisplay(IEnumerable<PackageGroup> packages)
    {
        return packages
            .OrderByDescending(p => GetCommunitySummary(p).Score)
            .ThenByDescending(p => GetCommunitySummary(p).Likes)
            .ThenByDescending(p => GetCommunitySummary(p).CommentCount)
            .ThenBy(p => p.DisplayName, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private void DrawPackage(PackageGroup package)
    {
        var installed = GetInstalledVersion(package.Id);
        var versions = package.Versions;
        int selected = GetSelectedVersionIndex(package, installed.Version);
        var selectedVersion = versions[selected];

        using (new EditorGUILayout.VerticalScope(EditorStyles.helpBox))
        {
            using (new EditorGUILayout.HorizontalScope())
            {
                bool expanded = _expandedPackageIds.Contains(package.Id);
                bool nextExpanded = EditorGUILayout.Foldout(expanded, package.DisplayName, true, EditorStyles.foldoutHeader);
                if (nextExpanded) _expandedPackageIds.Add(package.Id);
                else _expandedPackageIds.Remove(package.Id);

                var community = GetCommunitySummary(package);
                GUILayout.FlexibleSpace();
                EditorGUILayout.LabelField($"Installed: {installed.Label}", EditorStyles.miniLabel, GUILayout.Width(150));
                EditorGUILayout.LabelField($"Latest: {package.LatestVersionLabel}", EditorStyles.miniLabel, GUILayout.Width(150));
                EditorGUILayout.LabelField($"Score: {community.Score}  L/D: {community.Likes}/{community.Dislikes}  C: {community.CommentCount}", EditorStyles.miniLabel, GUILayout.Width(170));
                EditorGUILayout.LabelField($"Owner: {package.Owner}", EditorStyles.miniLabel, GUILayout.Width(120));
                EditorGUILayout.LabelField(package.Id, EditorStyles.miniLabel, GUILayout.Width(240));
            }

            if (!_expandedPackageIds.Contains(package.Id)) return;

            EditorGUILayout.LabelField("Installed", installed.Label);
            EditorGUILayout.LabelField("Owner", package.Owner);
            DrawReadonlyText("Description", selectedVersion.Description);
            DrawReadonlyText("Selected Version Changelog", selectedVersion.Changelog);

            using (new EditorGUILayout.HorizontalScope())
            {
                EditorGUILayout.LabelField("Version", GUILayout.Width(80));
                selected = EditorGUILayout.Popup(selected, versions.Select(v => v.VersionLabel).ToArray());
                _selectedVersionByPackage[package.Id] = selected;

                EditorGUI.BeginDisabledGroup(!CanApplySelectedVersion(installed, selectedVersion));
                if (GUILayout.Button(installed.IsInstalled ? "Apply Version" : "Install", GUILayout.Width(120)))
                    ApplyPackage(package, selectedVersion);
                EditorGUI.EndDisabledGroup();

                EditorGUI.BeginDisabledGroup(!installed.IsInstalled);
                if (GUILayout.Button("Remove", GUILayout.Width(90)))
                    RemovePackage(package);
                EditorGUI.EndDisabledGroup();

                if (installed.IsInstalled && installed.IsEmbedded)
                {
                    if (GUILayout.Button("Use Downloaded", GUILayout.Width(120)))
                        ConvertEmbeddedToDownloaded(package, selectedVersion);
                }
                else if (installed.IsInstalled)
                {
                    if (GUILayout.Button("Embed for Edit", GUILayout.Width(120)))
                        ConvertDownloadedToEmbedded(package, installed);
                }

                if (GUILayout.Button("History", GUILayout.Width(90)))
                    ShowHistory(package, false, installed.Version, selectedVersion.Version);

                if (GUILayout.Button("Latest Git", GUILayout.Width(90)))
                    OpenLatestGit(package);

                if (installed.IsInstalled && !IsSameVersion(selectedVersion.Version, installed.Version))
                {
                    if (GUILayout.Button("Changes", GUILayout.Width(90)))
                        ShowHistory(package, true, installed.Version, selectedVersion.Version);
                }
            }

            if (installed.IsEmbedded)
                EditorGUILayout.HelpBox("Embedded package is present under Packages/. Applying a different version will write the Git UPM dependency, remove the embedded folder, and run Package Manager resolve.", MessageType.Info);

            if (!string.IsNullOrWhiteSpace(selectedVersion.Dependencies))
                EditorGUILayout.LabelField("Dependencies", selectedVersion.Dependencies, EditorStyles.miniLabel);

            string updateStatus = GetUpdateStatus(package, installed);
            if (!string.IsNullOrWhiteSpace(updateStatus))
                EditorGUILayout.LabelField("Update", updateStatus, EditorStyles.miniLabel);

            DrawCommunity(package);
        }
    }

    private void DrawCommunity(PackageGroup package)
    {
        var summary = GetCommunitySummary(package);

        using (new EditorGUILayout.VerticalScope(EditorStyles.helpBox))
        {
            using (new EditorGUILayout.HorizontalScope())
            {
                bool expanded = _expandedCommunityPackageIds.Contains(package.Id);
                bool nextExpanded = EditorGUILayout.Foldout(expanded, "Community", true);
                if (nextExpanded) _expandedCommunityPackageIds.Add(package.Id);
                else _expandedCommunityPackageIds.Remove(package.Id);

                GUILayout.FlexibleSpace();
                EditorGUILayout.LabelField($"Score: {summary.Score}", EditorStyles.miniLabel, GUILayout.Width(70));
                EditorGUILayout.LabelField($"Likes: {summary.Likes}", EditorStyles.miniLabel, GUILayout.Width(75));
                EditorGUILayout.LabelField($"Dislikes: {summary.Dislikes}", EditorStyles.miniLabel, GUILayout.Width(90));
                EditorGUILayout.LabelField($"Comments: {summary.CommentCount}", EditorStyles.miniLabel, GUILayout.Width(105));
            }

            if (!_expandedCommunityPackageIds.Contains(package.Id)) return;

            DrawVoteControls(package);
            DrawCommentPanel(package);

            if (_communityMessagesByPackage.TryGetValue(package.Id, out string message) &&
                !string.IsNullOrWhiteSpace(message))
            {
                EditorGUILayout.HelpBox(message, IsCommunityFailureMessage(message) ? MessageType.Warning : MessageType.Info);
            }
        }
    }

    private void DrawVoteControls(PackageGroup package)
    {
        string localVote = ActionFitPackageCommunityClient.GetLocalVote(package.Id);
        bool hasLocalVote = !string.IsNullOrWhiteSpace(localVote);
        using (new EditorGUILayout.HorizontalScope())
        {
            EditorGUILayout.LabelField("Vote", GUILayout.Width(80));

            EditorGUI.BeginDisabledGroup(hasLocalVote);
            if (GUILayout.Button(string.Equals(localVote, ActionFitPackageCommunityClient.VoteLike, StringComparison.OrdinalIgnoreCase) ? "Liked" : "Like", GUILayout.Width(90)))
                SubmitVote(package, ActionFitPackageCommunityClient.VoteLike);
            EditorGUI.EndDisabledGroup();

            EditorGUI.BeginDisabledGroup(hasLocalVote);
            if (GUILayout.Button(string.Equals(localVote, ActionFitPackageCommunityClient.VoteDislike, StringComparison.OrdinalIgnoreCase) ? "Disliked" : "Dislike", GUILayout.Width(90)))
                SubmitVote(package, ActionFitPackageCommunityClient.VoteDislike);
            EditorGUI.EndDisabledGroup();

            GUILayout.FlexibleSpace();
        }
    }

    private void DrawCommentPanel(PackageGroup package)
    {
        var state = GetCommentState(package.Id);

        using (new EditorGUILayout.HorizontalScope())
        {
            EditorGUILayout.LabelField($"Comments ({GetCommunitySummary(package).CommentCount})", EditorStyles.boldLabel);
            GUILayout.FlexibleSpace();
        }

        if (!state.Loaded)
        {
            EditorGUILayout.HelpBox("Update Catalog to refresh package comments.", MessageType.None);
        }
        else if (state.Comments.Count == 0)
        {
            EditorGUILayout.HelpBox("No comments yet.", MessageType.None);
        }
        else
        {
            for (int i = 0; i < state.Comments.Count; i++)
                DrawComment(package, state.Comments[i], i);
        }

        EditorGUILayout.Space(4);
        EditorGUILayout.LabelField(state.HasMyComment ? "My Comment" : "Add Comment", EditorStyles.boldLabel);
        state.TitleDraft = EditorGUILayout.TextField("Title", state.TitleDraft ?? "");
        EditorGUILayout.LabelField("Description");
        var textAreaStyle = new GUIStyle(EditorStyles.textArea) { wordWrap = true };
        state.BodyDraft = EditorGUILayout.TextArea(state.BodyDraft ?? "", textAreaStyle, GUILayout.MinHeight(64f));

        using (new EditorGUILayout.HorizontalScope())
        {
            EditorGUILayout.LabelField("One editable comment per project and package.", EditorStyles.miniLabel);
            GUILayout.FlexibleSpace();

            bool canSubmit = !string.IsNullOrWhiteSpace(state.TitleDraft) && !string.IsNullOrWhiteSpace(state.BodyDraft);
            EditorGUI.BeginDisabledGroup(!canSubmit);
            if (GUILayout.Button(state.HasMyComment ? "Update Comment" : "Save Comment", GUILayout.Width(130)))
                SaveComment(package);
            EditorGUI.EndDisabledGroup();
        }
    }

    private void DrawComment(PackageGroup package, ActionFitPackageCommunityClient.Comment comment, int index)
    {
        string key = CommentFoldoutKey(package.Id, comment, index);
        bool expanded = _expandedCommentKeys.Contains(key);
        string title = string.IsNullOrWhiteSpace(comment.title) ? "(untitled)" : comment.title.Trim();
        string updated = FirstNonEmpty(comment.updated_at, comment.created_at);
        string mine = comment.is_mine ? " (mine)" : "";
        string label = string.IsNullOrWhiteSpace(updated) ? $"{title}{mine}" : $"{title}{mine} - {updated}";

        bool nextExpanded = EditorGUILayout.Foldout(expanded, label, true);
        if (nextExpanded) _expandedCommentKeys.Add(key);
        else _expandedCommentKeys.Remove(key);

        if (!nextExpanded) return;

        DrawReadonlyText("Description", comment.body);
    }

    private void SubmitVote(PackageGroup package, string vote)
    {
        _settings = ActionFitPackageCatalogSettingsProvider.FindOrCreate();
        if (ActionFitPackageCommunityClient.SubmitVote(_settings, package.Id, vote, out var summary, out string message))
        {
            _communitySummariesByPackage[package.Id] = summary;
            _communityMessagesByPackage[package.Id] = message;
            Repaint();
            return;
        }

        _communityMessagesByPackage[package.Id] = message;
        UnityEngine.Debug.LogWarning($"[ActionFitPackageManager] {message}");
        Repaint();
    }

    private void SaveComment(PackageGroup package)
    {
        _settings = ActionFitPackageCatalogSettingsProvider.FindOrCreate();
        var state = GetCommentState(package.Id);
        if (ActionFitPackageCommunityClient.UpsertComment(_settings, package.Id, state.TitleDraft, state.BodyDraft, out var summary, out string message))
        {
            _communitySummariesByPackage[package.Id] = summary;
            _communityMessagesByPackage[package.Id] = message;
            ApplySavedCommentToState(package, state);
            Repaint();
            return;
        }

        _communityMessagesByPackage[package.Id] = message;
        UnityEngine.Debug.LogWarning($"[ActionFitPackageManager] {message}");
        Repaint();
    }

    private void LoadCachedCommentStates()
    {
        _commentStatesByPackage.Clear();
        bool hasCommentCache = ActionFitPackageCommunityClient.HasCommentCache;
        var cached = ActionFitPackageCommunityClient.LoadCachedCommentsByPackage();

        foreach (var package in _packages)
        {
            var state = GetCommentState(package.Id);
            state.Loaded = hasCommentCache;
            if (!cached.TryGetValue(package.Id, out var result)) continue;

            state.Comments = result.Comments;
            state.HasMyComment = result.MyComment != null;
            if (result.MyComment == null) continue;

            state.TitleDraft = result.MyComment.title ?? "";
            state.BodyDraft = result.MyComment.body ?? "";
        }
    }

    private static void ApplySavedCommentToState(PackageGroup package, CommentPanelState state)
    {
        state.Loaded = true;
        state.HasMyComment = true;

        var mine = state.Comments.FirstOrDefault(c => c.is_mine);
        if (mine == null)
        {
            mine = new ActionFitPackageCommunityClient.Comment
            {
                comment_id = $"{package.Id}:local",
                package_id = package.Id,
                created_at = DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ssZ"),
                is_mine = true
            };
            state.Comments.Insert(0, mine);
        }

        mine.title = (state.TitleDraft ?? "").Trim();
        mine.body = (state.BodyDraft ?? "").Trim();
        mine.updated_at = DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ssZ");
    }

    private CommentPanelState GetCommentState(string packageId)
    {
        if (!_commentStatesByPackage.TryGetValue(packageId, out var state))
        {
            state = new CommentPanelState();
            _commentStatesByPackage[packageId] = state;
        }

        return state;
    }

    private ActionFitPackageCommunityClient.Summary GetCommunitySummary(PackageGroup package)
    {
        if (_communitySummariesByPackage.TryGetValue(package.Id, out var summary))
            return summary;

        return new ActionFitPackageCommunityClient.Summary(package.Likes, package.Dislikes, package.CommentCount);
    }

    private static string CommentFoldoutKey(string packageId, ActionFitPackageCommunityClient.Comment comment, int index)
    {
        string stable = FirstNonEmpty(comment.comment_id, comment.updated_at, comment.created_at, comment.title, index.ToString());
        return $"{packageId}:{stable}";
    }

    private static bool IsCommunityFailureMessage(string message)
    {
        return message.IndexOf("failed", StringComparison.OrdinalIgnoreCase) >= 0 ||
               message.IndexOf("required", StringComparison.OrdinalIgnoreCase) >= 0 ||
               message.IndexOf("invalid", StringComparison.OrdinalIgnoreCase) >= 0 ||
               message.IndexOf("unauthorized", StringComparison.OrdinalIgnoreCase) >= 0;
    }

    private void DrawUpdateManager(List<PackageGroup> packages)
    {
        var candidates = BuildUpdateCandidates(packages);
        var updatable = candidates.Where(c => c.CanApply).ToList();

        using (new EditorGUILayout.VerticalScope(EditorStyles.helpBox))
        {
            using (new EditorGUILayout.HorizontalScope())
            {
                EditorGUILayout.LabelField($"Available Updates ({updatable.Count}/{candidates.Count})", EditorStyles.boldLabel);
                GUILayout.FlexibleSpace();

                EditorGUI.BeginDisabledGroup(updatable.Count == 0);
                if (GUILayout.Button("Select All", GUILayout.Width(80)))
                {
                    _selectedUpdatePackageIds.Clear();
                    foreach (var candidate in updatable)
                        _selectedUpdatePackageIds.Add(candidate.Package.Id);
                }

                if (GUILayout.Button("Clear", GUILayout.Width(60)))
                    _selectedUpdatePackageIds.Clear();

                var selectedPackages = updatable
                    .Where(c => _selectedUpdatePackageIds.Contains(c.Package.Id))
                    .Select(c => c.Package)
                    .ToList();

                EditorGUI.BeginDisabledGroup(selectedPackages.Count == 0);
                if (GUILayout.Button($"Update Selected ({selectedPackages.Count})", GUILayout.Width(150)))
                    UpdateAllLatest(selectedPackages);
                EditorGUI.EndDisabledGroup();

                if (GUILayout.Button($"Update All ({updatable.Count})", GUILayout.Width(120)))
                    UpdateAllLatest(updatable.Select(c => c.Package).ToList());
                EditorGUI.EndDisabledGroup();
            }

            if (candidates.Count == 0)
            {
                EditorGUILayout.HelpBox("No installed packages have a newer catalog latest version.", MessageType.None);
                DrawHistoryPanel();
                return;
            }

            foreach (var candidate in candidates)
            {
                using (new EditorGUILayout.HorizontalScope())
                {
                    EditorGUI.BeginDisabledGroup(!candidate.CanApply);
                    bool selected = _selectedUpdatePackageIds.Contains(candidate.Package.Id);
                    bool nextSelected = EditorGUILayout.Toggle(selected, GUILayout.Width(18));
                    if (nextSelected) _selectedUpdatePackageIds.Add(candidate.Package.Id);
                    else _selectedUpdatePackageIds.Remove(candidate.Package.Id);
                    EditorGUI.EndDisabledGroup();

                    EditorGUILayout.LabelField(candidate.Package.DisplayName, GUILayout.MinWidth(180));
                    EditorGUILayout.LabelField($"{candidate.Installed.Version} -> {candidate.Latest.Version}", EditorStyles.miniLabel, GUILayout.Width(150));
                    EditorGUILayout.LabelField(candidate.Installed.IsEmbedded ? "embedded" : candidate.Package.Id, EditorStyles.miniLabel, GUILayout.MinWidth(180));

                    if (GUILayout.Button("Changes", GUILayout.Width(80)))
                        ShowHistory(candidate.Package, true, candidate.Installed.Version, candidate.Latest.Version);

                    if (GUILayout.Button("History", GUILayout.Width(80)))
                        ShowHistory(candidate.Package, false, candidate.Installed.Version, candidate.Latest.Version);

                    if (GUILayout.Button("Latest Git", GUILayout.Width(90)))
                        OpenLatestGit(candidate.Package);

                    EditorGUI.BeginDisabledGroup(!candidate.CanApply);
                    if (GUILayout.Button("Update", GUILayout.Width(80)))
                        ApplyPackage(candidate.Package, candidate.Latest);
                    EditorGUI.EndDisabledGroup();
                }

                if (candidate.Installed.IsEmbedded)
                    EditorGUILayout.HelpBox($"{candidate.Package.Id} is embedded under Packages/. Updating will convert it to a Git UPM dependency and remove the embedded folder.", MessageType.Info);
            }

            DrawHistoryPanel();
        }
    }

    private List<UpdateCandidate> BuildUpdateCandidates(List<PackageGroup> packages)
    {
        var result = new List<UpdateCandidate>();
        foreach (var package in packages)
        {
            var installed = GetInstalledVersion(package.Id);
            if (!installed.IsInstalled || package.LatestVersion == null) continue;
            if (!IsVersionNewer(package.LatestVersion.Version, installed.Version)) continue;

            result.Add(new UpdateCandidate(package, installed, package.LatestVersion, CanApplySelectedVersion(installed, package.LatestVersion)));
        }

        return result
            .OrderByDescending(c => c.CanApply)
            .ThenBy(c => c.Package.DisplayName, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private void ShowHistory(PackageGroup package, bool rangeOnly, string fromVersion, string toVersion)
    {
        _historyPackageId = package.Id;
        _historyRangeOnly = rangeOnly;
        _historyFromVersion = fromVersion;
        _historyToVersion = toVersion;
    }

    private static void OpenLatestGit(PackageGroup package)
    {
        var latest = package.LatestVersion;
        string url = BuildLatestGitBrowserUrl(latest?.RepoUrl, latest?.Version);
        if (string.IsNullOrWhiteSpace(url))
        {
            EditorUtility.DisplayDialog("ActionFit Package Manager", $"Git URL not found for {package.DisplayName}.", "OK");
            return;
        }

        Application.OpenURL(url);
    }

    private static string BuildLatestGitBrowserUrl(string repoUrl, string version)
    {
        string url = (repoUrl ?? "").Trim();
        if (string.IsNullOrWhiteSpace(url)) return "";

        int hashIndex = url.IndexOf('#');
        if (hashIndex >= 0)
            url = url[..hashIndex];

        if (url.StartsWith("git@github.com:", StringComparison.OrdinalIgnoreCase))
            url = "https://github.com/" + url["git@github.com:".Length..];

        url = url.Replace("\\", "/");
        if (url.EndsWith(".git", StringComparison.OrdinalIgnoreCase))
            url = url[..^4];

        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) ||
            !string.Equals(uri.Host, "github.com", StringComparison.OrdinalIgnoreCase))
            return url;

        string path = uri.AbsolutePath.Trim('/');
        if (path.EndsWith(".git", StringComparison.OrdinalIgnoreCase))
            path = path[..^4];

        if (string.IsNullOrWhiteSpace(path)) return url;

        string versionPath = string.IsNullOrWhiteSpace(version)
            ? ""
            : $"/tree/{Uri.EscapeDataString(version)}";
        return $"https://github.com/{path}{versionPath}";
    }

    private void DrawHistoryPanel()
    {
        if (string.IsNullOrWhiteSpace(_historyPackageId)) return;

        var package = _packages.FirstOrDefault(p => p.Id == _historyPackageId);
        if (package == null) return;

        EditorGUILayout.Space(8);
        using (new EditorGUILayout.VerticalScope(EditorStyles.helpBox))
        {
            using (new EditorGUILayout.HorizontalScope())
            {
                EditorGUILayout.LabelField(
                    _historyRangeOnly
                        ? $"Update Changes: {package.DisplayName} ({_historyFromVersion} -> {_historyToVersion})"
                        : $"Version History: {package.DisplayName}",
                    EditorStyles.boldLabel);
                if (GUILayout.Button("Close", GUILayout.Width(70)))
                    _historyPackageId = "";
            }

            var versions = _historyRangeOnly
                ? GetVersionRange(package, _historyFromVersion, _historyToVersion)
                : package.Versions;

            if (versions.Count == 0)
            {
                EditorGUILayout.HelpBox("No changelog rows were found for the selected version range.", MessageType.None);
                return;
            }

            foreach (var version in versions)
            {
                EditorGUILayout.LabelField(version.VersionLabel, EditorStyles.boldLabel);
                DrawReadonlyText("Changelog", version.Changelog);
            }
        }
    }

    private static List<PackageVersion> GetVersionRange(PackageGroup package, string fromVersion, string toVersion)
    {
        if (string.IsNullOrWhiteSpace(toVersion)) return new List<PackageVersion>();

        return package.Versions
            .Where(v =>
                (string.IsNullOrWhiteSpace(fromVersion) || PackageVersionComparer.Instance.Compare(v.Version, fromVersion) > 0) &&
                PackageVersionComparer.Instance.Compare(v.Version, toVersion) <= 0)
            .OrderBy(v => v.Version, PackageVersionComparer.Instance)
            .ToList();
    }

    private void DrawReadonlyText(string label, string value)
    {
        EditorGUILayout.LabelField(label);
        var style = new GUIStyle(EditorStyles.textArea)
        {
            wordWrap = true
        };
        float height = Mathf.Max(38f, style.CalcHeight(new GUIContent(value ?? ""), EditorGUIUtility.currentViewWidth - 48f));
        EditorGUILayout.SelectableLabel(value ?? "", style, GUILayout.MinHeight(height));
    }

    private int GetSelectedVersionIndex(PackageGroup package, string installedVersion)
    {
        if (_selectedVersionByPackage.TryGetValue(package.Id, out int selected) &&
            selected >= 0 && selected < package.Versions.Count)
            return selected;

        int installed = package.Versions.FindIndex(v => v.Version == installedVersion);
        if (installed >= 0) return installed;

        int latest = package.Versions.FindIndex(v => v.IsLatest);
        return latest >= 0 ? latest : 0;
    }

    private static string GetUpdateStatus(PackageGroup package, InstalledPackage installed)
    {
        if (!installed.IsInstalled) return "not installed";
        if (package.LatestVersion == null) return "latest version not found in catalog";
        if (IsSameVersion(package.LatestVersion.Version, installed.Version)) return "already latest";

        if (!IsVersionNewer(package.LatestVersion.Version, installed.Version))
            return $"installed version is newer than catalog latest ({installed.Version} > {package.LatestVersion.Version})";

        string source = installed.IsEmbedded ? "embedded, " : "";
        return $"{installed.Version} -> {package.LatestVersion.Version} ({source}newer)";
    }

    private static bool CanApplySelectedVersion(InstalledPackage installed, PackageVersion version)
    {
        if (version == null) return false;
        if (!installed.IsInstalled) return true;
        if (!installed.IsEmbedded) return true;
        return !IsSameVersion(version.Version, installed.Version);
    }

    private void Reload()
    {
        _packages.Clear();
        _selectedVersionByPackage.Clear();

        string path = ProjectRelativeFullPath(ActiveCatalogPath);
        if (File.Exists(path))
        {
            var rows = ReadCatalog(path);
            _packages.AddRange(rows
                .GroupBy(r => r.Id)
                .Select(g => new PackageGroup(g.Key, g.OrderByDescending(v => v.Version, PackageVersionComparer.Instance).ToList()))
                .OrderBy(g => g.DisplayName, StringComparer.OrdinalIgnoreCase));
        }

        LoadCachedCommentStates();
    }

    private static string ActiveCatalogPath
    {
        get
        {
            string local = ActionFitPackageCatalogSettingsProvider.LocalCatalogPath;
            return File.Exists(ProjectRelativeFullPath(local)) ? local : PackageCatalogPath;
        }
    }

    private void UpdateCatalog()
    {
        _settings = ActionFitPackageCatalogSettingsProvider.FindOrCreate();
        if (!EditorUtility.DisplayDialog(
                "ActionFit Package Manager",
                "Spreadsheet -> local package catalog CSV\n\nUpdate will overwrite the local catalog CSV with spreadsheet data.",
                "Update",
                "Cancel"))
            return;

        bool ok = ActionFitPackageCatalogUpdater.UpdateCatalog(_settings, out string message);
        if (ok)
        {
            UnityEngine.Debug.Log($"[ActionFitPackageManager] {message}");
            Reload();
        }
        else
        {
            UnityEngine.Debug.LogError($"[ActionFitPackageManager] Update failed: {message}");
            EditorUtility.DisplayDialog("ActionFit Package Manager", message, "OK");
        }
    }

    private static List<PackageVersion> ReadCatalog(string path)
    {
        var records = ReadCsvRecords(File.ReadAllText(path));
        if (records.Count < 2) return new List<PackageVersion>();

        var header = SplitCsvLine(records[0]).Select(h => h.Trim()).ToArray();
        var index = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        for (int i = 0; i < header.Length; i++) index[header[i]] = i;

        var rows = new List<PackageVersion>();
        for (int i = 1; i < records.Count; i++)
        {
            if (string.IsNullOrWhiteSpace(records[i])) continue;
            if (records[i].Contains("(string)", StringComparison.OrdinalIgnoreCase)) continue;

            string[] cols = SplitCsvLine(records[i]);
            string version = Get(cols, index, "version");
            int likes = ParseInt(GetAny(cols, index, "likes", "like_count", "likeCount"));
            int dislikes = ParseInt(GetAny(cols, index, "dislikes", "dislike_count", "dislikeCount"));
            string voteScoreRaw = GetAny(cols, index, "vote_score", "voteScore", "score");
            var row = new PackageVersion
            {
                Id = Get(cols, index, "package_id"),
                DisplayName = Get(cols, index, "display_name"),
                Owner = Get(cols, index, "owner"),
                RepoUrl = Get(cols, index, "repo_url"),
                Version = version,
                Status = Get(cols, index, "status"),
                IsLatest = IsTrue(Get(cols, index, "is_latest")),
                UnityMin = Get(cols, index, "unity_min"),
                Description = Get(cols, index, "description"),
                Changelog = NormalizeChangelog(Get(cols, index, "changelog"), version),
                Dependencies = Get(cols, index, "dependencies"),
                Likes = likes,
                Dislikes = dislikes,
                VoteScore = string.IsNullOrWhiteSpace(voteScoreRaw) ? likes - dislikes : ParseInt(voteScoreRaw),
                CommentCount = ParseInt(GetAny(cols, index, "comment_count", "commentCount", "comments")),
            };

            if (!string.IsNullOrWhiteSpace(row.Id) &&
                !string.IsNullOrWhiteSpace(row.RepoUrl) &&
                !string.IsNullOrWhiteSpace(row.Version))
                rows.Add(row);
        }

        return rows;
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

    private static string NormalizeChangelog(string raw, string version)
    {
        string text = (raw ?? "").Replace("\r\n", "\n").Replace('\r', '\n').Trim();
        if (string.IsNullOrWhiteSpace(text) || string.IsNullOrWhiteSpace(version))
            return text;

        string exactHeading = $@"^\s*#+\s*{Regex.Escape(version)}\s*(\n|$)";
        return Regex.Replace(text, exactHeading, "", RegexOptions.IgnoreCase).Trim();
    }

    private void ApplyPackage(PackageGroup package, PackageVersion version)
    {
        var installed = GetInstalledVersion(package.Id);
        bool replaceEmbedded = installed.IsEmbedded;
        if (replaceEmbedded && IsSameVersion(installed.Version, version.Version))
        {
            EditorUtility.DisplayDialog("ActionFit Package Manager", "Embedded package is already on the selected version.", "OK");
            return;
        }

        if (replaceEmbedded &&
            !EditorUtility.DisplayDialog(
                "ActionFit Package Manager",
                $"Replace embedded package with Git UPM dependency?\n\n{package.Id}: {installed.Version} -> {version.Version}\n\nThis will remove Packages/{package.Id} after writing Packages/manifest.json.",
                "Replace",
                "Cancel"))
            return;

        string manifestPath = ManifestFullPath;
        if (!File.Exists(manifestPath))
        {
            EditorUtility.DisplayDialog("ActionFit Package Manager", "Packages/manifest.json not found.", "OK");
            return;
        }

        string manifest = File.ReadAllText(manifestPath);
        var changes = new List<string>();

        if (!ApplyPackageDependencies(ref manifest, version, changes))
            return;

        manifest = SetDependency(manifest, version.Id, version.PackageUrl);
        changes.Add($"{version.Id} -> {version.PackageUrl}");

        File.WriteAllText(manifestPath, manifest, new UTF8Encoding(false));
        UnityEngine.Debug.Log("[ActionFitPackageManager] Updated manifest:\n" + string.Join("\n", changes));

        if (replaceEmbedded)
            RemoveEmbeddedFolderWithoutManifestChange(package);

        ActionFitPackageAiGuideRouter.EnsureProjectRouter();
        AssetDatabase.Refresh();
        Client.Resolve();
        QueueReload();
    }

    private static void RemoveEmbeddedFolderWithoutManifestChange(PackageGroup package)
    {
        string packageRoot = $"Packages/{package.Id}";
        string fullPackageRoot = Path.Combine(ProjectRootPath, "Packages", package.Id);
        if (!Directory.Exists(fullPackageRoot)) return;

        bool removedFolder = AssetDatabase.MoveAssetToTrash(packageRoot);
        if (removedFolder)
            UnityEngine.Debug.Log($"[ActionFitPackageManager] Removed embedded package folder for Git UPM replacement: {packageRoot}");
        else
            UnityEngine.Debug.LogError($"[ActionFitPackageManager] Failed to remove embedded package folder after manifest update: {packageRoot}");
    }

    private void UpdateAllLatest(List<PackageGroup> packages)
    {
        if (packages == null || packages.Count == 0) return;

        string list = string.Join("\n", packages.Select(p => $"- {p.Id}: {GetInstalledVersion(p.Id).Version} -> {p.LatestVersion.Version}"));
        if (!EditorUtility.DisplayDialog(
                "ActionFit Package Manager",
                $"Update selected packages to latest versions?\n\nEmbedded packages will be converted to Git UPM dependencies.\n\n{list}",
                "Update All Latest",
                "Cancel"))
            return;

        string manifestPath = ManifestFullPath;
        if (!File.Exists(manifestPath))
        {
            EditorUtility.DisplayDialog("ActionFit Package Manager", "Packages/manifest.json not found.", "OK");
            return;
        }

        string manifest = File.ReadAllText(manifestPath);
        var changes = new List<string>();
        var embeddedReplacements = new List<PackageGroup>();
        foreach (var package in packages)
        {
            if (GetInstalledVersion(package.Id).IsEmbedded)
                embeddedReplacements.Add(package);

            if (!ApplyPackageDependencies(ref manifest, package.LatestVersion, changes))
                return;

            manifest = SetDependency(manifest, package.LatestVersion.Id, package.LatestVersion.PackageUrl);
            changes.Add($"{package.LatestVersion.Id} -> {package.LatestVersion.PackageUrl}");
        }

        File.WriteAllText(manifestPath, manifest, new UTF8Encoding(false));
        UnityEngine.Debug.Log("[ActionFitPackageManager] Updated manifest to latest versions:\n" + string.Join("\n", changes.Distinct()));

        foreach (var package in embeddedReplacements)
            RemoveEmbeddedFolderWithoutManifestChange(package);

        ActionFitPackageAiGuideRouter.EnsureProjectRouter();
        AssetDatabase.Refresh();
        Client.Resolve();
        QueueReload();
    }

    private void RemovePackage(PackageGroup package)
    {
        var installed = GetInstalledVersion(package.Id);
        if (!installed.IsInstalled)
        {
            UnityEngine.Debug.LogWarning($"[ActionFitPackageManager] Package is not installed: {package.Id}");
            return;
        }

        if (installed.IsEmbedded)
        {
            RemoveEmbeddedPackage(package);
            return;
        }

        RemoveManifestPackage(package);
    }

    private void ConvertDownloadedToEmbedded(PackageGroup package, InstalledPackage installed)
    {
        if (!installed.IsInstalled || installed.IsEmbedded)
        {
            EditorUtility.DisplayDialog("ActionFit Package Manager", "Only downloaded packages can be embedded for edit.", "OK");
            return;
        }

        string packageRoot = $"Packages/{package.Id}";
        string fullPackageRoot = Path.Combine(ProjectRootPath, "Packages", package.Id);
        if (Directory.Exists(fullPackageRoot))
        {
            UseExistingLocalFolderForEdit(package, packageRoot, fullPackageRoot);
            return;
        }

        if (!TryFindDownloadedPackageSource(package.Id, out string sourcePath, out string sourceError))
        {
            EditorUtility.DisplayDialog("ActionFit Package Manager", sourceError, "OK");
            return;
        }

        string sourcePackageJson = Path.Combine(sourcePath, "package.json");
        string sourcePackageId = ExtractJsonString(File.ReadAllText(sourcePackageJson), "name");
        if (!string.Equals(sourcePackageId, package.Id, StringComparison.Ordinal))
        {
            EditorUtility.DisplayDialog(
                "ActionFit Package Manager",
                $"Resolved package source does not match.\n\nExpected: {package.Id}\nFound: {sourcePackageId}\nSource: {ToProjectRelativePath(sourcePath)}",
                "OK");
            return;
        }

        string sourceVersion = ExtractJsonString(File.ReadAllText(sourcePackageJson), "version");
        if (!EditorUtility.DisplayDialog(
                "ActionFit Package Manager",
                $"Embed downloaded package for edit?\n\n{package.Id}: {sourceVersion}\n\nSource: {ToProjectRelativePath(sourcePath)}\nDestination: {packageRoot}\n\nThis will copy the package into Packages/ and replace its Git UPM dependency with a local file dependency.",
                "Embed for Edit",
                "Cancel"))
            return;

        string manifestPath = ManifestFullPath;
        if (!File.Exists(manifestPath))
        {
            EditorUtility.DisplayDialog("ActionFit Package Manager", "Packages/manifest.json not found.", "OK");
            return;
        }

        string manifest = File.ReadAllText(manifestPath);
        string updatedManifest = SetDependency(manifest, package.Id, LocalPackageDependencyValue(package.Id));
        string tempPackageRoot = Path.Combine(ProjectRootPath, "Temp", "ActionFitPackageManager", package.Id);

        try
        {
            if (Directory.Exists(tempPackageRoot))
                Directory.Delete(tempPackageRoot, true);

            CopyDirectoryForEmbeddedPackage(sourcePath, tempPackageRoot);
            if (!TryValidateLocalPackageFolder(package.Id, tempPackageRoot, out _, out string tempValidationError))
                throw new InvalidOperationException(tempValidationError);

            if (Directory.Exists(fullPackageRoot))
                throw new InvalidOperationException($"Destination package folder already exists: {packageRoot}");

            Directory.CreateDirectory(Path.GetDirectoryName(fullPackageRoot));
            Directory.Move(tempPackageRoot, fullPackageRoot);
            if (!TryValidateLocalPackageFolder(package.Id, fullPackageRoot, out _, out string embeddedValidationError))
                throw new InvalidOperationException(embeddedValidationError);

            File.WriteAllText(manifestPath, updatedManifest, new UTF8Encoding(false));
        }
        catch (Exception ex)
        {
            if (Directory.Exists(tempPackageRoot))
                Directory.Delete(tempPackageRoot, true);

            if (Directory.Exists(fullPackageRoot))
                Directory.Delete(fullPackageRoot, true);

            EditorUtility.DisplayDialog("ActionFit Package Manager", $"Embed for edit failed:\n{ex.Message}", "OK");
            return;
        }

        UnityEngine.Debug.Log($"[ActionFitPackageManager] Embedded package for edit: {package.Id}\nSource: {ToProjectRelativePath(sourcePath)}\nDestination: {packageRoot}");
        ActionFitPackageAiGuideRouter.EnsureProjectRouter();
        AssetDatabase.Refresh();

        try
        {
            ActionFitPackageInfoUtility.CreateOrUpdate(packageRoot);
        }
        catch (Exception ex)
        {
            UnityEngine.Debug.LogWarning($"[ActionFitPackageManager] PackageInfo refresh skipped for {package.Id}: {ex.Message}");
        }

        AssetDatabase.Refresh();
        Client.Resolve();
        QueueReload();
    }

    private void UseExistingLocalFolderForEdit(PackageGroup package, string packageRoot, string fullPackageRoot)
    {
        if (!TryValidateLocalPackageFolder(package.Id, fullPackageRoot, out string localVersion, out string validationError))
        {
            EditorUtility.DisplayDialog("ActionFit Package Manager", $"{validationError}\n\nFolder: {packageRoot}", "OK");
            return;
        }

        if (!EditorUtility.DisplayDialog(
                "ActionFit Package Manager",
                $"Use existing local package folder for edit?\n\n{package.Id}: {localVersion}\n\nFolder: {packageRoot}\n\nThis will replace the Git UPM dependency with a local file dependency without copying or overwriting the local folder.",
                "Use Existing Local",
                "Cancel"))
            return;

        string manifestPath = ManifestFullPath;
        if (!File.Exists(manifestPath))
        {
            EditorUtility.DisplayDialog("ActionFit Package Manager", "Packages/manifest.json not found.", "OK");
            return;
        }

        string manifest = File.ReadAllText(manifestPath);
        string updatedManifest = SetDependency(manifest, package.Id, LocalPackageDependencyValue(package.Id));

        File.WriteAllText(manifestPath, updatedManifest, new UTF8Encoding(false));
        UnityEngine.Debug.Log($"[ActionFitPackageManager] Using existing local package folder for edit: {package.Id}\nDependency: {LocalPackageDependencyValue(package.Id)}\nFolder: {packageRoot}");

        ActionFitPackageAiGuideRouter.EnsureProjectRouter();
        AssetDatabase.Refresh();

        try
        {
            ActionFitPackageInfoUtility.CreateOrUpdate(packageRoot);
        }
        catch (Exception ex)
        {
            UnityEngine.Debug.LogWarning($"[ActionFitPackageManager] PackageInfo refresh skipped for {package.Id}: {ex.Message}");
        }

        AssetDatabase.Refresh();
        Client.Resolve();
        QueueReload();
    }

    private static bool TryValidateLocalPackageFolder(string expectedPackageId, string fullPackageRoot, out string version, out string error)
    {
        version = "";
        error = "";

        if (string.IsNullOrWhiteSpace(fullPackageRoot) || !Directory.Exists(fullPackageRoot))
        {
            string displayPath = string.IsNullOrWhiteSpace(fullPackageRoot) ? "<empty>" : ToProjectRelativePath(fullPackageRoot);
            error = $"Local package folder does not exist: {displayPath}";
            return false;
        }

        string packageJsonPath = Path.Combine(fullPackageRoot, "package.json");
        if (!File.Exists(packageJsonPath))
        {
            error = $"Local package folder is missing package.json: {ToProjectRelativePath(packageJsonPath)}";
            return false;
        }

        string packageJson = File.ReadAllText(packageJsonPath);
        string localPackageId = ExtractJsonString(packageJson, "name");
        if (!string.Equals(localPackageId, expectedPackageId, StringComparison.Ordinal))
        {
            error =
                "Local package folder package ID does not match.\n\n" +
                $"Expected: {expectedPackageId}\n" +
                $"Found: {localPackageId}\n" +
                $"Folder: {ToProjectRelativePath(fullPackageRoot)}";
            return false;
        }

        version = ExtractJsonString(packageJson, "version");
        return true;
    }

    private void ConvertEmbeddedToDownloaded(PackageGroup package, PackageVersion version)
    {
        if (version == null)
        {
            EditorUtility.DisplayDialog("ActionFit Package Manager", "Catalog version not found.", "OK");
            return;
        }

        string packageRoot = $"Packages/{package.Id}";
        string fullPackageRoot = Path.Combine(ProjectRootPath, "Packages", package.Id);
        if (!Directory.Exists(fullPackageRoot))
        {
            EditorUtility.DisplayDialog("ActionFit Package Manager", $"Embedded package folder not found:\n{packageRoot}", "OK");
            QueueReload();
            return;
        }

        if (!EditorUtility.DisplayDialog(
                "ActionFit Package Manager",
                $"Use downloaded Git UPM package instead of local embedded package?\n\n{package.Id}: {version.Version}\n\nThis will write Packages/manifest.json, move {packageRoot} to trash, and run Package Manager resolve.",
                "Use Downloaded",
                "Cancel"))
            return;

        string manifestPath = ManifestFullPath;
        if (!File.Exists(manifestPath))
        {
            EditorUtility.DisplayDialog("ActionFit Package Manager", "Packages/manifest.json not found.", "OK");
            return;
        }

        string manifest = File.ReadAllText(manifestPath);
        var changes = new List<string>();

        if (!ApplyPackageDependencies(ref manifest, version, changes))
            return;

        manifest = SetDependency(manifest, version.Id, version.PackageUrl);
        changes.Add($"{version.Id} -> {version.PackageUrl}");

        File.WriteAllText(manifestPath, manifest, new UTF8Encoding(false));
        UnityEngine.Debug.Log("[ActionFitPackageManager] Converted embedded package to downloaded dependency:\n" + string.Join("\n", changes));

        RemoveEmbeddedFolderWithoutManifestChange(package);
        ActionFitPackageAiGuideRouter.EnsureProjectRouter();
        AssetDatabase.Refresh();
        Client.Resolve();
        QueueReload();
    }

    private void RemoveManifestPackage(PackageGroup package)
    {
        if (!EditorUtility.DisplayDialog(
                "ActionFit Package Manager",
                $"Remove {package.Id} from Packages/manifest.json?",
                "Remove",
                "Cancel"))
            return;

        string manifestPath = ManifestFullPath;
        if (!File.Exists(manifestPath))
        {
            EditorUtility.DisplayDialog("ActionFit Package Manager", "Packages/manifest.json not found.", "OK");
            return;
        }

        string manifest = File.ReadAllText(manifestPath);
        manifest = RemoveDependency(manifest, package.Id, out bool removed);
        if (!removed)
        {
            UnityEngine.Debug.LogWarning($"[ActionFitPackageManager] Package is not in manifest: {package.Id}");
            return;
        }

        File.WriteAllText(manifestPath, manifest, new UTF8Encoding(false));
        UnityEngine.Debug.Log($"[ActionFitPackageManager] Removed manifest dependency: {package.Id}");

        ActionFitPackageAiGuideRouter.EnsureProjectRouter();
        AssetDatabase.Refresh();
        Client.Resolve();
        QueueReload();
    }

    private void RemoveEmbeddedPackage(PackageGroup package)
    {
        string packageRoot = $"Packages/{package.Id}";
        string fullPackageRoot = Path.Combine(ProjectRootPath, "Packages", package.Id);
        if (!Directory.Exists(fullPackageRoot))
        {
            UnityEngine.Debug.LogWarning($"[ActionFitPackageManager] Embedded package folder not found: {packageRoot}");
            QueueReload();
            return;
        }

        if (!EditorUtility.DisplayDialog(
                "ActionFit Package Manager",
                $"Move embedded package folder to trash?\n\n{packageRoot}\n\nIf the same package also exists in Packages/manifest.json, that dependency will be removed too.",
                "Remove",
                "Cancel"))
            return;

        bool removedFolder = AssetDatabase.MoveAssetToTrash(packageRoot);
        if (!removedFolder)
        {
            EditorUtility.DisplayDialog("ActionFit Package Manager", $"Failed to remove embedded package folder:\n{packageRoot}", "OK");
            return;
        }

        string manifestPath = ManifestFullPath;
        if (File.Exists(manifestPath))
        {
            string manifest = File.ReadAllText(manifestPath);
            string updated = RemoveDependency(manifest, package.Id, out bool removedDependency);
            if (removedDependency)
            {
                File.WriteAllText(manifestPath, updated, new UTF8Encoding(false));
                UnityEngine.Debug.Log($"[ActionFitPackageManager] Removed manifest dependency: {package.Id}");
            }
        }

        UnityEngine.Debug.Log($"[ActionFitPackageManager] Removed embedded package folder: {packageRoot}");
        ActionFitPackageAiGuideRouter.EnsureProjectRouter();
        AssetDatabase.Refresh();
        Client.Resolve();
        QueueReload();
    }

    private void OnRegisteredPackages(PackageRegistrationEventArgs args)
    {
        QueueReload();
    }

    private void QueueReload()
    {
        EditorApplication.delayCall += () =>
        {
            if (this == null) return;
            Reload();
            Repaint();
        };
    }

    private PackageVersion FindVersion(string packageId, string version)
    {
        var group = _packages.FirstOrDefault(p => p.Id == packageId);
        if (group == null) return null;
        if (!string.IsNullOrWhiteSpace(version))
            return group.Versions.FirstOrDefault(v => v.Version == version);
        return group.Versions.FirstOrDefault(v => v.IsLatest) ?? group.Versions.FirstOrDefault();
    }

    private bool ApplyPackageDependencies(ref string manifest, PackageVersion version, List<string> changes)
    {
        foreach (var dep in ParseDependencies(version.Dependencies))
        {
            if (!TryResolveDependencyValue(dep, out string value, out bool shouldAbort))
            {
                if (!shouldAbort) continue;

                EditorUtility.DisplayDialog(
                    "ActionFit Package Manager",
                    $"Required dependency is missing from the catalog.\n\n{dep.Id}@{dep.Version}\n\nUpdate the catalog row before installing {version.Id}.",
                    "OK");
                return false;
            }

            manifest = SetDependency(manifest, dep.Id, value);
            changes.Add($"{dep.Id} -> {value}");
        }

        return true;
    }

    private bool TryResolveDependencyValue((string Id, string Version) dependency, out string value, out bool shouldAbort)
    {
        value = "";
        shouldAbort = false;
        string dependencyLabel = string.IsNullOrWhiteSpace(dependency.Version)
            ? dependency.Id
            : $"{dependency.Id}@{dependency.Version}";

        if (string.IsNullOrWhiteSpace(dependency.Id))
            return false;

        var depVersion = FindVersion(dependency.Id, dependency.Version);
        if (depVersion != null)
        {
            value = depVersion.PackageUrl;
            return true;
        }

        if (IsActionFitPackageId(dependency.Id))
        {
            UnityEngine.Debug.LogError($"[ActionFitPackageManager] ActionFit dependency is missing from catalog: {dependencyLabel}");
            shouldAbort = true;
            return false;
        }

        if (!string.IsNullOrWhiteSpace(dependency.Version))
        {
            value = dependency.Version;
            UnityEngine.Debug.LogWarning($"[ActionFitPackageManager] Dependency not found in catalog; writing raw dependency value: {dependencyLabel}");
            return true;
        }

        UnityEngine.Debug.LogWarning($"[ActionFitPackageManager] Dependency not found in catalog: {dependencyLabel}");
        return false;
    }

    private static bool TryFindDownloadedPackageSource(string packageId, out string sourcePath, out string error)
    {
        sourcePath = "";
        error = "";

        try
        {
            var packageInfo = UnityEditor.PackageManager.PackageInfo
                .GetAllRegisteredPackages()
                .FirstOrDefault(p => string.Equals(p.name, packageId, StringComparison.Ordinal));

            if (packageInfo != null && IsValidDownloadedPackageSource(packageId, packageInfo.resolvedPath))
            {
                sourcePath = packageInfo.resolvedPath;
                return true;
            }
        }
        catch (Exception ex)
        {
            UnityEngine.Debug.LogWarning($"[ActionFitPackageManager] Registered package lookup failed for {packageId}: {ex.Message}");
        }

        string cacheRoot = ProjectRelativeFullPath(PackageCachePath);
        if (Directory.Exists(cacheRoot))
        {
            foreach (string dir in Directory.GetDirectories(cacheRoot, packageId + "@*", SearchOption.TopDirectoryOnly)
                         .OrderByDescending(Directory.GetLastWriteTimeUtc))
            {
                if (!IsValidDownloadedPackageSource(packageId, dir)) continue;

                sourcePath = dir;
                return true;
            }
        }

        error =
            $"Downloaded package cache was not found for {packageId}.\n\n" +
            "Open the project in Unity, run Package Manager resolve/reload, then try again.";
        return false;
    }

    private static bool IsValidDownloadedPackageSource(string packageId, string path)
    {
        if (string.IsNullOrWhiteSpace(path) || !Directory.Exists(path))
            return false;

        string relativePath = ToProjectRelativePath(path).Replace("\\", "/");
        if (relativePath.StartsWith("Packages/", StringComparison.OrdinalIgnoreCase))
            return false;

        string packageJson = Path.Combine(path, "package.json");
        if (!File.Exists(packageJson))
            return false;

        string name = ExtractJsonString(File.ReadAllText(packageJson), "name");
        return string.Equals(name, packageId, StringComparison.Ordinal);
    }

    private static void CopyDirectoryForEmbeddedPackage(string source, string destination)
    {
        string fullSource = Path.GetFullPath(source).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        Directory.CreateDirectory(destination);

        foreach (string dir in Directory.GetDirectories(fullSource, "*", SearchOption.AllDirectories))
        {
            string relative = dir[(fullSource.Length + 1)..];
            if (IsGitMetadataPath(relative)) continue;

            Directory.CreateDirectory(Path.Combine(destination, relative));
        }

        foreach (string file in Directory.GetFiles(fullSource, "*", SearchOption.AllDirectories))
        {
            string relative = file[(fullSource.Length + 1)..];
            if (IsGitMetadataPath(relative)) continue;

            string target = Path.Combine(destination, relative);
            Directory.CreateDirectory(Path.GetDirectoryName(target));
            File.Copy(file, target, true);
            File.SetAttributes(target, File.GetAttributes(target) & ~FileAttributes.ReadOnly);
        }
    }

    private static bool IsGitMetadataPath(string relativePath)
    {
        string normalized = relativePath.Replace("\\", "/");
        return normalized.Equals(".git", StringComparison.OrdinalIgnoreCase) ||
               normalized.StartsWith(".git/", StringComparison.OrdinalIgnoreCase) ||
               normalized.Contains("/.git/", StringComparison.OrdinalIgnoreCase);
    }

    private static string SetDependency(string manifest, string packageId, string value)
    {
        var dependencies = ReadDependencies(manifest, out int openBrace, out int closeBrace);
        bool updated = false;
        for (int i = 0; i < dependencies.Count; i++)
        {
            if (!string.Equals(dependencies[i].Id, packageId, StringComparison.Ordinal)) continue;

            dependencies[i] = (packageId, value);
            updated = true;
            break;
        }

        if (!updated)
            dependencies.Add((packageId, value));

        return WriteDependencies(manifest, openBrace, closeBrace, dependencies);
    }

    private static string RemoveDependency(string manifest, string packageId, out bool removed)
    {
        var dependencies = ReadDependencies(manifest, out int openBrace, out int closeBrace);
        int removedCount = dependencies.RemoveAll(d => string.Equals(d.Id, packageId, StringComparison.Ordinal));
        removed = removedCount > 0;
        return removed ? WriteDependencies(manifest, openBrace, closeBrace, dependencies) : manifest;
    }

    private static List<(string Id, string Value)> ReadDependencies(string manifest, out int openBrace, out int closeBrace)
    {
        int dependenciesStart = manifest.IndexOf("\"dependencies\"", StringComparison.Ordinal);
        if (dependenciesStart < 0) throw new InvalidOperationException("manifest.json has no dependencies block.");

        openBrace = manifest.IndexOf('{', dependenciesStart);
        closeBrace = FindMatchingBrace(manifest, openBrace);
        string dependenciesBody = manifest.Substring(openBrace + 1, closeBrace - openBrace - 1);
        var dependencies = new List<(string Id, string Value)>();
        var pattern = new Regex("^\\s*\"([^\"]+)\"\\s*:\\s*\"([^\"]*)\"\\s*,?\\s*$");

        foreach (string line in dependenciesBody.Replace("\r\n", "\n").Split('\n'))
        {
            if (string.IsNullOrWhiteSpace(line)) continue;

            var match = pattern.Match(line);
            if (!match.Success) continue;

            dependencies.Add((match.Groups[1].Value, match.Groups[2].Value));
        }

        return dependencies;
    }

    private static string WriteDependencies(string manifest, int openBrace, int closeBrace, List<(string Id, string Value)> dependencies)
    {
        string newline = manifest.Contains("\r\n") ? "\r\n" : "\n";
        var sb = new StringBuilder();
        sb.Append('{');

        if (dependencies.Count > 0)
        {
            sb.Append(newline);
            for (int i = 0; i < dependencies.Count; i++)
            {
                var dependency = dependencies[i];
                string comma = i < dependencies.Count - 1 ? "," : "";
                sb.Append("    ")
                    .Append('"').Append(dependency.Id).Append("\": \"")
                    .Append(dependency.Value).Append('"').Append(comma)
                    .Append(newline);
            }

            sb.Append("  ");
        }

        sb.Append('}');
        return manifest.Substring(0, openBrace) + sb + manifest[(closeBrace + 1)..];
    }

    private InstalledPackage GetInstalledVersion(string packageId)
    {
        string manifestPath = ManifestFullPath;
        if (File.Exists(manifestPath))
        {
            string manifestValue = ExtractDependencyValue(File.ReadAllText(manifestPath), packageId);
            if (!string.IsNullOrWhiteSpace(manifestValue))
            {
                if (!IsLocalPackageDependency(manifestValue))
                {
                    string versionFromUrl = ExtractVersionFromPackageUrl(manifestValue);
                    string label = string.IsNullOrWhiteSpace(versionFromUrl) ? manifestValue : versionFromUrl;
                    return new InstalledPackage(true, false, versionFromUrl, label);
                }

                string localVersion = ExtractLocalPackageVersion(packageId, manifestValue);
                string localLabel = string.IsNullOrWhiteSpace(localVersion) ? manifestValue : $"embedded ({localVersion})";
                return new InstalledPackage(true, true, localVersion, localLabel);
            }
        }

        string embeddedPackageJson = Path.Combine(ProjectRootPath, "Packages", packageId, "package.json");
        if (File.Exists(embeddedPackageJson))
        {
            string version = ExtractJsonString(File.ReadAllText(embeddedPackageJson), "version");
            return new InstalledPackage(true, true, version, $"embedded ({version})");
        }

        return InstalledPackage.NotInstalled;
    }

    private static List<(string Id, string Version)> ParseDependencies(string raw)
    {
        var result = new List<(string Id, string Version)>();
        if (string.IsNullOrWhiteSpace(raw)) return result;

        foreach (string token in raw.Split(new[] { ';', ',', '|' }, StringSplitOptions.RemoveEmptyEntries))
        {
            string trimmed = token.Trim();
            int at = trimmed.IndexOf('@');
            result.Add(at > 0
                ? (trimmed[..at].Trim(), trimmed[(at + 1)..].Trim())
                : (trimmed, ""));
        }

        return result;
    }

    private static string ExtractDependencyValue(string manifest, string packageId)
    {
        var match = Regex.Match(manifest, $"\"{Regex.Escape(packageId)}\"\\s*:\\s*\"([^\"]*)\"");
        return match.Success ? match.Groups[1].Value : "";
    }

    private static string ExtractJsonString(string json, string key)
    {
        var match = Regex.Match(json, $"\"{Regex.Escape(key)}\"\\s*:\\s*\"([^\"]*)\"");
        return match.Success ? match.Groups[1].Value : "";
    }

    private static string ExtractVersionFromPackageUrl(string packageUrl)
    {
        int hash = packageUrl.LastIndexOf('#');
        return hash >= 0 && hash < packageUrl.Length - 1 ? packageUrl[(hash + 1)..] : "";
    }

    private static string LocalPackageDependencyValue(string packageId)
    {
        return $"file:{packageId}";
    }

    private static bool IsLocalPackageDependency(string manifestValue)
    {
        return manifestValue.Trim().StartsWith("file:", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsActionFitPackageId(string packageId)
    {
        return packageId.StartsWith("com.actionfit.", StringComparison.OrdinalIgnoreCase);
    }

    private static string ExtractLocalPackageVersion(string packageId, string manifestValue)
    {
        string value = manifestValue.Trim();
        string relative = value.StartsWith("file:", StringComparison.OrdinalIgnoreCase) ? value[5..] : value;
        string fullPath = Path.GetFullPath(Path.Combine(Path.GetDirectoryName(ManifestFullPath), relative));
        string packageJson = Path.Combine(fullPath, "package.json");
        if (!File.Exists(packageJson))
            packageJson = Path.Combine(ProjectRootPath, "Packages", packageId, "package.json");

        return File.Exists(packageJson) ? ExtractJsonString(File.ReadAllText(packageJson), "version") : "";
    }

    private static bool IsVersionNewer(string candidateVersion, string installedVersion)
    {
        if (string.IsNullOrWhiteSpace(candidateVersion)) return false;
        if (string.IsNullOrWhiteSpace(installedVersion)) return true;

        int compare = PackageVersionComparer.Instance.Compare(candidateVersion, installedVersion);
        return compare > 0;
    }

    private static bool IsSameVersion(string left, string right)
    {
        return string.Equals(left?.Trim(), right?.Trim(), StringComparison.OrdinalIgnoreCase);
    }

    private static int FindMatchingBrace(string text, int openBrace)
    {
        int depth = 0;
        for (int i = openBrace; i < text.Length; i++)
        {
            if (text[i] == '{') depth++;
            if (text[i] == '}')
            {
                depth--;
                if (depth == 0) return i;
            }
        }

        throw new InvalidOperationException("Could not find dependencies block end.");
    }

    private static string Get(string[] cols, Dictionary<string, int> index, string key)
    {
        return index.TryGetValue(key, out int i) && i < cols.Length ? cols[i].Trim() : "";
    }

    private static string GetAny(string[] cols, Dictionary<string, int> index, params string[] keys)
    {
        foreach (string key in keys)
        {
            string value = Get(cols, index, key);
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

    private static bool IsTrue(string value)
    {
        return value.Equals("true", StringComparison.OrdinalIgnoreCase) ||
               value.Equals("1", StringComparison.OrdinalIgnoreCase) ||
               value.Equals("yes", StringComparison.OrdinalIgnoreCase);
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

    private sealed class CommentPanelState
    {
        public bool Loaded;
        public bool HasMyComment;
        public List<ActionFitPackageCommunityClient.Comment> Comments = new();
        public string TitleDraft = "";
        public string BodyDraft = "";
    }

    private sealed class PackageGroup
    {
        public PackageGroup(string id, List<PackageVersion> versions)
        {
            Id = id;
            Versions = versions;
            DisplayName = versions.FirstOrDefault()?.DisplayName ?? id;
            Owner = versions.FirstOrDefault(v => !string.IsNullOrWhiteSpace(v.Owner))?.Owner ?? "ActionFit";
            LatestVersionLabel = versions.FirstOrDefault(v => v.IsLatest)?.Version ?? versions.FirstOrDefault()?.Version ?? "";
            var summarySource = versions.FirstOrDefault(v => v.IsLatest) ?? versions.FirstOrDefault();
            Likes = summarySource?.Likes ?? 0;
            Dislikes = summarySource?.Dislikes ?? 0;
            CommentCount = summarySource?.CommentCount ?? 0;
        }

        public string Id { get; }
        public string DisplayName { get; }
        public string Owner { get; }
        public string LatestVersionLabel { get; }
        public int Likes { get; }
        public int Dislikes { get; }
        public int CommentCount { get; }
        public PackageVersion LatestVersion => Versions.FirstOrDefault(v => v.IsLatest) ?? Versions.FirstOrDefault();
        public List<PackageVersion> Versions { get; }
    }

    private sealed class PackageVersionComparer : IComparer<string>
    {
        public static readonly PackageVersionComparer Instance = new();

        public int Compare(string x, string y)
        {
            var left = ParseVersion(x);
            var right = ParseVersion(y);
            for (int i = 0; i < Math.Max(left.Count, right.Count); i++)
            {
                int l = i < left.Count ? left[i] : 0;
                int r = i < right.Count ? right[i] : 0;
                int cmp = l.CompareTo(r);
                if (cmp != 0) return cmp;
            }

            return string.Compare(x, y, StringComparison.OrdinalIgnoreCase);
        }

        private static List<int> ParseVersion(string version)
        {
            var result = new List<int>();
            foreach (string part in (version ?? "").Split('.'))
            {
                string digits = new(part.TakeWhile(char.IsDigit).ToArray());
                result.Add(int.TryParse(digits, out int value) ? value : 0);
            }
            return result;
        }
    }

    private sealed class PackageVersion
    {
        public string Id;
        public string DisplayName;
        public string Owner;
        public string RepoUrl;
        public string Version;
        public string Status;
        public bool IsLatest;
        public string UnityMin;
        public string Description;
        public string Changelog;
        public string Dependencies;
        public int Likes;
        public int Dislikes;
        public int VoteScore;
        public int CommentCount;
        public string PackageUrl => $"{RepoUrl}#{Version}";
        public string VersionLabel => IsLatest ? $"{Version} ({Status}, latest)" : $"{Version} ({Status})";
    }

    private readonly struct UpdateCandidate
    {
        public UpdateCandidate(PackageGroup package, InstalledPackage installed, PackageVersion latest, bool canApply)
        {
            Package = package;
            Installed = installed;
            Latest = latest;
            CanApply = canApply;
        }

        public PackageGroup Package { get; }
        public InstalledPackage Installed { get; }
        public PackageVersion Latest { get; }
        public bool CanApply { get; }
    }

    private readonly struct InstalledPackage
    {
        public static readonly InstalledPackage NotInstalled = new(false, false, "", "not installed");

        public InstalledPackage(bool isInstalled, bool isEmbedded, string version, string label)
        {
            IsInstalled = isInstalled;
            IsEmbedded = isEmbedded;
            Version = version;
            Label = label;
        }

        public bool IsInstalled { get; }
        public bool IsEmbedded { get; }
        public string Version { get; }
        public string Label { get; }
    }
}
#endif
