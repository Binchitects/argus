# Code Studio's workspaces (compose profile "workspaces"): Code Arena's own engine, one per
# workspace, each as its own user (supervisor.py), over the tools people build with: .NET's
# SDK, Node.js, Python, git, a C toolchain. The IDE's page is Arena's (Code Studio): the
# engine serves its API alone, so it is built without its page.
#
# Built from the repository root: docker build -f deploy/services/workspaces/Dockerfile .

FROM mcr.microsoft.com/dotnet/sdk:10.0 AS code-arena
WORKDIR /src
COPY global.json Directory.Build.props VERSION ./
COPY src/CodeArena/ src/CodeArena/
COPY skills/ skills/
# The one file Code Arena shares with the app (linked in its project): how a context refusal reads.
COPY src/Llm.Core/Chat/ContextOverflow.cs src/Llm.Core/Chat/
RUN rm -rf src/CodeArena/web src/CodeArena/bin src/CodeArena/obj && mkdir -p src/CodeArena/web \
 && dotnet publish src/CodeArena/CodeArena.csproj -c Release -o /opt/code-arena \
 && rm -f /opt/code-arena/*.pdb

FROM mcr.microsoft.com/dotnet/sdk:10.0
RUN apt-get update \
 && apt-get install -y --no-install-recommends python3 python3-venv python3-pip nodejs npm build-essential ripgrep less procps \
 && rm -rf /var/lib/apt/lists/*
COPY --from=code-arena /opt/code-arena/ /opt/code-arena/
RUN printf '#!/bin/sh\nexec dotnet /opt/code-arena/code-arena.dll "$@"\n' > /opt/code-arena/code-arena && chmod 755 /opt/code-arena/code-arena
COPY deploy/services/workspaces/supervisor.py /app/supervisor.py
ENV WORKSPACES_DIR=/workspaces CODE_ARENA=/opt/code-arena/code-arena DOTNET_CLI_TELEMETRY_OPTOUT=1 PYTHONUNBUFFERED=1
EXPOSE 4000
HEALTHCHECK --interval=30s --timeout=5s --start-period=20s \
  CMD ["python3", "-c", "import urllib.request; urllib.request.urlopen('http://127.0.0.1:4000/health', timeout=4)"]
CMD ["python3", "/app/supervisor.py"]
