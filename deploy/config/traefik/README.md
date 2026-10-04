# Traefik

Traefik is the only way in, and it does TLS. `routes.yml` is all of its
configuration besides the flags in `docker-compose.yml`.

- **Nothing set**: Traefik serves its own certificate. Browsers warn once;
  tools need to skip the check (`curl -k`, `NODE_TLS_REJECT_UNAUTHORIZED=0`).
- **`ACME_EMAIL` in `.env`**: certificates from Let's Encrypt for `DOMAIN`,
  `gateway.DOMAIN` and `argus.DOMAIN`. The names must resolve to this machine
  from the internet, and port 443 must reach it.
- **A certificate of your own**: `scripts/make-cert.sh` (from `deploy/`). With
  no options it makes, with openssl, a CA of this deployment's own and a
  certificate it signs for `DOMAIN`, `gateway.DOMAIN`, `argus.DOMAIN` and
  `*.DOMAIN` (825 days); run it again to renew with the same CA. With
  `--cert FILE --key FILE [--chain FILE]` it installs a company's certificate
  instead. Either way the files go to `deploy/certs/` (mounted at `/certs`) and
  the script writes `certificate.yml` here, which Traefik picks up without a
  restart:

  ```yaml
  tls:
    stores:
      default:
        defaultCertificate:
          certFile: /certs/tls.crt
          keyFile: /certs/tls.key
  ```

  Trust `deploy/certs/ca.crt` where the stack is used (the script prints how,
  for Linux, Windows, macOS, curl and Node). None of these files are committed.
  With `ACME_EMAIL` set, Let's Encrypt's certificates are served instead.
