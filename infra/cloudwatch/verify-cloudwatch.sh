#!/usr/bin/env bash
# Verification script for Amazon CloudWatch Logs, Metric Filters, SNS Alerts, and Alarms (AI Exam Maker - Task O13).
# Audits 15 architecture, observability, and $0 Free Tier cost-control checks:
#   1. SSM Parameter Store CloudWatch metadata (CloudWatch/LogGroupName, CloudWatch/MetricNamespace)
#   2. CloudWatch Log Group exists with retentionInDays == 7, STANDARD class, and no paid custom KMS CMK
#   3. All 4 CloudWatch Logs Metric Filters exist and publish to ExamMaker/Dev
#   4. Amazon SNS Topic 'exammaker-dev-alerts' exists with >= MIN_SUBSCRIBERS email subscription(s)
#   5–14. All 10 required CloudWatch Alarms exist, ActionsEnabled=true, route to SNS topic, use Standard
#         resolution (Period=300s), and match expected metric/threshold/operator/dimensions
#   15. Cost guardrail: total alarms in region <= 10, 0 composite alarms, 0 high-resolution alarms,
#       and custom metrics in ExamMaker/Dev <= 10 (100% within AWS Free Tier = $0.00/month)
#
# Privacy: Never prints AWS Account ID, raw subscriber email addresses, EC2/EBS resource IDs, or unmasked ARNs.
#
# Dependencies: aws (CLI v2), jq (both pre-installed in AWS CloudShell).
#
# Usage:
#   ./infra/cloudwatch/verify-cloudwatch.sh
#   MIN_SUBSCRIBERS=4 ./infra/cloudwatch/verify-cloudwatch.sh

set -euo pipefail

export MSYS_NO_PATHCONV=1
export MSYS2_ARG_CONV_EXCL="*"

EXPECTED_REGION="ap-southeast-1"
AWS_REGION="${AWS_REGION:-$EXPECTED_REGION}"
ALLOW_NON_DEFAULT_REGION="${ALLOW_NON_DEFAULT_REGION:-0}"
ENV_NAME="${ENV_NAME:-dev}"
SSM_PREFIX="${SSM_PREFIX:-/exammaker/${ENV_NAME}}"
SNS_TOPIC_NAME="${SNS_TOPIC_NAME:-exammaker-${ENV_NAME}-alerts}"
EC2_TAG_NAME="${EC2_TAG_NAME:-aem-exammaker-ec2}"
MIN_SUBSCRIBERS="${MIN_SUBSCRIBERS:-1}"

command -v aws >/dev/null 2>&1 || { echo "ERROR: aws CLI is required." >&2; exit 1; }
command -v jq >/dev/null 2>&1 || { echo "ERROR: jq is required (pre-installed in AWS CloudShell; on Windows install via 'winget install jqlang.jq')." >&2; exit 1; }

if [ "$AWS_REGION" != "$EXPECTED_REGION" ] && [ "$ALLOW_NON_DEFAULT_REGION" != "1" ]; then
  echo "ERROR: AWS_REGION is '${AWS_REGION}', but AI Exam Maker requires '${EXPECTED_REGION}' (Singapore). Unset AWS_REGION, switch CloudShell to ${EXPECTED_REGION}, or set ALLOW_NON_DEFAULT_REGION=1 if intentional." >&2
  exit 1
fi

if ! [[ "$MIN_SUBSCRIBERS" =~ ^[0-9]+$ ]] || [ "$MIN_SUBSCRIBERS" -lt 1 ] || [ "$MIN_SUBSCRIBERS" -gt 10 ]; then
  echo "ERROR: MIN_SUBSCRIBERS must be an integer between 1 and 10 (got '${MIN_SUBSCRIBERS}')." >&2
  exit 1
fi

PASSED=0
FAILURES=0

pass() { echo "[PASS] $1"; PASSED=$((PASSED + 1)); }
fail() { echo "[FAIL] $1" >&2; FAILURES=$((FAILURES + 1)); }

echo "==> Verifying CloudWatch Logs, Metric Filters, SNS Alerts, and Alarms (${AWS_REGION}, env=${ENV_NAME}, min_subscribers=${MIN_SUBSCRIBERS})..."

# 1. SSM CloudWatch & target metadata
SSM_CW_JSON="$(aws ssm get-parameters-by-path --region "$AWS_REGION" --path "${SSM_PREFIX}/CloudWatch" --output json)"
SSM_RDS_JSON="$(aws ssm get-parameters-by-path --region "$AWS_REGION" --path "${SSM_PREFIX}/Rds" --output json)"
SSM_SQS_JSON="$(aws ssm get-parameters-by-path --region "$AWS_REGION" --path "${SSM_PREFIX}/Sqs" --output json)"

CW_LOG_GROUP_NAME="$(jq -r --arg n "${SSM_PREFIX}/CloudWatch/LogGroupName" '.Parameters[]? | select(.Name == $n) | .Value // empty' <<<"$SSM_CW_JSON" | tr -d '\r\n')"
CW_METRIC_NAMESPACE="$(jq -r --arg n "${SSM_PREFIX}/CloudWatch/MetricNamespace" '.Parameters[]? | select(.Name == $n) | .Value // empty' <<<"$SSM_CW_JSON" | tr -d '\r\n')"
RDS_INSTANCE_ID="$(jq -r --arg n "${SSM_PREFIX}/Rds/InstanceId" '.Parameters[]? | select(.Name == $n) | .Value // empty' <<<"$SSM_RDS_JSON" | tr -d '\r\n')"
SQS_MAIN_ARN="$(jq -r --arg n "${SSM_PREFIX}/Sqs/AiJobsQueueArn" '.Parameters[]? | select(.Name == $n) | .Value // empty' <<<"$SSM_SQS_JSON" | tr -d '\r\n')"
SQS_DLQ_ARN="$(jq -r --arg n "${SSM_PREFIX}/Sqs/AiJobsDlqArn" '.Parameters[]? | select(.Name == $n) | .Value // empty' <<<"$SSM_SQS_JSON" | tr -d '\r\n')"

if [ -n "$CW_LOG_GROUP_NAME" ] && [ -n "$CW_METRIC_NAMESPACE" ]; then
  pass "SSM CloudWatch metadata exists (LogGroupName='${CW_LOG_GROUP_NAME}', MetricNamespace='${CW_METRIC_NAMESPACE}')"
else
  fail "Missing SSM CloudWatch metadata under '${SSM_PREFIX}/CloudWatch/'"
  echo "SUMMARY: ${PASSED} passed, ${FAILURES} failed." >&2
  exit 1
fi

SQS_MAIN_QUEUE_NAME="${SQS_MAIN_ARN##*:}"
SQS_DLQ_NAME="${SQS_DLQ_ARN##*:}"

EC2_QUERY_JSON="$(aws ec2 describe-instances \
  --region "$AWS_REGION" \
  --filters "Name=tag:Name,Values=${EC2_TAG_NAME}" "Name=instance-state-name,Values=pending,running,stopping,stopped" \
  --output json)"
EC2_INSTANCE_ID="$(jq -r '([.Reservations[].Instances[]] | sort_by(.LaunchTime) | reverse | .[0].InstanceId) // empty' <<<"$EC2_QUERY_JSON" | tr -d '\r\n')"
EBS_VOLUME_ID="$(jq -r '([.Reservations[].Instances[]] | sort_by(.LaunchTime) | reverse | .[0] | (.RootDeviceName as $root | ([.BlockDeviceMappings[]? | select(.DeviceName == $root) | .Ebs.VolumeId][0]) // .BlockDeviceMappings[0].Ebs.VolumeId)) // empty' <<<"$EC2_QUERY_JSON" | tr -d '\r\n')"

# 2. CloudWatch Log Group exists, retentionInDays == 7, STANDARD class, no custom CMK
LG_JSON="$(aws logs describe-log-groups \
  --region "$AWS_REGION" \
  --log-group-name-prefix "$CW_LOG_GROUP_NAME" \
  --output json)"
LG_OBJ="$(jq -c --arg lg "$CW_LOG_GROUP_NAME" '.logGroups[]? | select(.logGroupName == $lg)' <<<"$LG_JSON")"

if [ -n "$LG_OBJ" ]; then
  LG_RET="$(jq -r '.retentionInDays // "never"' <<<"$LG_OBJ" | tr -d '\r\n')"
  LG_CLASS="$(jq -r '.logGroupClass // "STANDARD"' <<<"$LG_OBJ" | tr -d '\r\n')"
  LG_KMS="$(jq -r '.kmsKeyId // empty' <<<"$LG_OBJ" | tr -d '\r\n')"
  if [ "$LG_RET" = "7" ] && [ "$LG_CLASS" = "STANDARD" ] && [ -z "$LG_KMS" ]; then
    pass "Log Group '${CW_LOG_GROUP_NAME}' exists with retentionInDays=7, class=STANDARD, and AWS-managed encryption (\$0)"
  else
    fail "Log Group '${CW_LOG_GROUP_NAME}' misconfigured (retentionInDays='${LG_RET}' expected 7, class='${LG_CLASS}' expected STANDARD, custom_kms='${LG_KMS:+set}')"
  fi
else
  fail "Log Group '${CW_LOG_GROUP_NAME}' not found"
fi

# 3. CloudWatch Logs Metric Filters (all 4 filters exist and target CW_METRIC_NAMESPACE)
MF_JSON="$(aws logs describe-metric-filters \
  --region "$AWS_REGION" \
  --log-group-name "$CW_LOG_GROUP_NAME" \
  --output json 2>/dev/null || echo '{"metricFilters":[]}')"

check_filter_present() {
  local fname="$1"
  local mname="$2"
  local match=""
  match="$(jq -r --arg fn "$fname" --arg mn "$mname" --arg ns "$CW_METRIC_NAMESPACE" \
    '[.metricFilters[]? | select(.filterName == $fn and (.metricTransformations[0].metricName // "") == $mn and (.metricTransformations[0].metricNamespace // "") == $ns)] | length' \
    <<<"$MF_JSON" | tr -d '\r\n')"
  [ "$match" -eq 1 ]
}

if check_filter_present "exammaker-${ENV_NAME}-api-5xx-filter" "Api5xxCount" \
  && check_filter_present "exammaker-${ENV_NAME}-worker-heartbeat-filter" "WorkerHeartbeat" \
  && check_filter_present "exammaker-${ENV_NAME}-worker-stuck-jobs-filter" "StuckRunningJobs" \
  && check_filter_present "exammaker-${ENV_NAME}-worker-errors-filter" "WorkerErrorCount"; then
  pass "All 4 Metric Filters exist on '${CW_LOG_GROUP_NAME}' -> namespace '${CW_METRIC_NAMESPACE}' (Api5xxCount, WorkerHeartbeat, StuckRunningJobs, WorkerErrorCount)"
else
  fail "One or more required Metric Filters missing or misconfigured on '${CW_LOG_GROUP_NAME}'"
fi

# 4. Amazon SNS Alert Topic & Email Subscriptions (privacy-safe: never prints emails or Account ID)
TOPICS_JSON="$(aws sns list-topics --region "$AWS_REGION" --output json)"
SNS_TOPIC_ARN="$(jq -r --arg suffix ":${SNS_TOPIC_NAME}" '[.Topics[]?.TopicArn | select(endswith($suffix))][0] // empty' <<<"$TOPICS_JSON" | tr -d '\r\n')"

if [ -n "$SNS_TOPIC_ARN" ]; then
  SUBS_JSON="$(aws sns list-subscriptions-by-topic --region "$AWS_REGION" --topic-arn "$SNS_TOPIC_ARN" --output json)"
  TOTAL_EMAIL_SUBS="$(jq -r '[.Subscriptions[]? | select(.Protocol == "email")] | length' <<<"$SUBS_JSON" | tr -d '\r\n')"
  CONFIRMED_EMAIL_SUBS="$(jq -r '[.Subscriptions[]? | select(.Protocol == "email" and .SubscriptionArn != "PendingConfirmation" and .SubscriptionArn != "Deleted")] | length' <<<"$SUBS_JSON" | tr -d '\r\n')"
  PENDING_EMAIL_SUBS=$((TOTAL_EMAIL_SUBS - CONFIRMED_EMAIL_SUBS))
  if [ "$TOTAL_EMAIL_SUBS" -ge "$MIN_SUBSCRIBERS" ]; then
    pass "SNS Topic '${SNS_TOPIC_NAME}' exists with ${TOTAL_EMAIL_SUBS} email subscription(s) (confirmed=${CONFIRMED_EMAIL_SUBS}, pending_inbox_confirmation=${PENDING_EMAIL_SUBS})"
  else
    fail "SNS Topic '${SNS_TOPIC_NAME}' has only ${TOTAL_EMAIL_SUBS} email subscription(s) (expected >= ${MIN_SUBSCRIBERS})"
  fi
else
  fail "SNS Topic '${SNS_TOPIC_NAME}' not found in ${AWS_REGION}"
fi

# Fetch all alarms in region for Checks 5–15
ALL_ALARMS_JSON="$(aws cloudwatch describe-alarms --region "$AWS_REGION" --output json)"

check_alarm() {
  local label="$1"
  local alarm_name="$2"
  local exp_ns="$3"
  local exp_metric="$4"
  local exp_stat="$5"
  local exp_period="$6"
  local exp_eval="$7"
  local exp_thresh="$8"
  local exp_op="$9"
  local exp_missing="${10}"
  local exp_dim_name="${11:-}"
  local exp_dim_val="${12:-}"
  local dim_display="${13:-}"

  local a_obj=""
  a_obj="$(jq -c --arg an "$alarm_name" '.MetricAlarms[]? | select(.AlarmName == $an)' <<<"$ALL_ALARMS_JSON")"
  if [ -z "$a_obj" ]; then
    fail "${label}: alarm '${alarm_name}' not found"
    return 0
  fi

  local ok=""
  ok="$(jq -r \
    --arg ns "$exp_ns" \
    --arg mn "$exp_metric" \
    --arg st "$exp_stat" \
    --argjson per "$exp_period" \
    --argjson ev "$exp_eval" \
    --argjson th "$exp_thresh" \
    --arg op "$exp_op" \
    --arg tm "$exp_missing" \
    --arg sns "$SNS_TOPIC_ARN" \
    --arg dn "$exp_dim_name" \
    --arg dv "$exp_dim_val" \
    '
      (.ActionsEnabled == true) and
      (.Namespace == $ns) and
      (.MetricName == $mn) and
      (.Statistic == $st) and
      (.Period == $per) and
      (.EvaluationPeriods == $ev) and
      ((.Threshold | tonumber) == $th) and
      (.ComparisonOperator == $op) and
      ((.TreatMissingData // "missing") == $tm) and
      ((.AlarmActions // []) | index($sns) != null) and
      (if $dn == "" then
         ((.Dimensions // []) | length) == 0
       else
         ((.Dimensions // []) | map(select(.Name == $dn and .Value == $dv)) | length) == 1
       end)
    ' <<<"$a_obj" | tr -d '\r\n')"

  if [ "$ok" = "true" ]; then
    local dim_info=""
    if [ -n "$dim_display" ]; then
      dim_info=", target=${dim_display}"
    elif [ -n "$exp_dim_name" ]; then
      dim_info=", ${exp_dim_name}=${exp_dim_val}"
    fi
    pass "${label}: '${alarm_name}' (${exp_ns}::${exp_metric} ${exp_op} ${exp_thresh}, period=${exp_period}s x ${exp_eval}, missing=${exp_missing}${dim_info})"
  else
    fail "${label}: '${alarm_name}' configuration mismatch"
  fi
}

# 5. Mandatory Alarm #1: SQS DLQ depth > 0
check_alarm "Mandatory Alarm #1 (DLQ Depth)" \
  "exammaker-${ENV_NAME}-sqs-dlq-depth" \
  "AWS/SQS" "ApproximateNumberOfMessagesVisible" "Maximum" 300 1 0 \
  "GreaterThanThreshold" "notBreaching" \
  "QueueName" "$SQS_DLQ_NAME"

# 6. Mandatory Alarm #2: API 5xx >= 5
check_alarm "Mandatory Alarm #2 (API 5xx)" \
  "exammaker-${ENV_NAME}-api-5xx" \
  "$CW_METRIC_NAMESPACE" "Api5xxCount" "Sum" 300 1 5 \
  "GreaterThanOrEqualToThreshold" "notBreaching"

# 7. Mandatory Alarm #3: Worker Heartbeat < 1 (TreatMissingData=breaching)
check_alarm "Mandatory Alarm #3 (Worker Heartbeat)" \
  "exammaker-${ENV_NAME}-worker-heartbeat" \
  "$CW_METRIC_NAMESPACE" "WorkerHeartbeat" "Sum" 300 1 1 \
  "LessThanThreshold" "breaching"

# 8. Pipeline Alarm #4: SQS Oldest Message > 600s
check_alarm "Pipeline Alarm #4 (SQS Oldest Msg)" \
  "exammaker-${ENV_NAME}-sqs-oldest-msg" \
  "AWS/SQS" "ApproximateAgeOfOldestMessage" "Maximum" 300 1 600 \
  "GreaterThanThreshold" "notBreaching" \
  "QueueName" "$SQS_MAIN_QUEUE_NAME"

# 9. Pipeline Alarm #5: Worker Stuck Running Jobs > 0
check_alarm "Pipeline Alarm #5 (Stuck Running Jobs)" \
  "exammaker-${ENV_NAME}-worker-stuck-jobs" \
  "$CW_METRIC_NAMESPACE" "StuckRunningJobs" "Maximum" 300 1 0 \
  "GreaterThanThreshold" "notBreaching"

# 10. Pipeline Alarm #6: Worker Errors >= 3
check_alarm "Pipeline Alarm #6 (Worker Errors)" \
  "exammaker-${ENV_NAME}-worker-errors" \
  "$CW_METRIC_NAMESPACE" "WorkerErrorCount" "Sum" 300 1 3 \
  "GreaterThanOrEqualToThreshold" "notBreaching"

# 11. Infra Alarm #7: EC2 CPU > 80% (2 periods)
check_alarm "Infra Alarm #7 (EC2 High CPU)" \
  "exammaker-${ENV_NAME}-ec2-cpu-high" \
  "AWS/EC2" "CPUUtilization" "Average" 300 2 80 \
  "GreaterThanThreshold" "notBreaching" \
  "InstanceId" "$EC2_INSTANCE_ID" "EC2(tag:${EC2_TAG_NAME})"

# 12. Infra Alarm #8: EBS Root Volume Stalled IO > 0
check_alarm "Infra Alarm #8 (EBS Stalled IO)" \
  "exammaker-${ENV_NAME}-ebs-stalled-io" \
  "AWS/EBS" "VolumeStalledIOCheck" "Maximum" 300 1 0 \
  "GreaterThanThreshold" "notBreaching" \
  "VolumeId" "$EBS_VOLUME_ID" "EBS(root:${EC2_TAG_NAME})"

# 13. Infra Alarm #9: RDS Free Storage < 2 GB
check_alarm "Infra Alarm #9 (RDS Low Storage)" \
  "exammaker-${ENV_NAME}-rds-storage-low" \
  "AWS/RDS" "FreeStorageSpace" "Minimum" 300 1 2147483648 \
  "LessThanThreshold" "notBreaching" \
  "DBInstanceIdentifier" "$RDS_INSTANCE_ID"

# 14. Infra Alarm #10: RDS Database Connections > 50
check_alarm "Infra Alarm #10 (RDS High Connections)" \
  "exammaker-${ENV_NAME}-rds-connections-high" \
  "AWS/RDS" "DatabaseConnections" "Average" 300 1 50 \
  "GreaterThanThreshold" "notBreaching" \
  "DBInstanceIdentifier" "$RDS_INSTANCE_ID"

# 15. Cost guardrail: Free Tier alarm & custom metric ceiling (<= 10 standard alarms, 0 composite, 0 high-res, <= 10 custom metrics)
METRIC_ALARM_COUNT="$(jq -r '.MetricAlarms | length' <<<"$ALL_ALARMS_JSON" | tr -d '\r\n')"
COMPOSITE_ALARM_COUNT="$(jq -r '.CompositeAlarms | length' <<<"$ALL_ALARMS_JSON" | tr -d '\r\n')"
HIGH_RES_ALARM_COUNT="$(jq -r '[.MetricAlarms[]? | select((.Period // 300) < 60)] | length' <<<"$ALL_ALARMS_JSON" | tr -d '\r\n')"

CUSTOM_METRICS_JSON="$(aws cloudwatch list-metrics --region "$AWS_REGION" --namespace "$CW_METRIC_NAMESPACE" --output json)"
CUSTOM_METRIC_COUNT="$(jq -r '.Metrics | length' <<<"$CUSTOM_METRICS_JSON" | tr -d '\r\n')"

if [ "$METRIC_ALARM_COUNT" -le 10 ] && [ "$COMPOSITE_ALARM_COUNT" -eq 0 ] && [ "$HIGH_RES_ALARM_COUNT" -eq 0 ] && [ "$CUSTOM_METRIC_COUNT" -le 10 ]; then
  pass "Cost guardrail: ${METRIC_ALARM_COUNT}/10 standard alarms, ${COMPOSITE_ALARM_COUNT} composite alarms, ${HIGH_RES_ALARM_COUNT} high-res alarms, ${CUSTOM_METRIC_COUNT}/10 custom metrics in '${CW_METRIC_NAMESPACE}' (\$0.00 Free Tier)"
else
  fail "Cost guardrail exceeded Free Tier limits (metric_alarms=${METRIC_ALARM_COUNT}/10, composite=${COMPOSITE_ALARM_COUNT}, high_res=${HIGH_RES_ALARM_COUNT}, custom_metrics=${CUSTOM_METRIC_COUNT}/10)"
fi

echo ""
if [ "$FAILURES" -eq 0 ]; then
  echo "ALL CHECKS PASSED (${PASSED}/${PASSED})."
  exit 0
else
  echo "VERIFICATION FAILED: ${PASSED} passed, ${FAILURES} failed." >&2
  exit 1
fi
