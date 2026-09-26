# Operations

Everything here runs from `deploy/argus-standalone/`. Each `make` target has a script in
`deploy/argus-standalone/scripts/`, in Bash and in PowerShell.

## Is it working?

```bash
make health   # every container, and every endpoint answering
make smoke    # sign in, send a message, read the model's answer back
```

`make health` fails on the chat model until it has loaded; a large model
paging in from disk takes minutes on a cold start (`make logs S=llamacpp`).

## People

Administrators manage people on the **People** page: add (a password is
generated and shown once), change role, link a GitLab username when the email
does not match, set a budget, reset a password (their sessions end), disable
(sessions and keys stop working at once), delete (their gateway account and
keys go too).

The last active administrator cannot be removed, demoted or disabled, and
nobody can disable or delete themselves. From the shell:

```bash
make user A="list"
make user A="passwd admin"                 # generates and prints a password
make user A="add dana --email dana@example.com --admin"
make user A="role dana user"
```

## Keys

Each person creates their own on **Settings & keys**:

- **code index keys** (`ak_…`) for editors and agents over MCP at `/mcp`;
- **model API keys** (`sk-…`) for the gateway's OpenAI-compatible API.

Both are shown once. Revoking one stops it immediately. `clients/` has the
configuration for each supported editor.

## Budgets

Every person has a budget per period in the gateway (default
`LITELLM_DEFAULT_USER_BUDGET` per `LITELLM_BUDGET_DURATION`). Chat in the app
and calls with their model keys count against the same budget, because the
app sends each person's chats with their own key. When it is spent, the chat
says so; raise it on the People page.

## Backup and restore

```bash
make backup
```

writes `backups/argus-<time>/` (`BACKUP_DIR`), keeping the newest
`BACKUP_KEEP`:

| file | holds |
|---|---|
| `argus/app.db` | people, sessions, code index keys, conversations |
| `argus/index.db`, `argus/index-audit.db` | the code index and the audit log |
| `argus/packs/` | installed knowledge packs |
| `argus/config.yaml` | the index configuration |
| `litellm.dump` | the gateway: people, keys, budgets, spend |
| `env` | `.env` — its `LITELLM_SALT_KEY` is needed to read `litellm.dump` |
| `SHA256SUMS` | checksums of all of the above |

Each database is copied with SQLite's `VACUUM INTO` and checked with
`integrity_check`, so a backup taken while the server runs is consistent.
**The backup contains every secret** — keep it private.

To restore onto a fresh host:

```bash
cp backups/argus-<time>/env deploy/argus-standalone/.env
cd deploy/argus-standalone && docker compose up -d postgres argus && docker compose stop argus
docker exec -i postgres pg_restore -U llmservice -d litellm --clean --if-exists < ../backups/argus-<time>/litellm.dump
docker compose cp ../backups/argus-<time>/argus/. argus:/var/lib/argus/
docker compose run --rm --no-deps --user root --entrypoint chown argus -R 10001:10001 /var/lib/argus
docker compose up -d
```

The `chown` matters: copied files keep the host's ownership, and the app runs
as UID 10001.

## Updating

```bash
git pull
make build && make up     # the app image: backend and frontend
make pull && make up      # newer llama.cpp, LiteLLM and Postgres images
```

Database schemas migrate on start. The symbol-extraction contract is
versioned, so an index built by an older version is re-extracted on the next
pass without any manual step.

## Changing the model

Edit the MODEL block of `.env` (or copy another sample's), then
`make restart`. `model-init` downloads the new weights first.

## HTTPS

Put a certificate and key in `deploy/argus-standalone/config/argus/tls/`, set
`ARGUS_TLS_CERT=/etc/argus/tls/cert.pem` and
`ARGUS_TLS_KEY=/etc/argus/tls/key.pem`, and `docker compose up -d argus`. The
session cookie becomes `Secure`. The gateway port stays HTTP; put it behind the
same certificate with your own proxy if it leaves the machine.

## When something is wrong

| symptom | cause |
|---|---|
| the chat says *No model gateway is configured* | `ARGUS_GATEWAY_URL` unset — only outside the stack |
| *The model gateway is unreachable* | `litellm` is down or still starting: `make logs S=litellm` |
| *Budget has been exceeded* | that person's budget is spent: raise it on People |
| code tools answer *No GitLab account matches …* | the person's email is not a GitLab account's public email: link their GitLab username on People |
| code tools answer *Cannot verify your GitLab access* | GitLab is unreachable and no recent permission is cached |
| an editor gets `421` on `/mcp` | connect by a name listed in `ARGUS_HOSTNAME` |
| an editor gets `401` on `/mcp` | the key is wrong, revoked, or its account disabled |
| *Too many failed sign-ins* | ten failures for that name in ten minutes; wait, or reset the password |
| the Overview says the index is stale | the schedule is off (`ARGUS_INDEX_INTERVAL=0`) or GitLab is failing: see the Indexing page's log |
