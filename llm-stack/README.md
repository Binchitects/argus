# LLMService

A self-hosted LLM service: GPU inference, a chat UI, an API gateway with a key and a
budget per person, single sign-on, an admin panel and dashboards.

**Everything you need to deploy it is in the top-level README:
[Deploying the LLM stack](../README.md#deploying-the-llm-stack--read-this-part).**
The short version:

```bash
cp env-samples/qwen3.8-flash-next.rtx5090.env .env
```

```bash
awk '/^# SECRETS/{s=1} /^# PEOPLE/{s=0} s && /^[A-Z0-9_]+=$/{c="openssl rand -hex 24"; c|getline r; close(c); if ($0 ~ /^LITELLM_/) r="sk-" r; $0=$0 r} {print}' .env > .env.new && mv .env.new .env && chmod 600 .env
```

```bash
make up
```

`make up` runs `scripts/preflight.sh` first, then `docker compose up -d`. The
preflight is worth the extra second: if this checkout has MOVED since the stack
was last started, Docker has already created empty directories at the old
absolute paths and the containers bind to those instead. Nothing errors -- you
get Authelia crash-looping on a missing config, Alertmanager on a missing
`alertmanager.yml`, the temperature exporter on a missing `exporter.py` and
Traefik exiting 127, four unrelated-looking failures that all name files which
plainly exist on disk. The preflight says "the containers were created from
/old/path" instead. Use `make preflight` to run it alone.

For a host with no network, `make airgap` writes a self-contained bundle to
`dist/`: every image, optionally the model weights and knowledge packs, a
generated `.env` with the secrets emptied, and a loader. On the isolated host it
is `cp llm-stack/.env.airgap llm-stack/.env && ./fill-secrets.sh && ./load.sh --up`
-- no registry, no Hugging Face, no build. See "Deploying without a network" in
the top-level README.

`.env` is the whole configuration: model, paths, domain, power limits, secrets.
`env-samples/` holds complete, measured deployments for Qwen3.8-Flash-Next and
Qwen3.8-27B on an RTX 5090.

Two references go with this file:

* **[docs/ARCHITECTURE.md](docs/ARCHITECTURE.md) — every service, the network and
  volumes, the three authentication mechanisms, the request flows, and a table of
  what each failure actually means.
* **[docs/CONFIGURATION.md](docs/CONFIGURATION.md) — every `.env` variable and
  every file under `config/`, with what breaks when each is wrong.

`docs/` otherwise holds reference material and measurement history; the top-level
README is authoritative where they differ.
