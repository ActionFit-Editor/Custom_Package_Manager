# Custom Package Manager (com.actionfit.custompackagemanager)

ActionFit UPM package catalog viewer and installer for Unity. It installs packages into `Packages/manifest.json` as Git URL dependencies, applies selected versions, removes packages, and supports manual publish workflows for ActionFit editor packages.

## Install

```json
{
  "dependencies": {
    "com.actionfit.custompackagemanager": "https://github.com/ActionFit-Editor/Custom_Package_Manager.git#1.1.36"
  }
}
```

## Menu

- `Tools > ActionFit > Package Manager > Package Manager`: install, apply versions, remove packages, and inspect updates.
- `Tools > ActionFit > Package Manager > Manager Console`: create packages, create repositories, publish packages, open README/catalog/manifest/settings.

## Package Manager

- `Reload`: reloads the active catalog and current package install state.
- `Update Catalog`: downloads the local catalog CSV from the configured spreadsheet/web app.
- `Settings`: selects `Assets/_Data/_CustomPackageManager/ActionFitPackageCatalogSettings_SO.asset`.
- `Updates`: shows installed packages whose catalog latest version is higher than the current version.
- `Console`: opens the Manager Console.

Package sections are grouped as Package Manager, Embedded Packages, Downloaded Packages, and Available Packages. Git/registry dependencies in `Packages/manifest.json` are shown as Downloaded Packages. Local `file:` dependencies or package folders under `Packages/` without a manifest dependency are shown as Embedded Packages.

Package sections are sorted by package community score, `likes - dislikes`, highest first. When the catalog spreadsheet Web App exposes `package_vote_summary`, `Update Catalog` imports `likes`, `dislikes`, `vote_score`, and `comment_count` into the local catalog CSV.

Downloaded packages include `Embed for Edit`. This copies the resolved package source from Unity's package cache into `Packages/<packageId>/`, writes `file:<packageId>` to `Packages/manifest.json`, and runs Package Manager resolve so the package becomes editable as a local embedded package. If the local package folder already exists and its `package.json` name matches, the tool can use that existing folder instead of copying over it. Embedded packages include `Use Downloaded`, which writes the selected catalog Git UPM version back to `Packages/manifest.json`, removes the local folder, and returns the package to the downloaded flow.

After editing an embedded package, bump its `package.json` version above the catalog latest version before using `Publish Changed`.

## Community Feedback

Each package detail view includes a `Community` foldout.

- `Like` and `Dislike` send one vote per anonymous project ID and package.
- The anonymous project ID is stored at `UserSettings/ActionFitPackageManager/community_id.txt` and is not a user account or machine identity.
- Clicking the already selected vote is disabled. Switching from `Like` to `Dislike`, or the reverse, updates the same project vote instead of adding another vote.
- Comments use a `Title` and `Description`. Comment titles are shown as foldouts so users can scan titles first and open only the descriptions they want to read.
- Each project can keep one editable comment per package.

The configured catalog Web App must support `votePackage`, `getPackageComments`, and `upsertPackageComment`. See `Editor/Documentation/PackageCommunityWebAppContract.md` for the required sheet and response contract.

## Updates

The `Updates` panel shows installed packages only when the catalog latest version is higher than the current installed version.

- Downloaded packages can be updated individually, by selection, or all at once.
- Embedded packages are shown too. Selecting a different version converts them to Git UPM dependencies.
- `Changes` shows changelog rows between the installed version and the selected target version.
- `History` shows all catalog changelog rows for the package.
- If the installed package is newer than the catalog latest version, it stays out of the `Updates` panel to avoid accidental downgrade.

For example, updating from `1.0.1` to `1.0.4` shows the changelog rows for `1.0.2`, `1.0.3`, and `1.0.4` at display time. Those rows are not stored inside the newest release note.

## Changelog Rules

Each package's `ActionFitPackageInfo_SO.ReleaseNote` must contain only the single version being prepared. Do not accumulate old changelog entries in the newest release note.

Package Manager composes `History` and `Changes` from separate catalog version rows. Release notes do not need headings such as `## 1.1.28`; the UI already displays the version label.

## AI Guide

Every ActionFit package should ship an `AI_GUIDE.md` at the package root. This file lets AI assistants in consuming projects understand package-specific rules without access to this source project's `Docs/AI` folder.

- `README.md`: human-facing setup and usage.
- `AI_GUIDE.md`: AI-facing package identity, editing rules, release-note rules, migration notes, and the package's requested router entry.
- `PACKAGE_AI_GUIDE_ROUTER.md`: package-shipped AI router for choosing which installed package `AI_GUIDE.md` should be read for a task, plus the request to link this router from the project's default AI reading sequence.
- `package.json`: package ID, version, Unity version, and dependencies.
- `Editor/PackageInfo/ActionFitPackageInfo_SO.asset`: catalog metadata and release note source.

When package behavior changes, update that package's `AI_GUIDE.md` before publishing. Custom Package Manager reads each package's `Requested router entry` and refreshes `PACKAGE_AI_GUIDE_ROUTER.md` automatically.

Custom Package Manager scans installed `AI_GUIDE.md` files from embedded `Packages/com.actionfit.*` folders and Git UPM `Library/PackageCache/com.actionfit.*@*` folders, then refreshes `PACKAGE_AI_GUIDE_ROUTER.md` from their `Requested router entry` blocks. Router entries are rewritten to the actual discovered guide path, so Git UPM packages point at `Library/PackageCache/...@hash/AI_GUIDE.md`. When a consuming project already has a primary AI markdown entry point, it also generates a `packages/actionfit-packages.md` compatibility pointer next to that entry point and adds an auto-managed section so the project-level AI router can discover `PACKAGE_AI_GUIDE_ROUTER.md`.

If an AI assistant reads this package documentation before the automatic router has registered it, the package `AI_GUIDE.md` exposes its requested router entry and `PACKAGE_AI_GUIDE_ROUTER.md` tells the assistant where the router should be linked.

## Manager Console

- `1. Create Package`: creates the `Packages/com.actionfit.*` package skeleton, README, AI guide, asmdef, and PackageInfo SO.
- `2. Create Repo`: creates/checks the GitHub repository and first catalog row for packages not yet registered. The create window has a `Public` / `Private` selector.
- `3. Publish Package`: publishes an already registered package version.
- `Publish Changed`: finds packages whose local `package.json` version is higher than the catalog latest version and publishes them.
- `README`: opens this README in a dedicated window.
- `Open Catalog`: selects the local or fallback catalog CSV.
- `Open Manifest`: opens the project `Packages/manifest.json`.
- `Settings`: selects the catalog settings SO.
- `Refresh AI Guide Router`: refreshes `PACKAGE_AI_GUIDE_ROUTER.md`, regenerates the local `packages/actionfit-packages.md` compatibility pointer next to the discovered AI entry point, and refreshes that entry point's auto-managed package guide section when one exists. The router code now keeps AI entry point registration behind adapter-style helpers so additional AI tools can be added without duplicating package guide scanning.

## Catalog And Manifest

- Local catalog path: `Assets/_Data/_CustomPackageManager/package_catalog.csv`.
- Fallback catalog path: `Packages/com.actionfit.custompackagemanager/Editor/Catalog/package_catalog.csv`.
- Optional community summary sheet: `package_vote_summary`.
- Installing or applying a version updates `Packages/manifest.json` and runs Unity Package Manager resolve.
- Manifest dependency formatting is normalized when written.

## Publish Notes

This package does not automatically publish itself. To publish, open Unity and run `Tools > ActionFit > Package Manager > Manager Console`, then use `3. Publish Package` or `Publish Changed`.

`Settings` stores separate repository creation profiles for public and private repositories. Fill `Repo Creation - Public` and `Repo Creation - Private` with the GitHub org and token that should own each kind of repository. If those profile-specific values are empty, the publisher falls back to the legacy `GitHub Publish Default` org/token for compatibility. Private catalog entries can point at private GitHub repositories, so consuming projects still need GitHub access to install them.

Before pushing package contents, the publisher refreshes the local publish clone from `origin/main` so an older cached clone does not trigger a non-fast-forward push rejection.
