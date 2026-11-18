1a 위험 확인 게이트는 2026-11-16 완료됐다. 이 파일은 당시 계획의 이력 보관용 사본이다.
원본: ~/.opencode/plan/assistant-phase1a.md (작성 2026-11-13)

---

# Assistant 1a 계획 초안 (v3)

작성: 2026-11-13
상태: 2차 검토 반영, 최종 확인 대기
범위: 1단계를 1a/1b로 나누고 1a만 상세 계획한다. 1b는 1a 실측 후 계획한다.

## 반영 요약

v1 -> v2
- 상태 디렉터리를 Docker named volume으로 변경 (Syncthing이 `~` 전체를 sendreceive로 동기화, 확인함)
- 호스트에 새 시크릿 파일을 만들지 않고 기존 `auth.json`에서 볼륨으로 1회 주입
- uid 1654 유지, 샌드박스만 chown
- `--auto` 추가(없으면 비대화형 편집이 자동 거부됨), `permission` 키는 `bash`
- `external_directory`, `webfetch`, `websearch` 명시적 deny
- `OPENCODE_DISABLE_PROJECT_CONFIG=1`
- `${HOME}` 사용, `setsid` + 프로세스 그룹 kill, 계속 drain
- 토큰 회전의 한계 명시 (`/proc/1/environ` 노출 확인)

v2 -> v3
- 볼륨 이름을 compose에 `name: assistant_opencode-state`로 고정
- seed 스크립트는 `--user root`로 실행
- 테스트용 fake 바이너리는 `/app/scripts/...` 절대 경로, 이미지에 COPY + 실행권한
- `ASSISTANT_OPENCODE_BIN`은 `ASSISTANT_RUN_ONCE`가 있을 때만 적용
- 게이트 조건에 실제 명령 1줄 명기
- 이미지에 `ps`, `pgrep`, `setsid`, `kill`이 있음을 확인 (Ubuntu 24.04 기반). 해당 사실 오류 제거
- 문서 스타일: 새 줄은 `->`와 하이픈을 쓴다. 기존 줄의 `→`와 em dash는 건드리지 않는다 (전역 작성 규칙: 키보드로 입력 가능한 문자만)

---

## A. 볼트 문서 수정안

대상: `인공지능 비서를 구현하기 위해 무엇부터 개발할까.md`

### A-1. 개발 순서 1번 교체

기존:

```
## 1. 위임 루프 (핵심 가설 검증)
- [ ] 메시지 수신 → `opencode run` 실행 → 결과 답장
- [ ] repo 하나, 지시 하나로 고정
- [ ] 실행 중 표시와 타임아웃
- 완료 조건: 폰에서 "이거 고쳐줘"라고 보내면 결과가 돌아온다
```

교체:

```
## 1. 위임 루프 (핵심 가설 검증)

1번 항목의 계획이 길어져 내부를 두 순서로 나눈다. 1a는 위험 확인 게이트이고, 1b가 1번의 완료 조건이다.

### 1a. 위험 확인 게이트 (수동 실행)
- [ ] 봇 토큰 회전, env 교체
- [ ] 컨테이너에서 opencode를 실행할 수 있게 정비 (자원, 마운트, git)
- [ ] opencode 설정과 인증 격리
- [ ] 지시 1건을 실행하고 출력, 종료코드, 타임아웃을 수집하는 러너
- 게이트 통과 조건: 서버에서 아래 명령 1줄로 지시를 실행하면 `/work` 파일이 실제로 바뀌고, 타임아웃 시 자식 프로세스가 남지 않는다
  `docker compose run --rm -e ASSISTANT_RUN_ONCE="Create PHASE1A_OK in the repo root containing ok" bot`

### 1b. 전화 위임 (1번 완료 조건)
- [ ] 메시지 수신 -> `opencode run` 실행 -> 결과 답장
- [ ] repo 하나, 지시 하나로 고정
- [ ] 실행 중 표시와 타임아웃
- 완료 조건: 폰에서 "이거 고쳐줘"라고 보내면 결과가 돌아온다
```

1b는 체크리스트를 더 쓰지 않는다. 계획은 1a 완료 직전에 세운다.

### A-2. 보류 목록 정리

기존:

```
- [ ] 봇 토큰 회전 (1번 위임 전, 컨테이너에 셸 권한이 생기기 전)
- [ ] 1번 위임 전 검토: compose `secrets` 전환, 자식 프로세스에 키 상속 금지
```

교체:

```
- [ ] compose `secrets` 전환 (1a는 named volume 1회 주입으로 대체)
- [ ] 러너 컨테이너 분리 (텔레그램 토큰과 opencode 인증의 완전 격리, 1b 이후)
- [ ] 이미지에 opencode 버전 고정 (1a는 호스트 바이너리 ro 마운트)
- [ ] opencode 세션 DB 증가 대응 (state GC 또는 쿼터)
- [ ] 샌드박스 리셋 절차 (git clean 등)
- [ ] 봇 재시작 시 잔존 opencode 프로세스 처리
- [ ] 7번 승인 정책에서 1a의 permission 규칙을 대체
- [ ] 진단 모드(`ASSISTANT_RUN_ONCE`, `ASSISTANT_OPENCODE_BIN`) 운영 차단 또는 제거
```

토큰 회전은 1a 체크리스트로, 자식 env 화이트리스트는 A4로 옮긴다. Syncthing 줄 끝에는 다음을 덧붙인다.

```
  - 1a는 `~` 아래에 새 시크릿과 세션 DB를 만들지 않는 것으로 회피한다
```

### A-3. 진행 기록

1a 완료 시 `# 진행 기록`에 `## 1a - 2026-11-13` 형식으로 추가한다. 기존 기록은 em dash를 쓰지만, 새 줄은 전역 작성 규칙에 따라 하이픈을 쓴다.

---

## B. 1a 계획

### A1. 토큰 회전과 env 교체

무엇을
- BotFather에서 `@martens_assistant_bot` 토큰 재발급
- `~/.config/assistant/env`의 `TELEGRAM_BOT_TOKEN` 교체
- `docker compose up -d --force-recreate`

어디에
- `~/.config/assistant/env` (600 유지)
- 서버 `~/Repositories/Assistant`

어떻게 검증
- 새 토큰으로 기동, 기존 echo 응답 동작
- 이전 토큰으로 `getMe` 호출 시 401
- `docker compose logs bot`에 토큰 문자열 없음

한계(명시)
- 같은 컨테이너 같은 uid면 `/proc/1/environ`으로 새 토큰도 읽힌다(확인함). 회전은 과거 노출 무효화가 목적이다.
- 1a의 실행은 신뢰된 지시 1건, 수동 실행을 전제로 한다. 실제 격리는 러너 분리(보류)에서 다룬다.

### A2. 실행 환경 정비

무엇을
- 샌드박스 저장소 `~/Repositories/opencode-sandbox`: `git init`, 초기 커밋. 1회 `sudo chown -R 1654:1654`
- 런타임 이미지에 `git` 설치 (`Dockerfile` runtime 스테이지)
- 테스트 스크립트 `scripts/fake-slow.sh`, `scripts/fake-env.sh`, `scripts/fake-args.sh`를 이미지 `/app/scripts/`에 COPY + 실행권한
- named volume `assistant_opencode-state` 생성 + 1회 seed
  - `scripts/seed-opencode-state.sh`
  - `docker run --rm --user root -v assistant_opencode-state:/state -v "$HOME/.local/share/opencode/auth.json:/src/auth.json:ro" debian:13-slim sh -c 'mkdir -p /state/data/opencode /state/cache /state/config /state/tmp && chown -R 1654:1654 /state && install -m 600 -o 1654 -g 1654 /src/auth.json /state/data/opencode/auth.json'`
- compose 수정
  - `user: "1654:1654"` 유지
  - `mem_limit: 1536m`, `pids_limit: 512`
  - `tmpfs: /tmp:size=512m,noexec,nosuid,nodev`
  - `volumes`: `${HOME}/Repositories/opencode-sandbox:/work`, `${HOME}/.opencode/bin/opencode:/usr/local/bin/opencode:ro`, `opencode-state:/state`
  - `volumes:` 최상위에 `opencode-state: { name: assistant_opencode-state }`
  - env 추가: `HOME=/state`, `XDG_DATA_HOME=/state/data`, `XDG_CACHE_HOME=/state/cache`, `XDG_CONFIG_HOME=/state/config`, `OPENCODE_CONFIG=/etc/assistant/opencode.json`, `OPENCODE_DISABLE_PROJECT_CONFIG=1`, `OPENCODE_DISABLE_AUTOUPDATE=1`, `TMPDIR=/tmp`
  - 기존 하드닝 유지 확인: `read_only`, `cap_drop: ALL`, `no-new-privileges`, `init: true`, `ulimits core: 0`, `DOTNET_EnableDiagnostics=0`, `dns`, `restart: on-failure:5`, json-file 10m x 3
- `opencode/opencode.json`을 이미지에 COPY

어디에
- `compose.yaml`, `Dockerfile`, `scripts/seed-opencode-state.sh`, `scripts/fake-*.sh`, `opencode/opencode.json`
- 호스트 새 디렉터리 없음

어떻게 검증
- `docker compose exec bot sh -c 'opencode --version; git --version; id; command -v setsid kill ps pgrep git'`
- 확인된 사실: 베이스는 Ubuntu 24.04.5. `setsid`, `kill`, `ps`, `pgrep`, `pkill`은 이미 있고 `git`만 없다. 게이트 실행 시 `git`이 새로 보이면 성공
- `/work` 생성/삭제 성공, `/`와 `/etc` 쓰기 실패
- `docker inspect`로 `ReadonlyRootfs`, `CapDrop`, `User`, `Memory`, `PidsLimit` 확인
- 재기동 후 `/state` 데이터 유지
- 홈 트리 오염 없음: 실행 전후 `find ~ -name 'opencode.db' | wc -l` 값 동일
- `docker volume inspect assistant_opencode-state`의 Mountpoint가 `/var/lib/docker/volumes/...`

### A3. opencode 설정과 인증 격리

무엇을
- `opencode/opencode.json` (저장소에 커밋, 시크릿 없음)
  - `"model": "opencode/deepseek-v4.1-flash"`
  - `"permission"`: `read`, `edit`, `bash`는 `allow`; `external_directory`, `webfetch`, `websearch`는 `deny`
- `OPENCODE_CONFIG=/etc/assistant/opencode.json`
- `OPENCODE_DISABLE_PROJECT_CONFIG=1`
- 실행 인자에 `--pure`, `--auto` 포함

전제(명시)
- permission 규칙은 보안 경계가 아니라 설정 위생이다. `bash`가 허용되면 `cat /proc/1/environ` 같은 우회가 가능하다. 실제 경계는 컨테이너다.

어떻게 검증
- `opencode auth list`에 opencode 계열 1개 이상
- 호스트 `~/.local/share/opencode/auth.json`의 mtime/hash 불변
- `/work` 밖 경로 read 시도가 거부되는지 (위생 확인)
- 비대화형 실행에서 편집이 실제로 승인되는지 (게이트 테스트 1)

### A4. 러너

무엇을
- 신규 `Runner.cs`
  - `RunAsync(string instruction, CancellationToken ct)` -> `RunResult(ExitCode, Stdout, Stderr, TimedOut, Elapsed)`
  - 실행: `/usr/bin/setsid /usr/local/bin/opencode run --pure --auto --dir /work <instruction>`
  - `ArgumentList` 사용, 셸 미경유
  - `WorkingDirectory=/work`, stdout/stderr 동시 drain
  - 보관 상한 1MB, 초과분은 계속 읽고 버림
  - env 초기화 후 화이트리스트: `PATH`, `HOME`, `XDG_*`, `TMPDIR`, `LANG`, `LC_ALL`, `OPENCODE_*`. `TELEGRAM_*` 제외
  - 타임아웃 기본 300초 (`ASSISTANT_JOB_TIMEOUT_SECONDS`로 조정)
  - 초과 시 `/bin/kill -TERM -<pid>` (프로세스 그룹) -> 5초 대기 -> `-KILL` -> `Kill(entireProcessTree)` 백업
  - `ct` 취소도 같은 경로
- `Program.cs` 진단 모드
  - `ASSISTANT_RUN_ONCE`가 있으면 러너만 실행하고 결과 JSON 출력 후 종료
  - `ASSISTANT_OPENCODE_BIN`은 `ASSISTANT_RUN_ONCE`가 설정된 경우에만 적용. 값은 `/app/scripts/...` 절대 경로

어디에
- `Runner.cs`, `Program.cs`

어떻게 검증 (결정적, 토큰 소모 최소)
- 실행 전 `docker compose stop bot`으로 봇을 내린다. `docker compose run`이 같은 `/state` 볼륨을 동시에 열면 SQLite 접근이 겹친다. 게이트가 끝나면 `up -d`로 복구한다.
1. 정상: `ASSISTANT_RUN_ONCE="Create PHASE1A_OK in the repo root containing ok"` -> exit 0, `/work/PHASE1A_OK` 존재, `git -C /work status`에 변경
2. 타임아웃: `ASSISTANT_OPENCODE_BIN=/app/scripts/fake-slow.sh`, `ASSISTANT_JOB_TIMEOUT_SECONDS=15` -> `TimedOut=true`, `docker top`과 `/proc` 스캔에 자식 없음
3. env: `ASSISTANT_OPENCODE_BIN=/app/scripts/fake-env.sh` -> 출력에 `TELEGRAM_` 없음
4. 인자: `ASSISTANT_OPENCODE_BIN=/app/scripts/fake-args.sh` + `; rm -rf /work` 포함 지시 -> 인자 1개로 전달, `/work` 변화 없음
5. 취소: 2번 실행 중 `ct` 취소 -> 2번과 같은 종료 경로

### A5. 1a 마감

무엇을
- 게이트 테스트 1~5를 같은 날 연속 재현
- 실측 기록: 1회 소요 시간, `docker stats` peak RSS, 타임아웃 동작, 실패 유형, `/state` 크기(`du -sh`는 볼륨 안에서)
- 재주입 절차 확인: 호스트에서 opencode 재로그인 후 `scripts/seed-opencode-state.sh` 재실행 1회
- 볼트 문서 1a 체크, 진행 기록 추가

어디에
- `인공지능 비서를 구현하기 위해 무엇부터 개발할까.md`

어떻게 검증
- 기록된 수치로 1b의 timeout, mem_limit, 볼륨 크기 가정을 정할 수 있음
- 컨테이너 재기동 후 게이트 테스트 1 재현

---

## C. 확정한 결정

1. uid: 1654 유지, 샌드박스만 1회 chown.
2. 상태: named volume `assistant_opencode-state`, 홈 트리 밖.
3. 인증: 호스트에 새 파일을 만들지 않고 볼륨에 1회 주입. 재로그인 시 reseed.
4. 바이너리: 호스트 바이너리 ro 마운트. 이미지 고정은 보류.
5. 타임아웃: 기본 300초.
6. 모델: `opencode/deepseek-v4.1-flash`.
7. `--auto` 사용, deny 규칙 명시.

## D. 1a에서 하지 않는 것

- 텔레그램 라우터, 동시성, `/ping`, `/cancel`, 진행 표시 (1b에서 계획)
- 결과 요약, 변경 파일 목록 (2번)
- repo 선택, 되묻기 (3번)
- 볼트 캡처, cron, 스레드, 승인 (4~7번)
- opencode 서버 모드, 세션 재개, 스트리밍
- 러너 컨테이너 분리, docker socket, egress 제한
- git commit/push 자동화, CI
- Syncthing, CouchDB, SSH 설정 변경

## E. 1b 계획 때 결정할 것 (지금 계획하지 않음)

- chat 락을 작업 시간 동안 잡지 않는 방법
- `/ping`, `/cancel` 필요 여부
- 실행 중 새 지시를 거부할지 대기시킬지
- 텔레그램 메시지 분할과 편집 주기
- 1a 실측값에 따른 타임아웃과 자원 재조정
