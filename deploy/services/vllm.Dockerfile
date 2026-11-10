# arena-vllm: vLLM's own image (vllm/vllm-openai, pinned) with its GGUF plugin and the stack's supervisor, the stack's
# vLLM engine. Compose builds it with the vllm feature on (./arenactl build, or docker compose --profile vllm build vllm);
# vllm-pypi.Dockerfile builds the same from PyPI for a host that cannot pull from Docker Hub (vllm-build.yml).
#
# Build arguments:
#   VLLM_IMAGE  vLLM's own image, pinned (v0.31.0: CUDA 13, FlashInfer's kernels built ahead and nvcc for the rest);
#               compose passes VLLM_IMAGE from .env
#
# It runs services/vllm/supervisor.sh, which runs one `vllm serve` per model the app wants (through services/vllm/serve.py:
# a GGUF file's config and tokenizer from its own header). Models come from the library (HF_HUB_OFFLINE: vLLM never
# fetches one itself).
ARG VLLM_IMAGE=vllm/vllm-openai:v0.31.0
FROM ${VLLM_IMAGE}

# GGUF files from the library: vLLM 0.31 moved GGUF out of its tree into vllm-gguf-plugin (its CUDA kernels built for
# CUDA 13 and torch's stable ABI) and gguf, both pinned by hash, in a layer of their own on vLLM's. --no-deps: pip changes
# neither vLLM nor torch (what the two need is there). Checked: vLLM, torch and gguf import, and the plugin's own kernels
# load (not its slower Triton fallback).
RUN --mount=type=cache,target=/root/.cache/pip \
    printf '%s\n' \
      "vllm-gguf-plugin==0.0.5 --hash=sha256:e795779830ad8907cb0e51bf661dd7784ebaf244b0b324af07a8d3925b6a9739 --hash=sha256:36a1538d0ac6797c8b194c12d94eceed944fe945dbc2585830393cb4c3b92d5b" \
      "gguf==0.19.0 --hash=sha256:70bcd10edfe697fb2dad6e40af2234b9d8ece9a41a99761405121ebda1c3c1cd" > /tmp/gguf.txt \
 && python3 -m pip install --no-deps --require-hashes -r /tmp/gguf.txt \
 && rm /tmp/gguf.txt \
 && python3 -c "import importlib.metadata as m, vllm, torch, gguf, vllm_gguf_plugin.ops as ops; \
assert ops._CUDA_AVAILABLE, 'the GGUF plugin kernels do not load'; \
print('vllm', m.version('vllm'), 'torch', torch.__version__, 'vllm-gguf-plugin', m.version('vllm-gguf-plugin'), 'gguf', m.version('gguf'))"

# A llama GGUF file's rotary layout (vllm-gguf-rope.py: in the image only, nothing mounts it), as a vLLM general plugin
# beside vLLM: a module and its entry point, which vLLM loads in each of its processes. Checked: vLLM finds it.
COPY vllm-gguf-rope.py /tmp/arena_gguf_rope.py
RUN site="$(python3 -c 'import sysconfig; print(sysconfig.get_paths()["purelib"])')" \
 && mv /tmp/arena_gguf_rope.py "$site/arena_gguf_rope.py" \
 && mkdir "$site/arena_gguf_rope-1.dist-info" \
 && printf 'Metadata-Version: 2.1\nName: arena-gguf-rope\nVersion: 1\n' > "$site/arena_gguf_rope-1.dist-info/METADATA" \
 && printf '[vllm.general_plugins]\narena_gguf_rope = arena_gguf_rope:register\n' > "$site/arena_gguf_rope-1.dist-info/entry_points.txt" \
 && python3 -c "import importlib.metadata as m; assert 'arena_gguf_rope' in [e.name for e in m.entry_points(group='vllm.general_plugins')]; import arena_gguf_rope"

ENV HF_HUB_OFFLINE=1 \
    HF_HOME=/tmp/hf \
    VLLM_NO_USAGE_STATS=1 \
    DO_NOT_TRACK=1 \
    CUDA_DEVICE_ORDER=PCI_BUS_ID

# The supervisor, vLLM's launcher and the GPU probe, as compose and the chart mount them (the mounts win when present).
COPY vllm/supervisor.sh vllm/vllm-helper.py vllm/serve.py vllm/gguf_config.py /vllm/
COPY gpu-probe.py /gpu-probe.py

LABEL org.opencontainers.image.title="arena-vllm" \
      org.opencontainers.image.description="vLLM's own image with its GGUF plugin and Arena's supervisor"

ENTRYPOINT ["/bin/sh", "/vllm/supervisor.sh"]
