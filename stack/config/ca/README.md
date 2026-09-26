# Extra certificate authorities

Put the PEM certificate of any CA the stack must trust here, as `.crt` or `.pem`:
typically the company CA that signs your GitLab. Then:

```bash
docker compose up -d && docker compose restart argus open-webui
```

`tls-init` appends every file here to `bundle.crt`, which Argus (GitLab API calls
and git mirroring) and Open WebUI verify against. Only public certificates belong
here, never a private key. The files are ignored by git.
