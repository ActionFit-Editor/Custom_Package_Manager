---
name: package-manager-help
description: Explain Custom Package Manager, its installed skills, package audit and validation commands, menus, release preparation, and safety boundaries.
---

# Custom Package Manager Help

Answer in the user's language. Explain capabilities without installing, updating, embedding, removing, publishing, or changing project files unless the user separately requests an appropriate write workflow.

1. Read `PACKAGE_SKILLS.md` first. Treat its generated package identity, complete related-skill table, `$skill-name` invocations, descriptions, and access boundaries as authoritative.
2. Read `Packages/com.actionfit.custompackagemanager/README.md` and `Packages/com.actionfit.custompackagemanager/AI_GUIDE.md` when present. If downloaded, resolve `Library/PackageCache/com.actionfit.custompackagemanager@*` without editing it.
3. Explain these distinct capabilities:
   - read-only installed-package and agent-skill inventory through `$package-manager-audit`;
   - Unity-independent package contract validation through `$package-manager-validate` and `Tools~/package_contract_validator.py`;
   - explicit Unity menu workflows for install/update/remove, Embed for Edit, skill refresh, and manual package publishing.
4. Explain that package source changes require a version bump, README/AI guide/release-note alignment, remote tag checks, contract validation, and manual publishing.
5. State that help, audit, and validation do not refresh the catalog, rewrite `Packages/manifest.json`, modify installed skills, create repositories, push commits or tags, append catalog rows, publish packages, or expose credentials.

List `Package Manager`, `Manager Console`, `Install or Refresh Agent Skills`, `Remove Managed Agent Skills`, `Add Agent Skill`, `Setting SO`, and `README` under `Tools > Package > Custom Package Manager`. Recommend the installed README and AI guide for exact current behavior rather than reconstructing a stale command catalog.
