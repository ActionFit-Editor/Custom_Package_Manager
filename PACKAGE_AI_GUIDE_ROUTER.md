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

- `Library/PackageCache/com.actionfit.ai-jira@59bb90824136/AI_GUIDE.md` - AI Jira defines ActionFit Jira automation rules. Read when creating Jira issues, discovering Jira tasks, changing Jira lifecycle/status behavior, editing Jira REST scripts, or handling Jira local config.
- `Library/PackageCache/com.actionfit.buildautomation@c8c8e9c80ad6/AI_GUIDE.md` - Build Automation manages BuildCommit requests, Git tag CI triggers, GitHub Actions mobile build workflows, and macOS self-hosted runner guidance. Read when changing automatic build request behavior, `.build/build_request.json`, workflow templates, or CI batchmode entry points.
- `Library/PackageCache/com.actionfit.buildsetting@6b1ebd93701d/AI_GUIDE.md` - Build Setting manages generic Android/iOS build/player settings. Read when changing Android/iOS build setup, versioning, signing, Addressables prebuild, or build menu behavior.
- `Library/PackageCache/com.actionfit.csvimporter@2e5e732f2870/AI_GUIDE.md` - CSV Importer imports CSV data into generated/scriptable data assets. Read when changing CSV parsing, table SO generation, Google Sheet sync, or import settings.
- `Library/PackageCache/com.actionfit.csvimporter_actionfit@a8ddbbcc22c7/AI_GUIDE.md` - CSV Importer ActionFit bootstraps ActionFit project `CsvImportConfig_SO` defaults and installs CSV Importer dependencies. Read when changing ActionFit-specific CSVImporter settings bootstrap, package metadata, or release flow.
- `Packages/com.actionfit.custompackagemanager/AI_GUIDE.md` - Custom Package Manager manages ActionFit UPM catalog install/update/remove/publish workflows. Read when changing package install state, manifest writes, catalog rows, history/changelog UI, AI guide routing, or publishing tools.
- `Library/PackageCache/com.actionfit.customsymbols@c50719b1668f/AI_GUIDE.md` - Custom Symbols manages scripting define symbols. Read when changing platform symbol presets, build inclusion rules, or symbol settings assets.
- `Library/PackageCache/com.actionfit.githubauth@90ca6c570eee/AI_GUIDE.md` - GitHub Auth provides shared GitHub credential diagnostics and user guidance for ActionFit Unity editor automation packages. Read when changing local GitHub authentication checks, push preflight behavior, token/credential guidance, or packages that depend on GitHub push/publish access.
- `Library/PackageCache/com.actionfit.sosingleton@0859788b601a/AI_GUIDE.md` - SO Singleton provides ScriptableObject singleton loading conventions. Read when changing singleton load paths, Resources/SO behavior, or singleton base APIs.

## Generated Project Index

When Custom Package Manager is loaded in a project that already has a primary AI markdown entry point, `ActionFitPackageAiGuideRouter` can generate a local `packages/actionfit-packages.md` compatibility pointer next to that entry point and link the package-shipped router from the entry point.

That generated index is a project-local compatibility pointer. This router is the package-shipped source of package guide routing.

## Maintenance Rules

- When a package adds or changes an `AI_GUIDE.md`, update that guide's `Project Router Registration` section.
- When a package's role or task routing changes, update that package guide's `Requested router entry`; Custom Package Manager will refresh the matching entry in this file.
- Keep this file focused on routing. Do not copy full package documentation here.
- Publishing remains manual. Updating this file does not imply GitHub push, tag creation, catalog upsert, or package publication.
