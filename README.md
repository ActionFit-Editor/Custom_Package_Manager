# Custom Package Manager (com.actionfit.custompackagemanager)

ActionFit UPM package catalog viewer and installer for Unity. It installs packages into `Packages/manifest.json` as Git URL dependencies, applies selected versions, removes packages, and supports manual publish workflows for ActionFit editor packages.

## Install

```json
{
  "dependencies": {
    "com.actionfit.custompackagemanager": "https://github.com/ActionFit-Editor/Custom_Package_Manager.git#1.1.86"
  }
}
```

## Menu

- `Tools > Package > Custom Package Manager > Package Manager`: install, apply versions, remove packages, inspect package details, and check updates.
- `Tools > Package > Custom Package Manager > Manager Console`: create packages, publish changed or selected package versions, add schema v2 agent skills, open catalog/manifest files, and refresh the AI guide router.
- `Tools > Package > Custom Package Manager > Install or Refresh Agent Skills`: discovers registered skills from installed ActionFit packages and safely synchronizes their managed project-local copies.
- `Tools > Package > Custom Package Manager > Remove Managed Agent Skills`: removes only unchanged managed copies and disables automatic recreation until an explicit refresh.
- `Tools > Package > Custom Package Manager > Add Agent Skill`: adds a schema v2 skill to an editable embedded package and creates its mandatory package help skill on first use.
- `Tools > Package > <Package Name> > README`: opens that package's README in an editor window.
- `Tools > Package > <Package Name> > Setting SO`: focuses that package's settings ScriptableObject when the package has one.

## Package Agent Skills

Custom Package Manager의 `Install or Refresh Agent Skills`는 이 패키지 자체에서도 Codex와 Claude에 다음 read-only skill을 설치합니다.

- `package-manager-help`: 패키지 관리 기능, 설치된 스킬, 릴리스 준비와 안전 경계를 설명합니다.
- `package-manager-audit`: 로컬 manifest/lock/package metadata와 agent-skill 등록 상태를 변경 없이 점검합니다.
- `package-manager-validate`: 기존 `Tools~/package_contract_validator.py`를 한 패키지, 변경 패키지 또는 전체 embedded 패키지 범위로 실행합니다.

세 skill은 catalog refresh, manifest rewrite, install/update/embed/remove, installed skill refresh, publish, repository 생성, Git push/tag와 catalog append를 실행하지 않습니다.

An ActionFit package can register Codex and Claude skills with `Skills~/manifest.json`:

```json
{
  "schemaVersion": 2,
  "skillPrefix": "sample",
  "helpSkill": "sample-help",
  "skills": [
    {
      "name": "sample-help",
      "agents": ["codex", "claude"],
      "includeShared": false,
      "access": "read-only"
    },
    {
      "name": "sample-run",
      "agents": ["codex", "claude"],
      "includeShared": true,
      "access": "write-capable"
    }
  ]
}
```

Schema v2 requires an explicit lowercase `skillPrefix`, `helpSkill` equal to `<skillPrefix>-help`, and a registered help skill whose agents cover every agent used by the package. Every skill name starts with `<skillPrefix>-`; `access` is explicitly `read-only` or `write-capable`. The runtime installer temporarily continues to read schema v1 packages, but the package contract validator rejects v1 for changed or newly published packages.

Place sources at `Skills~/Codex/<skill-name>` and `Skills~/Claude/<skill-name>`. Each source must contain a `SKILL.md` whose frontmatter `name` matches the registration and whose `description` explains what the skill does and when to use it. Optional package-wide files under `Skills~/Shared` are overlaid only when `includeShared` is true; shared and agent sources must not contain the same relative file.

During installation, the manager generates `PACKAGE_SKILLS.md` inside each installed help skill from `package.json`, the schema v2 manifest, and the target agent's frontmatter descriptions. The generated file is included in the managed hash and is the help skill's authoritative inventory for package identity, related skills, `$name` invocation, and access. Do not author `PACKAGE_SKILLS.md` in package sources.

The manager installs registered sources into `.agents/skills/<skill-name>` and `.claude/skills/<skill-name>`. Installation runs after Editor load and package registration, but is skipped in batch mode. Managed ownership and hashes are stored in ignored `UserSettings/ActionFitPackageManager/skill-install-state.json`. Missing targets are installed, unchanged managed targets are updated, and unmanaged, modified, linked, or conflicting targets are preserved with a warning. Automatic synchronization never deletes a target when a package disappears; deletion is available only through the explicit removal menu and only for unchanged managed copies.

The Package Manager detail view shows each package's aggregate `Agent Skills` state: registered, current, update available, missing, preserved, and conflict counts. Embedded package rows also expose `Add Agent Skill`; downloaded packages must be embedded before their sources can be edited. The same scaffolding window is available from Manager Console.

Skill names are limited to lowercase letters, digits, and hyphens. The manifest cannot specify source or target paths, so packages cannot redirect installation outside the fixed package and project-local skill roots. Symbolic links and reparse points are rejected, and copied scripts are never executed during installation.

Projects previously managed by AI Jira keep their old `UserSettings/AIJira/skill-install-state.json`. Custom Package Manager reads that file as migration input, adopts ownership only when the target still matches its recorded hash, preserves the legacy file, and respects a previously disabled automatic-install preference.

## Package Contract Validator

`Tools~/package_contract_validator.py` validates embedded `Packages/com.actionfit.*` packages without starting Unity or contacting external services. Run it from the consuming Unity project root with Python 3:

```bash
# One package. Add --base-ref to enforce a version bump for changed files.
python Packages/com.actionfit.custompackagemanager/Tools~/package_contract_validator.py \
  --package com.actionfit.custompackagemanager \
  --base-ref origin/dev_jewoo

# Every package changed from the merge base of the supplied Git ref.
python Packages/com.actionfit.custompackagemanager/Tools~/package_contract_validator.py \
  --changed \
  --base-ref origin/dev_jewoo \
  --output Temp/actionfit-package-contract.json

# The current contract state of every embedded ActionFit package.
python Packages/com.actionfit.custompackagemanager/Tools~/package_contract_validator.py --all
```

The validator checks `package.json`, SemVer and changed-package version bumps, README install tags, `AI_GUIDE.md` identity/version/router entries, schema v2 prefix/help/access rules, registered skill sources and `SKILL.md` frontmatter, `ActionFitPackageInfo_SO`, and package asmdefs. It writes the same JSON schema to stdout and optional `--output`; every diagnostic includes `code`, `severity`, `path`, `line`, `message`, and `suggestedFix`.

Exit codes are stable for local automation and CI:

- `0`: every selected package passed.
- `1`: one or more package contract diagnostics have `severity: error`.
- `2`: the validator could not run reliably because of arguments, Git, repository, or result-output infrastructure.

`--changed` requires `--base-ref`. `--package` and `--all` can run without Git comparison, but version-bump enforcement is enabled only when `--base-ref` is supplied. The validator does not inspect catalogs, GitHub remotes, credentials, Unity compilation, or package tests.

When Unity invokes the package-owned validator from an isolated `file:` dependency, the Editor adapter resolves the real package repository root from the installed package path instead of assuming the disposable project's virtual `Packages` path is physical. Publish preparation also returns contract diagnostics before requiring a physical embedded folder or inspecting catalog, credential, and remote state.

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

Downloaded packages include `Embed for Edit` and `Fork as New`. `Embed for Edit` uses Unity Package Manager's official `Client.Embed` operation to materialize the downloaded package under `Packages/<packageId>/`. After Unity reports success, the manager validates `package.json`, removes cache-only `_fingerprint` metadata, normalizes `Packages/manifest.json` to `file:<packageId>`, and preserves catalog repository metadata so edits can be published back to the existing package repository. If a physical local package folder already exists and its `package.json` name matches, the tool can use that existing folder without overwriting it. The manager records a package file baseline under `UserSettings/ActionFitPackageManager/EmbeddedBaselines` so later conversion warnings can report whether files changed after embedding. Modifying a downloaded package requires this Embed for Edit flow; updating the upstream repository directly and hand-pushing a version tag is not an approved modification path because the catalog never records the new version.

Downloaded embedding is delegated to Unity Package Manager so Unity's virtual `Packages/<packageId>` mapping cannot redirect a custom folder move back into `Library/PackageCache`. Existing-folder conversion and `Fork as New` continue to use the atomic, journaled transaction shared by the UI and public AI API. Manifest writes use a verified temporary file and atomic replacement, and rollback restores affected dependency values before deleting any transaction-created package folder. Pending custom transactions are recorded under `UserSettings/ActionFitPackageManager/Transactions` and recovered after an Editor restart or domain reload. If dependency recovery cannot be verified, the local package folder is preserved and the API returns `RECOVERY_REQUIRED` with the journal path.

Unity can expose downloaded package cache contents through a logical `Packages/<packageId>` path even when no physical embedded folder exists. Conversion therefore uses guarded physical directory enumeration instead of `Directory.Exists` alone when deciding whether a reusable local folder exists. This prevents a downloaded cache projection from being mistaken for an embedded package and avoids writing a `file:` dependency without copying the package.

`Fork as New` copies the downloaded package into a new `Packages/<newPackageId>/` folder, rewrites package metadata for the new `com.actionfit.*` package ID, creates PackageInfo metadata for a new repository, removes the original manifest dependency, and writes the new local `file:` dependency. Both `1. Create Package` and `Fork as New` require an explicit `Public` or `Private` repository visibility choice; there is no silent public default, and API requests without `RepositoryVisibilitySpecified = true` fail validation. This prevents the source package and the fork from compiling duplicate assemblies at the same time. If the copy or validation fails, the manifest is left unchanged so Unity does not resolve a broken `file:` dependency. Embedded packages include `Use Downloaded`, which returns the package to the downloaded flow through the same protected replacement process used by embedded updates.

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
- Install or refresh agent skills: `Tools > Package > Custom Package Manager > Install or Refresh Agent Skills`.
- Remove managed agent skills: `Tools > Package > Custom Package Manager > Remove Managed Agent Skills`.
- Add a schema v2 agent skill: `Tools > Package > Custom Package Manager > Add Agent Skill`.
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

ActionFitPackageEmbedApi.EmbedForEditAsync(new ActionFitPackageEmbedRequest
{
    PackageId = "com.actionfit.example",
    Resolve = true,
}, embed => Debug.Log($"{embed.Code}: {embed.Message}"));

var skill = ActionFitPackageSkillScaffoldApi.Add(new ActionFitPackageSkillScaffoldRequest
{
    PackageId = "com.actionfit.example",
    SkillPrefix = "example",
    SkillName = "example-run",
    Description = "Run the example package workflow when explicitly requested.",
    Agents = new[] { "codex", "claude" },
    Access = "write-capable",
});
```

- `ActionFitPackageWorkflowApi.Inspect`: optionally refreshes the shared spreadsheet without confirmation UI, reads the latest catalog row, compares it with the installed version, reports embedded change state, and returns safe workflow options.
- `ActionFitPackageWorkflowApi.InspectJson`: JSON wrapper for Unity connectors and AI tools.
- `ActionFitPackageEmbedApi.GetCandidates`: lists installed ActionFit packages that have a downloadable source.
- `ActionFitPackageEmbedApi.Validate`: dry, read-only validation for `Embed for Edit`.
- `ActionFitPackageEmbedApi.EmbedForEditAsync`: conversion API used by the Package Manager UI; its callback receives the final `EMBEDDED` or failure result after Unity Package Manager finishes.
- `ActionFitPackageEmbedApi.EmbedForEdit`: start-oriented convenience API. Downloaded packages return `EMBED_STARTED`; callers that require final success must use the async overload or inspect package state afterward.
- `ActionFitPackageEmbedApi.ExecuteJson`: JSON start-result wrapper for Unity connectors and AI tools. A returned `EMBED_STARTED` is not the final conversion result.
- `ActionFitPackageEmbedApi.RecoverPendingTransactions`: explicit recovery entry point in addition to automatic Editor-load recovery.
- `ActionFitPackageSkillScaffoldApi.Add` / `AddJson`: adds a schema v2 package skill to an embedded package without overwriting existing sources. The first addition creates `<skillPrefix>-help` for Codex and Claude plus Codex `agents/openai.yaml` metadata.
- `ActionFitPackagePublishApi.Prepare`: runs the local package contract first, then refreshes the catalog, blocks reused versions, validates package metadata/authentication, checks the GitHub repository and immutable tag, and returns a content-bound plan ID without changing external state. When the catalog source URL differs from a selected Private target, it also compares actual visibility, default branch, every branch/tag ref, and target documentation before preparing a repository migration.
- `ActionFitPackagePublishApi.Execute`: re-runs every preflight and requires the same plan ID plus the exact `RequiredApprovalText` before repository push, tag push, and catalog upsert. Repository relocation additionally requires `ApproveRepositoryMigration = true` and the exact separate `MigrationApprovalText`.
- `ActionFitPackagePublishApi.PrepareJson` / `ExecuteJson`: JSON wrappers for AI connectors. Execution never infers approval from preparation.
- `ActionFitPackagePublishApi.PrepareCatalogRecovery` / `ExecuteCatalogRecovery`: verifies an existing immutable remote tag against the local package, requires an exact recovery approval, and appends only the missing catalog row without pushing `main` or tags. JSON wrappers are available as `PrepareCatalogRecoveryJson` / `ExecuteCatalogRecoveryJson`.
- `ActionFitPackageBulkPublishApi.PrepareAllChanged`: discovers changed embedded packages or validates an explicit package ID list, refreshes catalog/GitHub state, and returns one content-bound bulk plan without external changes. A package whose immutable tag already exists while its Catalog version is missing is verified and classified under `CatalogRecoveryPackageIds` instead of failing as a normal publish conflict.
- `ActionFitPackageBulkPublishApi.ExecuteAll`: requires the exact bulk plan ID, exact publish approval, exact repository-creation package set, and—when needed—the exact migration and Catalog recovery approvals. Approved migrations finish first; only `PublishPackageIds` enter repository workers, while verified recovery rows join the Catalog batch without any repository action.
- `ActionFitPackageBulkPublishApi.PrepareAllChangedJson` / `ExecuteAllJson`: JSON wrappers for AI connectors. The Manager Console's `Publish All Changed` button calls this same API.

Batchmode callers can use:

- `-executeMethod ActionFitPackageEmbedCli.Run` with `-actionFitEmbedRequest <request.json>` and `-actionFitEmbedResult <result.json>`. Do not add Unity's `-quit` option: this command waits for the asynchronous embed result, writes the result file, and exits Unity itself.
- `-executeMethod ActionFitPackageWorkflowCli.Run` with `-actionFitInspectRequest <request.json>` and `-actionFitInspectResult <result.json>`.
- `-executeMethod ActionFitPackagePublishCli.Prepare` or `ActionFitPackagePublishCli.Execute` with `-actionFitPublishRequest <request.json>` and `-actionFitPublishResult <result.json>`.
- `-executeMethod ActionFitPackageBulkPublishCli.PrepareAllChanged` or `ActionFitPackageBulkPublishCli.ExecuteAll` with `-actionFitBulkPublishRequest <request.json>` and `-actionFitBulkPublishResult <result.json>`.

Inspection is advisory and never publishes. Workflow options mark repository publishing with `RequiresExplicitPublishApproval`; AI must not push, tag, create a repository, or append a catalog row unless the user explicitly requests that external action.

`Prepare` is always read-only. A successful plan returns an exact approval string such as `PUBLISH com.actionfit.example@1.2.3 PLAN <planId>`. `Execute` rejects missing or mismatched approval, rechecks the refreshed catalog and remote tag, and rejects a changed content hash or plan. New repository creation additionally requires `ApproveRepositoryCreation = true`. Public-to-Private repository relocation has a separate `MIGRATE ... PLAN <planId>` approval and never changes, archives, or deletes the source repository. If repository push succeeds but catalog upsert fails, the result reports `RetryCatalogAppendAvailable = true` instead of pushing the repository again.

Custom Package Manager scans installed `AI_GUIDE.md` files from embedded `Packages/com.actionfit.*` folders and Git UPM `Library/PackageCache/com.actionfit.*@*` folders, then refreshes `PACKAGE_AI_GUIDE_ROUTER.md` from their `Requested router entry` blocks. Router entries are rewritten to the actual discovered guide path, so Git UPM packages point at `Library/PackageCache/...@hash/AI_GUIDE.md`. When a consuming project already has a primary AI markdown entry point, it also generates a `packages/actionfit-packages.md` compatibility pointer next to that entry point and adds an auto-managed section so the project-level AI router can discover `PACKAGE_AI_GUIDE_ROUTER.md`.

If an AI assistant reads this package documentation before the automatic router has registered it, the package `AI_GUIDE.md` exposes its requested router entry and `PACKAGE_AI_GUIDE_ROUTER.md` tells the assistant where the router should be linked.

## Manager Console

- `1. Create Package`: requires an explicit `Public` or `Private` repository visibility choice, then creates the `Packages/com.actionfit.*` package skeleton, README, AI guide, README-only package menu file, asmdef, and PackageInfo SO. Creation validation rejects requests that omit the explicit choice, and the completed skeleton must pass the package-owned round-trip contract validator before creation returns successfully.
- `2. Publish Changed`: normal publish path and the second Manager Console action. It finds top-level `Packages/com.actionfit.*` packages whose local `package.json` version is higher than the catalog latest version, includes newly created packages that are not yet registered, and uses the same approval-gated API for single and bulk publication. Nested `package.json` files under test fixtures or package content are not publish candidates. `Publish All Changed` separates normal publish rows from verified Catalog recovery rows, asks for a separate exact recovery approval, runs only normal repository publishes with up to 4 workers, then appends both groups by one Catalog batch request. Each package's `Repository Visibility` in `ActionFitPackageInfo_SO` selects the public/private GitHub profile.
- `Add Agent Skill`: the third Manager Console action. It opens the same no-overwrite schema v2 scaffolding window used by embedded package detail rows. The first addition creates the mandatory help sources for Codex and Claude; later additions update only the manifest and newly requested source paths.
- `Publish Package`: manual publish path for an already registered package when you need to type a specific version. After writing that version, it enters the same approval-gated preflight, migration, repository publish, and catalog sequence as `Publish Changed`.
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

Publish preflight also supports a repository that exists but has no first commit yet. GitHub's empty-repository conflict response from the tag lookup is treated as an available tag while the repository remains classified as existing. Git command output streams are drained concurrently so large warning output, including line-ending warnings, cannot block publication.

`Publish All Changed` validates package contracts once before approval, reuses the same in-process approved plan during execution, and rechecks the mutable catalog, content hash, version, repository, tag, and recovery equivalence immediately before mutation. Deserialized/API-supplied plan data cannot bypass contract validation because its validation receipt is not serialized. A matching existing tag is marked `Recover Catalog only` in the plan; content or visibility mismatches remain blocked and recommend the next patch version. GitHub remote preflight and normal repository publishing run with up to 4 workers. Recovery candidates never enter repository workers and require `CatalogRecoveryApprovalText` separately from the publish approval. The progress dialog identifies local validation, GitHub checks, repository publishing, catalog batch/fallback, and final refresh stages and can cancel before mutation or stop before catalog registration after repository publishing completes.

When an existing catalog repository URL differs from the selected Private publish target, `Publish Changed` treats it as an explicit repository migration. Preflight checks both repositories' actual GitHub visibility, default branch, all branch/tag refs, current-version tag conflicts, and requires both `README.md` and `AI_GUIDE.md` to reference the target URL. A missing target may be created only with the existing creation approval; source branches and tags are then mirrored without force or prune against the target, the target default branch is aligned, and all refs are rechecked. Existing Public targets and conflicting target refs are blocked. The source repository is never modified, archived, deleted, or made Private. The package version and catalog URL are published only after migration verification, so a failed or partially completed attempt leaves catalog rows unchanged and can be retried safely.

Catalog and GitHub HTTP requests use a 30-second connection/read timeout. The catalog Web App should support `upsertPackageVersions` and return either a matching `count` or per-item confirmations. Unsupported batch responses fall back to serial `upsertPackageVersion`; timeout and cancellation failures do not start a potentially long serial fallback. If repository publishing succeeds but catalog append fails or is canceled, the window keeps those rows and shows `Retry Catalog Append` so the spreadsheet update can be retried without pushing repositories again. The Unity Console logs elapsed milliseconds for catalog refresh, GitHub preflight, repository publish, batch append, serial fallback, and total bulk execution.

If the window-held retry rows are no longer available after a window recreation, domain reload, or Editor restart, use `Recover Catalog Entry` on the changed package row. Recovery requires the version to be absent from the refreshed catalog, the immutable remote tag and repository visibility to match, and the checked-out tag content to match the local package after only safe `_fingerprint`, JSON whitespace, and Unity PackageInfo YAML serialization normalization. A mismatch is blocked and recommends the next patch version. A successful recovery performs only catalog upsert and refresh; it never creates a repository or pushes, moves, deletes, or overwrites a branch or tag.

`Settings` stores one GitHub token in `GitHub Publish Default` and separate repository creation organizations for public and private repositories. Fill `_githubToken` once, then set `Repo Creation - Public` and `Repo Creation - Private` org values when the repository owners differ. Private catalog entries can point at private GitHub repositories, so consuming projects still need GitHub access to install them.

Before preparing package contents, the publisher refreshes the local publish clone from `origin/main` so an older cached clone does not affect the prepared state.
