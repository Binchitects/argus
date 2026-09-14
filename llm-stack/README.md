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
docker compose up -d
```

`.env` is the whole configuration: model, paths, domain, power limits, secrets.
`env-samples/` holds complete, measured deployments for Qwen3.8-Flash-Next and
Qwen3.8-27B on an RTX 5090. `docs/` holds reference material and measurement history; the
top-level README is authoritative where they differ.
