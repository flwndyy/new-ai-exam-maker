# O1 Infrastructure Framework Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:executing-plans to implement this plan task-by-task.

**Goal:** Create the Console-first AWS deployment scaffold and runbook required by O1, without provisioning AWS resources or storing real identifiers/secrets.

**Architecture:** Keep infrastructure artifacts under `/infra` and operational instructions under `docs/`. The scaffold provides reusable sample configuration for EC2 bootstrap, Nginx, systemd, and the CloudFront Function; later O2–O5 tasks will fill in the actual AWS resources and values through the Console.

**Tech Stack:** Bash-compatible shell scripts, Nginx, systemd, CloudFront Functions JavaScript, Markdown.

**Spec:** `docs/context.md` sections 5.2–5.4, 11, 14; `docs/tasks.md` O1 and definition of done.

## Global Constraints

- Use AWS Console plus scripts/sample configuration in `/infra`; complete IaC is deferred until after MVP.
- EC2 uses `t4g.small`/ARM64 and the backend is published for `linux-arm64`.
- Secrets/configuration are supplied through Parameter Store; do not commit access keys, secrets, real IDs, or ARNs.
- No NAT Gateway, ALB, SSH, or Multi-AZ resources are introduced by O1.
- Origin uses `origin.<domain>` with Let's Encrypt HTTP-01; port 80 is only for the ACME challenge and port 443 is restricted to CloudFront.
- Infrastructure work must document how to build and verify it; Console-only work without documentation is incomplete.

## Review Focus

- A fresh account can follow the runbook without guessing where placeholders must be replaced.
- ARM64 bootstrap commands do not assume x86 package paths or binaries.
- Nginx rejects direct-origin requests that lack the origin verification header.
- The CloudFront Function only rewrites static frontend paths and does not affect `/api/*`.
- Secret/identifier scans do not mistake examples for real credentials or AWS resource identifiers.

### Task 1: Console-first infrastructure scaffold

**Files:**
- Create: `infra/README.md`
- Create: `infra/bootstrap/user-data.sh.example`
- Create: `infra/nginx/exammaker.conf.example`
- Create: `infra/systemd/exammaker-api.service.example`
- Create: `infra/systemd/exammaker-worker.service.example`
- Create: `infra/cloudfront-function/index-html-rewrite.js`

**Interfaces:**
- Produces documented sample files for O2–O5 to complete later.

- [ ] Add the scaffold files with placeholder-only values and comments describing required Parameter Store/configuration inputs.
- [ ] Add the CloudFront Function rewrite behavior for frontend paths while leaving `/api/*` unchanged.
- [ ] Verify shell syntax, JavaScript syntax where available, and scan the diff for secrets/real AWS identifiers.
- [ ] Commit the scaffold as `feat: add console-first AWS infrastructure scaffold`.

### Task 2: AWS deployment and verification runbook

**Files:**
- Create: `docs/aws-runbook.md`

**Interfaces:**
- Consumes the sample files from Task 1 and the architecture decisions in `docs/context.md`.

- [ ] Document prerequisites, Console deployment order, placeholder replacement rules, and the O2→O5 handoff.
- [ ] Document verification and cleanup steps, including no-SSH/SSM access, CloudFront-to-origin checks, and cost-control checks.
- [ ] Verify every O1 acceptance criterion from `docs/tasks.md` and commit as `docs: add AWS infrastructure runbook`.
