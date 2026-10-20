# The 3D sandbox (compose profile "3d"): Blender headless, run by the Python sandbox's runner (runner.py with
# SANDBOX_RUN=blender), each job as a user of its own, at run time with no network at all. The model writes
# Blender Python (bpy); what the job writes (.glb, .blend, .stl, renders) comes back to the chat.
#
# Blender 4.5 LTS from download.blender.org, its checksum pinned. CPU only: renders with Workbench or EEVEE's
# software path; Cycles on the CPU when asked.
FROM python:3.13-slim-bookworm

ARG BLENDER_VERSION=4.5.14
ARG BLENDER_SHA256=9ba871ff2ecd36526b77432745980b7e6664ecd0c7ca11c48849073dcfe06da3

# What Blender's binary links to, headless (no X server: --background), and Mesa's software OpenGL for
# Workbench and EEVEE renders.
RUN apt-get update \
 && apt-get install -y --no-install-recommends \
      ca-certificates curl xz-utils libx11-6 libxi6 libxxf86vm1 libxfixes3 libxrender1 libxkbcommon0 libsm6 libice6 \
      libgl1 libegl1 libgl1-mesa-dri libglu1-mesa libxext6 libxinerama1 libxcursor1 libxrandr2 libwayland-client0 libdbus-1-3 \
 && curl -fsSL -o /tmp/blender.tar.xz "https://download.blender.org/release/Blender${BLENDER_VERSION%.*}/blender-${BLENDER_VERSION}-linux-x64.tar.xz" \
 && echo "${BLENDER_SHA256}  /tmp/blender.tar.xz" | sha256sum -c - \
 && mkdir -p /opt/blender && tar -xJf /tmp/blender.tar.xz -C /opt/blender --strip-components=1 \
 && rm /tmp/blender.tar.xz \
 && apt-get purge -y curl xz-utils && apt-get autoremove -y \
 && rm -rf /var/lib/apt/lists/* \
 && /opt/blender/blender --background --factory-startup --python-expr "import bpy; print('blender', bpy.app.version_string)" \
 && find / -xdev -perm /6000 -type f -exec chmod a-s {} +

COPY sandbox/runner.py /runner.py

ENV SANDBOX_RUN=blender \
    SANDBOX_BLENDER=/opt/blender/blender \
    SANDBOX_MAX_OUT_FILE_MB=64 \
    SANDBOX_MAX_OUT_TOTAL_MB=128

LABEL org.opencontainers.image.title="arena-blender" \
      org.opencontainers.image.description="Blender ${BLENDER_VERSION} headless, run as Argus Arena's 3D sandbox"

ENTRYPOINT ["python3", "-I", "/runner.py"]
