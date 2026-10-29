#!/usr/bin/env python3
"""
SGLang's server as the supervisor starts it (services/vllm/supervisor.sh, ENGINE_SERVER=sglang): its arguments as given,
and the key from the environment (SGLANG_API_KEY), added here, inside the process, so it is never on a command line that
ps shows to anyone in the container. SGLang's own workers get it from this process, not from a command line either.

    serve.py --model-path /library/hf/... --tp-size 2 ... --served-model-name NAME --host HOST --port PORT
"""
import os
import runpy
import sys

key = os.environ.pop("SGLANG_API_KEY", "")
sys.argv = ["sglang.launch_server", *sys.argv[1:], *(["--api-key", key] if key else [])]
runpy.run_module("sglang.launch_server", run_name="__main__", alter_sys=True)
