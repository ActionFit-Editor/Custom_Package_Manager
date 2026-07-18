---
name: package-manager-update-dependencies
description: Plan and explicitly apply dependency version updates across physical embedded ActionFit packages, then prepare separately approved dependency-safe publishing.
---

# Update Embedded Package Dependencies

Use the package-owned Python planner for dependency metadata changes and the existing Unity APIs for Catalog refresh and publishing. Planning is read-only. Applying files and publishing packages are separate write operations that each require explicit user approval.

1. Read repository instructions plus the Custom Package Manager `README.md` and `AI_GUIDE.md`.
2. Confirm that the target repository contains physical top-level `Packages/com.actionfit.*` package roots. Never edit `Library/PackageCache`, downloaded packages, project overrides, nested fixtures, or linked package roots.
3. Ask an already-running Unity connector to call `ActionFitPackageWorkflowApi.Inspect` with `RefreshCatalog = true`. Pass `--catalog-refreshed` only when that result explicitly reports a successful refresh. Do not infer freshness from a local CSV timestamp.
4. From the repository root, create a read-only plan:

    python3 Packages/com.actionfit.custompackagemanager/Tools~/package_dependency_updater.py plan \
      --repo-root . \
      --catalog-refreshed

Omit `--catalog-refreshed` when refresh cannot be proven; the result remains plan-only and cannot be applied. Add `--allow-major` only after the user explicitly approves major dependency changes.

5. Report every affected package, old/new package version, dependency change, local-ahead publish prerequisite, warning, conflict, publish layer, `planId`, and `requiredApprovalText`. Cycles, malformed SemVer, missing physical dependencies, project overrides, or unapproved major changes block apply.
6. Never infer approval from the original request or from approval to create the plan. Apply only after the user explicitly confirms the exact current `requiredApprovalText`, then rebuild and execute the same content-bound plan:

    python3 Packages/com.actionfit.custompackagemanager/Tools~/package_dependency_updater.py apply \
      --repo-root . \
      --catalog-refreshed \
      --expected-plan-id <exact-plan-id> \
      --approval '<exact-required-approval-text>'

The apply command atomically updates only affected `package.json`, README install tag, AI guide version, and PackageInfo release note files. It runs package contract validation and rolls every write back if validation fails. Re-plan after any file or Catalog change; never reuse stale approval.

7. Report validation results and the resulting publish layers. Applying dependency files does not authorize publishing.
8. Publish only when the user separately asks to publish after successful apply. For each dependency-safe layer, use `ActionFitPackageBulkPublishApi.PrepareAllChanged`, present its exact content-bound approval and repository-creation approvals, and call `ExecuteAll` only after those exact approvals. Complete one layer successfully before preparing the next; stop on any repository, tag, validation, or Catalog failure.

Do not implement GitHub push, tag creation, Catalog append, credential access, or direct network publishing in Python. Never print tokens or raw authentication errors. Do not run install/update/embed/remove or installed-skill synchronization from this skill.
