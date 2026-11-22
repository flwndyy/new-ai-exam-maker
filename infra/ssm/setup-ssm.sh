#!/usr/bin/env bash
# Create-if-absent setup script for AWS Systems Manager Parameter Store (AI Exam Maker - Task O12).
# Publishes shared application, Bedrock, AI quota, rate-limit, CloudWatch, and origin secret
# parameters under ${SSM_PREFIX}/* using Standard tier ($0) and AWS-managed KMS key alias/aws/ssm ($0).
#
# Prerequisite parameters created by earlier tasks (not modified here):
#   - O9  (Cognito): ${SSM_PREFIX}/Cognito/{Region,UserPoolId,ClientId,HostedUiDomain,Authority}
#   - O10 (RDS):     ${SSM_PREFIX}/ConnectionStrings/ExamMaker, ${SSM_PREFIX}/Rds/*
#   - O11 (SQS):     ${SSM_PREFIX}/Sqs/{AiJobsQueueUrl,AiJobsQueueArn,AiJobsDlqUrl,AiJobsDlqArn}
#
# Safety behavior:
#   - Non-secret config parameters are create-if-absent by default so live demo overrides
#     (e.g. switching ModelId from Haiku 4.5 to Sonnet 4.6) are preserved on re-run.
#     Set OVERWRITE_CONFIG=1 to reset non-secret parameters to script defaults.
#   - Origin/VerifySecret is NEVER overwritten unless ORIGIN_VERIFY_SECRET is provided
#     or ROTATE_ORIGIN_SECRET=1 is set explicitly (prevents breaking CloudFront <-> Nginx 403).
#
# Dependencies: aws (CLI v2), jq, openssl (or python3 for random hex fallback).
#
# Usage (AWS CloudShell, Linux, macOS, or Git Bash with jq):
#   ./infra/ssm/setup-ssm.sh
#   OVERWRITE_CONFIG=1 ./infra/ssm/setup-ssm.sh
#   ROTATE_ORIGIN_SECRET=1 ./infra/ssm/setup-ssm.sh

set -euo pipefail

# Prevent Git Bash (MSYS2) on Windows from rewriting SSM paths starting with '/' into Windows paths.
export MSYS_NO_PATHCONV=1
export MSYS2_ARG_CONV_EXCL="*"

AWS_REGION="${AWS_REGION:-ap-southeast-1}"
ENV_NAME="${ENV_NAME:-dev}"
SSM_PREFIX="${SSM_PREFIX:-/exammaker/${ENV_NAME}}"

OVERWRITE_CONFIG="${OVERWRITE_CONFIG:-0}"
ROTATE_ORIGIN_SECRET="${ROTATE_ORIGIN_SECRET:-0}"
ORIGIN_VERIFY_SECRET="${ORIGIN_VERIFY_SECRET:-}"

# Bedrock defaults (docs/context.md §6, §14 & docs/aws-o3-ec2-ssm.md §2.1)
# Verified against `aws bedrock list-inference-profiles` & `get-foundation-model` in ap-southeast-1:
# Claude 3 Haiku and Claude 3.5 Sonnet v2 foundation models have reached EOL;
# Claude Haiku 4.5 and Claude Sonnet 4.6 global inference profiles are ACTIVE.
BEDROCK_REGION="${BEDROCK_REGION:-ap-southeast-1}"
BEDROCK_DEV_MODEL_ID="${BEDROCK_DEV_MODEL_ID:-global.anthropic.claude-haiku-4-5-20251001-v1:0}"
BEDROCK_MODEL_ID="${BEDROCK_MODEL_ID:-$BEDROCK_DEV_MODEL_ID}"
BEDROCK_DEMO_MODEL_ID="${BEDROCK_DEMO_MODEL_ID:-global.anthropic.claude-sonnet-4-6}"
BEDROCK_ENABLE_VERIFICATION="${BEDROCK_ENABLE_VERIFICATION:-false}"

# AI Quota defaults (exact match with ExamMaker.Application.Ai.AiQuotaOptions)
AI_JOBS_PER_USER_PER_DAY="${AI_JOBS_PER_USER_PER_DAY:-10}"
AI_MAX_QUESTIONS_PER_JOB="${AI_MAX_QUESTIONS_PER_JOB:-10}"
AI_TOKENS_PER_USER_PER_DAY="${AI_TOKENS_PER_USER_PER_DAY:-100000}"
AI_SYSTEM_TOKENS_PER_DAY="${AI_SYSTEM_TOKENS_PER_DAY:-500000}"
AI_MAX_NOTE_LENGTH="${AI_MAX_NOTE_LENGTH:-1000}"
AI_MAX_ATTEMPTS="${AI_MAX_ATTEMPTS:-3}"

# AI Rate Limit defaults (exact match with ExamMaker.Api.Ai.AiRateLimitOptions)
AI_RATE_PERMIT_LIMIT="${AI_RATE_PERMIT_LIMIT:-5}"
AI_RATE_WINDOW="${AI_RATE_WINDOW:-00:01:00}"

# CloudWatch metadata defaults (used by O3 IAM policy & O13 CloudWatch)
CW_LOG_GROUP_NAME="${CW_LOG_GROUP_NAME:-/exammaker/${ENV_NAME}/app}"
CW_METRIC_NAMESPACE="${CW_METRIC_NAMESPACE:-ExamMaker/$(echo "${ENV_NAME:0:1}" | tr '[:lower:]' '[:upper:]')${ENV_NAME:1}}"

command -v aws >/dev/null 2>&1 || { echo "ERROR: aws CLI is required." >&2; exit 1; }
command -v jq >/dev/null 2>&1 || { echo "ERROR: jq is required (pre-installed in AWS CloudShell; on Windows install via 'winget install jqlang.jq')." >&2; exit 1; }

if [ "$BEDROCK_ENABLE_VERIFICATION" != "true" ] && [ "$BEDROCK_ENABLE_VERIFICATION" != "false" ]; then
  echo "ERROR: BEDROCK_ENABLE_VERIFICATION must be 'true' or 'false' (got '${BEDROCK_ENABLE_VERIFICATION}')." >&2
  exit 1
fi

if [ "$BEDROCK_MODEL_ID" != "$BEDROCK_DEV_MODEL_ID" ] && [ "$BEDROCK_MODEL_ID" != "$BEDROCK_DEMO_MODEL_ID" ]; then
  echo "ERROR: BEDROCK_MODEL_ID ('${BEDROCK_MODEL_ID}') must match BEDROCK_DEV_MODEL_ID ('${BEDROCK_DEV_MODEL_ID}') or BEDROCK_DEMO_MODEL_ID ('${BEDROCK_DEMO_MODEL_ID}')." >&2
  exit 1
fi

# Validate AI Quota defaults / overrides (must match ExamMaker.Application.Ai.AiQuotaOptions & SQS maxReceiveCount=3)
for quota_var in "AI_JOBS_PER_USER_PER_DAY:${AI_JOBS_PER_USER_PER_DAY}" \
                 "AI_MAX_QUESTIONS_PER_JOB:${AI_MAX_QUESTIONS_PER_JOB}" \
                 "AI_TOKENS_PER_USER_PER_DAY:${AI_TOKENS_PER_USER_PER_DAY}" \
                 "AI_SYSTEM_TOKENS_PER_DAY:${AI_SYSTEM_TOKENS_PER_DAY}" \
                 "AI_MAX_NOTE_LENGTH:${AI_MAX_NOTE_LENGTH}"; do
  var_name="${quota_var%%:*}"
  var_val="${quota_var#*:}"
  if [[ ! "$var_val" =~ ^[1-9][0-9]*$ ]]; then
    echo "ERROR: ${var_name} must be a positive integer (got '${var_val}')." >&2
    exit 1
  fi
done

if [ "$AI_MAX_ATTEMPTS" != "3" ]; then
  echo "ERROR: AI_MAX_ATTEMPTS must be 3 to match SQS worker retry and DLQ maxReceiveCount=3 (got '${AI_MAX_ATTEMPTS}')." >&2
  exit 1
fi

if [ "$AI_SYSTEM_TOKENS_PER_DAY" -lt "$AI_TOKENS_PER_USER_PER_DAY" ]; then
  echo "ERROR: AI_SYSTEM_TOKENS_PER_DAY (${AI_SYSTEM_TOKENS_PER_DAY}) must be >= AI_TOKENS_PER_USER_PER_DAY (${AI_TOKENS_PER_USER_PER_DAY})." >&2
  exit 1
fi

# Validate AI Rate Limit defaults / overrides (must match ExamMaker.Api.Ai.AiRateLimitOptions & .NET TimeSpan)
if [[ ! "$AI_RATE_PERMIT_LIMIT" =~ ^[1-9][0-9]*$ ]]; then
  echo "ERROR: AI_RATE_PERMIT_LIMIT must be a positive integer (got '${AI_RATE_PERMIT_LIMIT}')." >&2
  exit 1
fi

if [[ ! "$AI_RATE_WINDOW" =~ ^([0-9]+\.)?(0?[0-9]|1[0-9]|2[0-3]):[0-5][0-9]:[0-5][0-9](\.[0-9]{1,7})?$ ]]; then
  echo "ERROR: AI_RATE_WINDOW ('${AI_RATE_WINDOW}') is not a valid .NET TimeSpan format (expected 'hh:mm:ss' or '[d.]hh:mm:ss[.fffffff]')." >&2
  exit 1
fi

AI_RATE_WINDOW_NONZERO="${AI_RATE_WINDOW//[0.:]/}"
if [ -z "$AI_RATE_WINDOW_NONZERO" ]; then
  echo "ERROR: AI_RATE_WINDOW ('${AI_RATE_WINDOW}') cannot be zero duration." >&2
  exit 1
fi

TMP_JSON_FILE=""
cleanup_tmp_json() {
  if [ -n "${TMP_JSON_FILE:-}" ]; then
    rm -f "$TMP_JSON_FILE"
  fi
}
trap cleanup_tmp_json EXIT

generate_origin_secret() {
  if command -v openssl >/dev/null 2>&1; then
    openssl rand -hex 32 | tr -d '\r\n'
  elif command -v python3 >/dev/null 2>&1; then
    python3 -c "import secrets; print(secrets.token_hex(32))" | tr -d '\r\n'
  else
    echo "ERROR: openssl or python3 is required to generate a secure random Origin/VerifySecret." >&2
    return 1
  fi
}

# Returns "exists" if the parameter exists, "missing" only on ParameterNotFound,
# and aborts with the real AWS error on any other failure (e.g. AccessDenied, Throttling).
check_ssm_param_status() {
  local name="$1"
  local out=""
  local rc=0
  out="$(aws ssm get-parameter --region "$AWS_REGION" --name "$name" --query "Parameter.Name" --output text 2>&1)" || rc=$?
  if [ "$rc" -eq 0 ]; then
    echo "exists"
    return 0
  fi

  if grep -q "ParameterNotFound" <<<"$out"; then
    echo "missing"
    return 0
  fi

  echo "ERROR: Failed to inspect SSM parameter '${name}': ${out}" >&2
  return 1
}

put_string_param() {
  local name="$1"
  local value="$2"
  local desc="$3"
  local status=""
  status="$(check_ssm_param_status "$name")"

  if [ "$status" = "exists" ]; then
    if [ "$OVERWRITE_CONFIG" = "1" ]; then
      aws ssm put-parameter \
        --region "$AWS_REGION" \
        --name "$name" \
        --type String \
        --tier Standard \
        --value "$value" \
        --description "$desc" \
        --overwrite >/dev/null
      echo "    [UPDATED]  ${name} = ${value}"
    else
      echo "    [KEPT]     ${name} (already exists; set OVERWRITE_CONFIG=1 to overwrite)"
    fi
  else
    aws ssm put-parameter \
      --region "$AWS_REGION" \
      --name "$name" \
      --type String \
      --tier Standard \
      --value "$value" \
      --description "$desc" \
      --tags "Key=Project,Value=ai-exam-maker" "Key=Environment,Value=${ENV_NAME}" "Key=Task,Value=O12" >/dev/null
    echo "    [CREATED]  ${name} = ${value}"
  fi
}

put_origin_secret_param() {
  local name="${SSM_PREFIX}/Origin/VerifySecret"
  local desc="Shared secret for CloudFront X-Origin-Verify header and Nginx origin-secret.conf (O4/O5/O12)"
  local status=""
  status="$(check_ssm_param_status "$name")"

  if [ "$status" = "exists" ] && [ -z "$ORIGIN_VERIFY_SECRET" ] && [ "$ROTATE_ORIGIN_SECRET" != "1" ]; then
    echo "    [KEPT]     ${name} (SecureString already exists; preserving CloudFront <-> Nginx secret)"
    return 0
  fi

  local secret_val="$ORIGIN_VERIFY_SECRET"
  if [ -z "$secret_val" ]; then
    secret_val="$(generate_origin_secret)"
  fi

  if [ "${#secret_val}" -lt 32 ] || [[ ! "$secret_val" =~ ^[A-Za-z0-9][A-Za-z0-9._-]+$ ]]; then
    echo "ERROR: Origin/VerifySecret must be at least 32 chars, start with [A-Za-z0-9], and contain only [A-Za-z0-9._-]." >&2
    exit 1
  fi

  # Write parameter payload via temporary file (0600) so the secret is never exposed in process argv (`ps`).
  # Convert POSIX path via `cygpath -m` on Git Bash (Windows) because MSYS_NO_PATHCONV=1 disables auto-conversion for aws.exe.
  TMP_JSON_FILE="$(mktemp)"
  chmod 600 "$TMP_JSON_FILE"
  local param_file="$TMP_JSON_FILE"
  if command -v cygpath >/dev/null 2>&1; then
    param_file="$(cygpath -m "$TMP_JSON_FILE")"
  fi

  if [ "$status" = "exists" ]; then
    local prev_ver=""
    prev_ver="$(aws ssm get-parameter --region "$AWS_REGION" --name "$name" --query "Parameter.Version" --output text)"
    jq -nc \
      --arg name "$name" \
      --arg val "$secret_val" \
      --arg desc "$desc" \
      '{
        Name: $name,
        Type: "SecureString",
        Tier: "Standard",
        KeyId: "alias/aws/ssm",
        Value: $val,
        Description: $desc,
        Overwrite: true
      }' >"$TMP_JSON_FILE"
    local put_ver=""
    put_ver="$(aws ssm put-parameter \
      --region "$AWS_REGION" \
      --cli-input-json "file://${param_file}" \
      --query "Version" \
      --output text)"
    rm -f "$TMP_JSON_FILE"
    TMP_JSON_FILE=""
    echo "    [ROTATED]  ${name} (SecureString previous version ${prev_ver} -> new version ${put_ver} via alias/aws/ssm — follow 3-step rotation in runbook!)"
  else
    jq -nc \
      --arg name "$name" \
      --arg val "$secret_val" \
      --arg desc "$desc" \
      --arg env "$ENV_NAME" \
      '{
        Name: $name,
        Type: "SecureString",
        Tier: "Standard",
        KeyId: "alias/aws/ssm",
        Value: $val,
        Description: $desc,
        Tags: [
          {Key: "Project", Value: "ai-exam-maker"},
          {Key: "Environment", Value: $env},
          {Key: "Task", Value: "O12"}
        ]
      }' >"$TMP_JSON_FILE"
    local put_ver=""
    put_ver="$(aws ssm put-parameter \
      --region "$AWS_REGION" \
      --cli-input-json "file://${param_file}" \
      --query "Version" \
      --output text)"
    rm -f "$TMP_JSON_FILE"
    TMP_JSON_FILE=""
    echo "    [CREATED]  ${name} (SecureString version ${put_ver} via alias/aws/ssm; value not printed)"
  fi
}

echo "==> Region: ${AWS_REGION} | Env: ${ENV_NAME} | SSM Prefix: ${SSM_PREFIX}"
echo "==> Mode: OVERWRITE_CONFIG=${OVERWRITE_CONFIG}, ROTATE_ORIGIN_SECRET=${ROTATE_ORIGIN_SECRET}"

echo ""
echo "==> 1. AWS Core parameters..."
put_string_param "${SSM_PREFIX}/AWS/Region" "$AWS_REGION" "Default AWS region for SDK clients (AWS:Region)"

echo ""
echo "==> 2. Amazon Bedrock parameters..."
put_string_param "${SSM_PREFIX}/Bedrock/Region" "$BEDROCK_REGION" "AWS region for Amazon Bedrock invocation (Bedrock:Region)"
put_string_param "${SSM_PREFIX}/Bedrock/ModelId" "$BEDROCK_MODEL_ID" "Active Bedrock model/inference profile ID (Claude Haiku 4.5 for dev, Claude Sonnet 4.6 for demo)"
put_string_param "${SSM_PREFIX}/Bedrock/DevModelId" "$BEDROCK_DEV_MODEL_ID" "Target Bedrock model/inference profile ID for dev/cost-saving rollback (Claude Haiku 4.5)"
put_string_param "${SSM_PREFIX}/Bedrock/DemoModelId" "$BEDROCK_DEMO_MODEL_ID" "Target Bedrock model/inference profile ID for demo (Claude Sonnet 4.6)"
put_string_param "${SSM_PREFIX}/Bedrock/EnableAnswerVerification" "$BEDROCK_ENABLE_VERIFICATION" "Toggle optional AI answer verification pass (false for dev, true for demo)"

echo ""
echo "==> 3. AI Quota parameters (bound to AiQuotaOptions)..."
put_string_param "${SSM_PREFIX}/AiQuota/JobsPerUserPerDay" "$AI_JOBS_PER_USER_PER_DAY" "Maximum AI generation jobs per user per UTC day"
put_string_param "${SSM_PREFIX}/AiQuota/MaxQuestionsPerJob" "$AI_MAX_QUESTIONS_PER_JOB" "Maximum questions requested per single AI job"
put_string_param "${SSM_PREFIX}/AiQuota/TokensPerUserPerDay" "$AI_TOKENS_PER_USER_PER_DAY" "Maximum input+output tokens per user per UTC day"
put_string_param "${SSM_PREFIX}/AiQuota/SystemTokensPerDay" "$AI_SYSTEM_TOKENS_PER_DAY" "System-wide token ceiling per UTC day"
put_string_param "${SSM_PREFIX}/AiQuota/MaxNoteLength" "$AI_MAX_NOTE_LENGTH" "Maximum character length of optional teacher note in AI prompt"
put_string_param "${SSM_PREFIX}/AiQuota/MaxAttempts" "$AI_MAX_ATTEMPTS" "Maximum worker attempts per AI job (must match SQS maxReceiveCount=3)"

echo ""
echo "==> 4. AI Rate Limit parameters (bound to AiRateLimitOptions)..."
put_string_param "${SSM_PREFIX}/AiRateLimit/PermitLimit" "$AI_RATE_PERMIT_LIMIT" "Per-user burst permit limit on POST /api/v1/ai/generate"
put_string_param "${SSM_PREFIX}/AiRateLimit/Window" "$AI_RATE_WINDOW" "Fixed-window TimeSpan for per-user AI rate limiter (hh:mm:ss)"

echo ""
echo "==> 5. Origin Verify Secret (CloudFront <-> Nginx)..."
put_origin_secret_param

echo ""
echo "==> 6. CloudWatch Observability metadata..."
put_string_param "${SSM_PREFIX}/CloudWatch/LogGroupName" "$CW_LOG_GROUP_NAME" "CloudWatch Logs group name for API and Worker"
put_string_param "${SSM_PREFIX}/CloudWatch/MetricNamespace" "$CW_METRIC_NAMESPACE" "CloudWatch custom metrics namespace for ExamMaker"

echo ""
echo "DONE: Parameter Store setup (O12) completed under '${SSM_PREFIX}/'."
echo "Next step: run './infra/ssm/verify-ssm.sh' to verify all parameters across O9-O12, encryption, and \$0 cost tier."
