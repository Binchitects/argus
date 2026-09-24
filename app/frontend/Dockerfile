# The web: the React build, served as static files by nginx.
# Traefik sends /api, /connect and /.well-known to the app, and everything else here.

FROM node:22-alpine AS build
WORKDIR /web
COPY package.json package-lock.json ./
RUN npm ci --no-audit --no-fund
COPY . .
# NO_MINIFY=1 builds a readable bundle for chasing production-only bugs.
ARG NO_MINIFY=
RUN NO_MINIFY=$NO_MINIFY npm run build

# Alpine's own nginx package: no image from Docker Hub beyond the base.
FROM alpine:3
RUN apk add --no-cache nginx \
 && rm -rf /etc/nginx/http.d /var/www
COPY nginx.conf /etc/nginx/nginx.conf
COPY --from=build /web/dist /srv/web
ARG APP_VERSION=0.0.0
LABEL org.opencontainers.image.title="llm-web" org.opencontainers.image.version="$APP_VERSION"
USER nginx
EXPOSE 8080
HEALTHCHECK --interval=15s --timeout=3s --start-period=5s --retries=3 CMD wget -qO /dev/null http://127.0.0.1:8080/healthz || exit 1
# -e stderr: nginx opens its built-in error log before reading the config, and
# that path is on the read-only root.
CMD ["nginx", "-e", "stderr", "-g", "daemon off;"]
