#!/usr/bin/env bash
# Create-if-absent setup script for Amazon RDS PostgreSQL 16 (AI Exam Maker - Task O10).
# Creates (if not already present) and publishes configuration for:
#   - DB Subnet Group spanning >= 2 private subnets in different AZs (from O2: aem-private-a, aem-private-b)
#   - RDS PostgreSQL 16 instance: db.t4g.micro, Single-AZ, private (non-public), gp3 20 GB,
#     storage-encrypted (AWS-managed aws/rds key = $0), automated backups (3-day retention)
#   - SSM Parameter Store entries under ${SSM_PREFIX}/ConnectionStrings/ExamMaker (SecureString),
#     ${SSM_PREFIX}/Rds/MasterPassword (SecureString), and ${SSM_PREFIX}/Rds/* (Standard String metadata)
#
# Note: For drift detection and compliance auditing against the $200 budget, run ./infra/rds/verify-rds.sh.
#
# Dependencies: aws (CLI v2), jq, openssl (all pre-installed in AWS CloudShell).
#
# Usage (AWS CloudShell, Linux, macOS, or Git Bash with jq):
#   ./infra/rds/setup-rds.sh
#
# Explicitly set DeletionProtection (e.g., for demo protection):
#   DELETION_PROTECTION=true ./infra/rds/setup-rds.sh
#
# Custom O2 resource names (if different from docs/aws-o2-vpc-security-groups.md):
#   VPC_NAME=aem-demo-vpc PRIVATE_SUBNET_NAMES="aem-private-a,aem-private-b" RDS_SG_NAME=aem-rds-sg \
#     ./infra/rds/setup-rds.sh

set -euo pipefail

# Prevent Git Bash (MSYS2) on Windows from rewriting SSM paths starting with '/' into Windows paths.
export MSYS_NO_PATHCONV=1
export MSYS2_ARG_CONV_EXCL="*"

AWS_REGION="${AWS_REGION:-ap-southeast-1}"
ENV_NAME="${ENV_NAME:-dev}"
SSM_PREFIX="${SSM_PREFIX:-/exammaker/${ENV_NAME}}"

# O2 network naming defaults (docs/aws-o2-vpc-security-groups.md)
VPC_NAME="${VPC_NAME:-aem-demo-vpc}"
PRIVATE_SUBNET_NAMES="${PRIVATE_SUBNET_NAMES:-aem-private-a,aem-private-b}"
RDS_SG_NAME="${RDS_SG_NAME:-aem-rds-sg}"
SUBNET_IDS="${SUBNET_IDS:-}"
RDS_SG_ID="${RDS_SG_ID:-}"

# RDS configuration defaults (docs/context.md §5.2, §6, §12, §14)
DB_INSTANCE_ID="${DB_INSTANCE_ID:-exammaker-${ENV_NAME}-pg}"
DB_SUBNET_GROUP_NAME="${DB_SUBNET_GROUP_NAME:-exammaker-${ENV_NAME}-db-subnets}"
DB_INSTANCE_CLASS="${DB_INSTANCE_CLASS:-db.t4g.micro}"
DB_ENGINE_MAJOR="${DB_ENGINE_MAJOR:-16}"
DB_ENGINE_VERSION="${DB_ENGINE_VERSION:-}"
DB_NAME="${DB_NAME:-exammaker}"
DB_USERNAME="${DB_USERNAME:-exammaker_admin}"
DB_ALLOCATED_STORAGE="${DB_ALLOCATED_STORAGE:-20}"
DB_MAX_ALLOCATED_STORAGE="${DB_MAX_ALLOCATED_STORAGE:-40}"
DB_BACKUP_RETENTION_DAYS="${DB_BACKUP_RETENTION_DAYS:-3}"
# 13:00-13:30 UTC = 20:00-20:30 ICT (comfortably before nightly stop at 23:00 to avoid InvalidDBInstanceState)
DB_BACKUP_WINDOW="${DB_BACKUP_WINDOW:-13:00-13:30}"
DB_MAINTENANCE_WINDOW="${DB_MAINTENANCE_WINDOW:-sun:13:30-sun:14:00}"
DELETION_PROTECTION="${DELETION_PROTECTION:-}"
DB_PASSWORD="${DB_PASSWORD:-}"

command -v aws >/dev/null 2>&1 || { echo "ERROR: aws CLI is required." >&2; exit 1; }
command -v jq >/dev/null 2>&1 || { echo "ERROR: jq is required (pre-installed in AWS CloudShell; on Windows install via 'winget install jqlang.jq')." >&2; exit 1; }
command -v openssl >/dev/null 2>&1 || { echo "ERROR: openssl is required." >&2; exit 1; }

if [ -n "$DELETION_PROTECTION" ] && [ "$DELETION_PROTECTION" != "true" ] && [ "$DELETION_PROTECTION" != "false" ]; then
  echo "ERROR: DELETION_PROTECTION must be 'true' or 'false' (got '${DELETION_PROTECTION}')." >&2
  exit 1
fi

get_ssm_param_safe() {
  local pname="$1"
  local with_decryption="${2:-false}"
  local decrypt_flag=""
  [ "$with_decryption" = "true" ] && decrypt_flag="--with-decryption"

  local out=""
  local rc=0
  out="$(aws ssm get-parameter --region "$AWS_REGION" --name "$pname" $decrypt_flag --output json 2>&1)" || rc=$?
  if [ "$rc" -eq 0 ]; then
    jq -r '.Parameter.Value // empty' <<<"$out" | tr -d '\r\n'
    return 0
  fi

  if grep -q "ParameterNotFound" <<<"$out"; then
    echo ""
    return 0
  fi

  echo "ERROR: Failed to read SSM parameter '${pname}': ${out}" >&2
  return 1
}

echo "==> Region: ${AWS_REGION} | Env: ${ENV_NAME} | DB Instance: ${DB_INSTANCE_ID} | SSM Prefix: ${SSM_PREFIX}"

# 1. Resolve VPC, private subnets, and RDS Security Group created in O2
if [ -z "$SUBNET_IDS" ] || [ -z "$RDS_SG_ID" ]; then
  VPC_JSON="$(aws ec2 describe-vpcs \
    --region "$AWS_REGION" \
    --filters "Name=tag:Name,Values=${VPC_NAME}" \
    --output json)"
  VPC_COUNT="$(jq -r '.Vpcs | length' <<<"$VPC_JSON")"
  if [ "$VPC_COUNT" -eq 0 ]; then
    echo "ERROR: VPC '${VPC_NAME}' not found in ${AWS_REGION}. Complete Task O2 (docs/aws-o2-vpc-security-groups.md) first or pass SUBNET_IDS and RDS_SG_ID." >&2
    exit 1
  elif [ "$VPC_COUNT" -gt 1 ]; then
    echo "ERROR: Multiple VPCs matched Name='${VPC_NAME}'. Pass explicit SUBNET_IDS and RDS_SG_ID." >&2
    exit 1
  fi
  VPC_ID="$(jq -r '.Vpcs[0].VpcId' <<<"$VPC_JSON")"
  echo "==> Found VPC '${VPC_NAME}'."
fi

RESOLVED_SUBNET_IDS=()
if [ -n "$SUBNET_IDS" ]; then
  IFS=',' read -r -a RESOLVED_SUBNET_IDS <<<"$SUBNET_IDS"
else
  IFS=',' read -r -a SUBNET_NAME_LIST <<<"$PRIVATE_SUBNET_NAMES"
  for sname in "${SUBNET_NAME_LIST[@]}"; do
    sname="$(printf '%s' "$sname" | xargs)"
    [ -z "$sname" ] && continue
    SJSON="$(aws ec2 describe-subnets \
      --region "$AWS_REGION" \
      --filters "Name=vpc-id,Values=${VPC_ID}" "Name=tag:Name,Values=${sname}" \
      --output json)"
    SCOUNT="$(jq -r '.Subnets | length' <<<"$SJSON")"
    if [ "$SCOUNT" -ne 1 ]; then
      echo "ERROR: Expected 1 subnet named '${sname}' in VPC '${VPC_NAME}', found ${SCOUNT}." >&2
      exit 1
    fi
    RESOLVED_SUBNET_IDS+=("$(jq -r '.Subnets[0].SubnetId' <<<"$SJSON")")
  done
fi

if [ "${#RESOLVED_SUBNET_IDS[@]}" -lt 2 ]; then
  echo "ERROR: RDS DB Subnet Group requires at least 2 private subnets in 2 different AZs (found ${#RESOLVED_SUBNET_IDS[@]})." >&2
  exit 1
fi

# Verify subnets span at least 2 distinct Availability Zones
SUBNET_DESC_JSON="$(aws ec2 describe-subnets \
  --region "$AWS_REGION" \
  --subnet-ids "${RESOLVED_SUBNET_IDS[@]}" \
  --output json)"
AZ_COUNT="$(jq -r '[.Subnets[].AvailabilityZone] | unique | length' <<<"$SUBNET_DESC_JSON")"
if [ "$AZ_COUNT" -lt 2 ]; then
  echo "ERROR: Selected subnets must span at least 2 distinct Availability Zones (found ${AZ_COUNT})." >&2
  exit 1
fi

if [ -z "$RDS_SG_ID" ]; then
  SG_JSON="$(aws ec2 describe-security-groups \
    --region "$AWS_REGION" \
    --filters "Name=vpc-id,Values=${VPC_ID}" "Name=group-name,Values=${RDS_SG_NAME}" \
    --output json)"
  SG_COUNT="$(jq -r '.SecurityGroups | length' <<<"$SG_JSON")"
  if [ "$SG_COUNT" -ne 1 ]; then
    echo "ERROR: Expected 1 Security Group named '${RDS_SG_NAME}' in VPC '${VPC_NAME}', found ${SG_COUNT}." >&2
    exit 1
  fi
  RDS_SG_ID="$(jq -r '.SecurityGroups[0].GroupId' <<<"$SG_JSON")"
fi

echo "==> Resolved ${#RESOLVED_SUBNET_IDS[@]} private subnets across ${AZ_COUNT} AZs and RDS Security Group '${RDS_SG_NAME}'."

# 2. Create or verify DB Subnet Group
SGRP_ERR=""
SGRP_RC=0
SGRP_ERR="$(aws rds describe-db-subnet-groups \
  --region "$AWS_REGION" \
  --db-subnet-group-name "$DB_SUBNET_GROUP_NAME" \
  --output json 2>&1)" || SGRP_RC=$?

if [ "$SGRP_RC" -eq 0 ]; then
  echo "==> DB Subnet Group '${DB_SUBNET_GROUP_NAME}' already exists."
elif grep -q "DBSubnetGroupNotFoundFault" <<<"$SGRP_ERR"; then
  echo "==> Creating DB Subnet Group '${DB_SUBNET_GROUP_NAME}'..."
  aws rds create-db-subnet-group \
    --region "$AWS_REGION" \
    --db-subnet-group-name "$DB_SUBNET_GROUP_NAME" \
    --db-subnet-group-description "Private subnets for AI Exam Maker RDS (${ENV_NAME})" \
    --subnet-ids "${RESOLVED_SUBNET_IDS[@]}" \
    --tags "Key=Project,Value=ai-exam-maker" "Key=Environment,Value=${ENV_NAME}" "Key=Task,Value=O10" >/dev/null
else
  echo "ERROR: Failed to inspect DB Subnet Group '${DB_SUBNET_GROUP_NAME}': ${SGRP_ERR}" >&2
  exit 1
fi

# 3. Check if RDS instance already exists
DB_DESC_OUT=""
DB_DESC_RC=0
DB_DESC_OUT="$(aws rds describe-db-instances \
  --region "$AWS_REGION" \
  --db-instance-identifier "$DB_INSTANCE_ID" \
  --output json 2>&1)" || DB_DESC_RC=$?

INSTANCE_EXISTS="false"
if [ "$DB_DESC_RC" -eq 0 ]; then
  INSTANCE_EXISTS="true"
elif grep -q "DBInstanceNotFound" <<<"$DB_DESC_OUT"; then
  INSTANCE_EXISTS="false"
else
  echo "ERROR: Failed to inspect RDS instance '${DB_INSTANCE_ID}': ${DB_DESC_OUT}" >&2
  exit 1
fi

# 4. Create RDS PostgreSQL 16 instance (Single-AZ, private, encrypted, gp3 20 GB) if absent
if [ "$INSTANCE_EXISTS" = "false" ]; then
  if [ -z "$DB_ENGINE_VERSION" ]; then
    DB_ENGINE_VERSION="$(aws rds describe-db-engine-versions \
      --region "$AWS_REGION" \
      --engine postgres \
      --engine-version "$DB_ENGINE_MAJOR" \
      --default-only \
      --query "DBEngineVersions[0].EngineVersion" \
      --output text)"
    if [ -z "$DB_ENGINE_VERSION" ] || [ "$DB_ENGINE_VERSION" = "None" ]; then
      DB_ENGINE_VERSION="$(aws rds describe-db-engine-versions \
        --region "$AWS_REGION" \
        --engine postgres \
        --engine-version "$DB_ENGINE_MAJOR" \
        --query "DBEngineVersions[-1].EngineVersion" \
        --output text)"
    fi
  fi

  PASSWORD_PARAM_NAME="${SSM_PREFIX}/Rds/MasterPassword"
  if [ -z "$DB_PASSWORD" ]; then
    # Check if MasterPassword already exists in SSM (e.g. from an earlier interrupted run)
    EXISTING_SSM_PASS="$(get_ssm_param_safe "$PASSWORD_PARAM_NAME" "true")"
    if [ -n "$EXISTING_SSM_PASS" ]; then
      DB_PASSWORD="$EXISTING_SSM_PASS"
      echo "==> Reusing existing master password from SSM '${PASSWORD_PARAM_NAME}'."
    else
      # Alphanumeric 32-char password avoids Npgsql delimiter/quoting issues (;, =, ', ", @, /) and avoids SIGPIPE under pipefail
      DB_PASSWORD="A$(openssl rand -hex 15)9"
    fi
  fi

  # Persist master password to SSM SecureString BEFORE calling create-db-instance so password is never lost if interrupted
  echo "==> Persisting master password to SSM SecureString '${PASSWORD_PARAM_NAME}'..."
  aws ssm put-parameter \
    --region "$AWS_REGION" \
    --name "$PASSWORD_PARAM_NAME" \
    --type SecureString \
    --value "$DB_PASSWORD" \
    --description "RDS PostgreSQL master password for AI Exam Maker (AWS-managed aws/ssm)" \
    --overwrite >/dev/null

  CREATE_DEL_PROT="${DELETION_PROTECTION:-$([ "$ENV_NAME" = "prod" ] && echo "true" || echo "false")}"
  DEL_PROT_FLAG="--no-deletion-protection"
  if [ "$CREATE_DEL_PROT" = "true" ]; then
    DEL_PROT_FLAG="--deletion-protection"
  fi

  echo "==> Creating RDS PostgreSQL ${DB_ENGINE_VERSION} instance '${DB_INSTANCE_ID}' (${DB_INSTANCE_CLASS}, Single-AZ, private, encrypted, gp3 ${DB_ALLOCATED_STORAGE}GB, DeletionProtection=${CREATE_DEL_PROT})..."
  aws rds create-db-instance \
    --region "$AWS_REGION" \
    --db-instance-identifier "$DB_INSTANCE_ID" \
    --db-instance-class "$DB_INSTANCE_CLASS" \
    --engine postgres \
    --engine-version "$DB_ENGINE_VERSION" \
    --db-name "$DB_NAME" \
    --master-username "$DB_USERNAME" \
    --master-user-password "$DB_PASSWORD" \
    --allocated-storage "$DB_ALLOCATED_STORAGE" \
    --max-allocated-storage "$DB_MAX_ALLOCATED_STORAGE" \
    --storage-type gp3 \
    --storage-encrypted \
    --no-multi-az \
    --no-publicly-accessible \
    --db-subnet-group-name "$DB_SUBNET_GROUP_NAME" \
    --vpc-security-group-ids "$RDS_SG_ID" \
    --backup-retention-period "$DB_BACKUP_RETENTION_DAYS" \
    --preferred-backup-window "$DB_BACKUP_WINDOW" \
    --preferred-maintenance-window "$DB_MAINTENANCE_WINDOW" \
    --auto-minor-version-upgrade \
    --copy-tags-to-snapshot \
    --monitoring-interval 0 \
    --no-enable-performance-insights \
    "$DEL_PROT_FLAG" \
    --tags "Key=Project,Value=ai-exam-maker" "Key=Environment,Value=${ENV_NAME}" "Key=Task,Value=O10" >/dev/null

  echo "==> Waiting for RDS instance '${DB_INSTANCE_ID}' to become available (typically 4-7 minutes)..."
  aws rds wait db-instance-available \
    --region "$AWS_REGION" \
    --db-instance-identifier "$DB_INSTANCE_ID"
else
  CURRENT_STATUS="$(jq -r '.DBInstances[0].DBInstanceStatus // empty' <<<"$DB_DESC_OUT")"
  echo "==> Reusing existing RDS instance '${DB_INSTANCE_ID}' (status=${CURRENT_STATUS})."
  case "$CURRENT_STATUS" in
    creating|starting|modifying|backing-up|rebooting|upgrading|storage-optimization)
      echo "==> Waiting for RDS instance '${DB_INSTANCE_ID}' to become available..."
      aws rds wait db-instance-available \
        --region "$AWS_REGION" \
        --db-instance-identifier "$DB_INSTANCE_ID"
      ;;
    available)
      ;;
    stopped)
      echo "WARNING: RDS instance '${DB_INSTANCE_ID}' is currently 'stopped'." >&2
      echo "         Start it via: aws rds start-db-instance --region ${AWS_REGION} --db-instance-identifier ${DB_INSTANCE_ID}" >&2
      ;;
    *)
      echo "NOTE: RDS instance status is '${CURRENT_STATUS}'."
      ;;
  esac
fi

# 5. Fetch live endpoint/port/db/user metadata
FINAL_DB_JSON="$(aws rds describe-db-instances \
  --region "$AWS_REGION" \
  --db-instance-identifier "$DB_INSTANCE_ID" \
  --output json)"

DB_ENDPOINT="$(jq -r '.DBInstances[0].Endpoint.Address // empty' <<<"$FINAL_DB_JSON")"
DB_PORT="$(jq -r '.DBInstances[0].Endpoint.Port // 5432' <<<"$FINAL_DB_JSON")"
ACTUAL_DB_NAME="$(jq -r '.DBInstances[0].DBName // empty' <<<"$FINAL_DB_JSON")"
ACTUAL_USERNAME="$(jq -r '.DBInstances[0].MasterUsername // empty' <<<"$FINAL_DB_JSON")"

if [ -z "$DB_ENDPOINT" ]; then
  echo "ERROR: RDS instance '${DB_INSTANCE_ID}' does not have an Endpoint yet (current status may be stopped). Start it and re-run." >&2
  exit 1
fi

# 6. Publish metadata and SecureString connection string to SSM Parameter Store (AWS-managed aws/ssm = $0)
put_string_param() {
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
  echo "    Stored SSM String parameter: ${name}"
}

echo "==> Writing RDS metadata to SSM Parameter Store under '${SSM_PREFIX}/Rds/'..."
put_string_param "${SSM_PREFIX}/Rds/InstanceId" "$DB_INSTANCE_ID" "RDS PostgreSQL instance identifier"
put_string_param "${SSM_PREFIX}/Rds/Endpoint" "$DB_ENDPOINT" "RDS PostgreSQL private endpoint hostname"
put_string_param "${SSM_PREFIX}/Rds/Port" "$DB_PORT" "RDS PostgreSQL port"
put_string_param "${SSM_PREFIX}/Rds/Database" "${ACTUAL_DB_NAME:-$DB_NAME}" "RDS PostgreSQL database name"
put_string_param "${SSM_PREFIX}/Rds/Username" "${ACTUAL_USERNAME:-$DB_USERNAME}" "RDS PostgreSQL master username"

CONN_PARAM_NAME="${SSM_PREFIX}/ConnectionStrings/ExamMaker"
PASSWORD_PARAM_NAME="${SSM_PREFIX}/Rds/MasterPassword"

if [ -n "$DB_PASSWORD" ]; then
  # Explicit DB_PASSWORD provided (new instance OR password update/rotation on existing instance)
  # Update BOTH MasterPassword and ConnectionStrings so they stay 100% in sync!
  aws ssm put-parameter \
    --region "$AWS_REGION" \
    --name "$PASSWORD_PARAM_NAME" \
    --type SecureString \
    --value "$DB_PASSWORD" \
    --description "RDS PostgreSQL master password for AI Exam Maker (AWS-managed aws/ssm)" \
    --overwrite >/dev/null
  echo "    Stored SSM SecureString parameter: ${PASSWORD_PARAM_NAME} (value hidden)"

  CONN_STRING="Host=${DB_ENDPOINT};Port=${DB_PORT};Database=${ACTUAL_DB_NAME:-$DB_NAME};Username=${ACTUAL_USERNAME:-$DB_USERNAME};Password=${DB_PASSWORD};SSL Mode=Require"
  aws ssm put-parameter \
    --region "$AWS_REGION" \
    --name "$CONN_PARAM_NAME" \
    --type SecureString \
    --value "$CONN_STRING" \
    --description "Npgsql PostgreSQL connection string for AI Exam Maker (AWS-managed aws/ssm)" \
    --overwrite >/dev/null
  unset DB_PASSWORD CONN_STRING
  echo "    Stored SSM SecureString parameter: ${CONN_PARAM_NAME} (value hidden)"
else
  # No DB_PASSWORD provided:
  # Check if connection string already exists
  EXISTING_CONN="$(get_ssm_param_safe "$CONN_PARAM_NAME" "true")"
  if [ -n "$EXISTING_CONN" ]; then
    echo "    Kept existing SSM SecureString parameter: ${CONN_PARAM_NAME}"
  else
    # Connection string does NOT exist, try to construct it from MasterPassword if available
    SAVED_PASSWORD="$(get_ssm_param_safe "$PASSWORD_PARAM_NAME" "true")"
    if [ -n "$SAVED_PASSWORD" ]; then
      CONN_STRING="Host=${DB_ENDPOINT};Port=${DB_PORT};Database=${ACTUAL_DB_NAME:-$DB_NAME};Username=${ACTUAL_USERNAME:-$DB_USERNAME};Password=${SAVED_PASSWORD};SSL Mode=Require"
      aws ssm put-parameter \
        --region "$AWS_REGION" \
        --name "$CONN_PARAM_NAME" \
        --type SecureString \
        --value "$CONN_STRING" \
        --description "Npgsql PostgreSQL connection string for AI Exam Maker (AWS-managed aws/ssm)" \
        --overwrite >/dev/null
      unset SAVED_PASSWORD CONN_STRING
      echo "    Stored SSM SecureString parameter: ${CONN_PARAM_NAME} from ${PASSWORD_PARAM_NAME} (value hidden)"
    else
      echo "WARNING: Existing RDS instance reused, but neither '${CONN_PARAM_NAME}' nor '${PASSWORD_PARAM_NAME}' was found in SSM." >&2
      echo "         Re-run with DB_PASSWORD='<existing-password>' ./infra/rds/setup-rds.sh to populate them." >&2
    fi
  fi
fi

echo ""
echo "DONE: RDS PostgreSQL and SSM parameters are ready."
echo "Share ONLY the SSM path '${SSM_PREFIX}/ConnectionStrings/ExamMaker' with DEV-A / EC2 runtime (never paste passwords in chat)."
echo "Next step: run './infra/rds/verify-rds.sh' to verify all 15 security, network, and budget checks."
