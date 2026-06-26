#if UNITY_EDITOR
using UnityEngine;

public class ActionFitPackageCatalogSettings_SO : ScriptableObject
{
    [Header("Google Sheet Catalog")]
    [SerializeField] private string _spreadSheetUrl = "";
    [SerializeField] private string _webAppUrl = "";
    [SerializeField] private string _fetchToken = "";

    [Header("GitHub Publish")]
    [SerializeField] private string _githubOrg = "ActionFit-Editor";
    [SerializeField] private string _githubToken = "";
    [SerializeField] private string _publishRoot = "~/upm-publish";

    public string SpreadSheetUrl => _spreadSheetUrl;
    public string WebAppUrl => _webAppUrl;
    public string FetchToken => _fetchToken;
    public string GitHubOrg => _githubOrg;
    public string GitHubToken => _githubToken;
    public string PublishRoot => _publishRoot;
}
#endif
