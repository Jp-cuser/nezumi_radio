# 🚀 Nezumi Radio VPS 導入ガイド (初心者向け)

C# (.NET) のプログラムを Linux VPS (Ubuntu 等) で動かすための手順を、ステップバイステップで解説します。

---

## 1. サーバーの準備 (VPS)

推奨OS: **Ubuntu 22.04 LTS** または **24.04 LTS**

### .NET 10 (Runtime) のインストール
Botを動かすために、Microsoft公式のランタイムをインストールします。

```bash
# パッケージリストの更新
sudo apt update

# .NET 10 SDK のインストール (ビルドも行うためSDKを推奨)
sudo apt install -y dotnet-sdk-10.0
```

### Java のインストール (Lavalink用)
Lavalinkを動かすために Java 17 以降が必要です。

```bash
sudo apt install -y openjdk-17-jre-headless
```

---

## 2. Lavalink の起動

1.  適当なディレクトリを作成します。
    ```bash
    mkdir ~/lavalink && cd ~/lavalink
    ```
2.  `Lavalink.jar` と `application.yml` (設定ファイル) を配置します。
3.  起動確認:
    ```bash
    java -jar Lavalink.jar
    ```
    ※ 正常に起動したら `Ctrl+C` で一旦止めます。

---

## 3. Bot のデプロイ

### VPS上でビルドする

1.  VPSにプロジェクトファイルをアップロード（または `git clone`）します。
2.  プロジェクトディレクトリに移動します。
3.  `.env` ファイルを作成し、トークンなどを記入します。
    ```bash
    nano .env
    ```
4.  ビルドします。
    ```bash
    dotnet build -c Release
    ```

---

## 4. 6つのユニットを同時に動かす

Linuxでは PowerShell スクリプトの代わりに、シェルスクリプトを作成します。

### 起動用スクリプト (`start_bots.sh`) の作成

```bash
nano start_bots.sh
```

以下の内容を貼り付けます：
```bash
#!/bin/bash

for i in {0..5}
do
    echo "Starting Bot Unit $((i+1))..."
    # BOT_INDEX を環境変数として渡してバックグラウンド実行
    export BOT_INDEX=$i
    dotnet run --no-build -c Release &
    sleep 2 # 起動の衝突を防ぐため少し待機
done

echo "All 6 bots started in background."
```

実行権限を与えて実行します：
```bash
chmod +x start_bots.sh
./start_bots.sh
```

---

## 5. Botを24時間維持する

サーバーを閉じてもBotが止まらないようにする方法です。

**最も簡単な方法: `screen` を使う**

1.  `screen` をインストール: `sudo apt install screen`
2.  新しいセッションを作成: `screen -S nezumi`
3.  そこで `./start_bots.sh` を実行。
4.  `Ctrl + A` を押した後に `D` を押して離脱（バックグラウンドで動き続けます）。
5.  戻りたい時は: `screen -r nezumi`

---

## 💡 トラブルシューティング

- **ビルドエラーが出る**: `dotnet --version` で 10.0 以上であることを確認してください。
- **BotがVCに入らない**: Lavalinkが正しく起動しているか、`.env` の `LAVALINK_URL` が正しいか確認してください（VPS内の場合は `http://127.0.0.1:2333`）。
- **API上限が心配**: 月間50万枠であれば、今回の6台運用でも約17%程度の消費で済みますので安心してください。

---

C# は一度ビルドしてしまえば、Linux上でも非常に高速かつ安定して動作します。頑張ってください！
