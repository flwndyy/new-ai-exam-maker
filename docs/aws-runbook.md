# AWS deployment runbook (Console-first)

This runbook is the O1 deliverable for the AI Exam Maker demo. It describes the order and
checks for O2–O5. O1 itself does not create AWS resources. Use the AWS Console for resource
creation and the templates in `/infra` for host configuration.

## 1. Decisions and safety rules

The current project decisions are:

- Region: `ap-southeast-1`.
- EC2: `t4g.small`, ARM64, API and Worker on the same host.
- RDS: `db.t4g.micro`, Single-AZ, private subnet.
- EC2 is in a public subnet; RDS is in a private subnet.
- No NAT Gateway, ALB, Multi-AZ, or SSH ingress.
- Frontend: `app.<domain>` through CloudFront; origin: `origin.<domain>` to the EC2 Elastic IP.
- Origin TLS: Let's Encrypt HTTP-01. Port 80 is only for the ACME challenge; port 443 is
  restricted to the CloudFront managed origin-facing prefix list.
- CloudFront ACM certificate must be requested in `us-east-1`.

Before creating anything, record the chosen domain and AWS region in the team workspace.
Do not paste secrets, access keys, private keys, account IDs, resource IDs, or ARNs into GitHub.
Use Parameter Store for runtime secrets and configuration.

## 2. Deployment order

### O2 — VPC and Security Groups

1. In `ap-southeast-1`, create one VPC and at least two Availability Zone subnets:
   - public subnet for EC2;
   - private subnet for RDS.
2. Attach an Internet Gateway to the VPC. Do not create a NAT Gateway.
3. Create the EC2 Security Group:
   - TCP 443: source `com.amazonaws.global.cloudfront.origin-facing` managed prefix list;
   - TCP 80: `0.0.0.0/0` only because Let's Encrypt HTTP-01 needs it;
   - no TCP 22 rule.
4. Create the RDS Security Group:
   - PostgreSQL 5432: source must be the EC2 Security Group only.
5. Check the Security Group rule quota before adding the CloudFront prefix list rule.

Record resource names locally for the next steps, but do not commit their IDs/ARNs.

### O3 — EC2, EBS, Elastic IP, SSM, and IAM

1. Launch an ARM64 Amazon Linux 2023 `t4g.small` instance in the public subnet with a
   20–30 GB `gp3` EBS root volume and the EC2 Security Group.
2. Attach an Elastic IP and record it in the private team notes only.
3. Attach an instance profile with least privilege for the services the application needs:
   SQS, Bedrock, SSM, Cognito Admin, S3 if required, and CloudWatch logging as separately
   justified. Do not put an access key on the instance.
4. Confirm the SSM Agent is online and open a Session Manager shell. Do not open SSH.
5. Apply `infra/bootstrap/user-data.sh.example` or run its reviewed commands through SSM.
6. Copy the systemd templates to `/etc/systemd/system/` after replacing placeholders and
   install the published ARM64 API/Worker binaries under `/opt/exammaker/current`.

### O4 — Domain, DNS, Nginx, and TLS

1. Create DNS records:
   - `app.<domain>` → CloudFront distribution alias (after O5 creates it);
   - `origin.<domain>` → EC2 Elastic IP.
2. Request the CloudFront ACM certificate in `us-east-1` for `app.<domain>` and validate it
   through DNS.
3. On EC2, create `/var/www/letsencrypt`, install Certbot using the approved Amazon Linux
   package instructions, and issue the origin certificate for `origin.<domain>` with
   HTTP-01.
4. Copy `infra/nginx/exammaker.conf.example` to the Nginx configuration directory. Replace
   only the domain and the origin secret. Load the secret from the approved Parameter Store
   deployment step; do not commit the value.
5. Enable the Nginx configuration, run `nginx -t`, reload Nginx, and confirm the Certbot
   renewal timer is active.
6. Confirm direct requests without the `X-Origin-Verify` header receive `403`. Confirm the
   API receives `X-Forwarded-For` and `X-Forwarded-Proto` from Nginx.

### O5 — S3 and CloudFront

1. Create a private S3 bucket in the selected region for the static frontend. Block public
   access and configure CloudFront Origin Access Control (OAC).
2. Upload the Next.js static export. Do not use an S3 website endpoint.
3. Create a CloudFront distribution with:
   - default `/*` behavior to S3, normal caching;
   - `/api/*` behavior to `origin.<domain>` over HTTPS only;
   - `/api/*` cache policy `CachingDisabled`;
   - `/api/*` origin request policy `AllViewerExceptHostHeader`;
   - all API methods required by the OpenAPI contract;
   - custom `X-Origin-Verify` header with the same secret configured at Nginx;
   - the ACM certificate from `us-east-1`;
   - no custom error response rewriting to `index.html`.
4. Associate `infra/cloudfront-function/index-html-rewrite.js` with the viewer-request
   event on the default `/*` behavior only. It must not be attached to `/api/*`.
5. Add the `app.<domain>` alternate domain name and point its DNS record to CloudFront.
6. Wait for deployment, then test the frontend route, API route, direct origin rejection,
   and a refresh on a static-export route.

## 3. Verification checklist

- [ ] `git diff --check` returns no whitespace errors.
- [ ] `rg` finds no access keys, private keys, real account/resource IDs, ARNs, or secrets.
- [ ] EC2 is ARM64 and the API/Worker binaries are `linux-arm64`.
- [ ] SSM Session Manager works and no Security Group rule allows SSH.
- [ ] EC2 443 is reachable through CloudFront only; port 80 serves only ACME challenge files.
- [ ] Nginx rejects a request without the correct origin verification header.
- [ ] CloudFront `/api/*` does not cache and forwards the required authorization/header data.
- [ ] CloudFront `/*` serves S3 content and the function rewrites route paths to `index.html`.
- [ ] S3 blocks public access and is reachable only through CloudFront OAC.
- [ ] `nginx -t`, systemd status checks, and CloudFront test requests are recorded in the PR.
- [ ] AWS Budget alarms are configured by O14; stop unused EC2/RDS resources after testing.

## 4. Cleanup after the demo

Follow the project cleanup task O16: snapshot what is needed, stop RDS/EC2 when not in use,
remove unattached EBS volumes, release unused Elastic IPs, and verify that no NAT Gateway,
load balancer, idle public IP, or test CloudFront distribution remains.
