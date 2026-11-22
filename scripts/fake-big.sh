#!/usr/bin/env bash
# 절단 검증용 대역 바이너리. 앞 1MB는 버려지고 꼬리만 남는지 확인한다.
echo "HEAD-MARKER"
head -c 2097152 /dev/zero | tr '\0' 'a'
printf '\nTAIL-MARKER-END\n'
