#if UNITY_EDITOR
using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using NUnit.Framework;
using UnityEngine;

public class ActionFitPackageSkillInstallServiceTests
{
    private string _root;
    private string _projectRoot;
    private string _statePath;
    private string _legacyStatePath;
    private string _tempRoot;

    [SetUp]
    public void SetUp()
    {
        _root = Path.Combine(Path.GetTempPath(), "ActionFitPackageSkillTests", Guid.NewGuid().ToString("N"));
        _projectRoot = Path.Combine(_root, "project");
        _statePath = Path.Combine(_projectRoot, "UserSettings", "ActionFitPackageManager", "skill-install-state.json");
        _legacyStatePath = Path.Combine(_projectRoot, "UserSettings", "AIJira", "skill-install-state.json");
        _tempRoot = Path.Combine(_projectRoot, "Temp", "ActionFitPackageSkills");
        Directory.CreateDirectory(_projectRoot);
    }

    [TearDown]
    public void TearDown()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, true);
    }

    [Test]
    public void FirstInstallAndRepeatAreIdempotentWithSharedResources()
    {
        string packageRoot = CreatePackage("com.actionfit.skill-one", "shared-skill", true, "codex", "claude");
        WriteFile(Path.Combine(packageRoot, "Skills~", "Shared", "scripts", "helper.py"), "shared helper");

        ActionFitPackageSkillInstallResult first = Install(packageRoot);
        ActionFitPackageSkillInstallResult second = Install(packageRoot);

        Assert.That(first.Installed, Is.EqualTo(2));
        Assert.That(second.Unchanged, Is.EqualTo(2));
        Assert.That(second.Installed + second.Updated, Is.Zero);
        Assert.That(File.ReadAllText(Target(".agents", "shared-skill", "scripts", "helper.py")),
            Is.EqualTo("shared helper"));
        Assert.That(File.ReadAllText(Target(".claude", "shared-skill", "scripts", "helper.py")),
            Is.EqualTo("shared helper"));
    }

    [Test]
    public void SchemaV2HelpInstallGeneratesDeterministicPackageInventory()
    {
        string packageRoot = CreateSchemaV2Package(
            "com.actionfit.skill-one", "sample", "sample-todo", "read-only", "codex", "claude");

        ActionFitPackageSkillInstallResult first = Install(packageRoot);
        ActionFitPackageSkillInstallResult second = Install(packageRoot);

        Assert.That(first.Installed, Is.EqualTo(4));
        Assert.That(second.Unchanged, Is.EqualTo(4));
        string inventory = File.ReadAllText(Target(".agents", "sample-help", "PACKAGE_SKILLS.md"));
        Assert.That(inventory, Does.Contain("com.actionfit.skill-one"));
        Assert.That(inventory, Does.Contain("Skill One"));
        Assert.That(inventory, Does.Contain("A package used by installer tests."));
        Assert.That(inventory, Does.Contain("`$sample-help`"));
        Assert.That(inventory, Does.Contain("`$sample-todo`"));
        Assert.That(inventory, Does.Contain("read-only"));
        Assert.That(Inspect(packageRoot).Packages.Single().Current, Is.EqualTo(4));
    }

    [Test]
    public void SchemaV2RelatedDescriptionChangeRefreshesGeneratedHelpInventory()
    {
        string packageRoot = CreateSchemaV2Package(
            "com.actionfit.skill-one", "sample", "sample-run", "write-capable", "codex");
        Install(packageRoot);
        WriteFile(
            Path.Combine(packageRoot, "Skills~", "Codex", "sample-run", "SKILL.md"),
            "---\nname: sample-run\ndescription: Run the updated package workflow only after explicit approval.\n---\n\n# Run\n");

        ActionFitPackageSkillInstallResult result = Install(packageRoot);

        Assert.That(result.Updated, Is.EqualTo(2));
        string inventory = File.ReadAllText(Target(".agents", "sample-help", "PACKAGE_SKILLS.md"));
        Assert.That(inventory, Does.Contain("Run the updated package workflow only after explicit approval."));
        Assert.That(inventory, Does.Contain("write-capable"));
    }

    [Test]
    public void InvalidSchemaV2WithoutMandatoryHelpRejectsEntirePackage()
    {
        string packageRoot = CreatePackageRoot("com.actionfit.skill-one");
        WriteSkill(packageRoot, "Codex", "sample-todo", "instructions");
        WriteManifest(packageRoot,
            "{\n  \"schemaVersion\": 2,\n  \"skillPrefix\": \"sample\",\n  \"helpSkill\": \"sample-help\",\n"
            + "  \"skills\": [\n"
            + "    { \"name\": \"sample-todo\", \"agents\": [\"codex\"], \"includeShared\": false, \"access\": \"read-only\" }\n"
            + "  ]\n}");

        ActionFitPackageSkillInstallResult result = Install(packageRoot);

        Assert.That(result.Installed, Is.Zero);
        Assert.That(result.Warnings.Any(message => message.Contains("help skill is not registered")), Is.True);
        Assert.That(Directory.Exists(Target(".agents", "sample-todo")), Is.False);
    }

    [Test]
    public void PackageUpdateRefreshesOnlyUnmodifiedManagedSkill()
    {
        string packageRoot = CreatePackage("com.actionfit.skill-one", "update-skill", false, "codex", "claude");
        Install(packageRoot);
        WriteSkill(packageRoot, "Codex", "update-skill", "updated instructions");

        ActionFitPackageSkillInstallResult result = Install(packageRoot);

        Assert.That(result.Updated, Is.EqualTo(1));
        Assert.That(result.Unchanged, Is.EqualTo(1));
        Assert.That(File.ReadAllText(Target(".agents", "update-skill", "SKILL.md")),
            Does.Contain("updated instructions"));
    }

    [Test]
    public void UserModifiedAndUnmanagedTargetsArePreserved()
    {
        string packageRoot = CreatePackage("com.actionfit.skill-one", "preserve-skill", false, "codex");
        string unmanaged = Target(".agents", "preserve-skill", "SKILL.md");
        WriteFile(unmanaged, "user-owned");

        ActionFitPackageSkillInstallResult unmanagedResult = Install(packageRoot);
        Assert.That(unmanagedResult.Warnings, Has.Count.EqualTo(1));
        Assert.That(File.ReadAllText(unmanaged), Is.EqualTo("user-owned"));

        Directory.Delete(Target(".agents", "preserve-skill"), true);
        Install(packageRoot);
        File.WriteAllText(unmanaged, "user-modified");
        WriteSkill(packageRoot, "Codex", "preserve-skill", "package update");

        ActionFitPackageSkillInstallResult modifiedResult = Install(packageRoot);
        Assert.That(modifiedResult.Warnings, Has.Count.EqualTo(1));
        Assert.That(File.ReadAllText(unmanaged), Is.EqualTo("user-modified"));
    }

    [Test]
    public void ConflictingPackageClaimsInstallNeitherSkill()
    {
        string first = CreatePackage("com.actionfit.skill-one", "conflict-skill", false, "codex");
        string second = CreatePackage("com.actionfit.skill-two", "conflict-skill", false, "codex");

        ActionFitPackageSkillInstallResult result = Install(first, second);

        Assert.That(result.Installed, Is.Zero);
        Assert.That(result.Warnings, Has.Count.EqualTo(1));
        Assert.That(result.Warnings[0], Does.Contain("com.actionfit.skill-one"));
        Assert.That(result.Warnings[0], Does.Contain("com.actionfit.skill-two"));
        Assert.That(Directory.Exists(Target(".agents", "conflict-skill")), Is.False);
    }

    [Test]
    public void InvalidNameAndUnsupportedAgentCannotEscapeProjectTargets()
    {
        string packageRoot = CreatePackageRoot("com.actionfit.skill-one");
        WriteManifest(packageRoot,
            "{\n  \"schemaVersion\": 1,\n  \"skills\": [\n"
            + "    { \"name\": \"../escape\", \"agents\": [\"codex\"], \"includeShared\": false },\n"
            + "    { \"name\": \"safe-skill\", \"agents\": [\"unknown\"], \"includeShared\": false }\n"
            + "  ]\n}");

        ActionFitPackageSkillInstallResult result = Install(packageRoot);

        Assert.That(result.Installed, Is.Zero);
        Assert.That(result.Warnings, Has.Count.EqualTo(2));
        Assert.That(Directory.Exists(Path.Combine(_projectRoot, ".agents", "escape")), Is.False);
    }

    [Test]
    public void SharedFileCollisionPreservesTargetAndRejectsRegistration()
    {
        string packageRoot = CreatePackage("com.actionfit.skill-one", "collision-skill", true, "codex");
        WriteFile(Path.Combine(packageRoot, "Skills~", "Codex", "collision-skill", "scripts", "helper.py"),
            "agent helper");
        WriteFile(Path.Combine(packageRoot, "Skills~", "Shared", "scripts", "helper.py"), "shared helper");

        ActionFitPackageSkillInstallResult result = Install(packageRoot);

        Assert.That(result.Installed, Is.Zero);
        Assert.That(result.Warnings, Has.Count.EqualTo(1));
        Assert.That(result.Warnings[0], Does.Contain("collides"));
        Assert.That(Directory.Exists(Target(".agents", "collision-skill")), Is.False);
    }

    [Test]
    public void PackageDisappearanceDoesNotDeleteInstalledSkill()
    {
        string packageRoot = CreatePackage("com.actionfit.skill-one", "orphan-skill", false, "codex");
        Install(packageRoot);

        ActionFitPackageSkillInstallResult result = Install();

        Assert.That(result.Removed, Is.Zero);
        Assert.That(File.Exists(Target(".agents", "orphan-skill", "SKILL.md")), Is.True);
    }

    [Test]
    public void ExplicitRemovalDeletesOnlyUnchangedManagedSkillsAndDisablesAutomaticInstall()
    {
        string packageRoot = CreatePackage("com.actionfit.skill-one", "remove-skill", false, "codex", "claude");
        Install(packageRoot);
        string modified = Target(".claude", "remove-skill", "SKILL.md");
        File.WriteAllText(modified, "keep me");

        ActionFitPackageSkillInstallResult result = ActionFitPackageSkillInstallService.RemoveManaged(
            _projectRoot, _statePath, _tempRoot);

        Assert.That(result.Removed, Is.EqualTo(1));
        Assert.That(result.Warnings, Has.Count.EqualTo(1));
        Assert.That(Directory.Exists(Target(".agents", "remove-skill")), Is.False);
        Assert.That(File.ReadAllText(modified), Is.EqualTo("keep me"));
        Assert.That(ActionFitPackageSkillInstallService.IsAutoInstallEnabled(_statePath), Is.False);
    }

    [Test]
    public void LegacyAiJiraOwnershipMigratesOnlyWhenTargetHashStillMatches()
    {
        string packageRoot = CreatePackage("com.actionfit.ai-jira", "legacy-skill", false, "codex");
        string targetDirectory = Target(".agents", "legacy-skill");
        WriteFile(Path.Combine(targetDirectory, "SKILL.md"), SkillText("legacy-skill", "legacy instructions"));
        string hash = ActionFitPackageSkillInstallService.ComputeDirectoryHash(targetDirectory);
        WriteFile(_legacyStatePath,
            "{\n  \"autoInstallEnabled\": 1,\n  \"entries\": [\n"
            + $"    {{ \"targetPath\": \".agents/skills/legacy-skill\", \"installedHash\": \"{hash}\" }}\n"
            + "  ]\n}");

        ActionFitPackageSkillInstallResult result = Install(packageRoot);

        Assert.That(result.Updated, Is.EqualTo(1));
        Assert.That(File.ReadAllText(Path.Combine(targetDirectory, "SKILL.md")), Does.Contain("codex instructions"));
        Assert.That(File.ReadAllText(_statePath), Does.Contain("com.actionfit.ai-jira"));
        Assert.That(File.Exists(_legacyStatePath), Is.True);
    }

    [Test]
    public void LegacyDisabledAutomaticInstallPreferenceIsPreserved()
    {
        WriteFile(_legacyStatePath, "{\n  \"autoInstallEnabled\": 0,\n  \"entries\": []\n}");

        Assert.That(ActionFitPackageSkillInstallService.IsAutoInstallEnabled(_statePath, _legacyStatePath), Is.False);
    }

    [Test]
    public void LinkedSourceEntryIsRejected()
    {
        if (Application.platform == RuntimePlatform.WindowsEditor)
            Assert.Ignore("Symbolic-link fixture uses the Unix ln command.");

        string packageRoot = CreatePackage("com.actionfit.skill-one", "linked-skill", false, "codex");
        string outside = Path.Combine(_root, "outside.txt");
        File.WriteAllText(outside, "outside");
        string link = Path.Combine(packageRoot, "Skills~", "Codex", "linked-skill", "linked.txt");
        CreateSymbolicLink(outside, link);

        ActionFitPackageSkillInstallResult result = Install(packageRoot);

        Assert.That(result.Installed, Is.Zero);
        Assert.That(result.Warnings, Has.Count.EqualTo(1));
        Assert.That(Directory.Exists(Target(".agents", "linked-skill")), Is.False);
    }

    [Test]
    public void LinkedTargetAncestorCannotRedirectInstallationOutsideProject()
    {
        if (Application.platform == RuntimePlatform.WindowsEditor)
            Assert.Ignore("Symbolic-link fixture uses the Unix ln command.");

        string packageRoot = CreatePackage("com.actionfit.skill-one", "target-link-skill", false, "codex");
        string outside = Path.Combine(_root, "outside-target");
        Directory.CreateDirectory(outside);
        CreateSymbolicLink(outside, Path.Combine(_projectRoot, ".agents"));

        ActionFitPackageSkillInstallResult result = Install(packageRoot);

        Assert.That(result.Installed, Is.Zero);
        Assert.That(result.Warnings, Has.Count.EqualTo(1));
        Assert.That(result.Warnings[0], Does.Contain("linked skill path"));
        Assert.That(Directory.Exists(Path.Combine(outside, "skills", "target-link-skill")), Is.False);
    }

    [Test]
    public void DiscoveryPrefersEmbeddedPackageOverCachedCopy()
    {
        string embedded = CreatePackageAt(
            Path.Combine(_projectRoot, "Packages", "com.actionfit.skill-one"),
            "com.actionfit.skill-one", "discovery-skill", false, "codex");
        CreatePackageAt(
            Path.Combine(_projectRoot, "Library", "PackageCache", "com.actionfit.skill-one@hash"),
            "com.actionfit.skill-one", "discovery-skill", false, "codex");

        var roots = ActionFitPackageSkillBootstrap.FindRegisteredPackageRoots(_projectRoot);

        Assert.That(roots, Is.EqualTo(new[] { embedded }));
    }

    [Test]
    public void InspectionReportsMissingCurrentUpdateAndPreservedTargets()
    {
        string packageRoot = CreatePackage(
            "com.actionfit.skill-one", "status-skill", false, "codex", "claude");

        ActionFitPackageSkillStatus missing = Inspect(packageRoot).Packages.Single();
        Assert.That(missing.Registered, Is.EqualTo(2));
        Assert.That(missing.Missing, Is.EqualTo(2));

        Install(packageRoot);
        ActionFitPackageSkillStatus current = Inspect(packageRoot).Packages.Single();
        Assert.That(current.Current, Is.EqualTo(2));

        WriteSkill(packageRoot, "Codex", "status-skill", "package update");
        File.WriteAllText(Target(".claude", "status-skill", "SKILL.md"), "user-modified");

        ActionFitPackageSkillStatus changed = Inspect(packageRoot).Packages.Single();
        Assert.That(changed.UpdateAvailable, Is.EqualTo(1));
        Assert.That(changed.Preserved, Is.EqualTo(1));
        Assert.That(changed.Summary, Does.Contain("update available 1"));
        Assert.That(changed.Summary, Does.Contain("preserved 1"));
    }

    private ActionFitPackageSkillInstallResult Install(params string[] packageRoots)
    {
        return ActionFitPackageSkillInstallService.InstallOrRefresh(
            packageRoots, _projectRoot, _statePath, _tempRoot, _legacyStatePath);
    }

    private ActionFitPackageSkillInspectionResult Inspect(params string[] packageRoots)
    {
        return ActionFitPackageSkillInstallService.Inspect(
            packageRoots, _projectRoot, _statePath, _legacyStatePath);
    }

    private string CreatePackage(string packageId, string skillName, bool includeShared, params string[] agents)
    {
        return CreatePackageAt(Path.Combine(_root, packageId), packageId, skillName, includeShared, agents);
    }

    private string CreateSchemaV2Package(
        string packageId,
        string prefix,
        string skillName,
        string access,
        params string[] agents)
    {
        string packageRoot = CreatePackageRoot(packageId);
        string helpName = prefix + "-help";
        string agentJson = string.Join(", ", Array.ConvertAll(agents, agent => $"\"{agent}\""));
        WriteManifest(packageRoot,
            "{\n  \"schemaVersion\": 2,\n"
            + $"  \"skillPrefix\": \"{prefix}\",\n  \"helpSkill\": \"{helpName}\",\n"
            + "  \"skills\": [\n"
            + $"    {{ \"name\": \"{helpName}\", \"agents\": [{agentJson}], \"includeShared\": false, \"access\": \"read-only\" }},\n"
            + $"    {{ \"name\": \"{skillName}\", \"agents\": [{agentJson}], \"includeShared\": false, \"access\": \"{access}\" }}\n"
            + "  ]\n}");
        foreach (string agent in agents)
        {
            string sourceDirectory = agent == "codex" ? "Codex" : "Claude";
            WriteSkill(packageRoot, sourceDirectory, helpName, "Read PACKAGE_SKILLS.md before answering.");
            WriteSkill(packageRoot, sourceDirectory, skillName, agent + " instructions");
        }
        return packageRoot;
    }

    private string CreatePackageAt(
        string packageRoot,
        string packageId,
        string skillName,
        bool includeShared,
        params string[] agents)
    {
        CreatePackageRoot(packageRoot, packageId);
        string agentJson = string.Join(", ", Array.ConvertAll(agents, agent => $"\"{agent}\""));
        WriteManifest(packageRoot,
            "{\n  \"schemaVersion\": 1,\n  \"skills\": [\n"
            + $"    {{ \"name\": \"{skillName}\", \"agents\": [{agentJson}], "
            + $"\"includeShared\": {includeShared.ToString().ToLowerInvariant()} }}\n"
            + "  ]\n}");
        foreach (string agent in agents)
        {
            string sourceDirectory = agent == "codex" ? "Codex" : agent == "claude" ? "Claude" : agent;
            WriteSkill(packageRoot, sourceDirectory, skillName, agent + " instructions");
        }
        return packageRoot;
    }

    private string CreatePackageRoot(string packageId)
    {
        return CreatePackageRoot(Path.Combine(_root, packageId), packageId);
    }

    private static string CreatePackageRoot(string packageRoot, string packageId)
    {
        WriteFile(
            Path.Combine(packageRoot, "package.json"),
            $"{{ \"name\": \"{packageId}\", \"displayName\": \"Skill One\", \"description\": \"A package used by installer tests.\" }}");
        return packageRoot;
    }

    private static void WriteManifest(string packageRoot, string json)
    {
        WriteFile(Path.Combine(packageRoot, "Skills~", "manifest.json"), json);
    }

    private static void WriteSkill(string packageRoot, string agent, string skillName, string instructions)
    {
        WriteFile(Path.Combine(packageRoot, "Skills~", agent, skillName, "SKILL.md"),
            SkillText(skillName, instructions));
    }

    private static string SkillText(string skillName, string instructions)
    {
        return $"---\nname: {skillName}\ndescription: Test {skillName}.\n---\n\n# Test\n\n{instructions}\n";
    }

    private string Target(string agentDirectory, string skillName, params string[] children)
    {
        string path = Path.Combine(_projectRoot, agentDirectory, "skills", skillName);
        foreach (string child in children) path = Path.Combine(path, child);
        return path;
    }

    private static void WriteFile(string path, string contents)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path) ?? throw new InvalidOperationException());
        File.WriteAllText(path, contents);
    }

    private static void CreateSymbolicLink(string source, string target)
    {
        using Process process = Process.Start(new ProcessStartInfo
        {
            FileName = "/bin/ln",
            Arguments = $"-s {source} {target}",
            UseShellExecute = false,
            CreateNoWindow = true,
        });
        process?.WaitForExit();
        Assert.That(process?.ExitCode, Is.Zero);
    }
}
#endif
