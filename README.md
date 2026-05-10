# 🐭 Nezumi Radio - Unified Core v2.0

24時間365日、自律して動き続ける究極のマルチユニット・ラジオシステム。  
6台のBotが1つの心臓（統合コア）を共有し、メモリ上で直接連携することで、これまでにない安定性と超高速なレスポンスを実現しました。

---

## 🌟 特徴的な機能

### 1. 統合コア・アーキテクチャ
6台のBotを1つのプロセス内で管理します。従来のファイルベース通信を完全に排除し、メモリ上でのダイレクトなオーケストレーションにより、コマンド反映のラグをゼロにしました。

### 2. 完全自律動作 (Autonomous) & 永続化
`radio_system_state.json` により、放送状態を常にバックアップ。VPSの再起動やプログラムの更新後も、以前放送していたVCへ自動的に戻り、何事もなかったかのように放送を再開します。

### 3. シームレス・トランジション
ジャンル切り替えの15分前（毎時45分）から、バックグラウンドで次時間の選曲とロードを開始。毎時0分になった瞬間、一瞬の途切れもなく新ジャンルのプレイリストへと移行します。

### 4. インテリジェント・プレイリスト
100曲の大容量プレイリストを構築。1曲目を即座にストリーミング開始しつつ、残りの99曲は裏側で並列ロードするため、リスナーを待たせることがありません。

### 5. 仮想再生 (Virtual Queue) システム
VCにリスナーがいない間も、サーバー内部で音楽を「流し」続けます。誰かがVCに入った瞬間、**「今まさにその時間帯で流れているべき箇所」**から同期して再生がスタートします。

---

## 📅 放送スケジュール (ジャンル配分)

6台のBotは2台ずつ3つのグループに分かれ、それぞれ異なる雰囲気の音楽を担当します。

### グループ構成
- **グループ1 (ユニット01-02)**: エレクトロニック / ハウス系 (Dance & High Energy)
- **グループ2 (ユニット03-04)**: ヒップホップ / ロック / ポップ系 (Urban & Groove)
- **グループ3 (ユニット05-06)**: アンビエント / ジャズ / ローファイ系 (Chill & Relax)

### 🕒 24時間番組表 (ジャンル対応表)

| 時間 | グループ1 (Unit 1-2) | グループ2 (Unit 3-4) | グループ3 (Unit 5-6) |
|:---:|:---|:---|:---|
| **00:00** | Deep House (ディープハウス) | Trap (トラップ) | Ambient (アンビエント) |
| **01:00** | Tech House (テックハウス) | Jersey Club (ジャージークラブ) | Vaporwave (ヴェイパーウェイヴ) |
| **02:00** | Techno (テクノ) | Moombahton (ムーンバートン) | Classical (クラシック) |
| **03:00** | Jungle (ジャングル) | Dancehall (ダンスホール) | Devotional (デヴォーショナル) |
| **04:00** | Drum & Bass (ドラムンベース) | Glitch Hop (グリッチホップ) | Ambient (アンビエント) |
| **05:00** | Progressive House (プログレハウス) | Pop (ポップ) | Acoustic (アコースティック) |
| **06:00** | Electro (エレクトロ) | Funk (ファンク) | Jazz (ジャズ) |
| **07:00** | Future House (フューチャーハウス) | R&B/Soul (R&B/ソウル) | Lo-Fi (ローファイ) |
| **08:00** | House (ハウス) | Pop (ポップ) | Acoustic (アコースティック) |
| **09:00** | Tropical House (トロピカルハウス) | Rock (ロック) | World (ワールド) |
| **10:00** | Disco (ディスコ) | Alternative (オルタナティブ) | Reggae (レゲエ) |
| **11:00** | Future Bass (フューチャーベース) | Punk (パンク) | Latin (ラテン) |
| **12:00** | House (ハウス) | Rock (ロック) | Folk (フォーク) |
| **13:00** | Progressive House (プログレハウス) | Alternative (オルタナティブ) | Country (カントリー) |
| **14:00** | Tropical House (トロピカルハウス) | Punk (パンク) | World (ワールド) |
| **15:00** | Future Bass (フューチャーベース) | Rock (ロック) | Reggae (レゲエ) |
| **16:00** | Future House (フューチャーハウス) | Alternative (オルタナティブ) | Latin (ラテン) |
| **17:00** | Trance (トランス) | Metal (メタル) | Blues (ブルース) |
| **18:00** | Hardstyle (ハードスタイル) | Hyperpop (ハイパーポップ) | Downtempo (ダウンテンポ) |
| **19:00** | Electro (エレクトロ) | Metal (メタル) | Jazz (ジャズ) |
| **20:00** | Dubstep (ダブステップ) | Hip-Hop/Rap (ヒップホップ) | Lo-Fi (ローファイ) |
| **21:00** | Trap (トラップ) | Hip-Hop/Rap (ヒップホップ) | Vaporwave (ヴェイパーウェイヴ) |
| **22:00** | Techno (テクノ) | R&B/Soul (R&B/ソウル) | Ambient (アンビエント) |
| **23:00** | Deep House (ディープハウス) | Trap (トラップ) | Lo-Fi (ローファイ) |

※ すべての楽曲は Audius API を通じて世界中の最新トレンドから自動選曲されます。

---

## 🛠 セットアップ & 運用

### 1. 環境変数の設定 (`.env`)
```env
BOT_TOKEN_0=...  # ユニット01 (司令塔)
BOT_TOKEN_1=...  # ユニット02
BOT_TOKEN_2=...  # ユニット03
BOT_TOKEN_3=...  # ユニット04
BOT_TOKEN_4=...  # ユニット05
BOT_TOKEN_5=...  # ユニット06
LAVALINK_URL=http://localhost:2333
LAVALINK_PASSWORD=youshallnotpass
```

### 2. 運用コマンド
PM2を使用してプロセスを管理します。

```bash
# 起動
pm2 start ecosystem.config.js

# ログのリアルタイム監視 (重要)
pm2 logs nezumi-radio-core

# 稼働状況の確認
pm2 monit

# 停止
pm2 stop nezumi-radio-core
```

### 3. トラブルシューティング
**「BadImageFormatException」や「ビルドエラー」が発生した場合**  
以下の手順で古いバイナリを完全にクリーンにしてからビルドし直してください。
```bash
pm2 stop all
rm -rf bin/ obj/
dotnet clean
dotnet build -c Release
pm2 start ecosystem.config.js
```

---

## 📂 コード構造 (Developer Guide)

- **`RadioSystem`**: システム全体の司令塔。6つのBotクライアントを起動し、状態保存とメインループを統括します。
- **`BotUnit`**: 各Botの個体データ。Discord接続、Lavalink接続、仮想キューの状態を保持します。
- **`TickUnitAsync`**: 毎秒実行されるBotの「脳」。ジャンル選曲、VC接続判断、仮想再生の同期を行います。
- **`AudiusApiService`**: 世界中のAudiusノードから最適なサーバーを選び、音楽ストリームURLを取得します。

---
Developed by Antigravity x USER
