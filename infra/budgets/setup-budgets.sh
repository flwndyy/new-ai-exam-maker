#!/usr/bin/env bash
# Create-if-absent setup script for AWS Budgets $200 Cost Guardrail (AI Exam Maker - Task O14).
# Creates a notification-only AWS Cost Budget ($0/month) tracking gross credit consumption
# (IncludeCredit=false, IncludeRefund=false) with 4 alert thresholds:
#   - ACTUAL     >= 50%  ($100)
#   - ACTUAL     >= 80%  ($160)
#   - ACTUAL     >= 100% ($200)
#   - FORECASTED >= 80%  ($160)
#
# Security & Privacy:
#   - Never hardcode email addresses or AWS Account ID in Git.
#   - Pass team emails via BUDGET_EMAILS_STDIN=1 (recommended on shared shells to avoid shell history)
#     or BUDGET_EMAILS="email1@...,email2@...,email3@...,email4@..." (private shell only).
#   - Script never prints raw email addresses or Account ID to stdout and never passes emails in jq/aws argv.
#
# Dependencies: aws (CLI v2), jq (both pre-installed in AWS CloudShell).
#
# Usage (AWS CloudShell, Linux, macOS, or Git Bash with jq):
#   BUDGET_EMAILS_STDIN=1 ./infra/budgets/setup-budgets.sh
#   BUDGET_EMAILS="devA@example.com,devB@example.com,ops1@example.com,ops2@example.com" ./infra/budgets/setup-budgets.sh
#   UPDATE_BUDGET=1 BUDGET_EMAILS_STDIN=1 ./infra/budgets/setup-budgets.sh

set -euo pipefail

export MSYS_NO_PATHCONV=1
export MSYS2_ARG_CONV_EXCL="*"

# AWS Budgets is a global billing API served via us-east-1 endpoint.
AWS_BILLING_REGION="${AWS_BILLING_REGION:-us-east-1}"
BUDGET_NAME="${BUDGET_NAME:-exammaker-200usd-budget}"
BUDGET_LIMIT_USD="${BUDGET_LIMIT_USD:-200}"
BUDGET_TIME_UNIT="${BUDGET_TIME_UNIT:-QUARTERLY}"
UPDATE_BUDGET="${UPDATE_BUDGET:-0}"
BUDGET_EMAILS="${BUDGET_EMAILS:-}"
BUDGET_EMAILS_STDIN="${BUDGET_EMAILS_STDIN:-0}"

command -v aws >/dev/null 2>&1 || { echo "ERROR: aws CLI is required." >&2; exit 1; }
command -v jq >/dev/null 2>&1 || { echo "ERROR: jq is required (pre-installed in AWS CloudShell; on Windows install via 'winget install jqlang.jq')." >&2; exit 1; }

if ! [[ "$BUDGET_LIMIT_USD" =~ ^[1-9][0-9]*(\.[0-9]+)?$ ]]; then
  echo "ERROR: BUDGET_LIMIT_USD must be a positive number (got '${BUDGET_LIMIT_USD}')." >&2
  exit 1
fi

case "$BUDGET_TIME_UNIT" in
  MONTHLY|QUARTERLY|ANNUALLY) ;;
  *)
    echo "ERROR: BUDGET_TIME_UNIT must be MONTHLY, QUARTERLY, or ANNUALLY (got '${BUDGET_TIME_UNIT}')." >&2
    exit 1
    ;;
esac

if [ "$BUDGET_EMAILS_STDIN" = "1" ]; then
  if [ -t 0 ]; then
    echo "Enter comma-separated team email addresses for AWS Budget notifications:" >&2
  fi
  if ! IFS= read -r BUDGET_EMAILS && [ -z "$BUDGET_EMAILS" ]; then
    echo "ERROR: Failed to read BUDGET_EMAILS from stdin (EOF)." >&2
    exit 1
  fi
fi

if [ -z "$BUDGET_EMAILS" ]; then
  echo "ERROR: BUDGET_EMAILS is required (comma-separated team emails, never committed to Git)." >&2
  echo "Preferred (no shell history): BUDGET_EMAILS_STDIN=1 ./infra/budgets/setup-budgets.sh" >&2
  echo "Or in a private shell:        BUDGET_EMAILS=\"member1@example.com,member2@example.com\" ./infra/budgets/setup-budgets.sh" >&2
  exit 1
fi

# Parse, trim, validate, and deduplicate email subscribers without echoing raw addresses
# or exposing email addresses in jq process argv (ps -ef).
SUBSCRIBERS_JSON="[]"
SUBSCRIBER_COUNT=0
IFS=',' read -r -a RAW_EMAILS <<<"$BUDGET_EMAILS"
for raw in "${RAW_EMAILS[@]}"; do
  email="$(printf '%s' "$raw" | tr -d '\r\n' | sed -e 's/^[[:space:]]*//' -e 's/[[:space:]]*$//')"
  [ -z "$email" ] && continue
  if ! [[ "$email" =~ ^[^[:space:]@]+@[^[:space:]@]+\.[^[:space:]@]+$ ]]; then
    echo "ERROR: Invalid email format detected in BUDGET_EMAILS." >&2
    exit 1
  fi
  if [ "$(EMAIL_VAL="$email" jq -r '[.[] | select(.Address == env.EMAIL_VAL)] | length' <<<"$SUBSCRIBERS_JSON")" -eq 0 ]; then
    SUBSCRIBERS_JSON="$(EMAIL_VAL="$email" jq -c '. + [{SubscriptionType: "EMAIL", Address: env.EMAIL_VAL}]' <<<"$SUBSCRIBERS_JSON")"
    SUBSCRIBER_COUNT=$((SUBSCRIBER_COUNT + 1))
  fi
done

if [ "$SUBSCRIBER_COUNT" -eq 0 ]; then
  echo "ERROR: No valid email addresses found in BUDGET_EMAILS." >&2
  exit 1
fi

if [ "$SUBSCRIBER_COUNT" -gt 10 ]; then
  echo "ERROR: AWS Budgets allows at most 10 email subscribers per notification (got ${SUBSCRIBER_COUNT})." >&2
  exit 1
fi

ACCOUNT_ID="$(aws sts get-caller-identity --query Account --output text 2>/dev/null | tr -d '\r\n' || true)"
if [ -z "$ACCOUNT_ID" ] || ! [[ "$ACCOUNT_ID" =~ ^[0-9]{12}$ ]]; then
  echo "ERROR: Failed to resolve 12-digit AWS Account ID via sts get-caller-identity." >&2
  exit 1
fi

# Secure temporary directory for --cli-input-json payloads on Windows/Git Bash where /dev/stdin is unsupported by AWS CLI.
TMP_PAYLOAD_DIR=""
cleanup_tmp() {
  if [ -n "$TMP_PAYLOAD_DIR" ] && [ -d "$TMP_PAYLOAD_DIR" ]; then
    rm -rf "$TMP_PAYLOAD_DIR"
  fi
}
trap cleanup_tmp EXIT
trap 'cleanup_tmp; exit 130' INT
trap 'cleanup_tmp; exit 143' TERM

run_budgets_cli_json() {
  local subcommand="$1"
  local payload="$2"

  if [ -e /dev/stdin ] && ! [[ "$(uname -s 2>/dev/null || true)" =~ ^(MINGW|MSYS|CYGWIN) ]]; then
    printf '%s' "$payload" | aws budgets "$subcommand" \
      --region "$AWS_BILLING_REGION" \
      --cli-input-json file:///dev/stdin >/dev/null
    return 0
  fi

  if [ -z "$TMP_PAYLOAD_DIR" ]; then
    TMP_PAYLOAD_DIR="$(umask 077 && mktemp -d "${TMPDIR:-/tmp}/exammaker-budgets.XXXXXXXXXX")"
    chmod 700 "$TMP_PAYLOAD_DIR" 2>/dev/null || true
    if command -v icacls >/dev/null 2>&1 && command -v cygpath >/dev/null 2>&1; then
      local win_dir
      win_dir="$(cygpath -w "$TMP_PAYLOAD_DIR")"
      icacls "$win_dir" /inheritance:r /grant:r "${USERNAME:-$(id -un)}:(OI)(CI)F" >/dev/null 2>&1 || true
    fi
  fi

  local tmp_file="${TMP_PAYLOAD_DIR}/payload.json"
  (umask 077 && printf '%s' "$payload" >"$tmp_file")
  local file_arg="$tmp_file"
  if command -v cygpath >/dev/null 2>&1 && [[ "$(uname -s 2>/dev/null || true)" =~ ^(MINGW|MSYS|CYGWIN) ]]; then
    file_arg="$(cygpath -m "$tmp_file")"
  fi

  aws budgets "$subcommand" \
    --region "$AWS_BILLING_REGION" \
    --cli-input-json "file://${file_arg}" >/dev/null
  rm -f "$tmp_file"
}

check_budget_status() {
  local out=""
  local rc=0
  out="$(aws budgets describe-budget \
    --region "$AWS_BILLING_REGION" \
    --account-id "$ACCOUNT_ID" \
    --budget-name "$BUDGET_NAME" \
    --output json 2>&1)" || rc=$?
  if [ "$rc" -eq 0 ]; then
    echo "exists"
    return 0
  fi

  if grep -q "NotFoundException" <<<"$out"; then
    echo "missing"
    return 0
  fi

  echo "ERROR: Failed to inspect AWS Budget '${BUDGET_NAME}': ${out}" >&2
  return 1
}

# Build budget payload.
# IncludeCredit=false and IncludeRefund=false are CRITICAL so the $200 AWS credit burn
# triggers 50%/80%/100% alerts instead of staying at $0 until credits are exhausted.
BUDGET_JSON="$(jq -nc \
  --arg name "$BUDGET_NAME" \
  --arg amount "$BUDGET_LIMIT_USD" \
  --arg unit "$BUDGET_TIME_UNIT" \
  '{
    BudgetName: $name,
    BudgetLimit: {
      Amount: $amount,
      Unit: "USD"
    },
    CostTypes: {
      IncludeTax: true,
      IncludeSubscription: true,
      UseBlended: false,
      IncludeRefund: false,
      IncludeCredit: false,
      IncludeUpfront: true,
      IncludeRecurring: true,
      IncludeOtherSubscription: true,
      IncludeSupport: true,
      IncludeDiscount: true,
      UseAmortized: false
    },
    TimeUnit: $unit,
    BudgetType: "COST"
  }')"

NOTIFICATIONS_JSON="$(SUBS_ENV="$SUBSCRIBERS_JSON" jq -nc \
  '(env.SUBS_ENV | fromjson) as $subs | [
    {
      Notification: {
        NotificationType: "ACTUAL",
        ComparisonOperator: "GREATER_THAN",
        Threshold: 50,
        ThresholdType: "PERCENTAGE"
      },
      Subscribers: $subs
    },
    {
      Notification: {
        NotificationType: "ACTUAL",
        ComparisonOperator: "GREATER_THAN",
        Threshold: 80,
        ThresholdType: "PERCENTAGE"
      },
      Subscribers: $subs
    },
    {
      Notification: {
        NotificationType: "ACTUAL",
        ComparisonOperator: "GREATER_THAN",
        Threshold: 100,
        ThresholdType: "PERCENTAGE"
      },
      Subscribers: $subs
    },
    {
      Notification: {
        NotificationType: "FORECASTED",
        ComparisonOperator: "GREATER_THAN",
        Threshold: 80,
        ThresholdType: "PERCENTAGE"
      },
      Subscribers: $subs
    }
  ]')"

echo "==> Budget: ${BUDGET_NAME} | Limit: \$${BUDGET_LIMIT_USD} USD | TimeUnit: ${BUDGET_TIME_UNIT}"
echo "==> Credit tracking: IncludeCredit=false, IncludeRefund=false | Subscribers: ${SUBSCRIBER_COUNT} email(s)"

STATUS="$(check_budget_status)"
if [ "$STATUS" = "exists" ]; then
  if [ "$UPDATE_BUDGET" != "1" ]; then
    echo "==> [KEPT] Budget '${BUDGET_NAME}' already exists (set UPDATE_BUDGET=1 to update limit and notifications)."
  else
    echo "==> [UPDATING] Updating budget '${BUDGET_NAME}' configuration..."
    UPDATE_PAYLOAD="$(ACCT_ENV="$ACCOUNT_ID" BUDGET_ENV="$BUDGET_JSON" jq -nc \
      '{AccountId: env.ACCT_ENV, NewBudget: (env.BUDGET_ENV | fromjson)}')"
    run_budgets_cli_json "update-budget" "$UPDATE_PAYLOAD"

    # Ensure each required notification exists and has the requested subscribers
    EXISTING_NOTIFS="$(aws budgets describe-notifications-for-budget \
      --region "$AWS_BILLING_REGION" \
      --account-id "$ACCOUNT_ID" \
      --budget-name "$BUDGET_NAME" \
      --output json)"

    for idx in 0 1 2 3; do
      notif_obj="$(jq -c ".[$idx].Notification" <<<"$NOTIFICATIONS_JSON")"
      ntype="$(jq -r '.NotificationType' <<<"$notif_obj")"
      nthresh="$(jq -r '.Threshold' <<<"$notif_obj")"
      exists_notif="$(jq -r --arg t "$ntype" --argjson th "$nthresh" \
        '[.Notifications[]? | select(.NotificationType == $t and (.Threshold | tonumber) == $th and ((.ThresholdType // "PERCENTAGE") == "PERCENTAGE") and .ComparisonOperator == "GREATER_THAN")] | length' \
        <<<"$EXISTING_NOTIFS")"

      if [ "$exists_notif" -eq 0 ]; then
        CREATE_NOTIF_PAYLOAD="$(ACCT_ENV="$ACCOUNT_ID" BNAME_ENV="$BUDGET_NAME" NOTIF_ENV="$notif_obj" SUBS_ENV="$SUBSCRIBERS_JSON" jq -nc \
          '{AccountId: env.ACCT_ENV, BudgetName: env.BNAME_ENV, Notification: (env.NOTIF_ENV | fromjson), Subscribers: (env.SUBS_ENV | fromjson)}')"
        run_budgets_cli_json "create-notification" "$CREATE_NOTIF_PAYLOAD"
        echo "    [CREATED]  Notification ${ntype} > ${nthresh}% (${SUBSCRIBER_COUNT} subscriber(s))"
      else
        # 1. Add any missing subscribers first (so a notification is never left with 0 subscribers)
        existing_subs="$(aws budgets describe-subscribers-for-notification \
          --region "$AWS_BILLING_REGION" \
          --account-id "$ACCOUNT_ID" \
          --budget-name "$BUDGET_NAME" \
          --notification "$notif_obj" \
          --output json)"
        for sidx in $(seq 0 $((SUBSCRIBER_COUNT - 1))); do
          sub_obj="$(jq -c ".[$sidx]" <<<"$SUBSCRIBERS_JSON")"
          sub_addr="$(jq -r '.Address' <<<"$sub_obj")"
          has_sub="$(EMAIL_VAL="$sub_addr" jq -r '[.Subscribers[]? | select(.SubscriptionType == "EMAIL" and .Address == env.EMAIL_VAL)] | length' <<<"$existing_subs")"
          if [ "$has_sub" -eq 0 ]; then
            CREATE_SUB_PAYLOAD="$(ACCT_ENV="$ACCOUNT_ID" BNAME_ENV="$BUDGET_NAME" NOTIF_ENV="$notif_obj" SUB_ENV="$sub_obj" jq -nc \
              '{AccountId: env.ACCT_ENV, BudgetName: env.BNAME_ENV, Notification: (env.NOTIF_ENV | fromjson), Subscriber: (env.SUB_ENV | fromjson)}')"
            run_budgets_cli_json "create-subscriber" "$CREATE_SUB_PAYLOAD"
          fi
        done

        # 2. Remove stale EMAIL subscribers not in BUDGET_EMAILS
        stale_list="$(DESIRED_ENV="$SUBSCRIBERS_JSON" jq -c \
          '(env.DESIRED_ENV | fromjson) as $desired | [.Subscribers[]? | select(.SubscriptionType == "EMAIL") | .Address as $a | select(($desired | map(.Address) | index($a)) == null) | {SubscriptionType: "EMAIL", Address: $a}]' \
          <<<"$existing_subs")"
        stale_count="$(jq -r 'length' <<<"$stale_list")"
        if [ "$stale_count" -gt 0 ]; then
          for ridx in $(seq 0 $((stale_count - 1))); do
            rm_sub="$(jq -c ".[$ridx]" <<<"$stale_list")"
            DEL_SUB_PAYLOAD="$(ACCT_ENV="$ACCOUNT_ID" BNAME_ENV="$BUDGET_NAME" NOTIF_ENV="$notif_obj" SUB_ENV="$rm_sub" jq -nc \
              '{AccountId: env.ACCT_ENV, BudgetName: env.BNAME_ENV, Notification: (env.NOTIF_ENV | fromjson), Subscriber: (env.SUB_ENV | fromjson)}')"
            run_budgets_cli_json "delete-subscriber" "$DEL_SUB_PAYLOAD"
          done
        fi
        echo "    [SYNCED]   Notification ${ntype} > ${nthresh}% (${SUBSCRIBER_COUNT} subscriber(s), removed ${stale_count} stale)"
      fi
    done
  fi
else
  echo "==> [CREATING] Creating AWS Budget '${BUDGET_NAME}' with 4 alert thresholds (50%/80%/100% ACTUAL + 80% FORECASTED)..."
  CREATE_BUDGET_PAYLOAD="$(ACCT_ENV="$ACCOUNT_ID" BUDGET_ENV="$BUDGET_JSON" NOTIFS_ENV="$NOTIFICATIONS_JSON" jq -nc \
    '{AccountId: env.ACCT_ENV, Budget: (env.BUDGET_ENV | fromjson), NotificationsWithSubscribers: (env.NOTIFS_ENV | fromjson)}')"
  run_budgets_cli_json "create-budget" "$CREATE_BUDGET_PAYLOAD"
  echo "    [CREATED]  Budget '${BUDGET_NAME}' (\$${BUDGET_LIMIT_USD} USD ${BUDGET_TIME_UNIT}, ${SUBSCRIBER_COUNT} email subscriber(s))"
fi

echo ""
echo "DONE: AWS Budget '${BUDGET_NAME}' is configured (\$0/month notification-only budget)."
echo "Reminder: AWS Budgets only sends alerts and does NOT automatically stop AWS resources."
echo "Next step: run './infra/budgets/verify-budgets.sh' to verify all 10 cost-control checks."
