#!/usr/bin/env bash
# On-host deployment script executed on EC2 via AWS SSM Run Command (AI Exam Maker - Task O15).
# Performs:
#   1. Extracts release archive into /opt/exammaker/releases/<RELEASE_ID> with exammaker:exammaker ownership
#   2. Runs EF Core migration bundle (efbundle) BEFORE switching /opt/exammaker/current symlink
#      (reads ConnectionStrings/ExamMaker from SSM Parameter Store into env only — never in argv or disk)
#   3. Atomically switches /opt/exammaker/current symlink and restarts exammaker-api & exammaker-worker
#   4. Verifies http://127.0.0.1:5000/health and systemd active state; auto-rolls back on failure
#   5. Prunes older releases, keeping the N most recent releases (default KEEP_RELEASES=5)

set -euo pipefail

APP_ROOT="${APP_ROOT:-/opt/exammaker}"
APP_USER="${APP_USER:-exammaker}"
AWS_REGION="${AWS_REGION:-ap-southeast-1}"
SSM_PREFIX="${SSM_PREFIX:-/exammaker/dev}"
RELEASE_ID="${RELEASE_ID:?RELEASE_ID is required}"
ARCHIVE_PATH="${ARCHIVE_PATH:?ARCHIVE_PATH is required}"
SKIP_MIGRATIONS="${SKIP_MIGRATIONS:-0}"
KEEP_RELEASES="${KEEP_RELEASES:-5}"
HEALTH_URL="${HEALTH_URL:-http://127.0.0.1:5000/health}"
HEALTH_TIMEOUT_SECONDS="${HEALTH_TIMEOUT_SECONDS:-30}"

if ! [[ "$RELEASE_ID" =~ ^[A-Za-z0-9._-]+$ ]]; then
  echo "ERROR: Invalid RELEASE_ID '${RELEASE_ID}'." >&2
  exit 1
fi

if ! [[ "$KEEP_RELEASES" =~ ^[1-9][0-9]*$ ]]; then
  echo "ERROR: KEEP_RELEASES must be a positive integer (got '${KEEP_RELEASES}')." >&2
  exit 1
fi

RELEASES_DIR="${APP_ROOT}/releases"
RELEASE_DIR="${RELEASES_DIR}/${RELEASE_ID}"
CURRENT_LINK="${APP_ROOT}/current"

if [ -e "$CURRENT_LINK" ] && [ ! -L "$CURRENT_LINK" ]; then
  echo "ERROR: '${CURRENT_LINK}' exists and is not a symlink. Refusing to overwrite." >&2
  exit 1
fi

PREV_RELEASE=""
if [ -L "$CURRENT_LINK" ]; then
  PREV_RELEASE="$(readlink -f "$CURRENT_LINK" || true)"
fi

echo "==> Deploying release '${RELEASE_ID}' to '${RELEASE_DIR}'..."
install -d -o "$APP_USER" -g "$APP_USER" -m 0750 "$RELEASES_DIR"
rm -rf "$RELEASE_DIR"
install -d -o "$APP_USER" -g "$APP_USER" -m 0750 "$RELEASE_DIR"

tar -xzf "$ARCHIVE_PATH" -C "$RELEASE_DIR"
chmod +x "${RELEASE_DIR}/ExamMaker.Api" "${RELEASE_DIR}/ExamMaker.Worker"
if [ -f "${RELEASE_DIR}/efbundle" ]; then
  chmod +x "${RELEASE_DIR}/efbundle"
fi
chown -R "${APP_USER}:${APP_USER}" "$RELEASE_DIR"

# 1. Run EF Core migration bundle BEFORE switching the active release symlink
if [ "$SKIP_MIGRATIONS" = "1" ]; then
  echo "==> [SKIPPED] EF Core migration bundle skipped (SKIP_MIGRATIONS=1)."
else
  if [ ! -x "${RELEASE_DIR}/efbundle" ]; then
    echo "ERROR: Migration bundle '${RELEASE_DIR}/efbundle' not found or not executable." >&2
    exit 1
  fi
  echo "==> Running EF Core migration bundle before switching symlink..."
  CONN_STR="$(aws ssm get-parameter \
    --region "$AWS_REGION" \
    --name "${SSM_PREFIX}/ConnectionStrings/ExamMaker" \
    --with-decryption \
    --query "Parameter.Value" \
    --output text | tr -d '\r\n')"
  if [ -z "$CONN_STR" ]; then
    echo "ERROR: Failed to fetch '${SSM_PREFIX}/ConnectionStrings/ExamMaker' from SSM Parameter Store." >&2
    exit 1
  fi

  # Pass connection string via environment variable (read by ExamMakerDbContextFactory) so it never appears in ps argv.
  MIG_RC=0
  ConnectionStrings__ExamMaker="$CONN_STR" "${RELEASE_DIR}/efbundle" || MIG_RC=$?
  unset CONN_STR
  if [ "$MIG_RC" -ne 0 ]; then
    echo "ERROR: EF Core migration bundle failed (exit=${MIG_RC}). Current symlink was NOT changed." >&2
    exit "$MIG_RC"
  fi
  echo "==> EF Core migrations applied successfully."
fi

check_services_healthy() {
  local deadline=$(( $(date +%s) + HEALTH_TIMEOUT_SECONDS ))
  while [ "$(date +%s)" -le "$deadline" ]; do
    if systemctl is-active --quiet exammaker-api \
      && systemctl is-active --quiet exammaker-worker \
      && curl -fsS --max-time 3 "$HEALTH_URL" >/dev/null 2>&1; then
      return 0
    fi
    sleep 2
  done
  return 1
}

# 2. Switch symlink atomically and restart systemd services
echo "==> Switching symlink '${CURRENT_LINK}' -> '${RELEASE_DIR}' and restarting systemd services..."
ln -sfn "$RELEASE_DIR" "$CURRENT_LINK"
systemctl restart exammaker-api exammaker-worker

# 3. Verify health and auto-rollback on failure
if check_services_healthy; then
  echo "==> [HEALTHY] exammaker-api (/health) and exammaker-worker are active."
else
  echo "ERROR: Health check failed after restarting services for release '${RELEASE_ID}'." >&2
  systemctl status exammaker-api --no-pager -l >&2 || true
  systemctl status exammaker-worker --no-pager -l >&2 || true

  if [ -n "$PREV_RELEASE" ] && [ -d "$PREV_RELEASE" ] && [ "$PREV_RELEASE" != "$RELEASE_DIR" ]; then
    echo "==> [AUTO-ROLLBACK] Reverting '${CURRENT_LINK}' -> '${PREV_RELEASE}'..." >&2
    ln -sfn "$PREV_RELEASE" "$CURRENT_LINK"
    systemctl restart exammaker-api exammaker-worker || true
    if check_services_healthy; then
      echo "==> [AUTO-ROLLBACK] Previous release '${PREV_RELEASE}' is healthy again." >&2
    else
      echo "ERROR: Previous release '${PREV_RELEASE}' also failed health check — manual intervention required!" >&2
    fi
  else
    echo "WARN: No previous release available for auto-rollback." >&2
  fi
  exit 1
fi

# 4. Prune old releases, always keeping the active release and the N newest directories
ACTIVE_REAL="$(readlink -f "$CURRENT_LINK")"
mapfile -t ALL_RELEASES < <(find "$RELEASES_DIR" -mindepth 1 -maxdepth 1 -type d -printf '%T@ %p\n' | sort -rn | awk '{print $2}')
KEPT=0
REMOVED=0
for dir in "${ALL_RELEASES[@]}"; do
  if [ "$dir" = "$ACTIVE_REAL" ]; then
    KEPT=$((KEPT + 1))
    continue
  fi
  if [ "$KEPT" -lt "$KEEP_RELEASES" ]; then
    KEPT=$((KEPT + 1))
  else
    rm -rf "$dir"
    REMOVED=$((REMOVED + 1))
  fi
done
echo "==> Release retention complete (kept=${KEPT}, removed=${REMOVED}, limit=${KEEP_RELEASES})."
echo "DONE: Release '${RELEASE_ID}' is live."
