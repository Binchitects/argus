#!/usr/bin/env python3
"""
SGLang's server as the supervisor starts it (services/vllm/supervisor.sh, ENGINE_SERVER=sglang): its arguments as given,
and the key from the environment (SGLANG_API_KEY), added here, inside the process, so it is never on a command line that
ps shows to anyone in the container. SGLang's own workers get it from this process, not from a command line either.

    serve.py --model-path /library/hf/... --tp-size 2 ... --served-model-name NAME --host HOST --port PORT

SGLang reads a GGUF file itself, one file only: a part of a split one is refused here, with why. Its tokenizer comes from
a folder written from the file's own header (vllm/gguf_config.py, beside the supervisor), as SGLang's own reading of it
fails for some architectures (a llama one); --tokenizer-path in the arguments is left as it is. A llama GGUF file holds its
query and key weights reordered for llama.cpp's rotary layout (GPT-J's), which SGLang does not undo: it is told to use
that layout (rope_is_neox_style false, in --json-model-override-args), else it answers wrongly. A GGUF file starts with
--disable-prefill-cuda-graph: capturing prefill graphs of its dequantized weights takes minutes and the cache's memory.

The app gives a model a share of each GPU's memory (--mem-fraction-static 0.3: 30% for its weights and its cache), as it
gives vLLM's --gpu-memory-utilization. SGLang reads its fraction otherwise: what it leaves free of the whole GPU, the memory
other processes hold counted as its own. With another model on the GPU, 0.3 leaves it nothing ("Loaded weights leave no GPU
memory for the KV cache"): the share is made SGLang's fraction here, as the GPUs stand when it starts (what others hold
added), leaving what SGLang takes beside its fraction (its CUDA graphs, the activations: some 3 to 4 GiB) free: at least
4.5 GiB of the GPU, and 8% of a large one.
"""
import json
import os
import runpy
import sys

sys.path.insert(0, os.path.join(os.path.dirname(os.path.abspath(__file__)), "..", "vllm"))


def args(argv: list[str]) -> list[str]:
    """SGLang's arguments: a GGUF file's tokenizer folder added (written first), else as given; a ValueError says why not."""
    if "--model-path" not in argv[:-1]:
        return argv
    path = argv[argv.index("--model-path") + 1]
    if not path.lower().endswith(".gguf"):
        return argv
    import gguf_config

    if gguf_config.split_part(path):
        raise ValueError(f"SGLang cannot run {path}: it reads one GGUF file, and this is a part of one split in several. "
                         "Merge the parts into one file (llama-gguf-split --merge), or run it on vLLM or llama.cpp.")
    if gguf_config.architecture(path) == "llama":
        argv = rotary(argv)
    # Its weights dequantized as it runs: capturing a CUDA graph for each prefill size takes a minute a size and the memory
    # its cache was to have (it ran out at the second of 50 on a 24 GiB GPU). Decoding keeps its graphs.
    if not gguf_config.named(argv, ("--disable-prefill-cuda-graph", "--disable-cuda-graph")):
        argv = [*argv, "--disable-prefill-cuda-graph"]
    if gguf_config.named(argv, ("--tokenizer-path",)):
        return argv
    folder = gguf_config.prepare(path, "SGLang")
    return [*argv, "--tokenizer-path", folder] if folder else argv


# What SGLang's fraction leaves free of a GPU, for what it takes beside it (CUDA graphs, activations): at least this much,
# and this share of a large GPU.
RESERVE_BYTES = 4.5 * 2**30
RESERVE_SHARE = 0.08


def share(argv: list[str], used=None) -> list[str]:
    """--mem-fraction-static as SGLang reads it: the model's share plus what other processes hold of its busiest GPU."""
    flag = "--mem-fraction-static"
    if flag not in argv[:-1]:
        return argv
    i = argv.index(flag) + 1
    try:
        wanted = float(argv[i])
    except ValueError:
        return argv
    gpu = (used or held_by_others)()
    if gpu is None or gpu[0] <= 0:
        return argv
    held, total = gpu
    most = 1 - max(RESERVE_SHARE, RESERVE_BYTES / total)
    fraction = round(min(most, held + wanted), 3)
    print(f"serve.py: {flag} {wanted} is this model's share; other processes hold {held:.0%} of its GPU, so SGLang is given {fraction}",
          file=sys.stderr, flush=True)
    return [*argv[:i], str(fraction), *argv[i + 1:]]


def held_by_others() -> tuple[float, int] | None:
    """The largest share of memory in use on this process's GPUs (CUDA_VISIBLE_DEVICES, physical indexes) and the size of
    the smallest of them, in bytes; None unknown."""
    try:
        import pynvml

        pynvml.nvmlInit()
        try:
            ids = [int(x) for x in os.environ.get("CUDA_VISIBLE_DEVICES", "").split(",") if x.strip().isdigit()] or range(pynvml.nvmlDeviceGetCount())
            seen = [pynvml.nvmlDeviceGetMemoryInfo(pynvml.nvmlDeviceGetHandleByIndex(gpu)) for gpu in ids]
            return (max(m.used / m.total for m in seen), min(m.total for m in seen)) if seen else None
        finally:
            pynvml.nvmlShutdown()
    except Exception:
        return None


def rotary(argv: list[str]) -> list[str]:
    """The arguments with rope_is_neox_style false in --json-model-override-args (added, or put in the overrides given)."""
    flag = "--json-model-override-args"
    if flag not in argv[:-1]:
        return [*argv, flag, json.dumps({"rope_is_neox_style": False})]
    i = argv.index(flag) + 1
    return [*argv[:i], json.dumps(json.loads(argv[i]) | {"rope_is_neox_style": False}), *argv[i + 1:]]


if __name__ == "__main__":
    try:
        run = share(args(sys.argv[1:]))
    except ValueError as e:
        print(f"serve.py: {e}", file=sys.stderr, flush=True)
        sys.exit(1)
    key = os.environ.pop("SGLANG_API_KEY", "")
    sys.argv = ["sglang.launch_server", *run, *(["--api-key", key] if key else [])]
    runpy.run_module("sglang.launch_server", run_name="__main__", alter_sys=True)
