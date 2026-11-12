# Assistant

텔레그램 기반 개인 AI 비서. 개발 순서는 볼트의
`인공지능 비서를 구현하기 위해 무엇부터 개발할까` 문서를 따른다.

## 준비

    ./scripts/setup.sh          # ~/.config/assistant/env 생성 (700/600)
    $EDITOR ~/.config/assistant/env

`TELEGRAM_ALLOWED_USER_IDS`가 비어 있으면 아무에게도 응답하지 않는다.
봇에게 메시지를 보낸 뒤 로그의 `message from <id>` 값을 넣으면 된다.

## 실행

    docker compose up -d --build
    docker compose logs -f

## 이미지 태그 (롤백용)

    docker tag assistant:dev assistant:$(git rev-parse --short HEAD)

## 커밋 훅 활성화 (최초 1회)

    git config core.hooksPath scripts/hooks

## 중지

    docker compose stop
