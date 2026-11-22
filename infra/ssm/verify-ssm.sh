#!/usr/bin/env bash
# Verification script for AWS Systems Manager Parameter Store (AI Exam Maker - Task O12).
# Audits the complete parameter tree under ${SSM_PREFIX}/* across O9 (Cognito), O10 (RDS),
# O11 (SQS), and O12 (Bedrock, AI Quota, AI Rate Limit, Origin Secret, CloudWatch) for:
#   - Presence and .NET IConfiguration key alignment
#   - Proper SecureString encryption using AWS-managed key alias/aws/ssm ($0)
#   - Standard tier enforcement across all parameters ($0)
#   - Valid numeric/boolean/TimeSpan values and budget guardrails
#
# Never prints secret values (ConnectionStrings/ExamMaker, Origin/VerifySecret, Rds/MasterPassword).
#
# Dependencies: aws (CLI v2), jq (both pre-installed in AWS CloudShell).
#
# Usage:
#   ./infra/ssm/verify-ssm.sh

set -euo pipefail

export MSYS_NO_PATHCONV=1
export MSYS2_ARG_CONV_EXCL="*"

AWS_REGION="${AWS_REGION:-ap-southeast-1}"
ENV_NAME="${ENV_NAME:-dev}"
SSM_PREFIX="${SSM_PREFIX:-/exammaker/${ENV_NAME}}"

command -v aws >/dev/null 2>&1 || { echo "ERROR: aws CLI is required." >&2; exit 1; }
command -v jq >/dev/null 2>&1 || { echo "ERROR: jq is required (pre-installed in AWS CloudShell; on Windows install via 'winget install jqlang.jq')." >&2; exit 1; }

PASSED=0
FAILURES=0

pass() { echo "[PASS] $1"; PASSED=$((PASSED + 1)); }
fail() { echo "[FAIL] $1" >&2; FAILURES=$((FAILURES + 1)); }
warn() { echo "[WARN] $1" >&2; }

echo "==> Auditing SSM Parameter Store under '${SSM_PREFIX}/' (${AWS_REGION}, env=${ENV_NAME})..."

# Fetch all parameter metadata (Tier, Type, KeyId) via describe-parameters
DESCRIBE_JSON="$(aws ssm describe-parameters \
  --region "$AWS_REGION" \
  --parameter-filters "Key=Name,Option=BeginsWith,Values=${SSM_PREFIX}/" \
  --output json)"

# Fetch all parameter values (including decrypted SecureString for length/presence validation only; never printed)
VALUES_JSON="$(aws ssm get-parameters-by-path \
  --region "$AWS_REGION" \
  --path "${SSM_PREFIX}" \
  --recursive \
  --with-decryption \
  --output json)"

param_exists() {
  local suffix="$1"
  local full="${SSM_PREFIX}/${suffix}"
  local found=""
  found="$(jq -r --arg name "$full" '.Parameters[]? | select(.Name == $name) | .Name' <<<"$DESCRIBE_JSON" | tr -d '\r\n')"
  [ -n "$found" ]
}

param_type() {
  local suffix="$1"
  local full="${SSM_PREFIX}/${suffix}"
  jq -r --arg name "$full" '.Parameters[]? | select(.Name == $name) | .Type // empty' <<<"$DESCRIBE_JSON" | tr -d '\r\n'
}

param_val() {
  local suffix="$1"
  local full="${SSM_PREFIX}/${suffix}"
  jq -r --arg name "$full" '.Parameters[]? | select(.Name == $name) | .Value // empty' <<<"$VALUES_JSON" | tr -d '\r\n'
}

check_group_present() {
  local label="$1"
  shift
  local missing=()
  local suffix=""
  for suffix in "$@"; do
    if ! param_exists "$suffix" || [ -z "$(param_val "$suffix")" ]; then
      missing+=("$suffix")
    fi
  done

  if [ "${#missing[@]}" -eq 0 ]; then
    pass "${label}: all $# required parameters exist and are non-empty"
  else
    fail "${label}: missing or empty parameter(s): ${missing[*]}"
  fi
}

is_pos_int() {
  [[ "$1" =~ ^[1-9][0-9]*$ ]]
}

# 1. O9 Cognito parameters
check_group_present "O9 Cognito" \
  "Cognito/Region" \
  "Cognito/UserPoolId" \
  "Cognito/ClientId" \
  "Cognito/HostedUiDomain" \
  "Cognito/Authority"

# 2. O10 RDS ConnectionString (must exist and be SecureString)
if param_exists "ConnectionStrings/ExamMaker" && [ "$(param_type "ConnectionStrings/ExamMaker")" = "SecureString" ] && [ -n "$(param_val "ConnectionStrings/ExamMaker")" ]; then
  pass "O10 RDS: ConnectionStrings/ExamMaker exists as SecureString"
else
  fail "O10 RDS: ConnectionStrings/ExamMaker missing, empty, or not SecureString (type='$(param_type "ConnectionStrings/ExamMaker")')"
fi

# 3. O10 RDS metadata parameters
check_group_present "O10 RDS metadata" \
  "Rds/InstanceId" \
  "Rds/Endpoint" \
  "Rds/Port" \
  "Rds/Database" \
  "Rds/Username"

# 4. O11 SQS parameters
check_group_present "O11 SQS" \
  "Sqs/AiJobsQueueUrl" \
  "Sqs/AiJobsQueueArn" \
  "Sqs/AiJobsDlqUrl" \
  "Sqs/AiJobsDlqArn"

# 5. O12 AWS Core region
AWS_REGION_VAL="$(param_val "AWS/Region")"
if [ "$AWS_REGION_VAL" = "$AWS_REGION" ]; then
  pass "O12 AWS Core: AWS/Region = '${AWS_REGION_VAL}'"
else
  fail "O12 AWS Core: expected AWS/Region='${AWS_REGION}', got '${AWS_REGION_VAL}'"
fi

# 6. O12 Bedrock parameters
check_group_present "O12 Bedrock" \
  "Bedrock/Region" \
  "Bedrock/ModelId" \
  "Bedrock/DevModelId" \
  "Bedrock/DemoModelId" \
  "Bedrock/EnableAnswerVerification"

BEDROCK_REGION_VAL="$(param_val "Bedrock/Region")"
BEDROCK_ACTIVE_MODEL="$(param_val "Bedrock/ModelId")"
BEDROCK_DEV_MODEL="$(param_val "Bedrock/DevModelId")"
BEDROCK_DEMO_MODEL="$(param_val "Bedrock/DemoModelId")"

PROFILES_JSON="{}"
FOUNDATION_JSON="{}"
BEDROCK_QUERY_OK=1
if ! PROFILES_OUT="$(aws bedrock list-inference-profiles --region "$BEDROCK_REGION_VAL" --output json 2>&1)"; then
  BEDROCK_QUERY_OK=0
  fail "O12 Bedrock: failed to list inference profiles in '${BEDROCK_REGION_VAL}' (requires bedrock:ListInferenceProfiles): ${PROFILES_OUT}"
else
  PROFILES_JSON="$PROFILES_OUT"
fi

if ! FOUNDATION_OUT="$(aws bedrock list-foundation-models --region "$BEDROCK_REGION_VAL" --output json 2>&1)"; then
  BEDROCK_QUERY_OK=0
  fail "O12 Bedrock: failed to list foundation models in '${BEDROCK_REGION_VAL}' (requires bedrock:ListFoundationModels): ${FOUNDATION_OUT}"
else
  FOUNDATION_JSON="$FOUNDATION_OUT"
fi

# Checks foundation model lifecycle in BEDROCK_REGION_VAL using already-fetched FOUNDATION_JSON first,
# then falls back to get-foundation-model. Returns:
#   0 = ACTIVE, 1 = found and explicitly LEGACY, 2 = not offered in BEDROCK_REGION_VAL, 3 = inconclusive (throttle/transient/unknown)
check_foundation_model_lifecycle() {
  local fm_id="$1"
  local lifecycle=""
  lifecycle="$(jq -r --arg id "$fm_id" '.modelSummaries[]? | select(.modelId == $id) | .modelLifecycle.status // empty' <<<"$FOUNDATION_JSON" | tr -d '\r\n')"
  if [ -n "$lifecycle" ]; then
    if [ "$lifecycle" = "LEGACY" ]; then
      return 1
    elif [ "$lifecycle" = "ACTIVE" ]; then
      return 0
    fi
    return 3
  fi

  local fm_out=""
  local rc=0
  fm_out="$(aws bedrock get-foundation-model --region "$BEDROCK_REGION_VAL" --model-identifier "$fm_id" --output json 2>&1)" || rc=$?
  if [ "$rc" -ne 0 ]; then
    if grep -q "ResourceNotFoundException" <<<"$fm_out"; then
      return 2
    fi
    # Transient API errors (throttling, rate limits, access check) -> inconclusive
    return 3
  fi

  lifecycle="$(jq -r '.modelDetails.modelLifecycle.status // empty' <<<"$fm_out" | tr -d '\r\n')"
  if [ "$lifecycle" = "LEGACY" ]; then
    return 1
  elif [ "$lifecycle" = "ACTIVE" ]; then
    return 0
  fi
  return 3
}

BEDROCK_LIFECYCLE_NOTE=""
is_valid_bedrock_id() {
  local mid="$1"
  BEDROCK_LIFECYCLE_NOTE=""
  local profile_json=""
  # 1. Profile validity: status must be ACTIVE in BEDROCK_REGION_VAL
  profile_json="$(jq -c --arg id "$mid" '.inferenceProfileSummaries[]? | select(.inferenceProfileId == $id and .status == "ACTIVE")' <<<"$PROFILES_JSON")"
  if [ -n "$profile_json" ]; then
    local fm_ids=""
    local confirmed_active=0
    local unverified_cross_region=0
    local inconclusive_lookup=0
    fm_ids="$(jq -r '.models[]?.modelArn | split("/") | last' <<<"$profile_json" | sort -u)"
    if [ -n "$fm_ids" ]; then
      while IFS= read -r fm; do
        [ -z "$fm" ] && continue
        local fm_rc=0
        check_foundation_model_lifecycle "$fm" || fm_rc=$?
        if [ "$fm_rc" -eq 0 ]; then
          confirmed_active=$((confirmed_active + 1))
        elif [ "$fm_rc" -eq 1 ]; then
          # Explicitly reject only when an underlying foundation model is confirmed LEGACY.
          return 1
        elif [ "$fm_rc" -eq 2 ]; then
          unverified_cross_region=$((unverified_cross_region + 1))
        elif [ "$fm_rc" -eq 3 ]; then
          inconclusive_lookup=$((inconclusive_lookup + 1))
          warn "O12 Bedrock: lifecycle lookup for foundation model '${fm}' (profile '${mid}') in '${BEDROCK_REGION_VAL}' was inconclusive; relying on profile.status=ACTIVE"
        fi
      done <<<"$fm_ids"
    fi
    if [ "$confirmed_active" -gt 0 ] && [ "$inconclusive_lookup" -eq 0 ]; then
      BEDROCK_LIFECYCLE_NOTE="profile.status=ACTIVE, local foundation model lifecycle confirmed ACTIVE"
    elif [ "$inconclusive_lookup" -gt 0 ]; then
      BEDROCK_LIFECYCLE_NOTE="profile.status=ACTIVE, foundation model lifecycle lookup inconclusive"
    else
      BEDROCK_LIFECYCLE_NOTE="profile.status=ACTIVE, destination foundation models are cross-region"
    fi
    return 0
  fi

  # 2. On-demand foundation model (non-profile) fallback
  local on_demand_fm=""
  on_demand_fm="$(jq -r --arg id "$mid" '.modelSummaries[]? | select(.modelId == $id and ((.inferenceTypesSupported // []) | index("ON_DEMAND") != null) and (.modelLifecycle.status // "") == "ACTIVE") | .modelId' <<<"$FOUNDATION_JSON" | tr -d '\r\n')"
  if [ -n "$on_demand_fm" ]; then
    BEDROCK_LIFECYCLE_NOTE="ON_DEMAND foundation model lifecycle confirmed ACTIVE"
    return 0
  fi
  return 1
}

if [ "$BEDROCK_QUERY_OK" -eq 1 ]; then
  for label_id in "ModelId:${BEDROCK_ACTIVE_MODEL}" "DevModelId:${BEDROCK_DEV_MODEL}" "DemoModelId:${BEDROCK_DEMO_MODEL}"; do
    key_name="${label_id%%:*}"
    model_val="${label_id#*:}"
    if [ -n "$model_val" ] && is_valid_bedrock_id "$model_val"; then
      pass "O12 Bedrock: ${key_name} ('${model_val}') is valid in '${BEDROCK_REGION_VAL}' (${BEDROCK_LIFECYCLE_NOTE})"
    else
      fail "O12 Bedrock: ${key_name} ('${model_val}') is missing, LEGACY/EOL, or not ACTIVE in '${BEDROCK_REGION_VAL}'"
    fi
  done
fi

if [ -n "$BEDROCK_ACTIVE_MODEL" ] && { [ "$BEDROCK_ACTIVE_MODEL" = "$BEDROCK_DEV_MODEL" ] || [ "$BEDROCK_ACTIVE_MODEL" = "$BEDROCK_DEMO_MODEL" ]; }; then
  pass "O12 Bedrock: ModelId matches DevModelId or DemoModelId"
else
  fail "O12 Bedrock: ModelId ('${BEDROCK_ACTIVE_MODEL}') does not match DevModelId ('${BEDROCK_DEV_MODEL}') or DemoModelId ('${BEDROCK_DEMO_MODEL}')"
fi

BEDROCK_VERIFY="$(param_val "Bedrock/EnableAnswerVerification")"
if [ "$BEDROCK_VERIFY" = "true" ] || [ "$BEDROCK_VERIFY" = "false" ]; then
  pass "O12 Bedrock: EnableAnswerVerification is valid boolean ('${BEDROCK_VERIFY}')"
else
  fail "O12 Bedrock: EnableAnswerVerification must be 'true' or 'false', got '${BEDROCK_VERIFY}'"
fi

# 7. O12 AI Quota parameters (AiQuotaOptions)
check_group_present "O12 AI Quota" \
  "AiQuota/JobsPerUserPerDay" \
  "AiQuota/MaxQuestionsPerJob" \
  "AiQuota/TokensPerUserPerDay" \
  "AiQuota/SystemTokensPerDay" \
  "AiQuota/MaxNoteLength" \
  "AiQuota/MaxAttempts"

Q_JOBS="$(param_val "AiQuota/JobsPerUserPerDay")"
Q_MAXQ="$(param_val "AiQuota/MaxQuestionsPerJob")"
Q_UTOK="$(param_val "AiQuota/TokensPerUserPerDay")"
Q_STOK="$(param_val "AiQuota/SystemTokensPerDay")"
Q_NOTE="$(param_val "AiQuota/MaxNoteLength")"
Q_ATT="$(param_val "AiQuota/MaxAttempts")"

if is_pos_int "$Q_JOBS" && is_pos_int "$Q_MAXQ" && is_pos_int "$Q_UTOK" && is_pos_int "$Q_STOK" && is_pos_int "$Q_NOTE" && [ "$Q_ATT" = "3" ] && [ "$Q_STOK" -ge "$Q_UTOK" ]; then
  pass "O12 AI Quota values valid (jobs=${Q_JOBS}/d, maxQ=${Q_MAXQ}, userTok=${Q_UTOK}, sysTok=${Q_STOK}, maxAttempts=${Q_ATT})"
else
  fail "O12 AI Quota guardrail violation (jobs='${Q_JOBS}', maxQ='${Q_MAXQ}', userTok='${Q_UTOK}', sysTok='${Q_STOK}', note='${Q_NOTE}', maxAttempts='${Q_ATT}' expected 3)"
fi

# 8. O12 AI Rate Limit parameters (AiRateLimitOptions)
# Accepts .NET TimeSpan format [d.]hh:mm:ss[.fffffff] (single- or two-digit hours) and rejects zero duration.
RL_PERMIT="$(param_val "AiRateLimit/PermitLimit")"
RL_WINDOW="$(param_val "AiRateLimit/Window")"
RL_NONZERO="${RL_WINDOW//[0.:]/}"
if is_pos_int "$RL_PERMIT" && [[ "$RL_WINDOW" =~ ^([0-9]+\.)?(0?[0-9]|1[0-9]|2[0-3]):[0-5][0-9]:[0-5][0-9](\.[0-9]{1,7})?$ ]] && [ -n "$RL_NONZERO" ]; then
  pass "O12 AI Rate Limit valid (PermitLimit=${RL_PERMIT}, Window=${RL_WINDOW})"
else
  fail "O12 AI Rate Limit invalid (PermitLimit='${RL_PERMIT}', Window='${RL_WINDOW}' — must be positive .NET TimeSpan > 0)"
fi

# 9. O12 Origin VerifySecret (SecureString, >= 32 chars, never printed)
ORIGIN_TYPE="$(param_type "Origin/VerifySecret")"
ORIGIN_VAL="$(param_val "Origin/VerifySecret")"
if [ "$ORIGIN_TYPE" = "SecureString" ] && [ "${#ORIGIN_VAL}" -ge 32 ]; then
  pass "O12 Origin/VerifySecret exists as SecureString with length ${#ORIGIN_VAL} (>= 32 chars)"
else
  fail "O12 Origin/VerifySecret invalid (type='${ORIGIN_TYPE}', length=${#ORIGIN_VAL}, expected SecureString >= 32 chars)"
fi

# 10. O12 CloudWatch metadata
check_group_present "O12 CloudWatch metadata" \
  "CloudWatch/LogGroupName" \
  "CloudWatch/MetricNamespace"

# 11. Cost guardrail: ALL parameters under prefix must be Standard tier ($0)
NON_STANDARD="$(jq -r '[.Parameters[]? | select(.Tier != "Standard") | "\(.Name)(\(.Tier))"] | join(", ")' <<<"$DESCRIBE_JSON" | tr -d '\r\n')"
if [ -z "$NON_STANDARD" ]; then
  TOTAL_COUNT="$(jq -r '.Parameters | length' <<<"$DESCRIBE_JSON" | tr -d '\r\n')"
  pass "Cost control: all ${TOTAL_COUNT} parameters under '${SSM_PREFIX}/' use Standard tier (\$0)"
else
  fail "Cost control: non-Standard tier parameters detected: ${NON_STANDARD}"
fi

# 12. Cost guardrail: ALL SecureString parameters must use AWS-managed KMS key (alias/aws/ssm = $0)
CUSTOM_KMS="$(jq -r '[.Parameters[]? | select(.Type == "SecureString" and (.KeyId // "") != "alias/aws/ssm") | "\(.Name)(\(.KeyId // "unknown"))"] | join(", ")' <<<"$DESCRIBE_JSON" | tr -d '\r\n')"
if [ -z "$CUSTOM_KMS" ]; then
  pass "Cost control: all SecureString parameters use AWS-managed key alias/aws/ssm (\$0)"
else
  fail "Cost control: SecureString parameter(s) not using alias/aws/ssm: ${CUSTOM_KMS}"
fi

# 13. Security guardrail: No sensitive parameter stored as plaintext String
PLAINTEXT_SECRETS="$(jq -r '[.Parameters[]? | select((.Name | test("Secret|Password|ConnectionStrings"; "i")) and .Type != "SecureString") | .Name] | join(", ")' <<<"$DESCRIBE_JSON" | tr -d '\r\n')"
if [ -z "$PLAINTEXT_SECRETS" ]; then
  pass "Security: no Secret/Password/ConnectionString parameter is stored as plaintext String"
else
  fail "Security: sensitive parameter(s) stored as non-SecureString: ${PLAINTEXT_SECRETS}"
fi

# Advisory check for pre-existing O10 Rds/MasterPassword under app prefix
if param_exists "Rds/MasterPassword"; then
  warn "Security/Least-privilege advisory: '${SSM_PREFIX}/Rds/MasterPassword' exists under app prefix '${SSM_PREFIX}/' (loaded into IConfiguration via AddSystemsManager); migrate to '/exammaker-ops/${ENV_NAME}/Rds/MasterPassword' or delete after storing in OPS password manager"
fi

echo ""
if [ "$FAILURES" -eq 0 ]; then
  echo "ALL CHECKS PASSED (${PASSED}/${PASSED})."
  exit 0
else
  echo "VERIFICATION FAILED: ${PASSED} passed, ${FAILURES} failed." >&2
  exit 1
fi
