# Documentation

## Running the platform

| page | what it covers |
|---|---|
| [deployment.md](deployment.md) | deploying: the samples and secrets, addresses, connecting tools, a host with no network (the offline installer: install, upgrade from 5.2.0, repair, remove), switching the model, the traps |
| [configuration.md](configuration.md) | every `.env` variable and every file under `deploy/config/` |
| [architecture.md](architecture.md) | every service, how a request flows through them, where state lives, and what each failure looks like |
| [authentication.md](authentication.md) | who signs in where, and how identity reaches each service |
| [admin.md](admin.md) | the admin area: people, groups, models, tools, dashboards, logs, alerts, usage and cost |
| [settings.md](settings.md) | the Settings page: what applies at once, after a restart, or on the host |
| [chat.md](chat.md) | the chat: models, thinking, files, tools and their limits |
| [plugins.md](plugins.md) | plugins and APIs as tools: installing, each person's own account, the manifest, a catalog |
| [ci.md](ci.md) | the API in CI: a review of each merge request and an explanation of each failed pipeline, from your own GitLab pipeline, with the `arena` CLI |
| [mcp.md](mcp.md) | Arena MCP: each person's chat tools for their own agent at `/mcp`, signed in with their API key |
| [knowledge.md](knowledge.md) | company knowledge: GitLab wikis and issues, Confluence, SharePoint and OneDrive, folders and websites, searched by each person within their rights; long files by their passages |
| [integrations.md](integrations.md) | where people already are: Slack, Mattermost and Teams bots, email in, the installable app with push notifications, the browser extension |
| [code-arena.md](code-arena.md) | Code Arena, our own coding agent and its IDE: getting it offline, signing in, the IDE (code-arena) and its terminals, modes, tools, ARENA.md, sessions, MCP servers, building and packaging it |
| [cpu-temperature.md](cpu-temperature.md) | how CPU temperature reaches the dashboards, on Linux and on Windows |
| [hermes.md](hermes.md) | pointing Hermes at the model and at Argus |

## Argus, the code index

| page | what it covers |
|---|---|
| [argus/README.md](argus/README.md) | Argus in the platform: per-person access, private CAs, the audit stream |
| [argus/overview.md](argus/overview.md) | what it gives an agent, keeping the index current, knowledge packs, measurements |
| [argus/clients.md](argus/clients.md) | connecting an MCP client, and the reference client |
| [argus/knowledge-packs.md](argus/knowledge-packs.md) | building and publishing packs |
| [argus/branches.md](argus/branches.md) | indexing more than one branch |
| [argus/pgvector-backend.md](argus/pgvector-backend.md) | the optional Postgres backend for embeddings |
| [argus/backup-and-restore.md](argus/backup-and-restore.md) | what is worth keeping and how to get it back |
| [argus/roadmap.md](argus/roadmap.md) | what is not yet proven |
| [argus/standalone/](argus/standalone/architecture.md) | Argus alone, without the platform: [architecture](argus/standalone/architecture.md), [configuration](argus/standalone/configuration.md), [operations](argus/standalone/operations.md) |

## Building and testing

| page | what it covers |
|---|---|
| [development.md](development.md) | the repository's layout, building and testing each part, making a release, conventions |
| [testing.md](testing.md) | what each test layer proves, what a green run skips, and the tests still missing |
| [plan.md](plan.md) | the plan: its phases, what each delivered, and what is next |

## Measurements

The numbers behind the claims, kept as evidence:
[model comparison](measurements/model-comparison.md),
[model serving](measurements/model-serving.md),
[index](measurements/index-measurements.md),
[packs](measurements/pack-measurements.md),
[KPIs](measurements/kpis.md),
[verification report](measurements/verification-report.md).
