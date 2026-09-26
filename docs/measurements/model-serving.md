# Serving the models: what was measured

> Measured with the benchmark scripts of the earlier stack, through the same LiteLLM
> gateway and llama.cpp engine this stack runs; the settings are in `stack/env-samples/`.

## Throughput

**RTX 5090, the shipped samples.** i7-14700K (20 physical cores), 123 GB RAM, RTX 5090
32 GB, NVMe, no power caps (the CPU peaked at 80 °C under two people). 400-token answers through the gateway, medians of 3 rounds, q8_0 KV cache, 256K context:

| | one person | two people, each | 28,500-token prompt | VRAM |
|---|---|---|---|---|
| Qwen3.8-Flash-Next UD-IQ4_XS, `N_CPU_MOE=37`, `-ub 2048` (sample) | **26.9 tok/s** | 16.1 tok/s | 48 s | 31.0 GB |
| Qwen3.8-Flash-Next UD-IQ4_XS, `N_CPU_MOE=36`, `-ub 1024` | 28.2 tok/s | 16.6 tok/s | 75 s | 28.2 GB |
| Qwen3.8-Flash-Next NVFP4 W4A16 (180 GB), `N_CPU_MOE=39` | 23.9 tok/s | 13.9 tok/s | 90 s | 29.6 GB |
| Qwen3.8-27B NVFP4, MTP 2 drafts (sample) | **112.8 tok/s** | 83.2 tok/s | 7.8 s | 28.2 GB |
| Qwen3.8-27B NVFP4, MTP 3 drafts | 107.0 tok/s | 89.3 tok/s | | |
| Qwen3.8-27B NVFP4, no MTP | 77.7 tok/s | 66.4 tok/s | | |

The NVFP4 build of Flash-Next keeps attention, shared experts and per-layer embeddings
in BF16, which makes it larger than RAM plus VRAM, so part of it is always paged from
NVMe; UD-IQ4_XS fits in memory and is faster at everything measured. For the 27B, NVFP4
runs entirely on the card and MTP with 2 drafts is the fastest for one person (~69% of
drafts accepted); 3 drafts helps two people a little and one person less.

The rest of this section is from the earlier machine, which no sample targets any more:
i7-13700K (16 physical cores), 61 GB RAM, RTX 3090, NVMe, with the
power limits below applied. 

**Two people at once** — Qwen3.8-Flash-Next, 400-token answers through the gateway
(medians of 3 rounds, repeated across 4 engine restarts):

| | decode | time to first token |
|---|---|---|
| one person | **20.2 tok/s** | 0.3 s |
| two people, each | **12.1 tok/s** | 2.0 s |
| two people, combined | 24.4 tok/s (1.2× one) | |

Other scenarios from the same script: a third request on two slots queued 18 s and then
ran at full speed; secrets in two concurrent prompts never crossed (0 leaks in 3 rounds);
a repeated 14,000-token prompt answered in 0.3 s from the prefix cache, for the other
person too.

**Qwen3.8-27B on the same 3090** — 131K context in 23.8 of 24 GB VRAM. **10.3 tok/s
with MTP** (2 drafted tokens, ~70% accepted) against 6.8 without: for a dense model on
the GPU, MTP is a 50% win. Measured at the 150 W GPU cap, which it hits; raise
`GPU_POWER_LIMIT_W` for a 27B deployment.

**Qwen Code on a large codebase** — Qt Creator 4.11.2 (12,091 files), headless, scored
against answers fixed beforehand with grep. It found the
text editor's duplicate-selection implementation at the right line in an 8,708-line
file (247 s), and answered a question about the 222,876-line `sqlite3.c` correctly with
grep and ranged reads instead of reading it whole (255 s; 63,345 of 74,100 prompt
tokens served from the prefix cache).

**Power limits.** This board shipped with Intel's CPU power limits removed (4095 W).
Under two users the CPU held 93–97 °C, and fewer threads did not help — measured at
16, 12, 10 and 8 — because package temperature follows the hottest core, and each busy
core runs flat out. Capping power did:

| two people, 16 threads | no caps | CPU 125 W, GPU 150 W |
|---|---|---|
| peak CPU temperature | 95 °C | **86 °C** |
| samples at or above 90 °C | 17% | **0%** |
| decode, one person / each of two | 20.4 / 12.2 tok/s | 20.2 / 11.8 tok/s |

**The caps are not what limits Flash-Next on the 3090, and raising them is not a
speed-up.** Measured directly: `GPU_POWER_LIMIT_W` 150 → 350 changed decode by
nothing (21.25 → 21.5 tok/s, n=4 each) — the card never drew more than 169 W peak
or 134 W average at 22–38% SM utilisation, so it is not power-limited.
`CPU_POWER_LIMIT_W` 125 → 253 bought +2% (inside the run-to-run spread) for
**+43 °C** of package temperature. Both are best left at the values in `.env`.
The same measurement rules out the other obvious levers: moving expert layers to
the GPU (`N_CPU_MOE` 42 → 40) changed nothing and spent 2.2 GB of VRAM, and
raising `-ub` to 2048 lifted prompt processing only 8.5% while costing 7% of
decode. On this hardware the engine is at its documented number — **20.9 tok/s
warm against 21.7 in the table below** — and the limit is that a 94 GB model is
running on 61 GB of RAM.

**MTP does not help Qwen3.8-Flash-Next here.** Four alternating runs, same build:
off 20.3 / 12.2 tok/s (one / each of two), on 19.9 / 11.6. Each drafted token routes to
different experts in system RAM, so verification multiplies the slow part. Mainline
llama.cpp has no MTP graph for this architecture anyway (ggml-org/llama.cpp#28243);
`LLAMACPP_ENGINE_URL` can run Unsloth's build that has one, and the stock image and
that build measured the same within noise with MTP off.

## RAM

**Pinning.** `LLAMACPP_MLOCK=auto` (the default in every sample) pins the weights in
RAM with `--mlock` whenever they live in system RAM and fit beside
`LLAMACPP_RAM_RESERVE_GB`. Pinned, nothing is ever read from disk again. The engine
log says what it decided and why, with the numbers, for example:

```
memory: pinning 52 GB of weights in RAM (--mlock); 76 GB left for the rest
memory: weights run on the GPU; nothing in RAM to pin
memory: NOT pinning -- model 87 GB exceeds RAM 61 GB minus the 8 GB reserve; ...
```

Pinning is not forced when the model is larger than RAM, because it cannot work:
the kernel would thrash or kill something to honour it. For Qwen3.8-Flash-Next that
means **96 GB+ of RAM to pin it**; the 27B runs entirely on the GPU and has nothing to
pin. `LLAMACPP_PRELOAD=auto` reads a model that fits into the page cache before
serving, at full sequential disk speed, rather than during the first requests.

**When the model is larger than RAM** (Flash-Next on 64 GB), the weights page in from
NVMe on demand. RAM is still fully used — as page cache, which `free` reports under
`buff/cache`, not `used`. Measured on a 61 GB machine under two users, the paging
fades as the cache settles, and decode speed never suffered:

| after start | NVMe reads | major page faults | decode, one / each of two |
|---|---|---|---|
| ~5 min | 24 MB/s | 524/s | 19.9 / 11.9 tok/s |
| ~12 min | 10 MB/s | 251/s | 19.9 / 11.9 tok/s |
| ~20 min | 6 MB/s | 198/s | 21.2 / 12.6 tok/s |

**Swap.** `HOST_SWAPPINESS` sets the host's `vm.swappiness` (the power-limits service
applies it and restores the original when cleared). Measured: 60 and 150 made no
difference here — the improvement above is the cache warming — so the samples leave
the system default. Idle services' memory goes to swap on its own (5.4 GB after 20
minutes), which is what gives the model the room.

## The traps

**The reboot trap.** If the checkout lives on a removable or automounted volume,
Docker's `restart: unless-stopped` starts the stack before the volume mounts and binds
empty directories. Mount it from `/etc/fstab` with `x-systemd.before=docker.service`:

```
UUID=<uuid>  /mnt/data  ntfs3  defaults,nofail,x-systemd.before=docker.service,uid=1000,gid=1000  0 0
```

**Never change `LITELLM_SALT_KEY` after the first start.** It encrypts credentials the
gateway stores in its database, which become unreadable.

**A long paste stalls the other person.** A 28,500-token paste took 97 s to process,
and a short question sent 2 s later waited 95 s behind it: the engine processes one
prompt at a time. Chat-sized prompts are unaffected, and agents re-sending a
conversation hit the prefix cache (a repeated 14,000-token prompt answered in 0.3 s).

**Set `BIND_ADDRESS`.** The samples use `127.0.0.1`. `0.0.0.0` serves everything to
your network.

**`-t` must be the physical core count, never logical.** On the 13700K (16 physical,
24 logical): 24 threads 10.2 tok/s, **16 threads 21.7**, 8 threads 17.9. That is
`LLAMACPP_THREADS`.

**On a startup CUDA OOM, lower `-ub` before the context.** The compute buffer scales
with context × ubatch and appears in no weights-plus-KV calculation; at 256K context
`-ub 4096` asked for 15 GB in one allocation, `-ub 1024` needed 3.7 GB.

**Empty replies.** These models return their reasoning in a separate
`reasoning_content` field. With a small `max_tokens` the whole budget goes to
reasoning and the reply is empty. Give it 300+ tokens.

**Low GPU utilisation with Flash-Next.** Expected: the experts run on the CPU, and the
card idles between attention layers.
