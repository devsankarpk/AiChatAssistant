#!/usr/bin/env bash
# Creates docker.env (gitignored) with random secrets. Safe to re-run: never overwrites.
set -euo pipefail

cd "$(dirname "$0")/.."

if [[ -e docker.env ]]; then
  echo "docker.env already exists - leaving it alone. Delete it first to regenerate." >&2
  exit 1
fi

rand() { openssl rand -hex 32; }

cat > docker.env <<EOF
MYSQL_ROOT_PASSWORD=$(rand)
MYSQL_PASSWORD=$(rand)
JWT_KEY=$(rand)
INTERNAL_API_KEY=$(rand)
RABBITMQ_PASSWORD=$(rand)
REDIS_PASSWORD=$(rand)

DEEPINFRA_API_KEY=
SMTP_USERNAME=
SMTP_PASSWORD=

PUBLIC_URL=http://localhost:8080
EOF
chmod 600 docker.env

echo "Created docker.env. Next: open it and set DEEPINFRA_API_KEY (needed for chat replies)."
echo "SMTP_USERNAME / SMTP_PASSWORD are optional (real forgot-password emails only)."
