# Assistant 1b 계획: 전화 위임

작성: 2026-11-18
상태: 계획 확정, 구현 전
범위: 메시지 수신 -> `opencode run` 실행 -> 결과 답장. repo 하나, 지시 하나로 고정. 실행 중 표시와 타임아웃

완료 조건: 폰에서 지시를 보내면 결과가 돌아온다

## 배경

1a에서 실행 경로를 완성했다 (커밋 `f37b522`).

- `Runner.RunAsync(binary, instruction, ct)`가 `setsid -w`로 실행하고, 타임아웃 시 프로세스 그룹을 정리하며, stdout/stderr를 각 1MB까지 수집한다
- 타임아웃 기본값 300초 (`ASSISTANT_JOB_TIMEOUT_SECONDS`)
- 모델 `opencode/deepseek-v4.1-flash`, `/work` 샌드박스 고정, uid 1654, `mem_limit 2g`
- 봇은 `BotHost`의 GetUpdates 롱폴 1개, 채널(1000), 워커 4개, chat별 `SemaphoreSlim` 구조다
- `Router.HandleAsync`는 아직 echo만 한다. 1b에서 이 지점을 교체한다

폴링 루프는 워커와 분리되어 있어 긴 작업이 수신을 막지 않는다. 설계 과제는 수신이 아니라 작업 조정, 상태 표시, 결과 전달이다.

## 확정 결정

### 1. 실행 조정: 전역 슬롯 하나, 워커에서 await

- 인가와 텍스트 필터를 통과한 뒤 전역 `SemaphoreSlim(1,1)`에 `Wait(0)`으로 진입한다
- 실패하면 즉시 "이미 작업이 실행 중" 답장 후 버린다. 대기열에 쌓지 않는다
- 작업은 워커에서 await한다. fire-and-forget 금지
- 근거: 작업 분리 시 종료 시점에 `Task.WhenAll(workers)`가 끝나고 프로세스가 종료되어 진행 작업의 정리와 로그를 보장할 수 없다. 전역 슬롯이 1이면 chat별 사전은 동작을 바꾸지 못한다. opencode 두 개가 같은 `/state` SQLite를 동시에 여는 안전성은 검증되지 않았고 `mem_limit 2g`도 여유가 크지 않다

### 2. 표시: 시작 답장 1건, 완료 시 1회 편집, 결과는 새 메시지

- 시작 메시지는 원문에 대한 reply로 보낸다. 원문이 삭제되어도 실패하지 않도록 `ReplyParameters.AllowSendingWithoutReply = true`
- 완료 시 시작 메시지를 `[완료]`, `[실패]`, `[시간 초과]`, `[중단됨]`으로 한 번만 편집한다
- 결과 본문은 새 메시지로 보낸다
- 근거: 편집은 텔레그램 알림을 만들지 않는다. 결과를 새 메시지로 보내야 폰을 내려놓은 사용자가 완료를 인지한다. 작업당 API 호출 3회는 단일 사용자 트래픽에서 문제가 없다
- 편집 실패(400 not modified, message not found)는 새 메시지 전송으로 대체하고 로그만 남긴다

### 3. 결과 본문

- 헤더: `[완료] 12.3초, exit 0`
- 본문: 표준출력 꼬리 마지막 3000자
- 푸터: 생략 시 `(앞부분 생략, 전체 N자 중 마지막 3000자)`, 1MB 초과 시 `(출력 1MB 초과로 앞부분 유실)`
- 실패 시: stderr 꼬리 1200자, stdout 꼬리 1800자
- 출력이 비면 `(출력 없음, exit 0)` 같은 대체 문구. 빈 문자열은 전송하지 않는다 (400)
- 총 4000자 이하. UTF-16 코드 단위로 계산하고 서로게이트 경계를 보정한다. 마지막 개행 우선
- `parse_mode` 미사용, `LinkPreviewOptions.IsDisabled = true`
- ANSI 제거는 하지 않는다. 비TTY에서 opencode의 stdout은 순수 텍스트이고, ANSI는 stderr 경로에만 있다. CR과 3줄 이상 연속 공백만 정리한다
- stderr 전체는 텔레그램에 보내지 않는다. 절대 경로와 도구 본문이 포함된다
- 전체 로그는 저장하지 않는다. 컨테이너 로그에는 kind, exit, elapsed, bytes 요약만 남긴다
- `--format json` 전환은 2번 후보로 미룬다

### 4. 타임아웃과 취소 문구

- 타임아웃: `[시간 초과] 302.1초에 강제 종료 (제한 300초)` + `이미 쓰인 파일은 남고, 쓰는 중이던 파일은 일부만 반영됐을 수 있습니다` + stdout 꼬리(없으면 출력 없음)
- opencode는 완료된 텍스트 파트만 stdout에 쓰므로 타임아웃 시 출력이 비어 있을 가능성이 높다. 부분 답변을 약속하는 문구를 쓰지 않는다
- 작업 중 봇 종료: `[중단됨] 봇이 재시작되어 작업이 중단되었습니다`를 best-effort로 보낸다
- 결과와 상태 전송은 `CancellationToken.None` + 10초 상한으로 보낸다. Runner가 취소되면 ct가 이미 취소 상태라 ct를 쓰면 답장이 사라지고 "실행 중" 표시만 남는다

### 5. 고정 범위

- repo: `/work` 샌드박스 1개. 메시지에서 repo나 경로를 해석하지 않는다
- 지시: 메시지 텍스트 전체를 그대로 `Runner`의 인자 1개로 전달한다. 대화 맥락과 세션 재개는 없다
- 따라서 "이거 고쳐줘"만으로는 대상이 없다. 1b 완료 조건은 자기완결적 지시로 검증하고, 이 한계를 기록한다 (3번에서 해결)
- 공백만 있는 텍스트는 무시한다. `/`로 시작하는 메시지는 별도 명령 처리 없이 지시로 취급한다 (`/ping`, `/cancel`은 범위 밖)

### 6. 진단 훅

- `ASSISTANT_DIAGNOSTIC=1`과 `ASSISTANT_OPENCODE_BIN`이 동시에 설정된 경우에만 봇 경로에서 바이너리를 교체한다
- 적용 시 기동 로그에 `[warn] diagnostic binary in use`
- 근거: 타임아웃 UX를 실제 텔레그램 경로로 검증하려면 느린 바이너리가 필요하다. 두 스위치로 오설정 사고를 막는다

## 필수 범위 조정 (1a 대비)

1. 봇 모드 SIGTERM 등록 (`Program.cs`). 현재 SIGINT만 등록되어 있어 `docker compose stop`과 `up -d --build`에서 정리 경로가 돌지 않는다
2. `compose.yaml`에 `stop_grace_period: 30s`. 1a 취소 실측 7.55초에 전송 시간을 더한다
3. `Runner.Capture`에 64KB 꼬리 ring buffer 추가, `RunResult`에 `StdoutTail`, `StderrTail` 노출. 현재는 앞 1MB만 보존하므로 꼬리가 없다
4. 빈 출력, 4096 초과, 편집 400 폴백, `AllowSendingWithoutReply` 처리

## 변경 파일

| 파일 | 내용 |
|---|---|
| 신규 `Delegation.cs` | 실행 조정, 시작/완료/결과 전송, 예외 처리 |
| 신규 `MessageFormat.cs` | 본문 조립, 절단, 푸터. 순수 함수 |
| `BotHost.cs` | 워커에서 gate 제거, 전역 슬롯과 위임 호출 |
| `Router.cs` | 인가/필터 유지, echo 제거 |
| `Program.cs` | 봇 모드 SIGTERM 등록 |
| `Runner.cs` | 꼬리 캡처, 바이너리 선택 공용화 |
| `compose.yaml` | `stop_grace_period: 30s` |

## 검증 절차

전제: 구현 후 실행. `docker compose config` 전체 출력은 금지 (토큰 평문).

1. 러너 회귀 (무료): 사전 정리 `docker compose run --rm -T --entrypoint sh bot -c 'rm -f /work/PHASE1B_OK'`, 이어서 `ASSISTANT_RUN_ONCE` + `fake-slow` + 20초로 타임아웃과 잔존 프로세스 0 확인. `--name`을 주고 두 번째 ssh에서 `docker exec ... ps -eo pid,pgid,stat,args`로 관찰
2. 폰 정상 위임: `Create PHASE1B_OK in the repo root containing ok` 전송. 시작 답장, 완료 편집, 결과 3건 순서 확인. 파일은 `docker exec assistant-bot-1 ls -l /work/PHASE1B_OK`로 확인 (호스트 `git status`는 dubious ownership으로 실패)
3. busy 즉시성: 실행 중 두 번째 메시지가 2초 이내에 거절되는지. 완료 후 세 번째 메시지가 정상 실행되는지 (플래그 해제)
4. 타임아웃 UX: `docker compose stop bot` 후 `docker compose run --rm -T --name assistant-timeout-probe -e ASSISTANT_DIAGNOSTIC=1 -e ASSISTANT_OPENCODE_BIN=/app/scripts/fake-slow.sh -e ASSISTANT_JOB_TIMEOUT_SECONDS=20 bot`. 폰에서 지시, `[시간 초과]` 확인, Ctrl-C 후 `docker ps -a --filter label=com.docker.compose.oneoff=True`로 잔존 0 확인
5. 실패 UX와 경계: fake non-zero로 `[실패]`, `true` 바이너리로 빈 출력 문구, 2MiB 출력으로 잘림 표시
6. 복구: `docker compose up -d --force-recreate bot`, `logged in as`, 409/401 없음(`docker logs --since 3m | grep -E 'logged in|409|401'`), `/work/.git/index.lock` 없음
7. 실측 기록: 1회 소요 시간, peak RSS, `/state` 증가, 타임아웃 답장까지 걸린 시간

## 반증된 가정 (계획 검토에서 확정)

| 가정 | 실제 |
|---|---|
| 봇 종료 시 Runner 그룹 kill이 돌아간다 | 봇 모드는 SIGINT만 등록. SIGTERM이면 dotnet이 즉시 종료되고 opencode는 SIGKILL된다 |
| stdout에서 마지막 3000자를 쓸 수 있다 | `Capture`는 앞 1MB만 보존한다. 꼬리가 없다 |
| 실패 시 stderr에 원인이 남는다 | opencode는 도구 로그를 stderr로 쓰지만 `--print-logs`는 기본 off다. 실패 1건으로 확인 필요 |
| stdout의 ANSI를 걷어내야 한다 | 비TTY stdout은 순수 텍스트다. ANSI는 stderr 경로에만 있다 |
| 라이브러리가 긴 본문을 나눠 보낸다 | Telegram.Bot 22.10.4에는 분할/검증이 없다. 4096 초과는 400이고 현재 코드는 침묵한다 |
| 작업을 분리(fire-and-forget)해도 종료 정리가 된다 | 워커 `WhenAll` 후 프로세스가 끝나 분리 작업은 유실된다 |

추가 확인:
- 타임아웃 시 stdout이 비어 있을 가능성이 높다 (완료된 텍스트 파트만 출력)
- `SendMessage` 빈 문자열은 400 `message text is empty`
- `EditMessageText`는 400 `message is not modified`, `message to edit not found`를 반환할 수 있다
- 원문 답장은 원문 삭제 시 400. `AllowSendingWithoutReply` 필요
- 숨은 폴러 없음 (`assistant-bot-1` 1개, one-off 0, systemd/cron 없음). 409 위험은 우리가 만드는 두 번째 인스턴스뿐

## 위험

- 재시작 시 진행 작업 유실과 "실행 중" 표시 잔존 (job 상태 영속화는 보류)
- Telegram 응답 유실 시 같은 지시 중복 실행 가능 (update id 중복 방지는 보류)
- SIGKILL된 opencode가 `/work/.git/index.lock`을 남길 수 있음 (검증에서 확인, 자동 정리는 범위 밖)
- 300초 단일 작업이 전체 위임을 막음 (의도된 트레이드오프)
- `/state` 증가 (게이트 1회에 약 +150M). 1b에서 실측만 한다

## 소요

구현 3~4시간, 검증 1.5~2시간. 합계 약 0.75일

## 하지 않는 것

요약과 변경 파일 목록(2번), repo 선택과 되묻기(3번), 캡처, cron, 스레드, 승인, `/ping`, `/cancel`, SSH/CouchDB/Syncthing/토큰 회전, 러너 컨테이너 분리, `--format json` 전환

## 참조

- 로드맵: `docs/roadmap.md`
- 선행 단계: `docs/plans/phase1a.md`, 커밋 `f37b522`
