#!/usr/bin/env python3
"""
vLLM's server as the supervisor starts it (services/vllm/supervisor.sh, ENGINE_SERVER=vllm): `vllm serve` with its arguments
as given. For a GGUF file from the library it first writes the model's config, tokenizer and generation config from the
file's own header (gguf_config.py), which vLLM's GGUF plugin reads, and names them with --tokenizer. Arguments that name a
tokenizer or a config already (a snapshot's) are left as they are. A file split in parts is named by its first part: the
plugin reads the others beside it.

    serve.py serve /library/ORG/REPO/model-Q4_K_M.gguf --max-model-len 8192 ... --served-model-name NAME --host HOST --port PORT

A file transformers cannot read (an architecture it does not know) ends here with why, in the model's log.
"""
from __future__ import annotations

import os
import sys

import gguf_config


def args(argv: list[str]) -> list[str]:
    """`vllm serve`'s arguments: a GGUF file's config and tokenizer folder added (written first), else as given."""
    if len(argv) < 2 or argv[0] != "serve" or not argv[1].lower().endswith(".gguf") or gguf_config.named(argv, ("--tokenizer", "--hf-config-path")):
        return argv
    folder = gguf_config.prepare(argv[1], "vLLM")
    return [*argv, "--tokenizer", folder] if folder else argv


def main(argv: list[str]) -> int:
    try:
        run = args(argv)
    except ValueError as e:
        print(f"serve.py: {e}", file=sys.stderr, flush=True)
        return 1
    os.execvp("vllm", ["vllm", *run])
    return 0


if __name__ == "__main__":
    sys.exit(main(sys.argv[1:]))
