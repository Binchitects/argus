"""`lmdeploy serve api_server ARGS` for a stack without LMDeploy: the fake server, as LMDeploy, with the arguments
services/lmdeploy/serve.py runs it with (its key among them, inside the process)."""
import os
import runpy
import sys


def run() -> int:
    argv = sys.argv[1:]
    if argv[:2] != ["serve", "api_server"]:
        print("fake lmdeploy: only `lmdeploy serve api_server` is here", file=sys.stderr)
        return 2
    fake = os.path.join(os.path.dirname(__file__), "..", "..", "..", "fake_vllm.py")
    sys.argv = [fake, "serve", "--lmdeploy", *argv[2:]]
    runpy.run_path(fake, run_name="__main__")
    return 0
