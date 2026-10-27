# 싱글플레이 저장·이어하기 재검증

검사일: 2026-10-27 (KST)

검사한 게임 코드: `b04889b46276405f8133a2353b37d92b7ad863da`

환경: Windows, .NET SDK 8.0.303 / 10.0.400, Unity 6000.3.21f1 참조 DLL.

## 통과한 검사

| 검사 | 결과 |
| --- | --- |
| `SinglePlayerSaveSmoke` | 시드 42, 12345, -7 각각 63일차까지 진행 후 직렬화·복원 일치. 시계 잔여 시간과 이후 100회 진행 결과 일치. 미래 버전, 알 수 없는 행동, NaN 시간 거부. |
| `CoreSimulationSmoke` | 지역 6, 세력 3, 자원지 80, 사건 1283, 미션 1281, 군대 3의 시뮬레이션 검사 통과. |
| `PvpMapAuthoritySmoke` | 158턴 동안 이동·광산·성·적 수도와 최종 승자 검증 통과. |
| `UnityCoreCompile` | 컴파일 오류 0개, 경고 24개. Unity 실행 검사는 아님. |
| 저장 테스트 코드 및 `UNITY_WEBGL` 분기 컴파일 | 설치된 NUnit·Unity TestRunner DLL을 참조한 별도 컴파일 하네스에서 오류 0개, 경고 31개. 테스트 메서드를 실행한 것은 아님. |
| 데스크톱 파일 저장 | 실제 `SinglePlayerFileStorage`를 호출하는 별도 .NET 하네스에서 원자적 교체, 한글, 백업 분리, 중단된 임시 파일 쓰기, 잘못된 슬롯 거부 통과. |
| 브라우저 저장 | 실제 IndexedDB에서 빈 슬롯, 1.3 MB 한글 포함 데이터, 페이지 새로고침 후 복원, 자동·수동·백업 슬롯 분리, 저장소 사용 불가 오류 전달 통과. |

브라우저 검사는 저장소 플러그인 `.jslib`를 사용하고 Unity 문자열 변환/`SendMessage` 연결은 테스트용 함수로 대체한다. 실제 WebGL 게임 전체를 실행한 결과와 구분해야 한다. 데스크톱 보조 검사는 파일 입출력 계층만 검증하며 Unity JSON 직렬화와 UI는 포함하지 않는다.

## 실행하지 못한 검사

`Game.Tests.Playability.SinglePlayerSaveTests`를 Unity EditMode Test Runner로 재시도했으나 테스트 시작 전에 종료 코드 **198**로 종료됐다.

```text
No valid Unity Editor license found. Please activate your license.
```

따라서 전체 컨트롤러 저장·이어하기, Unity JSON 직렬화, 실제 게임 화면과 WebGL 빌드는 아직 검증되지 않았다. 위 컴파일 및 독립 검사 통과를 전체 Unity 테스트 통과로 해석하면 안 된다. Unity 라이선스 활성화 후 이 테스트와 실제 WebGL 저장·재접속을 확인해야 한다.

## 저장소에 포함된 검사 재현

저장소 루트에서 실행한다. 처음 실행할 때는 `--no-restore`를 생략해 복원을 허용한다.

```powershell
# .NET 8 SDK
dotnet run --project Validation/SinglePlayerSaveSmoke
dotnet run --project Validation/CoreSimulationSmoke
dotnet build Validation/UnityCoreCompile

# .NET 10 SDK
dotnet run --project Validation/PvpMapAuthoritySmoke

# 실행 후 http://127.0.0.1:8127/ 열기 (실서비스와 분리된 로컬 주소)
node Validation/SinglePlayerWebStorageBrowserSmoke/server.cjs
```

Unity 라이선스를 활성화한 환경에서는 다음과 같이 실행한다. 결과 파일이 생성되고 통과한 것을 확인한 뒤 실제 WebGL 빌드에서도 저장 후 재접속을 검사한다.

```powershell
& '<Unity 설치 경로>/Editor/Unity.exe' -batchmode -nographics `
  -projectPath (Get-Location).Path -runTests -testPlatform EditMode `
  -testFilter Game.Tests.Playability.SinglePlayerSaveTests `
  -testResults save-tests.xml -logFile save-tests.log
```
