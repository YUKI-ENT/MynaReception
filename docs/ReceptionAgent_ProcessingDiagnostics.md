# XML取得時間・iCall構造エラーの診断

## 終了時の診断

終了ボタンは監視・結果確認をキャンセルし、Kiosk Webサーバーと進行中の処理を非同期で待って閉じます。終了を連打しても停止処理は一度だけです。通常はすぐ閉じ、応答しない共有フォルダーやCOMなどが残る場合の待機上限は8秒です。終了待ちの間はUIスレッドを同期ブロックしません。未完了要求は保存済みのIDと状態で次回確認します。

処理ログの `shutdown_started`、`shutdown_timeout`（待機上限）、`shutdown_completed`、`shutdown_error`、`shutdown_cancel_error` を確認すると、処理待ちが残ったかを追えます。Windows専用の終了回帰テストは `dotnet run --project src/ReceptionAgent.Windows.Tests/ReceptionAgent.Windows.Tests.csproj` です。分離した一時DBと実際のWinFormsメッセージループ・Webサーバーを使用し、通常終了・連打・キャンセル・応答しない処理でのUI応答と待機上限を確認します。

設定画面のiCallManager responseフォルダー欄の下にある「監視開始時にエクスプローラで開く」を有効にすると、監視開始時に設定されたresponseフォルダーをエクスプローラーで開き、最小化します。既定はOFFです。監視を止めてもエクスプローラーは閉じません。開く処理の失敗でXML監視は停止しません。通常の処理ログに `response_explorer_opened`（最小化要求の成否）または `response_explorer_failed` を記録します。この設定による高速化は実環境で比較してください。

取得時間はXMLファイル名の生成時刻から受付分類確定までです。XMLを発見する前の時間、書込み完了確認、処理順番待ち、Dynamics検索、iCall応答待ちを含みます。XML生成側とReceptionAgentのPCの時計差も含むため、発見前の時間をファイル共有の遅延と断定できません。

ReceptionAgentは約1秒間隔でフォルダーを走査し、通常300ms間隔で2回同じXMLを読めた時に取得します。走査には共有フォルダーへのアクセス時間が加わります。処理は直列なので他の患者の処理待ちもあります。iCall findの応答待ちは2秒、タイムアウト後は10秒後に同じ要求IDで再確認します。旧版ではiCall側UI Automationの読取または定期同期の待ちで15秒程度になることがありました。現在のiCallManagerは `find` / `find_candidates` を正常同期済みキャッシュで応答し、要求時のUI読取・UIA待ち行列を省きます。ReceptionAgentのタイムアウトや実操作の再試行方針は変更していません。

## ReceptionAgent

`%LOCALAPPDATA%\ReceptionAgent\Reception\Logs\processing-yyyyMMdd.jsonl`

- `xml_captured`: XML生成時刻、初回発見時刻、取得完了、読取時間、出力先/trash、読取試行数。
- `step_started` / `step_completed`: 各処理の開始・終了、段階、所要時間、要求ID、応答コード。
- `patient_lookup`: Dynamics/XML参照番号によるカルテ検索時間と候補件数。
- `icall_request_written` / `icall_response_received` / `icall_timeout`: 送信と受信・タイムアウト、要求ID、iCall側の完了時刻。
- `step_retry`: 再確認予定時刻、例外種類、タイムアウト回数。
- `scan_slow`: フォルダーの列挙だけで1秒以上かかった場合の時間とファイル件数。

行をダブルクリックすると主要な時刻、カルテ検索時間、タイムアウト回数、取込IDとログフォルダーを表示します。旧記録には新しい計測値はありません。初回発見はその監視インスタンスで最初に読取を開始した時刻です。読取失敗やtrashへの移動後も保持しますが、アプリの再起動をまたいで取得前の時刻は保存しません。

## iCallManager

`%LOCALAPPDATA%\iCallManager\Logs\automation-yyyyMMdd.jsonl`

予約照会は `bridge_received` → `request_queued` → `request_started` → `lookup_cache` → `request_completed` → `bridge_response_written` を要求IDと時刻で対応付けます。`lookup_cache` の `usable`・`lastSuccess`・`ageMs`・`rows` で使用データを確認できます。実操作は `uia_read` または `uia_parse_failed` を記録し、UI読取は同じ専用スレッドで発生します。定期同期のUI読取も記録されます。同期に失敗しても正常キャッシュは保持しますが、2分以上更新できていない場合や日付が変わった場合は照会に使用しません。

構造解析に失敗すると、その解析に使用した同一のUIツリーを `layout-yyyyMMdd-*.txt` に保存します。後から画面を読み直した診断ではないため、失敗時の見出し・列・ダイアログを確認できます。`changedSample` は最初の最大100個の文字要素を再照合して変化した件数、`unavailableSample` は再照合時に取得できなかった件数です。変化があれば表示切替の手掛かりになりますが、ゼロでも表示切替を否定できません。ツリー取得前のエラーではツリーは保存できません。

画面構造ファイルには患者情報が含まれます。外部共有時は匿名化してください。通常のJSONLは氏名・生年月日・保険情報を出力しません。JSONLは1日20MiBで追記停止、構造ファイルは1日10件・各200万文字までです。自動削除は行いません。保存失敗で受付処理は停止しません。
