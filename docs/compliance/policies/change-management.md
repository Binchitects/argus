# Change and vulnerability management policy

> A template. Replace each [bracket], remove what does not apply to you,
> approve it and set its review date. Arena's own development is described in
> [development.md](../../development.md); this policy is about how [Company]
> changes the Arena it runs.

| | |
|---|---|
| Owner | [IT] |
| Approved by | [Name, role, date] |
| Review | every year |

## Changes

1. A change to the Arena installation (an upgrade, a setting that weakens a
   control, a new model outside the company, a new module) is recorded in
   [the change log / tickets] with what, why, the risk, the rollback and who
   approves it. Settings changes are audited by Arena (Admin → Audit).
2. Upgrades: read the release's changelog, including any **Weakened controls**;
   take a backup; try the upgrade first on [a test installation] with a copy of
   the data (`./arenactl upgrade --dry-run` in a checkout, or
   `sh argus-arena-VERSION-offline.run upgrade --dry-run` with the offline
   installer); apply it in [the maintenance window]; run
   `./arenactl check compliance` after.
3. Rollback is a restore of the backup taken before (`./arenactl restore
   --from DIR`).
4. Emergency changes (a security fix) may be made first and recorded within
   [one working day].
5. Test data is not production data; where a copy of it must be used, it is
   protected as production is and deleted after.
6. Hosts are configured from [the installer and `.env`, kept in version
   control without secrets]; drift is checked [how].

## Vulnerabilities

1. Each Arena release lists what it ships (its SBOM,
   `argus-arena-VERSION.cdx.json`, attached to the release) and has passed a
   scan of its packages against OSV; [IT] keeps the SBOM of the installed
   release.
2. [IT] follows advisories for the hosts, Docker, the drivers and Arena, and
   applies fixes: critical within [7 days], high within [30 days], the rest
   with the next maintenance.
3. Hosts are patched [monthly]; their exposed ports are scanned [quarterly].
4. A penetration test of the installation [every year], by [whom].

## Development [Company] does itself

Plugins, agents, apps and tools [Company] builds for Arena are reviewed by a
second person, tested, and run in Arena's sandbox. Outsourced development is
under the [supplier policy](suppliers.md).

## Records

The change records, the SBOMs of the releases installed, scan and test reports.
Kept [six years].
