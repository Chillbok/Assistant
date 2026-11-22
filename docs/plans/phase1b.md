# Assistant 1b 계획: 전화 위임

작성: 2026-11-18
갱신: 2026-11-23 (서브에이전트 5축 검증 반영)
상태: 구현 완료, 서버 검증 대기
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

- 인가와 텍스트 필터를 통과한 뒤 `Interlocked.CompareExchange(ref busy, 1, 0)`로 진입한다
- 실패하면 즉시 "이미 작업이 실행 중" 답장 후 버린다. 대기열에 쌓지 않는다
- 작업은 워커에서 await한다. fire-and-forget 금지
- 슬롯 반납은 상태 편집과 결과 전송이 모두 끝난 뒤 `finally`에서 `Interlocked.Exchange(ref busy, 0)`으로 한다
- 근거: 작업 분리 시 종료 시점에 `Task.WhenAll(workers)`가 끝나고 프로세스가 종료되어 진행 작업의 정리와 로그를 보장할 수 없다. 전역 슬롯이 1이면 chat별 사전은 동작을 바꾸지 못한다. opencode 두 개가 같은 `/state` SQLite를 동시에 여는 안전성은 검증되지 않았고 `mem_limit 2g`도 여유가 크지 않다
- `SemaphoreSlim.Wait(0)` 대신 CAS를 쓰는 이유: `Release` 중복 호출로 인한 `SemaphoreFullException` 가능성을 구조적으로 없애고, busy 여부를 락 없이 읽을 수 있다
- 반납을 전송 뒤로 미루는 이유: 결과 전송 중에 슬롯이 풀리면 다음 메시지의 거절 답장이 결과보다 먼저 도착해 순서가 뒤집힌다

### 2. 표시: 시작 답장 1건, 완료 시 1회 편집, 결과는 새 메시지

- 시작 메시지는 원문에 대한 reply로 보낸다. 원문이 삭제되어도 실패하지 않도록 `ReplyParameters.AllowSendingWithoutReply = true`
- 시작 메시지 전송이 실패하면 로그만 남기고 작업은 계속한다. 결과는 전송한다
- 완료 시 시작 메시지를 `[완료]`, `[실패]`, `[시간 초과]`, `[중단됨]`으로 한 번만 편집한다
- 결과 본문은 새 메시지로 보낸다
- busy 거절은 원문 reply 없이 새 메시지로 보낸다. 원문 삭제와 무관하고, 실패 처리 분기가 줄어든다
- 근거: 편집은 텔레그램 알림을 만들지 않는다. 결과를 새 메시지로 보내야 폰을 내려놓은 사용자가 완료를 인지한다. 작업당 API 호출 3회(시작, 편집, 결과), 채팅창 표시 2건은 단일 사용자 트래픽에서 문제가 없다
- 편집 실패는 `ErrorCode == 400`과 문구 포함으로 판정한다. 접두사 `Bad Request: `를 포함하므로 완전 일치 비교는 쓰지 않는다
  - `message is not modified`: 무시한다. 대체 전송하면 같은 문구가 중복된다
  - `message to edit not found` 등 그 외: 상태 문구를 새 메시지로 보낸다
- 모든 상태/결과 전송은 `CancellationToken.None`이 아니라 **하나의 `CancellationTokenSource(8초)`를 공유**한다. 전송 2건이 8초를 나눠 쓴다
  - `CancellationToken.None`은 상한이 없다. `TelegramBotClient.Timeout`(HttpClient, 기본 100초)까지 매달릴 수 있다
  - `OperationCanceledException`은 `ApiRequestException`과 별도로 잡는다. 429 자동 재시도도 이 예산 안에서 취소된다

### 3. 결과 본문

- 조립 순서: 정규화 -> 빈 판정 -> 고정 비용 예약 -> 본문 절단 -> 서로게이트 보정 -> 실제 표시 수 확정 -> 푸터 -> 조립
- 고정 비용(헤더, 라벨, 푸터, 개행)을 먼저 빼고 본문 예산을 정한다. 조립 결과 전체를 4000으로 자르는 방식은 금지한다. 푸터가 잘려 절단 사실이 사라지기 때문이다
- 헤더: `[완료] 12.3초, exit 0`. exit가 음수면 exit 표기를 생략한다
- 성공 본문: stdout 꼬리 마지막 3000자
- 실패 본문: `[stderr, 마지막 1200자]` + stderr 꼬리 1200자, `[stdout, 마지막 1800자]` + stdout 꼬리 1800자. 라벨의 숫자는 실제 표시된 수(코드포인트)다
- 타임아웃: `[시간 초과] 302.1초에 강제 종료 (제한 300초)` + `이미 쓰인 파일은 남고, 쓰는 중이던 파일은 일부만 반영됐을 수 있습니다` + stdout 꼬리(없으면 출력 없음)
- 정규화: `\r\n` -> `\n`, 남은 `\r` -> `\n`, 3줄 이상 연속 개행 -> 2줄, 꼬리 끝의 U+FFFD 제거(최대 3개), 후행 공백 정리
  - `\r`을 삭제하지 않고 개행으로 바꾸는 이유: 진행률 덮어쓰기 출력이 한 줄로 뭉개지지 않게 한다
- ANSI: stdout은 제거하지 않는다. 비TTY에서 순수 텍스트다. 실패 본문에 들어가는 stderr만 CSI/OSC를 제거한다
- 빈 판정은 정규화와 trim 이후 한 곳에서만 한다. 정규화 후 빈 문자열은 전송하지 않는다 (400 `message text is empty`)
- 표시표 (정규화 후)

| stdout | stderr | exit | 표시 |
|---|---|---|---|
| 빈 | 빈 | 0 | `(출력 없음, exit 0)` |
| 빈 | 있음 | 0 | stderr 라벨 + 꼬리 |
| 있음 | 있음 | 0 | stdout만. stderr는 로그에 크기만 |
| 빈 | 빈 | 0 아님 | `(출력 없음, exit N)` |
| 빈 | 있음 | 0 아님 | stderr 라벨 + 꼬리 |
| 있음 | 빈 | 0 아님 | stdout 라벨 + 꼬리 |
| 빈 | 빈 | 타임아웃 | 주의 문구 + `(출력 없음)` |
| 있음 | 빈 | 타임아웃 | 주의 문구 + stdout 꼬리 |

- 푸터는 스트림별로 상호 배타다. 두 문구를 동시에 쓰지 않는다
  - `Truncated`(1MB 초과): `(출력 1MB 초과, 마지막 k자만 표시)`. 전체 문자 수는 알 수 없으므로 N을 쓰지 않는다
  - 그 외 절단: `(앞부분 생략, 전체 N자 중 마지막 k자)`. N은 정규화된 전체 문자열의 코드포인트 수
  - k는 실제로 포함된 코드포인트 수다. 서로게이트 보정과 trim 후 값이다
- 길이: 헤더/라벨/푸터 포함 총 4000 UTF-16 코드 단위 이하. 예산은 UTF-16, 사용자에게 보이는 수는 코드포인트로 센다. 최악 조립은 약 3180자다
- 서로게이트 보정: 앞을 자를 때 선두의 low surrogate를 건너뛰고, 뒤를 자를 때 후미의 high surrogate를 제거한다. 연속 이상 입력을 위해 while로 처리한다
- `parse_mode` 미사용, `LinkPreviewOptions`는 인스턴스로 만들어 `IsDisabled = true`를 준다 (정적 멤버 아님)
- stderr 전체는 텔레그램에 보내지 않는다. 절대 경로와 도구 본문이 포함된다
- 전체 로그는 저장하지 않는다. 컨테이너 로그에는 kind, exit, elapsed, bytes, procs, zombies와 **stderr 첫 줄 160자(redact 경유)**만 남긴다
  - 첫 줄은 실패 원인을 구분하는 최소 정보(모델 오류, 인증 만료, 권한 거부)를 담는다
  - 여러 줄, 전체 stderr, 절대 경로 목록은 금지한다
- `--format json` 전환은 2번 후보로 미룬다

### 4. 타임아웃과 취소 문구

- 타임아웃: `[시간 초과] 302.1초에 강제 종료 (제한 300초)` + `이미 쓰인 파일은 남고, 쓰는 중이던 파일은 일부만 반영됐을 수 있습니다` + stdout 꼬리(없으면 출력 없음)
- 타임아웃 경과는 제한 시간보다 길다. kill 유예 2.5초와 drain 2초가 붙어 상한은 약 304.5초다
- opencode는 완료된 텍스트 파트만 stdout에 쓰므로 타임아웃 시 출력이 비어 있을 가능성이 높다. 부분 답변을 약속하는 문구를 쓰지 않는다
- 작업 중 봇 종료: `[중단됨] 봇이 재시작되어 작업이 중단되었습니다`를 **새 메시지로 먼저** 보내고, 시작 메시지 편집은 best-effort로 시도한다. 중단에는 결과 본문을 붙이지 않는다
- 상태 판정은 `RunResult.Kind`만 쓴다. `ct.IsCancellationRequested`를 상태 판정에 쓰지 않는다. 종료 시점에는 항상 true라 타임아웃이 중단됨으로 오분류된다
- `Cancelled` 외 `ExternalSignal`, `RunnerError`는 `[실패]`로 표시한다
- 종료 시간 예산: Runner 최악 26.5초(kill 3회 x 7.5초 + drain 2회 x 2초) + 알림 8초 = 34.5초. `stop_grace_period: 45s` 안에 들어온다
- `Runner.RunAsync`가 예외를 던질 수 있으므로 Delegation에서 감싸 합성 `RunResult`로 변환한다
  - `OperationCanceledException` + ct 취소: Cancelled 합성 결과
  - 그 외 예외: RunnerError 합성 결과

### 5. 고정 범위

- repo: `/work` 샌드박스 1개. 메시지에서 repo나 경로를 해석하지 않는다
- 지시: 메시지 텍스트 전체를 그대로 `Runner`의 인자 1개로 전달한다. 대화 맥락과 세션 재개는 없다
- 따라서 "이거 고쳐줘"만으로는 대상이 없다. 1b 완료 조건은 자기완결적 지시로 검증하고, 이 한계를 기록한다 (3번에서 해결)
- 공백만 있는 텍스트는 무시한다. `/`로 시작하는 메시지는 별도 명령 처리 없이 지시로 취급한다 (`/ping`, `/cancel`은 범위 밖)

### 6. 진단 훅

- `ASSISTANT_DIAGNOSTIC=1`과 `ASSISTANT_OPENCODE_BIN`이 동시에 설정된 경우에만 봇 경로에서 바이너리를 교체한다
- 적용 시 기동 로그에 `[warn] diagnostic binary in use`
- `ASSISTANT_RUN_ONCE`는 1a 계약대로 `ASSISTANT_OPENCODE_BIN`을 무조건 적용한다. 검증 절차가 이 동작에 의존한다
- 근거: 타임아웃 UX를 실제 텔레그램 경로로 검증하려면 느린 바이너리가 필요하다. 두 스위치로 오설정 사고를 막는다

## 필수 범위 조정 (1a 대비)

1. 봇 모드 SIGTERM 등록 (`Program.cs`). 현재 SIGINT만 등록되어 있어 `docker compose stop`과 `up -d --build`에서 정리 경로가 돌지 않는다
2. `compose.yaml`에 `stop_grace_period: 45s`. 최악 34.5초를 담는다. `docker stop`에 `-t`를 주지 않으면 이 값이 쓰이고, `docker rm -f`는 즉시 SIGKILL이다
3. `Runner.Capture`에 64KB 꼬리 ring buffer 추가, `RunResult`에 `StdoutTail`, `StderrTail` 노출
   - head 1MB는 그대로 유지한다. `Classify`가 stderr head의 `setsid: failed to execute`를 읽는다
   - 꼬리 디코드 시 선두의 UTF-8 연속 바이트를 건너뛰고, 끝의 U+FFFD를 제거한다
4. 빈 출력, 4096 초과, 편집 400 폴백, `AllowSendingWithoutReply` 처리
5. 취소 직전 정상 종료 경합 처리: `WaitForExitAsync` 취소 예외에서 `process.HasExited`를 먼저 확인한다
6. 봇 경로에 opencode 설정 존재 가드 추가. 1a 교훈: 설정이 없으면 조용히 기본 설정으로 실행된다
7. `TimeoutSeconds`의 `int.TryParse`에 Trim 적용. 공백이 섞인 env가 조용히 기본값이 되는 것을 막는다
8. stderr 첫 줄 160자 redact 로그 (위 3번 결정)
9. 검증용 대역 스크립트: `fake-fail.sh`, `fake-big.sh` 추가. 빈 출력은 `/usr/bin/true`로 확인한다. `fake-slow.sh`의 `ps` 의존 제거
   - 런타임 이미지에 procps가 없다. `ps -o pgid=`는 실패한다. `/proc/$$/stat` 5번 필드로 교체한다
10. `env.example`에 `ASSISTANT_DIAGNOSTIC`, `ASSISTANT_OPENCODE_BIN`, `ASSISTANT_JOB_TIMEOUT_SECONDS` 키 이름 추가
11. 종료 관찰성: SIGTERM 수신, 종료 소요 시간, 폐기한 queued update 수를 로그에 남긴다. 워커의 비-OCE 예외가 `Task.WhenAll` 밖으로 던져져 `on-failure` 재기동으로 이어지지 않게 한다

## 구현 상세

### 순서

1. `Runner.cs`: 꼬리 링, `StdoutTail`/`StderrTail`, 바이너리 선택 공용화, `HasExited` 경합, Trim
2. `MessageFormat.cs` + 단위 테스트: 순수 함수부터 고정한다
3. `Delegation.cs`: CAS 슬롯, 시작/편집/결과, 예외 변환
4. `BotHost.cs`, `Router.cs`, `Program.cs`, `compose.yaml`, `env.example`, scripts
5. 빌드와 `dotnet test`, 그다음 서버 검증

### API (Telegram.Bot 22.10.4, 패키지 소스로 확인)

- `SendMessage(chatId, text, parseMode, replyParameters, replyMarkup, linkPreviewOptions, ..., cancellationToken)`
- `EditMessageText(chatId, messageId, text, parseMode, replyMarkup, linkPreviewOptions, ..., cancellationToken)`. `replyMarkup`이 `linkPreviewOptions`보다 앞이다
- `ReplyParameters.MessageId`는 `int?`, `AllowSendingWithoutReply`는 `bool`
- `Message.Id`가 메시지 id다
- 라이브러리는 4096 초과를 분할하지 않는다. 빈 문자열은 400 `message text is empty`

### 로그 형식

```
[info] job kind=Success exit=0 elapsed=12.3s out=4210B err=0B procs=0 zomb=0 notify=0.4s
[info] job rejected: busy
[warn] job kind=Application exit=1 err1="Error: model not found"
```

`err1`은 stderr 첫 줄 160자, `Log.Redact` 경유. 여러 줄 금지.

### 테스트

`tests/Assistant.Bot.Tests` (xunit)에 `MessageFormat` 케이스만 둔다. 텔레그램 호출, 목킹은 없다.

- 빈 출력 3조합, 3000자 경계, 1MB 초과 푸터, 푸터 상호 배타
- 이모지 절단 시 lone surrogate 없음, 총 길이 4000 이하
- `\r` 변환, 3연속 개행 축약, stderr ANSI 제거, stdout ANSI 유지
- stderr만 있는 실패, exit 음수 헤더, U+FFFD 제거

`Assistant.Bot.csproj`에 `InternalsVisibleTo`를 추가한다. Docker 빌드는 테스트 프로젝트를 참조하지 않으며 `.dockerignore`에 `tests/`를 추가한다.

실행은 경로를 명시한다. 루트에서 `dotnet test`를 돌리면 테스트 프로젝트를 찾지 못하고 0개로 성공한다.

```
DOTNET_ROLL_FORWARD=Major dotnet test tests/Assistant.Bot.Tests
```

`DOTNET_ROLL_FORWARD`는 .NET 10 런타임이 없고 11만 있는 개발 맥에서만 필요하다. 서버나 런타임이 맞는 기기에서는 빼도 된다.

## 변경 파일

| 파일 | 내용 |
|---|---|
| 신규 `Delegation.cs` | 실행 조정, 시작/완료/결과 전송, 예외 변환, 로그 |
| 신규 `MessageFormat.cs` | 본문 조립, 절단, 푸터, 정규화. 순수 함수 |
| 신규 `tests/Assistant.Bot.Tests/` | `MessageFormat` 단위 테스트 |
| `BotHost.cs` | chat gate 제거, binary 전달, 폐기 수 로그, 워커 예외 처리 |
| `Router.cs` | 인가/필터 유지, echo 제거, Delegation 호출 |
| `Program.cs` | 봇 모드 SIGTERM, config 가드, 종료 로그, 바이너리 선택 |
| `Runner.cs` | 꼬리 캡처, 바이너리 선택 공용화, HasExited 경합, Trim |
| `compose.yaml` | `stop_grace_period: 45s` |
| `scripts/fake-{fail,big}.sh` | 신규 대역 바이너리 |
| `scripts/fake-slow.sh` | `ps` 의존 제거 |
| `env.example` | 선택 env 키 이름 추가 |
| `.dockerignore` | `tests/` 제외 |

## 검증 절차

전제: 구현 후 실행. `docker compose config` 전체 출력은 금지 (토큰 평문).

0. 준비: `docker compose build bot` (run은 이미지를 재빌드하지 않는다), 도구 확인
   `docker compose run --rm -T --entrypoint sh bot -c 'ls -l /usr/bin/true /usr/bin/setsid; command -v tr head du cut'`
1. 기준값: `/work`의 `git status --porcelain`, 컨테이너 안 `du -sb /state`
2. 러너 회귀 (봇 가동 중 가능, run-once는 폴링하지 않는다)
   - 사전 정리: `docker compose run --rm -T --entrypoint sh bot -c 'rm -f /work/PHASE1B_OK'`
   - `docker compose run --rm -T --name assistant-runner-probe -e ASSISTANT_RUN_ONCE='runner probe' -e ASSISTANT_OPENCODE_BIN=/app/scripts/fake-slow.sh -e ASSISTANT_JOB_TIMEOUT_SECONDS=45 bot`
   - 다른 ssh에서 `docker top assistant-runner-probe -eo pid,pgid,stat,args`로 관찰
   - 판정: `Kind=Timeout`, `TimedOut=true`, `RemainingInGroup=0`, `ZombiesInGroup=0`, 약 47~49초. 컨테이너 잔존 0
   - 종료코드는 두 값을 구분한다. JSON `ExitCode`는 신호 종료값(137/143)일 수 있고, 프로세스 종료코드는 Program이 `Kind=Timeout`을 124로 매핑한다
   - 이미지에 `ps`가 없으므로 `docker exec ps` 대신 `docker top`을 쓴다
3. 실패/경계 (봇 가동 중, fake만 사용)
   - `... -e ASSISTANT_RUN_ONCE='fail' -e ASSISTANT_OPENCODE_BIN=/app/scripts/fake-fail.sh bot`: `Kind=Application`, exit 7, stderr 꼬리
   - `... -e ASSISTANT_RUN_ONCE='empty' -e ASSISTANT_OPENCODE_BIN=/usr/bin/true bot`: stdout 0B
   - `... -e ASSISTANT_RUN_ONCE='big' -e ASSISTANT_OPENCODE_BIN=/app/scripts/fake-big.sh -e ASSISTANT_JOB_TIMEOUT_SECONDS=60 bot`: `StdoutTruncated=true`, `Stdout`에 `HEAD-MARKER` 있음, `StdoutTail` 64KB에 `TAIL-MARKER-END` 있음, 텔레그램 본문에는 `HEAD-MARKER` 없음
   - 두 tail 필드는 JSON에서 각 64KB까지다. 출력이 길어 터미널이 느려지면 `| tail -40`으로 확인한다
   - 1a 계약 회귀: `ASSISTANT_DIAGNOSTIC` 없이도 run-once의 `bin` 로그가 fake 경로인지 확인
4. 타임아웃 UX (봇 down)
   - `docker compose stop bot` -> `docker compose ps --status running --quiet bot`이 빈 출력인지 확인
   - `docker compose run --rm -T --name assistant-timeout-probe -e ASSISTANT_DIAGNOSTIC=1 -e ASSISTANT_OPENCODE_BIN=/app/scripts/fake-slow.sh -e ASSISTANT_JOB_TIMEOUT_SECONDS=20 bot`
   - 폰에서 지시, `[시간 초과]`와 안내 문구 확인 후 다른 ssh에서 `docker stop -t 45 assistant-timeout-probe`
   - `docker ps -a --filter label=com.docker.compose.oneoff=True` 잔존 0, `/work`의 `index.lock` 유무 확인
5. SIGTERM 중단 (봇 down 유지)
   - `docker compose run -d --name assistant-term-probe -e ASSISTANT_DIAGNOSTIC=1 -e ASSISTANT_OPENCODE_BIN=/app/scripts/fake-slow.sh bot`
   - 폰에서 지시, 시작 답장 확인 후 `docker stop -t 45 assistant-term-probe`
   - `[중단됨]` 수신, `docker inspect -f '{{.State.ExitCode}}'`가 0, 종료 소요 로그, 잔존 프로세스 0, `docker rm -f assistant-term-probe`
6. 복구
   - `docker compose up -d bot`, `logged in as` 확인
   - `docker logs --since 3m assistant-bot-1 2>&1 | grep -E 'logged in|409|401'`
   - `docker ps --filter label=com.docker.compose.oneoff=True` 빈 출력
   - 진단 env 잔재는 개수만 확인: `grep -cE '^(ASSISTANT_DIAGNOSTIC|ASSISTANT_OPENCODE_BIN|ASSISTANT_JOB_TIMEOUT_SECONDS)=' ~/.config/assistant/env` (0)
7. 폰 정상 위임: `Create PHASE1B_OK in the repo root containing ok` 전송
   - 시작 답장, 완료 편집, 결과의 API 3회. 채팅창 표시는 2건
   - 파일은 `docker exec assistant-bot-1 ls -l /work/PHASE1B_OK`로 확인 (호스트 `git status`는 dubious ownership으로 실패)
8. busy 즉시성: 실행 중 두 번째 메시지가 2초 이내에 거절되는지. 완료 후 세 번째가 정상 실행되는지. 0.5초 간격 2건을 3회 반복해 순서 역전 여부를 기록 (단일 사용자 환경에서는 수용)
9. 표시 경계: 시작 답장을 폰에서 삭제한 뒤 완료 -> 새 메시지 폴백. 지시 전송 직후 원문 삭제 -> 시작 답장 성공. 3990/4000/4200자 출력 -> 400 없음, 서로게이트 깨짐 없음
10. 유출/누수/실측
   - `docker logs assistant-bot-1 2>&1 | grep -cE '[0-9]{8,10}:[A-Za-z0-9_-]{35}'` = 0
   - 반복 실행 후 `docker top assistant-bot-1` 프로세스 수와 `/proc` 수가 증가하지 않는지
   - 1회 소요 시간, peak RSS, `/state` 증가, 타임아웃 답장 지연, SIGTERM 종료 소요

## 반증된 가정 (검토에서 확정)

| 가정 | 실제 |
|---|---|
| 봇 종료 시 Runner 그룹 kill이 돌아간다 | 봇 모드는 SIGINT만 등록. SIGTERM이면 dotnet이 즉시 종료되고 opencode는 SIGKILL된다 |
| stdout에서 마지막 3000자를 쓸 수 있다 | `Capture`는 앞 1MB만 보존한다. 꼬리가 없다 |
| 실패 시 stderr에 원인이 남는다 | opencode는 도구 로그를 stderr로 쓰지만 `--print-logs`는 기본 off다. 실패 1건으로 확인 필요 |
| stdout의 ANSI를 걷어내야 한다 | 비TTY stdout은 순수 텍스트다. ANSI는 stderr 경로에만 있다 |
| 라이브러리가 긴 본문을 나눠 보낸다 | Telegram.Bot 22.10.4에는 분할/검증이 없다. 4096 초과는 400이고 현재 코드는 침묵한다 |
| 작업을 분리(fire-and-forget)해도 종료 정리가 된다 | 워커 `WhenAll` 후 프로세스가 끝나 분리 작업은 유실된다 |
| 1a 실측 7.55초면 30초로 충분하다 | 최악 경로는 kill 3회와 send 2회로 46.5초다. 30초면 SIGKILL로 답장이 사라진다 |
| `CancellationToken.None` + 10초 상한 | None은 상한이 없다. CTS를 따로 만들어야 한다 |
| 편집 오류는 문구 완전 일치로 판정한다 | 실제 메시지는 `Bad Request: ` 접두사를 포함한다 |
| `LinkPreviewOptions.IsDisabled`는 정적 멤버다 | 인스턴스 프로퍼티다. 정적 `Disabled`는 공유 mutable 인스턴스라 쓰지 않는다 |
| Runner는 예외를 던지지 않는다 | `StandardInput.Close`, `BaseStream` 접근이 try 밖이라 던질 수 있다 |
| 이미지에 `ps`가 있다 | git/ripgrep만 설치한다. `fake-slow.sh`의 `ps`가 실패한다 |
| 재시작하면 같은 지시가 중복 실행된다 | 기동 시 backlog를 폐기하므로 유실된다. 중복은 아니다 |

추가 확인:
- 타임아웃 시 stdout이 비어 있을 가능성이 높다 (완료된 텍스트 파트만 출력)
- `SendMessage` 빈 문자열은 400 `message text is empty`
- `EditMessageText`는 400 `message is not modified`, `message to edit not found`를 반환할 수 있다
- 원문 답장은 원문 삭제 시 400. `AllowSendingWithoutReply` 필요
- 숨은 폴러 없음 (`assistant-bot-1` 1개, one-off 0, systemd/cron 없음). 409 위험은 우리가 만드는 두 번째 인스턴스뿐
- 로컬 NuGet 캐시는 비어 있었다. API 확인은 배포된 nupkg와 라이브러리 소스로 했다

## 위험

- 재시작 시 진행 작업 유실과 "실행 중" 표시 잔존 (job 상태 영속화는 보류)
- Telegram 응답 유실 시 지시가 실행되지 않은 채 사라질 수 있다 (update id 중복 방지는 보류)
- SIGKILL된 opencode가 `/work/.git/index.lock`을 남길 수 있음 (검증에서 확인, 자동 정리는 범위 밖)
- 300초 단일 작업이 전체 위임을 막음 (의도된 트레이드오프)
- `/state` 증가 (게이트 1회에 약 +150M). 1b에서 실측만 한다
- OOM 등 외부 신호 종료는 `[실패]`로만 표시되어 원인 구분이 약하다

## 소요

구현 4~5시간, 단위 테스트 1시간, 검증 2시간. 합계 약 1일

## 하지 않는 것

요약과 변경 파일 목록(2번), repo 선택과 되묻기(3번), 캡처, cron, 스레드, 승인, `/ping`, `/cancel`, SSH/CouchDB/Syncthing/토큰 회전, 러너 컨테이너 분리, `--format json` 전환, job 상태 영속화, `index.lock` 자동 정리, stderr 에러 라인 휴리스틱

## 진행 상태

- 2026-11-23: 구현 완료 (커밋 `7031e38`). 로컬 Debug/Release 빌드 경고 0, 단위 테스트 13개 통과
- 단위 테스트는 경로를 명시해야 한다. 루트 `dotnet test`는 0개로 성공한다
- 서버 배포 시도: `my-server`(p14s)가 Tailscale에서 offline(마지막 접속 3일 전)이라 ssh 타임아웃. 배포와 검증 0~10은 보류
- 다음 순서: 서버 기동 -> `deploy.sh` -> 검증 0~3(봇 없이 가능) -> 폰 테스트 4, 5, 7, 8, 9

## 참조

- 로드맵: `docs/roadmap.md`
- 선행 단계: `docs/plans/phase1a.md`, 커밋 `f37b522`
