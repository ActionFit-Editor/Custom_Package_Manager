#!/usr/bin/env python3
"""Plan and apply ActionFit embedded-package dependency release updates safely."""

from __future__ import annotations

import argparse
import ast
import csv
import hashlib
import json
import os
import re
import subprocess
import sys
import tempfile
from dataclasses import dataclass
from datetime import datetime, timezone
from functools import total_ordering
from pathlib import Path
from typing import Any, Callable, Dict, List, Optional, Sequence, Set, Tuple


SCHEMA_VERSION = "1.0"
ACTIONFIT_PREFIX = "com.actionfit."
SEMVER_PATTERN = re.compile(
    r"^(0|[1-9]\d*)\.(0|[1-9]\d*)\.(0|[1-9]\d*)"
    r"(?:-((?:0|[1-9]\d*|\d*[A-Za-z-][0-9A-Za-z-]*)"
    r"(?:\.(?:0|[1-9]\d*|\d*[A-Za-z-][0-9A-Za-z-]*))*))?"
    r"(?:\+([0-9A-Za-z-]+(?:\.[0-9A-Za-z-]+)*))?$"
)
README_INSTALL_PATTERN = re.compile(
    r'"(?P<package_id>com\.actionfit\.[^"]+)"\s*:\s*"[^"]+\.git#(?P<version>[^"\s]+)"'
)
AI_GUIDE_VERSION_PATTERN = re.compile(
    r"Current package version at generation time:\s*`(?P<version>[^`]+)`"
)
RELEASE_NOTE_PATTERN = re.compile(
    r"(?ms)^(?P<indent>[ \t]*)_releaseNote:\s*(?P<scalar>.*?)"
    r"(?=^[ \t]*_dependenciesOverride:)"
)


class DependencyUpdateError(RuntimeError):
    """Raised when the dependency update cannot be planned reliably."""


class JsonArgumentParser(argparse.ArgumentParser):
    def error(self, message: str) -> None:
        raise DependencyUpdateError("Invalid arguments: {0}".format(message))


@total_ordering
@dataclass(frozen=True, eq=False)
class SemVer:
    major: int
    minor: int
    patch: int
    prerelease: Tuple[str, ...] = ()
    build: Tuple[str, ...] = ()

    @staticmethod
    def parse(value: str) -> "SemVer":
        match = SEMVER_PATTERN.fullmatch(value or "")
        if match is None:
            raise ValueError("Invalid SemVer: {0}".format(value))
        prerelease = tuple((match.group(4) or "").split(".")) if match.group(4) else ()
        build = tuple((match.group(5) or "").split(".")) if match.group(5) else ()
        return SemVer(int(match.group(1)), int(match.group(2)), int(match.group(3)), prerelease, build)

    def __str__(self) -> str:
        value = "{0}.{1}.{2}".format(self.major, self.minor, self.patch)
        if self.prerelease:
            value += "-" + ".".join(self.prerelease)
        if self.build:
            value += "+" + ".".join(self.build)
        return value

    def __lt__(self, other: object) -> bool:
        if not isinstance(other, SemVer):
            return NotImplemented
        core = (self.major, self.minor, self.patch)
        other_core = (other.major, other.minor, other.patch)
        if core != other_core:
            return core < other_core
        if not self.prerelease:
            return bool(other.prerelease)
        if not other.prerelease:
            return True
        for left, right in zip(self.prerelease, other.prerelease):
            if left == right:
                continue
            left_numeric = left.isdigit()
            right_numeric = right.isdigit()
            if left_numeric and right_numeric:
                return int(left) < int(right)
            if left_numeric != right_numeric:
                return left_numeric
            return left < right
        return len(self.prerelease) < len(other.prerelease)

    def __eq__(self, other: object) -> bool:
        if not isinstance(other, SemVer):
            return NotImplemented
        return (
            self.major,
            self.minor,
            self.patch,
            self.prerelease,
        ) == (
            other.major,
            other.minor,
            other.patch,
            other.prerelease,
        )

    def __hash__(self) -> int:
        return hash((self.major, self.minor, self.patch, self.prerelease))

    def bump_patch(self) -> "SemVer":
        return SemVer(self.major, self.minor, self.patch + 1)


@dataclass
class EmbeddedPackage:
    package_id: str
    root: Path
    manifest: Dict[str, Any]
    version: SemVer
    dependencies: Dict[str, str]


@dataclass
class PlanBuild:
    result: Dict[str, Any]
    mutations: Dict[Path, bytes]
    input_hashes: Dict[Path, str]
    package_inventory_hash: str


def sha256_bytes(value: bytes) -> str:
    return hashlib.sha256(value).hexdigest()


def sha256_file(path: Path) -> str:
    return sha256_bytes(path.read_bytes())


def canonical_hash(value: Any) -> str:
    payload = json.dumps(value, ensure_ascii=False, sort_keys=True, separators=(",", ":"))
    return hashlib.sha256(payload.encode("utf-8")).hexdigest()[:20].upper()


def embedded_inventory_hash(repo_root: Path) -> str:
    packages_root = repo_root / "Packages"
    entries = [
        {
            "name": root.name,
            "isSymlink": root.is_symlink(),
            "hasManifest": (root / "package.json").is_file(),
        }
        for root in sorted(packages_root.glob("com.actionfit.*"))
    ]
    return canonical_hash(entries)


def relative(repo_root: Path, path: Path) -> str:
    try:
        return path.absolute().relative_to(repo_root.absolute()).as_posix()
    except ValueError:
        return path.as_posix()


def issue(code: str, message: str, package_id: str = "", path: str = "") -> Dict[str, str]:
    return {
        "code": code,
        "message": message,
        "packageId": package_id,
        "path": path,
    }


def add_unique(items: List[Dict[str, str]], item: Dict[str, str]) -> None:
    key = (item["code"], item["message"], item["packageId"], item["path"])
    if all((value["code"], value["message"], value["packageId"], value["path"]) != key for value in items):
        items.append(item)


def load_json(path: Path) -> Dict[str, Any]:
    try:
        value = json.loads(path.read_text(encoding="utf-8-sig"))
    except (OSError, json.JSONDecodeError) as error:
        raise DependencyUpdateError("Could not read JSON {0}: {1}".format(path, error))
    if not isinstance(value, dict):
        raise DependencyUpdateError("JSON root must be an object: {0}".format(path))
    return value


def resolve_catalog(repo_root: Path, requested: Optional[str]) -> Path:
    candidates = []
    if requested:
        requested_path = Path(requested)
        candidates.append(requested_path if requested_path.is_absolute() else repo_root / requested_path)
    else:
        candidates.extend(
            [
                repo_root / "Assets/_Data/_CustomPackageManager/package_catalog.csv",
                repo_root / "Packages/com.actionfit.custompackagemanager/Editor/Catalog/package_catalog.csv",
            ]
        )
    for candidate in candidates:
        if candidate.is_symlink():
            raise DependencyUpdateError("Catalog paths must not be symbolic links: {0}".format(candidate))
        if candidate.is_file():
            try:
                candidate.resolve().relative_to(repo_root.resolve())
            except ValueError:
                raise DependencyUpdateError("Catalog must be inside the repository: {0}".format(candidate))
            return candidate
    raise DependencyUpdateError("Package catalog CSV was not found.")


def load_catalog(
    path: Path,
) -> Tuple[Dict[str, SemVer], List[Dict[str, str]], List[Dict[str, str]]]:
    latest_values: Dict[str, List[SemVer]] = {}
    warnings: List[Dict[str, str]] = []
    conflicts: List[Dict[str, str]] = []
    try:
        with path.open("r", encoding="utf-8-sig", newline="") as handle:
            for row in csv.DictReader(handle):
                package_id = str(row.get("package_id", "")).strip()
                version_text = str(row.get("version", "")).strip()
                is_latest = str(row.get("is_latest", "")).strip().lower() in {"true", "1", "yes"}
                if not package_id.startswith(ACTIONFIT_PREFIX) or not is_latest:
                    continue
                try:
                    version = SemVer.parse(version_text)
                except ValueError:
                    add_unique(
                        conflicts,
                        issue(
                            "CATALOG_LATEST_VERSION_INVALID",
                            "Latest Catalog version must use SemVer: {0}.".format(version_text),
                            package_id,
                            path.as_posix(),
                        ),
                    )
                    continue
                latest_values.setdefault(package_id, []).append(version)
    except (OSError, csv.Error) as error:
        raise DependencyUpdateError("Could not read Catalog CSV {0}: {1}".format(path, error))

    latest: Dict[str, SemVer] = {}
    for package_id, versions in latest_values.items():
        distinct = sorted(set(versions))
        latest[package_id] = distinct[-1]
        if len(distinct) > 1:
            add_unique(
                warnings,
                issue(
                    "CATALOG_MULTIPLE_LATEST_ROWS",
                    "Multiple latest rows were found; selected highest version {0}.".format(distinct[-1]),
                    package_id,
                    path.as_posix(),
                ),
            )
    return latest, warnings, conflicts


def load_project_overrides(repo_root: Path) -> Set[str]:
    path = repo_root / "ProjectSettings/ActionFitPackageOverrides.json"
    if not path.exists():
        return set()
    value = load_json(path)
    overrides = value.get("overrides", [])
    if not isinstance(overrides, list):
        raise DependencyUpdateError("ActionFitPackageOverrides.json overrides must be an array.")
    return {
        str(item.get("packageId") or item.get("package_id") or "").strip()
        for item in overrides
        if isinstance(item, dict) and str(item.get("packageId") or item.get("package_id") or "").strip()
    }


def load_embedded_packages(repo_root: Path) -> Tuple[Dict[str, EmbeddedPackage], List[Dict[str, str]]]:
    packages_root = repo_root / "Packages"
    packages: Dict[str, EmbeddedPackage] = {}
    conflicts: List[Dict[str, str]] = []
    if not packages_root.is_dir():
        raise DependencyUpdateError("Packages directory was not found: {0}".format(packages_root))

    for root in sorted(packages_root.glob("com.actionfit.*")):
        if root.is_symlink():
            add_unique(
                conflicts,
                issue(
                    "EMBEDDED_PACKAGE_LINK_REJECTED",
                    "Embedded package roots must be physical directories, not links.",
                    root.name,
                    relative(repo_root, root),
                ),
            )
            continue
        manifest_path = root / "package.json"
        if not root.is_dir() or not manifest_path.is_file():
            continue
        if manifest_path.is_symlink():
            add_unique(
                conflicts,
                issue(
                    "EMBEDDED_PACKAGE_MANIFEST_LINK_REJECTED",
                    "Embedded package manifests must be physical files, not links.",
                    root.name,
                    relative(repo_root, manifest_path),
                ),
            )
            continue
        try:
            manifest = load_json(manifest_path)
            package_id = str(manifest.get("name", "")).strip()
            version = SemVer.parse(str(manifest.get("version", "")).strip())
        except (DependencyUpdateError, ValueError) as error:
            add_unique(
                conflicts,
                issue("EMBEDDED_PACKAGE_INVALID", str(error), root.name, relative(repo_root, manifest_path)),
            )
            continue
        if package_id != root.name:
            add_unique(
                conflicts,
                issue(
                    "EMBEDDED_PACKAGE_ID_MISMATCH",
                    "package.json name does not match the physical directory.",
                    package_id or root.name,
                    relative(repo_root, manifest_path),
                ),
            )
            continue
        dependencies = manifest.get("dependencies", {})
        if not isinstance(dependencies, dict) or any(
            not isinstance(key, str) or not isinstance(value, str) for key, value in dependencies.items()
        ):
            add_unique(
                conflicts,
                issue(
                    "PACKAGE_DEPENDENCIES_INVALID",
                    "package.json dependencies must be a string-to-string object.",
                    package_id,
                    relative(repo_root, manifest_path),
                ),
            )
            continue
        packages[package_id] = EmbeddedPackage(package_id, root, manifest, version, dict(dependencies))
    return packages, conflicts


def higher(left: SemVer, right: Optional[SemVer]) -> SemVer:
    return left if right is None or left > right else right


def replace_readme_version(text: str, package_id: str, version: str) -> str:
    matches = [match for match in README_INSTALL_PATTERN.finditer(text) if match.group("package_id") == package_id]
    if len(matches) != 1:
        raise DependencyUpdateError(
            "README.md must contain exactly one Git UPM install entry for {0}; found {1}.".format(
                package_id, len(matches)
            )
        )
    match = matches[0]
    start, end = match.span("version")
    return text[:start] + version + text[end:]


def replace_ai_guide_version(text: str, version: str) -> str:
    matches = list(AI_GUIDE_VERSION_PATTERN.finditer(text))
    if len(matches) != 1:
        raise DependencyUpdateError(
            "AI_GUIDE.md must contain exactly one generated package version; found {0}.".format(len(matches))
        )
    match = matches[0]
    start, end = match.span("version")
    return text[:start] + version + text[end:]


def decode_release_note_scalar(scalar: str) -> str:
    lines = [line.strip() for line in scalar.strip().splitlines()]
    folded = " ".join(line for line in lines if line)
    if not folded.startswith('"') or not folded.endswith('"'):
        raise DependencyUpdateError("PackageInfo _releaseNote must use a double-quoted scalar.")
    try:
        value = ast.literal_eval(folded)
    except (SyntaxError, ValueError) as error:
        raise DependencyUpdateError("PackageInfo _releaseNote could not be decoded: {0}".format(error))
    if not isinstance(value, str) or not value.strip():
        raise DependencyUpdateError("PackageInfo _releaseNote must not be empty.")
    return value


def read_release_note(text: str) -> str:
    matches = list(RELEASE_NOTE_PATTERN.finditer(text))
    if len(matches) != 1:
        raise DependencyUpdateError(
            "PackageInfo must contain exactly one _releaseNote before _dependenciesOverride; found {0}.".format(
                len(matches)
            )
        )
    return decode_release_note_scalar(matches[0].group("scalar"))


def replace_release_note(text: str, release_note: str) -> str:
    matches = list(RELEASE_NOTE_PATTERN.finditer(text))
    if len(matches) != 1:
        raise DependencyUpdateError(
            "PackageInfo must contain exactly one _releaseNote before _dependenciesOverride; found {0}.".format(
                len(matches)
            )
        )
    match = matches[0]
    rendered = "{0}_releaseNote: {1}\n".format(
        match.group("indent"), json.dumps(release_note, ensure_ascii=True)
    )
    return text[: match.start()] + rendered + text[match.end() :]


def build_release_note(
    package: EmbeddedPackage,
    catalog_version: Optional[SemVer],
    dependency_changes: Sequence[Dict[str, str]],
    package_info_text: str,
) -> str:
    details = ", ".join(
        "{0} {1} -> {2}".format(change["dependencyId"], change["fromVersion"], change["toVersion"])
        for change in dependency_changes
    )
    dependency_bullet = (
        "- package.json의 ActionFit dependency를 최신 준비 버전으로 갱신했습니다: {0}.".format(details)
    )
    if catalog_version is not None and package.version > catalog_version:
        existing = [line.strip() for line in read_release_note(package_info_text).splitlines() if line.strip()]
        if any(not line.startswith("- ") for line in existing) or not 2 <= len(existing) <= 5:
            raise DependencyUpdateError(
                "Unpublished PackageInfo release notes must contain 2-5 bullet lines before dependency notes can be merged."
            )
        return "\n".join(existing + [dependency_bullet])
    return "\n".join(
        [
            dependency_bullet,
            "- dependency 변경과 함께 package version, README Git UPM tag, AI_GUIDE.md version을 정렬해 publish 전 contract 검증이 가능하도록 했습니다.",
            "- 기존 runtime/editor API 구현은 변경하지 않았으며 실제 publish는 별도 승인을 거쳐 Custom Package Manager에서 수행합니다.",
        ]
    )


def preview_mutations(
    repo_root: Path,
    package: EmbeddedPackage,
    new_version: SemVer,
    catalog_version: Optional[SemVer],
    dependency_changes: Sequence[Dict[str, str]],
) -> Dict[Path, bytes]:
    manifest_path = package.root / "package.json"
    readme_path = package.root / "README.md"
    ai_guide_path = package.root / "AI_GUIDE.md"
    package_info_path = package.root / "Editor/PackageInfo/ActionFitPackageInfo_SO.asset"
    required = [manifest_path, readme_path, ai_guide_path, package_info_path]
    missing = [relative(repo_root, path) for path in required if not path.is_file()]
    if missing:
        raise DependencyUpdateError("Required release metadata is missing: {0}".format(", ".join(missing)))
    linked = []
    for path in required:
        current = path
        while current != package.root:
            if current.is_symlink():
                linked.append(relative(repo_root, current))
                break
            current = current.parent
    if linked:
        raise DependencyUpdateError(
            "Release metadata paths must not contain symbolic links: {0}".format(", ".join(sorted(linked)))
        )

    manifest = dict(package.manifest)
    dependencies = dict(package.dependencies)
    for change in dependency_changes:
        dependencies[change["dependencyId"]] = change["toVersion"]
    manifest["version"] = str(new_version)
    manifest["dependencies"] = dependencies
    manifest_text = json.dumps(manifest, ensure_ascii=False, indent=2) + "\n"

    readme_text = readme_path.read_text(encoding="utf-8")
    ai_guide_text = ai_guide_path.read_text(encoding="utf-8")
    package_info_text = package_info_path.read_text(encoding="utf-8")
    release_note = build_release_note(package, catalog_version, dependency_changes, package_info_text)
    return {
        manifest_path: manifest_text.encode("utf-8"),
        readme_path: replace_readme_version(readme_text, package.package_id, str(new_version)).encode("utf-8"),
        ai_guide_path: replace_ai_guide_version(ai_guide_text, str(new_version)).encode("utf-8"),
        package_info_path: replace_release_note(package_info_text, release_note).encode("utf-8"),
    }


def dependency_layers(
    nodes: Set[str],
    packages: Dict[str, EmbeddedPackage],
) -> Tuple[List[List[str]], List[str]]:
    indegree = {node: 0 for node in nodes}
    dependents: Dict[str, Set[str]] = {node: set() for node in nodes}
    for consumer_id in nodes:
        package = packages.get(consumer_id)
        if package is None:
            continue
        for dependency_id in package.dependencies:
            if dependency_id not in nodes:
                continue
            if consumer_id not in dependents[dependency_id]:
                dependents[dependency_id].add(consumer_id)
                indegree[consumer_id] += 1

    remaining = set(nodes)
    layers: List[List[str]] = []
    while remaining:
        layer = sorted(node for node in remaining if indegree[node] == 0)
        if not layer:
            return layers, sorted(remaining)
        layers.append(layer)
        for node in layer:
            remaining.remove(node)
            for dependent in dependents[node]:
                indegree[dependent] -= 1
    return layers, []


def build_plan(
    repo_root: Path,
    catalog_path: Path,
    catalog_refreshed: bool,
    allow_major: bool,
) -> PlanBuild:
    repo_root = repo_root.resolve()
    catalog_path = catalog_path.resolve()
    catalog_latest, catalog_warnings, catalog_conflicts = load_catalog(catalog_path)
    packages, package_conflicts = load_embedded_packages(repo_root)
    conflicts = list(catalog_conflicts) + package_conflicts
    warnings = list(catalog_warnings)
    overrides = load_project_overrides(repo_root)
    for package_id in sorted(overrides & set(packages)):
        add_unique(
            conflicts,
            issue(
                "PROJECT_OVERRIDE_BLOCKED",
                "Project override packages cannot participate in upstream dependency automation.",
                package_id,
                relative(repo_root, packages[package_id].root / "package.json"),
            ),
        )

    target_versions: Dict[str, SemVer] = dict(catalog_latest)
    for package_id, package in packages.items():
        catalog_version = catalog_latest.get(package_id)
        if catalog_version is None or package.version > catalog_version:
            target_versions[package_id] = package.version
            if catalog_version is None:
                add_unique(
                    warnings,
                    issue(
                        "EMBEDDED_PACKAGE_NOT_IN_CATALOG",
                        "Used the physical embedded version because no latest Catalog row exists.",
                        package_id,
                        relative(repo_root, package.root / "package.json"),
                    ),
                )

    new_versions: Dict[str, SemVer] = {}
    changed = True
    while changed:
        changed = False
        final_targets = dict(target_versions)
        final_targets.update(new_versions)
        for package_id in sorted(packages):
            package = packages[package_id]
            if package_id in overrides:
                add_unique(
                    warnings,
                    issue(
                        "PROJECT_OVERRIDE_SKIPPED",
                        "Project overrides are not eligible for upstream dependency release preparation.",
                        package_id,
                    ),
                )
                continue
            for dependency_id, declared_text in sorted(package.dependencies.items()):
                if not dependency_id.startswith(ACTIONFIT_PREFIX):
                    continue
                try:
                    declared = SemVer.parse(declared_text)
                except ValueError:
                    add_unique(
                        conflicts,
                        issue(
                            "ACTIONFIT_DEPENDENCY_VERSION_INVALID",
                            "ActionFit dependency must use exact SemVer: {0}.".format(declared_text),
                            package_id,
                            relative(repo_root, package.root / "package.json"),
                        ),
                    )
                    continue
                if dependency_id not in packages:
                    add_unique(
                        conflicts,
                        issue(
                            "ACTIONFIT_DEPENDENCY_NOT_EMBEDDED",
                            "ActionFit dependency must have a physical top-level embedded package: {0}.".format(
                                dependency_id
                            ),
                            package_id,
                            relative(repo_root, package.root / "package.json"),
                        ),
                    )
                    continue
                target = final_targets.get(dependency_id)
                if target is None:
                    add_unique(
                        conflicts,
                        issue(
                            "ACTIONFIT_DEPENDENCY_TARGET_UNKNOWN",
                            "No latest Catalog or physical embedded version is available for {0}.".format(
                                dependency_id
                            ),
                            package_id,
                            relative(repo_root, package.root / "package.json"),
                        ),
                    )
                    continue
                if declared > target:
                    add_unique(
                        warnings,
                        issue(
                            "DEPENDENCY_NEWER_THAN_TARGET_PRESERVED",
                            "Preserved newer dependency {0}; resolved target is {1}.".format(declared, target),
                            package_id,
                            relative(repo_root, package.root / "package.json"),
                        ),
                    )
                    continue
                if declared == target:
                    continue
                if declared.major != target.major and not allow_major:
                    add_unique(
                        conflicts,
                        issue(
                            "MAJOR_UPDATE_APPROVAL_REQUIRED",
                            "Dependency {0} requires major update {1} -> {2}; rerun plan with --allow-major after explicit approval.".format(
                                dependency_id, declared, target
                            ),
                            package_id,
                            relative(repo_root, package.root / "package.json"),
                        ),
                    )
                    continue
                if package_id not in new_versions:
                    package_catalog = catalog_latest.get(package_id)
                    if package_catalog is not None and package.version < package_catalog:
                        add_unique(
                            conflicts,
                            issue(
                                "EMBEDDED_PACKAGE_BEHIND_CATALOG",
                                "Refresh or re-embed the package before preparing a release; local {0} is behind Catalog {1}.".format(
                                    package.version, package_catalog
                                ),
                                package_id,
                                relative(repo_root, package.root / "package.json"),
                            ),
                        )
                        continue
                    base = higher(package.version, package_catalog)
                    new_versions[package_id] = base.bump_patch()
                    changed = True

    final_targets = dict(target_versions)
    final_targets.update(new_versions)
    dependency_changes_by_package: Dict[str, List[Dict[str, str]]] = {}
    prerequisites: Set[str] = set()
    for package_id in sorted(new_versions):
        package = packages[package_id]
        changes_for_package: List[Dict[str, str]] = []
        for dependency_id, declared_text in sorted(package.dependencies.items()):
            if not dependency_id.startswith(ACTIONFIT_PREFIX):
                continue
            try:
                declared = SemVer.parse(declared_text)
            except ValueError:
                continue
            if dependency_id not in packages:
                continue
            target = final_targets.get(dependency_id)
            if target is None or declared >= target:
                continue
            changes_for_package.append(
                {
                    "dependencyId": dependency_id,
                    "fromVersion": str(declared),
                    "toVersion": str(target),
                }
            )
            dependency_package = packages.get(dependency_id)
            dependency_catalog = catalog_latest.get(dependency_id)
            if (
                dependency_id not in new_versions
                and dependency_package is not None
                and (dependency_catalog is None or dependency_package.version > dependency_catalog)
            ):
                prerequisites.add(dependency_id)
        dependency_changes_by_package[package_id] = changes_for_package

    publish_nodes = set(new_versions) | prerequisites
    layers, cycle_nodes = dependency_layers(publish_nodes, packages)
    if cycle_nodes:
        add_unique(
            conflicts,
            issue(
                "DEPENDENCY_CYCLE_BLOCKED",
                "Dependency-safe publish order could not be computed for: {0}.".format(", ".join(cycle_nodes)),
            ),
        )

    mutations: Dict[Path, bytes] = {}
    package_results: List[Dict[str, Any]] = []
    for package_id in sorted(new_versions):
        package = packages[package_id]
        dependency_changes = dependency_changes_by_package[package_id]
        try:
            package_mutations = preview_mutations(
                repo_root,
                package,
                new_versions[package_id],
                catalog_latest.get(package_id),
                dependency_changes,
            )
        except (DependencyUpdateError, OSError, UnicodeError) as error:
            add_unique(
                conflicts,
                issue(
                    "RELEASE_METADATA_CONFLICT",
                    str(error),
                    package_id,
                    relative(repo_root, package.root),
                ),
            )
            package_mutations = {}
        mutations.update(package_mutations)
        input_hashes = {
            relative(repo_root, path): sha256_file(path)
            for path in sorted(package_mutations, key=lambda item: item.as_posix())
        }
        output_hashes = {
            relative(repo_root, path): sha256_bytes(content)
            for path, content in sorted(package_mutations.items(), key=lambda item: item[0].as_posix())
        }
        package_results.append(
            {
                "packageId": package_id,
                "currentVersion": str(package.version),
                "catalogVersion": str(catalog_latest[package_id]) if package_id in catalog_latest else "",
                "newVersion": str(new_versions[package_id]),
                "dependencyChanges": dependency_changes,
                "inputHashes": input_hashes,
                "outputHashes": output_hashes,
            }
        )

    graph_hashes = {
        relative(repo_root, package.root / "package.json"): sha256_file(package.root / "package.json")
        for package in packages.values()
    }
    override_path = repo_root / "ProjectSettings/ActionFitPackageOverrides.json"
    plan_payload = {
        "schemaVersion": SCHEMA_VERSION,
        "catalogHash": sha256_file(catalog_path),
        "catalogRefreshed": catalog_refreshed,
        "allowMajor": allow_major,
        "packageInventoryHash": embedded_inventory_hash(repo_root),
        "graphHashes": dict(sorted(graph_hashes.items())),
        "overrideHash": sha256_file(override_path) if override_path.is_file() else "",
        "packages": package_results,
        "publishPrerequisitePackageIds": sorted(prerequisites),
        "publishLayers": layers,
    }
    plan_id = canonical_hash(plan_payload)
    success = not conflicts
    if not package_results and success:
        code = "NO_CHANGES"
        message = "All exact ActionFit dependency declarations already match or exceed their resolved targets."
    elif success and catalog_refreshed:
        code = "READY_TO_APPLY"
        message = "Dependency update plan is ready to apply after exact approval."
    elif success:
        code = "READY_PLAN_ONLY"
        message = "Plan is ready for review, but apply is blocked until Catalog refresh is confirmed."
    else:
        code = "PLAN_CONFLICT"
        message = "Dependency update planning found conflicts; no files were changed."

    required_approval = (
        "UPDATE ACTIONFIT DEPENDENCIES {0} PACKAGES PLAN {1}".format(len(package_results), plan_id)
        if package_results and success and catalog_refreshed
        else ""
    )
    result = {
        "schemaVersion": SCHEMA_VERSION,
        "success": success,
        "code": code,
        "message": message,
        "mode": "plan",
        "repoRoot": str(repo_root),
        "catalogPath": relative(repo_root, catalog_path),
        "catalogUpdatedUtc": datetime.fromtimestamp(catalog_path.stat().st_mtime, timezone.utc).isoformat(),
        "catalogHash": plan_payload["catalogHash"],
        "catalogRefreshed": catalog_refreshed,
        "applyAllowed": success and bool(package_results) and catalog_refreshed,
        "allowMajor": allow_major,
        "planId": plan_id,
        "requiredApprovalText": required_approval,
        "packages": package_results,
        "publishPrerequisitePackageIds": sorted(prerequisites),
        "publishPackageIds": sorted(publish_nodes),
        "publishLayers": layers,
        "warnings": sorted(warnings, key=lambda value: (value["code"], value["packageId"], value["path"])),
        "conflicts": sorted(conflicts, key=lambda value: (value["code"], value["packageId"], value["path"])),
        "summary": {
            "embeddedPackages": len(packages),
            "changedPackages": len(package_results),
            "dependencyChanges": sum(len(item["dependencyChanges"]) for item in package_results),
            "publishPackages": len(publish_nodes),
            "publishLayers": len(layers),
        },
    }
    input_hashes = {catalog_path: sha256_file(catalog_path)}
    input_hashes.update(
        {
            package.root / "package.json": graph_hashes[
                relative(repo_root, package.root / "package.json")
            ]
            for package in packages.values()
        }
    )
    input_hashes[override_path] = plan_payload["overrideHash"]
    input_hashes.update({path: sha256_file(path) for path in mutations})
    return PlanBuild(result, mutations, input_hashes, plan_payload["packageInventoryHash"])


def atomic_write_bytes(path: Path, content: bytes) -> None:
    mode = path.stat().st_mode
    temporary_path: Optional[Path] = None
    try:
        with tempfile.NamedTemporaryFile(prefix=".{0}.".format(path.name), suffix=".tmp", dir=str(path.parent), delete=False) as handle:
            temporary_path = Path(handle.name)
            handle.write(content)
            handle.flush()
            os.fsync(handle.fileno())
        os.chmod(str(temporary_path), mode)
        os.replace(str(temporary_path), str(path))
    finally:
        if temporary_path is not None and temporary_path.exists():
            temporary_path.unlink()


def run_contract_validation(repo_root: Path, package_id: str) -> Tuple[bool, Dict[str, Any]]:
    validator = Path(__file__).resolve().with_name("package_contract_validator.py")
    if not validator.is_file():
        return False, {"code": "VALIDATOR_MISSING", "message": "Package contract validator was not found."}
    completed = subprocess.run(
        [sys.executable, str(validator), "--repo-root", str(repo_root), "--package", package_id],
        check=False,
        stdout=subprocess.PIPE,
        stderr=subprocess.PIPE,
        text=True,
        encoding="utf-8",
    )
    try:
        payload = json.loads(completed.stdout) if completed.stdout else {}
    except json.JSONDecodeError:
        payload = {"message": completed.stderr.strip() or "Validator returned invalid JSON."}
    return completed.returncode == 0 and bool(payload.get("success")), payload


def apply_plan(
    plan: PlanBuild,
    expected_plan_id: str,
    approval: str,
    validator: Callable[[Path, str], Tuple[bool, Dict[str, Any]]] = run_contract_validation,
) -> Dict[str, Any]:
    result = dict(plan.result)
    result["mode"] = "apply"
    if not result.get("success"):
        result.update(code="PLAN_NOT_APPLICABLE", message="Conflicted plans cannot be applied.", applied=False)
        return result
    if not result.get("applyAllowed"):
        result.update(
            success=False,
            code="CATALOG_REFRESH_REQUIRED",
            message="Apply requires a plan created after a confirmed Catalog refresh.",
            applied=False,
        )
        return result
    if expected_plan_id != result.get("planId"):
        result.update(
            success=False,
            code="PLAN_ID_MISMATCH",
            message="Expected plan ID does not match the current content-bound plan.",
            applied=False,
        )
        return result
    if approval != result.get("requiredApprovalText"):
        result.update(
            success=False,
            code="APPROVAL_TEXT_MISMATCH",
            message="Approval text must exactly match the current plan.",
            applied=False,
        )
        return result

    stale_paths = []
    if embedded_inventory_hash(Path(result["repoRoot"])) != plan.package_inventory_hash:
        stale_paths.append("Packages/com.actionfit.*")
    for path, expected_hash in plan.input_hashes.items():
        try:
            current_hash = sha256_file(path)
        except OSError:
            current_hash = ""
        if current_hash != expected_hash:
            stale_paths.append(relative(Path(result["repoRoot"]), path))
    if stale_paths:
        result.update(
            success=False,
            code="PLAN_CONTENT_CHANGED",
            message="Plan inputs changed after planning; rebuild the plan and request fresh approval.",
            applied=False,
            changedPaths=[],
            stalePaths=sorted(stale_paths),
        )
        return result

    originals = {path: path.read_bytes() for path in plan.mutations}
    changed_paths: List[str] = []
    validations: List[Dict[str, Any]] = []
    try:
        for path, content in sorted(plan.mutations.items(), key=lambda item: item[0].as_posix()):
            if originals[path] == content:
                continue
            atomic_write_bytes(path, content)
            changed_paths.append(relative(Path(result["repoRoot"]), path))
        for package in result["packages"]:
            valid, payload = validator(Path(result["repoRoot"]), package["packageId"])
            validations.append({"packageId": package["packageId"], "success": valid, "result": payload})
            if not valid:
                raise DependencyUpdateError(
                    "Package contract validation failed for {0}.".format(package["packageId"])
                )
    except Exception as error:
        rollback_errors = []
        for path, content in originals.items():
            try:
                atomic_write_bytes(path, content)
            except Exception as rollback_error:
                rollback_errors.append("{0}: {1}".format(path, rollback_error))
        result.update(
            success=False,
            code="ROLLBACK_FAILED" if rollback_errors else "APPLY_ROLLED_BACK",
            message=str(error),
            applied=False,
            rolledBack=not rollback_errors,
            rollbackErrors=rollback_errors,
            changedPaths=changed_paths,
            validations=validations,
        )
        return result

    result.update(
        success=True,
        code="APPLIED",
        message="Dependency release metadata was updated and validated. No package was published.",
        applied=True,
        rolledBack=False,
        changedPaths=changed_paths,
        validations=validations,
    )
    return result


def add_common_arguments(parser: argparse.ArgumentParser) -> None:
    parser.add_argument("--repo-root", default=".", help="Repository root containing Packages/.")
    parser.add_argument("--catalog", help="Repository-relative Catalog CSV path.")
    parser.add_argument(
        "--catalog-refreshed",
        action="store_true",
        help="Assert that ActionFitPackageWorkflowApi reported CatalogRefreshed=true immediately before planning.",
    )
    parser.add_argument(
        "--allow-major",
        action="store_true",
        help="Include explicitly approved major dependency upgrades in the content-bound plan.",
    )


def parse_args(arguments: Optional[Sequence[str]]) -> argparse.Namespace:
    parser = JsonArgumentParser(description=__doc__)
    subparsers = parser.add_subparsers(dest="command", required=True)
    plan_parser = subparsers.add_parser("plan", help="Build a deterministic read-only dependency update plan.")
    add_common_arguments(plan_parser)
    apply_parser = subparsers.add_parser("apply", help="Apply an exact approved plan and validate every package.")
    add_common_arguments(apply_parser)
    apply_parser.add_argument("--expected-plan-id", required=True)
    apply_parser.add_argument("--approval", required=True)
    return parser.parse_args(arguments)


def main(arguments: Optional[Sequence[str]] = None) -> int:
    try:
        args = parse_args(arguments)
        repo_root = Path(args.repo_root).resolve()
        catalog_path = resolve_catalog(repo_root, args.catalog)
        plan = build_plan(repo_root, catalog_path, args.catalog_refreshed, args.allow_major)
        result = (
            plan.result
            if args.command == "plan"
            else apply_plan(plan, args.expected_plan_id, args.approval)
        )
        print(json.dumps(result, ensure_ascii=False, indent=2))
        return 0 if result.get("success") else 1
    except DependencyUpdateError as error:
        print(
            json.dumps(
                {
                    "schemaVersion": SCHEMA_VERSION,
                    "success": False,
                    "code": "INFRASTRUCTURE_ERROR",
                    "message": str(error),
                },
                ensure_ascii=False,
                indent=2,
            )
        )
        return 2
    except Exception as error:
        print(
            json.dumps(
                {
                    "schemaVersion": SCHEMA_VERSION,
                    "success": False,
                    "code": "UNEXPECTED_ERROR",
                    "message": str(error),
                },
                ensure_ascii=False,
                indent=2,
            )
        )
        return 2


if __name__ == "__main__":
    raise SystemExit(main())
