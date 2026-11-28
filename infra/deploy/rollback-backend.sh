#!/usr/bin/env bash
# Operator rollback script for AI Exam Maker backend on EC2 via AWS SSM Run Command (Task O15).
# Switches /opt/exammaker/current symlink back to a previous release (or specified TARGET_RELEASE),
# restarts exammaker-api and exammaker-worker, and verifies http://127.0.0.1:5000/health.
#
# Usage:
#   # List available releases on EC2:
#   LIST_ONLY=1 ./infra/deploy/rollback-backend.sh
#
#   # Roll back automatically to the immediately previous release:
#   ./infra/deploy/rollback-backend.sh
#
#   # Roll back to a specific release ID:
#   TARGET_RELEASE="20261123150000-abcdef123456" ./infra/deploy/rollback-backend.sh

set -euo pipefail

export MSYS_NO_PATHCONV=1
export MSYS2_ARG_CONV_EXCL="*"

EXPECTED_REGION="ap-southeast-1"
AWS_REGION="${AWS_REGION:-$EXPECTED_REGION}"
ALLOW_NON_DEFAULT_REGION="${ALLOW_NON_DEFAULT_REGION:-0}"
EC2_TAG_NAME="${EC2_TAG_NAME:-aem-exammaker-ec2}"
TARGET_RELEASE="${TARGET_RELEASE:-}"
LIST_ONLY="${LIST_ONLY:-0}"

command -v aws >/dev/null 2>&1 || { echo "ERROR: aws CLI is required." >&2; exit 1; }
command -v jq >/dev/null 2>&1 || { echo "ERROR: jq is required." >&2; exit 1; }

if [ "$AWS_REGION" != "$EXPECTED_REGION" ] && [ "$ALLOW_NON_DEFAULT_REGION" != "1" ]; then
  echo "ERROR: AWS_REGION is '${AWS_REGION}', expected '${EXPECTED_REGION}'." >&2
  exit 1
fi

if [ -n "$TARGET_RELEASE" ] && ! [[ "$TARGET_RELEASE" =~ ^[A-Za-z0-9._-]+$ ]]; then
  echo "ERROR: Invalid TARGET_RELEASE '${TARGET_RELEASE}'." >&2
  exit 1
fi

EC2_QUERY_JSON="$(aws ec2 describe-instances \
  --region "$AWS_REGION" \
  --filters "Name=tag:Name,Values=${EC2_TAG_NAME}" "Name=instance-state-name,Values=running" \
  --output json)"
EC2_INSTANCE_ID="$(jq -r '([.Reservations[].Instances[]] | sort_by(.LaunchTime) | reverse | .[0].InstanceId) // empty' <<<"$EC2_QUERY_JSON" | tr -d '\r\n')"

if [ -z "$EC2_INSTANCE_ID" ]; then
  echo "ERROR: No running EC2 instance found with tag Name='${EC2_TAG_NAME}' in ${AWS_REGION}." >&2
  exit 1
fi

REMOTE_SCRIPT="$(cat <<EOF
set -euo pipefail
APP_ROOT="/opt/exammaker"
RELEASES_DIR="\${APP_ROOT}/releases"
CURRENT_LINK="\${APP_ROOT}/current"
LIST_ONLY="${LIST_ONLY}"
TARGET_RELEASE="${TARGET_RELEASE}"

CURRENT_TARGET="\$(readlink -f "\$CURRENT_LINK" 2>/dev/null || true)"
echo "Current active release: \${CURRENT_TARGET:-<none>}"
echo "Available releases (newest first):"
mapfile -t RELEASES < <(find "\$RELEASES_DIR" -mindepth 1 -maxdepth 1 -type d -printf '%T@ %f\n' | sort -rn | awk '{print \$2}')
for rel in "\${RELEASES[@]}"; do
  marker=""
  if [ "\${RELEASES_DIR}/\${rel}" = "\$CURRENT_TARGET" ]; then
    marker=" (current)"
  fi
  echo "  - \${rel}\${marker}"
done

if [ "\$LIST_ONLY" = "1" ]; then
  exit 0
fi

if [ -z "\$TARGET_RELEASE" ]; then
  for rel in "\${RELEASES[@]}"; do
    if [ "\${RELEASES_DIR}/\${rel}" != "\$CURRENT_TARGET" ]; then
      TARGET_RELEASE="\$rel"
      break
    fi
  done
fi

if [ -z "\$TARGET_RELEASE" ]; then
  echo "ERROR: No previous release found in '\$RELEASES_DIR' to roll back to." >&2
  exit 1
fi

TARGET_DIR="\${RELEASES_DIR}/\${TARGET_RELEASE}"
if [ ! -d "\$TARGET_DIR" ]; then
  echo "ERROR: Target release directory '\$TARGET_DIR' does not exist." >&2
  exit 1
fi

echo "==> Rolling back '\$CURRENT_LINK' -> '\$TARGET_DIR'..."
ln -sfn "\$TARGET_DIR" "\$CURRENT_LINK"
systemctl restart exammaker-api exammaker-worker

for _ in \$(seq 1 15); do
  if systemctl is-active --quiet exammaker-api \
    && systemctl is-active --quiet exammaker-worker \
    && curl -fsS --max-time 3 http://127.0.0.1:5000/health >/dev/null 2>&1; then
    echo "DONE: Rolled back to release '\$TARGET_RELEASE' — exammaker-api (/health) and exammaker-worker are active."
    exit 0
  fi
  sleep 2
done

echo "ERROR: Services failed health check after rolling back to '\$TARGET_RELEASE'." >&2
systemctl status exammaker-api --no-pager -l >&2 || true
systemctl status exammaker-worker --no-pager -l >&2 || true
exit 1
EOF
)"

PARAMS_JSON="$(jq -nc --arg cmd "$REMOTE_SCRIPT" '{commands: [$cmd]}')"
COMMAND_ID="$(aws ssm send-command \
  --region "$AWS_REGION" \
  --document-name "AWS-RunShellScript" \
  --instance-ids "$EC2_INSTANCE_ID" \
  --comment "Backend rollback (list_only=${LIST_ONLY})" \
  --timeout-seconds 300 \
  --parameters "$PARAMS_JSON" \
  --query "Command.CommandId" \
  --output text | tr -d '\r\n')"

echo "==> Dispatched SSM Run Command to EC2 (tag Name=${EC2_TAG_NAME}). Polling status..."
STATUS="Pending"
for _ in $(seq 1 60); do
  sleep 3
  INV_JSON="$(aws ssm get-command-invocation \
    --region "$AWS_REGION" \
    --command-id "$COMMAND_ID" \
    --instance-id "$EC2_INSTANCE_ID" \
    --output json 2>/dev/null || echo '{"Status":"Pending"}')"
  STATUS="$(jq -r '.Status // "Pending"' <<<"$INV_JSON")"
  case "$STATUS" in
    Pending|InProgress|Delayed) continue ;;
    *) break ;;
  esac
done

jq -r '.StandardOutputContent // ""' <<<"$INV_JSON"
STDERR_OUT="$(jq -r '.StandardErrorContent // ""' <<<"$INV_JSON")"
if [ -n "$STDERR_OUT" ]; then
  printf '%s\n' "$STDERR_OUT" >&2
fi

if [ "$STATUS" != "Success" ]; then
  echo "ERROR: Rollback command failed (SSM status=${STATUS})." >&2
  exit 1
fi
