"""The TLS policy both GitLab transports share.

Argus reaches GitLab two ways that cannot see each other's configuration:
`httpx` for the API, and the `git` binary for clones. The whole point of
`argus.tls` is that one setting drives both, so each test below pins one half
of that contract. The failure being prevented is specific: the API enumerates
projects perfectly and the first clone then dies with "server certificate
verification failed", which reads as a credential or URL problem and sends an
operator looking in the wrong place.
"""
import ssl

import httpx
import pytest

from argus import tls
from argus.config import GitLabConfig

#: A throwaway CA -- public half only, generated for this suite, no private key
#: kept, valid until 2126. Embedded rather than made at test time so the suite
#: needs neither an openssl binary nor a network.
CA_PEM = """-----BEGIN CERTIFICATE-----
MIIDPTCCAiWgAwIBAgIUZmyaShjnVRmtQuTIw4SfPhOpuZowDQYJKoZIhvcNAQEL
BQAwLTETMBEGA1UECgwKQXJndXMgVGVzdDEWMBQGA1UEAwwNYXJndXMtdGVzdC1j
YTAgFw0yNjA5MTUxODMyMjlaGA8yMTI2MDgyMjE4MzIyOVowLTETMBEGA1UECgwK
QXJndXMgVGVzdDEWMBQGA1UEAwwNYXJndXMtdGVzdC1jYTCCASIwDQYJKoZIhvcN
AQEBBQADggEPADCCAQoCggEBAKKWXboAunapHc5xX1NIww1fmFsWH2i5WYUb9wx8
LgNBILgfO8Y4FhxE/u3qTTn8dSI3AsUySC6IsKGp2feJ9BLdfRkH+meTubcdEnir
YTzEHqYw6LmJQj7Thh9Xev+Z4gXiTAowXXHZkTILEPvywm+I6okVVJPKXA0cSL36
/V+zpuMAmVv6xlcWG4/AVvgEqmwaBVPR6FxW3F70oR+TtYnxvh8HyD7qA/0aEAZM
+ngVJGtDG/dH/uSJWwXWWSiyeh3PjrsEyA20EtQR+QUG6zmqALTWILAryRMNz7cW
DjUBACaPrzq4pMYs/G7Eyy/p7dxLGFnIHlzNvdf2FEVbdm0CAwEAAaNTMFEwHQYD
VR0OBBYEFAUvBRS9P4EL1tFQBkqEsT+jUT+yMB8GA1UdIwQYMBaAFAUvBRS9P4EL
1tFQBkqEsT+jUT+yMA8GA1UdEwEB/wQFMAMBAf8wDQYJKoZIhvcNAQELBQADggEB
AB/bdygvHPRFZquCopn8EN/gLHIfsy/mvHmGADxwaV11xDSh9gV+ZpQk6B+WZNCL
DHTYNemqjTtGMWsDBm/IOgPwuh9w2gDVyT08EZjGRbSOiOKUy9zG+0/5iwrGy9wH
iUm/Hi+Od5/Zu6M7GanRbkSIcJFKZVDcdLeJHdr8EC+4m8r6qa06Y/MTa9R5046o
SB8yW7twWJegJmG+HAL7VQH4QBBsY5ryWfAGSwlD8U5kz6tsnFBwqy73IjXL1W+U
1aZy9yT1kQZiGIpDcQRm2YUmTGlF+GI8R11UsSHXNJbtZZJ3Tw72G4/ixicO0Mg5
jYLSmvrhWy27LrUcYO9L3xM=
-----END CERTIFICATE-----
"""


def cfg(**kw) -> GitLabConfig:
    kw.setdefault("url", "https://gitlab.internal")
    kw.setdefault("token", "glpat-x")
    return GitLabConfig(**kw)


def test_default_verification_is_untouched():
    # No policy configured has to mean exactly what httpx did before this
    # module existed -- verify against certifi. Anything else would silently
    # change trust for every deployment already in the field.
    assert tls.verify_for(cfg()) is True


def test_verification_can_be_turned_off():
    # The self-signed-GitLab-with-no-CA-file case. It has to work, because the
    # alternative is an operator patching Argus.
    assert tls.verify_for(cfg(verify=False)) is False


def test_a_ca_bundle_keeps_the_public_roots_too(tmp_path):
    pem = tmp_path / "private-ca.pem"
    pem.write_text(CA_PEM, encoding="utf-8")

    context = tls.verify_for(cfg(ca_cert=str(pem)))

    assert isinstance(context, ssl.SSLContext)
    assert context.verify_mode == ssl.CERT_REQUIRED
    assert context.check_hostname is True
    # The reason a context is built instead of handing httpx the path:
    # verify="ca.pem" REPLACES the default store, so a GitLab that redirects
    # to a public host -- or any other HTTPS call sharing this client -- would
    # stop verifying a certificate it used to accept. One extra CA on top of
    # the defaults is the whole assertion.
    public_roots = ssl.create_default_context().get_ca_certs()
    assert len(context.get_ca_certs()) == len(public_roots) + 1


def test_a_ca_bundle_that_is_not_a_file_raises(tmp_path):
    # FileNotFoundError, not SSLError: that is what
    # SSLContext.load_verify_locations raises, and swallowing it into
    # something vaguer would hide which path was wrong.
    #
    # In normal use `Config.load` refuses a missing ca_cert before an operator
    # ever gets here, so reaching this means the file disappeared between
    # startup and the request -- a different problem, and worth its own error.
    with pytest.raises(FileNotFoundError):
        tls.verify_for(cfg(ca_cert=str(tmp_path / "absent.pem")))


def test_client_for_builds_an_httpx_client(tmp_path):
    client = tls.client_for(cfg(verify=False), timeout=5.0)
    try:
        assert isinstance(client, httpx.Client)
    finally:
        client.close()


def test_git_is_told_not_to_verify():
    env = tls.git_env(cfg(verify=False), {})
    assert env["GIT_SSL_NO_VERIFY"] == "true"
    assert "GIT_SSL_CAINFO" not in env


def test_git_is_pointed_at_the_bundle():
    env = tls.git_env(cfg(ca_cert="/etc/ssl/private-ca.pem"), {})
    assert env["GIT_SSL_CAINFO"] == "/etc/ssl/private-ca.pem"
    assert "GIT_SSL_NO_VERIFY" not in env


def test_git_env_changes_nothing_by_default():
    # The default must not set either variable. GIT_SSL_NO_VERIFY reaching an
    # unrelated remote would weaken a connection nobody asked to weaken, and
    # these variables are inherited by every child process.
    env = tls.git_env(cfg(), {"PATH": "/usr/bin"})
    assert env == {"PATH": "/usr/bin"}
