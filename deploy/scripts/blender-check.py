#!/usr/bin/env python3
"""Checks of the 3D sandbox (profile 3d), against the running container: Blender scripts go through the blender-jobs
volume exactly as the app sends them, as the app's user (sandbox-check.py's client).
    python3 scripts/blender-check.py
What must hold: a script makes a model and it comes back; the 3D tool's own script (its builder, the parts, its
epilogue: a model and two pictures, rendered with Cycles) is done well within the tool's time; a run has no network;
a run past its time is stopped. Exit status: failures.
"""
import importlib.util
import json
import sys
import time
from pathlib import Path

spec = importlib.util.spec_from_file_location("sandbox_check", Path(__file__).with_name("sandbox-check.py"))
check = importlib.util.module_from_spec(spec)
spec.loader.exec_module(check)
check.VOLUME = f"{check.PROJECT}_blender"
# The client plays the app with the 3D sandbox's own image (its Python), so it needs nothing else.
check.CLIENT_IMAGE = "arena-blender:latest"

# The tool's own pieces, as the app adds them around what the model asks for (BlenderTool.cs).
TOOL = Path(__file__).resolve().parents[1] / "services" / "blender" / "tool"
# The tool's time for a run (Blender:TimeoutSeconds).
TOOL_SECONDS = 240


def tool_script(parts):
    """What make_3d_parts runs: the builder, the parts as JSON, the epilogue (a model and two pictures)."""
    body = (f"LOOK = 'realistic'\nLIGHTING = 'day'\nPRINT = False\nPRINT_SIZE = None\nTURNTABLE = False\n"
            f"import json as _json\nfor _p in _json.loads({json.dumps(json.dumps(parts))}):\n    part(_p)\n")
    return ("import bpy\nbpy.ops.wm.read_factory_settings(use_empty=True)\n" + (TOOL / "builder.py").read_text()
            + body + (TOOL / "epilogue.py").read_text())


def main():
    if check.subprocess.run(["docker", "inspect", "blender"], capture_output=True, check=False).returncode != 0:
        print("blender-check: the 3D sandbox is not running (profile 3d)")
        return 1
    print("3D sandbox checks")

    made = check.run("""
        import bpy
        bpy.ops.wm.read_factory_settings(use_empty=True)
        bpy.ops.mesh.primitive_cube_add(size=1)
        bpy.ops.export_scene.gltf(filepath="model.glb")
        print("objects", len(bpy.data.objects))
        """, timeout=120)
    check.rec("a script makes a model and it comes back", made.get("exit_code") == 0 and "model.glb" in made.get("made", {}),
              (made.get("stderr") or made.get("error") or "")[-300:])

    if TOOL.is_dir():
        started = time.monotonic()
        full = check.run(tool_script([
            {"shape": "box", "size": [4, 4, 0.5], "at": [0, 0, 0], "color": "grey", "pattern": "brick"},
            {"shape": "sphere", "size": [1, 1, 1], "at": [0, 0, 1.5], "color": "red", "shader": "glass"},
        ]), timeout=TOOL_SECONDS)
        took = time.monotonic() - started
        got = set(full.get("made", {}))
        check.rec(f"the 3D tool's own script makes the model and its two pictures within its {TOOL_SECONDS} s",
                  full.get("exit_code") == 0 and {"model.glb", "view-front.png", "view-three-quarter.png"} <= got,
                  f"{took:.0f} s; {sorted(got)}; {(full.get('stderr') or full.get('error') or '')[-200:]}")
        check.rec("... with room to spare (under half its time)", took < TOOL_SECONDS / 2, f"{took:.0f} s")

    offline = check.run("""
        import socket
        try:
            socket.create_connection(("1.1.1.1", 80), timeout=3)
            print("reached")
        except OSError as e:
            print("no network:", e)
        """, timeout=60)
    check.rec("a run has no network", "no network" in offline.get("stdout", "") and "reached" not in offline.get("stdout", ""),
              offline.get("stdout", "")[-200:])

    slow = check.run("""
        import time
        while True:
            time.sleep(1)
        """, timeout=5)
    check.rec("a run past its time is stopped", slow.get("timed_out") is True, json.dumps({k: slow.get(k) for k in ("timed_out", "exit_code", "error")}))

    failed = sum(1 for _, ok in check.RESULTS if not ok)
    print(f"\n{len(check.RESULTS) - failed}/{len(check.RESULTS)} checks passed")
    return failed


if __name__ == "__main__":
    sys.exit(main())
