# Drop a private CA here

This directory exists so that the bind mount in `docker-compose.yml` never has
to be created by Docker. A directory Docker creates is owned by root and empty,
which is the failure this whole stack spent the longest tracking down: the
container starts, the mount resolves, and every file inside it is missing.

Put the PEM bundle that signed your GitLab server's certificate here, then
point `ARGUS_GITLAB_CA_CERT` at it **as the container sees it**:

```dotenv
# .env
ARGUS_GITLAB_CA_CERT=/etc/argus/tls/gitlab-ca.pem
```

A file named `gitlab-ca.pem` in this directory appears at
`/etc/argus/tls/gitlab-ca.pem` inside the container. The public roots stay
loaded alongside it, so a GitLab that redirects to a public host keeps working.

## If there is no CA file

Some GitLab instances serve a self-signed certificate and the CA is simply not
available to you. Set this instead, and leave `ARGUS_GITLAB_CA_CERT` empty:

```dotenv
ARGUS_GITLAB_VERIFY=false
```

That turns certificate verification off for every API call and every clone to
that GitLab, so anything able to answer on its hostname can read the access
token. It is a last resort, not a convenience: setting it *and*
`ARGUS_GITLAB_CA_CERT` is refused at startup rather than silently resolved,
because a CA bundle is exactly what makes verification possible.

Both settings apply to the API **and** to the `git` clones. Argus reaches
GitLab over two transports that cannot see each other's TLS configuration, so
configuring only one is the failure that looks like a bad credential.
