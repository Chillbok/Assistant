#!/usr/bin/env bash
# opencode 상태 디렉터리(named volume) 1회 seed.
# 호스트에 새 시크릿 파일을 만들지 않고 기존 auth.json을 볼륨으로 복사한다.
# 재로그인 후 다시 실행하면 auth.json만 갱신된다(세션 DB는 유지).
set -euo pipefail

VOLUME="${ASSISTANT_STATE_VOLUME:-assistant_opencode-state}"
AUTH="${XDG_DATA_HOME:-$HOME/.local/share}/opencode/auth.json"

if [ ! -f "$AUTH" ]; then
  echo "auth 파일 없음: $AUTH (opencode auth login 먼저)" >&2
  exit 1
fi

docker volume create "$VOLUME" >/dev/null

docker run --rm --user root \
  -v "$VOLUME:/state" \
  -v "$AUTH:/src/auth.json:ro" \
  debian:13-slim sh -c '
set -e
# mkdir 후 chown 순서를 지킨다. 볼륨 최초 상태(root:root 755)에서 순서가 바뀌면
# 하위 디렉터리가 root 소유로 남아 opencode가 EACCES로 즉사한다.
mkdir -p /state/data/opencode /state/cache /state/config /state/state /state/tmp
chown -R 1654:1654 /state
chmod 700 /state/data/opencode
install -m 600 -o 1654 -g 1654 /src/auth.json /state/data/opencode/auth.json
'

echo "seeded $VOLUME"
