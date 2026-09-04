# Per-person API keys, quotas and usage accounting

Every person gets their own API key, their own ceiling, and one usage total
that counts their API calls **and** their chat messages together.

## The problem this solves

Per-person API keys were always attributable: LiteLLM records spend against
the key that made the call. The web UI was not.

Open WebUI authenticates to the gateway with a **single shared key**
(`WEBUI_BACKEND_KEY`, defaulting to the master key). Every chat message from
every person therefore arrived as spend against that one key. The gateway
could tell you the web UI had spent $40 this month and could not tell you who
spent it -- and a limit you cannot attribute is a limit you cannot enforce.

## How the two surfaces become one identity

The stack keys everything on **email address**, because it is the only
identifier Authelia, Open WebUI and LiteLLM all agree on. An Open WebUI user
id is a row id in Open WebUI's own database and means nothing anywhere else.

```
  Authelia    issues email over OIDC
      |
  Open WebUI  forwards it as X-OpenWebUI-User-Email
      |         (ENABLE_FORWARD_USER_INFO_HEADERS, docker-compose.yml)
      v
  LiteLLM     maps that header onto internal_user
      ^         (user_header_mappings, config/litellm/config.yaml)
      |
  API key     minted with user_id = the same email
                (scripts/sync-llm-users.py)
```

Both paths resolve to one internal user, so one person has one running total
and one ceiling.

**This is why keys must be minted by the script rather than by hand.** A key
created in the LiteLLM UI without a `user_id` records spend against nothing in
particular; it will not appear in that person's total, and the numbers quietly
stop meaning what they claim to.

## Setting it up

1. Put people in `config/authelia/team.yml` with an `email`, and optionally
   `max_budget`, `budget_duration`, `tpm_limit`, `rpm_limit`,
   `max_parallel_requests`.

2. Set the stack-wide default in `.env`:

   ```
   LITELLM_DEFAULT_USER_BUDGET=50
   LITELLM_BUDGET_DURATION=1mo
   ```

   Not `0`. LiteLLM reads 0 as a budget of zero and refuses every request,
   which presents exactly like a broken gateway.

3. Plan, then apply:

   ```bash
   ./scripts/llm-users.sh            # prints what would change
   ./scripts/llm-users.sh --apply    # creates users and keys
   ```

   Each key is printed **once**. LiteLLM stores only a hash and the script
   deliberately writes none of them to disk -- one file containing every
   person's key is a worse failure than a re-issue. To replace a lost key:

   ```bash
   ./scripts/llm-users.sh --rotate alice@example.com --apply
   ```

## Reading the numbers

The LiteLLM admin UI at `https://gateway.<LLM_DOMAIN>/ui` shows spend per user
and per key, and is the quickest answer to "who used what".

For dashboards, query Postgres directly. LiteLLM's `/metrics` endpoint is an
enterprise feature on current builds -- which is why the Prometheus job for it
is commented out in `config/prometheus/prometheus.yml` -- but the spend tables
are plain Postgres and are the same data the UI reads:

```sql
-- spend per person this month, both surfaces together
SELECT "user", SUM(spend) AS spend, SUM(total_tokens) AS tokens,
       COUNT(*) AS calls
  FROM "LiteLLM_SpendLogs"
 WHERE "startTime" >= date_trunc('month', now())
 GROUP BY "user"
 ORDER BY spend DESC;
```

## Which key belongs to whom

A recurring confusion, because LiteLLM models them as separate entities: a
**key** is a credential, a **person** is an identity, and one person may hold
several keys. `LiteLLM_SpendLogs` stores only the **hashed** key in `api_key`,
so the two look unrelated until you join them.

The link is `LiteLLM_VerificationToken.user_id`. Resolve a request to a person
in this order — the **Usage by person** dashboard does exactly this:

```sql
coalesce(
  nullif(s."user", ''),                    -- attributed at request time
  s.metadata->>'user_api_key_user_id',     -- the key's owner, denormalised
  v.user_id,                               -- the key's owner, via join
  nullif(s.end_user, ''),                  -- the customer record
  '(unattributed)'
)
```

**Prefer `metadata` over the join.** LiteLLM copies the key's alias and owner
into each spend row, so attribution **survives the key being deleted**. A plain
join to `LiteLLM_VerificationToken` silently drops those rows — measured here,
98 of 219 rows had no surviving key to join to, and every one of them still
carried its owner in `metadata`.

To see the inventory directly:

```bash
docker compose exec -T postgres psql -U "$POSTGRES_USER" -d litellm -c   'select key_alias, user_id, key_name, spend, max_budget from "LiteLLM_VerificationToken" order by spend desc'
```

`key_name` is masked. The full key is shown **once**, when it is created, and is
never stored in the clear — if someone loses theirs, mint a new one rather than
trying to recover it.

### `(master key)` means attributed to nobody

`LITELLM_MASTER_KEY` is a superuser credential with **no owner and no budget**.
It does not appear in `LiteLLM_VerificationToken`, so its requests resolve to
`default_user_id` or to nothing at all — usage that cannot be billed to a
person and that no ceiling binds. That is the single most common reason a
person's total looks too low.

The **Unattributed usage** panel exists to surface it. Anything listed there is
a client still configured with the master key; give it a real one with
`./scripts/llm-users.sh --apply`.

## What happens at the limit

Measured against a live gateway on both surfaces.

A person over their ceiling is refused with **HTTP 429**, naming them:

    ExceededBudget: End User=proxytest@example.com over budget.
    Spend=6.8e-06, Budget=4e-06

**Enforcing chat took a second mechanism, and this is worth understanding
before changing any of it.** LiteLLM checks an internal-user budget against a
key that user *owns*. Chat does not use anyone's key -- every message arrives
on one shared key with the person named only in a header. So the header alone
attributes spend and stops nothing, which was measured: an over-budget person
was refused through their API key and served normally through chat.

Three things were tried. Mapping the header to `customer` instead does not
enforce either. Mapping it to `internal_user` *and* `customer` breaks both.
What does work is the OpenAI `user` field in the request body, which LiteLLM
checks against the end-user budget -- so `deploy/identity-proxy` sits between
Open WebUI and the gateway and copies the identity from the header into that
field. Nothing else.

    Open WebUI --(shared key + X-OpenWebUI-User-Email)--> identity-proxy
                --(same, plus "user": <email> in the body)--> LiteLLM

The header is passed through untouched, so attribution keeps working exactly
as before; the body field is what makes the ceiling bind. Measured, same
person and same over-budget state: direct to the gateway, four calls and
never refused; through the proxy, refused on the second.

This is why `scripts/llm-users.sh` provisions each person **twice** -- once as
an internal user, once as an end user, same email and same ceiling. Skip the
end-user half and chat is attributed and unlimited.

`max_parallel_requests` still belongs to the key and so bounds API traffic
only. On a single-GPU box it is the setting that matters most: budgets bound
a month, concurrency bounds a moment.

## Verified through the real web UI

Not simulated headers -- an account signed in to Open WebUI, chatting.

| | 8B (`deepseek-r1:8b`) | 27B (`qwen3.8:27b`) |
|---|---|---|
| attributed to the signed-in person | yes | yes |
| refused when over the ceiling | yes (429/400 `ExceededBudget`) | -- |

Two things that will waste an afternoon if you do not know them:

**Budget changes are not instant.** LiteLLM caches user records, so lowering
a ceiling and immediately chatting still succeeds -- measured, and it looks
exactly like enforcement being broken. The same test with the ceiling set
*before* the person's first request refuses on the second message. When
changing a live quota, expect a lag rather than concluding it does not work.

**Open WebUI persists its connection in its own database.** The
`OPENAI_API_BASE_URL` environment variable seeds that on first boot and is
ignored afterwards, so an existing install keeps pointing wherever it was
first configured -- here, at `vllm:8000`, which produced an empty model list
and "Model not found" on every chat while the same URL worked perfectly when
curled from inside that container. Fix it in the UI under
**Admin → Settings → Connections**, or via the API:

```bash
curl -X POST http://open-webui:8080/openai/config/update   -H "Authorization: Bearer <an admin session token>"   -H 'Content-Type: application/json'   -d '{"ENABLE_OPENAI_API": true,
       "OPENAI_API_BASE_URLS": ["http://identity-proxy:8080/v1"],
       "OPENAI_API_KEYS": ["<the shared key>"],
       "OPENAI_API_CONFIGS": {}}'
```

## Cached answers are free, and invisible

`litellm_settings.cache` is on with a one-hour TTL, so an identical request is
served from Redis. That reply costs nothing, records **no spend**, and appears
in nobody's usage. Two people asking the same question in the same hour show
one charge between them.

That is usually what you want on a shared box — it is a large win for repeated
evals and for UI clients that resend the same system prompt — but it does mean
usage totals measure *what the GPU did*, not what people asked for.

It also makes tests lie. `scripts/e2e-check.py` sent the same prompt every run
and its attribution checks failed against a stack that was working perfectly:
the first run passed, every rerun failed, which is what a cache hit looks like
from outside. Its prompts are unique per run for that reason. If you are
measuring usage, vary the prompt or the numbers will quietly be about the
cache instead.

## The engine serves one model

vLLM claims `VLLM_GPU_MEMORY_UTILIZATION` of the card at startup, so nothing
else can load beside it — on a 24 GB card an 8B and a 27B cannot coexist, and
Ollama cannot run alongside. `scripts/switch-model` changes which model is
loaded; the gateway's `model:` values must keep matching
`--served-model-name`, or requests 404 at the engine while the gateway looks
healthy.

## Limitations worth knowing

**The dollar figures are notional for local models** unless you set
`input_cost_per_token` / `output_cost_per_token` on the model in
`config/litellm/config.yaml`. Until then the budget really bounds tokens, and
the number in the UI is an accounting unit rather than money.

**The gateway now receives the email address of everyone who chats.** That is
inherent to attributing usage -- it cannot bill someone it cannot name -- but
it is a real change in what the gateway logs, and worth stating out loud
before turning it on.

**Anyone holding the master key bypasses all of this.** It is a superuser
credential with no budget. Keep it to the operator and to
`scripts/llm-users.sh`; give people minted keys.
