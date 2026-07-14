---
name: package-manager-audit
description: Audit installed ActionFit package distribution, metadata, AI guides, and agent-skill registration without refreshing catalogs or changing files.
---

# Audit ActionFit Packages

Keep the audit read-only. Do not fetch, refresh the catalog, install, update, embed, remove, scaffold, publish, rewrite manifests, or synchronize installed skills.

1. Read repository instructions plus the Custom Package Manager `README.md` and `AI_GUIDE.md`.
2. Confirm the repository root and inspect only existing local state:
   - `Packages/manifest.json` and `Packages/packages-lock.json`;
   - physical embedded roots under `Packages/com.actionfit.*`;
   - downloaded roots under `Library/PackageCache/com.actionfit.*@*`;
   - each selected package's `package.json`, `AI_GUIDE.md`, PackageInfo asset, and `Skills~/manifest.json` when present.
3. Classify each selected package as embedded or downloaded from manifest/lock data and physical ownership. Do not treat a Unity-projected logical `Packages/<package-id>` path as proof of a physical embedded folder.
4. For registered skills, report schema version, prefix, help skill, names, agents, access, and whether fixed Codex/Claude source paths exist. Do not inspect or print credential-bearing project settings.
5. If an already-running Unity connector can call `ActionFitPackageWorkflowApi.Inspect`, set `RefreshCatalog = false`. Do not start Unity solely for this audit and do not invoke publish, embed, or scaffold APIs.
6. Report package IDs, versions, distribution, missing metadata/guides/registrations, skill-name conflicts, and recommended follow-up commands. Clearly label stale remote-tracking information because this audit does not fetch.

Do not turn a recommendation into permission to mutate package, Git, catalog, Jira, credential, or installed-skill state.
