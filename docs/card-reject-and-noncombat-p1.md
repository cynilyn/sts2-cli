# 카드 거부 원인 수정(작업 A)과 비전투 프로토콜 P1(작업 B)

브랜치 `noncombat-p1` (`local/fix/stall-investigation` + 2커밋). 로컬 전용, 푸시하지 않음.
표기: **[확인]** 재현·계측으로 직접 확인, **[추정]** 정황으로 추정, **[미확인]** 확인하지 못함.

| 커밋 | 내용 |
|---|---|
| `96a26cb` | 작업 A: 살아 있는 적이 없을 때 단일 대상 카드는 `can_play=false` |
| `8de7202` | 작업 B: 비전투 프로토콜 필드 추가 (`purpose`/`prompt`, 상점 ID, `event_id`, 지도) |

## 작업 A. `could not be played` 원인과 수정

### 원인 [확인]
`can_play=true`인데 `play_card`가 `Card could not be played (still in hand after action)`를 내던 거부는 **3막 보스 Test Subject 전투에서만** 났다.

- 거부 순간 시뮬레이터에 모든 크리처(사망 포함)를 덤프하는 진단 로그를 넣어 확인했다. 재현한 4건(Ironclad 시드 1에서 Rampage 3건, 시드 3에서 Anger 1건)이 모두 같았다.
  `TEST_SUBJECT alive=False hp=0 powers=[ADAPTABLE_POWER:1]`, `phase=Play`, 대상 `target=null`.
- 즉 보스가 폼 사이에서 hp 0 / not alive 상태인데 플레이어 턴(Play 단계)은 계속 진행된다. `enemies`(살아 있는 적만 직렬화)가 비고, `AnyEnemy` 카드는 대상이 null이 되어 게임이 카드를 시작조차 하지 않는다. 손패가 그대로라 "still in hand"로 보고됐다.
- `CanPlay()`는 대상 유무를 보지 않아 `can_play`가 거짓으로 true였다.
- 로그 전수 대조: 거부가 한 번이라도 있었던 런은 **A0 200/200, A10 174/174가 모두 마지막 전투가 Test Subject**였다. 다른 적과의 전투에서 난 거부는 하나도 없다.
- 거부된 카드가 거의 공격 카드(Shiv, Strike, Anger, Go for the Eyes 등)인 것도 같은 설명과 맞는다. 공격 카드는 `AnyEnemy`이고 방어·스킬 카드는 대상이 필요 없어 영향을 받지 않는다. [추정] 한 런에서 거부가 여러 번 나는 것도 같은 이유로 보인다(정책이 다른 공격 카드를 골라도 대상이 없기는 마찬가지).

### 추정으로 남는 부분
- 왜 게임이 이 구간에서 Play 단계를 유지하는지(Test Subject의 부활 메커니즘이 정확히 어떤 턴 시점에 hp 0 상태를 만드는지)는 디컴파일 없이 확인하지 못했다. **[추정]** 폼 전환 중 대기 상태. 관측 사실은 "alive=False, hp=0, ADAPTABLE_POWER만 남음, end_turn 후 전투가 계속된다"까지다.
- 거부 558건·538건 중 재현해 본 것은 24건(같은 시드 100런)이다. 나머지는 "거부가 있던 모든 런이 Test Subject로 끝남"이라는 정황으로 같은 원인이라고 본다. **[추정]**

### 수정
`src/Sts2Headless/RunSimulator.cs`
- 직렬화: 살아 있는 적이 없으면 `AnyEnemy` 카드의 `can_play`를 false로.
- `DoPlayCard`: 대상이 없으면 `Cannot play card X: no living enemy to target` 오류 (기존의 오해를 부르는 메시지 대신).
- 남은 `could not be played` 경로에는 모든 크리처를 기록하는 `DIAG reject ...` 로그를 남겼다. 다른 원인이 생기면 바로 보인다.

못 고친 것은 없다. 보스가 hp 0으로 서 있는 구간 자체는 게임 동작이라 건드리지 않았다. 이 구간에서는 비대상 카드만 낼 수 있고 `end_turn`이 정상 동작한다(테스트로 확인).

### 측정
같은 시드(`probe_heuristic_{캐릭터}_0..19`, 무적 모드, A0, 5캐릭터 × 20런 = 100런)로 수정 전 로그(`sts2_bot/logs/stall_runs_v5`)와 비교했다.

| | 수정 전 | 수정 후 |
|---|---|---|
| 카드 거부 | 24건 | **0건** |
| 모든 오류 | 24건 | **0건** |
| 결과(시드별) | 승 54 / 패 45 / 타임아웃 1 | 승 54 / 패 45 / 타임아웃 1 (시드별로도 전부 동일) |
| 교착(stuck/process_error/end_turn_failed) | 0 | **0** |

최종 빌드로 5캐릭터 × 8런을 다시 돌렸을 때도 오류 0건, 교착 0건이었다(승 24 / 패 15 / 타임아웃 1).

타임아웃 1건은 `Necrobinder_3`이다. 엘리트 Decimillipede 두 마리를 상대로 1,590라운드를 도는 정책의 무한 교착이다(시뮬레이터는 계속 응답). 수정 전 로그에도 같은 시드가 같은 방식으로 있다. 이번 작업의 대상이 아니다.

### 테스트
`tests/test_dead_target.py` (3개). `start_combat`으로 `TEST_SUBJECT_BOSS`를 시작해 보스가 쓰러질 때까지 싸운 뒤 확인한다.
- 단일 대상 카드가 모두 `can_play=false`
- `play_card`가 `no living enemy` 오류
- `end_turn` 후 전투 계속

수정을 되돌린 빌드에서 앞의 2개가 실패하고 수정 후 통과함을 확인했다(세 번째는 회귀 방지용).

## 작업 B. 비전투 프로토콜 P1

기존 응답 형식은 바꾸지 않았고 필드만 추가했다. 설계: `sts2_bot/docs/noncombat_env_design.md` §6.

| # | 항목 | 결과 |
|---|---|---|
| 1 | 비전투 `card_select`의 목적 | `purpose`, `prompt` 추가 |
| 2 | 상점·보유품 ID | 상점 카드·유물·포션에 `id`, `can_buy` 추가. 보유 유물·포션 `id`는 이미 있었음(d6f92c2) |
| 3 | `event_choice`의 이벤트 ID | `event_id` 추가 (`EVENT.X`) |
| 4 | 전체 지도 | `map_select`에 `map{rows,boss,current_coord}` 추가 (`get_map`은 이미 있었음) |
| 5 | 보물방·보스 유물 | 별도 결정 없음, 자동 수집 (아래) |

### 1. `purpose` / `prompt`
- `ICardSelector.GetSelectedCards(options, min, max)`에는 목적 인자가 없고 선택기 내부의 `PendingPrompt`는 선언만 있고 한 번도 채워지지 않았다(설계 문서의 "선택기 내부에 PendingPrompt가 있음"과 다르다).
- 대신 `CardSelectCmd.From*` 진입점(`FromDeckForUpgrade/Removal/Transformation/Enchantment/...`)에 Harmony 접두 패치를 걸어 동기 구간 동안 목적을 기억한다(기존 `canSkip` 패치와 같은 방식). 가장 바깥 호출이 우선한다. 게임이 `CardSelectorPrefs.Prompt`를 주면 그 키를 `prompt`로 노출한다(예: `card_selection.TO_REMOVE`, `cards.HEADBUTT.selectionScreenPrompt`). `card_selection.TO_*` 프롬프트는 일반 진입점(grid/hand/pile)으로 들어온 선택의 목적도 구분해 준다.
- `purpose` 값: `upgrade`, `remove`, `transform`, `enchant`, `discard`, `exhaust`, `choose`, `deck`, `grid`, `hand`, `pile`, `other`. `prompt`는 게임이 프롬프트를 주지 않는 선택에서는 빈 문자열이다.
- **주의** [확인]: 전투 중 카드 선택(버리기, 소멸, 손패 선택)도 같은 필드를 갖는다. 비전투 여부는 `decision`/`context.room_type`으로 구분해야 한다.

### 2. 상점 ID
- 카드 `CARD.X`, 유물·포션은 보유품과 같은 `Entry` 형식(예: `GORGET`). 참고 포크는 유물·포션을 `RELIC.X` 형식으로 썼지만, 보유 유물 `id`와 같은 어휘로 인덱싱하려고 `Entry`로 맞췄다. 테스트가 "산 유물이 `player.relics`에 상점이 알려준 id 그대로 나타남"을 확인한다.
- `can_buy`(재고 있음 + 골드 충분)를 카드·유물에도 추가했다. 포션은 이미 슬롯 조건까지 포함한 `can_buy`가 있었다.
- 함께 고친 버그: `buy_relic`이 구매에 **성공한 뒤** 로그 줄에서 구매 후 null이 된 `entry.Model`을 읽어 `Buy relic failed: Object reference...` 오류를 반환했다. 오류인데 유물은 지급됐다. 참고 포크 e1a0688의 첫 변경과 같은 문제다. 구매 전에 이름과 비용을 읽도록 했다(포션도 동일).

### 3. `event_id`
`localEvent.Id.ToString()` (예: `EVENT.WOOD_CARVINGS`, Neow는 `EVENT.NEOW`).

### 4. 지도
`map_select`의 `map`은 `get_map`과 같은 `rows`, `boss`, `current_coord`다(테스트가 `get_map`과 동일함을 확인).

### 5. 보물방·보스 유물 [확인]
- 보물방: 싱글플레이에서는 제시 유물이 1개(`Auto-picking treasure relic 0 of 1`)라 자동 수집이 게임과 같다. 선택이 없다.
- 전투 보상 타입을 로그로 남겨 100런을 집계했다. 보스 보상은 `GoldReward, CardReward`(+`PotionReward`)뿐이고(138회, 최종 보스 54회는 보상 없이 종료), 엘리트는 `RelicReward`를 포함해 자동 수집된다. 보스 유물 선택 결정은 이 빌드에 존재하지 않는다. 별도 결정 유형을 만들 필요가 없다.

### 필드 확인 (100런, `logs/b_after/cases.fields.json`)

| 필드 | 응답 수 | 누락 |
|---|---|---|
| `card_select.purpose` | 4,639 | 0 |
| `event_choice.event_id` | 613 | 0 |
| `map_select.map` | 3,790 | 0 |
| 상점 카드 `id` / 유물 `id` / 포션 `id` | 1,428 / 612 / 612 | 0 |
| 보유 유물 `id` / 포션 `id` | 687,613 / 337,681 | 0 |

100런은 `purpose` 보강 전 빌드이고, 보강 후 빌드의 40런에서도 누락 0, 오류 0, 교착 0이었다.

### 테스트
`tests/test_protocol_p1.py` (11개): 휴식처 강화 → `upgrade`, 상점 제거 → `remove`, 연속 선택 간 목적 누수 없음, 상점 ID와 `can_buy`, 산 유물 ID 일치, 보유품 ID, 이벤트 ID, 지도 필드와 `get_map` 동일성, 보물방 유물 지급.

## 검증 요약

| 항목 | 결과 |
|---|---|
| 기준선: `sts2_bot/rl/tests` (이 빌드로 `STS2_CLI_PROJECT` 지정) | 수정 전 32 통과, 수정 후 32 통과 |
| sts2-cli `tests/` | 83 통과, 1 실패 (아래) |
| 짧은 전체 런 (100런 ×2, 40런) | 교착 0, 오류 0 |

## 남은 문제

1. `tests/test_characters.py::TestFullRun::test_full_run[Necrobinder]`는 **수정 전 빌드에서도 실패**한다(테스트 정책이 2,000스텝 안에 끝내지 못함). 이번 변경과 무관해 그대로 두었다.
2. `Necrobinder_3` 타임아웃(위): 정책이 엘리트를 못 죽이는 무한 라운드. 환경이 라운드 상한으로 끊어야 한다.
3. A10과 2,000런 전수 재측정은 하지 않았다. A0 100런(+40런) 측정만 했다. 학습이 끝난 뒤 같은 스크립트로 전수 측정이 가능하다(`scripts/repro_card_reject.py --ascension 10`).
4. `sts2_bot`의 "거부된 카드를 그 라운드 동안 마스크" 처리는 더는 필요 없지만 두어도 해롭지 않다. 다만 Test Subject 구간에서는 `enemies`가 빈 채 전투가 진행되므로 환경이 이를 정상 상태로 처리해야 한다(현재 `can_play=false`가 되어 합법 행동은 비대상 카드와 턴 종료뿐).
5. 작업 트리의 `localization_eng/`, `localization_zhs/`는 `sts2-cli-main`에서 복사한 갱신본이라 수정 상태로 남아 있고 커밋하지 않았다. `lib/`는 `.gitignore` 대상이다.
6. 브랜치 `noncombat-p1`은 `local/fix/stall-investigation`을 추적한다. 업스트림에 푸시하지 않았다. `fix/stall-investigation`으로의 병합도 하지 않았다.
7. `DIAG reject` 로그와 `Post-combat rewards` 로그는 stderr에 나가는 진단이다. 소음이 문제면 제거해도 된다.
