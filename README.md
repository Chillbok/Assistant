# Assistant

텔레그램 기반 개인 AI 비서.

## 요구 사항

- Docker Engine 24+ 및 Docker Compose v2+
- 텔레그램 계정과 봇 토큰

## 1. 봇 만들기

텔레그램에서 `@BotFather` → `/newbot` → 이름과 username 지정 → 토큰 발급.

기존 봇을 쓰려면 `/mybots` → 봇 선택 → API Token.

## 2. 설정

```bash
./scripts/setup.sh
$EDITOR ~/.config/assistant/env
```

`setup.sh`는 `~/.config/assistant/env`를 700/600으로 만들고 필수 값 누락을 검사한다.
기존 파일은 덮어쓰지 않는다.

| 키 | 설명 |
|---|---|
| `TELEGRAM_BOT_TOKEN` | 필수. BotFather 토큰 |
| `TELEGRAM_ALLOWED_USER_IDS` | 응답할 사용자 id, 쉼표 구분. 비우면 아무에게도 응답하지 않고 수신 id만 로그에 남긴다 |

파일 형식은 `env.example`의 주석을 따른다. 이 파일은 git에 커밋하지 않는다.

## 3. 실행

```bash
docker compose up -d --build
docker compose logs -f
```

정상이면 `logged in as @<봇이름>`이 나온다.

## 4. 사용자 id 찾기

`TELEGRAM_ALLOWED_USER_IDS`가 비어 있는 상태로 봇에게 메시지를 보내면 로그에 남는다.

```
[info] message from 123456789 (@username) ignored
```

## 5. 시크릿 변경

`docker compose restart`는 환경변수를 다시 읽지 않는다. 반드시 재생성한다.

```bash
$EDITOR ~/.config/assistant/env
docker compose up -d --force-recreate
```

토큰 회전: BotFather에서 `/revoke` → 위 절차로 교체.

## 6. 종료

```bash
docker compose down
```

## 보안 주의

- `docker compose config`는 시크릿을 평문으로 출력한다. 공유하거나 이슈에 붙이지 않는다.
- `docker inspect`도 환경변수를 평문으로 보여준다. docker 그룹 사용자는 값을 읽을 수 있다.
- 이 저장소를 public으로 공개할 경우, 텔레그램 봇 토큰은 GitHub push protection 대상이 아니다. `scripts/hooks/pre-commit`이 유일한 자동 방어선이다.
- `sudo docker compose`로 실행하지 않는다. `HOME`이 `/root`로 바뀌어 설정 파일을 찾지 못한다.

## 커밋 훅

```bash
git config core.hooksPath scripts/hooks
```
