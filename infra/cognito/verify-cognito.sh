#!/usr/bin/env bash
# Verification script for Amazon Cognito User Pool & SSM Parameter Store (Task O9).
# Reads ${SSM_PREFIX}/Cognito/* from SSM and checks all security requirements from docs/context.md §3.3.
#
# Usage:
#   ./infra/cognito/verify-cognito.sh
#   ENV_NAME=prod ./infra/cognito/verify-cognito.sh

set -euo pipefail

AWS_REGION="${AWS_REGION:-ap-southeast-1}"
ENV_NAME="${ENV_NAME:-dev}"
SSM_PREFIX="${SSM_PREFIX:-/exammaker/${ENV_NAME}}"

command -v aws >/dev/null 2>&1 || { echo "ERROR: aws CLI is required." >&2; exit 1; }
command -v curl >/dev/null 2>&1 || { echo "ERROR: curl is required." >&2; exit 1; }

FAILURES=0

pass() { echo "[PASS] $1"; }
fail() { echo "[FAIL] $1" >&2; FAILURES=$((FAILURES + 1)); }

get_param() {
  aws ssm get-parameter \
    --region "$AWS_REGION" \
    --name "$1" \
    --query "Parameter.Value" \
    --output text 2>/dev/null || true
}

echo "==> Reading SSM parameters from '${SSM_PREFIX}/Cognito/*' (${AWS_REGION})..."
USER_POOL_ID="$(get_param "${SSM_PREFIX}/Cognito/UserPoolId")"
CLIENT_ID="$(get_param "${SSM_PREFIX}/Cognito/ClientId")"
AUTHORITY="$(get_param "${SSM_PREFIX}/Cognito/Authority")"
HOSTED_UI_DOMAIN="$(get_param "${SSM_PREFIX}/Cognito/HostedUiDomain")"

if [ -z "$USER_POOL_ID" ] || [ -z "$CLIENT_ID" ] || [ -z "$AUTHORITY" ] || [ -z "$HOSTED_UI_DOMAIN" ]; then
  echo "ERROR: Missing one or more SSM parameters under ${SSM_PREFIX}/Cognito/." >&2
  exit 1
fi
pass "All required SSM parameters exist under ${SSM_PREFIX}/Cognito/"

# 1. Self sign-up disabled
ALLOW_ADMIN_ONLY="$(aws cognito-idp describe-user-pool \
  --region "$AWS_REGION" \
  --user-pool-id "$USER_POOL_ID" \
  --query "UserPool.AdminCreateUserConfig.AllowAdminCreateUserOnly" \
  --output text)"

if [ "$ALLOW_ADMIN_ONLY" = "True" ] || [ "$ALLOW_ADMIN_ONLY" = "true" ]; then
  pass "Self sign-up is disabled (AllowAdminCreateUserOnly=true)"
else
  fail "Self sign-up is NOT disabled (AllowAdminCreateUserOnly=${ALLOW_ADMIN_ONLY})"
fi

# 2. Account recovery via verified_email (forgot password)
RECOVERY_NAME="$(aws cognito-idp describe-user-pool \
  --region "$AWS_REGION" \
  --user-pool-id "$USER_POOL_ID" \
  --query "UserPool.AccountRecoverySetting.RecoveryMechanisms[0].Name" \
  --output text)"

if [ "$RECOVERY_NAME" = "verified_email" ]; then
  pass "Account recovery (forgot password) uses verified_email"
else
  fail "Account recovery expected verified_email, got '${RECOVERY_NAME}'"
fi

# 3. Three required groups exist
for group in admin head teacher; do
  if aws cognito-idp get-group --region "$AWS_REGION" --user-pool-id "$USER_POOL_ID" --group-name "$group" >/dev/null 2>&1; then
    pass "Cognito group '${group}' exists"
  else
    fail "Cognito group '${group}' is missing"
  fi
done

# 4. Public SPA PKCE client checks
CLIENT_SECRET="$(aws cognito-idp describe-user-pool-client \
  --region "$AWS_REGION" \
  --user-pool-id "$USER_POOL_ID" \
  --client-id "$CLIENT_ID" \
  --query "UserPoolClient.ClientSecret" \
  --output text)"

if [ -z "$CLIENT_SECRET" ] || [ "$CLIENT_SECRET" = "None" ]; then
  pass "App Client has no ClientSecret (public PKCE SPA client)"
else
  fail "App Client must NOT have a ClientSecret for public SPA PKCE"
fi

OAUTH_FLOWS="$(aws cognito-idp describe-user-pool-client \
  --region "$AWS_REGION" \
  --user-pool-id "$USER_POOL_ID" \
  --client-id "$CLIENT_ID" \
  --query "UserPoolClient.AllowedOAuthFlows" \
  --output text)"

if [ "$OAUTH_FLOWS" = "code" ]; then
  pass "AllowedOAuthFlows is strictly 'code' (Authorization Code + PKCE)"
else
  fail "AllowedOAuthFlows expected 'code', got '${OAUTH_FLOWS}'"
fi

ACCESS_TTL="$(aws cognito-idp describe-user-pool-client \
  --region "$AWS_REGION" \
  --user-pool-id "$USER_POOL_ID" \
  --client-id "$CLIENT_ID" \
  --query "UserPoolClient.AccessTokenValidity" \
  --output text)"
ACCESS_UNIT="$(aws cognito-idp describe-user-pool-client \
  --region "$AWS_REGION" \
  --user-pool-id "$USER_POOL_ID" \
  --client-id "$CLIENT_ID" \
  --query "UserPoolClient.TokenValidityUnits.AccessToken" \
  --output text)"

if [ "$ACCESS_TTL" = "60" ] && [ "$ACCESS_UNIT" = "minutes" ]; then
  pass "Access token TTL is 60 minutes"
else
  fail "Access token TTL expected 60 minutes, got '${ACCESS_TTL} ${ACCESS_UNIT}'"
fi

PREVENT_ERRORS="$(aws cognito-idp describe-user-pool-client \
  --region "$AWS_REGION" \
  --user-pool-id "$USER_POOL_ID" \
  --client-id "$CLIENT_ID" \
  --query "UserPoolClient.PreventUserExistenceErrors" \
  --output text)"

if [ "$PREVENT_ERRORS" = "ENABLED" ]; then
  pass "PreventUserExistenceErrors is ENABLED"
else
  fail "PreventUserExistenceErrors expected ENABLED, got '${PREVENT_ERRORS}'"
fi

TOKEN_REVOCATION="$(aws cognito-idp describe-user-pool-client \
  --region "$AWS_REGION" \
  --user-pool-id "$USER_POOL_ID" \
  --client-id "$CLIENT_ID" \
  --query "UserPoolClient.EnableTokenRevocation" \
  --output text)"

if [ "$TOKEN_REVOCATION" = "True" ] || [ "$TOKEN_REVOCATION" = "true" ]; then
  pass "EnableTokenRevocation is enabled"
else
  fail "EnableTokenRevocation is NOT enabled (${TOKEN_REVOCATION})"
fi

CALLBACK_URLS="$(aws cognito-idp describe-user-pool-client \
  --region "$AWS_REGION" \
  --user-pool-id "$USER_POOL_ID" \
  --client-id "$CLIENT_ID" \
  --query "UserPoolClient.CallbackURLs[]" \
  --output text)"

if echo "$CALLBACK_URLS" | grep -q "/auth/callback/"; then
  pass "Callback URL includes trailing slash '/auth/callback/' (matches Next.js trailingSlash: true)"
else
  fail "Callback URLs do not contain '/auth/callback/': ${CALLBACK_URLS}"
fi

# 5. OIDC Discovery endpoint reachable
HTTP_CODE="$(curl -sS -o /dev/null -w "%{http_code}" "${AUTHORITY}/.well-known/openid-configuration")"
if [ "$HTTP_CODE" = "200" ]; then
  pass "OIDC discovery endpoint returned HTTP 200"
else
  fail "OIDC discovery endpoint returned HTTP ${HTTP_CODE}"
fi

echo ""
if [ "$FAILURES" -eq 0 ]; then
  echo "ALL CHECKS PASSED."
  exit 0
else
  echo "FAILED: ${FAILURES} check(s) did not pass." >&2
  exit 1
fi
