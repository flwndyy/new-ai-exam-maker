#!/usr/bin/env bash
# Verification script for Backend CI/CD AWS infrastructure (AI Exam Maker - Task O15).
# Audits 10 security, OIDC, S3, IAM, and EC2 SSM readiness checks:
#   1. GitHub OIDC provider exists in account
#   2. Backend deploy IAM role exists and has NO AdministratorAccess managed policy
#   3. Trust policy strictly locks aud, sub (main), workflow ("Deploy backend"), and ref (refs/heads/main)
#   4. Inline least-privilege policy 'aem-github-backend-deploy-policy' exists on deploy role
#   5. Private S3 artifacts bucket exists
#   6. S3 Block Public Access enabled (all 4 flags = true)
#   7. S3 default encryption enabled (SSE-S3 AES256 = $0)
#   8. S3 lifecycle rule enabled to expire old releases under backend/releases/
#   9. EC2 role has read-only s3:GetObject access to backend/releases/*
#  10. Target EC2 instance (tag Name=aem-exammaker-ec2) is managed by SSM (PingStatus=Online or instance stopped)
#
# Privacy: Never prints 12-digit AWS Account ID, unmasked ARNs, or EC2 Instance IDs.
#
# Usage:
#   ./infra/deploy/verify-backend-cd.sh
#   BACKEND_ARTIFACTS_BUCKET="my-bucket" ./infra/deploy/verify-backend-cd.sh

set -euo pipefail

export MSYS_NO_PATHCONV=1
export MSYS2_ARG_CONV_EXCL="*"

EXPECTED_REGION="ap-southeast-1"
AWS_REGION="${AWS_REGION:-$EXPECTED_REGION}"
ALLOW_NON_DEFAULT_REGION="${ALLOW_NON_DEFAULT_REGION:-0}"
ENV_NAME="${ENV_NAME:-dev}"
EC2_TAG_NAME="${EC2_TAG_NAME:-aem-exammaker-ec2}"
EC2_ROLE_NAME="${EC2_ROLE_NAME:-aem-exammaker-ec2-role}"
BACKEND_ROLE_NAME="${BACKEND_ROLE_NAME:-aem-github-backend-deploy-role}"
GITHUB_REPO="${GITHUB_REPO:-flwndyy/new-ai-exam-maker}"
GITHUB_WORKFLOW_NAME="${GITHUB_WORKFLOW_NAME:-Deploy backend}"

command -v aws >/dev/null 2>&1 || { echo "ERROR: aws CLI is required." >&2; exit 1; }
command -v jq >/dev/null 2>&1 || { echo "ERROR: jq is required." >&2; exit 1; }

if [ "$AWS_REGION" != "$EXPECTED_REGION" ] && [ "$ALLOW_NON_DEFAULT_REGION" != "1" ]; then
  echo "ERROR: AWS_REGION is '${AWS_REGION}', expected '${EXPECTED_REGION}'." >&2
  exit 1
fi

PASSED=0
FAILURES=0
pass() { echo "[PASS] $1"; PASSED=$((PASSED + 1)); }
fail() { echo "[FAIL] $1" >&2; FAILURES=$((FAILURES + 1)); }

ACCOUNT_ID="$(aws sts get-caller-identity --query Account --output text 2>/dev/null | tr -d '\r\n' || true)"
if ! [[ "$ACCOUNT_ID" =~ ^[0-9]{12}$ ]]; then
  echo "ERROR: Failed to resolve 12-digit AWS Account ID via sts get-caller-identity." >&2
  exit 1
fi

if [ -z "${BACKEND_ARTIFACTS_BUCKET:-}" ]; then
  if command -v sha256sum >/dev/null 2>&1; then
    HASH_SUFFIX="$(printf '%s' "exammaker-backend-artifacts-${ACCOUNT_ID}" | sha256sum | cut -c1-10)"
  elif command -v shasum >/dev/null 2>&1; then
    HASH_SUFFIX="$(printf '%s' "exammaker-backend-artifacts-${ACCOUNT_ID}" | shasum -a 256 | cut -c1-10)"
  else
    echo "ERROR: sha256sum or shasum is required (or set BACKEND_ARTIFACTS_BUCKET)." >&2
    exit 1
  fi
  BACKEND_ARTIFACTS_BUCKET="aem-${ENV_NAME}-backend-artifacts-${HASH_SUFFIX}"
fi

echo "==> Verifying Backend CI/CD infrastructure (${AWS_REGION}, role=${BACKEND_ROLE_NAME}, bucket=${BACKEND_ARTIFACTS_BUCKET})..."

# 1. GitHub OIDC provider exists
OIDC_ARN="arn:aws:iam::${ACCOUNT_ID}:oidc-provider/token.actions.githubusercontent.com"
if aws iam get-open-id-connect-provider --open-id-connect-provider-arn "$OIDC_ARN" >/dev/null 2>&1; then
  pass "GitHub OIDC identity provider (token.actions.githubusercontent.com) exists"
else
  fail "GitHub OIDC identity provider not found"
fi

# 2. Backend deploy IAM role exists & no AdministratorAccess
ROLE_JSON=""
if ROLE_JSON="$(aws iam get-role --role-name "$BACKEND_ROLE_NAME" --output json 2>/dev/null)"; then
  ATTACHED_JSON="$(aws iam list-attached-role-policies --role-name "$BACKEND_ROLE_NAME" --output json)"
  HAS_ADMIN="$(jq -r '[.AttachedPolicies[]? | select(.PolicyName == "AdministratorAccess")] | length' <<<"$ATTACHED_JSON" | tr -d '\r\n')"
  if [ "$HAS_ADMIN" -eq 0 ]; then
    pass "IAM role '${BACKEND_ROLE_NAME}' exists without AdministratorAccess"
  else
    fail "IAM role '${BACKEND_ROLE_NAME}' has AdministratorAccess attached (violates least privilege)"
  fi
else
  fail "IAM role '${BACKEND_ROLE_NAME}' not found"
fi

# 3. Trust policy locked to aud, sub (main), workflow ("Deploy backend"), ref (refs/heads/main)
if [ -n "$ROLE_JSON" ]; then
  TRUST_OK="$(jq -r \
    --arg oidc "$OIDC_ARN" \
    --arg sub "repo:${GITHUB_REPO}:ref:refs/heads/main" \
    --arg wf "$GITHUB_WORKFLOW_NAME" \
    '
      (.Role.AssumeRolePolicyDocument.Statement // []) |
      map(select(
        .Effect == "Allow" and
        .Action == "sts:AssumeRoleWithWebIdentity" and
        .Principal.Federated == $oidc and
        .Condition.StringEquals["token.actions.githubusercontent.com:aud"] == "sts.amazonaws.com" and
        .Condition.StringEquals["token.actions.githubusercontent.com:sub"] == $sub and
        .Condition.StringEquals["token.actions.githubusercontent.com:workflow"] == $wf and
        .Condition.StringEquals["token.actions.githubusercontent.com:ref"] == "refs/heads/main"
      )) | length > 0
    ' <<<"$ROLE_JSON" | tr -d '\r\n')"
  if [ "$TRUST_OK" = "true" ]; then
    pass "OIDC trust policy strictly locks repo '${GITHUB_REPO}', branch 'main', and workflow '${GITHUB_WORKFLOW_NAME}'"
  else
    fail "OIDC trust policy on '${BACKEND_ROLE_NAME}' does not strictly match required claims"
  fi
else
  fail "Cannot verify trust policy because role '${BACKEND_ROLE_NAME}' is missing"
fi

# 4. Inline least-privilege deploy policy exists
if aws iam get-role-policy --role-name "$BACKEND_ROLE_NAME" --policy-name "aem-github-backend-deploy-policy" >/dev/null 2>&1; then
  pass "Least-privilege inline policy 'aem-github-backend-deploy-policy' is attached to '${BACKEND_ROLE_NAME}'"
else
  fail "Inline policy 'aem-github-backend-deploy-policy' not found on '${BACKEND_ROLE_NAME}'"
fi

# 5. Private S3 artifacts bucket exists
if aws s3api head-bucket --bucket "$BACKEND_ARTIFACTS_BUCKET" >/dev/null 2>&1; then
  pass "S3 backend artifacts bucket '${BACKEND_ARTIFACTS_BUCKET}' exists"
else
  fail "S3 backend artifacts bucket '${BACKEND_ARTIFACTS_BUCKET}' not found"
fi

# 6. S3 Block Public Access enabled
PAB_JSON="$(aws s3api get-public-access-block --bucket "$BACKEND_ARTIFACTS_BUCKET" --output json 2>/dev/null || echo '{}')"
PAB_OK="$(jq -r '(.PublicAccessBlockConfiguration | (.BlockPublicAcls == true and .IgnorePublicAcls == true and .BlockPublicPolicy == true and .RestrictPublicBuckets == true)) // false' <<<"$PAB_JSON" | tr -d '\r\n')"
if [ "$PAB_OK" = "true" ]; then
  pass "S3 Block Public Access enabled (all 4 flags = true)"
else
  fail "S3 Block Public Access not fully enabled on '${BACKEND_ARTIFACTS_BUCKET}'"
fi

# 7. S3 SSE-S3 encryption enabled ($0)
ENC_JSON="$(aws s3api get-bucket-encryption --bucket "$BACKEND_ARTIFACTS_BUCKET" --output json 2>/dev/null || echo '{}')"
ENC_ALGO="$(jq -r '.ServerSideEncryptionConfiguration.Rules[0].ApplyServerSideEncryptionByDefault.SSEAlgorithm // empty' <<<"$ENC_JSON" | tr -d '\r\n')"
if [ "$ENC_ALGO" = "AES256" ]; then
  pass "S3 server-side encryption enabled (SSE-S3 AES256, \$0)"
else
  fail "Expected SSE-S3 AES256 on '${BACKEND_ARTIFACTS_BUCKET}', got '${ENC_ALGO}'"
fi

# 8. S3 Lifecycle expiration enabled for backend/releases/
LC_JSON="$(aws s3api get-bucket-lifecycle-configuration --bucket "$BACKEND_ARTIFACTS_BUCKET" --output json 2>/dev/null || echo '{}')"
LC_DAYS="$(jq -r '[.Rules[]? | select(.Status == "Enabled" and (.Filter.Prefix // "") == "backend/releases/") | .Expiration.Days][0] // empty' <<<"$LC_JSON" | tr -d '\r\n')"
if [ -n "$LC_DAYS" ] && [ "$LC_DAYS" -le 90 ]; then
  pass "S3 Lifecycle rule expires old release archives under 'backend/releases/' after ${LC_DAYS} days"
else
  fail "Missing or invalid S3 Lifecycle expiration rule for 'backend/releases/' (got '${LC_DAYS}')"
fi

# 9. EC2 role has read-only s3:GetObject access to backend/releases/*
if EC2_POL_JSON="$(aws iam get-role-policy --role-name "$EC2_ROLE_NAME" --policy-name "aem-ec2-read-backend-artifacts" --output json 2>/dev/null)"; then
  EC2_POL_OK="$(jq -r --arg res "arn:aws:s3:::${BACKEND_ARTIFACTS_BUCKET}/backend/releases/*" \
    '[.PolicyDocument.Statement[]? | select(.Effect == "Allow" and .Action == "s3:GetObject" and .Resource == $res)] | length > 0' \
    <<<"$EC2_POL_JSON" | tr -d '\r\n')"
  if [ "$EC2_POL_OK" = "true" ]; then
    pass "EC2 role '${EC2_ROLE_NAME}' has least-privilege s3:GetObject access to 'backend/releases/*'"
  else
    fail "EC2 role policy 'aem-ec2-read-backend-artifacts' does not match expected bucket prefix"
  fi
else
  fail "EC2 role '${EC2_ROLE_NAME}' missing inline policy 'aem-ec2-read-backend-artifacts'"
fi

# 10. Target EC2 instance SSM readiness (without printing InstanceId)
EC2_QUERY_JSON="$(aws ec2 describe-instances \
  --region "$AWS_REGION" \
  --filters "Name=tag:Name,Values=${EC2_TAG_NAME}" "Name=instance-state-name,Values=pending,running,stopping,stopped" \
  --output json)"
EC2_INSTANCE_ID="$(jq -r '([.Reservations[].Instances[]] | sort_by(.LaunchTime) | reverse | .[0].InstanceId) // empty' <<<"$EC2_QUERY_JSON" | tr -d '\r\n')"
EC2_STATE="$(jq -r '([.Reservations[].Instances[]] | sort_by(.LaunchTime) | reverse | .[0].State.Name) // empty' <<<"$EC2_QUERY_JSON" | tr -d '\r\n')"

if [ -n "$EC2_INSTANCE_ID" ]; then
  if [ "$EC2_STATE" = "running" ]; then
    SSM_PING="$(aws ssm describe-instance-information \
      --region "$AWS_REGION" \
      --filters "Key=InstanceIds,Values=${EC2_INSTANCE_ID}" \
      --query "InstanceInformationList[0].PingStatus" \
      --output text 2>/dev/null | tr -d '\r\n' || echo "Unknown")"
    if [ "$SSM_PING" = "Online" ]; then
      pass "EC2 instance (tag Name=${EC2_TAG_NAME}) is running and SSM Agent PingStatus=Online"
    else
      fail "EC2 instance (tag Name=${EC2_TAG_NAME}) is running but SSM PingStatus='${SSM_PING}' (expected Online)"
    fi
  else
    pass "EC2 instance (tag Name=${EC2_TAG_NAME}) found (state=${EC2_STATE}; start EC2 before running Deploy backend workflow)"
  fi
else
  fail "No EC2 instance found with tag Name='${EC2_TAG_NAME}' in ${AWS_REGION}"
fi

unset ACCOUNT_ID EC2_INSTANCE_ID

echo ""
if [ "$FAILURES" -eq 0 ]; then
  echo "ALL CHECKS PASSED (${PASSED}/${PASSED})."
  exit 0
else
  echo "VERIFICATION FAILED: ${PASSED} passed, ${FAILURES} failed." >&2
  exit 1
fi
