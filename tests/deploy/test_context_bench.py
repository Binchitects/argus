"""
evals/run_context_bench.py, Code Arena's long-session measurement: the engine and the benchmark started as the host runs
them (the model's folder read-only, the arms and the model's address given to the test), and the runs summed per arm into
tokens per task, tasks passed and the tool calls' fault rates, as docs/measurements/context-condensing.md shows them.
"""
from __future__ import annotations

import importlib.util
import pathlib
import unittest

from support import REPO


def bench():
    spec = importlib.util.spec_from_file_location("run_context_bench", REPO / "evals" / "run_context_bench.py")
    module = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(module)
    return module


def run(arm: str, tasks: list[tuple[str, bool, int, int]], calls: dict) -> dict:
    return {
        "arm": arm, "run": 1,
        "tasks": [{"task": t, "passed": ok, "prompt": prompt, "cached": cached, "completion": 100, "requests": 4, "seconds": 10.0} for t, ok, prompt, cached in tasks],
        "calls": calls,
    }


REPORT = {
    "model": "m",
    "arms": [
        run("before", [("subtotal", True, 40_000, 30_000), ("tax", False, 60_000, 40_000)],
            {"calls": 20, "unknownTool": 2, "badJson": 1, "badArguments": 1, "editMisses": 2, "otherErrors": 0, "mended": 0, "retries": 3}),
        run("after", [("subtotal", True, 30_000, 25_000), ("tax", True, 30_000, 26_000)],
            {"calls": 20, "unknownTool": 0, "badJson": 0, "badArguments": 1, "editMisses": 1, "otherErrors": 0, "mended": 3, "retries": 1,
             "condensed": {"summarised": 4, "outlined": 2, "dropped": 1, "freedChars": 50_000}}),
    ],
}


class ContextBenchTests(unittest.TestCase):
    def test_the_engine_reads_the_model_read_only_and_answers_on_the_host_only(self):
        b = bench()
        command = b.llama_command(pathlib.Path("/models/x/Qwen3-4B.gguf"), "Qwen3-4B", 32768, 18080, "bench")
        self.assertEqual(command[:4], ["docker", "run", "-d", "--rm"])
        self.assertIn("/models/x:/models:ro", command)
        self.assertIn("127.0.0.1:18080:8080", command)
        self.assertEqual(command[command.index("-m") + 1], "/models/Qwen3-4B.gguf")
        self.assertEqual(command[command.index("-c") + 1], "32768")
        self.assertIn("--jinja", command)

    def test_the_benchmark_is_told_the_model_and_the_arms_and_runs_only_its_own_test(self):
        b = bench()
        command = b.bench_command("http://127.0.0.1:18080", "Qwen3-4B", 32768, ["before", "after"], 2, ["tax"], "/repo/evals/out.json", 1000, 1000, pathlib.Path("/c"))
        env = {command[i + 1].split("=", 1)[0]: command[i + 1].split("=", 1)[1] for i, a in enumerate(command) if a == "-e"}
        self.assertEqual(env["CODE_ARENA_BENCH_URL"], "http://127.0.0.1:18080")
        self.assertEqual(env["CODE_ARENA_BENCH_ARMS"], "before,after")
        self.assertEqual(env["CODE_ARENA_BENCH_RUNS"], "2")
        self.assertEqual(env["CODE_ARENA_BENCH_TASKS"], "tax")
        self.assertEqual(env["CODE_ARENA_BENCH_OUT"], "/repo/evals/out.json")
        self.assertEqual(command[command.index("--filter") + 1], b.TEST)
        self.assertNotIn("/var/run/docker.sock:/var/run/docker.sock", command)
        # The test it runs is there, under that name.
        source = (REPO / "tests" / "CodeArena.Tests" / "ContextBenchmarkTests.cs").read_text()
        self.assertIn("A_long_session_of_coding_tasks_before_and_after_condensing_and_mending", source)
        for name in ("CODE_ARENA_BENCH_URL", "CODE_ARENA_BENCH_ARMS", "CODE_ARENA_BENCH_RUNS", "CODE_ARENA_BENCH_TASKS", "CODE_ARENA_BENCH_OUT", "CODE_ARENA_BENCH_CONTEXT", "CODE_ARENA_BENCH_MODEL"):
            self.assertIn(name, source)

    def test_runs_are_summed_per_arm_into_tokens_per_task_passes_and_fault_rates(self):
        summary = bench().summarize(REPORT)
        before, after = summary["before"], summary["after"]
        self.assertEqual((before["passed"], before["tasks"]), (1, 2))
        self.assertEqual(before["perTask"], {"prompt": 50_000, "uncached": 15_000, "completion": 100, "requests": 4.0, "seconds": 10.0})
        self.assertEqual(after["perTask"]["prompt"], 30_000)
        self.assertEqual(before["errorRate"], 0.3)
        self.assertEqual(before["callFaultRate"], 0.2)
        self.assertEqual(after["errorRate"], 0.1)
        self.assertEqual(after["condensed"]["outlined"], 2)
        self.assertEqual(before["byTask"]["tax"], {"passed": 0, "runs": 1, "prompt": 60_000})

    def test_the_table_says_each_arm_and_after_against_before(self):
        b = bench()
        text = b.table(b.summarize(REPORT), "Qwen3-4B")
        self.assertIn("| before | 1 / 2 | 50,000 (15,000) | 100 | 4.0 | 10.0 | 30.0% | 20.0% | 0 | 3 |", text)
        self.assertIn("| after | 2 / 2 | 30,000 (4,500) |", text)
        self.assertIn("After against before: prompt tokens per task −40%, uncached −70%, seconds +0%; passed 1 → 2 of 2; tool-call error rate 30.0% → 10.0%.", text)
        self.assertIn("| tax | 0 / 1, 60,000 | 1 / 1, 30,000 |", text)
        self.assertIn("| after | 20 | 0 | 0 | 1 | 1 | 0 | 4 | 2 | 1 |", text)
        self.assertEqual(b.change(0, 5), "—")


if __name__ == "__main__":
    unittest.main()
