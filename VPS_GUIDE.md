# VPSでの運用ガイド (Linux / Ubuntu / Debian)

「ねずみラジオ」をVPS（Ubuntu等のLinux）で24時間安定稼働させるための手順です。

## 1. サーバーの準備 (前提条件)
- **OS**: Ubuntu 22.04 LTS / 24.04 LTS 推奨
- **Java**: Lavalink用 (Java 17以上)
- **.NET SDK**: Bot用 (.NET 10.0)

### .NET 10.0 のインストール
```bash
# Microsoftのリポジトリを追加してインストール
sudo apt-get update && sudo apt-get install -y dotnet-sdk-10.0
```

## 2. Lavalinkのセットアップ
VPS上でLavalinkサーバーを起動します。

1.  適当なディレクトリを作成: `mkdir ~/lavalink && cd ~/lavalink`
2.  `Lavalink.jar` をダウンロード
3.  `application.yml` を作成し、**Audiusプラグイン**の設定を記述。
4.  起動確認: `java -jar Lavalink.jar`

## 3. Botのデプロイ
1.  ローカルで開発したソースコード一式をVPSにアップロードします。
2.  `.env` ファイルをVPS上のディレクトリに作成し、トークン等を設定します。
3.  ビルドを行います:
    ```bash
    cd ~/nezumi_radio
    dotnet build -c Release
    ```

## 4. PM2による6ユニットの同時管理 (推奨)
Linuxで複数のBotプロセスを管理するには、`pm2` が非常に便利です。

### PM2のインストール
```bash
sudo apt install npm -y
sudo npm install pm2 -g
```

### 起動用エコシステムファイル (`ecosystem.config.js`) の作成
プロジェクトのルートディレクトリに以下の内容で `ecosystem.config.js` を作成します。

```javascript
module.exports = {
  apps: [0, 1, 2, 3, 4, 5].map(i => ({
    name: `nezumi-radio-unit-${i + 1}`,
    script: "dotnet",
    // runコマンドではなく、ビルドされたdllを直接実行するように変更
    args: `bin/Release/net10.0/NezumiRadio.dll`,
    env: {
      BOT_INDEX: i,
      DOTNET_ENVIRONMENT: "Production"
    },
    restart_delay: 5000
  }))
}

```

### 6台一括起動
```bash
pm2 start ecosystem.config.js
```

### 状態確認・ログ
```bash
pm2 status          # 全Botの稼働状況を確認
pm2 logs            # リアルタイムログを表示
pm2 save            # サーバー再起動時に自動復旧するように設定
pm2 startup         # OS起動時の自動起動設定コマンドを表示（指示に従って実行）
```

## 5. 注意事項
- **ポート開放**: Lavalinkが使用するポート（デフォルト2333）が外部からアクセスされないよう、ファイアウォール（ufw等）で適切に保護してください（Botと同じサーバー内で動かす場合は `localhost` のみ許可）。
- **メモリ**: 6台のBotプロセスを動かすため、最低でも 1GB〜2GB 程度のRAMを搭載したVPSを推奨します。
- ** recoveryファイル**: Linuxでも `recovery_X.json` が作成されるため、再起動時の自動復旧が有効に機能します。
