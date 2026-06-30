#if UNITY_EDITOR
using UnityEngine;

public enum ActionFitPackageRepositoryVisibility
{
    Public = 0,
    Private = 1,
}

public readonly struct ActionFitPackageGitHubProfile
{
    public ActionFitPackageGitHubProfile(string organization, string token, ActionFitPackageRepositoryVisibility visibility, string label)
    {
        Organization = organization ?? "";
        Token = token ?? "";
        Visibility = visibility;
        Label = label ?? "";
    }

    public string Organization { get; }
    public string Token { get; }
    public ActionFitPackageRepositoryVisibility Visibility { get; }
    public string Label { get; }
    public bool IsPrivate => Visibility == ActionFitPackageRepositoryVisibility.Private;
}

public class ActionFitPackageCatalogSettings_SO : ScriptableObject
{
    [Header("Google Sheet Catalog")]
    [SerializeField] private string _spreadSheetUrl = "";
    [SerializeField] private string _webAppUrl = "";
    [SerializeField] private string _fetchToken = "";

    [Header("GitHub Publish Default")]
    [SerializeField] private string _githubOrg = "ActionFit-Editor";
    [SerializeField] private string _githubToken = "";
    [SerializeField] private string _publishRoot = "~/upm-publish";

    [Header("Repo Creation - Public")]
    [SerializeField] private string _publicGitHubOrg = "ActionFit-Editor";
    [SerializeField] private string _publicGitHubToken = "";

    [Header("Repo Creation - Private")]
    [SerializeField] private string _privateGitHubOrg = "ActionFit-Editor";
    [SerializeField] private string _privateGitHubToken = "";

    public string SpreadSheetUrl => _spreadSheetUrl;
    public string WebAppUrl => _webAppUrl;
    public string FetchToken => _fetchToken;
    public string GitHubOrg => _githubOrg;
    public string GitHubToken => _githubToken;
    public string PublishRoot => _publishRoot;

    public ActionFitPackageGitHubProfile DefaultGitHubProfile =>
        new(
            FirstNonEmpty(_githubOrg, "ActionFit-Editor"),
            _githubToken,
            ActionFitPackageRepositoryVisibility.Public,
            "GitHub Publish Default");

    public ActionFitPackageGitHubProfile GetRepositoryCreationProfile(ActionFitPackageRepositoryVisibility visibility)
    {
        bool isPrivate = visibility == ActionFitPackageRepositoryVisibility.Private;
        string organization = isPrivate
            ? FirstNonEmpty(_privateGitHubOrg, _githubOrg, "ActionFit-Editor")
            : FirstNonEmpty(_publicGitHubOrg, _githubOrg, "ActionFit-Editor");
        string token = isPrivate
            ? FirstNonEmpty(_privateGitHubToken, _githubToken)
            : FirstNonEmpty(_publicGitHubToken, _githubToken);
        string label = isPrivate ? "Private Repo Creation" : "Public Repo Creation";

        return new ActionFitPackageGitHubProfile(organization, token, visibility, label);
    }

    private static string FirstNonEmpty(params string[] values)
    {
        foreach (string value in values)
        {
            if (!string.IsNullOrWhiteSpace(value))
                return value.Trim();
        }

        return "";
    }
}
#endif
