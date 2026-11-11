# The app's own gateway against LiteLLM

Was replacing LiteLLM with the app's own gateway a good idea? It was. Commit 17 of
[own-gateway.md](../design/own-gateway.md) asked for measured gates before the own
gateway becomes the default. Every gate measured passes:

- it adds a third of LiteLLM's latency over the engine;
- it moves as many tokens a second at every level from 1 to 128 at once;
- it books every answer in the ledger;
- it frees the engine's slot as fast when a client stops;
- it never answers 500 when the engine is down.

It also uses less CPU and no extra process. Removing LiteLLM frees about 1 GB of memory.

## How it was measured

- **Where:** the offline test host (`arena-e2e`), 8.0.0 installed over 7.10.1 with every feature.
- **Hardware and model:** one RTX 3090, llama.cpp as a router, Qwen3-4B-Q4_K_M with 4 slots.
- **Order:** both modes back to back, three runs each, in the order LiteLLM, own, LiteLLM, own, LiteLLM, own. `Gateway:Mode` was switched through the admin settings between runs.
- **Who:** a throwaway person with their own key. A second person was added for W4.
- **Tool:** `deploy/scripts/gateway-compare.py --model Qwen3-4B-Q4_K_M --requests 200 --engine-down`, on 2026-11-11, which took about 4 hours.
- **Results:** every number is in [gateway-results.json](gateway-results.json). The tables give medians of the three runs.

| | Workload |
|---|---|
| W1 | 200 short streamed chats, temperature 0, 64 output tokens. The same requests are also sent straight to the engine as the baseline, in every run. |
| W2 | 3 long uncached prompts of 5,000 words each, so the first token comes after about a minute. |
| W3 | 128 output tokens per request, at 1, 4, 8, 16, 32, 64 and 128 at once, with CPU and memory of the app and LiteLLM sampled meanwhile. |
| W4 | `multiuser-bench.py` with two people: single, parallel, staggered, longpaste, overflow, isolation and prefix. |
| W5 | One plain answer (not streamed) and one tool call. |
| Limits | A key allowed 5 requests a minute is asked 8 times. |
| Abort | The client closes after 10 tokens; the engine's `/slots` is watched until the slot is idle. |
| W8 | llama.cpp is stopped, 3 requests are sent, then it is started again; the statuses and the time until it answers again are recorded. |

## Results

### Latency over the engine (W1)

| | LiteLLM | Own | Engine direct |
|---|---|---|---|
| First token p50 | 0.096 s | 0.090 s | 0.084 s |
| First token p95 | 0.104 s | 0.093 s | 0.086 s |
| First token p99 | 0.109 s | 0.095 s | 0.089 s |
| **Overhead over the engine, p50** | **11.7 ms** | **5.8 ms** | |
| **Overhead over the engine, p95** | **17.1 ms** | **6.4 ms** | |
| Whole answer p50 | 1.693 s | 1.683 s | 1.678 s |

LiteLLM costs 17 ms at p95; the own gateway costs 6 ms. That is the hop the design removed (app → LiteLLM → engine became app → engine). Its spread is also tighter: p99 is 3 ms above p50, against LiteLLM's 13 ms.

### Long prompts (W2)

First token after 60.83 s (LiteLLM) and 60.70 s (own), p95 88.1 s and 88.0 s. No request was cut off in either mode.

### Throughput (W3)

| At once | LiteLLM tokens/s | Own tokens/s | LiteLLM ITL p95 | Own ITL p95 | Errors |
|---|---|---|---|---|---|
| 1 | 38.0 | 38.1 | 26.4 ms | 26.3 ms | 0 / 0 |
| 4 | 88.0 | 88.4 | 46.0 ms | 45.3 ms | 0 / 0 |
| 8 | 88.4 | 88.2 | 45.8 ms | 46.1 ms | 0 / 0 |
| 16 | 87.0 | 87.5 | 46.6 ms | 46.2 ms | 0 / 0 |
| 32 | 88.2 | 87.6 | 46.4 ms | 46.1 ms | 0 / 0 |
| 64 | 88.7 | 87.8 | 46.1 ms | 46.0 ms | 0 / 0 |
| 128 | 88.9 | 87.8 | 45.8 ms | 46.1 ms | 0 / 0 |

The engine is full at 4 at once, its slot count. Beyond that both gateways pass the queue on to it, and the rate is the engine's.

At 32 and above the own gateway is 0.5 to 1.2% lower in most runs, though not all: at 64 and 128 one own run was the fastest of the six. The design's gate asks for "≥". The script counts within 2% as equal, so this is within noise, but it is written down rather than rounded away. The own gateway holds nothing back (when every copy is full, it sends anyway and the engine queues) and its HTTP client has no connection cap, so the cause is not known. Running W3 on the H200 host, where the engine is not the limit at 4, will tell.

### Two people (W4)

Every scenario answered in both modes.

| Scenario | LiteLLM | Own |
|---|---|---|
| single, first token | 1.07 s | 1.06 s |
| parallel (A and B at once) | 0.81 s each | 0.80 s each |
| staggered | 0.44 s | 0.44 s |
| longpaste: B's chat while A pastes 28k tokens | 14.8 s | 14.8 s |
| overflow: A's third request has to queue | 0.29 s | 0.27 s |
| prefix: A again, then B with the same prompt | 0.28 s, 14,191 cached | 0.25 s, 14,192 cached |
| isolation: each person sees only their own answer | clean in all 3 runs | clean in all 3 runs |

### Correctness

| | LiteLLM | Own |
|---|---|---|
| Ledger: answers sent / rows booked done / answers whose usage is not in the ledger | 712 / 715 / 0 | 712 / 715 / 0 |
| Plain answer (W5) | 200, usage, `stop` | 200, usage, `stop` |
| Tool call (W5) | 200, 1 call, valid JSON | 200, 1 call, valid JSON |
| Limits: passed / refused with 429 | 5 / 3, Retry-After 60 | 5 / 3, Retry-After 58–59 (the seconds to the minute's end) |
| Abort: the engine's slot free after | 0.11–0.15 s | 0.11–0.14 s |

The 3 rows more than answers are the limit test's refusals, booked as refused.

After the runs, `deploy/scripts/accounting-check.py` compared Admin → Accounting with the ledger over the same 24 hours: 5,605 requests, 1,521,088 tokens and spend 0.62004428. The totals, people, models, sources, kinds, the series, shares, tags and rows all agree (22 of 22 checks). Before the fix in afbdaa75, spend was rounded to 4 places per row, so the rows did not add up to the total (19 of 22).

### The engine down (W8)

| | LiteLLM | Own |
|---|---|---|
| Status while llama.cpp is stopped | 500, `litellm.InternalServerError: … Connection error` | 502, "The model server could not be reached: … (llamacpp:8080)" |
| Answers again after the start | 1.6–2.1 s | 2.2 s |

The design asks for no 500s. The own gateway gives 502 with the deployment named, as §1.10 says for an unreachable upstream. The table in the measurement plan says "503 with Retry-After when nothing can answer"; that is the busy case (every copy full past `Gateway:PoolWait`), which this run did not reach.

### What it costs to run (W3, at the highest level)

| | LiteLLM mode | Own mode |
|---|---|---|
| App CPU (mean) | 2.5% | 3.4% |
| LiteLLM CPU (mean) | 5.6% | 0.7% (idle, still deployed) |
| **CPU, both** | **8.1%** | **4.1%** |
| App memory (peak) | 458 MiB | 492 MiB |
| LiteLLM memory (peak) | 1,018 MiB | 1,012 MiB (idle) |

The own gateway does LiteLLM's work in the app for about 1 point more of the app's CPU and 34 MiB more of its memory. LiteLLM's 5.6% and its 1 GB go away once it is removed (commits 19–20).

## Gates

| Gate (own-gateway.md, measurement plan) | Result |
|---|---|
| Own p95 overhead ≤ LiteLLM's | **pass**: 6.4 ms against 17.1 ms |
| Long prefill: equal first token, never cut | **pass** |
| Throughput ≥ LiteLLM at each level, errors ≤ | **pass** within 2%: 0.5–1.2% lower at 32–128 (see above); 0 errors |
| Fairness and isolation | **pass**: equal or better, isolation clean |
| Usage and cost in the ledger | **pass**: 0 answers missing, Accounting 22 of 22 |
| Response shape (W5) | **pass** |
| Limits: the 6th request in a minute refused | **pass** (one replica) |
| Abort: slot freed within 1 s | **pass**: 0.11–0.14 s |
| No 500s with the engine down | **pass**: 502 naming the server |

## After commit 18: the gates left (2026-11-12)

On the same test host with the own gateway the default, run by `deploy/scripts/gateway-scale-check.py` and
`clients-check.py`, the app as two replicas (`scale.yml`):

| Gate | Result |
|---|---|
| Limits across two replicas: a 5-a-minute key, 8 requests asked of each replica in turn | **pass**: 5 answered, 3 got 429 with Retry-After, both replicas asked |
| Pool balance: the model twice (llama.cpp, and the same engine through a proxy, added as another GPU server), 40 answers 8 at once | **pass**: 19 and 21 (47.5% and 52.5% for equal weights) |
| A copy killed mid-run (the proxy stopped), 20 answers next | **pass**: 20 of 20 answered, none failed, 2 retried on the other copy (attempts 2 in the ledger), none by the copy gone. This found a gap, fixed in 335ba91d: a copy not reached went to the model's fallback, not to the pool's other copy |
| Media: speech and its transcription with a person's key | **pass**: both answered (the words heard back), booked with their characters and seconds at their prices |
| Media: a picture | **not measured**: the host has no picture model's file (FLUX.2-klein-4B did not load within five minutes; the gateway said so with 503 model_not_ready) |
| `clients-check.py` | **pass**, 15 of 15: the OpenAI API (list, chat, stream, tool call), Anthropic's Messages and OpenAI's Responses, the spend booked, Qwen Code answering and writing its file through the gateway. Claude Code, Codex and Aider are not installed here; their wire (Messages, Responses) is checked |

## Still to measure

- **vLLM and SGLang** copies, and their health: llama.cpp only here (H200 host).
- **A picture through the gateway**, on a host with a picture model.
- **The 24-hour soak** (`deploy/scripts/gateway-soak.py`): error rate, the first token's p95 and the app's memory hour by hour, and every answer in the ledger.
