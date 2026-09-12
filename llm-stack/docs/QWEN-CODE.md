# Pointing Qwen Code at this stack

[Qwen Code](https://github.com/QwenLM/qwen-code) is an OpenAI-compatible CLI
agent, so it needs the same three things any client here needs -- a base URL, a
key, and trust for the stack's private CA -- plus one extra step that catches
people out, because it runs on Node.

Its config lives at `~/.qwen/settings.json`.

## The quick check

Nothing persistent, just prove the path works:

```bash
export OPENAI_BASE_URL="https://gateway.llm.localhost/v1"
export OPENAI_API_KEY="sk-..."          # YOUR key, not the master key
export OPENAI_MODEL="local"
export NODE_EXTRA_CA_CERTS="/path/to/llm-stack/config/traefik/certs/ca.crt"

qwen --approval-mode plan "Reply with one sentence saying which model you are."
```

`--approval-mode plan` means it can read and answer but cannot edit files or run
commands, which is what you want from a connectivity test.

**Use your own key, not `LITELLM_MASTER_KEY`.** The master key has no budget and
attributes usage to nobody, which defeats the per-person accounting the gateway
exists for. Mint one with `./scripts/llm-users.sh --apply`, from the admin panel
at `https://admin.<LLM_DOMAIN>`, or ask whoever runs the stack.

## The gotcha: Node does not use the system trust store

This stack serves TLS from a private CA (it must -- see MODELS.md and the TLS
note in traefik.yml; `.localhost` can never have a publicly-trusted
certificate). Adding that CA to the OS trust store is not enough for Qwen Code,
because Node ships its own bundled root list and ignores the system one.

The symptom is a TLS failure that looks like the stack is down:

```
self-signed certificate in certificate chain    (UNABLE_TO_VERIFY_LEAF_SIGNATURE)
```

`NODE_EXTRA_CA_CERTS` is the fix, and it must point at the CA certificate --
`ca.crt` -- not at the server certificate.

## Making it persistent

Add a provider to `~/.qwen/settings.json` alongside whatever you already have;
this does not disturb existing providers:

```json
{
  "env": {
    "LOCAL_LLM_API_KEY": "sk-...",
    "NODE_EXTRA_CA_CERTS": "/path/to/llm-stack/config/traefik/certs/ca.crt"
  },
  "modelProviders": {
    "openai": [
      {
        "id": "local",
        "name": "[Local] Qwen3.8-Flash-Next",
        "baseUrl": "https://gateway.llm.localhost/v1",
        "envKey": "LOCAL_LLM_API_KEY"
      }
    ]
  }
}
```

Then `qwen -m local`.

## Four things that will waste your time

**Give it room to think.** This model emits its reasoning in a separate
`reasoning_content` field. With a small `max_tokens` it will spend the whole
budget reasoning and return `finish_reason: "length"` with an EMPTY `content` --
which reads exactly like a broken model rather than a truncated one. A few
hundred tokens is a sensible floor.

**Point at the gateway, not the engine.** `gateway.<domain>` is LiteLLM: it
enforces your budget, rate limits and per-person attribution. `api.<domain>` is
the raw engine and bypasses all of it. The model name at the gateway is `local`.

**2FA does not apply here, and that is deliberate.** The portal requires a
second factor for humans, but the gateway authenticates with an API key and
`api.` accepts a machine token from the `client_credentials` grant, which
Authelia treats as 1FA by definition. Requiring 2FA on those would lock out
every automated caller while looking like a hardening win.

**One slot.** `LLAMACPP_PARALLEL=1` by default, so concurrent requests queue
rather than run in parallel, and two clients alternating will evict each other's
prefix cache. Raising it divides the context window between slots.

## Checking it works

```bash
# the model, through the gateway, with your key
curl --cacert config/traefik/certs/ca.crt \
  https://gateway.llm.localhost/v1/chat/completions \
  -H "Authorization: Bearer $YOUR_KEY" -H 'Content-Type: application/json' \
  -d '{"model":"local","messages":[{"role":"user","content":"Say hello."}],"max_tokens":400}'
```

If curl works and Qwen Code does not, it is `NODE_EXTRA_CA_CERTS` -- curl reads
`--cacert`, Node does not.

Measured on the reference box (RTX 3090, i7-13700K, UD-IQ4_XS at 256K): about
21-23 tok/s generation and ~400 tok/s prefill. See MODELS.md for the full table
and for why `-t` must be the physical core count.
