# arena-sglang: SGLang pinned from PyPI, with the stack's supervisor (services/vllm/supervisor.sh with
# ENGINE_SERVER=sglang), for a host that cannot pull lmsysorg/sglang from Docker Hub.
#
#   docker compose -f docker-compose.yml -f sglang-build.yml --profile sglang build sglang   # about 8-12 GB fetched
#
# Build arguments:
#   BASE_IMAGE      the base: python:3.13-slim (SGLang's wheels bring PyTorch with its own CUDA libraries, so no CUDA
#                   base is needed: the host's driver, >= R580 for CUDA 13, is all it takes)
#   SGLANG_VERSION  the SGLang release, pinned (0.5.21: CUDA 13 by default)
#
# It runs one SGLang server per model the app wants. Models come from the library (HF_HUB_OFFLINE: SGLang never
# fetches one itself).
ARG BASE_IMAGE=python:3.13-slim
FROM ${BASE_IMAGE}

ARG SGLANG_VERSION=0.5.21

# gcc and the C headers for the kernels compiled as they are first used (Triton, FlashInfer's JIT); procps for the supervisor.
RUN apt-get update \
 && apt-get install -y --no-install-recommends gcc g++ libc6-dev ca-certificates procps libnuma1 \
 && rm -rf /var/lib/apt/lists/*

# Checked by its metadata: SGLang's server looks for a GPU, which a build has none of. pip's cache is kept between
# builds (a cache mount, not in the image): a build that fails late does not fetch it all again.
RUN --mount=type=cache,target=/root/.cache/pip \
    python3 -m venv /opt/sglang \
 && /opt/sglang/bin/pip install "sglang==${SGLANG_VERSION}" \
 && /opt/sglang/bin/python -c "import importlib.metadata as m; print('sglang', m.version('sglang'))"

ENV PATH=/opt/sglang/bin:${PATH} \
    HF_HUB_OFFLINE=1 \
    HF_HOME=/tmp/hf \
    DO_NOT_TRACK=1 \
    CUDA_DEVICE_ORDER=PCI_BUS_ID \
    ENGINE_SERVER=sglang

# The supervisor, its helper, the launcher and the GPU probe, as compose and the chart mount them (the mounts win when present).
COPY vllm/supervisor.sh vllm/vllm-helper.py /vllm/
COPY sglang/serve.py /sglang/
COPY gpu-probe.py /gpu-probe.py

LABEL org.opencontainers.image.title="arena-sglang" \
      org.opencontainers.image.description="SGLang ${SGLANG_VERSION} with Argus Arena's supervisor"

ENTRYPOINT ["/bin/sh", "/vllm/supervisor.sh"]
