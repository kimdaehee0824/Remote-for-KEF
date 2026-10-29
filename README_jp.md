<p align="center">
  <picture>
    <source media="(prefers-color-scheme: dark)" srcset="docs/images/banner-ja-dark.png">
    <img src="docs/images/banner-ja-light.png" alt="Remote for KEF" width="100%">
  </picture>
</p>

<p align="center"><a href="README.md">English</a> · <a href="README_ko.md">한국어</a> · 日本語</p>

<p align="center">
  <a href="https://github.com/kimdaehee0824/Remote-for-KEF/releases/latest/download/RemoteForKEF-win-x64.zip"><picture><source media="(prefers-color-scheme: dark)" srcset="docs/images/download-windows-dark.svg"><img src="docs/images/download-windows-light.svg" height="44" alt="Windows版をダウンロード"></picture></a>
  &nbsp;
  <a href="https://github.com/kimdaehee0824/Remote-for-KEF/releases/latest/download/RemoteForKEF-macos.zip"><picture><source media="(prefers-color-scheme: dark)" srcset="docs/images/download-macos-dark.svg"><img src="docs/images/download-macos-light.svg" height="44" alt="macOS版をダウンロード"></picture></a>
</p>

入力を切り替えたり、スピーカーの電源を入れたり、音量を少し変えたりするたびに、スマートフォンを手に取る必要はありません。**Remote for KEF**はWindowsのタスクトレイとMacのメニューバーに常駐し、ローカルネットワーク経由でKEFのワイヤレススピーカーを直接操作します。

- **電源と入力**: スピーカーの電源オン・スタンバイと、USB-C、Wi-Fi、Bluetooth、TV、Opticalの切り替え
- **正確な音量**: 0〜100を1刻みで、常にスピーカーが返した実際の値を表示
- **音量の上限**: 自分で決めた上限とKEF Connectの最大音量のうち、低いほうで止まる
- **正確な音量合わせ**: オンにすると、どの音量でも上から合わせるため、同じ数値ならいつも同じ大きさで聞こえる
- **Windowsのショートカットキー**: どこからでも音量の上げ下げとミュート、画面に音量を表示
- **スピーカーの色**: お使いのスピーカーの色でスピーカーアイコンを表示

各機能の詳しい動作は[機能の説明](docs/features_jp.md)にあります。

## 動作環境

| 項目         | Windows                   | macOS                             |
| ------------ | ------------------------- | --------------------------------- |
| OS           | Windows 10（1809）または11 | macOS 14以降                      |
| ハードウェア | x64、ARM64                | AppleシリコンとIntel（Universal） |

| スピーカー                              | 状況                         |
| --------------------------------------- | ---------------------------- |
| KEF LSX II LT                           | 実機で確認済み               |
| KEF LSX II、LS50 Wireless II、LS60、XIO | 同じプロトコル、未確認       |

画面はシステムの言語に合わせて、日本語、英語、韓国語で表示されます。

## インストール

署名・公証をしていないビルドのため、初回起動時に一度だけOSの確認が表示されます。

**Windows**

1. [Releases](https://github.com/kimdaehee0824/Remote-for-KEF/releases)から`RemoteForKEF-win-x64.zip`（ARM搭載PCは`RemoteForKEF-win-arm64.zip`）をダウンロードし、残しておくフォルダ（例: `C:\Apps\RemoteForKEF`）に展開します。
2. `RemoteForKEF.exe`を実行します。SmartScreenに「WindowsによってPCが保護されました」と表示されたら、**詳細情報 → 実行**を選びます。
3. スピーカーのIPアドレスを入力します。KEF Connectのスピーカー情報で確認できます。
4. Windows 11では新しいトレイアイコンが「^」の中に隠れます。常に表示するには、**設定 → 個人用設定 → タスク バー → その他のシステム トレイ アイコン**でオンにします。

**macOS**

1. [Releases](https://github.com/kimdaehee0824/Remote-for-KEF/releases)から`RemoteForKEF-macos.zip`をダウンロードして展開し、**Remote for KEF.app**を「アプリケーション」フォルダに移動します。
2. アプリを開きます。開発元を確認できないと表示されたら、**システム設定 → プライバシーとセキュリティ**で**このまま開く**をクリックします。
3. **ローカルネットワーク**へのアクセスを求められたら許可します。許可しないとスピーカーに接続できません。
4. メニューバーのスピーカーアイコンをクリックし、**設定**でIPアドレスを入力します。

## 使い方

トレイまたはメニューバーのアイコンをクリックすると、電源・入力・音量を確認し、その場で変更できます。Windowsでは<kbd>Ctrl</kbd>+<kbd>Alt</kbd>+<kbd>↑</kbd> / <kbd>↓</kbd>で音量を、<kbd>Ctrl</kbd>+<kbd>Alt</kbd>+<kbd>M</kbd>でミュートを切り替えます。一部のIDEのショートカットと重なるため、必要に応じて**設定 → ショートカット**で変更してください。

## 謝辞

- Robert Oberdorfer氏の[kef-lsx-volume](https://github.com/roberdorfer/kef-lsx-volume)。正確な音量合わせとWindowsの音量ステップは、このプロジェクトが公開している音量カーブの測定値をもとに作りました。測定値を公開してくださったことに感謝します。
- [homeassistant-kef](https://github.com/EvotecIT/homeassistant-kef)。KEFのネットワークプロトコルと認証方式を参考にしました。

## コントリビューション

ソースからのビルド、ドライバーの仕様、ほかのスピーカーを追加する方法は[CONTRIBUTING.md](CONTRIBUTING.md)にまとめています。

## ライセンス

Remote for KEFは[MIT License](LICENSE)のもとで公開されています。[THIRD-PARTY-NOTICES.md](THIRD-PARTY-NOTICES.md)もあわせてご確認ください。

KEFとは関係のない非公式プロジェクトです。KEFおよび製品名は各社の商標です。
