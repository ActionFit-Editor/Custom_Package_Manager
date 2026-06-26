#if UNITY_EDITOR
using UnityEngine;

public class ActionFitPackageInfo_SO : ScriptableObject
{
    [Header("Package")]
    [SerializeField] private string _packageId = "";
    [SerializeField] private string _displayName = "";
    [SerializeField] private string _repoName = "";
    [SerializeField] private string _owner = "";
    [SerializeField] private string _status = "verified";

    [Header("Catalog Text")]
    [TextArea(2, 5)]
    [SerializeField] private string _description = "";
    [TextArea(2, 5)]
    [SerializeField] private string _releaseNote = "";
    [SerializeField] private string _dependenciesOverride = "";

    public string PackageId => _packageId;
    public string DisplayName => _displayName;
    public string RepoName => _repoName;
    public string Owner => _owner;
    public string Status => _status;
    public string Description => _description;
    public string ReleaseNote => _releaseNote;
    public string DependenciesOverride => _dependenciesOverride;
}
#endif
