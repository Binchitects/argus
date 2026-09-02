"""Run Argus's smoke_test.py against a newer `mcp` package.

The test imports `streamablehttp_client`; recent mcp releases renamed it to
`streamable_http_client`. Aliasing is safer than pinning an old mcp, which
would test a client nobody actually runs.
"""
import runpy
import sys

import mcp.client.streamable_http as sh

if not hasattr(sh, "streamablehttp_client") and hasattr(sh, "streamable_http_client"):
    sh.streamablehttp_client = sh.streamable_http_client
    print("  [shim] aliased streamablehttp_client -> streamable_http_client")

sys.argv = ["smoke_test.py"] + sys.argv[1:]
runpy.run_path("/deploy/smoke_test.py", run_name="__main__")
