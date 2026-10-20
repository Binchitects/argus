#!/usr/bin/env python3
"""
A stand-in for vLLM's OpenAI-compatible server, for tests and for a stack without a big GPU.
Python's standard library only.

It takes the command line `vllm serve` takes (unknown options are ignored, so the real
arguments the app writes pass), and answers what the app and the gateway ask of vLLM:

    GET  /health                     200 once loaded (nothing listens while it loads, as vLLM)
    GET  /v1/models                  the served model, with max_model_len
    POST /v1/chat/completions        streamed or not, "tok" words at --tps after --ttft
    POST /v1/completions             the same, for a prompt
    GET  /metrics                    Prometheus text with vLLM's own names (vllm:num_requests_running, ...)
    POST /sleep?level=1, /wake_up    sleep mode (with --enable-sleep-mode); GET /is_sleeping
    POST /fake/state                 tests only: extra requests running or waiting, KV cache use, prefix hits

The key comes from VLLM_API_KEY (or --api-key, which the supervisor never uses: a command
line shows to anyone in the container). With a key, /v1/* need it; /health and /metrics
do not, as in vLLM.

    fake_vllm.py serve MODEL --port 8000 [--served-model-name NAME] [--max-model-len N]
        [--max-num-seqs N] [--load-delay S] [--ttft S] [--tps N] [--fail] [--crash-after S]

--fail: exits with an error after the load delay, as vLLM does when the model does not fit.
--crash-after: exits with code 9 that many seconds after it is ready, as a crashed engine.
SIGTERM: stops taking requests, lets the running ones end (up to --drain seconds), then exits 0.
"""
from __future__ import annotations

import argparse
import json
import os
import signal
import sys
import threading
import time
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer
from urllib.parse import parse_qs, urlparse


def parse(argv: list[str]) -> argparse.Namespace:
    p = argparse.ArgumentParser(prog="vllm", add_help=True)
    p.add_argument("command", nargs="?", default="serve")
    p.add_argument("model", nargs="?", default="fake-model")
    p.add_argument("--host", default="0.0.0.0")
    p.add_argument("--port", type=int, default=8000)
    p.add_argument("--served-model-name", nargs="+", default=None)
    p.add_argument("--max-model-len", type=int, default=32768)
    p.add_argument("--max-num-seqs", type=int, default=16)
    p.add_argument("--api-key", default=None)
    p.add_argument("--enable-sleep-mode", action="store_true")
    p.add_argument("--tensor-parallel-size", type=int, default=1)
    p.add_argument("--load-delay", type=float, default=float(os.environ.get("FAKE_VLLM_LOAD_DELAY", "0")))
    p.add_argument("--ttft", type=float, default=float(os.environ.get("FAKE_VLLM_TTFT", "0.05")))
    p.add_argument("--tps", type=float, default=float(os.environ.get("FAKE_VLLM_TPS", "200")))
    p.add_argument("--drain", type=float, default=30.0)
    p.add_argument("--fail", action="store_true", default=os.environ.get("FAKE_VLLM_FAIL") == "1")
    p.add_argument("--crash-after", type=float, default=None)
    args, _unknown = p.parse_known_args(argv)
    return args


class State:
    """What the server counts: requests running and waiting, tokens, prefix hits, sleep."""

    def __init__(self, args: argparse.Namespace):
        self.args = args
        self.name = (args.served_model_name or [args.model])[0]
        self.lock = threading.Lock()
        self.running = 0
        self.waiting = 0
        self.extra_running = 0
        self.extra_waiting = 0
        self.kv_usage = 0.0
        self.prompt_tokens = 0
        self.generation_tokens = 0
        self.prefix_queries = 0
        self.prefix_hits = 0
        self.preemptions = 0
        self.requests = 0
        self.sleeping = False
        self.draining = False
        self.key = os.environ.get("VLLM_API_KEY") or args.api_key
        self.started = time.time()


def metrics(s: State) -> str:
    label = f'model_name="{s.name}"'
    blocks = 4096
    lines = [
        "# HELP vllm:num_requests_running Number of requests in model execution batches.",
        "# TYPE vllm:num_requests_running gauge",
        f"vllm:num_requests_running{{{label}}} {s.running + s.extra_running}",
        "# TYPE vllm:num_requests_waiting gauge",
        f"vllm:num_requests_waiting{{{label}}} {s.waiting + s.extra_waiting}",
        "# TYPE vllm:kv_cache_usage_perc gauge",
        f"vllm:kv_cache_usage_perc{{{label}}} {s.kv_usage}",
        "# TYPE vllm:prefix_cache_queries_total counter",
        f"vllm:prefix_cache_queries_total{{{label}}} {s.prefix_queries}",
        "# TYPE vllm:prefix_cache_hits_total counter",
        f"vllm:prefix_cache_hits_total{{{label}}} {s.prefix_hits}",
        "# TYPE vllm:num_preemptions_total counter",
        f"vllm:num_preemptions_total{{{label}}} {s.preemptions}",
        "# TYPE vllm:prompt_tokens_total counter",
        f"vllm:prompt_tokens_total{{{label}}} {s.prompt_tokens}",
        "# TYPE vllm:generation_tokens_total counter",
        f"vllm:generation_tokens_total{{{label}}} {s.generation_tokens}",
        "# TYPE vllm:request_success_total counter",
        f'vllm:request_success_total{{finished_reason="stop",{label}}} {s.requests}',
        "# TYPE vllm:cache_config_info gauge",
        f'vllm:cache_config_info{{block_size="16",cache_dtype="fp8",enable_prefix_caching="True",gpu_memory_utilization="0.9",'
        f'num_cpu_blocks="0",num_gpu_blocks="{blocks}"}} 1.0',
        "# TYPE vllm:time_to_first_token_seconds histogram",
        f'vllm:time_to_first_token_seconds_bucket{{le="+Inf",{label}}} {s.requests}',
        f"vllm:time_to_first_token_seconds_count{{{label}}} {s.requests}",
        f"vllm:time_to_first_token_seconds_sum{{{label}}} {s.requests * s.args.ttft:.3f}",
    ]
    return "\n".join(lines) + "\n"


def handler(s: State):
    class Handler(BaseHTTPRequestHandler):
        protocol_version = "HTTP/1.1"

        def log_message(self, fmt, *a):  # quiet: the supervisor keeps the log
            pass

        def send(self, code: int, body: bytes | str, kind: str = "application/json"):
            data = body.encode() if isinstance(body, str) else body
            self.send_response(code)
            self.send_header("Content-Type", kind)
            self.send_header("Content-Length", str(len(data)))
            self.end_headers()
            self.wfile.write(data)

        def error(self, code: int, message: str):
            self.send(code, json.dumps({"error": {"message": message, "type": "fake_vllm", "code": code}}))

        def authorized(self) -> bool:
            if not s.key or not self.path.startswith("/v1"):
                return True
            return self.headers.get("Authorization", "") == f"Bearer {s.key}"

        def body(self) -> dict:
            n = int(self.headers.get("Content-Length") or 0)
            raw = self.rfile.read(n) if n else b""
            try:
                return json.loads(raw or b"{}")
            except json.JSONDecodeError:
                return {}

        def do_GET(self):
            path = urlparse(self.path).path
            if not self.authorized():
                return self.error(401, "Unauthorized")
            if path == "/health":
                return self.send(503 if s.draining else 200, "")
            if path == "/metrics":
                with s.lock:
                    return self.send(200, metrics(s), "text/plain; version=0.0.4")
            if path == "/v1/models":
                return self.send(200, json.dumps({"object": "list", "data": [
                    {"id": s.name, "object": "model", "owned_by": "vllm", "root": s.args.model, "max_model_len": s.args.max_model_len}]}))
            if path == "/is_sleeping":
                return self.send(200, json.dumps({"is_sleeping": s.sleeping}))
            return self.error(404, "Not Found")

        def do_POST(self):
            url = urlparse(self.path)
            path = url.path
            if not self.authorized():
                return self.error(401, "Unauthorized")
            if path == "/sleep":
                if not s.args.enable_sleep_mode:
                    return self.error(400, "Sleep mode is not enabled (--enable-sleep-mode).")
                s.sleeping = True
                return self.send(200, "")
            if path == "/wake_up":
                s.sleeping = False
                return self.send(200, "")
            if path == "/fake/state":
                b = self.body()
                with s.lock:
                    s.extra_running = int(b.get("running", s.extra_running))
                    s.extra_waiting = int(b.get("waiting", s.extra_waiting))
                    s.kv_usage = float(b.get("kv_usage", s.kv_usage))
                    s.prefix_queries = int(b.get("prefix_queries", s.prefix_queries))
                    s.prefix_hits = int(b.get("prefix_hits", s.prefix_hits))
                return self.send(200, "{}")
            if path in ("/v1/chat/completions", "/v1/completions"):
                return self.complete(path == "/v1/chat/completions")
            return self.error(404, "Not Found")

        def complete(self, chat: bool):
            b = self.body()
            if s.draining:
                return self.error(503, "The server is shutting down.")
            if s.sleeping:
                return self.error(503, "The model is sleeping: wake it up first (/wake_up).")
            if b.get("model") not in (s.name, None):
                return self.error(404, f"The model `{b.get('model')}` does not exist.")
            prompt = json.dumps(b.get("messages") if chat else b.get("prompt"))
            n = max(1, min(int(b.get("max_tokens") or b.get("max_completion_tokens") or 8), 4096))
            with s.lock:
                s.running += 1
                s.prompt_tokens += max(1, len(prompt) // 4)
                s.prefix_queries += max(1, len(prompt) // 4)
            try:
                time.sleep(s.args.ttft)
                created = int(time.time())
                rid = f"cmpl-fake-{created}-{threading.get_ident()}"
                step = 1.0 / s.args.tps if s.args.tps > 0 else 0
                if b.get("stream"):
                    self.send_response(200)
                    self.send_header("Content-Type", "text/event-stream")
                    self.send_header("Transfer-Encoding", "chunked")
                    self.end_headers()

                    def chunk(obj):
                        data = f"data: {obj if isinstance(obj, str) else json.dumps(obj)}\n\n".encode()
                        self.wfile.write(f"{len(data):x}\r\n".encode() + data + b"\r\n")
                        self.wfile.flush()

                    for i in range(n):
                        piece = "tok" if i == 0 else " tok"
                        delta = {"content": piece} if chat else None
                        choice = {"index": 0, "delta": delta, "finish_reason": None} if chat else {"index": 0, "text": piece, "finish_reason": None}
                        chunk({"id": rid, "object": "chat.completion.chunk" if chat else "text_completion", "created": created, "model": s.name, "choices": [choice]})
                        if step:
                            time.sleep(step)
                    end = {"index": 0, "delta": {}, "finish_reason": "length"} if chat else {"index": 0, "text": "", "finish_reason": "length"}
                    chunk({"id": rid, "object": "chat.completion.chunk" if chat else "text_completion", "created": created, "model": s.name, "choices": [end],
                           "usage": {"prompt_tokens": len(prompt) // 4, "completion_tokens": n, "total_tokens": len(prompt) // 4 + n}})
                    chunk("[DONE]")
                    self.wfile.write(b"0\r\n\r\n")
                    self.wfile.flush()
                else:
                    if step:
                        time.sleep(step * n)
                    text = " ".join(["tok"] * n)
                    choice = {"index": 0, "message": {"role": "assistant", "content": text}, "finish_reason": "length"} if chat else {"index": 0, "text": text, "finish_reason": "length"}
                    self.send(200, json.dumps({"id": rid, "object": "chat.completion" if chat else "text_completion", "created": created, "model": s.name,
                                               "choices": [choice], "usage": {"prompt_tokens": len(prompt) // 4, "completion_tokens": n, "total_tokens": len(prompt) // 4 + n}}))
                with s.lock:
                    s.generation_tokens += n
                    s.requests += 1
            except (BrokenPipeError, ConnectionResetError):
                pass
            finally:
                with s.lock:
                    s.running -= 1

    return Handler


def main(argv: list[str]) -> int:
    args = parse(argv)
    state = State(args)
    print(f"INFO fake vLLM: loading {args.model} as {state.name} (tp {args.tensor_parallel_size}, "
          f"GPUs {os.environ.get('CUDA_VISIBLE_DEVICES', 'all')})", flush=True)
    time.sleep(args.load_delay)
    if args.fail:
        print("ERROR fake vLLM: torch.OutOfMemoryError: CUDA out of memory (as asked: --fail)", file=sys.stderr, flush=True)
        return 1
    server = ThreadingHTTPServer((args.host, args.port), handler(state))
    server.daemon_threads = True

    def drain(*_):
        state.draining = True
        end = time.time() + args.drain
        while state.running > 0 and time.time() < end:
            time.sleep(0.05)
        threading.Thread(target=server.shutdown, daemon=True).start()

    signal.signal(signal.SIGTERM, lambda *_: threading.Thread(target=drain, daemon=True).start())
    signal.signal(signal.SIGINT, lambda *_: threading.Thread(target=drain, daemon=True).start())
    if args.crash_after is not None:
        def crash():
            time.sleep(args.crash_after)
            print("ERROR fake vLLM: engine core died (as asked: --crash-after)", file=sys.stderr, flush=True)
            os._exit(9)
        threading.Thread(target=crash, daemon=True).start()
    print(f"INFO fake vLLM: serving {state.name} on {args.host}:{args.port}", flush=True)
    server.serve_forever(poll_interval=0.1)
    print("INFO fake vLLM: stopped", flush=True)
    return 0


if __name__ == "__main__":
    sys.exit(main(sys.argv[1:]))
