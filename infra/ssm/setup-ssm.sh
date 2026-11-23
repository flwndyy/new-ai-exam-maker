#!/usr/bin/env bash
# Create-if-absent setup script for AWS Systems Manager Parameter Store (AI Exam Maker - Task O12).
# Publishes shared application, Bedrock, AI generation, AI quota, rate-limit, and CloudWatch
# parameters under ${SSM_PREFIX}/* (bound by .NET AddSystemsManager) and ops-only secrets
# (Origin/VerifySecret) under ${SSM_OPS_PREFIX}/* (isolated from EC2 app IConfiguration)
# using Standard tier ($0) and AWS-managed KMS key alias/aws/ssm ($0).
#
# Prerequisite parameters created by earlier tasks (not modified here, except migrating Rds/MasterPassword to ${SSM_OPS_PREFIX}):
#   - O9  (Cognito): ${SSM_PREFIX}/Cognito/{Region,UserPoolId,ClientId,HostedUiDomain,Authority}
#   - O10 (RDS):     ${SSM_PREFIX}/ConnectionStrings/ExamMaker, ${SSM_PREFIX}/Rds/{InstanceId,Endpoint,Port,Database,Username}
#   - O11 (SQS):     ${SSM_PREFIX}/Sqs/{AiJobsQueueUrl,AiJobsQueueArn,AiJobsDlqUrl,AiJobsDlqArn}
#
# Safety behavior:
#   - Non-secret config parameters are create-if-absent by default so live demo overrides
#     (e.g. switching ModelId from Haiku 4.5 to Sonnet 4.6) are preserved on re-run.
#   - Set OVERWRITE_KEYS="Key1,Key2" (e.g. OVERWRITE_KEYS="AiQuota/JobsPerUserPerDay")
#     to update specific parameter(s) without resetting anything else.
#   - Set OVERWRITE_CONFIG=1 to reset non-secret parameters to script defaults; the Bedrock model
#     triplet (Bedrock/ModelId, Bedrock/DevModelId, Bedrock/DemoModelId) and AiGeneration/VerifyAnswers
#     are STILL protected together from bulk reset unless OVERWRITE_BEDROCK_RUNTIME=1 or OVERWRITE_KEYS
#     explicitly includes them (preserving the ModelId in {DevModelId, DemoModelId} invariant).
#   - ${SSM_OPS_PREFIX}/Origin/VerifySecret is NEVER overwritten when it already exists unless
#     ROTATE_ORIGIN_SECRET=1 is explicitly set (even if ORIGIN_VERIFY_SECRET or ORIGIN_VERIFY_SECRET_STDIN=1
#     is present in the environment, preventing accidental silent rotation on re-runs). Only pass
#     ORIGIN_VERIFY_SECRET in a non-shared shell to avoid shell history exposure.
#
# Dependencies: aws (CLI v2), jq, openssl (or python3 for random hex fallback).
#
# Usage (AWS CloudShell, Linux, macOS, or Git Bash with jq):
#   ./infra/ssm/setup-ssm.sh
#   OVERWRITE_KEYS="AiQuota/JobsPerUserPerDay" AI_JOBS_PER_USER_PER_DAY=15 ./infra/ssm/setup-ssm.sh
#   OVERWRITE_CONFIG=1 ./infra/ssm/setup-ssm.sh
#   ROTATE_ORIGIN_SECRET=1 ./infra/ssm/setup-ssm.sh

set -euo pipefail

# Prevent Git Bash (MSYS2) on Windows from rewriting SSM paths starting with '/' into Windows paths.
export MSYS_NO_PATHCONV=1
export MSYS2_ARG_CONV_EXCL="*"

AWS_REGION="${AWS_REGION:-ap-southeast-1}"
ENV_NAME="${ENV_NAME:-dev}"
SSM_PREFIX="${SSM_PREFIX:-/exammaker/${ENV_NAME}}"
SSM_OPS_PREFIX="${SSM_OPS_PREFIX:-/exammaker-ops/${ENV_NAME}}"

OVERWRITE_CONFIG="${OVERWRITE_CONFIG:-0}"
OVERWRITE_BEDROCK_RUNTIME="${OVERWRITE_BEDROCK_RUNTIME:-0}"
OVERWRITE_KEYS="${OVERWRITE_KEYS:-}"
ROTATE_ORIGIN_SECRET="${ROTATE_ORIGIN_SECRET:-0}"
ORIGIN_VERIFY_SECRET="${ORIGIN_VERIFY_SECRET:-}"
ORIGIN_VERIFY_SECRET_STDIN="${ORIGIN_VERIFY_SECRET_STDIN:-0}"

# Bedrock & AI Generation defaults (docs/context.md §4.1.2, §6, §14 & docs/aws-o3-ec2-ssm.md §2.1)
# Verified against `aws bedrock list-inference-profiles` & `get-foundation-model` in ap-southeast-1:
# Claude 3 Haiku and Claude 3.5 Sonnet v2 foundation models have reached EOL;
# Claude Haiku 4.5 and Claude Sonnet 4.6 global inference profiles are ACTIVE.
# Answer verification toggle maps directly to ExamMaker.Application.Ai.Generation.AiGenerationOptions.VerifyAnswers (B4).
BEDROCK_REGION="${BEDROCK_REGION:-ap-southeast-1}"
BEDROCK_DEV_MODEL_ID="${BEDROCK_DEV_MODEL_ID:-global.anthropic.claude-haiku-4-5-20251001-v1:0}"
BEDROCK_MODEL_ID="${BEDROCK_MODEL_ID:-$BEDROCK_DEV_MODEL_ID}"
BEDROCK_DEMO_MODEL_ID="${BEDROCK_DEMO_MODEL_ID:-global.anthropic.claude-sonnet-4-6}"
AI_GENERATION_VERIFY_ANSWERS="${AI_GENERATION_VERIFY_ANSWERS:-${BEDROCK_ENABLE_VERIFICATION:-false}}"

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

is_pos_int32() {
  local val="$1"
  [[ "$val" =~ ^[1-9][0-9]*$ ]] || return 1
  [ "${#val}" -lt 10 ] || { [ "${#val}" -eq 10 ] && [[ ! "$val" > "2147483647" ]]; }
}

is_pos_int64() {
  local val="$1"
  [[ "$val" =~ ^[1-9][0-9]*$ ]] || return 1
  [ "${#val}" -lt 19 ] || { [ "${#val}" -eq 19 ] && [[ ! "$val" > "9223372036854775807" ]]; }
}

if [ "$AI_GENERATION_VERIFY_ANSWERS" != "true" ] && [ "$AI_GENERATION_VERIFY_ANSWERS" != "false" ]; then
  echo "ERROR: AI_GENERATION_VERIFY_ANSWERS must be 'true' or 'false' (got '${AI_GENERATION_VERIFY_ANSWERS}')." >&2
  exit 1
fi

if [ "$BEDROCK_MODEL_ID" != "$BEDROCK_DEV_MODEL_ID" ] && [ "$BEDROCK_MODEL_ID" != "$BEDROCK_DEMO_MODEL_ID" ]; then
  echo "ERROR: BEDROCK_MODEL_ID ('${BEDROCK_MODEL_ID}') must match BEDROCK_DEV_MODEL_ID ('${BEDROCK_DEV_MODEL_ID}') or BEDROCK_DEMO_MODEL_ID ('${BEDROCK_DEMO_MODEL_ID}')." >&2
  exit 1
fi

# Validate AI Quota defaults / overrides (int32 and int64 bounds matching ExamMaker.Application.Ai.AiQuotaOptions & SQS maxReceiveCount=3)
for int32_var in "AI_JOBS_PER_USER_PER_DAY:${AI_JOBS_PER_USER_PER_DAY}" \
                 "AI_MAX_QUESTIONS_PER_JOB:${AI_MAX_QUESTIONS_PER_JOB}" \
                 "AI_MAX_NOTE_LENGTH:${AI_MAX_NOTE_LENGTH}" \
                 "AI_RATE_PERMIT_LIMIT:${AI_RATE_PERMIT_LIMIT}"; do
  var_name="${int32_var%%:*}"
  var_val="${int32_var#*:}"
  if ! is_pos_int32 "$var_val"; then
    echo "ERROR: ${var_name} must be a positive 32-bit integer in 1..2147483647 (got '${var_val}')." >&2
    exit 1
  fi
done

for int64_var in "AI_TOKENS_PER_USER_PER_DAY:${AI_TOKENS_PER_USER_PER_DAY}" \
                 "AI_SYSTEM_TOKENS_PER_DAY:${AI_SYSTEM_TOKENS_PER_DAY}"; do
  var_name="${int64_var%%:*}"
  var_val="${int64_var#*:}"
  if ! is_pos_int64 "$var_val"; then
    echo "ERROR: ${var_name} must be a positive 64-bit integer in 1..9223372036854775807 (got '${var_val}')." >&2
    exit 1
  fi
done

if [ "$AI_MAX_ATTEMPTS" != "3" ]; then
  echo "ERROR: AI_MAX_ATTEMPTS must be 3 to match SQS worker retry and DLQ maxReceiveCount=3 (got '${AI_MAX_ATTEMPTS}')." >&2
  exit 1
fi

if [ "${#AI_SYSTEM_TOKENS_PER_DAY}" -lt "${#AI_TOKENS_PER_USER_PER_DAY}" ] || { [ "${#AI_SYSTEM_TOKENS_PER_DAY}" -eq "${#AI_TOKENS_PER_USER_PER_DAY}" ] && [[ "$AI_SYSTEM_TOKENS_PER_DAY" < "$AI_TOKENS_PER_USER_PER_DAY" ]]; }; then
  echo "ERROR: AI_SYSTEM_TOKENS_PER_DAY (${AI_SYSTEM_TOKENS_PER_DAY}) must be >= AI_TOKENS_PER_USER_PER_DAY (${AI_TOKENS_PER_USER_PER_DAY})." >&2
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

TMP_SECRET_DIR=""
cleanup_tmp_secret() {
  if [ -n "${TMP_SECRET_DIR:-}" ] && [ -d "${TMP_SECRET_DIR:-}" ]; then
    rm -rf "$TMP_SECRET_DIR"
  fi
}
trap cleanup_tmp_secret EXIT
trap 'cleanup_tmp_secret; exit 130' INT
trap 'cleanup_tmp_secret; exit 143' TERM

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

is_protected_runtime_suffix() {
  case "$1" in
    Bedrock/ModelId|Bedrock/DevModelId|Bedrock/DemoModelId|AiGeneration/VerifyAnswers)
      return 0
      ;;
    *)
      return 1
      ;;
  esac
}

should_overwrite_param() {
  local name="$1"
  local suffix="${name#"${SSM_PREFIX}/"}"

  # 1. Explicit per-key opt-in via OVERWRITE_KEYS (comma-separated suffixes or full paths)
  if [ -n "$OVERWRITE_KEYS" ]; then
    local item=""
    IFS=',' read -ra key_items <<<"$OVERWRITE_KEYS"
    for item in "${key_items[@]}"; do
      item="$(echo "$item" | sed 's/^[[:space:]]*//;s/[[:space:]]*$//')"
      if [ "$item" = "$suffix" ] || [ "$item" = "$name" ]; then
        return 0
      fi
    done
  fi

  # 2. Bulk overwrite via OVERWRITE_CONFIG=1 protects live demo runtime toggles AND
  #    the Bedrock model triplet together (Bedrock/{ModelId,DevModelId,DemoModelId} +
  #    AiGeneration/VerifyAnswers) unless OVERWRITE_BEDROCK_RUNTIME=1.
  if [ "$OVERWRITE_CONFIG" = "1" ]; then
    if is_protected_runtime_suffix "$suffix"; then
      [ "$OVERWRITE_BEDROCK_RUNTIME" = "1" ] && return 0 || return 1
    fi
    return 0
  fi

  return 1
}

put_string_param() {
  local name="$1"
  local value="$2"
  local desc="$3"
  local status=""
  local suffix="${name#"${SSM_PREFIX}/"}"
  status="$(check_ssm_param_status "$name")"

  if [ "$status" = "exists" ]; then
    if should_overwrite_param "$name"; then
      aws ssm put-parameter \
        --region "$AWS_REGION" \
        --name "$name" \
        --type String \
        --tier Standard \
        --value "$value" \
        --description "$desc" \
        --overwrite >/dev/null
      echo "    [UPDATED]  ${name} = ${value}"
    elif [ "$OVERWRITE_CONFIG" = "1" ] && is_protected_runtime_suffix "$suffix"; then
      echo "    [PROTECTED] ${name} (preserved Bedrock/AI runtime toggle; set OVERWRITE_BEDROCK_RUNTIME=1 or OVERWRITE_KEYS='${suffix}' to overwrite)"
    else
      echo "    [KEPT]     ${name} (already exists; set OVERWRITE_CONFIG=1 or OVERWRITE_KEYS='${suffix}' to overwrite)"
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

# Executes `aws ssm put-parameter` with a JSON payload without exposing the secret in argv (`ps`).
# Must be called directly (NOT inside `$(...)` command substitution) so `TMP_SECRET_DIR` is set in
# the main shell and always cleaned up by `trap cleanup_tmp_secret EXIT INT TERM` on Ctrl-C.
# Stores the resulting parameter version in `LAST_PUT_SECRET_VERSION`.
LAST_PUT_SECRET_VERSION=""
put_secret_json_payload() {
  local payload_json="$1"
  local ver=""
  local rc=0
  LAST_PUT_SECRET_VERSION=""

  if [ -e /dev/stdin ] && ! command -v cygpath >/dev/null 2>&1; then
    ver="$(printf '%s' "$payload_json" | aws ssm put-parameter \
      --region "$AWS_REGION" \
      --cli-input-json "file:///dev/stdin" \
      --query "Version" \
      --output text)" || rc=$?
    if [ "$rc" -ne 0 ] || [ -z "$ver" ] || [[ ! "$ver" =~ ^[0-9]+$ ]]; then
      echo "ERROR: aws ssm put-parameter failed via /dev/stdin (rc=${rc}, version='${ver}')." >&2
      return 1
    fi
    LAST_PUT_SECRET_VERSION="$ver"
    return 0
  fi

  TMP_SECRET_DIR="$(umask 077 && mktemp -d)" || return 1
  if command -v cygpath >/dev/null 2>&1; then
    if ! command -v icacls >/dev/null 2>&1; then
      rm -rf "$TMP_SECRET_DIR"
      TMP_SECRET_DIR=""
      echo "ERROR: icacls is required on Windows to restrict temporary secret directory ACLs." >&2
      return 1
    fi
    local win_dir=""
    win_dir="$(cygpath -w "$TMP_SECRET_DIR")"
    if ! icacls "$win_dir" /inheritance:r /grant:r "${USERNAME:-$(id -un)}:(OI)(CI)F" >/dev/null 2>&1; then
      rm -rf "$TMP_SECRET_DIR"
      TMP_SECRET_DIR=""
      echo "ERROR: Failed to restrict Windows NTFS ACLs on temporary secret directory '${win_dir}'." >&2
      return 1
    fi
  fi

  local tmp_file="${TMP_SECRET_DIR}/payload.json"
  if ! (umask 077 && printf '%s\n' "$payload_json" >"$tmp_file"); then
    rm -rf "$TMP_SECRET_DIR"
    TMP_SECRET_DIR=""
    echo "ERROR: Failed to write temporary secret payload file." >&2
    return 1
  fi

  local param_file="$tmp_file"
  if command -v cygpath >/dev/null 2>&1; then
    param_file="$(cygpath -m "$tmp_file")"
  fi

  ver="$(aws ssm put-parameter \
    --region "$AWS_REGION" \
    --cli-input-json "file://${param_file}" \
    --query "Version" \
    --output text)" || rc=$?
  rm -rf "$TMP_SECRET_DIR"
  TMP_SECRET_DIR=""
  if [ "$rc" -ne 0 ] || [ -z "$ver" ] || [[ ! "$ver" =~ ^[0-9]+$ ]]; then
    echo "ERROR: aws ssm put-parameter failed (rc=${rc}, version='${ver}')." >&2
    return 1
  fi
  LAST_PUT_SECRET_VERSION="$ver"
  return 0
}

put_origin_secret_param() {
  local name="${SSM_OPS_PREFIX}/Origin/VerifySecret"
  local legacy_name="${SSM_PREFIX}/Origin/VerifySecret"
  local desc="Shared secret for CloudFront X-Origin-Verify header and Nginx origin-secret.conf (O4/O5/O12; isolated under ops prefix)"
  local status=""
  local legacy_status=""
  status="$(check_ssm_param_status "$name")"
  legacy_status="$(check_ssm_param_status "$legacy_name")"

  # Automatically migrate legacy /exammaker/<env>/Origin/VerifySecret to /exammaker-ops/<env>/Origin/VerifySecret
  if [ "$legacy_status" = "exists" ]; then
    if [ "$status" = "missing" ] && [ "$ROTATE_ORIGIN_SECRET" != "1" ]; then
      local legacy_val=""
      legacy_val="$(aws ssm get-parameter --region "$AWS_REGION" --name "$legacy_name" --with-decryption --query "Parameter.Value" --output text)"
      local mig_payload=""
      mig_payload="$(jq -nc \
        --arg name "$name" \
        --arg val "$legacy_val" \
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
        }')"
      if ! put_secret_json_payload "$mig_payload" || [ -z "$LAST_PUT_SECRET_VERSION" ]; then
        echo "ERROR: Failed to migrate ${legacy_name} to ${name}." >&2
        return 1
      fi
      status="exists"
      echo "    [MIGRATED] ${legacy_name} -> ${name} (version ${LAST_PUT_SECRET_VERSION}, isolated from app prefix)"
    fi
    aws ssm delete-parameter --region "$AWS_REGION" --name "$legacy_name" >/dev/null
    echo "    [REMOVED]  ${legacy_name} (removed from app prefix '${SSM_PREFIX}/')"
  fi

  if [ "$status" = "exists" ] && [ "$ROTATE_ORIGIN_SECRET" != "1" ]; then
    if [ -n "$ORIGIN_VERIFY_SECRET" ] || [ "$ORIGIN_VERIFY_SECRET_STDIN" = "1" ]; then
      echo "    [KEPT]     ${name} (SecureString already exists; set ROTATE_ORIGIN_SECRET=1 to overwrite with provided secret)"
    else
      echo "    [KEPT]     ${name} (SecureString already exists; preserving CloudFront <-> Nginx secret)"
    fi
    return 0
  fi

  local secret_val="$ORIGIN_VERIFY_SECRET"
  if [ "$ORIGIN_VERIFY_SECRET_STDIN" = "1" ]; then
    if ! IFS= read -r secret_val && [ -z "$secret_val" ]; then
      echo "ERROR: Failed to read Origin/VerifySecret from stdin (empty input or unexpected EOF)." >&2
      exit 1
    fi
    secret_val="$(printf '%s' "$secret_val" | tr -d '\r\n')"
  elif [ -n "$secret_val" ]; then
    echo "    [WARN]     ORIGIN_VERIFY_SECRET was passed via environment variable; ensure this is a non-shared shell (prefer ROTATE_ORIGIN_SECRET=1 or ORIGIN_VERIFY_SECRET_STDIN=1)." >&2
  else
    secret_val="$(generate_origin_secret)"
  fi

  if [ "${#secret_val}" -lt 32 ] || [[ ! "$secret_val" =~ ^[A-Za-z0-9][A-Za-z0-9._-]+$ ]]; then
    echo "ERROR: Origin/VerifySecret must be at least 32 chars, start with [A-Za-z0-9], and contain only [A-Za-z0-9._-]." >&2
    exit 1
  fi

  local payload_json=""
  if [ "$status" = "exists" ]; then
    local prev_ver=""
    if ! prev_ver="$(aws ssm get-parameter --region "$AWS_REGION" --name "$name" --query "Parameter.Version" --output text)" || [ -z "$prev_ver" ]; then
      echo "ERROR: Failed to read current version of ${name} before rotation." >&2
      return 1
    fi
    payload_json="$(jq -nc \
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
      }')"
    if ! put_secret_json_payload "$payload_json" || [ -z "$LAST_PUT_SECRET_VERSION" ]; then
      echo "ERROR: Failed to rotate ${name} in SSM Parameter Store." >&2
      return 1
    fi
    echo "    [ROTATED]  ${name} (SecureString previous version ${prev_ver} -> new version ${LAST_PUT_SECRET_VERSION} via alias/aws/ssm — follow 3-step rotation in runbook!)"
  else
    payload_json="$(jq -nc \
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
      }')"
    if ! put_secret_json_payload "$payload_json" || [ -z "$LAST_PUT_SECRET_VERSION" ]; then
      echo "ERROR: Failed to create ${name} in SSM Parameter Store." >&2
      return 1
    fi
    echo "    [CREATED]  ${name} (SecureString version ${LAST_PUT_SECRET_VERSION} via alias/aws/ssm; value not printed)"
  fi
}

migrate_ops_rds_master_password() {
  local legacy_rds_pw="${SSM_PREFIX}/Rds/MasterPassword"
  local ops_rds_pw="${SSM_OPS_PREFIX}/Rds/MasterPassword"
  if [ "$(check_ssm_param_status "$legacy_rds_pw")" = "exists" ]; then
    if [ "$(check_ssm_param_status "$ops_rds_pw")" = "missing" ]; then
      local pw_val=""
      pw_val="$(aws ssm get-parameter --region "$AWS_REGION" --name "$legacy_rds_pw" --with-decryption --query "Parameter.Value" --output text)"
      local pw_payload=""
      pw_payload="$(jq -nc \
        --arg name "$ops_rds_pw" \
        --arg val "$pw_val" \
        --arg desc "RDS master password migrated out of app prefix (O10/O12 least privilege)" \
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
            {Key: "Task", Value: "O10"}
          ]
        }')"
      if ! put_secret_json_payload "$pw_payload" || [ -z "$LAST_PUT_SECRET_VERSION" ]; then
        echo "ERROR: Failed to migrate ${legacy_rds_pw} to ${ops_rds_pw}." >&2
        return 1
      fi
      echo "    [MIGRATED] ${legacy_rds_pw} -> ${ops_rds_pw} (isolated from app prefix)"
    fi
    aws ssm delete-parameter --region "$AWS_REGION" --name "$legacy_rds_pw" >/dev/null
    echo "    [REMOVED]  ${legacy_rds_pw} (removed from app prefix '${SSM_PREFIX}/')"
  fi
}

echo "==> Region: ${AWS_REGION} | Env: ${ENV_NAME} | App Prefix: ${SSM_PREFIX} | Ops Prefix: ${SSM_OPS_PREFIX}"
echo "==> Mode: OVERWRITE_CONFIG=${OVERWRITE_CONFIG}, OVERWRITE_BEDROCK_RUNTIME=${OVERWRITE_BEDROCK_RUNTIME}, OVERWRITE_KEYS='${OVERWRITE_KEYS}', ROTATE_ORIGIN_SECRET=${ROTATE_ORIGIN_SECRET}"

echo ""
echo "==> 1. AWS Core parameters..."
put_string_param "${SSM_PREFIX}/AWS/Region" "$AWS_REGION" "Default AWS region for SDK clients (AWS:Region)"

echo ""
echo "==> 2. Amazon Bedrock & AI Generation parameters..."
put_string_param "${SSM_PREFIX}/Bedrock/Region" "$BEDROCK_REGION" "AWS region for Amazon Bedrock invocation (Bedrock:Region)"
put_string_param "${SSM_PREFIX}/Bedrock/ModelId" "$BEDROCK_MODEL_ID" "Active Bedrock model/inference profile ID (Claude Haiku 4.5 for dev, Claude Sonnet 4.6 for demo)"
put_string_param "${SSM_PREFIX}/Bedrock/DevModelId" "$BEDROCK_DEV_MODEL_ID" "Target Bedrock model/inference profile ID for dev/cost-saving rollback (Claude Haiku 4.5)"
put_string_param "${SSM_PREFIX}/Bedrock/DemoModelId" "$BEDROCK_DEMO_MODEL_ID" "Target Bedrock model/inference profile ID for demo (Claude Sonnet 4.6)"

# Migrate legacy Bedrock/EnableAnswerVerification -> AiGeneration/VerifyAnswers (bound to AiGenerationOptions.VerifyAnswers in B4)
LEGACY_VERIFY_KEY="${SSM_PREFIX}/Bedrock/EnableAnswerVerification"
if [ "$(check_ssm_param_status "$LEGACY_VERIFY_KEY")" = "exists" ]; then
  if [ "$(check_ssm_param_status "${SSM_PREFIX}/AiGeneration/VerifyAnswers")" = "missing" ]; then
    LEGACY_VERIFY_VAL="$(aws ssm get-parameter --region "$AWS_REGION" --name "$LEGACY_VERIFY_KEY" --query "Parameter.Value" --output text)"
    if [ "$LEGACY_VERIFY_VAL" = "true" ] || [ "$LEGACY_VERIFY_VAL" = "false" ]; then
      AI_GENERATION_VERIFY_ANSWERS="$LEGACY_VERIFY_VAL"
    fi
  fi
  put_string_param "${SSM_PREFIX}/AiGeneration/VerifyAnswers" "$AI_GENERATION_VERIFY_ANSWERS" "Toggle optional AI answer verification pass (AiGeneration:VerifyAnswers; false for dev, true for demo)"
  aws ssm delete-parameter --region "$AWS_REGION" --name "$LEGACY_VERIFY_KEY" >/dev/null
  echo "    [REMOVED]  ${LEGACY_VERIFY_KEY} (migrated to ${SSM_PREFIX}/AiGeneration/VerifyAnswers)"
else
  put_string_param "${SSM_PREFIX}/AiGeneration/VerifyAnswers" "$AI_GENERATION_VERIFY_ANSWERS" "Toggle optional AI answer verification pass (AiGeneration:VerifyAnswers; false for dev, true for demo)"
fi

# Verify live SSM invariant: Bedrock/ModelId must match effective Bedrock/DevModelId or Bedrock/DemoModelId
LIVE_ACTIVE_MODEL="$(aws ssm get-parameter --region "$AWS_REGION" --name "${SSM_PREFIX}/Bedrock/ModelId" --query "Parameter.Value" --output text)"
LIVE_DEV_MODEL="$(aws ssm get-parameter --region "$AWS_REGION" --name "${SSM_PREFIX}/Bedrock/DevModelId" --query "Parameter.Value" --output text)"
LIVE_DEMO_MODEL="$(aws ssm get-parameter --region "$AWS_REGION" --name "${SSM_PREFIX}/Bedrock/DemoModelId" --query "Parameter.Value" --output text)"
if [ "$LIVE_ACTIVE_MODEL" != "$LIVE_DEV_MODEL" ] && [ "$LIVE_ACTIVE_MODEL" != "$LIVE_DEMO_MODEL" ]; then
  echo "ERROR: Live '${SSM_PREFIX}/Bedrock/ModelId' ('${LIVE_ACTIVE_MODEL}') does not match DevModelId ('${LIVE_DEV_MODEL}') or DemoModelId ('${LIVE_DEMO_MODEL}'). Use OVERWRITE_BEDROCK_RUNTIME=1 or update ModelId." >&2
  exit 1
fi

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
echo "==> 5. Ops-only Secrets under '${SSM_OPS_PREFIX}/' (Origin Verify Secret & RDS MasterPassword isolation)..."
put_origin_secret_param
migrate_ops_rds_master_password

echo ""
echo "==> 6. CloudWatch Observability metadata..."
put_string_param "${SSM_PREFIX}/CloudWatch/LogGroupName" "$CW_LOG_GROUP_NAME" "CloudWatch Logs group name for API and Worker"
put_string_param "${SSM_PREFIX}/CloudWatch/MetricNamespace" "$CW_METRIC_NAMESPACE" "CloudWatch custom metrics namespace for ExamMaker"

echo ""
echo "DONE: Parameter Store setup (O12) completed under '${SSM_PREFIX}/' and '${SSM_OPS_PREFIX}/'."
echo "Next step: run './infra/ssm/verify-ssm.sh' to verify all parameters across O9-O12, encryption, and \$0 cost tier."
