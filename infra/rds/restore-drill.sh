#!/usr/bin/env bash
# Automated Restore Drill for Amazon RDS PostgreSQL 16 (Task O10).
# Fulfills requirement from docs/context.md §11-§12:
#   1. Take a manual snapshot of live RDS instance (exammaker-dev-pg).
#   2. Restore to a temporary test instance (exammaker-dev-pg-restore-test) in the same private subnets & SG.
#   3. Verify restored database status (available, encrypted, private, Single-AZ).
#   4. Immediately tear down temporary instance and test snapshot to protect the $200 budget.
#
# Dependencies: aws (CLI v2), jq.
# Usage:
#   ./infra/rds/restore-drill.sh
#   ENV_NAME=dev ./infra/rds/restore-drill.sh

set -euo pipefail

export MSYS_NO_PATHCONV=1
export MSYS2_ARG_CONV_EXCL="*"

AWS_REGION="${AWS_REGION:-ap-southeast-1}"
ENV_NAME="${ENV_NAME:-dev}"
DB_INSTANCE_ID="${DB_INSTANCE_ID:-exammaker-${ENV_NAME}-pg}"
RESTORE_INSTANCE_ID="${DB_INSTANCE_ID}-restore-test"
DRILL_TIMESTAMP="$(date -u +%Y%m%d%H%M%S)"
SNAPSHOT_ID="${DB_INSTANCE_ID}-drill-${DRILL_TIMESTAMP}"

command -v aws >/dev/null 2>&1 || { echo "ERROR: aws CLI is required." >&2; exit 1; }
command -v jq >/dev/null 2>&1 || { echo "ERROR: jq is required." >&2; exit 1; }

echo "=========================================================================="
echo " Starting RDS Restore Drill: ${DB_INSTANCE_ID} (${AWS_REGION}, env=${ENV_NAME})"
echo " Snapshot ID: ${SNAPSHOT_ID}"
echo " Temporary Instance: ${RESTORE_INSTANCE_ID}"
echo "=========================================================================="

# 1. Inspect source DB instance to borrow its Subnet Group and SG
echo "==> Step 1: Inspecting source database '${DB_INSTANCE_ID}'..."
SOURCE_JSON="$(aws rds describe-db-instances \
  --region "$AWS_REGION" \
  --db-instance-identifier "$DB_INSTANCE_ID" \
  --output json)"

SOURCE_STATUS="$(jq -r '.DBInstances[0].DBInstanceStatus // empty' <<<"$SOURCE_JSON" | tr -d '\r\n')"
if [ "$SOURCE_STATUS" != "available" ]; then
  echo "ERROR: Source instance '${DB_INSTANCE_ID}' is in status '${SOURCE_STATUS}' (must be 'available')." >&2
  exit 1
fi

SUBNET_GROUP="$(jq -r '.DBInstances[0].DBSubnetGroup.DBSubnetGroupName // empty' <<<"$SOURCE_JSON" | tr -d '\r\n')"
SG_ID="$(jq -r '.DBInstances[0].VpcSecurityGroups[0].VpcSecurityGroupId // empty' <<<"$SOURCE_JSON" | tr -d '\r\n')"

if [ -z "$SUBNET_GROUP" ] || [ -z "$SG_ID" ]; then
  echo "ERROR: Could not resolve DB Subnet Group or Security Group from '${DB_INSTANCE_ID}'." >&2
  exit 1
fi
echo "    Found Subnet Group: ${SUBNET_GROUP}"
echo "    Found Security Group: ${SG_ID}"

# 2. Take manual snapshot
echo "==> Step 2: Creating manual snapshot '${SNAPSHOT_ID}'..."
aws rds create-db-snapshot \
  --region "$AWS_REGION" \
  --db-instance-identifier "$DB_INSTANCE_ID" \
  --db-snapshot-identifier "$SNAPSHOT_ID" \
  --tags "Key=Project,Value=ai-exam-maker" "Key=Task,Value=O10" "Key=Purpose,Value=restore-drill" >/dev/null

echo "==> Waiting for snapshot '${SNAPSHOT_ID}' to become available..."
aws rds wait db-snapshot-available \
  --region "$AWS_REGION" \
  --db-snapshot-identifier "$SNAPSHOT_ID"
echo "    Snapshot '${SNAPSHOT_ID}' is AVAILABLE."

# 3. Restore to temporary test instance
echo "==> Step 3: Restoring to temporary instance '${RESTORE_INSTANCE_ID}' (Single-AZ, private)..."
aws rds restore-db-instance-from-db-snapshot \
  --region "$AWS_REGION" \
  --db-instance-identifier "$RESTORE_INSTANCE_ID" \
  --db-snapshot-identifier "$SNAPSHOT_ID" \
  --db-instance-class db.t4g.micro \
  --db-subnet-group-name "$SUBNET_GROUP" \
  --vpc-security-group-ids "$SG_ID" \
  --no-multi-az \
  --no-publicly-accessible \
  --no-deletion-protection \
  --copy-tags-to-snapshot \
  --tags "Key=Project,Value=ai-exam-maker" "Key=Task,Value=O10" "Key=Purpose,Value=temporary-drill" >/dev/null

echo "==> Waiting for restored instance '${RESTORE_INSTANCE_ID}' to become available (~4-7 minutes)..."
aws rds wait db-instance-available \
  --region "$AWS_REGION" \
  --db-instance-identifier "$RESTORE_INSTANCE_ID"
echo "    Restored instance '${RESTORE_INSTANCE_ID}' is AVAILABLE."

# 4. Verify restored instance attributes
echo "==> Step 4: Verifying restored instance guardrails..."
RESTORE_DESC="$(aws rds describe-db-instances \
  --region "$AWS_REGION" \
  --db-instance-identifier "$RESTORE_INSTANCE_ID" \
  --output json)"

R_STATUS="$(jq -r '.DBInstances[0].DBInstanceStatus' <<<"$RESTORE_DESC" | tr -d '\r\n')"
R_ENCRYPTED="$(jq -r '.DBInstances[0].StorageEncrypted' <<<"$RESTORE_DESC" | tr -d '\r\n')"
R_PUBLIC="$(jq -r '.DBInstances[0].PubliclyAccessible' <<<"$RESTORE_DESC" | tr -d '\r\n')"
R_MULTIAZ="$(jq -r '.DBInstances[0].MultiAZ' <<<"$RESTORE_DESC" | tr -d '\r\n')"
R_ENDPOINT="$(jq -r '.DBInstances[0].Endpoint.Address // empty' <<<"$RESTORE_DESC" | tr -d '\r\n')"

echo "    Status:              ${R_STATUS}"
echo "    Storage Encrypted:   ${R_ENCRYPTED}"
echo "    Publicly Accessible: ${R_PUBLIC}"
echo "    Multi-AZ:            ${R_MULTIAZ}"
echo "    Endpoint:            ${R_ENDPOINT}"

DRILL_PASS=true
[ "$R_STATUS" = "available" ] || DRILL_PASS=false
[ "$R_ENCRYPTED" = "true" ] || DRILL_PASS=false
[ "$R_PUBLIC" = "false" ] || DRILL_PASS=false
[ "$R_MULTIAZ" = "false" ] || DRILL_PASS=false

if [ "$DRILL_PASS" != "true" ]; then
  echo "ERROR: Restored instance attributes do not match required guardrails." >&2
  exit 1
fi
echo "[PASS] Restored database successfully verified with all security guardrails."

# 5. Cleanup temporary test instance and snapshot immediately to save budget
echo "==> Step 5: Cleaning up temporary test instance and snapshot (Budget Protection)..."
aws rds delete-db-instance \
  --region "$AWS_REGION" \
  --db-instance-identifier "$RESTORE_INSTANCE_ID" \
  --skip-final-snapshot \
  --delete-automated-backups >/dev/null

echo "==> Waiting for temporary instance '${RESTORE_INSTANCE_ID}' to be deleted..."
aws rds wait db-instance-deleted \
  --region "$AWS_REGION" \
  --db-instance-identifier "$RESTORE_INSTANCE_ID"
echo "    Temporary instance '${RESTORE_INSTANCE_ID}' successfully DELETED."

echo "==> Deleting drill snapshot '${SNAPSHOT_ID}'..."
aws rds delete-db-snapshot \
  --region "$AWS_REGION" \
  --db-snapshot-identifier "$SNAPSHOT_ID" >/dev/null
echo "    Drill snapshot '${SNAPSHOT_ID}' successfully DELETED."

echo ""
echo "=========================================================================="
echo " RESTORE DRILL COMPLETED SUCCESSFULLY!"
echo " All temporary resources have been cleaned up. Zero unnecessary cost incurred."
echo "=========================================================================="
