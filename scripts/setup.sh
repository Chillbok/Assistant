#!/usr/bin/env bash
set -euo pipefail

ENV_DIR="${XDG_CONFIG_HOME:-$HOME/.config}/assistant"
ENV_FILE="$ENV_DIR/env"
REPO_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"

install -d -m 700 "$ENV_DIR"
chmod 700 "$ENV_DIR"

if [ -e "$ENV_FILE" ]; then
  echo "기존 파일 유지: $ENV_FILE"
else
  install -m 600 "$REPO_DIR/env.example" "$ENV_FILE"
  echo "생성: $ENV_FILE"
fi

chmod 600 "$ENV_FILE"

echo
echo "다음 단계: $ENV_FILE 에 값을 채운 뒤"
echo "  docker compose up -d --build"
