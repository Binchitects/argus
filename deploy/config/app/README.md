# config/app

The app's writable settings folder, mounted at `/settings`. The Settings page
saves changes to `.env` values here as `pending.env`. That file can hold a
secret, so it is mode 0600 and git ignores it. Apply them on the host with:

```bash
./scripts/apply-settings.sh
```

The script shows each change, asks, writes `.env` (keeping a backup), and runs
`docker compose up -d`, which recreates only what changed.
