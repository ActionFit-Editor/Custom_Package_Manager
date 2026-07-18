#!/usr/bin/env python3
from __future__ import annotations

import importlib.util
import json
import subprocess
import sys
import tempfile
import unittest
from pathlib import Path
from typing import Any, Dict, Optional


SCRIPT_DIR = Path(__file__).resolve().parent
PACKAGE_ROOT = SCRIPT_DIR.parents[1]
UPDATER = PACKAGE_ROOT / "Tools~" / "package_dependency_updater.py"
SPEC = importlib.util.spec_from_file_location("package_dependency_updater", UPDATER)
assert SPEC is not None and SPEC.loader is not None
MODULE = importlib.util.module_from_spec(SPEC)
sys.modules[SPEC.name] = MODULE
SPEC.loader.exec_module(MODULE)


class PackageDependencyUpdaterTests(unittest.TestCase):
    maxDiff = None

    def make_repo(self) -> tuple[tempfile.TemporaryDirectory[str], Path]:
        temporary = tempfile.TemporaryDirectory()
        repo_root = Path(temporary.name)
        (repo_root / "Packages").mkdir()
        (repo_root / "Assets/_Data/_CustomPackageManager").mkdir(parents=True)
        self.addCleanup(temporary.cleanup)
        return temporary, repo_root

    def write_catalog(self, repo_root: Path, versions: Dict[str, str]) -> Path:
        path = repo_root / "Assets/_Data/_CustomPackageManager/package_catalog.csv"
        rows = [
            "catalog_id,package_id,display_name,owner,repo_url,version,status,is_latest,unity_min,description,changelog,dependencies",
            "catalogId(string)[key],packageId(string),displayName(string),owner(string),repoUrl(string),version(string),status(string),isLatest(bool)[nondata],unityMin(string),description(string),changelog(string),dependencies(string)",
        ]
        for index, (package_id, version) in enumerate(sorted(versions.items()), start=1):
            rows.append(
                "catalog-{0},{1},{1},ActionFit,https://github.com/ActionFit/{2}.git,{3},verified,true,6000.2,Sample,Sample,".format(
                    index, package_id, package_id.rsplit(".", 1)[-1], version
                )
            )
        path.write_text("\n".join(rows) + "\n", encoding="utf-8")
        return path

    def write_package(
        self,
        repo_root: Path,
        package_id: str,
        version: str,
        dependencies: Optional[Dict[str, str]] = None,
        release_note: str = "- 기존 변경 사항입니다.\n- 기존 검증 사항입니다.\n- 기존 사용자 영향입니다.",
    ) -> Path:
        root = repo_root / "Packages" / package_id
        (root / "Editor/PackageInfo").mkdir(parents=True)
        manifest = {
            "name": package_id,
            "version": version,
            "displayName": package_id,
            "description": "Sample package",
            "unity": "6000.2",
            "author": {"name": "ActionFit"},
            "dependencies": dependencies or {},
        }
        (root / "package.json").write_text(
            json.dumps(manifest, ensure_ascii=False, indent=2) + "\n", encoding="utf-8"
        )
        repo_name = package_id.rsplit(".", 1)[-1]
        (root / "README.md").write_text(
            '# Sample\n\n```json\n{{\n  "{0}": "https://github.com/ActionFit/{1}.git#{2}"\n}}\n```\n'.format(
                package_id, repo_name, version
            ),
            encoding="utf-8",
        )
        (root / "AI_GUIDE.md").write_text(
            "# AI Guide\n\n- Package ID: `{0}`\n- Current package version at generation time: `{1}`\n\n"
            "Requested router entry:\n\n- `Packages/{0}/AI_GUIDE.md` - Sample.\n".format(package_id, version),
            encoding="utf-8",
        )
        rendered_note = json.dumps(release_note, ensure_ascii=True)
        (root / "Editor/PackageInfo/ActionFitPackageInfo_SO.asset").write_text(
            "%YAML 1.1\n--- !u!114 &11400000\nMonoBehaviour:\n"
            "  _packageId: {0}\n"
            "  _displayName: {0}\n"
            "  _repoName: {1}\n"
            "  _owner: ActionFit\n"
            "  _status: verified\n"
            "  _description: Sample package\n"
            "  _releaseNote: {2}\n"
            "  _dependenciesOverride: \n".format(package_id, repo_name, rendered_note),
            encoding="utf-8",
        )
        return root

    def build_plan(
        self,
        repo_root: Path,
        refreshed: bool = True,
        allow_major: bool = False,
    ) -> Any:
        catalog = MODULE.resolve_catalog(repo_root, None)
        return MODULE.build_plan(repo_root, catalog, refreshed, allow_major)

    def snapshot(self, repo_root: Path) -> Dict[str, bytes]:
        return {
            path.relative_to(repo_root).as_posix(): path.read_bytes()
            for path in sorted(repo_root.rglob("*"))
            if path.is_file()
        }

    def test_semver_build_metadata_does_not_change_precedence(self) -> None:
        left = MODULE.SemVer.parse("1.2.3+local")
        right = MODULE.SemVer.parse("1.2.3+catalog")

        self.assertEqual(left, right)
        self.assertFalse(left < right)
        self.assertFalse(left > right)

    def test_plan_computes_fixed_point_transitive_updates_and_layers(self) -> None:
        _, repo_root = self.make_repo()
        core = "com.actionfit.core"
        middle = "com.actionfit.middle"
        app = "com.actionfit.app"
        self.write_catalog(repo_root, {core: "1.1.0", middle: "1.0.1", app: "1.0.1"})
        self.write_package(repo_root, core, "1.1.0")
        self.write_package(repo_root, middle, "1.0.1", {core: "1.0.0"})
        self.write_package(repo_root, app, "1.0.1", {middle: "1.0.1"})

        plan = self.build_plan(repo_root).result

        self.assertTrue(plan["success"])
        self.assertEqual("READY_TO_APPLY", plan["code"])
        by_id = {item["packageId"]: item for item in plan["packages"]}
        self.assertEqual({middle, app}, set(by_id))
        self.assertEqual("1.0.2", by_id[middle]["newVersion"])
        self.assertEqual("1.0.2", by_id[app]["newVersion"])
        self.assertEqual("1.0.2", by_id[app]["dependencyChanges"][0]["toVersion"])
        self.assertEqual([[middle], [app]], plan["publishLayers"])

    def test_plan_includes_unpublished_local_dependency_as_publish_prerequisite(self) -> None:
        _, repo_root = self.make_repo()
        core = "com.actionfit.core"
        app = "com.actionfit.app"
        self.write_catalog(repo_root, {core: "1.1.0", app: "1.0.0"})
        self.write_package(repo_root, core, "1.1.1")
        self.write_package(repo_root, app, "1.0.0", {core: "1.1.0"})

        plan = self.build_plan(repo_root).result

        self.assertTrue(plan["success"])
        self.assertEqual([core], plan["publishPrerequisitePackageIds"])
        self.assertEqual([[core], [app]], plan["publishLayers"])
        self.assertEqual("1.1.1", plan["packages"][0]["dependencyChanges"][0]["toVersion"])

    def test_plan_preserves_newer_dependency_without_downgrade(self) -> None:
        _, repo_root = self.make_repo()
        core = "com.actionfit.core"
        app = "com.actionfit.app"
        self.write_catalog(repo_root, {core: "1.1.0", app: "1.0.0"})
        self.write_package(repo_root, core, "1.1.0")
        self.write_package(repo_root, app, "1.0.0", {core: "1.2.0"})

        plan = self.build_plan(repo_root).result

        self.assertTrue(plan["success"])
        self.assertEqual("NO_CHANGES", plan["code"])
        self.assertEqual([], plan["packages"])
        self.assertIn("DEPENDENCY_NEWER_THAN_TARGET_PRESERVED", {item["code"] for item in plan["warnings"]})

    def test_major_update_requires_explicit_opt_in(self) -> None:
        _, repo_root = self.make_repo()
        core = "com.actionfit.core"
        app = "com.actionfit.app"
        self.write_catalog(repo_root, {core: "2.0.0", app: "1.0.0"})
        self.write_package(repo_root, core, "2.0.0")
        self.write_package(repo_root, app, "1.0.0", {core: "1.5.0"})

        blocked = self.build_plan(repo_root, allow_major=False).result
        approved = self.build_plan(repo_root, allow_major=True).result

        self.assertFalse(blocked["success"])
        self.assertIn("MAJOR_UPDATE_APPROVAL_REQUIRED", {item["code"] for item in blocked["conflicts"]})
        self.assertTrue(approved["success"])
        self.assertEqual("2.0.0", approved["packages"][0]["dependencyChanges"][0]["toVersion"])

    def test_dependency_cycle_blocks_publish_plan(self) -> None:
        _, repo_root = self.make_repo()
        alpha = "com.actionfit.alpha"
        beta = "com.actionfit.beta"
        self.write_catalog(repo_root, {alpha: "1.0.1", beta: "1.0.1"})
        self.write_package(repo_root, alpha, "1.0.1", {beta: "1.0.0"})
        self.write_package(repo_root, beta, "1.0.1", {alpha: "1.0.0"})

        plan = self.build_plan(repo_root).result

        self.assertFalse(plan["success"])
        self.assertIn("DEPENDENCY_CYCLE_BLOCKED", {item["code"] for item in plan["conflicts"]})

    def test_self_dependency_cycle_blocks_publish_plan(self) -> None:
        _, repo_root = self.make_repo()
        package_id = "com.actionfit.self"
        self.write_catalog(repo_root, {package_id: "1.0.1"})
        self.write_package(repo_root, package_id, "1.0.1", {package_id: "1.0.0"})

        plan = self.build_plan(repo_root).result

        self.assertFalse(plan["success"])
        self.assertIn("DEPENDENCY_CYCLE_BLOCKED", {item["code"] for item in plan["conflicts"]})

    def test_invalid_catalog_semver_blocks_plan(self) -> None:
        _, repo_root = self.make_repo()
        core = "com.actionfit.core"
        app = "com.actionfit.app"
        self.write_catalog(repo_root, {core: "latest", app: "1.0.0"})
        self.write_package(repo_root, core, "1.1.0")
        self.write_package(repo_root, app, "1.0.0", {core: "1.0.0"})

        plan = self.build_plan(repo_root).result

        self.assertFalse(plan["success"])
        self.assertIn("CATALOG_LATEST_VERSION_INVALID", {item["code"] for item in plan["conflicts"]})

    def test_non_embedded_actionfit_dependency_blocks_plan(self) -> None:
        _, repo_root = self.make_repo()
        core = "com.actionfit.core"
        app = "com.actionfit.app"
        self.write_catalog(repo_root, {core: "1.1.0", app: "1.0.0"})
        self.write_package(repo_root, app, "1.0.0", {core: "1.0.0"})

        plan = self.build_plan(repo_root).result

        self.assertFalse(plan["success"])
        self.assertIn("ACTIONFIT_DEPENDENCY_NOT_EMBEDDED", {item["code"] for item in plan["conflicts"]})

    def test_affected_embedded_package_behind_catalog_blocks_plan(self) -> None:
        _, repo_root = self.make_repo()
        core = "com.actionfit.core"
        app = "com.actionfit.app"
        self.write_catalog(repo_root, {core: "1.1.0", app: "1.0.2"})
        self.write_package(repo_root, core, "1.1.0")
        self.write_package(repo_root, app, "1.0.0", {core: "1.0.0"})

        plan = self.build_plan(repo_root).result

        self.assertFalse(plan["success"])
        self.assertIn("EMBEDDED_PACKAGE_BEHIND_CATALOG", {item["code"] for item in plan["conflicts"]})

    def test_linked_release_metadata_blocks_plan(self) -> None:
        _, repo_root = self.make_repo()
        core = "com.actionfit.core"
        app = "com.actionfit.app"
        self.write_catalog(repo_root, {core: "1.1.0", app: "1.0.0"})
        self.write_package(repo_root, core, "1.1.0")
        app_root = self.write_package(repo_root, app, "1.0.0", {core: "1.0.0"})
        external = repo_root / "external-readme.md"
        external.write_text("# External\n", encoding="utf-8")
        readme = app_root / "README.md"
        readme.unlink()
        readme.symlink_to(external)

        plan = self.build_plan(repo_root).result

        self.assertFalse(plan["success"])
        self.assertIn("RELEASE_METADATA_CONFLICT", {item["code"] for item in plan["conflicts"]})

    def test_project_override_blocks_plan(self) -> None:
        _, repo_root = self.make_repo()
        core = "com.actionfit.core"
        app = "com.actionfit.app"
        self.write_catalog(repo_root, {core: "1.1.0", app: "1.0.0"})
        self.write_package(repo_root, core, "1.1.0")
        self.write_package(repo_root, app, "1.0.0", {core: "1.0.0"})
        (repo_root / "ProjectSettings").mkdir()
        (repo_root / "ProjectSettings/ActionFitPackageOverrides.json").write_text(
            json.dumps({"overrides": [{"packageId": core}]}),
            encoding="utf-8",
        )

        plan = self.build_plan(repo_root).result

        self.assertFalse(plan["success"])
        self.assertIn("PROJECT_OVERRIDE_BLOCKED", {item["code"] for item in plan["conflicts"]})

    def test_plan_only_is_deterministic_and_does_not_write(self) -> None:
        _, repo_root = self.make_repo()
        core = "com.actionfit.core"
        app = "com.actionfit.app"
        self.write_catalog(repo_root, {core: "1.1.0", app: "1.0.0"})
        self.write_package(repo_root, core, "1.1.0")
        self.write_package(repo_root, app, "1.0.0", {core: "1.0.0"})
        before = self.snapshot(repo_root)

        first = self.build_plan(repo_root, refreshed=False).result
        second = self.build_plan(repo_root, refreshed=False).result

        self.assertEqual(first["planId"], second["planId"])
        self.assertEqual(before, self.snapshot(repo_root))
        self.assertEqual("READY_PLAN_ONLY", first["code"])
        self.assertFalse(first["applyAllowed"])

    def test_apply_requires_exact_plan_and_updates_release_metadata(self) -> None:
        _, repo_root = self.make_repo()
        core = "com.actionfit.core"
        app = "com.actionfit.app"
        self.write_catalog(repo_root, {core: "1.1.0", app: "1.0.0"})
        self.write_package(repo_root, core, "1.1.0")
        app_root = self.write_package(repo_root, app, "1.0.0", {core: "1.0.0"})
        plan = self.build_plan(repo_root)
        before = self.snapshot(repo_root)

        rejected = MODULE.apply_plan(plan, "WRONG", plan.result["requiredApprovalText"], lambda *_: (True, {}))
        self.assertFalse(rejected["success"])
        self.assertEqual("PLAN_ID_MISMATCH", rejected["code"])
        self.assertEqual(before, self.snapshot(repo_root))

        applied = MODULE.apply_plan(
            plan,
            plan.result["planId"],
            plan.result["requiredApprovalText"],
            lambda *_: (True, {"success": True}),
        )

        self.assertTrue(applied["success"])
        manifest = json.loads((app_root / "package.json").read_text(encoding="utf-8"))
        self.assertEqual("1.0.1", manifest["version"])
        self.assertEqual("1.1.0", manifest["dependencies"][core])
        self.assertIn("git#1.0.1", (app_root / "README.md").read_text(encoding="utf-8"))
        self.assertIn("generation time: `1.0.1`", (app_root / "AI_GUIDE.md").read_text(encoding="utf-8"))
        self.assertIn(
            "dependency",
            MODULE.read_release_note(
                (app_root / "Editor/PackageInfo/ActionFitPackageInfo_SO.asset").read_text(encoding="utf-8")
            ),
        )

    def test_apply_rolls_back_every_file_after_validation_failure(self) -> None:
        _, repo_root = self.make_repo()
        core = "com.actionfit.core"
        app = "com.actionfit.app"
        self.write_catalog(repo_root, {core: "1.1.0", app: "1.0.0"})
        self.write_package(repo_root, core, "1.1.0")
        self.write_package(repo_root, app, "1.0.0", {core: "1.0.0"})
        plan = self.build_plan(repo_root)
        before = self.snapshot(repo_root)

        result = MODULE.apply_plan(
            plan,
            plan.result["planId"],
            plan.result["requiredApprovalText"],
            lambda *_: (False, {"success": False, "diagnostics": [{"code": "TEST_FAILURE"}]}),
        )

        self.assertFalse(result["success"])
        self.assertEqual("APPLY_ROLLED_BACK", result["code"])
        self.assertTrue(result["rolledBack"])
        self.assertEqual(before, self.snapshot(repo_root))

    def test_apply_rejects_files_changed_after_planning(self) -> None:
        _, repo_root = self.make_repo()
        core = "com.actionfit.core"
        app = "com.actionfit.app"
        self.write_catalog(repo_root, {core: "1.1.0", app: "1.0.0"})
        self.write_package(repo_root, core, "1.1.0")
        app_root = self.write_package(repo_root, app, "1.0.0", {core: "1.0.0"})
        plan = self.build_plan(repo_root)
        readme = app_root / "README.md"
        readme.write_text(readme.read_text(encoding="utf-8") + "\nConcurrent edit.\n", encoding="utf-8")
        before = self.snapshot(repo_root)

        result = MODULE.apply_plan(
            plan,
            plan.result["planId"],
            plan.result["requiredApprovalText"],
            lambda *_: (True, {"success": True}),
        )

        self.assertFalse(result["success"])
        self.assertEqual("PLAN_CONTENT_CHANGED", result["code"])
        self.assertEqual([], result["changedPaths"])
        self.assertEqual(before, self.snapshot(repo_root))

    def test_apply_rejects_embedded_inventory_changed_after_planning(self) -> None:
        _, repo_root = self.make_repo()
        core = "com.actionfit.core"
        app = "com.actionfit.app"
        self.write_catalog(repo_root, {core: "1.1.0", app: "1.0.0"})
        self.write_package(repo_root, core, "1.1.0")
        self.write_package(repo_root, app, "1.0.0", {core: "1.0.0"})
        plan = self.build_plan(repo_root)
        self.write_package(repo_root, "com.actionfit.new-package", "1.0.0")
        before = self.snapshot(repo_root)

        result = MODULE.apply_plan(
            plan,
            plan.result["planId"],
            plan.result["requiredApprovalText"],
            lambda *_: (True, {"success": True}),
        )

        self.assertFalse(result["success"])
        self.assertEqual("PLAN_CONTENT_CHANGED", result["code"])
        self.assertIn("Packages/com.actionfit.*", result["stalePaths"])
        self.assertEqual(before, self.snapshot(repo_root))

    def test_cli_plan_returns_json_without_writes(self) -> None:
        _, repo_root = self.make_repo()
        core = "com.actionfit.core"
        app = "com.actionfit.app"
        self.write_catalog(repo_root, {core: "1.1.0", app: "1.0.0"})
        self.write_package(repo_root, core, "1.1.0")
        self.write_package(repo_root, app, "1.0.0", {core: "1.0.0"})
        before = self.snapshot(repo_root)

        completed = subprocess.run(
            [sys.executable, str(UPDATER), "plan", "--repo-root", str(repo_root)],
            check=False,
            stdout=subprocess.PIPE,
            stderr=subprocess.PIPE,
            text=True,
            encoding="utf-8",
        )

        self.assertEqual(0, completed.returncode, completed.stderr)
        payload = json.loads(completed.stdout)
        self.assertTrue(payload["success"])
        self.assertEqual("READY_PLAN_ONLY", payload["code"])
        self.assertEqual(before, self.snapshot(repo_root))


if __name__ == "__main__":
    unittest.main()
