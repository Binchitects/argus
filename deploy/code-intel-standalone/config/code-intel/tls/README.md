# PEM files for the code-intel container

This directory is mounted at `/etc/code-intel/tls` inside the container. It exists
in the repository so the bind mount never has to be created by Docker (as an
empty, root-owned directory). Its contents are ignored by git.

## Serving HTTPS

Drop a certificate and its key here, then in `.env`:

```dotenv
CODE_INTEL_TLS_CERT=/etc/code-intel/tls/cert.pem
CODE_INTEL_TLS_KEY=/etc/code-intel/tls/key.pem
```

The app, its API and MCP are then served over HTTPS on `CODE_INTEL_HTTP_PORT`, and
the session cookie is marked `Secure`.

## A GitLab on a private CA

Put the PEM bundle that signed GitLab's certificate here and point at it as the
container sees it:

```dotenv
CODE_INTEL_GITLAB_CA_CERT=/etc/code-intel/tls/gitlab-ca.pem
```

The public roots stay loaded alongside it. It applies to the API **and** to
every `git` clone — two transports that cannot see each other's TLS settings,
so configuring only one looks like a bad token.

With no CA file at all, `CODE_INTEL_GITLAB_VERIFY=false` turns verification off for
that GitLab. It is a last resort: anything answering on its hostname can then
read the token. Setting it together with `CODE_INTEL_GITLAB_CA_CERT` is refused.
