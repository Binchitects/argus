#!/usr/bin/env bash
# Back up everything that cannot be rebuilt: people, keys and conversations
# (app.db), the code index and installed packs, the gateway's database (keys,
# budgets, spend) and .env (its LITELLM_SALT_KEY decrypts that database).
# Mirrors and worktrees are left out: they are fetched again from GitLab.
source "$(dirname "$0")/lib.sh"

dir=$(envget BACKUP_DIR ./backups)
keep=$(envget BACKUP_KEEP 14)
stamp=$(date +%Y%m%d-%H%M%S)
out="$dir/argus-$stamp"
mkdir -p "$out"
chmod 700 "$out"

echo "backing up to $out"
docker exec argus argus backup --config /etc/argus/config.yaml --out "/var/lib/argus/backups/$stamp" >/dev/null \
  && docker cp "argus:/var/lib/argus/backups/$stamp" "$out/argus" >/dev/null \
  && docker exec argus rm -rf "/var/lib/argus/backups/$stamp" \
  && ok "app database, index and packs" || bad "argus backup failed"
docker exec postgres pg_dump -U "$(envget LLM_PG_USER llmservice)" -d litellm -Fc > "$out/litellm.dump" \
  && ok "gateway database ($(du -h "$out/litellm.dump" | cut -f1))" || bad "pg_dump failed"
install -m 600 .env "$out/env" && ok ".env (holds the secrets: keep this backup private)"
(cd "$out" && find . -type f ! -name SHA256SUMS -print0 | sort -z | xargs -0 sha256sum > SHA256SUMS)

if [ "$FAILED" -eq 0 ]; then
  ls -1dt "$dir"/argus-* 2>/dev/null | tail -n +"$((keep + 1))" | xargs -r rm -rf
  echo "done: $out (keeping the newest $keep)"
else
  echo "$FAILED part(s) failed; nothing older was removed"
fi
exit "$FAILED"
