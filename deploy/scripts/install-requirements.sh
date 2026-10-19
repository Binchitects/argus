#!/usr/bin/env bash
# What the stack needs on this host, checked, and put right where the installer can:
#
#   ./scripts/install-requirements.sh            check only
#   ./scripts/install-requirements.sh --fix      install what is missing (as root, asking first)
#   ./scripts/install-requirements.sh --podman   for rootless Podman in place of Docker
#
# It is installer.sh's requirements command; see installer.sh help.
exec bash "$(dirname "${BASH_SOURCE[0]}")/installer.sh" requirements "$@"
