#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

/// <summary>One validated Unity Asset or folder extracted from a .unitypackage archive.</summary>
internal sealed class ActionFitSdkUnityPackageEntry
{
    public string Guid = "";
    public string Path = "";
    public byte[] AssetData;
    public byte[] MetaData;
    public string AssetSha256 = "";

    public bool IsFolder => AssetData == null;
}

/// <summary>Structured outcome of reading and validating one .unitypackage archive.</summary>
internal sealed class ActionFitSdkUnityPackageReadResult
{
    public bool Success;
    public ActionFitSdkUnityPackageEntry[] Entries = Array.Empty<ActionFitSdkUnityPackageEntry>();
    public ActionFitSdkProfileDiagnostic[] Diagnostics = Array.Empty<ActionFitSdkProfileDiagnostic>();
    public int IgnoredResourceForkMembers;

    public string FormatMessage()
    {
        if (Success) return "Unity package archive is valid.";
        return "Unity package archive validation failed:\n" + string.Join(
            "\n",
            Diagnostics.Select(diagnostic => $"- {diagnostic.Code} ({diagnostic.Path}): {diagnostic.Message}"));
    }
}

/// <summary>
/// Reads a .unitypackage archive in memory and enforces every archive-safety rule before any
/// AssetDatabase or project mutation. This type never writes to the project.
/// </summary>
internal static class ActionFitSdkUnityPackageArchive
{
    private const int BlockSize = 512;
    private const int MaxUncompressedBytes = 512 * 1024 * 1024;

    private static readonly Regex GuidPattern = new("^[0-9a-fA-F]{32}$", RegexOptions.CultureInvariant);
    private static readonly Regex MetaGuidPattern = new(
        "^guid:\\s*([0-9a-fA-F]{32})\\s*$",
        RegexOptions.Multiline | RegexOptions.CultureInvariant);

    /// <summary>
    /// Folder-shaped Unity assets whose inner files intentionally carry no .meta file.
    /// </summary>
    private static readonly string[] OpaqueFolderSuffixes =
    {
        ".androidlib",
        ".androidpack",
        ".bundle",
        ".framework",
        ".plugin",
        ".xcframework",
    };

    private static readonly HashSet<string> AllowedMembers = new(StringComparer.Ordinal)
    {
        "asset",
        "asset.meta",
        "pathname",
        "preview.png",
    };

    /// <summary>Reads and fully validates an archive without importing or writing anything.</summary>
    public static ActionFitSdkUnityPackageReadResult Read(string archivePath)
    {
        var diagnostics = new List<ActionFitSdkProfileDiagnostic>();
        if (string.IsNullOrWhiteSpace(archivePath) || !File.Exists(archivePath))
        {
            Add(diagnostics, "ARCHIVE_MISSING", "$", "Unity package archive was not found.");
            return Failure(diagnostics, 0);
        }

        byte[] tar;
        try
        {
            tar = Decompress(archivePath);
        }
        catch (Exception ex)
        {
            Add(diagnostics, "ARCHIVE_GZIP_INVALID", "$", $"Archive is not readable gzip data: {ex.Message}");
            return Failure(diagnostics, 0);
        }

        var members = new Dictionary<string, byte[]>(StringComparer.Ordinal);
        int ignoredForks = ReadMembers(tar, members, diagnostics);
        if (diagnostics.Count > 0)
            return Failure(diagnostics, ignoredForks);

        ActionFitSdkUnityPackageEntry[] entries = BuildEntries(members, diagnostics);
        if (diagnostics.Count > 0)
            return Failure(diagnostics, ignoredForks);

        return new ActionFitSdkUnityPackageReadResult
        {
            Success = true,
            Entries = entries,
            Diagnostics = Array.Empty<ActionFitSdkProfileDiagnostic>(),
            IgnoredResourceForkMembers = ignoredForks,
        };
    }

    /// <summary>Compares validated archive entries with the declared profile inventory.</summary>
    public static ActionFitSdkProfileDiagnostic[] CompareWithInventory(
        ActionFitSdkUnityPackageEntry[] entries,
        ActionFitSdkSourceDefinition source)
    {
        var diagnostics = new List<ActionFitSdkProfileDiagnostic>();
        var declared = new Dictionary<string, ActionFitSdkAssetEntry>(StringComparer.Ordinal);
        foreach (ActionFitSdkAssetEntry entry in source.AssetInventory)
        {
            if (entry != null && !string.IsNullOrWhiteSpace(entry.Path))
                declared[entry.Path.Trim()] = entry;
        }

        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (ActionFitSdkUnityPackageEntry entry in entries)
        {
            seen.Add(entry.Path);
            if (!declared.TryGetValue(entry.Path, out ActionFitSdkAssetEntry expected))
            {
                Add(diagnostics, "INVENTORY_ENTRY_UNDECLARED", entry.Path, "Archive contains an entry that the profile inventory does not declare.");
                continue;
            }

            bool expectFolder = expected.ResolveKind() == ActionFitSdkAssetEntryKind.Folder;
            if (expectFolder != entry.IsFolder)
            {
                Add(diagnostics, "INVENTORY_ENTRY_KIND_MISMATCH", entry.Path, $"Inventory declares {(expectFolder ? "folder" : "file")} but the archive contains {(entry.IsFolder ? "folder" : "file")}.");
                continue;
            }

            if (!string.Equals(expected.Guid?.Trim(), entry.Guid, StringComparison.OrdinalIgnoreCase))
                Add(diagnostics, "INVENTORY_GUID_MISMATCH", entry.Path, "Archive GUID does not match the declared inventory GUID.");

            if (!entry.IsFolder &&
                !string.Equals(expected.Sha256?.Trim(), entry.AssetSha256, StringComparison.OrdinalIgnoreCase))
            {
                Add(diagnostics, "INVENTORY_SHA256_MISMATCH", entry.Path, "Archive content hash does not match the declared inventory hash.");
            }
        }

        foreach (string missing in declared.Keys.Where(path => !seen.Contains(path)).OrderBy(path => path, StringComparer.Ordinal))
            Add(diagnostics, "INVENTORY_ENTRY_MISSING", missing, "Profile inventory declares an entry that the archive does not contain.");

        return diagnostics.ToArray();
    }

    private static byte[] Decompress(string archivePath)
    {
        using FileStream source = File.OpenRead(archivePath);
        using var gzip = new GZipStream(source, CompressionMode.Decompress);
        using var buffer = new MemoryStream();

        var chunk = new byte[81920];
        long total = 0;
        int read;
        while ((read = gzip.Read(chunk, 0, chunk.Length)) > 0)
        {
            total += read;
            if (total > MaxUncompressedBytes)
                throw new InvalidOperationException("Archive exceeds the supported uncompressed size.");
            buffer.Write(chunk, 0, read);
        }

        return buffer.ToArray();
    }

    private static int ReadMembers(
        byte[] tar,
        Dictionary<string, byte[]> members,
        List<ActionFitSdkProfileDiagnostic> diagnostics)
    {
        int ignoredForks = 0;
        int offset = 0;
        string pendingPathOverride = null;

        while (offset + BlockSize <= tar.Length)
        {
            if (IsZeroBlock(tar, offset))
                break;

            if (!TryReadHeader(tar, offset, out string rawName, out long size, out char typeFlag, out string error))
            {
                Add(diagnostics, "ARCHIVE_HEADER_INVALID", rawName ?? $"offset:{offset}", error);
                return ignoredForks;
            }

            int dataOffset = offset + BlockSize;
            long paddedSize = ((size + BlockSize - 1) / BlockSize) * BlockSize;
            if (dataOffset + paddedSize > tar.Length)
            {
                Add(diagnostics, "ARCHIVE_TRUNCATED", rawName, "Archive member data is truncated.");
                return ignoredForks;
            }

            offset = dataOffset + (int)paddedSize;

            // pax extended headers describe the member that follows them.
            if (typeFlag == 'x' || typeFlag == 'X')
            {
                pendingPathOverride = ReadPaxPath(tar, dataOffset, (int)size);
                continue;
            }

            if (typeFlag == 'g')
            {
                pendingPathOverride = null;
                continue;
            }

            string name = pendingPathOverride ?? rawName;
            pendingPathOverride = null;

            if (typeFlag == '1' || typeFlag == '2')
            {
                Add(diagnostics, "ARCHIVE_LINK_ENTRY", name, "Archive contains a link entry.");
                return ignoredForks;
            }

            if (typeFlag == 'L' || typeFlag == 'K')
            {
                Add(diagnostics, "ARCHIVE_LONGNAME_UNSUPPORTED", name, "GNU long-name extensions are not supported.");
                return ignoredForks;
            }

            bool isDirectory = typeFlag == '5';
            if (!isDirectory && typeFlag != '0' && typeFlag != '\0')
            {
                Add(diagnostics, "ARCHIVE_MEMBER_UNSUPPORTED", name, $"Unsupported archive member type '{typeFlag}'.");
                return ignoredForks;
            }

            if (!TryNormalizeMemberPath(name, out string normalized, out string pathError))
            {
                Add(diagnostics, "ARCHIVE_PATH_UNSAFE", name, pathError);
                return ignoredForks;
            }

            if (normalized.Length == 0)
                continue;

            string fileName = normalized.Substring(normalized.LastIndexOf('/') + 1);
            if (fileName.StartsWith("._", StringComparison.Ordinal))
            {
                // macOS AppleDouble resource forks are packaging noise, not archive content.
                ignoredForks++;
                continue;
            }

            if (isDirectory)
                continue;

            string[] segments = normalized.Split('/');
            if (segments.Length != 2)
            {
                Add(diagnostics, "ARCHIVE_MEMBER_DEPTH_INVALID", normalized, "Archive members must be <guid>/<member>.");
                return ignoredForks;
            }

            if (!GuidPattern.IsMatch(segments[0]))
            {
                Add(diagnostics, "ARCHIVE_MEMBER_GUID_INVALID", normalized, "Archive member directory must be a 32-character GUID.");
                return ignoredForks;
            }

            if (!AllowedMembers.Contains(segments[1]))
            {
                Add(diagnostics, "ARCHIVE_MEMBER_UNEXPECTED", normalized, $"Unexpected archive member {segments[1]}.");
                return ignoredForks;
            }

            if (members.ContainsKey(normalized))
            {
                Add(diagnostics, "ARCHIVE_MEMBER_DUPLICATE", normalized, "Archive member is declared more than once.");
                return ignoredForks;
            }

            var data = new byte[size];
            Array.Copy(tar, dataOffset, data, 0, (int)size);
            members[normalized] = data;
        }

        return ignoredForks;
    }

    private static ActionFitSdkUnityPackageEntry[] BuildEntries(
        Dictionary<string, byte[]> members,
        List<ActionFitSdkProfileDiagnostic> diagnostics)
    {
        var byGuid = new Dictionary<string, Dictionary<string, byte[]>>(StringComparer.Ordinal);
        foreach (KeyValuePair<string, byte[]> member in members)
        {
            string[] segments = member.Key.Split('/');
            if (!byGuid.TryGetValue(segments[0], out Dictionary<string, byte[]> slot))
            {
                slot = new Dictionary<string, byte[]>(StringComparer.Ordinal);
                byGuid[segments[0]] = slot;
            }
            slot[segments[1]] = member.Value;
        }

        var entries = new List<ActionFitSdkUnityPackageEntry>();
        var paths = new HashSet<string>(StringComparer.Ordinal);
        var lowered = new HashSet<string>(StringComparer.Ordinal);

        foreach (KeyValuePair<string, Dictionary<string, byte[]>> pair in byGuid.OrderBy(item => item.Key, StringComparer.Ordinal))
        {
            string guid = pair.Key;
            Dictionary<string, byte[]> slot = pair.Value;

            if (!slot.TryGetValue("pathname", out byte[] pathnameData))
            {
                Add(diagnostics, "ARCHIVE_PATHNAME_MISSING", guid, "Archive entry has no pathname member.");
                continue;
            }

            string assetPath = ReadFirstLine(pathnameData);
            if (!TryNormalizeMemberPath(assetPath, out string normalizedPath, out string pathError) || normalizedPath.Length == 0)
            {
                Add(diagnostics, "ARCHIVE_ASSET_PATH_UNSAFE", assetPath ?? guid, pathError ?? "Asset path is empty.");
                continue;
            }

            if (!paths.Add(normalizedPath))
            {
                Add(diagnostics, "ARCHIVE_ASSET_PATH_DUPLICATE", normalizedPath, "Two archive entries declare the same asset path.");
                continue;
            }

            if (!lowered.Add(normalizedPath.ToLowerInvariant()))
            {
                Add(diagnostics, "ARCHIVE_ASSET_PATH_CASE_COLLISION", normalizedPath, "Asset path collides with another entry on case-insensitive file systems.");
                continue;
            }

            slot.TryGetValue("asset", out byte[] assetData);
            slot.TryGetValue("asset.meta", out byte[] metaData);

            if (metaData == null && !IsInsideOpaqueFolder(normalizedPath))
            {
                Add(diagnostics, "ARCHIVE_ASSET_META_MISSING", normalizedPath, "Archive entry has no asset.meta member.");
                continue;
            }

            if (metaData != null && !TryVerifyMetaGuid(metaData, guid, out string metaError))
            {
                Add(diagnostics, "ARCHIVE_META_GUID_MISMATCH", normalizedPath, metaError);
                continue;
            }

            entries.Add(new ActionFitSdkUnityPackageEntry
            {
                Guid = guid.ToLowerInvariant(),
                Path = normalizedPath,
                AssetData = assetData,
                MetaData = metaData,
                AssetSha256 = assetData == null ? "" : Sha256(assetData),
            });
        }

        return entries.OrderBy(entry => entry.Path, StringComparer.Ordinal).ToArray();
    }

    private static bool TryReadHeader(
        byte[] tar,
        int offset,
        out string name,
        out long size,
        out char typeFlag,
        out string error)
    {
        name = ReadString(tar, offset, 100);
        size = 0;
        typeFlag = '\0';
        error = "";

        string magic = ReadString(tar, offset + 257, 6).Trim();
        if (!string.Equals(magic, "ustar", StringComparison.Ordinal))
        {
            error = "Archive header is not a ustar record.";
            return false;
        }

        if (!TryReadOctal(tar, offset + 124, 12, out size) || size < 0)
        {
            error = "Archive header declares an unreadable size.";
            return false;
        }

        typeFlag = (char)tar[offset + 156];

        string prefix = ReadString(tar, offset + 345, 155);
        if (prefix.Length > 0)
            name = prefix + "/" + name;

        return true;
    }

    private static string ReadPaxPath(byte[] tar, int offset, int length)
    {
        // pax records are "<decimal byte length> <key>=<value>\n". Record lengths count
        // bytes, and macOS writes binary xattr values, so this must stay byte-oriented.
        int position = 0;
        while (position < length)
        {
            int space = Array.IndexOf(tar, (byte)' ', offset + position, length - position);
            if (space < 0) break;

            int digits = space - (offset + position);
            if (digits <= 0 || digits > 10) break;

            int recordLength = 0;
            for (int i = 0; i < digits; i++)
            {
                byte digit = tar[offset + position + i];
                if (digit < (byte)'0' || digit > (byte)'9')
                    return null;
                recordLength = (recordLength * 10) + (digit - '0');
            }

            if (recordLength <= digits + 1 || position + recordLength > length)
                break;

            int keyStart = space + 1;
            int valueEnd = offset + position + recordLength - 1;
            int equals = Array.IndexOf(tar, (byte)'=', keyStart, valueEnd - keyStart);
            if (equals > 0 &&
                equals - keyStart == 4 &&
                tar[keyStart] == (byte)'p' && tar[keyStart + 1] == (byte)'a' &&
                tar[keyStart + 2] == (byte)'t' && tar[keyStart + 3] == (byte)'h')
            {
                return Encoding.UTF8.GetString(tar, equals + 1, valueEnd - equals - 1);
            }

            position += recordLength;
        }

        return null;
    }

    private static bool TryNormalizeMemberPath(string value, out string normalized, out string error)
    {
        normalized = "";
        error = "";
        if (string.IsNullOrWhiteSpace(value))
        {
            error = "Archive member path is empty.";
            return false;
        }

        string candidate = value.Replace('\\', '/').Trim();
        if (candidate.StartsWith("/", StringComparison.Ordinal))
        {
            error = "Archive member path is absolute.";
            return false;
        }

        if (candidate.Length > 1 && candidate[1] == ':')
        {
            error = "Archive member path is rooted.";
            return false;
        }

        var segments = new List<string>();
        foreach (string segment in candidate.Split('/'))
        {
            if (segment.Length == 0 || segment == ".")
                continue;
            if (segment == "..")
            {
                error = "Archive member path escapes the archive root.";
                return false;
            }
            segments.Add(segment);
        }

        normalized = string.Join("/", segments);
        return true;
    }

    private static bool IsInsideOpaqueFolder(string path)
    {
        string[] segments = path.Split('/');
        for (int i = 0; i < segments.Length - 1; i++)
        {
            foreach (string suffix in OpaqueFolderSuffixes)
            {
                if (segments[i].EndsWith(suffix, StringComparison.OrdinalIgnoreCase))
                    return true;
            }
        }

        return false;
    }

    private static bool TryVerifyMetaGuid(byte[] metaData, string guid, out string error)
    {
        string text = Encoding.UTF8.GetString(metaData);
        Match match = MetaGuidPattern.Match(text);
        if (!match.Success)
        {
            error = "Asset meta does not declare a GUID.";
            return false;
        }

        if (!string.Equals(match.Groups[1].Value, guid, StringComparison.OrdinalIgnoreCase))
        {
            error = "Asset meta GUID does not match its archive directory.";
            return false;
        }

        error = "";
        return true;
    }

    private static string ReadFirstLine(byte[] data)
    {
        string text = Encoding.UTF8.GetString(data);
        int newline = text.IndexOfAny(new[] { '\r', '\n' });
        return (newline < 0 ? text : text.Substring(0, newline)).Trim();
    }

    private static bool IsZeroBlock(byte[] tar, int offset)
    {
        for (int i = 0; i < BlockSize; i++)
        {
            if (tar[offset + i] != 0)
                return false;
        }

        return true;
    }

    private static string ReadString(byte[] tar, int offset, int length)
    {
        int end = offset;
        int limit = Math.Min(offset + length, tar.Length);
        while (end < limit && tar[end] != 0)
            end++;
        return Encoding.UTF8.GetString(tar, offset, end - offset);
    }

    private static bool TryReadOctal(byte[] tar, int offset, int length, out long value)
    {
        value = 0;
        string text = ReadString(tar, offset, length).Trim();
        if (text.Length == 0)
            return true;

        foreach (char character in text)
        {
            if (character < '0' || character > '7')
                return false;
            value = (value * 8) + (character - '0');
        }

        return true;
    }

    private static string Sha256(byte[] data)
    {
        using SHA256 sha = SHA256.Create();
        return BitConverter.ToString(sha.ComputeHash(data)).Replace("-", "").ToLowerInvariant();
    }

    private static void Add(List<ActionFitSdkProfileDiagnostic> diagnostics, string code, string path, string message)
    {
        diagnostics.Add(new ActionFitSdkProfileDiagnostic { Code = code, Path = path, Message = message });
    }

    private static ActionFitSdkUnityPackageReadResult Failure(
        List<ActionFitSdkProfileDiagnostic> diagnostics,
        int ignoredForks)
    {
        return new ActionFitSdkUnityPackageReadResult
        {
            Success = false,
            Entries = Array.Empty<ActionFitSdkUnityPackageEntry>(),
            Diagnostics = diagnostics.ToArray(),
            IgnoredResourceForkMembers = ignoredForks,
        };
    }
}
#endif
