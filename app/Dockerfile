# The API. The web (app/frontend) is its own image; Traefik sends /api,
# /connect and /.well-known here and everything else there.

FROM mcr.microsoft.com/dotnet/sdk:10.0 AS api
ARG APP_VERSION=0.0.0
WORKDIR /src
COPY Directory.Build.props Llm.slnx ./
COPY src/Llm.Core/Llm.Core.csproj src/Llm.Core/
COPY src/Llm.Api/Llm.Api.csproj src/Llm.Api/
RUN dotnet restore src/Llm.Api/Llm.Api.csproj
COPY src/ src/
RUN dotnet publish src/Llm.Api/Llm.Api.csproj -c Release --no-restore -o /out -p:AppVersion=$APP_VERSION \
 && rm -f /out/*.pdb

# Chiseled: no shell, no package manager, runs as a non-root user.
FROM mcr.microsoft.com/dotnet/aspnet:10.0-noble-chiseled
WORKDIR /app
COPY --from=api /out ./
ENV ASPNETCORE_HTTP_PORTS=8080
EXPOSE 8080
HEALTHCHECK --interval=10s --timeout=5s --start-period=30s --retries=5 CMD ["dotnet", "Llm.Api.dll", "healthcheck"]
ENTRYPOINT ["dotnet", "Llm.Api.dll"]
