#if UNITY_EDITOR
using System;
using System.IO;
using NUnit.Framework;

public sealed class ActionFitPackageSkillScaffoldApiTests
{
    private string _root;
    private string _packageRoot;

    [SetUp]
    public void SetUp()
    {
        _root = Path.Combine(Path.GetTempPath(), "ActionFitPackageSkillScaffoldTests", Guid.NewGuid().ToString("N"));
        _packageRoot = Path.Combine(_root, "com.actionfit.sample");
        Directory.CreateDirectory(_packageRoot);
        File.WriteAllText(
            Path.Combine(_packageRoot, "package.json"),
            "{\"name\":\"com.actionfit.sample\",\"displayName\":\"Sample Tools\",\"description\":\"Sample package description.\"}");
    }

    [TearDown]
    public void TearDown()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, true);
    }

    [Test]
    public void FirstSkillCreatesSchemaV2HelpForBothAgentsAndRequestedSkill()
    {
        ActionFitPackageSkillScaffoldResult result = Add("sample-todo", "read-only", "codex");

        Assert.That(result.Success, Is.True);
        Assert.That(result.Code, Is.EqualTo("SKILL_SYSTEM_CREATED"));
        string manifest = Read("Skills~", "manifest.json");
        Assert.That(manifest, Does.Contain("\"schemaVersion\": 2"));
        Assert.That(manifest, Does.Contain("\"skillPrefix\": \"sample\""));
        Assert.That(manifest, Does.Contain("\"helpSkill\": \"sample-help\""));
        Assert.That(manifest, Does.Contain("\"access\": \"read-only\""));
        Assert.That(File.Exists(PackagePath("Skills~", "Codex", "sample-help", "SKILL.md")), Is.True);
        Assert.That(File.Exists(PackagePath("Skills~", "Claude", "sample-help", "SKILL.md")), Is.True);
        Assert.That(File.Exists(PackagePath("Skills~", "Codex", "sample-todo", "SKILL.md")), Is.True);
        Assert.That(File.Exists(PackagePath("Skills~", "Claude", "sample-todo", "SKILL.md")), Is.False);
        Assert.That(Read("Skills~", "Codex", "sample-help", "SKILL.md"), Does.Contain("PACKAGE_SKILLS.md"));
        Assert.That(File.Exists(PackagePath("Skills~", "Codex", "sample-help", "agents", "openai.yaml")), Is.True);
    }

    [Test]
    public void ExistingSkillAndUserModifiedHelpArePreservedWhileAddingAnotherSkill()
    {
        Add("sample-todo", "read-only", "codex");
        string helpPath = PackagePath("Skills~", "Codex", "sample-help", "SKILL.md");
        File.AppendAllText(helpPath, "\nUser package guidance.\n");

        ActionFitPackageSkillScaffoldResult duplicate = Add("sample-todo", "write-capable", "claude");
        ActionFitPackageSkillScaffoldResult added = Add("sample-run", "write-capable", "claude");

        Assert.That(duplicate.Success, Is.False);
        Assert.That(duplicate.Code, Is.EqualTo("SKILL_ALREADY_EXISTS"));
        Assert.That(added.Success, Is.True);
        Assert.That(added.Code, Is.EqualTo("SKILL_ADDED"));
        Assert.That(Read("Skills~", "Codex", "sample-help", "SKILL.md"), Does.Contain("User package guidance."));
        Assert.That(File.Exists(PackagePath("Skills~", "Claude", "sample-run", "SKILL.md")), Is.True);
        string manifest = Read("Skills~", "manifest.json");
        Assert.That(manifest, Does.Contain("\"name\": \"sample-run\""));
        Assert.That(manifest, Does.Contain("\"access\": \"write-capable\""));
    }

    [Test]
    public void PrefixAndPackageMismatchFailBeforeWritingSources()
    {
        ActionFitPackageSkillScaffoldResult invalidPrefix = ActionFitPackageSkillScaffoldApi.AddAtPackageRoot(
            _packageRoot,
            new ActionFitPackageSkillScaffoldRequest
            {
                PackageId = "com.actionfit.sample",
                SkillPrefix = "Sample Tools",
                SkillName = "sample-todo",
                Description = "Inspect sample work when requested.",
                Agents = new[] { "codex" },
                Access = "read-only",
            });
        ActionFitPackageSkillScaffoldResult mismatch = ActionFitPackageSkillScaffoldApi.AddAtPackageRoot(
            _packageRoot,
            new ActionFitPackageSkillScaffoldRequest
            {
                PackageId = "com.actionfit.other",
                SkillPrefix = "sample",
                SkillName = "sample-todo",
                Description = "Inspect sample work when requested.",
                Agents = new[] { "codex" },
                Access = "read-only",
            });
        ActionFitPackageSkillScaffoldResult uppercaseName = ActionFitPackageSkillScaffoldApi.AddAtPackageRoot(
            _packageRoot,
            new ActionFitPackageSkillScaffoldRequest
            {
                PackageId = "com.actionfit.sample",
                SkillPrefix = "sample",
                SkillName = "Sample-todo",
                Description = "Inspect sample work when requested.",
                Agents = new[] { "codex" },
                Access = "read-only",
            });

        Assert.That(invalidPrefix.Code, Is.EqualTo("SKILL_PREFIX_INVALID"));
        Assert.That(mismatch.Code, Is.EqualTo("PACKAGE_ID_MISMATCH"));
        Assert.That(uppercaseName.Code, Is.EqualTo("SKILL_NAME_INVALID"));
        Assert.That(Directory.Exists(PackagePath("Skills~")), Is.False);
    }

    [Test]
    public void UnsupportedAgentFailsWithoutSilentlyCreatingSupportedSources()
    {
        ActionFitPackageSkillScaffoldResult result = ActionFitPackageSkillScaffoldApi.AddAtPackageRoot(
            _packageRoot,
            new ActionFitPackageSkillScaffoldRequest
            {
                PackageId = "com.actionfit.sample",
                SkillPrefix = "sample",
                SkillName = "sample-todo",
                Description = "Inspect sample work when requested.",
                Agents = new[] { "codex", "other" },
                Access = "read-only",
            });

        Assert.That(result.Success, Is.False);
        Assert.That(result.Code, Is.EqualTo("SKILL_AGENT_UNSUPPORTED"));
        Assert.That(Directory.Exists(PackagePath("Skills~")), Is.False);
    }

    private ActionFitPackageSkillScaffoldResult Add(string skillName, string access, params string[] agents)
    {
        return ActionFitPackageSkillScaffoldApi.AddAtPackageRoot(
            _packageRoot,
            new ActionFitPackageSkillScaffoldRequest
            {
                PackageId = "com.actionfit.sample",
                SkillPrefix = "sample",
                SkillName = skillName,
                Description = $"Use {skillName} when the sample package workflow is requested.",
                Agents = agents,
                Access = access,
            });
    }

    private string PackagePath(params string[] parts)
    {
        string path = _packageRoot;
        foreach (string part in parts) path = System.IO.Path.Combine(path, part);
        return path;
    }

    private string Read(params string[] parts) => File.ReadAllText(PackagePath(parts));
}
#endif
