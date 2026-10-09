# Architecture: every part of the stack, and how they fit

What runs, why it is there, and how a request travels through it. Setting
values: [configuration.md](configuration.md). Deploying: [deployment.md](deployment.md).

## 1. The shape

One host, one network, one way in. Only Traefik publishes ports:

```
                       :80 / :443  (HTTP_PORT / HTTPS_PORT)
                                │
                        ┌───────▼────────┐
                        │    traefik     │  TLS, routing (config/traefik/routes.yml)
                        └───────┬────────┘
                                │  the stack's network
   ┌────────────┬───────────────┼────────────────┬──────────────┐
┌──▼───┐   ┌────▼────┐    ┌─────▼──────┐   ┌─────▼─────┐  ┌─────▼────┐
│ web  │   │ litellm │    │ llamacpp   │   │ argus     │  │ prometh. │
│ app  │   │ postgres│    │ imagegen   │   │ embed     │  │ alertmgr │
│      │   │         │    │ videogen   │   │ sandbox*  │  │ loki ... │
│      │   │         │    │ audio      │   │ searxng   │  │          │
│      │   │         │    │            │   │ laya**    │  │          │
└──────┘   └─────────┘    └────────────┘   └───────────┘  └──────────┘
                                         * no network at all
                                         ** off by default; a network of its own with the app
```

A broken Traefik is a total outage; a broken Prometheus is not.

## 2. Hostnames

`DOMAIN` in `.env` names three addresses: `https://DOMAIN` (the web, with
`/api`, `/connect`, `/.well-known`, `/scim` and `/mcp` going to the app; `/mcp` is
[Arena MCP](mcp.md)), `gateway.DOMAIN`
(LiteLLM) and `argus.DOMAIN` (Argus's MCP). Nothing else is published:
Prometheus, Alertmanager, Loki, the exporters and the engines are reached only
inside the stack, and the app shows their data to admins.

## 3. Modules

Every service runs. One is left out with `profiles: [off]` in
`docker-compose.override.yml`; the app notices (a module left out has no name
on the network) and stops offering what needs it. The core is `traefik`,
`web`, `app`, `postgres` and `litellm`.

## 4. Network and volumes

**One network**, compose's default: every service reaches every other by its
service name (`http://litellm:4000`). The sandbox has none (`network_mode:
none`): the app hands it jobs through a volume. Laya (when on) is only on a
network of its own with the app, `internal`, so it reaches nothing. `argus`
and `cpu-temp-exporter` also reach the host (`host.docker.internal`): a GitLab on the same machine, and
the host's hardware monitor.

**Named volumes**, prefixed with the project (`arena`): `postgres`, `argus`,
`engine`, `directory`, `audio`, `sandbox`, `prometheus`, `loki`,
`alertmanager`, `acme`, `power-limits` ([configuration.md](configuration.md)
says what each holds). **Models** are in `MODELS_DIR` on the host, read by every
server and written only by the app.

## 5. Authentication

One issuer, the app.

- **People** sign in at the app: accounts, LDAP or Active Directory,
  two-factor. A session cookie for the domain, 1 hour idle, 12 at most.
- **A person's tools** use their API key at the gateway (`sk-...`), which LiteLLM
  checks itself: the spend is theirs, within their credit and the models
  they may use.
- **Coding agents at Argus** bring the person's API key (`sk-...`); Argus asks
  the app whose it is (with `ARGUS_KEY`, inside the network) and answers within
  that person's GitLab membership. Argus takes no GitLab token in the platform.
- **Inside the stack**: the app speaks to the gateway with its master key and
  to Argus with `ARGUS_KEY` (naming the person, whose GitLab membership
  applies); the gateway and Prometheus reach the engine with `ENGINE_KEY`.

## 6. Request flows

**A person chats.** Browser → Traefik → app (session) → the gateway with the
chat's own key, naming the person (their spend, their credit) → llama.cpp. The
app streams the answer back, runs tools (Argus, Python in the sandbox, the web,
pictures, video, speech), and keeps every message. An answer first takes a
place in its model's line (`AnswerGate`: each model has as many places as it
serves at once, so people on one model never wait for another's). Each turn
goes to the engine slot that holds its conversation's start (`SlotTable`, sent
as `id_slot`, which LiteLLM passes on), so the engine reads only the new turn
(a sub-agent keeps a slot the same way); side requests (titles, the
safeguards' check, summaries, Auto's choice) go to the last slot first, and to
another idle one while it is busy. A model that is not loaded while the engine
is full gets room from an idle model that is not kept loaded, not the one new
chats use and no bigger (`EngineRoute`); with none idle, the chat says the
engine is full rather than have it unload the big model.

**Sound and video in.** On upload the app has the sandbox's ffmpeg make an MP3
of a sound, and a video's frames and sound track. A model that hears gets the
sound (an `input_audio` part); one that does not gets a transcript from the
gateway's speech to text, made once. Frames go as pictures to a model that sees.

**A tool calls the API.** Client → Traefik → `gateway.DOMAIN` with the person's
key → LiteLLM → llama.cpp, another GPU server, the picture server or the speech
server. While the answer cache is on (Settings → API keys), chat completions
go by the app first: a repeated request is answered from its database, the
rest go on to LiteLLM with the same key ([admin.md](admin.md#the-answer-cache-for-api-keys)).

**Argus.** A developer's agent → `argus.DOMAIN/mcp` with the person's API key,
which Argus checks with the app (`app:8080/api/authz/key`); the chat →
`argus:7700/mcp` with `ARGUS_KEY` and the person's email. Argus reads the
GitLab it mirrors with a read-only token, and embeddings from `embed`.

**Company knowledge.** The app reads the sources an admin added (GitLab with
its bot's token, folders under `/knowledge`, websites through the web guard),
embeds their passages with `embed`, and keeps them in its own Postgres
database with each document's readers; the chat's `search_knowledge` searches
only what the asker may read ([knowledge.md](knowledge.md)).

**Metrics and logs.** Prometheus scrapes the engine's loaded models (the app
writes their list), the exporters, Traefik and Argus; Promtail ships this
project's container logs to Loki; the app queries both for its dashboards,
logs and alerts.

## 7. Who prepares what

There are no setup containers; each running image prepares itself.

| who | prepares |
|---|---|
| `postgres` | the gateway's database (`POSTGRES_DB`); the app makes its own and migrates it |
| `litellm` | its tables (about 160 migrations on a first start: the first chat waits for them) |
| `app` | the first admin; the engine's model list, the models kept loaded, how many at once; the picture and video servers' on/off; the downloads (`MODEL`, the picture, video and embedding files); asking the speech server for its models; the gateway's model list |
| `llamacpp` | nothing: `services/llamacpp/router.sh` waits for the app's model list, starts llama-server in router mode, and restarts it when the list changes |
| `imagegen`, `videogen`, `embed` | nothing: they wait for their files (`services/sd-serve.sh` also follows the app's on/off) |
| `audio` | fetches its models when the app asks |
| `prometheus` | its scrape credentials, from its environment, at start |
| `searxng` | a fresh secret each start |

## 8. When something is broken

| what you see | what it means |
|---|---|
| every address fails | Traefik is down, or its routes file does not parse (`docker compose logs traefik`) |
| the app answers, the chat says the gateway is unreachable | LiteLLM is starting (migrations) or down |
| "… is not loaded right now" | the chat's model is not loaded; Admin → Models |
| a picture or video model waits for its files | the app is fetching them (Admin → Models → Downloads) |
| a module's tools are missing | it is turned off (Admin → Models) or left out (`docker-compose.override.yml`) |
| Argus pages say "not set up" | `ARGUS_KEY` is empty, or Argus is left out |
| dashboards are empty | Prometheus is down, or the engine has no model loaded to scrape |

## 9. Several app replicas

The app can run as several replicas on one database (`scale.yml` with compose,
or the Helm chart's `app.replicas`). What must happen once runs on one of them:

- **The lead.** Each replica holds a Postgres connection of its own and tries
  for an advisory lock on it every 5 seconds; the one holding it leads. It runs
  the scheduled tasks' clock, the engine's models and presets, the gateway's
  model list, the media servers' on/off, the downloads, the first model's
  provisioning, the directory check, Argus's schedule, the key access sync and
  the alerts for the bell. A replica that stops lets go of the lock; one that
  dies loses it with its connection, and another leads within seconds.
- **The start.** Replicas starting together go one at a time through creating
  the database, migrating it, the first admin and the OIDC keys (a lock in the
  server's `postgres` database).
- **Signals.** The same connection listens (Postgres `LISTEN`/`NOTIFY`): a yes
  or no to a tool call, a stop or "answer now" posted to one replica reaches the
  one writing the answer; a saved setting is read again on all; a model loaded
  or a download started on one wakes the leader.
- **Scheduled tasks.** A run is claimed in the database first (the task row
  names the replica running it, renewed every 20 seconds, let go after two
  minutes without it): a task never runs twice at once. Events that come while
  it runs wait in the `task_events` table, and the replica free next takes them
  in order.
- **The answers' line.** Each replica keeps its own, with its share of the
  places the engine serves at once (rounded up). An answer runs on the replica
  that was asked and streams from there: Traefik's sticky cookie keeps a browser
  on one replica. A page that reaches another replica sees the answer when it
  is saved, not live.
- **Kept in the database already**: sessions and two-factor (the Data
  Protection key ring), OIDC keys, settings, chats. Per replica: the sign-in
  throttle and rate limits, the gateway's model list cache (a minute).

## 10. What is generated, and what you edit

You edit `.env`, `docker-compose.override.yml` and, if you want, the files in
`config/`. The app writes the `engine` and `directory` volumes; nothing writes
into `config/`.
