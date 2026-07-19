#!/usr/bin/env python3
"""Validate ActionFit UPM package contracts without starting Unity."""

from __future__ import annotations

import argparse
import json
import re
import subprocess
import sys
from dataclasses import asdict, dataclass
from pathlib import Path
from typing import Any, Iterable, Sequence
from urllib.parse import parse_qsl, urlparse


SCHEMA_VERSION = "1.0"
PACKAGE_ID_PATTERN = re.compile(r"^com\.actionfit\.[a-z0-9]+(?:[._-][a-z0-9]+)*$")
SEMVER_PATTERN = re.compile(
    r"^(0|[1-9]\d*)\.(0|[1-9]\d*)\.(0|[1-9]\d*)"
    r"(?:-((?:0|[1-9]\d*|\d*[A-Za-z-][0-9A-Za-z-]*)"
    r"(?:\.(?:0|[1-9]\d*|\d*[A-Za-z-][0-9A-Za-z-]*))*))?"
    r"(?:\+([0-9A-Za-z-]+(?:\.[0-9A-Za-z-]+)*))?$"
)
UNITY_VERSION_PATTERN = re.compile(r"^\d+\.\d+$")
README_INSTALL_PATTERN = re.compile(
    r'"(?P<package_id>com\.actionfit\.[^"]+)"\s*:\s*"[^"]+\.git#(?P<version>[^"\s]+)"'
)
AI_GUIDE_VERSION_PATTERN = re.compile(
    r"Current package version at generation time:\s*`(?P<version>[^`]+)`"
)
SKILL_NAME_PATTERN = re.compile(r"^[a-z0-9]+(?:-[a-z0-9]+)*$")
SKILL_AGENTS = {"codex": "Codex", "claude": "Claude"}
SKILL_ACCESS = {"read-only", "write-capable"}
GENERATED_SKILL_INVENTORY = "PACKAGE_SKILLS.md"
CUSTOM_PACKAGE_MANAGER_ID = "com.actionfit.custompackagemanager"
GENERATED_ROUTER_PATH = (
    "Packages/com.actionfit.custompackagemanager/PACKAGE_AI_GUIDE_ROUTER.md"
)
SDK_PROFILE_RELATIVE_PATH = Path("Editor/SDKInstallProfile.json")
SDK_FORBIDDEN_FILE_NAMES = {"google-services.json", "googleservice-info.plist"}
SDK_FORBIDDEN_SUFFIXES = {
    ".7z",
    ".a",
    ".aar",
    ".bundle",
    ".dll",
    ".dylib",
    ".exe",
    ".framework",
    ".gz",
    ".jar",
    ".lib",
    ".mdb",
    ".pdb",
    ".rar",
    ".so",
    ".tar",
    ".tgz",
    ".unitypackage",
    ".xcframework",
    ".zip",
}
SDK_CREDENTIAL_PATTERN = re.compile(
    r"(?:-----BEGIN (?:RSA |EC |OPENSSH )?PRIVATE KEY-----|"
    r"(?:api[_-]?key|client[_-]?secret|access[_-]?token)\s*[:=]\s*[\"'][^\"']{8,}[\"'])",
    re.IGNORECASE,
)


class InfrastructureError(RuntimeError):
    """Raised when validation cannot run reliably."""


class JsonArgumentParser(argparse.ArgumentParser):
    def error(self, message: str) -> None:
        raise InfrastructureError(f"Invalid arguments: {message}")


@dataclass(frozen=True)
class Diagnostic:
    code: str
    severity: str
    path: str
    line: int
    message: str
    suggestedFix: str


@dataclass(frozen=True)
class SemVer:
    major: int
    minor: int
    patch: int
    prerelease: tuple[str, ...]


def diagnostic(
    code: str,
    path: str,
    message: str,
    suggested_fix: str,
    *,
    line: int = 1,
    severity: str = "error",
) -> Diagnostic:
    return Diagnostic(
        code=code,
        severity=severity,
        path=path.replace("\\", "/"),
        line=max(1, line),
        message=message,
        suggestedFix=suggested_fix,
    )


def parse_semver(value: str) -> SemVer | None:
    match = SEMVER_PATTERN.fullmatch(value)
    if match is None:
        return None
    prerelease = tuple(match.group(4).split(".")) if match.group(4) else ()
    return SemVer(int(match.group(1)), int(match.group(2)), int(match.group(3)), prerelease)


def compare_semver(left: SemVer, right: SemVer) -> int:
    left_core = (left.major, left.minor, left.patch)
    right_core = (right.major, right.minor, right.patch)
    if left_core != right_core:
        return 1 if left_core > right_core else -1
    if not left.prerelease and not right.prerelease:
        return 0
    if not left.prerelease:
        return 1
    if not right.prerelease:
        return -1
    for left_part, right_part in zip(left.prerelease, right.prerelease):
        if left_part == right_part:
            continue
        left_numeric = left_part.isdigit()
        right_numeric = right_part.isdigit()
        if left_numeric and right_numeric:
            return 1 if int(left_part) > int(right_part) else -1
        if left_numeric != right_numeric:
            return -1 if left_numeric else 1
        return 1 if left_part > right_part else -1
    if len(left.prerelease) == len(right.prerelease):
        return 0
    return 1 if len(left.prerelease) > len(right.prerelease) else -1


def relative_path(repo_root: Path, path: Path) -> str:
    try:
        return path.resolve().relative_to(repo_root.resolve()).as_posix()
    except ValueError:
        return path.as_posix()


def line_for(text: str, needle: str) -> int:
    for index, line in enumerate(text.splitlines(), start=1):
        if needle in line:
            return index
    return 1


def read_text(repo_root: Path, path: Path, diagnostics: list[Diagnostic]) -> str | None:
    rel_path = relative_path(repo_root, path)
    try:
        return path.read_text(encoding="utf-8-sig")
    except FileNotFoundError:
        diagnostics.append(
            diagnostic(
                "REQUIRED_FILE_MISSING",
                rel_path,
                f"Required file is missing: {rel_path}",
                "Create the required file using the Custom Package Manager package template.",
            )
        )
    except (OSError, UnicodeError) as exc:
        diagnostics.append(
            diagnostic(
                "FILE_READ_FAILED",
                rel_path,
                f"Could not read {rel_path}: {exc}",
                "Make the file readable UTF-8 text and run validation again.",
            )
        )
    return None


def load_json(
    repo_root: Path,
    path: Path,
    diagnostics: list[Diagnostic],
    invalid_code: str,
) -> tuple[dict[str, Any] | None, str | None]:
    text = read_text(repo_root, path, diagnostics)
    if text is None:
        return None, None
    try:
        value = json.loads(text)
    except json.JSONDecodeError as exc:
        diagnostics.append(
            diagnostic(
                invalid_code,
                relative_path(repo_root, path),
                f"Invalid JSON: {exc.msg}",
                "Fix the JSON syntax at the reported line and rerun validation.",
                line=exc.lineno,
            )
        )
        return None, text
    if not isinstance(value, dict):
        diagnostics.append(
            diagnostic(
                invalid_code,
                relative_path(repo_root, path),
                "The JSON root must be an object.",
                "Replace the JSON root with an object containing the required fields.",
            )
        )
        return None, text
    return value, text


def require_non_empty_string(
    manifest: dict[str, Any],
    field: str,
    manifest_text: str,
    manifest_path: str,
    diagnostics: list[Diagnostic],
) -> str | None:
    value = manifest.get(field)
    if isinstance(value, str) and value.strip():
        return value.strip()
    diagnostics.append(
        diagnostic(
            "PACKAGE_JSON_REQUIRED_FIELD",
            manifest_path,
            f'package.json field "{field}" must be a non-empty string.',
            f'Add a non-empty "{field}" value to package.json.',
            line=line_for(manifest_text, f'"{field}"'),
        )
    )
    return None


def validate_manifest(
    repo_root: Path,
    package_id: str,
    package_root: Path,
    diagnostics: list[Diagnostic],
) -> tuple[dict[str, Any] | None, str | None, SemVer | None]:
    manifest_path = package_root / "package.json"
    manifest, text = load_json(repo_root, manifest_path, diagnostics, "PACKAGE_JSON_INVALID")
    if manifest is None or text is None:
        return None, None, None
    rel_path = relative_path(repo_root, manifest_path)
    name = require_non_empty_string(manifest, "name", text, rel_path, diagnostics)
    version = require_non_empty_string(manifest, "version", text, rel_path, diagnostics)
    require_non_empty_string(manifest, "displayName", text, rel_path, diagnostics)
    require_non_empty_string(manifest, "description", text, rel_path, diagnostics)
    unity = require_non_empty_string(manifest, "unity", text, rel_path, diagnostics)

    if name is not None:
        if not PACKAGE_ID_PATTERN.fullmatch(name):
            diagnostics.append(
                diagnostic(
                    "PACKAGE_ID_INVALID",
                    rel_path,
                    f'Package name "{name}" is not a valid ActionFit package ID.',
                    "Use a lowercase com.actionfit.* package ID with no path separators.",
                    line=line_for(text, '"name"'),
                )
            )
        if name != package_id:
            diagnostics.append(
                diagnostic(
                    "PACKAGE_ID_DIRECTORY_MISMATCH",
                    rel_path,
                    f'package.json name "{name}" does not match directory "{package_id}".',
                    f'Set package.json name to "{package_id}" or rename the package directory safely.',
                    line=line_for(text, '"name"'),
                )
            )

    parsed_version = parse_semver(version) if version is not None else None
    if version is not None and parsed_version is None:
        diagnostics.append(
            diagnostic(
                "PACKAGE_VERSION_INVALID_SEMVER",
                rel_path,
                f'Package version "{version}" is not valid SemVer 2.0.0.',
                "Use MAJOR.MINOR.PATCH with an optional valid prerelease/build suffix.",
                line=line_for(text, '"version"'),
            )
        )

    if unity is not None and not UNITY_VERSION_PATTERN.fullmatch(unity):
        diagnostics.append(
            diagnostic(
                "PACKAGE_UNITY_VERSION_INVALID",
                rel_path,
                f'Unity version "{unity}" must use MAJOR.MINOR format.',
                'Set the "unity" field to a value such as "6000.2".',
                line=line_for(text, '"unity"'),
            )
        )

    author = manifest.get("author")
    author_name = author.get("name") if isinstance(author, dict) else None
    if not isinstance(author_name, str) or not author_name.strip():
        diagnostics.append(
            diagnostic(
                "PACKAGE_AUTHOR_INVALID",
                rel_path,
                'package.json author must be an object with a non-empty "name".',
                'Add an author object such as {"name": "ActionFit"}.',
                line=line_for(text, '"author"'),
            )
        )

    dependencies = manifest.get("dependencies")
    if not isinstance(dependencies, dict):
        diagnostics.append(
            diagnostic(
                "PACKAGE_DEPENDENCIES_INVALID",
                rel_path,
                'package.json "dependencies" must be an object.',
                'Add "dependencies": {} when the package has no dependencies.',
                line=line_for(text, '"dependencies"'),
            )
        )
    elif any(not isinstance(key, str) or not isinstance(value, str) for key, value in dependencies.items()):
        diagnostics.append(
            diagnostic(
                "PACKAGE_DEPENDENCY_ENTRY_INVALID",
                rel_path,
                "Every package dependency key and value must be a string.",
                "Use package IDs as keys and version or Git references as string values.",
                line=line_for(text, '"dependencies"'),
            )
        )
    return manifest, text, parsed_version


def validate_readme(
    repo_root: Path,
    package_id: str,
    package_root: Path,
    version: str | None,
    diagnostics: list[Diagnostic],
) -> None:
    path = package_root / "README.md"
    text = read_text(repo_root, path, diagnostics)
    if text is None:
        return
    rel_path = relative_path(repo_root, path)
    if package_id not in text:
        diagnostics.append(
            diagnostic(
                "README_PACKAGE_ID_MISSING",
                rel_path,
                f"README does not identify {package_id}.",
                "Add the exact package ID to the README title or install section.",
            )
        )
    matches = [match for match in README_INSTALL_PATTERN.finditer(text) if match.group("package_id") == package_id]
    if not matches:
        diagnostics.append(
            diagnostic(
                "README_INSTALL_REFERENCE_MISSING",
                rel_path,
                f"README has no Git UPM install entry for {package_id}.",
                "Add a Packages/manifest.json example using the package Git URL and current tag.",
            )
        )
    elif version is not None:
        install_version = matches[0].group("version")
        if install_version != version:
            diagnostics.append(
                diagnostic(
                    "README_INSTALL_VERSION_MISMATCH",
                    rel_path,
                    f"README install tag {install_version} does not match package.json version {version}.",
                    f"Update the README install URL tag to #{version}.",
                    line=text[: matches[0].start()].count("\n") + 1,
                )
            )


def validate_ai_guide(
    repo_root: Path,
    package_id: str,
    package_root: Path,
    version: str | None,
    diagnostics: list[Diagnostic],
) -> None:
    path = package_root / "AI_GUIDE.md"
    text = read_text(repo_root, path, diagnostics)
    if text is None:
        return
    rel_path = relative_path(repo_root, path)
    if f"Package ID: `{package_id}`" not in text:
        diagnostics.append(
            diagnostic(
                "AI_GUIDE_PACKAGE_ID_MISMATCH",
                rel_path,
                f"AI_GUIDE.md does not declare Package ID `{package_id}`.",
                "Update the Package Identity section to the exact package ID.",
                line=line_for(text, "Package ID:"),
            )
        )
    version_match = AI_GUIDE_VERSION_PATTERN.search(text)
    if version_match is None:
        diagnostics.append(
            diagnostic(
                "AI_GUIDE_VERSION_MISSING",
                rel_path,
                "AI_GUIDE.md does not declare its generated package version.",
                "Add `Current package version at generation time: `<version>` to Package Identity.",
                line=line_for(text, "Package Identity"),
            )
        )
    elif version is not None and version_match.group("version") != version:
        diagnostics.append(
            diagnostic(
                "AI_GUIDE_VERSION_MISMATCH",
                rel_path,
                f"AI_GUIDE version {version_match.group('version')} does not match package.json version {version}.",
                f"Update the AI guide generated version to `{version}`.",
                line=text[: version_match.start()].count("\n") + 1,
            )
        )
    requested_path = f"Packages/{package_id}/AI_GUIDE.md"
    if requested_path not in text:
        diagnostics.append(
            diagnostic(
                "AI_GUIDE_ROUTER_ENTRY_MISSING",
                rel_path,
                f"AI_GUIDE.md does not request a router entry for {requested_path}.",
                "Add the package's exact AI_GUIDE.md path to Requested router entry.",
                line=line_for(text, "Requested router entry"),
            )
        )


def skill_frontmatter(text: str) -> tuple[dict[str, str] | None, int]:
    lines = text.replace("\r\n", "\n").split("\n")
    if not lines or lines[0].strip() != "---":
        return None, 1
    fields: dict[str, str] = {}
    for index, line in enumerate(lines[1:], start=2):
        if line.strip() == "---":
            return fields, index
        if ":" not in line:
            continue
        key, value = line.split(":", 1)
        value = value.strip()
        if len(value) >= 2 and value[0] == value[-1] and value[0] in {'"', "'"}:
            value = value[1:-1]
        fields[key.strip()] = value
    return None, 1


def first_linked_path(root: Path) -> Path | None:
    if root.is_symlink():
        return root
    try:
        for path in root.rglob("*"):
            if path.is_symlink():
                return path
    except OSError:
        return root
    return None


def validate_skill_source_tree(
    repo_root: Path,
    source_root: Path,
    diagnostics: list[Diagnostic],
) -> bool:
    linked_path = first_linked_path(source_root)
    if linked_path is None:
        return True
    diagnostics.append(
        diagnostic(
            "SKILL_SOURCE_LINK_REJECTED",
            relative_path(repo_root, linked_path),
            "Registered skill sources must not contain symbolic links or reparse points.",
            "Replace the linked entry with package-owned files under Skills~.",
        )
    )
    return False


def validate_skills(
    repo_root: Path,
    package_id: str,
    package_root: Path,
    diagnostics: list[Diagnostic],
) -> None:
    skills_root = package_root / "Skills~"
    if not skills_root.exists():
        return

    manifest_path = skills_root / "manifest.json"
    registered_source_exists = any((skills_root / name).is_dir() for name in (*SKILL_AGENTS.values(), "Shared"))
    if not manifest_path.is_file():
        if registered_source_exists:
            diagnostics.append(
                diagnostic(
                    "SKILL_MANIFEST_MISSING",
                    relative_path(repo_root, manifest_path),
                    f"{package_id} ships skill sources without Skills~/manifest.json.",
                    "Register package skills in a schemaVersion 2 Skills~/manifest.json file with a mandatory help skill.",
                )
            )
        return

    if manifest_path.is_symlink():
        diagnostics.append(
            diagnostic(
                "SKILL_MANIFEST_LINK_REJECTED",
                relative_path(repo_root, manifest_path),
                "Skills~/manifest.json must be a package-owned regular file.",
                "Replace the linked manifest with a regular JSON file.",
            )
        )
        return

    linked_path = first_linked_path(skills_root)
    if linked_path is not None:
        diagnostics.append(
            diagnostic(
                "SKILL_SOURCE_LINK_REJECTED",
                relative_path(repo_root, linked_path),
                "Skills~ must not contain symbolic links or reparse points.",
                "Replace the linked entry with package-owned files under Skills~.",
            )
        )
        return

    manifest, text = load_json(repo_root, manifest_path, diagnostics, "SKILL_MANIFEST_INVALID")
    if manifest is None or text is None:
        return
    rel_manifest = relative_path(repo_root, manifest_path)
    if manifest.get("schemaVersion") != 2:
        diagnostics.append(
            diagnostic(
                "SKILL_MANIFEST_SCHEMA_UNSUPPORTED",
                rel_manifest,
                "Published or changed package skills must declare schemaVersion 2.",
                'Set "schemaVersion" to 2 and add skillPrefix, helpSkill, and access fields.',
                line=line_for(text, '"schemaVersion"'),
            )
        )

    skill_prefix = manifest.get("skillPrefix")
    if not isinstance(skill_prefix, str) or not SKILL_NAME_PATTERN.fullmatch(skill_prefix):
        diagnostics.append(
            diagnostic(
                "SKILL_PREFIX_INVALID",
                rel_manifest,
                f"Skills~/manifest.json has invalid skillPrefix {skill_prefix!r}.",
                "Use an explicit lowercase letters/digits/hyphens prefix; do not infer it from the package ID.",
                line=line_for(text, '"skillPrefix"'),
            )
        )
        skill_prefix = None

    help_skill = manifest.get("helpSkill")
    expected_help = f"{skill_prefix}-help" if skill_prefix else None
    if not isinstance(help_skill, str) or help_skill != expected_help:
        diagnostics.append(
            diagnostic(
                "SKILL_HELP_NAME_INVALID",
                rel_manifest,
                f"helpSkill must be exactly {expected_help or '<skillPrefix>-help'!r}.",
                "Set helpSkill to <skillPrefix>-help and register that skill in the skills array.",
                line=line_for(text, '"helpSkill"'),
            )
        )

    skills = manifest.get("skills")
    if not isinstance(skills, list) or not skills:
        diagnostics.append(
            diagnostic(
                "SKILL_MANIFEST_SKILLS_INVALID",
                rel_manifest,
                'Skills~/manifest.json field "skills" must be a non-empty array.',
                "Add at least one skill registration object.",
                line=line_for(text, '"skills"'),
            )
        )
        return

    targets: set[tuple[str, str]] = set()
    registered_names: set[str] = set()
    agents_by_skill: dict[str, set[str]] = {}
    shared_valid: bool | None = None
    for index, skill in enumerate(skills):
        if not isinstance(skill, dict):
            diagnostics.append(
                diagnostic(
                    "SKILL_MANIFEST_ENTRY_INVALID",
                    rel_manifest,
                    f"Skill registration at index {index} must be an object.",
                    "Replace the entry with name, agents, and optional includeShared fields.",
                )
            )
            continue

        name = skill.get("name")
        if not isinstance(name, str) or not SKILL_NAME_PATTERN.fullmatch(name):
            diagnostics.append(
                diagnostic(
                    "SKILL_NAME_INVALID",
                    rel_manifest,
                    f"Skill registration at index {index} has invalid name {name!r}.",
                    "Use lowercase letters, digits, and single hyphens only.",
                    line=line_for(text, f'"name": "{name}"') if isinstance(name, str) else 1,
                )
            )
            continue

        if name in registered_names:
            diagnostics.append(
                diagnostic(
                    "SKILL_NAME_DUPLICATE",
                    rel_manifest,
                    f"Skill {name} is registered more than once.",
                    "Keep one registration per skill name and list its agents in that registration.",
                    line=line_for(text, f'"name": "{name}"'),
                )
            )
        registered_names.add(name)

        if skill_prefix and not name.startswith(f"{skill_prefix}-"):
            diagnostics.append(
                diagnostic(
                    "SKILL_PREFIX_MISMATCH",
                    rel_manifest,
                    f"Skill {name} does not start with the declared {skill_prefix}- prefix.",
                    f"Rename the skill to start with {skill_prefix}- or choose the correct explicit skillPrefix.",
                    line=line_for(text, f'"name": "{name}"'),
                )
            )

        agents = skill.get("agents")
        if not isinstance(agents, list) or not agents:
            diagnostics.append(
                diagnostic(
                    "SKILL_AGENTS_INVALID",
                    rel_manifest,
                    f"Skill {name} must register at least one agent.",
                    'Use an "agents" array containing "codex" and/or "claude".',
                )
            )
            continue

        include_shared = skill.get("includeShared", False)
        if not isinstance(include_shared, bool):
            diagnostics.append(
                diagnostic(
                    "SKILL_SHARED_FLAG_INVALID",
                    rel_manifest,
                    f"Skill {name} includeShared must be true or false.",
                    "Replace includeShared with a JSON boolean.",
                )
            )
            include_shared = False

        access = skill.get("access")
        if access not in SKILL_ACCESS:
            diagnostics.append(
                diagnostic(
                    "SKILL_ACCESS_INVALID",
                    rel_manifest,
                    f"Skill {name} access must be one of {sorted(SKILL_ACCESS)}, not {access!r}.",
                    'Set access to "read-only" or "write-capable" so package help can state its safety boundary.',
                    line=line_for(text, '"access"'),
                )
            )
        if name == help_skill and access != "read-only":
            diagnostics.append(
                diagnostic(
                    "SKILL_HELP_ACCESS_INVALID",
                    rel_manifest,
                    f"Help skill {name} must be read-only.",
                    'Set the help skill access to "read-only".',
                    line=line_for(text, f'"name": "{name}"'),
                )
            )

        shared_root = skills_root / "Shared"
        if include_shared and shared_valid is None:
            if not shared_root.is_dir():
                diagnostics.append(
                    diagnostic(
                        "SKILL_SHARED_SOURCE_MISSING",
                        relative_path(repo_root, shared_root),
                        "A registered skill requests Shared resources, but Skills~/Shared is missing.",
                        "Create Skills~/Shared or set includeShared to false.",
                    )
                )
                shared_valid = False
            else:
                shared_valid = validate_skill_source_tree(repo_root, shared_root, diagnostics)

        for agent in agents:
            if not isinstance(agent, str) or agent not in SKILL_AGENTS:
                diagnostics.append(
                    diagnostic(
                        "SKILL_AGENT_UNSUPPORTED",
                        rel_manifest,
                        f"Skill {name} registers unsupported agent {agent!r}.",
                        'Use only "codex" or "claude".',
                    )
                )
                continue

            agents_by_skill.setdefault(name, set()).add(agent)

            target = (agent, name)
            if target in targets:
                diagnostics.append(
                    diagnostic(
                        "SKILL_TARGET_DUPLICATE",
                        rel_manifest,
                        f"Skill target {agent}/{name} is registered more than once.",
                        "Keep one registration for each agent and skill name.",
                    )
                )
                continue
            targets.add(target)

            source_root = skills_root / SKILL_AGENTS[agent] / name
            if not source_root.is_dir():
                diagnostics.append(
                    diagnostic(
                        "SKILL_SOURCE_MISSING",
                        relative_path(repo_root, source_root),
                        f"Registered skill source is missing for {agent}/{name}.",
                        f"Create Skills~/{SKILL_AGENTS[agent]}/{name}/SKILL.md.",
                    )
                )
                continue
            if not validate_skill_source_tree(repo_root, source_root, diagnostics):
                continue

            skill_path = source_root / "SKILL.md"
            skill_text = read_text(repo_root, skill_path, diagnostics)
            if skill_text is None:
                continue
            fields, _ = skill_frontmatter(skill_text)
            if fields is None:
                diagnostics.append(
                    diagnostic(
                        "SKILL_FRONTMATTER_INVALID",
                        relative_path(repo_root, skill_path),
                        "SKILL.md must start with closed YAML frontmatter.",
                        "Add --- frontmatter containing name and description before the instructions.",
                    )
                )
                continue
            declared_name = fields.get("name")
            description = fields.get("description")
            if declared_name != name:
                diagnostics.append(
                    diagnostic(
                        "SKILL_FRONTMATTER_NAME_MISMATCH",
                        relative_path(repo_root, skill_path),
                        f"SKILL.md name {declared_name!r} does not match registered name {name!r}.",
                        f"Set the frontmatter name to {name}.",
                        line=line_for(skill_text, "name:"),
                    )
                )
            if not isinstance(description, str) or not description.strip():
                diagnostics.append(
                    diagnostic(
                        "SKILL_FRONTMATTER_DESCRIPTION_MISSING",
                        relative_path(repo_root, skill_path),
                        "SKILL.md frontmatter must contain a non-empty description.",
                        "Describe what the skill does and when it should trigger.",
                        line=line_for(skill_text, "description:"),
                    )
                )

            if name == help_skill:
                inventory_path = source_root / GENERATED_SKILL_INVENTORY
                if inventory_path.exists():
                    diagnostics.append(
                        diagnostic(
                            "SKILL_GENERATED_INVENTORY_RESERVED",
                            relative_path(repo_root, inventory_path),
                            f"{GENERATED_SKILL_INVENTORY} is generated during installation and must not be package-authored.",
                            f"Delete the source file and make {name}/SKILL.md read the installed generated inventory.",
                        )
                    )
                if GENERATED_SKILL_INVENTORY not in skill_text:
                    diagnostics.append(
                        diagnostic(
                            "SKILL_HELP_INVENTORY_REFERENCE_MISSING",
                            relative_path(repo_root, skill_path),
                            f"Help skill {name} does not read {GENERATED_SKILL_INVENTORY}.",
                            f"Require the help workflow to read {GENERATED_SKILL_INVENTORY} before describing package skills.",
                        )
                    )

            if include_shared and shared_valid:
                source_files = {
                    path.relative_to(source_root).as_posix()
                    for path in source_root.rglob("*")
                    if path.is_file()
                }
                shared_files = {
                    path.relative_to(shared_root).as_posix()
                    for path in shared_root.rglob("*")
                    if path.is_file()
                }
                collisions = sorted(source_files & shared_files)
                if collisions:
                    diagnostics.append(
                        diagnostic(
                            "SKILL_SHARED_SOURCE_COLLISION",
                            relative_path(repo_root, source_root / collisions[0]),
                            f"Shared resources collide with agent skill files: {', '.join(collisions)}",
                            "Keep each installed relative file in either the agent source or Shared, not both.",
                        )
                    )

    if isinstance(help_skill, str) and help_skill not in registered_names:
        diagnostics.append(
            diagnostic(
                "SKILL_HELP_REGISTRATION_MISSING",
                rel_manifest,
                f"Mandatory help skill {help_skill} is not registered.",
                "Add the help skill to skills and provide Codex/Claude sources for every agent used by the package.",
                line=line_for(text, '"helpSkill"'),
            )
        )
    elif isinstance(help_skill, str):
        help_agents = agents_by_skill.get(help_skill, set())
        related_agents = {
            agent
            for name, skill_agents in agents_by_skill.items()
            if name != help_skill
            for agent in skill_agents
        }
        missing_help_agents = sorted(related_agents - help_agents)
        if missing_help_agents:
            diagnostics.append(
                diagnostic(
                    "SKILL_HELP_AGENTS_INCOMPLETE",
                    rel_manifest,
                    f"Help skill {help_skill} does not cover agents: {', '.join(missing_help_agents)}.",
                    "Register the help skill for the union of agents used by all related package skills.",
                    line=line_for(text, f'"name": "{help_skill}"'),
                )
            )

    shared_inventory = skills_root / "Shared" / GENERATED_SKILL_INVENTORY
    if shared_inventory.exists():
        diagnostics.append(
            diagnostic(
                "SKILL_GENERATED_INVENTORY_RESERVED",
                relative_path(repo_root, shared_inventory),
                f"Shared/{GENERATED_SKILL_INVENTORY} would collide with the generated help inventory.",
                "Delete the shared source file; Custom Package Manager generates it only in each installed help skill.",
            )
        )


def yaml_scalar(text: str, field: str) -> tuple[str | None, int]:
    pattern = re.compile(rf"^\s*{re.escape(field)}:\s*(.*?)\s*$", re.MULTILINE)
    match = pattern.search(text)
    if match is None:
        return None, 1
    value = match.group(1).strip()
    if len(value) >= 2 and value.startswith('"') and value.endswith('"'):
        try:
            value = json.loads(value)
        except json.JSONDecodeError:
            pass
    return value, text[: match.start()].count("\n") + 1


def validate_package_info(
    repo_root: Path,
    package_id: str,
    package_root: Path,
    manifest: dict[str, Any] | None,
    diagnostics: list[Diagnostic],
) -> None:
    path = package_root / "Editor" / "PackageInfo" / "ActionFitPackageInfo_SO.asset"
    text = read_text(repo_root, path, diagnostics)
    if text is None:
        return
    rel_path = relative_path(repo_root, path)
    expected = {
        "_packageId": package_id,
        "_displayName": manifest.get("displayName") if manifest else None,
    }
    for field, expected_value in expected.items():
        value, line = yaml_scalar(text, field)
        if value is None:
            diagnostics.append(
                diagnostic(
                    "PACKAGE_INFO_FIELD_MISSING",
                    rel_path,
                    f"PackageInfo is missing {field}.",
                    f"Set {field} in ActionFitPackageInfo_SO.asset.",
                    line=line,
                )
            )
        elif expected_value is not None and value != expected_value:
            diagnostics.append(
                diagnostic(
                    "PACKAGE_INFO_FIELD_MISMATCH",
                    rel_path,
                    f"PackageInfo {field} value {value!r} does not match {expected_value!r}.",
                    f"Set {field} to {expected_value!r}.",
                    line=line,
                )
            )
    for field in ("_repoName", "_owner", "_status", "_description", "_releaseNote"):
        value, line = yaml_scalar(text, field)
        if value is None or not value.strip():
            diagnostics.append(
                diagnostic(
                    "PACKAGE_INFO_FIELD_EMPTY",
                    rel_path,
                    f"PackageInfo {field} must not be empty.",
                    f"Set a package-specific {field} value in ActionFitPackageInfo_SO.asset.",
                    line=line,
                )
            )
    meta_path = Path(f"{path}.meta")
    if not meta_path.is_file():
        diagnostics.append(
            diagnostic(
                "PACKAGE_INFO_META_MISSING",
                relative_path(repo_root, meta_path),
                "PackageInfo asset is missing its Unity .meta file.",
                "Restore or create the matching .meta file without changing an existing GUID.",
            )
        )


def validate_asmdefs(
    repo_root: Path,
    package_id: str,
    package_root: Path,
    diagnostics: list[Diagnostic],
) -> None:
    asmdef_paths = sorted(
        path
        for path in package_root.rglob("*.asmdef")
        if not any(part.endswith("~") for part in path.relative_to(package_root).parts)
    )
    if not asmdef_paths:
        diagnostics.append(
            diagnostic(
                "ASMDEF_MISSING",
                relative_path(repo_root, package_root),
                f"{package_id} contains no assembly definition.",
                "Add at least one package-owned .asmdef to keep package compilation isolated.",
            )
        )
        return
    names: dict[str, str] = {}
    for path in asmdef_paths:
        asmdef, text = load_json(repo_root, path, diagnostics, "ASMDEF_JSON_INVALID")
        if asmdef is None or text is None:
            continue
        rel_path = relative_path(repo_root, path)
        name = asmdef.get("name")
        if not isinstance(name, str) or not name.strip():
            diagnostics.append(
                diagnostic(
                    "ASMDEF_NAME_MISSING",
                    rel_path,
                    "Assembly definition name must be a non-empty string.",
                    f"Set the assembly name to {package_id} or a {package_id}.* child name.",
                    line=line_for(text, '"name"'),
                )
            )
        else:
            if name != package_id and not name.startswith(f"{package_id}."):
                diagnostics.append(
                    diagnostic(
                        "ASMDEF_NAME_PACKAGE_MISMATCH",
                        rel_path,
                        f'Assembly name "{name}" is outside the {package_id} namespace.',
                        f"Rename the assembly to {package_id} or a {package_id}.* child name.",
                        line=line_for(text, '"name"'),
                    )
                )
            if name in names:
                diagnostics.append(
                    diagnostic(
                        "ASMDEF_NAME_DUPLICATE",
                        rel_path,
                        f'Assembly name "{name}" is already declared by {names[name]}.',
                        "Give every assembly definition a unique package-owned name.",
                        line=line_for(text, '"name"'),
                    )
                )
            else:
                names[name] = rel_path
        normalized_parts = {part.lower() for part in path.relative_to(package_root).parts}
        include_platforms = asmdef.get("includePlatforms")
        if "editor" in normalized_parts and (
            not isinstance(include_platforms, list) or "Editor" not in include_platforms
        ):
            diagnostics.append(
                diagnostic(
                    "ASMDEF_EDITOR_PLATFORM_MISSING",
                    rel_path,
                    "An assembly under an Editor folder must include only the Editor platform.",
                    'Set "includePlatforms" to ["Editor"].',
                    line=line_for(text, '"includePlatforms"'),
                )
            )
        if "tests" in normalized_parts:
            optional_references = asmdef.get("optionalUnityReferences")
            if asmdef.get("autoReferenced") is not False:
                diagnostics.append(
                    diagnostic(
                        "ASMDEF_TEST_AUTO_REFERENCED",
                        rel_path,
                        "Test assemblies must set autoReferenced to false.",
                        'Set "autoReferenced": false.',
                        line=line_for(text, '"autoReferenced"'),
                    )
                )
            if not isinstance(optional_references, list) or "TestAssemblies" not in optional_references:
                diagnostics.append(
                    diagnostic(
                        "ASMDEF_TEST_REFERENCE_MISSING",
                        rel_path,
                        "Test assemblies must opt into Unity Test Framework references.",
                        'Add "optionalUnityReferences": ["TestAssemblies"].',
                        line=line_for(text, '"optionalUnityReferences"'),
                    )
                )
        meta_path = Path(f"{path}.meta")
        if not meta_path.is_file():
            diagnostics.append(
                diagnostic(
                    "ASMDEF_META_MISSING",
                    relative_path(repo_root, meta_path),
                    "Assembly definition is missing its Unity .meta file.",
                    "Restore or create the matching .meta file without changing an existing GUID.",
                )
            )


def validate_sdk_bridge(
    repo_root: Path,
    package_id: str,
    package_root: Path,
    manifest: dict[str, Any] | None,
    diagnostics: list[Diagnostic],
) -> None:
    profile_path = package_root / SDK_PROFILE_RELATIVE_PATH
    if not profile_path.is_file():
        return

    dependencies = manifest.get("dependencies") if isinstance(manifest, dict) else None
    manager_version = dependencies.get("com.actionfit.custompackagemanager") if isinstance(dependencies, dict) else None
    if not isinstance(manager_version, str) or parse_semver(manager_version) is None:
        diagnostics.append(
            diagnostic(
                "SDK_BRIDGE_MANAGER_DEPENDENCY_MISSING",
                relative_path(repo_root, package_root / "package.json"),
                "SDK bridge package must declare an exact com.actionfit.custompackagemanager dependency.",
                "Regenerate the bridge package or add the installed manager version to package.json dependencies.",
            )
        )

    profile, text = load_json(repo_root, profile_path, diagnostics, "SDK_PROFILE_JSON_INVALID")
    rel_profile = relative_path(repo_root, profile_path)
    if profile is not None and text is not None:
        schema_version = profile.get("SchemaVersion")
        if schema_version not in {1, 2}:
            diagnostics.append(
                diagnostic(
                    "SDK_PROFILE_SCHEMA_UNSUPPORTED",
                    rel_profile,
                    "SDK install profile must declare SchemaVersion 1 or 2.",
                    "Regenerate the profile with the current SDK bridge package template.",
                    line=line_for(text, '"SchemaVersion"'),
                )
            )
        if profile.get("BridgePackageId") != package_id:
            diagnostics.append(
                diagnostic(
                    "SDK_PROFILE_BRIDGE_PACKAGE_MISMATCH",
                    rel_profile,
                    f"SDK profile BridgePackageId must be {package_id}.",
                    "Set BridgePackageId to the package directory ID.",
                    line=line_for(text, '"BridgePackageId"'),
                )
            )
        for field in ("ProfileId", "ProfileVersion", "Vendor", "DisplayName", "LicenseUrl", "SupportUrl"):
            if not isinstance(profile.get(field), str) or not profile[field].strip():
                diagnostics.append(
                    diagnostic(
                        "SDK_PROFILE_REQUIRED_FIELD",
                        rel_profile,
                        f"SDK install profile field {field} must be a non-empty string.",
                        f"Set {field} in Editor/SDKInstallProfile.json.",
                        line=line_for(text, f'"{field}"'),
                    )
                )

        domains = profile.get("AllowedDomains")
        allowed_domains = {
            value.strip().rstrip(".").lower()
            for value in domains
            if isinstance(value, str) and value.strip()
        } if isinstance(domains, list) else set()
        if not allowed_domains:
            diagnostics.append(
                diagnostic(
                    "SDK_PROFILE_ALLOWED_DOMAINS_MISSING",
                    rel_profile,
                    "SDK install profile must declare official AllowedDomains.",
                    "List each official source host without scheme, port, wildcard, or path.",
                    line=line_for(text, '"AllowedDomains"'),
                )
            )

        sources = profile.get("Sources")
        if not isinstance(sources, list) or not sources:
            diagnostics.append(
                diagnostic(
                    "SDK_PROFILE_SOURCES_MISSING",
                    rel_profile,
                    "SDK install profile must declare at least one immutable official source.",
                    "Add an artifact, git, or registry source with immutable version data.",
                    line=line_for(text, '"Sources"'),
                )
            )
        else:
            for index, source in enumerate(sources):
                if not isinstance(source, dict):
                    diagnostics.append(
                        diagnostic(
                            "SDK_PROFILE_SOURCE_INVALID",
                            rel_profile,
                            f"SDK source at index {index} must be an object.",
                            "Replace the source entry with the bridge profile source schema.",
                        )
                    )
                    continue
                url = source.get("Url")
                parsed = urlparse(url) if isinstance(url, str) else None
                host = (parsed.hostname or "").lower() if parsed else ""
                host_allowed = any(host == domain or host.endswith(f".{domain}") for domain in allowed_domains)
                if (
                    parsed is None
                    or parsed.scheme.lower() != "https"
                    or not host
                    or parsed.username is not None
                    or parsed.password is not None
                    or bool(parsed.query)
                    or bool(parsed.fragment)
                    or not host_allowed
                ):
                    diagnostics.append(
                        diagnostic(
                            "SDK_PROFILE_SOURCE_URL_UNSAFE",
                            rel_profile,
                            f"SDK source at index {index} must use credential-free HTTPS on AllowedDomains.",
                            "Use an official HTTPS URL and declare its host in AllowedDomains.",
                            line=line_for(text, str(url)) if isinstance(url, str) else 1,
                        )
                    )
                kind = source.get("Kind")
                resolution_policy = source.get("ResolutionPolicy", "exact")
                latest = resolution_policy == "anyInstalledElseLatestStable"
                if resolution_policy not in {"", "exact", "anyInstalledElseLatestStable"}:
                    diagnostics.append(
                        diagnostic(
                            "SDK_PROFILE_RESOLUTION_POLICY_INVALID",
                            rel_profile,
                            f"SDK source at index {index} has unsupported ResolutionPolicy {resolution_policy!r}.",
                            "Use exact or anyInstalledElseLatestStable.",
                            line=line_for(text, '"ResolutionPolicy"'),
                        )
                    )
                if latest:
                    resolver = source.get("LatestResolver")
                    expected_resolver = {
                        "registry": "registryMetadata",
                        "git": "gitRelease",
                        "artifact": "artifactMetadata",
                    }.get(kind)
                    metadata_url = source.get("MetadataUrl")
                    metadata_parsed = urlparse(metadata_url) if isinstance(metadata_url, str) else None
                    metadata_host = (metadata_parsed.hostname or "").lower() if metadata_parsed else ""
                    metadata_host_allowed = any(
                        metadata_host == domain or metadata_host.endswith(f".{domain}")
                        for domain in allowed_domains
                    )
                    if schema_version != 2 or resolver != expected_resolver:
                        diagnostics.append(
                            diagnostic(
                                "SDK_PROFILE_LATEST_RESOLVER_INVALID",
                                rel_profile,
                                f"SDK source at index {index} must use its matching schema-v2 latest resolver.",
                                "Use registryMetadata, gitRelease, or artifactMetadata for the matching source kind.",
                                line=line_for(text, '"LatestResolver"'),
                            )
                        )
                    if (
                        metadata_parsed is None
                        or metadata_parsed.scheme.lower() != "https"
                        or not metadata_host
                        or metadata_parsed.username is not None
                        or metadata_parsed.password is not None
                        or bool(metadata_parsed.query)
                        or bool(metadata_parsed.fragment)
                        or not metadata_host_allowed
                    ):
                        diagnostics.append(
                            diagnostic(
                                "SDK_PROFILE_METADATA_URL_UNSAFE",
                                rel_profile,
                                f"SDK source at index {index} must use credential-free official metadata on AllowedDomains.",
                                "Set MetadataUrl to the official HTTPS metadata endpoint.",
                                line=line_for(text, '"MetadataUrl"'),
                            )
                        )
                    version_family = source.get("VersionFamily", "")
                    if version_family and re.fullmatch(r"[a-z0-9][a-z0-9._-]*", version_family) is None:
                        diagnostics.append(
                            diagnostic(
                                "SDK_PROFILE_VERSION_FAMILY_INVALID",
                                rel_profile,
                                f"SDK source at index {index} has an invalid VersionFamily.",
                                "Use a portable lowercase family identifier.",
                                line=line_for(text, '"VersionFamily"'),
                            )
                        )
                if kind == "artifact":
                    checksum = source.get("Sha256")
                    if (not latest or checksum) and (
                        not isinstance(checksum, str) or re.fullmatch(r"[0-9A-Fa-f]{64}", checksum) is None
                    ):
                        diagnostics.append(
                            diagnostic(
                                "SDK_PROFILE_ARTIFACT_CHECKSUM_INVALID",
                                rel_profile,
                                f"Artifact source at index {index} requires a SHA-256 checksum.",
                                "Record the verified official artifact SHA-256.",
                                line=line_for(text, '"Sha256"'),
                            )
                        )
                elif kind == "git":
                    revision = source.get("ImmutableRevision")
                    immutable = isinstance(revision, str) and (
                        re.fullmatch(r"[0-9A-Fa-f]{40}", revision) is not None
                        or re.fullmatch(r"v?\d+\.\d+\.\d+(?:[-+][0-9A-Za-z.-]+)?", revision) is not None
                    )
                    if (not latest or revision) and not immutable:
                        diagnostics.append(
                            diagnostic(
                                "SDK_PROFILE_GIT_REVISION_MUTABLE",
                                rel_profile,
                                f"Git source at index {index} must use a full commit or exact SemVer tag.",
                                "Replace branch names with an immutable revision.",
                                line=line_for(text, '"ImmutableRevision"'),
                            )
                        )
                    git_subpath = source.get("GitSubpath")
                    if isinstance(git_subpath, str) and git_subpath:
                        segments = git_subpath.split("/")
                        valid_subpath = (
                            git_subpath == git_subpath.strip()
                            and not git_subpath.startswith("/")
                            and not git_subpath.endswith("/")
                            and "\\" not in git_subpath
                            and all(
                                segment not in {"", ".", ".."}
                                and re.fullmatch(r"[0-9A-Za-z._-]+", segment) is not None
                                for segment in segments
                            )
                        )
                        if not valid_subpath:
                            diagnostics.append(
                                diagnostic(
                                    "SDK_PROFILE_GIT_SUBPATH_INVALID",
                                    rel_profile,
                                    f"Git source at index {index} has an unsafe UPM package subpath.",
                                    "Use a relative GitSubpath with safe path segments and no traversal.",
                                    line=line_for(text, '"GitSubpath"'),
                                )
                            )
                elif kind == "registry":
                    version = source.get("ImmutableVersion")
                    package_name = source.get("PackageId")
                    if (
                        (not latest or version)
                        and (not isinstance(version, str) or parse_semver(version) is None)
                        or not isinstance(package_name, str)
                        or not package_name.strip()
                    ):
                        diagnostics.append(
                            diagnostic(
                                "SDK_PROFILE_REGISTRY_VERSION_INVALID",
                                rel_profile,
                                f"Registry source at index {index} must use exact SemVer and a package ID.",
                                "Set ImmutableVersion to MAJOR.MINOR.PATCH and PackageId to the resolved package.",
                                line=line_for(text, '"ImmutableVersion"'),
                            )
                        )
                else:
                    diagnostics.append(
                        diagnostic(
                            "SDK_PROFILE_SOURCE_KIND_INVALID",
                            rel_profile,
                            f"SDK source at index {index} has unsupported Kind {kind!r}.",
                            "Use artifact, git, or registry.",
                        )
                    )

    notices_path = package_root / "THIRD_PARTY_NOTICES.md"
    notices = read_text(repo_root, notices_path, diagnostics)
    if notices is not None and "contains no redistributed" not in notices.lower():
        diagnostics.append(
            diagnostic(
                "SDK_THIRD_PARTY_NOTICE_BOUNDARY_MISSING",
                relative_path(repo_root, notices_path),
                "Third-party notices must state that vendor SDK files are not redistributed.",
                "Document the source-only bridge boundary and vendor license links.",
            )
        )

    package_info_path = package_root / "Editor/PackageInfo/ActionFitPackageInfo_SO.asset"
    package_info = read_text(repo_root, package_info_path, diagnostics)
    if package_info is not None and not re.search(r"^\s*_repositoryVisibility:\s*0\s*$", package_info, re.MULTILINE):
        diagnostics.append(
            diagnostic(
                "SDK_BRIDGE_REPOSITORY_NOT_PUBLIC",
                relative_path(repo_root, package_info_path),
                "SDK bridge packages must use Public repository visibility.",
                "Set PackageInfo _repositoryVisibility to 0 (Public).",
                line=line_for(package_info, "_repositoryVisibility:"),
            )
        )

    for path in sorted(package_root.rglob("*")):
        if path.is_symlink():
            diagnostics.append(
                diagnostic(
                    "SDK_BRIDGE_LINK_FORBIDDEN",
                    relative_path(repo_root, path),
                    "Source-only SDK bridge packages must not contain symbolic links.",
                    "Replace the link with package-owned source text or remove it.",
                )
            )
            continue
        relative = path.relative_to(package_root).as_posix()
        lower_name = path.name.lower()
        lower_relative = relative.lower()
        forbidden_suffix = next((suffix for suffix in SDK_FORBIDDEN_SUFFIXES if lower_relative.endswith(suffix)), None)
        if lower_name in SDK_FORBIDDEN_FILE_NAMES or forbidden_suffix or lower_relative.endswith(".tar.gz"):
            diagnostics.append(
                diagnostic(
                    "SDK_BRIDGE_VENDOR_FILE_FORBIDDEN",
                    relative_path(repo_root, path),
                    f"Source-only SDK bridge package contains forbidden vendor file {relative}.",
                    "Remove SDK binaries, archives, and vendor configuration files from the bridge package.",
                )
            )
            continue
        if not path.is_file():
            continue
        if path.stat().st_size > 5 * 1024 * 1024:
            diagnostics.append(
                diagnostic(
                    "SDK_BRIDGE_FILE_TOO_LARGE",
                    relative_path(repo_root, path),
                    f"SDK bridge file {relative} exceeds the 5 MiB source-only limit.",
                    "Remove redistributed or generated vendor content from the bridge package.",
                )
            )
            continue
        if path.suffix.lower() not in {".md", ".json", ".cs", ".asmdef", ".asset", ".meta", ".txt", ".py", ".sh"}:
            continue
        try:
            candidate_text = path.read_text(encoding="utf-8-sig")
        except (OSError, UnicodeError):
            continue
        if SDK_CREDENTIAL_PATTERN.search(candidate_text):
            diagnostics.append(
                diagnostic(
                    "SDK_BRIDGE_CREDENTIAL_FORBIDDEN",
                    relative_path(repo_root, path),
                    f"SDK bridge file {relative} appears to contain a credential or private key.",
                    "Remove credentials and keep local vendor configuration outside the package.",
                )
                )


def validate_settings_so_lifecycle(
    repo_root: Path,
    package_id: str,
    package_root: Path,
    manifest: dict[str, Any] | None,
    diagnostics: list[Diagnostic],
) -> None:
    source_files: list[tuple[Path, str]] = []
    for path in sorted(package_root.rglob("*.cs")):
        try:
            source_files.append((path, path.read_text(encoding="utf-8-sig")))
        except (OSError, UnicodeError):
            continue

    registrations = [
        (path, text)
        for path, text in source_files
        if re.search(r"\[\s*ActionFitSettingsAsset\s*\(", text)
    ]
    if not registrations:
        return

    manifest_path = package_root / "package.json"
    dependencies = manifest.get("dependencies") if isinstance(manifest, dict) else None
    so_version = dependencies.get("com.actionfit.sosingleton") if isinstance(dependencies, dict) else None
    if not isinstance(so_version, str) or parse_semver(so_version) is None:
        diagnostics.append(
            diagnostic(
                "SETTINGS_SO_DEPENDENCY_MISSING",
                relative_path(repo_root, manifest_path),
                "A registered settings SO requires a direct semantic-version dependency on com.actionfit.sosingleton.",
                "Add com.actionfit.sosingleton to package.json dependencies using the prepared package version.",
                line=line_for(json.dumps(manifest or {}, ensure_ascii=False, indent=2), '"dependencies"'),
            )
        )

    all_source = "\n".join(text for _, text in source_files)
    if not re.search(r'MenuItem\s*\([^\n]*"[^"\n]*Setting SO"', all_source):
        diagnostics.append(
            diagnostic(
                "SETTINGS_SO_MENU_MISSING",
                relative_path(repo_root, registrations[0][0]),
                "A registered settings SO package must expose a Setting SO menu entry.",
                "Add a package-scoped Setting SO menu that resolves the asset through ActionFitSettingsAssetProvider.",
            )
        )

    asmdefs: list[tuple[Path, dict[str, Any]]] = []
    for path in sorted(package_root.rglob("*.asmdef")):
        try:
            value = json.loads(path.read_text(encoding="utf-8-sig"))
        except (OSError, UnicodeError, json.JSONDecodeError):
            continue
        if isinstance(value, dict):
            asmdefs.append((path, value))

    reference_names = {
        reference
        for _, asmdef in asmdefs
        for reference in asmdef.get("references", [])
        if isinstance(reference, str)
    }
    required_references = {"com.actionfit.sosingleton", "com.actionfit.sosingleton.Editor"}
    missing_references = sorted(required_references - reference_names)
    if missing_references:
        diagnostics.append(
            diagnostic(
                "SETTINGS_SO_ASMDEF_REFERENCE_MISSING",
                relative_path(repo_root, registrations[0][0]),
                "Registered settings SO assemblies are missing shared lifecycle references: "
                + ", ".join(missing_references),
                "Reference com.actionfit.sosingleton from the settings assembly and com.actionfit.sosingleton.Editor from its Editor integration assembly.",
            )
        )

    for path, text in registrations:
        relative = path.relative_to(package_root).as_posix()
        is_runtime = "ActionFitSettingsAssetLifetime.RuntimeSingleton" in text
        if is_runtime:
            class_match = re.search(r"class\s+(?P<type>[A-Za-z_][A-Za-z0-9_]*)\s*:\s*SO_Singleton\s*<\s*(?P=type)\s*>", text)
            if class_match is None:
                diagnostics.append(
                    diagnostic(
                        "SETTINGS_SO_RUNTIME_BASE_INVALID",
                        relative_path(repo_root, path),
                        "RuntimeSingleton settings must inherit SO_Singleton<Self>.",
                        "Change the registered type to inherit SO_Singleton with its own type argument.",
                        line=line_for(text, "RuntimeSingleton"),
                    )
                )
            if not relative.startswith("Runtime/"):
                diagnostics.append(
                    diagnostic(
                        "SETTINGS_SO_RUNTIME_LOCATION_INVALID",
                        relative_path(repo_root, path),
                        "RuntimeSingleton settings source must live under the package Runtime folder.",
                        "Move the runtime settings type to Runtime without moving or reserializing its project-owned asset.",
                    )
                )
        elif not relative.startswith("Editor/"):
            diagnostics.append(
                diagnostic(
                    "SETTINGS_SO_EDITOR_LOCATION_INVALID",
                    relative_path(repo_root, path),
                    "EditorOnly settings source must live under the package Editor folder.",
                    "Move the settings source into the Editor assembly; preserve the project-owned asset GUID and path.",
                )
            )


def run_git(repo_root: Path, arguments: Sequence[str]) -> str:
    try:
        completed = subprocess.run(
            ["git", "-C", str(repo_root), *arguments],
            check=False,
            stdout=subprocess.PIPE,
            stderr=subprocess.PIPE,
            text=True,
            encoding="utf-8",
            errors="replace",
        )
    except OSError as exc:
        raise InfrastructureError(f"Could not start git: {exc}") from exc
    if completed.returncode != 0:
        detail = completed.stderr.strip() or completed.stdout.strip() or "unknown git error"
        raise InfrastructureError(f"git {' '.join(arguments)} failed: {detail}")
    return completed.stdout


def resolve_base_commit(repo_root: Path, base_ref: str) -> str:
    run_git(repo_root, ["rev-parse", "--verify", f"{base_ref}^{{commit}}"])
    return run_git(repo_root, ["merge-base", base_ref, "HEAD"]).strip()


def changed_paths(repo_root: Path, base_commit: str) -> set[str]:
    outputs = [
        run_git(repo_root, ["diff", "--name-only", f"{base_commit}...HEAD"]),
        run_git(repo_root, ["diff", "--name-only"]),
        run_git(repo_root, ["diff", "--cached", "--name-only"]),
        run_git(repo_root, ["ls-files", "--others", "--exclude-standard"]),
    ]
    return {line.strip().replace("\\", "/") for output in outputs for line in output.splitlines() if line.strip()}


def package_ids_from_paths(paths: Iterable[str]) -> set[str]:
    paths_by_package: dict[str, set[str]] = {}
    for path in paths:
        parts = path.split("/")
        if len(parts) < 2 or parts[0] != "Packages":
            continue

        candidate = parts[1]
        if len(parts) == 2 and candidate.endswith(".meta"):
            candidate = candidate[:-5]
        if PACKAGE_ID_PATTERN.fullmatch(candidate):
            paths_by_package.setdefault(candidate, set()).add(path)

    package_ids: set[str] = set()
    for package_id, package_paths in paths_by_package.items():
        if package_id == CUSTOM_PACKAGE_MANAGER_ID and package_paths == {GENERATED_ROUTER_PATH}:
            continue
        package_ids.add(package_id)
    return package_ids


def manifest_at_commit(repo_root: Path, commit: str, package_id: str) -> dict[str, Any] | None:
    path = f"Packages/{package_id}/package.json"
    tree_paths = run_git(repo_root, ["ls-tree", "--name-only", commit, "--", path]).splitlines()
    if path not in {item.strip().replace("\\", "/") for item in tree_paths}:
        return None
    text = run_git(repo_root, ["show", f"{commit}:{path}"])
    try:
        value = json.loads(text)
    except json.JSONDecodeError as exc:
        raise InfrastructureError(f"Base manifest {path} is invalid JSON: {exc}") from exc
    if not isinstance(value, dict):
        raise InfrastructureError(f"Base manifest {path} is not a JSON object")
    return value


def validate_version_bump(
    repo_root: Path,
    package_id: str,
    manifest: dict[str, Any] | None,
    current_version: SemVer | None,
    base_commit: str | None,
    changed_package_ids: set[str],
    diagnostics: list[Diagnostic],
) -> None:
    if base_commit is None or package_id not in changed_package_ids or manifest is None or current_version is None:
        return
    base_manifest = manifest_at_commit(repo_root, base_commit, package_id)
    if base_manifest is None:
        return
    base_version_text = base_manifest.get("version")
    base_version = parse_semver(base_version_text) if isinstance(base_version_text, str) else None
    manifest_path = f"Packages/{package_id}/package.json"
    if base_version is None:
        diagnostics.append(
            diagnostic(
                "BASE_PACKAGE_VERSION_INVALID",
                manifest_path,
                f"Base version {base_version_text!r} is not valid SemVer.",
                "Repair the target branch package version before comparing this change.",
            )
        )
    elif compare_semver(current_version, base_version) <= 0:
        current_text = manifest.get("version")
        diagnostics.append(
            diagnostic(
                "PACKAGE_VERSION_NOT_INCREMENTED",
                manifest_path,
                f"Changed package version {current_text} is not greater than base version {base_version_text}.",
                "Bump package.json to the next unused SemVer and align README, AI_GUIDE, and PackageInfo release notes.",
            )
        )


def immutable_git_dependency(value: str) -> bool:
    if value.count("#") != 1:
        return False
    source_url, revision = value.split("#", 1)
    parsed = urlparse(source_url)
    query = parse_qsl(parsed.query, keep_blank_values=True)
    safe_subpath = not query or (
        len(query) == 1
        and query[0][0] == "path"
        and bool(query[0][1])
        and not query[0][1].startswith("/")
        and ".." not in Path(query[0][1]).parts
    )
    safe_url = (
        parsed.scheme.lower() == "https"
        and bool(parsed.hostname)
        and parsed.username is None
        and parsed.password is None
        and parsed.path.endswith(".git")
        and safe_subpath
    )
    normalized_revision = revision[1:] if revision.startswith("v") else revision
    immutable_revision = (
        re.fullmatch(r"[0-9a-fA-F]{40}", revision) is not None
        or parse_semver(normalized_revision) is not None
    )
    return safe_url and immutable_revision


def validate_downloaded_transition(
    repo_root: Path,
    package_id: str,
    base_commit: str,
) -> dict[str, Any]:
    diagnostics: list[Diagnostic] = []
    project_manifest_path = repo_root / "Packages" / "manifest.json"
    project_lock_path = repo_root / "Packages" / "packages-lock.json"
    project_manifest, manifest_text = load_json(
        repo_root,
        project_manifest_path,
        diagnostics,
        "PROJECT_MANIFEST_JSON_INVALID",
    )
    project_lock, lock_text = load_json(
        repo_root,
        project_lock_path,
        diagnostics,
        "PROJECT_PACKAGE_LOCK_JSON_INVALID",
    )

    dependency: str | None = None
    dependencies = project_manifest.get("dependencies") if project_manifest else None
    candidate = dependencies.get(package_id) if isinstance(dependencies, dict) else None
    if isinstance(candidate, str) and candidate.strip():
        dependency = candidate.strip()
    else:
        diagnostics.append(
            diagnostic(
                "DOWNLOADED_TRANSITION_DEPENDENCY_MISSING",
                relative_path(repo_root, project_manifest_path),
                f"Deleted embedded package {package_id} is not a top-level project dependency.",
                f"Add {package_id} to Packages/manifest.json with an immutable HTTPS Git reference or restore the embedded directory.",
                line=line_for(manifest_text or "", f'"{package_id}"'),
            )
        )

    if dependency is not None and not immutable_git_dependency(dependency):
        diagnostics.append(
            diagnostic(
                "DOWNLOADED_TRANSITION_GIT_REFERENCE_INVALID",
                relative_path(repo_root, project_manifest_path),
                f"Downloaded package {package_id} must use a credential-free HTTPS .git URL with a full commit or exact SemVer tag.",
                "Replace the manifest dependency with an immutable HTTPS Git UPM reference.",
                line=line_for(manifest_text or "", f'"{package_id}"'),
            )
        )

    lock_dependencies = project_lock.get("dependencies") if project_lock else None
    lock_entry = lock_dependencies.get(package_id) if isinstance(lock_dependencies, dict) else None
    lock_valid = (
        isinstance(lock_entry, dict)
        and lock_entry.get("depth") == 0
        and lock_entry.get("source") == "git"
        and dependency is not None
        and lock_entry.get("version") == dependency
        and isinstance(lock_entry.get("hash"), str)
        and re.fullmatch(r"[0-9a-fA-F]{40}", lock_entry["hash"]) is not None
    )
    if not lock_valid:
        diagnostics.append(
            diagnostic(
                "DOWNLOADED_TRANSITION_LOCK_INVALID",
                relative_path(repo_root, project_lock_path),
                f"Downloaded package {package_id} must have a depth-0 Git lock entry whose version matches the manifest and whose hash is a full commit.",
                "Resolve the immutable Git dependency with Unity and commit the matching Packages/packages-lock.json entry.",
                line=line_for(lock_text or "", f'"{package_id}"'),
            )
        )

    base_manifest = manifest_at_commit(repo_root, base_commit, package_id)
    if base_manifest is None:
        diagnostics.append(
            diagnostic(
                "DOWNLOADED_TRANSITION_BASE_PACKAGE_MISSING",
                f"Packages/{package_id}",
                f"The base commit does not contain an embedded package manifest for {package_id}.",
                "Restore the missing package or validate the downloaded dependency outside an embedded-to-downloaded transition.",
            )
        )

    valid = not any(item.severity == "error" for item in diagnostics)
    return {
        "packageId": package_id,
        "version": dependency,
        "valid": valid,
        "diagnostics": [asdict(item) for item in diagnostics],
    }


def validate_package(
    repo_root: Path,
    package_id: str,
    base_commit: str | None,
    changed_package_ids: set[str],
) -> dict[str, Any]:
    diagnostics: list[Diagnostic] = []
    package_root = repo_root / "Packages" / package_id
    if not package_root.is_dir():
        if base_commit is not None and package_id in changed_package_ids:
            return validate_downloaded_transition(repo_root, package_id, base_commit)
        diagnostics.append(
            diagnostic(
                "PACKAGE_DIRECTORY_MISSING",
                f"Packages/{package_id}",
                f"Changed package directory Packages/{package_id} does not exist.",
                "Restore the package directory or remove the package change from this validation scope.",
            )
        )
        return {"packageId": package_id, "version": None, "valid": False, "diagnostics": [asdict(x) for x in diagnostics]}

    manifest, _, current_version = validate_manifest(repo_root, package_id, package_root, diagnostics)
    version_text = manifest.get("version") if manifest and isinstance(manifest.get("version"), str) else None
    validate_readme(repo_root, package_id, package_root, version_text, diagnostics)
    validate_ai_guide(repo_root, package_id, package_root, version_text, diagnostics)
    validate_skills(repo_root, package_id, package_root, diagnostics)
    validate_package_info(repo_root, package_id, package_root, manifest, diagnostics)
    validate_asmdefs(repo_root, package_id, package_root, diagnostics)
    validate_settings_so_lifecycle(repo_root, package_id, package_root, manifest, diagnostics)
    validate_sdk_bridge(repo_root, package_id, package_root, manifest, diagnostics)
    validate_version_bump(
        repo_root,
        package_id,
        manifest,
        current_version,
        base_commit,
        changed_package_ids,
        diagnostics,
    )
    valid = not any(item.severity == "error" for item in diagnostics)
    return {
        "packageId": package_id,
        "version": version_text,
        "valid": valid,
        "diagnostics": [asdict(item) for item in diagnostics],
    }


def find_repo_root(explicit_root: str | None) -> Path:
    candidates: list[Path] = []
    if explicit_root:
        candidates.append(Path(explicit_root))
    else:
        candidates.extend([Path.cwd(), *Path.cwd().parents])
        script_path = Path(__file__).resolve()
        candidates.extend([script_path, *script_path.parents])
    for candidate in candidates:
        root = candidate.resolve()
        if root.is_file():
            root = root.parent
        if (root / "Packages").is_dir():
            return root
    raise InfrastructureError("Could not find a repository root containing a Packages directory")


def all_package_ids(repo_root: Path) -> list[str]:
    packages_root = repo_root / "Packages"
    return sorted(
        path.name
        for path in packages_root.iterdir()
        if path.is_dir() and PACKAGE_ID_PATTERN.fullmatch(path.name) and (path / "package.json").is_file()
    )


def build_result(
    *,
    mode: str,
    base_ref: str | None,
    package_results: list[dict[str, Any]],
    infrastructure_diagnostics: list[Diagnostic] | None = None,
) -> dict[str, Any]:
    infra = infrastructure_diagnostics or []
    package_diagnostics = [item for package in package_results for item in package["diagnostics"]]
    diagnostics = [asdict(item) for item in infra] + package_diagnostics
    errors = sum(1 for item in diagnostics if item["severity"] == "error")
    warnings = sum(1 for item in diagnostics if item["severity"] == "warning")
    exit_code = 2 if infra else (1 if errors else 0)
    return {
        "schemaVersion": SCHEMA_VERSION,
        "tool": "actionfit-package-contract-validator",
        "mode": mode,
        "baseRef": base_ref,
        "success": exit_code == 0,
        "exitCode": exit_code,
        "summary": {
            "packages": len(package_results),
            "errors": errors,
            "warnings": warnings,
        },
        "packages": package_results,
        "diagnostics": diagnostics,
    }


def parse_arguments(argv: Sequence[str]) -> argparse.Namespace:
    parser = JsonArgumentParser(description=__doc__)
    mode = parser.add_mutually_exclusive_group(required=True)
    mode.add_argument("--package", metavar="PACKAGE_ID", help="Validate one com.actionfit.* package")
    mode.add_argument("--changed", action="store_true", help="Validate packages changed from --base-ref")
    mode.add_argument("--all", action="store_true", help="Validate all embedded com.actionfit.* packages")
    parser.add_argument("--base-ref", help="Git base ref used for changed selection and version-bump checks")
    parser.add_argument("--repo-root", help="Repository root containing Packages (auto-detected by default)")
    parser.add_argument("--output", help="Also write the JSON result to this file")
    return parser.parse_args(argv)


def execute(arguments: argparse.Namespace) -> dict[str, Any]:
    if arguments.changed and not arguments.base_ref:
        raise InfrastructureError("--changed requires --base-ref")
    repo_root = find_repo_root(arguments.repo_root)
    base_commit = resolve_base_commit(repo_root, arguments.base_ref) if arguments.base_ref else None
    paths = changed_paths(repo_root, base_commit) if base_commit else set()
    changed_package_ids = package_ids_from_paths(paths)

    if arguments.package:
        if not PACKAGE_ID_PATTERN.fullmatch(arguments.package):
            raise InfrastructureError(f"Invalid package ID: {arguments.package}")
        mode = "package"
        package_ids = [arguments.package]
    elif arguments.changed:
        mode = "changed"
        package_ids = sorted(changed_package_ids)
    else:
        mode = "all"
        package_ids = all_package_ids(repo_root)

    results = [validate_package(repo_root, package_id, base_commit, changed_package_ids) for package_id in package_ids]
    return build_result(mode=mode, base_ref=arguments.base_ref, package_results=results)


def serialize_result(result: dict[str, Any]) -> str:
    return json.dumps(result, ensure_ascii=False, indent=2) + "\n"


def main(argv: Sequence[str] | None = None) -> int:
    arguments: argparse.Namespace | None = None
    try:
        arguments = parse_arguments(argv if argv is not None else sys.argv[1:])
        result = execute(arguments)
    except InfrastructureError as exc:
        result = build_result(
            mode="unknown",
            base_ref=getattr(arguments, "base_ref", None),
            package_results=[],
            infrastructure_diagnostics=[
                diagnostic(
                    "INFRASTRUCTURE_ERROR",
                    ".",
                    str(exc),
                    "Fix the repository, arguments, or local git environment and run validation again.",
                )
            ],
        )
    except Exception as exc:  # Defensive CLI boundary: always preserve the JSON contract.
        result = build_result(
            mode="unknown",
            base_ref=getattr(arguments, "base_ref", None),
            package_results=[],
            infrastructure_diagnostics=[
                diagnostic(
                    "INFRASTRUCTURE_UNEXPECTED",
                    ".",
                    f"Unexpected validator failure: {exc}",
                    "Report this failure with the JSON result and validator version.",
                )
            ],
        )

    output_path = getattr(arguments, "output", None)
    if output_path:
        try:
            path = Path(output_path)
            path.parent.mkdir(parents=True, exist_ok=True)
            path.write_text(serialize_result(result), encoding="utf-8")
        except OSError as exc:
            result = build_result(
                mode=result["mode"],
                base_ref=result["baseRef"],
                package_results=result["packages"],
                infrastructure_diagnostics=[
                    diagnostic(
                        "RESULT_WRITE_FAILED",
                        output_path,
                        f"Could not write JSON result: {exc}",
                        "Choose a writable --output path and rerun validation.",
                    )
                ],
            )
    sys.stdout.write(serialize_result(result))
    return int(result["exitCode"])


if __name__ == "__main__":
    raise SystemExit(main())
