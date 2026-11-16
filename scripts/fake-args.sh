#!/usr/bin/env bash
# 인자 전달 검증용 대역 바이너리. 셸을 거치지 않고 argv 하나로 전달되는지 확인한다.
echo "argc=$#"
i=0
for arg in "$@"; do
  i=$((i + 1))
  echo "argv[$i]=$arg"
done
echo "work_exists=$([ -d /work ] && echo yes || echo no)"
