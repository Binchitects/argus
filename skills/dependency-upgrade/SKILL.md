---
name: dependency-upgrade
title: Upgrade a dependency
description: Plan and carry out a dependency or framework upgrade safely — what changed between the versions, what in this code it breaks, the steps, and how to check and roll back. Use for a library, runtime, framework or base image upgrade, or a security update.
parameters:
  - { name: dependency, description: "The dependency, and the versions from and to" }
---
Upgrade {{dependency}} ($ARGUMENTS) safely.

1. **What changed**: the release notes and changelog between the two versions — breaking changes, deprecations, new requirements (runtime, platform), security fixes.
2. **What it touches here**: find every use in this code (imports, configuration, build files, lock files, Dockerfiles) and which of the changes affect them.
3. **Steps**, small and checked: update the version and the lock file, fix each break, replace what is deprecated, run the build and the tests after each.
4. **Risks**: behaviour that changes silently (defaults, timeouts, serialisation), transitive dependencies that move, and what to test by hand.
5. **Rollback**: how to go back if it fails after release.

Change only what the upgrade needs. List what you could not verify.
