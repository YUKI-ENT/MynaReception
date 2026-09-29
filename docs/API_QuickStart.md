# JSON APIを試す

現在のAPIはSMBのファイル要求／応答です。HTTPサーバーは使用しません。

フォルダー名は **`request` / `response`** です（`req` / `res` という略称のフォルダーは監視しません）。iCallManagerを起動すると設定されたBridgeDirectoryの下に作成されます。

初期設定の場所:

```text
%LOCALAPPDATA%\iCallManager\Bridge\request\
%LOCALAPPDATA%\iCallManager\Bridge\response\
```

同じPCであればこの場所を直接使用できます。別PCから使う場合は、Bridgeフォルダーを共有し、例として `\\iCallPC\iCallBridge` を指定します。独自の配置を設定している場合は `%LOCALAPPDATA%\iCallManager\settings.json` の `bridgeDirectory` を確認してください。

保存先はアプリの **「設定」→「API連携フォルダー」** から変更できます。パスを入力するか「参照…」でこのPC上のフォルダーを選び、「保存」を押してください。指定先に `request` と `response` が作成され、**アプリを終了して起動し直すと反映**されます。設定ファイル自体の場所は変わりません。旧フォルダーの要求・応答は移動しません。共有を利用する場合は新しいフォルダーの共有設定と接続元のパスも合わせて変更してください。

## 患者IDから順番番号を取得

`request/test-find-001.json` に以下を置きます。例は架空患者です。患者IDは先頭のゼロを保持するため文字列で指定します。

```json
{
  "requestId": "test-find-001",
  "action": "find",
  "patientId": "00011"
}
```

約1秒間隔で受信し、画面の読取が終わると `response/test-find-001.json` に返します。応答の主要項目は以下のとおりです。

```json
{
  "requestId": "test-find-001",
  "success": true,
  "code": "found",
  "patientId": "00011",
  "receptionNo": "102",
  "patientName": "テスト　患者"
}
```

実際の応答には `message`、`completedAt`、`requestFingerprint` も含まれます。`receptionNo` はiCallの「受付番号」です。「待ち順」（現在何番目に待っているか）ではありません。患者がいないときは `success: false, code: "not_found"` で、受付番号は `null` です。

## 来院確認・連携

| action | 動作 |
|---|---|
| `find` | 患者IDから受付番号を取得（読取のみ） |
| `arrived` | 対象患者の「来院確認」ボタンを実行 |
| `link` | 対象患者の「連携」ボタンを実行 |

操作要求は、直前のfind結果の受付番号・患者名を付け、新しい要求IDで送ります。

```json
{
  "requestId": "test-arrived-001",
  "action": "arrived",
  "patientId": "00011",
  "expectedReceptionNo": "102",
  "expectedPatientName": "テスト　患者"
}
```

連携は `action` を `link`、`requestId` を例えば `test-link-001` にします。ファイル名も要求IDと一致させます。

アプリ側の「実操作を有効化」が必要です。無効なら `operations_disabled`、番号・氏名が変われば `identity_changed` を返します。患者IDが未確定の行は操作できません。

`success: true, code: "invoked"` はボタン操作を送信した意味です。Dynamicsへの登録完了までを保証する値ではありません。処理結果が不明な `outcome_unknown` は再実行せず、iCall側を確認してください。

## 患者IDと操作名だけで試すスクリプト

`scripts/Send-ICallRequest.ps1` をPowerShellから実行します。Windows PowerShell 5.1／PowerShell 7向けで、追加ライブラリは不要です。

```powershell
# 同じPC・同じユーザーで受付番号取得（アプリ設定を自動で読みます）
.\scripts\Send-ICallRequest.ps1 -PatientId '00011'

# 別PCから受付番号取得
.\scripts\Send-ICallRequest.ps1 -BridgeDirectory '\\iCallPC\iCallBridge' -PatientId '00011' -Action find

# 来院確認（実際に操作します）
.\scripts\Send-ICallRequest.ps1 -BridgeDirectory '\\iCallPC\iCallBridge' -PatientId '00011' -Action arrived

# 連携（実際に操作します）
.\scripts\Send-ICallRequest.ps1 -BridgeDirectory '\\iCallPC\iCallBridge' -PatientId '00011' -Action link
```

来院確認・連携の場合は、スクリプトがfind→結果照合用情報を付けて操作要求、の2段階を実行します。find失敗時は操作しません。各要求IDと応答の保存場所を表示し、最後に応答オブジェクトを返します。アプリの確認ダイアログはデバッグボタン用なので、SMB経由の要求では表示しません。

## 送信時のルール

- JSONはUTF-8で `.tmp` に書き、書込を閉じてから `.json` にリネームします。スクリプトでは自動処理します。
- ファイル名と `requestId` を一致させます。新しい処理には新しいIDが必要です。
- 同じID・同じ内容を再送すると前回応答を返します。新しい検索結果が欲しい場合も新しいIDを使います。
- タイムアウトはキャンセルではありません。ボタン操作のタイムアウト後にスクリプトを再実行すると別IDの新しい操作になるため、まず表示された応答パスとiCall画面を確認します。
- 不正JSONやID不一致は `State/rejected` に移され、ログに理由が残ります。これらにはresponseを生成しません。

詳細は `iCallManager_Usage.md` を参照してください。
