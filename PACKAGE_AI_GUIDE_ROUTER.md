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

- `Packages/com.actionfit.atlassuspender/AI_GUIDE.md` - Atlas Suspender manages SpriteAtlas suspend/restore workflows. Read when changing atlas packable-reference handling, sprite atlas move safety, or related editor tooling.
- `Packages/com.actionfit.buildsetting/AI_GUIDE.md` - Build Setting manages ActionFit build/player settings. Read when changing Android/iOS build setup, versioning, signing, Addressables prebuild, or build menu behavior.
- `Packages/com.actionfit.checkmissingscripts/AI_GUIDE.md` - Check Missing Scripts scans assets for missing MonoBehaviour references. Read when changing missing-script scan, cleanup, or report workflows.
- `Packages/com.actionfit.csvimporter/AI_GUIDE.md` - CSV Importer imports CSV data into generated/scriptable data assets. Read when changing CSV parsing, table SO generation, Google Sheet sync, or import settings.
- `Packages/com.actionfit.custompackagemanager/AI_GUIDE.md` - Custom Package Manager manages ActionFit UPM catalog install/update/remove/publish workflows. Read when changing package install state, manifest writes, catalog rows, history/changelog UI, AI guide routing, or publishing tools.
- `Packages/com.actionfit.customsymbols/AI_GUIDE.md` - Custom Symbols manages scripting define symbols. Read when changing platform symbol presets, build inclusion rules, or symbol settings assets.
- `Packages/com.actionfit.devicepreview/AI_GUIDE.md` - Device Preview manages editor device preview shortcuts and layouts. Read when changing preview UI, target devices, or preview menu behavior.
- `Packages/com.actionfit.filenamereplace/AI_GUIDE.md` - File Name Replace manages batch file renaming. Read when changing rename rules, before/after replacement workflows, or file rename safety.
- `Packages/com.actionfit.findfolder/AI_GUIDE.md` - Find Folder manages saved folder/file focus shortcuts. Read when changing GUID/path tracking, shortcut storage, migration, or Project window focusing.
- `Packages/com.actionfit.playmodelogsaver/AI_GUIDE.md` - Play Mode Log Saver saves editor play mode logs. Read when changing log capture, save paths, or play mode log settings.
- `Packages/com.actionfit.sosingleton/AI_GUIDE.md` - SO Singleton provides ScriptableObject singleton loading conventions. Read when changing singleton load paths, Resources/SO behavior, or singleton base APIs.
- `Packages/com.actionfit.texturebatchimporter/AI_GUIDE.md` - Texture Batch Importer manages batch texture and atlas import settings. Read when changing texture importer presets, sprite import settings, or atlas batch workflows.

## Generated Project Index

When Custom Package Manager is loaded in a project that already has a primary AI markdown entry point, `ActionFitPackageAiGuideRouter` can generate a local `packages/actionfit-packages.md` compatibility pointer next to that entry point and link the package-shipped router from the entry point.

That generated index is a project-local compatibility pointer. This router is the package-shipped source of package guide routing.

## Maintenance Rules

- When a package adds or changes an `AI_GUIDE.md`, update that guide's `Project Router Registration` section.
- When a package's role or task routing changes, update that package guide's `Requested router entry`; Custom Package Manager will refresh the matching entry in this file.
- Keep this file focused on routing. Do not copy full package documentation here.
- Publishing remains manual. Updating this file does not imply GitHub push, tag creation, catalog upsert, or package publication.
