# Content Bundle Installer Template

Copy these files into a new public Editor-only installer package, replace all `__PLACEHOLDER__` values, and follow `Editor/Documentation/ContentBundleInstallerContract.md`.

The bootstrap intentionally resolves Custom Package Manager by reflection. Its manager bootstrap must preserve non-canonical or embedded manager sources and may add only the exact canonical manager Git tag declared by the installer.
