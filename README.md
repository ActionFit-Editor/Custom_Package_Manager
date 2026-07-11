# Custom Package Manager (com.actionfit.custompackagemanager)

ActionFit UPM package catalog viewer and installer for Unity. It installs packages into `Packages/manifest.json` as Git URL dependencies, applies selected versions, removes packages, and supports manual publish workflows for ActionFit editor packages.

## Install

```json
{
  "dependencies": {
    "com.actionfit.custompackagemanager": "https://github.com/ActionFit-Editor/Custom_Package_Manager.git#1.1.59"
  }
}
```

## Menu

- `Tools > Package > Custom Package Manager > Package Manager`: install, apply versions, remove packages, inspect package details, and check updates.
- `Tools > Package > Custom Package Manager > Manager Console`: create packages, publish changed package versions, publish selected package versions, open catalog/manifest files, and refresh the AI guide router.
- `Tools > Package > <Package Name> > README`: opens that package's README in an editor window.
- `Tools > Package > <Package Name> > Setting SO`: focuses that package's settings ScriptableObject when the package has one.

## Package Manager

- `Reload`: reloads the active catalog and current package install state.
- `Update Catalog`: downloads the local catalog CSV from the configured spreadsheet/web app.
- `Force Update`: runs `Update Catalog`, lists downloaded packages, and re-applies their catalog latest Git UPM URLs to `Packages/manifest.json` after confirmation. Embedded packages are skipped.
- `Check Update`: shows installed packages whose catalog latest version is higher than the current version.
- `Console`: opens the Manager Console.

Package sections are grouped as Package Manager, Embedded Packages, Downloaded Packages, and Available Packages. Git/registry dependencies in `Packages/manifest.json` are shown as Downloaded Packages. Local `file:` dependencies or package folders under `Packages/` without a manifest dependency are shown as Embedded Packages.

Package sections are sorted by package community score, `likes - dislikes`, highest first. When the catalog spreadsheet Web App exposes `package_vote_summary`, `Update Catalog` imports `likes`, `dislikes`, `vote_score`, and `comment_count` into the local catalog CSV.

Package README and settings access live in the Unity top menu, not inside the Package Manager package rows. Each package gets `Tools > Package > <Package Name> > README`; packages that own or bootstrap a shared settings ScriptableObject also get `Setting SO` in the same separated lower menu group. Each package owns its own menu file and uses its package-specific asset path or safe factory method for `Setting SO`.

`Tools > Package` must stay grouped by menu priority: the package-wide Custom Package Manager entry is first, packages with executable tool commands come next, packages with only `Setting SO` + `README` come after that, and README-only packages are placed at the bottom. Keep separator gaps between those groups. Inside a package root, real tool commands stay above the separated lower `Setting SO` and `README` entries.

Use the established priority bands unless a package has a documented reason to differ: Package Manager `0-9`, executable tools `20-99`, `Setting SO` + `README` only packages `600-699`, and README-only packages `900-999`. New packages created by `1. Create Package` include a README-only package menu file by default.

Downloaded packages include `Embed for Edit` and `Fork as New`. `Embed for Edit` copies the resolved package source from Unity's package cache into a temporary folder, validates the copied `package.json`, moves it into `Packages/<packageId>/`, writes `file:<packageId>` to `Packages/manifest.json`, and preserves catalog repository metadata so edits can be published back to the existing package repository. If the local package folder already exists and its `package.json` name matches, the tool can use that existing folder instead of copying over it. The manager records a package file baseline under `UserSettings/ActionFitPackageManager/EmbeddedBaselines` so later conversion warnings can report whether files changed after embedding.

Package conversion now uses an atomic, journaled transaction shared by the UI and public AI API. The package folder is validated before the manifest is replaced, manifest writes use a verified temporary file and atomic replacement, and rollback restores the affected dependency values before deleting any transaction-created package folder. Pending operations are recorded under `UserSettings/ActionFitPackageManager/Transactions` and recovered after an Editor restart or domain reload. If dependency recovery cannot be verified, the local package folder is preserved and the API returns `RECOVERY_REQUIRED` with the journal path.

Unity can expose downloaded package cache contents through a logical `Packages/<packageId>` path even when no physical embedded folder exists. Conversion therefore uses guarded physical directory enumeration instead of `Directory.Exists` alone when deciding whether a reusable local folder exists. This prevents a downloaded cache projection from being mistaken for an embedded package and avoids writing a `file:` dependency without copying the package.

`Fork as New` copies the downloaded package into a new `Packages/<newPackageId>/` folder, rewrites package metadata for the new `com.actionfit.*` package ID, creates PackageInfo metadata for a new repository, removes the original manifest dependency, and writes the new local `file:` dependency. This prevents the source package and the fork from compiling duplicate assemblies at the same time. If the copy or validation fails, the manifest is left unchanged so Unity does not resolve a broken `file:` dependency. Embedded packages include `Use Downloaded`, which returns the package to the downloaded flow through the same protected replacement process used by embedded updates.

Before an embedded package is replaced, the manager reports whether it changed since `Embed for Edit`, warns that local modifications are not merged, and creates a timestamped safety copy under `UserSettings/ActionFitPackageManager/EmbeddedBackups/<packageId>/`. The manifest is changed only after every required backup succeeds. If an embedded folder cannot be moved to the operating system trash, the manager restores the original manifest and any package folders already moved during that operation. Backups remain until they are removed manually.

After editing an embedded package, bump its `package.json` version above the catalog latest version before using `Publish Changed`.

## Community Feedback

Each package detail view includes a `Community` foldout.

- `Like` and `Dislike` share one vote per anonymous project ID and package.
- The anonymous project ID is stored at `UserSettings/ActionFitPackageManager/community_id.txt` and is not a user account or machine identity.
- After a project submits either `Like` or `Dislike`, both vote buttons are disabled for that package. The first submitted vote is final.
- Comments use a `Title` and `Description`. Comment titles are shown as foldouts so users can scan titles first and open only the descriptions they want to read.
- Each project can keep one editable comment per package.

The configured catalog Web App must support `votePackage`, `upsertPackageComment`, and returning the `package_comments` sheet during `Update Catalog`. See `Editor/Documentation/PackageCommunityWebAppContract.md` for the required sheet and response contract.

## Check Update

The `Check Update` panel shows installed packages only when the catalog latest version is higher than the current installed version.

- Downloaded packages can be updated individually, by selection, or all at once. `Select Downloaded` and `Update Downloaded` never select embedded packages automatically.
- Embedded packages are shown too, but must be selected explicitly or updated with `Convert & Update`. Selecting a different version creates a safety backup and converts the package to a Git UPM dependency; local modifications are not merged.
- `Changes` shows changelog rows between the installed version and the selected target version.
- `History` shows all catalog changelog rows for the package.
- Package detail rows expose the same `Changes` and `History` behavior inline under the expanded package.
- `Latest Git` opens the package's catalog latest GitHub repository tag in the default browser.
- If the installed package is newer than the catalog latest version, it stays out of the `Check Update` panel to avoid accidental downgrade.

`Force Update` is separate from `Check Update`. It first refreshes the catalog, then shows a confirmation list of downloaded packages and writes the catalog latest URL for each listed package. This can refresh same-version manifest entries and dependency URLs without touching embedded packages.

For example, updating from `1.0.1` to `1.0.4` shows the changelog rows for `1.0.2`, `1.0.3`, and `1.0.4` at display time. Those rows are not stored inside the newest release note.

## Changelog Rules

Each package's `ActionFitPackageInfo_SO.ReleaseNote` must contain only the single version being prepared. Do not accumulate old changelog entries in the newest release note.

Package Manager composes `History` and `Changes` from separate catalog version rows. Release notes do not need headings such as `## 1.1.28`; the UI already displays the version label.

## Unity Menu

- Package root: `Tools > Package > Custom Package Manager`.
- README: `Tools > Package > Custom Package Manager > README`.
- Setting SO: `Tools > Package > Custom Package Manager > Setting SO`.
- Package commands stay under the same package root and appear above the separated README/Setting SO entries when those entries exist.
- `Tools > Package` group order must remain: package-wide manager, executable tool packages, Setting SO + README-only packages, README-only packages.
- Priority bands: Package Manager `0-9`, executable tools `20-99`, Setting SO + README-only `600-699`, README-only `900-999`.

## AI Guide

Every ActionFit package should ship an `AI_GUIDE.md` at the package root. This file lets AI assistants in consuming projects understand package-specific rules without access to this source project's `Docs/AI` folder.

- `README.md`: human-facing setup and usage.
- `AI_GUIDE.md`: AI-facing package identity, editing rules, release-note rules, migration notes, and the package's requested router entry.
- `PACKAGE_AI_GUIDE_ROUTER.md`: package-shipped AI router for choosing which installed package `AI_GUIDE.md` should be read for a task, plus the request to link this router from the project's default AI reading sequence.
- `package.json`: package ID, version, Unity version, and dependencies.
- `Editor/PackageInfo/ActionFitPackageInfo_SO.asset`: catalog metadata and release note source.

When package behavior changes, update that package's `AI_GUIDE.md` before publishing. Custom Package Manager reads each package's `Requested router entry` and refreshes `PACKAGE_AI_GUIDE_ROUTER.md` automatically.

### Public AI APIs

The APIs below are Editor-only, public, static, dialog-free, and return serializable result objects.

```csharp
var inspection = ActionFitPackageWorkflowApi.Inspect(new ActionFitPackageInspectionRequest
{
    PackageId = "com.actionfit.example",
    RefreshCatalog = true,
});

var validation = ActionFitPackageEmbedApi.Validate(new ActionFitPackageEmbedRequest
{
    PackageId = "com.actionfit.example",
    DryRun = true,
});

var embed = ActionFitPackageEmbedApi.EmbedForEdit(new ActionFitPackageEmbedRequest
{
    PackageId = "com.actionfit.example",
    Resolve = true,
});
```

- `ActionFitPackageWorkflowApi.Inspect`: optionally refreshes the shared spreadsheet without confirmation UI, reads the latest catalog row, compares it with the installed version, reports embedded change state, and returns safe workflow options.
- `ActionFitPackageWorkflowApi.InspectJson`: JSON wrapper for Unity connectors and AI tools.
- `ActionFitPackageEmbedApi.GetCandidates`: lists installed ActionFit packages that have a downloadable source.
- `ActionFitPackageEmbedApi.Validate`: dry, read-only validation for `Embed for Edit`.
- `ActionFitPackageEmbedApi.EmbedForEdit`: recoverable conversion used by both AI and the Package Manager UI.
- `ActionFitPackageEmbedApi.ExecuteJson`: JSON wrapper for Unity connectors and AI tools.
- `ActionFitPackageEmbedApi.RecoverPendingTransactions`: explicit recovery entry point in addition to automatic Editor-load recovery.
- `ActionFitPackagePublishApi.Prepare`: refreshes the catalog, blocks reused versions, validates package metadata/authentication, checks the GitHub repository and immutable tag, and returns a content-bound plan ID without changing external state.
- `ActionFitPackagePublishApi.Execute`: re-runs every preflight and requires the same plan ID plus the exact `RequiredApprovalText` before repository push, tag push, and catalog upsert.
- `ActionFitPackagePublishApi.PrepareJson` / `ExecuteJson`: JSON wrappers for AI connectors. Execution never infers approval from preparation.

Batchmode callers can use:

- `-executeMethod ActionFitPackageEmbedCli.Run` with `-actionFitEmbedRequest <request.json>` and `-actionFitEmbedResult <result.json>`.
- `-executeMethod ActionFitPackageWorkflowCli.Run` with `-actionFitInspectRequest <request.json>` and `-actionFitInspectResult <result.json>`.
- `-executeMethod ActionFitPackagePublishCli.Prepare` or `ActionFitPackagePublishCli.Execute` with `-actionFitPublishRequest <request.json>` and `-actionFitPublishResult <result.json>`.

Inspection is advisory and never publishes. Workflow options mark repository publishing with `RequiresExplicitPublishApproval`; AI must not push, tag, create a repository, or append a catalog row unless the user explicitly requests that external action.

`Prepare` is always read-only. A successful plan returns an exact approval string such as `PUBLISH com.actionfit.example@1.2.3 PLAN <planId>`. `Execute` rejects missing or mismatched approval, rechecks the refreshed catalog and remote tag, and rejects a changed content hash or plan. New repository creation additionally requires `ApproveRepositoryCreation = true`. If repository push succeeds but catalog upsert fails, the result reports `RetryCatalogAppendAvailable = true` instead of pushing the repository again.

Custom Package Manager scans installed `AI_GUIDE.md` files from embedded `Packages/com.actionfit.*` folders and Git UPM `Library/PackageCache/com.actionfit.*@*` folders, then refreshes `PACKAGE_AI_GUIDE_ROUTER.md` from their `Requested router entry` blocks. Router entries are rewritten to the actual discovered guide path, so Git UPM packages point at `Library/PackageCache/...@hash/AI_GUIDE.md`. When a consuming project already has a primary AI markdown entry point, it also generates a `packages/actionfit-packages.md` compatibility pointer next to that entry point and adds an auto-managed section so the project-level AI router can discover `PACKAGE_AI_GUIDE_ROUTER.md`.

If an AI assistant reads this package documentation before the automatic router has registered it, the package `AI_GUIDE.md` exposes its requested router entry and `PACKAGE_AI_GUIDE_ROUTER.md` tells the assistant where the router should be linked.

## Manager Console

- `1. Create Package`: creates the `Packages/com.actionfit.*` package skeleton, README, AI guide, README-only package menu file, asmdef, and PackageInfo SO.
- `2. Publish Changed`: normal publish path. It finds packages whose local `package.json` version is higher than the catalog latest version, includes newly created packages that are not yet registered, prepares local publish clones, creates missing repositories, pushes package contents/tags, and appends catalog rows. `Publish All Changed` runs repository publishes up to 4 packages at once, then appends all catalog rows by one batch request when the Web App supports it. Each package's `Repository Visibility` in `ActionFitPackageInfo_SO` selects the public/private GitHub profile for both new and already registered package publishes.
- `Publish Package`: manual publish path for an already registered package version when you need to type a specific version before publishing.
- `Open Catalog`: selects the local or fallback catalog CSV.
- `Open Manifest`: opens the project `Packages/manifest.json`.
- `Refresh AI Guide Router`: refreshes `PACKAGE_AI_GUIDE_ROUTER.md`, regenerates the local `packages/actionfit-packages.md` compatibility pointer next to the discovered AI entry point, and refreshes that entry point's auto-managed package guide section when one exists. The router code now keeps AI entry point registration behind adapter-style helpers so additional AI tools can be added without duplicating package guide scanning.

## Catalog And Manifest

- Local catalog path: `Assets/_Data/_CustomPackageManager/package_catalog.csv`.
- Fallback catalog path: `Packages/com.actionfit.custompackagemanager/Editor/Catalog/package_catalog.csv`.
- Optional community summary sheet: `package_vote_summary`.
- Optional community comments sheet: `package_comments`, cached under `UserSettings/ActionFitPackageManager/package_comments.csv` when `Update Catalog` runs.
- Installing or applying a version updates `Packages/manifest.json` and runs Unity Package Manager resolve.
- Catalog `dependencies` entries are applied before the selected package. ActionFit package dependencies must exist in the catalog so they can be written as Git UPM URLs; non-ActionFit registry dependencies fall back to their raw version string.
- Expanded package rows show the selected version's dependencies. ActionFit dependencies show the resolved catalog Git URL when available; registry/raw dependencies show the raw version value.
- `Update Catalog` preserves quoted multi-line CSV cells such as release notes, so later columns like `dependencies` stay aligned and show correctly in package details.
- Manifest dependency formatting is normalized when written.

## Publish Notes

`Publish Package` and `Publish Changed` refresh the catalog before uploading, block publishing when the refreshed catalog already contains the same `package_id@version`, create or refresh the local publish clone, commit copied package files, push `main`, push the version tag when needed, and append the catalog row. When a duplicate version is found, the default policy is to stop instead of overwriting the existing Git tag/catalog row; change `package.json` or `Publish Version`, or use `Fork as New` when the package should become a separate package/repository. The Unity Console prints `[ActionFitPackageManager]` logs for repository check, clone path, file copy, commit/tag, branch push, tag push, and catalog append steps.

`Publish All Changed` snapshots the selected packages before upload, runs only the GitHub repository publish step in parallel, and appends catalog rows after every repository publish succeeds. The catalog Web App should support `upsertPackageVersions` and return either a matching `count` or per-item confirmations. If the Web App does not support that batch action yet, the tool falls back to serial `upsertPackageVersion` requests. If repository publish succeeds but catalog append fails, the window keeps those rows and shows `Retry Catalog Append` so the spreadsheet update can be retried without pushing repositories again.

`Settings` stores one GitHub token in `GitHub Publish Default` and separate repository creation organizations for public and private repositories. Fill `_githubToken` once, then set `Repo Creation - Public` and `Repo Creation - Private` org values when the repository owners differ. Private catalog entries can point at private GitHub repositories, so consuming projects still need GitHub access to install them.

Before preparing package contents, the publisher refreshes the local publish clone from `origin/main` so an older cached clone does not affect the prepared state.
