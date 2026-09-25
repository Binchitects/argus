# config/engine

Written by the app (Admin → Models); nothing here is edited by hand.

| File | Read by | What |
|---|---|---|
| `models.ini` | the engine (`llamacpp`, read only) | the presets of the models admins added. The engine restarts llama-server when it changes. |
| `active` | the engine | the model to load when it starts: an admin's last choice, or `-` for none. |
| `targets.json` | Prometheus (read only) | the loaded model(s), whose metrics it scrapes (`/metrics?model=NAME`). |

`auth-init` makes the folder the app's (755). See `stack/deploy/llamacpp/router.sh`.
