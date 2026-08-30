# Deploying to a Linux server

The stack was developed on Windows/WSL2 but targets Linux as the production
host. Linux is the better platform for it: node-exporter measures the real
machine, DCGM works, and vLLM can use its faster V2 model runner.

---

## Quick start

```bash
git clone <your-repo> llmservice && cd llmservice
```

```bash
sudo ./scripts/install-requirements.sh
```

```bash
./scripts/bootstrap.sh && ./scripts/gen-auth.sh && ./scripts/up.sh
```

`install-requirements.sh` installs Docker Engine and the Compose plugin, the
**NVIDIA Container Toolkit**, and the supporting CLI tools; verifies GPU
passthrough; and applies the Linux-appropriate `.env` defaults.

Preview it without changing anything:

```bash
./scripts/install-requirements.sh --check-only
```

---

## What differs from Windows

| | Windows / WSL2 | Linux |
|---|---|---|
| `VLLM_USE_V2_MODEL_RUNNER` | **must be 0** — WSL2 has no UVA | `1` — the faster path |
| Host metrics | node-exporter sees the **VM**, so windows_exporter is needed | node-exporter sees the real machine |
| CPU temperature | ACPI thermal zone via windows_exporter | `hwmon`, no extra service |
| GPU exporter | `smi` only — DCGM cannot work | `smi` or **`dcgm`** (SM occupancy, PCIe, NVLink, ECC) |
| GPU wiring | Docker Desktop WSL2 integration | `nvidia-container-toolkit` + `nvidia-ctk runtime configure` |
| `host.docker.internal` | resolves automatically | **does not resolve** without `extra_hosts: host-gateway` |

The installer sets the first item and removes the Windows-only Prometheus job.
To enable the richer GPU exporter:

```bash
COMPOSE_PROFILES=smi,dcgm,logging,homepage,proxy,gateway,tracing,auth
```

---

## The NVIDIA Container Toolkit is not optional

The single most common failure. Installing the driver makes `nvidia-smi` work
**on the host**; it does not let containers see the GPU. That needs the
container toolkit and a daemon restart:

```bash
sudo nvidia-ctk runtime configure --runtime=docker && sudo systemctl restart docker
```

Verify:

```bash
docker run --rm --gpus all nvidia/cuda:12.4.1-base-ubuntu22.04 nvidia-smi
```

If that prints your GPU, everything else will work. If it does not, nothing
else will.

---

## Line endings — read this before you clone

Shell scripts with CRLF endings **fail on Linux** with
`$'\r': command not found`, because the carriage return becomes part of the
command. `.gitattributes` pins `*.sh` to `eol=lf`, which handles the normal
clone. If you copied the tree over by other means (zip, SMB share, USB), fix it:

```bash
python3 scripts/fix-line-endings.py
```

It normalises shell scripts, YAML, JSON and `.env` and reports what it changed.
This also matters for `config/traefik/auth/users.htpasswd` — a stray `\r` in a
password hash silently breaks basic-auth.

---

## A real domain and trusted certificates

`llm.localhost` only resolves on the machine itself. For a server, point a real
domain at it:

```bash
sudo ./scripts/install-requirements.sh --domain llm.example.com
```

Then decide how you want certificates.

**Private CA (default).** Keeps working with any domain and needs no inbound
internet. Every client must trust `config/traefik/certs/ca.crt`.

```bash
./scripts/gen-certs.sh --force
```

**Let's Encrypt (public domains).** Replace the static certificate block in
`config/traefik/traefik.yml` with an ACME resolver:

```yaml
certificatesResolvers:
  letsencrypt:
    acme:
      email: you@example.com
      storage: /etc/traefik/certs/acme.json
      httpChallenge:
        entryPoint: web
```

Then swap `tls: true` for `tls.certresolver=letsencrypt` on each router label,
and delete the `redirections` block on the `web` entrypoint — the HTTP-01
challenge needs port 80 reachable and unredirected. Requires a public DNS record
and inbound 80/443.

If you change `LLM_DOMAIN`, also update the certificate filenames in
`config/traefik/dynamic/tls.yml` and the redirect URIs in
`scripts/gen-auth.sh`, then regenerate:

```bash
./scripts/gen-auth.sh --force && docker compose up -d --force-recreate authelia grafana open-webui langfuse
```

> Regenerating auth with `--force` rotates the OIDC client secrets, so the three
> app containers **must** be recreated or every login fails. See
> [AUTHENTICATION.md](AUTHENTICATION.md).

---

## Hostnames

The stack routes by hostname and publishes no service ports, so names must
resolve. For the `*.localhost` development default:

```bash
sudo ./scripts/setup-hosts.sh
```

On a real server with a real domain you do not need this — point DNS at the host
and set `LLM_DOMAIN` instead.

---

## Firewall

Traefik is the only thing reachable at all: no other service publishes a port.
Set the firewall explicitly anyway:

```bash
sudo ufw allow 80/tcp && sudo ufw allow 443/tcp && sudo ufw enable
```

Do **not** re-add `ports:` mappings to individual services on an
internet-facing host: they bypass Traefik and therefore bypass SSO entirely.

---

## Starting on boot

Compose restart policies (`unless-stopped`) bring containers back when Docker
starts, so enabling Docker is usually enough:

```bash
sudo systemctl enable docker
```

For ordered startup and `systemctl status llmservice`, install a unit:

```ini
# /etc/systemd/system/llmservice.service
[Unit]
Description=LLMService stack
Requires=docker.service
After=docker.service network-online.target

[Service]
Type=oneshot
RemainAfterExit=yes
WorkingDirectory=/opt/llmservice
ExecStart=/usr/bin/docker compose up -d
ExecStop=/usr/bin/docker compose down
TimeoutStartSec=0

[Install]
WantedBy=multi-user.target
```

```bash
sudo systemctl daemon-reload && sudo systemctl enable --now llmservice
```

---

## Sizing a server

| | |
|---|---|
| GPU | Any CUDA card with enough VRAM for your model; see the README table |
| RAM | 32 GB with the `tracing` profile — ClickHouse alone wants several GB |
| Disk | 40 GB minimum. Model weights and Prometheus TSDB dominate; put `/var/lib/docker` on fast storage |
| CPU | Modest. Inference is GPU-bound; the CPU handles tokenisation and the proxy |

Watch disk especially: `PROMETHEUS_RETENTION_SIZE` caps metrics, but the model
cache is unbounded and grows with every model you try.

---

## Verifying a deployment

```bash
./scripts/health.sh
```

```bash
./scripts/smoke-test.sh
```

```bash
./scripts/audit-auth.sh
```

Those cover component health and scrape targets, the inference path end to end,
and every authentication path. All three should be clean before you call it
done.

---

## Multi-GPU

Set `VLLM_TENSOR_PARALLEL_SIZE` to the number of GPUs to shard one model across
them:

```bash
VLLM_TENSOR_PARALLEL_SIZE=2
```

To pin engines to specific cards instead — useful with the `multi-model`
profile — replace `count: all` in the compose GPU reservation with explicit
device ids:

```yaml
device_ids: ['0']
```

Tensor parallelism needs NVLink or good PCIe bandwidth to pay off; across slow
links, two independent engines often beat one sharded model.
