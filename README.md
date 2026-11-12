# jarvis

텔레그램 봇. 개발 순서는 볼트의 `인공지능 비서를 구현하기 위해 무엇부터 개발할까` 문서를 따른다.

## 실행

    cp .env.example .env   # 토큰과 허용 사용자 ID 입력
    chmod 600 .env
    docker compose up -d --build
    docker compose logs -f

## 중지

    docker compose down
