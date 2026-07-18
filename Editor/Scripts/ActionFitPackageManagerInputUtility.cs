#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.Linq;

internal static class ActionFitPackageManagerInputUtility
{
    internal static bool IsCollectionPackage(string packageId, string packageType)
    {
        return string.Equals(packageType?.Trim(), "collection", StringComparison.OrdinalIgnoreCase) ||
               (!string.IsNullOrWhiteSpace(packageId) &&
                packageId.EndsWith(".installer", StringComparison.OrdinalIgnoreCase));
    }

    internal static bool MatchesSearch(string filter, IEnumerable<string> values)
    {
        if (string.IsNullOrWhiteSpace(filter)) return true;

        string term = filter.Trim();
        return values != null && values.Any(value =>
            !string.IsNullOrWhiteSpace(value) &&
            value.IndexOf(term, StringComparison.OrdinalIgnoreCase) >= 0);
    }

    internal static bool MatchesContentBundle(string filter, ActionFitContentBundleStatus status)
    {
        if (status == null) return false;

        IEnumerable<string> moduleValues = (status.modules ?? Array.Empty<ActionFitContentBundleModuleSpec>())
            .SelectMany(module => new[] { module.moduleId, module.displayName }
                .Concat(module.packageIds ?? Array.Empty<string>()));
        IEnumerable<string> values = new[]
            {
                status.bundleId,
                status.bundleVersion,
                status.displayName,
                status.state,
                status.bootstrapPackageId,
            }
            .Concat(status.requiredPackageIds ?? Array.Empty<string>())
            .Concat(status.selectedModuleIds ?? Array.Empty<string>())
            .Concat(status.requiredModuleIds ?? Array.Empty<string>())
            .Concat(status.conflicts ?? Array.Empty<string>())
            .Concat(moduleValues);

        return MatchesSearch(filter, values);
    }

    internal static bool TryNormalizeGitUrl(string raw, out string normalized, out string error)
    {
        normalized = "";
        error = "";
        string candidate = raw?.Trim() ?? "";
        if (candidate.Length == 0)
        {
            error = "Enter a credential-free HTTPS or SSH Git URL.";
            return false;
        }

        if (candidate.Any(character => char.IsWhiteSpace(character) || char.IsControl(character)))
        {
            error = "Git URL must not contain whitespace or control characters.";
            return false;
        }

        if (candidate.EndsWith("#", StringComparison.Ordinal) || candidate.Count(character => character == '#') > 1)
        {
            error = "Git URL revision must be non-empty and contain at most one fragment.";
            return false;
        }

        if (!HasSupportedQuery(candidate, out error))
            return false;

        if (TryParseScpStyle(candidate, out bool validScpStyle))
        {
            if (!validScpStyle)
            {
                error = "SSH Git URL must include a user, host, and repository path without credentials.";
                return false;
            }

            normalized = candidate;
            return true;
        }

        if (!Uri.TryCreate(candidate, UriKind.Absolute, out Uri uri) ||
            (!string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase) &&
             !string.Equals(uri.Scheme, "ssh", StringComparison.OrdinalIgnoreCase)))
        {
            error = "Only credential-free HTTPS and SSH Git URLs are supported.";
            return false;
        }

        if (string.IsNullOrWhiteSpace(uri.Host) || string.IsNullOrWhiteSpace(uri.AbsolutePath.Trim('/')))
        {
            error = "Git URL must include a host and repository path.";
            return false;
        }

        if (string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase) &&
            !string.IsNullOrWhiteSpace(uri.UserInfo))
        {
            error = "HTTPS Git URL must not contain embedded credentials.";
            return false;
        }

        if (string.Equals(uri.Scheme, "ssh", StringComparison.OrdinalIgnoreCase) &&
            uri.UserInfo.Contains(":"))
        {
            error = "SSH Git URL must not contain an embedded password.";
            return false;
        }

        normalized = candidate;
        return true;
    }

    private static bool HasSupportedQuery(string candidate, out string error)
    {
        error = "";
        int fragmentIndex = candidate.IndexOf('#');
        string withoutFragment = fragmentIndex >= 0 ? candidate[..fragmentIndex] : candidate;
        int queryIndex = withoutFragment.IndexOf('?');
        if (queryIndex < 0) return true;

        string query = withoutFragment[(queryIndex + 1)..];
        if (!query.StartsWith("path=", StringComparison.OrdinalIgnoreCase) ||
            query.Length == "path=".Length ||
            query.Contains('&') ||
            query.IndexOf('=', "path=".Length) >= 0)
        {
            error = "Git URL query may contain only one non-empty ?path= value.";
            return false;
        }

        return true;
    }

    private static bool TryParseScpStyle(string candidate, out bool valid)
    {
        valid = false;
        if (candidate.Contains("://", StringComparison.Ordinal)) return false;

        int fragmentIndex = candidate.IndexOf('#');
        string withoutFragment = fragmentIndex >= 0 ? candidate[..fragmentIndex] : candidate;
        int queryIndex = withoutFragment.IndexOf('?');
        string core = queryIndex >= 0 ? withoutFragment[..queryIndex] : withoutFragment;
        int atIndex = core.IndexOf('@');
        int colonIndex = core.IndexOf(':', atIndex + 1);
        if (atIndex < 0 && colonIndex < 0) return false;

        string user = atIndex > 0 ? core[..atIndex] : "";
        string host = atIndex >= 0 && colonIndex > atIndex ? core[(atIndex + 1)..colonIndex] : "";
        string path = colonIndex >= 0 && colonIndex < core.Length - 1 ? core[(colonIndex + 1)..] : "";
        valid = atIndex > 0 &&
                colonIndex > atIndex + 1 &&
                colonIndex < core.Length - 1 &&
                core.IndexOf('@', atIndex + 1) < 0 &&
                core.IndexOf(':') == colonIndex &&
                user.All(IsSshNameCharacter) &&
                host.All(IsSshHostCharacter) &&
                path.Length > 0;
        return true;
    }

    private static bool IsSshNameCharacter(char character)
    {
        return char.IsLetterOrDigit(character) || character is '.' or '_' or '-';
    }

    private static bool IsSshHostCharacter(char character)
    {
        return char.IsLetterOrDigit(character) || character is '.' or '-';
    }
}
#endif
