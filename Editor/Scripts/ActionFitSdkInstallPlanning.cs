#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using UnityEngine;

public enum ActionFitSdkInstallOperation
{
    Apply = 0,
    Repair = 1,
    Update = 2,
    Remove = 3,
}

public enum ActionFitSdkPlannedChangeArea
{
    Dependency = 0,
    ScopedRegistry = 1,
    ArtifactCache = 2,
    Ownership = 3,
    UnityAsset = 4,
}

public enum ActionFitSdkPlannedChangeAction
{
    Add = 0,
    Adopt = 1,
    Update = 2,
    Preserve = 3,
    Remove = 4,
    Download = 5,
    Reuse = 6,
}

/// <summary>Controls module selection and compatible-install adoption for one read-only plan.</summary>
[Serializable]
public sealed class ActionFitSdkPlanRequest
{
    public ActionFitSdkInstallOperation Operation = ActionFitSdkInstallOperation.Apply;
    public string[] SelectedModuleIds = Array.Empty<string>();
    public bool AdoptCompatible = true;
    public bool TakeOwnershipOfCompatibleEntries;
}

/// <summary>Defines the bounded project paths used by inspection, planning, and execution.</summary>
[Serializable]
public sealed class ActionFitSdkProjectContext
{
    public string ProjectRoot = "";
    public string ManifestPath = "";
    public string PackagesLockPath = "";
    public string OwnershipPath = "";
    public string TransactionRoot = "";

    /// <summary>Creates a context for the currently open Unity project.</summary>
    public static ActionFitSdkProjectContext ForCurrentProject()
    {
        return ForProjectRoot(ActionFitPackagePaths.ProjectRoot);
    }

    /// <summary>Creates a bounded context for an explicit project root, including isolated test projects.</summary>
    public static ActionFitSdkProjectContext ForProjectRoot(string projectRoot)
    {
        if (string.IsNullOrWhiteSpace(projectRoot))
            throw new ArgumentException("Project root is required.", nameof(projectRoot));

        string root = Path.GetFullPath(projectRoot).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        return new ActionFitSdkProjectContext
        {
            ProjectRoot = root,
            ManifestPath = Path.Combine(root, "Packages", "manifest.json"),
            PackagesLockPath = Path.Combine(root, "Packages", "packages-lock.json"),
            OwnershipPath = Path.Combine(root, "ProjectSettings", "ActionFitSdkProfiles.json"),
            TransactionRoot = Path.Combine(root, "UserSettings", "ActionFitPackageManager", "SdkTransactions"),
        };
    }

    internal void Validate()
    {
        string root = Normalize(ProjectRoot);
        EnsureChild(ManifestPath, Path.Combine(root, "Packages"), "manifest");
        EnsureChild(PackagesLockPath, Path.Combine(root, "Packages"), "packages lock");
        EnsureChild(OwnershipPath, Path.Combine(root, "ProjectSettings"), "ownership");
        EnsureChild(TransactionRoot, Path.Combine(root, "UserSettings"), "transaction");
    }

    internal string ResolveProjectPath(string relativePath)
    {
        string resolved = Normalize(Path.Combine(ProjectRoot, (relativePath ?? "").Replace('/', Path.DirectorySeparatorChar)));
        EnsureChild(resolved, ProjectRoot, "project");
        return resolved;
    }

    private static void EnsureChild(string path, string root, string label)
    {
        string fullPath = Normalize(path);
        string fullRoot = Normalize(root);
        if (!fullPath.StartsWith(fullRoot + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException($"SDK {label} path is outside its allowed root: {fullPath}");
    }

    private static string Normalize(string path)
    {
        return Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
    }
}

[Serializable]
public sealed class ActionFitSdkInstallationFinding
{
    public string RuleId = "";
    public ActionFitSdkInstallationClassification Classification;
    public string Kind = "";
    public string Value = "";
    public string Message = "";
}

[Serializable]
public sealed class ActionFitSdkPlannedChange
{
    public ActionFitSdkPlannedChangeArea Area;
    public ActionFitSdkPlannedChangeAction Action;
    public string Key = "";
    public string Before = "";
    public string After = "";
    public bool OwnedByProfile;
}

[Serializable]
public sealed class ActionFitSdkInspectionResult
{
    public bool Success;
    public string Code = "";
    public string Message = "";
    public string ProfileId = "";
    public string ProfileVersion = "";
    public string[] SelectedModuleIds = Array.Empty<string>();
    public ActionFitSdkProfileDiagnostic[] ValidationDiagnostics = Array.Empty<ActionFitSdkProfileDiagnostic>();
    public ActionFitSdkInstallationFinding[] Findings = Array.Empty<ActionFitSdkInstallationFinding>();
}

[Serializable]
public sealed class ActionFitSdkInstallPlan
{
    public bool Success;
    public string Code = "";
    public string Message = "";
    public string PlanId = "";
    public string ProfileId = "";
    public string ProfileVersion = "";
    public ActionFitSdkInstallOperation Operation;
    public string[] SelectedModuleIds = Array.Empty<string>();
    public ActionFitSdkProfileDiagnostic[] ValidationDiagnostics = Array.Empty<ActionFitSdkProfileDiagnostic>();
    public ActionFitSdkInstallationFinding[] Findings = Array.Empty<ActionFitSdkInstallationFinding>();
    public ActionFitSdkPlannedChange[] Changes = Array.Empty<ActionFitSdkPlannedChange>();
    public ActionFitSdkResolutionSnapshot ResolutionSnapshot;

    internal ActionFitSdkInstallProfile Profile;
    internal ActionFitSdkProjectContext Context;
    internal string ProfileSnapshot = "";
    internal string OriginalManifest = "";
    internal string UpdatedManifest = "";
    internal string OriginalOwnership = "";
    internal string UpdatedOwnership = "";
    internal string OriginalManifestHash = "";
    internal string OriginalOwnershipHash = "";
    internal bool OriginalOwnershipExisted;
    internal string PreparedPlanId = "";
    internal ActionFitSdkArtifactPlan[] ArtifactPlans = Array.Empty<ActionFitSdkArtifactPlan>();
    internal ActionFitSdkAssetPlan[] AssetPlans = Array.Empty<ActionFitSdkAssetPlan>();
}

[Serializable]
public sealed class ActionFitSdkScopedRegistryValue
{
    public string Name = "";
    public string Url = "";
    public string[] Scopes = Array.Empty<string>();

    internal ActionFitSdkScopedRegistryValue Clone()
    {
        return new ActionFitSdkScopedRegistryValue
        {
            Name = Name,
            Url = Url,
            Scopes = (Scopes ?? Array.Empty<string>()).ToArray(),
        };
    }
}

[Serializable]
public sealed class ActionFitSdkOwnershipDocument
{
    public const int CurrentSchemaVersion = 1;

    public int SchemaVersion = CurrentSchemaVersion;
    public ActionFitSdkProfileOwnership[] Profiles = Array.Empty<ActionFitSdkProfileOwnership>();

    internal void Normalize()
    {
        Profiles ??= Array.Empty<ActionFitSdkProfileOwnership>();
        foreach (ActionFitSdkProfileOwnership profile in Profiles)
            profile?.Normalize();
    }
}

[Serializable]
public sealed class ActionFitSdkProfileOwnership
{
    public string ProfileId = "";
    public string ProfileVersion = "";
    public string[] SelectedModuleIds = Array.Empty<string>();
    public ActionFitSdkDependencyOwnership[] Dependencies = Array.Empty<ActionFitSdkDependencyOwnership>();
    public ActionFitSdkRegistryOwnership[] ScopedRegistries = Array.Empty<ActionFitSdkRegistryOwnership>();
    public ActionFitSdkArtifactOwnership[] Artifacts = Array.Empty<ActionFitSdkArtifactOwnership>();
    public ActionFitSdkAssetOwnership[] Assets = Array.Empty<ActionFitSdkAssetOwnership>();

    internal void Normalize()
    {
        SelectedModuleIds ??= Array.Empty<string>();
        Dependencies ??= Array.Empty<ActionFitSdkDependencyOwnership>();
        ScopedRegistries ??= Array.Empty<ActionFitSdkRegistryOwnership>();
        Artifacts ??= Array.Empty<ActionFitSdkArtifactOwnership>();
        Assets ??= Array.Empty<ActionFitSdkAssetOwnership>();
        foreach (ActionFitSdkRegistryOwnership registry in ScopedRegistries)
            registry?.Normalize();
    }
}

[Serializable]
public sealed class ActionFitSdkDependencyOwnership
{
    public string PackageId = "";
    public string Value = "";
    public bool CreatedByProfile;
}

[Serializable]
public sealed class ActionFitSdkRegistryOwnership
{
    public string Name = "";
    public string Url = "";
    public bool RegistryCreatedByProfile;
    public string[] RequiredScopes = Array.Empty<string>();
    public string[] OwnedScopes = Array.Empty<string>();

    internal void Normalize()
    {
        RequiredScopes ??= Array.Empty<string>();
        OwnedScopes ??= Array.Empty<string>();
    }
}

[Serializable]
public sealed class ActionFitSdkArtifactOwnership
{
    public string ProjectRelativePath = "";
    public string Sha256 = "";
    public bool CreatedByProfile;
}

/// <summary>
/// Ownership of one Unity Asset installed or adopted from a unitypackageArtifact source.
/// Ownership binds to the asset GUID and content hash. The .meta payload is intentionally
/// not hashed because dependency resolvers normalize importer labels in place.
/// </summary>
[Serializable]
public sealed class ActionFitSdkAssetOwnership
{
    public string ProjectRelativePath = "";
    public string Guid = "";
    public string Sha256 = "";
    public string State = "";
    public bool CreatedByProfile;

    public ActionFitSdkAssetOwnershipState ResolveState()
    {
        if (string.Equals(State, "created", StringComparison.OrdinalIgnoreCase))
            return ActionFitSdkAssetOwnershipState.Created;
        if (string.Equals(State, "adopted", StringComparison.OrdinalIgnoreCase))
            return ActionFitSdkAssetOwnershipState.Adopted;
        if (string.Equals(State, "shared", StringComparison.OrdinalIgnoreCase))
            return ActionFitSdkAssetOwnershipState.Shared;
        if (string.Equals(State, "generated", StringComparison.OrdinalIgnoreCase))
            return ActionFitSdkAssetOwnershipState.Generated;
        if (string.Equals(State, "projectOwned", StringComparison.OrdinalIgnoreCase))
            return ActionFitSdkAssetOwnershipState.ProjectOwned;
        return ActionFitSdkAssetOwnershipState.Unknown;
    }

    internal static string StateName(ActionFitSdkAssetOwnershipState state)
    {
        return state switch
        {
            ActionFitSdkAssetOwnershipState.Created => "created",
            ActionFitSdkAssetOwnershipState.Adopted => "adopted",
            ActionFitSdkAssetOwnershipState.Shared => "shared",
            ActionFitSdkAssetOwnershipState.Generated => "generated",
            ActionFitSdkAssetOwnershipState.ProjectOwned => "projectOwned",
            _ => "",
        };
    }
}

/// <summary>One planned Unity Asset operation bound to exact current and expected content.</summary>
[Serializable]
internal sealed class ActionFitSdkAssetPlan
{
    public string SourceId = "";
    public string ProjectRelativePath = "";
    public string TargetPath = "";
    public string Guid = "";
    public string ExpectedSha256 = "";
    public string CurrentSha256 = "";
    public bool IsFolder;
    public bool WriteContent;
    public bool Remove;
    public ActionFitSdkAssetOwnershipState State = ActionFitSdkAssetOwnershipState.Unknown;
    public bool CreatedByProfile;
}

[Serializable]
internal sealed class ActionFitSdkArtifactPlan
{
    public string SourceId = "";
    public string Url = "";
    public string PackageId = "";
    public string PackageVersion = "";
    public string Sha256 = "";
    public string ProjectRelativePath = "";
    public string TargetPath = "";
    public bool DownloadRequired;
    public bool Remove;
    public bool CreatedByProfile;
}

internal static class ActionFitSdkOwnershipStore
{
    public static ActionFitSdkOwnershipDocument Read(ActionFitSdkProjectContext context, out string json)
    {
        context.Validate();
        if (!File.Exists(context.OwnershipPath))
        {
            var empty = new ActionFitSdkOwnershipDocument();
            empty.Normalize();
            json = ToJson(empty);
            return empty;
        }

        json = File.ReadAllText(context.OwnershipPath);
        ActionFitSdkOwnershipDocument document;
        try
        {
            document = JsonUtility.FromJson<ActionFitSdkOwnershipDocument>(json);
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException($"SDK ownership state is invalid: {ex.Message}", ex);
        }

        if (document == null || document.SchemaVersion != ActionFitSdkOwnershipDocument.CurrentSchemaVersion)
            throw new InvalidOperationException("SDK ownership state has an unsupported schema version.");
        document.Normalize();
        return document;
    }

    public static string ToJson(ActionFitSdkOwnershipDocument document)
    {
        document ??= new ActionFitSdkOwnershipDocument();
        document.Normalize();
        document.Profiles = document.Profiles
            .Where(item => item != null)
            .OrderBy(item => item.ProfileId, StringComparer.Ordinal)
            .ToArray();
        return JsonUtility.ToJson(document, true) + "\n";
    }
}

/// <summary>Builds content-bound, read-only SDK installation plans.</summary>
public static class ActionFitSdkInstallPlanner
{
    /// <summary>Inspects the current project for declared compatible, conflicting, or unsupported installations.</summary>
    public static ActionFitSdkInspectionResult Inspect(
        ActionFitSdkInstallProfile profile,
        string[] selectedModuleIds = null,
        ActionFitSdkProjectContext context = null)
    {
        context ??= ActionFitSdkProjectContext.ForCurrentProject();
        ActionFitSdkProfileValidationResult validation = ActionFitSdkInstallProfileValidator.Validate(profile);
        if (!validation.Success)
        {
            return new ActionFitSdkInspectionResult
            {
                Success = false,
                Code = "PROFILE_INVALID",
                Message = validation.FormatMessage(),
                ProfileId = profile?.ProfileId ?? "",
                ProfileVersion = profile?.ProfileVersion ?? "",
                ValidationDiagnostics = validation.Diagnostics,
            };
        }

        try
        {
            context.Validate();
            string manifest = ReadManifest(context);
            string[] modules = ResolveModules(profile, selectedModuleIds);
            ActionFitSdkInstallationFinding[] findings = InspectFindings(profile, modules, context, manifest);
            return new ActionFitSdkInspectionResult
            {
                Success = true,
                Code = "INSPECTED",
                Message = $"Inspected {profile.ProfileId} without changing project state.",
                ProfileId = profile.ProfileId,
                ProfileVersion = profile.ProfileVersion,
                SelectedModuleIds = modules,
                Findings = findings,
            };
        }
        catch (Exception ex)
        {
            return new ActionFitSdkInspectionResult
            {
                Success = false,
                Code = "INSPECTION_FAILED",
                Message = ex.Message,
                ProfileId = profile.ProfileId,
                ProfileVersion = profile.ProfileVersion,
                ValidationDiagnostics = validation.Diagnostics,
            };
        }
    }

    /// <summary>Produces the exact dependency, registry, artifact, and ownership changes for explicit review.</summary>
    public static ActionFitSdkInstallPlan Plan(
        ActionFitSdkInstallProfile profile,
        ActionFitSdkPlanRequest request,
        ActionFitSdkProjectContext context = null)
    {
        context ??= ActionFitSdkProjectContext.ForCurrentProject();
        request ??= new ActionFitSdkPlanRequest();
        if (profile != null && profile.RequiresAsyncResolution())
        {
            return new ActionFitSdkInstallPlan
            {
                Success = false,
                Code = "ASYNC_RESOLUTION_REQUIRED",
                Message = "AnyInstalledElseLatestStable profiles require PreparePlanAsync so installed state and official metadata can be resolved first.",
                ProfileId = profile.ProfileId,
                ProfileVersion = profile.ProfileVersion,
                Operation = request.Operation,
            };
        }
        return PlanInternal(profile, request, context, null);
    }

    internal static ActionFitSdkInstallPlan PlanResolved(
        ActionFitSdkInstallProfile profile,
        ActionFitSdkPlanRequest request,
        ActionFitSdkProjectContext context,
        ActionFitSdkResolutionSnapshot resolutionSnapshot)
    {
        return PlanInternal(profile, request, context, resolutionSnapshot);
    }

    private static ActionFitSdkInstallPlan PlanInternal(
        ActionFitSdkInstallProfile profile,
        ActionFitSdkPlanRequest request,
        ActionFitSdkProjectContext context,
        ActionFitSdkResolutionSnapshot resolutionSnapshot)
    {
        context ??= ActionFitSdkProjectContext.ForCurrentProject();
        request ??= new ActionFitSdkPlanRequest();
        if (!Enum.IsDefined(typeof(ActionFitSdkInstallOperation), request.Operation))
        {
            return new ActionFitSdkInstallPlan
            {
                Success = false,
                Code = "OPERATION_INVALID",
                Message = $"Unsupported SDK install operation value: {(int)request.Operation}",
                ProfileId = profile?.ProfileId ?? "",
                ProfileVersion = profile?.ProfileVersion ?? "",
                Operation = request.Operation,
            };
        }
        ActionFitSdkProfileValidationResult validation = ActionFitSdkInstallProfileValidator.Validate(profile);
        if (!validation.Success)
            return InvalidPlan(profile, request, validation);

        try
        {
            context.Validate();
            string manifest = ReadManifest(context);
            bool ownershipExisted = File.Exists(context.OwnershipPath);
            ActionFitSdkOwnershipDocument ownership = ActionFitSdkOwnershipStore.Read(context, out string ownershipJson);
            string[] modules = ResolveModules(profile, request.SelectedModuleIds);
            ActionFitSdkInstallationFinding[] findings = InspectFindings(profile, modules, context, manifest);
            ActionFitSdkInstallPlan plan = BuildPlan(profile, request, context, manifest, ownershipJson, ownership, modules, findings, resolutionSnapshot);
            plan.OriginalOwnershipExisted = ownershipExisted;
            return plan;
        }
        catch (Exception ex)
        {
            return new ActionFitSdkInstallPlan
            {
                Success = false,
                Code = "PLAN_FAILED",
                Message = ex.Message,
                ProfileId = profile.ProfileId,
                ProfileVersion = profile.ProfileVersion,
                Operation = request.Operation,
                ValidationDiagnostics = validation.Diagnostics,
            };
        }
    }

    private static ActionFitSdkInstallPlan BuildPlan(
        ActionFitSdkInstallProfile profile,
        ActionFitSdkPlanRequest request,
        ActionFitSdkProjectContext context,
        string manifest,
        string ownershipJson,
        ActionFitSdkOwnershipDocument ownership,
        string[] modules,
        ActionFitSdkInstallationFinding[] initialFindings,
        ActionFitSdkResolutionSnapshot resolutionSnapshot)
    {
        var changes = new List<ActionFitSdkPlannedChange>();
        var findings = new List<ActionFitSdkInstallationFinding>(initialFindings);
        var artifactPlans = new List<ActionFitSdkArtifactPlan>();
        var assetPlans = new List<ActionFitSdkAssetPlan>();
        string updatedManifest = manifest;
        ActionFitSdkProfileOwnership previous = ownership.Profiles.FirstOrDefault(item =>
            item != null && string.Equals(item.ProfileId, profile.ProfileId, StringComparison.Ordinal));
        var selectedModules = new HashSet<string>(modules, StringComparer.Ordinal);
        var installedSourceIds = new HashSet<string>(
            resolutionSnapshot?.Sources?
                .Where(item => item != null && item.Origin == ActionFitSdkResolutionOrigin.Installed)
                .Select(item => item.SourceId) ?? Array.Empty<string>(),
            StringComparer.Ordinal);
        ActionFitSdkDependencyDefinition[] selectedDependencies = profile.Dependencies
            .Where(item => item != null && IsSelected(item.ModuleId, selectedModules))
            .ToArray();
        bool preserveResolvedInstallation = request.Operation == ActionFitSdkInstallOperation.Apply &&
            selectedDependencies.Length > 0 &&
            selectedDependencies.All(item => installedSourceIds.Contains(item.SourceId));

        ActionFitSdkProfileOwnership next = request.Operation == ActionFitSdkInstallOperation.Remove
            ? null
            : preserveResolvedInstallation
                ? previous
                : new ActionFitSdkProfileOwnership
                {
                    ProfileId = profile.ProfileId,
                    ProfileVersion = profile.ProfileVersion,
                    SelectedModuleIds = modules,
                };

        if ((request.Operation == ActionFitSdkInstallOperation.Repair || request.Operation == ActionFitSdkInstallOperation.Update) && previous == null)
        {
            findings.Add(Finding("ownership-required", ActionFitSdkInstallationClassification.Unsupported, "ownership", profile.ProfileId, $"{request.Operation} requires existing profile ownership. Use Apply for a new installation."));
        }
        else if (request.Operation == ActionFitSdkInstallOperation.Repair && previous != null &&
                 !string.Equals(previous.ProfileVersion, profile.ProfileVersion, StringComparison.Ordinal))
        {
            findings.Add(Finding("repair-version-mismatch", ActionFitSdkInstallationClassification.Unsupported, "ownership", previous.ProfileVersion, "Repair requires the installed profile version. Use Update with a newer profile version."));
        }
        else if (request.Operation == ActionFitSdkInstallOperation.Update && previous != null &&
                 string.Equals(previous.ProfileVersion, profile.ProfileVersion, StringComparison.Ordinal))
        {
            findings.Add(Finding("update-version-unchanged", ActionFitSdkInstallationClassification.Unsupported, "ownership", previous.ProfileVersion, "Update requires a different versioned profile. Use Repair for the installed profile version."));
        }

        if (request.Operation == ActionFitSdkInstallOperation.Remove)
        {
            if (previous == null)
            {
                findings.Add(Finding("ownership", ActionFitSdkInstallationClassification.Unsupported, "ownership", profile.ProfileId, "Profile has no ownership state to remove."));
            }
            else
            {
                ApplyRemoval(profile, previous, ownership, context, ref updatedManifest, changes, findings, artifactPlans, assetPlans);
            }
        }
        else if (preserveResolvedInstallation)
        {
            foreach (ActionFitSdkResolvedSourceSnapshot source in resolutionSnapshot.Sources)
            {
                findings.Add(Finding(
                    "dependency-installed-preserved",
                    ActionFitSdkInstallationClassification.Adoptable,
                    "dependency",
                    source.PackageId,
                    $"Resolved SDK package {source.PackageId} @ {source.Version} is preserved without dependency or ownership changes."));
            }
        }
        else
        {
            ApplyDesiredState(profile, request, previous, ownership, modules, context, resolutionSnapshot, ref updatedManifest, next, changes, findings, artifactPlans, assetPlans);
        }

        ActionFitSdkOwnershipDocument updatedOwnership = ReplaceOwnership(ownership, profile.ProfileId, next);
        string updatedOwnershipJson = ActionFitSdkOwnershipStore.ToJson(updatedOwnership);
        bool blocked = findings.Any(item =>
            item.Classification == ActionFitSdkInstallationClassification.Conflicting ||
            item.Classification == ActionFitSdkInstallationClassification.Unsupported);

        string profileSnapshot = profile.ToJson();
        var plan = new ActionFitSdkInstallPlan
        {
            Success = !blocked,
            Code = blocked ? "CONFLICT" : changes.Count == 0 ? "NO_CHANGES" : "READY",
            Message = blocked
                ? "SDK install plan is blocked by conflicting or unsupported project state."
                : changes.Count == 0
                    ? "SDK install profile already matches the reviewed project state."
                    : "SDK install plan is ready for explicit execution.",
            ProfileId = profile.ProfileId,
            ProfileVersion = profile.ProfileVersion,
            Operation = request.Operation,
            SelectedModuleIds = modules,
            Findings = findings.ToArray(),
            Changes = changes.ToArray(),
            ResolutionSnapshot = resolutionSnapshot,
            Profile = profile,
            ProfileSnapshot = profileSnapshot,
            Context = context,
            OriginalManifest = manifest,
            UpdatedManifest = updatedManifest,
            OriginalOwnership = ownershipJson,
            UpdatedOwnership = updatedOwnershipJson,
            OriginalManifestHash = Hash(manifest),
            OriginalOwnershipHash = Hash(ownershipJson),
            ArtifactPlans = artifactPlans.ToArray(),
            AssetPlans = assetPlans.ToArray(),
        };
        plan.PlanId = ComputePlanId(plan);
        plan.PreparedPlanId = plan.PlanId;
        return plan;
    }

    private static void ApplyDesiredState(
        ActionFitSdkInstallProfile profile,
        ActionFitSdkPlanRequest request,
        ActionFitSdkProfileOwnership previous,
        ActionFitSdkOwnershipDocument ownership,
        string[] modules,
        ActionFitSdkProjectContext context,
        ActionFitSdkResolutionSnapshot resolutionSnapshot,
        ref string manifest,
        ActionFitSdkProfileOwnership next,
        List<ActionFitSdkPlannedChange> changes,
        List<ActionFitSdkInstallationFinding> findings,
        List<ActionFitSdkArtifactPlan> artifactPlans,
        List<ActionFitSdkAssetPlan> assetPlans)
    {
        var selected = new HashSet<string>(modules, StringComparer.Ordinal);
        ActionFitSdkDependencyDefinition[] desiredDependencies = profile.Dependencies
            .Where(item => item != null && IsSelected(item.ModuleId, selected))
            .OrderBy(item => item.Order)
            .ToArray();
        var nextDependencyOwnership = new List<ActionFitSdkDependencyOwnership>();

        foreach (ActionFitSdkDependencyDefinition dependency in desiredDependencies)
        {
            ActionFitSdkSourceDefinition source = profile.Sources.First(item =>
                item != null && string.Equals(item.Id, dependency.SourceId, StringComparison.Ordinal));
            ActionFitSdkResolvedSourceSnapshot resolvedSource = resolutionSnapshot?.Sources.FirstOrDefault(item =>
                item != null && string.Equals(item.SourceId, source.Id, StringComparison.Ordinal));
            string desiredValue = resolvedSource?.DependencyValue ?? ResolveDependencyValue(source, context);
            string currentValue = ActionFitPackageManifestUtility.GetDependency(manifest, dependency.PackageId);
            ActionFitSdkDependencyOwnership prior = previous?.Dependencies.FirstOrDefault(item =>
                item != null && string.Equals(item.PackageId, dependency.PackageId, StringComparison.Ordinal));
            bool createdByProfile;

            if (string.IsNullOrEmpty(currentValue))
            {
                createdByProfile = true;
                manifest = ActionFitPackageManifestUtility.SetDependency(manifest, dependency.PackageId, desiredValue);
                changes.Add(Change(ActionFitSdkPlannedChangeArea.Dependency, ActionFitSdkPlannedChangeAction.Add, dependency.PackageId, "", desiredValue, true));
            }
            else if (resolvedSource != null && resolvedSource.Origin == ActionFitSdkResolutionOrigin.Installed)
            {
                desiredValue = currentValue;
                if (prior != null)
                    createdByProfile = prior.CreatedByProfile;
                else if (request.AdoptCompatible)
                    createdByProfile = request.TakeOwnershipOfCompatibleEntries;
                else
                {
                    findings.Add(Finding("dependency-adoption", ActionFitSdkInstallationClassification.Adoptable, "dependency", dependency.PackageId, "Installed dependency exists and adoption was not selected."));
                    continue;
                }
                changes.Add(Change(
                    ActionFitSdkPlannedChangeArea.Dependency,
                    prior == null ? ActionFitSdkPlannedChangeAction.Adopt : ActionFitSdkPlannedChangeAction.Preserve,
                    dependency.PackageId,
                    currentValue,
                    currentValue,
                    createdByProfile));
            }
            else if (string.Equals(currentValue, desiredValue, StringComparison.Ordinal))
            {
                if (prior != null)
                    createdByProfile = prior.CreatedByProfile;
                else if (request.AdoptCompatible)
                    createdByProfile = request.TakeOwnershipOfCompatibleEntries;
                else
                {
                    findings.Add(Finding("dependency-adoption", ActionFitSdkInstallationClassification.Adoptable, "dependency", dependency.PackageId, "Compatible dependency exists and adoption was not selected."));
                    continue;
                }

                changes.Add(Change(
                    ActionFitSdkPlannedChangeArea.Dependency,
                    prior == null ? ActionFitSdkPlannedChangeAction.Adopt : ActionFitSdkPlannedChangeAction.Preserve,
                    dependency.PackageId,
                    currentValue,
                    desiredValue,
                    createdByProfile));
            }
            else if (prior != null && prior.CreatedByProfile && string.Equals(currentValue, prior.Value, StringComparison.Ordinal))
            {
                createdByProfile = true;
                manifest = ActionFitPackageManifestUtility.SetDependency(manifest, dependency.PackageId, desiredValue);
                changes.Add(Change(ActionFitSdkPlannedChangeArea.Dependency, ActionFitSdkPlannedChangeAction.Update, dependency.PackageId, currentValue, desiredValue, true));
            }
            else
            {
                findings.Add(Finding("dependency-conflict", ActionFitSdkInstallationClassification.Conflicting, "dependency", dependency.PackageId, $"Current value does not match the requested or previously owned value: {ActionFitSdkValueRedactor.Redact(currentValue)}"));
                continue;
            }

            nextDependencyOwnership.Add(new ActionFitSdkDependencyOwnership
            {
                PackageId = dependency.PackageId,
                Value = desiredValue,
                CreatedByProfile = createdByProfile,
            });

            if (resolvedSource == null || resolvedSource.Origin != ActionFitSdkResolutionOrigin.Installed)
                AddArtifactPlan(source, createdByProfile, request.TakeOwnershipOfCompatibleEntries, context, changes, findings, artifactPlans);
        }

        RemoveObsoleteDependencies(previous, nextDependencyOwnership, ownership, ref manifest, changes, findings);
        next.Dependencies = nextDependencyOwnership.OrderBy(item => item.PackageId, StringComparer.Ordinal).ToArray();

        List<ActionFitSdkScopedRegistryValue> registryValues = ActionFitPackageManifestUtility.ReadScopedRegistries(manifest).Select(item => item.Clone()).ToList();
        var nextRegistryOwnership = new List<ActionFitSdkRegistryOwnership>();
        foreach (ActionFitSdkScopedRegistryDefinition desired in profile.ScopedRegistries.Where(item => item != null && IsSelected(item.ModuleId, selected)))
        {
            string url = NormalizeRegistryUrl(desired.Url);
            bool supportsAdoptedDependency = nextDependencyOwnership.Any(dependency => !dependency.CreatedByProfile &&
                RegistryDependencyUses(profile, dependency.PackageId, url));
            ActionFitSdkScopedRegistryValue current = registryValues.FirstOrDefault(item => RegistryUrlEquals(item.Url, url));
            ActionFitSdkRegistryOwnership prior = previous?.ScopedRegistries.FirstOrDefault(item => item != null && RegistryUrlEquals(item.Url, url));
            bool registryCreated = prior?.RegistryCreatedByProfile ?? (current == null && !supportsAdoptedDependency);
            if (current == null)
            {
                current = new ActionFitSdkScopedRegistryValue { Name = desired.Name, Url = url, Scopes = Array.Empty<string>() };
                registryValues.Add(current);
                changes.Add(Change(ActionFitSdkPlannedChangeArea.ScopedRegistry, ActionFitSdkPlannedChangeAction.Add, url, "", desired.Name, true));
            }

            var scopes = new HashSet<string>(current.Scopes ?? Array.Empty<string>(), StringComparer.Ordinal);
            var ownedScopes = new HashSet<string>(prior?.OwnedScopes ?? Array.Empty<string>(), StringComparer.Ordinal);
            foreach (string scope in desired.Scopes.OrderBy(item => item, StringComparer.Ordinal))
            {
                ActionFitSdkScopedRegistryValue scopeConflict = registryValues.FirstOrDefault(item =>
                    !RegistryUrlEquals(item.Url, url) && (item.Scopes ?? Array.Empty<string>()).Contains(scope));
                if (scopeConflict != null)
                {
                    findings.Add(Finding("registry-scope-conflict", ActionFitSdkInstallationClassification.Conflicting, "registry", scope, $"Scope {scope} is already assigned to {scopeConflict.Url}."));
                    continue;
                }
                if (scopes.Add(scope))
                {
                    if (!supportsAdoptedDependency) ownedScopes.Add(scope);
                    changes.Add(Change(ActionFitSdkPlannedChangeArea.ScopedRegistry, ActionFitSdkPlannedChangeAction.Add, url + "#" + scope, "", scope, !supportsAdoptedDependency));
                }
                else if (prior == null && request.TakeOwnershipOfCompatibleEntries)
                {
                    ownedScopes.Add(scope);
                    changes.Add(Change(ActionFitSdkPlannedChangeArea.ScopedRegistry, ActionFitSdkPlannedChangeAction.Adopt, url + "#" + scope, scope, scope, true));
                }
                else
                {
                    changes.Add(Change(ActionFitSdkPlannedChangeArea.ScopedRegistry, prior == null ? ActionFitSdkPlannedChangeAction.Adopt : ActionFitSdkPlannedChangeAction.Preserve, url + "#" + scope, scope, scope, ownedScopes.Contains(scope)));
                }
            }

            current.Scopes = scopes.OrderBy(item => item, StringComparer.Ordinal).ToArray();
            nextRegistryOwnership.Add(new ActionFitSdkRegistryOwnership
            {
                Name = current.Name,
                Url = url,
                RegistryCreatedByProfile = registryCreated,
                RequiredScopes = desired.Scopes.OrderBy(item => item, StringComparer.Ordinal).ToArray(),
                OwnedScopes = ownedScopes.OrderBy(item => item, StringComparer.Ordinal).ToArray(),
            });
        }

        RemoveObsoleteRegistries(previous, nextRegistryOwnership, ownership, registryValues, changes, findings);
        manifest = ActionFitPackageManifestUtility.SetScopedRegistries(manifest, registryValues);
        next.ScopedRegistries = nextRegistryOwnership.OrderBy(item => item.Url, StringComparer.OrdinalIgnoreCase).ToArray();

        var nextArtifactOwnership = new List<ActionFitSdkArtifactOwnership>();
        foreach (ActionFitSdkArtifactPlan artifact in artifactPlans.Where(item => !item.Remove))
        {
            ActionFitSdkArtifactOwnership prior = previous?.Artifacts.FirstOrDefault(item => item != null &&
                string.Equals(NormalizeRelativePath(item.ProjectRelativePath), NormalizeRelativePath(artifact.ProjectRelativePath), StringComparison.OrdinalIgnoreCase));
            nextArtifactOwnership.Add(new ActionFitSdkArtifactOwnership
            {
                ProjectRelativePath = artifact.ProjectRelativePath,
                Sha256 = artifact.Sha256,
                CreatedByProfile = prior?.CreatedByProfile ?? artifact.CreatedByProfile,
            });
        }

        AddObsoleteArtifactRemovals(previous, nextArtifactOwnership, ownership, context, changes, findings, artifactPlans);
        next.Artifacts = nextArtifactOwnership.OrderBy(item => item.ProjectRelativePath, StringComparer.OrdinalIgnoreCase).ToArray();

        // Unity Asset sources are installed into Assets/ rather than resolved as UPM dependencies,
        // so they are planned directly from the profile sources.
        var nextAssetOwnership = new List<ActionFitSdkAssetOwnership>();
        foreach (ActionFitSdkSourceDefinition source in profile.Sources.Where(item =>
                     item != null && item.ResolveKind() == ActionFitSdkSourceKind.UnityPackageArtifact))
        {
            AddUnityPackageAssetPlans(source, context, changes, findings, assetPlans, nextAssetOwnership);
        }

        next.Assets = nextAssetOwnership.OrderBy(item => item.ProjectRelativePath, StringComparer.OrdinalIgnoreCase).ToArray();
    }

    private static void ApplyRemoval(
        ActionFitSdkInstallProfile profile,
        ActionFitSdkProfileOwnership previous,
        ActionFitSdkOwnershipDocument ownership,
        ActionFitSdkProjectContext context,
        ref string manifest,
        List<ActionFitSdkPlannedChange> changes,
        List<ActionFitSdkInstallationFinding> findings,
        List<ActionFitSdkArtifactPlan> artifactPlans,
        List<ActionFitSdkAssetPlan> assetPlans)
    {
        foreach (ActionFitSdkDependencyOwnership dependency in previous.Dependencies.Where(item => item != null))
        {
            string current = ActionFitPackageManifestUtility.GetDependency(manifest, dependency.PackageId);
            if (!dependency.CreatedByProfile)
            {
                changes.Add(Change(ActionFitSdkPlannedChangeArea.Dependency, ActionFitSdkPlannedChangeAction.Preserve, dependency.PackageId, current, current, false));
                continue;
            }
            if (OtherProfileRequiresDependency(ownership, profile.ProfileId, dependency.PackageId, dependency.Value))
            {
                changes.Add(Change(ActionFitSdkPlannedChangeArea.Dependency, ActionFitSdkPlannedChangeAction.Preserve, dependency.PackageId, current, current, true));
                continue;
            }
            if (!string.Equals(current, dependency.Value, StringComparison.Ordinal))
            {
                findings.Add(Finding("dependency-remove-conflict", ActionFitSdkInstallationClassification.Conflicting, "dependency", dependency.PackageId, "Owned dependency changed after apply and will be preserved."));
                continue;
            }

            manifest = ActionFitPackageManifestUtility.RemoveDependency(manifest, dependency.PackageId, out _);
            changes.Add(Change(ActionFitSdkPlannedChangeArea.Dependency, ActionFitSdkPlannedChangeAction.Remove, dependency.PackageId, current, "", true));
        }

        List<ActionFitSdkScopedRegistryValue> registries = ActionFitPackageManifestUtility.ReadScopedRegistries(manifest).Select(item => item.Clone()).ToList();
        foreach (ActionFitSdkRegistryOwnership registry in previous.ScopedRegistries.Where(item => item != null))
        {
            ActionFitSdkScopedRegistryValue current = registries.FirstOrDefault(item => RegistryUrlEquals(item.Url, registry.Url));
            if (current == null) continue;
            var scopes = new HashSet<string>(current.Scopes ?? Array.Empty<string>(), StringComparer.Ordinal);
            foreach (string scope in registry.OwnedScopes ?? Array.Empty<string>())
            {
                if (OtherProfileRequiresRegistryScope(ownership, profile.ProfileId, registry.Url, scope)) continue;
                if (scopes.Remove(scope))
                    changes.Add(Change(ActionFitSdkPlannedChangeArea.ScopedRegistry, ActionFitSdkPlannedChangeAction.Remove, registry.Url + "#" + scope, scope, "", true));
            }
            current.Scopes = scopes.OrderBy(item => item, StringComparer.Ordinal).ToArray();
            bool otherRegistryOwner = ownership.Profiles.Any(item => item != null && !string.Equals(item.ProfileId, profile.ProfileId, StringComparison.Ordinal) &&
                item.ScopedRegistries.Any(other => other != null && RegistryUrlEquals(other.Url, registry.Url)));
            if (registry.RegistryCreatedByProfile && current.Scopes.Length == 0 && !otherRegistryOwner)
            {
                registries.Remove(current);
                changes.Add(Change(ActionFitSdkPlannedChangeArea.ScopedRegistry, ActionFitSdkPlannedChangeAction.Remove, registry.Url, registry.Name, "", true));
            }
        }
        manifest = ActionFitPackageManifestUtility.SetScopedRegistries(manifest, registries);

        foreach (ActionFitSdkArtifactOwnership artifact in previous.Artifacts.Where(item => item != null))
        {
            if (!artifact.CreatedByProfile || OtherProfileRequiresArtifact(ownership, profile.ProfileId, artifact.ProjectRelativePath))
            {
                changes.Add(Change(ActionFitSdkPlannedChangeArea.ArtifactCache, ActionFitSdkPlannedChangeAction.Preserve, artifact.ProjectRelativePath, artifact.Sha256, artifact.Sha256, false));
                continue;
            }

            string target = context.ResolveProjectPath(artifact.ProjectRelativePath);
            if (File.Exists(target) && !string.Equals(ActionFitSdkArtifactVerifier.ComputeSha256(target), artifact.Sha256, StringComparison.OrdinalIgnoreCase))
            {
                findings.Add(Finding("artifact-remove-conflict", ActionFitSdkInstallationClassification.Conflicting, "artifact", artifact.ProjectRelativePath, "Owned artifact changed after apply and will be preserved."));
                continue;
            }
            artifactPlans.Add(new ActionFitSdkArtifactPlan
            {
                ProjectRelativePath = artifact.ProjectRelativePath,
                TargetPath = target,
                Sha256 = artifact.Sha256,
                Remove = true,
                CreatedByProfile = true,
            });
            changes.Add(Change(ActionFitSdkPlannedChangeArea.ArtifactCache, ActionFitSdkPlannedChangeAction.Remove, artifact.ProjectRelativePath, artifact.Sha256, "", true));
        }

        RemoveOwnedAssets(profile, previous, ownership, context, changes, findings, assetPlans);
    }

    /// <summary>
    /// Removes only assets this profile created. Adopted, shared, generated, and project-owned
    /// entries are preserved, and any post-apply content change downgrades removal to preservation.
    /// </summary>
    private static void RemoveOwnedAssets(
        ActionFitSdkInstallProfile profile,
        ActionFitSdkProfileOwnership previous,
        ActionFitSdkOwnershipDocument ownership,
        ActionFitSdkProjectContext context,
        List<ActionFitSdkPlannedChange> changes,
        List<ActionFitSdkInstallationFinding> findings,
        List<ActionFitSdkAssetPlan> assetPlans)
    {
        // Deeper paths first so folders empty out before they are considered for removal.
        ActionFitSdkAssetOwnership[] ordered = previous.Assets
            .Where(item => item != null)
            .OrderByDescending(item => NormalizeRelativePath(item.ProjectRelativePath).Count(character => character == '/'))
            .ThenByDescending(item => item.ProjectRelativePath, StringComparer.OrdinalIgnoreCase)
            .ToArray();

        foreach (ActionFitSdkAssetOwnership asset in ordered)
        {
            string relative = NormalizeRelativePath(asset.ProjectRelativePath);
            if (!asset.CreatedByProfile ||
                OtherProfileRequiresAsset(ownership, profile.ProfileId, relative))
            {
                changes.Add(Change(ActionFitSdkPlannedChangeArea.UnityAsset, ActionFitSdkPlannedChangeAction.Preserve, relative, asset.Sha256, asset.Sha256, false));
                continue;
            }

            string target = context.ResolveProjectPath(relative);
            bool isFolder = string.IsNullOrEmpty(asset.Sha256);
            if (!isFolder && File.Exists(target) &&
                !string.Equals(ActionFitSdkArtifactVerifier.ComputeSha256(target), asset.Sha256, StringComparison.OrdinalIgnoreCase))
            {
                findings.Add(Finding("asset-remove-conflict", ActionFitSdkInstallationClassification.Conflicting, "asset", relative, "Owned asset changed after apply and will be preserved."));
                continue;
            }

            assetPlans.Add(new ActionFitSdkAssetPlan
            {
                ProjectRelativePath = relative,
                TargetPath = target,
                Guid = asset.Guid,
                CurrentSha256 = asset.Sha256,
                IsFolder = isFolder,
                Remove = true,
                State = asset.ResolveState(),
                CreatedByProfile = true,
            });
            changes.Add(Change(ActionFitSdkPlannedChangeArea.UnityAsset, ActionFitSdkPlannedChangeAction.Remove, relative, asset.Sha256, "", true));
        }
    }

    private static bool OtherProfileRequiresAsset(ActionFitSdkOwnershipDocument ownership, string profileId, string path)
    {
        return ownership.Profiles.Any(profile => profile != null && !string.Equals(profile.ProfileId, profileId, StringComparison.Ordinal) &&
            profile.Assets.Any(item => item != null && string.Equals(NormalizeRelativePath(item.ProjectRelativePath), NormalizeRelativePath(path), StringComparison.OrdinalIgnoreCase)));
    }

    private static void RemoveObsoleteDependencies(
        ActionFitSdkProfileOwnership previous,
        ICollection<ActionFitSdkDependencyOwnership> next,
        ActionFitSdkOwnershipDocument ownership,
        ref string manifest,
        List<ActionFitSdkPlannedChange> changes,
        List<ActionFitSdkInstallationFinding> findings)
    {
        if (previous == null) return;
        var desired = new HashSet<string>(next.Select(item => item.PackageId), StringComparer.Ordinal);
        foreach (ActionFitSdkDependencyOwnership prior in previous.Dependencies.Where(item => item != null && !desired.Contains(item.PackageId)))
        {
            string current = ActionFitPackageManifestUtility.GetDependency(manifest, prior.PackageId);
            if (!prior.CreatedByProfile || OtherProfileRequiresDependency(ownership, previous.ProfileId, prior.PackageId, prior.Value))
            {
                changes.Add(Change(ActionFitSdkPlannedChangeArea.Dependency, ActionFitSdkPlannedChangeAction.Preserve, prior.PackageId, current, current, prior.CreatedByProfile));
            }
            else if (!string.Equals(current, prior.Value, StringComparison.Ordinal))
            {
                findings.Add(Finding("dependency-update-conflict", ActionFitSdkInstallationClassification.Conflicting, "dependency", prior.PackageId, "Previously owned dependency changed and will be preserved."));
            }
            else
            {
                manifest = ActionFitPackageManifestUtility.RemoveDependency(manifest, prior.PackageId, out _);
                changes.Add(Change(ActionFitSdkPlannedChangeArea.Dependency, ActionFitSdkPlannedChangeAction.Remove, prior.PackageId, current, "", true));
            }
        }
    }

    private static void RemoveObsoleteRegistries(
        ActionFitSdkProfileOwnership previous,
        ICollection<ActionFitSdkRegistryOwnership> next,
        ActionFitSdkOwnershipDocument ownership,
        List<ActionFitSdkScopedRegistryValue> registries,
        List<ActionFitSdkPlannedChange> changes,
        List<ActionFitSdkInstallationFinding> findings)
    {
        if (previous == null) return;
        foreach (ActionFitSdkRegistryOwnership prior in previous.ScopedRegistries.Where(item => item != null))
        {
            ActionFitSdkRegistryOwnership desired = next.FirstOrDefault(item => item != null && RegistryUrlEquals(item.Url, prior.Url));
            ActionFitSdkScopedRegistryValue current = registries.FirstOrDefault(item => RegistryUrlEquals(item.Url, prior.Url));
            if (current == null) continue;
            var scopes = new HashSet<string>(current.Scopes ?? Array.Empty<string>(), StringComparer.Ordinal);
            var desiredScopes = new HashSet<string>(desired?.RequiredScopes ?? Array.Empty<string>(), StringComparer.Ordinal);
            foreach (string scope in prior.OwnedScopes.Where(scope => !desiredScopes.Contains(scope)))
            {
                if (OtherProfileRequiresRegistryScope(ownership, previous.ProfileId, prior.Url, scope)) continue;
                if (scopes.Remove(scope))
                    changes.Add(Change(ActionFitSdkPlannedChangeArea.ScopedRegistry, ActionFitSdkPlannedChangeAction.Remove, prior.Url + "#" + scope, scope, "", true));
            }
            current.Scopes = scopes.OrderBy(item => item, StringComparer.Ordinal).ToArray();

            if (desired == null && prior.RegistryCreatedByProfile && current.Scopes.Length == 0 &&
                !ownership.Profiles.Any(item => item != null && !string.Equals(item.ProfileId, previous.ProfileId, StringComparison.Ordinal) &&
                    item.ScopedRegistries.Any(other => other != null && RegistryUrlEquals(other.Url, prior.Url))))
            {
                registries.Remove(current);
                changes.Add(Change(ActionFitSdkPlannedChangeArea.ScopedRegistry, ActionFitSdkPlannedChangeAction.Remove, prior.Url, prior.Name, "", true));
            }
            else if (desired == null && current.Scopes.Length > 0)
            {
                findings.Add(Finding("registry-preserved", ActionFitSdkInstallationClassification.Adoptable, "registry", prior.Url, "Registry contains shared or user-managed scopes and will be preserved."));
            }
        }
    }

    private static void AddArtifactPlan(
        ActionFitSdkSourceDefinition source,
        bool dependencyOwnedByProfile,
        bool takeOwnershipOfCompatibleEntries,
        ActionFitSdkProjectContext context,
        List<ActionFitSdkPlannedChange> changes,
        List<ActionFitSdkInstallationFinding> findings,
        List<ActionFitSdkArtifactPlan> plans)
    {
        if (source.ResolveKind() != ActionFitSdkSourceKind.Artifact || plans.Any(item => string.Equals(item.SourceId, source.Id, StringComparison.Ordinal)))
            return;

        string relative = NormalizeRelativePath(source.CacheRelativePath);
        string target = context.ResolveProjectPath(relative);
        var plan = new ActionFitSdkArtifactPlan
        {
            SourceId = source.Id,
            Url = source.Url,
            PackageId = source.PackageId,
            PackageVersion = source.PackageVersion,
            Sha256 = source.Sha256.ToLowerInvariant(),
            ProjectRelativePath = relative,
            TargetPath = target,
            DownloadRequired = !File.Exists(target),
            CreatedByProfile = File.Exists(target)
                ? takeOwnershipOfCompatibleEntries
                : dependencyOwnedByProfile,
        };
        if (File.Exists(target))
        {
            string actual = ActionFitSdkArtifactVerifier.ComputeSha256(target);
            if (!string.Equals(actual, source.Sha256, StringComparison.OrdinalIgnoreCase))
            {
                findings.Add(Finding("artifact-conflict", ActionFitSdkInstallationClassification.Conflicting, "artifact", relative, "Cached artifact checksum does not match the profile and will not be overwritten."));
                return;
            }
            if (!ActionFitSdkArtifactVerifier.TryValidatePackageIdentity(target, source.PackageId, source.PackageVersion, out string error))
            {
                findings.Add(Finding("artifact-identity-conflict", ActionFitSdkInstallationClassification.Conflicting, "artifact", relative, error));
                return;
            }
        }

        plans.Add(plan);
        changes.Add(Change(
            ActionFitSdkPlannedChangeArea.ArtifactCache,
            plan.DownloadRequired ? ActionFitSdkPlannedChangeAction.Download : ActionFitSdkPlannedChangeAction.Reuse,
            relative,
            plan.DownloadRequired ? "" : plan.Sha256,
            plan.Sha256,
            plan.CreatedByProfile));
    }

    /// <summary>
    /// Plans Unity Asset install or compatible adoption for one unitypackageArtifact source.
    /// Adoption keeps existing files and their .meta GUIDs; any unexplained difference outside a
    /// declared preserve path blocks the plan instead of overwriting project state.
    /// </summary>
    private static void AddUnityPackageAssetPlans(
        ActionFitSdkSourceDefinition source,
        ActionFitSdkProjectContext context,
        List<ActionFitSdkPlannedChange> changes,
        List<ActionFitSdkInstallationFinding> findings,
        List<ActionFitSdkAssetPlan> plans,
        ICollection<ActionFitSdkAssetOwnership> ownedAssets)
    {
        if (source.ResolveKind() != ActionFitSdkSourceKind.UnityPackageArtifact ||
            plans.Any(item => string.Equals(item.SourceId, source.Id, StringComparison.Ordinal)))
        {
            return;
        }

        string[] preservePaths = source.PreservePaths;
        string[] excludedPaths = source.ExcludedPaths;

        foreach (ActionFitSdkAssetEntry entry in source.AssetInventory)
        {
            if (entry == null) continue;

            string relative = NormalizeRelativePath(entry.Path);
            if (IsUnderDeclaredPath(relative, excludedPaths))
                continue;

            string target = context.ResolveProjectPath(relative);
            bool preserve = IsUnderDeclaredPath(relative, preservePaths);
            bool isFolder = entry.ResolveKind() == ActionFitSdkAssetEntryKind.Folder;
            string expected = (entry.Sha256 ?? "").ToLowerInvariant();
            string guid = (entry.Guid ?? "").ToLowerInvariant();

            if (isFolder)
            {
                bool folderExists = Directory.Exists(target);
                AddAssetPlan(
                    plans, changes, ownedAssets, source.Id, relative, target, guid, "", "",
                    isFolder: true,
                    writeContent: !folderExists,
                    state: folderExists ? ActionFitSdkAssetOwnershipState.Adopted : ActionFitSdkAssetOwnershipState.Created,
                    createdByProfile: !folderExists,
                    action: folderExists ? ActionFitSdkPlannedChangeAction.Adopt : ActionFitSdkPlannedChangeAction.Add);
                continue;
            }

            bool exists = File.Exists(target);
            if (!exists)
            {
                AddAssetPlan(
                    plans, changes, ownedAssets, source.Id, relative, target, guid, "", expected,
                    isFolder: false,
                    writeContent: true,
                    state: preserve ? ActionFitSdkAssetOwnershipState.Generated : ActionFitSdkAssetOwnershipState.Created,
                    createdByProfile: true,
                    action: ActionFitSdkPlannedChangeAction.Add);
                continue;
            }

            if (!MetaGuidMatches(target, guid, out string actualGuid))
            {
                findings.Add(Finding(
                    "asset-guid-conflict",
                    ActionFitSdkInstallationClassification.Conflicting,
                    "asset",
                    relative,
                    $"Existing asset GUID {actualGuid} does not match the declared inventory GUID {guid}. Adoption would break serialized references."));
                continue;
            }

            string current = ActionFitSdkArtifactVerifier.ComputeSha256(target).ToLowerInvariant();
            if (preserve)
            {
                // Project-generated or project-owned content: keep the value, record ownership only.
                AddAssetPlan(
                    plans, changes, ownedAssets, source.Id, relative, target, guid, current, current,
                    isFolder: false,
                    writeContent: false,
                    state: ActionFitSdkAssetOwnershipState.Generated,
                    createdByProfile: false,
                    action: ActionFitSdkPlannedChangeAction.Preserve);
                continue;
            }

            if (!string.Equals(current, expected, StringComparison.OrdinalIgnoreCase))
            {
                findings.Add(Finding(
                    "asset-content-conflict",
                    ActionFitSdkInstallationClassification.Conflicting,
                    "asset",
                    relative,
                    "Existing asset content differs from the verified artifact and is not a declared preserve path. It will not be overwritten."));
                continue;
            }

            AddAssetPlan(
                plans, changes, ownedAssets, source.Id, relative, target, guid, current, expected,
                isFolder: false,
                writeContent: false,
                state: ActionFitSdkAssetOwnershipState.Adopted,
                createdByProfile: false,
                action: ActionFitSdkPlannedChangeAction.Adopt);
        }
    }

    private static void AddAssetPlan(
        List<ActionFitSdkAssetPlan> plans,
        List<ActionFitSdkPlannedChange> changes,
        ICollection<ActionFitSdkAssetOwnership> ownedAssets,
        string sourceId,
        string relative,
        string target,
        string guid,
        string current,
        string expected,
        bool isFolder,
        bool writeContent,
        ActionFitSdkAssetOwnershipState state,
        bool createdByProfile,
        ActionFitSdkPlannedChangeAction action)
    {
        plans.Add(new ActionFitSdkAssetPlan
        {
            SourceId = sourceId,
            ProjectRelativePath = relative,
            TargetPath = target,
            Guid = guid,
            ExpectedSha256 = expected,
            CurrentSha256 = current,
            IsFolder = isFolder,
            WriteContent = writeContent,
            State = state,
            CreatedByProfile = createdByProfile,
        });

        ownedAssets.Add(new ActionFitSdkAssetOwnership
        {
            ProjectRelativePath = relative,
            Guid = guid,
            Sha256 = expected,
            State = ActionFitSdkAssetOwnership.StateName(state),
            CreatedByProfile = createdByProfile,
        });

        changes.Add(Change(
            ActionFitSdkPlannedChangeArea.UnityAsset,
            action,
            relative,
            current,
            expected,
            createdByProfile));
    }

    private static bool IsUnderDeclaredPath(string relative, string[] declared)
    {
        foreach (string candidate in declared)
        {
            string normalized = NormalizeRelativePath(candidate);
            if (string.IsNullOrEmpty(normalized)) continue;
            if (string.Equals(relative, normalized, StringComparison.OrdinalIgnoreCase) ||
                relative.StartsWith(normalized + "/", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Compares the on-disk .meta GUID with the declared inventory GUID. Only the GUID is compared:
    /// dependency resolvers rewrite importer labels in place, and that normalization is not drift.
    /// </summary>
    private static bool MetaGuidMatches(string assetPath, string expectedGuid, out string actualGuid)
    {
        actualGuid = "";
        string metaPath = assetPath + ".meta";
        if (!File.Exists(metaPath))
        {
            // Files inside opaque folder assets legitimately carry no .meta file.
            actualGuid = "(none)";
            return IsInsideOpaqueFolderAsset(assetPath);
        }

        foreach (string line in File.ReadLines(metaPath))
        {
            string trimmed = line.Trim();
            if (!trimmed.StartsWith("guid:", StringComparison.Ordinal)) continue;
            actualGuid = trimmed.Substring("guid:".Length).Trim().ToLowerInvariant();
            return string.Equals(actualGuid, expectedGuid, StringComparison.OrdinalIgnoreCase);
        }

        actualGuid = "(unparsed)";
        return false;
    }

    private static bool IsInsideOpaqueFolderAsset(string path)
    {
        string[] segments = path.Replace('\\', '/').Split('/');
        for (int i = 0; i < segments.Length - 1; i++)
        {
            if (segments[i].EndsWith(".androidlib", StringComparison.OrdinalIgnoreCase) ||
                segments[i].EndsWith(".androidpack", StringComparison.OrdinalIgnoreCase) ||
                segments[i].EndsWith(".bundle", StringComparison.OrdinalIgnoreCase) ||
                segments[i].EndsWith(".framework", StringComparison.OrdinalIgnoreCase) ||
                segments[i].EndsWith(".plugin", StringComparison.OrdinalIgnoreCase) ||
                segments[i].EndsWith(".xcframework", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    private static void AddObsoleteArtifactRemovals(
        ActionFitSdkProfileOwnership previous,
        ICollection<ActionFitSdkArtifactOwnership> next,
        ActionFitSdkOwnershipDocument ownership,
        ActionFitSdkProjectContext context,
        List<ActionFitSdkPlannedChange> changes,
        List<ActionFitSdkInstallationFinding> findings,
        List<ActionFitSdkArtifactPlan> plans)
    {
        if (previous == null) return;
        var desired = new HashSet<string>(next.Select(item => NormalizeRelativePath(item.ProjectRelativePath)), StringComparer.OrdinalIgnoreCase);
        foreach (ActionFitSdkArtifactOwnership prior in previous.Artifacts.Where(item => item != null && !desired.Contains(NormalizeRelativePath(item.ProjectRelativePath))))
        {
            if (!prior.CreatedByProfile || OtherProfileRequiresArtifact(ownership, previous.ProfileId, prior.ProjectRelativePath)) continue;
            string target = context.ResolveProjectPath(prior.ProjectRelativePath);
            if (File.Exists(target) && !string.Equals(ActionFitSdkArtifactVerifier.ComputeSha256(target), prior.Sha256, StringComparison.OrdinalIgnoreCase))
            {
                findings.Add(Finding("artifact-update-conflict", ActionFitSdkInstallationClassification.Conflicting, "artifact", prior.ProjectRelativePath, "Previously owned artifact changed and will be preserved."));
                continue;
            }
            plans.Add(new ActionFitSdkArtifactPlan
            {
                ProjectRelativePath = prior.ProjectRelativePath,
                TargetPath = target,
                Sha256 = prior.Sha256,
                Remove = true,
                CreatedByProfile = true,
            });
            changes.Add(Change(ActionFitSdkPlannedChangeArea.ArtifactCache, ActionFitSdkPlannedChangeAction.Remove, prior.ProjectRelativePath, prior.Sha256, "", true));
        }
    }

    private static ActionFitSdkOwnershipDocument ReplaceOwnership(
        ActionFitSdkOwnershipDocument ownership,
        string profileId,
        ActionFitSdkProfileOwnership replacement)
    {
        var profiles = ownership.Profiles
            .Where(item => item != null && !string.Equals(item.ProfileId, profileId, StringComparison.Ordinal))
            .ToList();
        if (replacement != null) profiles.Add(replacement);
        return new ActionFitSdkOwnershipDocument
        {
            SchemaVersion = ActionFitSdkOwnershipDocument.CurrentSchemaVersion,
            Profiles = profiles.OrderBy(item => item.ProfileId, StringComparer.Ordinal).ToArray(),
        };
    }

    internal static string[] ResolveModules(ActionFitSdkInstallProfile profile, IEnumerable<string> selectedModuleIds)
    {
        var requested = new HashSet<string>(
            (selectedModuleIds ?? Array.Empty<string>()).Where(item => !string.IsNullOrWhiteSpace(item)),
            StringComparer.Ordinal);
        if (requested.Count == 0)
        {
            foreach (ActionFitSdkModuleDefinition module in profile.Modules.Where(item => item != null && (item.Required || item.DefaultSelected)))
                requested.Add(module.Id);
        }
        foreach (ActionFitSdkModuleDefinition module in profile.Modules.Where(item => item != null && item.Required))
            requested.Add(module.Id);

        var declared = new HashSet<string>(profile.Modules.Where(item => item != null).Select(item => item.Id), StringComparer.Ordinal);
        string unknown = requested.FirstOrDefault(item => !declared.Contains(item));
        if (!string.IsNullOrEmpty(unknown))
            throw new InvalidOperationException($"Selected SDK module is not declared: {unknown}");

        var queue = new Queue<string>(requested);
        while (queue.Count > 0)
        {
            string moduleId = queue.Dequeue();
            ActionFitSdkModuleDefinition module = profile.Modules.First(item => item != null && string.Equals(item.Id, moduleId, StringComparison.Ordinal));
            foreach (string required in module.Requires)
            {
                if (requested.Add(required)) queue.Enqueue(required);
            }
        }
        return requested.OrderBy(item => item, StringComparer.Ordinal).ToArray();
    }

    private static ActionFitSdkInstallationFinding[] InspectFindings(
        ActionFitSdkInstallProfile profile,
        string[] modules,
        ActionFitSdkProjectContext context,
        string manifest)
    {
        var findings = new List<ActionFitSdkInstallationFinding>();
        if (!ActionFitSdkInstallProfileValidator.IsUnityVersionCompatible(profile, Application.unityVersion))
        {
            findings.Add(Finding(
                "unity-version-unsupported",
                ActionFitSdkInstallationClassification.Unsupported,
                "unity",
                Application.unityVersion,
                $"Unity {Application.unityVersion} is outside the supported range {profile.MinimumUnityVersion} - {profile.MaximumUnityVersion}."));
        }
        List<ActionFitSdkScopedRegistryValue> registries = ActionFitPackageManifestUtility.ReadScopedRegistries(manifest);
        foreach (ActionFitSdkDetectionRule rule in profile.DetectionRules.Where(item => item != null))
        {
            bool match = false;
            switch (rule.ResolveKind())
            {
                case ActionFitSdkDetectionKind.AssetPath:
                    string path = context.ResolveProjectPath(rule.Value);
                    match = File.Exists(path) || Directory.Exists(path);
                    break;
                case ActionFitSdkDetectionKind.Dependency:
                    string value = ActionFitPackageManifestUtility.GetDependency(manifest, rule.Value);
                    match = !string.IsNullOrEmpty(value) &&
                            (string.IsNullOrWhiteSpace(rule.ExpectedValue) || string.Equals(value, rule.ExpectedValue, StringComparison.Ordinal));
                    break;
                case ActionFitSdkDetectionKind.Registry:
                    match = registries.Any(item => RegistryUrlEquals(item.Url, rule.Value));
                    break;
            }
            if (!match) continue;
            findings.Add(Finding(rule.Id, rule.ResolveClassification(), rule.Kind, rule.Value, rule.Message));
        }
        return findings.ToArray();
    }

    private static string ResolveDependencyValue(ActionFitSdkSourceDefinition source, ActionFitSdkProjectContext context)
    {
        switch (source.ResolveKind())
        {
            case ActionFitSdkSourceKind.Artifact:
                string target = context.ResolveProjectPath(source.CacheRelativePath);
                string packagesRoot = Path.Combine(context.ProjectRoot, "Packages");
                return "file:" + Path.GetRelativePath(packagesRoot, target).Replace('\\', '/');
            case ActionFitSdkSourceKind.Git:
                string gitUrl = source.Url.TrimEnd('#');
                if (!string.IsNullOrWhiteSpace(source.GitSubpath))
                    gitUrl += "?path=" + source.GitSubpath.Trim();
                return gitUrl + "#" + source.ImmutableRevision;
            case ActionFitSdkSourceKind.Registry:
                return source.ImmutableVersion;
            default:
                throw new InvalidOperationException($"Unsupported SDK source kind: {source.Kind}");
        }
    }

    private static string ReadManifest(ActionFitSdkProjectContext context)
    {
        if (!File.Exists(context.ManifestPath))
            throw new FileNotFoundException("Packages/manifest.json was not found.", context.ManifestPath);
        string manifest = File.ReadAllText(context.ManifestPath);
        ActionFitPackageManifestUtility.Validate(manifest);
        return manifest;
    }

    private static ActionFitSdkInstallPlan InvalidPlan(
        ActionFitSdkInstallProfile profile,
        ActionFitSdkPlanRequest request,
        ActionFitSdkProfileValidationResult validation)
    {
        return new ActionFitSdkInstallPlan
        {
            Success = false,
            Code = "PROFILE_INVALID",
            Message = validation.FormatMessage(),
            ProfileId = profile?.ProfileId ?? "",
            ProfileVersion = profile?.ProfileVersion ?? "",
            Operation = request.Operation,
            ValidationDiagnostics = validation.Diagnostics,
        };
    }

    internal static string ComputePlanId(ActionFitSdkInstallPlan plan)
    {
        var builder = new StringBuilder();
        builder.Append(plan.ProfileSnapshot).Append('\n')
            .Append(plan.Operation).Append('\n')
            .Append(string.Join(",", plan.SelectedModuleIds)).Append('\n')
            .Append(plan.OriginalManifestHash).Append('\n')
            .Append(plan.OriginalOwnershipHash).Append('\n')
            .Append(Hash(plan.UpdatedManifest)).Append('\n')
            .Append(Hash(plan.UpdatedOwnership)).Append('\n');
        if (plan.ResolutionSnapshot != null)
            builder.Append(JsonUtility.ToJson(plan.ResolutionSnapshot, false)).Append('\n');
        foreach (ActionFitSdkPlannedChange change in plan.Changes)
            builder.Append(change.Area).Append('|').Append(change.Action).Append('|').Append(change.Key).Append('|').Append(change.Before).Append('|').Append(change.After).Append('|').Append(change.OwnedByProfile).Append('\n');
        return Hash(builder.ToString()).ToLowerInvariant();
    }

    internal static string Hash(string value)
    {
        using SHA256 sha256 = SHA256.Create();
        return BitConverter.ToString(sha256.ComputeHash(Encoding.UTF8.GetBytes(value ?? ""))).Replace("-", "");
    }

    private static bool IsSelected(string moduleId, HashSet<string> selected)
    {
        return string.IsNullOrWhiteSpace(moduleId) || selected.Contains(moduleId);
    }

    private static bool OtherProfileRequiresDependency(ActionFitSdkOwnershipDocument ownership, string profileId, string packageId, string value)
    {
        return ownership.Profiles.Any(profile => profile != null && !string.Equals(profile.ProfileId, profileId, StringComparison.Ordinal) &&
            profile.Dependencies.Any(item => item != null && string.Equals(item.PackageId, packageId, StringComparison.Ordinal) && string.Equals(item.Value, value, StringComparison.Ordinal)));
    }

    private static bool OtherProfileRequiresRegistryScope(ActionFitSdkOwnershipDocument ownership, string profileId, string url, string scope)
    {
        return ownership.Profiles.Any(profile => profile != null && !string.Equals(profile.ProfileId, profileId, StringComparison.Ordinal) &&
            profile.ScopedRegistries.Any(item => item != null && RegistryUrlEquals(item.Url, url) && item.RequiredScopes.Contains(scope)));
    }

    private static bool OtherProfileRequiresArtifact(ActionFitSdkOwnershipDocument ownership, string profileId, string path)
    {
        return ownership.Profiles.Any(profile => profile != null && !string.Equals(profile.ProfileId, profileId, StringComparison.Ordinal) &&
            profile.Artifacts.Any(item => item != null && string.Equals(NormalizeRelativePath(item.ProjectRelativePath), NormalizeRelativePath(path), StringComparison.OrdinalIgnoreCase)));
    }

    private static bool RegistryDependencyUses(ActionFitSdkInstallProfile profile, string packageId, string registryUrl)
    {
        ActionFitSdkDependencyDefinition dependency = profile.Dependencies.FirstOrDefault(item =>
            item != null && string.Equals(item.PackageId, packageId, StringComparison.Ordinal));
        ActionFitSdkSourceDefinition source = dependency == null ? null : profile.Sources.FirstOrDefault(item =>
            item != null && string.Equals(item.Id, dependency.SourceId, StringComparison.Ordinal));
        return source != null && source.ResolveKind() == ActionFitSdkSourceKind.Registry && RegistryUrlEquals(source.Url, registryUrl);
    }

    private static string NormalizeRegistryUrl(string url)
    {
        return (url ?? "").Trim().TrimEnd('/');
    }

    private static bool RegistryUrlEquals(string left, string right)
    {
        return string.Equals(NormalizeRegistryUrl(left), NormalizeRegistryUrl(right), StringComparison.OrdinalIgnoreCase);
    }

    private static string NormalizeRelativePath(string path)
    {
        return (path ?? "").Replace('\\', '/').Trim('/');
    }

    private static ActionFitSdkPlannedChange Change(
        ActionFitSdkPlannedChangeArea area,
        ActionFitSdkPlannedChangeAction action,
        string key,
        string before,
        string after,
        bool owned)
    {
        return new ActionFitSdkPlannedChange
        {
            Area = area,
            Action = action,
            Key = key,
            Before = ActionFitSdkValueRedactor.Redact(before),
            After = ActionFitSdkValueRedactor.Redact(after),
            OwnedByProfile = owned,
        };
    }

    private static ActionFitSdkInstallationFinding Finding(
        string id,
        ActionFitSdkInstallationClassification classification,
        string kind,
        string value,
        string message)
    {
        return new ActionFitSdkInstallationFinding
        {
            RuleId = id,
            Classification = classification,
            Kind = kind,
            Value = ActionFitSdkValueRedactor.Redact(value),
            Message = message,
        };
    }
}

internal static class ActionFitSdkValueRedactor
{
    public static string Redact(string value)
    {
        if (string.IsNullOrWhiteSpace(value)) return value ?? "";
        if (!Uri.TryCreate(value, UriKind.Absolute, out Uri uri)) return value;
        if (string.IsNullOrEmpty(uri.Query) && string.IsNullOrEmpty(uri.UserInfo)) return value;
        return uri.GetLeftPart(UriPartial.Path) + (string.IsNullOrEmpty(uri.Query) ? "" : "?[redacted]");
    }
}
#endif
