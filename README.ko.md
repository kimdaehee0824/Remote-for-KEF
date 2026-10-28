# Remote for KEF

KEF 무선 스피커용 비공식 데스크톱 리모컨입니다. 다른 네트워크 플레이어도 붙일 수 있는 구조로 만들었습니다. Windows를 먼저 지원하고 macOS를 이어서 지원합니다.

[English](README.md)

> KEF와 관계없는 비공식 프로젝트입니다. "KEF"와 제품명은 각 소유자의 상표이며 호환성 설명 목적으로만 사용합니다.

## 기능

- 스피커 이름, 주소, 연결 상태, 현재 입력 표시
- 입력 전환(예: USB-C / PC, Wi-Fi). 음소거 → 전환 → 확인 순서로 진행하고, 새 입력의 음량이 이전보다 높지 않을 때만 음소거를 다시 해제합니다
- 음량 슬라이더와 음소거. 항상 스피커의 실제 값을 표시합니다
- 앱 음량 상한(기본 30). 올리는 것만 제한하고 내리는 것은 항상 허용합니다. 스피커 자체 상한도 함께 적용합니다
- 트레이 상주. 창을 닫아도 계속 실행되고, Windows 로그인 시 조용히 시작하는 옵션이 있습니다
- 시작이나 재연결 때 아무것도 쓰지 않고, 네트워크가 끊겼다 돌아와도 이전 명령을 다시 보내지 않습니다
- Windows 마스터 음량과 동기화하지 않습니다. 스피커 음량을 직접 조절합니다
- Windows 전역 단축키: 음량 올리기/내리기, 음소거(기본 Ctrl+Alt+↑/↓/M, 설정에서 변경). 길게 누르면 계속 바뀌고, 스피커에는 최신 목표값만 보냅니다. 조절할 때 화면 우측 하단에 음량 표시가 나타나며, 라이트/다크 테마와 강조색을 따르고 마우스 휠로도 조절됩니다

진행 중: 메뉴바 음량 슬라이더(macOS).

## 지원 기기

| 기기 | 상태 |
|---|---|
| KEF LSX II LT | 실기 검증 (펌웨어 2.1.86) |
| KEF LSX II, LS50 Wireless II, LS60, XIO | 같은 W2 프로토콜, 미검증 |

다른 플레이어(BluOS, WiiM/LinkPlay, Yamaha MusicCast 등)는 실기 테스트를 할 수 없어 넣지 않았습니다. 기기와는 작은 드라이버 계약으로만 통신하므로, 새 기기는 클래스 하나와 테스트로 추가할 수 있습니다. 기기를 가지고 계시면 [docs/drivers.md](docs/drivers.md)의 계약과 안전 점검 절차를 보고 PR을 보내 주세요.

## 빌드

```powershell
dotnet build windows/KefRemote.slnx -c Release
dotnet test windows/KefRemote.slnx
dotnet run --project windows/src/KefRemote.Windows
```

`RemoteForKEF.exe --background`는 창 없이 트레이로 시작합니다. 설정은 `%LOCALAPPDATA%\KefRemote\settings.json`에 저장하고, 스피커 암호는 현재 Windows 사용자 범위의 DPAPI로 보호합니다.

## 라이선스

MIT. [LICENSE](LICENSE), [THIRD-PARTY-NOTICES.md](THIRD-PARTY-NOTICES.md) 참고.
