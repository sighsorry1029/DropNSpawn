# Expand World Prefabs 개요

## 목적

`Expand World Prefabs`는 Valheim의 Expand World 계열 서버 자동화 모드다. 서버 관리자가 YAML 규칙을 작성하면, 서버가 prefab/object/ZDO/world event를 감지하고 그에 맞춰 spawn, swap, remove, data 수정, terrain 수정, RPC 호출, poke, command 실행 같은 작업을 수행한다.

공개 README는 이 모드를 "object가 spawn, destroy 되는 것 등에 반응하는 rule을 만들 수 있는 모드"로 설명하며, 서버에 설치하라고 안내한다. 로컬 DLL도 이 설명과 맞는다. 디컴파일된 plugin attribute 기준 GUID는 `expand_world_prefabs`, 이름은 `Expand World Prefabs`, 런타임 namespace는 `ExpandWorld.Prefab`, 모드 버전은 `1.52`다.

## 참고한 자료

- 공개 README: <https://github.com/JereKuusela/valheim-expand_world_prefabs/blob/main/README.md>
- Raw README: <https://raw.githubusercontent.com/JereKuusela/valheim-expand_world_prefabs/main/README.md>
- 로컬 DLL: `C:/Users/blizz/Documents/0_Codex/ExpandWorldPrefabs.dll`
- 로컬 디컴파일 조각: `tmp_ewp_ilspy_20260420`

로컬 DLL 메타데이터:

- Assembly name: `ExpandWorldPrefabs`
- Assembly version: `1.0.0.0`
- File size: `232,960` bytes
- File timestamp: `2026-04-20 16:06:09`
- BepInEx plugin attribute: `expand_world_prefabs`, `Expand World Prefabs`, `1.52`

## 설정 파일과 기본 구조

주 설정 파일은 다음 경로다.

```text
expand_world/expand_prefabs.yaml
```

README 기준으로 이 파일은 world를 로드할 때 생성된다. 또한 World Edit Commands의 data system을 사용하므로, YAML 값은 단순 고정값만 쓰는 것이 아니라 reusable data entry, parameter, function 기반으로 확장할 수 있다.

규칙 하나의 흐름은 대략 다음과 같다.

1. 대상 prefab 또는 prefab group을 고른다.
2. 어떤 trigger에서 실행될지 정한다.
3. biome, global key, 위치, object data, nearby object 같은 filter를 검사한다.
4. `chance` 또는 `weight`로 실행 여부나 선택 대상을 결정한다.
5. spawn, swap, remove, data 수정, RPC, terrain, command 등의 action을 실행한다.

즉 단순한 spawn replacement 모드라기보다는 prefab/ZDO/world event에 반응하는 범용 rule engine에 가깝다.

## Trigger 종류

README에서 확인되는 주요 trigger는 다음과 같다.

- `create`: object가 생성될 때.
- `destroy`: object가 파괴될 때.
- `change`: object data가 바뀔 때.
- `state`: object가 특정 state change RPC를 broadcast할 때.
- `say`: object 또는 player가 특정 text를 말할 때.
- `poke`: 다른 rule/action에서 수동으로 발생시키는 내부 trigger.
- `globalkey`: global key가 set/remove될 때.
- `key`: custom saved data가 set/remove될 때.
- `custom`: custom code가 직접 발생시키는 event.
- `event`: random event가 시작/종료될 때.
- `time`: Valheim world time 기준 trigger.
- `realtime`: 서버의 실제 시간 기준 trigger.

그래서 이 모드는 spawn/despawn lifecycle뿐 아니라 chat command, global progression, random event, object state, scheduled task 같은 영역에도 개입할 수 있다.

## Filter 종류

rule은 여러 filter로 좁힐 수 있다. README 기준으로 중요한 filter 묶음은 다음과 같다.

- 정확한 prefab name, wildcard, component group, `creature`, `structure` 같은 keyword 기반 prefab 매칭.
- `biomes`, `bannedBiomes`.
- `day`, `night`.
- 월드 중심 거리, x/y/z 좌표 범위.
- altitude, terrain height, terrain paint.
- `environments`, `bannedEnvironments`.
- `globalKeys`, `bannedGlobalKeys`.
- custom saved `keys`, `bannedKeys`.
- nearby `locations`, `bannedLocations`.
- nearby `events`, player progression 기반 `playerEvents`, `bannedPlayerEvents`.
- 다른 모드가 group 구현을 제공하는 경우의 `groups`, `bannedGroups`.
- bool, float, hash, int, quat, string, vec, item/container data 기반 data filter.
- nearby object filter. 거리, 높이, offset, weight, data filter까지 조합할 수 있다.

성능 관점에서는 broad wildcard, 아주 큰 nearby object radius, 전역 ZDO scan이 필요한 object filter가 무거워질 수 있다.

## Action 종류

filter와 chance/weight를 통과하면 다음과 같은 action을 수행할 수 있다.

- console command 실행.
- 일부 triggering RPC/action 흐름 cancel.
- ZDO data 설정 또는 inject.
- container item 추가/제거.
- object owner 변경.
- 원본 object remove. delay 가능.
- 다른 prefab spawn.
- 원본 prefab을 다른 prefab으로 swap.
- 원본 object의 drop 또는 명시된 item data spawn.
- 다른 object를 `poke`해서 그 object의 `poke` rule 실행.
- object 관련 RPC 또는 client RPC 호출.
- terrain operation helper를 통한 terrain 수정.

디컴파일된 `Manager` 코드에서도 weighted spawn/swap, delayed remove, data injection, add/remove items, poke, terrain operation, object RPC, client RPC, global client RPC, drop spawning 처리가 확인된다.

## Chance와 Weight

이 모드는 `chance`와 `weight`를 둘 다 지원한다.

- `chance`는 filter가 모두 통과한 뒤 최종 실행 확률로 사용된다.
- `weight`는 weighted entry 중 하나를 선택할 때 사용된다.
- README 기준 선택 확률은 `weight / sum`이다.
- weight 합계는 최소 `1`로 취급되므로, 전체 weight가 1보다 작으면 아무것도 선택되지 않을 여지가 생긴다.
- `fallback` entry는 다른 matching entry가 선택되지 않았을 때만 실행될 수 있다.

따라서 "이 rule이 실행될 확률"과 "여러 후보 중 무엇을 고를지"를 분리해서 설계할 수 있다.

## 서버 런타임 구조

디컴파일 코드 기준 핵심 rule 실행은 서버 중심으로 보인다.

- `Manager.HandleGlobal(...)`과 `Manager.Handle(...)`은 `ZNet.instance.IsServer()`가 false면 early return한다.
- `EWP.LateUpdate()`에서 created/changed 처리, delayed spawn, delayed remove, delayed poke, delayed RPC, delayed terrain, delayed owner, saved data 저장을 처리한다.
- data/loading watcher를 세팅하고, `ewp_reload` console command로 saved data reload를 지원한다.
- `expand_world_events`가 설치되어 있으면 해당 plugin과 연동해 current random event 조회를 확장한다.

즉 rule 선택과 world/ZDO mutation은 서버가 담당하는 구조로 보는 것이 맞다. 다만 action 중에는 client RPC처럼 클라이언트에 영향을 주는 호출도 포함된다.

## 가능한 활용 예시

실사용 관점에서 가능한 일은 다음에 가깝다.

- creature가 spawn될 때 다른 creature로 바꾸거나 data를 수정한다.
- built structure나 generated object를 다른 object로 교체한다.
- object가 파괴될 때 다른 object나 item을 spawn한다.
- global key, custom data, time, realtime을 이용해 서버 이벤트를 만든다.
- `say` trigger와 command를 이용해 chat/admin command 비슷한 동작을 만든다.
- 어떤 object의 lifecycle에 반응해 주변 object를 `poke`하고 연쇄 rule을 실행한다.
- ZDO data를 수정해 health, owner, container contents, serialized state 등을 바꾼다.
- matching object 주변 terrain을 수정한다.
- global key나 player event를 이용해 progression-sensitive world behavior를 만든다.

결론적으로, 목적이 명확한 단일 시스템 편집기라기보다는 서버 YAML로 world behavior를 자동화하는 도구다.

## 운영상 주의점

이 모드는 잘못 쓰면 월드를 크게 망가뜨릴 수 있는 수준의 권한을 가진다.

운영 시에는 다음을 권장한다.

- 먼저 백업 월드에서 테스트한다.
- wildcard나 component group보다 좁은 prefab filter부터 시작한다.
- `remove`, `swap`, `triggerRules`, repeat, delayed action, terrain operation은 특히 조심한다.
- nearby object filter의 radius를 과하게 키우지 않는다.
- README는 terrain change가 client가 로드한 zone에서만 동작하며 radius를 대략 100m 이하로 유지하라고 안내한다.
- spawn/remove가 다시 rule을 trigger하도록 허용하면 연쇄 실행이 생길 수 있으므로 주의한다.
- `say` trigger는 server client를 추가하고 boss kill loot scaling에 영향을 줄 수 있다고 README가 경고한다.

## DropNSpawn과 비교

DropNSpawn 관점에서 보면 Expand World Prefabs는 더 넓고 더 저수준이다.

DropNSpawn은 object drop, character drop, spawner, location, spawnsystem처럼 도메인이 나뉜 구조를 제공하고, 각 도메인별 schema와 generated example을 통해 비교적 제한된 범위의 설정을 안전하게 다루려는 쪽이다.

반면 Expand World Prefabs는 prefab lifecycle, object state, ZDO data, RPC, terrain, command, custom data를 포괄하는 generic event/action rule engine에 가깝다. 더 많은 일을 할 수 있지만, validation과 performance, safety는 사용자가 작성한 YAML의 폭과 정확도에 더 크게 좌우된다.

짧게 정리하면 다음과 같다.

- DropNSpawn: 도메인별로 제한된 고수준 설정.
- Expand World Prefabs: 서버 YAML 기반 범용 prefab/ZDO/world automation.

## 한 줄 요약

`Expand World Prefabs`는 서버의 `expand_world/expand_prefabs.yaml`에 다음과 같은 규칙을 적을 수 있게 해주는 모드다.

```text
이 prefab/event/state/time/global key가 발생하고,
filter 조건이 맞으면,
chance/weight로 실행 대상을 고른 뒤,
spawn, swap, remove, data 수정, RPC, poke, command, terrain 수정 등을 실행한다.
```

따라서 이 모드는 단순 spawn replacement가 아니라, Valheim 서버에서 prefab과 world behavior를 YAML rule로 자동화하는 강력한 서버 중심 모드로 보는 것이 적절하다.
