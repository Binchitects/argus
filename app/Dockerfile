# The whole app in one image: the React UI is built, then served by the API.

FROM node:22-alpine AS web
WORKDIR /web
COPY web/package.json web/package-lock.json ./
RUN npm ci --no-audit --no-fund
COPY web/ ./
RUN npm run build

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
COPY --from=web /web/dist /out/wwwroot

# Chiseled: no shell, no package manager, runs as a non-root user.
FROM mcr.microsoft.com/dotnet/aspnet:10.0-noble-chiseled
WORKDIR /app
COPY --from=api /out ./
ENV ASPNETCORE_HTTP_PORTS=8080
EXPOSE 8080
HEALTHCHECK --interval=10s --timeout=5s --start-period=30s --retries=5 CMD ["dotnet", "Llm.Api.dll", "healthcheck"]
ENTRYPOINT ["dotnet", "Llm.Api.dll"]
