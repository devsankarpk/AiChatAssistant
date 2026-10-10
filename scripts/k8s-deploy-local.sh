#!/usr/bin/env bash
# Build the four images locally and (re)deploy them to Docker Desktop's Kubernetes cluster.
#   ./scripts/k8s-deploy-local.sh              # reads docker.env (see scripts/gen-docker-env.sh)
#   ENV_FILE=other.env ./scripts/k8s-deploy-local.sh
# Tear down: kubectl delete namespace aichat   (also deletes the MySQL volume)
set -euo pipefail

cd "$(dirname "$0")/.."
ENV_FILE="${ENV_FILE:-docker.env}"
NS=aichat

if [[ ! -f "$ENV_FILE" ]]; then
  echo "$ENV_FILE not found - run ./scripts/gen-docker-env.sh first." >&2
  exit 1
fi

# Safety rail: this script creates secrets and applies manifests, so never point it at a
# cluster you didn't mean to (a work cluster, say) just because it's the current context.
context="$(kubectl config current-context 2>/dev/null || true)"
if [[ "$context" != "docker-desktop" && -z "${ALLOW_CONTEXT:-}" ]]; then
  echo "Current kubectl context is '${context:-none}', expected 'docker-desktop'." >&2
  echo "Enable Docker Desktop > Settings > Kubernetes, or set ALLOW_CONTEXT=1 to override." >&2
  exit 1
fi

echo "==> Building images"
docker build -q -f backend-dotnet/Dockerfile --target api     -t aichat-api:local .
docker build -q -f backend-dotnet/Dockerfile --target migrate -t aichat-migrate:local .
docker build -q -t aichat-ai-service:local ai-service-python
docker build -q -t aichat-web:local frontend

existing=false
kubectl -n "$NS" get deployment api >/dev/null 2>&1 && existing=true

echo "==> Applying manifests to namespace '$NS'"
kubectl apply -f k8s/base/namespace.yaml
kubectl -n "$NS" create secret generic aichat-secrets --from-env-file="$ENV_FILE" \
  --dry-run=client -o yaml | kubectl apply -f -
kubectl apply -k k8s/overlays/local

# Rebuilt images keep the :local tag, so an unchanged manifest wouldn't roll the pods.
if [[ "$existing" == true ]]; then
  kubectl -n "$NS" rollout restart deployment/api deployment/ai-service deployment/web
fi

echo "==> Waiting for everything to become ready (first run pulls mysql and migrates the DB)"
kubectl -n "$NS" rollout status statefulset/mysql --timeout=300s
kubectl -n "$NS" rollout status deployment/ai-service --timeout=300s
kubectl -n "$NS" rollout status deployment/api --timeout=300s
kubectl -n "$NS" rollout status deployment/web --timeout=300s

echo
kubectl -n "$NS" get pods
echo
echo "Open http://localhost:8081"
