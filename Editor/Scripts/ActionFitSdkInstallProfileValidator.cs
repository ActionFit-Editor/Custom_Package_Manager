#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;

/// <summary>Validates the portable SDK install-profile contract before planning or execution.</summary>
public static class ActionFitSdkInstallProfileValidator
{
    private static readonly Regex IdentifierPattern = new("^[a-z0-9][a-z0-9._-]*$", RegexOptions.CultureInvariant);
    private static readonly Regex PackageIdPattern = new("^[a-z0-9][a-z0-9._-]*$", RegexOptions.CultureInvariant);
    private static readonly Regex SemVerPattern = new(
        "^(0|[1-9][0-9]*)\\.(0|[1-9][0-9]*)\\.(0|[1-9][0-9]*)(?:-[0-9A-Za-z.-]+)?(?:\\+[0-9A-Za-z.-]+)?$",
        RegexOptions.CultureInvariant);
    private static readonly Regex GitCommitPattern = new("^[0-9a-fA-F]{40}$", RegexOptions.CultureInvariant);
    private static readonly Regex Sha256Pattern = new("^[0-9a-fA-F]{64}$", RegexOptions.CultureInvariant);
    private static readonly Regex UnityVersionPattern = new("^[0-9]+\\.[0-9]+(?:\\.[0-9]+)?$", RegexOptions.CultureInvariant);

    /// <summary>Returns every deterministic validation diagnostic without changing project state.</summary>
    public static ActionFitSdkProfileValidationResult Validate(ActionFitSdkInstallProfile profile)
    {
        var diagnostics = new List<ActionFitSdkProfileDiagnostic>();
        if (profile == null)
        {
            Add(diagnostics, "PROFILE_MISSING", "$", "SDK install profile is required.");
            return Result(diagnostics);
        }

        profile.NormalizeCollections();
        if (profile.SchemaVersion != ActionFitSdkInstallProfile.LegacySchemaVersion &&
            profile.SchemaVersion != ActionFitSdkInstallProfile.CurrentSchemaVersion)
        {
            Add(diagnostics, "SCHEMA_VERSION_UNSUPPORTED", "SchemaVersion", $"Expected schema version {ActionFitSdkInstallProfile.LegacySchemaVersion} or {ActionFitSdkInstallProfile.CurrentSchemaVersion}, found {profile.SchemaVersion}.");
        }

        RequireIdentifier(diagnostics, profile.ProfileId, "ProfileId", "PROFILE_ID_INVALID");
        RequireSemVer(diagnostics, profile.ProfileVersion, "ProfileVersion", "PROFILE_VERSION_INVALID");
        RequireText(diagnostics, profile.Vendor, "Vendor", "VENDOR_MISSING");
        RequireText(diagnostics, profile.DisplayName, "DisplayName", "DISPLAY_NAME_MISSING");
        RequirePackageId(diagnostics, profile.BridgePackageId, "BridgePackageId", "BRIDGE_PACKAGE_ID_INVALID");
        RequireUnityVersion(diagnostics, profile.MinimumUnityVersion, "MinimumUnityVersion");
        if (!string.IsNullOrWhiteSpace(profile.MaximumUnityVersion))
        {
            RequireUnityVersion(diagnostics, profile.MaximumUnityVersion, "MaximumUnityVersion");
            if (TryParseUnityVersion(profile.MinimumUnityVersion, false, out int[] minimum) &&
                TryParseUnityVersion(profile.MaximumUnityVersion, true, out int[] maximum) &&
                CompareVersionParts(minimum, maximum) > 0)
            {
                Add(diagnostics, "UNITY_VERSION_RANGE_INVALID", "MaximumUnityVersion", "MaximumUnityVersion must not be lower than MinimumUnityVersion.");
            }
        }
        RequireHttpsUrl(diagnostics, profile.LicenseUrl, "LicenseUrl", "LICENSE_URL_INVALID", false, profile.AllowedDomains);
        RequireHttpsUrl(diagnostics, profile.SupportUrl, "SupportUrl", "SUPPORT_URL_INVALID", false, profile.AllowedDomains);

        ValidateAllowedDomains(profile, diagnostics);
        ValidatePlatforms(profile, diagnostics);
        ValidateSources(profile, diagnostics);
        ValidateModules(profile, diagnostics);
        ValidateDependencies(profile, diagnostics);
        ValidateRegistries(profile, diagnostics);
        ValidateDetectionRules(profile, diagnostics);
        return Result(diagnostics);
    }

    internal static bool IsExactSemVer(string value)
    {
        return !string.IsNullOrWhiteSpace(value) && SemVerPattern.IsMatch(value.Trim());
    }

    internal static bool IsImmutableGitRevision(string value)
    {
        if (string.IsNullOrWhiteSpace(value)) return false;
        string revision = value.Trim();
        if (GitCommitPattern.IsMatch(revision)) return true;
        if (revision.StartsWith("v", StringComparison.OrdinalIgnoreCase))
            revision = revision[1..];
        return SemVerPattern.IsMatch(revision);
    }

    internal static bool IsPackageId(string value)
    {
        return !string.IsNullOrWhiteSpace(value) && PackageIdPattern.IsMatch(value.Trim());
    }

    internal static bool IsUnityVersionCompatible(ActionFitSdkInstallProfile profile, string unityVersion)
    {
        if (profile == null || !TryParseUnityVersion(unityVersion, false, out int[] current) ||
            !TryParseUnityVersion(profile.MinimumUnityVersion, false, out int[] minimum))
        {
            return false;
        }
        if (CompareVersionParts(current, minimum) < 0) return false;
        return string.IsNullOrWhiteSpace(profile.MaximumUnityVersion) ||
               (TryParseUnityVersion(profile.MaximumUnityVersion, true, out int[] maximum) && CompareVersionParts(current, maximum) <= 0);
    }

    internal static bool IsAllowedHttpsUrl(string value, IEnumerable<string> allowedDomains, out string error)
    {
        return IsAllowedHttpsUrl(value, allowedDomains, false, out error);
    }

    internal static bool IsAllowedRedirectUrl(string value, IEnumerable<string> allowedDomains, out string error)
    {
        return IsAllowedHttpsUrl(value, allowedDomains, true, out error);
    }

    private static bool IsAllowedHttpsUrl(
        string value,
        IEnumerable<string> allowedDomains,
        bool allowQuery,
        out string error)
    {
        if (!Uri.TryCreate(value, UriKind.Absolute, out Uri uri) ||
            !string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase))
        {
            error = "URL must be absolute HTTPS.";
            return false;
        }

        if (!string.IsNullOrEmpty(uri.UserInfo))
        {
            error = "URL must not contain embedded credentials.";
            return false;
        }
        if ((!allowQuery && !string.IsNullOrEmpty(uri.Query)) || !string.IsNullOrEmpty(uri.Fragment))
        {
            error = "Official source URL must not contain query parameters or fragments.";
            return false;
        }

        string[] domains = (allowedDomains ?? Array.Empty<string>())
            .Where(domain => !string.IsNullOrWhiteSpace(domain))
            .Select(NormalizeDomain)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        if (domains.Length == 0)
        {
            error = "AllowedDomains must declare the source host.";
            return false;
        }

        bool allowed = domains.Any(domain =>
            string.Equals(uri.Host, domain, StringComparison.OrdinalIgnoreCase) ||
            uri.Host.EndsWith("." + domain, StringComparison.OrdinalIgnoreCase));
        error = allowed ? "" : $"Host {uri.Host} is not declared in AllowedDomains.";
        return allowed;
    }

    private static void ValidateAllowedDomains(ActionFitSdkInstallProfile profile, List<ActionFitSdkProfileDiagnostic> diagnostics)
    {
        if (profile.AllowedDomains.Length == 0)
            Add(diagnostics, "ALLOWED_DOMAINS_MISSING", "AllowedDomains", "At least one official source domain is required.");

        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        for (int i = 0; i < profile.AllowedDomains.Length; i++)
        {
            string path = $"AllowedDomains[{i}]";
            string domain = NormalizeDomain(profile.AllowedDomains[i]);
            if (string.IsNullOrWhiteSpace(domain) || domain.Contains('/') || domain.Contains(':') || domain.Contains('*') ||
                !domain.Contains('.') || Uri.CheckHostName(domain) != UriHostNameType.Dns || domain.EndsWith(".local", StringComparison.OrdinalIgnoreCase))
            {
                Add(diagnostics, "ALLOWED_DOMAIN_INVALID", path, "Use a public DNS host name without scheme, port, path, credentials, wildcard, IP address, localhost, or .local suffix.");
            }
            else if (!seen.Add(domain))
                Add(diagnostics, "ALLOWED_DOMAIN_DUPLICATE", path, $"Domain {domain} is duplicated.");
        }
    }

    private static void ValidatePlatforms(ActionFitSdkInstallProfile profile, List<ActionFitSdkProfileDiagnostic> diagnostics)
    {
        var supported = new HashSet<string>(new[] { "Editor", "Android", "iOS" }, StringComparer.OrdinalIgnoreCase);
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (profile.SupportedPlatforms.Length == 0)
            Add(diagnostics, "PLATFORMS_MISSING", "SupportedPlatforms", "At least one supported platform is required.");
        for (int i = 0; i < profile.SupportedPlatforms.Length; i++)
        {
            string platform = (profile.SupportedPlatforms[i] ?? "").Trim();
            if (!supported.Contains(platform))
                Add(diagnostics, "PLATFORM_UNSUPPORTED", $"SupportedPlatforms[{i}]", $"Unsupported platform {platform}.");
            else if (!seen.Add(platform))
                Add(diagnostics, "PLATFORM_DUPLICATE", $"SupportedPlatforms[{i}]", $"Platform {platform} is duplicated.");
        }
    }

    private static void ValidateSources(ActionFitSdkInstallProfile profile, List<ActionFitSdkProfileDiagnostic> diagnostics)
    {
        if (profile.Sources.Length == 0)
            Add(diagnostics, "SOURCES_MISSING", "Sources", "At least one official source is required.");

        var ids = new HashSet<string>(StringComparer.Ordinal);
        for (int i = 0; i < profile.Sources.Length; i++)
        {
            ActionFitSdkSourceDefinition source = profile.Sources[i];
            string path = $"Sources[{i}]";
            if (source == null)
            {
                Add(diagnostics, "SOURCE_MISSING", path, "Source entry is null.");
                continue;
            }

            RequireIdentifier(diagnostics, source.Id, path + ".Id", "SOURCE_ID_INVALID");
            if (!string.IsNullOrWhiteSpace(source.Id) && !ids.Add(source.Id.Trim()))
                Add(diagnostics, "SOURCE_ID_DUPLICATE", path + ".Id", $"Source {source.Id} is duplicated.");

            ActionFitSdkSourceKind kind = source.ResolveKind();
            if (kind == ActionFitSdkSourceKind.Unknown)
                Add(diagnostics, "SOURCE_KIND_INVALID", path + ".Kind", "Kind must be artifact, git, or registry.");
            RequireHttpsUrl(diagnostics, source.Url, path + ".Url", "SOURCE_URL_INVALID", true, profile.AllowedDomains);

            bool latest = source.ResolvePolicy() == ActionFitSdkResolutionPolicy.AnyInstalledElseLatestStable;
            bool knownPolicy = string.IsNullOrWhiteSpace(source.ResolutionPolicy) ||
                               string.Equals(source.ResolutionPolicy, "exact", StringComparison.OrdinalIgnoreCase) ||
                               string.Equals(source.ResolutionPolicy, "anyInstalledElseLatestStable", StringComparison.OrdinalIgnoreCase);
            if (!knownPolicy)
                Add(diagnostics, "SOURCE_RESOLUTION_POLICY_INVALID", path + ".ResolutionPolicy", "ResolutionPolicy must be exact or anyInstalledElseLatestStable.");
            if (latest && profile.SchemaVersion != ActionFitSdkInstallProfile.CurrentSchemaVersion)
                Add(diagnostics, "SOURCE_RESOLUTION_POLICY_SCHEMA", path + ".ResolutionPolicy", "AnyInstalledElseLatestStable requires profile schema version 2.");
            if (latest)
                ValidateLatestResolution(profile, source, kind, path, diagnostics);
            else if (!string.IsNullOrWhiteSpace(source.LatestResolver) || !string.IsNullOrWhiteSpace(source.MetadataUrl) || !string.IsNullOrWhiteSpace(source.VersionFamily))
                Add(diagnostics, "SOURCE_LATEST_FIELDS_UNEXPECTED", path, "Exact sources must not declare latest-resolution fields.");

            switch (kind)
            {
                case ActionFitSdkSourceKind.Artifact:
                    if (!latest || !string.IsNullOrWhiteSpace(source.ImmutableVersion))
                        RequireSemVer(diagnostics, source.ImmutableVersion, path + ".ImmutableVersion", "SOURCE_VERSION_INVALID");
                    RequirePackageId(diagnostics, source.PackageId, path + ".PackageId", "SOURCE_PACKAGE_ID_INVALID");
                    if (!latest || !string.IsNullOrWhiteSpace(source.PackageVersion))
                        RequireSemVer(diagnostics, source.PackageVersion, path + ".PackageVersion", "SOURCE_PACKAGE_VERSION_INVALID");
                    if ((!latest || !string.IsNullOrWhiteSpace(source.Sha256)) && !Sha256Pattern.IsMatch((source.Sha256 ?? "").Trim()))
                        Add(diagnostics, "SOURCE_SHA256_INVALID", path + ".Sha256", "Artifact sources require a 64-character SHA-256.");
                    ValidateCachePath(diagnostics, source.CacheRelativePath, path + ".CacheRelativePath");
                    string cachePath = (source.CacheRelativePath ?? "").ToLowerInvariant();
                    if (!cachePath.EndsWith(".tgz", StringComparison.Ordinal) &&
                        !cachePath.EndsWith(".tar.gz", StringComparison.Ordinal))
                    {
                        Add(diagnostics, "SOURCE_ARTIFACT_FORMAT_INVALID", path + ".CacheRelativePath", "Artifact sources must use a .tgz or .tar.gz UPM package archive.");
                    }
                    if (!string.IsNullOrWhiteSpace(source.ImmutableRevision))
                        Add(diagnostics, "SOURCE_REVISION_UNEXPECTED", path + ".ImmutableRevision", "Artifact sources use ImmutableVersion and must not declare ImmutableRevision.");
                    if (!string.IsNullOrWhiteSpace(source.GitSubpath))
                        Add(diagnostics, "SOURCE_GIT_SUBPATH_UNEXPECTED", path + ".GitSubpath", "Artifact sources must not declare GitSubpath.");
                    break;

                case ActionFitSdkSourceKind.Git:
                    RequirePackageId(diagnostics, source.PackageId, path + ".PackageId", "SOURCE_PACKAGE_ID_INVALID");
                    if ((!latest || !string.IsNullOrWhiteSpace(source.ImmutableRevision)) && !IsImmutableGitRevision(source.ImmutableRevision))
                        Add(diagnostics, "SOURCE_GIT_REVISION_MUTABLE", path + ".ImmutableRevision", "Git sources require a full 40-character commit or SemVer tag.");
                    ValidateGitSubpath(diagnostics, source.GitSubpath, path + ".GitSubpath");
                    if (!string.IsNullOrWhiteSpace(source.Sha256) || !string.IsNullOrWhiteSpace(source.CacheRelativePath))
                        Add(diagnostics, "SOURCE_GIT_ARTIFACT_FIELDS", path, "Git sources must not declare artifact checksum or cache path fields.");
                    break;

                case ActionFitSdkSourceKind.Registry:
                    if (!latest || !string.IsNullOrWhiteSpace(source.ImmutableVersion))
                        RequireSemVer(diagnostics, source.ImmutableVersion, path + ".ImmutableVersion", "SOURCE_VERSION_INVALID");
                    RequirePackageId(diagnostics, source.PackageId, path + ".PackageId", "SOURCE_PACKAGE_ID_INVALID");
                    if (!string.IsNullOrWhiteSpace(source.ImmutableRevision) || !string.IsNullOrWhiteSpace(source.Sha256) || !string.IsNullOrWhiteSpace(source.CacheRelativePath))
                        Add(diagnostics, "SOURCE_REGISTRY_EXTRA_FIELDS", path, "Registry sources must not declare revision, checksum, or cache path fields.");
                    if (!string.IsNullOrWhiteSpace(source.GitSubpath))
                        Add(diagnostics, "SOURCE_GIT_SUBPATH_UNEXPECTED", path + ".GitSubpath", "Registry sources must not declare GitSubpath.");
                    break;
            }
        }
    }

    private static void ValidateLatestResolution(
        ActionFitSdkInstallProfile profile,
        ActionFitSdkSourceDefinition source,
        ActionFitSdkSourceKind sourceKind,
        string path,
        List<ActionFitSdkProfileDiagnostic> diagnostics)
    {
        ActionFitSdkLatestResolverKind resolver = source.ResolveLatestResolver();
        if (resolver == ActionFitSdkLatestResolverKind.Unknown)
            Add(diagnostics, "SOURCE_LATEST_RESOLVER_INVALID", path + ".LatestResolver", "LatestResolver must be registryMetadata, gitRelease, or artifactMetadata.");
        if ((sourceKind == ActionFitSdkSourceKind.Registry && resolver != ActionFitSdkLatestResolverKind.RegistryMetadata) ||
            (sourceKind == ActionFitSdkSourceKind.Git && resolver != ActionFitSdkLatestResolverKind.GitRelease) ||
            (sourceKind == ActionFitSdkSourceKind.Artifact && resolver != ActionFitSdkLatestResolverKind.ArtifactMetadata))
        {
            Add(diagnostics, "SOURCE_LATEST_RESOLVER_MISMATCH", path + ".LatestResolver", "LatestResolver must match the source kind.");
        }

        RequireHttpsUrl(diagnostics, source.MetadataUrl, path + ".MetadataUrl", "SOURCE_METADATA_URL_INVALID", true, profile.AllowedDomains);
        if (!string.IsNullOrWhiteSpace(source.VersionFamily) && !IdentifierPattern.IsMatch(source.VersionFamily.Trim()))
            Add(diagnostics, "SOURCE_VERSION_FAMILY_INVALID", path + ".VersionFamily", "VersionFamily must use a portable lowercase identifier.");
    }

    private static void ValidateGitSubpath(
        List<ActionFitSdkProfileDiagnostic> diagnostics,
        string value,
        string path)
    {
        if (string.IsNullOrWhiteSpace(value)) return;

        string subpath = value.Trim();
        string[] segments = subpath.Split('/');
        if (subpath.StartsWith("/", StringComparison.Ordinal) ||
            subpath.EndsWith("/", StringComparison.Ordinal) ||
            subpath.Contains('\\') ||
            segments.Any(segment => string.IsNullOrWhiteSpace(segment) ||
                                    segment == "." ||
                                    segment == ".." ||
                                    !Regex.IsMatch(segment, "^[0-9A-Za-z._-]+$", RegexOptions.CultureInvariant)))
        {
            Add(
                diagnostics,
                "SOURCE_GIT_SUBPATH_INVALID",
                path,
                "GitSubpath must be a relative UPM package path containing only safe path segments.");
        }
    }

    private static void ValidateModules(ActionFitSdkInstallProfile profile, List<ActionFitSdkProfileDiagnostic> diagnostics)
    {
        var ids = new HashSet<string>(StringComparer.Ordinal);
        for (int i = 0; i < profile.Modules.Length; i++)
        {
            ActionFitSdkModuleDefinition module = profile.Modules[i];
            string path = $"Modules[{i}]";
            if (module == null)
            {
                Add(diagnostics, "MODULE_MISSING", path, "Module entry is null.");
                continue;
            }

            RequireIdentifier(diagnostics, module.Id, path + ".Id", "MODULE_ID_INVALID");
            RequireText(diagnostics, module.DisplayName, path + ".DisplayName", "MODULE_DISPLAY_NAME_MISSING");
            if (!string.IsNullOrWhiteSpace(module.Id) && !ids.Add(module.Id.Trim()))
                Add(diagnostics, "MODULE_ID_DUPLICATE", path + ".Id", $"Module {module.Id} is duplicated.");
        }

        for (int i = 0; i < profile.Modules.Length; i++)
        {
            ActionFitSdkModuleDefinition module = profile.Modules[i];
            if (module == null) continue;
            for (int j = 0; j < module.Requires.Length; j++)
            {
                string required = (module.Requires[j] ?? "").Trim();
                if (!ids.Contains(required))
                    Add(diagnostics, "MODULE_REQUIREMENT_MISSING", $"Modules[{i}].Requires[{j}]", $"Required module {required} is not declared.");
                if (string.Equals(required, module.Id, StringComparison.Ordinal))
                    Add(diagnostics, "MODULE_SELF_REQUIREMENT", $"Modules[{i}].Requires[{j}]", "A module cannot require itself.");
            }
        }

        foreach (ActionFitSdkModuleDefinition module in profile.Modules.Where(item => item != null))
        {
            if (HasModuleCycle(module.Id, profile.Modules, new HashSet<string>(StringComparer.Ordinal), new HashSet<string>(StringComparer.Ordinal)))
            {
                Add(diagnostics, "MODULE_REQUIREMENT_CYCLE", "Modules", $"Module dependency cycle includes {module.Id}.");
                break;
            }
        }
    }

    private static bool HasModuleCycle(
        string moduleId,
        IEnumerable<ActionFitSdkModuleDefinition> modules,
        HashSet<string> visiting,
        HashSet<string> visited)
    {
        if (visited.Contains(moduleId)) return false;
        if (!visiting.Add(moduleId)) return true;
        ActionFitSdkModuleDefinition module = modules.FirstOrDefault(item => item != null && string.Equals(item.Id, moduleId, StringComparison.Ordinal));
        if (module != null && module.Requires.Any(required => HasModuleCycle(required, modules, visiting, visited)))
            return true;
        visiting.Remove(moduleId);
        visited.Add(moduleId);
        return false;
    }

    private static void ValidateDependencies(ActionFitSdkInstallProfile profile, List<ActionFitSdkProfileDiagnostic> diagnostics)
    {
        var sourceIds = new HashSet<string>(profile.Sources.Where(item => item != null).Select(item => item.Id), StringComparer.Ordinal);
        var moduleIds = new HashSet<string>(profile.Modules.Where(item => item != null).Select(item => item.Id), StringComparer.Ordinal);
        var packageIds = new HashSet<string>(StringComparer.Ordinal);
        var orders = new HashSet<int>();

        for (int i = 0; i < profile.Dependencies.Length; i++)
        {
            ActionFitSdkDependencyDefinition dependency = profile.Dependencies[i];
            string path = $"Dependencies[{i}]";
            if (dependency == null)
            {
                Add(diagnostics, "DEPENDENCY_MISSING", path, "Dependency entry is null.");
                continue;
            }

            RequirePackageId(diagnostics, dependency.PackageId, path + ".PackageId", "DEPENDENCY_PACKAGE_ID_INVALID");
            if (!string.IsNullOrWhiteSpace(dependency.PackageId) && !packageIds.Add(dependency.PackageId.Trim()))
                Add(diagnostics, "DEPENDENCY_PACKAGE_ID_DUPLICATE", path + ".PackageId", $"Dependency {dependency.PackageId} is duplicated.");
            if (!sourceIds.Contains((dependency.SourceId ?? "").Trim()))
                Add(diagnostics, "DEPENDENCY_SOURCE_MISSING", path + ".SourceId", $"Source {dependency.SourceId} is not declared.");
            if (!string.IsNullOrWhiteSpace(dependency.ModuleId) && !moduleIds.Contains(dependency.ModuleId.Trim()))
                Add(diagnostics, "DEPENDENCY_MODULE_MISSING", path + ".ModuleId", $"Module {dependency.ModuleId} is not declared.");
            if (dependency.Order < 0 || !orders.Add(dependency.Order))
                Add(diagnostics, "DEPENDENCY_ORDER_INVALID", path + ".Order", "Dependency order must be unique and zero or greater.");

            ActionFitSdkSourceDefinition source = profile.Sources.FirstOrDefault(item => item != null && string.Equals(item.Id, dependency.SourceId, StringComparison.Ordinal));
            if (source != null && !string.IsNullOrWhiteSpace(source.PackageId) &&
                !string.Equals(source.PackageId, dependency.PackageId, StringComparison.Ordinal))
            {
                Add(diagnostics, "DEPENDENCY_SOURCE_PACKAGE_MISMATCH", path, $"Dependency {dependency.PackageId} does not match source package {source.PackageId}.");
            }
            if (source != null && source.ResolveKind() == ActionFitSdkSourceKind.Registry)
            {
                bool scoped = profile.ScopedRegistries.Any(registry => registry != null &&
                    string.Equals((registry.Url ?? "").TrimEnd('/'), (source.Url ?? "").TrimEnd('/'), StringComparison.OrdinalIgnoreCase) &&
                    registry.Scopes.Any(scope => string.Equals(dependency.PackageId, scope, StringComparison.Ordinal) ||
                        dependency.PackageId.StartsWith(scope + ".", StringComparison.Ordinal)));
                if (!scoped)
                    Add(diagnostics, "DEPENDENCY_REGISTRY_SCOPE_MISSING", path, $"Registry dependency {dependency.PackageId} is not covered by a scoped registry with the same URL.");
            }
        }
    }

    private static void ValidateRegistries(ActionFitSdkInstallProfile profile, List<ActionFitSdkProfileDiagnostic> diagnostics)
    {
        var moduleIds = new HashSet<string>(profile.Modules.Where(item => item != null).Select(item => item.Id), StringComparer.Ordinal);
        var urls = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        for (int i = 0; i < profile.ScopedRegistries.Length; i++)
        {
            ActionFitSdkScopedRegistryDefinition registry = profile.ScopedRegistries[i];
            string path = $"ScopedRegistries[{i}]";
            if (registry == null)
            {
                Add(diagnostics, "REGISTRY_MISSING", path, "Scoped registry entry is null.");
                continue;
            }

            RequireText(diagnostics, registry.Name, path + ".Name", "REGISTRY_NAME_MISSING");
            RequireHttpsUrl(diagnostics, registry.Url, path + ".Url", "REGISTRY_URL_INVALID", true, profile.AllowedDomains);
            string normalizedUrl = (registry.Url ?? "").TrimEnd('/');
            if (!string.IsNullOrWhiteSpace(normalizedUrl) && !urls.Add(normalizedUrl))
                Add(diagnostics, "REGISTRY_URL_DUPLICATE", path + ".Url", $"Registry {normalizedUrl} is duplicated.");
            if (!string.IsNullOrWhiteSpace(registry.ModuleId) && !moduleIds.Contains(registry.ModuleId.Trim()))
                Add(diagnostics, "REGISTRY_MODULE_MISSING", path + ".ModuleId", $"Module {registry.ModuleId} is not declared.");
            if (registry.Scopes.Length == 0)
                Add(diagnostics, "REGISTRY_SCOPES_MISSING", path + ".Scopes", "Scoped registry requires at least one scope.");

            var scopes = new HashSet<string>(StringComparer.Ordinal);
            for (int j = 0; j < registry.Scopes.Length; j++)
            {
                string scope = (registry.Scopes[j] ?? "").Trim();
                if (!IsPackageId(scope))
                    Add(diagnostics, "REGISTRY_SCOPE_INVALID", $"{path}.Scopes[{j}]", $"Invalid package scope {scope}.");
                else if (!scopes.Add(scope))
                    Add(diagnostics, "REGISTRY_SCOPE_DUPLICATE", $"{path}.Scopes[{j}]", $"Scope {scope} is duplicated.");
            }
        }
    }

    private static void ValidateDetectionRules(ActionFitSdkInstallProfile profile, List<ActionFitSdkProfileDiagnostic> diagnostics)
    {
        var ids = new HashSet<string>(StringComparer.Ordinal);
        for (int i = 0; i < profile.DetectionRules.Length; i++)
        {
            ActionFitSdkDetectionRule rule = profile.DetectionRules[i];
            string path = $"DetectionRules[{i}]";
            if (rule == null)
            {
                Add(diagnostics, "DETECTION_RULE_MISSING", path, "Detection rule is null.");
                continue;
            }

            RequireIdentifier(diagnostics, rule.Id, path + ".Id", "DETECTION_RULE_ID_INVALID");
            if (!string.IsNullOrWhiteSpace(rule.Id) && !ids.Add(rule.Id.Trim()))
                Add(diagnostics, "DETECTION_RULE_ID_DUPLICATE", path + ".Id", $"Detection rule {rule.Id} is duplicated.");
            if (rule.ResolveKind() == ActionFitSdkDetectionKind.Unknown)
                Add(diagnostics, "DETECTION_KIND_INVALID", path + ".Kind", "Kind must be assetPath, dependency, or registry.");
            if (rule.ResolveClassification() == ActionFitSdkInstallationClassification.None)
                Add(diagnostics, "DETECTION_CLASSIFICATION_INVALID", path + ".Classification", "Classification must be adoptable, conflicting, or unsupported.");
            RequireText(diagnostics, rule.Value, path + ".Value", "DETECTION_VALUE_MISSING");
            RequireText(diagnostics, rule.Message, path + ".Message", "DETECTION_MESSAGE_MISSING");

            if (rule.ResolveKind() == ActionFitSdkDetectionKind.AssetPath)
                ValidateRelativeProjectPath(diagnostics, rule.Value, path + ".Value", "DETECTION_ASSET_PATH_INVALID");
            else if (rule.ResolveKind() == ActionFitSdkDetectionKind.Dependency)
                RequirePackageId(diagnostics, rule.Value, path + ".Value", "DETECTION_DEPENDENCY_INVALID");
            else if (rule.ResolveKind() == ActionFitSdkDetectionKind.Registry &&
                     !Uri.TryCreate(rule.Value, UriKind.Absolute, out _))
                Add(diagnostics, "DETECTION_REGISTRY_INVALID", path + ".Value", "Registry detection value must be an absolute URL.");
        }
    }

    private static void ValidateCachePath(List<ActionFitSdkProfileDiagnostic> diagnostics, string value, string path)
    {
        ValidateRelativeProjectPath(diagnostics, value, path, "SOURCE_CACHE_PATH_INVALID");
        string normalized = (value ?? "").Replace('\\', '/').Trim('/');
        if (!normalized.Contains("/ActionFit/", StringComparison.OrdinalIgnoreCase) &&
            !normalized.StartsWith("ActionFitSdkArtifacts/", StringComparison.OrdinalIgnoreCase))
        {
            Add(diagnostics, "SOURCE_CACHE_ROOT_UNSAFE", path, "Artifact cache path must be under an ActionFit-owned project folder.");
        }
    }

    private static void ValidateRelativeProjectPath(
        List<ActionFitSdkProfileDiagnostic> diagnostics,
        string value,
        string path,
        string code)
    {
        string normalized = (value ?? "").Replace('\\', '/').Trim();
        if (string.IsNullOrWhiteSpace(normalized) || Path.IsPathRooted(normalized) ||
            normalized.Split('/').Any(part => part == "..") || normalized.Contains(':'))
        {
            Add(diagnostics, code, path, "Path must be project-relative and must not contain traversal or drive segments.");
        }
    }

    private static void RequireIdentifier(List<ActionFitSdkProfileDiagnostic> diagnostics, string value, string path, string code)
    {
        if (string.IsNullOrWhiteSpace(value) || !IdentifierPattern.IsMatch(value.Trim()))
            Add(diagnostics, code, path, "Value must contain lowercase letters, digits, dots, underscores, or hyphens and start with a letter or digit.");
    }

    private static void RequirePackageId(List<ActionFitSdkProfileDiagnostic> diagnostics, string value, string path, string code)
    {
        if (!IsPackageId(value)) Add(diagnostics, code, path, $"Invalid package ID {value}.");
    }

    private static void RequireText(List<ActionFitSdkProfileDiagnostic> diagnostics, string value, string path, string code)
    {
        if (string.IsNullOrWhiteSpace(value)) Add(diagnostics, code, path, "Value is required.");
    }

    private static void RequireSemVer(List<ActionFitSdkProfileDiagnostic> diagnostics, string value, string path, string code)
    {
        if (!IsExactSemVer(value)) Add(diagnostics, code, path, $"Expected exact SemVer, found {value}.");
    }

    private static void RequireUnityVersion(List<ActionFitSdkProfileDiagnostic> diagnostics, string value, string path)
    {
        if (string.IsNullOrWhiteSpace(value) || !UnityVersionPattern.IsMatch(value.Trim()))
            Add(diagnostics, "UNITY_VERSION_INVALID", path, $"Expected Unity major.minor or major.minor.patch, found {value}.");
    }

    private static bool TryParseUnityVersion(string value, bool upperBound, out int[] parts)
    {
        Match match = Regex.Match(value ?? "", "^(?<major>[0-9]+)\\.(?<minor>[0-9]+)(?:\\.(?<patch>[0-9]+))?", RegexOptions.CultureInvariant);
        if (!match.Success)
        {
            parts = Array.Empty<int>();
            return false;
        }
        if (!int.TryParse(match.Groups["major"].Value, out int major) ||
            !int.TryParse(match.Groups["minor"].Value, out int minor) ||
            (match.Groups["patch"].Success && !int.TryParse(match.Groups["patch"].Value, out _)))
        {
            parts = Array.Empty<int>();
            return false;
        }
        int patch = match.Groups["patch"].Success
            ? int.Parse(match.Groups["patch"].Value)
            : upperBound ? int.MaxValue : 0;
        parts = new[]
        {
            major,
            minor,
            patch,
        };
        return true;
    }

    private static int CompareVersionParts(IReadOnlyList<int> left, IReadOnlyList<int> right)
    {
        for (int i = 0; i < 3; i++)
        {
            int comparison = left[i].CompareTo(right[i]);
            if (comparison != 0) return comparison;
        }
        return 0;
    }

    private static void RequireHttpsUrl(
        List<ActionFitSdkProfileDiagnostic> diagnostics,
        string value,
        string path,
        string code,
        bool enforceAllowedDomains,
        IEnumerable<string> allowedDomains)
    {
        if (!Uri.TryCreate(value, UriKind.Absolute, out Uri uri) ||
            !string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase) ||
            !string.IsNullOrEmpty(uri.UserInfo))
        {
            Add(diagnostics, code, path, "URL must be absolute HTTPS and must not contain embedded credentials.");
            return;
        }

        if (enforceAllowedDomains && !IsAllowedHttpsUrl(value, allowedDomains, out string error))
            Add(diagnostics, "SOURCE_DOMAIN_NOT_ALLOWED", path, error);
    }

    private static string NormalizeDomain(string value)
    {
        return (value ?? "").Trim().TrimEnd('.').ToLowerInvariant();
    }

    private static ActionFitSdkProfileValidationResult Result(List<ActionFitSdkProfileDiagnostic> diagnostics)
    {
        return new ActionFitSdkProfileValidationResult
        {
            Success = diagnostics.Count == 0,
            Diagnostics = diagnostics
                .OrderBy(item => item.Path, StringComparer.Ordinal)
                .ThenBy(item => item.Code, StringComparer.Ordinal)
                .ToArray(),
        };
    }

    private static void Add(List<ActionFitSdkProfileDiagnostic> diagnostics, string code, string path, string message)
    {
        diagnostics.Add(new ActionFitSdkProfileDiagnostic
        {
            Code = code,
            Path = path,
            Message = message,
        });
    }
}
#endif
