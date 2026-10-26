# Data flows: where people's data goes

Where personal data (and health data, where people write it) travels in an
Arena installation, what protects it on the way and where it comes to rest.
It is the description a record of processing, a DPIA or an auditor asks for;
the parts are in [architecture.md](../architecture.md). Flows that leave the
company are in **bold**: each is one [Company] turns on and answers for.

## Inside the host

```
 browser ──TLS 1.2+──▶ traefik ──▶ web / app ──▶ postgres (people, chats, files sealed, audit log)
                                    │   │  └───▶ audit volume (the chain's daily anchors)
                                    │   ├──────▶ litellm (the gateway) ──▶ llamacpp / vllm, imagegen,
                                    │   │                                  videogen, audio (the company's models)
                                    │   ├──────▶ embed (passages to vectors)
                                    │   ├──────▶ sandbox (no network: code, ffmpeg)
                                    │   ├──────▶ argus (code questions, the person's GitLab membership)
                                    │   └──────▶ searxng, browser (the web)
                                    └─ logs, masked ──▶ promtail ──▶ loki
```

| Data | From | To | Protected by | At rest |
|---|---|---|---|---|
| Sign-in: name, password, second factor | browser | app | TLS; the password checked against its hash, never kept | hash in Postgres |
| Chats, files, sound, pictures | browser | app | TLS, the person's session | Postgres; files sealed with AES-256-GCM (DAT-05) |
| A question and its context | app | the gateway, a model | inside the stack; the safeguards first mask or stop personal data, secrets and health data as the rules say (DAT-04, DAT-08) | the engine's memory only; the gateway's request log (DAT-07 sets how long) |
| Answers | model | app, browser | the safeguards on answers | Postgres, with the chat |
| Code a model writes, sound and video to convert | app | sandbox | no network; limits on CPU, memory and time (OPS-02) | the sandbox volume while the job runs |
| Company knowledge's passages | the sources | app, embed | each document keeps its readers (IAM-05) | Postgres |
| Who did what | app | the audit log | a hash chain, anchored daily (LOG-03) | Postgres, six years; anchors on the audit volume |
| Logs | every service | Loki | personal data masked in the app's lines (DAT-08) | the loki volume, 90 days |
| Metrics | services | Prometheus | counts and timings by model and service, not what people wrote | the prometheus volume |
| Backups | every volume | `BACKUP_DIR` | encrypted with `BACKUP_KEY_FILE` (OPS-01) | as many as `BACKUP_KEEP` |

## Out of the company, when turned on

| Flow | What leaves | Turned on in | Its safeguards |
|---|---|---|---|
| **A cloud model** | the questions, files and answers of chats that use it | Admin → Models (a provider added to the gateway) | the safeguards keep health and identity data and secrets from it; a DPA, a BAA for health data ([suppliers](policies/suppliers.md)) |
| **Web search and the browser agent** | the search words, the pages' addresses | the chat's tools (Admin → Tools) | the web guard (no addresses inside the company); people's words may be in a query |
| **Company sign-in** | who signs in | Settings → Company sign-in | TLS, signed answers |
| **E-mail** | notifications: names, titles of what happened | Settings → Email | TLS to the relay |
| **The alerts webhook** | alerts' titles | Settings → Notifications | its host must be allowed |
| **The SIEM** | the audit log, each entry | Settings → Audit log | TLS; signed (LOG-04) |
| **GitLab, Gitea, GitHub** | reviews' comments; Argus reads only | Settings → Argus, the integrations | read-only tokens for Argus |
| **Off-site backups** | the encrypted backups | the backup copy you set up | encrypted before they leave |

Nothing goes to Arena's makers: no telemetry, no licence check.

## What this means for a record of processing

Each row above that carries personal data is part of an activity in the
[record of processing](templates/record-of-processing.md); each bold flow
needs a supplier, a contract and, outside the EEA, a transfer safeguard. A
cloud model given health data needs a DPIA first ([template](templates/dpia.md)).
