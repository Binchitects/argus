#!/usr/bin/env bash
# End to end through the running stack: sign in as the administrator, ask the
# model a question in a new conversation, and read the streamed answer back.
source "$(dirname "$0")/lib.sh"

base="http://127.0.0.1:$(envget ARGUS_HTTP_PORT 8080)"
user=$(envget ARGUS_ADMIN_USERNAME admin)
pass=$(envget ARGUS_ADMIN_PASSWORD)
jar=$(mktemp); trap 'rm -f "$jar"' EXIT

echo "smoke test against $base"
code=$(curl -s -o /dev/null -w '%{http_code}' -c "$jar" -H 'Content-Type: application/json' \
  -d "{\"username\":\"$user\",\"password\":\"$pass\"}" "$base/api/auth/login")
[ "$code" = 200 ] && ok "signed in as $user" || { bad "sign-in returned HTTP $code"; exit 1; }

models=$(curl -s -b "$jar" "$base/api/models")
case "$models" in *\"*) ok "models: $models" ;; *) bad "the gateway lists no model: $models" ;; esac

conv=$(curl -s -b "$jar" -H 'Content-Type: application/json' -H 'X-Argus-Request: 1' -d '{}' "$base/api/conversations")
id=$(printf '%s' "$conv" | sed -n 's/.*"id":"\([0-9a-f]*\)".*/\1/p')
[ -n "$id" ] && ok "conversation $id" || { bad "could not create a conversation: $conv"; exit 1; }

started=$(date +%s)
stream=$(curl -s -N -m 600 -b "$jar" -H 'Content-Type: application/json' -H 'X-Argus-Request: 1' \
  -d '{"content":"Reply with the single word: ready","tools":false}' "$base/api/conversations/$id/messages")
if printf '%s' "$stream" | grep -q '"type":"done"'; then
  answer=$(printf '%s' "$stream" | sed -n 's/^data: {"type":"content","text":"\(.*\)"}$/\1/p' | tr -d '\n' | sed 's/\\n/ /g')
  ok "the model answered in $(( $(date +%s) - started ))s: ${answer:0:80}"
else
  bad "no answer: $(printf '%s' "$stream" | grep -o '"message":"[^"]*"' | head -1)"
fi
curl -s -o /dev/null -b "$jar" -X DELETE -H 'X-Argus-Request: 1' "$base/api/conversations/$id"
[ "$FAILED" -eq 0 ] && echo "ready to use: $base" || echo "$FAILED problem(s)"
exit "$FAILED"
