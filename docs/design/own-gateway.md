# Argus Arena's own model gateway: replacing LiteLLM

The design for item 10 of the requests of 2026-10-29: "develop a better
alternative for litellm". Paths are relative to the repository's root, and
line numbers are as of commit 127b2a5b; each claim was read in the file cited.

**Decision.** The gateway goes inside the existing ASP.NET Core app, not into a new service.

- **No new hop.** The app already sits in the data path for every model call:
  - The chat's own calls go through `GatewayChat` (src/Llm.Api/Chat/GatewayChat.cs:55).
  - Every API key's request already arrives at the app. `gateway.DOMAIN` routes only to the app (deploy/config/traefik/routes.yml:25-29, helm templates/ingress.yaml:41-45). From there the app proxies it to LiteLLM (src/Llm.Api/Gateway/AnswerCache.cs:518-780, KeySpeech.cs:37, ApiPassage.cs:37).
  - Replacing LiteLLM therefore removes a hop: app → engine instead of app → LiteLLM → engine.
- **Coordination already exists.** The app replicas already share Postgres advisory-lock leadership and LISTEN/NOTIFY (src/Llm.Api/Operations/Replicas.cs:31). The gateway needs no Redis or Valkey.
- **Credentials are already held.** The app already has the engine key (`Engine__ApiKey`, deploy/docker-compose.yml:87; `EngineOptions.ApiKey`, src/Llm.Api/Models/Engine.cs:36). It already decrypts the remote servers' keys (ModelCatalog.cs:1065).

```
before:  caller ─▶ Traefik ─▶ app (KeyLine, AnswerGate, cache, prompt log) ─HTTP─▶ LiteLLM ─▶ guardrail callback ─▶ app
                                                                                     └──▶ llamacpp / vllm / sglang / remote / imagegen / audio
after:   caller ─▶ Traefik ─▶ app: GatewayEndpoints ─▶ Admission ─▶ protocol adapter ─▶ ModelGateway ─▶ Balancer ─▶ Upstream ─▶ engines
         app's own callers ─▶ GatewayChat (same public API) ─▶ ModelGateway ...                 └▶ RequestLog (gateway.requests)
```

---

## 1. What the gateway must do

### 1.1 Entry points

| Path(s) | Methods | Callers | Today | New |
|---|---|---|---|---|
| `/v1/chat/completions`, `/chat/completions` | POST, plus OPTIONS (missing today) | API keys, Code Arena, Code Studio workspaces, agents | AnswerCache.cs:430-431 → LiteLLM | `GatewayEndpoints`: Admission, answer cache, prompt log, KeyLine, AnswerGate, slot pin, `ModelGateway` |
| `/v1/messages`, `/messages` | POST, OPTIONS | Claude Code (`ANTHROPIC_BASE_URL`, src/web/src/pages/setup-tools.ts:227-299) | AnswerCache.cs:434-438, LiteLLM translates | Anthropic adapter (§1.5) → the same pipeline as chat completions |
| `/v1/messages/count_tokens`, `/messages/count_tokens` | POST | Claude Code | ApiPassage.cs:23 | Counted by the engine's tokenizer (§1.5) |
| `/v1/responses`, `/responses` | POST, OPTIONS | Codex (`wire_api="responses"`) | AnswerCache.cs:434-438 | Responses adapter (§1.5) |
| `/v1/completions`, `/completions` | POST, OPTIONS | Code Arena's editor completion (src/CodeArena/Gateway.cs:519-532) | AnswerCache.cs:434-438 | Passed through to the engine (llama.cpp and vLLM serve it), metered |
| `/v1/images/generations`, `/images/generations` | POST | API keys; chat tools via `GenerateImageAsync` (ChatTools.cs:438) | KeySpeech.cs:31-79, GatewayChat.cs:134-152 | Picture credit, `GpuShare` hold and `MediaControl.ReadyAsync` (KeySpeech.cs:49-79) kept → imagegen |
| `/v1/audio/transcriptions`, `/audio/transcriptions` | POST | API keys; Talk.cs:73, Media.cs:171, MeetingEngine.cs:118 | KeySpeech.cs:241-263, GatewayChat.cs:158-224 | The language part spliced in (kept); `verbose_json` forced upstream; priced by duration |
| `/v1/audio/speech`, `/audio/speech` | POST | API keys; MediaTools.cs:265, StoryRuns.cs:255, VoiceEndpoints.cs:284 | KeySpeech.cs:205-234, GatewayChat.cs:276-293 | The person's voice, model and speed defaults kept; priced per character; streamed |
| `/v1/audio/translations`, `/v1/images/edits`, `/v1/images/variations` (with and without `/v1`) | POST | API keys | ApiPassage.cs:24-25 | Routed to the audio and image servers when a route of that mode exists, else 404 `model_not_found` |
| `/v1/embeddings`, `/embeddings` | POST | API keys | ApiPassage.cs:20. LiteLLM has no embedding model registered (ModelCatalog registers only chat, image and audio, :1015-1145), so a local model cannot be reached this way today. | New route: the embed server `nomic-embed-text` at `http://embed:8080/v1` (deploy/docker-compose.yml:283-293) |
| `/v1/rerank`, `/rerank`, `/v2/rerank`, `/v1/moderations`, `/moderations` | POST | API keys | ApiPassage.cs:21-22 | 404 `model_not_found` (no such route) until one exists |
| `/v1/models`, `/models`, `/v1/models/{id}`, `/models/{id}` | GET | Code Arena (Gateway.cs:121-131), every OpenAI client | ApiPassage.cs:18 | From the route table, filtered by the key's allowed models, copies `NAME@i` hidden (LiteLlmClient.cs:114-117) |
| `/v1/model/info`, `/model/info` | GET | Code Arena (Gateway.cs:136-181) | ApiPassage.cs:19 | The same JSON shape: `data[].model_name`, `model_info.{mode, max_input_tokens, max_output_tokens, supports_function_calling, supports_reasoning, supports_vision, input_cost_per_token, cache_read_input_token_cost, output_cost_per_token}` |
| CORS preflight on all of the above | OPTIONS | Browsers (VoiceTests.cs:540-546) | Answered by LiteLLM | Answered by the app: `Access-Control-Allow-Origin: *`, the request headers echoed, `Max-Age` |
| The app's own calls | in-process | ChatService.cs:597/1341, Compaction.cs:296, ChatTitles.cs:55, AutoModel.cs:124, Safeguards.cs:578, CustomTools.cs:225, MeetingEngine.cs:239, BusinessMap.cs:169, SecurityReview.cs:801, ReviewEngine.cs:572, FixAgent.cs:74, FixChecks.cs:173, OneQuestion.cs:63 (and its 19 callers) | `GatewayChat` over HTTP with the chat key | `GatewayChat` → `ModelGateway` in-process; the public API is unchanged |

Nothing else is mapped. No management API exists to expose, so NET-01 holds with no extra work. Its evidence test (`ApiPassageTests.The_gateways_own_management_is_not_found_from_outside`, ApiPassageTests.cs:50) keeps its name and paths. Anything unmapped still gets the plain 404 (Program.cs:102).

### 1.2 Routing: model name → deployment

The route table, `GatewayRoutes`, is built from the app's own database. It is the same data `ModelCatalog.SyncGatewayOnceAsync` registers at LiteLLM today (ModelCatalog.cs:1009-1117).

| Kind | Upstream | Auth | Per deployment | Source |
|---|---|---|---|---|
| llama.cpp | `Engine:Url` + `/v1` (`http://llamacpp:8080/v1`), model = `Name`; the llama-server router picks by name | `Bearer Engine:ApiKey` | Info: mode chat, vision and audio from the projector, tools, thinking, `max_input_tokens` = slot window | ModelCatalog.cs:1015-1028, Engine.cs:34-36 |
| vLLM / SGLang | `http://{backend.Host}:{e.Port}/v1` per copy, model = `e.Name` | `Bearer Engine:ApiKey` | `MaxParallel = max(1, Parallel)`; several copies form a pool; each copy is also reachable as `NAME@i` (hidden); fallback | ModelCatalog.cs:1031-1062, EngineRoute.cs:209-212 |
| Remote OpenAI-compatible server (another vLLM, SGLang, TGI, Ollama, a cloud) | `server.BaseUrl`, model = `m.Remote` | Server key, or none | `VerifyTls` on https; `Parallel`; server name | ModelCatalog.cs:1063-1081, RemoteServers.cs:9-42, EngineKinds.cs:32 |
| Pool (one name on several servers) | Every copy above | as above | `MaxParallel` = slots, `weight` = slots or 1 | ModelCatalog.cs:1083-1090, 1174-1183 |
| Picture models | `MediaModels.ImageUrl/v1`, model `sd-cpp-local` (one server, the app loads the right model first) | none | Price per image | ModelCatalog.cs:1120-1137 |
| Speech and transcription | `MediaModels.AudioUrl/v1`, model `m.Id` | none | Price per character or per second | ModelCatalog.cs:1138-1145 |
| Embeddings (new) | `http://embed:8080/v1`, `nomic-embed-text` | none | Price per input token | compose:293 |
| Video | Not routed; made by the app and booked into the ledger | – | – | SpendLog.cs:69-70 |

Rules:

- **Modules.** Media routes exist only while their module runs (`modules.HasAsync("imagegen" | "audio")`, ModelCatalog.cs:1134-1142).
- **Rebuilding.**
  - The table is rebuilt wherever `SyncGatewayAsync` is called today: EngineWatcher.cs:106 and :469, ModelEndpoints.cs:353 and :419, MediaModelEndpoints.cs:293, RemoteServerEndpoints.cs:214, Prices.cs:139.
  - It also rebuilds once on an unknown name, before answering 404. This replaces the "Invalid model name" sync-and-retry at GatewayChat.cs:115-122.
  - A rebuild on one replica is announced on the `Replicas` topic `gateway:routes`.
- **App-side routing stays as it is.** `EngineRoute.TakeAsync` picks the slot, the conversation's copy (`Copy`, EngineRoute.cs:255) and the fallback when a model is not loaded (:199-206). The route table resolves `NAME@i` to the copy's deployment.

### 1.3 Request shaping

**The app's own calls** (GatewayChat.cs:68-94):

- `model` is replaced by the routed copy or fallback.
- `priority=-1` is added for vLLM and SGLang.
- `id_slot` is set from SlotTable.
- `fallbacks` becomes a field of the in-process call, not of the body.

**Every call, by the deployment's dialect:**

| Field | llama.cpp | vLLM / SGLang | remote | Why |
|---|---|---|---|---|
| `model` | upstream name | upstream name | `Remote` | ModelCatalog.cs:1022, 1047, 1068 |
| `id_slot` | kept | dropped | dropped | GatewayChat.cs:87-94; only llama.cpp has slots |
| `priority` | dropped | kept | dropped | GatewayChat.cs:77-81 |
| `chat_template_kwargs` | kept | kept | kept; confirm per remote kind (Risk 6) | ChatOptions.cs:128-137 |
| `metadata`, `fallbacks` | dropped (`metadata.tags` stored in the log's `tag`) | dropped | dropped | Tags at Safeguards.cs:573, CustomTools.cs:220, MeetingEngine.cs:236, SecurityReview.cs:797, ReviewEngine.cs:568, FixAgent.cs:68, BusinessMap.cs:165, OneQuestion.cs:58 |
| `user` | kept (engines ignore it) | kept | kept | Callers set it (ChatService.cs:485); attribution no longer depends on it |
| `stream_options.include_usage` | forced true when streaming; the usage chunk is stripped from the client stream if the client did not ask | same | same | Exact usage on every stream, so cost never goes uncounted |
| Transcription `response_format` | – | – | – | Forced to `verbose_json` upstream; the client's format (json, text, srt, vtt, verbose_json) is rendered from it. LiteLLM priced a non-verbose answer at nothing (GatewayChat.cs:162). |

Attribution comes from the call itself: the person, user id, surface, tag and conversation. It no longer comes from the `X-LLM-User-Email` header (GatewayChat.cs:59, 341; litellm.yaml:36-38). That fixes the lower-casing inconsistency (AutoModel.cs:115 and ChatTitles.cs:48 lower-case the `user` value, other callers do not) and the `"safeguards"` pseudo-user (Safeguards.cs:565).

### 1.4 Streaming and reading responses

- **Pass-through.** Bytes go to the caller as they come, flushed per piece, with buffering disabled. This is the same behaviour as AnswerCache.cs:709-750 and KeySpeech.cs.
- **Metering beside the copy.** An SSE meter reads only `data:` lines, stops at `[DONE]`, and collects:
  - `usage.prompt_tokens`, `prompt_tokens_details.cached_tokens` and `completion_tokens` (GatewayChat.cs:398-404). When `cached_tokens` is missing, llama.cpp's `timings.cache_n` is used.
  - `timings` (:405-408).
  - From the deltas: `reasoning_content`, `content`, `tool_calls` and `finish_reason` (:413-435).
  - The time of the first content byte (TTFT).
- **Errors in the stream.** A chunk with `error` ends the call with no status (GatewayChat.cs:393-397).
- **The answer cache and the prompt log** read the assembled answer from the meter. Today they keep raw buffers of up to 4 MB and 8 MB per request (AnswerCache.cs:397-400); the meter bounds memory per stream.
- **The `model` field in responses** is rewritten to the name the caller used, but only when the upstream name differs (copies `NAME@i`, a remote's `Remote` name). §7 compares this with LiteLLM's behaviour.
- **What GatewayChat's events keep reading.** `ReasoningDelta`, `ContentDelta`, `ToolCallDelta`, `Finished`, `UsageReport` and `EngineTimings` keep their exact meaning. Images still read `data[0].b64_json` (:149). Transcriptions still read `text`, `words`, `segments`, `language` and `duration` (:209-223).

### 1.5 Protocol translation

Every protocol is turned into one chat-completions request. Admission, safeguards, the line, the prompt log and slot pinning then apply the same way to all of them. Today `/v1/messages`, `/v1/responses` and `/v1/completions` get only KeyLine before the pass-through (AnswerCache.cs:532, 603, 634).

**Anthropic Messages (`/v1/messages`).**

- Auth: `x-api-key` or Bearer (AnswerCache.cs:528-531). `anthropic-version` and `anthropic-beta` are accepted and ignored.
- Request mapping:
  - `system` (a string or blocks) → the system message.
  - Content blocks: `text`, `image` (base64 or url) → image parts; `tool_use` → `tool_calls`; `tool_result` → `tool` messages; `cache_control` is ignored.
  - `tools[].input_schema` → function `parameters`.
  - `tool_choice`: auto → auto, any → required, tool → named, none → none.
  - `max_tokens`, `temperature`, `top_p` and `top_k` keep their meaning; `stop_sequences` → `stop`.
  - `thinking` → `chat_template_kwargs` (enabled or `enable_thinking:false`).
  - `metadata.user_id` is dropped.
- Response: `message` with `thinking`, `text` and `tool_use` blocks.
  - `stop_reason`: stop → `end_turn`, length → `max_tokens`, tool_calls → `tool_use`, a matched stop sequence → `stop_sequence`.
  - `usage`: `input_tokens`, `output_tokens`, `cache_read_input_tokens`. Code Arena reads the last one (Gateway.cs:511).
- SSE: `message_start`, then `content_block_start`, `content_block_delta` (`text_delta`, `thinking_delta`, `input_json_delta`) and `content_block_stop`, then `message_delta` (stop_reason, usage), `message_stop`, and `ping` while waiting.
- Errors use Anthropic's shape: `{"type":"error","error":{"type","message"}}`.
- Server tools (for example web search) get a clear 400. They are not dropped silently.
- `count_tokens` uses the engine's tokenizer: llama.cpp `/apply-template` then `/tokenize`, vLLM `/tokenize` with messages. A remote server gets an estimate marked as such.

**OpenAI Responses (`/v1/responses`).**

- Request mapping:
  - `instructions` → system.
  - `input` (a string or items: `message` with `input_text`, `input_image` and `output_text`; `function_call`; `function_call_output`; reasoning items, which are dropped) → messages.
  - `tools` (type function) → function tools; built-in tools get a 400.
  - `tool_choice` and `max_output_tokens` keep their meaning; `reasoning.effort` → `chat_template_kwargs.reasoning_effort` (as ChatOptions.cs:136).
  - `store` and `previous_response_id`: the gateway is stateless; `previous_response_id` gets a 400.
- Output items: `reasoning`, `message`/`output_text`, `function_call`.
- `usage`: `input_tokens`, `input_tokens_details.cached_tokens`, `output_tokens`, `output_tokens_details.reasoning_tokens`.
- SSE: `response.created`, `response.in_progress`, `response.output_item.added`, `response.content_part.added`, `response.output_text.delta` and `.done`, `response.function_call_arguments.delta` and `.done`, `response.output_item.done`, then `response.completed` or `response.failed`.

**Legacy completions.** Passed through to engines that serve them (`choices[0].text`, Code Arena Gateway.cs:519-532).

**On native support.** Translation in the app is the design because it works for every upstream, remote servers included, and it gives one pipeline. The design does not rely on llama.cpp or vLLM serving `/v1/messages` or `/v1/responses` natively. A per-deployment native pass-through can be added later, once it is measured to match.

### 1.6 Admission: keys, access, budgets and checks

API keys and Code Studio workspace keys are admitted in this order. Each refusal writes a ledger row with a `refusal` reason and sends nothing upstream.

| # | Check | Answer | Source today |
|---|---|---|---|
| 1 | The key from `Authorization: Bearer` or `x-api-key`; must exist in `gateway.api_keys` by SHA-256 hex | 401 `invalid_api_key` | AnswerCache.cs:70 and :528-531; LiteLLM `/key/info` (LiteLlmClient.cs:218-231) |
| 2 | Key expired | 401 | `GatewayKeyInfo.Expires` (ILiteLlm.cs:31); KeyCheckTests.cs:89 (IAM-06 evidence) |
| 3 | Person disabled | 403 "This account is disabled." | GuardrailEndpoints.cs:65-68; KeyAccess.cs:54-70 |
| 4 | API access off | 403 "Your API access is off…" | GuardrailEndpoints.cs:69-72; PeopleService.cs:269-276 |
| 5 | Model allowed, decided live by `KeyAccess.ListForAsync` (policy plus feature permissions for media modes) | 403 `model_not_allowed`; an unknown model gets 404 `model_not_found` | KeyAccess.cs:33-51; LiteLLM key `models` |
| 6 | Requests and tokens a minute: the person's resolved limits, tightened by the own key's | 429 with `Retry-After` = seconds to the window's end, `x-ratelimit-limit-*`, `-remaining-*`, `-reset-*`, and the sentence "… Limit type: requests. Current limit: N" | RateLimits.cs:100-130, OwnKeys.cs:45-50; the Code Arena parser (Gateway.cs:599) |
| 7 | The own key's 30-day budget (`MonthlyBudget`) over a rolling 30 days | 400 `budget_exceeded` "Budget has been exceeded! …" (GatewayChat.Explain matches "budget" plus "exceed", :462) | OwnKeys.cs:118 and :169-171; LiteLlmClient.cs:152-159 |
| 8 | Credit of the request's kind (the person's and their groups') | 400 `credit` with the refusal text | GuardrailEndpoints.cs:74-79; CreditKinds.cs:10 |
| 9 | `Safeguards.CheckApiAsync`: BLOCKED gives 400; INTERVENED masks the texts in the messages | 400 `safeguards` or masked text | GuardrailEndpoints.cs:80-82; Safeguards.cs:221-244 |
| 10 | Room for the model: `EngineRoute.RoomForKeyAsync` (llama.cpp room and `LineFull`; vLLM `StartForKeyAsync` and `PressureAsync`) | 503 `model_not_ready` with Retry-After 30 | EngineRoute.cs:282-397 |
| 11 | Answer cache, prompt log, KeyLine (per-key places, `ApiQueueTimeout`, 429 Retry-After 30), AnswerGate (503 `service_busy`, 429 `TooLong`), slot pinning | as today | AnswerCache.cs:539-669; KeyLine.cs:32-58; KeyPeople.cs:107-134 |

Calls the app makes itself skip checks 1-10, as the guardrail let the chat key's requests pass (GuardrailEndpoints.cs:59-62). The chat checks credit and safeguards itself, for example ModelCatalog.cs:1375 and ChatTools.cs:419.

**Key cache.** `KeyBook` caches one resolved key for 10 seconds: the person's state, allowed models, rates, priority, at-once count and cache opt-in. The 10 seconds match LiteLLM's `user_api_key_cache_ttl` (litellm.yaml:32). A revocation, rotation, disable, API-off, access or group change clears it at once on every replica through the topic `keys:changed`. It replaces five caches with different lifetimes:

| Cache today | Lifetime |
|---|---|
| `KeyPeople` | 1 min |
| `OwnKeyPlaces` | 1 min |
| `AnswerCacheState.Keys` | 30 s |
| `KeyCheck` | 60 s |
| `McpPeople` | 30 s |

### 1.7 Rate limits

- **Counting.** One row per key and minute in the UNLOGGED table `gateway.rate_windows`, written with `INSERT … ON CONFLICT DO UPDATE … RETURNING`. All app replicas share it, so counts stay global without Valkey.
  - LiteLLM needed Redis for that (litellm.yaml:17-20, ha.yml:19-20). Helm says each replica counts alone without it (values.yaml:130-132).
  - Keys with no limit, the default, never touch the table ("Nothing is limited until an admin sets a limit", RateLimits.cs:47-52).
- **What counts.** Requests are counted at admission. Tokens (prompt − cached + completion, as `MinuteAsync` counts them, RateLimits.cs:297-302) are added when the request ends.
- **"Used this minute"** on Account → API keys and the admin person page is read from this table, so it is exact. Today it lags LiteLLM's batched writes (RateLimits.cs:260-262).
- **Arena MCP model calls** (`StartModelCallAsync`, RateLimits.cs:326-347) add the person's key minutes from this table.
- **At once.** KeyLine already queues and has no per-key refusal. LiteLLM's `max_parallel_requests` = ApiRequestsPerKey × 4 safety net (KeyAccess.cs:25-27) is dropped: every path now passes through KeyLine, Code Studio included.

### 1.8 Balancing, retries, timeouts and fallbacks

| Behaviour | Today (LiteLLM) | New |
|---|---|---|
| Choosing a copy in a pool | `least-busy`; per replica unless Redis (litellm.yaml:9, 17-20) | Least busy by the engine's own counts (`EngineClient.CapacityAsync`, cached 1 s), then this replica's in-flight count, then weight. Never more than a copy's `MaxParallel`. When every copy is full it waits up to `Gateway:PoolWait` (30 s) before answering 429. |
| Retries | `num_retries: 2` (litellm.yaml:10) | Up to `Gateway:Retries` (2), and only before the first byte reaches the caller: connection refused, 502, 503 or 504 → the next copy, then the same one after backoff. A context-overflow or other 4xx is never retried. |
| Engine still loading | GatewayChat waits 5 s, up to 24 times, for the chat only (:109-114) | In the gateway, for every caller: llama.cpp's `503 "Loading model"` is waited out up to `Gateway:LoadWait` (2 min) |
| Fallbacks | Per request (`fallbacks`) and `router_settings.fallbacks` (LiteLlmClient.cs:182-190; ModelCatalog.cs:1106-1114) | The route's `Fallback` (LocalModels.Fallback) and the call's own; used after retries fail, before the first byte; logged as `fallback_from` |
| Timeouts | `timeout` and `stream_timeout` of 960 s (litellm.yaml:15-16) | `Gateway:FirstByteTimeout` 960 s (long prefill) and `Gateway:IdleTimeout` 960 s between chunks; the app's own calls are also capped by `Chat:RequestTimeout` (ChatOptions.cs:41) |
| Caller goes away | – | The upstream request is cancelled, so the engine frees its slot (measured in §7) |
| Connection waits | `Gateway:ConnectWaitSeconds` (30) and `KeyWaitSeconds` (90) for LiteLLM's start-up (LiteLlmClient.cs:19, 26; GatewayChat.cs:313-357; ChatKey.cs:43-59) | Removed: nothing to wait for |

### 1.9 Metering: cost and the request log

One row in `gateway.requests` per request, whatever happened to it: done, failed, stopped, refused or cached. The app's own media are booked in the same table (SpendLog.cs:69-111 callers: MediaTools.cs:205, VectorJobs.cs:256, StoryRuns.cs:181/212/240/292, MediaJobs.cs:310/314/457/460/463).

- **Pricing.** Cost is priced at write time from `PriceBook` (Prices.cs:74-131) and the media model's own `UnitPrice` (ModelCatalog.cs:1131):
  - Chat and API: prompt, cached and completion tokens with their prices (Prices.cs:42).
  - Images: count × per-image price.
  - Transcription: seconds × per-minute price / 60.
  - Speech: characters × per-thousand price / 1000.
  - Embeddings: prompt tokens × input price.
- **One price source.** LiteLLM priced from a copy of the same prices, registered at sync time (ModelCatalog.cs:1149-1167, 1204-1209), so the two could drift until the next sync.
- **Writing.** Rows go through a bounded channel, flushed every `Gateway:LogFlush` (1 s) or every 500 rows with Npgsql binary COPY, and drained on shutdown. The key's lifetime `spend` and `last_used_at` are updated in the same flush.
- **Prompt log link.** A logged API request uses the same id in `prompt_log` and `gateway.requests`, so the prompt log can show cost and cached tokens.
- **Credit kind and surface** are decided at write time:

| Kind | Rule | Rule today (UsageEndpoints.cs:36-42) |
|---|---|---|
| `pictures` | image calls | `call_type` image* |
| `video` | video calls | `call_type` video* |
| `speech` | speech and transcription calls | `call_type` speech* / transcription* |
| `chat` | `surface` chat or app | key alias `chat` |
| `api` | everything else | everything else |

### 1.10 Errors

- **Bodies.** Every error body takes the OpenAI shape `{"error":{"message","type","code"}}`, or Anthropic's on `/v1/messages`. `GatewayChat.Explain` already reads `error.message` (GatewayChat.cs:455), and PromptLogWriter's `ErrorOf` reads the same shape (PromptLogWriter.cs:112-113).
- **Sentences kept**, because deployed clients parse them:
  - "Limit type: requests|tokens. Current limit: N" (CodeArena/Gateway.cs:582-600).
  - "Budget has been exceeded" (GatewayChat.cs:462; GatewayRateLimitTests.cs:95-96).
  - llama.cpp's own "Loading model", `exceed_context_size_error` with `n_prompt_tokens` and `n_ctx`, and "failed to load" (ContextOverflow.cs:39-51; GatewayChat.cs:489).
- **Removed:** LiteLLM's class-name prefixes. The `litellm\.` part of the regex goes at GatewayChat.cs:492 and NewsText.cs:26, and "Invalid model name" handling goes at GatewayChat.cs:115-122.
- **Unreachable upstream** gives 502 `upstream_unreachable` naming the deployment. Today it is `gateway_unreachable`, AnswerCache.cs:702-708.

### 1.11 Health and metrics

**Health.** Admin → Overview's "Model gateway" probe (OperationsEndpoints.cs:367, `Stack:LiteLlmProbeUrl`) becomes the in-process status of each deployment:

- llama.cpp `/health`
- vLLM and SGLang `/health`
- remote servers, through `RemoteHealth` (IdentityWiring.cs:513)
- the media servers

`functional-test.py:239,246` keeps finding "Model gateway" among the core services.

**Metrics.** These are added to the app's `/metrics`, rendered by hand like LineMetrics.cs:76-101:

- `arena_gateway_requests_total{model,deployment,surface,status}`
- `arena_gateway_request_seconds` and `arena_gateway_ttft_seconds` (histograms by model)
- `arena_gateway_tokens_total{model,type=prompt|cached|completion}`
- `arena_gateway_inflight{deployment}`
- `arena_gateway_upstream_errors_total{deployment,reason}`
- `arena_gateway_refused_total{reason}`
- `arena_gateway_fallbacks_total{model,fallback}`
- `arena_gateway_log_pending`

They come from the existing `app` scrape job (prometheus.yml:70-73). LiteLLM's metrics were never scraped: the job was enterprise-only (spend-data map §5).

### 1.12 Improvements over LiteLLM

1. One hop and one container less: no ~160 first-start migrations (docs/architecture.md:168, 181), no start-up waits, no 1 GiB Python pod (values.yaml:134).
2. Every protocol gets admission, safeguards, the line, the prompt log and slot pinning. Today messages, responses and completions are passed straight through.
3. Access is decided live from the app's tables. The 10-minute push to LiteLLM goes: `KeyAccessWatcher` (KeyAccess.cs:104-245), `KeyAccess.SyncAsync` and `BlocksAsync`, `RateLimits.SyncAsync`, `KeyWriters` (whose only reason is racing those pushes, KeyWriters.cs:3-8), `GroupTeams`, `CreditMove`. Revocation is instant on every replica.
4. Usage is exact on every stream (include_usage forced), with cached tokens from `timings.cache_n` when missing.
5. The ledger stores the person at write time, plus surface, tag, refusal reason and cache hits. Panels need no `coalesce` chains, side calls show spend per feature (tags were sent but never stored), and GDPR erasure becomes correct (§2.8).
6. Rate counts are global across replicas without Valkey, and "used this minute" is exact.
7. Pools are balanced by the engines' own counts, not per-replica guesses, and wait briefly for a free copy instead of failing.
8. Transcriptions are priced by duration whatever format the client asks for.
9. Embeddings work for API keys.
10. Standard `x-ratelimit-*` headers.
11. Health per deployment, and metrics.
12. Three defects fixed (§6, commit 1).
13. An external Postgres can use `verify-ca` and `verify-full` (values.yaml:122-124 says only disable, prefer and require work for the gateway today).
14. Less data kept: no client IP, messages or response stored in the ledger (`requester_ip_address`, `messages` and `response` were nulled only by erasure, SpendLog.cs:38-41).
15. One secret fewer (`GATEWAY_KEY`).

### 1.13 Deliberately not carried over

- **LiteLLM users, end users, budgets and teams.** Every caller passes `null` budgets (LiteLlmClient.cs:46-69); `SetTeamAsync` has no caller.
- **The chat's service key** (ChatKey.cs) and the **master key**, with the dashboards' "(master key)" category.
- **The `X-LLM-User-Email` header.**
- **The runtime model registry:** `/model/new`, `/model/delete`, `/config/update`, `store_model_in_db`, the `openai/` prefix.
- **`LITELLM_LOCAL_MODEL_COST_MAP`.**
- **The guardrail callback** (`/internal/guardrail/...`, GuardrailEndpoints.cs:27).
- **`/internal/models/defaults`**, keyed by the master key (DefaultModelEndpoints.cs:19, 63). No installer script calls it; the installer uses `DEFAULT_MODELS` (installer.sh:1402) instead.

---

## 2. Data

### 2.1 New tables

All three tables live in the app database `llmapp`, in a schema `gateway`. Columns are snake_case through `HasColumnName` so panel SQL stays readable; app tables use quoted PascalCase, for example AppDbContext.cs:781.

**`gateway.requests`** (entity `GatewayRequest`, src/Llm.Core/Gateway/GatewayEntities.cs):

| Column | Type | Notes |
|---|---|---|
| `id` | uuid PK | `Guid.CreateVersion7()`; the same id as `prompt_log.Id` when logged |
| `started_at` | timestamptz not null | Replaces `"startTime"` (timestamp without zone, SpendLog.cs:97) |
| `first_token_at` | timestamptz null | TTFT; replaces `"completionStartTime"` |
| `ended_at` | timestamptz not null | |
| `duration_ms` | int not null | Replaces `request_duration_ms` |
| `surface` | varchar(12) not null | `chat` (a conversation's turn), `app` (side calls), `api` (people's keys), `studio` (workspace keys), `media` (booked by the app) |
| `call` | varchar(24) not null | `chat`, `completion`, `embedding`, `image`, `image_edit`, `transcription`, `translation`, `speech`, `video`, `music`, `rerank`; replaces `call_type` |
| `protocol` | varchar(16) not null | `openai`, `anthropic`, `responses`, `completions`, `internal` |
| `credit_kind` | varchar(12) not null | `chat`, `api`, `pictures`, `video`, `speech` |
| `user_id` | uuid null | FK `AspNetUsers`, ON DELETE SET NULL |
| `person` | varchar(256) null | Lower-case email at write time; null means unattributed; `'erased'` after Article 17 |
| `key_hash` | char(64) null | SHA-256 hex; null for the app's own calls |
| `key_alias` | varchar(120) null | |
| `tag` | varchar(64) null | `title`, `safeguards`, `deck`, `meetings`, … |
| `conversation_id` | uuid null | |
| `model` | varchar(200) not null | The name callers use; replaces `coalesce(model_group, model)` |
| `deployment` | varchar(200) null | `llamacpp`, `vllm:flash@1`, `remote:<server>`, `imagegen`, … |
| `fallback_from` | varchar(200) null | |
| `attempts` | smallint not null default 1 | |
| `prompt_tokens`, `cached_tokens`, `completion_tokens` | int not null default 0 | `cached_tokens` replaces `metadata.usage_object…cached_tokens` |
| `reasoning_tokens` | int null | |
| `total_tokens` | int generated always as (`prompt_tokens + completion_tokens`) stored | |
| `usage_estimated` | bool not null default false | An aborted stream with no usage |
| `cost` | numeric(18,9) not null default 0 | Replaces `spend` (float8) |
| `seconds` | double null | Audio, video or music length; replaces `metadata.video_seconds` and `music_seconds` |
| `pictures`, `characters` | int null | |
| `status` | varchar(10) not null | `done`, `failed`, `stopped`, `refused`, `cached` |
| `http_status` | smallint not null | |
| `refusal` | varchar(16) null | `rpm`, `tpm`, `parallel`, `budget`, `credit`, `blocked`, `expired`, `disabled`, `api_off`, `model`, `safeguards`, `busy`, `too_long`, `room`; replaces `error_class='ProxyRateLimitError'` and "Limit type" |
| `error` | varchar(500) null | |
| `source_id` | varchar(100) null | LiteLLM's `request_id` for imported or mirrored rows; unique where not null |

Indexes on `gateway.requests`:

- `(started_at)` INCLUDE `(person, credit_kind, cost)`, which covers the month scan of credit and the ledger
- `(person, started_at)`
- `(user_id, started_at)`
- `(key_hash, started_at)` where `key_hash` is not null
- unique `(source_id)` where `source_id` is not null

**`gateway.api_keys`** (entity `ApiKey`). It replaces `LiteLLM_VerificationToken` and the app's `own_keys` (OwnKey.cs:8-23, AppDbContext.cs:826-834), which is merged in and dropped.

| Column | Type | Notes |
|---|---|---|
| `hash` | char(64) PK | SHA-256 hex, as LiteLLM keeps it (AnswerCache.cs:70), so imported keys keep working |
| `user_id` | uuid not null | FK, cascade |
| `kind` | varchar(8) not null | `main`, `own`, `studio`; replaces alias parsing (OwnKeys.cs:68, PeopleService.cs:229) |
| `alias` | varchar(120) unique not null | `app-{username}`, `app-{username}-own-xxxxxxxx`, `studio:{id:N}` |
| `name` | varchar(80) null | An own key's name |
| `preview` | varchar(20) not null | `sk-…abcd` |
| `workspace_id` | uuid null | FK `StudioWorkspace`, cascade |
| `requests_per_minute`, `tokens_per_minute`, `parallel` | int null | An own key's limits |
| `monthly_budget` | numeric(12,4) null | Its 30-day budget |
| `expires_at` | timestamptz null | |
| `created_at` | timestamptz not null | |
| `last_used_at` | timestamptz null | Updated at most once a minute |
| `spend` | numeric(18,9) not null default 0 | Lifetime; shown as key spend (OwnKeys.cs:71, AdminEndpoints.cs:229, AccountEndpoints.cs:232) |

**`gateway.rate_windows`** (UNLOGGED, created in the migration's SQL):

- Columns: `key_hash` char(64), `minute` timestamptz, `requests` int, `tokens` bigint.
- Primary key `(key_hash, minute)`.
- The writer deletes rows older than 2 minutes.

**Views for dashboard SQL.** These are the only things in the schema that reach app tables, and they expose only the columns listed:

- `gateway.key_owners(hash, alias, kind, email, spend, created_at)`
- `gateway.month_spend(person, credit_kind, spend)`

**Settings rows deleted by the migration:** `chat.gateway_key` (ChatKey.cs:18), `credits.moved` (CreditMove.cs:16), `gateway.rate_limits_keep` (RateLimits.cs:75).

**Migration test.** Following the memory note on migrations with data: an upgrade test with existing `own_keys` rows checks they land in `gateway.api_keys` with their limits.

### 2.2 Writers

| Writer | Writes |
|---|---|
| `RequestLog` (new, a background service with a channel) | Every call through the gateway |
| `SpendLog.BookVideoAsync`, `BookPicturesAsync`, `BookSoundAsync` (SpendLog.cs:69-79) | Rows through `RequestLog` with surface `media` (today a raw Npgsql INSERT into `LiteLLM_SpendLogs`, :88-91) |
| `ApiKeys` (new; replaces `LiteLlmClient` key methods) | `gateway.api_keys` |
| `RateWindows` | `gateway.rate_windows` |
| `LedgerImport` (one-shot; §2.9) | Imported rows and keys |

### 2.3 Readers rewritten

| Reader | Today | New |
|---|---|---|
| `/api/usage/me` (UsageEndpoints.cs:47-101; usage.tsx:80) | `lower(Person)=@me`, NotRefused, 3 queries over the SpendLogs/VerificationToken join | EF: `person == me && status != "refused"`. Totals: count, `prompt_tokens − cached_tokens`, `cached_tokens`, `completion_tokens`, `cost`. Series bucketed by `floor(epoch/secs)`; by model: `model`, count, `total_tokens`, `cost`. |
| `/api/admin/usage/groups` (GroupUsage.cs:37-45) | per lower(Person) | EF group by `person` on `[from,to)` with `status != "refused"` |
| Prompt list, API part (PromptUsage.cs:158-197) | KeyAlias <> 'chat', `call_type <> ''`, `status <> 'failure'` | `surface in ("api","studio") && status in ("done","cached")`; Kind from `call` (replaces `Kind(callType)`, :200-207); the key name from `key_alias`; people from `user_id`. Optional: `surface == "app"` rows listed by `tag`. |
| `CreditBook.SpendAsync` and `MonthlyAsync` (Credit.cs:77-106); then `RefusalAsync`, `StandingAsync`, `Ledger`, NewsWatch.cs:89, AccountEndpoints.cs:226, AdminEndpoints.cs:147/209/226, OperationsEndpoints.cs:297/416, GroupEndpoints.cs:108 and chargeback :309-336 | SQL through the read-only `SqlDatasource` on the `litellm` database | EF: group by `person, credit_kind` with sum `cost`; the month as `date_trunc('month', started_at)`; through the app's `AppDbContext`, which drops the dashboard role from the credit path |
| `RateLimits.UseAsync` and `MinuteAsync` (RateLimits.cs:265-303) | SpendLogs, `api_key = any(@keys)`, `error_code <> 429`, refusals by `error_class` and regex | Minute: `gateway.rate_windows` (exact). Refusals in a day: `gateway.requests where status='refused' and refusal in ('rpm','tpm','parallel') and (key_hash = any(@keys) or person=@me)`, group by refusal. |
| `StartModelCallAsync` (RateLimits.cs:326-347) | `MinuteAsync` plus `gateway.KeysAsync` | `rate_windows` for the person's keys from `api_keys` |
| `SpendLog.RepriceAsync` (SpendLog.cs:120-187; CostRecalculation.cs:27-28) | CTE over SpendLogs, `metadata.video_seconds` | The same CTE on `gateway.requests` with `cached_tokens`, `seconds` and `pictures` columns. Transcriptions and speech become repriceable from `seconds` and `characters`; today they are "Unpriced", SpendLog.cs:10. |
| `SpendLog.DeleteBeforeAsync` (:48-55; RecordRetention.cs:32-36) | `"startTime" < @before` | EF `ExecuteDeleteAsync` on `started_at < before`; `Retention:GatewayLogDays` unchanged (Retention.cs:24) |
| `SpendLog.ForgetAsync` (:34-45; DataRequests.cs:236-244) | `WHERE "user" = @email`; misses chat and app-booked rows, which keep the email in `end_user` (:91) | `user_id == id \|\| person == email` → `person = 'erased'`, `user_id = null`, `key_alias = null`, `conversation_id = null` |
| Key lists (AccountEndpoints.cs:225, AdminEndpoints.cs:213, OwnKeys.cs:52-60) | `/key/list` | `api_keys where user_id = …` |
| Storage page (StorageReport.cs:63-68, 169, 240) | `LiteLLM_*` labels in the `litellm` database | Labels for `gateway.requests`, `gateway.api_keys`, `gateway.rate_windows`, all in the app database |
| `functional-test.py:386-431` | `/spend/logs` inside the litellm container | An admin API (`/api/admin/usage/prompts`) or SQL on `gateway.requests` |
| `audit-dashboards.py:29,113` | `SQL_UID="litellm-db"` | `gateway-db` |

**Shared SQL fragments that collapse to columns:**

| Fragment | Today | New |
|---|---|---|
| `UsageEndpoints.Person` | `coalesce(nullif(s."user",''), metadata->>'user_api_key_user_id', v.user_id, nullif(s.end_user,''), '(unattributed)')` | `coalesce(r.person,'(unattributed)')` |
| `UsageEndpoints.Cached` | `metadata->'usage_object'->'prompt_tokens_details'->>'cached_tokens'` | `r.cached_tokens` |
| `UsageEndpoints.Model` | `coalesce(nullif(model_group,''), model)` | `r.model` |
| `UsageEndpoints.From` | SpendLogs left-joined to VerificationToken | `from gateway.requests r` |
| `UsageEndpoints.KeyAlias` | `coalesce(metadata->>'user_api_key_alias', v.key_alias, '(master key)')` | `coalesce(r.key_alias, r.surface)` |
| `UsageEndpoints.CreditKind` | a CASE on `call_type` and alias | `r.credit_kind` |
| `RateLimits.Refused` / `NotRefused` | `error_class='ProxyRateLimitError'` | `r.status='refused' and r.refusal in ('rpm','tpm','parallel')` / `r.status <> 'refused'` |
| `RateLimits.Kind` | regex on `error_message` | `case r.refusal when 'rpm' then 'requests' when 'tpm' then 'tokens' when 'parallel' then 'at once' else 'other' end` |

### 2.4 Dashboards: all 54 SQL panels

There are 12 panels in llm-overview.json, 19 in llm-users.json and 23 in usage-by-user.json. Each is rewritten mechanically with the rules above.

- `$__timeFilter("startTime")` → `$__timeFilter(started_at)`. The macro emits `'…Z'` literals (SqlMacros.cs:34, 61); compared with timestamptz they are now read in UTC.
- `request_duration_ms` → `duration_ms`.
- TTFT: `extract(epoch from first_token_at - started_at)*1000 where first_token_at is not null`.
- `spend` → `cost`.
- Failures: `status in ('failed','stopped')`.
- `cache_hit` → `status = 'cached'`.

Panels whose meaning changes:

- **llm-overview :785 "Recent requests"** shows `person` instead of the raw `"user"`.
- **usage-by-user :360 "Master-key requests"** is replaced by "Requests by surface" (chat, app, api, studio, media). The master key no longer exists.
- **usage-by-user :294 "Attributed %"** becomes `person is not null`.
- **usage-by-user :459 "Which key belongs to whom"** reads `gateway.key_owners`.
- **usage-by-user :605 "Budget headroom"** reads `LiteLLM_UserTable`, whose budgets `CreditMove` already clears (CreditMove.cs:41-48), so it is stale. It becomes "Spend this month by person and kind" from `gateway.month_spend`.
- **usage-by-user :643 "Unattributed usage"** lists `surface`, `tag` and `key_alias` where `person is null`.
- **llm-users :1437 and usage-by-user :681 "Refused by rate limits"** use `refusal`.
- **Requests panels** exclude every refusal (`status <> 'refused'`). Today they exclude only rate-limit refusals; this goes in the CHANGELOG.

**Datasource** (SqlDatasource.cs:13-93, DashboardStore.cs:12, DashboardEndpoints.cs:20):

- The uid becomes `gateway-db` and the database is the app's. `Dashboards:SqlDatabase` is removed.
- The role `llm_dashboards` gets `USAGE` and `SELECT` on schema `gateway` only, with default privileges on that schema. The grant on `public` is revoked: granting it on the app database would expose every app table to panel SQL.
- Each query runs with `SET LOCAL search_path = gateway`, keeping today's read-only transaction and statement timeout (SqlDatasource.cs:38-39).
- The help topics and the dashboards list keep their ids: dashboards.tsx:30/33/49, topics.ts:1162/1171-1172.

### 2.5 Existing data in the `litellm` database (needs your decision)

Your preference is no backward compatibility and no footprint of the old stack. What stays behind after the upgrade is people's `sk-` keys, held as SHA-256 hashes in `LiteLLM_VerificationToken`, and up to 400 days of `LiteLLM_SpendLogs`.

- **Recommended:** a one-shot `LedgerImport` at the first start of the release.
  - Keys: `token` → `hash`; `key_alias`, `key_name` → `preview`, `user_id` → `user_id` by email, `created_at`, `spend`, `expires`. The `chat` service key is skipped. `studio:{id}` becomes kind studio with its workspace id; `-own-` aliases join their `own_keys` row.
  - Spend: every row, with the person resolved by today's `Person` rule and the refusal decoded. Idempotent through `source_id`.
  - It marks `gateway.imported`. The installer then drops the `litellm` database after its pre-upgrade backup.
  - The importer is deleted in the release after.
  - Without it, every person's API key, Code Arena machine and Code Studio workspace stops working at the upgrade, and credit resets mid-month.
- **Alternative:** abandon both, and tell everyone to make new keys.

The continuous mirror of commit 6 is the same code run every 5 seconds, so the import is exercised for weeks before release.

---

## 3. The seam

**What moves.** Today the app ends at an HTTP boundary to LiteLLM: `ILiteLlm` for management (ILiteLlm.cs:37-98, 22 methods, 21 consuming files) and `GatewayChat`'s typed HttpClient for calls (IdentityWiring.cs:288-294). The new boundary is the app's HTTP client to the engines. Tests substitute it by HttpMessageHandler, as they already do for FakeModel (AppFixture.cs:202-203).

**Interfaces.**

- **`ILiteLlm` is not renamed; it is split and deleted.**
  - Its key half becomes `ApiKeys`, a concrete EF store: make, list, find by secret, revoke, set own limits.
  - Its model half becomes `GatewayRoutes`, concrete and built from the database.
  - Users, budgets, teams, the service key, fallbacks and model registration have no successor.
- **`GatewayChat` stays as the facade for the app's own callers**, with the same public methods: `StreamAsync` ×2, `GenerateImageAsync`, `TranscribeAsync`, `TranscribePartsAsync`, `SpeakAsync`, `OpenSpeechAsync`. None of its 30+ call sites change.
- **New: `ModelGateway`**, the call API used by `GatewayChat` and `GatewayEndpoints`. It takes a `GatewayCall { Call, Protocol, Body, Caller(Person, UserId, Surface, Tag, Conversation, KeyHash), Fallbacks, Accept }` and returns a response wrapper that meters when disposed.

Every class LiteLLM touches today:

| Class | Fate |
|---|---|
| `ILiteLlm`, `LiteLlmClient`, `LiteLlmOptions`, `GatewayKey`/`GatewayUser`/`GatewayTeam`/`ManagedModel` records | Delete. `GatewayModel` becomes `RouteModel` in `GatewayRoutes`. |
| `ChatKey` | Delete |
| `GatewayChat` | Change internals only: no HttpClient, no ChatKey, no retry loops (they move into ModelGateway), no `UserEmailHeader` |
| `ChatGatewayException` | Keep. `From` and `Explain` stay for engine errors; the `litellm.` regex part goes. |
| `GatewayException`, `GatewayExceptionHandler` | Delete. Key-store errors are database errors; upstream errors are `ChatGatewayException` or protocol error bodies. |
| `AnswerCache` | Keep the cache. `KeyAsync` reads `KeyBook`; `AnswerCacheState.Keys` goes. |
| `AnswerCacheEndpoints.ProxyAsync` | Becomes `GatewayEndpoints.ChatCompletionsAsync`, its steps kept; the forwarding block (:671-750) is replaced by ModelGateway |
| `ApiPassage` | Delete; its paths are mapped by `GatewayEndpoints` |
| `KeySpeech` | Keep the pre-processing (voice defaults, language splice, picture credit, GpuShare, ReadyAsync); forwarding goes through ModelGateway; `PersonAsync` reads `KeyBook` |
| `KeyLine`, `AnswerGate`, `SlotTable`, `PromptLogWriter` | Keep; KeyLine takes a hash and limits from `KeyBook` |
| `KeyPeople`, `OwnKeyPlaces` | Delete (into `KeyBook`) |
| `EngineRoute` | Keep. `RoomForKeyAsync` is called by Admission. The StartForKeyAsync wording "the gateway's fallbacks answer" now refers to the app's own fallback. |
| `ModelCatalog` | `SyncGatewayAsync`, `SyncGatewayOnceAsync`, `MediaAsync`, `MediaPrices`, `Pooled`, `Info`, `Prices`, `GivenFallbacks` and `Syncing` (:985-1220) move to `GatewayRoutes.BuildAsync`, which returns `Deployment` records with no LiteLLM JSON |
| `ChatModels` | Reads `GatewayRoutes`, no longer `/model/info` with a 1-minute cache and a gateway-down fallback (ChatModels.cs:52-93) |
| `KeyAccess` | Keep `ListForAsync` and `FeatureOf`; delete `MaxParallel`, `NoModels`, `BlocksAsync`, `SyncAsync` and `KeyAccessWatcher` (KeyAccess.cs:104-245); the `keys:wake` callers (§1 of the management map) now clear `KeyBook` |
| `RateLimits` | Keep `Resolve`, `ForAsync` and `ViewAsync`; delete `ApplyAsync`, `SyncAsync` and `KeepRow`; `UseAsync`, `MinuteAsync` and `StartModelCallAsync` change readers |
| `OwnKeys` | Keep its rules (MaxKeys, names, Tighter); store in `api_keys`; no `KeyWriters`, no gateway calls |
| `PeopleService`, `CompanyPeople`, `ScimUsers`, `SignInService` | Key calls go to `ApiKeys`. `EnsureUserAsync` and `SetBudgetAsync` go. `MoveGatewayAsync` is deleted (CompanyPeople.cs:167): keys belong to the user id, so an email change moves nothing. |
| `StudioWorkspaces` | `ApiKeys.MakeAsync(kind: studio)`; `gateway_url` = `ArenaUrl` (StudioWorkspaces.cs:27, 139) |
| `KeyCheck`, `McpPeople`, `CodeArenaSync` | Read `KeyBook`; revoke through `ApiKeys` |
| `Credit`/`CreditBook`, `Ledger`, `GroupUsage`, `PromptUsage`, `UsageEndpoints`, `SpendLog`, `CostRecalculation`, `RecordRetention`, `DataRequests`, `StorageReport`, `SqlDatasource`, `DashboardStore` | Readers and writers changed (§2) |
| `GuardrailEndpoints`, `GroupTeams`, `CreditMove`, `KeyWriters` | Delete |
| `DefaultModelEndpoints` | Delete the internal path, which has no caller (§1.13) |
| `OperationsEndpoints` probe, `StackOptions.LiteLlmProbeUrl` | Replaced by `GatewayHealth` |
| `Risks.cs:215` ("The gateway (LiteLLM)") | Rename the asset to "The model gateway (in the app)" |
| New: `GatewayRoutes`, `Deployment`, `Balancer`, `Upstream` (clients `gateway-upstream` and `gateway-upstream-unchecked`, modelled on IdentityWiring.cs:507-510), `Admission`, `KeyBook`, `ApiKeys`, `RateWindows`, `RequestLog`, `SseMeter`, `Protocols/AnthropicMessages`, `Protocols/OpenAiResponses`, `Protocols/Audio`, `GatewayEndpoints`, `GatewayMetrics`, `GatewayHealth`, `LedgerImport` | |

---

## 4. Deployment

**Compose** (deploy/docker-compose.yml):

- Delete the `litellm` service (:157-172).
- Remove `Gateway__MasterKey` (:86).
- Postgres: `POSTGRES_DB: llmapp` (:150) and the healthcheck to `pg_isready -U arena -d postgres` (:155).
- Rewrite the comments at :6, :149, :161, :174, :488 and :668-670: the workspaces network is the app plus the workspaces.
- Keep port 4000 for the workspaces service itself (:99).

**Overlays and config:**

- Delete deploy/ha.yml, deploy/config/litellm.yaml and deploy/config/litellm-ha.yaml.
- deploy/features: drop "the gateway" from the core list (:7) and the `ha` row (:39). Valkey stays only under its own profile for `LMCACHE_REMOTE` (compose:95-96, 174-177).
- deploy/.env.example: remove `GATEWAY_KEY` (:39-40) and `LITELLM_REPLICAS` (:86).
- deploy/scale.yml needs no change: the gateway scales with the app replicas, and the shared state is in Postgres.

**Helm** (deploy/helm/argus-arena):

- templates/gateway.yaml: delete the litellm Deployment and Service (:56-108). Move Postgres into templates/postgres.yaml with `POSTGRES_DB: llmapp` and readiness `-d postgres`. Valkey stays only when `valkey.enabled`.
- configmaps.yaml: delete the `litellm.yaml` key (:8-10).
- availability.yaml: remove `"litellm"` from `range list "app" "web" "litellm"` (:4).
- tools.yaml: remove the litellm selector from the NetworkPolicy (:455, :474).
- app.yaml: remove `Gateway__MasterKey` (:42).
- secrets.yaml: remove `GATEWAY_KEY` (:14).
- values.yaml:
  - Remove :3 (litellm in the services list), :33 and :44 (`GATEWAY_KEY`, `gatewayKey`), :64 (`images.litellm`), :86 and :96 (`maxReplicas.litellm`) and the `litellm:` block (:129-134).
  - Rewrite the text at :122-124: only `llmapp` is needed, and all five SSL modes work.
  - Rewrite the Valkey text (:136): LMCache only. Rewrite the workspaces comment (:242).
- _helpers.tpl:105: remove the `ha` feature mapping.
- Chart.yaml: remove the `litellm` keyword (:9).
- Delete files/litellm*.yaml and their entries in check-chart.py (:23-24).
- kind-values.yaml then pulls no LiteLLM image.
- Ingress (:41-45) and NOTES.txt (:4) stay.

**Installer, bundles and the front door:**

- installer.sh:
  - Remove GATEWAY_KEY generation (:1371, :1404-1406).
  - `DATA_KEYS="DB_PASSWORD APP_KEY"` (:2200).
  - The kept-config cleanup already removes an unchanged config/litellm.yaml (:1261).
  - New upgrade step: once the app reports `gateway.imported`, drop the `litellm` database. It runs after the existing pre-upgrade backup.
  - `--remove-orphans` (:1822, :2890) and `remove_off_images` (:1691-1712) already remove the old container and image; the rollback tag (:2166-2170) keeps it for a rollback.
- kube-install.sh: remove `GATEWAY_KEY` from the Secret (:316, :323) and `litellm: {replicas}` (:362); rewrite the `--replicas` and `--valkey` help (:37-39, :219) and `[ha]=valkey` (:257).
- Remove the `GATEWAY_KEY` placeholder from airgap.sh (:164), make-installer.sh (:147) and arenactl `PLACEHOLDERS` (:50). The airgap image list follows `compose config --images` on its own (airgap.sh:150-167).

**Backups and drills:**

- backup.sh: `pg_isready -d postgres` (:397, :456; otherwise the backup falls back to a raw volume copy), and the text at :34, :393, :405, :454.
- restore-drill.sh: `-d postgres` (:42) and `for db in llmapp` (:44).
- test-project.sh: `TP_CORE="postgres app web traefik"` (:20, comment :8).
- restore-test.sh (:79) and rollback-test.sh (:181): drop `ghcr.io/berriai/litellm` from the image filters; also rollback-test.sh :111 and the comments (restore-test.sh :19, :28; rollback-test.sh :19).
- recovery-check.py text (:195, :245).

**Traefik, Prometheus and logs:**

- routes.yml: comments only (:21-24). Its tests stay: test_runtime_parity.py:352, AnswerCacheTests.cs:242.
- Prometheus: no scrape change, since the metrics come from the `app` job.
- Add rules `GatewayDeploymentDown` and `GatewayUpstreamErrors` to config/prometheus/rules/engines.yml and its Helm copy. Reword "(they set the gateway's limit)" at :36 and :62.
- Promtail, Loki and Alertmanager: nothing to change.

**CI** (.github/workflows/ci.yml):

- Remove :123-124 (`create database litellm` and the schema load) and the `Gateway__*` settings at :128.
- Rename `E2E_NO_GATEWAY` → `E2E_NO_ENGINE` (:97, :104; src/web/e2e/helpers.ts:4-5).
- Remove the litellm logs from the kind job (:223, :241) and `GATEWAY_KEY=sk-ci` (:252).
- Keep :260-264 (argus-standalone samples).
- tests/deploy: support.py:34-48, test_features.py:15, delete test_ha.py, test_runtime_parity.py:40-49/103/361-365, test_airgap.py, test_installer.py (THIRD_PARTY images, litellm.yaml cases, the drop-database step), test_kube_install.py:302-315, test_recovery.py:153, test_recovery_check.py:35-38 (drop the decoy), and the GATEWAY_KEY placeholders in test_backup.py:259, test_sglang_image.py:54, test_pitr.py:55, test_vllm_image.py:47.
- tests/Llm.Tests/HelmChartTests.cs:14 loses its litellm.yaml pair.

**Environment and config keys:**

| Removed | Where |
|---|---|
| `GATEWAY_KEY` → `Gateway__MasterKey` | .env, compose:86, Helm, installer, kube-install, airgap, make-installer, arenactl, CI, tests/deploy |
| `LITELLM_MASTER_KEY`, litellm `DATABASE_URL`, `ENGINE_API_BASE`, `ENGINE_API_KEY`, `STORE_MODEL_IN_DB`, `LITELLM_LOCAL_MODEL_COST_MAP` | compose:165-170, helm gateway.yaml:82-90 |
| `REDIS_HOST`, `REDIS_PORT` (litellm), `LITELLM_REPLICAS` | ha.yml, helm gateway.yaml:91-95, .env.example:86 |
| `Gateway:Url`, `Gateway:MasterKey`, `Gateway:ConnectWaitSeconds`, `Gateway:KeyWaitSeconds` | LiteLlmClient.cs:12-26 |
| `Chat:GatewayUrl` (and IdentityWiring.cs:278-284) | ChatOptions.cs:7 |
| `Stack:LiteLlmProbeUrl` | StackOptions.cs:7 |
| `Workspaces:GatewayUrl` | StudioWorkspaces.cs:27 |
| `Dashboards:SqlDatabase` | DashboardStore.cs:12 |
| `Keys:Watch` | KeyAccess.cs |
| `Gateway:AnswerCacheKeyCheck` (folded into `Gateway:KeyCacheSeconds`) | AnswerCache.cs:32 |
| Helm `images.litellm`, `litellm.*`, `availability.autoscale.maxReplicas.litellm`, `secrets.gatewayKey` | values.yaml |
| Database name `litellm`, datasource uid `litellm-db`, header `X-LLM-User-Email` | — |

| Added | Default |
|---|---|
| `Gateway:FirstByteTimeout`, `Gateway:IdleTimeout` | 960 s each |
| `Gateway:Retries` | 2 |
| `Gateway:LoadWait` | 2 min |
| `Gateway:PoolWait` | 30 s |
| `Gateway:LogFlush` | 1 s |
| `Gateway:KeyCacheSeconds` | 10 |
| `Gateway:Mode` | Temporary: `litellm`, `chat` or `own`; a Settings-page setting, deleted in commit 19 |

Kept: `ENGINE_KEY`, now also used by the app for chat traffic upstream; `Gateway:LogPrompts`, `AnswerCache`, `AnswerCacheTtl`, `RequestsPerMinute`, `TokensPerMinute`; `Chat:ApiRequestsPerKey`, `ApiQueueTimeout`, `RequestTimeout`; `Retention:GatewayLogDays`.

**Docs and compliance** (all compiled into the in-app Help through manual.ts):

- **Rewrite:**
  - README.md:228-235; architecture.md:19, 36, 45, 50, 87-94, 109, 135-143, 167-181.
  - deployment.md:16-17, 160, 284-305, 347-349, 422-429 (VoiceTests.cs:866 checks this section's wording), 483, 493, 510, 515, 522 (already stale), 826, 1043, 1077.
  - admin.md:552-557, 1038, 1155-1162, 1179-1189, 1248-1249, 1275-1338, 1376-1401.
  - chat.md:226, 1920-1921, 2047-2048, 2064, 2177.
  - authentication.md:15, 24, 59-64 (already stale), 534; settings.md:156-160, 175, 190; configuration.md:28, 56.
  - code-arena.md:1840, 2269-2270; hermes.md:25, 147-148, 190, 207; development.md:20, 81, 122; testing.md:12.
  - multi-gpu-server.md (27 places: §5.2, §16, the failure table and :541); clients/README.md:176-181.
  - docs/plan.md "Target" (:21-24, :50), plus a "Next" entry for this work.
- **Compliance:**
  - data-flows.md:14, 27, 40; product-evaluation.md:13 (the TOE becomes app and web).
  - controls.yaml and controls.md: DAT-07, DAT-08 and NET-01 wording. The evidence test names stay, so the compliance-docs.py guard holds. IAM-02, IAM-06, DAT-04 and DAT-08 cite tests that are rewritten but keep their names (§5).
  - Recheck help topics topics.ts:405, 420, 528, 719, 877, 883, 1137, 1140, 1162.
- **Leave as history:** CHANGELOG history, docs/measurements/model-serving.md and docs/design/v5.9-code-review-access.md.
- **Out of scope, needs a decision:** `deploy/argus-standalone`, a separate product with its own LiteLLM and no Llm.Api (argus-standalone/docker-compose.yml:395-566; src/Argus/Platform/Gateway.cs). Options: keep it on LiteLLM and say so, or retire it. Under "no footprint", LiteLLM stays in the repository only there.

---

## 5. Tests

### 5.1 What FakeGateway becomes

`tests/Llm.Tests/FakeGateway.cs` ("LiteLLM in memory", :7-296; registered at AppFixture.cs:197-199; used in 126 files) is deleted. Its roles move as follows:

| FakeGateway role | Becomes |
|---|---|
| `Keys`, `KeysOf`, key minting (`sk-<guid>`, SHA-256 token) | The real `ApiKeys` on the test Postgres; `AppFixture.KeysOf(email)` reads `gateway.api_keys`. Used by IdentityTests, CompanySignInTests, SamlSignInTests, ScimTests, LdapTests, OwnKeyTests, CodeStudioTests, FairUseTests, ModelsTests:492. |
| `Models` (Qwen3.8-Flash-Next seed) and adding picture, speech and transcription models (~25 files: ArenaMcp, Browser, ChatBranch, ChatQuote, ChatTools, Deck, DeepResearch, MediaStory, Media, Meeting, RoomCall, Talk, Storage, Quality, SmallModel, TokenCost, RateLimit; removals in Concurrency, ModelState*, Models) | `AppFixture.AddModelAsync(...)` and `RemoveModelAsync(...)`. A chat model is a `RemoteServer` "engine.test" pointing at FakeEngine; a media model is a media model row. The fixture seeds the default model the same way. |
| `Managed`, `AddModelAsync`, `DeleteModelAsync`, fingerprints (ModelsTests:291, TokenCostTests:75, MediaModelsTests:35/64/113, RemoteServersTests:51/122, ScaleOutTests:328, VllmGatewayTests:31, EngineKindTests:164, GatewaySyncTests:23) | `GatewayRoutesTests` assert `Deployment` records (upstream URL, model, auth, MaxParallel, weight, pool, hidden copies, fallback, TLS, prices), plus one end-to-end case per kind showing that FakeEngine received the request at the right host and model |
| `Fallbacks` (VllmGatewayTests:120) | Route fallback, plus a failure test before the first byte |
| `Down` (ArenaMcpTests, TokenCostTests, CompanySignInTests, LayaTests, RemoteServersTests, ScaleOutTests, SamlSignInTests, IdentityTests:269/292, RateLimitTests, ChatTests) | Cases that test "the gateway is unreachable" are deleted: it cannot happen in-process. Cases that test "the engine is down" use `FakeEngine.Refuse`. |
| `ServiceKeyFailures`, `GenerateServiceKeyAsync` (ChatTests:83, ChatToolsTests:705) | Deleted |
| `KeyLookups` (KeyCheckTests:42, ArenaMcpTests:166) | `KeyBook` cache tests: one database read per key per TTL; cleared on revoke |
| `BeforeRate`, `BeforeKeys`, `Hold` races (OwnKeyTests:98, RateLimitTests:304) | Deleted with `KeyWriters`; nothing syncs any more |
| `Teams`, `Budgets`, `Users` (GroupCreditTests:175-184, CreditMoveTests:15-53) | Deleted |
| `RateChanges` (RateLimitTests) | Assertions on resolved limits through `KeyBook`, and 429s through the API |

Other fixtures:

- **LiteLlmClientTests.cs** is deleted.
- **LitellmSeed.cs and Resources/litellm-schema.sql** become `GatewaySeed`, which writes rows r1-r5 and two keys into `gateway.requests` and `gateway.api_keys` over the same window (2026-09-01 to 2026-09-10), so DashboardTests' expected numbers stay. A copy of LitellmSeed lives on only as `LedgerImportTests`' input until the importer is deleted.
- **AppFixture.SpendAsync** (:93-130) inserts into `gateway.requests` (surface, person, credit_kind). Its callers stay: GroupCreditTests, NotificationsTests, OperationsTests, VoiceTests, TokenCostTests.
- **Direct SQL** in RateLimitTests:399/528/590/663, TokenCostTests:323/386/482, MediaStudioTests:256/413, DashboardTests:170/233/235 and StorageTests:877 moves to the new tables or EF.

### 5.2 FakeModel becomes FakeEngine

The type is renamed and keeps its recording API (`Requests`, `Passed`, `ImageRequests`, `Transcriptions`, `SpeechRequests`). It is wired to the `gateway-upstream` clients.

- **LiteLLM texts removed:**
  - `RevokedKeys`: the 401 is now the app's admission, asserted with nothing sent upstream.
  - `UnknownOnce`: deleted.
  - `LoadingOnce`: llama.cpp's own `{"error":{"code":503,"message":"Loading model"}}`.
  - `[budget]`: now produced by admission, through a key budget or credit.
  - `Overflow`: llama.cpp, vLLM and OpenAI shapes only.
- **Tag assertions** on `metadata.tags[0]` (AppTests:89, DataTests:182, DeckTests:75, ReviewTests:73, SecurityReviewTests:177, BusinessMapTests:74, MeetingTests:127, and the filters in Panel, RoomCall, Room, Vector, MemoryReflection, SkillLearning, BusinessMap) move to the logged `tag` column, because `metadata` is no longer sent upstream.
- **Assertions on `user`** stay, since `user` is still forwarded (~30 files).
- **ChatTests:122**, which checks the `X-LLM-User-Email` header, becomes an assertion on the logged `person`.

Guardrail tests become API requests through `https://gateway.llm.test` with a key, asserting the status, the error body, the logged refusal, and that FakeEngine received nothing or masked text. This covers the helpers at GroupCreditTests.cs:56-79, ConcurrencyTests.cs:1142 and :1280, SecretScanningTests.cs:156-166, and IdentityTests:239/250. Their test names stay where compliance cites them (DAT-04: SecretScanningTests).

Unchanged: CodeArena.Tests Fakes.cs (only comments change; GatewayRateLimitTests and HangCompactionTests stay valid because the sentences are kept) and Argus.Tests.

Web and e2e:

- storage.test.tsx:27 fixture database name.
- admin.spec.ts:50 (usage dashboard over the app database).
- helpers.ts rename (`E2E_NO_ENGINE`).
- setup.test.tsx is unchanged (Anthropic base URL).

### 5.3 New tests

1. `GatewayRoutesTests`: every kind of §1.2, pools, `NAME@i` hidden, media gated by modules, rebuilt on change and on an unknown name.
2. `BalancerTests`: least busy by engine counts, weights, `MaxParallel` waits then 429 after `PoolWait`.
3. `UpstreamTests`:
   - Retries only before the first byte, at most 2; never on 4xx or context overflow.
   - "Loading model" waited out; fallback used and logged.
   - A caller abort cancels the upstream call (FakeEngine sees the cancellation).
   - First-byte and idle timeouts.
   - Dialect field filtering (`id_slot`, `priority`, `metadata`, `fallbacks`).
4. `SseMeterTests`: usage parsed; cached tokens from `timings.cache_n`; the usage chunk stripped when not asked; TTFT; model rename; an error inside the stream; a bounded assembly buffer.
5. `AdmissionTests`: each of the 11 steps in §1.6 gives the right status, body and headers, writes a refused row with the reason, and sends nothing upstream. Covers `/v1/messages` and `/v1/responses` too.
6. `RateWindowsTests`: two `WebApplicationFactory` instances on one database count as one; windows roll over; tokens are charged at the end; "used this minute" is exact.
7. `KeyBudgetTests`: a rolling 30-day budget, and the "Budget has been exceeded" sentence.
8. `RequestLogTests`: one row per status (done, failed, stopped, refused, cached); batch flush and drain at shutdown; cost per call type; the `prompt_log` id link; person and surface for every caller kind (Safeguards with no person included).
9. Protocol goldens in `tests/Llm.Tests/Resources/gateway/` for Anthropic (text, tools, thinking, images, stream event order, errors, count_tokens, x-api-key) and for Responses (input items, function_call, reasoning, SSE, `previous_response_id` refused), with captures taken from LiteLLM in commit 14 and 15's shadow runs.
10. Media: transcription priced by `duration` for every `response_format`; speech priced per character; images by `n`; CORS preflight on every `/v1` path.
11. `LedgerImportTests`: LitellmSeed → rows equal to today's `Person`, `Cached`, `Model`, `KeyAlias` and `CreditKind` rules; idempotent; keys of every kind; the chat key skipped.
12. `DashboardRoleTests`: panel SQL cannot read `public."AspNetUsers"` (permission denied) and can read the `gateway` views; the existing read-only and statement-timeout tests are kept.
13. A parity test kept from commit 6 until the removal: the old and new reader SQL give equal results on the seed.
14. A migration upgrade test: `own_keys` → `api_keys`; settings rows removed.
15. `GatewayMetricsTests` and `GatewayHealthTests`.

### 5.4 Live checks, for every commit that changes the path

- deploy/scripts/clients-check.py: Claude Code `/v1/messages` with `x-api-key` (:183-185), Codex, Qwen Code, Aider, the OpenAI SDK; keys start with `sk-` (:148).
- functional-test.py, rewritten to read the app's ledger.
- benchmark.py, multiuser-bench.py, scale-test.py, people-load.py, qwen-code-realworld.py.
- A Code Studio workspace answering.
- Admin → Overview, the usage pages, chargeback CSV, the three dashboards.

---

## 6. Commits, in order

Each commit builds, passes the full Llm.Tests and CodeArena.Tests suites and the tests/deploy suite, and is pushed and redeployed live before the next, per your no-parallel-agents memory note. Until commit 18, `Gateway:Mode` defaults to `litellm`, so every commit before it ships with today's behaviour on by default.

| # | Commit | What it changes | How it is verified |
|---|---|---|---|
| 0 | Plan the app's own gateway | docs/design/own-gateway.md (this design) and a docs/plan.md "Next" entry | compliance-docs.py and release-scan guards |
| 1 | API completions: refusals, fallbacks and preflights | Three defects on today's path. (a) AnswerCache.cs:661-664: `TakeAsync`'s `ChatGatewayException` gives 503 `model_not_ready` with Retry-After and closes the prompt-log line as Refused, not a bare 500 (EngineRoute.cs:658-663; GatewayExceptionHandler.cs:8). (b) Send the fallback's model with its slot (`json["model"] = sending.Model`). (c) OPTIONS on `/v1/chat/completions` and `/chat/completions`. | New cases in ApiLineTests and AnswerCacheTests; live: a full engine gives a JSON 503 |
| 2 | Code Studio workspaces call the app | `gateway_url` = `ArenaUrl` (`http://app:8080`); `Workspaces:GatewayUrl` removed; litellm off the `workspaces` network (compose:161-162; helm tools.yaml:455/474). Workspaces now get KeyLine, AnswerGate, the prompt log and slot pinning. | CodeStudioTests (spec `gateway_url`); test_runtime_parity; the Helm render test; live: a workspace's Code Arena answers and appears in the prompt log |
| 3 | Routes built from the app's database | `GatewayRoutes.BuildAsync` and `Deployment` are extracted from SyncGatewayOnceAsync; LiteLLM JSON is produced from Deployments with byte-identical params and fingerprints, so nothing is re-registered on deploy | The existing registration tests pass unchanged; new GatewayRoutesTests; live: LiteLLM's `/model/info` unchanged after deploy (no deletes in the log) |
| 4 | The model list from the app | `ChatModels` reads GatewayRoutes; `AppFixture.AddModelAsync` replaces `Gateway.Models` in ~25 test files | Full suite; live: `/api/chat/models` JSON identical before and after |
| 5 | The gateway's tables | EF entities and migration: `gateway.requests`, `gateway.api_keys`, UNLOGGED `gateway.rate_windows`, the two views | Migration upgrade test; live migration |
| 6 | A mirror of LiteLLM's request log | `LedgerImport` on the leader, every 5 s plus a 400-day backfill, resolving person, surface, kind, refusal and cached tokens; idempotent | LedgerImportTests; the parity test (each old reader's SQL equals its new SQL on the seed); live: per-person month totals, old vs new, equal to 1e-9 |
| 7 | Credit, usage and retention read the app's ledger | §2.3 C# readers on EF; `SpendLog.Book*` writes `gateway.requests`; erasure matches the person (fixes the missed rows); repricing on the new table | TokenCostTests, MediaStudioTests, RateLimitTests, GroupCreditTests, NotificationsTests, OperationsTests, VoiceTests, a new erasure test; live: Account, Admin → People, chargeback CSV unchanged |
| 8 | Dashboards on the app's ledger | The datasource on the app database with the narrow role and search_path; uid `gateway-db`; 54 panels rewritten; stale panels replaced; audit-dashboards.py; StorageReport labels | DashboardTests on GatewaySeed (the same expected numbers); DashboardRoleTests; admin.spec.ts:50; live: audit-dashboards.py, every panel rendering |
| 9 | API keys kept by the app | `ApiKeys` makes the secret and registers it at LiteLLM with `/key/generate {key: …}` (LiteLLM's documented `key` field; check it on the deployed image); keys imported once; `own_keys` merged; `KeyBook` replaces five caches; every lookup reads it; `MoveGatewayAsync` deleted | KeyCheckTests (expiry through `expires_at`), ArenaMcpTests, OwnKeyTests, IdentityTests, CodeStudioTests on `AppFixture.KeysOf`; live: old keys still answer; new keys work through LiteLLM |
| 10 | The gateway core for the app's own calls | `ModelGateway`, `Upstream`, `Balancer`, `SseMeter`, `RequestLog`; `GatewayChat` uses it when Mode is `chat` or `own` | The chat-path classes (Chat, ChatTools, Compaction, ContextManagement, Meeting, SecurityReview, Review, BusinessMap, Deck, DeepResearch, SubAgents) also run in a collection with Mode=own; Upstream, SseMeter and RequestLog tests; live: Mode=chat for a day, compared with §7 |
| 11 | API keys through the gateway | `Admission` (§1.6), `RateWindows`, key budgets; `GatewayEndpoints` for chat completions and legacy completions in own mode; error sentences kept | AdmissionTests, RateWindowsTests, KeyBudgetTests; the guardrail tests rewritten; CodeArena GatewayRateLimitTests unchanged |
| 12 | Pictures, speech and transcription through the gateway | Images, transcriptions (verbose_json, duration price, client format), speech, translations and edits in own mode; KeySpeech pre-processing kept; CORS | VoiceTests, ModelStateMediaTests, MediaTests, TalkTests and MeetingTests in own mode; the media and CORS tests |
| 13 | Models, model info and embeddings | `/v1/models` (filtered, copies hidden), `/v1/models/{id}`, `/v1/model/info` (Code Arena shape), `/v1/embeddings` (embed route), rerank and moderations → 404 | ApiPassageTests (NET-01 names kept), CodeArenaClientTests; Code Arena live: model window and price shown |
| 14 | Anthropic's API by the app | `/v1/messages` and `count_tokens` adapter | Goldens captured from LiteLLM; clients-check.py with Claude Code live (tools, thinking, long session) |
| 15 | OpenAI's Responses API by the app | `/v1/responses` adapter | Goldens; Codex live through clients-check.py |
| 16 | Gateway metrics and health | `arena_gateway_*`; Admin → Overview per deployment; alert rules | Metrics and health tests; OperationsTests; functional-test "Model gateway" |
| 17 | Measure our gateway against LiteLLM | `arenactl gateway-compare` (or deploy/scripts/gateway-compare.py) runs §7 in both modes; results in docs/measurements/gateway.md | The gates in §7 met on the live stack |
| 18 | Our gateway by default | `Gateway:Mode` defaults to `own`; LiteLLM still deployed as a quick fallback | A soak of at least 24 h: dashboards, error rate, clients-check |
| 19 | No LiteLLM in the app | Delete everything in §3 marked Delete, plus FakeGateway, LiteLlmClientTests, LitellmSeed, litellm-schema.sql, the `Gateway:Mode` switch, the old options and the settings rows | Full suite; the build carries no "litellm" outside the importer and the argus-standalone code; live |
| 20 | Deploy without LiteLLM | All of §4: compose, ha.yml, config files, features, .env.example, installer (and the drop of the litellm database after import), kube-install, bundles, arenactl, Helm, backups and drills, CI, tests/deploy, functional-test.py rewrite | tests/deploy; the kind CI job; live: installer upgrade on the live stack (the container and image go), restore-drill, clients-check, functional-test |
| 21 | Docs, compliance and help for our gateway | All of the docs and compliance list in §4; `Risks.cs:215`; CHANGELOG ("Weakened controls" rule: none weakened), whats-new; then the release tag and `gh release create` | compliance-docs.py, release-scan.py, VoiceTests:866 doc checks, the help build |

---

## 7. Risks, and how the new gateway is measured against LiteLLM before LiteLLM is removed

### Risks

1. **Protocol translation has a long tail** (Anthropic beta features, Responses items).
   - Goldens are captured from LiteLLM on the live stack before the switch.
   - Real clients run through clients-check.py.
   - An unsupported feature gets a clear 400, never a silent drop.
   - The commit order lets `/v1/messages` and `/v1/responses` ship last; Mode=`chat` keeps API keys on LiteLLM meanwhile.
2. **Ledger write volume and table growth over 400 days.**
   - Batched COPY with a 1 s flush, a covering index, and measured writes (`pg_stat_statements`).
   - `Ledger.ReadAsync(fresh: true)` scans the month on every page view (Ledger.cs:31); if that measures slow, add a daily rollup table or monthly partitions (retention then drops a partition).
3. **Engine-copy limits and the per-key line are counted per app replica.** KeyLine is per replica; LiteLLM shared counts only with Valkey. Balancing by the engines' own counts limits the effect. A shared in-flight counter can be added if a test with 2 replicas shows a problem.
4. **Memory per stream.** Today one logged and cached API stream may buffer up to 8 MB + 4 MB (AnswerCache.cs:397-400), and the app's Helm limit is 2 GiB (values.yaml:102). The meter assembles from parsed deltas with a cap, and 128 concurrent streams are measured.
5. **The chat path is used by 30+ callers.** GatewayChat's public API is unchanged, the chat test classes run in own mode, and Mode=`chat` comes before `own`.
6. **Fields passed to remote servers.** `chat_template_kwargs` and others may be rejected by strict OpenAI-compatible clouds. A per-dialect filter applies, tested against each remote kind in use (vLLM, SGLang, Ollama, a cloud) during commit 17.
7. **Semantics that change; each goes into admin.md and the CHANGELOG:**
   - fixed one-minute windows
   - a rolling 30-day key budget, where LiteLLM reset 30 days after the last reset
   - Requests panels exclude every refusal
   - no "master key" surface
   - tokens a minute are counted as prompt − cached + completion
8. **Upgrade data.**
   - The SHA-256 key format is already relied on (AnswerCache.cs:70; OwnKey.Token).
   - The mirror runs for weeks before release, with a parity check on live data.
   - The `litellm` database is dropped only after the import mark and the installer's backup.
9. **Old Code Arena clients.** The 429 and budget sentences and the `/v1/model/info` shape are kept, and CodeArena.Tests guard them.
10. **Security.**
    - Upstream URLs come only from admin-made routes, never from a request, so there is no SSRF path.
    - `Authorization`, `x-api-key` and remote keys are never logged.
    - The engine key and remote keys are already in the app process (compose:87; ModelCatalog.cs:1065).
    - The ledger keeps less personal data than LiteLLM did.
11. **Blast radius of one process.** The app already proxies all API traffic, so this is no worse than today. App replicas (Helm `app.replicas: 2`, scale.yml) give rolling restarts.
12. **Argus standalone still uses LiteLLM** until you decide (§4).

### Measurement plan (commit 17, live stack, both modes)

**How the modes are compared.** Same hardware, same models, the same key, back to back. Each workload runs in Mode=`litellm`, then in Mode=`own`, three runs each. Mirrored shadow traffic is not used: it would double the GPU load and distort latency.

| What | Workload | Metric | Gate |
|---|---|---|---|
| Overhead per request | W1: short streamed chat, temperature 0, 64 output tokens, ×200. Also sent directly to the engine as a baseline. | Gateway TTFT overhead (via the gateway − direct) p50/p95/p99; total time | Own p95 overhead ≤ LiteLLM's, expected lower with one hop less |
| Long prefill | W2: 16k uncached prompt | TTFT; no cut-off before 960 s | Equal TTFT ± noise; never cut |
| Throughput | W3: benchmark.py `--concurrency 1,4,8,16,32,64,128` | Aggregate tokens/s, ITL p50/p95, error rate | Own ≥ LiteLLM at each level; errors ≤ LiteLLM's |
| Fairness and isolation | W4: multiuser-bench.py `single`, `parallel`, `staggered`, `longpaste`, `overflow`, `isolation`, `prefix`, `ramp`, `prefixshare` | Per-person latency, queueing, no leaks, cached prefix share | Equal or better; `isolation` clean |
| Resource cost | W3 at 32 and 128 | CPU-seconds and RSS of app plus litellm (cAdvisor `container_cpu_usage_seconds_total`); Postgres write time for the log | Own total CPU per 1k requests lower; app RSS within the 2 GiB limit |
| Usage and cost correctness | W1, W3, W5 (tools, JSON, non-stream), media W7 | Per request: prompt, cached and completion tokens equal to the engine's `usage`; cost equal to the PriceBook formula to 1e-9; ledger per person × kind over the window | Zero differences, except LiteLLM's known gaps (non-verbose transcriptions priced at 0), which are listed |
| Response shape | W5, W6 | finish_reason, tool_calls JSON valid and equal, `model` field, SSE framing; clients-check.py for Claude Code, Codex, Qwen Code, Aider and the OpenAI SDK | All green in own mode |
| Limits | rpm=5 key, 2 app replicas (scale.yml) | The 6th request in a minute gets 429 with Retry-After; "used this minute" | Own refuses at exactly 6 across replicas. LiteLLM without Valkey may allow up to 10, which is documented. |
| Fallback and retries | W8: kill a vLLM copy mid-run; stop llama.cpp; fill the engine | The request answered by the fallback or the other copy; 503 with Retry-After when nothing can answer; no retry after the first byte | No 500s; every case logged with `fallback_from`, refusal or attempts |
| Pool balance | 2 copies `flash@0` and `flash@1`, one kept busy with long requests | Requests per copy; queue time | New requests go to the idle copy; the split follows the weights |
| Abort | Client closes after 10 tokens | llama.cpp `/slots` idle, or vLLM `num_requests_running` down | Freed within 1 s in own mode; LiteLLM's figure recorded for comparison |

LiteLLM is removed (commits 19-20) only after every gate passes and the 24-hour soak (commit 18) shows error rate and latency on the dashboards no worse than the LiteLLM baseline week.

### Decisions taken (2026-10-29)

The person asked for the app's own gateway and is away; these are the safe
defaults, each easy to change before the commit that acts on it.

1. **Existing keys and spend are imported once** (`LedgerImport`): people's
   API keys, Code Arena machines and Code Studio workspaces keep working, and
   the month's credit does not reset.
2. **`deploy/argus-standalone` stays on LiteLLM** for now: it is a separate
   product with no Llm.Api. It is the only place LiteLLM remains.
3. **`/internal/models/defaults` is removed**: it has no caller.
4. **The temporary `Gateway:Mode` switch is accepted** for commits 10 to 18,
   so each step ships with the old path as a quick way back; it is deleted in
   commit 19.
