# The science lab's proof checker (compose profile "lean"): Lean 4 with Mathlib, run by the sandbox's runner
# (sandbox/runner.py, SANDBOX_RUN=lean) as the 3D sandbox runs Blender: each job a Lean file checked as its own
# user, with no network at all, a read-only root and limits. A proof is "checked" only when Lean accepts it with
# no sorry; the app says so and nothing else (Lab/ProofTool.cs).
#
# Downloaded once, when the image is built: elan (Lean's installer) and the Lean toolchain (594 MB) from GitHub's
# releases, Mathlib and the packages it needs from GitHub, and Mathlib's compiled library from its cache
# (lake exe cache get): about 13 GB as an image. Apache-2.0 (Lean, Mathlib, and its packages).
FROM python:3.13-slim-bookworm

ARG ELAN_VERSION=4.2.4
ARG ELAN_SHA256=42b94d4244e8353142c456ec0e4ca6528fd898a6c604d4059f494e706e431f63
ARG LEAN_VERSION=4.35.0
ARG LEAN_SHA256=7289597a44543246c9227b8cae501767e095ce17f6d448b95fc4a8dc97268f6d

ENV ELAN_HOME=/opt/elan
ENV PATH=/opt/elan/bin:$PATH

# elan, and the toolchain from Lean's GitHub release unpacked where elan keeps it (elan itself would fetch it from
# release.lean-lang.org); both checked against their release's SHA-256.
RUN apt-get update \
 && apt-get install -y --no-install-recommends ca-certificates curl git zstd \
 && curl -fsSL -o /tmp/elan.tar.gz "https://github.com/leanprover/elan/releases/download/v${ELAN_VERSION}/elan-x86_64-unknown-linux-gnu.tar.gz" \
 && echo "${ELAN_SHA256}  /tmp/elan.tar.gz" | sha256sum -c - \
 && tar -xzf /tmp/elan.tar.gz -C /tmp \
 && /tmp/elan-init -y --no-modify-path --default-toolchain none \
 && curl -fsSL -o /tmp/lean.tar.zst "https://github.com/leanprover/lean4/releases/download/v${LEAN_VERSION}/lean-${LEAN_VERSION}-linux.tar.zst" \
 && echo "${LEAN_SHA256}  /tmp/lean.tar.zst" | sha256sum -c - \
 && mkdir -p "/opt/elan/toolchains/leanprover--lean4---v${LEAN_VERSION}" \
 && tar --zstd -xf /tmp/lean.tar.zst -C "/opt/elan/toolchains/leanprover--lean4---v${LEAN_VERSION}" --strip-components=1 \
 && elan default "leanprover/lean4:v${LEAN_VERSION}" \
 && lean --version \
 && rm -f /tmp/elan.tar.gz /tmp/elan-init /tmp/lean.tar.zst

# 1: Mathlib's cache read from its Azure store at once (a host that cannot reach cache.mathlib.org: LEAN_CACHE_AZURE=1).
ARG MATHLIB_CACHE_DEBUG_USE_LEGACY=""

WORKDIR /opt/lab
COPY lean/lakefile.toml lean/lean-toolchain lean/Check.lean ./

# Mathlib and its packages at the pinned release, then its compiled library from its cache (nothing compiled here):
# from cache.mathlib.org, or, where that does not answer within ten minutes, from the cache's Azure store. Then the
# image's own check, run as a job will run it: lean on a file, the packages on LEAN_PATH. One layer, so that the
# packages' git history it reads is not kept.
RUN MATHLIB_NO_CACHE_ON_UPDATE=1 lake update \
 && (timeout 600 lake exe cache get || MATHLIB_CACHE_DEBUG_USE_LEGACY=1 lake exe cache get) \
 && rm -rf .lake/packages/*/.git /root/.cache \
 # What checking a file never reads: the compiled C, the editor's indexes, the build's hashes and traces, the
 # cache tool, the toolchain's static libraries.
 && rm -rf .lake/packages/*/.lake/build/ir .lake/packages/mathlib/.lake/build/bin \
 && find .lake /opt/elan/toolchains -type f \( -name '*.ilean' -o -name '*.hash' -o -name '*.trace' -o -name '*.a' \) -delete \
 && LEAN_PATH="$(ls -d /opt/lab/.lake/packages/*/.lake/build/lib/lean | paste -sd:)" \
      "/opt/elan/toolchains/leanprover--lean4---v${LEAN_VERSION}/bin/lean" Check.lean \
 && apt-get purge -y curl git && apt-get autoremove -y \
 && rm -rf /var/lib/apt/lists/* \
 && chmod -R a+rX /opt/elan /opt/lab \
 && find / -xdev -perm /6000 -type f -exec chmod a-s {} +

COPY sandbox/runner.py /runner.py

ENV SANDBOX_RUN=lean \
    SANDBOX_LEAN="/opt/elan/toolchains/leanprover--lean4---v${LEAN_VERSION}/bin/lean" \
    SANDBOX_LEAN_PROJECT=/opt/lab

LABEL org.opencontainers.image.title="arena-lean" \
      org.opencontainers.image.description="Lean ${LEAN_VERSION} with Mathlib, run as Argus Arena's proof checker"

ENTRYPOINT ["python3", "-I", "/runner.py"]
