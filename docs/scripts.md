# Scripts and `arenactl`

Everything you run on Argus Arena from outside the app goes through one
command at the top of the repository, **`arenactl`**. It checks what a machine
has, builds the images and Code Arena, makes the offline installer, installs,
upgrades and repairs a stack, and runs the operator's scripts and the checks.
The scripts it runs are still there, each usable alone; this page says what
each one is for and when to use it.

`arenactl` needs only Python 3.8 or newer: `./arenactl COMMAND` on Linux and
macOS, `python arenactl COMMAND` on Windows. Building Code Arena works on all
three. The stack itself runs on Linux, so installing does too, and the
offline installer is made on Linux (or WSL2).

## Start here

| I want to | Run |
|---|---|
| know what this machine is missing, with what to do about each | `./arenactl doctor` (or `--for build`, `code-arena`, `bundle`, `install`) |
| install Argus Arena on this Linux host, from this checkout | `./arenactl install --domain chat.example.com --admin-email me@example.com` |
| make one file that installs it on another host, even with no network | `./arenactl bundle --with-deps`, then on that host `sh argus-arena-VERSION-offline.run install` |
| give people Code Arena for their machines | `./arenactl code-arena` (packages for five systems in `dist/`) |
| upgrade an installation to this checkout's release | `./arenactl upgrade` |
| put an installation right | `./arenactl repair` |
| see what is installed and how it is | `./arenactl status` |
| back it up | `./arenactl backup` (daily: `sudo ./arenactl backup --install-timer`) |
| know whether the running stack works | `./arenactl check functional` |
| run the tests | `./arenactl test` |
| see every script | `./arenactl scripts` |

`./arenactl COMMAND --help` says the options of each command. The installer's
commands take every option of `deploy/scripts/installer.sh` (its `help` lists
them).

## Installing

### From a checkout, on a host with the network

```bash
git clone https://github.com/Binchitects/argus && cd argus
./arenactl doctor --for install          # what is missing, and how to get it
./arenactl install --domain chat.example.com --admin-email me@example.com
```

`install` builds the release's images for the features that will be on and
pulls the others, then runs the installer from this checkout: it checks the
host (and offers to install what is missing: Docker and its compose plugin,
NVIDIA's driver and container toolkit, the tools it uses; `--install-requirements`
does it without asking), asks what it needs to know, writes the installation
in `/srv/arena` (as root) or `~/arena`, with an `.env` whose secrets it
generates, makes the certificate, starts the stack and waits until every
service is healthy. The admin's first password is shown once.

The features (`deploy/features`) are chosen on a terminal, or with
`--with A,B` and `--without A,B`; `--cpu-only` for a host without an NVIDIA GPU.
A feature turned on later (`./arenactl repair --with laya`) has its images
built then.

### On a host with no network

On a Linux machine with the network and this checkout:

```bash
./arenactl bundle --with-deps             # dist/argus-arena-VERSION-offline.run and its .sha256
```

It builds every image (or `--no-build`, those there already), makes Code
Arena's packages when `dist/` has none of this version, and writes one file.
Options:

| option | what |
|---|---|
| `--with-deps` | Docker's static binaries, its compose plugin and NVIDIA's container toolkit (Debian's and Red Hat's packages) inside: the installer installs them from the bundle where the host lacks them, with nothing from the network. The NVIDIA driver is not inside: it is built for each kernel |
| `--models` | the models too (else they are listed, and only the embedding model goes in) |
| `--packs` | the knowledge packs in `packs/` |
| `--without A,B` | features whose images are left out: a smaller file, and those features cannot be turned on from it |
| `--no-build` | the images as this host has them |

The file is large (tens of GB with every feature; more with the models), and
that is fine: it is everything the host needs. Copy it and its `.sha256` to
the host, then:

```bash
sha256sum -c argus-arena-VERSION-offline.run.sha256
sh argus-arena-VERSION-offline.run install --dir /srv/arena
```

`sh argus-arena-VERSION-offline.run help` lists the installer's commands;
[deployment.md](deployment.md#the-offline-installer) says what each does.

### Afterwards

| command | what |
|---|---|
| `./arenactl upgrade` | this checkout's release over an installation (from 5.2.0 or newer): a backup first, the files merged, a rollback by itself on a failure |
| `./arenactl repair` | the installation checked against the release and put right; `--with A` and `--without A` turn features on and off |
| `./arenactl status` | the version each service runs, the features, health, disk and GPU |
| `./arenactl features` | the features, each on or off, with what they need |
| `./arenactl verify` | the installation's files against their checksums |
| `./arenactl remove` | the stack's containers and images; the data stays unless `--purge` |

With a bundle, the same commands run from the file: `sh argus-arena-VERSION-offline.run upgrade --dir /srv/arena`.

### The certificate and Code Arena's signature

Install, upgrade and repair make a certificate for the domain from a CA of
the deployment's own (`scripts/make-cert.sh`), unless Let's Encrypt makes one
(`--acme-email`), one is there already (it is never made over), or `--no-cert`
leaves Traefik's own. Trust `deploy/certs/ca.crt` on the machines and in the
tools that use the stack. The same CA signs the Code Arena builds the app
serves (`scripts/sign-code-arena.sh`) after every install, upgrade and
repair, so `install.sh`, `install.ps1` and `code-arena update` can check that
a build is the one this server signed. By hand:

```bash
./arenactl cert --domain chat.example.com   # again to renew, with the same CA
./arenactl cert --cert company.crt --key company.key --chain chain.crt
./arenactl sign            # after the app's image changed (the installer does it)
./arenactl sign --check    # exit 1 when the builds served are not the ones signed
```

## Code Arena's packages

```bash
./arenactl code-arena                     # linux-x64 linux-arm64 osx-x64 osx-arm64 win-x64
./arenactl code-arena linux-x64 win-x64   # some of them
./arenactl code-arena --offline           # the .NET packs from tools/offline-nuget only
```

Each system gets `dist/code-arena-VERSION-RID.tar.gz` (a `.zip` for Windows)
with the program, a `README.txt` (installing, signing in, the IDE, the
terminal, updates, the licence) and the licences, and `dist/SHA256SUMS`
lists them. The same files give the same package.

It needs the .NET SDK 10 and Node.js 20 or newer; without them, Docker or
Podman runs each in its container (`mcr.microsoft.com/dotnet/sdk:10.0`,
`node:22-alpine`), with this checkout mounted and the files written as you.
Without Node and without an engine, the IDE's page built already
(`src/CodeArena/web`) is used. The packs come from nuget.org; `--offline`
takes them from `tools/offline-nuget/` (`tools/fetch-code-arena-packs.sh`
fills it, once, on a machine with the network).

## Building the images

```bash
./arenactl build                  # the default features' images
./arenactl build --with laya      # and Laya's
./arenactl build --all            # every feature's (a bundle needs them)
./arenactl build --services app,web
```

The release's own images are built (`arena-SERVICE`, as compose names them),
the others pulled when this host lacks them. Compose 2.20 or newer is needed.
A host that cannot reach Docker Hub needs a registry mirror in Docker's
`daemon.json` (`"registry-mirrors"`); `./arenactl doctor` says when the
engine does not answer.

## Operating a stack

These run the installation's own copy of each script (`--dir DIR` picks the
installation; without it, `/srv/arena` or `~/arena`, else this checkout's
`deploy/`):

| command | script | what |
|---|---|---|
| `./arenactl backup` | `backup.sh` | the databases, settings and files into `BACKUP_DIR`; `--list`, `--verify`, `--prune`, `--install-timer` (daily, through systemd), `--pitr` |
| `./arenactl restore --from DIR` | `backup.sh --restore` | a backup put back (the stack stopped); `--with-config` also `.env` and the config files |
| `./arenactl cert` | `make-cert.sh` | the certificate (above) |
| `./arenactl sign` | `sign-code-arena.sh` | Code Arena's signature (above) |
| `./arenactl hosts` | `setup-hosts.sh` | the stack's names in `/etc/hosts`, for tools that do not resolve `*.localhost` (root) |
| `./arenactl kube COMMAND` | `kube-install.sh` | install, upgrade, status and remove on Kubernetes with Helm ([deployment.md](deployment.md#helm)) |
| `./arenactl airgap pack OUT.tar` | `airgap.sh` | a running stack carried to a host with no network, its data's models and speech models with it |

## Checking a running stack

`./arenactl check NAME [options]` runs a check against the stack (each says
its own options with `--help`):

| name | script | what it proves |
|---|---|---|
| `functional` | `functional-test.py` | the whole stack end to end, as a person and an admin: chat, keys, usage, credit, tools |
| `compliance` | `compliance-check.py` | the compliance controls ([compliance](compliance/README.md)) |
| `recovery` | `recovery-check.py` | the recovery plan's steps |
| `sandbox` | `sandbox-check.py` | the code sandbox's isolation and limits |
| `lean` | `lean-check.py` | the science lab's proof checker |
| `clients` | `clients-check.py` | Code Arena, the CLI and MCP clients against the stack |
| `gpu` | `gpu-check.sh` | a GPU server's driver, toolkit and engines ([multi-gpu-server.md](multi-gpu-server.md)) |
| `dashboards` | `audit-dashboards.py` | every dashboard panel's queries answer, with data |
| `argus` | `tools/smoke_test.py` | Argus's MCP serves agents: sign-in refused without a key, tools, packs, the index |
| `upgrade` | `upgrade-test.py` | an upgrade from the previous release, in a throwaway stack |
| `rollback` | `rollback-test.sh` | an upgrade's rollback, in a throwaway stack |
| `restore` | `restore-test.sh` | a backup restored into a throwaway stack, checked through the API |
| `restore-drill` | `restore-drill.sh` | point-in-time recovery restored and checked |
| `scale` | `scale-test.py` | several app replicas under load |
| `bench` | `multiuser-bench.py` | many people chatting at once |
| `load` | `people-load.py` | thousands of people signing in and asking |
| `benchmark` | `benchmark.py` | the model's speed and quality |
| `qwen-code` | `qwen-code-realworld.py` | Qwen Code against the gateway on real tasks |

The throwaway stacks (`upgrade`, `rollback`, `restore`) never touch the live
one: `test-project.sh`, which they read, keeps their names, volumes and ports
apart.

## Tests

```bash
./arenactl test                     # app, code-arena, argus, web, deploy
./arenactl test web deploy
./arenactl test app --filter PromptLogTests
```

The .NET suites need the SDK 10, or Docker for its container (`tools/dn`);
the web's need `npm ci` in `src/web` once. [testing.md](testing.md) says more.

## Every script

`./arenactl scripts` prints this list from the scripts themselves.

**`deploy/scripts/`: running a stack**

| script | what | run by |
|---|---|---|
| `installer.sh` | install, upgrade, repair, remove, status, verify, features, requirements | `arenactl` and the `.run` |
| `make-installer.sh` | the offline installer (`.run`), or a checkout's bundle for `arenactl install` | `arenactl bundle`, `arenactl install` |
| `airgap.sh` | a running stack packed for a host with no network, and loaded there | `arenactl airgap`, `make-installer.sh` |
| `kube-install.sh` | Kubernetes with Helm | `arenactl kube`, `installer.sh --kubernetes` |
| `backup.sh` | backups, restores, the daily timer, point-in-time recovery | `arenactl backup`, the installer, the timer |
| `make-cert.sh` | the certificate | `arenactl cert`, the installer |
| `sign-code-arena.sh` | Code Arena's signature | `arenactl sign`, the installer |
| `setup-hosts.sh` | names in `/etc/hosts` | `arenactl hosts`, `installer.sh install --hosts` |
| `gpu-check.sh`, `*-check.py`, `*-test.*`, `*bench*.py`, `people-load.py`, `audit-dashboards.py`, `qwen-code-realworld.py`, `restore-drill.sh` | the checks (above) | `arenactl check` |
| `test-project.sh` | read by `restore-test.sh` and `rollback-test.sh`, not run | |

**`tools/`: building and releasing**

| script | what |
|---|---|
| `publish-code-arena.sh` | Code Arena's programs, in the app image's build (`src/Llm.Api/Dockerfile`); `arenactl code-arena` does the same anywhere |
| `fetch-code-arena-packs.sh` | the .NET packs for `--offline` builds, into `tools/offline-nuget/` |
| `dn` | the .NET SDK in its container (`tools/dn build`, `tools/dn test`) |
| `build-packs.sh` | Argus's knowledge packs, one at a time |
| `fetch-security-sources.sh`, `security-texts.py` | the security pack's sources and pages |
| `fetch-sqlite-vec.sh` | the vector extension Argus loads into SQLite |
| `compliance-docs.py` | the compliance documents from `docs/compliance/controls.yaml` |
| `grc-catalogs.py` | the frameworks' catalogs |
| `release-scan.py` | a release's software bill of materials and known vulnerabilities: the release gate |
| `smoke_test.py` | Argus's MCP acceptance check (`arenactl check argus`) |
| `agent_client_example.py` | an example agent loop against Argus's MCP ([argus/clients.md](argus/clients.md)) |

## When something fails

| it says | do |
|---|---|
| `neither Docker nor Podman answers here` | install one (`./arenactl requirements --fix` on Linux), start its daemon (`sudo systemctl start docker`), and on Linux be in the `docker` group (sign in again after) or use rootless Podman |
| `compose 2.20+` missing | Docker's compose plugin: `./arenactl requirements --fix`, or the `docker-compose-plugin` package |
| `free disk` too little | the images take tens of GB: free space where Docker keeps them (`docker info` names the folder) or move it (`data-root` in `daemon.json`) |
| pulling an image fails (`toomanyrequests`, timeouts) | Docker Hub limits or blocks this host: sign in (`docker login`) or set a registry mirror in `/etc/docker/daemon.json` |
| `--offline: tools/offline-nuget lacks ...` | leave out `--offline` (nuget.org), or `tools/fetch-code-arena-packs.sh` on a machine with the network |
| `no git tag v5.2.0` | `git fetch --tags`; a download without git still makes a bundle that installs and repairs, but an upgrade from 5.2.0 keeps every file it finds changed |
| `the driver sees the GPU, but NVIDIA's container toolkit is not installed` | `./arenactl requirements --fix`, or a bundle made with `--with-deps` |
| `an NVIDIA card is there but its driver is not` | `--fix` installs it and asks for a reboot (exit 7: run the installer again after); or `--cpu-only` |
| a port is taken | `--http-port` and `--https-port`, or stop what listens (it is named) |
| `Making the bundle runs on Linux` | make it on a Linux machine or in WSL2; Code Arena's packages are made anywhere |

Every installer run keeps a log in `INSTALLATION/.arena-install/logs/`, and
its exit code says what happened: 0 done, 1 failed (the log says where), 2 a
wrong option, 3 a requirement not met, 4 an upgrade rolled back, 5 an upgrade
and its rollback failed, 6 checksums do not match, 7 reboot for the driver.
