#!/usr/bin/env bash
set -Eeuo pipefail

usage() {
  echo "Usage: $0 --bucket BUCKET --artifact-key KEY --checksum-key KEY --release-id ID [options]" >&2
  echo "  --region REGION       AWS region (default: ap-southeast-1)" >&2
  echo "  --app-root PATH       Frontend root (default: /var/www/exammaker-frontend)" >&2
  echo "  --public-url URL      Public HTTPS URL (default: https://app.khaothiai.xyz)" >&2
}

BUCKET=""
AWS_REGION="${AWS_REGION:-ap-southeast-1}"
ARTIFACT_KEY=""
CHECKSUM_KEY=""
RELEASE_ID=""
APP_ROOT="/var/www/exammaker-frontend"
PUBLIC_URL="https://app.khaothiai.xyz"

while (($#)); do
  case "$1" in
    --region) AWS_REGION="$2"; shift 2 ;;
    --bucket) BUCKET="$2"; shift 2 ;;
    --artifact-key) ARTIFACT_KEY="$2"; shift 2 ;;
    --checksum-key) CHECKSUM_KEY="$2"; shift 2 ;;
    --release-id) RELEASE_ID="$2"; shift 2 ;;
    --app-root) APP_ROOT="$2"; shift 2 ;;
    --public-url) PUBLIC_URL="$2"; shift 2 ;;
    -h|--help) usage; exit 0 ;;
    *) echo "Unknown argument: $1" >&2; usage; exit 2 ;;
  esac
done

if [[ -z "$BUCKET" || -z "$ARTIFACT_KEY" || -z "$CHECKSUM_KEY" || -z "$RELEASE_ID" ]]; then
  echo "ERROR: bucket, artifact-key, checksum-key, and release-id are required." >&2
  usage
  exit 2
fi

if [[ ! "$RELEASE_ID" =~ ^[A-Za-z0-9._-]+$ ]]; then
  echo "ERROR: invalid release ID: $RELEASE_ID" >&2
  exit 2
fi

RELEASES_DIR="$APP_ROOT/releases"
CURRENT_LINK="$APP_ROOT/current"
RELEASE_DIR="$RELEASES_DIR/$RELEASE_ID"
WORK_DIR="$(mktemp -d "/tmp/frontend-deploy-${RELEASE_ID}.XXXXXX")"
PREVIOUS_RELEASE=""

if [[ -L "$CURRENT_LINK" ]]; then
  PREVIOUS_RELEASE="$(readlink -f "$CURRENT_LINK" || true)"
elif [[ -e "$CURRENT_LINK" ]]; then
  echo "ERROR: $CURRENT_LINK exists and is not a symlink." >&2
  exit 1
fi

rollback() {
  local rc=$?
  if ((rc != 0)) && [[ -n "$PREVIOUS_RELEASE" && -d "$PREVIOUS_RELEASE" ]]; then
    echo "Deploy failed; restoring $PREVIOUS_RELEASE" >&2
      ln -s "$PREVIOUS_RELEASE" "$CURRENT_LINK.next"
      mv -Tf "$CURRENT_LINK.next" "$CURRENT_LINK"
    if nginx -t; then
      systemctl reload nginx || true
    fi
  elif ((rc != 0)) && [[ -L "$CURRENT_LINK" ]]; then
    rm -f "$CURRENT_LINK"
  fi
  rm -rf "$WORK_DIR"
  exit "$rc"
}
trap rollback EXIT

install -d -o nginx -g nginx -m 0755 "$RELEASES_DIR"
install -d -m 0755 "$WORK_DIR"

ARCHIVE_PATH="$WORK_DIR/frontend.tar.gz"
CHECKSUM_PATH="$WORK_DIR/frontend.tar.gz.sha256"

aws s3 cp --region "$AWS_REGION" "s3://$BUCKET/$ARTIFACT_KEY" "$ARCHIVE_PATH"
aws s3 cp --region "$AWS_REGION" "s3://$BUCKET/$CHECKSUM_KEY" "$CHECKSUM_PATH"
(
  cd "$WORK_DIR"
  sha256sum -c "$(basename "$CHECKSUM_PATH")"
)

rm -rf "$RELEASE_DIR"
install -d -o nginx -g nginx -m 0755 "$RELEASE_DIR"
tar -xzf "$ARCHIVE_PATH" -C "$RELEASE_DIR"
chown -R nginx:nginx "$RELEASE_DIR"
find "$RELEASE_DIR" -type d -exec chmod 755 {} \;
find "$RELEASE_DIR" -type f -exec chmod 644 {} \;

rm -f "$CURRENT_LINK.next"
ln -s "$RELEASE_DIR" "$CURRENT_LINK.next"
mv -Tf "$CURRENT_LINK.next" "$CURRENT_LINK"
nginx -t
systemctl reload nginx

curl --fail --silent --show-error --head "$PUBLIC_URL/" >/dev/null
ASSET_PATH="$(curl --fail --silent "$PUBLIC_URL/" | grep -oE '/_next/static/[^" ]+\.js' | head -1)"
if [[ -z "$ASSET_PATH" ]]; then
  echo "ERROR: no fingerprinted JavaScript asset found in the deployed HTML." >&2
  exit 1
fi
curl --fail --silent --show-error --head "$PUBLIC_URL$ASSET_PATH" >/dev/null

mapfile -t RELEASES < <(find "$RELEASES_DIR" -mindepth 1 -maxdepth 1 -type d -printf '%T@ %p\n' | sort -rn | cut -d' ' -f2-)
KEPT=0
ACTIVE_RELEASE="$(readlink -f "$CURRENT_LINK")"
for release in "${RELEASES[@]}"; do
  if [[ "$release" == "$ACTIVE_RELEASE" ]]; then
    KEPT=$((KEPT + 1))
  elif ((KEPT < 5)); then
    KEPT=$((KEPT + 1))
  else
    rm -rf "$release"
  fi
done

trap - EXIT
rm -rf "$WORK_DIR"
echo "Frontend release $RELEASE_ID deployed successfully."
