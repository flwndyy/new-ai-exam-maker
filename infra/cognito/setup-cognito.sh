#!/usr/bin/env bash
# Idempotent setup script for Amazon Cognito User Pool (AI Exam Maker - Task O9).
# Creates or updates:
#   - Cognito User Pool (self sign-up disabled, email login, forgot password via email, Essentials tier)
#   - Groups: admin, head, teacher
#   - Managed Login domain (v2) + default branding
#   - Public SPA App Client (Authorization Code + PKCE, no client secret, 60-minute access token TTL)
#   - SSM Parameter Store entries under ${SSM_PREFIX}/Cognito/* (compatible with .NET ParameterStoreConfiguration)
#
# Usage (AWS CloudShell, Linux, macOS, or Git Bash):
#   COGNITO_DOMAIN_PREFIX="exammaker-dev-<unique-suffix>" ./infra/cognito/setup-cognito.sh
#
# With production/demo custom domain (once OPS-1 finalizes O4):
#   ENV_NAME=prod INCLUDE_LOCALHOST=false APP_DOMAIN="yourdomain.example" \
#     COGNITO_DOMAIN_PREFIX="exammaker-prod-<unique-suffix>" \
#     ./infra/cognito/setup-cognito.sh

set -euo pipefail

AWS_REGION="${AWS_REGION:-ap-southeast-1}"
ENV_NAME="${ENV_NAME:-dev}"
USER_POOL_NAME="${USER_POOL_NAME:-exammaker-${ENV_NAME}-users}"
APP_CLIENT_NAME="${APP_CLIENT_NAME:-exammaker-${ENV_NAME}-spa}"
SSM_PREFIX="${SSM_PREFIX:-/exammaker/${ENV_NAME}}"
COGNITO_DOMAIN_PREFIX="${COGNITO_DOMAIN_PREFIX:-}"
APP_DOMAIN="${APP_DOMAIN:-}"
INCLUDE_LOCALHOST="${INCLUDE_LOCALHOST:-$([ "$ENV_NAME" = "prod" ] && echo "false" || echo "true")}"

command -v aws >/dev/null 2>&1 || { echo "ERROR: aws CLI is required." >&2; exit 1; }

echo "==> Region: ${AWS_REGION} | Env: ${ENV_NAME} | SSM Prefix: ${SSM_PREFIX}"

# 1. Build callback and logout URL lists (trailingSlash: true in frontend/next.config.ts)
CALLBACK_URLS=()
LOGOUT_URLS=()

if [ "$INCLUDE_LOCALHOST" = "true" ]; then
  CALLBACK_URLS+=("http://localhost:3000/auth/callback/")
  LOGOUT_URLS+=("http://localhost:3000/")
fi

if [ -n "$APP_DOMAIN" ]; then
  CALLBACK_URLS+=("https://app.${APP_DOMAIN}/auth/callback/")
  LOGOUT_URLS+=("https://app.${APP_DOMAIN}/")
fi

if [ "${#CALLBACK_URLS[@]}" -eq 0 ]; then
  echo "ERROR: At least one callback URL is required. Set APP_DOMAIN or INCLUDE_LOCALHOST=true." >&2
  exit 1
fi

POLICIES_JSON='{"PasswordPolicy":{"MinimumLength":10,"RequireUppercase":true,"RequireLowercase":true,"RequireNumbers":true,"RequireSymbols":false,"TemporaryPasswordValidityDays":7}}'
RECOVERY_JSON='{"RecoveryMechanisms":[{"Priority":1,"Name":"verified_email"}]}'
SCHEMA_JSON='[{"Name":"email","AttributeDataType":"String","Required":true,"Mutable":true},{"Name":"name","AttributeDataType":"String","Required":false,"Mutable":true}]'

# 2. Create or update User Pool (self sign-up disabled)
USER_POOL_ID="$(aws cognito-idp list-user-pools \
  --region "$AWS_REGION" \
  --max-results 60 \
  --query "UserPools[?Name=='${USER_POOL_NAME}'].Id | [0]" \
  --output text)"

if [ -z "$USER_POOL_ID" ] || [ "$USER_POOL_ID" = "None" ]; then
  echo "==> Creating Cognito User Pool '${USER_POOL_NAME}'..."
  USER_POOL_ID="$(aws cognito-idp create-user-pool \
    --region "$AWS_REGION" \
    --pool-name "$USER_POOL_NAME" \
    --user-pool-tier ESSENTIALS \
    --username-attributes email \
    --username-configuration CaseSensitive=false \
    --auto-verified-attributes email \
    --admin-create-user-config AllowAdminCreateUserOnly=true \
    --policies "$POLICIES_JSON" \
    --account-recovery-setting "$RECOVERY_JSON" \
    --email-configuration EmailSendingAccount=COGNITO_DEFAULT \
    --schema "$SCHEMA_JSON" \
    --deletion-protection INACTIVE \
    --query "UserPool.Id" \
    --output text)"
else
  echo "==> Reusing existing User Pool '${USER_POOL_NAME}' and syncing policies..."
  aws cognito-idp update-user-pool \
    --region "$AWS_REGION" \
    --user-pool-id "$USER_POOL_ID" \
    --user-pool-tier ESSENTIALS \
    --auto-verified-attributes email \
    --admin-create-user-config AllowAdminCreateUserOnly=true \
    --policies "$POLICIES_JSON" \
    --account-recovery-setting "$RECOVERY_JSON" \
    --email-configuration EmailSendingAccount=COGNITO_DEFAULT \
    --deletion-protection INACTIVE >/dev/null
fi

# 3. Ensure 3 role groups exist (must match Role enum in docs/api/openapi.yaml)
ensure_group() {
  local name="$1"
  local precedence="$2"
  local description="$3"
  if aws cognito-idp get-group --region "$AWS_REGION" --user-pool-id "$USER_POOL_ID" --group-name "$name" >/dev/null 2>&1; then
    echo "==> Group '${name}' already exists."
  else
    echo "==> Creating group '${name}' (precedence ${precedence})..."
    aws cognito-idp create-group \
      --region "$AWS_REGION" \
      --user-pool-id "$USER_POOL_ID" \
      --group-name "$name" \
      --precedence "$precedence" \
      --description "$description" >/dev/null
  fi
}

ensure_group "admin" 1 "System administrators (publish, user/catalog management, fallback review)"
ensure_group "head" 2 "Department heads (teacher permissions plus department review)"
ensure_group "teacher" 3 "Teachers (generate AI questions, manage own questions and exams)"

# 4. Ensure Managed Login domain (v2) exists
EXISTING_DOMAIN="$(aws cognito-idp describe-user-pool \
  --region "$AWS_REGION" \
  --user-pool-id "$USER_POOL_ID" \
  --query "UserPool.Domain" \
  --output text)"

if [ -n "$EXISTING_DOMAIN" ] && [ "$EXISTING_DOMAIN" != "None" ]; then
  DOMAIN_PREFIX="$EXISTING_DOMAIN"
  echo "==> Reusing existing Cognito domain prefix '${DOMAIN_PREFIX}'."
else
  if [ -z "$COGNITO_DOMAIN_PREFIX" ]; then
    echo "ERROR: COGNITO_DOMAIN_PREFIX is required on first setup (globally unique in ${AWS_REGION}, lowercase letters/numbers/hyphens)." >&2
    exit 1
  fi
  DOMAIN_PREFIX="$COGNITO_DOMAIN_PREFIX"
  echo "==> Creating Managed Login domain '${DOMAIN_PREFIX}' (version 2)..."
  aws cognito-idp create-user-pool-domain \
    --region "$AWS_REGION" \
    --user-pool-id "$USER_POOL_ID" \
    --domain "$DOMAIN_PREFIX" \
    --managed-login-version 2 >/dev/null
fi

# 5. Create or update Public SPA App Client (Authorization Code + PKCE, no client secret)
CLIENT_ID="$(aws cognito-idp list-user-pool-clients \
  --region "$AWS_REGION" \
  --user-pool-id "$USER_POOL_ID" \
  --max-results 60 \
  --query "UserPoolClients[?ClientName=='${APP_CLIENT_NAME}'].ClientId | [0]" \
  --output text)"

if [ -z "$CLIENT_ID" ] || [ "$CLIENT_ID" = "None" ]; then
  echo "==> Creating public PKCE App Client '${APP_CLIENT_NAME}'..."
  CLIENT_ID="$(aws cognito-idp create-user-pool-client \
    --region "$AWS_REGION" \
    --user-pool-id "$USER_POOL_ID" \
    --client-name "$APP_CLIENT_NAME" \
    --no-generate-secret \
    --allowed-o-auth-flows-user-pool-client \
    --allowed-o-auth-flows code \
    --allowed-o-auth-scopes openid email profile \
    --supported-identity-providers COGNITO \
    --explicit-auth-flows ALLOW_REFRESH_TOKEN_AUTH ALLOW_USER_SRP_AUTH \
    --callback-urls "${CALLBACK_URLS[@]}" \
    --logout-urls "${LOGOUT_URLS[@]}" \
    --access-token-validity 60 \
    --id-token-validity 60 \
    --refresh-token-validity 7 \
    --token-validity-units AccessToken=minutes,IdToken=minutes,RefreshToken=days \
    --prevent-user-existence-errors ENABLED \
    --enable-token-revocation \
    --query "UserPoolClient.ClientId" \
    --output text)"
else
  echo "==> Updating existing App Client '${APP_CLIENT_NAME}'..."
  aws cognito-idp update-user-pool-client \
    --region "$AWS_REGION" \
    --user-pool-id "$USER_POOL_ID" \
    --client-id "$CLIENT_ID" \
    --client-name "$APP_CLIENT_NAME" \
    --allowed-o-auth-flows-user-pool-client \
    --allowed-o-auth-flows code \
    --allowed-o-auth-scopes openid email profile \
    --supported-identity-providers COGNITO \
    --explicit-auth-flows ALLOW_REFRESH_TOKEN_AUTH ALLOW_USER_SRP_AUTH \
    --callback-urls "${CALLBACK_URLS[@]}" \
    --logout-urls "${LOGOUT_URLS[@]}" \
    --access-token-validity 60 \
    --id-token-validity 60 \
    --refresh-token-validity 7 \
    --token-validity-units AccessToken=minutes,IdToken=minutes,RefreshToken=days \
    --prevent-user-existence-errors ENABLED \
    --enable-token-revocation >/dev/null
fi

# 6. Ensure Managed Login branding exists for the App Client (required for Managed Login v2 UI)
if aws cognito-idp describe-managed-login-branding-by-client \
  --region "$AWS_REGION" \
  --user-pool-id "$USER_POOL_ID" \
  --client-id "$CLIENT_ID" >/dev/null 2>&1; then
  echo "==> Managed Login branding already exists for client."
else
  echo "==> Creating default Managed Login branding..."
  aws cognito-idp create-managed-login-branding \
    --region "$AWS_REGION" \
    --user-pool-id "$USER_POOL_ID" \
    --client-id "$CLIENT_ID" \
    --use-cognito-provided-values >/dev/null
fi

# 7. Publish non-secret configuration to SSM Parameter Store (Standard String = Free)
AUTHORITY="https://cognito-idp.${AWS_REGION}.amazonaws.com/${USER_POOL_ID}"
HOSTED_UI_DOMAIN="https://${DOMAIN_PREFIX}.auth.${AWS_REGION}.amazoncognito.com"

put_param() {
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
  echo "    Stored SSM parameter: ${name}"
}

echo "==> Writing Cognito configuration to SSM Parameter Store under '${SSM_PREFIX}/Cognito/'..."
put_param "${SSM_PREFIX}/Cognito/Region" "$AWS_REGION" "AWS region hosting the Cognito User Pool"
put_param "${SSM_PREFIX}/Cognito/UserPoolId" "$USER_POOL_ID" "Cognito User Pool ID for AI Exam Maker"
put_param "${SSM_PREFIX}/Cognito/ClientId" "$CLIENT_ID" "Cognito Public SPA PKCE App Client ID"
put_param "${SSM_PREFIX}/Cognito/Authority" "$AUTHORITY" "OIDC Issuer / Authority URL for JWT validation"
put_param "${SSM_PREFIX}/Cognito/HostedUiDomain" "$HOSTED_UI_DOMAIN" "Cognito Managed Login base URL"

echo ""
echo "DONE: Cognito User Pool and SSM parameters are ready."
echo "Share ONLY the SSM path '${SSM_PREFIX}/Cognito' with DEV-A and DEV-B (do not paste raw IDs in public chat)."
echo "Next step: run './infra/cognito/verify-cognito.sh' to verify all security settings."
