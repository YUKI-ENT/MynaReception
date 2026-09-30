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

枠指定を省略した場合は、現在の当日一覧で「待ち順」が最も早い未割当ダミー枠を選びます。受付番号の大小では選びません。枠を指定する場合は expectedReceptionNo を追加します。

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
7. 検索条件が「診察券番号」であることを確認し、AutomationId=findwordの編集欄にValuePatternでpatientIdを設定します。読み直した入力値が一致してから「患者検索」を呼び出します。
8. 左側の結果欄「診察券」「おなまえ」を読み、診察券番号が一致していることと「割当する」が操作可能であることを確認します。
9. 操作直前にも患者の検索結果と、一覧上の未割当状態・既存予約の有無を確認して「割当する」を呼び出します。
10. 一覧の対象受付番号・内部IDに患者IDが入ったことを待ち、通常の同期経路でも結果を再確認して応答します。

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
| dummy_order_unverified | 待ち順を読み取れない。受付番号指定が必要 |
| dummy_slot_changed | 対象行またはポップアップの枠が不一致 |
| assignment_popup_busy | 既に割当画面が開いている |
| assignment_popup_not_unique | 割当画面を一意に特定できない |
| patient_search_mode_unverified | 検索条件が診察券番号と確認できない |
| value_unavailable | 入力欄をValuePatternで編集できない |
| patient_search_unverified | 検索結果の診察券番号・氏名を確認できない |
| assignment_timeout | 割当確定前の画面更新待ちがタイムアウト |
| outcome_unknown | 割当するの呼出し後に結果を確認できない |

検索待ち中に確認できない状態が続いた場合はassignment_timeoutを返します。失敗時の受付番号は通常nullです。already_reservedのみ、判明した既存予約番号を返します。

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

既存の匿名化した当日一覧ツリーでダミー行の解析を確認しています。ポップアップは提供されたInspect結果・画像に基づく模擬ツリーで検証しています。

実機では、未予約のテスト患者とダミー枠1件で、入力欄のValuePattern、検索条件コンボの選択値、左側結果欄のツリー構造、割当後の一覧更新と内部IDの維持を確認してください。読み取りに失敗した場合は、割当画面を表示した状態で「行構造診断」の結果を確認します。診断には今回から編集欄・コンボのvalueも含めています。

ReceptionAgentからのassign呼出し・画面ボタンは今回の変更に含めていません。今回の対象はiCallManagerのAPI受信と割当処理です。
