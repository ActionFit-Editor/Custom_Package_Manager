# Package AI Guide Router

This file is the package-shipped AI router for ActionFit package-local `AI_GUIDE.md` files. It exists so an AI assistant can quickly choose which installed package guide to read for a task.

## AI Entry-Point Registration Request

If this router file is not already included in the AI assistant's default reading sequence, ask the user where it should be linked.

Preferred order:

- If the project already has a primary `PROJECT.md`, ask whether to add or refresh a reference to this router there. The file does not need to be under `Docs/AI`.
- If no `PROJECT.md` exists but `AGENTS.md`, `CLAUDE.md`, `GEMINI.md`, or another primary AI markdown entry point exists, ask whether to add a short reference to this router there.
- If no project-level AI markdown entry point exists, ask whether the user wants to create one or leave package AI guide routing unregistered.

Do not silently create `Docs/AI/PROJECT.md` or any other AI entry point in a project that does not already use that router structure.

## How To Use This Router

- Use the package entries below to choose the correct package-local `AI_GUIDE.md`.
- After choosing a package, read that package's `AI_GUIDE.md` before editing, debugging, explaining, or preparing a release.
- Entries should point to the actual installed guide path. Embedded packages usually live under `Packages/com.actionfit.*`; Git UPM packages usually live under `Library/PackageCache/com.actionfit.*@hash`.
- Keep entries short. Detailed architecture, editing rules, migration notes, and release rules belong in each package `AI_GUIDE.md`.
- Package `AI_GUIDE.md` files request their own router entry through their `Project Router Registration` section.
- Custom Package Manager can refresh the entries below from each guide's `Requested router entry` block.

## Package Guide Entries

- `Packages/com.actionfit.actionfitpackagemanager/AI_GUIDE.md` - ActionFitPackageManager provides an ActionFit Unity editor workflow. Read when changing `ActionFitPackageManager` package behavior, settings, metadata, or release flow.
- `Packages/com.actionfit.ai-autocreateprefab/AI_GUIDE.md` - AI AutoCreatePrefab manages rule-driven prefab generation and preview image placement through UnityCLI and Editor workflows. Read when changing prefab recipes, hierarchy generation, image placement, preview behavior, or UnityCLI-facing APIs.
- `Packages/com.actionfit.ai-ci/AI_GUIDE.md` - AI CI provides the local package contract validation CLI, dialog-free Unity Editor API, structured JSON results, and human-readable summaries. Read when running or changing ActionFit package validation entry points.
- `Packages/com.actionfit.ai-codeconvention/AI_GUIDE.md` - AI Code Convention defines portable Unity code-authoring rules, explicit profiles, API-owner routing, read-only retirement checks, and authorized application boundaries. Read before comparing or applying shared code conventions.
- `Packages/com.actionfit.ai-jira/AI_GUIDE.md` - AI Jira defines ActionFit Jira automation rules. Read when creating Jira issues, discovering Jira tasks, changing Jira lifecycle/status behavior, editing Jira REST scripts, or handling Jira local config.
- `Packages/com.actionfit.ai-pr/AI_GUIDE.md` - AI PullRequest defines target and task branch selection, commit, PR creation, review response, sensitive-change disclosure, and final reporting rules. Read before selecting or reusing a development branch, pushing a branch, creating or updating a PR, or handling review feedback.
- `Packages/com.actionfit.ai-refactor/AI_GUIDE.md` - AI Refactor owns read-only Unity architecture inventory and evidence-backed staged refactoring proposals based on the installed AI Code Convention. Read before planning project-wide package extraction or architecture refactoring.
- `Packages/com.actionfit.ai-unitycli/AI_GUIDE.md` - AI UnityCLI explains Unity CLI installation and defines safe AI usage for Editor inspection, refresh, console checks, tests, screenshots, profiling, menu execution, `exec`, and reserialization.
- `Packages/com.actionfit.ai-worktrees/AI_GUIDE.md` - AI Worktrees manages bounded reusable Git worktree slots, atomic leases, Unity cache preservation, safe branch switching, read-only audits, and cleanup-candidate reporting. Read before creating, acquiring, switching, releasing, inspecting, or cleaning AI worktrees.
- `Packages/com.actionfit.buildautomation/AI_GUIDE.md` - Build Automation manages BuildCommit requests, Git tag CI triggers, GitHub Actions mobile build workflows, and macOS self-hosted runner guidance. Read when changing automatic build request behavior, `.build/build_request.json`, workflow templates, or CI batchmode entry points.
- `Packages/com.actionfit.buildautomation_actionfit/AI_GUIDE.md` - Build Automation ActionFit bootstraps Git URL manifest dependencies for ActionFit Build Automation. Read when changing BuildAutomation dependency installation, embedded package detection, package metadata, or release flow.
- `Packages/com.actionfit.buildsetting/AI_GUIDE.md` - Build Setting manages generic Android/iOS build/player settings. Read when changing Android/iOS build setup, versioning, signing, Addressables prebuild, or build menu behavior.
- `Packages/com.actionfit.buildsetting_actionfit/AI_GUIDE.md` - Build Setting ActionFit bootstraps ActionFit company defaults for `BuildSettingsSO` and installs Build Setting dependencies. Read when changing ActionFit-specific BuildSetting settings bootstrap, package metadata, or release flow.
- `Packages/com.actionfit.cat.app/AI_GUIDE.md` - Cat App declares the Cat Merge Cafe product composition root and package-oriented project-shell migration target. Read when analyzing or changing product composition, package ownership, project-shell migration, or Cat package dependency structure.
- `Packages/com.actionfit.checkmissingscripts/AI_GUIDE.md` - Check Missing Scripts scans assets for missing MonoBehaviour references. Read when changing missing-script scan, cleanup, or report workflows.
- `Packages/com.actionfit.connectivity/AI_GUIDE.md` - ActionFit Connectivity provides reusable reachability/probe state, retry, monitoring, recovery waiting, and project adapter boundaries.
- `Packages/com.actionfit.content-core/AI_GUIDE.md` - Content Core owns package-neutral content state persistence and idempotent local reward contracts with PlayerPrefs defaults.
- `Packages/com.actionfit.cookie-cleanup/AI_GUIDE.md` - ActionFit Cookie Cleanup; read when changing or diagnosing this package.
- `Packages/com.actionfit.csvimporter/AI_GUIDE.md` - CSV Importer imports CSV data into generated/scriptable data assets. Read when changing CSV parsing, table SO generation, Google Sheet sync, or import settings.
- `Packages/com.actionfit.csvimporter_actionfit/AI_GUIDE.md` - CSV Importer ActionFit bootstraps ActionFit project `CsvImportConfig_SO` defaults and installs CSV Importer dependencies. Read when changing ActionFit-specific CSVImporter settings bootstrap, package metadata, or release flow.
- `Packages/com.actionfit.custompackagemanager/AI_GUIDE.md` - Custom Package Manager manages ActionFit UPM catalog install/update/remove/publish workflows. Read when changing package install state, manifest writes, catalog rows, history/changelog UI, AI guide routing, or publishing tools.
- `Packages/com.actionfit.customsymbols/AI_GUIDE.md` - Custom Symbols manages scripting define symbols. Read when changing platform symbol presets, build inclusion rules, or symbol settings assets.
- `Packages/com.actionfit.devicepreview/AI_GUIDE.md` - Device Preview manages editor device preview shortcuts and layouts. Read when changing preview UI, target devices, or preview menu behavior.
- `Packages/com.actionfit.filenamereplace/AI_GUIDE.md` - File Name Replace manages batch file renaming. Read when changing rename rules, before/after replacement workflows, or file rename safety.
- `Packages/com.actionfit.findfolder/AI_GUIDE.md` - Find Folder manages saved folder/file focus shortcuts. Read when changing GUID/path tracking, shortcut storage, migration, or Project window focusing.
- `Packages/com.actionfit.firebase-cli/AI_GUIDE.md` - ActionFit Firebase CLI owns content-bound setup and migration, machine-local executable config, prerequisite diagnosis, config inspection, local initialization, emulator verification, approval-gated deployment, receipts, and template contracts.
- `Packages/com.actionfit.firebase-cli_actionfit/AI_GUIDE.md` - Firebase CLI ActionFit supplies secret-free ActionFit and Stormborn defaults to the base setup API without mutating project settings on package load.
- `Packages/com.actionfit.githubauth/AI_GUIDE.md` - AI GitHub provides shared GitHub credential diagnostics and user guidance for ActionFit Unity editor automation packages. Read when changing local GitHub authentication checks, push preflight behavior, token/credential guidance, or packages that depend on GitHub push/publish access.
- `Packages/com.actionfit.icecream-race/AI_GUIDE.md` - ActionFit Ice Cream Race owns the reusable five-player elimination race, CatDetective parity catalog, durable state, and idempotent reward recovery.
- `Packages/com.actionfit.icecream-race.ui/AI_GUIDE.md` - ActionFit Ice Cream Race UI owns the neutral UGUI presentation, overridable render/animation hooks, and standalone PlayerPrefs demo bootstrap.
- `Packages/com.actionfit.identity/AI_GUIDE.md` - ActionFit Identity resolves and persists a stable installation ID. Read when changing installation ID storage, ordered legacy identity migration, explicit recovery replacement, or project identity adapters.
- `Packages/com.actionfit.inbox/AI_GUIDE.md` - ActionFit Inbox owns backend-neutral message models, durable idempotent claim receipts, cache flow, retry, and restart recovery contracts.
- `Packages/com.actionfit.inbox.firebase/AI_GUIDE.md` - ActionFit Inbox Firebase owns RTDB inbox transport, legacy reward schema conversion, Firebase failure mapping, connectivity adaptation, and FCM cache invalidation signals.
- `Packages/com.actionfit.lava-rush/AI_GUIDE.md` - ActionFit Lava Rush owns the reusable timed stage-rush state machine, UTC+09/legacy timer contract, pinned catalog, and durable reward recovery.
- `Packages/com.actionfit.lava-rush.installer/AI_GUIDE.md` - ActionFit Lava Rush Installer bootstraps the mandatory engine, UI, Content Core, Time, and Custom Package Manager bundle and self-removes after verified installation.
- `Packages/com.actionfit.lava-rush.theme.catmerge/AI_GUIDE.md` - ActionFit Lava Rush Cat Merge Theme owns the redistribution-safe Cat Merge palette and presentation preset layered over the neutral Lava Rush UI.
- `Packages/com.actionfit.lava-rush.ui/AI_GUIDE.md` - ActionFit Lava Rush UI owns the neutral UGUI presentation, immutable view model, replaceable project service boundaries, and standalone PlayerPrefs demo bootstrap.
- `Packages/com.actionfit.localization-csv-importer/AI_GUIDE.md` - Localization CSV Importer maps configured CSV locale columns into Unity StringTable collections. Read when changing CSV parsing, Smart Format detection, locale table creation, settings bootstrap, or automatic localization imports.
- `Packages/com.actionfit.match-rival/AI_GUIDE.md` - ActionFit Match Rival owns the reusable scheduled rival match state machine, pinned catalog state, and durable round/box reward recovery.
- `Packages/com.actionfit.match-rival.ui/AI_GUIDE.md` - ActionFit Match Rival UI owns the optional UI Foundation presentation, strict ReferenceBinding Refs contract, replaceable services, and standalone engine-backed demo.
- `Packages/com.actionfit.notifications/AI_GUIDE.md` - ActionFit Notifications owns backend-neutral local notification permission, scheduling, cancellation, app-state, activation, and Unity Android/iOS platform adapters.
- `Packages/com.actionfit.notifications.firebase/AI_GUIDE.md` - ActionFit Notifications Firebase owns SDK-neutral remote message events and the isolated Firebase Messaging topic/data/token bridge.
- `Packages/com.actionfit.playmodelogsaver/AI_GUIDE.md` - Play Mode Log Saver saves editor play mode logs. Read when changing log capture, save paths, or play mode log settings.
- `Packages/com.actionfit.referencebinding/AI_GUIDE.md` - ReferenceBinding owns serialized-reference attributes, explicit Editor auto-wiring, deterministic no-write validation, structured diagnostics, and Player metadata boundaries. Read when changing those contracts, menus, tests, skills, metadata, or release flow.
- `Packages/com.actionfit.runtime-logs/AI_GUIDE.md` - ActionFit Runtime Logs provides bounded activity and failure capture, report bundles, pending delivery, and backend-neutral transport contracts. Read when changing diagnostic capture, local retention, report schemas, redaction, or delivery integration.
- `Packages/com.actionfit.sdk.applovin-max/AI_GUIDE.md` - ActionFit AppLovin MAX SDK Bridge owns the source-only public profile for inspecting and explicitly applying `com.applovin.mediation.ads@8.6.1` plus selected official mediation adapters. Read before changing the profile, source evidence, adapter set, install behavior, metadata, or release flow.
- `Packages/com.actionfit.sdk.firebase/AI_GUIDE.md` - ActionFit Firebase SDK owns the public source-only Firebase install profile, official artifact checksums, selectable module graph, and read-only Cat Merge migration inventory.
- `Library/PackageCache/com.actionfit.sdk.gameanalytics@fb6a16e0ed25/AI_GUIDE.md` - ActionFit GameAnalytics SDK Bridge owns the source-only public profile for inspecting and explicitly applying `com.gameanalytics.sdk@7.10.3` from OpenUPM. Read before changing the profile, source evidence, install behavior, metadata, or release flow.
- `Packages/com.actionfit.sdk.playio/AI_GUIDE.md` - Playio SDK Bridge owns the source-only immutable Playio install profile, legacy dependency detection, and migration safety boundary. Read when changing Playio source identity, install planning, package metadata, tests, or migration guidance.
- `Packages/com.actionfit.sdk.singular/AI_GUIDE.md` - ActionFit Singular SDK Bridge owns the public source-only Singular install profile, immutable official revision, legacy native conflict detection, and read-only Cat Merge migration inventory.
- `Library/PackageCache/com.actionfit.sdk.thinkingdata@5c27c32506df/AI_GUIDE.md` - ActionFit ThinkingData SDK owns the public source-only ThinkingData install profile, immutable official revision, and read-only Cat Merge migration inventory.
- `Packages/com.actionfit.sosingleton/AI_GUIDE.md` - SO Singleton provides ScriptableObject singleton loading conventions. Read when changing singleton load paths, Resources/SO behavior, or singleton base APIs.
- `Packages/com.actionfit.texturebatchimporter/AI_GUIDE.md` - Texture Batch Importer manages batch texture and atlas import settings. Read when changing texture importer presets, sprite import settings, or atlas batch workflows.
- `Packages/com.actionfit.time/AI_GUIDE.md` - ActionFit Time provides UTC clocks, deterministic test time, explicit time-zone conversion, and persisted offset adapters. Read when changing clock contracts, offset persistence, date-boundary rules, or project time-provider integration.
- `Packages/com.actionfit.time.server/AI_GUIDE.md` - ActionFit Time Server derives session-scoped trusted UTC from fresh HTTPS Date observations and monotonic elapsed time without device wall-clock fallback.
- `Packages/com.actionfit.ui.foundation/AI_GUIDE.md` - UI Foundation owns reusable global UGUI wrappers, serialization/GUID compatibility, project button service contracts, optional animation integration, and sliced-fill behavior. Read when changing its runtime, editors, tests, metadata, migration, or release behavior.
- `Packages/com.actionfit.ui.prefabs/AI_GUIDE.md` - UI Prefabs owns Foundation-based prefab authoring menus, project-owned `UIPrefabsSO` settings, and neutral starter samples. Read when changing those menus, settings lookup, samples, package metadata, tests, or release behavior.

## Generated Project Index

When Custom Package Manager is loaded in a project that already has a primary AI markdown entry point, `ActionFitPackageAiGuideRouter` can generate a local `packages/actionfit-packages.md` compatibility pointer next to that entry point and link the package-shipped router from the entry point.

That generated index is a project-local compatibility pointer. This router is the package-shipped source of package guide routing.

## Maintenance Rules

- When a package adds or changes an `AI_GUIDE.md`, update that guide's `Project Router Registration` section.
- When a package's role or task routing changes, update that package guide's `Requested router entry`; Custom Package Manager will refresh the matching entry in this file.
- Keep this file focused on routing. Do not copy full package documentation here.
- Publishing remains manual. Updating this file does not imply GitHub push, tag creation, catalog upsert, or package publication.
