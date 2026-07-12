#!/usr/bin/env python3
from __future__ import annotations

import json
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

    def test_valid_package_and_all_modes_succeed(self) -> None:
        temporary, repo_root, _ = self.make_repo("valid-package")
        self.addCleanup(temporary.cleanup)

        for arguments in (("--package", PACKAGE_ID), ("--all",)):
            code, result = self.run_cli(repo_root, *arguments)
            self.assertEqual(0, code)
            self.assertTrue(result["success"])
            self.assertEqual(1, result["summary"]["packages"])
            self.assertEqual([], result["diagnostics"])

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

    def test_changed_mode_detects_missing_and_valid_version_bumps(self) -> None:
        temporary, repo_root, package_root = self.make_repo("valid-package")
        self.addCleanup(temporary.cleanup)
        self.git(repo_root, "init")
        self.git(repo_root, "config", "user.email", "package-contract-test@example.invalid")
        self.git(repo_root, "config", "user.name", "Package Contract Test")
        self.git(repo_root, "add", "Packages")
        self.git(repo_root, "commit", "-m", "fixture base")

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
