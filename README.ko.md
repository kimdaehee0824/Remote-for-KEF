# Remote for KEF

**A Very Revolutionary Way to Control Your KEF Speakers.**

Windows와 macOS용 KEF 무선 스피커 비공식 데스크톱 리모컨입니다. 다른 네트워크 플레이어도 붙일 수 있는 구조로 만들었습니다.

[English](README.md)

> KEF와 관계없는 비공식 프로젝트입니다. "KEF"와 제품명은 각 소유자의 상표이며 호환성 설명 목적으로만 사용합니다.

## 왜 만들었나

KEF LSX II LT를 PC(USB-C)와 Spotify 사이에서 바꾸거나, 켜거나, 음량을 조금 바꾸려고 매번 휴대폰을 집어 들 필요는 없습니다. 이 앱은 트레이(Windows)나 메뉴 막대(macOS)에 있다가 로컬 네트워크로 스피커를 직접 조작합니다. 한 번 눌러 상태를 보고, 한 번 더 눌러 바꿉니다.

Windows나 Mac의 시스템 음량과는 일부러 동기화하지 않습니다. 스피커 자체 음량을 직접 조절합니다.

## 기능

- **전원**: 스피커 이름 옆 스위치로 대기 상태로 끄거나 마지막 입력으로 켭니다. 대기 중에 입력을 고르면 그 입력으로 켜집니다.
- **상태 한눈에 보기**: 전원은 스위치, 입력은 선택 표시, 음량은 숫자가 보여 줍니다. 이름 아래 줄은 평소에는 모델명이고, 대기·전환 중·연결 끊김처럼 평소와 다를 때만 바뀝니다.
- **입력 전환**(LSX II LT 기준 USB-C / PC, Wi-Fi, Bluetooth, TV, Optical): 음소거 → 전환 → 확인 순서로 진행하고, 새 입력의 음량이 이전보다 높지 않을 때만 음소거를 풉니다.
- **0~100, 1 단위의 정확한 음량**. 항상 스피커가 돌려준 실제 값을 표시합니다.
- **음량 상한**: 앱 상한(기본 30)과 KEF Connect에서 정한 최대 음량 중 낮은 값을 씁니다. 슬라이더에 상한 눈금과 출처(앱 설정 / KEF 기기 설정)를 표시합니다. 올리기는 거기서 멈추고, 내리기는 언제나 됩니다.
- **정확한 음량 맞추기**(기본 꺼짐): KEF 스피커는 같은 숫자라도 아래에서 올라올 때 더 작게 들리는 구간이 있습니다. 켜면 올릴 때 목표보다 조금 높은 값을 잠깐 거쳐(상한은 넘지 않음) 위에서 내려와 맞춘 것과 같은 크기로 들리게 합니다. 앱의 (?) / (i) 버튼에 설명이 있습니다.
- **Windows**: WinUI 3 트레이 플라이아웃, 음량 올리기/내리기·음소거 전역 단축키(기본 Ctrl+Alt+↑/↓/M, 변경 가능)와 화면 오른쪽 아래 음량 표시, 설정 창.
- **macOS**: 시스템 슬라이더와 스위치를 쓴 메뉴 막대 패널, 포스 터치 트랙패드에서 끌 때 5마다 햅틱, macOS 26 이후 Liquid Glass 버튼, 탭으로 나눈 설정 창.
- **안전 기본값**: 시작, 재연결, 상태 확인 때는 아무것도 쓰지 않고, 쓴 값은 모두 다시 읽어 확인하며, 네트워크가 끊겼다 돌아와도 이전 명령을 다시 보내지 않습니다. [docs/drivers.md](docs/drivers.md) 참고.

## 내려받기와 처음 실행

유료 인증서로 서명하지 않은 빌드라서, 처음 실행할 때 운영체제가 한 번 확인을 요구합니다.

**Windows 10(1809) 이상 또는 Windows 11**, `RemoteForKEF-win-x64.zip`(ARM PC는 `-arm64`):

1. 계속 둘 폴더에 압축을 풉니다. 예: `C:\Apps\RemoteForKEF`. "Windows 시작 시 실행"이 이 폴더를 가리키므로 다운로드 폴더는 피하세요.
2. `RemoteForKEF.exe`를 실행합니다. SmartScreen이 "Windows의 PC 보호" 창을 띄우면 **추가 정보 → 실행**을 누릅니다.
3. 열리는 설정 창에 스피커 IP 주소를 입력합니다. KEF Connect의 스피커 정보에서 확인할 수 있습니다.
4. Windows 11은 새 트레이 아이콘을 숨김(^) 메뉴에 넣습니다. 늘 보이게 하려면 **설정 → 개인 설정 → 작업 표시줄 → 기타 시스템 트레이 아이콘 → Remote for KEF**를 켭니다.

설치 프로그램이나 .NET 설치는 필요 없습니다. 설정은 `%LOCALAPPDATA%\KefRemote`에 저장하고, 스피커 암호는 현재 Windows 사용자 범위의 DPAPI로 보호합니다.

**macOS 14 이상**, `RemoteForKEF-macos.zip`(Apple 실리콘, Intel 공용):

1. 압축을 풀고 **Remote for KEF.app**을 응용 프로그램 폴더로 옮깁니다.
2. 앱을 엽니다. 개발자를 확인할 수 없다는 창이 뜨면 **시스템 설정 → 개인정보 보호 및 보안**에서 아래로 내려 Remote for KEF 옆의 **그래도 열기**를 누릅니다. macOS 14에서는 앱을 Control-클릭한 뒤 **열기**를 골라도 됩니다.
3. **로컬 네트워크** 접근을 물으면 허용합니다. 허용하지 않으면 스피커에 연결할 수 없습니다.
4. 메뉴 막대의 스피커 아이콘을 누르고 **설정**에서 IP 주소를 입력합니다.

## Windows 단축키

앱이 켜져 있는 동안 시스템 전체에 등록되므로 앞에 있는 앱보다 먼저 받습니다. 기본값은 다음과 겹칩니다.

| 조합 | 같은 조합을 쓰는 곳 |
|---|---|
| Ctrl+Alt+↑ / ↓ | VS Code(위·아래에 커서 추가), JetBrains IDE(이전·다음 검색 결과), Visual Studio(Ctrl+Alt+↓, 열린 파일 목록), 단축키를 켜 둔 Intel 그래픽 드라이버의 화면 회전 |
| Ctrl+Alt+M | JetBrains IDE(메서드 추출), Word(메모 삽입) |

이런 프로그램을 자주 쓰면 **설정 → 단축키**에서 다른 조합으로 바꾸세요. 다른 앱이 먼저 등록한 조합은 그 화면에 등록하지 못했다고 표시됩니다.

## 지원 기기

| 기기 | 상태 |
|---|---|
| KEF LSX II LT | 음량, 음소거, 입력 전환은 실기 검증(펌웨어 2.1.86). 전원 제어는 같은 프로토콜을 따르며 실기 검증 예정 |
| KEF LSX II, LS50 Wireless II, LS60, XIO | 같은 W2 프로토콜, 미검증 |

다른 플레이어(BluOS, WiiM/LinkPlay, Yamaha MusicCast 등)는 실기 테스트를 할 수 없어 넣지 않았습니다. 기기와는 작은 드라이버 계약으로만 통신하므로, 새 기기는 클래스 하나와 테스트로 추가할 수 있습니다. 기기를 가지고 계시면 [docs/drivers.md](docs/drivers.md)의 계약과 안전 점검 절차를 보고 PR을 보내 주세요.

## 빌드

**Windows**, .NET 10 SDK(Windows App SDK는 NuGet으로 받습니다):

```powershell
dotnet test windows/tests/KefRemote.Tests/KefRemote.Tests.csproj
dotnet publish windows/src/KefRemote.App/KefRemote.App.csproj -c Release -r win-x64 -p:Platform=x64 -o dist/win-x64
dist/win-x64/RemoteForKEF.exe
```

UI는 XAML 페이지 없이 C#으로 만들었고(스타일만 `Styles.xaml`), 그래서 Linux와 macOS에서도 컴파일 확인은 됩니다. 실행은 Windows에서만 됩니다.

`RemoteForKEF.exe --background`는 아무것도 열지 않고 트레이로 시작합니다("Windows 시작 시 실행"이 사용). `RemoteForKEF.exe --self-test <폴더>`는 가상 스피커로 모든 화면 상태를 돌며 스크린샷과 `report.txt`를 저장하고, 전부 통과했을 때만 0으로 끝납니다. CI가 푸시마다 실행합니다.

**macOS**: [macos/README.md](macos/README.md) 참고.

## 저장소 구성

```text
windows/   C# .NET 10: Core(컨트롤러, 드라이버 계약), Drivers, Desktop(공용 UI 로직), App(WinUI 3), 테스트
macos/     Swift 패키지: 코어, KEF 드라이버, SwiftUI 메뉴 막대 앱
docs/      프로토콜 메모와 드라이버 계약
spec/      두 앱이 함께 쓰는 언어 중립 테스트 벡터
```

## 라이선스

MIT. [LICENSE](LICENSE), [THIRD-PARTY-NOTICES.md](THIRD-PARTY-NOTICES.md) 참고.
