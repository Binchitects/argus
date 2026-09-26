# The API: sign-in, OIDC, the chat, the admin and every /api route.
# The web (src/web) is its own image; Traefik sends /api, /connect and
# /.well-known here and everything else there.
#
# Built from the repository root: docker build -f src/Llm.Api/Dockerfile .

FROM mcr.microsoft.com/dotnet/sdk:10.0 AS api
ARG APP_VERSION=0.0.0
WORKDIR /src
COPY global.json Directory.Build.props ./
COPY src/Llm.Core/Llm.Core.csproj src/Llm.Core/
COPY src/Llm.Api/Llm.Api.csproj src/Llm.Api/
RUN dotnet restore src/Llm.Api/Llm.Api.csproj
COPY src/Llm.Core/ src/Llm.Core/
COPY src/Llm.Api/ src/Llm.Api/
RUN dotnet publish src/Llm.Api/Llm.Api.csproj -c Release --no-restore -o /out -p:AppVersion=$APP_VERSION \
 && rm -f /out/*.pdb

# Chiseled: no shell, no package manager, runs as a non-root user.
FROM mcr.microsoft.com/dotnet/aspnet:10.0-noble-chiseled
WORKDIR /app
COPY --from=api /out ./
# Time zones for the chat's date and time tool: the chiseled image has none,
# and the -extra image carries ICU as well, which nothing here needs.
COPY --from=api /usr/share/zoneinfo /usr/share/zoneinfo
ENV ASPNETCORE_HTTP_PORTS=8080
EXPOSE 8080
HEALTHCHECK --interval=10s --timeout=5s --start-period=30s --retries=5 CMD ["dotnet", "Llm.Api.dll", "healthcheck"]
ENTRYPOINT ["dotnet", "Llm.Api.dll"]
