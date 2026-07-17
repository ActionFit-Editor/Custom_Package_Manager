#if UNITY_EDITOR
using System;

[Serializable]
public sealed class ActionFitContentBundleProfile
{
    public int schemaVersion = 1;
    public string bundleId = "";
    public string bundleVersion = "";
    public string displayName = "";
    public string bootstrapPackageId = "";
    public ActionFitContentBundlePackageSpec[] packages = Array.Empty<ActionFitContentBundlePackageSpec>();
    public ActionFitContentBundleModuleSpec[] modules = Array.Empty<ActionFitContentBundleModuleSpec>();
    public string[] allowedReleaseGitHubLogins = Array.Empty<string>();
}

[Serializable]
public sealed class ActionFitContentBundleModuleSpec
{
    public string moduleId = "";
    public string displayName = "";
    public bool required;
    public bool defaultSelected = true;
    public string[] packageIds = Array.Empty<string>();
}

[Serializable]
public sealed class ActionFitContentBundlePackageSpec
{
    public string packageId = "";
    public string version = "";
    public string gitUrl = "";
    public bool required = true;
    public bool removeOnRelease = true;
}

public enum ActionFitContentBundleChangeKind
{
    None,
    Add,
    Update,
    Preserve,
    PreserveEmbedded,
    Remove,
    Restore,
    Conflict,
}

[Serializable]
public sealed class ActionFitContentBundleChange
{
    public string packageId = "";
    public string from = "";
    public string to = "";
    public string detail = "";
    public ActionFitContentBundleChangeKind kind;
}

[Serializable]
public sealed class ActionFitContentBundlePlan
{
    public bool success;
    public string code = "";
    public string message = "";
    public string bundleId = "";
    public string bundleVersion = "";
    public bool authorized;
    public ActionFitContentBundleChange[] changes = Array.Empty<ActionFitContentBundleChange>();
    public string[] conflicts = Array.Empty<string>();
    public string[] requiredPackageIds = Array.Empty<string>();
    public string[] selectedModuleIds = Array.Empty<string>();
    public string[] requiredModuleIds = Array.Empty<string>();
    public ActionFitContentBundleModuleSpec[] modules = Array.Empty<ActionFitContentBundleModuleSpec>();
}

[Serializable]
public sealed class ActionFitContentBundleResult
{
    public bool success;
    public bool pending;
    public bool changed;
    public bool recoveryRequired;
    public string code = "";
    public string message = "";
    public string bundleId = "";
    public string journalPath = "";
    public ActionFitContentBundlePlan plan;
}

[Serializable]
public sealed class ActionFitContentBundleStatus
{
    public string bundleId = "";
    public string bundleVersion = "";
    public string displayName = "";
    public string state = "";
    public string bootstrapPackageId = "";
    public bool bootstrapInstalled;
    public bool releaseAuthorized;
    public string[] requiredPackageIds = Array.Empty<string>();
    public string[] selectedModuleIds = Array.Empty<string>();
    public string[] requiredModuleIds = Array.Empty<string>();
    public ActionFitContentBundleModuleSpec[] modules = Array.Empty<ActionFitContentBundleModuleSpec>();
    public string[] conflicts = Array.Empty<string>();
}
#endif
