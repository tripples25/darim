#!/bin/sh
set -eu

kind=${1:?}
component='API'
link=${CI_PIPELINE_URL:?}
sha=${CI_COMMIT_SHA:?}
case "$kind" in
  failure)
    message=$(printf '❌ %s · пайплайн #%s упал\n%s · %.12s' "$component" "$CI_PIPELINE_NUMBER" "$CI_COMMIT_BRANCH" "$sha")
    ;;
  release)
    previous=$(cat .previous-production 2>/dev/null || true)
    [ "$previous" != "$sha" ] || exit 0
    if [ -n "$previous" ] && git cat-file -e "$previous^{commit}" 2>/dev/null; then
      notes=$(git log --no-merges -3 --format='• %s' "$previous..$sha")
      link="https://github.com/$CI_REPO/compare/$previous...$sha"
    else
      notes=$(git log -1 --format='• %s' "$sha")
      link="https://github.com/$CI_REPO/commit/$sha"
    fi
    [ -n "$notes" ] || notes=$(git log -1 --format='• %s' "$sha")
    message=$(printf '🚀 %s · %.12s\n%s' "$component" "$sha" "$notes")
    ;;
  *) exit 2 ;;
esac

button='Открыть запуск'
[ "$kind" != release ] || button='Все изменения'
: "${TELEGRAM_BOT_TOKEN:?Configure telegram_bot_token}"
: "${TELEGRAM_LICKREAL_ID:?Configure telegram_lickreal_id}"
: "${TELEGRAM_SHARPHURT_ID:?Configure telegram_sharphurt_id}"

# Telegram private chat IDs are positive integers. Reject groups and channels.
for chat_id in "$TELEGRAM_LICKREAL_ID" "$TELEGRAM_SHARPHURT_ID"; do
  case "$chat_id" in
    0*|*[!0-9]*) echo 'Expected a positive Telegram user ID' >&2; exit 2 ;;
  esac
done
if [ "$TELEGRAM_LICKREAL_ID" = "$TELEGRAM_SHARPHURT_ID" ]; then
  echo 'Expected two distinct Telegram user IDs' >&2
  exit 2
fi

delivery_status=0
for chat_id in "$TELEGRAM_LICKREAL_ID" "$TELEGRAM_SHARPHURT_ID"; do
  payload=$(jq -n --arg chat "$chat_id" --arg text "$message" \
    --arg url "$link" --arg label "$button" \
    '{chat_id:$chat,text:$text,reply_markup:{inline_keyboard:[[{text:$label,url:$url}]]}}')
  # Pass the token through stdin to keep it out of error messages.
  if ! printf 'url = "https://api.telegram.org/bot%s/sendMessage"\n' "$TELEGRAM_BOT_TOKEN" |
    curl --config - --fail --silent --max-time 20 \
      --header 'Content-Type: application/json' --data-binary "$payload" --output /dev/null; then
    echo 'Telegram delivery failed for one recipient' >&2
    delivery_status=1
  fi
done
exit "$delivery_status"
