# O7 EC2/SSM Frontend Deployment Design

## Intent

Adapt O7 to the approved O5 alternative: deploy the static frontend to the
EC2/Nginx origin without CloudFront, while keeping GitHub Actions authentication
on OIDC and avoiding SSH or long-lived AWS access keys.

## Goals

- Build `frontend/out` on GitHub Actions with Cognito configuration from GitHub
  Repository Variables.
- Upload a versioned frontend archive to the existing private S3 bucket.
- Use AWS Systems Manager Run Command to deploy the archive on EC2.
- Verify Nginx/frontend health and preserve the previous frontend release for
  rollback.
- Keep all real account IDs, ARNs, Cognito values, and bucket names out of Git.

## Non-goals

- Creating or using a CloudFront distribution.
- Opening SSH or storing SSH keys in GitHub.
- Changing the working backend deployment managed by O15.
- Replacing the currently working manual deployment until the workflow passes a
  full smoke test.

## Approaches considered

1. Manual S3 upload plus manual EC2 commands: already proven, but not CI/CD.
2. GitHub Actions → S3 artifact → SSM Run Command → EC2: selected because it
   matches the current architecture, uses temporary OIDC credentials, and keeps
   SSH closed.
3. GitHub Actions SCP/SSH to EC2: rejected because it violates the project's
   no-SSH deployment rule.

## Proposed flow

1. A push to `main` affecting `frontend/**` or the workflow starts `Deploy
   frontend`.
2. Actions validates public Cognito variables, installs dependencies, and runs
   the static build.
3. Actions creates a tarball containing only `frontend/out` and uploads it to a
   versioned S3 key such as `frontend/releases/<sha>.tar.gz`.
4. Actions assumes the O6 role through GitHub OIDC and calls SSM
   `SendCommand` on the tagged/known EC2 instance.
5. EC2 downloads the artifact using its instance role, verifies the archive,
   extracts it under `/var/www/exammaker-frontend/releases/<sha>`, atomically
   updates the `current` symlink, fixes ownership/permissions, and reloads
   Nginx.
6. The command verifies `/` and a fingerprinted `_next/static` asset over the
   public HTTPS domain. A failed check restores the previous symlink.

## IAM boundaries

- GitHub OIDC role: `s3:PutObject`/`s3:GetObject`/`s3:ListBucket` for the
  frontend artifact prefix and `ssm:SendCommand` only for the target EC2
  instance and approved SSM document.
- EC2 instance role: `s3:GetObject` only for the frontend release prefix and
  the existing runtime permissions.
- No `cloudfront:*`, `AdministratorAccess`, access keys, or SSH permissions are
  required for this alternative.

## Rollback

The EC2 deploy command keeps the active release and a bounded number of prior
release directories. If Nginx reload, index retrieval, or asset retrieval
fails, it restores the previous `current` symlink and reports the SSM command as
failed. The GitHub Actions artifact is retained for manual redeployment.

## Verification

- Workflow succeeds on `main`.
- SSM command succeeds and updates the EC2 release symlink.
- `https://app.<domain>/` returns the new build.
- A `/_next/static/*.js` request returns `200`.
- Cognito login and an authenticated API request continue to work.
- A deliberately failed smoke check leaves the previous release active.
