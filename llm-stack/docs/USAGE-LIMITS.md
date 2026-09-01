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

## What happens at the limit

A person over their ceiling gets HTTP 400 with a budget message, on both the
API and in chat. The budget resets on `budget_duration`; LiteLLM checks for
expiries roughly every ten minutes, so a reset is not instant.

`max_parallel_requests` is the setting that actually protects a single-GPU
box. Budgets bound a month; concurrency bounds a moment, and it is one heavy
user's ten parallel requests that makes the box unusable for everyone else.

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
