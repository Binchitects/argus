# Backup and restore

Most of what Argus keeps on disk is a **cache of GitLab**. Losing it costs
time, not information. Exactly one thing is irreplaceable, and knowing which
is the whole procedure.

## What is worth backing up

| On disk | Rebuildable? | Backed up |
|---|---|---|
| `index.db` — files, symbols, includes, `repo_deps` | yes, from GitLab | **yes** |
| `index.db` — the `audit` table | **no** | **yes** — the reason this exists |
| `config.yaml` | from memory, badly | **yes** |
| Knowledge packs | yes, with Ollama + sources + an hour | **yes** |
| `mirrors/`, `trees/` | yes, from GitLab | **no** |

`audit` records what the assistant showed which developer. No rebuild recovers
it, and it is the one table you would be asked for after an incident.

`mirrors/` and `trees/` are excluded deliberately. They dominate the footprint
and every byte is re-fetchable. Backing them up trades a large recurring cost
for a shorter one-off restore — the wrong way round.

## Taking a backup

```bash
argus backup --config /etc/argus/config.yaml --out /backups/argus/$(date +%F)
```

In a container deployment:

```bash
docker compose exec server argus backup \
    --config /etc/argus/config.yaml --out /var/lib/argus/backup
```

Output names what it took and what it refused:

```
index.db      29.8 MB  (7 repos, integrity ok)
audit rows    0  <- the only data no rebuild recovers
config.yaml   copied
packs         0
mirrors/ and trees/ deliberately excluded: re-fetchable from GitLab
```

**The indexer does not need to be stopped.** The index is copied with SQLite's
`VACUUM INTO`, never a file copy. A plain `cp` of a live database can capture
a torn page mid-transaction; `VACUUM INTO` takes a consistent point-in-time
snapshot and compacts it on the way out.

That is verified rather than assumed: a backup taken while a writer committed
2,675 rows produced a snapshot at 1,785 rows with `integrity_check: ok` — a
clean point in time, not a tear. The command runs `integrity_check` on its own
output and **fails rather than reporting success** if the copy is bad.

A missing index is an error, not an empty backup. A backup that silently
succeeds with nothing in it is the worst outcome, because it is only
discovered during a restore.

## Restoring

Restore is copying one file back.

```bash
systemctl stop argus            # or: docker compose down
cp /backups/argus/2026-08-07/index.db /var/lib/argus/index.db
cp /backups/argus/2026-08-07/config.yaml /etc/argus/config.yaml
cp -r /backups/argus/2026-08-07/packs/. /var/lib/argus/packs/   # if any
docker compose up -d
```

**Mirrors do not need restoring, and nothing waits for them.** Verified by
drill: against a restored `index.db` with no `mirrors/` directory at all,
`find_symbol`, `which_repo`, `repo_map` and `index_status` all answer
immediately. The next indexing pass re-clones what it needs in the background.

Expect the first pass after a restore to be slow — it re-fetches every mirror.
Everything keeps answering from the restored index while it runs.

### Verify the restore, don't assume it

```bash
argus status --config /etc/argus/config.yaml
```

Every repo should be listed. Then make one real query through `/mcp` as a
developer, not through `/healthz` — `/healthz` bypasses the auth middleware
and returns 200 whether or not the server can serve anything. See
`docs/deployment.md` for the exact call.

## If there is no backup

Everything except `audit` rebuilds from GitLab:

```bash
argus index --config /etc/argus/config.yaml
```

**Budget for this before you need it.** The only measured figure is 1,199
files in 14.8 s on four small C projects, which does not extrapolate to a real
estate. Time your first production pass and write the number here — that
number *is* your recovery time objective, and until it exists this section is
a guess.

Knowledge packs rebuild separately and need Ollama with the pinned model:

```bash
argus pack build --source python --work-dir /tmp/cpython \
    --out python.arguspack --version 3.13 --fetch
```

Or reinstall them from wherever they are published, which is faster.

## What this procedure does not cover

- **Point-in-time recovery.** Snapshots are whole-file. There is no WAL
  archiving and no way to roll to an arbitrary moment.
- **Retention and rotation.** Deliberately left to whatever already backs up
  the host; a second scheduler here would be one more thing to monitor.
- **Off-host copies.** `--out` writes locally. Getting the directory
  off the machine is the existing backup system's job.
- **A measured restore time**, which needs a production-scale index.
