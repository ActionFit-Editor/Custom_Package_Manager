#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using UnityEditor.PackageManager;
using UnityEngine;

public enum ActionFitSdkResolutionOrigin
{
    None = 0,
    Installed = 1,
    LatestStable = 2,
}

[Serializable]
public sealed class ActionFitSdkResolvedSourceSnapshot
{
    public string SourceId = "";
    public string PackageId = "";
    public ActionFitSdkResolutionOrigin Origin;
    public string DependencyValue = "";
    public string Version = "";
    public string ImmutableRevision = "";
    public string PackageVersion = "";
    public string Sha256 = "";
    public string ArtifactUrl = "";
    public string MetadataUrl = "";
    public string MetadataContentHash = "";
    public string VersionFamily = "";
}

[Serializable]
public sealed class ActionFitSdkResolutionSnapshot
{
    public string ProfileId = "";
    public string ProfileVersion = "";
    public string UnityVersion = "";
    public string ManifestHash = "";
    public string PackagesLockHash = "";
    public string RegisteredPackagesHash = "";
    public ActionFitSdkResolvedSourceSnapshot[] Sources = Array.Empty<ActionFitSdkResolvedSourceSnapshot>();
}

[Serializable]
public sealed class ActionFitSdkResolutionResult
{
    public bool Success;
    public string Code = "";
    public string Message = "";
    public ActionFitSdkInstallProfile ResolvedProfile;
    public ActionFitSdkResolutionSnapshot Snapshot;
    public ActionFitSdkInstallationFinding[] Findings = Array.Empty<ActionFitSdkInstallationFinding>();
}

[Serializable]
public sealed class ActionFitSdkLatestMetadataDocument
{
    public string PackageId = "";
    public ActionFitSdkLatestMetadataRelease[] Releases = Array.Empty<ActionFitSdkLatestMetadataRelease>();
}

[Serializable]
public sealed class ActionFitSdkLatestMetadataRelease
{
    public string Version = "";
    public string MinimumUnityVersion = "";
    public string MaximumUnityVersion = "";
    public string Url = "";
    public string ImmutableRevision = "";
    public string PackageVersion = "";
    public string Sha256 = "";
}

internal static class ActionFitSdkLatestResolver
{
    private const int MaximumMetadataCharacters = 16 * 1024 * 1024;
    private static readonly Regex StableSemVerPattern = new(
        "^(0|[1-9][0-9]*)\\.(0|[1-9][0-9]*)\\.(0|[1-9][0-9]*)(?:\\+[0-9A-Za-z.-]+)?$",
        RegexOptions.CultureInvariant);
    private static readonly Regex Sha256Pattern = new("^[0-9a-fA-F]{64}$", RegexOptions.CultureInvariant);

    internal static Func<string, CancellationToken, Task<string>> MetadataLoaderOverride;

    public static async Task<ActionFitSdkResolutionResult> ResolveAsync(
        ActionFitSdkInstallProfile profile,
        string[] selectedModuleIds,
        ActionFitSdkProjectContext context,
        CancellationToken cancellationToken)
    {
        context ??= ActionFitSdkProjectContext.ForCurrentProject();
        ActionFitSdkProfileValidationResult validation = ActionFitSdkInstallProfileValidator.Validate(profile);
        if (!validation.Success)
            return Failure("PROFILE_INVALID", validation.FormatMessage());
        if (!profile.RequiresAsyncResolution())
            return Failure("RESOLUTION_NOT_REQUIRED", "The profile contains no AnyInstalledElseLatestStable source.");

        try
        {
            context.Validate();
            cancellationToken.ThrowIfCancellationRequested();
            string manifest = File.ReadAllText(context.ManifestPath);
            ActionFitPackageManifestUtility.Validate(manifest);
            string[] modules = ActionFitSdkInstallPlanner.ResolveModules(profile, selectedModuleIds);
            var selected = new HashSet<string>(modules, StringComparer.Ordinal);
            ActionFitSdkDependencyDefinition[] dependencies = profile.Dependencies
                .Where(dependency => dependency != null &&
                    (string.IsNullOrWhiteSpace(dependency.ModuleId) || selected.Contains(dependency.ModuleId)))
                .OrderBy(dependency => dependency.Order)
                .ToArray();
            ActionFitSdkProjectStateSnapshot projectState = ActionFitSdkProjectStateSnapshot.Read(context, manifest);
            var findings = new List<ActionFitSdkInstallationFinding>();
            var states = new List<ActionFitSdkResolutionWorkItem>();
            foreach (ActionFitSdkDependencyDefinition dependency in dependencies)
            {
                ActionFitSdkSourceDefinition source = profile.Sources.First(item => item != null &&
                    string.Equals(item.Id, dependency.SourceId, StringComparison.Ordinal));
                if (source.ResolvePolicy() != ActionFitSdkResolutionPolicy.AnyInstalledElseLatestStable)
                    continue;
                states.Add(InspectInstalledSource(source, dependency, projectState, findings));
            }

            ValidateVersionFamilies(states, findings);
            if (HasBlockingFinding(findings))
                return Failure("INSTALLED_STATE_BLOCKED", "Installed SDK state is unresolved, partial, or inconsistent.", findings);

            var metadata = new Dictionary<string, ActionFitSdkMetadataSnapshot>(StringComparer.Ordinal);
            foreach (ActionFitSdkResolutionWorkItem state in states.Where(item => !item.Installed))
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (metadata.ContainsKey(state.Source.Id)) continue;
                metadata[state.Source.Id] = await LoadMetadataAsync(profile, state.Source, cancellationToken);
            }

            ResolveMissingSources(states, metadata, profile, findings);
            if (HasBlockingFinding(findings))
                return Failure("LATEST_STABLE_UNAVAILABLE", "A compatible latest stable SDK snapshot could not be resolved.", findings);

            ActionFitSdkInstallProfile resolvedProfile = JsonUtility.FromJson<ActionFitSdkInstallProfile>(profile.ToJson());
            foreach (ActionFitSdkResolutionWorkItem state in states)
            {
                if (state.Installed) continue;
                ActionFitSdkSourceDefinition resolved = resolvedProfile.Sources.First(item => item != null &&
                    string.Equals(item.Id, state.Source.Id, StringComparison.Ordinal));
                ApplyCandidate(resolved, state.Candidate);
            }

            var snapshot = new ActionFitSdkResolutionSnapshot
            {
                ProfileId = profile.ProfileId,
                ProfileVersion = profile.ProfileVersion,
                UnityVersion = Application.unityVersion,
                ManifestHash = projectState.ManifestHash,
                PackagesLockHash = projectState.PackagesLockHash,
                RegisteredPackagesHash = projectState.RegisteredPackagesHash,
                Sources = states.Select(BuildSnapshot).OrderBy(item => item.SourceId, StringComparer.Ordinal).ToArray(),
            };
            return new ActionFitSdkResolutionResult
            {
                Success = true,
                Code = "RESOLVED",
                Message = "Resolved installed SDK packages and compatible latest stable fallbacks without changing project state.",
                ResolvedProfile = resolvedProfile,
                Snapshot = snapshot,
                Findings = findings.ToArray(),
            };
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            return Failure("RESOLUTION_FAILED", ex.Message);
        }
    }

    public static async Task RevalidateAsync(
        ActionFitSdkInstallProfile profile,
        ActionFitSdkResolutionSnapshot snapshot,
        CancellationToken cancellationToken)
    {
        if (snapshot == null) return;
        foreach (IGrouping<string, ActionFitSdkResolvedSourceSnapshot> group in snapshot.Sources
                     .Where(item => item != null && item.Origin == ActionFitSdkResolutionOrigin.LatestStable)
                     .GroupBy(item => item.MetadataUrl, StringComparer.Ordinal))
        {
            ActionFitSdkResolvedSourceSnapshot first = group.First();
            string content = await LoadTextAsync(profile, first.MetadataUrl, cancellationToken);
            string hash = Hash(content);
            if (group.Any(item => !string.Equals(item.MetadataContentHash, hash, StringComparison.OrdinalIgnoreCase)))
                throw new InvalidOperationException("Official SDK metadata changed after plan preparation. Resolve and review a new plan.");
        }
    }

    private static ActionFitSdkResolutionWorkItem InspectInstalledSource(
        ActionFitSdkSourceDefinition source,
        ActionFitSdkDependencyDefinition dependency,
        ActionFitSdkProjectStateSnapshot projectState,
        List<ActionFitSdkInstallationFinding> findings)
    {
        string manifestValue = ActionFitPackageManifestUtility.GetDependency(projectState.Manifest, dependency.PackageId);
        bool hasLock = projectState.LockEntries.TryGetValue(dependency.PackageId, out ActionFitSdkPackageLockEntry lockEntry);
        bool hasRegistered = projectState.RegisteredEntries.TryGetValue(dependency.PackageId, out ActionFitSdkRegisteredPackage registered);
        bool currentProject = projectState.RegisteredStateObserved;
        var item = new ActionFitSdkResolutionWorkItem
        {
            Source = source,
            Dependency = dependency,
        };

        if (CountManifestDependency(projectState.Manifest, dependency.PackageId) > 1)
        {
            findings.Add(Finding("dependency-manifest-duplicate", ActionFitSdkInstallationClassification.Conflicting, dependency.PackageId,
                "manifest.json declares the SDK package more than once."));
            return item;
        }

        if (string.IsNullOrWhiteSpace(manifestValue) && !hasLock && !hasRegistered)
            return item;
        if (!string.IsNullOrWhiteSpace(manifestValue) && !hasLock)
        {
            findings.Add(Finding("dependency-unresolved", ActionFitSdkInstallationClassification.Unsupported, dependency.PackageId,
                "The SDK is declared in manifest.json but is not resolved in packages-lock.json."));
            return item;
        }
        if (!hasLock || string.IsNullOrWhiteSpace(lockEntry.Version))
        {
            findings.Add(Finding("dependency-lock-invalid", ActionFitSdkInstallationClassification.Unsupported, dependency.PackageId,
                "The resolved SDK package has no valid packages-lock.json entry."));
            return item;
        }
        if (currentProject && !hasRegistered)
        {
            findings.Add(Finding("dependency-registration-missing", ActionFitSdkInstallationClassification.Unsupported, dependency.PackageId,
                "The SDK is present in package files but is not registered by the open Unity Editor."));
            return item;
        }
        if (!LockSourceMatches(source.ResolveKind(), lockEntry.Source))
        {
            findings.Add(Finding("dependency-source-ambiguous", ActionFitSdkInstallationClassification.Unsupported, dependency.PackageId,
                $"The resolved package source '{lockEntry.Source}' does not match the declared {source.Kind} source."));
            return item;
        }
        if (source.ResolveKind() == ActionFitSdkSourceKind.Registry &&
            !ActionFitSdkInstallProfileValidator.IsExactSemVer(lockEntry.Version))
        {
            findings.Add(Finding("dependency-version-invalid", ActionFitSdkInstallationClassification.Unsupported, dependency.PackageId,
                "The resolved registry SDK version is not an exact SemVer."));
            return item;
        }
        if (source.ResolveKind() == ActionFitSdkSourceKind.Git &&
            !HasImmutableGitResolution(lockEntry))
        {
            findings.Add(Finding("dependency-git-revision-mutable", ActionFitSdkInstallationClassification.Unsupported, dependency.PackageId,
                "The resolved Git SDK package is not pinned to a commit or exact SemVer tag."));
            return item;
        }
        if (source.ResolveKind() == ActionFitSdkSourceKind.Registry &&
            !string.IsNullOrWhiteSpace(manifestValue) &&
            !string.Equals(manifestValue, lockEntry.Version, StringComparison.Ordinal))
        {
            findings.Add(Finding("dependency-version-inconsistent", ActionFitSdkInstallationClassification.Conflicting, dependency.PackageId,
                "manifest.json and packages-lock.json resolve different SDK versions."));
            return item;
        }
        if (hasRegistered && source.ResolveKind() == ActionFitSdkSourceKind.Registry &&
            !string.Equals(registered.Version, lockEntry.Version, StringComparison.Ordinal))
        {
            findings.Add(Finding("dependency-registration-inconsistent", ActionFitSdkInstallationClassification.Conflicting, dependency.PackageId,
                "Unity's registered package version differs from packages-lock.json."));
            return item;
        }

        string dependencyValue = !string.IsNullOrWhiteSpace(manifestValue)
            ? manifestValue
            : ResolveInstalledDependencyValue(source, lockEntry);
        if (string.IsNullOrWhiteSpace(dependencyValue))
        {
            findings.Add(Finding("dependency-value-ambiguous", ActionFitSdkInstallationClassification.Unsupported, dependency.PackageId,
                "The installed SDK source cannot be represented as an exact direct dependency."));
            return item;
        }

        item.Installed = true;
        item.InstalledDependencyValue = dependencyValue;
        item.InstalledVersion = !string.IsNullOrWhiteSpace(registered?.Version) ? registered.Version : lockEntry.Version;
        item.InstalledRevision = lockEntry.Hash;
        return item;
    }

    private static int CountManifestDependency(string manifest, string packageId)
    {
        return Regex.Matches(
            manifest ?? "",
            $"^\\s*\\\"{Regex.Escape(packageId)}\\\"\\s*:",
            RegexOptions.Multiline | RegexOptions.CultureInvariant).Count;
    }

    private static bool HasImmutableGitResolution(ActionFitSdkPackageLockEntry entry)
    {
        if (ActionFitSdkInstallProfileValidator.IsImmutableGitRevision(entry.Hash)) return true;
        int fragment = (entry.Version ?? "").LastIndexOf('#');
        string revision = fragment >= 0 ? entry.Version[(fragment + 1)..] : entry.Version;
        return ActionFitSdkInstallProfileValidator.IsImmutableGitRevision(revision);
    }

    private static void ValidateVersionFamilies(
        IReadOnlyCollection<ActionFitSdkResolutionWorkItem> states,
        List<ActionFitSdkInstallationFinding> findings)
    {
        foreach (IGrouping<string, ActionFitSdkResolutionWorkItem> family in states
                     .Where(item => !string.IsNullOrWhiteSpace(item.Source.VersionFamily))
                     .GroupBy(item => item.Source.VersionFamily, StringComparer.Ordinal))
        {
            string[] versions = family.Where(item => item.Installed)
                .Select(item => item.InstalledVersion)
                .Distinct(StringComparer.Ordinal)
                .ToArray();
            if (versions.Length > 1)
            {
                findings.Add(Finding("dependency-family-version-conflict", ActionFitSdkInstallationClassification.Conflicting, family.Key,
                    "Installed packages in a version-coupled SDK family do not share one resolved version."));
            }
        }
    }

    private static async Task<ActionFitSdkMetadataSnapshot> LoadMetadataAsync(
        ActionFitSdkInstallProfile profile,
        ActionFitSdkSourceDefinition source,
        CancellationToken cancellationToken)
    {
        string content = await LoadTextAsync(profile, source.MetadataUrl, cancellationToken);
        ActionFitSdkLatestMetadataDocument document;
        try
        {
            document = JsonUtility.FromJson<ActionFitSdkLatestMetadataDocument>(content);
        }
        catch (Exception)
        {
            document = null;
        }
        ActionFitSdkLatestMetadataRelease[] releases = document?.Releases ?? Array.Empty<ActionFitSdkLatestMetadataRelease>();
        if (releases.Length > 0)
        {
            if (!string.Equals(document.PackageId, source.PackageId, StringComparison.Ordinal))
                throw new InvalidOperationException($"Official metadata package ID does not match {source.PackageId}.");
        }
        else
        {
            releases = ActionFitSdkOfficialMetadataParser.Parse(source, content);
        }

        ActionFitSdkLatestMetadataRelease[] candidates = releases
            .Where(release => IsValidCandidate(profile, source, release))
            .OrderByDescending(release => release.Version, ActionFitSdkSemVerComparer.Instance)
            .ToArray();
        string duplicate = candidates.GroupBy(item => item.Version, StringComparer.Ordinal)
            .FirstOrDefault(group => group.Count() > 1)?.Key;
        if (!string.IsNullOrWhiteSpace(duplicate))
            throw new InvalidOperationException($"Official metadata contains ambiguous duplicate release version {duplicate} for {source.PackageId}.");
        return new ActionFitSdkMetadataSnapshot
        {
            ContentHash = Hash(content),
            Candidates = candidates,
        };
    }

    private static bool IsValidCandidate(
        ActionFitSdkInstallProfile profile,
        ActionFitSdkSourceDefinition source,
        ActionFitSdkLatestMetadataRelease release)
    {
        if (release == null || !StableSemVerPattern.IsMatch((release.Version ?? "").Trim()) ||
            !IsUnityCompatible(release.MinimumUnityVersion, release.MaximumUnityVersion, Application.unityVersion))
            return false;

        switch (source.ResolveLatestResolver())
        {
            case ActionFitSdkLatestResolverKind.RegistryMetadata:
                return source.ResolveKind() == ActionFitSdkSourceKind.Registry;
            case ActionFitSdkLatestResolverKind.GitRelease:
                return source.ResolveKind() == ActionFitSdkSourceKind.Git &&
                       ActionFitSdkInstallProfileValidator.IsImmutableGitRevision(release.ImmutableRevision);
            case ActionFitSdkLatestResolverKind.ArtifactMetadata:
                return source.ResolveKind() == ActionFitSdkSourceKind.Artifact &&
                       ActionFitSdkInstallProfileValidator.IsExactSemVer(release.PackageVersion) &&
                       Sha256Pattern.IsMatch((release.Sha256 ?? "").Trim()) &&
                       ActionFitSdkInstallProfileValidator.IsAllowedHttpsUrl(release.Url, profile.AllowedDomains, out _);
            default:
                return false;
        }
    }

    private static void ResolveMissingSources(
        IReadOnlyCollection<ActionFitSdkResolutionWorkItem> states,
        IReadOnlyDictionary<string, ActionFitSdkMetadataSnapshot> metadata,
        ActionFitSdkInstallProfile profile,
        List<ActionFitSdkInstallationFinding> findings)
    {
        foreach (ActionFitSdkResolutionWorkItem state in states.Where(item => !item.Installed && string.IsNullOrWhiteSpace(item.Source.VersionFamily)))
        {
            state.Candidate = metadata[state.Source.Id].Candidates.FirstOrDefault();
            if (state.Candidate == null)
                findings.Add(Finding("latest-stable-missing", ActionFitSdkInstallationClassification.Unsupported, state.Dependency.PackageId,
                    $"No stable {Application.unityVersion}-compatible release exists in the declared official metadata."));
        }

        foreach (IGrouping<string, ActionFitSdkResolutionWorkItem> family in states
                     .Where(item => !item.Installed && !string.IsNullOrWhiteSpace(item.Source.VersionFamily))
                     .GroupBy(item => item.Source.VersionFamily, StringComparer.Ordinal))
        {
            ActionFitSdkResolutionWorkItem[] installedFamily = states
                .Where(item => item.Installed && string.Equals(item.Source.VersionFamily, family.Key, StringComparison.Ordinal))
                .ToArray();
            string version = installedFamily.Length > 0
                ? installedFamily[0].InstalledVersion
                : family
                    .Select(item => metadata[item.Source.Id].Candidates.Select(candidate => candidate.Version).Distinct(StringComparer.Ordinal))
                    .Aggregate((left, right) => left.Intersect(right, StringComparer.Ordinal))
                    .OrderByDescending(item => item, ActionFitSdkSemVerComparer.Instance)
                    .FirstOrDefault();
            if (string.IsNullOrWhiteSpace(version))
            {
                findings.Add(Finding("latest-family-version-missing", ActionFitSdkInstallationClassification.Unsupported, family.Key,
                    "No common stable Unity-compatible version exists for the coupled SDK family."));
                continue;
            }
            foreach (ActionFitSdkResolutionWorkItem state in family)
            {
                state.Candidate = metadata[state.Source.Id].Candidates.FirstOrDefault(candidate =>
                    string.Equals(candidate.Version, version, StringComparison.Ordinal));
                if (state.Candidate == null)
                {
                    findings.Add(Finding("installed-family-version-unavailable", ActionFitSdkInstallationClassification.Unsupported, state.Dependency.PackageId,
                        $"The installed family version {version} is not available as a stable compatible release for the missing package."));
                }
            }
        }

        foreach (ActionFitSdkResolutionWorkItem state in states.Where(item => !item.Installed && item.Candidate != null))
            state.MetadataContentHash = metadata[state.Source.Id].ContentHash;
    }

    private static void ApplyCandidate(ActionFitSdkSourceDefinition source, ActionFitSdkLatestMetadataRelease candidate)
    {
        source.ImmutableVersion = candidate.Version;
        switch (source.ResolveKind())
        {
            case ActionFitSdkSourceKind.Registry:
                break;
            case ActionFitSdkSourceKind.Git:
                source.ImmutableRevision = candidate.ImmutableRevision;
                break;
            case ActionFitSdkSourceKind.Artifact:
                source.Url = candidate.Url;
                source.PackageVersion = candidate.PackageVersion;
                source.Sha256 = candidate.Sha256.ToLowerInvariant();
                source.CacheRelativePath = source.CacheRelativePath
                    .Replace("{version}", candidate.Version)
                    .Replace("{packageVersion}", candidate.PackageVersion);
                break;
        }
    }

    private static ActionFitSdkResolvedSourceSnapshot BuildSnapshot(ActionFitSdkResolutionWorkItem state)
    {
        if (state.Installed)
        {
            return new ActionFitSdkResolvedSourceSnapshot
            {
                SourceId = state.Source.Id,
                PackageId = state.Dependency.PackageId,
                Origin = ActionFitSdkResolutionOrigin.Installed,
                DependencyValue = state.InstalledDependencyValue,
                Version = state.InstalledVersion,
                ImmutableRevision = state.InstalledRevision,
                VersionFamily = state.Source.VersionFamily,
            };
        }

        ActionFitSdkLatestMetadataRelease candidate = state.Candidate;
        return new ActionFitSdkResolvedSourceSnapshot
        {
            SourceId = state.Source.Id,
            PackageId = state.Dependency.PackageId,
            Origin = ActionFitSdkResolutionOrigin.LatestStable,
            DependencyValue = ResolveCandidateDependencyValue(state.Source, candidate),
            Version = candidate.Version,
            ImmutableRevision = candidate.ImmutableRevision,
            PackageVersion = candidate.PackageVersion,
            Sha256 = candidate.Sha256,
            ArtifactUrl = candidate.Url,
            MetadataUrl = state.Source.MetadataUrl,
            MetadataContentHash = state.MetadataContentHash,
            VersionFamily = state.Source.VersionFamily,
        };
    }

    private static string ResolveCandidateDependencyValue(
        ActionFitSdkSourceDefinition source,
        ActionFitSdkLatestMetadataRelease candidate)
    {
        switch (source.ResolveKind())
        {
            case ActionFitSdkSourceKind.Registry:
                return candidate.Version;
            case ActionFitSdkSourceKind.Git:
                string url = source.Url.TrimEnd('#');
                if (!string.IsNullOrWhiteSpace(source.GitSubpath)) url += "?path=" + source.GitSubpath.Trim();
                return url + "#" + candidate.ImmutableRevision;
            case ActionFitSdkSourceKind.Artifact:
                string cache = source.CacheRelativePath
                    .Replace("{version}", candidate.Version)
                    .Replace("{packageVersion}", candidate.PackageVersion);
                return "file:../" + cache.Replace('\\', '/').TrimStart('/');
            default:
                return "";
        }
    }

    private static string ResolveInstalledDependencyValue(ActionFitSdkSourceDefinition source, ActionFitSdkPackageLockEntry entry)
    {
        switch (source.ResolveKind())
        {
            case ActionFitSdkSourceKind.Registry:
                return ActionFitSdkInstallProfileValidator.IsExactSemVer(entry.Version) ? entry.Version : "";
            case ActionFitSdkSourceKind.Git:
                if (entry.Version.StartsWith("https://", StringComparison.OrdinalIgnoreCase)) return entry.Version;
                string revision = !string.IsNullOrWhiteSpace(entry.Hash) ? entry.Hash : entry.Version;
                if (!ActionFitSdkInstallProfileValidator.IsImmutableGitRevision(revision)) return "";
                string url = source.Url.TrimEnd('#');
                if (!string.IsNullOrWhiteSpace(source.GitSubpath)) url += "?path=" + source.GitSubpath.Trim();
                return url + "#" + revision;
            case ActionFitSdkSourceKind.Artifact:
                return entry.Version.StartsWith("file:", StringComparison.OrdinalIgnoreCase) ? entry.Version : "";
            default:
                return "";
        }
    }

    private static bool LockSourceMatches(ActionFitSdkSourceKind kind, string source)
    {
        return kind switch
        {
            ActionFitSdkSourceKind.Registry => string.Equals(source, "registry", StringComparison.OrdinalIgnoreCase),
            ActionFitSdkSourceKind.Git => string.Equals(source, "git", StringComparison.OrdinalIgnoreCase),
            ActionFitSdkSourceKind.Artifact => string.Equals(source, "local", StringComparison.OrdinalIgnoreCase) ||
                                               string.Equals(source, "embedded", StringComparison.OrdinalIgnoreCase),
            _ => false,
        };
    }

    private static async Task<string> LoadTextAsync(
        ActionFitSdkInstallProfile profile,
        string url,
        CancellationToken cancellationToken)
    {
        if (!ActionFitSdkInstallProfileValidator.IsAllowedHttpsUrl(url, profile.AllowedDomains, out string error))
            throw new InvalidOperationException($"SDK metadata URL was rejected: {error}");
        if (MetadataLoaderOverride != null)
            return ValidateMetadataContent(await MetadataLoaderOverride(url, cancellationToken));

        using var handler = new HttpClientHandler { AllowAutoRedirect = false };
        using var client = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(30) };
        var current = new Uri(url, UriKind.Absolute);
        for (int redirectCount = 0; redirectCount <= 5; redirectCount++)
        {
            using HttpResponseMessage response = await client.GetAsync(current, cancellationToken);
            int statusCode = (int)response.StatusCode;
            if (statusCode >= 300 && statusCode <= 399)
            {
                if (redirectCount == 5 || response.Headers.Location == null)
                    throw new InvalidOperationException("SDK metadata exceeded the safe redirect limit or returned no redirect location.");
                Uri redirected = response.Headers.Location.IsAbsoluteUri
                    ? response.Headers.Location
                    : new Uri(current, response.Headers.Location);
                if (!ActionFitSdkInstallProfileValidator.IsAllowedRedirectUrl(redirected.AbsoluteUri, profile.AllowedDomains, out string redirectError))
                    throw new InvalidOperationException($"SDK metadata redirect was rejected: {redirectError}");
                current = redirected;
                continue;
            }
            response.EnsureSuccessStatusCode();
            if (response.Content.Headers.ContentLength > MaximumMetadataCharacters)
                throw new InvalidOperationException("SDK metadata exceeds the 16 MiB safety limit.");
            return ValidateMetadataContent(await response.Content.ReadAsStringAsync());
        }
        throw new InvalidOperationException("SDK metadata request did not reach a final response.");
    }

    private static string ValidateMetadataContent(string content)
    {
        if (string.IsNullOrWhiteSpace(content))
            throw new InvalidOperationException("SDK metadata response is empty.");
        if (content.Length > MaximumMetadataCharacters)
            throw new InvalidOperationException("SDK metadata exceeds the 16 MiB safety limit.");
        return content;
    }

    private static bool IsUnityCompatible(string minimum, string maximum, string current)
    {
        if (string.IsNullOrWhiteSpace(minimum) && string.IsNullOrWhiteSpace(maximum)) return true;
        if (!TryUnityVersion(current, out Version currentVersion)) return false;
        if (!string.IsNullOrWhiteSpace(minimum) &&
            (!TryUnityVersion(minimum, out Version minimumVersion) || currentVersion.CompareTo(minimumVersion) < 0)) return false;
        return string.IsNullOrWhiteSpace(maximum) ||
               (TryUnityVersion(maximum, out Version maximumVersion) && currentVersion.CompareTo(maximumVersion) <= 0);
    }

    private static bool TryUnityVersion(string value, out Version version)
    {
        Match match = Regex.Match(value ?? "", "^(?<major>[0-9]+)\\.(?<minor>[0-9]+)(?:\\.(?<patch>[0-9]+))?", RegexOptions.CultureInvariant);
        if (!match.Success)
        {
            version = null;
            return false;
        }
        int patch = match.Groups["patch"].Success ? int.Parse(match.Groups["patch"].Value) : 0;
        version = new Version(int.Parse(match.Groups["major"].Value), int.Parse(match.Groups["minor"].Value), patch);
        return true;
    }

    private static string Hash(string value)
    {
        using SHA256 sha256 = SHA256.Create();
        return BitConverter.ToString(sha256.ComputeHash(Encoding.UTF8.GetBytes(value ?? ""))).Replace("-", "").ToLowerInvariant();
    }

    private static bool HasBlockingFinding(IEnumerable<ActionFitSdkInstallationFinding> findings)
    {
        return findings.Any(item => item.Classification == ActionFitSdkInstallationClassification.Conflicting ||
                                    item.Classification == ActionFitSdkInstallationClassification.Unsupported);
    }

    private static ActionFitSdkInstallationFinding Finding(
        string ruleId,
        ActionFitSdkInstallationClassification classification,
        string value,
        string message)
    {
        return new ActionFitSdkInstallationFinding
        {
            RuleId = ruleId,
            Classification = classification,
            Kind = "dependency",
            Value = value,
            Message = message,
        };
    }

    private static ActionFitSdkResolutionResult Failure(
        string code,
        string message,
        IEnumerable<ActionFitSdkInstallationFinding> findings = null)
    {
        return new ActionFitSdkResolutionResult
        {
            Success = false,
            Code = code,
            Message = message,
            Findings = (findings ?? Array.Empty<ActionFitSdkInstallationFinding>()).ToArray(),
        };
    }

    private sealed class ActionFitSdkResolutionWorkItem
    {
        public ActionFitSdkSourceDefinition Source;
        public ActionFitSdkDependencyDefinition Dependency;
        public bool Installed;
        public string InstalledDependencyValue = "";
        public string InstalledVersion = "";
        public string InstalledRevision = "";
        public string MetadataContentHash = "";
        public ActionFitSdkLatestMetadataRelease Candidate;
    }

    private sealed class ActionFitSdkMetadataSnapshot
    {
        public string ContentHash = "";
        public ActionFitSdkLatestMetadataRelease[] Candidates = Array.Empty<ActionFitSdkLatestMetadataRelease>();
    }
}

internal sealed class ActionFitSdkProjectStateSnapshot
{
    public string Manifest = "";
    public string ManifestHash = "";
    public string PackagesLockHash = "";
    public string RegisteredPackagesHash = "";
    public bool RegisteredStateObserved;
    public Dictionary<string, ActionFitSdkPackageLockEntry> LockEntries = new(StringComparer.Ordinal);
    public Dictionary<string, ActionFitSdkRegisteredPackage> RegisteredEntries = new(StringComparer.Ordinal);

    public static ActionFitSdkProjectStateSnapshot Read(ActionFitSdkProjectContext context, string manifest)
    {
        string lockJson = File.Exists(context.PackagesLockPath) ? File.ReadAllText(context.PackagesLockPath) : "";
        var snapshot = new ActionFitSdkProjectStateSnapshot
        {
            Manifest = manifest,
            ManifestHash = ActionFitSdkInstallPlanner.Hash(manifest),
            PackagesLockHash = ActionFitSdkInstallPlanner.Hash(lockJson),
            LockEntries = ActionFitSdkPackageLockReader.Read(lockJson),
        };

        if (PathEquals(context.ProjectRoot, ActionFitPackagePaths.ProjectRoot))
        {
            snapshot.RegisteredStateObserved = true;
            PackageInfo[] registered = PackageInfo.GetAllRegisteredPackages() ?? Array.Empty<PackageInfo>();
            foreach (PackageInfo package in registered.Where(item => item != null).OrderBy(item => item.name, StringComparer.Ordinal))
            {
                if (snapshot.RegisteredEntries.ContainsKey(package.name))
                    throw new InvalidOperationException($"Unity registered duplicate package ID {package.name}.");
                snapshot.RegisteredEntries[package.name] = new ActionFitSdkRegisteredPackage
                {
                    PackageId = package.name,
                    Version = package.version ?? "",
                    Source = package.source.ToString(),
                };
            }
        }
        snapshot.RegisteredPackagesHash = ActionFitSdkInstallPlanner.Hash(string.Join("\n", snapshot.RegisteredEntries.Values
            .OrderBy(item => item.PackageId, StringComparer.Ordinal)
            .Select(item => item.PackageId + "|" + item.Version + "|" + item.Source)));
        return snapshot;
    }

    private static bool PathEquals(string left, string right)
    {
        return string.Equals(
            Path.GetFullPath(left).TrimEnd(Path.DirectorySeparatorChar),
            Path.GetFullPath(right).TrimEnd(Path.DirectorySeparatorChar),
            StringComparison.OrdinalIgnoreCase);
    }
}

internal sealed class ActionFitSdkPackageLockEntry
{
    public string PackageId = "";
    public string Version = "";
    public string Source = "";
    public string Hash = "";
}

internal sealed class ActionFitSdkRegisteredPackage
{
    public string PackageId = "";
    public string Version = "";
    public string Source = "";
}

internal static class ActionFitSdkPackageLockReader
{
    public static Dictionary<string, ActionFitSdkPackageLockEntry> Read(string json)
    {
        var entries = new Dictionary<string, ActionFitSdkPackageLockEntry>(StringComparer.Ordinal);
        if (string.IsNullOrWhiteSpace(json)) return entries;
        int dependencies = json.IndexOf("\"dependencies\"", StringComparison.Ordinal);
        int open = dependencies >= 0 ? json.IndexOf('{', dependencies) : -1;
        int close = open >= 0 ? FindMatchingBrace(json, open) : -1;
        if (open < 0 || close < 0) throw new InvalidOperationException("packages-lock.json dependencies block is invalid.");

        int index = open + 1;
        while (index < close)
        {
            SkipWhitespaceAndComma(json, ref index, close);
            if (index >= close) break;
            string packageId = ReadString(json, ref index, close);
            SkipWhitespace(json, ref index, close);
            if (index >= close || json[index++] != ':') throw new InvalidOperationException("packages-lock.json package entry is invalid.");
            SkipWhitespace(json, ref index, close);
            if (index >= close || json[index] != '{') throw new InvalidOperationException("packages-lock.json package value is not an object.");
            int objectClose = FindMatchingBrace(json, index);
            string body = json.Substring(index, objectClose - index + 1);
            if (entries.ContainsKey(packageId))
                throw new InvalidOperationException($"packages-lock.json contains duplicate package ID {packageId}.");
            entries.Add(packageId, new ActionFitSdkPackageLockEntry
            {
                PackageId = packageId,
                Version = ReadProperty(body, "version"),
                Source = ReadProperty(body, "source"),
                Hash = ReadProperty(body, "hash"),
            });
            index = objectClose + 1;
        }
        return entries;
    }

    private static string ReadProperty(string json, string property)
    {
        Match match = Regex.Match(json, $"\\\"{Regex.Escape(property)}\\\"\\s*:\\s*\\\"(?<value>(?:\\\\.|[^\\\"\\\\])*)\\\"", RegexOptions.CultureInvariant);
        return match.Success ? Regex.Unescape(match.Groups["value"].Value) : "";
    }

    private static string ReadString(string json, ref int index, int limit)
    {
        if (index >= limit || json[index++] != '"') throw new InvalidOperationException("packages-lock.json expected a package ID string.");
        var builder = new StringBuilder();
        bool escaped = false;
        while (index < limit)
        {
            char value = json[index++];
            if (escaped)
            {
                builder.Append('\\').Append(value);
                escaped = false;
            }
            else if (value == '\\') escaped = true;
            else if (value == '"') return Regex.Unescape(builder.ToString());
            else builder.Append(value);
        }
        throw new InvalidOperationException("packages-lock.json contains an unterminated package ID.");
    }

    private static void SkipWhitespaceAndComma(string json, ref int index, int limit)
    {
        while (index < limit && (char.IsWhiteSpace(json[index]) || json[index] == ',')) index++;
    }

    private static void SkipWhitespace(string json, ref int index, int limit)
    {
        while (index < limit && char.IsWhiteSpace(json[index])) index++;
    }

    private static int FindMatchingBrace(string json, int open)
    {
        int depth = 0;
        bool inString = false;
        bool escaped = false;
        for (int index = open; index < json.Length; index++)
        {
            char value = json[index];
            if (inString)
            {
                if (escaped) escaped = false;
                else if (value == '\\') escaped = true;
                else if (value == '"') inString = false;
                continue;
            }
            if (value == '"') inString = true;
            else if (value == '{') depth++;
            else if (value == '}' && --depth == 0) return index;
        }
        return -1;
    }
}

internal static class ActionFitSdkOfficialMetadataParser
{
    private static readonly Regex StableVersionPattern = new(
        "^v?(?<version>(0|[1-9][0-9]*)\\.(0|[1-9][0-9]*)\\.(0|[1-9][0-9]*)(?:\\+[0-9A-Za-z.-]+)?)$",
        RegexOptions.CultureInvariant);

    public static ActionFitSdkLatestMetadataRelease[] Parse(ActionFitSdkSourceDefinition source, string json)
    {
        return source.ResolveLatestResolver() switch
        {
            ActionFitSdkLatestResolverKind.RegistryMetadata => ParseVersionMap(source, json, false),
            ActionFitSdkLatestResolverKind.ArtifactMetadata => ParseVersionMap(source, json, true),
            ActionFitSdkLatestResolverKind.GitRelease => ParseGitReleases(json),
            _ => Array.Empty<ActionFitSdkLatestMetadataRelease>(),
        };
    }

    private static ActionFitSdkLatestMetadataRelease[] ParseVersionMap(
        ActionFitSdkSourceDefinition source,
        string json,
        bool artifact)
    {
        string identity = FirstNonEmpty(
            ReadStringProperty(json, "_id"),
            ReadStringProperty(json, "name"),
            ReadStringProperty(json, "PackageId"));
        if (!string.Equals(identity, source.PackageId, StringComparison.Ordinal))
            throw new InvalidOperationException($"Official metadata package ID does not match {source.PackageId}.");

        string versions = ReadObjectProperty(json, "versions");
        if (string.IsNullOrWhiteSpace(versions)) versions = ReadObjectProperty(json, "Versions");
        if (string.IsNullOrWhiteSpace(versions))
            throw new InvalidOperationException("Official registry or artifact metadata has no versions object.");

        var releases = new List<ActionFitSdkLatestMetadataRelease>();
        foreach ((string version, string body) in ReadObjectEntries(versions))
        {
            if (!StableVersionPattern.IsMatch(version)) continue;
            var release = new ActionFitSdkLatestMetadataRelease
            {
                Version = version,
                MinimumUnityVersion = FirstNonEmpty(
                    ReadStringProperty(body, "MinimumUnityVersion"),
                    ReadStringProperty(body, "minimumUnityVersion"),
                    ReadStringProperty(body, "unity")),
                MaximumUnityVersion = FirstNonEmpty(
                    ReadStringProperty(body, "MaximumUnityVersion"),
                    ReadStringProperty(body, "maximumUnityVersion")),
            };
            if (artifact)
            {
                string distribution = ReadObjectProperty(body, "dist");
                release.Url = FirstNonEmpty(
                    ReadStringProperty(body, "Url"),
                    ReadStringProperty(body, "url"),
                    ReadStringProperty(body, "tarball"),
                    ReadStringProperty(distribution, "tarball"));
                release.PackageVersion = FirstNonEmpty(
                    ReadStringProperty(body, "PackageVersion"),
                    ReadStringProperty(body, "packageVersion"),
                    version);
                release.Sha256 = FirstNonEmpty(
                    ReadStringProperty(body, "Sha256"),
                    ReadStringProperty(body, "sha256"),
                    ReadStringProperty(distribution, "sha256"));
            }
            releases.Add(release);
        }
        return releases.ToArray();
    }

    private static ActionFitSdkLatestMetadataRelease[] ParseGitReleases(string json)
    {
        string trimmed = (json ?? "").Trim();
        if (!trimmed.StartsWith("[", StringComparison.Ordinal) || !trimmed.EndsWith("]", StringComparison.Ordinal))
            throw new InvalidOperationException("Official Git release metadata must be a release array or the canonical Releases document.");

        var releases = new List<ActionFitSdkLatestMetadataRelease>();
        foreach (string body in ReadArrayObjects(trimmed))
        {
            if (ReadBooleanProperty(body, "draft") || ReadBooleanProperty(body, "prerelease")) continue;
            string tag = FirstNonEmpty(ReadStringProperty(body, "tag_name"), ReadStringProperty(body, "tagName"));
            Match version = StableVersionPattern.Match(tag);
            if (!version.Success) continue;
            string target = FirstNonEmpty(ReadStringProperty(body, "target_commitish"), ReadStringProperty(body, "targetCommitish"));
            releases.Add(new ActionFitSdkLatestMetadataRelease
            {
                Version = version.Groups["version"].Value,
                ImmutableRevision = ActionFitSdkInstallProfileValidator.IsImmutableGitRevision(target) ? target : tag,
            });
        }
        return releases.ToArray();
    }

    private static IEnumerable<(string Key, string Body)> ReadObjectEntries(string json)
    {
        int open = json.IndexOf('{');
        int close = open >= 0 ? FindMatching(json, open, '{', '}') : -1;
        if (open < 0 || close < 0) yield break;
        int index = open + 1;
        while (index < close)
        {
            SkipSeparators(json, ref index, close);
            if (index >= close) yield break;
            string key = ReadString(json, ref index, close);
            SkipWhitespace(json, ref index, close);
            if (index >= close || json[index++] != ':')
                throw new InvalidOperationException("Official metadata versions entry is invalid.");
            SkipWhitespace(json, ref index, close);
            if (index >= close || json[index] != '{')
                throw new InvalidOperationException("Official metadata version value must be an object.");
            int objectClose = FindMatching(json, index, '{', '}');
            if (objectClose < 0) throw new InvalidOperationException("Official metadata version object is incomplete.");
            yield return (key, json.Substring(index, objectClose - index + 1));
            index = objectClose + 1;
        }
    }

    private static IEnumerable<string> ReadArrayObjects(string json)
    {
        int close = json.Length - 1;
        int index = 1;
        while (index < close)
        {
            SkipSeparators(json, ref index, close);
            if (index >= close) yield break;
            if (json[index] != '{') throw new InvalidOperationException("Official Git release array contains a non-object entry.");
            int objectClose = FindMatching(json, index, '{', '}');
            if (objectClose < 0) throw new InvalidOperationException("Official Git release object is incomplete.");
            yield return json.Substring(index, objectClose - index + 1);
            index = objectClose + 1;
        }
    }

    private static string ReadObjectProperty(string json, string property)
    {
        int propertyIndex = (json ?? "").IndexOf($"\"{property}\"", StringComparison.Ordinal);
        int open = propertyIndex >= 0 ? json.IndexOf('{', propertyIndex) : -1;
        int close = open >= 0 ? FindMatching(json, open, '{', '}') : -1;
        return open >= 0 && close >= 0 ? json.Substring(open, close - open + 1) : "";
    }

    private static string ReadStringProperty(string json, string property)
    {
        if (string.IsNullOrWhiteSpace(json)) return "";
        Match match = Regex.Match(
            json,
            $"\\\"{Regex.Escape(property)}\\\"\\s*:\\s*\\\"(?<value>(?:\\\\.|[^\\\"\\\\])*)\\\"",
            RegexOptions.CultureInvariant);
        return match.Success ? Regex.Unescape(match.Groups["value"].Value) : "";
    }

    private static bool ReadBooleanProperty(string json, string property)
    {
        return Regex.IsMatch(
            json ?? "",
            $"\\\"{Regex.Escape(property)}\\\"\\s*:\\s*true",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    }

    private static string ReadString(string json, ref int index, int limit)
    {
        if (index >= limit || json[index++] != '"') throw new InvalidOperationException("Official metadata expected a string key.");
        var builder = new StringBuilder();
        bool escaped = false;
        while (index < limit)
        {
            char value = json[index++];
            if (escaped)
            {
                builder.Append('\\').Append(value);
                escaped = false;
            }
            else if (value == '\\') escaped = true;
            else if (value == '"') return Regex.Unescape(builder.ToString());
            else builder.Append(value);
        }
        throw new InvalidOperationException("Official metadata contains an unterminated string key.");
    }

    private static int FindMatching(string json, int open, char opening, char closing)
    {
        int depth = 0;
        bool inString = false;
        bool escaped = false;
        for (int index = open; index < json.Length; index++)
        {
            char value = json[index];
            if (inString)
            {
                if (escaped) escaped = false;
                else if (value == '\\') escaped = true;
                else if (value == '"') inString = false;
                continue;
            }
            if (value == '"') inString = true;
            else if (value == opening) depth++;
            else if (value == closing && --depth == 0) return index;
        }
        return -1;
    }

    private static void SkipSeparators(string json, ref int index, int limit)
    {
        while (index < limit && (char.IsWhiteSpace(json[index]) || json[index] == ',')) index++;
    }

    private static void SkipWhitespace(string json, ref int index, int limit)
    {
        while (index < limit && char.IsWhiteSpace(json[index])) index++;
    }

    private static string FirstNonEmpty(params string[] values)
    {
        return values.FirstOrDefault(value => !string.IsNullOrWhiteSpace(value)) ?? "";
    }
}

internal sealed class ActionFitSdkSemVerComparer : IComparer<string>
{
    public static readonly ActionFitSdkSemVerComparer Instance = new();

    public int Compare(string left, string right)
    {
        int[] leftParts = Parse(left);
        int[] rightParts = Parse(right);
        for (int index = 0; index < 3; index++)
        {
            int comparison = leftParts[index].CompareTo(rightParts[index]);
            if (comparison != 0) return comparison;
        }
        return string.Compare(left, right, StringComparison.Ordinal);
    }

    private static int[] Parse(string value)
    {
        string core = (value ?? "").Split('+')[0];
        string[] parts = core.Split('.');
        return new[] { int.Parse(parts[0]), int.Parse(parts[1]), int.Parse(parts[2]) };
    }
}
#endif
