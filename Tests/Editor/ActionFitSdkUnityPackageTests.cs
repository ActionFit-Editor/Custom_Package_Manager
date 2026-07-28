#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using NUnit.Framework;

/// <summary>
/// Contract tests for the schema-v3 unitypackageArtifact source, its archive validator, and the
/// backward compatibility of schema-v1 and schema-v2 profiles.
/// </summary>
public sealed class ActionFitSdkUnityPackageTests
{
    private const string AssetGuid = "0123456789abcdef0123456789abcdef";
    private const string FolderGuid = "fedcba9876543210fedcba9876543210";
    private const string AssetPath = "Assets/VendorSdk/Runtime/Vendor.cs";
    private const string FolderPath = "Assets/VendorSdk";

    private string _temporaryRoot;

    [SetUp]
    public void SetUp()
    {
        _temporaryRoot = Path.Combine(Path.GetTempPath(), "ActionFitSdkUnityPackageTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_temporaryRoot);
    }

    [TearDown]
    public void TearDown()
    {
        if (Directory.Exists(_temporaryRoot)) Directory.Delete(_temporaryRoot, true);
    }

    // ---------- archive validation ----------

    [Test]
    public void Read_ValidArchiveBuildsEntriesAndIgnoresAppleDoubleForks()
    {
        var members = DefaultMembers();
        // macOS bsdtar writes one AppleDouble sidecar per member; the official artifact is half forks.
        members.Add(new TarMember("._.", Array.Empty<byte>()));
        members.Add(new TarMember($"{AssetGuid}/._asset", new byte[] { 1, 2, 3 }));

        ActionFitSdkUnityPackageReadResult result = ActionFitSdkUnityPackageArchive.Read(WriteArchive(members));

        Assert.That(result.Success, Is.True, result.FormatMessage());
        Assert.That(result.IgnoredResourceForkMembers, Is.EqualTo(2));
        Assert.That(result.Entries.Length, Is.EqualTo(2));
        Assert.That(result.Entries.Single(entry => !entry.IsFolder).Path, Is.EqualTo(AssetPath));
        Assert.That(result.Entries.Single(entry => entry.IsFolder).Path, Is.EqualTo(FolderPath));
    }

    [Test]
    public void Read_RejectsTraversalPathInPathname()
    {
        var members = DefaultMembers();
        Replace(members, $"{AssetGuid}/pathname", Encoding.UTF8.GetBytes("Assets/../../outside.cs"));

        AssertFails(members, "ARCHIVE_ASSET_PATH_UNSAFE");
    }

    [Test]
    public void Read_RejectsLinkEntry()
    {
        var members = DefaultMembers();
        members.Add(new TarMember($"{AssetGuid}/asset.link", Array.Empty<byte>()) { TypeFlag = '2' });

        AssertFails(members, "ARCHIVE_LINK_ENTRY");
    }

    [Test]
    public void Read_RejectsAbsoluteMemberPath()
    {
        var members = DefaultMembers();
        members.Add(new TarMember("/etc/passwd", new byte[] { 9 }));

        AssertFails(members, "ARCHIVE_PATH_UNSAFE");
    }

    [Test]
    public void Read_RejectsUnexpectedMemberDepth()
    {
        var members = DefaultMembers();
        members.Add(new TarMember($"{AssetGuid}/nested/asset", new byte[] { 9 }));

        AssertFails(members, "ARCHIVE_MEMBER_DEPTH_INVALID");
    }

    [Test]
    public void Read_RejectsUnexpectedMemberName()
    {
        var members = DefaultMembers();
        members.Add(new TarMember($"{AssetGuid}/payload.sh", new byte[] { 9 }));

        AssertFails(members, "ARCHIVE_MEMBER_UNEXPECTED");
    }

    [Test]
    public void Read_RejectsDuplicateMember()
    {
        var members = DefaultMembers();
        members.Add(new TarMember($"{AssetGuid}/asset", new byte[] { 9 }));

        AssertFails(members, "ARCHIVE_MEMBER_DUPLICATE");
    }

    [Test]
    public void Read_RejectsMetaGuidThatDoesNotMatchItsDirectory()
    {
        var members = DefaultMembers();
        Replace(members, $"{AssetGuid}/asset.meta", Meta("99999999999999999999999999999999"));

        AssertFails(members, "ARCHIVE_META_GUID_MISMATCH");
    }

    [Test]
    public void Read_RejectsMissingPathname()
    {
        var members = DefaultMembers();
        members.RemoveAll(member => member.Name == $"{AssetGuid}/pathname");

        AssertFails(members, "ARCHIVE_PATHNAME_MISSING");
    }

    [Test]
    public void Read_RejectsMissingAssetMeta()
    {
        var members = DefaultMembers();
        members.RemoveAll(member => member.Name == $"{AssetGuid}/asset.meta");

        AssertFails(members, "ARCHIVE_ASSET_META_MISSING");
    }

    [Test]
    public void Read_AllowsMissingAssetMetaInsideOpaqueFolderAsset()
    {
        // Unity does not generate .meta files inside .androidlib folder assets.
        const string guid = "aaaabbbbccccddddeeeeffff00001111";
        var members = DefaultMembers();
        members.Add(new TarMember($"{guid}/pathname",
            Encoding.UTF8.GetBytes("Assets/Plugins/Android/Vendor.androidlib/project.properties")));
        members.Add(new TarMember($"{guid}/asset", Encoding.UTF8.GetBytes("target=android-34")));

        ActionFitSdkUnityPackageReadResult result = ActionFitSdkUnityPackageArchive.Read(WriteArchive(members));

        Assert.That(result.Success, Is.True, result.FormatMessage());
        Assert.That(result.Entries.Any(entry => entry.Path.EndsWith("project.properties", StringComparison.Ordinal)), Is.True);
    }

    [Test]
    public void Read_RejectsCaseCollidingAssetPaths()
    {
        const string guid = "11112222333344445555666677778888";
        var members = DefaultMembers();
        members.Add(new TarMember($"{guid}/pathname", Encoding.UTF8.GetBytes(AssetPath.ToUpperInvariant())));
        members.Add(new TarMember($"{guid}/asset", new byte[] { 7 }));
        members.Add(new TarMember($"{guid}/asset.meta", Meta(guid)));

        AssertFails(members, "ARCHIVE_ASSET_PATH_CASE_COLLISION");
    }

    [Test]
    public void Read_RejectsNonUstarHeader()
    {
        byte[] archive = File.ReadAllBytes(WriteArchive(DefaultMembers()));
        string path = Path.Combine(_temporaryRoot, "corrupt.unitypackage");
        byte[] raw = Decompress(archive);
        raw[257] = (byte)'x';
        File.WriteAllBytes(path, Compress(raw));

        ActionFitSdkUnityPackageReadResult result = ActionFitSdkUnityPackageArchive.Read(path);

        Assert.That(result.Success, Is.False);
        Assert.That(result.Diagnostics.Any(item => item.Code == "ARCHIVE_HEADER_INVALID"), Is.True, result.FormatMessage());
    }

    [Test]
    public void Read_RejectsNonGzipPayload()
    {
        string path = Path.Combine(_temporaryRoot, "plain.unitypackage");
        File.WriteAllText(path, "not a gzip archive");

        ActionFitSdkUnityPackageReadResult result = ActionFitSdkUnityPackageArchive.Read(path);

        Assert.That(result.Success, Is.False);
        Assert.That(result.Diagnostics.Any(item => item.Code == "ARCHIVE_GZIP_INVALID"), Is.True, result.FormatMessage());
    }

    // ---------- inventory comparison ----------

    [Test]
    public void CompareWithInventory_AcceptsExactInventory()
    {
        ActionFitSdkUnityPackageReadResult read = ActionFitSdkUnityPackageArchive.Read(WriteArchive(DefaultMembers()));
        Assert.That(read.Success, Is.True, read.FormatMessage());

        ActionFitSdkProfileDiagnostic[] drift =
            ActionFitSdkUnityPackageArchive.CompareWithInventory(read.Entries, CreateUnityPackageSource());

        Assert.That(drift, Is.Empty, string.Join("\n", drift.Select(item => item.Code + " " + item.Path)));
    }

    [Test]
    public void CompareWithInventory_RejectsUndeclaredEntry()
    {
        ActionFitSdkUnityPackageReadResult read = ActionFitSdkUnityPackageArchive.Read(WriteArchive(DefaultMembers()));
        ActionFitSdkSourceDefinition source = CreateUnityPackageSource();
        source.AssetInventory = source.AssetInventory.Where(entry => entry.Path != AssetPath).ToArray();

        ActionFitSdkProfileDiagnostic[] drift = ActionFitSdkUnityPackageArchive.CompareWithInventory(read.Entries, source);

        Assert.That(drift.Any(item => item.Code == "INVENTORY_ENTRY_UNDECLARED"), Is.True);
    }

    [Test]
    public void CompareWithInventory_RejectsMissingDeclaredEntry()
    {
        ActionFitSdkUnityPackageReadResult read = ActionFitSdkUnityPackageArchive.Read(WriteArchive(DefaultMembers()));
        ActionFitSdkSourceDefinition source = CreateUnityPackageSource();
        source.AssetInventory = source.AssetInventory.Append(new ActionFitSdkAssetEntry
        {
            Path = "Assets/VendorSdk/Runtime/Absent.cs",
            Guid = "cccccccccccccccccccccccccccccccc",
            Sha256 = Sha256(new byte[] { 42 }),
            Kind = "file",
        }).ToArray();

        ActionFitSdkProfileDiagnostic[] drift = ActionFitSdkUnityPackageArchive.CompareWithInventory(read.Entries, source);

        Assert.That(drift.Any(item => item.Code == "INVENTORY_ENTRY_MISSING"), Is.True);
    }

    [Test]
    public void CompareWithInventory_RejectsContentAndGuidDrift()
    {
        ActionFitSdkUnityPackageReadResult read = ActionFitSdkUnityPackageArchive.Read(WriteArchive(DefaultMembers()));
        ActionFitSdkSourceDefinition source = CreateUnityPackageSource();
        ActionFitSdkAssetEntry declared = source.AssetInventory.First(entry => entry.Path == AssetPath);
        declared.Sha256 = Sha256(new byte[] { 99 });
        declared.Guid = "dddddddddddddddddddddddddddddddd";

        ActionFitSdkProfileDiagnostic[] drift = ActionFitSdkUnityPackageArchive.CompareWithInventory(read.Entries, source);

        Assert.That(drift.Any(item => item.Code == "INVENTORY_GUID_MISMATCH"), Is.True);
        Assert.That(drift.Any(item => item.Code == "INVENTORY_SHA256_MISMATCH"), Is.True);
    }

    // ---------- schema contract ----------

    [Test]
    public void Validate_SchemaThreeUnityPackageProfileIsValid()
    {
        ActionFitSdkProfileValidationResult result = ActionFitSdkInstallProfileValidator.Validate(CreateUnityPackageProfile());

        Assert.That(result.Success, Is.True, result.FormatMessage());
    }

    [Test]
    public void Validate_UnityPackageSourceRequiresSchemaThree()
    {
        ActionFitSdkInstallProfile profile = CreateUnityPackageProfile();
        profile.SchemaVersion = ActionFitSdkInstallProfile.LatestResolutionSchemaVersion;

        AssertDiagnostic(profile, "SOURCE_UNITYPACKAGE_SCHEMA");
    }

    [Test]
    public void Validate_UnityPackageSourceRejectsLatestPolicy()
    {
        ActionFitSdkInstallProfile profile = CreateUnityPackageProfile();
        profile.Sources[0].ResolutionPolicy = "anyInstalledElseLatestStable";
        profile.Sources[0].LatestResolver = "artifactMetadata";
        profile.Sources[0].MetadataUrl = "https://example.com/metadata.json";

        AssertDiagnostic(profile, "SOURCE_UNITYPACKAGE_POLICY");
    }

    [Test]
    public void Validate_RejectsAssetFieldsOnNonUnityPackageSource()
    {
        ActionFitSdkInstallProfile profile = CreateUnityPackageProfile();
        profile.Sources[0].Kind = "artifact";
        profile.Sources[0].CacheRelativePath = "ActionFitSdkArtifacts/vendor/sdk.tgz";

        AssertDiagnostic(profile, "SOURCE_ASSET_FIELDS_UNEXPECTED");
    }

    [Test]
    public void Validate_RejectsUnityPackageWithoutInventory()
    {
        ActionFitSdkInstallProfile profile = CreateUnityPackageProfile();
        profile.Sources[0].AssetInventory = Array.Empty<ActionFitSdkAssetEntry>();
        profile.Sources[0].PreservePaths = Array.Empty<string>();
        profile.Sources[0].ExcludedPaths = Array.Empty<string>();

        AssertDiagnostic(profile, "ASSET_INVENTORY_MISSING");
    }

    [Test]
    public void Validate_RejectsDuplicateInventoryGuid()
    {
        ActionFitSdkInstallProfile profile = CreateUnityPackageProfile();
        profile.Sources[0].AssetInventory[1].Guid = profile.Sources[0].AssetInventory[0].Guid;

        AssertDiagnostic(profile, "ASSET_GUID_DUPLICATE");
    }

    [Test]
    public void Validate_RejectsCaseCollidingInventoryPaths()
    {
        ActionFitSdkInstallProfile profile = CreateUnityPackageProfile();
        profile.Sources[0].AssetInventory[1].Path = profile.Sources[0].AssetInventory[0].Path.ToUpperInvariant();
        profile.Sources[0].PreservePaths = Array.Empty<string>();
        profile.Sources[0].ExcludedPaths = Array.Empty<string>();

        AssertDiagnostic(profile, "ASSET_PATH_CASE_COLLISION");
    }

    [Test]
    public void Validate_RejectsTraversalInventoryPath()
    {
        ActionFitSdkInstallProfile profile = CreateUnityPackageProfile();
        profile.Sources[0].AssetInventory[0].Path = "Assets/../outside.cs";
        profile.Sources[0].PreservePaths = Array.Empty<string>();
        profile.Sources[0].ExcludedPaths = Array.Empty<string>();

        AssertDiagnostic(profile, "ASSET_PATH_INVALID");
    }

    [Test]
    public void Validate_RejectsFolderEntryWithChecksum()
    {
        ActionFitSdkInstallProfile profile = CreateUnityPackageProfile();
        profile.Sources[0].AssetInventory.First(entry => entry.Kind == "folder").Sha256 = Sha256(new byte[] { 1 });

        AssertDiagnostic(profile, "ASSET_FOLDER_SHA256_UNEXPECTED");
    }

    [Test]
    public void Validate_RejectsPreservePathOutsideInventory()
    {
        ActionFitSdkInstallProfile profile = CreateUnityPackageProfile();
        profile.Sources[0].PreservePaths = new[] { "Assets/Unrelated/Path.cs" };

        AssertDiagnostic(profile, "ASSET_PRESERVE_PATH_UNKNOWN");
    }

    [Test]
    public void Validate_RejectsPathThatIsBothPreservedAndExcluded()
    {
        ActionFitSdkInstallProfile profile = CreateUnityPackageProfile();
        profile.Sources[0].PreservePaths = new[] { AssetPath };
        profile.Sources[0].ExcludedPaths = new[] { AssetPath };

        AssertDiagnostic(profile, "ASSET_PATH_PRESERVE_EXCLUDE_CONFLICT");
    }

    [Test]
    public void Validate_RejectsSchemaVersionAboveCurrent()
    {
        ActionFitSdkInstallProfile profile = CreateUnityPackageProfile();
        profile.SchemaVersion = ActionFitSdkInstallProfile.CurrentSchemaVersion + 1;

        AssertDiagnostic(profile, "SCHEMA_VERSION_UNSUPPORTED");
    }

    [Test]
    public void RoundTrip_UnityPackageProfileSurvivesJsonSerialization()
    {
        ActionFitSdkInstallProfile profile = CreateUnityPackageProfile();

        ActionFitSdkInstallProfile restored = ActionFitSdkInstallProfile.FromJson(profile.ToJson());

        Assert.That(restored.SchemaVersion, Is.EqualTo(ActionFitSdkInstallProfile.UnityPackageSchemaVersion));
        Assert.That(restored.Sources[0].ResolveKind(), Is.EqualTo(ActionFitSdkSourceKind.UnityPackageArtifact));
        Assert.That(restored.Sources[0].AssetInventory.Length, Is.EqualTo(2));
        Assert.That(restored.Sources[0].PreservePaths, Is.EquivalentTo(profile.Sources[0].PreservePaths));
    }

    // ---------- schema v1 / v2 regression ----------

    [Test]
    public void Validate_SchemaTwoStillAcceptsLatestResolutionPolicy()
    {
        // Regression: the latest-policy gate must key off LatestResolutionSchemaVersion, not the
        // newest schema version, or raising CurrentSchemaVersion silently invalidates v2 profiles.
        ActionFitSdkInstallProfile profile = CreateRegistryProfile(ActionFitSdkInstallProfile.LatestResolutionSchemaVersion);
        profile.Sources[0].ResolutionPolicy = "anyInstalledElseLatestStable";
        profile.Sources[0].LatestResolver = "registryMetadata";
        profile.Sources[0].MetadataUrl = "https://example.com/metadata.json";
        profile.Sources[0].ImmutableVersion = "";

        ActionFitSdkProfileValidationResult result = ActionFitSdkInstallProfileValidator.Validate(profile);

        Assert.That(result.Success, Is.True, result.FormatMessage());
        Assert.That(profile.RequiresAsyncResolution(), Is.True);
    }

    [Test]
    public void Validate_SchemaOneRejectsLatestResolutionPolicy()
    {
        ActionFitSdkInstallProfile profile = CreateRegistryProfile(ActionFitSdkInstallProfile.LegacySchemaVersion);
        profile.Sources[0].ResolutionPolicy = "anyInstalledElseLatestStable";
        profile.Sources[0].LatestResolver = "registryMetadata";
        profile.Sources[0].MetadataUrl = "https://example.com/metadata.json";

        AssertDiagnostic(profile, "SOURCE_RESOLUTION_POLICY_SCHEMA");
    }

    [Test]
    public void Validate_SchemaOneAndTwoExactProfilesRemainValid()
    {
        foreach (int schemaVersion in new[]
                 {
                     ActionFitSdkInstallProfile.LegacySchemaVersion,
                     ActionFitSdkInstallProfile.LatestResolutionSchemaVersion,
                 })
        {
            ActionFitSdkInstallProfile profile = CreateRegistryProfile(schemaVersion);

            ActionFitSdkProfileValidationResult result = ActionFitSdkInstallProfileValidator.Validate(profile);

            Assert.That(result.Success, Is.True, $"schema {schemaVersion}: {result.FormatMessage()}");
            Assert.That(profile.RequiresAsyncResolution(), Is.False);
            Assert.That(profile.DeclaresUnityPackageSources(), Is.False);
        }
    }

    // ---------- fixtures ----------

    private static ActionFitSdkInstallProfile CreateUnityPackageProfile()
    {
        return new ActionFitSdkInstallProfile
        {
            SchemaVersion = ActionFitSdkInstallProfile.UnityPackageSchemaVersion,
            ProfileId = "vendor.unity",
            ProfileVersion = "1.0.0",
            Vendor = "Vendor",
            DisplayName = "Vendor SDK",
            BridgePackageId = "com.actionfit.sdk.vendor",
            MinimumUnityVersion = "6000.2",
            LicenseUrl = "https://example.com/license",
            SupportUrl = "https://example.com/support",
            SupportedPlatforms = new[] { "Android" },
            AllowedDomains = new[] { "example.com" },
            Sources = new[] { CreateUnityPackageSource() },
            Modules = new[]
            {
                new ActionFitSdkModuleDefinition { Id = "core", DisplayName = "Core", Required = true, DefaultSelected = true },
            },
        };
    }

    private static ActionFitSdkSourceDefinition CreateUnityPackageSource()
    {
        return new ActionFitSdkSourceDefinition
        {
            Id = "vendor",
            Kind = "unitypackageArtifact",
            Url = "https://example.com/vendor/VendorSdk-1.0.0.unitypackage",
            ImmutableVersion = "1.0.0",
            PackageId = "com.vendor.sdk",
            PackageVersion = "1.0.0",
            Sha256 = Sha256(Encoding.UTF8.GetBytes("archive")),
            CacheRelativePath = "ActionFitSdkArtifacts/vendor/VendorSdk-1.0.0.unitypackage",
            AssetInventory = new[]
            {
                new ActionFitSdkAssetEntry
                {
                    Path = FolderPath,
                    Guid = FolderGuid,
                    Kind = "folder",
                },
                new ActionFitSdkAssetEntry
                {
                    Path = AssetPath,
                    Guid = AssetGuid,
                    Sha256 = Sha256(AssetContent),
                    Kind = "file",
                },
            },
            PreservePaths = new[] { AssetPath },
            ExcludedPaths = Array.Empty<string>(),
        };
    }

    private static ActionFitSdkInstallProfile CreateRegistryProfile(int schemaVersion)
    {
        return new ActionFitSdkInstallProfile
        {
            SchemaVersion = schemaVersion,
            ProfileId = "vendor.registry",
            ProfileVersion = "1.0.0",
            Vendor = "Vendor",
            DisplayName = "Vendor Registry SDK",
            BridgePackageId = "com.actionfit.sdk.vendor",
            MinimumUnityVersion = "6000.2",
            LicenseUrl = "https://example.com/license",
            SupportUrl = "https://example.com/support",
            SupportedPlatforms = new[] { "Android" },
            AllowedDomains = new[] { "example.com" },
            Sources = new[]
            {
                new ActionFitSdkSourceDefinition
                {
                    Id = "vendor",
                    Kind = "registry",
                    Url = "https://registry.example.com",
                    ImmutableVersion = "1.0.0",
                    PackageId = "com.vendor.sdk",
                },
            },
            Modules = new[]
            {
                new ActionFitSdkModuleDefinition { Id = "core", DisplayName = "Core", Required = true, DefaultSelected = true },
            },
            Dependencies = new[]
            {
                new ActionFitSdkDependencyDefinition { PackageId = "com.vendor.sdk", SourceId = "vendor", ModuleId = "core" },
            },
            ScopedRegistries = new[]
            {
                new ActionFitSdkScopedRegistryDefinition
                {
                    Name = "Vendor",
                    Url = "https://registry.example.com",
                    ModuleId = "core",
                    Scopes = new[] { "com.vendor" },
                },
            },
        };
    }

    private static readonly byte[] AssetContent = Encoding.UTF8.GetBytes("public sealed class Vendor {}\n");

    private static List<TarMember> DefaultMembers()
    {
        return new List<TarMember>
        {
            new($"{FolderGuid}/pathname", Encoding.UTF8.GetBytes(FolderPath)),
            new($"{FolderGuid}/asset.meta", Meta(FolderGuid)),
            new($"{AssetGuid}/pathname", Encoding.UTF8.GetBytes(AssetPath)),
            new($"{AssetGuid}/asset", AssetContent),
            new($"{AssetGuid}/asset.meta", Meta(AssetGuid)),
        };
    }

    private static void Replace(List<TarMember> members, string name, byte[] data)
    {
        int index = members.FindIndex(member => member.Name == name);
        Assert.That(index, Is.GreaterThanOrEqualTo(0), $"fixture member {name} is missing");
        members[index] = new TarMember(name, data);
    }

    private static byte[] Meta(string guid)
    {
        return Encoding.UTF8.GetBytes($"fileFormatVersion: 2\nguid: {guid}\nDefaultImporter:\n  userData:\n");
    }

    private void AssertFails(List<TarMember> members, string expectedCode)
    {
        ActionFitSdkUnityPackageReadResult result = ActionFitSdkUnityPackageArchive.Read(WriteArchive(members));

        Assert.That(result.Success, Is.False, "archive was expected to be rejected");
        Assert.That(
            result.Diagnostics.Any(item => item.Code == expectedCode),
            Is.True,
            $"expected {expectedCode} but got: {result.FormatMessage()}");
    }

    private static void AssertDiagnostic(ActionFitSdkInstallProfile profile, string expectedCode)
    {
        ActionFitSdkProfileValidationResult result = ActionFitSdkInstallProfileValidator.Validate(profile);

        Assert.That(result.Success, Is.False, "profile was expected to be rejected");
        Assert.That(
            result.Diagnostics.Any(item => item.Code == expectedCode),
            Is.True,
            $"expected {expectedCode} but got: {result.FormatMessage()}");
    }

    private static string Sha256(byte[] data)
    {
        using SHA256 sha = SHA256.Create();
        return BitConverter.ToString(sha.ComputeHash(data)).Replace("-", "").ToLowerInvariant();
    }

    // ---------- minimal ustar writer ----------

    private sealed class TarMember
    {
        public TarMember(string name, byte[] data)
        {
            Name = name;
            Data = data ?? Array.Empty<byte>();
        }

        public string Name { get; }
        public byte[] Data { get; }
        public char TypeFlag { get; set; } = '0';
    }

    private string WriteArchive(List<TarMember> members)
    {
        var tar = new MemoryStream();
        foreach (TarMember member in members)
        {
            tar.Write(BuildHeader(member), 0, 512);
            tar.Write(member.Data, 0, member.Data.Length);
            int padding = (512 - (member.Data.Length % 512)) % 512;
            tar.Write(new byte[padding], 0, padding);
        }

        tar.Write(new byte[1024], 0, 1024);

        string path = Path.Combine(_temporaryRoot, Guid.NewGuid().ToString("N") + ".unitypackage");
        File.WriteAllBytes(path, Compress(tar.ToArray()));
        return path;
    }

    private static byte[] BuildHeader(TarMember member)
    {
        var header = new byte[512];
        WriteAscii(header, 0, member.Name, 100);
        WriteAscii(header, 100, "0000644", 8);
        WriteAscii(header, 108, "0000000", 8);
        WriteAscii(header, 116, "0000000", 8);
        WriteAscii(header, 124, Convert.ToString(member.Data.Length, 8).PadLeft(11, '0'), 12);
        WriteAscii(header, 136, Convert.ToString(0, 8).PadLeft(11, '0'), 12);
        for (int i = 148; i < 156; i++) header[i] = (byte)' ';
        header[156] = (byte)member.TypeFlag;
        WriteAscii(header, 257, "ustar", 6);
        header[263] = (byte)'0';
        header[264] = (byte)'0';

        int checksum = header.Sum(value => (int)value);
        WriteAscii(header, 148, Convert.ToString(checksum, 8).PadLeft(6, '0'), 7);
        header[154] = 0;
        header[155] = (byte)' ';
        return header;
    }

    private static void WriteAscii(byte[] buffer, int offset, string value, int length)
    {
        byte[] bytes = Encoding.ASCII.GetBytes(value);
        int count = Math.Min(bytes.Length, length - 1);
        Array.Copy(bytes, 0, buffer, offset, count);
    }

    private static byte[] Compress(byte[] raw)
    {
        using var output = new MemoryStream();
        using (var gzip = new GZipStream(output, CompressionMode.Compress, true))
            gzip.Write(raw, 0, raw.Length);
        return output.ToArray();
    }

    private static byte[] Decompress(byte[] archive)
    {
        using var input = new MemoryStream(archive);
        using var gzip = new GZipStream(input, CompressionMode.Decompress);
        using var output = new MemoryStream();
        gzip.CopyTo(output);
        return output.ToArray();
    }
}
#endif
