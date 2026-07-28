#if UNITY_EDITOR
using System;
using System.IO;
using System.Linq;
using UnityEngine;

public enum ActionFitSdkSourceKind
{
    Unknown = 0,
    Artifact = 1,
    Git = 2,
    Registry = 3,
    UnityPackageArtifact = 4,
}

public enum ActionFitSdkAssetEntryKind
{
    Unknown = 0,
    File = 1,
    Folder = 2,
}

public enum ActionFitSdkAssetOwnershipState
{
    Unknown = 0,
    Created = 1,
    Adopted = 2,
    Shared = 3,
    Generated = 4,
    ProjectOwned = 5,
}

public enum ActionFitSdkResolutionPolicy
{
    Exact = 0,
    AnyInstalledElseLatestStable = 1,
}

public enum ActionFitSdkLatestResolverKind
{
    Unknown = 0,
    RegistryMetadata = 1,
    GitRelease = 2,
    ArtifactMetadata = 3,
}

public enum ActionFitSdkDetectionKind
{
    Unknown = 0,
    AssetPath = 1,
    Dependency = 2,
    Registry = 3,
}

public enum ActionFitSdkInstallationClassification
{
    None = 0,
    Adoptable = 1,
    Conflicting = 2,
    Unsupported = 3,
}

/// <summary>Describes one versioned, vendor-neutral external SDK installation profile.</summary>
[Serializable]
public sealed class ActionFitSdkInstallProfile
{
    public const int LegacySchemaVersion = 1;

    /// <summary>Lowest schema version that may declare latest-resolution sources.</summary>
    public const int LatestResolutionSchemaVersion = 2;

    /// <summary>Lowest schema version that may declare unitypackageArtifact sources.</summary>
    public const int UnityPackageSchemaVersion = 3;

    public const int CurrentSchemaVersion = UnityPackageSchemaVersion;

    public int SchemaVersion = CurrentSchemaVersion;
    public string ProfileId = "";
    public string ProfileVersion = "";
    public string Vendor = "";
    public string DisplayName = "";
    public string BridgePackageId = "";
    public string MinimumUnityVersion = "";
    public string MaximumUnityVersion = "";
    public string LicenseUrl = "";
    public string SupportUrl = "";
    public string[] SupportedPlatforms = Array.Empty<string>();
    public string[] AllowedDomains = Array.Empty<string>();
    public ActionFitSdkSourceDefinition[] Sources = Array.Empty<ActionFitSdkSourceDefinition>();
    public ActionFitSdkModuleDefinition[] Modules = Array.Empty<ActionFitSdkModuleDefinition>();
    public ActionFitSdkDependencyDefinition[] Dependencies = Array.Empty<ActionFitSdkDependencyDefinition>();
    public ActionFitSdkScopedRegistryDefinition[] ScopedRegistries = Array.Empty<ActionFitSdkScopedRegistryDefinition>();
    public ActionFitSdkDetectionRule[] DetectionRules = Array.Empty<ActionFitSdkDetectionRule>();

    /// <summary>Reads and validates a profile from an explicit file path.</summary>
    public static ActionFitSdkInstallProfile Read(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
            throw new ArgumentException("SDK install profile path is required.", nameof(path));

        string fullPath = Path.GetFullPath(path);
        if (!File.Exists(fullPath))
            throw new FileNotFoundException("SDK install profile was not found.", fullPath);

        return FromJson(File.ReadAllText(fullPath));
    }

    /// <summary>Parses and validates one SDK install profile JSON document.</summary>
    public static ActionFitSdkInstallProfile FromJson(string json)
    {
        if (string.IsNullOrWhiteSpace(json))
            throw new InvalidOperationException("SDK install profile JSON is empty.");

        ActionFitSdkInstallProfile profile;
        try
        {
            profile = JsonUtility.FromJson<ActionFitSdkInstallProfile>(json);
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException($"SDK install profile JSON is invalid: {ex.Message}", ex);
        }

        if (profile == null)
            throw new InvalidOperationException("SDK install profile JSON did not produce a profile.");

        profile.NormalizeCollections();
        ActionFitSdkProfileValidationResult validation = ActionFitSdkInstallProfileValidator.Validate(profile);
        if (!validation.Success)
            throw new InvalidOperationException(validation.FormatMessage());

        return profile;
    }

    /// <summary>Returns a deterministic, pretty-printed profile snapshot.</summary>
    public string ToJson()
    {
        NormalizeCollections();
        return JsonUtility.ToJson(this, true) + "\n";
    }

    internal void NormalizeCollections()
    {
        SupportedPlatforms ??= Array.Empty<string>();
        AllowedDomains ??= Array.Empty<string>();
        Sources ??= Array.Empty<ActionFitSdkSourceDefinition>();
        Modules ??= Array.Empty<ActionFitSdkModuleDefinition>();
        Dependencies ??= Array.Empty<ActionFitSdkDependencyDefinition>();
        ScopedRegistries ??= Array.Empty<ActionFitSdkScopedRegistryDefinition>();
        DetectionRules ??= Array.Empty<ActionFitSdkDetectionRule>();

        foreach (ActionFitSdkSourceDefinition source in Sources)
            source?.NormalizeCollections();
        foreach (ActionFitSdkModuleDefinition module in Modules)
            module?.NormalizeCollections();
        foreach (ActionFitSdkScopedRegistryDefinition registry in ScopedRegistries)
            registry?.NormalizeCollections();
    }

    internal bool RequiresAsyncResolution()
    {
        return SchemaVersion >= LatestResolutionSchemaVersion &&
               Sources.Any(source => source != null &&
                   source.ResolvePolicy() == ActionFitSdkResolutionPolicy.AnyInstalledElseLatestStable);
    }

    internal bool DeclaresUnityPackageSources()
    {
        return Sources.Any(source => source != null &&
            source.ResolveKind() == ActionFitSdkSourceKind.UnityPackageArtifact);
    }
}

[Serializable]
public sealed class ActionFitSdkSourceDefinition
{
    public string Id = "";
    public string Kind = "";
    public string Url = "";
    public string ImmutableVersion = "";
    public string ImmutableRevision = "";
    public string GitSubpath = "";
    public string PackageId = "";
    public string PackageVersion = "";
    public string Sha256 = "";
    public string CacheRelativePath = "";
    public string ResolutionPolicy = "exact";
    public string LatestResolver = "";
    public string MetadataUrl = "";
    public string VersionFamily = "";

    /// <summary>Complete declared Unity Asset inventory for a unitypackageArtifact source.</summary>
    public ActionFitSdkAssetEntry[] AssetInventory = Array.Empty<ActionFitSdkAssetEntry>();

    /// <summary>Project-owned or generated paths whose values are preserved rather than written.</summary>
    public string[] PreservePaths = Array.Empty<string>();

    /// <summary>Declared archive paths that this profile intentionally does not install.</summary>
    public string[] ExcludedPaths = Array.Empty<string>();

    public ActionFitSdkSourceKind ResolveKind()
    {
        if (string.Equals(Kind, "artifact", StringComparison.OrdinalIgnoreCase))
            return ActionFitSdkSourceKind.Artifact;
        if (string.Equals(Kind, "git", StringComparison.OrdinalIgnoreCase))
            return ActionFitSdkSourceKind.Git;
        if (string.Equals(Kind, "registry", StringComparison.OrdinalIgnoreCase))
            return ActionFitSdkSourceKind.Registry;
        if (string.Equals(Kind, "unitypackageArtifact", StringComparison.OrdinalIgnoreCase))
            return ActionFitSdkSourceKind.UnityPackageArtifact;
        return ActionFitSdkSourceKind.Unknown;
    }

    internal void NormalizeCollections()
    {
        AssetInventory ??= Array.Empty<ActionFitSdkAssetEntry>();
        PreservePaths ??= Array.Empty<string>();
        ExcludedPaths ??= Array.Empty<string>();
    }

    public ActionFitSdkResolutionPolicy ResolvePolicy()
    {
        return string.Equals(ResolutionPolicy, "anyInstalledElseLatestStable", StringComparison.OrdinalIgnoreCase)
            ? ActionFitSdkResolutionPolicy.AnyInstalledElseLatestStable
            : ActionFitSdkResolutionPolicy.Exact;
    }

    public ActionFitSdkLatestResolverKind ResolveLatestResolver()
    {
        if (string.Equals(LatestResolver, "registryMetadata", StringComparison.OrdinalIgnoreCase))
            return ActionFitSdkLatestResolverKind.RegistryMetadata;
        if (string.Equals(LatestResolver, "gitRelease", StringComparison.OrdinalIgnoreCase))
            return ActionFitSdkLatestResolverKind.GitRelease;
        if (string.Equals(LatestResolver, "artifactMetadata", StringComparison.OrdinalIgnoreCase))
            return ActionFitSdkLatestResolverKind.ArtifactMetadata;
        return ActionFitSdkLatestResolverKind.Unknown;
    }
}

/// <summary>Declares one expected Unity Asset or folder inside a unitypackageArtifact source.</summary>
[Serializable]
public sealed class ActionFitSdkAssetEntry
{
    public string Path = "";
    public string Guid = "";
    public string Sha256 = "";
    public string Kind = "";

    public ActionFitSdkAssetEntryKind ResolveKind()
    {
        if (string.Equals(Kind, "file", StringComparison.OrdinalIgnoreCase))
            return ActionFitSdkAssetEntryKind.File;
        if (string.Equals(Kind, "folder", StringComparison.OrdinalIgnoreCase))
            return ActionFitSdkAssetEntryKind.Folder;
        return ActionFitSdkAssetEntryKind.Unknown;
    }
}

[Serializable]
public sealed class ActionFitSdkModuleDefinition
{
    public string Id = "";
    public string DisplayName = "";
    public bool Required;
    public bool DefaultSelected;
    public string[] Requires = Array.Empty<string>();

    internal void NormalizeCollections()
    {
        Requires ??= Array.Empty<string>();
    }
}

[Serializable]
public sealed class ActionFitSdkDependencyDefinition
{
    public string PackageId = "";
    public string SourceId = "";
    public string ModuleId = "";
    public int Order;
}

[Serializable]
public sealed class ActionFitSdkScopedRegistryDefinition
{
    public string Name = "";
    public string Url = "";
    public string ModuleId = "";
    public string[] Scopes = Array.Empty<string>();

    internal void NormalizeCollections()
    {
        Scopes ??= Array.Empty<string>();
    }
}

[Serializable]
public sealed class ActionFitSdkDetectionRule
{
    public string Id = "";
    public string Kind = "";
    public string Value = "";
    public string ExpectedValue = "";
    public string Classification = "";
    public string Message = "";

    public ActionFitSdkDetectionKind ResolveKind()
    {
        if (string.Equals(Kind, "assetPath", StringComparison.OrdinalIgnoreCase))
            return ActionFitSdkDetectionKind.AssetPath;
        if (string.Equals(Kind, "dependency", StringComparison.OrdinalIgnoreCase))
            return ActionFitSdkDetectionKind.Dependency;
        if (string.Equals(Kind, "registry", StringComparison.OrdinalIgnoreCase))
            return ActionFitSdkDetectionKind.Registry;
        return ActionFitSdkDetectionKind.Unknown;
    }

    public ActionFitSdkInstallationClassification ResolveClassification()
    {
        if (string.Equals(Classification, "adoptable", StringComparison.OrdinalIgnoreCase))
            return ActionFitSdkInstallationClassification.Adoptable;
        if (string.Equals(Classification, "conflicting", StringComparison.OrdinalIgnoreCase))
            return ActionFitSdkInstallationClassification.Conflicting;
        if (string.Equals(Classification, "unsupported", StringComparison.OrdinalIgnoreCase))
            return ActionFitSdkInstallationClassification.Unsupported;
        return ActionFitSdkInstallationClassification.None;
    }
}

[Serializable]
public sealed class ActionFitSdkProfileDiagnostic
{
    public string Code = "";
    public string Path = "";
    public string Message = "";
}

[Serializable]
public sealed class ActionFitSdkProfileValidationResult
{
    public bool Success;
    public ActionFitSdkProfileDiagnostic[] Diagnostics = Array.Empty<ActionFitSdkProfileDiagnostic>();

    public string FormatMessage()
    {
        if (Success) return "SDK install profile is valid.";
        return "SDK install profile validation failed:\n" + string.Join(
            "\n",
            Array.ConvertAll(
                Diagnostics ?? Array.Empty<ActionFitSdkProfileDiagnostic>(),
                diagnostic => $"- {diagnostic.Code} ({diagnostic.Path}): {diagnostic.Message}"));
    }
}
#endif
