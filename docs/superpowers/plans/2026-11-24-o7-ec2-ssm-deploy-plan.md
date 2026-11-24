# O7 EC2/SSM Frontend Deployment Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:executing-plans to implement this plan task-by-task.

**Goal:** Replace the CloudFront-specific frontend deployment in O7 with an OIDC-authenticated GitHub Actions deployment through S3, SSM, and the EC2/Nginx origin.

**Architecture:** GitHub Actions builds the static export, uploads a versioned archive and deploy script to the private frontend S3 bucket, then sends an `AWS-RunShellScript` command to the EC2 instance. The EC2 script downloads, verifies, extracts, atomically switches the frontend `current` symlink, reloads Nginx, and rolls back on failed smoke checks.

**Tech Stack:** GitHub Actions, AWS OIDC, AWS CLI, S3, Systems Manager Run Command, Bash, Nginx, Next.js static export.

**Spec:** `docs/superpowers/specs/2026-11-24-o7-ec2-ssm-deploy-design.md`

## Global Constraints

- Do not use CloudFront, SSH, long-lived AWS access keys, or `AdministratorAccess`.
- Keep real account IDs, ARNs, Cognito values, bucket names, and instance IDs in GitHub Variables or AWS configuration only.
- Deploy only from `main` and retain the workflow name `Deploy frontend`.
- Preserve the current manual EC2 deployment until the automated workflow passes a full smoke test.
- Do not modify the O15 backend service or its `/opt/exammaker` release layout.

## Review Focus

- A missing or malformed Cognito variable must fail before the build.
- A failed SSM command must fail the workflow rather than report a successful deploy.
- A missing artifact or checksum mismatch must leave the previous frontend release active.
- A failed Nginx reload or HTTPS asset check must restore the previous `current` symlink.
- A rerun must use a unique release key and not overwrite another commit's release.

### Task 1: EC2 frontend release deploy script

**Files:**
- Create: `infra/deploy/deploy-frontend-on-ec2.sh`
- Test: `infra/deploy/deploy-frontend-on-ec2.test.sh` (or a shell-level validation fixture if the repository has no shell test harness)

- [ ] Define arguments for bucket, artifact key, release ID, frontend root, and public health URL.
- [ ] Download the archive and SHA-256 checksum using the EC2 instance role.
- [ ] Verify the checksum before extraction.
- [ ] Extract into `/var/www/exammaker-frontend/releases/<release-id>` with safe directory/file permissions and `nginx:nginx` ownership.
- [ ] Atomically update `/var/www/exammaker-frontend/current` and reload Nginx.
- [ ] Verify the root page and one `_next/static` asset over HTTPS.
- [ ] Restore the previous symlink if extraction, reload, or smoke verification fails.
- [ ] Keep a bounded number of previous frontend releases.

### Task 2: O7 workflow migration from CloudFront to S3 + SSM

**Files:**
- Modify: `.github/workflows/deploy-frontend.yml`

- [ ] Remove CloudFront variables, CloudFront invalidation, and CloudFront-specific upload behavior.
- [ ] Validate `AWS_ROLE_ARN`, `FRONTEND_BUCKET_NAME`, `EC2_INSTANCE_ID`, and Cognito variables.
- [ ] Build the static export and retain the GitHub artifact.
- [ ] Create a versioned tarball and checksum from `frontend/out` plus the deploy script.
- [ ] Upload the tarball, checksum, and script under `frontend/releases/<github.sha>/`.
- [ ] Call `ssm:SendCommand` with `AWS-RunShellScript` on the configured EC2 instance.
- [ ] Poll `GetCommandInvocation` until the command succeeds or fails, then print command output on failure.

### Task 3: IAM examples and repository variable documentation

**Files:**
- Modify: `infra/iam/github-actions-frontend-deploy-policy.example.json`
- Modify: `docs/aws-o6-github-oidc.md`
- Modify: `docs/aws-o7-frontend-cicd.md`

- [ ] Replace CloudFront invalidation permission with least-privilege SSM SendCommand and command-result permissions.
- [ ] Document the EC2 role read permission for `frontend/releases/*`.
- [ ] Document `EC2_INSTANCE_ID` and remove the CloudFront distribution variable.
- [ ] Document artifact keys, SSM execution, health verification, and rollback.
- [ ] Keep all real values as placeholders.

### Task 4: Verification

- [ ] Validate workflow YAML and shell syntax.
- [ ] Run the frontend build with non-secret test variables.
- [ ] Confirm the archive contains only the static export and deploy script.
- [ ] Review the diff for secrets, account IDs, ARNs, Cognito values, and CloudFront remnants.
- [ ] Push a feature branch and create the O7 PR only after checks pass.
