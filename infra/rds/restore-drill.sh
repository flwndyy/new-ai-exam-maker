#!/usr/bin/env bash
# Automated Restore Drill for Amazon RDS PostgreSQL 16 (Task O10).
# Fulfills requirement from docs/context.md §11-§12:
#   1. Take a manual snapshot of live RDS instance (exammaker-dev-pg).
#   2. Restore to a temporary test instance (exammaker-dev-pg-restore-test) in the same private subnets & SG.
#   3. Verify restored database attributes and security guardrails (available, encrypted, private, Single-AZ).
#   4. Immediately tear down temporary instance and test snapshot to protect the $200 budget (guaranteed via EXIT trap).
#
# Dependencies: aws (CLI v2), jq.
# Usage:
#   ./infra/rds/restore-drill.sh
#   ENV_NAME=dev ./infra/rds/restore-drill.sh
#
# Optional: keep instance open for manual inspection / data check before cleanup:
#   KEEP_FOR_MANUAL_CHECK=1 ./infra/rds/restore-drill.sh

set -euo pipefail

export MSYS_NO_PATHCONV=1
export MSYS2_ARG_CONV_EXCL="*"

AWS_REGION="${AWS_REGION:-ap-southeast-1}"
ENV_NAME="${ENV_NAME:-dev}"
DB_INSTANCE_ID="${DB_INSTANCE_ID:-exammaker-${ENV_NAME}-pg}"
RESTORE_INSTANCE_ID="${DB_INSTANCE_ID}-restore-test"
DRILL_TIMESTAMP="$(date -u +%Y%m%d%H%M%S)"
SNAPSHOT_ID="${DB_INSTANCE_ID}-drill-${DRILL_TIMESTAMP}"
KEEP_FOR_MANUAL_CHECK="${KEEP_FOR_MANUAL_CHECK:-0}"

command -v aws >/dev/null 2>&1 || { echo "ERROR: aws CLI is required." >&2; exit 1; }
command -v jq >/dev/null 2>&1 || { echo "ERROR: jq is required." >&2; exit 1; }

echo "=========================================================================="
echo " Starting RDS Restore Drill: ${DB_INSTANCE_ID} (${AWS_REGION}, env=${ENV_NAME})"
echo " Snapshot ID: ${SNAPSHOT_ID}"
echo " Temporary Instance: ${RESTORE_INSTANCE_ID}"
echo "=========================================================================="

# 0. Pre-flight check: ensure no leftover test instance from a previous failed run
EXISTING_TEST_JSON="$(aws rds describe-db-instances \
  --region "$AWS_REGION" \
  --db-instance-identifier "$RESTORE_INSTANCE_ID" \
  --output json 2>/dev/null || echo "")"

if [ -n "$EXISTING_TEST_JSON" ]; then
  EXISTING_STATUS="$(jq -r '.DBInstances[0].DBInstanceStatus // empty' <<<"$EXISTING_TEST_JSON" | tr -d '\r\n')"
  if [ -n "$EXISTING_STATUS" ] && [ "$EXISTING_STATUS" != "deleting" ]; then
    echo "ERROR: Leftover test instance '${RESTORE_INSTANCE_ID}' already exists (status=${EXISTING_STATUS})." >&2
    echo "       To protect budget, clean it up before re-running:" >&2
    echo "       aws rds delete-db-instance --region ${AWS_REGION} --db-instance-identifier ${RESTORE_INSTANCE_ID} --skip-final-snapshot --delete-automated-backups" >&2
    exit 1
  fi
fi

# Cleanup tracking flags for trap
SNAPSHOT_CREATED=false
RESTORE_INITIATED=false
CLEANUP_COMPLETED=false

cleanup() {
  local exit_code=$?
  if [ "$CLEANUP_COMPLETED" = "true" ]; then
    exit "$exit_code"
  fi

  echo ""
  echo "==> [CLEANUP TRAP] Ensuring temporary drill resources are purged..."
  if [ "$RESTORE_INITIATED" = "true" ]; then
    echo "    Deleting temporary instance '${RESTORE_INSTANCE_ID}'..."
    local del_inst_err=""
    local del_inst_rc=0
    del_inst_err="$(aws rds delete-db-instance \
      --region "$AWS_REGION" \
      --db-instance-identifier "$RESTORE_INSTANCE_ID" \
      --skip-final-snapshot \
      --delete-automated-backups 2>&1)" || del_inst_rc=$?

    if [ "$del_inst_rc" -eq 0 ]; then
      echo "    Waiting for temporary instance '${RESTORE_INSTANCE_ID}' to finish deleting..."
      if ! aws rds wait db-instance-deleted \
        --region "$AWS_REGION" \
        --db-instance-identifier "$RESTORE_INSTANCE_ID" 2>/dev/null; then
        echo "    [WARNING] Waiter timed out while waiting for instance deletion." >&2
        echo "              Check status: aws rds describe-db-instances --region ${AWS_REGION} --db-instance-identifier ${RESTORE_INSTANCE_ID}" >&2
      else
        echo "    Temporary instance '${RESTORE_INSTANCE_ID}' successfully DELETED."
      fi
    elif grep -q "DBInstanceNotFound" <<<"$del_inst_err"; then
      echo "    (Instance already deleted)"
    else
      echo "    [ERROR] Failed to delete instance: ${del_inst_err}" >&2
      echo "    [ATTENTION] You MUST manually delete temporary instance '${RESTORE_INSTANCE_ID}' to avoid costs!" >&2
      echo "    Run: aws rds delete-db-instance --region ${AWS_REGION} --db-instance-identifier ${RESTORE_INSTANCE_ID} --skip-final-snapshot --delete-automated-backups" >&2
    fi
  fi

  if [ "$SNAPSHOT_CREATED" = "true" ]; then
    echo "    Deleting drill snapshot '${SNAPSHOT_ID}'..."
    local del_snap_err=""
    local del_snap_rc=0
    del_snap_err="$(aws rds delete-db-snapshot \
      --region "$AWS_REGION" \
      --db-snapshot-identifier "$SNAPSHOT_ID" 2>&1)" || del_snap_rc=$?

    if [ "$del_snap_rc" -eq 0 ]; then
      echo "    Drill snapshot '${SNAPSHOT_ID}' deleted."
    elif grep -q "DBSnapshotNotFound" <<<"$del_snap_err"; then
      echo "    (Snapshot already deleted)"
    else
      echo "    [ERROR] Failed to delete snapshot: ${del_snap_err}" >&2
      echo "    [ATTENTION] You MUST manually delete snapshot '${SNAPSHOT_ID}'!" >&2
      echo "    Run: aws rds delete-db-snapshot --region ${AWS_REGION} --db-snapshot-identifier ${SNAPSHOT_ID}" >&2
    fi
  fi

  CLEANUP_COMPLETED=true
  if [ "$exit_code" -ne 0 ]; then
    echo "==========================================================================" >&2
    echo "WARNING: Drill failed with exit code ${exit_code}." >&2
    echo "         Verify manual cleanup in AWS Console or run:" >&2
    echo "         aws rds describe-db-instances --region ${AWS_REGION} --db-instance-identifier ${RESTORE_INSTANCE_ID}" >&2
    echo "         aws rds describe-db-snapshots --region ${AWS_REGION} --db-snapshot-identifier ${SNAPSHOT_ID}" >&2
    echo "==========================================================================" >&2
  fi
  exit "$exit_code"
}

trap cleanup EXIT INT TERM HUP

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

SNAPSHOT_CREATED=true

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

RESTORE_INITIATED=true

echo "==> Waiting for restored instance '${RESTORE_INSTANCE_ID}' to become available (~4-7 minutes)..."
aws rds wait db-instance-available \
  --region "$AWS_REGION" \
  --db-instance-identifier "$RESTORE_INSTANCE_ID"
echo "    Restored instance '${RESTORE_INSTANCE_ID}' is AVAILABLE."

# 4. Verify restored instance attributes and security guardrails
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
echo "[INFO] Infrastructure-level restore drill verified: Snapshot -> Restore -> Guardrails -> Cleanup."
echo "[NOTE] To verify application data (departments, subjects, topics, users), execute psql queries from an EC2 instance in aem-ec2-sg (see docs/runbooks/rds.md §7)."

if [ "$KEEP_FOR_MANUAL_CHECK" = "1" ]; then
  echo ""
  echo "==> KEEP_FOR_MANUAL_CHECK=1 is set. Pausing for manual inspection..."
  echo "    Connect to endpoint: ${R_ENDPOINT}:5432"
  echo "    Waiting up to 20 minutes (1200 seconds) for manual inspection, or press [ENTER] to proceed with cleanup..."
  read -r -t 1200 _ || true
fi

# 5. Cleanup temporary test instance and snapshot immediately to save budget
echo "==> Step 5: Cleaning up temporary test instance and snapshot (Budget Protection)..."
CLEANUP_WARN=false

aws rds delete-db-instance \
  --region "$AWS_REGION" \
  --db-instance-identifier "$RESTORE_INSTANCE_ID" \
  --skip-final-snapshot \
  --delete-automated-backups >/dev/null

echo "==> Waiting for temporary instance '${RESTORE_INSTANCE_ID}' to be deleted..."
if ! aws rds wait db-instance-deleted \
  --region "$AWS_REGION" \
  --db-instance-identifier "$RESTORE_INSTANCE_ID"; then
  echo "    [WARNING] Waiter timed out while waiting for instance deletion." >&2
  echo "              Check status: aws rds describe-db-instances --region ${AWS_REGION} --db-instance-identifier ${RESTORE_INSTANCE_ID}" >&2
  CLEANUP_WARN=true
else
  echo "    Temporary instance '${RESTORE_INSTANCE_ID}' successfully DELETED."
fi

echo "==> Deleting drill snapshot '${SNAPSHOT_ID}'..."
if ! aws rds delete-db-snapshot \
  --region "$AWS_REGION" \
  --db-snapshot-identifier "$SNAPSHOT_ID" >/dev/null 2>&1; then
  echo "    [WARNING] Failed to delete drill snapshot '${SNAPSHOT_ID}'." >&2
  echo "              Check status: aws rds describe-db-snapshots --region ${AWS_REGION} --db-snapshot-identifier ${SNAPSHOT_ID}" >&2
  CLEANUP_WARN=true
else
  echo "    Drill snapshot '${SNAPSHOT_ID}' successfully DELETED."
fi

CLEANUP_COMPLETED=true

echo ""
if [ "$CLEANUP_WARN" = "true" ]; then
  echo "==========================================================================" >&2
  echo " WARNING: RESTORE DRILL FINISHED WITH CLEANUP WARNINGS!" >&2
  echo " Please verify and manually purge any remaining resources to avoid cost:" >&2
  echo " aws rds describe-db-instances --region ${AWS_REGION} --db-instance-identifier ${RESTORE_INSTANCE_ID}" >&2
  echo " aws rds describe-db-snapshots --region ${AWS_REGION} --db-snapshot-identifier ${SNAPSHOT_ID}" >&2
  echo "==========================================================================" >&2
  exit 1
fi

echo "=========================================================================="
echo " RESTORE DRILL COMPLETED SUCCESSFULLY!"
echo " All temporary resources have been cleaned up. Zero unnecessary cost incurred."
echo "=========================================================================="
