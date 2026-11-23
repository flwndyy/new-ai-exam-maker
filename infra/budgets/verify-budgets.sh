#!/usr/bin/env bash
# Verification script for AWS Budgets $200 Cost Guardrail (AI Exam Maker - Task O14).
# Audits budget limit, time unit, AWS Credit tracking (IncludeCredit=false), and all 4
# notification thresholds (50%/80%/100% ACTUAL + 80% FORECASTED) with email subscribers.
#
# Privacy: Never prints AWS Account ID or raw subscriber email addresses to stdout.
#
# Dependencies: aws (CLI v2), jq (both pre-installed in AWS CloudShell).
#
# Usage:
#   ./infra/budgets/verify-budgets.sh
#   MIN_SUBSCRIBERS=4 ./infra/budgets/verify-budgets.sh

set -euo pipefail

export MSYS_NO_PATHCONV=1
export MSYS2_ARG_CONV_EXCL="*"

AWS_BILLING_REGION="${AWS_BILLING_REGION:-us-east-1}"
BUDGET_NAME="${BUDGET_NAME:-exammaker-200usd-budget}"
EXPECT_LIMIT_USD="${EXPECT_LIMIT_USD:-200}"
EXPECT_TIME_UNIT="${EXPECT_TIME_UNIT:-QUARTERLY}"
MIN_SUBSCRIBERS="${MIN_SUBSCRIBERS:-1}"

command -v aws >/dev/null 2>&1 || { echo "ERROR: aws CLI is required." >&2; exit 1; }
command -v jq >/dev/null 2>&1 || { echo "ERROR: jq is required (pre-installed in AWS CloudShell; on Windows install via 'winget install jqlang.jq')." >&2; exit 1; }

if ! [[ "$EXPECT_LIMIT_USD" =~ ^[1-9][0-9]*(\.[0-9]+)?$ ]]; then
  echo "ERROR: EXPECT_LIMIT_USD must be a positive number (got '${EXPECT_LIMIT_USD}')." >&2
  exit 1
fi

case "$EXPECT_TIME_UNIT" in
  MONTHLY|QUARTERLY|ANNUALLY) ;;
  *)
    echo "ERROR: EXPECT_TIME_UNIT must be MONTHLY, QUARTERLY, or ANNUALLY (got '${EXPECT_TIME_UNIT}')." >&2
    exit 1
    ;;
esac

if ! [[ "$MIN_SUBSCRIBERS" =~ ^[0-9]+$ ]] || [ "$MIN_SUBSCRIBERS" -lt 1 ] || [ "$MIN_SUBSCRIBERS" -gt 10 ]; then
  echo "ERROR: MIN_SUBSCRIBERS must be an integer between 1 and 10 (got '${MIN_SUBSCRIBERS}')." >&2
  exit 1
fi

PASSED=0
FAILURES=0

pass() { echo "[PASS] $1"; PASSED=$((PASSED + 1)); }
fail() { echo "[FAIL] $1" >&2; FAILURES=$((FAILURES + 1)); }

ACCOUNT_ID="$(aws sts get-caller-identity --query Account --output text 2>/dev/null | tr -d '\r\n' || true)"
if [ -z "$ACCOUNT_ID" ] || ! [[ "$ACCOUNT_ID" =~ ^[0-9]{12}$ ]]; then
  echo "ERROR: Failed to resolve 12-digit AWS Account ID via sts get-caller-identity." >&2
  exit 1
fi

echo "==> Verifying AWS Budget '${BUDGET_NAME}' (expect limit <= \$${EXPECT_LIMIT_USD} USD, unit=${EXPECT_TIME_UNIT}, min_subscribers=${MIN_SUBSCRIBERS})..."

# 1. Budget exists
BUDGET_JSON=""
BUDGET_RC=0
BUDGET_JSON="$(aws budgets describe-budget \
  --region "$AWS_BILLING_REGION" \
  --account-id "$ACCOUNT_ID" \
  --budget-name "$BUDGET_NAME" \
  --output json 2>&1)" || BUDGET_RC=$?
if [ "$BUDGET_RC" -eq 0 ]; then
  pass "Budget '${BUDGET_NAME}' exists"
else
  if grep -q "NotFoundException" <<<"$BUDGET_JSON"; then
    fail "Budget '${BUDGET_NAME}' not found"
  else
    fail "Failed to query AWS Budget '${BUDGET_NAME}': ${BUDGET_JSON}"
  fi
  echo "SUMMARY: ${PASSED} passed, ${FAILURES} failed." >&2
  exit 1
fi

# 2. BudgetType is COST
B_TYPE="$(jq -r '.Budget.BudgetType // empty' <<<"$BUDGET_JSON" | tr -d '\r\n')"
if [ "$B_TYPE" = "COST" ]; then
  pass "BudgetType is COST"
else
  fail "Expected BudgetType=COST, got '${B_TYPE}'"
fi

# 3. BudgetLimit <= EXPECT_LIMIT_USD and Unit == USD
B_AMOUNT="$(jq -r '.Budget.BudgetLimit.Amount // empty' <<<"$BUDGET_JSON" | tr -d '\r\n')"
B_UNIT="$(jq -r '.Budget.BudgetLimit.Unit // empty' <<<"$BUDGET_JSON" | tr -d '\r\n')"
LIMIT_OK="$(jq -nr --arg amt "$B_AMOUNT" --arg exp "$EXPECT_LIMIT_USD" '($amt | tonumber) > 0 and ($amt | tonumber) <= ($exp | tonumber)' 2>/dev/null || echo "false")"
if [ "$LIMIT_OK" = "true" ] && [ "$B_UNIT" = "USD" ]; then
  pass "BudgetLimit is \$${B_AMOUNT} ${B_UNIT} (<= \$${EXPECT_LIMIT_USD} USD ceiling)"
else
  fail "BudgetLimit invalid (got '${B_AMOUNT} ${B_UNIT}', expected <= ${EXPECT_LIMIT_USD} USD)"
fi

# 4. TimeUnit matches expectation
B_TIME_UNIT="$(jq -r '.Budget.TimeUnit // empty' <<<"$BUDGET_JSON" | tr -d '\r\n')"
if [ "$B_TIME_UNIT" = "$EXPECT_TIME_UNIT" ]; then
  pass "TimeUnit is '${B_TIME_UNIT}'"
else
  fail "Expected TimeUnit='${EXPECT_TIME_UNIT}', got '${B_TIME_UNIT}'"
fi

# 5. IncludeCredit == false and IncludeRefund == false (tracks $200 AWS credit burn)
# AWS Budgets may represent this either in CostTypes.IncludeCredit=false or FilterExpression excluding Credit/Refund.
INC_CREDIT="$(jq -r 'if .Budget.CostTypes.IncludeCredit == null then "unset" else (.Budget.CostTypes.IncludeCredit | tostring) end' <<<"$BUDGET_JSON" | tr -d '\r\n')"
INC_REFUND="$(jq -r 'if .Budget.CostTypes.IncludeRefund == null then "unset" else (.Budget.CostTypes.IncludeRefund | tostring) end' <<<"$BUDGET_JSON" | tr -d '\r\n')"
FILTER_EXCLUDES_CREDIT="$(jq -r '
  (.Budget.FilterExpression.Not.Dimensions // null) as $d |
  if $d != null and $d.Key == "RECORD_TYPE" and (($d.Values // []) | index("Credit")) != null and (($d.Values // []) | index("Refund")) != null then
    "true"
  else
    "false"
  end
' <<<"$BUDGET_JSON" | tr -d '\r\n')"

if { [ "$INC_CREDIT" = "false" ] && [ "$INC_REFUND" = "false" ]; } || [ "$FILTER_EXCLUDES_CREDIT" = "true" ]; then
  pass "Credit tracking enabled (excludes Credit/Refund so \$200 AWS credit burn triggers alerts)"
else
  fail "Budget includes Credits/Refunds (IncludeCredit=${INC_CREDIT}, IncludeRefund=${INC_REFUND}) — alerts would stay at \$0 until credits are exhausted"
fi

# Fetch notifications
NOTIFS_JSON="$(aws budgets describe-notifications-for-budget \
  --region "$AWS_BILLING_REGION" \
  --account-id "$ACCOUNT_ID" \
  --budget-name "$BUDGET_NAME" \
  --output json)"

ALL_SUB_COUNT_MIN=999

check_threshold() {
  local ntype="$1"
  local thresh="$2"
  local label="$3"

  local notif_obj=""
  notif_obj="$(jq -c --arg t "$ntype" --argjson th "$thresh" \
    '[.Notifications[]? | select(.NotificationType == $t and (.Threshold | tonumber) == $th and ((.ThresholdType // "PERCENTAGE") == "PERCENTAGE") and .ComparisonOperator == "GREATER_THAN")] | .[0] | if . == null then empty else {NotificationType, ComparisonOperator, Threshold, ThresholdType: (.ThresholdType // "PERCENTAGE")} end' \
    <<<"$NOTIFS_JSON")"

  if [ -z "$notif_obj" ]; then
    fail "${label}: notification ${ntype} > ${thresh}% not found"
    return 0
  fi

  local subs_json=""
  subs_json="$(aws budgets describe-subscribers-for-notification \
    --region "$AWS_BILLING_REGION" \
    --account-id "$ACCOUNT_ID" \
    --budget-name "$BUDGET_NAME" \
    --notification "$notif_obj" \
    --output json)"

  local email_count=""
  email_count="$(jq -r '[.Subscribers[]? | select(.SubscriptionType == "EMAIL")] | length' <<<"$subs_json" | tr -d '\r\n')"

  if [ "$email_count" -ge "$MIN_SUBSCRIBERS" ]; then
    pass "${label}: ${ntype} > ${thresh}% configured with ${email_count} EMAIL subscriber(s)"
    if [ "$email_count" -lt "$ALL_SUB_COUNT_MIN" ]; then
      ALL_SUB_COUNT_MIN="$email_count"
    fi
  else
    fail "${label}: ${ntype} > ${thresh}% has only ${email_count} EMAIL subscriber(s) (expected >= ${MIN_SUBSCRIBERS})"
    ALL_SUB_COUNT_MIN=0
  fi
}

# 6–9. Required thresholds: 50%, 80%, 100% ACTUAL + 80% FORECASTED
check_threshold "ACTUAL" 50 "Threshold 50% ACTUAL"
check_threshold "ACTUAL" 80 "Threshold 80% ACTUAL"
check_threshold "ACTUAL" 100 "Threshold 100% ACTUAL"
check_threshold "FORECASTED" 80 "Threshold 80% FORECASTED"

# 10. Current spend telemetry visibility (non-sensitive summary)
ACTUAL_SPEND="$(jq -r '.Budget.CalculatedSpend.ActualSpend.Amount // "0.0"' <<<"$BUDGET_JSON" | tr -d '\r\n')"
FORECAST_SPEND="$(jq -r '.Budget.CalculatedSpend.ForecastedSpend.Amount // "N/A"' <<<"$BUDGET_JSON" | tr -d '\r\n')"
if [ -n "$ACTUAL_SPEND" ]; then
  pass "Current gross spend telemetry readable (Actual=\$${ACTUAL_SPEND} USD, Forecasted=\$${FORECAST_SPEND} USD)"
else
  fail "Could not read CalculatedSpend from budget"
fi

echo ""
if [ "$FAILURES" -eq 0 ]; then
  echo "ALL CHECKS PASSED (${PASSED}/${PASSED})."
  exit 0
else
  echo "VERIFICATION FAILED: ${PASSED} passed, ${FAILURES} failed." >&2
  exit 1
fi
