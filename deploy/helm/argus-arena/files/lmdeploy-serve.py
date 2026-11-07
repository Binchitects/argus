#!/usr/bin/env python3
"""
LMDeploy's OpenAI-compatible server as the supervisor starts it (services/vllm/supervisor.sh, ENGINE_SERVER=lmdeploy):
`lmdeploy serve api_server` with its arguments as the app wrote them, the supervisor's name, host and port in LMDeploy's
words (--model-name, --server-name, --server-port), and the key from the environment (VLLM_API_KEY), added here, inside the
process, so it is never on a command line that ps shows to anyone in the container.

    serve.py /library/hf/... --tp 2 --session-len 32768 ... --served-model-name NAME --host HOST --port PORT

LMDeploy runs a model on TurboMind where TurboMind has it, else on its PyTorch engine, by itself; its metrics (/metrics,
lmdeploy:*) are on.
"""
from __future__ import annotations

import os
import sys

RENAMED = {"--served-model-name": "--model-name", "--host": "--server-name", "--port": "--server-port"}


def args(argv: list[str]) -> list[str]:
    """LMDeploy's arguments for the supervisor's: its own names for the model's name, host and port."""
    return [RENAMED.get(a, a) for a in argv]


if __name__ == "__main__":
    run = args(sys.argv[1:])
    key = os.environ.pop("VLLM_API_KEY", "")
    sys.argv = ["lmdeploy", "serve", "api_server", *run, *(["--api-keys", key] if key else [])]
    from lmdeploy.cli.entrypoint import run as lmdeploy

    sys.exit(lmdeploy())
