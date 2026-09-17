"""Do the Windows packs answer version questions, and what did that cost?

Ground truth is the sdk-api front matter, never a person's memory: every
question is generated from the page it is about, so the expected answer is
whatever Microsoft publishes. See evals/README.md for why that rule exists.

    python evals/run_windows_versions.py <packs_dir>          # grade
    python evals/run_windows_versions.py --ablate <work_dir>  # before/after

`--ablate` builds two packs over exactly the pages the questions name, one
with the current adapter and one with `_OS_FIELDS` emptied -- the pre-change
behaviour. One variable moves, so the delta is the metadata's contribution
rather than a difference between two corpora.

The `control-header` questions are the regression arm. They were answerable
before this work and must stay answerable: adding a line to every contract
changes what every embedding covers, and a gain on version questions that
cost header questions would not be a gain.
"""
from __future__ import annotations

import argparse, asyncio, collections, json, shutil, sys
from pathlib import Path

QUESTIONS = Path(__file__).with_name("questions-windows-versions.json")
norm = lambda s: s.replace("\xa0", " ")          # Microsoft's OS names use NBSP


async def _grade(packs_dir: str, label: str) -> tuple[int, int]:
    from argus.mcpsrv.tools import docs_lookup_impl, docs_search_impl

    per, tot = collections.Counter(), collections.Counter()
    grp, gt = collections.Counter(), collections.Counter()
    misses = []
    for x in json.loads(QUESTIONS.read_text())["questions"]:
        if x.get("mode") == "search":
            hits = await docs_search_impl(packs_dir, x["prompt"], limit=5)
            text = norm(" ".join((h.get("text") or "") for h in hits))
        else:
            hits = await docs_lookup_impl(packs_dir, x["api"])
            text = norm(" ".join((h.get("signature") or "") for h in hits))
        ok = bool(hits) and norm(x["expect"]) in text
        shape = "com-method" if "::" in x["api"] else "top-level"
        tot[x["kind"]] += 1; per[x["kind"]] += ok
        gt[shape] += 1; grp[shape] += ok
        if not ok:
            first = hits[0] if hits else {}
            misses.append((x["kind"], x["api"],
                           norm(first.get("signature") or first.get("text") or "")[:64]
                           or "(not found)"))

    print(f"=== {label} ===")
    for k in sorted(tot):
        print(f"  {k:16} {per[k]:>2}/{tot[k]:<2} {100 * per[k] / tot[k]:4.0f}%")
    print(f"  {'TOTAL':16} {sum(per.values()):>2}/{sum(tot.values()):<2} "
          f"{100 * sum(per.values()) / sum(tot.values()):4.0f}%")
    for shape in ("top-level", "com-method"):
        if gt[shape]:
            print(f"     {shape:12} {grp[shape]:>2}/{gt[shape]:<2} "
                  f"{100 * grp[shape] / gt[shape]:4.0f}%")
    for kind, api, got in misses[:8]:
        print(f"    miss [{kind}] {api}: {got!r}")
    return sum(per.values()), sum(tot.values())


def _ablate(work_dir: Path) -> None:
    """Build the same pages twice and grade both."""
    sys.path.insert(0, str(Path(__file__).resolve().parents[1] / "src"))
    import argus.packs.sources.microsoft_docs as md
    from argus.packs.build import build_pack
    from argus.packs.sources import SOURCES

    src = work_dir / "sdk-api-src" / "content"
    qs = json.loads(QUESTIONS.read_text())["questions"]
    stage = work_dir / "stage"
    for x in qs:
        dst = stage / "sdk-api-src/content" / x["path"]
        dst.parent.mkdir(parents=True, exist_ok=True)
        shutil.copy(src / x["path"], dst)

    real = md._OS_FIELDS                      # captured, not re-declared
    for label, fields in (("after", real), ("before", ())):
        md._OS_FIELDS = fields
        arm = work_dir / label
        arm.mkdir(parents=True, exist_ok=True)
        build_pack(SOURCES["win32-docs"](), work_dir=stage,
                   out_path=arm / "win32.arguspack", version="1.1",
                   source_commit="ablation", cache_path=work_dir / ".c.db")
        asyncio.run(_grade(str(arm), f"{label} (OS fields={len(fields)})"))
    md._OS_FIELDS = real


def main() -> int:
    ap = argparse.ArgumentParser()
    ap.add_argument("packs_dir", nargs="?", help="directory of installed packs")
    ap.add_argument("--ablate", metavar="WORK_DIR",
                    help="checkout of sdk-api to build both arms from")
    a = ap.parse_args()
    if a.ablate:
        _ablate(Path(a.ablate)); return 0
    if not a.packs_dir:
        ap.error("give a packs directory, or --ablate WORK_DIR")
    passed, total = asyncio.run(_grade(a.packs_dir, a.packs_dir))
    return 0 if passed == total else 1


if __name__ == "__main__":
    raise SystemExit(main())
