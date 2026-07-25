#!/usr/bin/env python3

from __future__ import annotations

import importlib.util
import json
import tempfile
import unittest
from pathlib import Path


PACKAGE = Path(__file__).resolve().parents[2]
MODULE_PATH = PACKAGE / "Tools~/agent_skill_profile_metrics.py"
SPEC = importlib.util.spec_from_file_location("agent_skill_profile_metrics", MODULE_PATH)
metrics = importlib.util.module_from_spec(SPEC)
assert SPEC and SPEC.loader
SPEC.loader.exec_module(metrics)


class AgentSkillProfileMetricsTests(unittest.TestCase):
    def test_metrics_count_agent_targets_and_frontmatter_without_body_bytes(self) -> None:
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            (root / "ProjectSettings").mkdir()
            (root / "ProjectSettings/ActionFitAgentSkillProfile.json").write_text(
                json.dumps(
                    {
                        "schemaVersion": 1,
                        "activeProfile": "core",
                        "profiles": [
                            {
                                "name": "core",
                                "all": False,
                                "packageIds": ["com.actionfit.core"],
                            },
                            {"name": "all", "all": True, "packageIds": []},
                        ],
                    }
                ),
                encoding="utf-8",
            )
            self._package(root, "com.actionfit.core", ["codex", "claude"], "short")
            self._package(root, "com.actionfit.extra", ["codex", "claude"], "x" * 5000)

            result = metrics.measure(root, "core")

        self.assertEqual(4, result["all"]["directories"])
        self.assertEqual(2, result["selected"]["directories"])
        self.assertEqual(50.0, result["reductionPercent"]["directories"])
        self.assertGreaterEqual(result["reductionPercent"]["frontmatterBytes"], 50.0)

    @staticmethod
    def _package(root: Path, package_id: str, agents: list[str], body: str) -> None:
        package = root / "Packages" / package_id
        package.mkdir(parents=True)
        (package / "package.json").write_text(json.dumps({"name": package_id}), encoding="utf-8")
        (package / "Skills~").mkdir()
        (package / "Skills~/manifest.json").write_text(
            json.dumps({"skills": [{"name": "sample-help", "agents": agents}]}),
            encoding="utf-8",
        )
        for source in ("Codex", "Claude"):
            skill = package / "Skills~" / source / "sample-help"
            skill.mkdir(parents=True)
            skill.joinpath("SKILL.md").write_text(
                "---\nname: sample-help\ndescription: fixed description\n---\n" + body,
                encoding="utf-8",
            )


if __name__ == "__main__":
    unittest.main()
