#!/usr/bin/env bash
# Verification script for Amazon Cognito User Pool & SSM Parameter Store (Task O9).
# Reads ${SSM_PREFIX}/Cognito/* from SSM and checks all 22 security & configuration
# requirements from docs/context.md §3.3, §5.4, §6, §9.2.
#
# Dependencies: aws (CLI v2), jq, curl (all pre-installed in AWS CloudShell).
#
# Usage:
#   ./infra/cognito/verify-cognito.sh
#   ENV_NAME=prod ./infra/cognito/verify-cognito.sh

set -euo pipefail

# Prevent Git Bash (MSYS2) on Windows from rewriting SSM paths starting with '/' into Windows paths.
export MSYS_NO_PATHCONV=1
export MSYS2_ARG_CONV_EXCL="*"

AWS_REGION="${AWS_REGION:-ap-southeast-1}"
ENV_NAME="${ENV_NAME:-dev}"
SSM_PREFIX="${SSM_PREFIX:-/exammaker/${ENV_NAME}}"
ALLOW_LOCALHOST="${ALLOW_LOCALHOST:-$([ "$ENV_NAME" = "prod" ] && echo "false" || echo "true")}"

command -v aws >/dev/null 2>&1 || { echo "ERROR: aws CLI is required." >&2; exit 1; }
command -v jq >/dev/null 2>&1 || { echo "ERROR: jq is required (pre-installed in AWS CloudShell; on Windows install via 'winget install jqlang.jq')." >&2; exit 1; }
command -v curl >/dev/null 2>&1 || { echo "ERROR: curl is required." >&2; exit 1; }

PASSED=0
FAILURES=0

pass() { echo "[PASS] $1"; PASSED=$((PASSED + 1)); }
fail() { echo "[FAIL] $1" >&2; FAILURES=$((FAILURES + 1)); }

echo "==> Reading SSM parameters from '${SSM_PREFIX}/Cognito' (${AWS_REGION}, env=${ENV_NAME}, allow_localhost=${ALLOW_LOCALHOST})..."

SSM_JSON="$(aws ssm get-parameters-by-path \
  --region "$AWS_REGION" \
  --path "${SSM_PREFIX}/Cognito" \
  --output json)"

get_ssm() {
  local suffix="$1"
  jq -r --arg name "${SSM_PREFIX}/Cognito/${suffix}" '.Parameters[]? | select(.Name == $name) | .Value' <<<"$SSM_JSON"
}

SSM_REGION="$(get_ssm "Region")"
USER_POOL_ID="$(get_ssm "UserPoolId")"
CLIENT_ID="$(get_ssm "ClientId")"
AUTHORITY="$(get_ssm "Authority")"
HOSTED_UI_DOMAIN="$(get_ssm "HostedUiDomain")"

# 1. All 5 SSM parameters exist, Region matches AWS_REGION, and Authority matches Region + UserPoolId
EXPECTED_AUTHORITY="https://cognito-idp.${AWS_REGION}.amazonaws.com/${USER_POOL_ID}"
if [ -n "$SSM_REGION" ] && [ -n "$USER_POOL_ID" ] && [ -n "$CLIENT_ID" ] && [ -n "$AUTHORITY" ] && [ -n "$HOSTED_UI_DOMAIN" ] && [ "$SSM_REGION" = "$AWS_REGION" ] && [ "$AUTHORITY" = "$EXPECTED_AUTHORITY" ]; then
  pass "All 5 SSM parameters exist under ${SSM_PREFIX}/Cognito/ (Region & Authority match pool)"
elif [ -z "$SSM_REGION" ] || [ -z "$USER_POOL_ID" ] || [ -z "$CLIENT_ID" ] || [ -z "$AUTHORITY" ] || [ -z "$HOSTED_UI_DOMAIN" ]; then
  echo "ERROR: Missing SSM parameters under ${SSM_PREFIX}/Cognito/ (expected Region, UserPoolId, ClientId, Authority, HostedUiDomain)." >&2
  exit 1
else
  fail "SSM Region/Authority mismatch (Region='${SSM_REGION}' expected '${AWS_REGION}'; Authority='${AUTHORITY}' expected '${EXPECTED_AUTHORITY}')"
fi

# Fetch User Pool and App Client once to minimize AWS API calls
POOL_JSON="$(aws cognito-idp describe-user-pool \
  --region "$AWS_REGION" \
  --user-pool-id "$USER_POOL_ID" \
  --output json)"

CLIENT_JSON="$(aws cognito-idp describe-user-pool-client \
  --region "$AWS_REGION" \
  --user-pool-id "$USER_POOL_ID" \
  --client-id "$CLIENT_ID" \
  --output json)"

# 2. Cognito User Pool tier is ESSENTIALS (Always Free <= 10,000 MAU; avoids PLUS billing)
POOL_TIER="$(jq -r '.UserPool.UserPoolTier // empty' <<<"$POOL_JSON")"
if [ "$POOL_TIER" = "ESSENTIALS" ]; then
  pass "UserPoolTier is ESSENTIALS (Always Free <= 10,000 MAU)"
else
  fail "UserPoolTier expected ESSENTIALS, got '${POOL_TIER}'"
fi

# 3. Self sign-up disabled
ALLOW_ADMIN_ONLY="$(jq -r '.UserPool.AdminCreateUserConfig.AllowAdminCreateUserOnly // false' <<<"$POOL_JSON")"
if [ "$ALLOW_ADMIN_ONLY" = "true" ]; then
  pass "Self sign-up is disabled (AllowAdminCreateUserOnly=true)"
else
  fail "Self sign-up is NOT disabled (AllowAdminCreateUserOnly=${ALLOW_ADMIN_ONLY})"
fi

# 4. Account recovery via verified_email (forgot password)
RECOVERY_MECHANISMS="$(jq -r '(.UserPool.AccountRecoverySetting.RecoveryMechanisms // []) | map(.Name) | join(",")' <<<"$POOL_JSON")"
if [ "$RECOVERY_MECHANISMS" = "verified_email" ]; then
  pass "Account recovery (forgot password) uses verified_email"
else
  fail "Account recovery expected 'verified_email', got '${RECOVERY_MECHANISMS}'"
fi

# 5. Managed Login domain exists, uses ManagedLoginVersion=2, and matches SSM HostedUiDomain
DOMAIN_PREFIX="$(jq -r '.UserPool.Domain // empty' <<<"$POOL_JSON")"
if [ -n "$DOMAIN_PREFIX" ]; then
  DOMAIN_JSON="$(aws cognito-idp describe-user-pool-domain \
    --region "$AWS_REGION" \
    --domain "$DOMAIN_PREFIX" \
    --output json)"
  MANAGED_LOGIN_VER="$(jq -r '.DomainDescription.ManagedLoginVersion // empty' <<<"$DOMAIN_JSON")"
  DOMAIN_STATUS="$(jq -r '.DomainDescription.Status // empty' <<<"$DOMAIN_JSON")"
  EXPECTED_HOSTED_UI_DOMAIN="https://${DOMAIN_PREFIX}.auth.${AWS_REGION}.amazoncognito.com"
  if [ "$MANAGED_LOGIN_VER" = "2" ] && [ "$DOMAIN_STATUS" = "ACTIVE" ] && [ "$HOSTED_UI_DOMAIN" = "$EXPECTED_HOSTED_UI_DOMAIN" ]; then
    pass "Managed Login domain is ACTIVE (v2) and matches SSM HostedUiDomain"
  else
    fail "Managed Login domain/SSM mismatch (status='${DOMAIN_STATUS}' version='${MANAGED_LOGIN_VER}' HostedUiDomain='${HOSTED_UI_DOMAIN}' expected '${EXPECTED_HOSTED_UI_DOMAIN}')"
  fi
else
  fail "Cognito User Pool has no Managed Login domain configured"
fi

# 6. Managed Login branding exists for App Client
if aws cognito-idp describe-managed-login-branding-by-client \
  --region "$AWS_REGION" \
  --user-pool-id "$USER_POOL_ID" \
  --client-id "$CLIENT_ID" >/dev/null 2>&1; then
  pass "Managed Login branding exists for App Client"
else
  fail "Managed Login branding is missing for App Client"
fi

# 7-9. Three required groups exist
for group in admin head teacher; do
  if aws cognito-idp get-group --region "$AWS_REGION" --user-pool-id "$USER_POOL_ID" --group-name "$group" >/dev/null 2>&1; then
    pass "Cognito group '${group}' exists"
  else
    fail "Cognito group '${group}' is missing"
  fi
done

# 10. Public SPA PKCE client has no ClientSecret
if jq -e '.UserPoolClient.ClientSecret == null or .UserPoolClient.ClientSecret == ""' <<<"$CLIENT_JSON" >/dev/null; then
  pass "App Client has no ClientSecret (public PKCE SPA client)"
else
  fail "App Client must NOT have a ClientSecret for public SPA PKCE"
fi

# 11. AllowedOAuthFlowsUserPoolClient is enabled
OAUTH_ENABLED="$(jq -r '.UserPoolClient.AllowedOAuthFlowsUserPoolClient // false' <<<"$CLIENT_JSON")"
if [ "$OAUTH_ENABLED" = "true" ]; then
  pass "AllowedOAuthFlowsUserPoolClient is enabled"
else
  fail "AllowedOAuthFlowsUserPoolClient expected true, got '${OAUTH_ENABLED}'"
fi

# 12. AllowedOAuthFlows is strictly ['code']
OAUTH_FLOWS="$(jq -r '(.UserPoolClient.AllowedOAuthFlows // []) | sort | join(",")' <<<"$CLIENT_JSON")"
if [ "$OAUTH_FLOWS" = "code" ]; then
  pass "AllowedOAuthFlows is strictly 'code' (Authorization Code + PKCE)"
else
  fail "AllowedOAuthFlows expected 'code', got '${OAUTH_FLOWS}'"
fi

# 13. AllowedOAuthScopes is exactly openid, email, profile
OAUTH_SCOPES="$(jq -r '(.UserPoolClient.AllowedOAuthScopes // []) | sort | join(",")' <<<"$CLIENT_JSON")"
if [ "$OAUTH_SCOPES" = "email,openid,profile" ]; then
  pass "AllowedOAuthScopes is strictly 'email,openid,profile'"
else
  fail "AllowedOAuthScopes expected 'email,openid,profile', got '${OAUTH_SCOPES}'"
fi

# 14. ExplicitAuthFlows is strictly ALLOW_REFRESH_TOKEN_AUTH (no direct SRP/password auth)
EXPLICIT_FLOWS="$(jq -r '(.UserPoolClient.ExplicitAuthFlows // []) | sort | join(",")' <<<"$CLIENT_JSON")"
if [ "$EXPLICIT_FLOWS" = "ALLOW_REFRESH_TOKEN_AUTH" ]; then
  pass "ExplicitAuthFlows is strictly 'ALLOW_REFRESH_TOKEN_AUTH' (no direct SRP/password auth)"
else
  fail "ExplicitAuthFlows expected 'ALLOW_REFRESH_TOKEN_AUTH', got '${EXPLICIT_FLOWS}'"
fi

# 15. Access token TTL is 60 minutes
ACCESS_TTL="$(jq -r '.UserPoolClient.AccessTokenValidity // empty' <<<"$CLIENT_JSON")"
ACCESS_UNIT="$(jq -r '.UserPoolClient.TokenValidityUnits.AccessToken // empty' <<<"$CLIENT_JSON")"
if [ "$ACCESS_TTL" = "60" ] && [ "$ACCESS_UNIT" = "minutes" ]; then
  pass "Access token TTL is 60 minutes"
else
  fail "Access token TTL expected 60 minutes, got '${ACCESS_TTL} ${ACCESS_UNIT}'"
fi

# 16. ID token TTL is 60 minutes
ID_TTL="$(jq -r '.UserPoolClient.IdTokenValidity // empty' <<<"$CLIENT_JSON")"
ID_UNIT="$(jq -r '.UserPoolClient.TokenValidityUnits.IdToken // empty' <<<"$CLIENT_JSON")"
if [ "$ID_TTL" = "60" ] && [ "$ID_UNIT" = "minutes" ]; then
  pass "ID token TTL is 60 minutes"
else
  fail "ID token TTL expected 60 minutes, got '${ID_TTL} ${ID_UNIT}'"
fi

# 17. Refresh token TTL is 7 days
REFRESH_TTL="$(jq -r '.UserPoolClient.RefreshTokenValidity // empty' <<<"$CLIENT_JSON")"
REFRESH_UNIT="$(jq -r '.UserPoolClient.TokenValidityUnits.RefreshToken // empty' <<<"$CLIENT_JSON")"
if [ "$REFRESH_TTL" = "7" ] && [ "$REFRESH_UNIT" = "days" ]; then
  pass "Refresh token TTL is 7 days"
else
  fail "Refresh token TTL expected 7 days, got '${REFRESH_TTL} ${REFRESH_UNIT}'"
fi

# 18. PreventUserExistenceErrors is ENABLED
PREVENT_ERRORS="$(jq -r '.UserPoolClient.PreventUserExistenceErrors // empty' <<<"$CLIENT_JSON")"
if [ "$PREVENT_ERRORS" = "ENABLED" ]; then
  pass "PreventUserExistenceErrors is ENABLED"
else
  fail "PreventUserExistenceErrors expected ENABLED, got '${PREVENT_ERRORS}'"
fi

# 19. EnableTokenRevocation is enabled
TOKEN_REVOCATION="$(jq -r '.UserPoolClient.EnableTokenRevocation // false' <<<"$CLIENT_JSON")"
if [ "$TOKEN_REVOCATION" = "true" ]; then
  pass "EnableTokenRevocation is enabled"
else
  fail "EnableTokenRevocation expected true, got '${TOKEN_REVOCATION}'"
fi

# 20. Callback URLs: non-empty, all end with '/auth/callback/', and no localhost when ALLOW_LOCALHOST=false
CALLBACK_OK="$(jq -r --arg allow_local "$ALLOW_LOCALHOST" '
  (.UserPoolClient.CallbackURLs // []) as $urls |
  if ($urls | length) == 0 then "empty"
  elif ($urls | all(endswith("/auth/callback/")) | not) then "missing_trailing_slash"
  elif ($allow_local == "false" and ($urls | any(test("^https?://(localhost|127\\.0\\.0\\.1)(:|/|$)")))) then "has_localhost"
  else "ok"
  end
' <<<"$CLIENT_JSON")"
CALLBACK_LIST="$(jq -r '(.UserPoolClient.CallbackURLs // []) | join(", ")' <<<"$CLIENT_JSON")"

if [ "$CALLBACK_OK" = "ok" ]; then
  pass "CallbackURLs valid (trailing '/auth/callback/', allow_localhost=${ALLOW_LOCALHOST})"
else
  fail "CallbackURLs check failed (${CALLBACK_OK}): ${CALLBACK_LIST}"
fi

# 21. Logout URLs: non-empty, and no localhost when ALLOW_LOCALHOST=false
LOGOUT_OK="$(jq -r --arg allow_local "$ALLOW_LOCALHOST" '
  (.UserPoolClient.LogoutURLs // []) as $urls |
  if ($urls | length) == 0 then "empty"
  elif ($allow_local == "false" and ($urls | any(test("^https?://(localhost|127\\.0\\.0\\.1)(:|/|$)")))) then "has_localhost"
  else "ok"
  end
' <<<"$CLIENT_JSON")"
LOGOUT_LIST="$(jq -r '(.UserPoolClient.LogoutURLs // []) | join(", ")' <<<"$CLIENT_JSON")"

if [ "$LOGOUT_OK" = "ok" ]; then
  pass "LogoutURLs valid (non-empty, allow_localhost=${ALLOW_LOCALHOST})"
else
  fail "LogoutURLs check failed (${LOGOUT_OK}): ${LOGOUT_LIST}"
fi

# 22. OIDC Discovery endpoint reachable (tolerant of network/DNS errors under set -e)
HTTP_CODE="000"
HTTP_CODE="$(curl -sS -o /dev/null -w "%{http_code}" "${AUTHORITY}/.well-known/openid-configuration")" || HTTP_CODE="000"
if [ "$HTTP_CODE" = "200" ]; then
  pass "OIDC discovery endpoint returned HTTP 200"
else
  fail "OIDC discovery endpoint returned HTTP ${HTTP_CODE}"
fi

TOTAL=$((PASSED + FAILURES))
echo ""
if [ "$FAILURES" -eq 0 ]; then
  echo "ALL CHECKS PASSED (${PASSED}/${TOTAL})."
  exit 0
else
  echo "FAILED: ${FAILURES}/${TOTAL} check(s) did not pass." >&2
  exit 1
fi
