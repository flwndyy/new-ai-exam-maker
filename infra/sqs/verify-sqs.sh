#!/usr/bin/env bash
# Verification script for Amazon SQS + DLQ setup (AI Exam Maker - Task O11).
# Reads parameters from SSM Parameter Store and verifies 12 architecture, security,
# retry/DLQ, and cost-control checks against live AWS SQS queues.
#
# Dependencies: aws (CLI v2), jq (both pre-installed in AWS CloudShell).
#
# Usage:
#   ./infra/sqs/verify-sqs.sh
#   # ONLY run SMOKE_TEST=1 before Worker (B6) is deployed or while exammaker-worker is stopped!
#   # Running SMOKE_TEST=1 against an active worker risks the worker consuming the synthetic
#   # message (no jobId) or smoke test touching a real job and incrementing ApproximateReceiveCount.
#   SMOKE_TEST=1 ./infra/sqs/verify-sqs.sh

set -euo pipefail

export MSYS_NO_PATHCONV=1
export MSYS2_ARG_CONV_EXCL="*"

AWS_REGION="${AWS_REGION:-ap-southeast-1}"
ENV_NAME="${ENV_NAME:-dev}"
SSM_PREFIX="${SSM_PREFIX:-/exammaker/${ENV_NAME}}"
MAIN_QUEUE_NAME="${MAIN_QUEUE_NAME:-exammaker-${ENV_NAME}-ai-jobs}"
DLQ_NAME="${DLQ_NAME:-exammaker-${ENV_NAME}-ai-jobs-dlq}"
SMOKE_TEST="${SMOKE_TEST:-0}"

command -v aws >/dev/null 2>&1 || { echo "ERROR: aws CLI is required." >&2; exit 1; }
command -v jq >/dev/null 2>&1 || { echo "ERROR: jq is required (pre-installed in AWS CloudShell; on Windows install via 'winget install jqlang.jq')." >&2; exit 1; }

PASSED=0
FAILURES=0

pass() { echo "[PASS] $1"; PASSED=$((PASSED + 1)); }
fail() { echo "[FAIL] $1" >&2; FAILURES=$((FAILURES + 1)); }

echo "==> Reading SSM parameters from '${SSM_PREFIX}/Sqs' (${AWS_REGION}, env=${ENV_NAME})..."

SSM_JSON="$(aws ssm get-parameters-by-path \
  --region "$AWS_REGION" \
  --path "${SSM_PREFIX}/Sqs" \
  --output json)"

get_sqs_ssm() {
  local suffix="$1"
  jq -r --arg name "${SSM_PREFIX}/Sqs/${suffix}" '.Parameters[]? | select(.Name == $name) | .Value' <<<"$SSM_JSON" | tr -d '\r\n'
}

MAIN_URL="$(get_sqs_ssm "AiJobsQueueUrl")"
MAIN_ARN="$(get_sqs_ssm "AiJobsQueueArn")"
DLQ_URL="$(get_sqs_ssm "AiJobsDlqUrl")"
DLQ_ARN="$(get_sqs_ssm "AiJobsDlqArn")"

# 1. All 4 SSM parameters exist
if [ -n "$MAIN_URL" ] && [ -n "$MAIN_ARN" ] && [ -n "$DLQ_URL" ] && [ -n "$DLQ_ARN" ]; then
  pass "All 4 SSM parameters exist under '${SSM_PREFIX}/Sqs/'"
else
  fail "Missing one or more required SSM parameters under '${SSM_PREFIX}/Sqs/' (AiJobsQueueUrl, AiJobsQueueArn, AiJobsDlqUrl, AiJobsDlqArn)"
  echo "SUMMARY: ${PASSED} passed, ${FAILURES} failed." >&2
  exit 1
fi

MAIN_ATTRS_JSON="$(aws sqs get-queue-attributes \
  --region "$AWS_REGION" \
  --queue-url "$MAIN_URL" \
  --attribute-names All \
  --output json)"

DLQ_ATTRS_JSON="$(aws sqs get-queue-attributes \
  --region "$AWS_REGION" \
  --queue-url "$DLQ_URL" \
  --attribute-names All \
  --output json)"

LIVE_MAIN_ARN="$(jq -r '.Attributes.QueueArn // empty' <<<"$MAIN_ATTRS_JSON" | tr -d '\r\n')"
LIVE_DLQ_ARN="$(jq -r '.Attributes.QueueArn // empty' <<<"$DLQ_ATTRS_JSON" | tr -d '\r\n')"

# 2. Main Queue ARN in SSM matches live SQS queue and expected queue name for this env
if [ "$LIVE_MAIN_ARN" = "$MAIN_ARN" ] && [[ "$LIVE_MAIN_ARN" == *":${MAIN_QUEUE_NAME}" ]]; then
  pass "Main Queue SSM ARN matches live SQS queue ':${MAIN_QUEUE_NAME}'"
else
  fail "Main Queue ARN mismatch (SSM vs live SQS or does not end with ':${MAIN_QUEUE_NAME}')"
fi

# 3. DLQ ARN in SSM matches live SQS DLQ and expected queue name for this env
if [ "$LIVE_DLQ_ARN" = "$DLQ_ARN" ] && [[ "$LIVE_DLQ_ARN" == *":${DLQ_NAME}" ]]; then
  pass "DLQ SSM ARN matches live SQS Dead-Letter Queue ':${DLQ_NAME}'"
else
  fail "DLQ ARN mismatch (SSM vs live SQS or does not end with ':${DLQ_NAME}')"
fi

# 4. Both queues are Standard queues (not FIFO)
MAIN_FIFO="$(jq -r '.Attributes.FifoQueue // "false"' <<<"$MAIN_ATTRS_JSON" | tr -d '\r\n')"
DLQ_FIFO="$(jq -r '.Attributes.FifoQueue // "false"' <<<"$DLQ_ATTRS_JSON" | tr -d '\r\n')"
if [ "$MAIN_FIFO" = "false" ] && [ "$DLQ_FIFO" = "false" ]; then
  pass "Both queues are Standard queues (matches worker idempotency + backoff design)"
else
  fail "Expected Standard queues, got FifoQueue main=${MAIN_FIFO}, dlq=${DLQ_FIFO}"
fi

# 5. RedrivePolicy points to DLQ with maxReceiveCount == 3
REDRIVE_RAW="$(jq -r '.Attributes.RedrivePolicy // empty' <<<"$MAIN_ATTRS_JSON" | tr -d '\r\n')"
REDRIVE_TARGET=""
REDRIVE_MAX=""
if [ -n "$REDRIVE_RAW" ]; then
  REDRIVE_TARGET="$(jq -r '.deadLetterTargetArn // empty' <<<"$REDRIVE_RAW" | tr -d '\r\n')"
  REDRIVE_MAX="$(jq -r '.maxReceiveCount // empty' <<<"$REDRIVE_RAW" | tr -d '\r\n')"
fi
if [ "$REDRIVE_TARGET" = "$LIVE_DLQ_ARN" ] && [ "$REDRIVE_MAX" = "3" ]; then
  pass "RedrivePolicy routes to DLQ with maxReceiveCount=3 (matches DB max_attempts=3)"
else
  fail "RedrivePolicy mismatch (expected target=DLQ and maxReceiveCount=3, got maxReceiveCount='${REDRIVE_MAX}')"
fi

# 6. Main Queue VisibilityTimeout >= 300s
VIS_TIMEOUT="$(jq -r '.Attributes.VisibilityTimeout // 0' <<<"$MAIN_ATTRS_JSON" | tr -d '\r\n')"
if [ "$VIS_TIMEOUT" -ge 300 ]; then
  pass "Main Queue VisibilityTimeout is ${VIS_TIMEOUT}s (>= 300s processing guardrail)"
else
  fail "Expected VisibilityTimeout >= 300s, got '${VIS_TIMEOUT}s'"
fi

# 7. Main Queue Long Polling == 20s (Free Tier request guardrail)
MAIN_WAIT="$(jq -r '.Attributes.ReceiveMessageWaitTimeSeconds // 0' <<<"$MAIN_ATTRS_JSON" | tr -d '\r\n')"
if [ "$MAIN_WAIT" -eq 20 ]; then
  pass "Main Queue Long Polling is enabled (ReceiveMessageWaitTimeSeconds=20s)"
else
  fail "Expected ReceiveMessageWaitTimeSeconds=20s for cost control, got '${MAIN_WAIT}s'"
fi

# 8. Main Queue MessageRetentionPeriod >= 4 days (345600s)
MAIN_RET="$(jq -r '.Attributes.MessageRetentionPeriod // 0' <<<"$MAIN_ATTRS_JSON" | tr -d '\r\n')"
if [ "$MAIN_RET" -ge 345600 ]; then
  pass "Main Queue MessageRetentionPeriod is ${MAIN_RET}s (>= 4 days)"
else
  fail "Expected Main Queue MessageRetentionPeriod >= 345600s, got '${MAIN_RET}s'"
fi

# 9. DLQ MessageRetentionPeriod == 14 days (1209600s)
DLQ_RET="$(jq -r '.Attributes.MessageRetentionPeriod // 0' <<<"$DLQ_ATTRS_JSON" | tr -d '\r\n')"
if [ "$DLQ_RET" -eq 1209600 ]; then
  pass "DLQ MessageRetentionPeriod is ${DLQ_RET}s (14 days for failure investigation)"
else
  fail "Expected DLQ MessageRetentionPeriod=1209600s (14 days), got '${DLQ_RET}s'"
fi

# 10. Main Queue SSE-SQS encryption enabled ($0)
MAIN_SSE="$(jq -r '.Attributes.SqsManagedSseEnabled // "false"' <<<"$MAIN_ATTRS_JSON" | tr -d '\r\n')"
if [ "$MAIN_SSE" = "true" ]; then
  pass "Main Queue server-side encryption is enabled (SqsManagedSseEnabled=true, \$0)"
else
  fail "Main Queue SqsManagedSseEnabled is '${MAIN_SSE}' (expected 'true')"
fi

# 11. DLQ SSE-SQS encryption enabled ($0)
DLQ_SSE="$(jq -r '.Attributes.SqsManagedSseEnabled // "false"' <<<"$DLQ_ATTRS_JSON" | tr -d '\r\n')"
if [ "$DLQ_SSE" = "true" ]; then
  pass "DLQ server-side encryption is enabled (SqsManagedSseEnabled=true, \$0)"
else
  fail "DLQ SqsManagedSseEnabled is '${DLQ_SSE}' (expected 'true')"
fi

# 12. Project tags present on both queues
MAIN_TAG_PROJ="$(aws sqs list-queue-tags --region "$AWS_REGION" --queue-url "$MAIN_URL" --query "Tags.Project" --output text 2>/dev/null | tr -d '\r\n' || echo "")"
DLQ_TAG_PROJ="$(aws sqs list-queue-tags --region "$AWS_REGION" --queue-url "$DLQ_URL" --query "Tags.Project" --output text 2>/dev/null | tr -d '\r\n' || echo "")"
if [ "$MAIN_TAG_PROJ" = "ai-exam-maker" ] && [ "$DLQ_TAG_PROJ" = "ai-exam-maker" ]; then
  pass "Both queues carry standard Project=ai-exam-maker tag"
else
  fail "Missing Project=ai-exam-maker tag (main='${MAIN_TAG_PROJ}', dlq='${DLQ_TAG_PROJ}')"
fi

# Informational DLQ depth check (helpful quick health summary)
DLQ_VISIBLE="$(jq -r '.Attributes.ApproximateNumberOfMessages // 0' <<<"$DLQ_ATTRS_JSON" | tr -d '\r\n')"
DLQ_INFLIGHT="$(jq -r '.Attributes.ApproximateNumberOfMessagesNotVisible // 0' <<<"$DLQ_ATTRS_JSON" | tr -d '\r\n')"
if [ "$DLQ_VISIBLE" -gt 0 ] || [ "$DLQ_INFLIGHT" -gt 0 ]; then
  echo "[WARNING] DLQ '${DLQ_NAME}' currently has messages (visible=${DLQ_VISIBLE}, in_flight=${DLQ_INFLIGHT}) — inspect per docs/runbooks/sqs.md §6."
else
  echo "[INFO] DLQ '${DLQ_NAME}' depth is 0 (visible=0, in_flight=0)."
fi

# Optional Smoke Test: send -> receive -> delete synthetic test message
if [ "$SMOKE_TEST" = "1" ]; then
  echo ""
  echo "==> Running SQS smoke test (ONLY safe before B6 deploy or while exammaker-worker is stopped)..."
  MAIN_VISIBLE="$(jq -r '.Attributes.ApproximateNumberOfMessages // 0' <<<"$MAIN_ATTRS_JSON" | tr -d '\r\n')"
  MAIN_INFLIGHT="$(jq -r '.Attributes.ApproximateNumberOfMessagesNotVisible // 0' <<<"$MAIN_ATTRS_JSON" | tr -d '\r\n')"
  if [ "$MAIN_VISIBLE" -gt 0 ] || [ "$MAIN_INFLIGHT" -gt 0 ]; then
    fail "Refusing SMOKE_TEST=1 while Main Queue has active messages (visible=${MAIN_VISIBLE}, in_flight=${MAIN_INFLIGHT})"
  else
    SMOKE_TOKEN="o11-smoke-$(date -u +%Y%m%d%H%M%S)"
    SMOKE_BODY="$(jq -nc --arg token "$SMOKE_TOKEN" '{smokeTest:true,token:$token}')"

    aws sqs send-message \
      --region "$AWS_REGION" \
      --queue-url "$MAIN_URL" \
      --message-body "$SMOKE_BODY" >/dev/null

    RECV_JSON="$(aws sqs receive-message \
      --region "$AWS_REGION" \
      --queue-url "$MAIN_URL" \
      --max-number-of-messages 1 \
      --wait-time-seconds 5 \
      --output json)"

    RECV_BODY="$(jq -r '.Messages[0].Body // empty' <<<"$RECV_JSON" | tr -d '\r\n')"
    RECV_HANDLE="$(jq -r '.Messages[0].ReceiptHandle // empty' <<<"$RECV_JSON" | tr -d '\r\n')"

    if [ -n "$RECV_HANDLE" ] && grep -q "$SMOKE_TOKEN" <<<"$RECV_BODY"; then
      aws sqs delete-message \
        --region "$AWS_REGION" \
        --queue-url "$MAIN_URL" \
        --receipt-handle "$RECV_HANDLE" >/dev/null
      pass "Smoke test succeeded: sent, received, and deleted synthetic message cleanly"
    else
      if [ -n "$RECV_HANDLE" ]; then
        aws sqs change-message-visibility \
          --region "$AWS_REGION" \
          --queue-url "$MAIN_URL" \
          --receipt-handle "$RECV_HANDLE" \
          --visibility-timeout 0 >/dev/null 2>&1 || true
      fi
      fail "Smoke test failed: could not receive matching test message (token=${SMOKE_TOKEN})"
    fi
  fi
fi

echo ""
if [ "$FAILURES" -eq 0 ]; then
  echo "ALL CHECKS PASSED (${PASSED}/${PASSED})."
  exit 0
else
  echo "VERIFICATION FAILED: ${PASSED} passed, ${FAILURES} failed." >&2
  exit 1
fi
