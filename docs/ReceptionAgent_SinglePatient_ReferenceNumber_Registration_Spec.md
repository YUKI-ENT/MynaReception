# ReceptionAgent 単患者照会番号登録仕様

## 1. 目的

ReceptionAgent から、オンライン資格確認等システムの **照会番号単件登録** を実行する。

BulkTool の一括登録（`OQSmuimm01req` → `01res` → `02req` → `02res`）は使用せず、単患者ごとに

```text
OQSsiimm01req
↓
OQSsiimm01res
```

で完結する単件登録方式を使用する。

主用途は、受付時に取得した face XML に `ReferenceNumber` が存在しない患者について、

```text
face XML
↓
Dynamics COM で患者IDを特定
↓
今回の face XML に含まれる最新資格情報を利用
↓
患者IDを照会番号として単件登録
```

することである。

---

## 2. 基本方針

### 2.1 照会番号

照会番号には原則として、

```text
Dynamics の枝番なし患者ID
```

を登録する。

例：

```text
Dynamics内部KARTENO = 244340
↓
枝番なし患者ID = 24434
↓
ReferenceNumber = 24434
```

Dynamics内部形式から患者IDを生成する処理は `DynamicsProvider` に閉じ込め、ReceptionWorkflow 等の上位層で文字列切り出しを行わない。

---

## 3. 単件登録ファイル

### 3.1 要求

```text
OQSsiimm01req_
```

用途：

```text
照会番号登録要求（単件）
```

外部IF：

```text
OQS-IF-003
```

ファイル名例：

```text
OQSsiimm01req_YYYYMMDDNNNN.xml
```

例：

```text
OQSsiimm01req_202609300001.xml
```

### 3.2 結果

```text
OQSsiimm01res_
```

用途：

```text
照会番号登録結果（単件）
```

要求と結果は1対1で対応する。

Bulk処理のような、

```text
ReceptionNumber取得
↓
02req
↓
02res
```

は不要。

---

## 4. 登録要求XML

文字コード：

```text
Shift_JIS
```

XML宣言：

```xml
<?xml version="1.0" encoding="Shift_JIS" standalone="no"?>
```

基本構造：

```xml
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

---

## 5. 要求項目

### MessageHeader

#### MedicalInstitutionCode

- 医療機関コード
- 必須
- 10桁

例：

```xml
<MedicalInstitutionCode>2714206386</MedicalInstitutionCode>
```

#### ArbitraryFileIdentifier

- 任意のファイル識別子
- 任意
- 要求と結果の対応確認に使用可能
- ReceptionAgent側では原則付与する

推奨例：

```text
yyyyMMddHHmmssfff + "-" + Guid先頭8文字
```

### MessageBody / ReferenceNumberRegistrationInfo

#### ReferenceNumber

- 照会番号
- 必須
- 最大50文字
- ReceptionAgentでは Dynamics の枝番なし患者IDを設定する

例：

```xml
<ReferenceNumber>24434</ReferenceNumber>
```

#### InsurerNumber

- 保険者番号
- 必須
- 8桁
- 8桁未満の場合は先頭スペース埋めが必要

※ 実装時は元仕様に従い、ゼロ埋めではなくスペース埋めを使用する。

#### InsuredCardSymbol

- 被保険者証記号
- 任意
- 最大20文字

値が空の場合はタグ自体を省略してよい。

#### InsuredIdentificationNumber

- 被保険者証番号
- 必須
- 最大20文字

#### InsuredBranchNumber

- 被保険者証枝番
- 任意項目扱い
- 後期高齢者医療制度以外では原則必要
- face XMLに存在する場合はその値をそのまま使用する

---

## 6. 登録元資格情報

照会番号登録時の資格情報には、**Dynamics内の古い保険情報ではなく、今回の face XML に含まれる最新資格情報を優先して使用する。**

face XML から取得する項目：

```text
InsurerNumber
InsuredCardSymbol
InsuredIdentificationNumber
InsuredBranchNumber
Birthdate
Name
NameKana
Sex1
```

このうち、照会番号登録XMLに使用するのは、

```text
InsurerNumber
InsuredCardSymbol
InsuredIdentificationNumber
InsuredBranchNumber
```

である。

---

## 7. face XML に ReferenceNumber がある場合

```text
face XML
↓
ReferenceNumberあり
↓
その値を PatientID として利用
↓
iCallManager.find
```

照会番号登録処理は不要。

---

## 8. face XML に ReferenceNumber がない場合

```text
face XML
↓
ReferenceNumberなし
↓
Dynamics COMで患者検索
↓
PatientID特定
↓
受付処理継続
↓
今回のface XML資格情報を使って照会番号単件登録
```

---

## 9. Dynamics COMによる患者検索

検索キーとして、face XMLから取得した情報を使用する。

初期実装では、

```text
Birthdate
+
NameKana
```

を主キーとする。

必要に応じて、

```text
Name
Sex1
```

も照合条件に追加する。

### 判定

```text
候補 0件
→ PATIENT_NOT_FOUND
→ 職員対応

候補 1件
→ PatientID確定
→ 自動処理継続

候補 2件以上
→ PATIENT_AMBIGUOUS
→ 自動確定しない
→ 職員対応
```

同姓同名・同生年月日の患者が存在する可能性を考慮し、複数候補時には絶対に自動選択しない。

---

## 10. 患者検索モデル

```csharp
public sealed class DynamicsPatientCandidate
{
    public string PatientId { get; init; } = "";
    public string Name { get; init; } = "";
    public string NameKana { get; init; } = "";
    public DateOnly BirthDate { get; init; }
    public string Sex { get; init; } = "";
}
```

推奨インターフェース：

```csharp
public interface IDynamicsPatientSearch
{
    Task<IReadOnlyList<DynamicsPatientCandidate>> SearchAsync(
        string nameKana,
        DateOnly birthDate,
        CancellationToken cancellationToken = default);
}
```

---

## 11. NameKana正規化

比較前に最低限以下を正規化する。

```text
全角/半角
空白除去
ひらがな/カタカナ統一
前後空白除去
```

ただし、過剰な曖昧一致は行わない。0件となった場合に職員対応へ回せるため、誤患者を拾うより安全側を優先する。

---

## 12. 登録対象モデル

```csharp
public sealed class ReferenceRegistrationTarget
{
    public string PatientId { get; init; } = "";
    public string ReferenceNumber { get; init; } = "";
    public string InsurerNumber { get; init; } = "";
    public string InsuredCardSymbol { get; init; } = "";
    public string InsuredIdentificationNumber { get; init; } = "";
    public string InsuredBranchNumber { get; init; } = "";
    public string ArbitraryFileIdentifier { get; init; } = "";
}
```

---

## 13. 単件登録サービス

```csharp
public interface IReferenceNumberRegistrationService
{
    Task<ReferenceRegistrationResult> RegisterAsync(
        ReferenceRegistrationTarget target,
        CancellationToken cancellationToken = default);
}
```

一括登録APIは設けない。

---

## 14. 登録結果モデル

```csharp
public sealed class ReferenceRegistrationResult
{
    public bool Success { get; init; }
    public string PatientId { get; init; } = "";
    public string ReferenceNumber { get; init; } = "";
    public string RequestFileName { get; init; } = "";
    public string ResponseFileName { get; init; } = "";
    public string SegmentOfResult { get; init; } = "";
    public string ProcessingResultStatus { get; init; } = "";
    public string ProcessingResultCode { get; init; } = "";
    public string ProcessingResultMessage { get; init; } = "";
    public DateTime CompletedAt { get; init; }
    public string ErrorMessage { get; init; } = "";
}
```

---

## 15. 登録処理フロー

```text
ReferenceRegistrationTarget作成
↓
OQSsiimm01req XML生成
↓
OQS\req へ配置
↓
対応する OQSsiimm01res を待機
↓
結果XML解析
↓
ProcessingResultStatus確認
↓
Completed / Failed
```

---

## 16. 状態

```csharp
public enum ReferenceRegistrationState
{
    Pending,
    RequestCreating,
    RequestSubmitted,
    ResultWaiting,
    Completed,
    Failed,
    Cancelled
}
```

---

## 17. 結果XML解析

結果XMLから最低限以下を取得する。

```text
SegmentOfResult
ErrorCode
ErrorMessage
ProcessingResultStatus
ProcessingResultCode
ProcessingResultMessage
ReferenceNumber
ArbitraryFileIdentifier
```

代表値：

```text
ProcessingResultStatus = 1
→ 正常終了

ProcessingResultStatus = 2
→ エラー
```

`SegmentOfResult` や `ErrorCode` も確認し、レスポンスファイルが存在しただけで成功扱いにしない。

---

## 18. req / res フォルダ

```text
OqsRoot
├─ req
├─ res
└─ trash
```

ReceptionAgentが照会番号登録で行う操作：

```text
req
→ 自分が生成した OQSsiimm01req を書き込む

res
→ OQSsiimm01res を読み取る
```

既存face XMLやDynamicsが使用するファイルは削除・移動しない。

---

## 19. ファイル書込み

要求ファイル生成時は、OQSが書込み途中ファイルを読まないようにする。

推奨：

```text
ReceptionAgentローカルWorkフォルダで完成ファイルを作成
↓
Flush / Close
↓
OQS reqへコピーまたは移動
```

---

## 20. 結果ファイル待機

```text
FileSystemWatcher
+
定期スキャン
```

を使用する。`FileSystemWatcher` のイベントだけに依存しない。

XML読込時は、他プロセスの処理を阻害しないようにする。

```csharp
new FileStream(
    path,
    FileMode.Open,
    FileAccess.Read,
    FileShare.ReadWrite | FileShare.Delete)
```

---

## 21. Kiosk受付との関係

照会番号登録は、Kiosk受付の完了条件にはしない。

```text
ReferenceNumberなし
↓
Dynamics COMでPatientID特定
↓
iCall受付
↓
患者受付完了
↓
照会番号登録をバックグラウンド実行
```

照会番号登録が失敗しても、その日の受付は完了できる設計にする。次回来院時に再試行可能とする。

---

## 22. エラー時運用

### 患者検索失敗

```text
PATIENT_NOT_FOUND
```

### 患者候補複数

```text
PATIENT_AMBIGUOUS
```

複数患者候補時は自動選択しない。

### 照会番号登録失敗

受付自体は継続・完了させる。

内部ログには以下を保存する。

```text
PatientId
RequestFileName
ProcessingResultCode
ProcessingResultMessage
Timestamp
```

---

## 23. 初回実装の受け入れテスト

1. face XMLに `ReferenceNumber` がないことを確認。
2. `Birthdate + NameKana` でDynamics COM検索。
3. 候補が1件だけであることを確認。
4. PatientIDを取得。
5. face XMLの最新資格情報から `OQSsiimm01req` を生成。
6. `req` へ配置。
7. `OQSsiimm01res` を取得して正常終了を確認。
8. 次回来院時のface XMLに `<ReferenceNumber>PatientID</ReferenceNumber>` が返ることを確認。
9. iCallManagerへ `find` を送り、該当患者・受付番号が一致することを確認する。

---

## 24. 実装優先順位

### Phase 1

```text
FaceXmlParser
ReferenceNumber有無判定
Birthdate / NameKana取得
```

### Phase 2

```text
Dynamics COM患者検索
0 / 1 / 複数件判定
```

### Phase 3

```text
ReferenceRegistrationTarget生成
OQSsiimm01req生成
```

### Phase 4

```text
req配置
OQSsiimm01res監視
結果解析
```

### Phase 5

```text
ReceptionWorkflow統合
バックグラウンド自動登録
```

---

## 25. Codex向け実装指示

初回実装では一括登録を実装しない。

実装対象は以下のみ。

```text
1患者
↓
face XMLにReferenceNumberなし
↓
Dynamics COMでPatientID一意検索
↓
OQSsiimm01req作成
↓
req配置
↓
OQSsiimm01res取得
↓
成功/失敗判定
```

既存の OQSDrug2 `BulkQualificationService.cs` から、以下の実装思想は流用してよい。

```text
Shift_JIS XmlWriter
req/res監視
ファイル連番
XML loader
FileShare.ReadWrite配慮
CancellationToken
ログ処理
結果コード・メッセージ保持
```

ただしプロトコル自体は Bulk 用の `OQSmuimm*` ではなく、単件用の `OQSsiimm01req / OQSsiimm01res` を使用する。

---

## 26. 仕様上の基本原則

```text
患者マスターの正本 = Dynamics

PatientIDの高速取得 = face XMLのReferenceNumber

ReferenceNumber未登録時 = Dynamics COM検索

照会番号登録に使う資格情報 = 今回のface XML

複数患者候補 = 自動確定しない

照会番号登録失敗 = 受付自体は止めない

登録方式 = 単件のみ
```

---

## 27. 参考

単件照会番号登録は `OQSsiimm01req` / `OQSsiimm01res` を使用する。要求側は `ReferenceNumberRegistrationInfo` に照会番号と資格情報を格納する。

実装前に、実環境で返却される `OQSsiimm01res` の実ファイルを1件取得し、結果XMLのタグ構造・ファイル名対応を最終確認すること。
