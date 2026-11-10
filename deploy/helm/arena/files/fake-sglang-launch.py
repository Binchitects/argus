"""`python -m sglang.launch_server ARGS` for a stack without SGLang (PYTHONPATH=/fake-vllm/sglang-module): the fake server,
as SGLang, with the arguments services/sglang/serve.py runs it with (its key among them, inside the process)."""
import os
import runpy
import sys

if __name__ == "__main__":
    fake = os.path.join(os.path.dirname(__file__), "..", "..", "fake_vllm.py")
    sys.argv = [fake, "serve", "--sglang", *sys.argv[1:]]
    runpy.run_path(fake, run_name="__main__")
