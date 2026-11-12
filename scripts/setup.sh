#!/usr/bin/env bash
set -euo pipefail

ENV_DIR="${XDG_CONFIG_HOME:-$HOME/.config}/assistant"
ENV_FILE="$ENV_DIR/env"
REPO_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"

command -v docker >/dev/null 2>&1 || { echo "docker가 필요합니다." >&2; exit 1; }
docker compose version >/dev/null 2>&1 || { echo "docker compose가 필요합니다." >&2; exit 1; }

install -d -m 700 "$ENV_DIR"
chmod 700 "$ENV_DIR"

if [ -e "$ENV_FILE" ]; then
  echo "기존 파일 유지: $ENV_FILE"
else
  install -m 600 "$REPO_DIR/env.example" "$ENV_FILE"
  echo "생성: $ENV_FILE"
fi

chmod 600 "$ENV_FILE"

fail=0

mode=$(stat -c '%a' "$ENV_FILE")
if [ "$mode" != "600" ]; then
  echo "권한 오류: $ENV_FILE 이 $mode 입니다. 600이어야 합니다." >&2
  fail=1
fi

token=$(grep -E '^TELEGRAM_BOT_TOKEN=' "$ENV_FILE" | cut -d= -f2- | tr -d '[:space:]' | tr -d '"' | tr -d "'" || true)
if [ -z "$token" ]; then
  echo "필수 값 누락: TELEGRAM_BOT_TOKEN 이 비어 있습니다." >&2
  fail=1
fi

allowed=$(grep -E '^TELEGRAM_ALLOWED_USER_IDS=' "$ENV_FILE" | cut -d= -f2- || true)
if [ -z "$allowed" ]; then
  echo "참고: TELEGRAM_ALLOWED_USER_IDS 가 비어 있습니다. 봇이 아무에게도 응답하지 않습니다."
fi

if [ "$fail" -ne 0 ]; then
  echo >&2
  echo "설정을 확인하세요: $ENV_FILE" >&2
  exit 1
fi

echo "설정 파일 정상: $ENV_FILE (600)"
echo
echo "다음 단계:"
echo "  cd $REPO_DIR && docker compose up -d --build"
echo "  docker compose logs -f"
