# AWS infrastructure scaffold

This directory contains the Console-first deployment artifacts for the AI Exam Maker demo.
It is intentionally a configuration scaffold, not a complete IaC implementation. Follow
[`../docs/aws-runbook.md`](../docs/aws-runbook.md) for the deployment order.

## Scope

- `bootstrap/`: ARM64 EC2 user-data template for installing the host prerequisites.
- `nginx/`: origin reverse-proxy template for `origin.<domain>`.
- `systemd/`: API and Worker service templates.
- `cloudfront-function/`: viewer-request function for static-export route fallback.

## Placeholder policy

Files ending in `.example` contain placeholders only. Replace them during deployment or
load values from AWS Systems Manager Parameter Store. Never commit real secrets, account
IDs, resource IDs, ARNs, private keys, or production domains to this directory.

The actual VPC, subnets, Security Groups, EC2, RDS, S3, CloudFront, and DNS resources are
created in the AWS Console by O2–O5. O1 does not provision AWS resources.
