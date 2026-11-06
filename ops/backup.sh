#!/bin/sh
set -eu
: "${S3_ENDPOINT:?Set S3_ENDPOINT}" "${S3_BUCKET:?Set S3_BUCKET}"
: "${AWS_ACCESS_KEY_ID:?Set AWS_ACCESS_KEY_ID}" "${AWS_SECRET_ACCESS_KEY:?Set AWS_SECRET_ACCESS_KEY}"
prefix=darim/daily
stamp=$(date -u +%Y%m%dT%H%M%SZ)
file="/tmp/wishlist-$stamp.dump"
listing="/tmp/wishlist-list-$stamp.txt"
trap 'test ! -f "$file" || unlink "$file"; test ! -f "$listing" || unlink "$listing"' EXIT
pg_dump --format=custom --no-owner --no-acl --file="$file"
aws --endpoint-url "$S3_ENDPOINT" s3 cp "$file" "s3://$S3_BUCKET/$prefix/$stamp.dump" --only-show-errors
# Retention applies only to this application's fixed prefix, after a successful upload.
cutoff=$(date -u -d '14 days ago' +%Y-%m-%dT%H:%M:%SZ)
aws --endpoint-url "$S3_ENDPOINT" s3api list-objects-v2 --bucket "$S3_BUCKET" --prefix "$prefix/" --query "Contents[?LastModified<'$cutoff'].Key" --output text > "$listing"
tr '\t' '\n' < "$listing" | while IFS= read -r key; do
  case "$key" in darim/daily/*.dump) aws --endpoint-url "$S3_ENDPOINT" s3api delete-object --bucket "$S3_BUCKET" --key "$key" >/dev/null ;; esac
done
printf 'Backup uploaded: %s\n' "$stamp"
