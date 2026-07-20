# AI Guide - Custom Package Manager

This file is shipped inside the UPM package so an AI assistant in a consuming Unity project can understand the package without access to the source project's `Docs/AI` folder.

## Package Identity

- Package ID: `com.actionfit.custompackagemanager`
- Display name: Custom Package Manager
- Repository: `https://github.com/ActionFit-Editor/Custom_Package_Manager.git`
- Current package version at generation time: `1.1.113`
- Unity version: `6000.2`

## Purpose

### Settings SO Lifecycle And Package Template

- `ActionFitPackageCatalogSettings_SO` is registered as `EditorOnly` with canonical path `Assets/_Data/_CustomPackageManager/ActionFitPackageCatalogSettings_SO.asset`.
- Package creation now offers `None`, `EditorOnly`, and `RuntimeSingleton` settings modes. `None` preserves the previous template.
- Settings modes generate the registration, direct SO Singleton dependency, matching Runtime/Editor asmdef references, package `Setting SO` menu, lifecycle documentation, and an EditMode registration/path test.
- `RuntimeSingleton` generates `SO_Singleton<Self>` and uses `Assets/_Data/_<Owner>/Resources/SO/<Type>.asset`; `EditorOnly` uses `Assets/_Data/_<Owner>/<Type>.asset`.
- The package contract validator enforces the direct dependency, menu, assembly references, source location, and runtime self-generic base only for explicitly registered settings types.

Custom Package Manager manages ActionFit UPM catalog search, collection classification, direct Git URL install, update/remove, and publish workflows. Use `README.md`, `package.json`, package source files, and `Editor/PackageInfo/ActionFitPackageInfo_SO.asset` together to understand the user-facing workflow and catalog metadata.

## Agent Skills

- `Skills~/manifest.json` registers schema v2 `package-manager-help`, `package-manager-audit`, `package-manager-validate`, and `package-manager-update-dependencies` for Codex and Claude.
- Help, audit, and validate are read-only. The dependency updater is manual-only and write-capable: plan is read-only, apply requires a proven Catalog refresh plus exact content-bound approval, and validation failure rolls back every planned file.
- Help reads generated `PACKAGE_SKILLS.md` as the authoritative inventory. Publishing remains a separate explicit approval through `ActionFitPackageBulkPublishApi`; no skill exposes credentials or implements direct GitHub/Catalog mutation in Python.

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
- `Editor/Scripts/ActionFitPackageManagerInputUtility.cs`: collection classification, unified package/bundle search matching, and credential-safe UPM Git URL validation.
- `Editor/Scripts/ActionFitPackageTransaction.cs`: atomic manifest writes, guarded package-folder transactions, rollback journals, restart recovery, and embedded baselines.
- `Editor/Scripts/ActionFitContentBundleApi.cs`: public bundle planning/install/release APIs, durable ownership state, recovery journals, authorization, and required-package reconciliation.
- `Editor/Scripts/ActionFitContentBundleModels.cs`: public serializable bundle profiles, plans, changes, results, and status DTOs.
- `Editor/Scripts/ActionFitPackageEmbedApi.cs`: public dialog-free Embed for Edit validation/execution APIs, JSON wrapper, and batchmode entry point.
- `Editor/Scripts/ActionFitPackageProjectOverrideApi.cs`: project-owned override state, base/current hash and upstream-version status, restore completion, and publish-exclusion query.
- `Editor/Scripts/ActionFitPackageWorkflowApi.cs`: shared-sheet refresh, installed/latest version comparison, local-change state, and workflow recommendations for AI callers.
- `Editor/Scripts/ActionFitPackagePublishApi.cs`: read-only publish plan preparation, content-bound approval, pre-execution revalidation, structured repository/catalog execution results, JSON wrappers, and batchmode entry points.
- `Editor/Scripts/ActionFitPackageCatalogRecoveryVerifier.cs`: read-only immutable-tag checkout and package-content equivalence checks for catalog-only recovery after retry state is lost.
- `Editor/Scripts/ActionFitPackageBulkPublishApi.cs`: shared AI/UI bulk publish preparation and execution, exact repository-creation approvals, bounded parallel repository publishing, catalog fallback, JSON wrappers, and batchmode entry points.
- `Editor/Scripts/ActionFitPackageRepositoryMigration.cs`: read-only source/target visibility and ref comparison plus separately approved, non-force bidirectional history migration and post-copy verification.
- `Editor/Scripts/ActionFitPackageRepositoryRetirementApi.cs`: public single/batch Keep, Archive, and Delete plans for verified Private sources after Public publication, with fresh Catalog/tag/ref/reference checks and separate exact approval.
- `Editor/Scripts/*PackageMenu.cs`: package-owned Unity top-menu `README` and required `Setting SO` entries when a package owns or bootstraps settings. These entries live in each package, not in Custom Package Manager.
- `Editor/Scripts/ActionFitPackageManagerConsoleWindow.cs`: operational console for create/repo/publish/catalog/manifest/router actions.
- `Editor/Scripts/ActionFitPackageCatalogSettings_SO.cs`: spreadsheet config, one GitHub publish token, public/private repo creation org profiles, and publish cache root.
- `Editor/Scripts/ActionFitPackageInfoUtility.cs`: package skeleton creation and PackageInfo/README/AI_GUIDE/README-only package menu generation.
- `Editor/Scripts/ActionFitSdkInstallProfile.cs` and `ActionFitSdkInstallProfileValidator.cs`: SDK profile schema version 1, immutable source metadata, module graph, detection rules, and deterministic validation.
- `Editor/Scripts/ActionFitSdkInstallPlanning.cs` and `ActionFitSdkInstallApi.cs`: read-only inspection/content-bound planning plus explicit apply/repair/update/remove, ownership state, artifact verification, transaction journaling, rollback, and explicit recovery.
- `Editor/Scripts/ActionFitSdkBridgePackageTemplate.cs`: public, source-only SDK bridge package template with profile, third-party notices, and contract-test assembly.
- `Editor/Scripts/ActionFitSdkProfileWindow.cs`: separate inspect, plan review, execution confirmation, and recovery UI under `Tools/Package/Custom Package Manager/SDK Profiles`.
- `Editor/Scripts/ActionFitPackagePublishWindow.cs`: publish target scan and publish UI.
- `Editor/Scripts/ActionFitPackagePublisher.cs`: GitHub repository check, local publish clone preparation, remote `git push`, tag push, single/batch catalog upsert, and publish step logging.
- `Editor/Scripts/ActionFitPackageCatalogUpdater.cs`: spreadsheet/web-app catalog download.
- `Editor/Scripts/ActionFitPackageCommunityClient.cs`: anonymous project vote ID, package vote/comment Web App requests, and local vote state.
- `Editor/Scripts/ActionFitPackageAiGuideRouter.cs`: scans embedded and Git UPM package `AI_GUIDE.md` files, syncs `PACKAGE_AI_GUIDE_ROUTER.md`, and connects discovered AI entry points through adapter-style helpers.
- `Editor/Scripts/ActionFitPackageSkillInstaller.cs`: discovers package `Skills~/manifest.json` registrations, safely synchronizes project-local Codex and Claude skills, preserves user modifications, and migrates legacy AI Jira ownership state.
- `Editor/Scripts/ActionFitPackageSkillScaffold.cs`: public schema v2 skill-add API plus the embedded-package Editor window; creates the mandatory help skill and Codex metadata without overwriting existing sources.
- `Tools~/package_contract_validator.py`: Unity-independent package contract CLI for package selection, changed-package discovery, SemVer/version-bump checks, metadata/document/asmdef validation, stable diagnostics, and JSON results.
- `Tools~/package_dependency_updater.py`: standard-library planner/applicator for fixed-point embedded ActionFit dependency updates, exact approval, atomic release metadata writes, rollback, and dependency-safe publish layers.
- `Tests/Shell/test-package-dependency-updater.py`: dependency closure, no-downgrade, local-ahead prerequisite, major/cycle blocking, deterministic plan, exact apply, and rollback regression coverage.
- `Tests/Shell/run-tests.sh`: Python fixture regression suite for package contracts and dependency automation.
- `Editor/Documentation/PackageCommunityWebAppContract.md`: required spreadsheet sheets and Web App actions for package votes, comments, and batch catalog publish confirmation.
- `Editor/Documentation/ContentBundleInstallerContract.md` and `Editor/Templates~/ContentBundleInstaller/`: reusable reflection-based, self-removing Git UPM installer contract and starter files.
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
- Keep `README.md` focused on human usage and write it primarily in Korean. Use Korean for headings, setup, usage, configuration, cautions, migration, and release-facing prose while preserving exact package IDs, type/API names, menu paths, commands, configuration keys, file paths, code samples, and externally defined proper names. Keep this file focused on AI-facing architecture, constraints, migration notes, and package-specific editing rules.
- Content bundle profiles must use HTTPS canonical Git URLs pinned to exact version tags. Preserve embedded, local/file, forked, branch, unparseable, newer canonical, shared, and user-modified package values according to the planner result. Keep schema version 1 compatible as an all-package legacy selection. Schema version 2 must assign every package to at least one module, force required modules into every selection, install shared package IDs once, and let optional module changes flow only through a reviewed `PlanModifyModules` / `ModifyModules` transaction.
- Keep bundle ownership at `ProjectSettings/ActionFitContentBundles.json` and transaction journals under `UserSettings/ActionFitPackageManager/ContentBundleTransactions`. Never delete a pending journal before the manifest and ownership state are verified.
- A bootstrap dependency may be removed only after every required package is registered and ownership state reloads successfully. Keep mutation disabled in batchmode.
- Required package reconciliation may restore only missing owned values. It must report conflicts instead of overwriting a user-managed value.
- Keep project override ownership at `ProjectSettings/ActionFitPackageOverrides.json`. Accept only a PackageInfo-declared Public package with a credential-free HTTPS Git dependency; store its public base repository URL, version/revision/content hash, and project-relative package path. Do not store absolute machine paths, credentials, or private remotes, and do not emit the remote URL into generated AI state. Exclude registered overrides from individual and automatic bulk upstream publishing, and require explicit restore-to-base completion or a new package ID/repository fork.
- Release authorization uses only the safe GitHub CLI login string and a profile allowlist. Never read, store, or log credentials, tokens, or raw authentication errors.
- Dependency automation must scan only physical top-level embedded ActionFit packages and must exclude project overrides, links, downloaded packages, and nested fixtures. Use Catalog/local maximum versions without downgrades, fixed-point consumer bumps, explicit major opt-in, and cycle blocking.
- Treat `package_dependency_updater.py plan` as read-only. Apply requires a successful Catalog refresh assertion, exact current `planId`, and exact approval text; update only package manifest/version documentation/PackageInfo release notes atomically and roll all planned files back when contract validation fails.
- Applying dependency metadata never authorizes package publication. Prepare and execute each dependency-safe publish layer only through the existing `ActionFitPackageBulkPublishApi` with a new exact approval, and stop the sequence on the first failure.

## Menu And Catalog Notes

- Main menu: `Tools/Package/Custom Package Manager/Package Manager`.
- SDK profile menu: `Tools/Package/Custom Package Manager/SDK Profiles`.
- Manager Console menu: `Tools/Package/Custom Package Manager/Manager Console`.
- Local catalog path: `Assets/_Data/_CustomPackageManager/package_catalog.csv`.
- Fallback catalog path: `Packages/com.actionfit.custompackagemanager/Editor/Catalog/package_catalog.csv`.
- Package Manager reads the local catalog when present, otherwise the embedded package catalog.
- Package Manager rendering and scrolling must read only the last successful in-memory snapshot. Catalog, manifest, embedded package manifests, content-bundle ownership, project overrides, agent skills, local votes, and embedded-change baselines are refresh-time inputs; mutation commands must still revalidate live state immediately before changing it.
- UPM registration, relevant catalog/package-manifest imports, content-bundle and project-override state commits, catalog completion, and agent-skill completion must request the shared coalescing refresh signal. Multiple requests in one Editor tick must produce one delayed reload/repaint, while the manual `Reload` button remains a fallback and a failed refresh keeps the previous snapshot.
- Content Bundle package Git URLs must use exact SemVer tags when available. A package whose repository publishes no version tag may pin a full 40-character immutable commit while retaining its package SemVer for registration checks; branches, short commits, and floating revisions remain invalid.
- A package profile may opt in with `allowCompatibleRegistryVersion: true` to preserve an already-installed stable registry SemVer that is equal to or newer than the declared minimum. The default remains false; local/file, Git source changes, prereleases, older registry versions, and non-opted-in registry values remain conflicts.
- It manages internal UPM package search/install/update/remove, package collection discovery, direct credential-free Git URL installation, repository creation, changelog/history display, AI guide routing, and manual publish flows.
- The optional catalog `package_type` column may use the exact case-insensitive value `collection`. For backward compatibility, package IDs ending in `.installer` are also collections. Keep collections in `Package Collections` and exclude them from Embedded, Downloaded, and Available package sections.
- `ActionFitPackageCatalogUpdater.BuildCatalogCsv` must preserve optional `package_type` / `packageType` metadata while joining package and version sheets, and emit an empty value for legacy rows.
- The Package Manager `Search` field must match package ID, display name, owner, package type, repository URL, version/status/Unity minimum, description, changelog, and dependencies. It must also filter active Content Bundles by bundle, state, required package, module, and conflict metadata.
- `Install from Git URL` delegates to `Client.Add` and polls its asynchronous request through `EditorApplication.update`. Accept credential-free HTTPS, `ssh://`, and SCP-style SSH plus the single UPM `?path=` query and optional non-empty `#revision`. Reject HTTP, embedded HTTPS credentials, SSH passwords, other queries, whitespace/control characters, and malformed URLs before mutation. Never log or include the submitted URL in failure diagnostics.
- Manager Console exposes `1. Create Package`, then `2. Publish Changed`, then `Add Agent Skill`, followed by `Publish Package`, catalog/manifest access, and AI guide router refresh. Package README and settings SO access must stay in Unity top-menu package entries such as `Tools/Package/<Package Name>/README` and `Tools/Package/<Package Name>/Setting SO`, not in `Project Files` or Package Manager package rows.
- `1. Create Package` creates an operational skeleton only: `package.json`, README, AI guide, package menu, PackageInfo, and an Editor asmdef. A content package must add and design its `Runtime`, optional UI, and `Tests/Editor` assemblies explicitly; skeleton creation does not establish a reusable content architecture or prove Unity compilation.
- Do not create another package merely for a configured instance of an existing content framework. Content-domain ownership and extraction decisions belong to the consuming project's content-boundary documentation.
- Publish-window discovery must enumerate only immediate `Packages/com.actionfit.*` directories. Never recursively treat nested `package.json` files, including `Tests/Shell/Fixtures~` validator packages, as publish candidates or pass them to `ActionFitPackageInfoUtility.CreateOrUpdate`.
- Each package's `ActionFitPackageInfo_SO` stores `Repository Visibility`. Publish flows use that package-local value to choose the public/private GitHub profile for both new and already registered packages, so `Publish All Changed` can safely publish mixed public/private packages in one run.
- New package creation and `Fork as New` default to `Public`. `ActionFitPackageCreateRequest` requests with an omitted visibility are normalized to `Public`; `Private` is valid only when `RepositoryVisibilitySpecified` is true, and invalid enum values remain blocked. UI surfaces must label Public as the default and Private as an explicit exception. Internal metadata refreshes preserve an existing package's known value instead of silently changing it.
- Tokens, credentials, private keys, signing material, vendor configuration, and other secrets are prohibited from every package source and metadata surface regardless of repository visibility. Keep them in ignored local settings, environment variables, or an approved secret store. Private is for approved ownership, distribution, or confidentiality constraints, not for embedding secrets; unresolved public-distribution rights must block publication for review rather than silently selecting Private.
- SDK bridge package creation is stricter: `ActionFitSdkBridgePackageTemplate.Create` requires explicit `Public` visibility, a valid schema-v1 profile, and an exact `BridgePackageId` match. It adds the installed Custom Package Manager version as an explicit package dependency, keeps the bridge source-only, and must not redistribute vendor SDK binaries, archives, credentials, or vendor configuration files.
- `Publish All Changed` must create publish request snapshots on the Unity main thread. It may run pure GitHub remote checks and repository publish work through `ActionFitPackagePublisher.DefaultMaxParallelPublishes`, but background work must not read Unity objects.
- Bulk preflight must classify a missing-Catalog package with an existing immutable tag as a Catalog recovery candidate only after the single-package recovery verifier confirms repository visibility, package identity, tag commit, and content equivalence. Keep publish and recovery package ID sets distinct in the content-bound plan.
- Bulk catalog append should call Web App action `upsertPackageVersions` first. The Web App must return either `count == item count` or per-item confirmations; unsupported response contracts fall back to serial `upsertPackageVersion`. Timeout and cancellation failures must retain retry rows without starting serial fallback.
- Publish HTTP requests must apply the shared 30-second connection/read timeout. Bulk UI progress must identify local validation, remote preflight, repository publish, catalog batch/fallback, and final catalog refresh stages; cancellation during a catalog request must abort that request, while cancellation during repository publishing takes effect after active repository operations finish.
- If repository publish succeeds but catalog append fails, keep the successful catalog append items in the publish window and expose `Retry Catalog Append` so the user can update the spreadsheet without pushing repositories again.
- When retry items are unavailable after window recreation, domain reload, or Editor restart, expose `Recover Catalog Entry` for changed package rows. Prepare recovery only when the refreshed catalog lacks the version, the immutable remote tag and repository visibility match, and the tag checkout matches the local package after narrowly normalizing `_fingerprint`, JSON whitespace, and Unity PackageInfo YAML serialization. Execute only after exact recovery approval and perform catalog upsert/refresh without repository, branch, or tag mutation.
- Package section classification should first remove collections into `Package Collections`, then treat Git/registry dependencies in `Packages/manifest.json` as Downloaded Packages. Only local `file:` dependencies or package folders under `Packages/` without a manifest dependency should be treated as Embedded Packages.
- The `Check Update` panel must include only installed packages whose catalog latest version is higher than the installed version. Do not treat any version difference as an update, because that can downgrade packages such as `1.0.30 -> 1.0.29`.
- `Force Update` must run catalog refresh first, show a confirmation list of downloaded packages, and then re-apply catalog latest Git UPM URLs/dependencies for downloaded packages only. Embedded packages are skipped so local edits are not deleted.
- `Latest Git` buttons in package details and the `Check Update` panel should open the catalog latest version's GitHub tag URL in the browser without modifying `Packages/manifest.json`.
- Each ActionFit package must be reachable from Unity's top `Tools/Package` menu for docs/config access. `README` menu items must open the installed package README from `Packages/` or `Library/PackageCache`. Packages with shared settings SOs must expose a `Setting SO` menu item that selects and pings the known settings asset. Keep this code in each package's own `Editor/Scripts/*PackageMenu.cs`, using that package's known asset path or safe factory method.
- `Tools/Package` must stay grouped by `MenuItem` priority: package-wide manager first, packages with executable tool commands next, packages with only `Setting SO` + `README` after that, and README-only packages at the bottom. Leave separator gaps between those groups. Inside each package root, real tool commands stay above the separated lower `Setting SO` and `README` entries.
- Use the established priority bands unless a package has a documented reason to differ: Package Manager `0-9`, executable tools `20-99`, `Setting SO` + `README` only packages `600-699`, and README-only packages `900-999`.
- Downloaded packages may be converted to editable embedded packages through `Embed for Edit` or the distinct `Project Override` mode. Downloaded-source conversion must use Unity Package Manager's official `Client.Embed` operation. A custom `Directory.Move` into the logical `Packages/<packageId>` path is unsafe because Unity can resolve that path back to `Library/PackageCache` and report that the cache folder already exists. After the official request succeeds, validate the physical local `package.json`, remove cache-only `_fingerprint` metadata, normalize the manifest dependency to `file:<packageId>`, preserve catalog repository metadata in PackageInfo, refresh AI routing, record a baseline, and resolve packages. Project Override additionally records committed project ownership after the physical package validates. Do not write a local `file:` dependency unless the target physical package folder is valid.
- Modifying any downloaded ActionFit package requires this `Embed for Edit` flow: convert the package to an editable embedded package, edit the embedded copy, bump `package.json` above the catalog latest version, and publish through the manager so the repository tag and the catalog `is_latest` row advance together. Updating the upstream repository directly and hand-pushing a version tag is not an approved modification path because the catalog never records the new version and keeps reporting the previous version as latest.
- Existing physical-folder conversion and `Fork as New` must use `ActionFitPackageTransaction`. Never delete a transaction-created package folder until the original affected manifest dependencies have been restored and verified. Manifest updates must use the atomic writer rather than direct `File.WriteAllText`.
- Do not use `Directory.Exists("Packages/<packageId>")` alone to decide that an embedded folder physically exists. Unity can project downloaded package cache contents through that logical path. Use `ActionFitPackageFileUtility.PhysicalDirectoryExists` plus package ID validation before reusing or deleting a package folder.
- Pending conversions are journaled under `UserSettings/ActionFitPackageManager/Transactions`. Editor-load recovery may finalize a valid local package or restore only the affected dependency values while preserving unrelated manifest changes. When recovery cannot be verified, preserve the package folder and return/log `RECOVERY_REQUIRED` with the journal path.
- AI callers should run `ActionFitPackageWorkflowApi.Inspect` with `RefreshCatalog = true` before choosing a package workflow. The result must distinguish installed, embedded, behind-latest, matches-latest, ahead-of-catalog, and local modification states and should present current-source fork and latest-source update/embed options.
- `ActionFitPackageWorkflowApi` is read/refresh/advice only. It must not push, tag, create repositories, or append catalog rows. Real publishing still requires an explicit user request.
- `ActionFitPackageEmbedApi` is the public dialog-free entry point for candidates, validation, JSON execution, embedding, and explicit custom-transaction recovery. The Package Manager UI must call the same API instead of maintaining a separate conversion implementation. Downloaded embedding is asynchronous: UI and automation that require a final result must use `EmbedForEditAsync`. The synchronous `EmbedForEdit` and `ExecuteJson` APIs can return `EMBED_STARTED`, which means only that Unity accepted the request. The embed CLI waits for the callback, writes the final result, and exits Unity itself, so callers must not pass Unity's `-quit` option.
- `ActionFitPackageSkillScaffoldApi.Add` / `AddJson` is the public dialog-free entry point for adding schema v2 skills to physical embedded packages. It validates fixed package/skill paths, never edits downloaded cache sources, rejects existing registrations or files, and creates Codex `agents/openai.yaml` metadata together with each new Codex skill.
- PackageInfo refresh, AI router refresh, baseline creation, and other destination post-processing must run only after the official embed request has succeeded and the physical embedded package has been validated. Post-processing warnings must not delete or roll back a successfully embedded package.
- AI publishing must use `ActionFitPackagePublishApi.Prepare` before `Execute`. Preparation is read-only and must refresh the catalog, reject an already registered version, require a version newer than the catalog latest, validate credentials without exposing them, and check the remote repository/tag.
- Catalog-only recovery must use `ActionFitPackagePublishApi.PrepareCatalogRecovery` before `ExecuteCatalogRecovery`. It must revalidate the catalog, repository visibility, immutable tag commit, and package content immediately before append; mismatched content remains an immutable-tag conflict and requires a new patch version.
- `Execute` must require the exact prepared plan ID and publish approval text, reject content/catalog/remote changes, and require `ApproveRepositoryCreation` for a missing repository. A repository migration additionally requires `ApproveRepositoryMigration` plus the exact separate `MigrationApprovalText`; publish approval must never imply migration approval. In-process callers should pass the returned `ApprovedPlan` object so execution can reuse contract validation while still refreshing the catalog and recomputing mutable local/remote state. A deserialized plan does not carry the in-process validation receipt and must run full contract validation. Never infer publish or migration approval from an earlier edit, embed, inspection, or preparation request.
- AI bulk publishing must use `ActionFitPackageBulkPublishApi.PrepareAllChanged` before `ExecuteAll`. In-process callers should pass the returned `ApprovedPlan` object; execution skips duplicate package contract processes only when that object retains its non-serialized validation receipt, but still refreshes the catalog, recomputes file/version state, rechecks GitHub, and re-verifies recovery tag content before comparing the exact bulk plan and approvals. It must provide the exact publish approval, `ApprovedRepositoryCreationPackageIds`, `ApprovedRepositoryMigrationPackageIds`, separate migration approval, and separate `CatalogRecoveryApprovalText` when returned. Only `PublishPackageIds` may reach repository migration/publish workers; `CatalogRecoveryPackageIds` may only contribute verified Catalog rows. The Manager Console must use this same API rather than a separate bulk implementation.
- Publish preparation must return `PROJECT_OVERRIDE_NOT_PUBLISHABLE` for an explicitly requested project override. Automatic changed-package discovery must skip overrides before package preflight so project-specific edits never enter an upstream publish approval set.
- A catalog source URL that differs from a selected Public or Private target must trigger read-only migration preflight. Inspect actual source/target visibility, default branches, and every branch/tag ref; reject a target whose visibility differs from the selection, missing source, current-version tag reuse, or README/AI guide URLs that still point only to the source. Tag SHAs must match exactly. A differing target branch is compatible only when GitHub compare proves the target commit is `ahead` of or `identical` to the source commit; `behind`, `diverged`, missing, or unverifiable ancestry remains a conflict. Never change visibility in place or mutate, force-push, or prune the source during publication. After explicit migration approval, create only the target when separately approved, push source branches/tags without force, align and verify the target default branch, then publish the current version and update catalog rows. Any failure before verification must leave catalog URLs and the source unchanged and support safe retry.
- Private source retirement must use `ActionFitPackageRepositoryRetirementApi.Prepare` before `Execute`, or the matching batch pair. Keep is the default and performs no mutation. Archive/Delete are allowed only after a distinct Public target, immutable published tag, Catalog latest URL, zero known Catalog version URLs for the source, PackageInfo target, complete migrated refs, current Private source visibility, and zero current-project source references are freshly verified. Require the exact content-bound plan and approval text; never infer retirement from publish or migration approval. Batch retirement must bind the exact package/mode set, revalidate each item immediately before mutation, execute serially, stop on the first failure, and preserve later sources. Warn that external consumers and non-Git repository metadata cannot be proven or migrated.
- Repository publication and catalog registration are separate result stages. If the repository succeeds and catalog upsert fails, return `RetryCatalogAppendAvailable` and retry only the catalog stage instead of pushing Git/tag again.
- If `Packages/<packageId>/` already exists during `Embed for Edit`, validate its `package.json` name and let the user use that existing folder by writing the local `file:<packageId>` dependency. Do not overwrite the local folder.
- Downloaded packages may also be copied through `Fork as New` when the user wants a new `com.actionfit.*` package ID and a new repository instead of publishing edits back to the source package repository. This flow must rewrite the copied package's `package.json` metadata, create PackageInfo metadata for the new repository, remove the original manifest dependency, and write the new local `file:<newPackageId>` dependency so duplicate source/fork assemblies are not compiled together.
- Embedded packages may be returned to the downloaded flow through `Use Downloaded`, which writes the selected catalog Git UPM dependency, removes the local package folder, and runs Package Manager resolve.
- Catalog `dependencies` are applied through the same manifest-writing path for install/apply, selected updates, and `Use Downloaded`. `com.actionfit.*` dependencies must resolve to catalog rows so the manager writes Git UPM URLs; registry dependencies outside ActionFit may fall back to their raw version string.
- Expanded package rows should show dependency details for the selected version, including resolved catalog Git URLs for ActionFit dependencies and raw registry version values for non-ActionFit dependencies.
- `ActionFitPackageCatalogUpdater` must parse CSV by records, not by raw lines, because release notes can contain quoted newlines. Otherwise `dependencies` and later columns shift and package details incorrectly show `None`.
- `Embed for Edit` does not publish by itself. After editing, the package `package.json` version must be bumped above the catalog latest version before `Publish Changed` will pick it up.
- `Embed for Edit` records a content baseline under `UserSettings/ActionFitPackageManager/EmbeddedBaselines`. Embedded update warnings rescan the package against that baseline, but a missing baseline must be treated as potentially modified rather than clean.
- Replacing an embedded package through `Convert & Apply`, `Convert & Update`, `Update Selected`, or `Use Downloaded` must first create a timestamped validated backup under `UserSettings/ActionFitPackageManager/EmbeddedBackups/<packageId>/`. Local edits are never merged into the downloaded version.
- Bulk update controls must not select embedded packages implicitly. `Select Downloaded` and `Update Downloaded` operate on downloaded packages only; users must explicitly select embedded rows before conversion.
- Embedded replacement must preserve the original manifest until backups succeed. If any embedded folder cannot be moved to trash after the manifest write, restore the original manifest and restore already moved folders from their safety backups before resolving packages.
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
- `History` and `Changes` must render from both `Check Update` rows and expanded package detail rows; package detail buttons should draw the panel inline under the selected package, not only inside the update manager panel.
- Release notes do not need headings such as `## 1.1.29`; the UI already renders the version label.
- Catalog CSV reading supports quoted multiline changelog fields. Do not rely on unquoted commas or raw newlines in catalog rows.

## External SDK Profile Contract

- `ActionFitSdkInstallApi.Inspect`, `Plan`, `ResolveAsync`, and `PreparePlanAsync` are read-only. Schema-v1 exact profiles retain synchronous `Plan`; schema-v2 `AnyInstalledElseLatestStable` profiles must use the async resolve/prepare path. Execution is available only through operation-specific `ApplyAsync`, `RepairAsync`, `UpdateAsync`, or `RemoveAsync` with the exact reviewed plan ID.
- `Apply` is the idempotent new-install path. `Repair` requires existing ownership at the same profile version, `Update` requires existing ownership at a different version, and `Remove` requires existing ownership. Unsupported operation/ownership combinations must remain blocked.
- Profile schema version 1 declares identity/version, bridge package ID, vendor/license/support metadata, Unity/platform compatibility, allowed domains, immutable artifact/Git/registry sources, modules, ordered dependencies, scoped registries, and detection rules. Schema version 2 adds source `ResolutionPolicy`, matching `LatestResolver`, official `MetadataUrl`, and optional `VersionFamily` without changing schema-v1 behavior.
- `AnyInstalledElseLatestStable` must compare manifest, packages lock, and open-Editor registered package state. Preserve any consistently resolved required package regardless of its version. When every selected latest-policy package is resolved, return `NO_CHANGES` without manifest or ownership writes. A manifest-only, duplicate, invalid, broken, or ambiguous state blocks planning; only a genuinely missing package may query latest metadata.
- Latest resolvers are restricted to `registryMetadata`, `gitRelease`, and `artifactMetadata`. Metadata and artifact URLs must use credential-free HTTPS on `AllowedDomains`; generic page scraping is forbidden. Accept the canonical `PackageId`/`Releases` document, native npm/UPM `versions` metadata for registries, and GitHub-style release arrays for Git sources. Exclude drafts, prereleases, and Unity-incompatible releases. Registry entries pin exact `Version`, Git entries pin a commit or exact SemVer tag, and artifact entries require exact `PackageVersion`, URL, and SHA-256. Fully missing coupled sources with `VersionFamily` resolve one newest common version. A partial family may add missing selected packages only at the installed family version when each official source still exposes that compatible version; otherwise block.
- Official artifact downloads require credential-free HTTPS on `AllowedDomains`, SHA-256 verification, and matching tarball `package/package.json` ID/version before any project mutation. Git sources use full commits or exact SemVer tags. An optional `GitSubpath` must be a traversal-free relative path made of safe segments and is composed as `?path=<subpath>#<revision>`; source URLs themselves must remain query-free. Registry sources use exact SemVer.
- `ProjectSettings/ActionFitSdkProfiles.json` is ownership schema version 1. It records dependency values, registry scopes, and cached artifacts created or explicitly adopted by a profile. Removal must preserve user-managed, changed, and shared entries.
- Plans bind the normalized profile snapshot, selected module closure, operation, original manifest/packages-lock/registered-package/ownership hashes, resolved immutable source snapshot, official metadata content hashes, desired content, and redacted changes. Revalidate every mutable snapshot immediately before execution.
- Use atomic manifest/ownership writes and journals under `UserSettings/ActionFitPackageManager/SdkTransactions`. In-process failures attempt rollback. Domain reload and Editor startup only report pending journals; they must not mutate project state until explicit recovery.
- Detection, plan conflicts, checksum failures, identity mismatches, unsafe paths, mutable revisions, and ownership drift block execution. Never overwrite a conflicting existing SDK installation automatically.
- The package contract CLI treats the presence of `Editor/SDKInstallProfile.json` as an SDK bridge declaration. It requires public visibility and `THIRD_PARTY_NOTICES.md`, validates immutable safe sources, and rejects archives, native/managed vendor binaries, vendor config files, credentials, and files over the source-only size limit.
- Tests must use isolated temporary project contexts. Do not apply fixture profiles to the consuming project's real `Packages/manifest.json` or `ProjectSettings` during validation.

## Package Contract Validation

- Run `python Packages/com.actionfit.custompackagemanager/Tools~/package_contract_validator.py --package <package-id>` for one package, `--changed --base-ref <ref>` for Git-diff selection and version-bump enforcement, or `--all` for the embedded baseline.
- `ActionFitPackageInfoUtility.CreatePackage` runs the same package-owned validator after writing and importing the generated skeleton, so a create request succeeds only when its round-trip package contract passes.
- `ActionFitPackagePublishApi.Prepare` runs contract validation before physical embedded-folder validation, catalog refresh, credential lookup, or GitHub checks. Contract failures return structured diagnostics and must stop publish preparation without external requests.
- The Unity Editor adapter resolves the package-owned validator from `PackageInfo.resolvedPath` and passes the repository root containing that physical `Packages/<package-id>` directory to the CLI. This must work for both the embedded project and an isolated `file:` dependency project.
- `Tools/AI/validate_ai_docs.py` delegates changed ActionFit package checks to this validator so local AI documentation validation and publish preparation share one contract implementation.
- `--package` and `--all` also enforce changed-package version bumps when `--base-ref` is supplied. Without a base ref they validate only the current package state.
- `--changed` accepts a deleted embedded package as a downloaded transition only when the base contains its package manifest and the current project manifest/lock agree on a credential-free immutable HTTPS Git dependency, depth zero, Git source, and a full resolved commit hash.
- A change limited to the generated `PACKAGE_AI_GUIDE_ROUTER.md` does not select Custom Package Manager for a release bump. Any additional change under the package keeps normal selection and version enforcement.
- The JSON result schema is shared by local AI and CI callers. Every diagnostic has `code`, `severity`, `path`, `line`, `message`, and `suggestedFix`; exit codes are `0` success, `1` contract failure, and `2` infrastructure failure.
- Contract checks cover package.json fields and JSON, SemVer, changed-package version increases, README Git UPM install tags, AI guide identity/version/router entries, schema v2 skill prefix/help/access/inventory rules, registered sources and `SKILL.md` frontmatter, PackageInfo identity/required metadata, package-owned asmdefs, and SDK bridge source-only/profile contracts when present.
- Directories whose names end in `~`, including validator fixtures and `Tools~`, are excluded from asmdef discovery because Unity does not import them as package assemblies.
- The validator is standard-library Python and must remain independent of Unity, network APIs, catalogs, credentials, publishing, and package compilation/test execution.
- Run `bash Packages/com.actionfit.custompackagemanager/Tests/Shell/run-tests.sh` after changing validator behavior or its stable result contract.

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
- The generated `packages/actionfit-packages.md` compatibility pointer must also summarize active bundle/module state and project override base/current state deterministically. Omit credentials, repository URLs, and machine-specific absolute paths.

## AI Documentation Package Design

- Shared AI operating rules should be portable across ActionFit projects without copying a project-specific docs tree. Package-owned `AI_GUIDE.md` files carry portable rules, while each project keeps a lightweight router document (for example `Docs/AI/PROJECT.md`) for project-specific architecture and workflow decisions.
- The package model is valid when the package boundary owns a stable workflow or tool contract, such as Jira automation, PR creation, document maintenance, project sequence management, or package publishing rules. It is not valid for game-specific runtime knowledge that depends on one project's assets, scenes, data rows, or content managers.
- Recommended AI workflow package families: `com.actionfit.ai-jira` (Jira skills and lifecycle), `com.actionfit.ai-worktrees` (reusable worktree slots), `com.actionfit.ai-pr` (branch/commit/PR workflow), `com.actionfit.ai-docs` (AI documentation maintenance rules; not yet created), `com.actionfit.ai-unitycli` (Unity CLI usage), and `com.actionfit.ai-project-sequence` (project initialization sequence; not yet created).
- Keep implementation scripts behind project compatibility wrappers until all active projects have moved to package routing.
- Migration order: add package skeletons with `package.json`, `README.md`, `AI_GUIDE.md`, and PackageInfo metadata first; move portable rules into package `AI_GUIDE.md` files while leaving project-specific rules in the project docs; add compatibility pointers from existing project docs to the package router; move executable tooling only after package-local wrappers preserve existing project command paths; publish manually after version checks, tag checks, catalog updates, and user approval.
- Write shared package `AI_GUIDE.md` files in English so another project can reuse them without project-local translation work. This is an explicit exception to the Korean-first rule for human-facing package `README.md` files. Keep user-facing generated Jira text, QA notes, and PR descriptions in Korean when a package rule requires Korean output.
- Do not commit local Jira URLs, credentials, board IDs, status names, or user-specific config into any package.
- Do not auto-publish packages from an AI session unless the user explicitly asks for publishing.

## Agent Skill Distribution Rule

- New or changed packages opt in with `Skills~/manifest.json` schema version 2. Runtime installation temporarily accepts schema v1 for compatibility, but the package contract/publish gate requires v2.
- Schema v2 declares an explicit lowercase-hyphenated `skillPrefix` and `helpSkill` exactly equal to `<skillPrefix>-help`; do not infer the prefix from the package ID. Every registered name starts with `<skillPrefix>-`.
- The help skill is mandatory, read-only, included in `skills`, and registered for the union of `codex`/`claude` agents used by all related skills. Each entry declares `access` as `read-only` or `write-capable` plus optional `includeShared`.
- Source paths are fixed at `Skills~/Codex/<name>` and `Skills~/Claude/<name>`; targets are fixed at `.agents/skills/<name>` and `.claude/skills/<name>`. Do not add manifest-defined paths.
- Every registered source must contain `SKILL.md` with matching `name` and a non-empty `description`. Codex skills should follow the project `skill-creator` contract and may include `agents/openai.yaml`.
- During staging, generate `PACKAGE_SKILLS.md` only inside the installed help skill from package ID/display name/description, the v2 registration list, target-agent frontmatter descriptions, `$name` invocations, and access values. Include it in the managed hash and require the help `SKILL.md` to read it instead of duplicating a related-skill list. Reject package-authored copies of this reserved file.
- `Skills~/Shared` is overlaid only for registrations with `includeShared: true`. Shared files must not collide with agent-specific relative paths.
- Reject invalid manifests, duplicate registrations, unsupported agents, linked sources, and package-to-package target conflicts. Never execute bundled scripts during installation.
- Automatic synchronization runs after Editor load and package registration outside batch mode. It installs missing targets and refreshes only unchanged managed targets; it never overwrites unmanaged, modified, file-backed, or linked targets.
- Keep ownership hashes in ignored `UserSettings/ActionFitPackageManager/skill-install-state.json`. Read the old `UserSettings/AIJira/skill-install-state.json` only as preserved migration input and adopt an existing target only when its current hash matches the recorded legacy hash.
- The Package Manager detail view reports per-package registered/current/update-available/missing/preserved/conflict counts through the read-only `InspectRegisteredSkills` path.
- Embedded package rows and Manager Console expose `Add Agent Skill`; first use creates schema v2 plus `<skillPrefix>-help` for Codex and Claude, and later additions preserve all existing source files.
- Package disappearance must not automatically delete installed skills. `Remove Managed Agent Skills` may delete only unchanged managed targets after confirmation and disables automatic recreation until explicit refresh.

## UPM Package Release Preparation

When files under `Packages/com.actionfit.*/` embedded packages are modified, prepare the package for release at task completion, but do not publish unless the user explicitly asks.

1. Report the modified package ID and key changed files, and state that the user must run publish manually.
2. Bump the package `package.json` version. Default to a patch bump unless the user asks otherwise or the compatibility impact clearly requires minor/major.
3. Before choosing or reusing a version, check the package's remote Git tags for the exact version and the next patch candidate.
4. Treat any version that already has a remote tag as immutable, even if local package files still show that version.
5. If a package version has already been tagged and any further code, README, PackageInfo, catalog, or release-note change is made, bump to the next unused patch version before finishing.
6. Do not infer publish status from local files or memory. Re-check remote tags after each follow-up change that affects the package release.
7. Treat each package's `Editor/PackageInfo/ActionFitPackageInfo_SO.asset` as the source of package metadata, repository name, description, and release note.
8. Before publish, check the package `README.md` that will be pushed to Git. It must be current and written primarily in Korean for human-facing headings, setup, usage, settings, cautions, migration, and release prose. Preserve exact identifiers, menu paths, commands, configuration keys, file paths, code samples, and externally defined proper names; do not rely on automatic translation.
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
- When registering or updating a package through Custom Package Manager, use `1. Create Package` to create the base `Packages/com.actionfit.*` structure, README-only package menu, and PackageInfo SO.
- `package.json` is the source for `name`, `version`, `unity`, and `dependencies`.
- PackageInfo SO is the source for `repoName`, repository visibility, description, owner, status, and release notes.
- Before package publish, treat that package's `README.md` as user-facing documentation that will be uploaded to GitHub and keep it up to date.
- `2. Publish Changed` handles the normal changed-version publish path and also handles first registration for PackageInfo SOs not yet in the catalog. It is the second Manager Console action, with `Add Agent Skill` immediately after it. Both single and bulk changed publishing use the approval-gated publish API. `Publish All Changed` publishes repositories in parallel and then batch-appends catalog rows, with serial append fallback when the Web App lacks `upsertPackageVersions`. Each package's `Repository Visibility` controls the selected public/private GitHub profile for both existing and newly registered package publishes.
- `Publish Package` writes the requested version for an already registered PackageInfo SO, then enters the same approval-gated preflight, optional repository migration, package push, and catalog sequence as `Publish Changed`.
- Creation and publish flow guidance should stay based on this package's README and Manager Console UI.
- For real publishing, use the configured ActionFit GitHub authentication and SSH/HTTPS settings in the local environment. AI may run real publish, push, tag, or catalog upsert only when the user explicitly asks.

## Package Tools Menu

- Unity menu root: `Tools/Package/Custom Package Manager/`.
- Keep package commands under this package root.
- `Install or Refresh Agent Skills`: discovers all registered installed-package skills, synchronizes safe managed copies, and re-enables automatic installation.
- `Remove Managed Agent Skills`: after confirmation, removes only unchanged managed copies and disables automatic recreation.
- `Add Agent Skill`: opens the schema v2 scaffolding window for a physical embedded package; package detail rows disable it for downloaded packages.
- `Tools/Package` top-level grouping is mandatory:
- Package-wide manager first, including `Tools/Package/Custom Package Manager/Package Manager`.
- Packages with executable tool commands next.
- Packages with only `Setting SO` + `README` next.
- README-only packages at the bottom.
- Priority bands: Package Manager `0-9`, executable tools `20-99`, `Setting SO` + `README` only `600-699`, README-only `900-999`.
- Use separated priority gaps between those groups.
- Lower separated package-root entries:
- `Setting SO`: required when a package owns or bootstraps a settings ScriptableObject; focuses the settings asset.
- `README`: required for every ActionFit package; opens this package README.
- Do not add README or Setting SO access back to Custom Package Manager package rows or Project Files.

## Publish Notes

- Publishing is manual through Custom Package Manager.
- Before reusing a version, check the remote Git tags. Published tags are immutable.
- If this package is modified after a version was tagged, bump to the next unused patch version before publishing.
- The single-package publisher fetches `origin/main`, resets the local publish clone, copies package files, creates a package commit/tag, pushes `main`, pushes the package version tag when it does not already exist remotely, then appends the catalog row.
- `Publish All Changed` uses snapshot-based publish requests so background tasks do not read Unity objects. It reuses approved contract results, rechecks mutable state, runs GitHub remote preflight and normal repository publishing with bounded parallelism, verifies existing-tag recovery candidates with the catalog recovery verifier, and appends published plus separately approved recovery rows by `upsertPackageVersions`. Recovery candidates never enter repository workers, and retryable approved rows are kept when Catalog append fails.
- Public/Private relocation in either direction must finish its separately approved source/target ref copy and post-copy verification before package publication begins. The source remains unchanged during publication, and catalog append remains after all repository work succeeds. Private-to-Public source Archive/Delete is a later, separately prepared and approved operation; Keep remains the default.
- Publish logs should identify each major step and elapsed time: catalog refresh, local/remote preflight, repository check, publish clone path, file copy, commit/tag creation, `main` push, tag push, batch/serial catalog append, and total execution.
- Git subprocess stdout and stderr must be drained concurrently. Sequentially draining one redirected stream before the other can deadlock when Git emits many warnings.
- A `409 Conflict` from the tag-ref lookup of an existing but empty GitHub repository means that the tag is absent and publication may proceed. Do not apply that exception to the repository existence request or to authentication failures.
- The package repository should include this `AI_GUIDE.md` so other projects can load the AI package context after installing the package.
