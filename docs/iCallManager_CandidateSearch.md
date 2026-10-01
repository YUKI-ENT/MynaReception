# 診察券番号不明の予約候補検索

既存のSMBファイルAPIに `action: "find_candidates"` を追加しました。現在表示されているiCall一覧を要求ごとに読み直し、患者名欄に含まれる生年月日で予約候補を返します。実操作の有効化は不要です。Kioskの条件分岐やReceptionAgentからの呼出しは今回の変更に含みません。

## 要求

`request/candidate-001.json` にUTF-8で送ります。既存APIと同様、tmpに書いて閉じた後jsonへリネームしてください。新しい検索には新しいrequestIdが必要です。

```json
{
  "requestId": "candidate-001",
  "action": "find_candidates",
  "birthdate": "1976-09-25",
  "patientName": "架空 花子",
  "nameKana": "テスト ハナコ",
  "givenName": "ハナコ"
}
```

- `patientId` は不要です。
- `birthdate` は実在する過去または当日の日付を `yyyy-MM-dd` で指定します。
- `patientName`（患者本人の漢字氏名等）、`nameKana`（患者本人の氏名カナ）、`givenName`（明示的に分離できた名前部分）のいずれか1つ以上を指定します。各100文字以内。保険の名義人の氏名は使用しません。
- 指定した氏名は一致の程度を示すために使います。生年月日が一致する行は、氏名が不一致でも候補から除外しません。

## 応答

以下は主要項目の例です。通常の `message`、`completedAt`、`requestFingerprint` も返します。

```json
{
  "requestId": "candidate-001",
  "success": true,
  "code": "candidates_found",
  "patientId": null,
  "receptionNo": null,
  "patientName": null,
  "candidates": [
    {
      "receptionNo": "10",
      "patientId": null,
      "patientName": "てすと はなこ (昭和51年9月25日)",
      "parsedName": "てすと はなこ",
      "birthdate": "1976-09-25",
      "nameMatch": "full_name",
      "requiresConfirmation": true
    }
  ]
}
```

候補の `patientName` はiCall上の原文、`parsedName` は生年月日を除いた氏名です。診察券番号が空欄・「-」なら候補のpatientIdはnullです。既知の番号がある候補では番号を保持します。トップレベルの患者・予約は選択しません。

| nameMatch | 意味 |
|---|---|
| `full_name` | 正規化後の氏名がpatientNameまたはnameKanaと完全一致 |
| `given_name_suffix` | 正規化後の氏名末尾が、明示されたgivenNameと一致（改姓の確認用ヒント） |
| `birthdate_only` | 生年月日のみ一致。漢字・カナの相違、入力誤り等も含む |

この順で並べ、同じ一致区分の行は一覧上の順序を維持します。全角・半角、空白、ひらがな・カタカナを正規化します。漢字の読み推測、姓・名の自動分割、編集距離による自動確定は行いません。givenNameは氏名から機械的に切り出して渡さないでください。末尾一致は姓・名の境界や本人同一性を保証しません。

候補が1件・氏名完全一致でも `requiresConfirmation: true` です。予約を選べてもカルテ患者の特定・受診歴判定とは別です。既存のarrived/link/assignを自動実行するものではありません。

## 生年月日の解析範囲と結果コード

氏名欄末尾の `氏名 (昭和51年9月25日)` 形式を解析します。全角括弧・数字・空白、明治／大正／昭和／平成／令和の元年、`氏名 (1976年9月25日)` と `氏名 (西暦1976年9月25日)` に対応します。実在日付・元号期間・未来日付を検証し、不正日付や切れた文字列は候補にしません。

| code | success | 意味 |
|---|---|---|
| `candidates_found` | true | 生年月日が一致する候補あり |
| `no_candidates` | true | 読取成功・候補0件。candidatesは空配列 |
| `invalid_request` | false | 生年月日・氏名の要求形式が不正 |
| `automation_unavailable` 等 | false | 一覧読取失敗。過去の同期結果で代用しない |

検索範囲は現在表示されている一覧のみです。通常の再診予約で氏名欄に生年月日がない行、別の一覧の予約、対応外の日付表記は検索できません。`no_candidates` を「予約なし」「当院初めて」へ変換しないでください。改姓した既存患者の通常予約を調べるには、Dynamicsなどで患者IDを復旧して従来のfindを呼ぶか、別の生年月日取得手段が必要です。

同じrequestIdの再送は保存された前回応答を返します。旧find要求のJSON・fingerprintは維持し、新しい検索条件はfingerprintに含めます。
