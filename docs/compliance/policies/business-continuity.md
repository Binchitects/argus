# Backup and business continuity policy

> A template. Replace each [bracket], remove what does not apply to you,
> approve it and set its review date. Where Arena does part of a rule, the
> script or setting is named ([deployment](../../deployment.md)).

| | |
|---|---|
| Owner | [IT] |
| Approved by | [Name, role, date] |
| Review | every year, and after every drill |

## How critical Arena is

[Arena is used for ...]. Without it, people [work without the models / ...]
for up to [the recovery time objective: e.g. 1 working day]; at most [the
recovery point objective: e.g. 1 day] of chats and files may be lost.

## Backups

1. `deploy/scripts/backup.sh` runs daily by its timer (`BACKUP_TIME`, 03:30):
   the database, people's files, the volumes that hold state, and the settings,
   encrypted with the key in `BACKUP_KEY_FILE`, which is kept apart from the
   backups ([where the key is kept, who holds a copy]).
2. Backups are kept [14] (`BACKUP_KEEP`) in `BACKUP_DIR`, a verified second
   copy on another disk (`BACKUP_COPY_DIR`), and a copy [weekly] off the site
   in [location].
3. A failed backup, or none good for a day, raises a security alert to every
   admin; [who] acts on it the same day.

## Restores, proven

1. `deploy/scripts/restore-test.sh` restores the newest backup into a stack of
   its own and checks it [every month], and after every upgrade.
2. A full drill, a new host from the backup and the installer, [every year]:
   timed against the recovery time objective, written up, and what it taught
   applied.

## Capacity

Admin's dashboards show the models' load, the disks' room and the queues; [IT] reviews them [monthly] and plans hardware [a quarter] ahead.
Alerts warn before a disk fills.

## When the host is lost

1. [Who] decides it is a disaster and tells [whom].
2. A spare host (or [a new one, ordered within N days]) is installed with the
   offline installer of the release that ran, and the newest backup restored
   (`backup.sh --restore`).
3. While it is down: [what people do instead; who tells them].
4. Emergency mode: [who may reach health information, and how, while Arena is
   down].

## Records

Backup results (each backup's `RESULT` and `MANIFEST`), the restore tests,
the drills' write-ups. Kept [six years].
