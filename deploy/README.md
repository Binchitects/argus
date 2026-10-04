# Argus Arena: the deployment

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
