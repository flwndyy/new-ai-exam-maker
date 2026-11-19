#!/usr/bin/env bash
# Idempotent setup script for Amazon Cognito User Pool (AI Exam Maker - Task O9).
# Creates or updates:
#   - Cognito User Pool (self sign-up disabled, email login, forgot password via email, Essentials tier)
#   - Groups: admin, head, teacher
#   - Managed Login domain (v2) + default branding
#   - Public SPA App Client (Authorization Code + PKCE, no client secret, 60m access/ID TTL, 7d refresh TTL)
#   - SSM Parameter Store entries under ${SSM_PREFIX}/Cognito/* (compatible with .NET ParameterStoreConfiguration)
#
# Dependencies: aws (CLI v2), jq (both pre-installed in AWS CloudShell).
#
# Usage (AWS CloudShell, Linux, macOS, or Git Bash with jq):
#   COGNITO_DOMAIN_PREFIX="exammaker-dev-<unique-suffix>" ./infra/cognito/setup-cognito.sh
#
# Add custom domain to existing dev pool (merges URLs by default):
#   APP_DOMAIN="yourdomain.example" ./infra/cognito/setup-cognito.sh
#
# Provision production/demo pool (no localhost URLs, DeletionProtection=ACTIVE by default):
#   ENV_NAME=prod INCLUDE_LOCALHOST=false APP_DOMAIN="yourdomain.example" \
#     COGNITO_DOMAIN_PREFIX="exammaker-prod-<unique-suffix>" \
#     ./infra/cognito/setup-cognito.sh

set -euo pipefail

# Prevent Git Bash (MSYS2) on Windows from rewriting SSM paths starting with '/' into Windows paths.
export MSYS_NO_PATHCONV=1
export MSYS2_ARG_CONV_EXCL="*"

AWS_REGION="${AWS_REGION:-ap-southeast-1}"
ENV_NAME="${ENV_NAME:-dev}"
USER_POOL_NAME="${USER_POOL_NAME:-exammaker-${ENV_NAME}-users}"
APP_CLIENT_NAME="${APP_CLIENT_NAME:-exammaker-${ENV_NAME}-spa}"
SSM_PREFIX="${SSM_PREFIX:-/exammaker/${ENV_NAME}}"
COGNITO_DOMAIN_PREFIX="${COGNITO_DOMAIN_PREFIX:-}"
APP_DOMAIN="${APP_DOMAIN:-}"
INCLUDE_LOCALHOST="${INCLUDE_LOCALHOST:-$([ "$ENV_NAME" = "prod" ] && echo "false" || echo "true")}"
REPLACE_URLS="${REPLACE_URLS:-false}"
DELETION_PROTECTION="${DELETION_PROTECTION:-}"

command -v aws >/dev/null 2>&1 || { echo "ERROR: aws CLI is required." >&2; exit 1; }
command -v jq >/dev/null 2>&1 || { echo "ERROR: jq is required (pre-installed in AWS CloudShell; on Windows install via 'winget install jqlang.jq')." >&2; exit 1; }

if [ -n "$DELETION_PROTECTION" ] && [ "$DELETION_PROTECTION" != "ACTIVE" ] && [ "$DELETION_PROTECTION" != "INACTIVE" ]; then
  echo "ERROR: DELETION_PROTECTION must be 'ACTIVE' or 'INACTIVE' (got '${DELETION_PROTECTION}')." >&2
  exit 1
fi

echo "==> Region: ${AWS_REGION} | Env: ${ENV_NAME} | SSM Prefix: ${SSM_PREFIX}"

# Helper: paginate through all User Pools and return the single ID matching USER_POOL_NAME.
find_user_pool_id_by_name() {
  local target_name="$1"
  local next_token=""
  local page_json=""
  local matches=()
  local id=""

  while true; do
    if [ -n "$next_token" ]; then
      page_json="$(aws cognito-idp list-user-pools \
        --region "$AWS_REGION" \
        --no-paginate \
        --max-results 60 \
        --next-token "$next_token" \
        --output json)"
    else
      page_json="$(aws cognito-idp list-user-pools \
        --region "$AWS_REGION" \
        --no-paginate \
        --max-results 60 \
        --output json)"
    fi

    while IFS= read -r id; do
      [ -n "$id" ] && matches+=("$id")
    done < <(jq -r --arg name "$target_name" '.UserPools[]? | select(.Name == $name) | .Id' <<<"$page_json")

    next_token="$(jq -r '.NextToken // empty' <<<"$page_json")"
    [ -z "$next_token" ] && break
  done

  if [ "${#matches[@]}" -gt 1 ]; then
    echo "ERROR: Multiple Cognito User Pools found with name '${target_name}': ${matches[*]}. Resolve duplicates before continuing." >&2
    exit 1
  fi

  if [ "${#matches[@]}" -eq 1 ]; then
    printf '%s\n' "${matches[0]}"
  fi
}

# Helper: paginate through all App Clients in a User Pool and return the single ClientId matching APP_CLIENT_NAME.
find_app_client_id_by_name() {
  local pool_id="$1"
  local target_name="$2"
  local next_token=""
  local page_json=""
  local matches=()
  local id=""

  while true; do
    if [ -n "$next_token" ]; then
      page_json="$(aws cognito-idp list-user-pool-clients \
        --region "$AWS_REGION" \
        --user-pool-id "$pool_id" \
        --no-paginate \
        --max-results 60 \
        --next-token "$next_token" \
        --output json)"
    else
      page_json="$(aws cognito-idp list-user-pool-clients \
        --region "$AWS_REGION" \
        --user-pool-id "$pool_id" \
        --no-paginate \
        --max-results 60 \
        --output json)"
    fi

    while IFS= read -r id; do
      [ -n "$id" ] && matches+=("$id")
    done < <(jq -r --arg name "$target_name" '.UserPoolClients[]? | select(.ClientName == $name) | .ClientId' <<<"$page_json")

    next_token="$(jq -r '.NextToken // empty' <<<"$page_json")"
    [ -z "$next_token" ] && break
  done

  if [ "${#matches[@]}" -gt 1 ]; then
    echo "ERROR: Multiple App Clients found with name '${target_name}': ${matches[*]}. Resolve duplicates before continuing." >&2
    exit 1
  fi

  if [ "${#matches[@]}" -eq 1 ]; then
    printf '%s\n' "${matches[0]}"
  fi
}

# 1. Build requested callback and logout URL lists (trailingSlash: true in frontend/next.config.ts)
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

if [ "${#CALLBACK_URLS[@]}" -gt 0 ]; then
  REQUESTED_CALLBACK_JSON="$(printf '%s\n' "${CALLBACK_URLS[@]}" | jq -R . | jq -s .)"
  REQUESTED_LOGOUT_JSON="$(printf '%s\n' "${LOGOUT_URLS[@]}" | jq -R . | jq -s .)"
else
  REQUESTED_CALLBACK_JSON="[]"
  REQUESTED_LOGOUT_JSON="[]"
fi

POLICIES_JSON='{"PasswordPolicy":{"MinimumLength":10,"RequireUppercase":true,"RequireLowercase":true,"RequireNumbers":true,"RequireSymbols":false,"TemporaryPasswordValidityDays":7}}'
RECOVERY_JSON='{"RecoveryMechanisms":[{"Priority":1,"Name":"verified_email"}]}'
SCHEMA_JSON='[{"Name":"email","AttributeDataType":"String","Required":true,"Mutable":true},{"Name":"name","AttributeDataType":"String","Required":false,"Mutable":true}]'

# 2. Create or safely reconcile User Pool (self sign-up disabled; preserves custom settings on update)
USER_POOL_ID="$(find_user_pool_id_by_name "$USER_POOL_NAME")"
CURRENT_POOL_JSON=""

if [ -z "$USER_POOL_ID" ]; then
  CREATE_DELETION_PROTECTION="${DELETION_PROTECTION:-$([ "$ENV_NAME" = "prod" ] && echo "ACTIVE" || echo "INACTIVE")}"
  echo "==> Creating Cognito User Pool '${USER_POOL_NAME}' (DeletionProtection=${CREATE_DELETION_PROTECTION})..."
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
    --deletion-protection "$CREATE_DELETION_PROTECTION" \
    --query "UserPool.Id" \
    --output text)"
else
  CURRENT_POOL_JSON="$(aws cognito-idp describe-user-pool \
    --region "$AWS_REGION" \
    --user-pool-id "$USER_POOL_ID" \
    --output json)"

  POOL_DRIFT="$(jq -r \
    --arg del_prot "$DELETION_PROTECTION" \
    '
    .UserPool as $p |
    (
      ($p.UserPoolTier != "ESSENTIALS") or
      ($p.AdminCreateUserConfig.AllowAdminCreateUserOnly != true) or
      ((($p.AutoVerifiedAttributes // []) | index("email")) == null) or
      ((($p.AccountRecoverySetting.RecoveryMechanisms // []) | map(.Name)) != ["verified_email"]) or
      ($p.Policies.PasswordPolicy.MinimumLength != 10) or
      ($p.Policies.PasswordPolicy.RequireUppercase != true) or
      ($p.Policies.PasswordPolicy.RequireLowercase != true) or
      ($p.Policies.PasswordPolicy.RequireNumbers != true) or
      ($p.Policies.PasswordPolicy.RequireSymbols != false) or
      ($p.Policies.PasswordPolicy.TemporaryPasswordValidityDays != 7) or
      ($del_prot != "" and $p.DeletionProtection != $del_prot)
    )
    ' <<<"$CURRENT_POOL_JSON")"

  if [ "$POOL_DRIFT" = "false" ]; then
    echo "==> Reusing existing User Pool '${USER_POOL_NAME}' (managed settings compliant; skipping update-user-pool)."
  else
    echo "==> Reconciling drifted settings on existing User Pool '${USER_POOL_NAME}' (preserving unmanaged settings)..."
    UPDATE_POOL_PAYLOAD="$(jq -c \
      --arg pool_id "$USER_POOL_ID" \
      --arg del_prot "$DELETION_PROTECTION" \
      '
      .UserPool as $p |
      ($p | {
        Policies,
        DeletionProtection,
        LambdaConfig,
        AutoVerifiedAttributes,
        SmsVerificationMessage,
        EmailVerificationMessage,
        EmailVerificationSubject,
        VerificationMessageTemplate,
        SmsAuthenticationMessage,
        UserAttributeUpdateSettings,
        MfaConfiguration,
        DeviceConfiguration,
        EmailConfiguration,
        SmsConfiguration,
        UserPoolTags,
        AdminCreateUserConfig,
        UserPoolAddOns,
        AccountRecoverySetting,
        UserPoolTier,
        KeyConfiguration,
        IssuerConfiguration,
        AcrConfiguration
      } | with_entries(select(.value != null)))
      | .UserPoolId = $pool_id
      | .UserPoolTier = "ESSENTIALS"
      | .AutoVerifiedAttributes = (((.AutoVerifiedAttributes // []) + ["email"]) | unique)
      | .Policies = ((.Policies // {}) * {
          PasswordPolicy: {
            MinimumLength: 10,
            RequireUppercase: true,
            RequireLowercase: true,
            RequireNumbers: true,
            RequireSymbols: false,
            TemporaryPasswordValidityDays: 7
          }
        })
      | .AdminCreateUserConfig = (((.AdminCreateUserConfig // {}) | del(.UnusedAccountValidityDays)) + {
          AllowAdminCreateUserOnly: true
        })
      | .AccountRecoverySetting = {
          RecoveryMechanisms: [{ Priority: 1, Name: "verified_email" }]
        }
      | .EmailConfiguration = (if (.EmailConfiguration // {}) == {} then { EmailSendingAccount: "COGNITO_DEFAULT" } else .EmailConfiguration end)
      | (if $del_prot != "" then .DeletionProtection = $del_prot else . end)
      | (if .VerificationMessageTemplate.EmailMessage != null then del(.EmailVerificationMessage) else . end)
      | (if .VerificationMessageTemplate.EmailSubject != null then del(.EmailVerificationSubject) else . end)
      | (if .VerificationMessageTemplate.SmsMessage != null then del(.SmsVerificationMessage) else . end)
      ' <<<"$CURRENT_POOL_JSON")"

    aws cognito-idp update-user-pool \
      --region "$AWS_REGION" \
      --cli-input-json "$UPDATE_POOL_PAYLOAD" >/dev/null
  fi
fi

# 3. Ensure 3 role groups exist (must match Role enum in docs/api/openapi.yaml)
ensure_group() {
  local name="$1"
  local precedence="$2"
  local description="$3"
  local err_out=""
  local rc=0

  err_out="$(aws cognito-idp get-group \
    --region "$AWS_REGION" \
    --user-pool-id "$USER_POOL_ID" \
    --group-name "$name" 2>&1)" || rc=$?

  if [ "$rc" -eq 0 ]; then
    echo "==> Group '${name}' already exists."
  elif grep -q "ResourceNotFoundException" <<<"$err_out"; then
    echo "==> Creating group '${name}' (precedence ${precedence})..."
    aws cognito-idp create-group \
      --region "$AWS_REGION" \
      --user-pool-id "$USER_POOL_ID" \
      --group-name "$name" \
      --precedence "$precedence" \
      --description "$description" >/dev/null
  else
    echo "ERROR: Failed to inspect group '${name}': ${err_out}" >&2
    exit 1
  fi
}

ensure_group "admin" 1 "System administrators (publish, user/catalog management, fallback review)"
ensure_group "head" 2 "Department heads (teacher permissions plus department review)"
ensure_group "teacher" 3 "Teachers (generate AI questions, manage own questions and exams)"

# 4. Ensure Managed Login domain (v2) exists
if [ -n "$CURRENT_POOL_JSON" ]; then
  EXISTING_DOMAIN="$(jq -r '.UserPool.Domain // empty' <<<"$CURRENT_POOL_JSON")"
else
  EXISTING_DOMAIN=""
fi

if [ -n "$EXISTING_DOMAIN" ]; then
  DOMAIN_PREFIX="$EXISTING_DOMAIN"
  EXISTING_DOMAIN_VER="$(aws cognito-idp describe-user-pool-domain \
    --region "$AWS_REGION" \
    --domain "$DOMAIN_PREFIX" \
    --query "DomainDescription.ManagedLoginVersion" \
    --output text)"
  if [ "$EXISTING_DOMAIN_VER" != "2" ]; then
    echo "==> Upgrading existing Cognito domain '${DOMAIN_PREFIX}' from version '${EXISTING_DOMAIN_VER}' to Managed Login v2..."
    aws cognito-idp update-user-pool-domain \
      --region "$AWS_REGION" \
      --user-pool-id "$USER_POOL_ID" \
      --domain "$DOMAIN_PREFIX" \
      --managed-login-version 2 >/dev/null
  else
    echo "==> Reusing existing Cognito domain prefix '${DOMAIN_PREFIX}' (Managed Login v2)."
  fi
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

# 5. Create or update Public SPA App Client (Authorization Code + PKCE, no client secret, no SRP)
CLIENT_ID="$(find_app_client_id_by_name "$USER_POOL_ID" "$APP_CLIENT_NAME")"

if [ -z "$CLIENT_ID" ]; then
  if [ "${#CALLBACK_URLS[@]}" -eq 0 ]; then
    echo "ERROR: At least one callback URL is required when creating App Client. Set APP_DOMAIN or INCLUDE_LOCALHOST=true." >&2
    exit 1
  fi
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
    --explicit-auth-flows ALLOW_REFRESH_TOKEN_AUTH \
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
  CURRENT_CLIENT_JSON="$(aws cognito-idp describe-user-pool-client \
    --region "$AWS_REGION" \
    --user-pool-id "$USER_POOL_ID" \
    --client-id "$CLIENT_ID" \
    --output json)"

  if jq -e '.UserPoolClient.ClientSecret != null and .UserPoolClient.ClientSecret != ""' <<<"$CURRENT_CLIENT_JSON" >/dev/null; then
    echo "ERROR: Existing App Client '${APP_CLIENT_NAME}' (${CLIENT_ID}) has a ClientSecret, which cannot be removed via update-user-pool-client. Delete the client or choose a different APP_CLIENT_NAME." >&2
    exit 1
  fi

  UPDATE_CLIENT_PAYLOAD="$(jq -c \
    --arg pool_id "$USER_POOL_ID" \
    --arg client_id "$CLIENT_ID" \
    --arg client_name "$APP_CLIENT_NAME" \
    --arg replace_urls "$REPLACE_URLS" \
    --arg include_localhost "$INCLUDE_LOCALHOST" \
    --argjson req_callbacks "$REQUESTED_CALLBACK_JSON" \
    --argjson req_logouts "$REQUESTED_LOGOUT_JSON" \
    '
    .UserPoolClient as $c |
    (
      (if $replace_urls == "true" then $req_callbacks else (($c.CallbackURLs // []) + $req_callbacks | unique) end)
      | if $include_localhost == "false" then map(select(test("^https?://(localhost|127\\.0\\.0\\.1)(:|/|$)") | not)) else . end
    ) as $final_callbacks |
    (
      (if $replace_urls == "true" then $req_logouts else (($c.LogoutURLs // []) + $req_logouts | unique) end)
      | if $include_localhost == "false" then map(select(test("^https?://(localhost|127\\.0\\.0\\.1)(:|/|$)") | not)) else . end
    ) as $final_logouts |
    ($c | {
      ReadAttributes,
      WriteAttributes,
      DefaultRedirectURI,
      AnalyticsConfiguration,
      EnablePropagateAdditionalUserContextData,
      AuthSessionValidity,
      RefreshTokenRotation
    } | with_entries(select(.value != null)))
    + {
      UserPoolId: $pool_id,
      ClientId: $client_id,
      ClientName: $client_name,
      AllowedOAuthFlowsUserPoolClient: true,
      AllowedOAuthFlows: ["code"],
      AllowedOAuthScopes: ["openid", "email", "profile"],
      SupportedIdentityProviders: ["COGNITO"],
      ExplicitAuthFlows: ["ALLOW_REFRESH_TOKEN_AUTH"],
      CallbackURLs: $final_callbacks,
      LogoutURLs: $final_logouts,
      AccessTokenValidity: 60,
      IdTokenValidity: 60,
      RefreshTokenValidity: 7,
      TokenValidityUnits: {
        AccessToken: "minutes",
        IdToken: "minutes",
        RefreshToken: "days"
      },
      PreventUserExistenceErrors: "ENABLED",
      EnableTokenRevocation: true
    }
    | .DefaultRedirectURI as $default_uri
    | if ($default_uri != null and ((.CallbackURLs | index($default_uri)) == null))
      then del(.DefaultRedirectURI)
      else .
      end
    ' <<<"$CURRENT_CLIENT_JSON")"

  if jq -e '(.CallbackURLs | length) == 0 or (.LogoutURLs | length) == 0' <<<"$UPDATE_CLIENT_PAYLOAD" >/dev/null; then
    echo "ERROR: Resulting CallbackURLs/LogoutURLs would be empty. Set APP_DOMAIN or INCLUDE_LOCALHOST=true." >&2
    exit 1
  fi

  echo "==> Updating existing App Client '${APP_CLIENT_NAME}' (merging URLs, preserving unmanaged settings)..."
  aws cognito-idp update-user-pool-client \
    --region "$AWS_REGION" \
    --cli-input-json "$UPDATE_CLIENT_PAYLOAD" >/dev/null
fi

# 6. Ensure Managed Login branding exists for the App Client (required for Managed Login v2 UI)
BRANDING_ERR=""
BRANDING_RC=0
BRANDING_ERR="$(aws cognito-idp describe-managed-login-branding-by-client \
  --region "$AWS_REGION" \
  --user-pool-id "$USER_POOL_ID" \
  --client-id "$CLIENT_ID" 2>&1)" || BRANDING_RC=$?

if [ "$BRANDING_RC" -eq 0 ]; then
  echo "==> Managed Login branding already exists for client."
elif grep -q "ResourceNotFoundException" <<<"$BRANDING_ERR"; then
  echo "==> Creating default Managed Login branding..."
  aws cognito-idp create-managed-login-branding \
    --region "$AWS_REGION" \
    --user-pool-id "$USER_POOL_ID" \
    --client-id "$CLIENT_ID" \
    --use-cognito-provided-values >/dev/null
else
  echo "ERROR: Failed to inspect Managed Login branding: ${BRANDING_ERR}" >&2
  exit 1
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
echo "Next step: run './infra/cognito/verify-cognito.sh' to verify all 22 security and configuration checks."
