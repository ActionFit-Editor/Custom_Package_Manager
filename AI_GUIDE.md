# AI Guide - Custom Package Manager

This file is shipped inside the UPM package so an AI assistant in a consuming Unity project can understand the package without access to the source project's `Docs/AI` folder.

## Package Identity

- Package ID: `com.actionfit.custompackagemanager`
- Display name: Custom Package Manager
- Repository: `https://github.com/ActionFit-Editor/Custom_Package_Manager.git`
- Current package version at generation time: `1.1.55`
- Unity version: `6000.2`

## Purpose

Custom Package Manager manages ActionFit UPM catalog install/update/remove/publish workflows. Use `README.md`, `package.json`, package source files, and `Editor/PackageInfo/ActionFitPackageInfo_SO.asset` together to understand the user-facing workflow and catalog metadata.

## Project Router Registration

This package should be listed in `Packages/com.actionfit.custompackagemanager/PACKAGE_AI_GUIDE_ROUTER.md`.

Requested router entry:

- `Packages/com.actionfit.custompackagemanager/AI_GUIDE.md` - Custom Package Manager manages ActionFit UPM catalog install/update/remove/publish workflows. Read when changing package install state, manifest writes, catalog rows, history/changelog UI, AI guide routing, or publishing tools.

If the router file is not already included in the AI assistant's default reading sequence, the router file is responsible for asking the user to link it from the project's primary AI markdown entry point. Prefer an existing `PROJECT.md` wherever the project keeps it, otherwise use `AGENTS.md`, `CLAUDE.md`, `GEMINI.md`, or another primary AI markdown entry point.

Read this file when:

- changing files under `Packages/com.actionfit.custompackagemanager/`
- diagnosing `Custom Package Manager` behavior in a consuming project
- preparing a release for `com.actionfit.custompackagemanager`
- editing package metadata, README, AI guide, package version, or release notes

## Main Files

- `Editor/Scripts/ActionFitPackageManagerWindow.cs`: package list, install/apply/remove/check-update/force-update/history UI.
- `Editor/Scripts/*PackageMenu.cs`: package-owned Unity top-menu `README` and optional `Setting SO` entries. These entries live in each package, not in Custom Package Manager.
- `Editor/Scripts/ActionFitPackageManagerConsoleWindow.cs`: operational console for create/repo/publish/catalog/manifest/router actions.
- `Editor/Scripts/ActionFitPackageCatalogSettings_SO.cs`: spreadsheet config, one GitHub publish token, public/private repo creation org profiles, and publish cache root.
- `Editor/Scripts/ActionFitPackageInfoUtility.cs`: package skeleton creation and PackageInfo/README/AI_GUIDE generation.
- `Editor/Scripts/ActionFitPackagePublishWindow.cs`: publish target scan and publish UI.
- `Editor/Scripts/ActionFitPackagePublisher.cs`: GitHub repository check, local publish clone preparation, remote `git push`, tag push, single/batch catalog upsert, and publish step logging.
- `Editor/Scripts/ActionFitPackageCatalogUpdater.cs`: spreadsheet/web-app catalog download.
- `Editor/Scripts/ActionFitPackageCommunityClient.cs`: anonymous project vote ID, package vote/comment Web App requests, and local vote state.
- `Editor/Scripts/ActionFitPackageAiGuideRouter.cs`: scans embedded and Git UPM package `AI_GUIDE.md` files, syncs `PACKAGE_AI_GUIDE_ROUTER.md`, and connects discovered AI entry points through adapter-style helpers.
- `Editor/Documentation/PackageCommunityWebAppContract.md`: required spreadsheet sheets and Web App actions for package votes, comments, and batch catalog publish confirmation.
- `Editor/PackageInfo/ActionFitPackageInfo_SO.asset`: catalog metadata source for this package.
- `PACKAGE_AI_GUIDE_ROUTER.md`: package-shipped AI router for choosing which package `AI_GUIDE.md` to read for a task.

## Required Reading For AI

- Read this `AI_GUIDE.md` before changing, diagnosing, or explaining this package.
- Read `Packages/com.actionfit.custompackagemanager/PACKAGE_AI_GUIDE_ROUTER.md` when deciding which installed ActionFit package `AI_GUIDE.md` applies to a task.
- Read `README.md` for human-facing setup and usage.
- Read `package.json` for package ID, version, Unity version, and dependencies.
- Read `Editor/PackageInfo/ActionFitPackageInfo_SO.asset` for catalog metadata, repository name, owner, status, description, release note, and dependency override.

## Editing Rules

- Keep changes scoped to this package unless the user explicitly asks for cross-package edits.
- Do not change package IDs, repository names, public menu paths, serialized field names, or package assembly names casually; these can affect installed projects.
- Preserve Unity `.meta` files when adding, moving, or renaming files inside the package.
- When behavior changes, update this `AI_GUIDE.md` in the same package before publishing so consuming projects receive the latest AI context.
- Keep `README.md` focused on human usage. Keep this file focused on AI-facing architecture, constraints, migration notes, and package-specific editing rules.

## Menu And Catalog Notes

- Main menu: `Tools/Package/Custom Package Manager/Package Manager`.
- Manager Console menu: `Tools/Package/Custom Package Manager/Manager Console`.
- Local catalog path: `Assets/_Data/_CustomPackageManager/package_catalog.csv`.
- Fallback catalog path: `Packages/com.actionfit.custompackagemanager/Editor/Catalog/package_catalog.csv`.
- Package Manager reads the local catalog when present, otherwise the embedded package catalog.
- It manages internal UPM package install/update/remove, repository creation, changelog/history display, AI guide routing, and manual publish flows.
- Manager Console exposes `1. Create Package`, `2. Publish Changed`, `Publish Package`, catalog/manifest access, and AI guide router refresh. Package README and settings SO access should stay in Unity top-menu package entries such as `Tools/Package/<Package Name>/README` and `Tools/Package/<Package Name>/Setting SO`, not in `Project Files` or Package Manager package rows.
- Each package's `ActionFitPackageInfo_SO` stores `Repository Visibility`. Publish flows use that package-local value to choose the public/private GitHub profile for both new and already registered packages, so `Publish All Changed` can safely publish mixed public/private packages in one run.
- `Publish All Changed` must create publish request snapshots on the Unity main thread, run only repository publish work in parallel with `ActionFitPackagePublisher.DefaultMaxParallelPublishes`, and append catalog rows only after all repository publishes succeed.
- Bulk catalog append should call Web App action `upsertPackageVersions` first. The Web App must return either `count == item count` or per-item confirmations; otherwise the client treats batch append as unsupported and falls back to serial `upsertPackageVersion`.
- If repository publish succeeds but catalog append fails, keep the successful catalog append items in the publish window and expose `Retry Catalog Append` so the user can update the spreadsheet without pushing repositories again.
- Package section classification should treat Git/registry dependencies in `Packages/manifest.json` as Downloaded Packages. Only local `file:` dependencies or package folders under `Packages/` without a manifest dependency should be treated as Embedded Packages.
- The `Check Update` panel must include only installed packages whose catalog latest version is higher than the installed version. Do not treat any version difference as an update, because that can downgrade packages such as `1.0.30 -> 1.0.29`.
- `Force Update` must run catalog refresh first, show a confirmation list of downloaded packages, and then re-apply catalog latest Git UPM URLs/dependencies for downloaded packages only. Embedded packages are skipped so local edits are not deleted.
- `Latest Git` buttons in package details and the `Check Update` panel should open the catalog latest version's GitHub tag URL in the browser without modifying `Packages/manifest.json`.
- Each ActionFit package should be reachable from Unity's top `Tools/Package` menu for docs/config access. `README` menu items should open the installed package README from `Packages/` or `Library/PackageCache`. Packages with shared settings SOs should expose a `Setting SO` menu item that selects and pings the known settings asset. Keep this code in each package's own `Editor/Scripts/*PackageMenu.cs`, using that package's known asset path or safe factory method.
- Downloaded packages may be converted to editable embedded packages through `Embed for Edit`. The conversion should copy the resolved package source into a temporary folder, validate that the copied `package.json` exists and has the expected package `name`, move it into `Packages/<packageId>/`, write `file:<packageId>` in `Packages/manifest.json`, preserve catalog repository metadata in PackageInfo for publishing back to the existing package repository, refresh package AI routing, and run Package Manager resolve without requiring publish credentials. Do not write a local `file:` dependency unless the target local package folder has a valid `package.json`; Unity Package Manager will fail project resolve when manifest points at a missing local package.
- If `Packages/<packageId>/` already exists during `Embed for Edit`, validate its `package.json` name and let the user use that existing folder by writing the local `file:<packageId>` dependency. Do not overwrite the local folder.
- Downloaded packages may also be copied through `Fork as New` when the user wants a new `com.actionfit.*` package ID and a new repository instead of publishing edits back to the source package repository. This flow must rewrite the copied package's `package.json` metadata, create PackageInfo metadata for the new repository, remove the original manifest dependency, and write the new local `file:<newPackageId>` dependency so duplicate source/fork assemblies are not compiled together.
- Embedded packages may be returned to the downloaded flow through `Use Downloaded`, which writes the selected catalog Git UPM dependency, removes the local package folder, and runs Package Manager resolve.
- Catalog `dependencies` are applied through the same manifest-writing path for install/apply, selected updates, and `Use Downloaded`. `com.actionfit.*` dependencies must resolve to catalog rows so the manager writes Git UPM URLs; registry dependencies outside ActionFit may fall back to their raw version string.
- Expanded package rows should show dependency details for the selected version, including resolved catalog Git URLs for ActionFit dependencies and raw registry version values for non-ActionFit dependencies.
- `ActionFitPackageCatalogUpdater` must parse CSV by records, not by raw lines, because release notes can contain quoted newlines. Otherwise `dependencies` and later columns shift and package details incorrectly show `None`.
- `Embed for Edit` does not publish by itself. After editing, the package `package.json` version must be bumped above the catalog latest version before `Publish Changed` will pick it up.
- Publish flows must refresh the catalog immediately before uploading and block when the refreshed catalog already contains the same `package_id@version`. The default policy is never to overwrite an existing Git tag or catalog version row. Tell users to bump `package.json`/`Publish Version`, or use `Fork as New` when they need a separate package/repository.
- Package sections should sort by community score (`likes - dislikes`) descending, then likes, comments, and display name. Keep `Package Manager` separate from normal catalog sections.
- Package community feedback uses an anonymous project ID stored under `UserSettings/ActionFitPackageManager/`, not a user identity. Do not move this ID into committed project settings.
- Package community voting allows only one final `Like` or `Dislike` per anonymous project ID and package. Do not re-enable switching between vote types without updating the Web App contract.
- The Web App must support `votePackage`, `upsertPackageComment`, and returning `package_comments` during `Update Catalog` for community feedback to persist and display without per-package comment fetches. For faster bulk publish, it should also support `upsertPackageVersions`. When editing these features, keep `Editor/Documentation/PackageCommunityWebAppContract.md` aligned with client requests and expected responses.
- `Update Catalog` should continue working if the Web App does not return `package_vote_summary`; community counts default to zero.

## Changelog And History Rules

- `ActionFitPackageInfo_SO.ReleaseNote` must contain only the single version being prepared.
- Do not copy older changelog entries into the newest release note.
- `History` and `Changes` compose multi-version update descriptions at display time from separate catalog version rows.
- Release notes do not need headings such as `## 1.1.29`; the UI already renders the version label.
- Catalog CSV reading supports quoted multiline changelog fields. Do not rely on unquoted commas or raw newlines in catalog rows.

## AI Guide Distribution Rule

- Every ActionFit package should include an `AI_GUIDE.md` at the package root.
- New packages created by Custom Package Manager should generate `AI_GUIDE.md` alongside `README.md`.
- Package `AI_GUIDE.md` files request their router entry through `Project Router Registration`.
- `ActionFitPackageAiGuideRouter` reads each package's `Requested router entry` and automatically refreshes the `Package Guide Entries` section in `PACKAGE_AI_GUIDE_ROUTER.md`.
- `PACKAGE_AI_GUIDE_ROUTER.md` keeps the short package-to-task routing table and asks to be linked from the project's AI default reading sequence when needed.
- `README.md` is for humans; `AI_GUIDE.md` is for AI assistants working inside consuming projects.
- Custom Package Manager should scan installed package guides from both embedded `Packages/com.actionfit.*` folders and Git UPM `Library/PackageCache/com.actionfit.*@*` folders, refresh `PACKAGE_AI_GUIDE_ROUTER.md`, then link the package router from an existing project AI entry point when one can be found.
- AI entry point registration should be routed through small adapter-style helpers so additional tools can be supported without duplicating package guide scan or router-write logic.
- Router entries must point to the actual discovered `AI_GUIDE.md` path. Embedded packages use `Packages/com.actionfit.*/AI_GUIDE.md`; Git UPM packages use `Library/PackageCache/com.actionfit.*@hash/AI_GUIDE.md`.
- AI entry point discovery should prefer known project router paths (`Docs/AI/PROJECT.md`, `PROJECT.md`), then a single unambiguous `PROJECT.md` found elsewhere in the project, then fallback files such as `AGENTS.md`, `CLAUDE.md`, or `GEMINI.md`.
- If multiple non-standard `PROJECT.md` files exist, do not choose one silently. Use a known fallback AI entry point if present; otherwise leave the package router refreshed and let the assistant ask the user where to register it.
- Project-level compatibility pointer generation should be placed next to the discovered AI entry point, not hard-coded to `Docs/AI`.

## UPM Package Release Preparation

When files under `Packages/com.actionfit.*/` embedded packages are modified, prepare the package for release at task completion, but do not publish unless the user explicitly asks.

1. Report the modified package ID and key changed files, and state that the user must run publish manually.
2. Bump the package `package.json` version. Default to a patch bump unless the user asks otherwise or the compatibility impact clearly requires minor/major.
3. Before choosing or reusing a version, check the package's remote Git tags for the exact version and the next patch candidate.
4. Treat any version that already has a remote tag as immutable, even if local package files still show that version.
5. If a package version has already been tagged and any further code, README, PackageInfo, catalog, or release-note change is made, bump to the next unused patch version before finishing.
6. Do not infer publish status from local files or memory. Re-check remote tags after each follow-up change that affects the package release.
7. Treat each package's `Editor/PackageInfo/ActionFitPackageInfo_SO.asset` as the source of package metadata, repository name, description, and release note.
8. Before publish, check the package `README.md` that will be pushed to Git. Update it when version, usage, menu paths, settings, cautions, or behavior no longer match.
9. Update the package root `AI_GUIDE.md` whenever behavior, workflow, migration, public menu paths, serialized data, release process, or AI-facing rules change.
10. Unless the user explicitly asks, do not run `publish_upm.sh`, `Publish Changed`, `Publish Package`, GitHub push/tag, or catalog upsert.

In the final report, state which remote tag check was performed and which version is the next unpublished release candidate.

## Package Changelog Rules

When updating a package version, write PackageInfo release notes using these standards:

- PackageInfo release notes must be written in Korean so planners and developers can read package patch notes directly. Keep code identifiers, package IDs, menu paths, config keys, and file paths in their original spelling.
- PackageInfo release notes must describe only the single version being prepared. Do not prepend or append older version notes to the same PackageInfo release-note field.
- Version history and update-range summaries must be composed by the Package Manager UI from separate catalog version rows, not by accumulating old changelogs into the newest package release note.
- Do not add a version heading such as `## 1.1.29` inside PackageInfo release notes unless the package UI explicitly requires it. The package/version row already provides the version label.
- Do not stop at a one-line summary. Write 3-6 bullets so the user can judge impact before manual publish.
- Each bullet should include both what changed and the user-visible effect.
- Do not only list file names or class names. When helpful, keep user-checkable identifiers unchanged, such as menu paths, settings SOs, catalog names, manifests, or public API names.
- For bug fixes, include the reproduction symptom and the corrected behavior.
- For new features, describe the newly possible workflow and required setup/prerequisites.
- If compatibility or migration is affected, add a separate `Caution:` bullet.
- If the change is internal refactoring with little user impact, explicitly state that user behavior is unchanged.

## ActionFit Package Catalog Rules

- The `description` and `changelog` fields in `Packages/com.actionfit.custompackagemanager/Editor/Catalog/package_catalog.csv` and the linked Google Spreadsheet tabs `package_catalog` / `package_versions` must be written in Korean so planners and developers can read them directly.
- Do not manually add a new package version row to `Assets/_Data/_CustomPackageManager/package_catalog.csv`, the embedded catalog CSV, or the linked Google Spreadsheet before the user has actually published that version. Keep the catalog latest row at the latest already-published version so `Publish Changed` can detect the new local `package.json` version. The new version row must be created by the user's manual Custom Package Manager publish/catalog append flow after the package repository push and tag succeed.
- When adding a new package version row, write the package role in `description` and write Korean bullet changelog entries in `changelog` using the Package Changelog Rules above. Code identifiers, package IDs, and menu paths may remain unchanged.
- When registering or updating a package through Custom Package Manager, use `1. Create Package` to create the base `Packages/com.actionfit.*` structure and PackageInfo SO.
- `package.json` is the source for `name`, `version`, `unity`, and `dependencies`.
- PackageInfo SO is the source for `repoName`, repository visibility, description, owner, status, and release notes.
- Before package publish, treat that package's `README.md` as user-facing documentation that will be uploaded to GitHub and keep it up to date.
- `2. Publish Changed` handles the normal changed-version publish path and also handles first registration for PackageInfo SOs not yet in the catalog. `Publish All Changed` publishes repositories in parallel and then batch-appends catalog rows, with serial append fallback when the Web App lacks `upsertPackageVersions`. Each package's `Repository Visibility` controls the selected public/private GitHub profile for both existing and newly registered package publishes.
- `Publish Package` prepares the local publish clone for already registered PackageInfo SOs when a manual version field is needed, pushes package contents/tags, and appends catalog rows.
- Creation and publish flow guidance should stay based on this package's README and Manager Console UI.
- For real publishing, use the configured ActionFit GitHub authentication and SSH/HTTPS settings in the local environment. AI may run real publish, push, tag, or catalog upsert only when the user explicitly asks.

## Package Tools Menu

- Unity menu root: `Tools/Package/Custom Package Manager/`.
- Keep package commands under this package root.
- Lower separated entries:
- `Setting SO`: focuses this package's settings ScriptableObject.
- `README`: opens this package README.
- Do not add README or Setting SO access back to Custom Package Manager package rows or Project Files.

## Publish Notes

- Publishing is manual through Custom Package Manager.
- Before reusing a version, check the remote Git tags. Published tags are immutable.
- If this package is modified after a version was tagged, bump to the next unused patch version before publishing.
- The single-package publisher fetches `origin/main`, resets the local publish clone, copies package files, creates a package commit/tag, pushes `main`, pushes the package version tag when it does not already exist remotely, then appends the catalog row.
- `Publish All Changed` uses snapshot-based publish requests so background tasks do not read Unity objects. It publishes repositories in parallel, appends catalog rows by `upsertPackageVersions` after all repository publishes succeed, falls back to serial `upsertPackageVersion` when batch append is unsupported, and keeps retryable catalog rows when append fails after successful repository publish.
- Publish logs should identify each major step: repository check, publish clone path, file copy, commit/tag creation, `main` push, tag push, and catalog append.
- The package repository should include this `AI_GUIDE.md` so other projects can load the AI package context after installing the package.
