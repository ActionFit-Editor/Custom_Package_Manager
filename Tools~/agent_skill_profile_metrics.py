#!/usr/bin/env python3
"""Measure deterministic managed-skill context projections from package sources."""

from __future__ import annotations

import argparse
import json
from pathlib import Path
from typing import Any


PROFILE_PATH = Path("ProjectSettings/ActionFitAgentSkillProfile.json")


def _frontmatter_bytes(path: Path) -> int:
    data = path.read_bytes()
    if not data.startswith(b"---"):
        raise ValueError(f"skill frontmatter is missing: {path}")
    end = data.find(b"\n---", 3)
    if end < 0:
        raise ValueError(f"skill frontmatter is unterminated: {path}")
    return end + len(b"\n---")


def load_profiles(project_root: Path) -> dict[str, dict[str, Any]]:
    value = json.loads((project_root / PROFILE_PATH).read_text(encoding="utf-8"))
    if value.get("schemaVersion") != 1:
        raise ValueError("agent skill profile schemaVersion must be 1")
    profiles = value.get("profiles")
    if not isinstance(profiles, list):
        raise ValueError("agent skill profiles must be a list")
    result = {}
    for profile in profiles:
        name = profile.get("name") if isinstance(profile, dict) else None
        if not isinstance(name, str) or not name or name in result:
            raise ValueError("agent skill profile names must be non-empty and unique")
        package_ids = profile.get("packageIds")
        if not isinstance(package_ids, list) or any(not isinstance(item, str) for item in package_ids):
            raise ValueError(f"profile {name} has invalid packageIds")
        result[name] = profile
    if value.get("activeProfile") not in result:
        raise ValueError("activeProfile does not name a declared profile")
    return result


def package_skill_metrics(project_root: Path) -> dict[str, dict[str, int]]:
    result: dict[str, dict[str, int]] = {}
    for package_root in sorted((project_root / "Packages").glob("com.actionfit.*")):
        manifest_path = package_root / "Skills~/manifest.json"
        if not manifest_path.is_file():
            continue
        package = json.loads((package_root / "package.json").read_text(encoding="utf-8"))
        manifest = json.loads(manifest_path.read_text(encoding="utf-8"))
        package_id = package.get("name")
        directories = 0
        frontmatter = 0
        for skill in manifest.get("skills") or []:
            name = skill["name"]
            for agent in skill.get("agents") or []:
                source = "Codex" if agent == "codex" else "Claude" if agent == "claude" else None
                if source is None:
                    raise ValueError(f"unsupported agent {agent}: {package_id}/{name}")
                directories += 1
                frontmatter += _frontmatter_bytes(
                    package_root / "Skills~" / source / name / "SKILL.md"
                )
        result[str(package_id)] = {
            "directories": directories,
            "frontmatterBytes": frontmatter,
        }
    return result


def measure(project_root: Path, profile_name: str = "core") -> dict[str, Any]:
    profiles = load_profiles(project_root)
    if profile_name not in profiles:
        raise ValueError(f"unknown profile: {profile_name}")
    packages = package_skill_metrics(project_root)
    profile = profiles[profile_name]
    selected = set(packages) if profile.get("all") else set(profile["packageIds"])
    missing = sorted(selected - set(packages))
    if missing:
        raise ValueError("profile references packages without registered skills: " + ", ".join(missing))

    all_directories = sum(value["directories"] for value in packages.values())
    all_frontmatter = sum(value["frontmatterBytes"] for value in packages.values())
    selected_directories = sum(packages[key]["directories"] for key in selected)
    selected_frontmatter = sum(packages[key]["frontmatterBytes"] for key in selected)

    def reduction(selected_value: int, all_value: int) -> float:
        return round((1.0 - selected_value / all_value) * 100.0, 2) if all_value else 0.0

    return {
        "version": 1,
        "profile": profile_name,
        "packageIds": sorted(selected),
        "all": {
            "directories": all_directories,
            "frontmatterBytes": all_frontmatter,
        },
        "selected": {
            "directories": selected_directories,
            "frontmatterBytes": selected_frontmatter,
        },
        "reductionPercent": {
            "directories": reduction(selected_directories, all_directories),
            "frontmatterBytes": reduction(selected_frontmatter, all_frontmatter),
        },
    }


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--project-root", default=".")
    parser.add_argument("--profile", default="core")
    parser.add_argument("--assert-minimum-percent", type=float)
    args = parser.parse_args()
    result = measure(Path(args.project_root).resolve(), args.profile)
    print(json.dumps(result, ensure_ascii=False, indent=2))
    minimum = args.assert_minimum_percent
    if minimum is not None and any(
        value < minimum for value in result["reductionPercent"].values()
    ):
        return 1
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
