# iCallManager ダミー枠への患者割当API

## 要求と応答

既存のファイルAPIに action = assign を追加しました。requestIdは操作ごとに一意にします。patientIdはDynamicsの枝番なし患者ID（iCallの診察券番号）です。

要求ファイル例: request/assign-20260930-001.json

~~~json
{
  "requestId": "assign-20260930-001",
  "action": "assign",
  "patientId": "11"
}
~~~

枠指定を省略した場合は、現在表示中の当日一覧を上から順に調べ、操作可能な「患者割当」ボタンを持つ最初の未割当ダミー枠を選びます。画面ツリーの行順を使用し、待ち順・受付番号の数値では並べ替えません。待ち順が「-」の枠も対象です。枠を指定する場合は expectedReceptionNo を追加します。

~~~json
{
  "requestId": "assign-20260930-002",
  "action": "assign",
  "patientId": "11",
  "expectedReceptionNo": "2"
}
~~~

expectedPatientName はassignでは不要です。氏名を外部要求の完全一致条件にはしません。

対応する response/<requestId>.json に、成功／失敗、code、message、patientId、receptionNo、patientName、completedAt、requestFingerprint を返します。割当完了を一覧で確認できた場合のみ success=true、code=assigned、receptionNo=割り当てた受付番号を返します。内部予約ID（rsv_sn）と表示上の受付番号は別です。

予約が既にある患者へのassignは success=false、code=already_reserved と既存の受付番号を返し、別のダミー枠には割り当てません。予約取得はfindを使用してください。

## 操作の流れ

1. 現在の一覧を同期し、患者IDがすでに予約に存在しないことを確認します。
2. 診察券が「-」、氏名が「-」または空欄で、「患者割当」ボタンが操作可能な枠を選びます。氏名がある診察券未設定行は対象外です。
3. 操作直前に対象行の受付番号・内部ID・未割当状態・待ち順を再確認します。
4. 「患者割当」をInvokePatternで呼び出します。
5. メイン画面、または同じiCallプロセスの別ウィンドウにある「順番待ち予約ダミー割当／順番 ダミー割当」を特定します。
6. ポップアップの割当予約番号と、URLにあるrsv_snを対象枠と照合します。
7. 検索条件が「診察券番号」であることを確認し、入力欄のUIA祖先から `Internet Explorer_Server` を特定します。`WM_HTML_GETOBJECT` と `ObjectFromLresult` で現在のHTML文書を取得し、`fid=daylist`・`vid=fmdmytrn`・対象の `rsv_sn` が一意に一致する文書を選びます。その文書の `findword` 入力欄にpatientIdを設定し、実際のHTML値を読み返して一致を確認します。`onchange` を通知後、「患者検索」のHTML要素の `click()` を1回呼び出します。検索ボタンはUIA／MSAAによる押下へフォールバックしません。
8. 左側の結果欄「診察券」「おなまえ」を読み、診察券番号が一致していることと「割当する」が操作可能であることを確認します。
9. 操作直前にも患者の検索結果と、一覧上の未割当状態・既存予約の有無を確認します。HTML側の割当ボタンがフォーム `dayrsv_add` に属し、`onmousedown="Action('insert');"` を持つことを確認し、このボタンに `onmousedown` イベントを1回送ります。
10. 一覧の対象受付番号・内部IDに患者IDが入ったことを待ち、通常の同期経路でも結果を再確認して応答します。

検索後はUIAの結果欄の診察券番号・患者名を照合します。検索時の画面遷移で入力欄が空になっていても、結果欄が一致すれば続行します。入力欄が別の番号に書き換わっている場合は停止します。割当確定前にも、対象枠・患者結果をUIAで再確認し、対象のHTML文書を取得し直して入力値・割当ボタンを検証します。HTMLボタンは通常のbutton／submitのほか、altまたはtitleが一致する画像型inputにも対応します。

**割当確定は、実際のHTML定義に合わせて `fireEvent("onmousedown", event)` を使用します。** 提供されたHTMLでは「割当する」は `type=submit` ですが、割当処理の `Action('insert')` はonclickではなくonmousedownに定義されていました。イベントには左ボタンを示す `button=1` を設定し、マウス移動・前面表示・座標操作は行いません。検索処理は引き続きHTMLの `click()` です。

`Action('insert')` が既存のメモ検証・予約重複確認等を行い、必要な値を設定してフォームを送信します。これらの検証・キャンセルを迂回せず、追加の `click()`、直接のフォーム送信、別方式での再押下は行いません。イベント定義または所属フォームが想定と違う場合は、送信前に `assignment_handler_unverified` で停止します。

HTML文書への接続は表示済み画面のCOMインターフェースを使用し、URLへの直接HTTP要求は送りません。参考: [Microsoft IHTMLElement::click](https://learn.microsoft.com/en-us/previous-versions/windows/internet-explorer/ie-developer/platform-apis/aa752277(v=vs.85))。

UIAは従来どおり専用MTAワーカーで処理します。MSHTMLのフレーム・ウィンドウのCOM操作は専用STAスレッドへ委譲し、HTML文書の取得から解放まで同じSTA内で完結させます。UIAワーカーは完了を待つため、同期・他の操作と同時には実行されません。

患者割当だけを行います。来院確認・連携・案内はこの要求では呼び出しません。iCallManagerの「実操作を有効化」が必要です。

UIの更新待ちは操作全体で最大30秒です。呼出し側の応答タイムアウトは、SMBの受信間隔や待ち行列を含め60秒以上を推奨します。UIAプロバイダー自体の呼出し時間はこの更新待ち期限では制限できません。

## 主な失敗コード

| code | 内容 |
| --- | --- |
| operations_disabled | 実操作が無効 |
| invalid_request | カルテ番号・指定受付番号が不正 |
| already_reserved | 患者は既に予約枠に登録済み |
| ambiguous_patient | 同じ患者IDが複数の予約に存在 |
| no_dummy_slot | 指定枠または操作可能なダミー枠がない |
| ambiguous_dummy_slot | 対象枠を一意に選べない |
| dummy_slot_changed | 対象行またはポップアップの枠が不一致 |
| assignment_popup_busy | 既に割当画面が開いている |
| assignment_popup_not_unique | 割当画面を一意に特定できない |
| patient_search_mode_unverified | 検索条件が診察券番号と確認できない |
| value_unavailable | 入力欄をValuePatternで編集できない |
| patient_search_unverified | 検索結果の診察券番号・氏名を確認できない |
| patient_search_empty | 診察券・おなまえの欄は見つかったが、検索結果が空欄（待機中の理由） |
| assignment_dom_unavailable | 入力欄のネイティブHTMLサーバー／HTML文書を取得できない |
| assignment_dom_not_unique | 内部予約IDが一致する割当HTML文書を一意に特定できない |
| assignment_dom_failed | HTML入力・検索等のCOM操作が失敗。HRESULTと処理段階を確認 |
| assignment_handler_unverified | 割当ボタンのonmousedownまたは所属フォームが実機で確認した定義と違うため、送信しなかった |
| assignment_timeout | 割当確定前の画面更新待ちがタイムアウト |
| outcome_unknown | 割当クリックの試行後に結果を確認できない。元のcode・処理段階もmessageに保持する |

検索待ち中に確認できない状態が続いた場合はassignment_timeoutを返します。失敗時の受付番号は通常nullです。already_reservedのみ、判明した既存予約番号を返します。

タイムアウトの応答には `phase=patient_search_submit_dom`（HTML入力・検索呼出し）、`patient_search_result`（検索結果）、`assigned_list`（割当後の一覧）を含めます。直前に読み取れた画面構造をLogsフォルダーの `assignment-last-failure.txt` に保存し、保存できた場合はファイル名も応答に含めます。このファイルは割当タイムアウトごとに上書きし、先頭に要求IDを記録します。通常の行構造診断の保存件数上限には影響されませんが、書込み権限等によって保存できない場合もあります。診断には患者情報を含みます。`automation-YYYYMMDD.jsonl` の `assignment_button_dispatch` で、検索・割当それぞれの呼出し方式を確認できます。

割当イベントの送信前に失敗した場合は、元のエラーコードを返します。`commit_patient_verification`（患者照合）、`commit_slot_verification`（枠の照合）、`commit_button_dom`（HTML検証・イベント送信）の段階と `commitAttempted` を応答・診断に記録します。`assignment_button_dispatch` の検索は `MSHTML.click`、割当確定は `MSHTML.fireEvent(onmousedown)` になります。`commitAttempted=True` はイベント送信を試みたという意味で、割当完了の保証ではありません。

送信試行後の例外は、ダイアログが閉じてCOM要素が無効になった場合も含め、直ちに失敗応答にせず一覧の読み取りへ進みます。割当ボタンは再押下しません。割当後の確認には検索とは別の30秒の待機枠を設け、COM例外または無効になったCOMラッパーの読み取りは200ミリ秒間隔で最大5回、新しい画面要素を取得し直します。通常一覧経由の独立した最終確認でも同じ読み取り再試行を行います。要求したカルテ番号・対象受付番号・内部予約IDの一致を確認できた場合だけ `assigned` を返し、確認できなければ `outcome_unknown` を返します。実際の管理画面が複数あるエラー等をCOM例外として再試行することはありません。

ログの `assignment_commit_dispatch_error` は送信時の例外、`assignment_confirmation_read_failed` は確認中のCOM例外と再試行回数・HRESULT・処理段階、`assignment_confirmed_after_dispatch_error` は例外後に一覧で成功を確認できたことを示します。未確認時の応答にも処理段階とHRESULT（BridgeExceptionの場合は元のコード・メッセージ）を含めます。

同じrequestIdの同じ要求は保存済み応答を再利用し、画面操作を繰り返しません。タイムアウトやoutcome_unknownの後は、新しいIDで再送せず、同じIDの応答とiCall画面を確認してください。確定前のエラーでポップアップが残った場合は、職員が閉じて状態を確認してください。

## 手動テスト用スクリプト

~~~powershell
# 実際の患者割当を要求します。iCallManagerの実操作を有効にしてから実行。
./scripts/Send-ICallRequest.ps1 -BridgeDirectory '\\iCallPC\iCallBridge' -PatientId '11' -Action assign -TimeoutSeconds 60

# ダミー枠の受付番号を明示する場合
./scripts/Send-ICallRequest.ps1 -BridgeDirectory '\\iCallPC\iCallBridge' -PatientId '11' -Action assign -ExpectedReceptionNo '2' -TimeoutSeconds 60
~~~

iCallManagerの一覧には「患者割当ボタン有」「ダミー割当可」を表示します。

## 検証状況と実機確認

### ダイアログのHTML・イベント定義を調べる

更新版の「行構造診断」は、ダミー割当ダイアログが開いている場合、出力の先頭に `=== 割当HTML診断（読取専用） ===` を追加します。対象内部予約IDを確認してから、割当ボタンのouterHTML・クリック／マウス／キーのイベント属性・所属フォームのaction/method/onsubmit・インラインスクリプト本文（最大60,000文字）を表示します。外部スクリプトはsrcだけを表示し、追加のHTTP取得は行いません。

診断のためにボタン押下、フォーム送信、スクリプト実行、マウス移動は行いません。実操作OFFで使用できます。ダイアログを開いたまま更新版を起動し、必要なら手動で患者検索まで進めてから「行構造診断」を押してください。`=== 割当HTML診断 終了 ===` までをコピーできます。HTMLやスクリプト中に患者情報を含む場合があるため、共有前に確認してください。HTMLの取得に失敗した場合は理由を先頭に表示し、通常のUIA構造診断を残します。

IEの割当ダイアログが管理画面と同じタイトルを持つ場合も、ネイティブクラス `Internet Explorer_TridentDlgFrame` によりメイン画面候補から除外し、同じプロセスの補助ウィンドウとして読み取ります。実際の管理画面が複数ある場合は引き続き `window_not_unique` で停止します。

外側のウィンドウが別のクラス名で管理画面と同じタイトルを持つ場合は、内部の割当画面名・`fmdmytrn` URLと予約一覧の有無でも判別します。割当完了後、クラス `Alternate Modal Top Most` の空のIE補助ウィンドウだけが残る場合も、予約一覧が存在しなければメイン画面候補から除外します。このクラスでも予約一覧が存在する場合や、未知のクラスの同名ウィンドウがある場合は安易に無視しません。実際の管理画面が2つあれば引き続き停止します。メイン画面の配下に既にある割当ウィンドウはUIA要素の同一性で照合し、二重に追加しません。`window_not_unique` の応答には `[window-detect-v3]` と候補のクラス名・プロセスID・一覧／割当画面の検出結果を含めます。

割当自体は成功したが、以前の版で完了確認に失敗して `outcome_unknown` になった要求の保存済み応答は書き換えません。更新版で「今すぐ同期」を実行し、新しい要求IDの `find` で患者の受付番号・氏名を確認してください。確認のために `assign` を再送しないでください。今後の割当要求は、更新後のウィンドウ判定を使って完了後の一覧を確認します。

別端末への再配布は `bin/Publish/win-x64/iCallManager.exe` の単一EXEを使用してください。`bin/Debug/net10.0-windows/iCallManager.exe` はDLL等と同じフォルダーで使用する実行ファイルで、EXEだけをコピーしても修正したDLLは更新されません。

既存の匿名化した当日一覧ツリーでダミー行の解析を確認しています。ポップアップは提供されたInspect結果・画像に基づく模擬ツリーで検証しています。

ローカルのMSHTMLを使い、入力値・onchange・検索のonclick・割当のonmousedown実行、操作OFF・ボタン重複・入力値変更・イベント定義やフォームの不一致時に停止することを検証しています。実機と同じonmousedown定義のsubmitボタンで、clickだけではActionが呼ばれないこと、onmousedownの送信では既存のActionを通り、検証キャンセルを尊重し余分なclickをしないことを再現しています。

ループバックのみのテストHTMLと子フレームを表示し、実際の `Internet Explorer_Server` からHTML文書を取得して内部予約IDを照合し、MTA→STAの経路で検索・割当イベントを実行し、Actionから `act=insert` のHTTPフォーム送信が1回だけ行われることも検証しています。実際のiCallページでの割当完了は現地確認が必要です。

実機では、未予約のテスト患者とダミー枠1件で、入力欄のValuePattern、検索条件コンボの選択値、左側結果欄のツリー構造、割当後の一覧更新と内部IDの維持を確認してください。読み取りに失敗した場合は、割当画面を表示した状態で「行構造診断」の結果を確認します。診断には今回から編集欄・コンボのvalueも含めています。

ReceptionAgentからのassign呼出し・画面ボタンは今回の変更に含めていません。今回の対象はiCallManagerのAPI受信と割当処理です。
