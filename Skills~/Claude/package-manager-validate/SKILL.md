---
name: package-manager-validate
description: Run read-only Custom Package Manager contract validation for one package, changed packages, or all embedded packages and interpret structured diagnostics.
---

# Validate Package Contracts

Validate the selected repository or worktree without changing package, catalog, manifest, installed-skill, Git, or external state.

1. Read repository instructions plus the Custom Package Manager `README.md` and `AI_GUIDE.md`.
2. Confirm the repository root and resolve the package from `Packages/com.actionfit.custompackagemanager`, otherwise `Library/PackageCache/com.actionfit.custompackagemanager@*`. Never edit PackageCache.
3. Select exactly one mode from the user's scope:
   - `--package <package-id>` for one physical embedded package;
   - `--changed --base-ref <ref>` for committed and uncommitted package changes;
   - `--all` only when all embedded ActionFit packages were explicitly requested.
4. Run the package-owned validator from the repository root, for example:

```bash
python3 Packages/com.actionfit.custompackagemanager/Tools~/package_contract_validator.py \
  --package com.actionfit.example \
  --base-ref origin/dev_jewoo

python3 Packages/com.actionfit.custompackagemanager/Tools~/package_contract_validator.py \
  --changed --base-ref origin/dev_jewoo \
  --output Temp/actionfit-package-contract.json
```

Use the resolved PackageCache path only when the validator package itself is downloaded. Add `--output` only when the user or repository workflow requests a durable JSON result.

5. Report the exact selection mode, base ref, selected packages, exit code, diagnostic codes and paths, and suggested fixes. Distinguish package failure (`1`) from infrastructure failure (`2`).

The validator does not compile Unity, run package tests, inspect catalogs or credentials, publish, deploy, or repair failures. Do not invoke those operations from this skill.
