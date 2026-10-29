# Argus Arena: the deployment

From the repository's root, on Linux:

```bash
./arenactl doctor --for install   # what this host lacks, and how to get it
./arenactl install                # builds the images, then installs, with .env and the certificate made
```

[docs/scripts.md](../docs/scripts.md) has every command: the offline
installer (`./arenactl bundle --with-deps`), upgrades, repairs, backups, the
checks. By hand instead:

```bash
cp .env.example .env      # the domain, the models folder, the first model, six secrets
docker compose up -d      # https://llm.localhost
```

Every module runs; leave one out in `docker-compose.override.yml`
(`services: { videogen: { profiles: [off] } }`). Rootless Podman:
`podman compose -f docker-compose.yml -f podman.yml up -d`.

- [docs/deployment.md](../docs/deployment.md): what runs, certificates, models,
  Podman, upgrading from v3, backups and restore, rollback, an offline install,
  testing a deployment.
- [docs/configuration.md](../docs/configuration.md): `.env`, the files under
  `config/`, the volumes.
