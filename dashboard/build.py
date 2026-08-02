#!/usr/bin/env python3
"""Collect review results and index status into dashboard/data.json.

The dashboard is a single static HTML file with no network access of its own,
so everything it renders has to be baked into one JSON file first. This script
does that:

  * scans docs/review/results/*.md for the machine-readable ```json block that
    CODE_REVIEW_TEMPLATE.md requires
  * optionally runs `argus status --config <cfg>` and captures per-repo freshness

Usage:
    python dashboard/build.py
    python dashboard/build.py --config config.yaml     # also collect index status

Then open dashboard/index.html directly in a browser. No server needed.
"""
from __future__ import annotations

import argparse
import datetime as dt
import json
import pathlib
import re
import subprocess
import sys

ROOT = pathlib.Path(__file__).resolve().parent.parent
RESULTS_DIR = ROOT / "docs" / "review" / "results"
OUT = ROOT / "dashboard" / "data.json"

# The template mandates a fenced ```json block. Take the LAST one in the file:
# earlier fences are usually illustrative snippets inside the prose.
JSON_BLOCK = re.compile(r"```json\s*\n(.*?)\n```", re.DOTALL)

REQUIRED = ("task", "reviewer_model", "verdict")
SCORE_AXES = (
    "verification_depth", "seam_awareness", "test_scepticism",
    "severity_calibration", "signal_to_noise", "prior_art_respect",
)


def load_reviews() -> tuple[list[dict], list[str]]:
    reviews: list[dict] = []
    problems: list[str] = []
    if not RESULTS_DIR.is_dir():
        return reviews, [f"no results directory at {RESULTS_DIR.relative_to(ROOT)}"]

    for path in sorted(RESULTS_DIR.glob("*.md")):
        blocks = JSON_BLOCK.findall(path.read_text(encoding="utf-8"))
        if not blocks:
            problems.append(f"{path.name}: no ```json block")
            continue
        try:
            data = json.loads(blocks[-1])
        except json.JSONDecodeError as exc:
            problems.append(f"{path.name}: malformed JSON ({exc})")
            continue

        missing = [k for k in REQUIRED if not data.get(k)]
        if missing:
            problems.append(f"{path.name}: missing {', '.join(missing)}")

        data["_source"] = path.name
        scores = data.get("scores") or {}
        # Only total when every axis is present; a partial total silently
        # flatters an incomplete review against a complete one.
        if all(a in scores for a in SCORE_AXES):
            data["_score_total"] = sum(int(scores[a]) for a in SCORE_AXES)
        else:
            data["_score_total"] = None
            problems.append(f"{path.name}: incomplete scorecard, excluded from totals")
        reviews.append(data)
    return reviews, problems


def load_index_status(config: str | None) -> dict:
    if not config:
        return {"collected": False, "reason": "not requested (pass --config)"}
    try:
        proc = subprocess.run(
            [sys.executable, "-m", "argus.cli", "status", "--config", config],
            capture_output=True, text=True, timeout=120, cwd=ROOT,
        )
    except Exception as exc:
        return {"collected": False, "reason": f"argus status failed: {exc}"}
    if proc.returncode != 0:
        return {"collected": False,
                "reason": f"argus status exited {proc.returncode}: {proc.stderr.strip()[:300]}"}
    return {"collected": True, "raw": proc.stdout.strip()}


def main() -> int:
    ap = argparse.ArgumentParser(description=__doc__)
    ap.add_argument("--config", help="argus config, to also collect index status")
    args = ap.parse_args()

    reviews, problems = load_reviews()
    payload = {
        "generated_at": dt.datetime.now(dt.timezone.utc).isoformat(timespec="seconds"),
        "reviews": reviews,
        "index_status": load_index_status(args.config),
        # Surfaced in the UI rather than swallowed: a dashboard that silently
        # drops malformed input is worse than one that shows the gap.
        "problems": problems,
        "score_axes": list(SCORE_AXES),
    }
    OUT.parent.mkdir(parents=True, exist_ok=True)
    blob = json.dumps(payload, indent=2)
    OUT.write_text(blob, encoding="utf-8")

    # data.js for local iteration: index.html loads it via <script>, not fetch(),
    # because browsers block fetch() over file:// and requiring a web server to
    # read a static report is friction for nothing.
    (OUT.parent / "data.js").write_text(
        "window.ARGUS_DATA = " + blob + ";\n", encoding="utf-8")

    # report.html: ONE self-contained file with the data inlined. This is the
    # artifact to publish or send to someone -- no sibling files, no server, and
    # it survives being copied somewhere on its own.
    template = (OUT.parent / "index.html").read_text(encoding="utf-8")
    inlined = template.replace(
        '<script src="data.js"></script>',
        "<script>window.ARGUS_DATA = " + blob + ";</script>",
    )
    if inlined == template:
        problems.append("index.html: could not find the data.js script tag to inline")
    (OUT.parent / "report.html").write_text(inlined, encoding="utf-8")

    print(f"wrote {OUT.relative_to(ROOT)}, data.js and report.html: "
          f"{len(reviews)} review(s)")
    for p in problems:
        print(f"  ! {p}")
    return 0


if __name__ == "__main__":
    sys.exit(main())
