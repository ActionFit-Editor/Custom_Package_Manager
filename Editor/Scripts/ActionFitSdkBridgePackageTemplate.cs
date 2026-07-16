#if UNITY_EDITOR
using System;
using System.IO;
using System.Text;
using UnityEditor;

/// <summary>Creates a public, source-only ActionFit bridge package around one SDK install profile.</summary>
public static class ActionFitSdkBridgePackageTemplate
{
    public const string ManagerPackageId = "com.actionfit.custompackagemanager";
    public const string ProfileRelativePath = "Editor/SDKInstallProfile.json";
    public const string ThirdPartyNoticesFileName = "THIRD_PARTY_NOTICES.md";

    /// <summary>Creates the normal package skeleton and adds the SDK bridge contract files.</summary>
    public static ActionFitPackageInfo_SO Create(
        ActionFitPackageCreateRequest packageRequest,
        ActionFitSdkInstallProfile profile)
    {
        ValidateRequest(packageRequest, profile);
        string requestedRoot = Path.GetFullPath(Path.Combine("Packages", packageRequest.PackageId));
        if (Directory.Exists(requestedRoot))
            throw new InvalidOperationException($"SDK bridge package already exists and will not be overwritten: {packageRequest.PackageId}");
        ActionFitPackageInfo_SO packageInfo = ActionFitPackageInfoUtility.CreatePackage(packageRequest);
        string packageRoot = Path.Combine("Packages", packageRequest.PackageId);
        WriteManagerDependency(Path.GetFullPath(packageRoot));
        WriteBridgeFiles(Path.GetFullPath(packageRoot), packageRequest.PackageId, profile);
        AssetDatabase.Refresh(ImportAssetOptions.ForceSynchronousImport | ImportAssetOptions.ForceUpdate);
        ActionFitPackageInfoUtility.ValidatePackageContract(packageRequest.PackageId);
        return packageInfo;
    }

    internal static void ValidateRequest(
        ActionFitPackageCreateRequest packageRequest,
        ActionFitSdkInstallProfile profile)
    {
        if (packageRequest == null) throw new ArgumentNullException(nameof(packageRequest));
        if (!packageRequest.RepositoryVisibilitySpecified ||
            packageRequest.RepositoryVisibility != ActionFitPackageRepositoryVisibility.Public)
        {
            throw new InvalidOperationException("SDK bridge packages must explicitly use Public repository visibility.");
        }

        ActionFitSdkProfileValidationResult validation = ActionFitSdkInstallProfileValidator.Validate(profile);
        if (!validation.Success) throw new InvalidOperationException(validation.FormatMessage());
        if (!string.Equals(packageRequest.PackageId?.Trim(), profile.BridgePackageId, StringComparison.Ordinal))
            throw new InvalidOperationException("Package request ID must exactly match SDK profile BridgePackageId.");
    }

    internal static void WriteBridgeFiles(
        string packageRoot,
        string packageId,
        ActionFitSdkInstallProfile profile)
    {
        if (string.IsNullOrWhiteSpace(packageRoot)) throw new ArgumentException("Package root is required.", nameof(packageRoot));
        if (!string.Equals(packageId, profile?.BridgePackageId, StringComparison.Ordinal))
            throw new InvalidOperationException("Bridge package ID does not match the SDK profile.");

        string fullRoot = Path.GetFullPath(packageRoot);
        string editorRoot = Path.Combine(fullRoot, "Editor");
        string testRoot = Path.Combine(fullRoot, "Tests", "Editor");
        Directory.CreateDirectory(editorRoot);
        Directory.CreateDirectory(testRoot);

        File.WriteAllText(Path.Combine(editorRoot, "SDKInstallProfile.json"), profile.ToJson(), new UTF8Encoding(false));
        File.WriteAllText(Path.Combine(fullRoot, ThirdPartyNoticesFileName), BuildThirdPartyNotices(profile), new UTF8Encoding(false));
        File.WriteAllText(Path.Combine(testRoot, packageId + ".Editor.Tests.asmdef"), BuildTestAsmdef(packageId), new UTF8Encoding(false));
        File.WriteAllText(Path.Combine(testRoot, "SdkInstallProfileContractTests.cs"), BuildProfileTest(packageId), new UTF8Encoding(false));
    }

    internal static void WriteManagerDependency(string packageRoot)
    {
        UnityEditor.PackageManager.PackageInfo manager =
            UnityEditor.PackageManager.PackageInfo.FindForAssembly(typeof(ActionFitSdkBridgePackageTemplate).Assembly);
        if (manager == null || string.IsNullOrWhiteSpace(manager.version))
            throw new InvalidOperationException("Could not resolve the installed Custom Package Manager version for the bridge dependency.");
        string packageJsonPath = Path.Combine(Path.GetFullPath(packageRoot), "package.json");
        if (!File.Exists(packageJsonPath)) throw new FileNotFoundException("Bridge package.json was not found.", packageJsonPath);
        string packageJson = File.ReadAllText(packageJsonPath);
        string updated = ActionFitPackageManifestUtility.SetDependency(packageJson, ManagerPackageId, manager.version);
        ActionFitPackageManifestUtility.WriteAtomic(packageJsonPath, updated);
    }

    private static string BuildThirdPartyNotices(ActionFitSdkInstallProfile profile)
    {
        return "# Third-Party Notices\n\n" +
               $"This bridge package contains no redistributed {profile.Vendor} SDK binaries, archives, credentials, or vendor configuration files.\n\n" +
               $"- SDK: {profile.DisplayName}\n" +
               $"- Vendor: {profile.Vendor}\n" +
               $"- License: {profile.LicenseUrl}\n" +
               $"- Support: {profile.SupportUrl}\n\n" +
               "The SDK install profile references immutable official sources. Review and accept the vendor terms before explicit installation.\n";
    }

    private static string BuildTestAsmdef(string packageId)
    {
        return "{\n" +
               $"  \"name\": \"{EscapeJson(packageId)}.Editor.Tests\",\n" +
               "  \"references\": [\"com.actionfit.custompackagemanager.Editor\"],\n" +
               "  \"includePlatforms\": [\"Editor\"],\n" +
               "  \"autoReferenced\": false,\n" +
               "  \"optionalUnityReferences\": [\"TestAssemblies\"]\n" +
               "}\n";
    }

    private static string BuildProfileTest(string packageId)
    {
        return "#if UNITY_EDITOR\n" +
               "using NUnit.Framework;\n\n" +
               "public sealed class SdkInstallProfileContractTests\n" +
               "{\n" +
               "    [Test]\n" +
               "    public void Profile_IsValidAndOwnedByThisBridgePackage()\n" +
               "    {\n" +
               $"        var profile = ActionFitSdkInstallApi.ReadProfile(\"Packages/{EscapeCSharp(packageId)}/Editor/SDKInstallProfile.json\");\n" +
               $"        Assert.That(profile.BridgePackageId, Is.EqualTo(\"{EscapeCSharp(packageId)}\"));\n" +
               "        Assert.That(ActionFitSdkInstallProfileValidator.Validate(profile).Success, Is.True);\n" +
               "    }\n" +
               "}\n" +
               "#endif\n";
    }

    private static string EscapeJson(string value)
    {
        return (value ?? "").Replace("\\", "\\\\").Replace("\"", "\\\"");
    }

    private static string EscapeCSharp(string value)
    {
        return EscapeJson(value).Replace("\n", "\\n").Replace("\r", "\\r");
    }
}
#endif
