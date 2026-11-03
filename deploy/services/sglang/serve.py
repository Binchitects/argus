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
that layout (rope_is_neox_style false, in --json-model-override-args), else it answers wrongly.
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
    if gguf_config.named(argv, ("--tokenizer-path",)):
        return argv
    folder = gguf_config.prepare(path, "SGLang")
    return [*argv, "--tokenizer-path", folder] if folder else argv


def rotary(argv: list[str]) -> list[str]:
    """The arguments with rope_is_neox_style false in --json-model-override-args (added, or put in the overrides given)."""
    flag = "--json-model-override-args"
    if flag not in argv[:-1]:
        return [*argv, flag, json.dumps({"rope_is_neox_style": False})]
    i = argv.index(flag) + 1
    return [*argv[:i], json.dumps(json.loads(argv[i]) | {"rope_is_neox_style": False}), *argv[i + 1:]]


if __name__ == "__main__":
    try:
        run = args(sys.argv[1:])
    except ValueError as e:
        print(f"serve.py: {e}", file=sys.stderr, flush=True)
        sys.exit(1)
    key = os.environ.pop("SGLANG_API_KEY", "")
    sys.argv = ["sglang.launch_server", *run, *(["--api-key", key] if key else [])]
    runpy.run_module("sglang.launch_server", run_name="__main__", alter_sys=True)
