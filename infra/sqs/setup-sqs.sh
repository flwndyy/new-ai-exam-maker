#!/usr/bin/env bash
# Idempotent setup script for Amazon SQS + Dead-Letter Queue (AI Exam Maker - Task O11).
# Creates (if absent) and reconciles attributes, RedrivePolicy, RedriveAllowPolicy, tags, and SSM entries for:
#   - Dead-Letter Queue (Standard): exammaker-${ENV_NAME}-ai-jobs-dlq (14-day retention, SSE-SQS = $0,
#     RedriveAllowPolicy restricted to Main Queue ARN)
#   - Main Queue (Standard): exammaker-${ENV_NAME}-ai-jobs (VisibilityTimeout=300s, Long Polling=20s,
#     RedrivePolicy maxReceiveCount=3 -> DLQ, SSE-SQS = $0)
#   - SSM Parameter Store entries under ${SSM_PREFIX}/Sqs/*:
#       AiJobsQueueUrl (bound by backend Sqs:AiJobsQueueUrl), AiJobsQueueArn, AiJobsDlqUrl, AiJobsDlqArn
#
# Note: Run ./infra/sqs/verify-sqs.sh after setup to verify all compliance checks.
#
# Dependencies: aws (CLI v2), jq (both pre-installed in AWS CloudShell).
#
# Usage (AWS CloudShell, Linux, macOS, or Git Bash with jq):
#   ./infra/sqs/setup-sqs.sh

set -euo pipefail

# Prevent Git Bash (MSYS2) on Windows from rewriting SSM paths starting with '/' into Windows paths.
export MSYS_NO_PATHCONV=1
export MSYS2_ARG_CONV_EXCL="*"

AWS_REGION="${AWS_REGION:-ap-southeast-1}"
ENV_NAME="${ENV_NAME:-dev}"
SSM_PREFIX="${SSM_PREFIX:-/exammaker/${ENV_NAME}}"

# Queue names and attributes (docs/context.md §4.1.1, §5.2, §6)
DLQ_NAME="${DLQ_NAME:-exammaker-${ENV_NAME}-ai-jobs-dlq}"
MAIN_QUEUE_NAME="${MAIN_QUEUE_NAME:-exammaker-${ENV_NAME}-ai-jobs}"
VISIBILITY_TIMEOUT="${VISIBILITY_TIMEOUT:-300}"
LONG_POLL_SECONDS="${LONG_POLL_SECONDS:-20}"
MAIN_RETENTION_SECONDS="${MAIN_RETENTION_SECONDS:-345600}"   # 4 days
DLQ_RETENTION_SECONDS="${DLQ_RETENTION_SECONDS:-1209600}"    # 14 days

# Fixed to 3 to stay strictly aligned with backend AiQuota:MaxAttempts=3 (docs/context.md §4.1.1).
readonly MAX_RECEIVE_COUNT=3

command -v aws >/dev/null 2>&1 || { echo "ERROR: aws CLI is required." >&2; exit 1; }
command -v jq >/dev/null 2>&1 || { echo "ERROR: jq is required (pre-installed in AWS CloudShell; on Windows install via 'winget install jqlang.jq')." >&2; exit 1; }

if [[ "$MAIN_QUEUE_NAME" == *.fifo ]] || [[ "$DLQ_NAME" == *.fifo ]]; then
  echo "ERROR: Standard queues (not .fifo) are required by docs/context.md §4.1.1." >&2
  exit 1
fi

get_queue_url_safe() {
  local qname="$1"
  local out=""
  local rc=0
  out="$(aws sqs get-queue-url --region "$AWS_REGION" --queue-name "$qname" --output json 2>&1)" || rc=$?
  if [ "$rc" -eq 0 ]; then
    jq -r '.QueueUrl // empty' <<<"$out" | tr -d '\r\n'
    return 0
  fi

  if grep -Eq "NonExistentQueue|QueueDoesNotExist" <<<"$out"; then
    echo ""
    return 0
  fi

  echo "ERROR: Failed to inspect SQS queue '${qname}': ${out}" >&2
  return 1
}

get_queue_arn() {
  local qurl="$1"
  aws sqs get-queue-attributes \
    --region "$AWS_REGION" \
    --queue-url "$qurl" \
    --attribute-names QueueArn \
    --query "Attributes.QueueArn" \
    --output text | tr -d '\r\n'
}

put_string_param() {
  local name="$1"
  local value="$2"
  local desc="$3"
  aws ssm put-parameter \
    --region "$AWS_REGION" \
    --name "$name" \
    --type String \
    --value "$value" \
    --description "$desc" \
    --overwrite >/dev/null
  echo "    Stored SSM String parameter: ${name}"
}

echo "==> Region: ${AWS_REGION} | Env: ${ENV_NAME} | SSM Prefix: ${SSM_PREFIX}"

# 1. Create or reconcile Dead-Letter Queue (DLQ)
DLQ_ATTRS="$(jq -nc \
  --arg ret "$DLQ_RETENTION_SECONDS" \
  --arg wait "$LONG_POLL_SECONDS" \
  '{
    MessageRetentionPeriod: $ret,
    ReceiveMessageWaitTimeSeconds: $wait,
    SqsManagedSseEnabled: "true"
  }')"

DLQ_URL="$(get_queue_url_safe "$DLQ_NAME")"
if [ -n "$DLQ_URL" ]; then
  echo "==> Reusing existing SQS DLQ '${DLQ_NAME}' — reconciling attributes and tags..."
  aws sqs set-queue-attributes \
    --region "$AWS_REGION" \
    --queue-url "$DLQ_URL" \
    --attributes "$DLQ_ATTRS" >/dev/null
  aws sqs tag-queue \
    --region "$AWS_REGION" \
    --queue-url "$DLQ_URL" \
    --tags "Project=ai-exam-maker,Environment=${ENV_NAME},Task=O11,Role=dlq" >/dev/null
else
  echo "==> Creating SQS DLQ '${DLQ_NAME}' (retention=${DLQ_RETENTION_SECONDS}s, long-poll=${LONG_POLL_SECONDS}s, SSE-SQS)..."
  DLQ_URL="$(aws sqs create-queue \
    --region "$AWS_REGION" \
    --queue-name "$DLQ_NAME" \
    --attributes "$DLQ_ATTRS" \
    --tags "Project=ai-exam-maker,Environment=${ENV_NAME},Task=O11,Role=dlq" \
    --query "QueueUrl" \
    --output text | tr -d '\r\n')"
fi

DLQ_ARN="$(get_queue_arn "$DLQ_URL")"
if [ -z "$DLQ_ARN" ] || [ "$DLQ_ARN" = "None" ]; then
  echo "ERROR: Failed to resolve QueueArn for DLQ '${DLQ_NAME}'." >&2
  exit 1
fi

# 2. Create or reconcile Main Queue with RedrivePolicy pointing to DLQ
REDRIVE_POLICY="$(jq -nc \
  --arg dlqArn "$DLQ_ARN" \
  --arg maxRecv "$MAX_RECEIVE_COUNT" \
  '{
    deadLetterTargetArn: $dlqArn,
    maxReceiveCount: $maxRecv
  }')"
MAIN_ATTRS="$(jq -nc \
  --arg vis "$VISIBILITY_TIMEOUT" \
  --arg wait "$LONG_POLL_SECONDS" \
  --arg ret "$MAIN_RETENTION_SECONDS" \
  --arg redrive "$REDRIVE_POLICY" \
  '{
    VisibilityTimeout: $vis,
    ReceiveMessageWaitTimeSeconds: $wait,
    MessageRetentionPeriod: $ret,
    SqsManagedSseEnabled: "true",
    RedrivePolicy: $redrive
  }')"

MAIN_QUEUE_URL="$(get_queue_url_safe "$MAIN_QUEUE_NAME")"
if [ -n "$MAIN_QUEUE_URL" ]; then
  echo "==> Reusing existing SQS Main Queue '${MAIN_QUEUE_NAME}' — reconciling attributes, RedrivePolicy, and tags..."
  aws sqs set-queue-attributes \
    --region "$AWS_REGION" \
    --queue-url "$MAIN_QUEUE_URL" \
    --attributes "$MAIN_ATTRS" >/dev/null
  aws sqs tag-queue \
    --region "$AWS_REGION" \
    --queue-url "$MAIN_QUEUE_URL" \
    --tags "Project=ai-exam-maker,Environment=${ENV_NAME},Task=O11,Role=main" >/dev/null
else
  echo "==> Creating SQS Main Queue '${MAIN_QUEUE_NAME}' (visibility=${VISIBILITY_TIMEOUT}s, maxReceiveCount=${MAX_RECEIVE_COUNT}, long-poll=${LONG_POLL_SECONDS}s, SSE-SQS)..."
  MAIN_QUEUE_URL="$(aws sqs create-queue \
    --region "$AWS_REGION" \
    --queue-name "$MAIN_QUEUE_NAME" \
    --attributes "$MAIN_ATTRS" \
    --tags "Project=ai-exam-maker,Environment=${ENV_NAME},Task=O11,Role=main" \
    --query "QueueUrl" \
    --output text | tr -d '\r\n')"
fi

MAIN_QUEUE_ARN="$(get_queue_arn "$MAIN_QUEUE_URL")"
if [ -z "$MAIN_QUEUE_ARN" ] || [ "$MAIN_QUEUE_ARN" = "None" ]; then
  echo "ERROR: Failed to resolve QueueArn for Main Queue '${MAIN_QUEUE_NAME}'." >&2
  exit 1
fi

# Restrict DLQ RedriveAllowPolicy so only this Main Queue can use it as a dead-letter target
REDRIVE_ALLOW_POLICY="$(jq -nc --arg mainArn "$MAIN_QUEUE_ARN" '{redrivePermission:"byQueue",sourceQueueArns:[$mainArn]}')"
DLQ_ALLOW_ATTRS="$(jq -nc --arg allow "$REDRIVE_ALLOW_POLICY" '{RedriveAllowPolicy:$allow}')"
aws sqs set-queue-attributes \
  --region "$AWS_REGION" \
  --queue-url "$DLQ_URL" \
  --attributes "$DLQ_ALLOW_ATTRS" >/dev/null
echo "==> Applied RedriveAllowPolicy on DLQ '${DLQ_NAME}' (byQueue -> '${MAIN_QUEUE_NAME}' only)."

# 3. Publish SQS metadata to SSM Parameter Store (Standard tier = $0)
echo "==> Writing SQS parameters to SSM Parameter Store under '${SSM_PREFIX}/Sqs/'..."
put_string_param "${SSM_PREFIX}/Sqs/AiJobsQueueUrl" "$MAIN_QUEUE_URL" "SQS Main Queue URL for AI generation jobs (Sqs:AiJobsQueueUrl)"
put_string_param "${SSM_PREFIX}/Sqs/AiJobsQueueArn" "$MAIN_QUEUE_ARN" "SQS Main Queue ARN for EC2 IAM policy"
put_string_param "${SSM_PREFIX}/Sqs/AiJobsDlqUrl" "$DLQ_URL" "SQS Dead-Letter Queue URL for failed AI jobs"
put_string_param "${SSM_PREFIX}/Sqs/AiJobsDlqArn" "$DLQ_ARN" "SQS Dead-Letter Queue ARN for CloudWatch alarms"

echo ""
echo "DONE: Amazon SQS Main Queue, DLQ, and SSM parameters are ready."
echo "Backend key '${SSM_PREFIX}/Sqs/AiJobsQueueUrl' maps directly to .NET 'Sqs:AiJobsQueueUrl'."
echo "Next step: run './infra/sqs/verify-sqs.sh' to verify all 12 architecture, security, and cost checks."
