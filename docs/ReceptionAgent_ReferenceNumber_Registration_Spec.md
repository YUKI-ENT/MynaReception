# ReceptionAgent 照会番号登録仕様

## 1. 目的

ReceptionAgent に、オンライン資格確認システムの **照会番号一括登録機能** を統合する。

照会番号には原則として、

```text
Dynamics の枝番なし患者ID
```

を登録する。

これにより、顔認証カードリーダーから生成される `OQSsiquc01res_face_*.xml` 内の `ReferenceNumber` から、直接 Dynamics / iCall の患者IDを特定できるようにする。

目標フロー：

```text
顔認証
↓
face XML
↓
ReferenceNumber
↓
PatientID
↓
iCallManager.find
↓
順番予約番号取得
```

## 2. ReceptionAgent内の構成

```text
ReceptionAgent
│
├─ Face/
│   ├─ FaceXmlWatcher.cs
│   ├─ FaceXmlParser.cs
│   ├─ FaceSession.cs
│   ├─ FaceSessionManager.cs
│   └─ FaceXmlMatcher.cs
│
├─ Dynamics/
│   ├─ DynamicsProvider.cs
│   ├─ DynamicsPatientIdentity.cs
│   └─ DynamicsInsuranceInfo.cs
│
├─ Oqs/
│   ├─ OqsFileClient.cs
│   ├─ OqsXmlLoader.cs
│   ├─ OqsRequestSequence.cs
│   └─ ReferenceNumber/
│       ├─ ReferenceNumberRegistrationService.cs
│       ├─ ReferenceNumberRequestBuilder.cs
│       ├─ ReferenceNumberResultParser.cs
│       ├─ ReferenceRegistrationTarget.cs
│       ├─ ReferenceRegistrationResult.cs
│       └─ ReferenceRegistrationJob.cs
│
├─ ICall/
│   └─ ICallBridgeClient.cs
│
└─ Workflow/
    └─ ReceptionWorkflow.cs
```

OQSDrug2 の `BulkQualificationService.cs` と同様に、

```text
XML作成
↓
req
↓
res待機
↓
ReceptionNumber取得
↓
結果DL要求作成
↓
req
↓
最終結果取得
```

という構造にする。

## 3. Dynamics側を唯一の患者マスターとする

患者マスターの CSV / SQLite / PostgreSQL 等への複製は行わない。患者情報は必要時に Dynamics COM から取得する。

```csharp
public sealed class DynamicsPatientIdentity
{
    public string RawChartNo { get; init; } = "";
    public string PatientId { get; init; } = "";
}
```

想定：

```text
RawChartNo = Dynamics内部カルテ番号
PatientId  = 枝番なし患者ID
```

Access BulkTool では照会番号生成時に、

```vb
strWork = CStr(rs("KARTENO"))
ReferenceNumber = Left(strWork, Len(strWork) - 1)
```

としている。このロジックは `DynamicsProvider` 内に閉じ込め、上位層で `Substring()` を直接使用しない。

## 4. 登録対象モデル

```csharp
public sealed class ReferenceRegistrationTarget
{
    public string PatientId { get; init; } = "";
    public string ReferenceNumber { get; init; } = "";
    public string InsurerNumber { get; init; } = "";
    public string InsuredCardSymbol { get; init; } = "";
    public string InsuredIdentificationNumber { get; init; } = "";
    public string InsuredBranchNumber { get; init; } = "";
    public bool IsPublicAssistance { get; init; }
}
```

健康保険の場合に送信する項目：

```text
ReferenceNumber
InsurerNumber
InsuredCardSymbol
InsuredIdentificationNumber
InsuredBranchNumber
```

## 5. 保険と公費の選択ルール

Access BulkTool の挙動を踏襲する。

```text
健康保険あり
↓
健康保険で登録

健康保険なし
↓
医療扶助等の公費で登録
```

同一患者について健康保険と公費の両方を無条件に登録しない。

## 6. OQS API / ファイル種別

```text
登録要求
OQSmuimm01req_

アップロード結果
OQSmuimm01res_

結果ダウンロード要求
OQSmuimm02req_

最終結果
OQSmuimm02res_
```

外部IF：

```text
OQSmuimm01req
OQS-IF-009
照会番号一括登録要求

OQSmuimm02res
OQS-IF-012
照会番号一括登録結果
```

## 7. 登録要求XML

文字コードは `Shift_JIS`。

```xml
<?xml version="1.0" encoding="Shift_JIS" standalone="no"?>
<XmlMsg>
  <MessageHeader>
    <MedicalInstitutionCode>2714206386</MedicalInstitutionCode>
    <ArbitraryFileIdentifier>...</ArbitraryFileIdentifier>
  </MessageHeader>
  <MessageBody>
    <ReferenceNumberRegistrationInfo>
      <ReferenceNumber>24434</ReferenceNumber>
      <InsurerNumber>...</InsurerNumber>
      <InsuredCardSymbol>...</InsuredCardSymbol>
      <InsuredIdentificationNumber>...</InsuredIdentificationNumber>
      <InsuredBranchNumber>00</InsuredBranchNumber>
    </ReferenceNumberRegistrationInfo>
  </MessageBody>
</XmlMsg>
```

## 8. 任意ファイル識別子

`ArbitraryFileIdentifier` は要求単位で一意にする。OQSDrug2 の方式を流用してよい。

例：

```csharp
string afi =
    DateTime.Now.ToString("yyyyMMddHHmmssfff")
    + "-"
    + Guid.NewGuid().ToString("N")[..8];
```

この値は `01req` と `01res` の対応確認にも使用する。

## 9. ファイル名

自動連携では、

```text
OQSmuimm01req_YYYYMMDDNNNN.xml
```

例：

```text
OQSmuimm01req_202609290001.xml
```

ReceptionAgent では `reference_request_sequence.txt` 等で日付＋4桁連番を管理してよい。

## 10. req/res フォルダ

```text
OqsRoot
├─ req
├─ res
└─ trash
```

ReceptionAgent は原則として以下のみ行う。

```text
読み取り
コピー
自分が生成したREQの書込み
```

既存 face XML は削除・移動しない。

## 11. 登録処理の状態機械

```csharp
public enum ReferenceRegistrationState
{
    Pending,
    RequestCreating,
    RequestCreated,
    RequestSubmitted,
    UploadResultWaiting,
    UploadAccepted,
    DownloadRequestCreating,
    DownloadRequestSubmitted,
    ResultWaiting,
    Processing,
    Completed,
    Failed,
    Cancelled
}
```

通常フロー：

```text
Pending
↓
RequestCreating
↓
RequestCreated
↓
RequestSubmitted
↓
UploadResultWaiting
↓
UploadAccepted
↓
DownloadRequestCreating
↓
DownloadRequestSubmitted
↓
ResultWaiting
↓
Completed
```

## 12. アップロード結果

`OQSmuimm01res_*.xml` を監視し、以下を取得する。

```text
ReceptionNumber
SegmentOfResult
ProcessingResultStatus
ProcessingResultCode
ProcessingResultMessage
ArbitraryFileIdentifier
```

成功時に `ReceptionNumber` を取得する。

## 13. ダウンロード要求

`ReceptionNumber` 取得後、`OQSmuimm02req_` を生成する。

```xml
<XmlMsg>
  <MessageHeader>
    <MedicalInstitutionCode>2714206386</MedicalInstitutionCode>
  </MessageHeader>
  <MessageBody>
    <ReceptionNumber>123456...</ReceptionNumber>
  </MessageBody>
</XmlMsg>
```

## 14. 最終結果

監視対象：

```text
OQSmuimm02res_*.xml
```

最低限読む項目：

```text
ReferenceNumber
InsurerNumber
InsuredCardSymbol
InsuredIdentificationNumber
InsuredBranchNumber
ProcessingResultStatus
ProcessingResultCode
ProcessingResultMessage
```

処理結果：

```text
1 = 正常終了
2 = エラー
```

## 15. 登録結果モデル

```csharp
public sealed class ReferenceRegistrationResult
{
    public bool Success { get; init; }
    public string PatientId { get; init; } = "";
    public string ReferenceNumber { get; init; } = "";
    public string ReceptionNumber { get; init; } = "";
    public string ProcessingResultStatus { get; init; } = "";
    public string ProcessingResultCode { get; init; } = "";
    public string ProcessingResultMessage { get; init; } = "";
    public DateTime CompletedAt { get; init; }
    public string? ErrorMessage { get; init; }
}
```

## 16. ReferenceNumberRegistrationService

```csharp
public interface IReferenceNumberRegistrationService
{
    Task<ReferenceRegistrationResult> RegisterAsync(
        ReferenceRegistrationTarget target,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<ReferenceRegistrationResult>> RegisterBatchAsync(
        IReadOnlyList<ReferenceRegistrationTarget> targets,
        CancellationToken cancellationToken = default);
}
```

内部処理：

```text
Build01Request
↓
Write req
↓
Wait01Result
↓
Parse ReceptionNumber
↓
Build02Request
↓
Write req
↓
Wait02Result
↓
Parse registration result
```

## 17. Face XMLとの統合

Face XML解析時には `ReferenceNumber` を最優先で探す。

```csharp
if (!string.IsNullOrWhiteSpace(face.ReferenceNumber))
{
    patientId = face.ReferenceNumber;
}
else
{
    // Dynamics fallback
}
```

今回確認した未登録状態の face XML では `InsurerNumber`、`InsuredCardSymbol`、`InsuredIdentificationNumber`、`InsuredBranchNumber`、`Birthdate`、`QualificationConfirmationKey` 等は存在するが、`ReferenceNumber` は存在しない。

## 18. face XML未登録時の受付

照会番号なしでも受付を止めない。

```text
face XML
↓
ReferenceNumberなし
↓
Dynamics COM
↓
患者候補検索
↓
一意に患者特定
↓
PatientID取得
↓
iCallManager.find
↓
受付継続
```

受付完了後、`ReferenceNumberRegistrationService` へ登録を依頼する。

```text
初回来院
Dynamics fallback

↓ 自動照会番号登録

次回来院
ReferenceNumber fast path
```

## 19. 登録タイミング

```text
① 初期一括整備
既存患者をまとめて登録

② 来院時補完
face XMLでReferenceNumberなし
→ Dynamicsで特定
→ 受付後に登録

③ 保険変更時
新しい保険資格を検出
→ 再登録
```

Kiosk受付のレスポンスを遅らせないため、来院時補完登録は基本的にバックグラウンド処理とする。

## 20. Face XML監視

例：

```text
OQSsiquc01res_face_27142063864da5260a16de5a3d3ad7_20260929160112.xml
```

監視は、

```text
FileSystemWatcher
+
定期スキャン
```

とする。

検出時：

```text
face
↓
ローカルWorkへコピー
↓
コピー側を解析
```

Dynamics が先に処理してファイルを移動した場合のみ `trash` から同名または対応ファイルを探索する。`trash` は救済経路であり主経路ではない。

## 21. FileShare設定

XML読込時は、Dynamics/OQSの処理を阻害しないため、

```csharp
new FileStream(
    path,
    FileMode.Open,
    FileAccess.Read,
    FileShare.ReadWrite | FileShare.Delete)
```

を使用する。

読み込み失敗時：

```text
200～500 ms待機
↓
数回retry
↓
消失した場合trash確認
```

## 22. FaceSessionとの関係

face XML は単純に「次に来た1件」を Kiosk 患者として扱わない。

```text
Kiosk
↓
BeginFaceSession
↓
患者識別情報取得
↓
face XML候補
↓
FaceXmlMatcher
↓
一致した場合のみ採用
```

照会番号が存在する場合でも、`FaceSession` との整合確認を行ってから PatientID として採用する。

## 23. 専用管理画面

ReceptionAgent に「照会番号管理」画面を追加する。

表示例：

```text
登録済           12,451
未登録              181
登録待ち             14
エラー                3
```

操作：

```text
未登録患者を検索
選択患者を登録
全未登録患者を登録
失敗分を再実行
結果ログ表示
```

## 24. ログ

ログには以下を記録する。

```text
PatientId
ReferenceNumber
RequestFileName
ReceptionNumber
Status
ResultCode
Timestamp
```

原則として、氏名・住所・保険証番号全文・face XML全文は保持しない。

## 25. 既存OQSDrug2コードとの共通化

可能なら OQSDrug2 の `BulkQualificationService` と同じ実装思想にする。

```text
OqsXmlLoader
OqsFileClient
OqsRequestSequence
CreateDownloadRequest
ReadUploadResult
WaitForExpectedResult
Shift_JIS XmlWriter設定
```

最初から共通DLL化する必要はない。ReceptionAgent側へ必要なコードを整理して移植し、動作安定後に `OqsCommon` 等への共通化を検討する。

## 26. 実装優先順位

### Phase 1

```text
ReferenceNumberRegistrationTarget作成
Dynamics COMから1患者取得
```

### Phase 2

```text
OQSmuimm01req生成
reqへの書込
```

### Phase 3

```text
01res監視
ReceptionNumber取得
```

### Phase 4

```text
02req生成
02res取得
```

### Phase 5

```text
face XML
↓
ReferenceNumber
↓
iCallManager
```

へ接続する。

## 27. 最初の受け入れテスト

最初は患者1名だけで行う。

```text
Dynamics PatientID
24434

↓ 登録

OQSmuimm01req

↓
01res
↓
02req
↓
02res

↓
正常終了

↓
次回face XML
<ReferenceNumber>24434</ReferenceNumber>
```

その後、

```json
{
  "action": "find",
  "patientId": "24434"
}
```

を iCallManager へ送信し、`receptionNo` と `patientName` が一致すれば、

```text
MyNa → OQS → PatientID → iCall
```

の高速経路が完成。

## 28. Codex実装時の注意

最初から FaceSession や Kiosk 全体を実装対象に含めない。

最初の実装単位は、

```text
PatientID + 保険情報
↓
OQSmuimm01req を正しく生成
```

までとする。

その後、

```text
01res
↓
ReceptionNumber
↓
02req
↓
02res
```

を追加する。

既存の OQSDrug2 `BulkQualificationService.cs` の実装方針を参考にし、特に以下を揃える。

```text
Shift_JIS
req/res監視
4桁連番
ReceptionNumber取得
結果ファイル待機
XML読込時のFileShare配慮
エラーコード・メッセージ保持
CancellationToken対応
```

最終的に ReceptionAgent の受付ワークフローから、

```text
ReferenceNumberあり
→ fast path

ReferenceNumberなし
→ Dynamics fallback
→ 受付継続
→ バックグラウンド照会番号登録
```

として利用する。
