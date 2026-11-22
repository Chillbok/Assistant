#!/usr/bin/env bash
# 실패 UX 검증용 대역 바이너리. stdout과 stderr를 모두 남기고 0이 아닌 코드로 끝난다.
echo "stdout-before-failure"
echo "fake failure: deliberate" >&2
exit 7
