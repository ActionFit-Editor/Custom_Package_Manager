# Content Bundle Installer Contract

This contract defines a reusable, self-removing Git UPM installer for an ActionFit content package family.

## Package Shape

- Keep the installer Editor-only and dependency-free so it can be added directly by Git URL.
- Store `Editor/ContentBundleProfile.json` beside an `[InitializeOnLoad]` bootstrap.
- Use a schema-version 2 profile for new installers. Declare every installable package once, then map package IDs into coherent modules.
- Mark the minimal Origin/Core closure as `required: true` modules. Mark supported UI, animation, SDK adapter, and project-convention leaves with explicit `defaultSelected` values.
- Pin the Custom Package Manager and every bundle package to canonical HTTPS Git URLs. Use an exact SemVer tag whenever the repository publishes one. A repository with no version tag may use only a full 40-character immutable commit while keeping the package's declared SemVer in `version`; branches, short commits, and floating revisions are forbidden.

## Bootstrap Sequence

1. Skip automatic execution when the installer itself is embedded for development.
2. Locate `ActionFitContentBundleApi` by reflection. The installer must not compile against the manager assembly.
3. When the API is absent, inspect the existing manager dependency. Add or upgrade only a missing or older canonical exact-tag dependency through `Client.Add`; preserve embedded, local, forked, branch, unparseable, and newer canonical sources.
4. After the API is registered, load the profile and invoke `InstallJson(profileJson)` so default modules are selected. A project may later use `PlanModifyModules` and `ModifyModules` to change optional leaves.
5. Let the manager plan and journal all manifest/state changes. The installer must not write `Packages/manifest.json` itself.
6. The manager removes `bootstrapPackageId` only after selected required packages are registered and ownership state reloads successfully.

## Module And Override Rules

- Schema version 1 remains accepted as one legacy all-packages selection.
- Schema version 2 requires every package to belong to at least one module. Required modules cannot be deselected. Shared packages may appear in multiple modules and are installed once.
- Active selections are committed to `ProjectSettings/ActionFitContentBundles.json`. Direct removal of a selected package is blocked; optional packages are removed through a reviewed module-change plan.
- `Project Override` is distinct from `Embed for Edit`. It accepts only PackageInfo-declared Public packages, and committed state contains the credential-free public base repository URL, project-relative path, and base tag/revision/content hashes. Generated AI state omits the remote URL, and overrides are excluded from single and bulk upstream publish plans.
- Restore an override to a downloaded base package, or fork it under a new package ID and repository. Never silently publish project-specific edits upstream.

Start from `Editor/Templates~/ContentBundleInstaller/`. Replace every `__PLACEHOLDER__`, keep the reflection boundary, and add installer-specific tests for manager-source classification, profile validation, and automatic cleanup.
