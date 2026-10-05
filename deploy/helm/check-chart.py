#!/usr/bin/env python3
"""Checks the Helm chart without helm or a cluster (standard library only).

    python3 deploy/helm/check-chart.py

- the configuration files the chart carries (files/) are the same as deploy/config and deploy/services;
- every template's actions are balanced (if, range, with, define each have their end);
- every .Values path a template reads is in values.yaml, and every include names a defined template;
- values.yaml holds no secret: each is empty, made at install, or given by the person installing;
- the app is told outright whether each module that can be off runs (Modules__<name>).

It does not render the templates: `helm lint` and `helm template` do that where helm is installed.
"""
import re
import sys
from pathlib import Path

HERE = Path(__file__).resolve().parent
CHART = HERE / "argus-arena"
DEPLOY = HERE.parent

COPIES = {
    "files/litellm.yaml": "config/litellm.yaml",
    "files/argus.yaml": "config/argus.yaml",
    "files/searxng.yml": "config/searxng.yml",
    "files/alertmanager.yml": "config/alertmanager.yml",
    "files/loki.yml": "config/loki.yml",
    "files/prometheus/prometheus.yml": "config/prometheus/prometheus.yml",
    "files/router.sh": "services/llamacpp/router.sh",
    "files/sd-serve.sh": "services/sd-serve.sh",
}
for rule in sorted((DEPLOY / "config/prometheus/rules").glob("*.yml")):
    COPIES[f"files/prometheus/rules/{rule.name}"] = f"config/prometheus/rules/{rule.name}"

# The modules that can be off, which the app would otherwise look up by name (src/Llm.Api/Operations/Modules.cs):
# the chart tells it outright, since a cluster's search domains can answer a short name.
MODULES = ("imagegen", "videogen", "audio", "laya")


def value_paths(text):
    """The key paths of values.yaml: block mappings by indentation, and one-line flow mappings."""
    paths, stack = set(), []
    for raw in text.splitlines():
        line = raw.split(" #")[0].rstrip() if not raw.lstrip().startswith("#") else ""
        if not line.strip() or line.lstrip().startswith("- "):
            continue
        m = re.match(r"^(\s*)([A-Za-z_][\w-]*):\s*(.*)$", line)
        if not m:
            continue
        indent, key, rest = len(m.group(1)), m.group(2), m.group(3)
        while stack and stack[-1][0] >= indent:
            stack.pop()
        path = ".".join([k for _, k in stack] + [key])
        paths.add(path)
        if rest.startswith("{"):
            flow(rest, path, paths)
        elif not rest:
            stack.append((indent, key))
    return paths


def flow(text, prefix, paths):
    """Keys of a flow mapping such as { requests: { cpu: 1 }, limits: { memory: 2Gi } }."""
    depth, key, stack, i = 0, "", [prefix], 0
    while i < len(text):
        c = text[i]
        if c == "{":
            depth += 1
            if key:
                stack.append(f"{stack[-1]}.{key}")
                key = ""
        elif c == "}":
            depth -= 1
            if len(stack) > depth + 1:
                stack.pop()
        elif c in "[":
            close = text.index("]", i)
            i = close
        else:
            m = re.match(r"\s*([A-Za-z_][\w-]*)\s*:", text[i:])
            if m and depth > 0:
                key = m.group(1)
                paths.add(f"{stack[-1]}.{key}")
                i += m.end()
                nxt = text[i:].lstrip()
                if not nxt.startswith("{"):
                    key = ""
                continue
        i += 1


def main():
    problems = []
    for copy, source in COPIES.items():
        a, b = CHART / copy, DEPLOY / source
        if not a.exists():
            problems.append(f"{copy}: missing (copy {source} into the chart)")
        elif a.read_bytes() != b.read_bytes():
            problems.append(f"{copy}: differs from deploy/{source} (copy it again)")

    values_text = (CHART / "values.yaml").read_text()
    values = value_paths(values_text)
    for key in re.findall(r"^\s{2}(\w+): \"(.+)\"", values_text.split("secrets:", 1)[1].split("\n\n", 1)[0], re.M):
        if key[0] != "existingSecret":
            problems.append(f"values.yaml: secrets.{key[0]} has a value; secrets are made at install or given then")

    app = (CHART / "templates/app.yaml").read_text()
    for name in MODULES:
        if f"- {{ name: Modules__{name}, value: {{{{ .Values.{name}.enabled | quote }}}} }}" not in app:
            problems.append(f"app.yaml: the app is not told whether {name} runs (Modules__{name} from {name}.enabled)")

    templates = sorted((CHART / "templates").glob("*"))
    defined = set()
    for t in templates:
        defined.update(re.findall(r'\{\{-?\s*define\s+"([^"]+)"', t.read_text()))
    for t in templates:
        text = t.read_text()
        actions = re.findall(r"\{\{-?\s*(/\*.*?\*/|[^}]*?)\s*-?\}\}", text, re.S)
        opened = 0
        for a in actions:
            word = a.split()[0] if a.split() else ""
            if word in ("if", "range", "with", "define"):
                opened += 1
            elif word == "end":
                opened -= 1
                if opened < 0:
                    problems.append(f"{t.name}: an end with nothing open")
                    opened = 0
        if opened:
            problems.append(f"{t.name}: {opened} if/range/with/define left without an end")
        if text.count("{{") != text.count("}}"):
            problems.append(f"{t.name}: unbalanced braces")
        for path in set(re.findall(r"\.Values((?:\.[A-Za-z_]\w*)+)", text)):
            if path.lstrip(".") not in values:
                problems.append(f"{t.name}: .Values{path} is not in values.yaml")
        for name in set(re.findall(r'include\s+"([^"]+)"', text)):
            if name not in defined:
                problems.append(f"{t.name}: include \"{name}\" names no template")
        for name in set(re.findall(r'\.Files\.Get\s+"([^"]+)"', text)):
            if not (CHART / name).exists():
                problems.append(f"{t.name}: {name} is not in the chart")

    for p in problems:
        print("x", p)
    print(f"{'Problems: ' + str(len(problems)) if problems else 'The chart checks out'} ({len(templates)} templates, {len(COPIES)} copied files).")
    return 1 if problems else 0


if __name__ == "__main__":
    sys.exit(main())
