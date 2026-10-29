<p align="center">
  <picture>
    <source media="(prefers-color-scheme: dark)" srcset="docs/images/banner-ko-dark.png">
    <img src="docs/images/banner-ko-light.png" alt="Remote for KEF" width="100%">
  </picture>
</p>

<p align="center"><a href="README.md">English</a> · 한국어 · <a href="README_jp.md">日本語</a></p>

<p align="center">
  <a href="https://github.com/kimdaehee0824/Remote-for-KEF/releases/latest/download/RemoteForKEF-win-x64.zip"><picture><source media="(prefers-color-scheme: dark)" srcset="docs/images/download-windows-dark.svg"><img src="docs/images/download-windows-light.svg" height="44" alt="Windows용 다운로드"></picture></a>
  &nbsp;
  <a href="https://github.com/kimdaehee0824/Remote-for-KEF/releases/latest/download/RemoteForKEF-macos.zip"><picture><source media="(prefers-color-scheme: dark)" srcset="docs/images/download-macos-dark.svg"><img src="docs/images/download-macos-light.svg" height="44" alt="macOS용 다운로드"></picture></a>
</p>

입력을 바꾸거나, 스피커를 켜거나, 음량을 조금 바꾸려고 매번 휴대폰을 집어 들 필요는 없습니다. **Remote for KEF**는 Windows 트레이와 Mac 메뉴 막대에 있다가 로컬 네트워크로 KEF 무선 스피커를 직접 조작합니다.

- **전원과 입력**: 스피커를 켜거나 대기로 두고, USB-C, Wi-Fi, Bluetooth, TV, Optical 사이를 전환
- **정확한 음량**: 0~100을 1 단위로, 항상 스피커가 돌려준 실제 값을 표시
- **음량 상한**: 직접 정한 상한과 KEF Connect의 최대 음량 중 낮은 쪽에서 멈춤
- **정확한 음량 맞추기**: 켜 두면 어느 음량이든 위에서 내려와 맞춰, 같은 숫자는 늘 같은 크기로 들림
- **Windows 단축키**: 어디서든 음량 올리기·내리기·음소거, 화면에 음량 표시
- **스피커 색상**: 내 스피커 색으로 스피커 아이콘을 그림

기능마다 자세한 동작은 [기능 설명](docs/features_ko.md)에 있습니다.

## 동작 환경

| 항목   | Windows                  | macOS                              |
| ------ | ------------------------ | ---------------------------------- |
| OS     | Windows 10(1809) 또는 11 | macOS 14 이상                      |
| 하드웨어 | x64, ARM64             | Apple Silicon과 Intel (Universal)  |

| 스피커                                   | 상태                    |
| ---------------------------------------- | ----------------------- |
| KEF LSX II LT                            | 실기 확인               |
| KEF LSX II, LS50 Wireless II, LS60, XIO  | 같은 프로토콜, 아직 미확인 |

화면은 시스템 언어에 맞춰 한국어, 영어, 일본어로 표시됩니다.

## 설치

유료 인증서로 서명·공증하지 않은 빌드라서, 처음 실행할 때 운영체제가 한 번 확인을 요구합니다.

**Windows**

1. [Releases](https://github.com/kimdaehee0824/Remote-for-KEF/releases)에서 `RemoteForKEF-win-x64.zip`(ARM PC는 `RemoteForKEF-win-arm64.zip`)을 내려받아 계속 둘 폴더에 풉니다. 예: `C:\Apps\RemoteForKEF`
2. `RemoteForKEF.exe`를 실행합니다. SmartScreen이 "Windows의 PC 보호" 창을 띄우면 **추가 정보 → 실행**을 누릅니다.
3. 스피커 IP 주소를 입력합니다. KEF Connect의 스피커 정보에서 확인할 수 있습니다.
4. Windows 11은 새 트레이 아이콘을 **^** 안에 숨깁니다. 늘 보이게 하려면 **설정 → 개인 설정 → 작업 표시줄 → 기타 시스템 트레이 아이콘**에서 켜 주세요.

**macOS**

1. [Releases](https://github.com/kimdaehee0824/Remote-for-KEF/releases)에서 `RemoteForKEF-macos.zip`을 내려받아 풀고, **Remote for KEF.app**을 응용 프로그램 폴더로 옮깁니다.
2. 앱을 엽니다. 개발자를 확인할 수 없다는 창이 뜨면 **시스템 설정 → 개인정보 보호 및 보안**에서 **그래도 열기**를 누릅니다.
3. **로컬 네트워크** 접근을 물으면 허용합니다. 허용하지 않으면 스피커에 연결할 수 없습니다.
4. 메뉴 막대의 스피커 아이콘을 누르고 **설정**에서 IP 주소를 입력합니다.

## 사용법

트레이나 메뉴 막대 아이콘을 누르면 전원, 입력, 음량을 보고 그 자리에서 바꿀 수 있습니다. Windows에서는 <kbd>Ctrl</kbd>+<kbd>Alt</kbd>+<kbd>↑</kbd> / <kbd>↓</kbd>로 음량을, <kbd>Ctrl</kbd>+<kbd>Alt</kbd>+<kbd>M</kbd>으로 음소거를 바꿉니다. 일부 IDE 단축키와 겹치므로 필요하면 **설정 → 단축키**에서 바꾸세요.

## 감사의 말

- Robert Oberdorfer의 [kef-lsx-volume](https://github.com/roberdorfer/kef-lsx-volume). 정확한 음량 맞추기와 Windows의 음량 단계는 이 프로젝트가 공개한 음량 곡선 측정값을 바탕으로 만들었습니다. 측정값을 공유해 주셔서 감사합니다.
- [homeassistant-kef](https://github.com/EvotecIT/homeassistant-kef). KEF 네트워크 프로토콜과 인증 방식을 참고했습니다.

## 기여하기

소스 빌드, 드라이버 계약, 다른 스피커를 추가하는 방법은 [CONTRIBUTING.md](CONTRIBUTING.md)에 정리되어 있습니다.

## 라이선스

Remote for KEF는 [MIT License](LICENSE)로 배포됩니다. [THIRD-PARTY-NOTICES.md](THIRD-PARTY-NOTICES.md)도 함께 확인해 주세요.

KEF와 관계없는 비공식 프로젝트입니다. KEF와 제품명은 각 소유자의 상표입니다.
