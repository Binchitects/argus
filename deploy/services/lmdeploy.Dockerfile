# arena-lmdeploy: LMDeploy pinned from PyPI, with the stack's supervisor (services/vllm/supervisor.sh with
# ENGINE_SERVER=lmdeploy), for a host that cannot pull openmmlab/lmdeploy from Docker Hub.
#
#   docker compose -f docker-compose.yml -f lmdeploy-build.yml --profile lmdeploy build lmdeploy   # about 6-10 GB fetched
#
# Build arguments:
#   BASE_IMAGE        the base: python:3.13-slim (LMDeploy's wheels bring PyTorch with its own CUDA libraries, and
#                     TurboMind its own kernels, so no CUDA base is needed: the host's driver is all it takes)
#   LMDEPLOY_VERSION  the LMDeploy release, pinned (0.18.0)
#
# It runs one LMDeploy server per model the app wants. Models come from the library (HF_HUB_OFFLINE: LMDeploy never
# fetches one itself).
ARG BASE_IMAGE=python:3.13-slim
FROM ${BASE_IMAGE}

ARG LMDEPLOY_VERSION=0.18.0

# gcc and the C headers for the kernels compiled as they are first used (Triton); procps for the supervisor.
RUN apt-get update \
 && apt-get install -y --no-install-recommends gcc g++ libc6-dev ca-certificates procps libnuma1 \
 && rm -rf /var/lib/apt/lists/*

# Checked by its metadata: LMDeploy's server looks for a GPU, which a build has none of. pip's cache is kept between
# builds (a cache mount, not in the image).
RUN --mount=type=cache,target=/root/.cache/pip \
    python3 -m venv /opt/lmdeploy \
 && /opt/lmdeploy/bin/pip install "lmdeploy==${LMDEPLOY_VERSION}" \
 && /opt/lmdeploy/bin/python -c "import importlib.metadata as m; print('lmdeploy', m.version('lmdeploy'))"

ENV PATH=/opt/lmdeploy/bin:${PATH} \
    HF_HUB_OFFLINE=1 \
    HF_HOME=/tmp/hf \
    CUDA_DEVICE_ORDER=PCI_BUS_ID \
    ENGINE_SERVER=lmdeploy

# The supervisor, its helper, the launcher and the GPU probe, as compose and the chart mount them (the mounts win when present).
COPY vllm/supervisor.sh vllm/vllm-helper.py /vllm/
COPY lmdeploy/serve.py /lmdeploy/
COPY gpu-probe.py /gpu-probe.py

LABEL org.opencontainers.image.title="arena-lmdeploy" \
      org.opencontainers.image.description="LMDeploy ${LMDEPLOY_VERSION} with Argus Arena's supervisor"

ENTRYPOINT ["/bin/sh", "/vllm/supervisor.sh"]
