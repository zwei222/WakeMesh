# WakeMesh

個人用マルチデバイス目覚まし。ローカル専用 **.NET SDK 11.0.100** と **.NET MAUI 11（stable）** を前提にしたモノレポです（preview SDK 表記は使いません）。

## 現状サマリ

| 領域 | 状態 |
|------|------|
| ローカル .NET SDK 11.0.100（`.tools/dotnet`） | 導入済み（リポジトリには含めない） |
| MAUI / Android ターゲット `net11.0-android` | **stable 11** 前提のソースあり。実機成功は未検証 |
| `WakeMesh.Core` 次回発火計算 | **実装済み**（ユニットテストあり） |
| `WakeMesh.Api` ASP.NET Core API | **実装済み**（ローカル／テストホスト）。実 Azure 未接続 |
| `WakeMesh.Client` 共有 HTTP API クライアント | **実装済み**。実証は **Client.Tests（テストホスト実 API）** のみ |
| `WakeMesh.Mobile` ローカル目覚まし UI / スケジュール | **ソース実装あり**（AlarmManager 等）。**実機鳴動・スヌーズ結合は未検証** |
| Mobile ↔ Client ProjectReference | **接続済み**（ソース参照）。実 MS / 実 Azure 結合は未検証 |
| MS 認証コード（MSAL 配線・設定モデル） | **コードあり**。**実 Microsoft 個人アカウントログインは未検証** |
| Cloud 同期 / Cloud UI / foreground sync コード | **コード＋managed テストあり**。実クラウド経路は未検証 |
| Push コード（トークン／ペイロード／outbox 連携想定） | **コード＋`WakeMesh.Mobile.Push.Tests` あり**。**実 Firebase 配信・実機受信は未検証** |
| フォアグラウンド同期 interval **15 秒** | **設計既定値**（`CloudForegroundSyncService.DefaultSyncInterval`）。OS・省電力・アプリ状態で保証されない |
| バックグラウンド同期 | **OS 制約あり**（Doze、バックグラウンド実行制限、FCM 優先度など）。常時 15 秒周期は想定しない |
| 日本の祝日データセットの取得・配布 | 呼び出し側／ローカル実装依存（ネットワーク取得は Core 外） |
| 実 Microsoft 個人アカウント認証（本番トークン） | **未接続・未検証** |
| 実 Azure リソース（SQL / ホスティング） | **未作成・未検証** |
| 実 FCM 配信（Firebase プロジェクト運用） | **未接続・未検証**（Accept ≠ 端末 apply） |
| 実機インストール／アラーム物理鳴動 | **未検証** |
| デバイス間同期の本番経路 | **未検証** |

> 本 README は「コードとして存在する機能」と「実アカウント／実クラウド／実機で未接続なもの」を区別します。  
> Client の実証範囲は **WebApplicationFactory 経由の実 API に対する Client.Tests** に限ります。

## ソリューションとプロジェクト数（ディスク上を数え、推測しない）

リポジトリ直下で `find src tests -name '*.csproj'` した結果（最終 prep 時点）:

| 区分 | 件数 | 内容 |
|------|------|------|
| `src/**/*.csproj` | **4** | Core, Api, Client, Mobile |
| `tests/**/*.csproj` | **7** | Core.Tests, Api.Tests, Client.Tests, Mobile.Tests, Mobile.Sync.Tests, Mobile.CloudUi.Tests, **Mobile.Push.Tests** |
| **合計** | **11** | |

| ファイル | 登録内容 | 件数 |
|----------|----------|------|
| `WakeMesh.slnx` | managed 通常経路: Core / Api / Client + 上記 **テスト 7**（**Mobile アプリ本体は含めない**） | **10** projects |
| `WakeMesh.Android.slnx` | Android 一覧: Core + Client + Mobile | **3** projects |
| `global.json` | SDK **`11.0.100`** 固定（`rollForward: disable`） | — |

```text
WakeMesh.slnx                 # サーバー / Client / managed テスト 10
WakeMesh.Android.slnx         # Android 関連一覧 3
global.json                   # SDK 11.0.100
src/WakeMesh.Core/
src/WakeMesh.Api/             # 詳細: src/WakeMesh.Api/README.md
src/WakeMesh.Client/
src/WakeMesh.Mobile/          # MAUI Android（managed solution 外）
tests/WakeMesh.Core.Tests/
tests/WakeMesh.Api.Tests/
tests/WakeMesh.Client.Tests/
tests/WakeMesh.Mobile.Tests/
tests/WakeMesh.Mobile.Sync.Tests/
tests/WakeMesh.Mobile.CloudUi.Tests/
tests/WakeMesh.Mobile.Push.Tests/
scripts/verify-local.sh
SECURITY.md
```

`WakeMesh.slnx` に Mobile アプリ本体を入れない理由: Android ワークロード無しの `dotnet test` を維持するため。Push.Tests は managed solution に **登録済み**。

## 実装機能マップ（コード有無）

| 機能 | コード | 実証境界 |
|------|--------|----------|
| **Local** 次回発火・ローカルアラームモデル | Core + Mobile | ユニット／managed。実機鳴動未検証 |
| **Cloud API** デバイス／アラームグループ／desired／ACK | Api + Client | テストホスト。実 Azure 未接続 |
| **MS code** MSAL クライアント ID・Redirect・設定 | Mobile | ビルド時 GUID 配線。**実 MS ログイン未検証** |
| **Foreground sync** 前面時の desired 同期ループ | Mobile `CloudForegroundSyncService` | **15s 設計既定**。周期保証なし |
| **Push code** トークン／validator／FCM accept と apply ACK の分離 | Api push outbox + Mobile push サービス + Push.Tests | **実 Firebase／実機未検証** |

### FCM Accepted と apply ACK（別物）

| 段階 | 意味 | まだ意味しないこと |
|------|------|-------------------|
| **FCM Accepted** | プッシュ送信経路がメッセージを受理（`PushSendOutcome.Accepted` / notification `accepted`） | 端末が desired を適用したこと |
| **Device apply + ACK** | 端末が desired を取り込み、`POST /api/v1/devices/{id}/acks` で applied revision を進めたこと | FCM 再送の成功そのもの |

サーバ保存成功 ≠ ターゲット端末適用。適用確認は **desired 取得 → ローカル apply → applied revision コミット → ACK** のみです。

### フォアグラウンド 15 秒 / バックグラウンド OS 制約

- 前面同期の既定間隔は **15 秒**（設計値）。タイマー遅延・ライフサイクル・メーカースキナーにより伸びる／止まる。
- 背面では OS が実行・ネット・FCM 優先度を制限する。**15 秒周期の背面保証はない**。
- アラーム受信側の短い実行枠（OS の grace）と、Cloud foreground 15 秒 interval は別概念。

### Zero GUID = 未設定 → ログイン不可

- Mobile 既定のコンパイル時 `WakeMeshAuthClientId` は **zero GUID** プレースホルダ。
- zero / empty GUID の Client ID は **未設定** として扱う（MSAL 構成・サインイン不可、本番 API 呼び出し前提を満たさない）。
- 実運用ビルドでは **nonzero の小文字 canonical GUID** をローカルプロパティで渡す（値は Git に置かない）。

## 要件

- 公式 **.NET SDK 11.0.100** をリポジトリ相対の `.tools/dotnet` に導入
- マシン全体の共有 `dotnet`（別バージョン）に依存しない
- Android ビルド時のみ、ローカル Android SDK（例: `.tools/android-sdk`）と MAUI/Android ワークロード
- JDK が必要なら `.tools/jdk` または呼び出し側が既に設定した `JAVA_HOME`（**ユーザ固有パスをスクリプトに埋め込まない**）

### ローカル SDK 導入（公式）

```bash
mkdir -p .tools/install
curl -fsSL -o .tools/install/dotnet-install.sh https://dot.net/v1/dotnet-install.sh
bash .tools/install/dotnet-install.sh --channel 11.0 --version 11.0.100 --install-dir "$(pwd)/.tools/dotnet"
```

`.tools/` は gitignore 対象です。

### 環境変数（リポジトリ相対）

```bash
export DOTNET_ROOT="$(pwd)/.tools/dotnet"
export PATH="$DOTNET_ROOT:$PATH"
export DOTNET_CLI_HOME="$(pwd)/.tools/cli-home"
export DOTNET_CLI_TELEMETRY_OPTOUT=1
# Android ビルド時の例（どちらも任意・リポジトリ相対）:
# export ANDROID_HOME="$(pwd)/.tools/android-sdk"
# export ANDROID_SDK_ROOT="$ANDROID_HOME"
# export JAVA_HOME="$(pwd)/.tools/jdk"
```

```bash
dotnet --version   # 期待: 11.0.100
```

### restore / test（通常 solution・Android 不要）

追加の `-p:WakeMeshAuthClientId=...` は managed テストでは不要です。Mobile.*Tests はテスト専用の架空 nonzero GUID を既定生成します（アプリ本体の zero 既定とは別）。**実 Microsoft ログイン・実 Azure・実 FCM・実機結合は含みません。**

```bash
dotnet restore WakeMesh.slnx
dotnet test WakeMesh.slnx --no-restore
```

または:

```bash
./scripts/verify-local.sh test
./scripts/verify-local.sh list
./scripts/verify-local.sh checkignore
```

### Android ビルドと運用値の渡し方（ローカル property）

solution 全体へ `-f net11.0-android` を渡さないこと（`WakeMesh.Core` の `net11.0` ProjectReference 解決を崩すため）。**Mobile.csproj を明示**します。

実運用に必要な値は **MSBuild プロパティ／gitignore 済みローカルファイル／環境変数** で渡し、リポジトリに書き込みません。

```bash
# 公開クライアント ID（小文字 GUID）。zero のままではログイン不可。
# google-services.json はリポジトリ外の絶対パスを指定（コミットしない）。
dotnet restore src/WakeMesh.Mobile/WakeMesh.Mobile.csproj
dotnet build src/WakeMesh.Mobile/WakeMesh.Mobile.csproj -f net11.0-android --no-restore \
  -p:WakeMeshAuthClientId=11111111-1111-4111-8111-111111111111 \
  -p:GoogleServicesJsonPath=/absolute/path/outside/repo/google-services.json
```

Release 署名（**名前のみ・値は秘密ストア**）の例:

```bash
dotnet build src/WakeMesh.Mobile/WakeMesh.Mobile.csproj -f net11.0-android -c Release --no-restore \
  -p:WakeMeshAuthClientId=<nonzero-lowercase-guid> \
  -p:GoogleServicesJsonPath=/absolute/path/outside/repo/google-services.json \
  -p:AndroidKeyStore=true \
  -p:AndroidSigningKeyStore=/absolute/path/outside/repo/upload.keystore \
  -p:AndroidSigningKeyAlias=<alias> \
  -p:AndroidSigningStorePass=<from-secret-store> \
  -p:AndroidSigningKeyPass=<from-secret-store>
```

または（SDK／JDK は `.tools/*` 優先、ユーザホーム直書きなし）:

```bash
./scripts/verify-local.sh android-build
```

APK/AAB・keystore・`google-services.json`・service account JSON はコミットしません（`.gitignore` / `SECURITY.md`）。

### API 側 FCM 資格情報（サーバのみ）

| 設定名 | 意味 |
|--------|------|
| `WakeMesh:Fcm:Configured` | 機能フラグ（true でも資格情報が無ければ送信不可） |
| `WakeMesh:Fcm:CredentialsPath` | **サーバ専用** service account JSON の絶対パス（設定値に秘密 JSON を埋め込まない） |

ADC（環境の Application Default Credentials）も可。パスも JSON も Git 対象外。

## HTTP 表面（名称転記: `src/WakeMesh.Api/README.md`）

運用個人値なし。詳細契約は Api README を正とする。

| Method | Path | Auth |
|--------|------|------|
| GET | `/health` | anonymous |
| GET/POST | `/api/v1/devices` | owner Bearer |
| GET | `/api/v1/devices/{id}` | owner Bearer |
| POST | `/api/v1/devices/{id}/retire` | owner Bearer |
| POST | `/api/v1/devices/{id}/revoke` | owner Bearer |
| GET | `/api/v1/devices/{id}/desired` | owner Bearer + `X-Device-Credential` |
| POST | `/api/v1/devices/{id}/acks` | owner Bearer + `X-Device-Credential` |
| GET/POST | `/api/v1/alarm-groups` | owner Bearer |
| PUT/DELETE | `/api/v1/alarm-groups/{id}` | owner Bearer + `If-Match` |

## 設定名（個人情報・実シークレットなし）

| 設定名 | 意味 |
|--------|------|
| `ConnectionStrings:DefaultConnection` | DB 接続文字列 |
| `WakeMesh:Database:Provider` | `SqlServer`（既定）または `Sqlite` |
| `WakeMesh:Auth:Issuer` | トークン issuer |
| `WakeMesh:Auth:Audience` | API クライアント ID（`aud`） |
| `WakeMesh:Auth:RequiredScope` | 要求スコープ（`scp`） |
| `WakeMesh:Auth:AllowedAzp` | 許可する公開クライアント ID（`azp` のみ。`appid` 代替不可） |
| `WakeMesh:Auth:AllowedOwnerOids` | オーナー `oid` 許可リスト |
| `WakeMesh:Auth:MetadataAddress` | OIDC メタデータ URL（**HTTPS 必須**） |
| `WakeMesh:Fcm:Configured` | FCM 機能フラグ |
| `WakeMesh:Fcm:CredentialsPath` | サーバ資格情報ファイルパス（任意） |

Issuer / Metadata の **公開定数例**（Microsoft 個人用アカウント consumers テナント GUID `9188040d-6c67-4c5b-b112-36a304b66dad`）:

```text
https://login.microsoftonline.com/9188040d-6c67-4c5b-b112-36a304b66dad/v2.0
https://login.microsoftonline.com/9188040d-6c67-4c5b-b112-36a304b66dad/v2.0/.well-known/openid-configuration
```

ローカル上書きは `appsettings.*.local.json` / 環境変数 / ユーザーシークレットなど **gitignore 対象** に置くこと。

### 本番認証の前提

- 署名鍵は **OIDC MetadataAddress（HTTPS）のみ**。本番設定にテスト用静的 PEM 経路はない。
- Azure 上の公開エンドポイントは **HTTPS のみ**前提。
- 自動テストは `WebApplicationFactory` の `PostConfigure<JwtBearerOptions>` で署名鍵と静的 metadata fixture を注入（設定キー経由ではない）。

## Core の実装範囲（抜粋）

`NextFireCalculator.GetNextFire` は純関数です（有効/無効、週次・一回、タイムゾーン、端末別ローカル時刻、祝日除外フラグ、除外日、検索上限など）。祝日集合のネットワーク取得は Core の責務外です。

## ドキュメント／テスト用の架空値

- デバイス表示名例: `device-alpha`, `device-beta`
- アラーム ID 例: `alarm-sample-1`
- テスト用 nonzero GUID 例: `11111111-1111-4111-8111-111111111111`
- 実アカウント・実トークン・電話番号・住所・端末固有 ID は含めません

## 秘密情報ポリシー

Git に入れないもの:

- `.tools/`（dotnet / android-sdk / jdk / cli-home）
- `.evidence/`、ログ、ローカル DB
- `*.apk` / `*.aab` / keystore / `*.jks` / `*.pfx` / `*.pem`
- `google-services.json`、service-account JSON、`credentials.json`
- 実クライアントシークレット、`.env`、署名パスワード

**テストソースは隠さない**: リポジトリ全体の `*secret*` / `*credential*` 一括除外は使わず、成果物・ファイル名パターンに限定します（`NoSecretLoggingTests.cs` 等は追跡可能）。詳細は `.gitignore` と `SECURITY.md`。

## 検証スクリプト

`scripts/verify-local.sh` はリポジトリ相対の `.tools/dotnet` を必須とし、Android/JDK は `.tools/android-sdk`・`.tools/jdk` または **既存の環境変数**のみ（ポータブル。`/home/...` 固定なし）。

| サブコマンド | 内容 |
|-------------|------|
| `list` | SDK 版、両 solution の `dotnet sln list`、ディスク上 csproj 件数 |
| `test` | `WakeMesh.slnx` の restore + test（Android 不要。Push.Tests 含む 7 テストプロジェクト） |
| `android-build` | `WakeMesh.Mobile.csproj` のみ `-f net11.0-android` |
| `checkignore` | 機微パスが ignore、テスト／ソースが非 ignore であることを確認 |

本 README は **未 MS 実ログイン / 未 Azure / 未 Firebase 実配信 / 未実機物理鳴動** を維持します。FCM Accepted と apply ACK は別段階です。フォアグラウンド 15 秒は設計既定であり周期保証ではありません。zero GUID ではログインできません。
