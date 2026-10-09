#!/usr/bin/env python3
"""
A stand-in for docker and podman on PATH, for the deploy script tests: it logs
every call (one JSON list a line, to FAKE_LOG) and answers the few the scripts
make with small fake files, so they run with no container engine at all.

    FAKE_IMAGES       what `compose config --images` prints (lines)
    FAKE_SERVICES     what `compose config --services` prints; unset: it fails
    FAKE_MISSING      image references `image inspect` does not find
    FAKE_VOLUMES      volumes `volume inspect` finds
    FAKE_VOLUME_LIST  what `volume ls -q` prints (no filter)
    FAKE_ENGINE       a folder standing for the engine volume (`cat /engine/X`)
    FAKE_CONFIG       what `compose config` prints (the normalised compose file)
    FAKE_PROFILES     what `compose config --profiles` prints

(With FAKE_STATE, compose config reads the compose files themselves, below.)

With FAKE_STATE (a JSON file) docker (or FAKE_STATE_ENGINE) keeps a store, for installer.sh: images by
reference (load, tag, rm, inspect), volumes, and the containers `compose up`
makes, one per service of FAKE_SERVICES. Compose's images are read from the
compose files (COMPOSE_FILE, -f, else docker-compose.yml and its override): a
service's image:, else PROJECT-service when it builds. A container is healthy
unless its image was loaded from a reference in FAKE_UNHEALTHY; the services in
FAKE_DOWN keep restarting. As podman, a short name tagged lands under localhost/,
and a short name is looked for there first, as Podman does.
"""
from __future__ import annotations

import gzip
import hashlib
import io
import json
import os
import sys
import tarfile

# The services' users, as installer.sh expects them on their volumes.
OWNERS = {"engine": "1000:1000", "directory": "1000:1000", "sandbox": "1000:1000", "audio": "1000:1000",
          "argus": "10001:10001", "loki": "10001:10001", "prometheus": "65534:65534", "alertmanager": "65534:65534"}


def lines(name: str) -> list[str]:
    return [x for x in os.environ.get(name, "").splitlines() if x.strip()]


def mount(args: list[str], target: str) -> str | None:
    """The host side of `-v HOST:TARGET[:ro]`."""
    for i, a in enumerate(args):
        if a == "-v" and i + 1 < len(args):
            parts = args[i + 1].split(":")
            if len(parts) >= 2 and parts[1] == target:
                return parts[0]
    return None


def norm(ref: str) -> str:
    """A reference as both engines resolve a short name."""
    for prefix in ("docker.io/library/", "localhost/"):
        if ref.startswith(prefix):
            ref = ref[len(prefix):]
    name = ref.rsplit("/", 1)[-1]
    return ref if ":" in name or "@" in ref else ref + ":latest"


PODMAN = os.path.basename(sys.argv[0]) == "podman"


def is_short(ref: str) -> bool:
    """A name with no registry in it (offtest-app, grafana/loki:3.4.1)."""
    first = ref.split("/", 1)[0]
    return "/" not in ref or ("." not in first and ":" not in first and first != "localhost")


def store_key(ref: str) -> str:
    """Where a reference is kept: Podman keeps its own localhost/ names apart."""
    if PODMAN and ref.startswith("localhost/"):
        return "localhost/" + norm(ref)
    return norm(ref)


def keys(ref: str) -> list[str]:
    """The names a reference may mean, the first found wins: Podman looks under localhost/ first."""
    return (["localhost/" + norm(ref)] if PODMAN and is_short(ref) else []) + [store_key(ref)]


class Store:
    def __init__(self, path: str):
        self.path = path
        try:
            with open(path) as f:
                self.s = json.load(f)
        except (OSError, ValueError):
            self.s = {}
        for key, empty in (("images", {}), ("origins", {}), ("containers", []), ("volumes", {}), ("next", 1)):
            self.s.setdefault(key, empty)

    def save(self) -> None:
        with open(self.path, "w") as f:
            json.dump(self.s, f, indent=1)

    def name(self, ref: str) -> str | None:
        """The name in the store a reference finds."""
        return next((k for k in keys(ref) if k in self.s["images"]), None)

    def image(self, ref: str) -> str | None:
        if self.name(ref):
            return self.s["images"][self.name(ref)]
        ref = norm(ref)
        if ref.startswith("sha256:") or len(ref.split(":")[0]) == 64:
            want = ref.split(":")[-1] if ref.startswith("sha256:") else ref.split(":")[0]
            return want if want in self.s["images"].values() else None
        return None

    def origin(self, image_id: str) -> str:
        return self.s["origins"].get(image_id, "")


def stateful(args: list[str]) -> int | None:
    """installer.sh's calls against the store; None: not one of them."""
    st = Store(os.environ["FAKE_STATE"])
    s = st.s
    project = os.environ.get("COMPOSE_PROJECT_NAME", "arena")
    if args[0] == "info":
        print("fake engine")
        return 0
    if args[0] == "--version":
        print("Fake engine version 1")
        return 0
    if args[:2] == ["image", "inspect"]:
        image_id = st.image(args[-1])
        if not image_id:
            print(f"Error: No such image: {args[-1]}", file=sys.stderr)
            return 1
        print("sha256:" + image_id)
        return 0
    if args[0] == "load":
        path = args[args.index("-i") + 1]
        raw = open(path, "rb").read()
        if raw[:2] == b"\x1f\x8b":
            raw = gzip.decompress(raw)
        ref = raw.decode().split()[2]
        image_id = hashlib.sha256(raw).hexdigest()
        s["images"][store_key(ref)] = image_id
        s["origins"][image_id] = norm(ref)
        st.save()
        print(f"Loaded image: {ref}")
        return 0
    if args[0] == "save":
        out = args[args.index("-o") + 1]
        image_id = st.image(args[-1]) or "missing"
        with open(out, "w") as f:
            f.write(f"fake image {norm(args[-1])} {image_id}\n")
        return 0
    if args[0] == "tag":
        image_id = st.image(args[1])
        if not image_id:
            print(f"Error: No such image: {args[1]}", file=sys.stderr)
            return 1
        # podman tag puts a short name under localhost/.
        s["images"]["localhost/" + norm(args[2]) if PODMAN and is_short(args[2]) else store_key(args[2])] = image_id
        st.save()
        return 0
    if args[:2] in (["image", "rm"], ["rmi"]) or args[0] == "rmi":
        ref = st.name(args[-1])
        if ref is None:
            return 1
        image_id = s["images"][ref]
        others = [r for r, i in s["images"].items() if i == image_id and r != ref]
        if not others and any(c["image_id"] == image_id for c in s["containers"]):
            print("Error: image is in use by a container", file=sys.stderr)
            return 1
        del s["images"][ref]
        st.save()
        return 0
    if args[:2] == ["volume", "inspect"]:
        return 0 if args[-1] in s["volumes"] or args[-1] in lines("FAKE_VOLUMES") else 1
    if args[:2] == ["volume", "create"]:
        s["volumes"][args[-1]] = {"labels": [a for a in args if "=" in a]}
        st.save()
        print(args[-1])
        return 0
    if args[:2] == ["volume", "ls"]:
        for name, v in s["volumes"].items():
            labels = [a.split("=", 1)[1] for a in args if a.startswith("label=")]
            if not labels or all(lab in v["labels"] for lab in labels):
                print(name)
        return 0
    if args[:2] == ["volume", "rm"]:
        s["volumes"].pop(args[-1], None)
        st.save()
        return 0
    if args[:2] == ["network", "ls"]:
        return 0
    if args[0] == "ps":
        flt = [args[i + 1] for i, a in enumerate(args) if a == "--filter"]
        rows = s["containers"]
        for f in flt:
            key, value = f.split("=", 1)
            if key == "label" and value.startswith("com.docker.compose.project="):
                rows = [c for c in rows if c["project"] == value.split("=", 1)[1]]
            elif key == "ancestor":
                rows = [c for c in rows if c["image_id"] == value.replace("sha256:", "")]
        if "-a" not in args and "-aq" not in args:
            rows = [c for c in rows if c["state"] == "running"]
        for c in rows:
            print(c["name"] if "--format" in args else c["id"])
        return 0
    if args[0] == "inspect":
        fmt = args[args.index("--format") + 1]
        for key in args[args.index("--format") + 2:]:
            c = next((c for c in s["containers"] if key in (c["id"], c["name"])), None)
            if c is None:
                return 1
            if "com.docker.compose.service" in fmt:
                print(f'{c["service"]}|/{c["name"]}|{c["state"]}|{c["health"]}|sha256:{c["image_id"]}|{c["image_ref"]}')
            elif "working_dir" in fmt:
                print(c["workdir"])
            else:
                print(c["project"])
        return 0
    if args[0] == "rm":
        ids = [a for a in args[1:] if not a.startswith("-")]
        s["containers"] = [c for c in s["containers"] if c["id"] not in ids]
        st.save()
        return 0
    if args[0] == "exec":
        if "pg_dumpall" in args:
            print("-- a fake dump\n--\n-- PostgreSQL database cluster dump complete\n--\n")
        return 0
    if args[0] == "run":
        joined = " ".join(args)
        if "stat -c %u:%g" in joined:
            vol = (mount(args, "/v") or "").split("_", 1)[-1]
            print(os.environ.get(f"FAKE_OWNER_{vol}", OWNERS.get(vol, "0:0")))
            return 0
        if "du -sk" in joined:
            print("1024\t/v")
            return 0
        if "ls -A /v" in joined:
            return 0
        if "python -c" in joined:
            out = mount(args, "/out")
            name = args[-1].split("/out/")[-1].split()[0]
            with gzip.open(os.path.join(out, name), "wb") as f:
                f.write(b"a volume\n")
            print(0)
            return 0
        return 0
    if args[0] == "compose":
        return compose(st, args, project)
    return None


def profiles_of(text: str) -> list[str]:
    return [x.strip() for x in text.split("profiles:", 1)[1].split("[", 1)[1].split("]", 1)[0].split(",") if x.strip()]


def merge_profiles(have: list[str], text: str) -> list[str]:
    """As compose merges a later file's profiles: added to the earlier ones, unless !override."""
    new = profiles_of(text)
    return new if "!override" in text.split("profiles:", 1)[1] else have + [p for p in new if p not in have]


def compose_model(args: list[str], project: str, everything: bool = False) -> dict[str, dict]:
    """service -> {image, build, profiles} of the active services in the compose files compose would read."""
    files = [args[i + 1] for i, a in enumerate(args) if a == "-f"]
    if not files and os.environ.get("COMPOSE_FILE"):
        files = os.environ["COMPOSE_FILE"].split(":")
    if not files:
        files = ["docker-compose.yml"] + (["docker-compose.override.yml"] if os.path.isfile("docker-compose.override.yml") else [])
    model: dict[str, dict] = {}
    for path in files:
        try:
            text = open(path).read()
        except OSError:
            continue
        inside, svc = False, None
        for line in text.splitlines():
            if line.startswith("services:"):
                inside = True
                continue
            if inside and line and not line.startswith(" "):
                inside = False
            if not inside or line.strip().startswith("#") or not line.strip():
                continue
            if line.startswith("  ") and not line.startswith("   "):
                svc = line.strip().split(":", 1)[0]
                entry = model.setdefault(svc, {"image": None, "build": False, "profiles": []})
                rest = line.split(":", 1)[1]
                if "image:" in rest:
                    entry["image"] = rest.split("image:", 1)[1].split("}")[0].strip().strip('"')
                if "profiles:" in rest:
                    entry["profiles"] = merge_profiles(entry["profiles"], rest)
            elif svc and line.startswith("    ") and not line.startswith("     "):
                key, _, value = line.strip().partition(":")
                if key == "image":
                    model[svc]["image"] = value.strip().strip('"')
                elif key == "build":
                    model[svc]["build"] = True
                elif key == "profiles":
                    model[svc]["profiles"] = merge_profiles(model[svc]["profiles"], line)
    if everything:
        return model
    on = set(x for x in os.environ.get("COMPOSE_PROFILES", "").split(",") if x)
    # A service runs when it has no profile, or one of its profiles is asked for.
    return {k: v for k, v in model.items() if not v["profiles"] or set(v["profiles"]) & on}


def service_image(model: dict[str, dict], svc: str, project: str) -> str:
    entry = model.get(svc) or {"image": None}
    return entry["image"] or f"{project}-{svc}"


def compose(st: Store, args: list[str], project: str) -> int:
    s = st.s
    model = compose_model(args, project)
    if "config" in args:
        if "--images" in args:
            if "FAKE_IMAGES" in os.environ:
                print("\n".join(lines("FAKE_IMAGES")))
            else:
                print("\n".join(sorted({service_image(model, x, project) for x in model if model[x]["image"] or model[x]["build"]})))
        elif "--services" in args:
            print("\n".join(x for x in lines("FAKE_SERVICES") if x in model or not model))
        elif "--volumes" in args:
            print("\n".join(lines("FAKE_COMPOSE_VOLUMES")))
        elif "--profiles" in args:
            print("\n".join(sorted({p for v in compose_model(args, project, True).values() for p in v["profiles"]})))
        else:
            # As compose prints it: each active service, what it builds or runs.
            print("services:")
            for name in sorted(model):
                print(f"  {name}:")
                if model[name]["build"]:
                    print("    build:\n      context: .")
                if model[name]["image"]:
                    print(f"    image: {model[name]['image']}")
        return 0
    if "version" in args:
        print("Docker Compose version v9")
        return 0
    if "down" in args:
        s["containers"] = [c for c in s["containers"] if c["project"] != project]
        st.save()
        return 0
    if "up" in args:
        if os.environ.get("FAKE_UP_FAIL"):
            print("Error: fake up fails", file=sys.stderr)
            return 1
        services = [x for x in lines("FAKE_SERVICES") if x in model or not model]
        named = [a for a in args[args.index("up") + 1:] if not a.startswith("-") and a not in ("never",)]
        if named:
            services = [x for x in services if x in named]
        # The project's volumes, as compose makes them when they are not there.
        for vol in lines("FAKE_COMPOSE_VOLUMES"):
            s["volumes"].setdefault(f"{project}_{vol}", {"labels": [f"com.docker.compose.project={project}"]})
        unhealthy = set(norm(x) for x in lines("FAKE_UNHEALTHY"))
        down = set(lines("FAKE_DOWN"))
        for svc in services:
            ref = service_image(model, svc, project)
            image_id = st.image(ref)
            if not image_id:
                print(f"Error: no image {ref} for {svc} (pull never)", file=sys.stderr)
                return 1
            s["containers"] = [c for c in s["containers"] if not (c["project"] == project and c["service"] == svc)]
            n = s["next"]
            s["next"] = n + 1
            s["containers"].append({
                "id": f"c{n:04d}", "name": svc, "service": svc, "project": project, "workdir": os.getcwd(),
                "image_ref": ref, "image_id": image_id,
                "state": "created" if "--no-start" in args else "restarting" if svc in down else "running",
                "health": "unhealthy" if st.origin(image_id) in unhealthy else "healthy"})
        st.save()
        return 0
    return 0


def main() -> int:
    args = sys.argv[1:]
    with open(os.environ["FAKE_LOG"], "a") as log:
        log.write(json.dumps([os.path.basename(sys.argv[0])] + args) + "\n")
    if not args:
        return 0
    # The store is one engine's (FAKE_STATE_ENGINE, docker by default); the other has nothing.
    if os.environ.get("FAKE_STATE") and os.path.basename(sys.argv[0]) == os.environ.get("FAKE_STATE_ENGINE", "docker"):
        rc = stateful(args)
        if rc is not None:
            return rc
    if args[0] == "compose":
        if "config" in args:
            if "--images" in args:
                print("\n".join(lines("FAKE_IMAGES")))
                return 0
            if "--services" in args:
                if "FAKE_SERVICES" not in os.environ:
                    return 1
                print("\n".join(lines("FAKE_SERVICES")))
                return 0
            if "--profiles" in args:
                print("\n".join(lines("FAKE_PROFILES")))
                return 0
            print(os.environ.get("FAKE_CONFIG", ""))
            return 0
        return 0
    if args[:2] == ["image", "inspect"]:
        ref = args[-1]
        if ref in lines("FAKE_MISSING"):
            print(f"Error: No such image: {ref}", file=sys.stderr)
            return 1
        print("sha256:" + hashlib.sha256(ref.encode()).hexdigest())
        return 0
    if args[0] == "tag":
        return 0
    if args[0] == "save":
        out = args[args.index("-o") + 1]
        with open(out, "w") as f:
            f.write(f"fake image {args[-1]}\n")
        return 0
    if args[0] == "load":
        print(f"Loaded image from {args[args.index('-i') + 1]}")
        return 0
    if args[:2] == ["volume", "inspect"]:
        return 0 if args[-1] in lines("FAKE_VOLUMES") else 1
    if args[:2] == ["volume", "create"]:
        print(args[-1])
        return 0
    if args[:2] == ["volume", "ls"]:
        if not any(a.startswith("label=") for a in args):
            print("\n".join(lines("FAKE_VOLUME_LIST")))
        return 0
    if args[0] == "run":
        if "cat" in args:
            name = args[args.index("cat") + 1]
            path = os.path.join(os.environ.get("FAKE_ENGINE", "/nonexistent"), os.path.basename(name))
            if not os.path.isfile(path):
                return 1
            sys.stdout.write(open(path).read())
            return 0
        joined = " ".join(args)
        if "tar -czf /out/audio.tar.gz" in joined:
            out = mount(args, "/out")
            with tarfile.open(os.path.join(out, "audio.tar.gz"), "w:gz") as tar:
                data = b"a speech model\n"
                info = tarfile.TarInfo("models--speaches-ai--Kokoro/blob")
                info.size = len(data)
                tar.addfile(info, io.BytesIO(data))
            return 0
        if "/bundle/audio.tar.gz" in joined:
            return 0 if os.path.isfile(os.path.join(mount(args, "/bundle") or "", "audio.tar.gz")) else 1
        return 0
    return 0


if __name__ == "__main__":
    sys.exit(main())
