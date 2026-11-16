#!/usr/bin/env bash
# 자식 env 화이트리스트 검증용 대역 바이너리.
# TELEGRAM_ 값은 절대 출력하지 않고 개수만 보고한다.
count=0
total=0
while IFS= read -r name; do
  total=$((total + 1))
  case "$name" in
    TELEGRAM_*) count=$((count + 1)) ;;
  esac
done < <(compgen -e)

echo "TELEGRAM_COUNT=$count"
echo "ENV_COUNT=$total"

for name in PATH HOME TMPDIR LANG TZ XDG_DATA_HOME XDG_CACHE_HOME XDG_CONFIG_HOME XDG_STATE_HOME OPENCODE_CONFIG OPENCODE_DISABLE_PROJECT_CONFIG OPENCODE_DISABLE_AUTOUPDATE DOTNET_ROOT; do
  value="${!name-<unset>}"
  echo "$name=$value"
done
