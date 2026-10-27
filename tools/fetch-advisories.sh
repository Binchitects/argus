#!/usr/bin/env bash
# The sources of Argus's `advisories` knowledge pack, fetched into one work directory, to be run each day on a
# connected machine (then tools/advisories-texts.py, argus pack build and argus pack index; an offline host takes the
# pack with argus pack update or airgap.sh):
#
#   tools/fetch-advisories.sh [DIR]     (DIR: .packwork/docsrc/advisories)
#
#   github/  GitHub's reviewed security advisories, in OSV's format (github/advisory-database, CC BY 4.0): npm, NuGet,
#            PyPI, Maven, Go, crates.io, RubyGems, Packagist and the rest, each with its CVE, CWE, severity and the
#            version ranges it affects and that fix it
#   kev.json CISA's Known Exploited Vulnerabilities catalog (public domain)
#
# Cloned shallow and sparse (only the reviewed advisories), so the commit is the provenance. Google's OSV storage is
# not used: it refuses some locations. A part is fetched into a new folder and swapped in only when whole.
set -euo pipefail
here="$(cd "$(dirname "$0")" && pwd)"
root="$(dirname "$here")"
dir="${1:-$root/.packwork/docsrc/advisories}"
mkdir -p "$dir"
dir="$(cd "$dir" && pwd)"

tmp="$dir/.github.new"
rm -rf "$tmp"
git clone --quiet --depth 1 --filter=blob:none --no-checkout https://github.com/github/advisory-database "$tmp"
git -C "$tmp" sparse-checkout set --no-cone '/advisories/github-reviewed/' '/LICENSE.md'
git -C "$tmp" checkout --quiet
rm -rf "${dir:?}/github"
mv "$tmp" "$dir/github"
echo "github: $(git -C "$dir/github" rev-parse --short=12 HEAD), $(find "$dir/github/advisories" -name '*.json' | wc -l) advisories, $(du -sh --exclude=.git "$dir/github" | cut -f1)"

curl -fsSL --retry 3 --max-time 300 -o "$dir/kev.json.part" https://www.cisa.gov/sites/default/files/feeds/known_exploited_vulnerabilities.json
python3 -I -c 'import json, sys; d = json.load(open(sys.argv[1])); assert d["vulnerabilities"], "empty"; print("kev:", d["catalogVersion"], len(d["vulnerabilities"]), "exploited")' "$dir/kev.json.part"
mv "$dir/kev.json.part" "$dir/kev.json"
