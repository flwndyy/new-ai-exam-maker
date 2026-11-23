#!/usr/bin/env bash
# Idempotent setup script for Amazon CloudWatch Logs, Metric Filters, SNS Alerts, and Alarms (AI Exam Maker - Task O13).
# Provisions and reconciles observability within the AWS Free Tier ($0.00/month: <= 5 GB logs, <= 10 custom metrics, <= 10 standard alarms):
#   1. CloudWatch Log Group: /exammaker/${ENV_NAME}/app (7-day retention, STANDARD class, AWS-managed encryption = $0)
#   2. CloudWatch Logs Metric Filters (4 filters -> namespace ExamMaker/Dev):
#      - exammaker-${ENV_NAME}-api-5xx-filter          -> Api5xxCount
#      - exammaker-${ENV_NAME}-worker-heartbeat-filter -> WorkerHeartbeat
#      - exammaker-${ENV_NAME}-worker-stuck-jobs-filter -> StuckRunningJobs
#      - exammaker-${ENV_NAME}-worker-errors-filter    -> WorkerErrorCount
#   3. Amazon SNS Topic: exammaker-${ENV_NAME}-alerts (email subscriptions via ALERT_EMAILS_STDIN=1 or ALERT_EMAILS)
#   4. CloudWatch Standard-Resolution Alarms (10/10 Free Tier ceiling, Period=300s):
#      - [Mandatory #1] exammaker-${ENV_NAME}-sqs-dlq-depth        (AWS/SQS ApproximateNumberOfMessagesVisible > 0 on DLQ)
#      - [Mandatory #2] exammaker-${ENV_NAME}-api-5xx              (ExamMaker/Dev Api5xxCount >= 5 in 5m)
#      - [Mandatory #3] exammaker-${ENV_NAME}-worker-heartbeat     (ExamMaker/Dev WorkerHeartbeat < 1 in 5m, TreatMissingData=breaching)
#      - [Pipeline #4]  exammaker-${ENV_NAME}-sqs-oldest-msg       (AWS/SQS ApproximateAgeOfOldestMessage > 600s on Main Queue)
#      - [Pipeline #5]  exammaker-${ENV_NAME}-worker-stuck-jobs    (ExamMaker/Dev StuckRunningJobs > 0 in 5m)
#      - [Pipeline #6]  exammaker-${ENV_NAME}-worker-errors        (ExamMaker/Dev WorkerErrorCount >= 3 in 5m)
#      - [Infra #7]     exammaker-${ENV_NAME}-ec2-cpu-high         (AWS/EC2 CPUUtilization > 80% for 2x5m)
#      - [Infra #8]     exammaker-${ENV_NAME}-ebs-stalled-io       (AWS/EBS VolumeStalledIOCheck > 0 on EC2 root gp3 volume)
#      - [Infra #9]     exammaker-${ENV_NAME}-rds-storage-low      (AWS/RDS FreeStorageSpace < 2 GB on RDS PostgreSQL)
#      - [Infra #10]    exammaker-${ENV_NAME}-rds-connections-high (AWS/RDS DatabaseConnections > 50 on RDS PostgreSQL)
#
# Security & Privacy:
#   - Never hardcode email addresses, AWS Account ID, EC2 Instance ID, EBS Volume ID, or ARNs in Git.
#   - Never print raw email addresses, Account ID, EC2/EBS resource IDs, or unmasked ARNs to stdout.
#   - Never pass email addresses in jq or aws CLI process argv (ps -ef).
#
# Dependencies: aws (CLI v2), jq (both pre-installed in AWS CloudShell).
#
# Usage:
#   ALERT_EMAILS_STDIN=1 ./infra/cloudwatch/setup-cloudwatch.sh
#   ALERT_EMAILS="devA@example.com,devB@example.com,ops1@example.com,ops2@example.com" ./infra/cloudwatch/setup-cloudwatch.sh
#   ./infra/cloudwatch/setup-cloudwatch.sh   # Reconciles log group, filters, and alarms while keeping existing SNS subscribers

set -euo pipefail

export MSYS_NO_PATHCONV=1
export MSYS2_ARG_CONV_EXCL="*"

EXPECTED_REGION="ap-southeast-1"
AWS_REGION="${AWS_REGION:-$EXPECTED_REGION}"
ALLOW_NON_DEFAULT_REGION="${ALLOW_NON_DEFAULT_REGION:-0}"
ENV_NAME="${ENV_NAME:-dev}"
SSM_PREFIX="${SSM_PREFIX:-/exammaker/${ENV_NAME}}"

SNS_TOPIC_NAME="${SNS_TOPIC_NAME:-exammaker-${ENV_NAME}-alerts}"
LOG_RETENTION_DAYS="${LOG_RETENTION_DAYS:-7}"
EC2_TAG_NAME="${EC2_TAG_NAME:-aem-exammaker-ec2}"
HEARTBEAT_TREAT_MISSING="${HEARTBEAT_TREAT_MISSING:-breaching}"
ALERT_EMAILS="${ALERT_EMAILS:-}"
ALERT_EMAILS_STDIN="${ALERT_EMAILS_STDIN:-0}"

command -v aws >/dev/null 2>&1 || { echo "ERROR: aws CLI is required." >&2; exit 1; }
command -v jq >/dev/null 2>&1 || { echo "ERROR: jq is required (pre-installed in AWS CloudShell; on Windows install via 'winget install jqlang.jq')." >&2; exit 1; }

if [ "$AWS_REGION" != "$EXPECTED_REGION" ] && [ "$ALLOW_NON_DEFAULT_REGION" != "1" ]; then
  echo "ERROR: AWS_REGION is '${AWS_REGION}', but AI Exam Maker requires '${EXPECTED_REGION}' (Singapore). Unset AWS_REGION, switch CloudShell to ${EXPECTED_REGION}, or set ALLOW_NON_DEFAULT_REGION=1 if intentional." >&2
  exit 1
fi

if [ "$LOG_RETENTION_DAYS" != "7" ]; then
  echo "ERROR: LOG_RETENTION_DAYS must be 7 per docs/context.md §10 (got '${LOG_RETENTION_DAYS}')." >&2
  exit 1
fi

case "$HEARTBEAT_TREAT_MISSING" in
  breaching|notBreaching|ignore|missing) ;;
  *)
    echo "ERROR: Invalid HEARTBEAT_TREAT_MISSING='${HEARTBEAT_TREAT_MISSING}' (expected breaching, notBreaching, ignore, or missing)." >&2
    exit 1
    ;;
esac

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

run_sns_subscribe_json() {
  local payload="$1"

  if [ -e /dev/stdin ] && ! [[ "$(uname -s 2>/dev/null || true)" =~ ^(MINGW|MSYS|CYGWIN) ]]; then
    printf '%s' "$payload" | aws sns subscribe \
      --region "$AWS_REGION" \
      --cli-input-json file:///dev/stdin >/dev/null
    return 0
  fi

  if [ -z "$TMP_PAYLOAD_DIR" ]; then
    TMP_PAYLOAD_DIR="$(umask 077 && mktemp -d "${TMPDIR:-/tmp}/exammaker-cw.XXXXXXXXXX")"
    chmod 700 "$TMP_PAYLOAD_DIR" 2>/dev/null || true
    if command -v icacls >/dev/null 2>&1 && command -v cygpath >/dev/null 2>&1; then
      local win_dir
      win_dir="$(cygpath -w "$TMP_PAYLOAD_DIR")"
      icacls "$win_dir" /inheritance:r /grant:r "${USERNAME:-$(id -un)}:(OI)(CI)F" >/dev/null 2>&1 || true
    fi
  fi

  local tmp_file="${TMP_PAYLOAD_DIR}/sns-sub.json"
  (umask 077 && printf '%s' "$payload" >"$tmp_file")
  local file_arg="$tmp_file"
  if command -v cygpath >/dev/null 2>&1 && [[ "$(uname -s 2>/dev/null || true)" =~ ^(MINGW|MSYS|CYGWIN) ]]; then
    file_arg="$(cygpath -m "$tmp_file")"
  fi

  aws sns subscribe \
    --region "$AWS_REGION" \
    --cli-input-json "file://${file_arg}" >/dev/null
  rm -f "$tmp_file"
}

if [ "$ALERT_EMAILS_STDIN" = "1" ]; then
  if [ -t 0 ]; then
    echo "Enter comma-separated team email addresses for CloudWatch SNS alerts:" >&2
  fi
  if ! IFS= read -r ALERT_EMAILS && [ -z "$ALERT_EMAILS" ]; then
    echo "ERROR: Failed to read ALERT_EMAILS from stdin (EOF)." >&2
    exit 1
  fi
fi

# If ALERT_EMAILS was not explicitly provided, attempt to reuse the team email list already configured
# in AWS Budgets (Task O14: exammaker-200usd-budget) in memory so operators do not need to re-enter emails.
if [ -z "$ALERT_EMAILS" ] && [ "${FALLBACK_FROM_BUDGET:-1}" = "1" ]; then
  ACCT_ID="$(aws sts get-caller-identity --query Account --output text 2>/dev/null | tr -d '\r\n' || true)"
  if [[ "$ACCT_ID" =~ ^[0-9]{12}$ ]]; then
    BUDGET_NOTIF_QUERY='{"NotificationType":"ACTUAL","ComparisonOperator":"GREATER_THAN","Threshold":50,"ThresholdType":"PERCENTAGE"}'
    BUDGET_SUBS_OUT="$(aws budgets describe-subscribers-for-notification \
      --region us-east-1 \
      --account-id "$ACCT_ID" \
      --budget-name "${BUDGET_NAME:-exammaker-200usd-budget}" \
      --notification "$BUDGET_NOTIF_QUERY" \
      --output json 2>/dev/null || true)"
    if [ -n "$BUDGET_SUBS_OUT" ]; then
      ALERT_EMAILS="$(jq -r '[.Subscribers[]? | select(.SubscriptionType == "EMAIL") | .Address] | join(",")' <<<"$BUDGET_SUBS_OUT" | tr -d '\r\n')"
    fi
  fi
  unset ACCT_ID BUDGET_SUBS_OUT
fi

# Parse, trim, validate, and deduplicate optional ALERT_EMAILS without echoing raw addresses or exposing in jq argv.
DESIRED_EMAILS_JSON="[]"
DESIRED_EMAIL_COUNT=0
if [ -n "$ALERT_EMAILS" ]; then
  IFS=',' read -r -a RAW_EMAILS <<<"$ALERT_EMAILS"
  for raw in "${RAW_EMAILS[@]}"; do
    email="$(printf '%s' "$raw" | tr -d '\r\n' | sed -e 's/^[[:space:]]*//' -e 's/[[:space:]]*$//')"
    [ -z "$email" ] && continue
    if ! [[ "$email" =~ ^[^[:space:]@]+@[^[:space:]@]+\.[^[:space:]@]+$ ]]; then
      echo "ERROR: Invalid email format detected in ALERT_EMAILS." >&2
      exit 1
    fi
    if [ "$(EMAIL_VAL="$email" jq -r '[.[] | select((. | ascii_downcase) == (env.EMAIL_VAL | ascii_downcase))] | length' <<<"$DESIRED_EMAILS_JSON")" -eq 0 ]; then
      DESIRED_EMAILS_JSON="$(EMAIL_VAL="$email" jq -c '. + [env.EMAIL_VAL]' <<<"$DESIRED_EMAILS_JSON")"
      DESIRED_EMAIL_COUNT=$((DESIRED_EMAIL_COUNT + 1))
    fi
  done
fi

get_ssm_val() {
  local name="$1"
  local default_val="${2:-}"
  local out=""
  local rc=0
  out="$(aws ssm get-parameter --region "$AWS_REGION" --name "$name" --query "Parameter.Value" --output text 2>&1)" || rc=$?
  if [ "$rc" -eq 0 ]; then
    printf '%s' "$out" | tr -d '\r\n'
    return 0
  fi
  if grep -q "ParameterNotFound" <<<"$out" && [ -n "$default_val" ]; then
    printf '%s' "$default_val"
    return 0
  fi
  echo "ERROR: Failed to read SSM parameter '${name}': ${out}" >&2
  return 1
}

echo "==> Region: ${AWS_REGION} | Env: ${ENV_NAME} | SSM Prefix: ${SSM_PREFIX}"

# Resolve metadata from SSM Parameter Store (O10, O11, O12)
CW_LOG_GROUP_NAME="$(get_ssm_val "${SSM_PREFIX}/CloudWatch/LogGroupName" "/exammaker/${ENV_NAME}/app")"
CW_METRIC_NAMESPACE="$(get_ssm_val "${SSM_PREFIX}/CloudWatch/MetricNamespace" "ExamMaker/Dev")"
RDS_INSTANCE_ID="$(get_ssm_val "${SSM_PREFIX}/Rds/InstanceId" "exammaker-${ENV_NAME}-pg")"
SQS_MAIN_ARN="$(get_ssm_val "${SSM_PREFIX}/Sqs/AiJobsQueueArn" "")"
SQS_DLQ_ARN="$(get_ssm_val "${SSM_PREFIX}/Sqs/AiJobsDlqArn" "")"

SQS_MAIN_QUEUE_NAME="${SQS_MAIN_ARN##*:}"
SQS_DLQ_NAME="${SQS_DLQ_ARN##*:}"
if [ -z "$SQS_MAIN_QUEUE_NAME" ] || [ -z "$SQS_DLQ_NAME" ]; then
  echo "ERROR: Failed to resolve SQS queue names from '${SSM_PREFIX}/Sqs/AiJobsQueueArn' and 'AiJobsDlqArn'." >&2
  exit 1
fi

# Resolve EC2 InstanceId and Root EBS VolumeId dynamically by tag Name (never hardcoded or printed)
EC2_QUERY_JSON="$(aws ec2 describe-instances \
  --region "$AWS_REGION" \
  --filters "Name=tag:Name,Values=${EC2_TAG_NAME}" "Name=instance-state-name,Values=pending,running,stopping,stopped" \
  --output json)"

EC2_INSTANCE_ID="$(jq -r '([.Reservations[].Instances[]] | sort_by(.LaunchTime) | reverse | .[0].InstanceId) // empty' <<<"$EC2_QUERY_JSON" | tr -d '\r\n')"
EBS_VOLUME_ID="$(jq -r '([.Reservations[].Instances[]] | sort_by(.LaunchTime) | reverse | .[0] | (.RootDeviceName as $root | ([.BlockDeviceMappings[]? | select(.DeviceName == $root) | .Ebs.VolumeId][0]) // .BlockDeviceMappings[0].Ebs.VolumeId)) // empty' <<<"$EC2_QUERY_JSON" | tr -d '\r\n')"

if [ -z "$EC2_INSTANCE_ID" ] || [ -z "$EBS_VOLUME_ID" ]; then
  echo "ERROR: Could not resolve EC2 instance or root EBS volume for tag Name='${EC2_TAG_NAME}' in ${AWS_REGION}." >&2
  exit 1
fi

echo "==> Resolved targets from SSM & EC2 tags:"
echo "    LogGroup: ${CW_LOG_GROUP_NAME} | Namespace: ${CW_METRIC_NAMESPACE}"
echo "    SQS Main: ${SQS_MAIN_QUEUE_NAME} | SQS DLQ: ${SQS_DLQ_NAME}"
echo "    RDS DB:   ${RDS_INSTANCE_ID} | EC2 & Root EBS: resolved via tag Name='${EC2_TAG_NAME}'"

# ---------------------------------------------------------------------------
# 1. CloudWatch Log Group & 7-day retention
# ---------------------------------------------------------------------------
echo ""
echo "==> 1. Configuring CloudWatch Log Group '${CW_LOG_GROUP_NAME}' (retention=${LOG_RETENTION_DAYS} days)..."
LG_JSON="$(aws logs describe-log-groups \
  --region "$AWS_REGION" \
  --log-group-name-prefix "$CW_LOG_GROUP_NAME" \
  --output json)"
LG_EXISTS="$(jq -r --arg lg "$CW_LOG_GROUP_NAME" '[.logGroups[]? | select(.logGroupName == $lg)] | length' <<<"$LG_JSON" | tr -d '\r\n')"

if [ "$LG_EXISTS" -eq 0 ]; then
  aws logs create-log-group \
    --region "$AWS_REGION" \
    --log-group-name "$CW_LOG_GROUP_NAME" \
    --tags "Project=ai-exam-maker,Environment=${ENV_NAME},Task=O13"
  echo "    [CREATED]  Log Group '${CW_LOG_GROUP_NAME}'"
else
  aws logs tag-resource \
    --region "$AWS_REGION" \
    --resource-arn "$(jq -r --arg lg "$CW_LOG_GROUP_NAME" '.logGroups[]? | select(.logGroupName == $lg) | .arn' <<<"$LG_JSON" | sed 's/:\*$//')" \
    --tags "Project=ai-exam-maker,Environment=${ENV_NAME},Task=O13" 2>/dev/null || true
  echo "    [EXISTS]   Log Group '${CW_LOG_GROUP_NAME}'"
fi

aws logs put-retention-policy \
  --region "$AWS_REGION" \
  --log-group-name "$CW_LOG_GROUP_NAME" \
  --retention-in-days "$LOG_RETENTION_DAYS"
echo "    [SET]      Retention policy = ${LOG_RETENTION_DAYS} days"

# ---------------------------------------------------------------------------
# 2. CloudWatch Logs Metric Filters (4 filters -> namespace ExamMaker/Dev)
# ---------------------------------------------------------------------------
echo ""
echo "==> 2. Configuring CloudWatch Logs Metric Filters on '${CW_LOG_GROUP_NAME}' -> '${CW_METRIC_NAMESPACE}'..."

put_metric_filter() {
  local filter_name="$1"
  local pattern="$2"
  local metric_name="$3"

  aws logs put-metric-filter \
    --region "$AWS_REGION" \
    --log-group-name "$CW_LOG_GROUP_NAME" \
    --filter-name "$filter_name" \
    --filter-pattern "$pattern" \
    --metric-transformations "metricName=${metric_name},metricNamespace=${CW_METRIC_NAMESPACE},metricValue=1,unit=Count"
  echo "    [READY]    Filter '${filter_name}' -> ${CW_METRIC_NAMESPACE} :: ${metric_name}"
}

# Matches Serilog UseSerilogRequestLogging ("HTTP ... responded 500 in ...") and structured JSON ("StatusCode":500)
put_metric_filter \
  "exammaker-${ENV_NAME}-api-5xx-filter" \
  '?"responded 500" ?"responded 501" ?"responded 502" ?"responded 503" ?"responded 504" ?"\"StatusCode\":5"' \
  "Api5xxCount"

# Matches periodic worker heartbeat log lines ("WorkerHeartbeat" / "worker_heartbeat"); B6 may also call PutMetricData directly
put_metric_filter \
  "exammaker-${ENV_NAME}-worker-heartbeat-filter" \
  '?"WorkerHeartbeat" ?"worker_heartbeat"' \
  "WorkerHeartbeat"

# Matches worker lease recovery log lines when a job stays Running past lease timeout
put_metric_filter \
  "exammaker-${ENV_NAME}-worker-stuck-jobs-filter" \
  '?"StuckRunningJob" ?"LeaseExpired" ?"stuck_running_job"' \
  "StuckRunningJobs"

# Matches Serilog error/fatal log lines ([ERR], [FTL], or JSON Level Error/Fatal)
put_metric_filter \
  "exammaker-${ENV_NAME}-worker-errors-filter" \
  '?"[ERR]" ?"[FTL]" ?"\"Level\":\"Error\"" ?"\"Level\":\"Fatal\""' \
  "WorkerErrorCount"

# ---------------------------------------------------------------------------
# 3. Amazon SNS Alert Topic & Email Subscriptions
# ---------------------------------------------------------------------------
echo ""
echo "==> 3. Configuring Amazon SNS Topic '${SNS_TOPIC_NAME}'..."
SNS_TOPIC_ARN="$(aws sns create-topic \
  --region "$AWS_REGION" \
  --name "$SNS_TOPIC_NAME" \
  --tags "Key=Project,Value=ai-exam-maker" "Key=Environment,Value=${ENV_NAME}" "Key=Task,Value=O13" \
  --query "TopicArn" \
  --output text | tr -d '\r\n')"

if [ -z "$SNS_TOPIC_ARN" ]; then
  echo "ERROR: Failed to create or resolve SNS Topic '${SNS_TOPIC_NAME}'." >&2
  exit 1
fi
echo "    [READY]    SNS Topic '${SNS_TOPIC_NAME}'"

EXISTING_SUBS_JSON="$(aws sns list-subscriptions-by-topic \
  --region "$AWS_REGION" \
  --topic-arn "$SNS_TOPIC_ARN" \
  --output json)"

if [ "$DESIRED_EMAIL_COUNT" -gt 0 ]; then
  NEW_SUB_COUNT=0
  KEPT_SUB_COUNT=0
  for idx in $(seq 0 $((DESIRED_EMAIL_COUNT - 1))); do
    email="$(jq -r ".[$idx]" <<<"$DESIRED_EMAILS_JSON")"
    already_sub="$(EMAIL_VAL="$email" jq -r '[.Subscriptions[]? | select(.Protocol == "email" and (.Endpoint | ascii_downcase) == (env.EMAIL_VAL | ascii_downcase))] | length' <<<"$EXISTING_SUBS_JSON" | tr -d '\r\n')"
    if [ "$already_sub" -eq 0 ]; then
      SUB_PAYLOAD="$(TOPIC_ENV="$SNS_TOPIC_ARN" EMAIL_VAL="$email" jq -nc \
        '{TopicArn: env.TOPIC_ENV, Protocol: "email", Endpoint: env.EMAIL_VAL}')"
      run_sns_subscribe_json "$SUB_PAYLOAD"
      NEW_SUB_COUNT=$((NEW_SUB_COUNT + 1))
    else
      KEPT_SUB_COUNT=$((KEPT_SUB_COUNT + 1))
    fi
  done
  echo "    [SUBSCRIBED] Added ${NEW_SUB_COUNT} new email subscriber(s), kept ${KEPT_SUB_COUNT} existing subscriber(s) (check inbox to confirm if pending)."
else
  CURRENT_EMAIL_SUBS="$(jq -r '[.Subscriptions[]? | select(.Protocol == "email")] | length' <<<"$EXISTING_SUBS_JSON" | tr -d '\r\n')"
  if [ "$CURRENT_EMAIL_SUBS" -gt 0 ]; then
    echo "    [KEPT]     SNS Topic '${SNS_TOPIC_NAME}' already has ${CURRENT_EMAIL_SUBS} email subscription(s)."
  else
    echo "    [WARN]     SNS Topic '${SNS_TOPIC_NAME}' currently has 0 email subscriptions. Run with ALERT_EMAILS_STDIN=1 to subscribe team members." >&2
  fi
fi

# ---------------------------------------------------------------------------
# 4. CloudWatch Standard-Resolution Alarms (10 alarms <= 10 Free Tier limit)
# ---------------------------------------------------------------------------
echo ""
echo "==> 4. Configuring 10 CloudWatch Alarms (Standard resolution Period=300s, Free Tier \$0)..."

put_standard_alarm() {
  local alarm_name="$1"
  local alarm_desc="$2"
  local namespace="$3"
  local metric_name="$4"
  local stat="$5"
  local period="$6"
  local eval_periods="$7"
  local threshold="$8"
  local comp_op="$9"
  local treat_missing="${10}"
  local dim_name="${11:-}"
  local dim_val="${12:-}"

  local args=(
    --region "$AWS_REGION"
    --alarm-name "$alarm_name"
    --alarm-description "$alarm_desc"
    --namespace "$namespace"
    --metric-name "$metric_name"
    --statistic "$stat"
    --period "$period"
    --evaluation-periods "$eval_periods"
    --threshold "$threshold"
    --comparison-operator "$comp_op"
    --treat-missing-data "$treat_missing"
    --actions-enabled
    --alarm-actions "$SNS_TOPIC_ARN"
    --tags "Key=Project,Value=ai-exam-maker" "Key=Environment,Value=${ENV_NAME}" "Key=Task,Value=O13"
  )

  if [ -n "$dim_name" ] && [ -n "$dim_val" ]; then
    args+=(--dimensions "Name=${dim_name},Value=${dim_val}")
  fi

  aws cloudwatch put-metric-alarm "${args[@]}"
  echo "    [ALARM]    ${alarm_name} (${namespace}::${metric_name} ${comp_op} ${threshold}, period=${period}s x ${eval_periods}, missing=${treat_missing})"
}

# 1. [Mandatory #1] SQS Dead-Letter Queue depth > 0
put_standard_alarm \
  "exammaker-${ENV_NAME}-sqs-dlq-depth" \
  "AI Exam Maker: SQS Dead-Letter Queue has visible failed AI generation job messages (> 0)" \
  "AWS/SQS" \
  "ApproximateNumberOfMessagesVisible" \
  "Maximum" 300 1 0 "GreaterThanThreshold" "notBreaching" \
  "QueueName" "$SQS_DLQ_NAME"

# 2. [Mandatory #2] API 5xx error spike (>= 5 errors in 5 minutes)
put_standard_alarm \
  "exammaker-${ENV_NAME}-api-5xx" \
  "AI Exam Maker: API HTTP 5xx responses >= 5 in 5 minutes" \
  "$CW_METRIC_NAMESPACE" \
  "Api5xxCount" \
  "Sum" 300 1 5 "GreaterThanOrEqualToThreshold" "notBreaching"

# 3. [Mandatory #3] Worker heartbeat missing (< 1 heartbeat in 5 minutes; TreatMissingData=breaching)
put_standard_alarm \
  "exammaker-${ENV_NAME}-worker-heartbeat" \
  "AI Exam Maker: Worker heartbeat missing (< 1 in 5 minutes; breaching on missing data)" \
  "$CW_METRIC_NAMESPACE" \
  "WorkerHeartbeat" \
  "Sum" 300 1 1 "LessThanThreshold" "$HEARTBEAT_TREAT_MISSING"

# 4. SQS Main Queue oldest message age > 600s (10 minutes)
put_standard_alarm \
  "exammaker-${ENV_NAME}-sqs-oldest-msg" \
  "AI Exam Maker: Oldest message in SQS Main Queue waiting > 600 seconds (10 minutes)" \
  "AWS/SQS" \
  "ApproximateAgeOfOldestMessage" \
  "Maximum" 300 1 600 "GreaterThanThreshold" "notBreaching" \
  "QueueName" "$SQS_MAIN_QUEUE_NAME"

# 5. Worker jobs stuck in Running state past lease timeout (> 0)
put_standard_alarm \
  "exammaker-${ENV_NAME}-worker-stuck-jobs" \
  "AI Exam Maker: AI generation job(s) stuck in Running state past worker lease (> 0)" \
  "$CW_METRIC_NAMESPACE" \
  "StuckRunningJobs" \
  "Maximum" 300 1 0 "GreaterThanThreshold" "notBreaching"

# 6. Worker/application error log spike (>= 3 errors in 5 minutes)
put_standard_alarm \
  "exammaker-${ENV_NAME}-worker-errors" \
  "AI Exam Maker: Worker or application error log count >= 3 in 5 minutes" \
  "$CW_METRIC_NAMESPACE" \
  "WorkerErrorCount" \
  "Sum" 300 1 3 "GreaterThanOrEqualToThreshold" "notBreaching"

# 7. EC2 CPU utilization > 80% for 2 consecutive 5-minute periods (10 minutes)
put_standard_alarm \
  "exammaker-${ENV_NAME}-ec2-cpu-high" \
  "AI Exam Maker: EC2 t4g.small CPU utilization > 80% for 10 minutes" \
  "AWS/EC2" \
  "CPUUtilization" \
  "Average" 300 2 80 "GreaterThanThreshold" "notBreaching" \
  "InstanceId" "$EC2_INSTANCE_ID"

# 8. EBS root gp3 volume stalled IO check (> 0)
put_standard_alarm \
  "exammaker-${ENV_NAME}-ebs-stalled-io" \
  "AI Exam Maker: EC2 root EBS gp3 volume stalled IO check failed (> 0)" \
  "AWS/EBS" \
  "VolumeStalledIOCheck" \
  "Maximum" 300 1 0 "GreaterThanThreshold" "notBreaching" \
  "VolumeId" "$EBS_VOLUME_ID"

# 9. RDS PostgreSQL free storage space < 2 GB (2147483648 bytes)
put_standard_alarm \
  "exammaker-${ENV_NAME}-rds-storage-low" \
  "AI Exam Maker: RDS PostgreSQL free storage space < 2 GB (2147483648 bytes)" \
  "AWS/RDS" \
  "FreeStorageSpace" \
  "Minimum" 300 1 2147483648 "LessThanThreshold" "notBreaching" \
  "DBInstanceIdentifier" "$RDS_INSTANCE_ID"

# 10. RDS PostgreSQL database connections > 50
put_standard_alarm \
  "exammaker-${ENV_NAME}-rds-connections-high" \
  "AI Exam Maker: RDS PostgreSQL active database connections > 50" \
  "AWS/RDS" \
  "DatabaseConnections" \
  "Average" 300 1 50 "GreaterThanThreshold" "notBreaching" \
  "DBInstanceIdentifier" "$RDS_INSTANCE_ID"

echo ""
echo "DONE: CloudWatch Log Group (7-day retention), 4 Metric Filters, SNS Topic '${SNS_TOPIC_NAME}', and 10 Alarms are configured (\$0 Free Tier)."
echo "Next step: run './infra/cloudwatch/verify-cloudwatch.sh' to verify all 15 observability & cost-control checks."
