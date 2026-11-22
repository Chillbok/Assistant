#!/usr/bin/env bash
# 타임아웃/잔존 프로세스 검증용 대역 바이너리.
# 손자 2개가 stdout 파이프를 물고 남으므로, 러너가 그룹 kill과 drain 유계 처리를
# 하지 않으면 영원히 끝나지 않는다.
# 런타임 이미지에 procps가 없으므로 pgid는 /proc에서 읽는다.
echo "fake-slow pgid=$(cut -d' ' -f5 /proc/$$/stat)"
sleep 600 &
sleep 600 &
sleep 600
