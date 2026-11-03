# arena-vllm from PyPI: vLLM pinned, with the GGUF plugin and the stack's supervisor, for a host that cannot pull
# vLLM's own image from Docker Hub (vllm.Dockerfile builds arena-vllm from that one; docs/multi-gpu-server.md §6).
#
#   docker compose -f docker-compose.yml -f vllm-build.yml --profile vllm build vllm   # approve first: about 8-12 GB
#
# It lacks what vLLM's own image adds: FlashInfer's kernels built ahead and nvcc to build the rest. On an Ampere GPU (an
# RTX 3090 here) an fp8 token cache needs FlashInfer to build a kernel as the model starts, which fails in this image
# ("Could not find nvcc"): give such a model its own cache type (auto), or use vllm.Dockerfile.
#
# Build arguments:
#   BASE_IMAGE       the base: python:3.13-slim (vLLM's wheels bring PyTorch with its own CUDA libraries, so
#                    no CUDA base is needed: the host's driver, >= R580 for CUDA 13, is all it takes)
#   VLLM_VERSION     the vLLM release, pinned (0.31.0: CUDA 13 by default, sm_120 kernels)
#   LMCACHE_VERSION  empty: no LMCache; a version (0.5.6) adds it, built against this vLLM
#                    (the KV tier's step 2; docs/multi-gpu-server.md §4)
#
# It runs services/vllm/supervisor.sh, which runs one `vllm serve` per model the app wants (through services/vllm/serve.py:
# a GGUF file's config and tokenizer from its own header). Models come from the library (HF_HUB_OFFLINE: vLLM never
# fetches one itself).
ARG BASE_IMAGE=python:3.13-slim
FROM ${BASE_IMAGE}

ARG VLLM_VERSION=0.31.0
ARG LMCACHE_VERSION=

# gcc and the C headers for Triton's kernels, compiled as they are first used; procps for the supervisor.
RUN apt-get update \
 && apt-get install -y --no-install-recommends gcc libc6-dev ca-certificates procps \
 && rm -rf /var/lib/apt/lists/*

# Checked by its metadata: `vllm --version` looks for a GPU, which a build has none of. pip's cache is kept
# between builds (a cache mount, not in the image): a build that fails late does not fetch 6 GB again.
RUN --mount=type=cache,target=/root/.cache/pip \
    python3 -m venv /opt/vllm \
 && /opt/vllm/bin/pip install "vllm==${VLLM_VERSION}" \
 && if [ -n "${LMCACHE_VERSION}" ]; then /opt/vllm/bin/pip install "lmcache==${LMCACHE_VERSION}"; fi \
 && /opt/vllm/bin/python -c "import importlib.metadata as m; print('vllm', m.version('vllm'))"

# GGUF files from the library: vLLM 0.31 moved GGUF out of its tree into vllm-gguf-plugin (its CUDA kernels built for
# CUDA 13 and torch's stable ABI) and gguf, both pinned by hash, in a layer of their own so the one above stays cached.
# --no-deps: pip changes neither vLLM nor torch (what the two need is there). Checked: vLLM, torch and gguf import, and
# the plugin's own kernels load (not its slower Triton fallback).
RUN --mount=type=cache,target=/root/.cache/pip \
    printf '%s\n' \
      "vllm-gguf-plugin==0.0.5 --hash=sha256:e795779830ad8907cb0e51bf661dd7784ebaf244b0b324af07a8d3925b6a9739 --hash=sha256:36a1538d0ac6797c8b194c12d94eceed944fe945dbc2585830393cb4c3b92d5b" \
      "gguf==0.19.0 --hash=sha256:70bcd10edfe697fb2dad6e40af2234b9d8ece9a41a99761405121ebda1c3c1cd" > /tmp/gguf.txt \
 && /opt/vllm/bin/pip install --no-deps --require-hashes -r /tmp/gguf.txt \
 && rm /tmp/gguf.txt \
 && /opt/vllm/bin/python -c "import importlib.metadata as m, vllm, torch, gguf, vllm_gguf_plugin.ops as ops; \
assert ops._CUDA_AVAILABLE, 'the GGUF plugin kernels do not load'; \
print('vllm', m.version('vllm'), 'torch', torch.__version__, 'vllm-gguf-plugin', m.version('vllm-gguf-plugin'), 'gguf', m.version('gguf'))"

# A llama GGUF file's rotary layout (vllm-gguf-rope.py: in the image only, nothing mounts it), as a vLLM general plugin
# beside vLLM: a module and its entry point, which vLLM loads in each of its processes. Checked: vLLM finds it.
COPY vllm-gguf-rope.py /tmp/arena_gguf_rope.py
RUN site="$(/opt/vllm/bin/python -c 'import sysconfig; print(sysconfig.get_paths()["purelib"])')" \
 && mv /tmp/arena_gguf_rope.py "$site/arena_gguf_rope.py" \
 && mkdir "$site/arena_gguf_rope-1.dist-info" \
 && printf 'Metadata-Version: 2.1\nName: arena-gguf-rope\nVersion: 1\n' > "$site/arena_gguf_rope-1.dist-info/METADATA" \
 && printf '[vllm.general_plugins]\narena_gguf_rope = arena_gguf_rope:register\n' > "$site/arena_gguf_rope-1.dist-info/entry_points.txt" \
 && /opt/vllm/bin/python -c "import importlib.metadata as m; assert 'arena_gguf_rope' in [e.name for e in m.entry_points(group='vllm.general_plugins')]; import arena_gguf_rope"

ENV PATH=/opt/vllm/bin:${PATH} \
    HF_HUB_OFFLINE=1 \
    HF_HOME=/tmp/hf \
    VLLM_NO_USAGE_STATS=1 \
    DO_NOT_TRACK=1 \
    CUDA_DEVICE_ORDER=PCI_BUS_ID

# The supervisor and the GPU probe, as compose and the chart mount them (the mounts win when present).
COPY vllm/supervisor.sh vllm/vllm-helper.py vllm/serve.py vllm/gguf_config.py /vllm/
COPY gpu-probe.py /gpu-probe.py

LABEL org.opencontainers.image.title="arena-vllm" \
      org.opencontainers.image.description="vLLM ${VLLM_VERSION} from PyPI with its GGUF plugin and Argus Arena's supervisor"

ENTRYPOINT ["/bin/sh", "/vllm/supervisor.sh"]
