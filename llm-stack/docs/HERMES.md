# Pointing Hermes at this stack

Two independent halves: **the model**, served by the gateway, and **Argus**,
an MCP server for code and documentation lookup. Either works without the
other.

Hermes' config lives at:

| | |
|---|---|
| Windows | `%LOCALAPPDATA%\hermes\config.yaml` |
| Linux/macOS | `~/.config/hermes/config.yaml` |

## The model

```yaml
model:
  base_url: https://gateway.llm.localhost/v1
  api_key: sk-...                 # YOUR key, from llm-users.sh
  name: local                     # or the served name, e.g. qwen3.8-27b
```

**Use your own key, not `LITELLM_MASTER_KEY`.** The master key is a superuser
credential with no budget: usage through it is attributed to nobody and
bounded by nothing, which quietly defeats the per-person accounting the whole
gateway exists for. Mint yours with:

```bash
./scripts/llm-users.sh --apply       # prints each person's key once
```

**Prefer `local` over the checkpoint name.** `local` is an alias that follows
whatever the engine is serving, so `switch-model` does not strand your config
pointing at a model that is no longer loaded.

## Argus

```yaml
mcp_servers:
  argus:
    url: https://argus.llm.localhost/mcp
    headers:
      Authorization: Bearer <your GitLab personal access token>
```

Argus resolves every request's identity against GitLab, so the token is a
GitLab PAT with `read_api` — **your own**, not the indexing service token.
What you can see through Argus is exactly what you can see in GitLab.

## Three things that will waste your time

**Proxy exclusions.** If `HTTP_PROXY`/`HTTPS_PROXY` are set, `NO_PROXY` must
include the stack's domain or requests are sent to a corporate proxy and fail
with a bare "Connection error" that names nothing:

```
NO_PROXY=localhost,127.0.0.1,::1,.local,llm.localhost,.llm.localhost
```

**Name resolution.** `*.localhost` resolves in browsers but **not** in curl,
SDK clients, or Hermes. Run `./scripts/setup-hosts.sh` once on the client
machine.

**Hermes caches MCP schemas.** After changing anything about a tool server,
clear it or you are testing the previous description:

```
%LOCALAPPDATA%\hermes\cache\mcp_schema_cache.json
```

That cache cost an afternoon here: a tool description change appeared to have
no effect, because the model was still being handed the old text.

## Checking it works

```bash
# the model, through the gateway, with your key
curl https://gateway.llm.localhost/v1/chat/completions \
  -H "Authorization: Bearer sk-..." -H 'Content-Type: application/json' \
  -d '{"model":"local","messages":[{"role":"user","content":"hi"}],"max_tokens":16}'
```

Then confirm the call landed on **you** — Grafana → **Usage by person**, or:

```bash
curl "https://gateway.llm.localhost/user/info?user_id=you@example.com" \
  -H "Authorization: Bearer $LITELLM_MASTER_KEY"
```

If spend did not move, the request was attributed to nobody — almost always
the master key in `api_key`, or a repeated prompt served from cache.

## What the local model can and cannot do

Tool calling needs a model that can hold context across several tool calls and
choose sensibly. Open WebUI's own documentation names current frontier models
as a reasonable minimum and says small models will not manage it. An 8B will
struggle to drive Argus well; the 27B is better but still not a frontier
model. If Argus tool use disappoints, that is a model limit rather than a
wiring fault — check the wiring with the curl above before concluding
otherwise.
