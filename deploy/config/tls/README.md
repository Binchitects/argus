# config/tls

Empty unless you bring your own certificate. Put `tls.crt` (the certificate,
followed by its chain) and `tls.key` here and run `docker compose up -d`:
tls-init serves them instead of a certificate from the stack's own CA.

Without them, the stack makes its own CA once and signs its certificate with
it (see `config/traefik/certs/ca.crt`). Files here are not committed.
