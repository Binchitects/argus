# Traefik

Traefik is the only way in, and it does TLS. `routes.yml` is all of its
configuration besides the flags in `docker-compose.yml`.

- **Nothing set**: Traefik serves its own certificate. Browsers warn once;
  tools need to skip the check (`curl -k`, `NODE_TLS_REJECT_UNAUTHORIZED=0`).
- **`ACME_EMAIL` in `.env`**: certificates from Let's Encrypt for `DOMAIN`,
  `gateway.DOMAIN` and `argus.DOMAIN`. The names must resolve to this machine
  from the internet, and port 443 must reach it.
- **A certificate of your own** (a company CA's, a wildcard): put the files
  here and a file beside `routes.yml` that names them, e.g. `certificate.yml`:

  ```yaml
  tls:
    stores:
      default:
        defaultCertificate:
          certFile: /etc/traefik/dynamic/tls.crt
          keyFile: /etc/traefik/dynamic/tls.key
  ```

  Traefik picks it up without a restart.
