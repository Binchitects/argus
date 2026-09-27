# config/engine

Written by the app (Admin → Models); nothing here is edited by hand.

| File | Read by | What |
|---|---|---|
| `models.ini` | the engine (`llamacpp`, read only) | the presets of the models admins added. The engine restarts llama-server when it changes. |
| `keep` | the engine | the models kept loaded, one a line: loaded when it starts, and again by the app whenever one is not. Absent: the .env model; empty: none. |
| `targets.json` | Prometheus (read only) | the loaded model(s), whose metrics it scrapes (`/metrics?model=NAME`). |

`app-init` makes the folder the app's (755). See `deploy/services/llamacpp/router.sh`.
