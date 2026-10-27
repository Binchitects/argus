# The security scanners (compose profile "scanners"): opengrep's engine with Arena's own rules (rules/, tested here on
# their own examples), Bandit for Python, gitleaks for secrets in the code and its history, and Trivy's checks of
# configuration (Dockerfiles, Kubernetes, Helm, Terraform). Built once; at run time it reaches nothing, so each one's
# rules and checks are the ones built in. Each download is pinned and its SHA-256 checked.
#
# Not here: gosec and the .NET analyzers, which build the project and so fetch its packages; Arena's Go and C# rules
# stand in for them.
FROM python:3.13-slim-bookworm

ARG OPENGREP_VERSION=1.31.0
ARG OPENGREP_SHA256=02328d41e710a0305d7407de255bf556f7901f515b6cc3637f68999c54800f6c
ARG GITLEAKS_VERSION=8.30.1
ARG GITLEAKS_SHA256=551f6fc83ea457d62a0d98237cbad105af8d557003051f41f3e7ca7b3f2470eb
ARG TRIVY_VERSION=0.75.0
ARG TRIVY_SHA256=c6e65abddb348e25f10549df887045629cf28cc72453cd1c63acb717316b3f3f

# git, to check out a bundle's commit and for gitleaks to read its history (its C commands only: Perl, which only
# git's interactive and mail commands use, is removed again); opengrep's engine alone (LGPL-2.1: its command-line
# wrapper unpacks some 250 MB of Python at each start, and the scanner gives the engine its rules and files itself);
# gitleaks (MIT) and Trivy (Apache-2.0). curl only while building. One layer, so what is removed takes no room.
RUN apt-get update \
 && apt-get install -y --no-install-recommends git ca-certificates curl \
 && curl -fsSL -o /tmp/opengrep.tgz "https://github.com/opengrep/opengrep/releases/download/v${OPENGREP_VERSION}/opengrep-core_linux_x86.tar.gz" \
 && echo "${OPENGREP_SHA256}  /tmp/opengrep.tgz" | sha256sum -c - \
 && mkdir -p /opt/opengrep && tar -xzf /tmp/opengrep.tgz -C /opt/opengrep opengrep-core \
 && curl -fsSL -o /tmp/gitleaks.tgz "https://github.com/gitleaks/gitleaks/releases/download/v${GITLEAKS_VERSION}/gitleaks_${GITLEAKS_VERSION}_linux_x64.tar.gz" \
 && echo "${GITLEAKS_SHA256}  /tmp/gitleaks.tgz" | sha256sum -c - \
 && tar -xzf /tmp/gitleaks.tgz -C /usr/local/bin gitleaks \
 && curl -fsSL -o /tmp/trivy.tgz "https://github.com/aquasecurity/trivy/releases/download/v${TRIVY_VERSION}/trivy_${TRIVY_VERSION}_Linux-64bit.tar.gz" \
 && echo "${TRIVY_SHA256}  /tmp/trivy.tgz" | sha256sum -c - \
 && tar -xzf /tmp/trivy.tgz -C /usr/local/bin trivy \
 && rm /tmp/*.tgz \
 && apt-get purge -y curl && apt-get autoremove -y \
 && dpkg --remove --force-depends perl perl-modules-5.36 libperl5.36 liberror-perl \
 && rm -rf /var/lib/apt/lists/* /usr/share/doc/* \
 && /opt/opengrep/opengrep-core -version && gitleaks version && trivy --version \
 && git init -q /tmp/g && git -C /tmp/g status --short && rm -rf /tmp/g

# Bandit (Apache-2.0), and PyYAML for the rules.
RUN pip install --no-cache-dir --only-binary=:all: bandit==1.9.4 pyyaml==6.0.3 \
 && bandit --version

COPY rules /opt/rules
COPY scan.py /app/scan.py
# The rules as the engine reads them, then the engine on their examples: the image is not built if one is wrong.
RUN SCANNERS_RULES=/opt/rules python -I /app/scan.py --build-rules \
 && SCANNERS_RULES=/opt/rules python -I /app/scan.py --self-test \
 && find / -xdev -perm /6000 -type f -exec chmod a-s {} +

ENV SCANNERS_PORT=7800 \
    SCANNERS_WORK=/work \
    HOME=/tmp \
    PYTHONDONTWRITEBYTECODE=1 \
    PYTHONUNBUFFERED=1

USER 65534:65534
EXPOSE 7800
HEALTHCHECK --interval=30s --timeout=10s --start-period=20s \
  CMD ["python", "-c", "import urllib.request; urllib.request.urlopen('http://127.0.0.1:7800/health', timeout=8)"]
CMD ["python", "-I", "/app/scan.py"]
