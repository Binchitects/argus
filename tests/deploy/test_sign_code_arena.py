"""
deploy/scripts/sign-code-arena.sh, against a deploy folder of its own: a CA as make-cert.sh makes it, a fake engine whose
`compose cp` gives the app image's SHA256SUMS and whose `run` stands in for the app image's sign-code-arena (a tar of the
Windows build "signed" and the list with its SHA-256). It makes a code-signing certificate from the CA, has the Windows
builds signed in a container with no network and the key on its standard input, signs the list, and its --check says
whether what the app serves is the image's builds signed.
"""
from __future__ import annotations

import os
import shutil
import subprocess
import tempfile
import unittest
from pathlib import Path

from support import REPO

SCRIPT = REPO / "deploy" / "scripts" / "sign-code-arena.sh"

FAKE_ENGINE = r"""#!/bin/sh
case "$1 $2" in
  # compose cp app:/downloads/code-arena/SHA256SUMS DEST: the image's list, as the test put it.
  "compose cp") [ -f "$IMAGE_SUMS" ] || exit 1; cp "$IMAGE_SUMS" "$4" ;;
  "compose ps") echo c0ffee ;;
  "inspect --format") [ "$4" = c0ffee ] && echo sha256:app-image ;;
  run*)
    # The app image's sign-code-arena: what it was given, and a tar of the Windows build signed and the list served.
    echo "$@" > "$RUN_ARGS"
    cat > "$RUN_STDIN"
    [ -z "${FAIL_RUN:-}" ] || { echo "signing failed" >&2; exit 1; }
    d=$(mktemp -d)
    cp "$IMAGE_SUMS" "$d/SHA256SUMS.image"
    mkdir "$d/win-x64"
    printf 'signed by %s' "$(tail -n 1 "$IMAGE_SUMS")" > "$d/win-x64/code-arena.exe"
    sum=$(openssl dgst -sha256 -r "$d/win-x64/code-arena.exe" | cut -d' ' -f1)
    sed "s|^[0-9a-f]*  win-x64/code-arena.exe\$|$sum  win-x64/code-arena.exe|" "$IMAGE_SUMS" > "$d/SHA256SUMS"
    tar -cf - -C "$d" .
    rm -rf "$d" ;;
  *) exit 2 ;;
esac
"""


@unittest.skipUnless(shutil.which("openssl"), "openssl is not installed")
class SignCodeArenaTests(unittest.TestCase):
    def setUp(self):
        self.dir = Path(tempfile.mkdtemp(prefix="sign-code-arena-"))
        self.addCleanup(shutil.rmtree, self.dir, True)
        self.deploy = self.dir / "deploy"
        (self.deploy / "scripts").mkdir(parents=True)
        shutil.copy(SCRIPT, self.deploy / "scripts" / "sign-code-arena.sh")
        self.data = self.dir / "data"
        (self.deploy / ".env").write_text(f"DOMAIN=chat.example.test\nDATA_DIR={self.data}\n")
        certs = self.deploy / "certs"
        certs.mkdir()
        subprocess.run(["openssl", "req", "-x509", "-new", "-nodes", "-newkey", "rsa:2048", "-sha256", "-days", "30",
                        "-keyout", str(certs / "ca.key"), "-out", str(certs / "ca.crt"), "-subj", "/CN=Test CA",
                        "-addext", "basicConstraints=critical,CA:TRUE,pathlen:0", "-addext", "keyUsage=critical,keyCertSign,cRLSign"],
                       check=True, capture_output=True)
        self.engine = self.dir / "engine"
        self.engine.write_text(FAKE_ENGINE)
        self.engine.chmod(0o755)
        self.sums = self.dir / "SHA256SUMS"
        self.sums.write_text("a" * 64 + "  linux-x64/code-arena\n" + "b" * 64 + "  win-x64/code-arena.exe\n")

    def run_script(self, *args: str, **extra: str) -> subprocess.CompletedProcess[str]:
        env = {**os.environ, "ENGINE": str(self.engine), "IMAGE_SUMS": str(self.sums), "RUN_ARGS": str(self.dir / "run-args"),
               "RUN_STDIN": str(self.dir / "run-stdin"), **extra}
        return subprocess.run(["sh", str(self.deploy / "scripts" / "sign-code-arena.sh"), *args], env=env, capture_output=True, text=True)

    def openssl(self, *args: str) -> subprocess.CompletedProcess[str]:
        return subprocess.run(["openssl", *args], capture_output=True, text=True)

    def test_signs_the_windows_builds_and_the_list_with_a_code_signing_certificate_of_the_ca_that_openssl_verifies(self):
        made = self.run_script()
        self.assertEqual(made.returncode, 0, made.stdout + made.stderr)
        self.assertIn("made signing/code-signing.crt from certs/ca.crt", made.stdout)
        self.assertIn("signed 2 builds (1 for Windows, with Authenticode)", made.stdout)
        out = self.data / "code-arena-signature"
        # The Windows build signed by the app's image, in a container with no network and nothing mounted, the key piped in.
        run = (self.dir / "run-args").read_text().split()
        self.assertEqual(run[0], "run")
        for flag in ["--network none", "--read-only", "--cap-drop ALL", "-i"]:
            self.assertIn(flag, " ".join(run))
        self.assertNotIn("-v", run)
        self.assertEqual(run[-2:], ["sha256:app-image", "sign-code-arena"])
        given = (self.dir / "run-stdin").read_text()
        self.assertIn("PRIVATE KEY", given)
        self.assertIn("BEGIN CERTIFICATE", given)
        exe = out / "win-x64" / "code-arena.exe"
        self.assertTrue(exe.read_text().startswith("signed by"))
        self.assertEqual((out / "SHA256SUMS.image").read_text(), self.sums.read_text())
        served = (out / "SHA256SUMS").read_text().splitlines()
        self.assertEqual(served[0], "a" * 64 + "  linux-x64/code-arena")
        self.assertEqual(served[1], self.openssl("dgst", "-sha256", "-r", str(exe)).stdout.split()[0] + "  win-x64/code-arena.exe")
        # The key stays on the host, apart: never in what the app serves.
        self.assertEqual((self.deploy / "signing" / "code-signing.key").stat().st_mode & 0o777, 0o600)
        self.assertNotIn("PRIVATE KEY", (out / "code-signing.crt").read_text())
        leaf = self.dir / "leaf.crt"
        self.openssl("x509", "-in", str(out / "code-signing.crt"), "-out", str(leaf))
        verified = self.openssl("verify", "-CAfile", str(self.deploy / "certs" / "ca.crt"), str(leaf))
        self.assertEqual(verified.returncode, 0, verified.stdout + verified.stderr)
        self.assertIn("Code Signing", self.openssl("x509", "-in", str(leaf), "-noout", "-ext", "extendedKeyUsage").stdout)
        self.assertIn("Code Arena (chat.example.test)", self.openssl("x509", "-in", str(leaf), "-noout", "-subject").stdout)
        pub = self.dir / "pub.pem"
        pub.write_text(self.openssl("x509", "-in", str(leaf), "-pubkey", "-noout").stdout)
        good = self.openssl("dgst", "-sha256", "-verify", str(pub), "-signature", str(out / "SHA256SUMS.sig"), str(out / "SHA256SUMS"))
        self.assertEqual(good.returncode, 0, good.stdout + good.stderr)

        checked = self.run_script("--check")
        self.assertEqual(checked.returncode, 0, checked.stdout)
        self.assertIn("signed by", checked.stdout)
        # A signed build changed after: not what the list signed.
        exe.write_text("changed")
        self.assertEqual(self.run_script("--check").returncode, 1)

        # Run again: the same certificate (made only near its end); the list signed again.
        before = (self.deploy / "signing" / "code-signing.crt").read_text()
        again = self.run_script()
        self.assertEqual(again.returncode, 0, again.stdout + again.stderr)
        self.assertNotIn("made signing/code-signing.crt", again.stdout)
        self.assertEqual((self.deploy / "signing" / "code-signing.crt").read_text(), before)

    def test_a_list_changed_since_it_was_signed_fails_the_check_until_signed_again(self):
        self.assertEqual(self.run_script().returncode, 0)
        out = self.data / "code-arena-signature"
        self.assertTrue((out / "win-x64" / "code-arena.exe").exists())
        self.sums.write_text("c" * 64 + "  linux-x64/code-arena\n")
        checked = self.run_script("--check")
        self.assertEqual(checked.returncode, 1)
        self.assertIn("are not signed", checked.stdout)
        # Signed again: no Windows build in the image now, so no signed copy of one either.
        again = self.run_script()
        self.assertEqual(again.returncode, 0, again.stdout + again.stderr)
        self.assertIn("signed 1 builds (0 for Windows", again.stdout)
        self.assertFalse((out / "win-x64" / "code-arena.exe").exists())
        self.assertEqual(self.run_script("--check").returncode, 0)

    def test_when_the_windows_builds_cannot_be_signed_nothing_changes(self):
        failed = self.run_script(FAIL_RUN="1")
        self.assertEqual(failed.returncode, 1)
        self.assertIn("could not be signed", failed.stderr)
        self.assertFalse((self.data / "code-arena-signature").exists())

    def test_without_a_ca_or_builds_nothing_is_signed_and_nothing_fails(self):
        shutil.rmtree(self.deploy / "certs")
        none = self.run_script()
        self.assertEqual(none.returncode, 0, none.stdout + none.stderr)
        self.assertIn("nothing signs the builds", none.stdout)
        self.assertFalse((self.data / "code-arena-signature").exists())
        self.sums.unlink()
        empty = self.run_script()
        self.assertEqual(empty.returncode, 0)
        self.assertIn("nothing to sign", empty.stdout)


if __name__ == "__main__":
    unittest.main()
