#!/usr/bin/env python3
"""Checks of the science lab's proof checker (profile lean), against the running container: Lean files go through
the lean-jobs volume exactly as the app sends them, as the app's user (sandbox-check.py's client).
    python3 scripts/lean-check.py
What must hold: Mathlib loads and a right proof is accepted; a wrong one is rejected with Lean's message by line and
column; sorry is said; the run cannot read the jobs or write the image; a run past its time is stopped. Exit
status: failures.
"""
import importlib.util
import re
import sys
from pathlib import Path

spec = importlib.util.spec_from_file_location("sandbox_check", Path(__file__).with_name("sandbox-check.py"))
check = importlib.util.module_from_spec(spec)
spec.loader.exec_module(check)
check.VOLUME = f"{check.PROJECT}_lean"


def main():
    right = check.run("""
        import Mathlib

        theorem lab_square (a b : ℝ) : (a + b) ^ 2 = a ^ 2 + 2 * a * b + b ^ 2 := by ring

        theorem lab_primes : ∀ n : ℕ, ∃ p, n ≤ p ∧ Nat.Prime p := Nat.exists_infinite_primes
        """, timeout=180)
    check.rec("Mathlib loads and right proofs are accepted", right.get("exit_code") == 0 and "error" not in right.get("stdout", ""),
              (right.get("stdout", "") + right.get("stderr", ""))[-300:].strip())

    wrong = check.run("""
        import Mathlib

        theorem lab_wrong (a b : ℝ) : (a + b) ^ 2 = a ^ 2 + b ^ 2 := by ring
        """, timeout=180)
    check.rec("a wrong proof is rejected with Lean's message by line and column",
              wrong.get("exit_code") not in (0, None) and re.search(r"^\.Proof\.lean:4:\d+: error", wrong.get("stdout", ""), re.M),
              wrong.get("stdout", "")[-300:].strip())

    sorry = check.run("""
        import Mathlib

        theorem lab_later (n : ℕ) : n + 0 = n := by sorry
        """, timeout=180)
    check.rec("sorry is said", re.search(r"declaration uses .sorry.", sorry.get("stdout", "")), sorry.get("stdout", "")[-200:].strip())

    reach = check.run("""
        import Mathlib

        #eval show IO Unit from do
          try
            let _ ← IO.FS.readFile "/jobs/heartbeat"
            IO.println "jobs: read"
          catch _ => IO.println "jobs: refused"
          try
            IO.FS.writeFile "/opt/lab/written" "x"
            IO.println "image: written"
          catch _ => IO.println "image: refused"
        """, timeout=180)
    out = reach.get("stdout", "")
    check.rec("a run cannot read the jobs or write the image", "jobs: refused" in out and "image: refused" in out, out[-200:].strip())

    slow = check.run("""
        import Mathlib

        partial def forever (n : Nat) : Nat := forever (n + 1)
        #eval forever 0
        """, timeout=20)
    check.rec("a run past its time is stopped", slow.get("timed_out") is True or slow.get("killed"), str({k: slow.get(k) for k in ("timed_out", "killed", "exit_code")}))

    failed = [n for n, ok in check.RESULTS if not ok]
    print(f"\n{len(check.RESULTS) - len(failed)}/{len(check.RESULTS)} checks passed")
    return len(failed)


if __name__ == "__main__":
    sys.exit(main())
