# arena-vllm: vLLM pinned from PyPI on NVIDIA's CUDA runtime, with the stack's supervisor
# (docs/multi-gpu-server.md §6, option 2: Docker Hub's vllm/vllm-openai is blocked here).
#
#   docker compose -f docker-compose.yml -f vllm-build.yml --profile vllm build vllm   # approve first: about 8-12 GB
#
# Build arguments:
#   BASE_IMAGE       the base: python:3.13-slim (vLLM's wheels bring PyTorch with its own CUDA libraries, so
#                    no CUDA base is needed: the host's driver, >= R580 for CUDA 13, is all it takes)
#   VLLM_VERSION     the vLLM release, pinned (0.31.0: CUDA 13 by default, sm_120 kernels)
#   LMCACHE_VERSION  empty: no LMCache; a version (0.5.6) adds it, built against this vLLM
#                    (the KV tier's step 2; docs/multi-gpu-server.md §4)
#
# It runs services/vllm/supervisor.sh, which runs one `vllm serve` per model the app wants.
# Models come from the library (HF_HUB_OFFLINE: vLLM never fetches one itself).
ARG BASE_IMAGE=python:3.13-slim
FROM ${BASE_IMAGE}

ARG VLLM_VERSION=0.31.0
ARG LMCACHE_VERSION=

# gcc and the C headers for Triton's kernels, compiled as they are first used; procps for the supervisor.
RUN apt-get update \
 && apt-get install -y --no-install-recommends gcc libc6-dev ca-certificates procps \
 && rm -rf /var/lib/apt/lists/*

RUN python3 -m venv /opt/vllm \
 && /opt/vllm/bin/pip install --no-cache-dir "vllm==${VLLM_VERSION}" \
 && if [ -n "${LMCACHE_VERSION}" ]; then /opt/vllm/bin/pip install --no-cache-dir "lmcache==${LMCACHE_VERSION}"; fi \
 && /opt/vllm/bin/vllm --version

ENV PATH=/opt/vllm/bin:${PATH} \
    HF_HUB_OFFLINE=1 \
    HF_HOME=/tmp/hf \
    VLLM_NO_USAGE_STATS=1 \
    DO_NOT_TRACK=1 \
    CUDA_DEVICE_ORDER=PCI_BUS_ID

# The supervisor and the GPU probe, as compose and the chart mount them (the mounts win when present).
COPY vllm/supervisor.sh vllm/vllm-helper.py /vllm/
COPY gpu-probe.py /gpu-probe.py

LABEL org.opencontainers.image.title="arena-vllm" \
      org.opencontainers.image.description="vLLM ${VLLM_VERSION} with Argus Arena's supervisor"

ENTRYPOINT ["/bin/sh", "/vllm/supervisor.sh"]
