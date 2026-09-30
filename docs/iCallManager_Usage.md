# ダミー枠の患者割当について

ファイルAPIの action=assign を追加しました。要求例・応答・実機テスト方法は [患者割当API](iCallManager_DummyAssignment.md) を参照してください。

# iCallManager の起動・SMB連携

今回の対象はiCallManagerのみです。ReceptionAgent、Kiosk、OQS、Dynamics本体の実装やネットワーク設定変更は含みません。

## 起動

1. iCall端末のEdge IEモードでログインし、当日の受付一覧を1画面だけ開きます。iCallManagerは画面の日付・ページ切替を自動操作しません。
2. `bin/Debug/net10.0-windows/iCallManager.exe` を実行します。.NET 10 Windows Desktop Runtimeが必要です。
3. 既定では10秒ごとに表示画面を読み取り、受付番号・患者ID・患者名・ボタンの有無を一覧表示します。「来院ボタン有」「連携ボタン有」は画面上の存在を示し、実行可否とは別です。患者IDや氏名が未確定の場合は「操作制限」列に理由を表示します。これは表示中のUIの同期です。ブラウザーの再読み込みや、非表示ページの巡回は行いません。
4. 同期失敗時は前回表示を残し、状態を赤字にします。APIは前回キャッシュを返さず、必ず画面を再読込します。
5. 初期状態は読取専用です。操作する際は「実操作を有効化（API・デバッグ共通）」をチェックします。この設定は再起動時に無効へ戻ります。
6. 一覧で患者を選び、「選択患者の来院確認」「選択患者の連携」を押します。既定では患者情報を確認するダイアログを表示します。API要求には確認ダイアログを出しません。

UIAは専用MTAスレッドで直列に実行します。同期、SMB要求、デバッグ操作が同時にiCallを操作することはありません。iCallやIEのUIA提供側が応答しない場合、処理待ちとなる可能性があります。クライアントのタイムアウトは操作キャンセルを意味しません。

## 画面解析の現状と設定

提供された実画面の診断データに対応しています。iCallのIE画面では行要素が省略され、見出し表と `tableList` のデータ表が分離されていました。見出し14セル（最後の「機能選択」がデータ側の3セルに対応）、患者1件あたり16セルの構造を検証して解析します。「受付番号」「診察券」「おなまえ」の対応を確認し、数値が入ったメモ欄を患者IDとして扱いません。

患者未割当の行も一覧表示しますが、来院確認・連携は無効にし、APIの患者検索対象からも除外します。ボタンがない行も列挙します。見出し・列数・受付番号リンクの位置・操作ボタンの列・連携と案内の内部IDが一致しない場合は操作せず停止します。案内ボタンは行の照合に使うだけで、実行しません。

MSAAのTable / Row / Cell、またはセルを持つUIA DataItemの構造も引き続き対応しています。提供された診断データによる読取の再現テストは完了していますが、実ボタン操作後の画面変化は実機での確認が必要です。

設定ファイルは初回起動時に `%LOCALAPPDATA%\iCallManager\settings.json` に生成します。API連携フォルダーはメイン画面の「設定」ボタンから変更できます。入力または「参照…」で選択し保存すると、書込権限を確認して `request`・`response` を作成します。変更は次回起動から反映され、現在使用中のフォルダーは設定画面に表示されます。旧フォルダーの要求・応答は移動しません。その他の設定はアプリを閉じてファイルを編集し、再起動してください。

```json
{
  "syncIntervalSeconds": 10,
  "windowTitleContains": "- 管理画面",
  "frameworkId": "InternetExplorer",
  "bridgeDirectory": "C:\\iCallBridge",
  "receptionNoColumn": -1,
  "patientIdColumn": -1,
  "patientNameColumn": -1
}
```

`bridgeDirectory`の初期値は `%LOCALAPPDATA%\iCallManager\Bridge` の展開済み絶対パスです。

列の `-1` はヘッダー検出です。通常はこのまま使用してください。今回の平坦なIEテーブルでは受付番号=2、患者ID=3、患者名=4の0始まり列番号を検証します。これと異なる手動設定はエラーとします。

行要素がある画面では、受付番号／予約番号／順番／番号、診察券番号／患者ID／PatientID／診察券、患者名／氏名／名前／おなまえを認識します。ヘッダーが異なる場合は、**診断で確認したセルの0始まり列番号**を設定してください。列構造が未確認のまま数値を入れないでください。複数テーブル・不明な行・患者ID重複はエラーとして扱います。

「行構造診断」は読取専用です。要素の型・MSAAロール・階層・AutomationId・Name・HelpTextを別ウィンドウに表示します。患者情報を含むため、開発用に共有する場合は患者ID・氏名を匿名化してください。診断全文は自動保存しません。

## 共有フォルダー

アプリは以下を作成します。Windowsの共有設定自体は自動変更しません。

```text
<bridgeDirectory>/
  request/       ReceptionAgent → iCallManager
  response/      iCallManager → ReceptionAgent（同じ共有内の応答ファイル）

%LOCALAPPDATA%/iCallManager/
  State/processing/  受信処理中の要求
  State/journal/     要求ID・内容のハッシュ・確定応答
  State/rejected/    不正JSONやID衝突等の要求
  Logs/             日別の状態ログ
```

`bridgeDirectory`だけを、例として `\\iCallPC\iCallBridge` に共有します。ReceptionAgentの専用アカウントにrequestの作成・リネーム権限、responseの読取権限を与えます。iCallManager実行ユーザーには両方の読取・書込・削除権限が必要です。Stateは共有しません。通信はVLAN3からVLAN1のSMB共有を読む／書く方向で成立します。

要求と応答には患者ID・患者名を含みます。共有アクセスは連携アカウントに限定してください。journalは再実行防止の記録です。要求IDは再利用せず、運用中にjournalを削除しないでください。応答・拒否ファイル・ログの保管期間と削除は運用側で決めます。

## JSON API

要求ファイル名は `<requestId>.json`。UTF-8で一旦 `.tmp` に書き、閉じてから同じrequestフォルダー内で `.json` にリネームして公開します。公開後の内容を変更しないでください。最大64KB、要求IDは英数字で始まる英数字・`_`・`-`の1～80文字（Windows予約名は不可）です。

予約検索の例（架空患者）：`request/20260928-find-001.json`

```json
{
  "requestId": "20260928-find-001",
  "action": "find",
  "patientId": "00011"
}
```

応答：`response/20260928-find-001.json`

実際の応答には、要求内容を照合するSHA-256の `requestFingerprint` も含みます（以下は主要項目）。C#クライアントはこれを検証し、同じIDを誤って別操作に使った場合に以前の結果を採用しません。

```json
{
  "requestId": "20260928-find-001",
  "success": true,
  "code": "found",
  "message": "予約を取得しました。",
  "patientId": "00011",
  "receptionNo": "36",
  "patientName": "テスト患者",
  "completedAt": "2026-09-28T09:00:00+09:00"
}
```

来院確認：**直前のfind応答を使用**し、新しい要求IDで送ります。

```json
{
  "requestId": "20260928-arrived-001",
  "action": "arrived",
  "patientId": "00011",
  "expectedReceptionNo": "36",
  "expectedPatientName": "テスト患者"
}
```

連携は `action: "link"` です。各操作では最新画面から患者行を再特定し、受付番号・患者名を照合します。`案内`・`保留`はAPI対象外です。`print`は差し替え用インターフェースだけ用意し、現時点では`printing_not_configured`を返します。

`success: true, code: "invoked"` はInvokePattern呼出しを送信した意味です。iCallの確認ダイアログ、サーバー処理やDynamics受付の完了は保証しません。実環境で来院確認後・連携後の表示を確認し、その後の完了判定を追加してください。自動の一括受付は現段階では実装していません。

| code | 意味・対応 |
|---|---|
| found | 現在の一覧から予約取得 |
| invoked | ボタン操作送信済み。業務処理完了は別途確認 |
| operations_disabled | アプリで実操作が無効 |
| not_found | 表示一覧に患者がいない |
| ambiguous_patient | 同じ患者IDの行が複数ある |
| identity_changed | 受付番号・氏名または操作直前の行状態が変化 |
| action_unavailable | ボタンがない／無効。実行済みと断定しない |
| table_not_unique / row_layout_unverified | 画面構造の確認が必要 |
| window_not_unique / framework_not_found | ログイン済みIE管理画面を確認 |
| outcome_unknown | 再送で実行せず、職員がiCall側を確認 |
| automation_unavailable | UIA読取エラー。キャッシュは返さない |
| printing_not_configured | 発券方式未設定 |

同じID・同じ内容の再送は保存済み応答を返し、操作を再実行しません。異なる内容でのID再利用はrejectedへ移し、既存応答を維持します。不正JSON／ID不一致もrejectedへ移しログを残します（応答を生成しません）。アプリ停止直前に処理開始を記録して完了記録が残らなかった要求は`outcome_unknown`とし、再実行しません。

## ReceptionAgent用C#クライアント

`Bridge/FileICallBridgeClient.cs`、`Core/Contracts.cs`、`Core/AppSettings.cs`はUIA参照なしで利用できます。

```csharp
var client = new FileICallBridgeClient(@"\\iCallPC\iCallBridge");
var request = new BridgeRequest(Guid.NewGuid().ToString("N"), "find", patientId);
var found = await client.SendAsync(request, TimeSpan.FromSeconds(30), cancellationToken);
// found.Successを確認し、患者情報を画面/上位ワークフローで確認してから操作する。
var arrivedRequest = new BridgeRequest(Guid.NewGuid().ToString("N"), "arrived",
    patientId, found.ReceptionNo, found.PatientName);
// await client.SendAsync(arrivedRequest, TimeSpan.FromSeconds(30), cancellationToken);
```

タイムアウト・キャンセルは送信済み要求を撤回しません。同じ要求IDを保持し、その応答を確認します。別IDによる自動リトライはしないでください。新しい論理要求には必ず新しいIDを使います。

## ビルド・検証

COM参照を維持しているため、Visual StudioのMSBuildを使用します（`dotnet build`はCOM参照解決をサポートしません）。

```powershell
& 'C:\Program Files\Microsoft Visual Studio\18\Community\MSBuild\Current\Bin\MSBuild.exe' iCallManager.csproj /restore /t:Build /p:Configuration=Debug
dotnet run --project Tests/iCallManager.Tests.csproj
```

テストは架空データと一時フォルダーを使用し、実際のiCallボタンを押しません。実端末では最初に読取だけで全行・列・ボタン対応を確認し、次にテスト患者で来院確認・連携後の画面とDynamics側の反映を確認してください。

## ランタイム同梱・単一EXEでの配布

`Properties/PublishProfiles/WinX64SingleFile.pubxml` を追加しています。Visual Studioのプロジェクトを右クリックして「発行」を開き、`WinX64SingleFile` を選択して発行できます。

PowerShellから発行する場合はプロジェクトフォルダーで次を実行します。

```powershell
& 'C:\Program Files\Microsoft Visual Studio\18\Community\MSBuild\Current\Bin\MSBuild.exe' iCallManager.csproj /restore /t:Publish /p:Configuration=Release /p:PublishProfile=WinX64SingleFile
```

出力先は `bin/Publish/win-x64/iCallManager.exe` です。このEXEに.NET Windows Desktopランタイム、アプリのDLL、COM相互運用DLL、アイコンを同梱します。配布先への.NETランタイムの別途インストールは不要です。Windows 64ビット用であり、iCallおよびEdge IEモードの準備は引き続き必要です。

主な設定は `SelfContained=true`、`PublishSingleFile=true`、`IncludeNativeLibrariesForSelfExtract=true` です。シンボルは `DebugType=embedded` で同梱し、WinForms／COMの互換性を維持するためトリミングは無効です。

配布ファイルは1つですが、実行時にネイティブDLL等が一時フォルダーへ展開されます。また、アプリの設定・ログ・SMB要求／応答は従来どおり別ファイルとして作成されます。ランタイムの更新を取り込む際は再発行してEXEを置き換えてください。

参考: [Microsoft — 単一ファイルでのアプリケーション配布](https://learn.microsoft.com/en-us/dotnet/core/deploying/single-file/overview)
