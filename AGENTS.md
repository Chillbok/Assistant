# Assistant 저장소 작업 지침

텔레그램 기반 개인 AI 비서. 설계 문서는 `docs/` 아래에 있다.

## 문서

- `docs/roadmap.md`: 전체 개발 순서, 보류 목록, 진행 기록. 원본은 Obsidian 볼트이고 단계 종료 시 복사한다
- `docs/plans/phase1a.md`: 1a 위험 확인 게이트 계획. 완료된 이력
- `docs/plans/phase1b.md`: 1b 전화 위임 확정 계획

## 다른 컴퓨터에서 이어서 작업하기

1. 작업 시작 전에 `git pull`, 작업이 끝나면 커밋 후 `git push`. 서버 저장소와 클론이 갈라지지 않게 한다
2. 한 번에 한 컴퓨터에서만 편집한다. 서버 저장소와 로컬 클론을 동시에 고치지 않는다
3. 코드, 문서, 설정 템플릿은 어디서나 편집할 수 있다. 실제 실행 검증은 서버에서 한다
4. 저장소에는 코드와 설정 템플릿만 둔다. 실행 상태, 자격 증명, 세션 데이터는 저장소 밖에 둔다

## 실행과 배포

- 실행 위치: `my-server`, `~/Repositories/Assistant`
- 배포: 서버에서 `git pull` 후 `docker compose up -d --build`
- 정상 로그: `logged in as @...`
- 설정 변경은 `docker compose restart`로 반영되지 않는다. `docker compose up -d --force-recreate`를 쓴다
- 호스트 계정은 `/work`를 읽기만 할 수 있다. 파일 검증은 컨테이너 안에서 한다
- 같은 봇 토큰으로 두 인스턴스를 동시에 폴링하지 않는다. 409가 나면 중복 폴러를 먼저 찾는다
- `docker compose run`으로 만든 컨테이너는 `--rm`으로도 남을 수 있다. 작업 후 one-off 컨테이너를 확인한다

## 민감 정보 (반드시 지킬 것)

- 금지: 토큰, API 키, 비밀번호, 인증 파일, 설정 덤프를 원격 저장소에 추가하는 일. 파일 내용뿐 아니라 브랜치명, 태그, 커밋 메시지, PR 본문, 이슈도 포함한다
- 시크릿은 `~/.config/assistant/env` (700/600, 저장소 밖)에만 둔다
- `env.example`에는 키 이름만 쓰고 값은 항상 비운다
- `docker compose config`, `docker inspect`, `/proc/<pid>/environ` 출력은 시크릿 평문을 포함한다. 파일로 저장하거나 채팅, 이슈, 로그에 붙이지 않는다
- 커밋 전에 `git diff --cached`로 확인한다
- 커밋 훅 `scripts/hooks/pre-commit`을 우회하지 않는다. `--no-verify` 금지. 새 클론에서는 `git config core.hooksPath scripts/hooks`가 설정됐는지 확인한다
- 새 기기에서는 env 파일을 새로 만든다. 다른 기기에서 복사하지 않는다
- 시크릿을 실수로 push했으면 즉시 해당 토큰을 회전하고 이력 정리 여부를 결정한다. 임의 force push는 금지
- 원격은 GitHub `Chillbok/Assistant`. push 전에 저장소 공개 범위를 확인한다
- 볼트 노트, 계획 문서에도 서버 경로와 환경변수 이름까지만 쓴다. 값은 쓰지 않는다

## 검증

- 변경 후 빌드와 실제 텔레그램 응답까지 확인한다
- 러너, 타임아웃, 취소를 건드렸으면 자식 프로세스 잔존 여부까지 확인한다
- 검증 명령과 결과는 해당 단계의 계획 문서에 남긴다
