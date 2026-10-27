#!/usr/bin/env bash
# The sources of Argus's `security` knowledge pack, fetched into one work directory, each part in a folder of its own:
#
#   tools/fetch-security-sources.sh [DIR]     (DIR: .packwork/docsrc/security)
#
# then: argus pack build --source security --work-dir "$PWD/DIR" --out packs/security-1.0.arguspack --version 1.0
#
# What is fetched, and under what terms (the pack records each):
#   mitre/        CWE and CAPEC, MITRE's XML (free to use, with MITRE's notice)
#   cheatsheets/  OWASP Cheat Sheet Series, cheatsheets/ only (CC BY-SA 4.0)
#   asvs/         OWASP ASVS 5.0, English (CC BY-SA 4.0)
#   wstg/         OWASP Web Security Testing Guide, document/ (CC BY-SA 4.0)
#   top10/        OWASP Top 10 (CC BY-SA 4.0)
#   api-top10/    OWASP API Security Top 10 (CC BY-SA 4.0)
#   llm-top10/    OWASP Top 10 for LLM Applications (CC BY-SA 4.0)
#   codeql/       CodeQL's security queries' help and metadata only, never its engine (MIT)
#   dotnet-rules/ .NET's code analysis rules, from dotnet/docs (CC BY 4.0)
#   bandit/       Bandit's checks (Apache-2.0)
#   gosec/        gosec's rules (Apache-2.0)
#   eslint-security/ eslint-plugin-security's rules (Apache-2.0)
# Git repositories are cloned shallow and sparse (only the folders above), so each records its commit. Not taken:
# Semgrep's registry rules and the SEI CERT wiki (their terms do not allow redistribution), the CIS benchmarks.
# Run again to update: each part is fetched afresh into a new folder and swapped in only when whole.
set -euo pipefail
here="$(cd "$(dirname "$0")" && pwd)"
root="$(dirname "$here")"
dir="${1:-$root/.packwork/docsrc/security}"
mkdir -p "$dir"
dir="$(cd "$dir" && pwd)"

# repo, folder, then the sparse patterns (no-cone: gitignore style).
sparse() {
  local repo="$1" name="$2"; shift 2
  local tmp="$dir/.$name.new"
  rm -rf "$tmp"
  git clone --quiet --depth 1 --filter=blob:none --no-checkout "https://github.com/$repo" "$tmp"
  git -C "$tmp" sparse-checkout set --no-cone "$@"
  git -C "$tmp" checkout --quiet
  rm -rf "${dir:?}/$name"
  mv "$tmp" "$dir/$name"
  echo "$name: $repo at $(git -C "$dir/$name" rev-parse --short=12 HEAD), $(du -sh --exclude=.git "$dir/$name" | cut -f1)"
}

fetch() {
  local url="$1" out="$2"
  curl -fsSL --retry 3 --max-time 300 -o "$out.part" "$url"
  mv "$out.part" "$out"
}

tmp="$dir/.mitre.new"
rm -rf "$tmp" && mkdir -p "$tmp"
fetch https://cwe.mitre.org/data/xml/cwec_latest.xml.zip "$tmp/cwec_latest.xml.zip"
fetch https://capec.mitre.org/data/xml/capec_latest.xml "$tmp/capec_latest.xml"
python3 -I -c 'import sys, zipfile; z = zipfile.ZipFile(sys.argv[1]); [n] = [n for n in z.namelist() if n.endswith(".xml") and "/" not in n]; open(sys.argv[2], "wb").write(z.read(n))' \
  "$tmp/cwec_latest.xml.zip" "$tmp/cwec.xml"
rm "$tmp/cwec_latest.xml.zip"
(cd "$tmp" && sha256sum cwec.xml capec_latest.xml > SHA256SUMS)
rm -rf "$dir/mitre" && mv "$tmp" "$dir/mitre"
echo "mitre: CWE and CAPEC, $(du -sh "$dir/mitre" | cut -f1)"

sparse OWASP/CheatSheetSeries cheatsheets '/cheatsheets/' '/LICENSE*'
sparse OWASP/ASVS asvs '/5.0/en/' '/LICENSE*'
sparse OWASP/wstg wstg '/document/' '/LICENSE*'
sparse OWASP/Top10 top10 '/2025/docs/en/' '/2021/docs/en/' '/LICENSE*'
sparse OWASP/API-Security api-top10 '/editions/2023/en/' '/LICENSE*'
sparse OWASP/www-project-top-10-for-large-language-model-applications llm-top10 '/2_0_vulns/LLM*.md' '/LICENSE*'
sparse github/codeql codeql '/*/ql/src/Security/' '/csharp/ql/src/Security Features/' '/csharp/ql/src/Input Validation/' \
  '/*/ql/src/queries/security/' '/swift/ql/src/queries/Security/' '/LICENSE'
sparse dotnet/docs dotnet-rules '/docs/fundamentals/code-analysis/quality-rules/' '/LICENSE'
sparse PyCQA/bandit bandit '/bandit/plugins/' '/bandit/blacklists/' '/bandit/core/issue.py' '/LICENSE'
sparse securego/gosec gosec '/RULES.md' '/LICENSE.txt' '/issue/issue.go'
sparse eslint-community/eslint-plugin-security eslint-security '/docs/rules/' '/LICENSE'
echo "all in $dir: $(du -sh --exclude=.git "$dir" | cut -f1)"
