#if UNITY_EDITOR
using System;
using System.IO;
using NUnit.Framework;

public class ActionFitAgentSkillProfileTests
{
    private string _root;

    [SetUp]
    public void SetUp()
    {
        _root = Path.Combine(Path.GetTempPath(), "ActionFitAgentSkillProfileTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
        WriteProfile("all");
    }

    [TearDown]
    public void TearDown()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, true);
    }

    [Test]
    public void CorePreviewAndApplyAreDeterministicAndAllRestoresBothAgents()
    {
        AddManagedSkill("com.actionfit.ai-jira", ".agents/skills/jira-help", "jira");
        AddManagedSkill("com.actionfit.ai-jira", ".claude/skills/jira-help", "jira");
        AddManagedSkill("com.actionfit.optional", ".agents/skills/optional-help", "optional");
        AddManagedSkill("com.actionfit.optional", ".claude/skills/optional-help", "optional");

        ActionFitAgentSkillProfileResult first =
            ActionFitAgentSkillProfileService.Preview(_root, "core");
        ActionFitAgentSkillProfileResult second =
            ActionFitAgentSkillProfileService.Preview(_root, "core");

        Assert.That(first.Deactivate, Is.EqualTo(2));
        Assert.That(first.ExactPreview, Is.EqualTo(second.ExactPreview));
        ActionFitAgentSkillProfileService.Apply(_root, "core");
        Assert.That(Directory.Exists(Path.Combine(_root, ".agents/skills/optional-help")), Is.False);
        Assert.That(Directory.Exists(Path.Combine(
            _root,
            "UserSettings/ActionFitPackageManager/InactiveSkills/.agents/skills/optional-help")), Is.True);
        Assert.That(Directory.Exists(Path.Combine(_root, ".agents/skills/jira-help")), Is.True);

        ActionFitAgentSkillProfileResult restore =
            ActionFitAgentSkillProfileService.Apply(_root, "all");
        Assert.That(restore.Activate, Is.EqualTo(2));
        Assert.That(Directory.Exists(Path.Combine(_root, ".agents/skills/optional-help")), Is.True);
        Assert.That(Directory.Exists(Path.Combine(_root, ".claude/skills/optional-help")), Is.True);
        Assert.That(ActionFitAgentSkillProfileService.Inspect(_root).Current, Is.EqualTo(4));
    }

    [Test]
    public void ModifiedManagedTargetIsPreserved()
    {
        AddManagedSkill("com.actionfit.optional", ".agents/skills/optional-help", "original");
        File.WriteAllText(
            Path.Combine(_root, ".agents/skills/optional-help/SKILL.md"),
            "user modified");

        ActionFitAgentSkillProfileResult preview =
            ActionFitAgentSkillProfileService.Preview(_root, "core");
        ActionFitAgentSkillProfileService.Apply(_root, "core");

        Assert.That(preview.Preserved, Is.EqualTo(1));
        Assert.That(preview.Operations[0].Reason, Does.Contain("modified"));
        Assert.That(File.ReadAllText(
            Path.Combine(_root, ".agents/skills/optional-help/SKILL.md")), Is.EqualTo("user modified"));
    }

    [Test]
    public void RollbackLastRestoresUnchangedMovesAndPreviousProfile()
    {
        AddManagedSkill("com.actionfit.optional", ".agents/skills/optional-help", "optional");
        ActionFitAgentSkillProfileService.Apply(_root, "core");

        ActionFitAgentSkillProfileService.RollbackLast(_root);

        Assert.That(Directory.Exists(
            Path.Combine(_root, ".agents/skills/optional-help")), Is.True);
        Assert.That(ActionFitAgentSkillProfileService.IsPackageActive(
            _root, "com.actionfit.optional"), Is.True);
    }

    [Test]
    public void RollbackLastPreservesTargetModifiedAfterApply()
    {
        AddManagedSkill("com.actionfit.optional", ".agents/skills/optional-help", "optional");
        ActionFitAgentSkillProfileService.Apply(_root, "core");
        string inactiveSkill = Path.Combine(
            _root,
            "UserSettings/ActionFitPackageManager/InactiveSkills/.agents/skills/optional-help");
        File.WriteAllText(Path.Combine(inactiveSkill, "SKILL.md"), "modified after apply");

        InvalidOperationException error = Assert.Throws<InvalidOperationException>(
            () => ActionFitAgentSkillProfileService.RollbackLast(_root));

        Assert.That(error.Message, Does.Contain("preserved a modified target"));
        Assert.That(Directory.Exists(inactiveSkill), Is.True);
        Assert.That(Directory.Exists(
            Path.Combine(_root, ".agents/skills/optional-help")), Is.False);
    }

    [Test]
    public void ActivePackageFilterUsesSelectedProfileAndRejectsUnknownProfile()
    {
        WriteProfile("core");
        Assert.That(ActionFitAgentSkillProfileService.IsPackageActive(
            _root, "com.actionfit.ai-jira"), Is.True);
        Assert.That(ActionFitAgentSkillProfileService.IsPackageActive(
            _root, "com.actionfit.optional"), Is.False);
        Assert.Throws<InvalidOperationException>(
            () => ActionFitAgentSkillProfileService.Preview(_root, "missing"));
        Assert.Throws<InvalidOperationException>(
            () => ActionFitAgentSkillProfileService.ValidateReferencedPackages(
                _root, new[] { "com.actionfit.optional" }));
    }

    private void AddManagedSkill(string packageId, string targetPath, string content)
    {
        string directory = Path.Combine(_root, targetPath);
        Directory.CreateDirectory(directory);
        File.WriteAllText(Path.Combine(directory, "SKILL.md"), content);
        string hash = ActionFitPackageSkillInstallService.ComputeDirectoryHash(directory);
        string statePath = Path.Combine(
            _root, "UserSettings/ActionFitPackageManager/skill-install-state.json");
        Directory.CreateDirectory(Path.GetDirectoryName(statePath));
        string entry =
            $"{{\"packageId\":\"{packageId}\",\"agent\":\"test\",\"skillName\":\"test\","
            + $"\"targetPath\":\"{targetPath}\",\"installedHash\":\"{hash}\"}}";
        if (!File.Exists(statePath))
        {
            File.WriteAllText(statePath, "{\"schemaVersion\":1,\"autoInstallEnabled\":1,\"entries\":["
                                         + entry + "]}");
            return;
        }
        string current = File.ReadAllText(statePath);
        File.WriteAllText(statePath, current.Replace("]}", "," + entry + "]}"));
    }

    private void WriteProfile(string activeProfile)
    {
        string path = Path.Combine(_root, ActionFitAgentSkillProfileService.ProfileRelativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(path));
        File.WriteAllText(
            path,
            "{\"schemaVersion\":1,\"activeProfile\":\"" + activeProfile + "\",\"profiles\":["
            + "{\"name\":\"core\",\"all\":false,\"packageIds\":[\"com.actionfit.ai-jira\"]},"
            + "{\"name\":\"all\",\"all\":true,\"packageIds\":[]}]}");
    }
}
#endif
