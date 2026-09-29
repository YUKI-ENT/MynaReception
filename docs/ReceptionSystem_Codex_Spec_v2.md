# 受付自動化システム 仕様書（Codex向け）

## 1. 目的

耳鼻科診療所の受付業務を段階的に自動化する。

現在は以下の処理を職員が行っている。

- iCallで順番予約
- 来院時に予約番号を確認
- iCallで「来院確認」
- iCallからDynamicsへ「連携」
- 保険証またはマイナンバーカード確認
- 初診患者への問診案内
- 発熱患者の別室案内
- 受付番号の発券

最終的には、患者がキオスク端末を操作し、

1. 発熱・感染症状チェック
2. 初診 / 再診確認
3. マイナンバーカード受付
4. 患者ID特定
5. iCall予約照合
6. iCall来院確認
7. Dynamics受付
8. 受付番号発券
9. 問診案内

までを可能な範囲で自動化する。

将来的にはiCall自体を自前予約システムへ置き換えられる設計とし、他社予約システムへの差し替えも可能にする。

---

# 2. 全体構成

アプリケーションは以下の3本を基本構成とする。

```text
┌──────────────────────────────┐
│ KioskApp                     │
│ 患者操作用キオスク           │
│                              │
│ ・発熱確認                   │
│ ・初診/再診                  │
│ ・マイナ案内                 │
│ ・診察券/氏名生年月日入力    │
│ ・問診案内                   │
└──────────────┬───────────────┘
               │
               ▼
┌──────────────────────────────┐
│ ReceptionAgent               │
│ VLAN3（Dynamics側）          │
│                              │
│ ・face XML監視               │
│ ・照会番号取得               │
│ ・Dynamics資格確認参照       │
│ ・PatientID確定              │
│ ・iCallManagerへの要求       │
│ ・Dynamics受付処理           │
└──────────────┬───────────────┘
               │ VLAN3 → VLAN1
               ▼
┌──────────────────────────────┐
│ iCallManager                 │
│ VLAN1（iCall端末）           │
│                              │
│ ・iCall UI Automation        │
│ ・患者ID→受付番号取得        │
│ ・番号札発券                 │
│ ・来院確認                   │
│ ・Dynamics連携               │
└──────────────────────────────┘
```

---

# 3. ネットワーク前提

## VLAN構成

```text
VLAN1 外部ネットワーク
192.168.10.0/24
・iCall端末
・iCallManager

VLAN2 オンライン資格確認
10.0.0.0/24
・顔認証カードリーダー
・OQS関連
・face XML

VLAN3 閉鎖系
192.168.253.0/24
・Dynamics
・ReceptionAgent
```

現在許可されている通信:

```text
VLAN3 → VLAN2 : SMB 許可
VLAN3 → VLAN1 : SMB 許可
```

原則として、VLAN1からVLAN3/VLAN2へ接続させない。

iCallManagerは外部ネットワーク側に存在するため、患者資格情報やface XMLをVLAN1へ直接持ち込まず、VLAN3上のReceptionAgentで患者IDを確定してから必要最小限の情報だけをiCallManagerへ送る。

---

# 4. アプリケーション1: iCallManager

## 4.1 目的

iCallの管理画面をWindows UI Automation経由で操作し、iCallを疑似API化する。

iCallManager以外のアプリはiCallのHTML、IEモード、UI Automationの詳細を意識しない。

## 4.2 動作環境

- Windows
- .NET 10
- WinForms
- iCallが動作している既存端末
- Edge IEモード
- COM参照: `UIAutomationClient`
- `Embed Interop Types = False`

通常のWebDriver/SeleniumではiCallがサポートページへ遷移するため、既存IEモード画面をUI Automationで操作する。

## 4.3 iCall起動

レジストリ:

```text
HKCU\Software\NOCAspRsv
```

主要値:

```text
login_id
login_pass
login_saved
lastLogin
```

`lastLogin` を使用して以下を起動可能。

```text
https://secure.atat.jp/login/login.php?mode=appmode&lastlogin={lastLogin}
```

起動例:

```text
msedge.exe --app="https://secure.atat.jp/login/login.php?mode=appmode&lastlogin=gp200628"
```

ID / Passwordは既存環境で自動入力され、数秒後に自動ログインする。

初期版は「すでにiCallが起動・ログイン済み」であることを前提としてよい。

## 4.4 iCallManagerの責務と論理API

iCallManagerは**受付時に必要な操作だけ**を担当する。

対象機能:

1. PatientIDから当日のiCall予約を検索し、受付番号を取得する
2. 受付番号の番号札を発券する
3. 対象患者の「来院確認」を行う
4. 対象患者の「連携」を実行し、Dynamics側の受付連携へ送る

`案内` と `保留` は診察進行を変更する操作であり、Kiosk受付の責務ではないため、**受付自動化APIから除外する**。
iCall画面上に存在していても、初期実装では操作対象にしない。

低レベルAPIの例:

```csharp
public interface IICallService
{
    Task<ICallReservation?> FindReservationAsync(
        string patientId,
        CancellationToken cancellationToken = default);

    Task<PrintTicketResult> PrintTicketAsync(
        string patientId,
        CancellationToken cancellationToken = default);

    Task<OperationResult> MarkArrivedAsync(
        string patientId,
        CancellationToken cancellationToken = default);

    Task<OperationResult> LinkToDynamicsAsync(
        string patientId,
        CancellationToken cancellationToken = default);
}
```

上位層からUI Automation要素、画面上の行番号、座標等を渡してはならない。
各操作時にiCallManager自身が最新の受付一覧を再読込し、PatientIDから対象行を再特定して実行する。

高レベルAPIとして一括受付を用意してもよい。

```csharp
public interface IICallReceptionService
{
    Task<ICallCheckInResult> CheckInAsync(
        string patientId,
        CancellationToken cancellationToken = default);
}
```

内部処理の基本順序:

```text
FindReservation
↓
受付番号取得
↓
MarkArrived
↓
LinkToDynamics
↓
PrintTicket
```

ただし、**既存サーマルプリンタがiCallのどの操作をトリガーに印刷するかは現時点で未確定**である。
そのため `PrintTicketAsync()` は独立した機能として実装し、既存iCall印刷・NOCAspRsv/CITIZEN経由・自前印刷のいずれにも差し替え可能にする。

将来自前予約システムへ移行する場合に備え、上位の受付ワークフローからはiCall固有実装を隠蔽する。

## 4.5 患者行モデル

```csharp
public sealed class ICallPatientRow
{
    public string ReceptionNo { get; set; } = "";
    public string PatientId { get; set; } = "";
    public string PatientName { get; set; } = "";
    public string InternalId { get; set; } = "";

    public bool CanMarkArrived { get; set; }
    public bool CanLink { get; set; }

    // 画面解析・診療側機能の調査用として取得してもよいが、
    // Kiosk受付APIからは操作しない。
    public bool HasHoldButton { get; set; }
    public bool HasGuideButton { get; set; }
}
```

UI AutomationのCOMオブジェクト自体は上位層へ露出させない。

## 4.6 確認済みUI Automation情報

### iCall管理画面

管理画面タイトル例:

```text
[ ゆうき耳鼻咽喉科 ] - 管理画面
```

内部IEフレーム例:

```text
http://s300.atat.jp/yuuki/admin/forward.php?fid=daylist&vid=daylist06&nm_sn=na000001
```

### 受付番号

例: `36`

```text
Name: "36"
ControlType: UIA_TextControlTypeId
FrameworkId: InternetExplorer
```

親に以下が存在:

```text
"36" リンク
"" 項目
"" 表
```

`LegacyIAccessible.Value` / `Value.Value` に以下のようなURLが入る。

```text
http://s300.atat.jp/yuuki/admin/forward.php?fid=daylist&vid=daylist06&nm_sn=na000001#edit
```

### 診察券番号 / PatientID

一部で以下のようなText要素を確認。

```text
Name: "11"
ControlType: UIA_TextControlTypeId
LegacyIAccessible.Role: 編集可能なテキスト
State: 読み取り専用
```

Ancestors:

```text
"11" 項目
"" 表
...
```

注意: 同じ数字が複数列に存在する場合があるため、単純に数値だけでPatientIDと判定しない。必ず同一行内の列位置・周辺要素・患者名との関係で特定する。

### 来院確認

```text
Name: "来院確認"
ControlType: UIA_ButtonControlTypeId
HelpText: 患者名
LegacyIAccessible.Description: 患者名
IsInvokePatternAvailable: true
```

### 保留

```text
Name: "保留"
ControlType: UIA_ButtonControlTypeId
HelpText: 患者名
IsInvokePatternAvailable: true
```

### 案内

```text
Name: "案内"
ControlType: UIA_ButtonControlTypeId
AutomationId: "guid0157760"
HelpText: 患者名
IsInvokePatternAvailable: true
```

`AutomationId`末尾の数値はiCall内部レコードIDである可能性が高い。

### 連携

```text
Name: ""
ControlType: UIA_ButtonControlTypeId
AutomationId: "chk57760"
IsInvokePatternAvailable: true
```

Nameが空のため、以下のいずれかで識別する。

1. AutomationIdが `chk` で始まる
2. 同一患者行の位置関係
3. 同一内部ID
4. HTML側の仕様との照合

## 4.7 行解析方針

「来院確認ボタンが存在する患者だけ」を列挙してはいけない。患者状態によりボタンがない行も存在するため、受付一覧テーブルそのものを走査する。

```text
順番待ちテーブル
  ↓
各行を取得
  ↓
行内のText / Buttonを収集
  ↓
受付番号
患者ID
患者名
連携
来院確認
保留
案内
を対応付ける
```

UI AutomationではHTMLの`<tr>`が必ずしもGridItemとして見えないため、IE/MSAAの「項目」「表」の階層を利用する。

まずデバッグ用に各候補行内の全要素を列挙し、列構造を確定すること。

## 4.8 初期開発フェーズ

### Phase I-1 読取のみ

- iCall管理画面検出
- 全受付行の列挙
- 受付番号取得
- 患者ID取得
- 患者名取得
- DataGridViewへ表示
- 一切ボタンを押さない

### Phase I-2 受付操作

患者ID一致を確認後、受付用途として以下のみを操作可能にする。

- 来院確認
- Dynamics連携
- 発券

`案内` と `保留` は受付自動化から除外する。
特に `案内` は待ち順番・患者向け案内番号・通知に影響するため、Kiosk受付では自動実行しない。

誤操作防止のため、開発中は実行前に確認ダイアログを表示できるDebugModeを持つ。

---

# 5. アプリケーション2: ReceptionAgent

## 5.1 目的

VLAN3上で動作する受付処理の中核。患者識別、Dynamics連携、OQS face XML監視、iCallManagerとの通信を担当する。

## 5.2 OQS face XML

設定ファイル:

```text
C:\ProgramData\OQS\OQSComApp\config\UserDefinitionForFace.property
```

主な設定:

```text
FaceDataDir=C:\OQS\face
OutputFlg=true
```

初期実装では既存設定を変更しない。

```text
OQSComApp
   ↓
C:\OQS\face
   ├→ Dynamics
   └→ ReceptionAgentが監視
```

ReceptionAgentは元XMLを削除・移動・ロックしない。

## 5.3 face XML監視

`FileSystemWatcher`だけに依存せず、定期フォルダスキャンを併用する。

- Created
- Renamed
- Changed

を監視。

さらに500ms～1秒ごとに未処理ファイルを再スキャンする。

XML生成直後は書込途中の可能性があるため、Open不可・XML parse失敗・ファイルサイズ変化中の場合は数百ms待って再試行する。

## 5.4 Kioskと顔認証カードリーダーの占有・紐付け制御

### 5.4.1 問題

Kiosk画面と顔認証カードリーダーは独立して動作する。

そのため、単純に

```text
Kioskが「顔認証待ち」
↓
次にfaceフォルダへ来たXML
↓
その患者をKiosk受付患者とみなす
```

という実装は禁止する。

Kiosk利用者Aが顔認証待ちの間に、窓口患者Bが別の顔認証カードリーダーを使用した場合、
BのXMLをAの受付に誤って結び付ける危険がある。

### 5.4.2 基本原則

- 顔認証カードリーダーは可能な限り**Kiosk専用機をKioskのすぐ横に配置**する
- Dynamicsへの既存OQS連携経路は常時維持する
- ReceptionAgentがすべてのface XMLを「Kiosk受付用」と解釈してはならない
- Kioskが明示的に顔認証待ちへ入ったときだけ `FaceSession` を開始する
- FaceSession中でも、**最初に来たXMLを無条件採用しない**
- KioskセッションとXMLが十分に一致した場合だけ、そのXMLをKiosk受付に採用する
- 一致しないXMLはKiosk処理では無視し、通常のDynamics処理には干渉しない
- face XMLそのものをVLAN1のiCallManagerへ送らない
- iCallManagerへ渡すのはReceptionAgentが確定したPatientIDと受付操作要求だけとする

### 5.4.3 FaceSession

例:

```csharp
public sealed class FaceSession
{
    public Guid SessionId { get; init; }
    public DateTime StartedAt { get; init; }
    public DateTime ExpiresAt { get; init; }

    public DateOnly? ExpectedBirthDate { get; init; }
    public string? ExpectedPatientId { get; init; }
    public string? ExpectedReceptionNo { get; init; }

    public bool Consumed { get; set; }
}
```

状態:

```text
Idle
↓
Kioskで「マイナンバーカードを使う」
↓
FaceSession Armed
↓
「カードリーダーにマイナンバーカードを置いてください」
↓
候補XML到着
↓
セッション情報と照合
├─ 一致 → Consumed → PatientID確定へ
└─ 不一致 → Kioskでは無視し、待機継続
↓
Timeout / Cancel / Success
↓
Idle
```

FaceSessionは1回限りとし、成功時には即時 `Consumed=true` として終了する。
タイムアウト時間は初期値60秒程度とし、設定可能にする。

### 5.4.4 XML採用条件

最低でも以下を組み合わせる。

- XML生成時刻がFaceSession開始後である
- XMLがFaceSessionの有効時間内に到着している
- Kioskで事前取得した生年月日等とXML内容が一致する
- 既にPatientID候補がある場合はPatientID / 照会番号との整合性を確認する
- 同一XMLを複数セッションへ割り当てない

**時刻だけで採用してはならない。**

将来、face XML内に顔認証カードリーダー名・端末識別子・顔認証アカウント等、
発生元リーダーを識別できる情報が確認できた場合は、それを最優先の照合条件として追加する。

### 5.4.5 Dynamicsへのスルー

初期実装ではOQS/Dynamicsの既存経路を変更しない。

```text
OQSComApp
   ↓
face
   ├────────→ Dynamics（常時・既存動作）
   │
   └────────→ ReceptionAgent（読み取り専用監視）
```

ReceptionAgentはFaceSessionがない場合、そのXMLをKiosk受付には使用しない。

```text
FaceSessionなし
    → Kiosk処理では無視
    → Dynamicsは通常処理

FaceSessionあり
    → XMLを候補として検証
       ├─ 一致 → Kiosk受付に採用
       └─ 不一致 → Kioskでは無視
    → Dynamicsはどちらの場合も通常処理
```

これによりReceptionAgentやKioskAppが停止していても、通常のオンライン資格確認業務を妨げない。

### 5.4.6 中間フォルダ方式

将来どうしても直接監視で取りこぼしが生じる場合のみ、

```text
OQSComApp
↓
中間フォルダ
↓
分配処理
├→ Dynamics用face
└→ ReceptionAgent
```

を検討する。

ただしこの方式では分配アプリがDynamics連携の単一障害点になるため、初期実装では採用しない。

## 5.5 患者IDの決定方法

### 第一優先: face XMLの照会番号

```text
face XML
↓
照会番号あり
↓
PatientIDとして利用
```

### 第二優先: Dynamics資格確認テーブル

```text
face XML
↓
Dynamics取込待ち
↓
資格確認テーブル
↓
Dynamicsが付与したカルテ番号
```

Dynamics側が生年月日・保険・氏名・照会番号などで既存患者照合を行うため、自作側で同じ照合ロジックを再実装しない。

### 第三優先: 職員確認

- 複数候補
- 新患
- 不一致
- 資格確認エラー

は自動決定しない。

## 5.6 Dynamics連携

必要機能候補:

```text
GetLatestQualificationResult()
GetPatientId()
CheckInPatient()
FindPatientByCardNo()
FindPatientByNameAndBirthDate()
```

新患登録の完全自動化は初期版では対象外。

## 5.7 iCallManagerとの通信

セキュリティ上、`VLAN3 → VLAN1` のみで成立する構造とする。

初期実装候補:

```text
SMB request/response
```

例:

```text
\\iCallPC\iCallBridge\request\
\\iCallPC\iCallBridge\response\
```

要求JSON例:

```json
{
  "requestId": "20260928-000001",
  "action": "find",
  "patientId": "40273"
}
```

返答例:

```json
{
  "requestId": "20260928-000001",
  "success": true,
  "patientId": "40273",
  "receptionNo": "36"
}
```

将来HTTPS/TCP/WebSocketへ交換可能なインターフェースとする。

---

# 6. アプリケーション3: KioskApp

## 6.1 目的

患者自身が操作する受付UI。バックエンドのDynamics・iCall・OQSの詳細は持たず、ReceptionAgentへ受付要求を送り状態表示だけ行う。

## 6.2 基本画面

### 1. 開始

```text
受付を開始します
```

### 2. 発熱・感染症状確認

```text
本日発熱がありますか？
感染症が疑われる症状がありますか？
```

該当時:

```text
通常受付停止
↓
別室/職員案内
```

### 3. 初診 / 再診

```text
当院を受診したことがありますか？
```

患者回答は補助情報として扱い、PatientID確認後のシステム判定を優先する。

### 4. 患者識別

優先順位:

```text
1. マイナンバーカード
2. 診察券
3. 氏名 + 生年月日
```

#### マイナあり

Kioskは顔認証カードリーダーと直接連携しているとは仮定しない。
まずReceptionAgentにFaceSession開始を要求し、FaceSessionが有効になってから患者へカード操作を案内する。

```text
Kioskで最低限の照合情報を取得
例: 生年月日、既知なら診察券番号等
↓
ReceptionAgentへ BeginFaceSession
↓
FaceSession = Armed
↓
Kiosk表示:
「こちらの顔認証カードリーダーに
 マイナンバーカードを置いてください」
↓
候補face XML到着
↓
ReceptionAgentがセッション情報と照合
├─ 一致 → PatientID確定
└─ 不一致 → そのXMLはKioskでは無視して待機継続
```

Kioskは「次に来たXML」を自分の患者として扱わない。

#### マイナなし・診察券あり

```text
診察券番号入力 / バーコード等
↓
Dynamics PatientID検索
```

#### 診察券なし

```text
氏名
生年月日
↓
患者候補検索
```

複数候補なら職員介入。

---

# 7. 受付ワークフロー

```text
受付開始
↓
発熱・感染症状確認
↓
初診/再診申告
↓
患者識別
↓
PatientID確定
↓
iCall予約照合
↓
予約あり？
├─ YES
│    ↓
│  受付番号取得
│    ↓
│  来院確認
│    ↓
│  iCall→Dynamics連携
│    ↓
│  発券
│
└─ NO
     ↓
   飛び込み受付 / 職員確認
```

## 7.1 マイナ受付 再診

```text
KioskApp
↓
最低限の照合情報取得
↓
ReceptionAgentへFaceSession開始要求
↓
FaceSession Armed
↓
マイナ案内
↓
Kiosk専用顔認証カードリーダー
↓
OQS face XML
↓
ReceptionAgent
↓
FaceSessionとの照合
↓
一致したXMLのみ採用
↓
照会番号取得
↓
PatientID確定
↓
iCallManagerへ検索要求
↓
iCall PatientID一致
↓
受付番号取得
↓
来院確認
↓
連携
↓
Dynamics受付
↓
発券
↓
受付完了
```

照会番号がない場合:

```text
face XML
↓
Dynamics取込待ち
↓
資格確認テーブル
↓
PatientID取得
↓
以降同じ
```

## 7.2 マイナなし 再診

```text
診察券あり？
├─ YES
│    ↓
│ PatientID取得
│
└─ NO
     ↓
 氏名+生年月日
     ↓
 Dynamics患者検索
```

PatientID確定後は通常ルートへ。

## 7.3 初診

初期版では職員介入を前提とする。

```text
マイナ
↓
資格確認
↓
PatientIDなし
↓
新患候補
↓
Web問診または紙問診
↓
職員が新規カルテ作成
↓
受付
```

将来:

```text
マイナ在宅Web
↓
事前資格確認
↓
Web問診
↓
事前カルテ作成補助
```

---

# 8. 問診フロー

状態:

```text
NotRequired
PaperRequired
WebCompleted
WebRequired
```

原則:

```text
初診
↓
Web問診済？
├ YES → 続行
└ NO
   ├ Web問診
   └ 紙問診
```

予約システムを将来自前化した場合、予約完了直後にWeb問診へ誘導する。

---

# 9. 発券

初期版:

```text
iCall受付番号
↓
iCallManager
↓
CITIZENサーマルプリンタ
```

既存NOCAspRsvの印刷経路解析は必須としない。受付番号さえ取得できれば自作印刷を許容する。

---

# 10. 受付状態モデル

```csharp
public enum ReceptionState
{
    Started,
    FeverCheck,
    FeverDetected,

    IdentityWaiting,
    FaceSessionArmed,
    FaceXmlWaiting,
    FaceXmlCandidateReceived,
    FaceXmlMatched,
    FaceSessionTimedOut,
    IdentityConfirmed,
    IdentityFailed,

    ReservationSearching,
    ReservationFound,
    ReservationNotFound,

    QualificationWaiting,
    QualificationConfirmed,
    QualificationFailed,

    DynamicsCheckIn,
    ICallArrived,
    ICallLinked,

    TicketPrinting,
    TicketPrinted,

    QuestionnaireRequired,
    QuestionnaireCompleted,

    Completed,
    StaffAssistance,
    Error
}
```

状態遷移をログに残す。

---

# 11. 安全設計

## 11.1 自動操作前の再確認

最低限:

```text
PatientID一致
+
受付行の患者名一致
```

を確認する。PatientIDだけの曖昧一致は禁止。

## 11.2 UI Automation誤操作防止

- 座標クリックを基本使用しない
- `InvokePattern`を使用
- 行の患者IDを確定してからボタンを探す
- 全画面から最初に見つかった「来院確認」を押さない
- DebugModeでは実操作を無効化可能とする

## 11.3 OQSファイルとFaceSession安全条件

face XMLは以下を禁止:

- 削除
- 移動
- 排他的ロック
- 内容変更

読み取り/コピーのみ。

さらに以下を必須とする。

- FaceSessionがないXMLをKiosk患者へ割り当てない
- FaceSession中でも「最初のXML」という理由だけで採用しない
- 生年月日・PatientID候補・照会番号・時刻など複数条件で照合する
- 不一致XMLはKioskでは無視し、Dynamics処理には干渉しない
- FaceSessionは成功・キャンセル・タイムアウトのいずれかで必ず終了する
- 1つのXMLを複数のKioskセッションに使用しない

## 11.4 ネットワーク

VLAN1からVLAN3/VLAN2への新規アクセスを極力作らない。PatientID等の必要最小限情報のみVLAN3からVLAN1へ渡す。

---

# 12. 開発フェーズ

## Phase 1: iCallManager 読取

- UI Automation接続
- 管理画面検出
- 受付一覧全行取得
- 受付番号 / PatientID / 氏名取得
- DataGridView表示

実操作なし。

## Phase 2: iCall受付操作

- 患者IDから受付番号取得
- 来院確認
- Dynamics連携
- 発券インターフェース
- DebugMode

`案内` と `保留` は受付自動化対象外。

## Phase 3: ReceptionAgent / face XML / FaceSession

- faceフォルダ監視
- XMLコピー
- XML解析
- FaceSession開始/終了
- FaceSessionと候補XMLの照合
- 別患者XMLの除外
- タイムアウト処理
- 照会番号取得

## Phase 4: Dynamics連携

- 資格確認テーブル参照
- PatientID確定
- 照会番号なしのフォールバック

## Phase 5: ReceptionAgent ↔ iCallManager

- request/response
- 患者検索
- 受付番号返却
- 来院確認
- Dynamics連携
- 発券要求

## Phase 6: KioskApp

- 発熱
- 初診/再診
- マイナ
- 診察券
- 氏名生年月日
- 状態表示

## Phase 7: 発券

- サーマルプリンタ連携

## Phase 8: 初診自動化

- Web問診
- マイナ在宅Web
- 新患カルテ作成補助

---

# 13. Codexへの実装方針

Codexは以下を守ること。

1. 一度に全機能を作らない
2. 読取機能を先に完成させる
3. UI Automation操作は患者行を特定してから行う
4. 実環境でのボタン操作は明示的に有効化する
5. Dynamics/OQS/iCallを直接結合しすぎない
6. 各外部システムをProvider/Adapterとして分離する
7. ログを詳細に残す
8. 失敗時は自動継続せずStaffAssistanceへ遷移する
9. PatientID誤同定を最優先で防止する
10. 将来自前予約システムに差し替えられる構造を維持する

---

# 14. 推奨プロジェクト構成

```text
ReceptionSystem.sln

src/
  Shared/
    Models/
    Contracts/
    Logging/

  iCallManager/
    UI/
    ICall/
      ICallAutomation.cs
      ICallRowParser.cs
      ICallReservationProvider.cs
    Bridge/
    Printing/

  ReceptionAgent/
    Oqs/
      FaceXmlWatcher.cs
      FaceXmlParser.cs
      FaceSession.cs
      FaceSessionManager.cs
      FaceXmlMatcher.cs
    Dynamics/
      DynamicsProvider.cs
    Bridge/
      ICallBridgeClient.cs
    Workflow/
      ReceptionWorkflow.cs

  KioskApp/
    UI/
    WorkflowClient/
```

---

# 15. 今後追加で取得したいiCall情報

以下はまだ完全には確定していないため、実患者がいる時間帯にInspectで追加確認する。

- 診察券番号セルのUI Automation階層
- 患者名セルのUI Automation階層
- 同一行を安定して判定できる最小祖先
- 連携ボタンとPatientIDの対応方法
- 来院確認後・連携後のUI構造
- 既存サーマルプリンタがどの操作を契機に番号札を印刷するか
- NOCAspRsv / CITIZEN印刷経路
- 予約なし患者の追加方法
- 受付番号の列位置
- iCall内部レコードIDとAutomationIdの対応規則
- face XML内に発生元カードリーダーを識別できるフィールドがあるか
- 複数の顔認証カードリーダーが同一faceフォルダへ出力する場合の識別方法

Phase 1の行解析時にデバッグログを出して確定する。
face XMLについては匿名化した実ファイルを用いて項目を確認する。

---

# 16. 最終目標

現在:

```text
iCall
+
Dynamics
+
OQS
+
職員操作
```

将来:

```text
                 ┌─ OQS / マイナ
                 │
KioskApp
   ↓
Reception Core ──┼─ Dynamics
                 │
                 └─ ReservationProvider
                       ├ iCall
                       ├ 自前予約
                       └ 他社予約
```

外部システムをAdapterとして切り離し、

```text
患者識別
予約
資格確認
電子カルテ
問診
発券
```

を独立モジュールとして扱う。

これにより、iCall廃止後もKioskAppとReceptionAgentの大部分を再利用できる構成を目指す。
