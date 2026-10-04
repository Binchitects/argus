# Traefik's certificate

`scripts/make-cert.sh` puts the certificate Traefik serves here (it is mounted
at `/certs`), and writes `config/traefik/certificate.yml`, which names it:

| file | what it is |
|---|---|
| `tls.crt`, `tls.key` | the certificate for `DOMAIN`, `gateway.DOMAIN`, `argus.DOMAIN` (and `*.DOMAIN`), and its key |
| `ca.crt` | the CA that signed it: trust it on the machines and in the tools that use the stack |
| `ca.key` | the CA's key: keep it here, private; it signs the next certificate when you run the script again |

A company's certificate instead: `scripts/make-cert.sh --cert FILE --key FILE
[--chain FILE]`. Nothing here but this file is committed.
