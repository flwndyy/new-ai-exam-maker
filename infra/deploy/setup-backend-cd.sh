#!/usr/bin/env bash
# Idempotent AWS infrastructure setup for Backend CI/CD & EF Core Migrations (AI Exam Maker - Task O15).
# Provisions and reconciles in ap-southeast-1:
#   1. Private S3 artifacts bucket (Block Public Access, SSE-S3 = $0, TLS-only policy, 30-day lifecycle expiry)
#   2. GitHub Actions OIDC IAM Role 'aem-github-backend-deploy-role' locked to:
#        repo:flwndyy/new-ai-exam-maker:ref:refs/heads/main + workflow:"Deploy backend"
#      with least-privilege inline policy (S3 releases prefix, SSM SendCommand to tag Name=aem-exammaker-ec2,
#      EC2 DescribeInstances, optional RDS pre-migration snapshot)
#   3. Least-privilege inline policy on EC2 role 'aem-exammaker-ec2-role' allowing s3:GetObject on backend/releases/*
#
# Privacy: Never prints 12-digit AWS Account ID or unmasked ARNs to stdout.
#
# Dependencies: aws (CLI v2), jq (both pre-installed in AWS CloudShell).
#
# Usage:
#   ./infra/deploy/setup-backend-cd.sh
#   BACKEND_ARTIFACTS_BUCKET="my-custom-bucket-name" ./infra/deploy/setup-backend-cd.sh

set -euo pipefail

export MSYS_NO_PATHCONV=1
export MSYS2_ARG_CONV_EXCL="*"

EXPECTED_REGION="ap-southeast-1"
AWS_REGION="${AWS_REGION:-$EXPECTED_REGION}"
ALLOW_NON_DEFAULT_REGION="${ALLOW_NON_DEFAULT_REGION:-0}"
ENV_NAME="${ENV_NAME:-dev}"
EC2_TAG_NAME="${EC2_TAG_NAME:-aem-exammaker-ec2}"
EC2_ROLE_NAME="${EC2_ROLE_NAME:-aem-exammaker-ec2-role}"
BACKEND_ROLE_NAME="${BACKEND_ROLE_NAME:-aem-github-backend-deploy-role}"
RDS_INSTANCE_ID="${RDS_INSTANCE_ID:-exammaker-${ENV_NAME}-pg}"
GITHUB_REPO="${GITHUB_REPO:-flwndyy/new-ai-exam-maker}"
GITHUB_WORKFLOW_NAME="${GITHUB_WORKFLOW_NAME:-Deploy backend}"
ARTIFACTS_RETENTION_DAYS="${ARTIFACTS_RETENTION_DAYS:-30}"

command -v aws >/dev/null 2>&1 || { echo "ERROR: aws CLI is required." >&2; exit 1; }
command -v jq >/dev/null 2>&1 || { echo "ERROR: jq is required." >&2; exit 1; }

if [ "$AWS_REGION" != "$EXPECTED_REGION" ] && [ "$ALLOW_NON_DEFAULT_REGION" != "1" ]; then
  echo "ERROR: AWS_REGION is '${AWS_REGION}', but AI Exam Maker requires '${EXPECTED_REGION}' (Singapore)." >&2
  exit 1
fi

ACCOUNT_ID="$(aws sts get-caller-identity --query Account --output text 2>/dev/null | tr -d '\r\n' || true)"
if ! [[ "$ACCOUNT_ID" =~ ^[0-9]{12}$ ]]; then
  echo "ERROR: Failed to resolve 12-digit AWS Account ID via sts get-caller-identity." >&2
  exit 1
fi

# Derive a globally unique, non-PII bucket suffix from a truncated SHA-256 hash of AccountId (never exposes AccountId)
if [ -z "${BACKEND_ARTIFACTS_BUCKET:-}" ]; then
  if command -v sha256sum >/dev/null 2>&1; then
    HASH_SUFFIX="$(printf '%s' "exammaker-backend-artifacts-${ACCOUNT_ID}" | sha256sum | cut -c1-10)"
  elif command -v shasum >/dev/null 2>&1; then
    HASH_SUFFIX="$(printf '%s' "exammaker-backend-artifacts-${ACCOUNT_ID}" | shasum -a 256 | cut -c1-10)"
  else
    echo "ERROR: sha256sum or shasum is required to derive default bucket name (or set BACKEND_ARTIFACTS_BUCKET explicitly)." >&2
    exit 1
  fi
  BACKEND_ARTIFACTS_BUCKET="aem-${ENV_NAME}-backend-artifacts-${HASH_SUFFIX}"
fi

echo "==> Region: ${AWS_REGION} | Env: ${ENV_NAME}"
echo "==> Artifacts Bucket: ${BACKEND_ARTIFACTS_BUCKET} | Deploy Role: ${BACKEND_ROLE_NAME} | EC2 Role: ${EC2_ROLE_NAME}"

# ---------------------------------------------------------------------------
# 1. Verify GitHub OIDC Provider exists in account (created in O6)
# ---------------------------------------------------------------------------
echo ""
echo "==> 1. Verifying GitHub OIDC Identity Provider..."
OIDC_ARN="arn:aws:iam::${ACCOUNT_ID}:oidc-provider/token.actions.githubusercontent.com"
if ! aws iam get-open-id-connect-provider --open-id-connect-provider-arn "$OIDC_ARN" >/dev/null 2>&1; then
  echo "ERROR: GitHub OIDC provider (token.actions.githubusercontent.com) not found. Complete Task O6 first." >&2
  exit 1
fi
echo "    [OK]       GitHub OIDC provider is configured."

# ---------------------------------------------------------------------------
# 2. Create & harden private S3 bucket for backend release artifacts
# ---------------------------------------------------------------------------
echo ""
echo "==> 2. Configuring private S3 artifacts bucket '${BACKEND_ARTIFACTS_BUCKET}'..."
if aws s3api head-bucket --bucket "$BACKEND_ARTIFACTS_BUCKET" 2>/dev/null; then
  echo "    [EXISTS]   Bucket '${BACKEND_ARTIFACTS_BUCKET}'"
else
  aws s3api create-bucket \
    --bucket "$BACKEND_ARTIFACTS_BUCKET" \
    --region "$AWS_REGION" \
    --create-bucket-configuration "LocationConstraint=${AWS_REGION}" >/dev/null
  echo "    [CREATED]  Bucket '${BACKEND_ARTIFACTS_BUCKET}'"
fi

aws s3api put-public-access-block \
  --bucket "$BACKEND_ARTIFACTS_BUCKET" \
  --public-access-block-configuration "BlockPublicAcls=true,IgnorePublicAcls=true,BlockPublicPolicy=true,RestrictPublicBuckets=true"

aws s3api put-bucket-encryption \
  --bucket "$BACKEND_ARTIFACTS_BUCKET" \
  --server-side-encryption-configuration '{"Rules":[{"ApplyServerSideEncryptionByDefault":{"SSEAlgorithm":"AES256"}}]}'

aws s3api put-bucket-tagging \
  --bucket "$BACKEND_ARTIFACTS_BUCKET" \
  --tagging "TagSet=[{Key=Project,Value=ai-exam-maker},{Key=Environment,Value=${ENV_NAME}},{Key=Task,Value=O15}]"

BUCKET_POLICY="$(jq -nc --arg b "$BACKEND_ARTIFACTS_BUCKET" '{
  Version: "2012-10-17",
  Statement: [
    {
      Sid: "DenyInsecureTransport",
      Effect: "Deny",
      Principal: "*",
      Action: "s3:*",
      Resource: ["arn:aws:s3:::" + $b, "arn:aws:s3:::" + $b + "/*"],
      Condition: { Bool: { "aws:SecureTransport": "false" } }
    }
  ]
}')"
aws s3api put-bucket-policy --bucket "$BACKEND_ARTIFACTS_BUCKET" --policy "$BUCKET_POLICY"

LIFECYCLE_CFG="$(jq -nc --argjson days "$ARTIFACTS_RETENTION_DAYS" '{
  Rules: [
    {
      ID: "ExpireOldBackendReleases",
      Status: "Enabled",
      Filter: { Prefix: "backend/releases/" },
      Expiration: { Days: $days },
      AbortIncompleteMultipartUpload: { DaysAfterInitiation: 3 }
    }
  ]
}')"
aws s3api put-bucket-lifecycle-configuration \
  --bucket "$BACKEND_ARTIFACTS_BUCKET" \
  --lifecycle-configuration "$LIFECYCLE_CFG" >/dev/null
echo "    [HARDENED] BlockPublicAccess=ON, SSE-S3=AES256, TLS-only policy, Lifecycle=${ARTIFACTS_RETENTION_DAYS}d expiry"

# ---------------------------------------------------------------------------
# 3. Create or update GitHub Actions OIDC Backend Deploy Role
# ---------------------------------------------------------------------------
echo ""
echo "==> 3. Configuring GitHub OIDC IAM Role '${BACKEND_ROLE_NAME}'..."
TRUST_POLICY="$(ACCT_ENV="$ACCOUNT_ID" REPO_ENV="$GITHUB_REPO" WF_ENV="$GITHUB_WORKFLOW_NAME" jq -nc '{
  Version: "2012-10-17",
  Statement: [
    {
      Sid: "GitHubActionsBackendMainOnly",
      Effect: "Allow",
      Principal: {
        Federated: ("arn:aws:iam::" + env.ACCT_ENV + ":oidc-provider/token.actions.githubusercontent.com")
      },
      Action: "sts:AssumeRoleWithWebIdentity",
      Condition: {
        StringEquals: {
          "token.actions.githubusercontent.com:aud": "sts.amazonaws.com",
          "token.actions.githubusercontent.com:sub": ("repo:" + env.REPO_ENV + ":ref:refs/heads/main"),
          "token.actions.githubusercontent.com:workflow": env.WF_ENV,
          "token.actions.githubusercontent.com:ref": "refs/heads/main"
        }
      }
    }
  ]
}')"

if aws iam get-role --role-name "$BACKEND_ROLE_NAME" >/dev/null 2>&1; then
  aws iam update-assume-role-policy \
    --role-name "$BACKEND_ROLE_NAME" \
    --policy-document "$TRUST_POLICY"
  echo "    [UPDATED]  Trust policy for '${BACKEND_ROLE_NAME}'"
else
  aws iam create-role \
    --role-name "$BACKEND_ROLE_NAME" \
    --assume-role-policy-document "$TRUST_POLICY" \
    --description "GitHub Actions OIDC deploy role for AI Exam Maker backend (main branch only, Task O15)" \
    --tags "Key=Project,Value=ai-exam-maker" "Key=Environment,Value=${ENV_NAME}" "Key=Task,Value=O15" >/dev/null
  echo "    [CREATED]  Role '${BACKEND_ROLE_NAME}'"
fi

DEPLOY_POLICY="$(ACCT_ENV="$ACCOUNT_ID" REG_ENV="$AWS_REGION" ENV_ENV="$ENV_NAME" BUCKET_ENV="$BACKEND_ARTIFACTS_BUCKET" TAG_ENV="$EC2_TAG_NAME" RDS_ENV="$RDS_INSTANCE_ID" jq -nc '{
  Version: "2012-10-17",
  Statement: [
    {
      Sid: "UploadBackendReleaseArtifacts",
      Effect: "Allow",
      Action: ["s3:PutObject", "s3:GetObject"],
      Resource: ("arn:aws:s3:::" + env.BUCKET_ENV + "/backend/releases/*")
    },
    {
      Sid: "ListBackendReleaseBucket",
      Effect: "Allow",
      Action: "s3:ListBucket",
      Resource: ("arn:aws:s3:::" + env.BUCKET_ENV),
      Condition: { StringLike: { "s3:prefix": "backend/releases/*" } }
    },
    {
      Sid: "ResolveTaggedEc2Instance",
      Effect: "Allow",
      Action: "ec2:DescribeInstances",
      Resource: "*"
    },
    {
      Sid: "SendSsmRunShellScriptToEc2",
      Effect: "Allow",
      Action: "ssm:SendCommand",
      Resource: ["arn:aws:ssm:" + env.REG_ENV + "::document/AWS-RunShellScript"]
    },
    {
      Sid: "SendSsmCommandToAppInstanceOnly",
      Effect: "Allow",
      Action: "ssm:SendCommand",
      Resource: ("arn:aws:ec2:" + env.REG_ENV + ":" + env.ACCT_ENV + ":instance/*"),
      Condition: { StringEquals: { "ssm:resourceTag/Name": env.TAG_ENV } }
    },
    {
      Sid: "PollSsmCommandInvocation",
      Effect: "Allow",
      Action: ["ssm:GetCommandInvocation", "ssm:ListCommandInvocations"],
      Resource: "*"
    },
    {
      Sid: "OptionalPreMigrationRdsSnapshot",
      Effect: "Allow",
      Action: ["rds:CreateDBSnapshot", "rds:DescribeDBSnapshots", "rds:DescribeDBInstances"],
      Resource: [
        ("arn:aws:rds:" + env.REG_ENV + ":" + env.ACCT_ENV + ":db:" + env.RDS_ENV),
        ("arn:aws:rds:" + env.REG_ENV + ":" + env.ACCT_ENV + ":snapshot:exammaker-" + env.ENV_ENV + "-pre-mig-*")
      ]
    }
  ]
}')"

aws iam put-role-policy \
  --role-name "$BACKEND_ROLE_NAME" \
  --policy-name "aem-github-backend-deploy-policy" \
  --policy-document "$DEPLOY_POLICY"
echo "    [ATTACHED] Least-privilege inline policy 'aem-github-backend-deploy-policy'"

# ---------------------------------------------------------------------------
# 4. Grant EC2 role read-only access to backend release artifacts in S3
# ---------------------------------------------------------------------------
echo ""
echo "==> 4. Granting EC2 role '${EC2_ROLE_NAME}' read-only s3:GetObject on backend/releases/*..."
if aws iam get-role --role-name "$EC2_ROLE_NAME" >/dev/null 2>&1; then
  EC2_S3_POLICY="$(BUCKET_ENV="$BACKEND_ARTIFACTS_BUCKET" jq -nc '{
    Version: "2012-10-17",
    Statement: [
      {
        Sid: "ReadBackendReleaseArtifacts",
        Effect: "Allow",
        Action: "s3:GetObject",
        Resource: ("arn:aws:s3:::" + env.BUCKET_ENV + "/backend/releases/*")
      }
    ]
  }')"
  aws iam put-role-policy \
    --role-name "$EC2_ROLE_NAME" \
    --policy-name "aem-ec2-read-backend-artifacts" \
    --policy-document "$EC2_S3_POLICY"
  echo "    [ATTACHED] Inline policy 'aem-ec2-read-backend-artifacts' on '${EC2_ROLE_NAME}'"
else
  echo "    [WARN]     EC2 role '${EC2_ROLE_NAME}' not found; attach ReadBackendReleaseArtifacts manually per infra/iam/exammaker-ec2-policy.example.json." >&2
fi

unset ACCOUNT_ID

echo ""
echo "DONE: Backend CD infrastructure (Task O15) is ready."
echo "Next steps:"
echo "  1. Configure GitHub Repository Variables (Settings -> Secrets and variables -> Actions -> Variables):"
echo "     - BACKEND_ARTIFACTS_BUCKET = ${BACKEND_ARTIFACTS_BUCKET}"
echo "     - AWS_BACKEND_ROLE_ARN     = arn:aws:iam::<your-account-id>:role/${BACKEND_ROLE_NAME}"
echo "  2. Run './infra/deploy/verify-backend-cd.sh' to verify all 10 security, OIDC, S3, and SSM checks."
