# Physical security policy

> A template. Replace each [bracket], remove what does not apply to you,
> approve it and set its review date. Arena runs on [Company]'s own hardware,
> so this part is wholly the company's.

| | |
|---|---|
| Owner | [Facilities, with IT] |
| Approved by | [Name, role, date] |
| Review | every year |

## Where the Arena hosts are

[Server room / data centre: address]. Its perimeter is [walls, locked doors];
entry is by [badge] for [the people listed], visitors are signed in and
accompanied, and entries are logged and kept [90 days]. The room is watched by
[cameras, alarms].

## Protecting the equipment

1. Power: [UPS for N minutes, generator]; cooling: [how]; fire: [detection,
   suppression]; water: [nothing above the racks].
2. Cables are [in trays, labelled]; network ports are [disabled unless used].
3. Equipment is maintained by [whom], with records kept; outside engineers are
   accompanied.
4. Equipment away from the site (a laptop with an export, a disk) is encrypted
   and never left unattended.

## Media and disposal

1. The data disks of the Arena hosts are encrypted (LUKS or the like; Admin →
   Compliance checks this). Backups are encrypted by Arena
   (`BACKUP_KEY_FILE`).
2. A disk leaving service is [wiped with NIST 800-88 purge, or shredded], and a
   record is kept of which disk, when and by whom.
3. Removable media holding [Company]'s data are [allowed only encrypted /
   not allowed].

## Records

The entry log, maintenance records, the disposal records. Kept [six years].
