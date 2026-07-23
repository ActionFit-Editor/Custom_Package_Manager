#!/usr/bin/env python3
from __future__ import annotations

import json
import os
import shutil
import subprocess
import sys
import tempfile
import unittest
from pathlib import Path


SCRIPT_DIR = Path(__file__).resolve().parent
PACKAGE_ROOT = SCRIPT_DIR.parents[1]
VALIDATOR = PACKAGE_ROOT / "Tools~" / "package_contract_validator.py"
FIXTURES = SCRIPT_DIR / "Fixtures~"
PACKAGE_ID = "com.actionfit.sample"
MANAGER_ID = "com.actionfit.custompackagemanager"


class PackageContractValidatorTests(unittest.TestCase):
    maxDiff = None

    def make_repo(self, fixture: str) -> tuple[tempfile.TemporaryDirectory[str], Path, Path]:
        temporary = tempfile.TemporaryDirectory()
        repo_root = Path(temporary.name)
        package_root = repo_root / "Packages" / PACKAGE_ID
        package_root.parent.mkdir(parents=True)
        shutil.copytree(FIXTURES / fixture, package_root)
        return temporary, repo_root, package_root

    def run_cli(self, repo_root: Path, *arguments: str) -> tuple[int, dict]:
        completed = subprocess.run(
            [sys.executable, str(VALIDATOR), "--repo-root", str(repo_root), *arguments],
            check=False,
            stdout=subprocess.PIPE,
            stderr=subprocess.PIPE,
            text=True,
            encoding="utf-8",
        )
        self.assertTrue(completed.stdout, completed.stderr)
        return completed.returncode, json.loads(completed.stdout)

    def git(self, repo_root: Path, *arguments: str) -> None:
        completed = subprocess.run(
            ["git", "-C", str(repo_root), *arguments],
            check=False,
            stdout=subprocess.PIPE,
            stderr=subprocess.PIPE,
            text=True,
            encoding="utf-8",
        )
        self.assertEqual(0, completed.returncode, completed.stderr)

    def initialize_git_repo(self, repo_root: Path) -> None:
        self.git(repo_root, "init")
        self.git(repo_root, "config", "user.email", "package-contract-test@example.invalid")
        self.git(repo_root, "config", "user.name", "Package Contract Test")
        self.git(repo_root, "add", "Packages")
        self.git(repo_root, "commit", "-m", "fixture base")

    def write_downloaded_dependency(
        self,
        repo_root: Path,
        *,
        source: str = "git",
        commit_hash: str = "0123456789abcdef0123456789abcdef01234567",
    ) -> str:
        dependency = "https://example.com/actionfit/sample.git#1.2.3"
        packages_root = repo_root / "Packages"
        (packages_root / "manifest.json").write_text(
            json.dumps({"dependencies": {PACKAGE_ID: dependency}}),
            encoding="utf-8",
        )
        (packages_root / "packages-lock.json").write_text(
            json.dumps(
                {
                    "dependencies": {
                        PACKAGE_ID: {
                            "version": dependency,
                            "depth": 0,
                            "source": source,
                            "dependencies": {},
                            "hash": commit_hash,
                        }
                    }
                }
            ),
            encoding="utf-8",
        )
        return dependency

    def make_manager_repo(self) -> tuple[tempfile.TemporaryDirectory[str], Path, Path]:
        temporary = tempfile.TemporaryDirectory()
        repo_root = Path(temporary.name)
        package_root = repo_root / "Packages" / MANAGER_ID
        package_root.parent.mkdir(parents=True)
        shutil.copytree(FIXTURES / "valid-package", package_root)
        for path in package_root.rglob("*"):
            if not path.is_file():
                continue
            try:
                content = path.read_text(encoding="utf-8")
            except UnicodeDecodeError:
                continue
            path.write_text(content.replace(PACKAGE_ID, MANAGER_ID), encoding="utf-8")
        return temporary, repo_root, package_root

    def write_settings_lifecycle_contract(
        self,
        package_root: Path,
        *,
        runtime: bool = False,
        valid_base: bool = True,
    ) -> None:
        manifest_path = package_root / "package.json"
        manifest = json.loads(manifest_path.read_text(encoding="utf-8"))
        manifest["dependencies"]["com.actionfit.sosingleton"] = "1.0.6"
        manifest_path.write_text(json.dumps(manifest), encoding="utf-8")

        editor_root = package_root / "Editor"
        (editor_root / "SamplePackageMenu.cs").write_text(
            '[MenuItem(MenuRoot + "Setting SO")]\n', encoding="utf-8"
        )
        editor_asmdef_path = editor_root / f"{PACKAGE_ID}.Editor.asmdef"
        editor_asmdef = json.loads(editor_asmdef_path.read_text(encoding="utf-8"))
        editor_asmdef["references"] = [
            "com.actionfit.sosingleton",
            "com.actionfit.sosingleton.Editor",
        ]
        editor_asmdef_path.write_text(json.dumps(editor_asmdef), encoding="utf-8")

        source_root = package_root / ("Runtime" if runtime else "Editor")
        source_root.mkdir(exist_ok=True)
        lifetime = "RuntimeSingleton" if runtime else "EditorOnly"
        base_type = "SO_Singleton<SampleSettingsSO>" if runtime and valid_base else "ScriptableObject"
        (source_root / "SampleSettingsSO.cs").write_text(
            "using ActionFit.SOSingleton;\n"
            f'[ActionFitSettingsAsset("Sample", ActionFitSettingsAssetLifetime.{lifetime})]\n'
            f"public sealed class SampleSettingsSO : {base_type} {{}}\n",
            encoding="utf-8",
        )

    def test_valid_package_and_all_modes_succeed(self) -> None:
        temporary, repo_root, _ = self.make_repo("valid-package")
        self.addCleanup(temporary.cleanup)

        for arguments in (("--package", PACKAGE_ID), ("--all",)):
            code, result = self.run_cli(repo_root, *arguments)
            self.assertEqual(0, code)
            self.assertTrue(result["success"])
            self.assertEqual(1, result["summary"]["packages"])
            self.assertEqual([], result["diagnostics"])

    def test_valid_registered_settings_so_contract_passes(self) -> None:
        temporary, repo_root, package_root = self.make_repo("valid-package")
        self.addCleanup(temporary.cleanup)
        self.write_settings_lifecycle_contract(package_root)

        code, result = self.run_cli(repo_root, "--package", PACKAGE_ID)

        self.assertEqual(0, code, result["diagnostics"])

    def test_registered_settings_so_reports_dependency_menu_asmdef_and_runtime_failures(self) -> None:
        temporary, repo_root, package_root = self.make_repo("valid-package")
        self.addCleanup(temporary.cleanup)
        self.write_settings_lifecycle_contract(package_root, runtime=True, valid_base=False)
        manifest_path = package_root / "package.json"
        manifest = json.loads(manifest_path.read_text(encoding="utf-8"))
        manifest["dependencies"].pop("com.actionfit.sosingleton")
        manifest_path.write_text(json.dumps(manifest), encoding="utf-8")
        (package_root / "Editor" / "SamplePackageMenu.cs").unlink()
        asmdef_path = package_root / "Editor" / f"{PACKAGE_ID}.Editor.asmdef"
        asmdef = json.loads(asmdef_path.read_text(encoding="utf-8"))
        asmdef["references"] = []
        asmdef_path.write_text(json.dumps(asmdef), encoding="utf-8")

        code, result = self.run_cli(repo_root, "--package", PACKAGE_ID)

        self.assertEqual(1, code)
        codes = {item["code"] for item in result["diagnostics"]}
        self.assertTrue(
            {
                "SETTINGS_SO_DEPENDENCY_MISSING",
                "SETTINGS_SO_MENU_MISSING",
                "SETTINGS_SO_ASMDEF_REFERENCE_MISSING",
                "SETTINGS_SO_RUNTIME_BASE_INVALID",
            }.issubset(codes)
        )

    def test_invalid_fixture_reports_stable_contract_codes(self) -> None:
        temporary, repo_root, _ = self.make_repo("invalid-package")
        self.addCleanup(temporary.cleanup)

        code, result = self.run_cli(repo_root, "--package", PACKAGE_ID)

        self.assertEqual(1, code)
        self.assertFalse(result["success"])
        codes = {item["code"] for item in result["diagnostics"]}
        self.assertTrue(
            {
                "PACKAGE_ID_DIRECTORY_MISMATCH",
                "PACKAGE_VERSION_INVALID_SEMVER",
                "README_INSTALL_VERSION_MISMATCH",
                "AI_GUIDE_PACKAGE_ID_MISMATCH",
                "AI_GUIDE_VERSION_MISMATCH",
                "PACKAGE_INFO_FIELD_MISMATCH",
                "ASMDEF_NAME_PACKAGE_MISMATCH",
                "ASMDEF_EDITOR_PLATFORM_MISSING",
            }.issubset(codes)
        )
        required_fields = {"code", "severity", "path", "line", "message", "suggestedFix"}
        self.assertTrue(all(required_fields == set(item) for item in result["diagnostics"]))

    def test_invalid_json_is_validation_failure(self) -> None:
        temporary, repo_root, package_root = self.make_repo("valid-package")
        self.addCleanup(temporary.cleanup)
        (package_root / "package.json").write_text('{"name": ', encoding="utf-8")

        code, result = self.run_cli(repo_root, "--package", PACKAGE_ID)

        self.assertEqual(1, code)
        self.assertIn("PACKAGE_JSON_INVALID", {item["code"] for item in result["diagnostics"]})

    def test_valid_source_only_sdk_bridge_passes(self) -> None:
        temporary, repo_root, package_root = self.make_repo("valid-package")
        self.addCleanup(temporary.cleanup)
        self.write_sdk_bridge_contract(package_root)

        code, result = self.run_cli(repo_root, "--package", PACKAGE_ID)

        self.assertEqual(0, code, result["diagnostics"])
        self.assertTrue(result["success"])

    def test_sdk_bridge_schema_two_latest_policy_requires_safe_matching_metadata(self) -> None:
        temporary, repo_root, package_root = self.make_repo("valid-package")
        self.addCleanup(temporary.cleanup)
        self.write_sdk_bridge_contract(package_root)
        profile_path = package_root / "Editor" / "SDKInstallProfile.json"
        profile = json.loads(profile_path.read_text(encoding="utf-8"))
        profile["SchemaVersion"] = 2
        source = profile["Sources"][0]
        source["ImmutableVersion"] = ""
        source["ResolutionPolicy"] = "anyInstalledElseLatestStable"
        source["LatestResolver"] = "registryMetadata"
        source["MetadataUrl"] = "https://registry.example.com/com.vendor.sdk"
        source["VersionFamily"] = "vendor-sdk"
        profile_path.write_text(json.dumps(profile), encoding="utf-8")

        valid_code, valid_result = self.run_cli(repo_root, "--package", PACKAGE_ID)

        self.assertEqual(0, valid_code, valid_result["diagnostics"])

        source["LatestResolver"] = "gitRelease"
        source["MetadataUrl"] = "https://evil.example.net/metadata?token=secret"
        profile_path.write_text(json.dumps(profile), encoding="utf-8")
        invalid_code, invalid_result = self.run_cli(repo_root, "--package", PACKAGE_ID)

        self.assertEqual(1, invalid_code)
        codes = {item["code"] for item in invalid_result["diagnostics"]}
        self.assertIn("SDK_PROFILE_LATEST_RESOLVER_INVALID", codes)
        self.assertIn("SDK_PROFILE_METADATA_URL_UNSAFE", codes)

    def test_sdk_bridge_accepts_safe_git_subpath_and_rejects_traversal(self) -> None:
        temporary, repo_root, package_root = self.make_repo("valid-package")
        self.addCleanup(temporary.cleanup)
        self.write_sdk_bridge_contract(package_root)
        profile_path = package_root / "Editor" / "SDKInstallProfile.json"
        profile = json.loads(profile_path.read_text(encoding="utf-8"))
        profile["Sources"][0] = {
            "Id": "git",
            "Kind": "git",
            "Url": "https://example.com/vendor/sdk.git",
            "ImmutableRevision": "0123456789abcdef0123456789abcdef01234567",
            "GitSubpath": "Assets/VendorSdk",
            "PackageId": "com.vendor.sdk",
        }
        profile_path.write_text(json.dumps(profile), encoding="utf-8")

        valid_code, valid_result = self.run_cli(repo_root, "--package", PACKAGE_ID)

        self.assertEqual(0, valid_code, valid_result["diagnostics"])

        profile["Sources"][0]["GitSubpath"] = "Assets/../VendorSdk"
        profile_path.write_text(json.dumps(profile), encoding="utf-8")
        invalid_code, invalid_result = self.run_cli(repo_root, "--package", PACKAGE_ID)

        self.assertEqual(1, invalid_code)
        self.assertIn(
            "SDK_PROFILE_GIT_SUBPATH_INVALID",
            {item["code"] for item in invalid_result["diagnostics"]},
        )

    def test_sdk_bridge_rejects_vendor_files_credentials_and_mutable_sources(self) -> None:
        temporary, repo_root, package_root = self.make_repo("valid-package")
        self.addCleanup(temporary.cleanup)
        self.write_sdk_bridge_contract(package_root)
        profile_path = package_root / "Editor" / "SDKInstallProfile.json"
        profile = json.loads(profile_path.read_text(encoding="utf-8"))
        profile["Sources"][0] = {
            "Id": "git",
            "Kind": "git",
            "Url": "https://token@example.com/vendor/sdk.git",
            "ImmutableRevision": "main",
            "PackageId": "com.vendor.sdk",
        }
        profile_path.write_text(json.dumps(profile), encoding="utf-8")
        (package_root / "Runtime").mkdir(exist_ok=True)
        (package_root / "Runtime" / "VendorSdk.dll").write_bytes(b"binary")
        (package_root / "Runtime" / "VendorSdk.framework").mkdir()
        (package_root / "Editor" / "LocalConfig.txt").write_text(
            'access_token = "secret-value-123"\n', encoding="utf-8"
        )

        code, result = self.run_cli(repo_root, "--package", PACKAGE_ID)

        self.assertEqual(1, code)
        codes = {item["code"] for item in result["diagnostics"]}
        self.assertTrue(
            {
                "SDK_PROFILE_SOURCE_URL_UNSAFE",
                "SDK_PROFILE_GIT_REVISION_MUTABLE",
                "SDK_BRIDGE_VENDOR_FILE_FORBIDDEN",
                "SDK_BRIDGE_CREDENTIAL_FORBIDDEN",
            }.issubset(codes)
        )

    @unittest.skipIf(os.name == "nt", "Windows symlink creation requires developer-mode privileges")
    def test_sdk_bridge_rejects_linked_content(self) -> None:
        temporary, repo_root, package_root = self.make_repo("valid-package")
        self.addCleanup(temporary.cleanup)
        self.write_sdk_bridge_contract(package_root)
        outside = repo_root / "vendor-sdk.tgz"
        outside.write_bytes(b"vendor")
        (package_root / "Editor" / "linked-sdk").symlink_to(outside)

        code, result = self.run_cli(repo_root, "--package", PACKAGE_ID)

        self.assertEqual(1, code)
        self.assertIn("SDK_BRIDGE_LINK_FORBIDDEN", {item["code"] for item in result["diagnostics"]})

    def test_valid_registered_skills_pass_package_contract(self) -> None:
        temporary, repo_root, package_root = self.make_repo("valid-package")
        self.addCleanup(temporary.cleanup)
        skills_root = package_root / "Skills~"
        (skills_root / "Codex" / "sample-help").mkdir(parents=True)
        (skills_root / "Claude" / "sample-help").mkdir(parents=True)
        (skills_root / "Codex" / "sample-skill").mkdir(parents=True)
        (skills_root / "Claude" / "sample-skill").mkdir(parents=True)
        (skills_root / "Shared" / "scripts").mkdir(parents=True)
        help_text = (
            "---\n"
            "name: sample-help\n"
            "description: Explain the sample package and its related skills.\n"
            "---\n\n"
            "# Sample Help\n\nRead PACKAGE_SKILLS.md before answering.\n"
        )
        skill_text = (
            "---\n"
            "name: sample-skill\n"
            "description: Validate a sample package skill.\n"
            "---\n\n"
            "# Sample Skill\n"
        )
        (skills_root / "Codex" / "sample-help" / "SKILL.md").write_text(help_text, encoding="utf-8")
        (skills_root / "Claude" / "sample-help" / "SKILL.md").write_text(help_text, encoding="utf-8")
        (skills_root / "Codex" / "sample-skill" / "SKILL.md").write_text(skill_text, encoding="utf-8")
        (skills_root / "Claude" / "sample-skill" / "SKILL.md").write_text(skill_text, encoding="utf-8")
        (skills_root / "Shared" / "scripts" / "helper.py").write_text("print('ok')\n", encoding="utf-8")
        (skills_root / "manifest.json").write_text(
            json.dumps(
                {
                    "schemaVersion": 2,
                    "skillPrefix": "sample",
                    "helpSkill": "sample-help",
                    "skills": [
                        {
                            "name": "sample-help",
                            "agents": ["codex", "claude"],
                            "includeShared": False,
                            "access": "read-only",
                        },
                        {
                            "name": "sample-skill",
                            "agents": ["codex", "claude"],
                            "includeShared": True,
                            "access": "read-only",
                        }
                    ],
                }
            ),
            encoding="utf-8",
        )

        code, result = self.run_cli(repo_root, "--package", PACKAGE_ID)

        self.assertEqual(0, code)
        self.assertTrue(result["success"])

    def write_sdk_bridge_contract(self, package_root: Path) -> None:
        package_json_path = package_root / "package.json"
        package_json = json.loads(package_json_path.read_text(encoding="utf-8"))
        package_json["dependencies"]["com.actionfit.custompackagemanager"] = "1.1.92"
        package_json_path.write_text(json.dumps(package_json), encoding="utf-8")
        profile = {
            "SchemaVersion": 1,
            "ProfileId": "vendor.sdk",
            "ProfileVersion": "1.0.0",
            "Vendor": "Vendor",
            "DisplayName": "Vendor SDK",
            "BridgePackageId": PACKAGE_ID,
            "MinimumUnityVersion": "6000.2",
            "MaximumUnityVersion": "",
            "LicenseUrl": "https://example.com/license",
            "SupportUrl": "https://example.com/support",
            "SupportedPlatforms": ["Android", "iOS"],
            "AllowedDomains": ["example.com"],
            "Sources": [
                {
                    "Id": "registry",
                    "Kind": "registry",
                    "Url": "https://registry.example.com",
                    "ImmutableVersion": "1.2.3",
                    "ImmutableRevision": "",
                    "GitSubpath": "",
                    "PackageId": "com.vendor.sdk",
                    "PackageVersion": "",
                    "Sha256": "",
                    "CacheRelativePath": "",
                }
            ],
            "Modules": [],
            "Dependencies": [],
            "ScopedRegistries": [],
            "DetectionRules": [],
        }
        (package_root / "Editor" / "SDKInstallProfile.json").write_text(
            json.dumps(profile), encoding="utf-8"
        )
        (package_root / "THIRD_PARTY_NOTICES.md").write_text(
            "# Third-Party Notices\n\nThis package contains no redistributed vendor SDK files.\n",
            encoding="utf-8",
        )
        package_info_path = package_root / "Editor" / "PackageInfo" / "ActionFitPackageInfo_SO.asset"
        package_info_path.write_text(
            package_info_path.read_text(encoding="utf-8") + "  _repositoryVisibility: 0\n",
            encoding="utf-8",
        )

    def test_invalid_registered_skills_report_stable_contract_codes(self) -> None:
        temporary, repo_root, package_root = self.make_repo("valid-package")
        self.addCleanup(temporary.cleanup)
        skills_root = package_root / "Skills~"
        help_root = skills_root / "Codex" / "sample-help"
        help_root.mkdir(parents=True)
        (help_root / "SKILL.md").write_text(
            "---\nname: sample-help\ndescription: Explain sample skills.\n---\n\nRead PACKAGE_SKILLS.md.\n",
            encoding="utf-8",
        )
        source_root = skills_root / "Codex" / "safe-skill"
        source_root.mkdir(parents=True)
        (source_root / "SKILL.md").write_text(
            "---\nname: wrong-skill\ndescription:\n---\n",
            encoding="utf-8",
        )
        (source_root / "agents").mkdir()
        (source_root / "agents" / "openai.yaml").write_text(
            "policy:\n  allow_implicit_invocation: false\n",
            encoding="utf-8",
        )
        (skills_root / "manifest.json").write_text(
            json.dumps(
                {
                    "schemaVersion": 2,
                    "skillPrefix": "sample",
                    "helpSkill": "sample-help",
                    "skills": [
                        {"name": "../escape", "agents": ["codex"]},
                        {
                            "name": "sample-help",
                            "agents": ["codex"],
                            "includeShared": False,
                            "access": "read-only",
                        },
                        {
                            "name": "safe-skill",
                            "agents": ["unknown", "codex", "codex"],
                            "includeShared": True,
                            "access": "read-only",
                        },
                    ],
                }
            ),
            encoding="utf-8",
        )

        code, result = self.run_cli(repo_root, "--package", PACKAGE_ID)

        self.assertEqual(1, code)
        codes = {item["code"] for item in result["diagnostics"]}
        self.assertTrue(
            {
                "SKILL_NAME_INVALID",
                "SKILL_AGENT_UNSUPPORTED",
                "SKILL_SHARED_SOURCE_MISSING",
                "SKILL_TARGET_DUPLICATE",
                "SKILL_CODEX_IMPLICIT_INVOCATION_DISABLED",
                "SKILL_FRONTMATTER_NAME_MISMATCH",
                "SKILL_FRONTMATTER_DESCRIPTION_MISSING",
            }.issubset(codes)
        )

    def test_skill_sources_require_explicit_manifest_registration(self) -> None:
        temporary, repo_root, package_root = self.make_repo("valid-package")
        self.addCleanup(temporary.cleanup)
        (package_root / "Skills~" / "Codex" / "sample-skill").mkdir(parents=True)

        code, result = self.run_cli(repo_root, "--package", PACKAGE_ID)

        self.assertEqual(1, code)
        self.assertIn("SKILL_MANIFEST_MISSING", {item["code"] for item in result["diagnostics"]})

    def test_schema_v1_is_runtime_compatible_but_rejected_for_package_contract(self) -> None:
        temporary, repo_root, package_root = self.make_repo("valid-package")
        self.addCleanup(temporary.cleanup)
        skill_root = package_root / "Skills~" / "Codex" / "legacy-skill"
        skill_root.mkdir(parents=True)
        (skill_root / "SKILL.md").write_text(
            "---\nname: legacy-skill\ndescription: Legacy runtime compatibility.\n---\n",
            encoding="utf-8",
        )
        (package_root / "Skills~" / "manifest.json").write_text(
            json.dumps(
                {
                    "schemaVersion": 1,
                    "skills": [{"name": "legacy-skill", "agents": ["codex"]}],
                }
            ),
            encoding="utf-8",
        )

        code, result = self.run_cli(repo_root, "--package", PACKAGE_ID)

        self.assertEqual(1, code)
        self.assertIn("SKILL_MANIFEST_SCHEMA_UNSUPPORTED", {item["code"] for item in result["diagnostics"]})

    def test_schema_v2_requires_help_prefix_access_and_agent_coverage(self) -> None:
        temporary, repo_root, package_root = self.make_repo("valid-package")
        self.addCleanup(temporary.cleanup)
        skills_root = package_root / "Skills~"
        for agent, directory in (("codex", "Codex"), ("claude", "Claude")):
            source = skills_root / directory / "sample-run"
            source.mkdir(parents=True)
            (source / "SKILL.md").write_text(
                "---\nname: sample-run\ndescription: Run sample changes.\n---\n",
                encoding="utf-8",
            )
        help_root = skills_root / "Codex" / "sample-help"
        help_root.mkdir(parents=True)
        (help_root / "SKILL.md").write_text(
            "---\nname: sample-help\ndescription: Explain sample skills.\n---\n\nNo inventory reference.\n",
            encoding="utf-8",
        )
        (skills_root / "manifest.json").write_text(
            json.dumps(
                {
                    "schemaVersion": 2,
                    "skillPrefix": "sample",
                    "helpSkill": "sample-help",
                    "skills": [
                        {
                            "name": "sample-help",
                            "agents": ["codex"],
                            "includeShared": False,
                            "access": "read-only",
                        },
                        {
                            "name": "sample-run",
                            "agents": ["codex", "claude"],
                            "includeShared": False,
                        },
                    ],
                }
            ),
            encoding="utf-8",
        )

        code, result = self.run_cli(repo_root, "--package", PACKAGE_ID)

        self.assertEqual(1, code)
        codes = {item["code"] for item in result["diagnostics"]}
        self.assertTrue(
            {
                "SKILL_ACCESS_INVALID",
                "SKILL_HELP_AGENTS_INCOMPLETE",
                "SKILL_HELP_INVENTORY_REFERENCE_MISSING",
            }.issubset(codes)
        )

    @unittest.skipIf(os.name == "nt", "Windows symlink creation requires developer-mode privileges")
    def test_linked_skill_source_is_rejected(self) -> None:
        temporary, repo_root, package_root = self.make_repo("valid-package")
        self.addCleanup(temporary.cleanup)
        skills_root = package_root / "Skills~"
        source_root = skills_root / "Codex" / "linked-skill"
        source_root.mkdir(parents=True)
        (source_root / "SKILL.md").write_text(
            "---\nname: linked-skill\ndescription: Reject linked sources.\n---\n",
            encoding="utf-8",
        )
        outside = repo_root / "outside.txt"
        outside.write_text("outside", encoding="utf-8")
        (source_root / "linked.txt").symlink_to(outside)
        help_root = skills_root / "Codex" / "linked-help"
        help_root.mkdir(parents=True)
        (help_root / "SKILL.md").write_text(
            "---\nname: linked-help\ndescription: Explain linked skills.\n---\n\nRead PACKAGE_SKILLS.md.\n",
            encoding="utf-8",
        )
        (skills_root / "manifest.json").write_text(
            json.dumps(
                {
                    "schemaVersion": 2,
                    "skillPrefix": "linked",
                    "helpSkill": "linked-help",
                    "skills": [
                        {
                            "name": "linked-help",
                            "agents": ["codex"],
                            "includeShared": False,
                            "access": "read-only",
                        },
                        {
                            "name": "linked-skill",
                            "agents": ["codex"],
                            "includeShared": False,
                            "access": "read-only",
                        },
                    ],
                }
            ),
            encoding="utf-8",
        )

        code, result = self.run_cli(repo_root, "--package", PACKAGE_ID)

        self.assertEqual(1, code)
        self.assertIn("SKILL_SOURCE_LINK_REJECTED", {item["code"] for item in result["diagnostics"]})

    def test_changed_mode_detects_missing_and_valid_version_bumps(self) -> None:
        temporary, repo_root, package_root = self.make_repo("valid-package")
        self.addCleanup(temporary.cleanup)
        self.initialize_git_repo(repo_root)

        readme_path = package_root / "README.md"
        readme_path.write_text(readme_path.read_text(encoding="utf-8") + "\nChanged behavior.\n", encoding="utf-8")
        code, result = self.run_cli(repo_root, "--changed", "--base-ref", "HEAD")
        self.assertEqual(1, code)
        self.assertEqual([PACKAGE_ID], [item["packageId"] for item in result["packages"]])
        self.assertIn("PACKAGE_VERSION_NOT_INCREMENTED", {item["code"] for item in result["diagnostics"]})

        replacements = (
            (package_root / "package.json", '"version": "1.2.3"', '"version": "1.2.4"'),
            (package_root / "README.md", "#1.2.3", "#1.2.4"),
            (package_root / "AI_GUIDE.md", "`1.2.3`", "`1.2.4`"),
        )
        for path, before, after in replacements:
            path.write_text(path.read_text(encoding="utf-8").replace(before, after), encoding="utf-8")

        code, result = self.run_cli(repo_root, "--changed", "--base-ref", "HEAD")
        self.assertEqual(0, code)
        self.assertTrue(result["success"])

    def test_changed_mode_maps_package_folder_meta_to_owning_package(self) -> None:
        temporary, repo_root, package_root = self.make_repo("valid-package")
        self.addCleanup(temporary.cleanup)
        self.initialize_git_repo(repo_root)

        (repo_root / "Packages" / f"{PACKAGE_ID}.meta").write_text(
            "fileFormatVersion: 2\nguid: 1234567890abcdef1234567890abcdef\n",
            encoding="utf-8",
        )
        replacements = (
            (package_root / "package.json", '"version": "1.2.3"', '"version": "1.2.4"'),
            (package_root / "README.md", "#1.2.3", "#1.2.4"),
            (package_root / "AI_GUIDE.md", "`1.2.3`", "`1.2.4`"),
        )
        for path, before, after in replacements:
            path.write_text(path.read_text(encoding="utf-8").replace(before, after), encoding="utf-8")

        code, result = self.run_cli(repo_root, "--changed", "--base-ref", "HEAD")

        self.assertEqual(0, code)
        self.assertTrue(result["success"])
        self.assertEqual([PACKAGE_ID], [item["packageId"] for item in result["packages"]])

    def test_changed_mode_accepts_embedded_package_converted_to_downloaded_git(self) -> None:
        temporary, repo_root, package_root = self.make_repo("valid-package")
        self.addCleanup(temporary.cleanup)
        self.initialize_git_repo(repo_root)
        shutil.rmtree(package_root)
        dependency = self.write_downloaded_dependency(repo_root)

        code, result = self.run_cli(repo_root, "--changed", "--base-ref", "HEAD")

        self.assertEqual(0, code, result["diagnostics"])
        self.assertTrue(result["success"])
        self.assertEqual([PACKAGE_ID], [item["packageId"] for item in result["packages"]])
        self.assertEqual(dependency, result["packages"][0]["version"])

    def test_changed_mode_rejects_deleted_package_without_downloaded_contract(self) -> None:
        temporary, repo_root, package_root = self.make_repo("valid-package")
        self.addCleanup(temporary.cleanup)
        self.initialize_git_repo(repo_root)
        shutil.rmtree(package_root)

        code, result = self.run_cli(repo_root, "--changed", "--base-ref", "HEAD")

        self.assertEqual(1, code)
        self.assertFalse(result["success"])
        self.assertIn(
            "DOWNLOADED_TRANSITION_DEPENDENCY_MISSING",
            {item["code"] for item in result["diagnostics"]},
        )

    def test_changed_mode_rejects_downloaded_transition_with_invalid_lock(self) -> None:
        temporary, repo_root, package_root = self.make_repo("valid-package")
        self.addCleanup(temporary.cleanup)
        self.initialize_git_repo(repo_root)
        shutil.rmtree(package_root)
        self.write_downloaded_dependency(repo_root, source="embedded", commit_hash="short")

        code, result = self.run_cli(repo_root, "--changed", "--base-ref", "HEAD")

        self.assertEqual(1, code)
        self.assertIn(
            "DOWNLOADED_TRANSITION_LOCK_INVALID",
            {item["code"] for item in result["diagnostics"]},
        )

    def test_changed_mode_excludes_generated_router_only_change(self) -> None:
        temporary, repo_root, package_root = self.make_manager_repo()
        self.addCleanup(temporary.cleanup)
        router_path = package_root / "PACKAGE_AI_GUIDE_ROUTER.md"
        router_path.write_text("# Router\n\n## Package Guide Entries\n", encoding="utf-8")
        self.initialize_git_repo(repo_root)
        router_path.write_text(
            router_path.read_text(encoding="utf-8") + "- Downloaded package guide.\n",
            encoding="utf-8",
        )

        code, result = self.run_cli(repo_root, "--changed", "--base-ref", "HEAD")

        self.assertEqual(0, code, result["diagnostics"])
        self.assertEqual([], result["packages"])

    def test_changed_mode_keeps_manager_selected_when_router_and_source_change(self) -> None:
        temporary, repo_root, package_root = self.make_manager_repo()
        self.addCleanup(temporary.cleanup)
        router_path = package_root / "PACKAGE_AI_GUIDE_ROUTER.md"
        router_path.write_text("# Router\n\n## Package Guide Entries\n", encoding="utf-8")
        self.initialize_git_repo(repo_root)
        router_path.write_text(
            router_path.read_text(encoding="utf-8") + "- Downloaded package guide.\n",
            encoding="utf-8",
        )
        readme_path = package_root / "README.md"
        readme_path.write_text(
            readme_path.read_text(encoding="utf-8") + "\nChanged validator behavior.\n",
            encoding="utf-8",
        )

        code, result = self.run_cli(repo_root, "--changed", "--base-ref", "HEAD")

        self.assertEqual(1, code)
        self.assertEqual([MANAGER_ID], [item["packageId"] for item in result["packages"]])
        self.assertIn(
            "PACKAGE_VERSION_NOT_INCREMENTED",
            {item["code"] for item in result["diagnostics"]},
        )

    def test_output_file_matches_stdout_contract(self) -> None:
        temporary, repo_root, _ = self.make_repo("valid-package")
        self.addCleanup(temporary.cleanup)
        output_path = repo_root / "artifacts" / "contract.json"

        code, result = self.run_cli(
            repo_root,
            "--package",
            PACKAGE_ID,
            "--output",
            str(output_path),
        )

        self.assertEqual(0, code)
        self.assertEqual(result, json.loads(output_path.read_text(encoding="utf-8")))

    def test_missing_base_ref_is_infrastructure_error(self) -> None:
        temporary, repo_root, _ = self.make_repo("valid-package")
        self.addCleanup(temporary.cleanup)

        code, result = self.run_cli(repo_root, "--changed")

        self.assertEqual(2, code)
        self.assertEqual("INFRASTRUCTURE_ERROR", result["diagnostics"][0]["code"])

    def test_argument_error_preserves_json_contract(self) -> None:
        temporary, repo_root, _ = self.make_repo("valid-package")
        self.addCleanup(temporary.cleanup)

        code, result = self.run_cli(repo_root)

        self.assertEqual(2, code)
        self.assertEqual("INFRASTRUCTURE_ERROR", result["diagnostics"][0]["code"])
        self.assertIn("Invalid arguments", result["diagnostics"][0]["message"])


if __name__ == "__main__":
    unittest.main(verbosity=2)
