#!/usr/bin/env bash
# Runs the end-to-end suites against the real Docker stack and prints a summary.
#
#   ./e2e/run.sh                 all suites (about 45 minutes)
#   ./e2e/run.sh 05 06           only suites whose file name starts with these prefixes
#   ./e2e/run.sh --keep ...      leave the stack running afterwards (default: empty the test data and stop it)
#
# Needs Docker, Node 20+ and Google Chrome (set CHROME_BIN if it is not in the usual place). The first run builds the images.
# Uses docker.env (created by scripts/gen-docker-env.sh); never touches a MySQL outside the compose stack.
set -uo pipefail
cd "$(dirname "$0")/.."
export E2E_BASE_COMPOSE="$PWD/docker-compose.yml"

KEEP=0; WANT=()
for a in "$@"; do if [ "$a" = "--keep" ]; then KEEP=1; else WANT+=("$a"); fi; done
[ -f docker.env ] || ./scripts/gen-docker-env.sh
(cd e2e && npm install --silent --no-audit --no-fund) || exit 1
mkdir -p e2e/results; rm -f e2e/results/*.json e2e/results/*.done

STUB="docker compose --env-file docker.env -f docker-compose.yml -f e2e/compose.e2e.yml"
REAL="docker compose --env-file docker.env"
# The Phase 21 abuse limits would throttle the suites themselves (dozens of registrations from one address), so every suite
# except 11-security runs with them effectively off; that suite sets real values itself and puts these back when it ends.
export RATE_AUTH_PER_MINUTE=${RATE_AUTH_PER_MINUTE:-100000} RATE_CHAT_PER_MINUTE=${RATE_CHAT_PER_MINUTE:-100000} LOCKOUT_MAX_ATTEMPTS=${LOCKOUT_MAX_ATTEMPTS:-100000}
selected() { local f; f=$(basename "$1"); [ ${#WANT[@]} -eq 0 ] && return 0; for w in "${WANT[@]}"; do [[ "$f" == "$w"* ]] && return 0; done; return 1; }
wait_healthy() { for _ in $(seq 1 90); do n=$($1 ps --format '{{.Service}} {{.Health}}' | grep -cE "$2"); [ "$n" = "$3" ] && return 0; sleep 3; done; return 1; }

RC=0
run_suite() { echo; echo "=================== $(basename "$1") ==================="; (cd e2e && E2E_STACK="$2" node "$1") || { RC=1; echo "!! $(basename "$1") finished with failures or crashed (see above)"; }; }

# ---------- phase A: the stack with the AI stub, three API replicas and a load balancer ----------
NEED_A=0; for f in e2e/suites/0*.mjs; do selected "$f" && NEED_A=1; done
if [ $NEED_A = 1 ]; then
  $STUB up -d --build --remove-orphans || exit 1
  wait_healthy "$STUB" '^(api|api2|api3|ai-service|rabbitmq|redis|mysql) healthy' 7 || { echo "stack did not become healthy"; exit 1; }
  sleep 10
  for f in e2e/suites/0*.mjs; do selected "$f" && run_suite "suites/$(basename "$f")" stub; done
fi

# ---------- phase B: the normal stack with the REAL Python service and the real LLM provider ----------
NEED_B=0; for f in e2e/suites/1*.mjs; do selected "$f" && NEED_B=1; done
if [ $NEED_B = 1 ]; then
  $STUB stop >/dev/null 2>&1
  $REAL up -d --build --remove-orphans --force-recreate || exit 1
  wait_healthy "$REAL" '^(api|web|ai-service|rabbitmq|redis|mysql) healthy' 6 || { echo "stack did not become healthy"; exit 1; }
  sleep 8
  # 11 checks the real nginx headers, rate limits and lockout against the normal stack, so it goes first (unchanged configuration)
  selected e2e/suites/11-security.mjs && run_suite suites/11-security.mjs real
  # 10a changes the provider key and the async flag itself, and says so; afterwards put the normal configuration back
  selected e2e/suites/10a-real-llm-failure.mjs && run_suite suites/10a-real-llm-failure.mjs real
  if selected e2e/suites/10b-regression-real-llm.mjs; then
    $REAL up -d --force-recreate ai-service api >/dev/null 2>&1; wait_healthy "$REAL" '^(api|web|ai-service) healthy' 3; sleep 8
    echo; echo "=================== 10b-regression-real-llm.mjs (default: synchronous replies) ==================="
    (cd e2e && E2E_STACK=real ASYNC=0 OUT=results/10b-regression-sync.json node suites/10b-regression-real-llm.mjs) || { RC=1; echo "!! 10b (sync) finished with failures or crashed"; }
    CHAT_ASYNC_REPLIES=true $REAL up -d --force-recreate api >/dev/null 2>&1; wait_healthy "$REAL" '^(api|web|ai-service) healthy' 3; sleep 8
    echo; echo "=================== 10b-regression-real-llm.mjs (Chat:AsyncReplies on) ==================="
    (cd e2e && E2E_STACK=real ASYNC=1 OUT=results/10b-regression-async.json node suites/10b-regression-real-llm.mjs) || { RC=1; echo "!! 10b (async) finished with failures or crashed"; }
  fi
fi

# ---------- summary and clean-up ----------
echo; echo "=================== SUMMARY ==================="
node e2e/report.mjs
if [ $KEEP = 0 ]; then
  echo "emptying the test data and stopping the stack"
  $REAL exec -T mysql sh -c 'mysql -uaichat -p"$MYSQL_PASSWORD" AiChatAssistant -e "DELETE FROM OutboxMessages; DELETE FROM AiReplyJobs; DELETE FROM DirectMessages; DELETE FROM DirectConversations; DELETE FROM UsageLogs; DELETE FROM Messages; DELETE FROM ChatSessions; DELETE FROM PasswordResetTokens; DELETE FROM UserRoles; DELETE FROM Users; ALTER TABLE Users AUTO_INCREMENT=1; ALTER TABLE OutboxMessages AUTO_INCREMENT=1;" 2>/dev/null' >/dev/null 2>&1
  $REAL exec -T redis redis-cli flushall >/dev/null 2>&1
  $STUB stop >/dev/null 2>&1; $REAL stop >/dev/null 2>&1
fi
exit $RC
