#!/usr/bin/env bash
# Verification script for Amazon RDS PostgreSQL 16 & SSM Parameter Store (Task O10).
# Reads ${SSM_PREFIX}/Rds/* and ${SSM_PREFIX}/ConnectionStrings/ExamMaker and verifies
# all 15 security, network isolation, backup, and budget guardrails from docs/context.md.
#
# Dependencies: aws (CLI v2), jq (both pre-installed in AWS CloudShell).
#
# Usage:
#   ./infra/rds/verify-rds.sh
#   ENV_NAME=prod ./infra/rds/verify-rds.sh

set -euo pipefail

# Prevent Git Bash (MSYS2) on Windows from rewriting SSM paths starting with '/' into Windows paths.
export MSYS_NO_PATHCONV=1
export MSYS2_ARG_CONV_EXCL="*"

AWS_REGION="${AWS_REGION:-ap-southeast-1}"
ENV_NAME="${ENV_NAME:-dev}"
SSM_PREFIX="${SSM_PREFIX:-/exammaker/${ENV_NAME}}"
EXPECT_DELETION_PROTECTION="${EXPECT_DELETION_PROTECTION:-$([ "$ENV_NAME" = "prod" ] && echo "true" || echo "any")}"

command -v aws >/dev/null 2>&1 || { echo "ERROR: aws CLI is required." >&2; exit 1; }
command -v jq >/dev/null 2>&1 || { echo "ERROR: jq is required (pre-installed in AWS CloudShell; on Windows install via 'winget install jqlang.jq')." >&2; exit 1; }

PASSED=0
FAILURES=0

pass() { echo "[PASS] $1"; PASSED=$((PASSED + 1)); }
fail() { echo "[FAIL] $1" >&2; FAILURES=$((FAILURES + 1)); }

echo "==> Reading SSM parameters from '${SSM_PREFIX}/Rds' and '${SSM_PREFIX}/ConnectionStrings/ExamMaker' (${AWS_REGION}, env=${ENV_NAME})..."

SSM_RDS_JSON="$(aws ssm get-parameters-by-path \
  --region "$AWS_REGION" \
  --path "${SSM_PREFIX}/Rds" \
  --output json)"

get_rds_ssm() {
  local suffix="$1"
  jq -r --arg name "${SSM_PREFIX}/Rds/${suffix}" '.Parameters[]? | select(.Name == $name) | .Value' <<<"$SSM_RDS_JSON"
}

DB_INSTANCE_ID="$(get_rds_ssm "InstanceId")"
SSM_ENDPOINT="$(get_rds_ssm "Endpoint")"
SSM_PORT="$(get_rds_ssm "Port")"
SSM_DATABASE="$(get_rds_ssm "Database")"
SSM_USERNAME="$(get_rds_ssm "Username")"

if [ -z "$DB_INSTANCE_ID" ] || [ -z "$SSM_ENDPOINT" ] || [ -z "$SSM_PORT" ] || [ -z "$SSM_DATABASE" ] || [ -z "$SSM_USERNAME" ]; then
  echo "ERROR: Missing one or more SSM metadata parameters under ${SSM_PREFIX}/Rds/ (InstanceId, Endpoint, Port, Database, Username)." >&2
  exit 1
fi

CONN_PARAM_JSON=""
CONN_PARAM_RC=0
CONN_PARAM_JSON="$(aws ssm get-parameter \
  --region "$AWS_REGION" \
  --name "${SSM_PREFIX}/ConnectionStrings/ExamMaker" \
  --with-decryption \
  --output json 2>&1)" || CONN_PARAM_RC=$?

if [ "$CONN_PARAM_RC" -ne 0 ]; then
  echo "ERROR: Missing SSM parameter '${SSM_PREFIX}/ConnectionStrings/ExamMaker'." >&2
  exit 1
fi

CONN_TYPE="$(jq -r '.Parameter.Type // empty' <<<"$CONN_PARAM_JSON")"
CONN_VALUE="$(jq -r '.Parameter.Value // empty' <<<"$CONN_PARAM_JSON")"

# Fetch RDS instance once
DB_JSON="$(aws rds describe-db-instances \
  --region "$AWS_REGION" \
  --db-instance-identifier "$DB_INSTANCE_ID" \
  --output json)"

ACTUAL_ENDPOINT="$(jq -r '.DBInstances[0].Endpoint.Address // empty' <<<"$DB_JSON")"
ACTUAL_PORT="$(jq -r '.DBInstances[0].Endpoint.Port // empty' <<<"$DB_JSON")"
ACTUAL_DB="$(jq -r '.DBInstances[0].DBName // empty' <<<"$DB_JSON")"
ACTUAL_USER="$(jq -r '.DBInstances[0].MasterUsername // empty' <<<"$DB_JSON")"

# 1. SSM SecureString connection string exists, is encrypted (SecureString), and matches live RDS endpoint/port/db/user + SSL Mode=Require
if [ "$CONN_TYPE" = "SecureString" ] \
  && grep -Fq "Host=${ACTUAL_ENDPOINT};" <<<"$CONN_VALUE" \
  && grep -Fq "Port=${ACTUAL_PORT};" <<<"$CONN_VALUE" \
  && grep -Fq "Database=${ACTUAL_DB};" <<<"$CONN_VALUE" \
  && grep -Fq "Username=${ACTUAL_USER};" <<<"$CONN_VALUE" \
  && grep -Fq "SSL Mode=Require" <<<"$CONN_VALUE" \
  && [ "$SSM_ENDPOINT" = "$ACTUAL_ENDPOINT" ] \
  && [ "$SSM_PORT" = "$ACTUAL_PORT" ] \
  && [ "$SSM_DATABASE" = "$ACTUAL_DB" ] \
  && [ "$SSM_USERNAME" = "$ACTUAL_USER" ]; then
  pass "SSM SecureString '${SSM_PREFIX}/ConnectionStrings/ExamMaker' and metadata match live RDS instance"
else
  fail "SSM SecureString '${SSM_PREFIX}/ConnectionStrings/ExamMaker' type='${CONN_TYPE}' or metadata does not match live RDS instance"
fi
unset CONN_PARAM_JSON CONN_VALUE

# 2. Engine is PostgreSQL 16.x
ENGINE="$(jq -r '.DBInstances[0].Engine // empty' <<<"$DB_JSON")"
ENGINE_VER="$(jq -r '.DBInstances[0].EngineVersion // empty' <<<"$DB_JSON")"
if [ "$ENGINE" = "postgres" ] && [[ "$ENGINE_VER" == 16.* ]]; then
  pass "Engine is PostgreSQL 16 (${ENGINE_VER})"
else
  fail "Expected postgres 16.x, got '${ENGINE} ${ENGINE_VER}'"
fi

# 3. Instance class is db.t4g.micro (budget guardrail)
INSTANCE_CLASS="$(jq -r '.DBInstances[0].DBInstanceClass // empty' <<<"$DB_JSON")"
if [ "$INSTANCE_CLASS" = "db.t4g.micro" ]; then
  pass "Instance class is db.t4g.micro (budget compliant)"
else
  fail "Expected instance class db.t4g.micro, got '${INSTANCE_CLASS}'"
fi

# 4. Single-AZ deployment (Multi-AZ forbidden under $200 budget)
MULTI_AZ="$(jq -r '.DBInstances[0].MultiAZ' <<<"$DB_JSON" | tr -d '\r\n')"
if [ "$MULTI_AZ" = "false" ]; then
  pass "Deployment is Single-AZ (MultiAZ=false)"
else
  fail "Expected Single-AZ (MultiAZ=false), got '${MULTI_AZ}'"
fi

# 5. Not publicly accessible
PUBLICLY_ACCESSIBLE="$(jq -r '.DBInstances[0].PubliclyAccessible' <<<"$DB_JSON" | tr -d '\r\n')"
if [ "$PUBLICLY_ACCESSIBLE" = "false" ]; then
  pass "RDS is private (PubliclyAccessible=false)"
else
  fail "RDS must NOT be publicly accessible (PubliclyAccessible=${PUBLICLY_ACCESSIBLE})"
fi

# 6. Storage encryption at rest enabled
STORAGE_ENCRYPTED="$(jq -r '.DBInstances[0].StorageEncrypted' <<<"$DB_JSON" | tr -d '\r\n')"
if [ "$STORAGE_ENCRYPTED" = "true" ]; then
  pass "Storage encryption at rest is enabled (StorageEncrypted=true)"
else
  fail "Storage encryption at rest is NOT enabled (StorageEncrypted=${STORAGE_ENCRYPTED})"
fi

# 7. Storage type is gp3 and allocated storage <= 20 GB (budget guardrail)
STORAGE_TYPE="$(jq -r '.DBInstances[0].StorageType // empty' <<<"$DB_JSON")"
ALLOCATED_STORAGE="$(jq -r '.DBInstances[0].AllocatedStorage // 0' <<<"$DB_JSON")"
if [ "$STORAGE_TYPE" = "gp3" ] && [ "$ALLOCATED_STORAGE" -le 20 ]; then
  pass "Storage is gp3 ${ALLOCATED_STORAGE} GB (<= 20 GB budget target)"
else
  fail "Expected gp3 <= 20 GB, got '${STORAGE_TYPE} ${ALLOCATED_STORAGE} GB'"
fi

# 8. Automated backup retention is short (1..7 days)
BACKUP_RETENTION="$(jq -r '.DBInstances[0].BackupRetentionPeriod // 0' <<<"$DB_JSON")"
if [ "$BACKUP_RETENTION" -ge 1 ] && [ "$BACKUP_RETENTION" -le 7 ]; then
  pass "Automated backup retention is ${BACKUP_RETENTION} day(s) (1-7 days short retention)"
else
  fail "Expected automated backup retention between 1 and 7 days, got '${BACKUP_RETENTION}'"
fi

# 9. CopyTagsToSnapshot enabled
COPY_TAGS="$(jq -r '.DBInstances[0].CopyTagsToSnapshot' <<<"$DB_JSON" | tr -d '\r\n')"
if [ "$COPY_TAGS" = "true" ]; then
  pass "CopyTagsToSnapshot is enabled"
else
  fail "CopyTagsToSnapshot is NOT enabled (${COPY_TAGS})"
fi

# 10. DB Subnet Group spans >= 2 AZs and none of its subnets route 0.0.0.0/0 to an Internet Gateway
SUBNET_GROUP_NAME="$(jq -r '.DBInstances[0].DBSubnetGroup.DBSubnetGroupName // empty' <<<"$DB_JSON" | tr -d '\r\n')"
VPC_ID="$(jq -r '.DBInstances[0].DBSubnetGroup.VpcId // empty' <<<"$DB_JSON" | tr -d '\r\n')"
AZ_COUNT="$(jq -r '[.DBInstances[0].DBSubnetGroup.Subnets[].SubnetAvailabilityZone.Name] | unique | length' <<<"$DB_JSON" | tr -d '\r\n')"
SUBNET_IDS_ARR=()
while IFS= read -r sid; do
  sid="$(printf '%s' "$sid" | tr -d '\r\n')"
  [ -n "$sid" ] && SUBNET_IDS_ARR+=("$sid")
done < <(jq -r '.DBInstances[0].DBSubnetGroup.Subnets[].SubnetIdentifier' <<<"$DB_JSON")

HAS_IGW_ROUTE="false"
if [ "${#SUBNET_IDS_ARR[@]}" -gt 0 ] && [ -n "$VPC_ID" ]; then
  RT_JSON="$(aws ec2 describe-route-tables \
    --region "$AWS_REGION" \
    --filters "Name=vpc-id,Values=${VPC_ID}" \
    --output json)"
  MAIN_RT_HAS_IGW="$(jq -r '
    [.RouteTables[] | select(any(.Associations[]?; .Main == true)) | .Routes[]? | select(.DestinationCidrBlock == "0.0.0.0/0" and ((.GatewayId // "") | startswith("igw-")))] | length > 0
  ' <<<"$RT_JSON")"
  for sid in "${SUBNET_IDS_ARR[@]}"; do
    EXPLICIT_RT="$(jq -c --arg sid "$sid" '[.RouteTables[] | select(any(.Associations[]?; .SubnetId == $sid))]' <<<"$RT_JSON")"
    if [ "$(jq -r 'length' <<<"$EXPLICIT_RT")" -gt 0 ]; then
      if jq -e '[.[].Routes[]? | select(.DestinationCidrBlock == "0.0.0.0/0" and ((.GatewayId // "") | startswith("igw-")))] | length > 0' <<<"$EXPLICIT_RT" >/dev/null; then
        HAS_IGW_ROUTE="true"
      fi
    elif [ "$MAIN_RT_HAS_IGW" = "true" ]; then
      HAS_IGW_ROUTE="true"
    fi
  done
fi

if [ "$AZ_COUNT" -ge 2 ] && [ "$HAS_IGW_ROUTE" = "false" ]; then
  pass "DB Subnet Group '${SUBNET_GROUP_NAME}' spans ${AZ_COUNT} AZs with private subnets (no 0.0.0.0/0 -> IGW)"
else
  fail "DB Subnet Group check failed (AZs=${AZ_COUNT}, has_igw_route=${HAS_IGW_ROUTE})"
fi

# 11. Security Group allows TCP 5432 ONLY from Security Group sources (no CIDR / 0.0.0.0/0 / ::/0)
SG_IDS_ARR=()
while IFS= read -r sgid; do
  sgid="$(printf '%s' "$sgid" | tr -d '\r\n')"
  [ -n "$sgid" ] && SG_IDS_ARR+=("$sgid")
done < <(jq -r '.DBInstances[0].VpcSecurityGroups[].VpcSecurityGroupId' <<<"$DB_JSON")

SG_OK="false"
if [ "${#SG_IDS_ARR[@]}" -gt 0 ]; then
  SG_JSON="$(aws ec2 describe-security-groups \
    --region "$AWS_REGION" \
    --group-ids "${SG_IDS_ARR[@]}" \
    --output json)"
  HAS_CIDR_INGRESS="$(jq -r '
    [.SecurityGroups[].IpPermissions[]? | ((.IpRanges // []) + (.Ipv6Ranges // []))[]] | length > 0
  ' <<<"$SG_JSON")"
  HAS_SG_5432="$(jq -r '
    [.SecurityGroups[].IpPermissions[]? | select(.IpProtocol == "tcp" and .FromPort == 5432 and .ToPort == 5432 and ((.UserIdGroupPairs // []) | length > 0))] | length > 0
  ' <<<"$SG_JSON")"
  HAS_OTHER_INGRESS="$(jq -r '
    [.SecurityGroups[].IpPermissions[]? | select((.IpProtocol == "tcp" and .FromPort == 5432 and .ToPort == 5432) | not)] | length > 0
  ' <<<"$SG_JSON")"
  if [ "$HAS_CIDR_INGRESS" = "false" ] && [ "$HAS_SG_5432" = "true" ] && [ "$HAS_OTHER_INGRESS" = "false" ]; then
    SG_OK="true"
  fi
fi

if [ "$SG_OK" = "true" ]; then
  pass "RDS Security Group restricts ingress strictly to TCP 5432 from Security Group source(s) only"
else
  fail "RDS Security Group ingress is not strictly TCP 5432 from SG source only (cidr=${HAS_CIDR_INGRESS:-unknown}, sg5432=${HAS_SG_5432:-unknown}, other=${HAS_OTHER_INGRESS:-unknown})"
fi

# 12. Performance Insights & Enhanced Monitoring disabled (cost guardrail)
PERF_INSIGHTS="$(jq -r '.DBInstances[0].PerformanceInsightsEnabled' <<<"$DB_JSON" | tr -d '\r\n')"
MON_INTERVAL="$(jq -r '.DBInstances[0].MonitoringInterval // 0' <<<"$DB_JSON" | tr -d '\r\n')"
if [ "$PERF_INSIGHTS" = "false" ] && [ "$MON_INTERVAL" -eq 0 ]; then
  pass "Performance Insights and Enhanced Monitoring are disabled (no extra CloudWatch cost)"
else
  fail "Expected PerformanceInsights=false and MonitoringInterval=0, got PI=${PERF_INSIGHTS}, MonitoringInterval=${MON_INTERVAL}"
fi

# 13. TLS in transit enforced (rds.force_ssl = 1 in effective parameter group)
PG_NAME="$(jq -r '.DBInstances[0].DBParameterGroups[0].DBParameterGroupName // empty' <<<"$DB_JSON" | tr -d '\r\n')"
FORCE_SSL=""
if [ -n "$PG_NAME" ]; then
  FORCE_SSL="$(aws rds describe-db-parameters \
    --region "$AWS_REGION" \
    --db-parameter-group-name "$PG_NAME" \
    --query "Parameters[?ParameterName=='rds.force_ssl'].ParameterValue" \
    --output text 2>/dev/null | tr -d '\r\n' || echo "")"
fi
if [ "$FORCE_SSL" = "1" ]; then
  pass "TLS in transit enforced (rds.force_ssl=1 in parameter group '${PG_NAME}')"
else
  fail "Expected rds.force_ssl=1 in parameter group '${PG_NAME}', got '${FORCE_SSL}'"
fi

# 14. DeletionProtection matches environment policy
DEL_PROT="$(jq -r '.DBInstances[0].DeletionProtection' <<<"$DB_JSON" | tr -d '\r\n')"
if [ "$EXPECT_DELETION_PROTECTION" = "any" ] || [ "$DEL_PROT" = "$EXPECT_DELETION_PROTECTION" ]; then
  pass "DeletionProtection=${DEL_PROT} matches policy (expected=${EXPECT_DELETION_PROTECTION})"
else
  fail "DeletionProtection expected '${EXPECT_DELETION_PROTECTION}', got '${DEL_PROT}'"
fi

# 15. Instance status is available (or warn if intentionally stopped overnight)
DB_STATUS="$(jq -r '.DBInstances[0].DBInstanceStatus // empty' <<<"$DB_JSON")"
if [ "$DB_STATUS" = "available" ]; then
  pass "RDS instance '${DB_INSTANCE_ID}' is available"
else
  fail "RDS instance '${DB_INSTANCE_ID}' status is '${DB_STATUS}' (expected 'available')"
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
