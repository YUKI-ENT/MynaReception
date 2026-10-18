using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using iCallManager.Core;

namespace iCallManager.ICall;

/// <summary>Operates only the already verified IE dummy-assignment document.</summary>
internal sealed class IEAssignmentDom : IDisposable
{
    private readonly List<object> owned;
    private readonly object document;
    private readonly string expectedUrl;

    internal IEAssignmentDom(object document, string expectedUrl) : this(document, expectedUrl, [document]) { }
    private IEAssignmentDom(object document, string expectedUrl, List<object> owned)
    { this.document = document; this.expectedUrl = expectedUrl; this.owned = owned; }

    private dynamic Own(object value) { owned.Add(value); return value; }
    private static void Release(List<object> objects)
    {
        foreach (object value in Enumerable.Reverse(objects))
            if (Marshal.IsComObject(value)) Marshal.ReleaseComObject(value);
    }
    public void Dispose() => Release(owned);

    internal static void Execute(nint server, string internalId, string patientId, string action, Func<bool> mayOperate, Action? beforeCommitDispatch = null)
    {
        if (action is not ("search" or "commit")) throw new ArgumentOutOfRangeException(nameof(action));
        OnSta(() =>
        {
            using var dom = Open(server, internalId);
            if (action == "search") dom.Search(patientId, mayOperate);
            else dom.Commit(patientId, mayOperate, beforeCommitDispatch);
            return true;
        });
    }

    internal static string Inspect(nint server, string internalId) => OnSta(() =>
    {
        using var dom = Open(server, internalId);
        return dom.Describe();
    });

    internal static string InspectLink(nint server, string internalId) => OnSta(() =>
    {
        using var dom = Open(server, internalId, reservationList: true);
        return dom.DescribeLink(internalId);
    });

    internal static void ExecuteLink(nint server, Reservation expected, Func<bool> mayOperate, Action? beforeDispatch = null) => OnSta(() =>
    {
        using var dom = Open(server, expected.InternalId, reservationList: true);
        dom.Link(expected, mayOperate, beforeDispatch);
        return true;
    });

    internal static void VerifyLinkHandler(string outerHtml, Reservation expected)
    {
        var attribute = Regex.Match(outerHtml, """\bonmousedown\s*=\s*(?:"([^"]*)"|'([^']*)')""", RegexOptions.IgnoreCase);
        string handler = System.Net.WebUtility.HtmlDecode(attribute.Groups.Cast<Group>().Skip(1).FirstOrDefault(g => g.Success)?.Value ?? "");
        const string literal = """(?:"(?:\\.|[^"\\])*"|'(?:\\.|[^'\\])*')""";
        var call = Regex.Match(handler, @"\A\s*changeChkFlag\s*\(\s*(?<args>" + literal + @"(?:\s*,\s*" + literal + @"){13})\s*\)\s*;?\s*\z");
        if (!attribute.Success || !call.Success)
            throw new BridgeException("link_handler_unverified", "連携ボタンのonmousedown=changeChkFlag(14引数)を確認できません。");
        // Check only literal identifiers. Never evaluate handler text, invent
        // arguments, or call the integration client separately from iCall.
        var args = Regex.Matches(call.Groups["args"].Value, literal).Select(m => m.Value[1..^1]).ToArray();
        if (args[0] != expected.InternalId || args[2] != expected.InternalId || args[3] != expected.ReceptionNo ||
            !string.IsNullOrWhiteSpace(expected.PatientId) && expected.PatientId != "-" && args[4] != expected.PatientId)
            throw new BridgeException("identity_changed", "連携イベントの受付番号・内部予約ID・診察券番号が対象行と一致しません。");
    }

    internal void Link(Reservation expected, Func<bool> mayOperate, Action? beforeDispatch = null)
    {
        VerifyDocument();
        if (!Regex.IsMatch(expected.InternalId, @"\A[0-9]+\z") || expected.IsUnassignedDummy)
            throw new BridgeException("link_handler_unverified", "連携対象の内部予約IDまたは患者割当状態を確認できません。");
        dynamic doc = document;
        dynamic inputs = Own((object)doc.getElementsByTagName("input"));
        var matches = new List<object>();
        for (int i = 0; i < (int)inputs.length; i++)
        {
            dynamic input = Own((object)inputs.item(i));
            if ((Convert.ToString(input.id) ?? "") == "chk" + expected.InternalId) matches.Add((object)input);
        }
        if (matches.Count != 1) throw new BridgeException("link_dom_not_unique", "対象連携ボタンのHTMLを1件に特定できません。");
        dynamic button = matches[0];
        if ((Convert.ToString(button.type) ?? "").ToLowerInvariant() != "button")
            throw new BridgeException("link_handler_unverified", "連携ボタンのHTML形式が診断済みの定義と異なります。");
        string value = Convert.ToString(button.value) ?? "";
        if (value == "〆") return; // Changed to ON since the UIA capture: never toggle it OFF.
        if (value != "") throw new BridgeException("link_state_unverified", "HTMLの連携ボタンが空欄・〆のどちらでもありません。");
        if ((bool)button.disabled) throw new BridgeException("action_unavailable", "HTMLの連携ボタンが無効です。");
        string className = Convert.ToString(button.className) ?? "";
        if (className is not ("btCheckOff" or "btCheckOffOver"))
            throw new BridgeException("link_state_unverified", "連携OFFのHTMLクラスを確認できません。");
        VerifyLinkHandler(Convert.ToString(button.outerHTML) ?? "", expected);
        dynamic mouseEvent = Own((object)doc.createEventObject());
        mouseEvent.button = 1;
        CheckAllowed(mayOperate);
        beforeDispatch?.Invoke();
        OperationalLog.Write("link_button_dispatch", new { expected.ReceptionNo, expected.InternalId,
            method = "MSHTML.fireEvent(onmousedown)", handler = "changeChkFlag" });
        button.fireEvent("onmousedown", (object)mouseEvent);
    }

    private static T OnSta<T>(Func<T> read)
    {
        Exception? failure = null;
        T result = default!;
        string? requestId = OperationalLog.RequestId;
        // MSHTML frame/window interfaces require an STA. UIA remains on its
        // existing MTA worker, which waits here so operations stay serialized.
        var thread = new Thread(() =>
        {
            try
            {
                OperationalLog.RequestId = requestId;
                result = read();
            }
            catch (Exception ex) { failure = ex; }
            finally { OperationalLog.RequestId = null; }
        }) { IsBackground = true, Name = "iCall HTML assignment" };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();
        if (failure is not null) System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(failure).Throw();
        return result;
    }

    internal static bool MatchesReservationUrl(string url, string internalId)
    {
        if (internalId.Length == 0 || !Uri.TryCreate(url, UriKind.Absolute, out var uri) ||
            uri.Scheme is not ("http" or "https")) return false;
        var parameters = uri.Query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries)
            .Select(p => p.Split('=', 2)).ToArray();
        bool Is(string key, string expected)
        {
            var values = parameters.Where(p => Uri.UnescapeDataString(p[0]) == key).ToArray();
            return values.Length == 1 && values[0].Length == 2 && Uri.UnescapeDataString(values[0][1]) == expected;
        }
        return Is("fid", "daylist") && Is("vid", "fmdmytrn") && Is("rsv_sn", internalId);
    }

    internal static bool MatchesListUrl(string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https")) return false;
        var parameters = uri.Query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries)
            .Select(p => p.Split('=', 2)).ToArray();
        bool Is(string key, string expected)
        {
            var values = parameters.Where(p => Uri.UnescapeDataString(p[0]) == key).ToArray();
            return values.Length == 1 && values[0].Length == 2 && Uri.UnescapeDataString(values[0][1]) == expected;
        }
        return Is("fid", "daylist") && Is("vid", "daylist06");
    }

    internal static IEAssignmentDom Open(nint server, string internalId, bool reservationList = false)
    {
        var objects = new List<object>();
        BridgeException Failure(string suffix, string message) =>
            new((reservationList ? "link" : "assignment") + "_dom_" + suffix, message);
        try
        {
            if (server == 0 || NativeClassName(server) != "Internet Explorer_Server")
                throw Failure("unavailable", "対象画面のInternet Explorer_Serverを特定できません。[dom-v1]");
            uint message = RegisterWindowMessage("WM_HTML_GETOBJECT");
            if (message == 0 || SendMessageTimeout(server, message, 0, 0, 2, 2000, out var result) == 0 || result == 0)
                throw Failure("unavailable", "IEからHTML文書を取得できません。[dom-v1]");
            var iid = new Guid("332C4425-26CB-11D0-B483-00C04FD90119"); // IHTMLDocument2
            int hr = ObjectFromLresult(result, ref iid, 0, out object root);
            if (hr < 0) Marshal.ThrowExceptionForHR(hr);
            objects.Add(root);
            var matches = new List<object>();
            int documents = 0;
            void Visit(object value, int depth)
            {
                if (++documents > 20 || depth > 8)
                    throw Failure("not_unique", "対象HTML文書のフレーム構造を確認できません。[dom-v1]");
                dynamic doc = value;
                string url = Convert.ToString(doc.URL) ?? "";
                if (reservationList ? MatchesListUrl(url) : MatchesReservationUrl(url, internalId)) matches.Add(value);
                object framesObject = doc.frames;
                objects.Add(framesObject);
                dynamic frames = framesObject;
                int length = (int)frames.length;
                for (int i = 0; i < length; i++)
                {
                    object windowObject = frames.item(i);
                    objects.Add(windowObject);
                    dynamic window = windowObject;
                    object child = window.document;
                    objects.Add(child);
                    Visit(child, depth + 1);
                }
            }
            Visit(root, 0);
            if (matches.Count != 1)
                throw Failure("not_unique", $"対象HTML文書が{matches.Count}件です。[dom-v1]");
            dynamic selected = matches[0];
            return new(matches[0], (string)selected.URL, objects);
        }
        catch (BridgeException) { Release(objects); throw; }
        catch (Exception ex) when (ex is COMException or InvalidCastException or Microsoft.CSharp.RuntimeBinder.RuntimeBinderException)
        {
            Release(objects);
            throw Failure("unavailable", $"IEのHTML文書を操作できません。[dom-v1] HRESULT=0x{ex.HResult:X8}");
        }
    }

    private void VerifyDocument()
    {
        dynamic doc = document;
        if ((string)doc.URL != expectedUrl)
            throw new BridgeException("dummy_slot_changed", "操作直前に割当HTML文書のURLが変わりました。[dom-v1]");
        if ((string)doc.readyState != "complete")
            throw new BridgeException("assignment_dom_loading", "割当HTML文書の読込み完了を待っています。[dom-v1]");
    }

    private dynamic Input()
    {
        dynamic doc = document;
        dynamic inputs = Own((object)doc.getElementsByTagName("input"));
        var matches = new List<object>();
        int length = (int)inputs.length;
        for (int i = 0; i < length; i++)
        {
            dynamic input = Own((object)inputs.item(i));
            if ((string?)input.id == "findword") matches.Add((object)input);
        }
        if (matches.Count != 1)
            throw new BridgeException("assignment_input_not_unique", "HTML内の診察券番号入力欄を1件に特定できません。[dom-v1]");
        dynamic selected = matches[0];
        if ((bool)selected.disabled || (bool)selected.readOnly)
            throw new BridgeException("value_unavailable", "HTML内の診察券番号入力欄が編集できません。[dom-v1]");
        return selected;
    }

    private dynamic Button(string caption, bool includeDisabled = false)
    {
        dynamic doc = document;
        var matches = new List<object>();
        foreach (string tag in new[] { "input", "button" })
        {
            dynamic elements = Own((object)doc.getElementsByTagName(tag));
            int length = (int)elements.length;
            for (int i = 0; i < length; i++)
            {
                dynamic button = Own((object)elements.item(i));
                string type = (Convert.ToString(button.type) ?? "").ToLowerInvariant();
                if (type is not ("button" or "submit" or "image")) continue;
                string text;
                if (tag == "input" && type == "image")
                {
                    text = Convert.ToString(button.alt) ?? "";
                    if (string.IsNullOrWhiteSpace(text)) text = Convert.ToString(button.title) ?? "";
                    if (string.IsNullOrWhiteSpace(text)) text = Convert.ToString(button.value) ?? "";
                }
                else text = tag == "input" ? Convert.ToString(button.value) ?? "" : Convert.ToString(button.innerText) ?? "";
                if (text.Trim() == caption && (includeDisabled || !(bool)button.disabled)) matches.Add((object)button);
            }
        }
        if (matches.Count != 1)
            throw new BridgeException("assignment_button_unavailable", $"HTML内の「{caption}」ボタンが{matches.Count}件です。[dom-v1]");
        return matches[0];
    }

    internal string Describe()
    {
        VerifyDocument();
        return DescribeButton((object)Button("割当する", includeDisabled: true), "割当", "割当する");
    }

    internal string DescribeLink(string internalId)
    {
        VerifyDocument();
        if (!Regex.IsMatch(internalId, @"\A[0-9]+\z"))
            throw new BridgeException("link_dom_unavailable", "連携診断の内部予約IDを確認できません。");
        dynamic doc = document;
        object? button = doc.getElementById("chk" + internalId);
        if (button is null) throw new BridgeException("link_dom_unavailable", "連携ボタンのHTMLがありません。");
        Own(button);
        return DescribeButton(button, "連携", "連携ボタン");
    }

    private string DescribeButton(object element, string title, string caption)
    {
        dynamic button = element;
        var output = new StringBuilder($"=== {title}HTML診断（読取専用） ===\r\n");
        output.AppendLine("患者情報やスクリプト内の値を含む場合があります。共有前に確認してください。");
        output.AppendLine("URL: " + expectedUrl);
        output.AppendLine($"--- {caption} outerHTML ---");
        output.AppendLine(Convert.ToString(button.outerHTML) ?? "");
        foreach (string attribute in new[] { "onclick", "onmousedown", "onmouseup", "onmouseover", "onmouseout", "onchange", "onkeydown", "onkeyup", "onfocus", "onblur" })
            output.AppendLine(attribute + ": " + (Convert.ToString(button.getAttribute(attribute, 2)) ?? ""));
        object? formObject = null;
        try { formObject = button.form; }
        catch (Exception ex) when (ex is COMException or Microsoft.CSharp.RuntimeBinder.RuntimeBinderException) { }
        if (formObject is not null)
        {
            dynamic form = Own(formObject);
            output.AppendLine("--- 所属フォーム ---");
            foreach (string attribute in new[] { "id", "name", "action", "method", "target", "onsubmit" })
                output.AppendLine(attribute + ": " + (Convert.ToString(form.getAttribute(attribute, 2)) ?? ""));
        }
        dynamic doc = document;
        dynamic scripts = Own((object)doc.getElementsByTagName("script"));
        int length = (int)scripts.length;
        int remaining = 60000;
        output.AppendLine("--- スクリプト定義（実行・外部取得なし） ---");
        for (int i = 0; i < length; i++)
        {
            dynamic script = Own((object)scripts.item(i));
            string src = Convert.ToString(script.src) ?? "";
            output.AppendLine($"script[{i}] src: {src}");
            string code = Convert.ToString(script.text) ?? "";
            if (code.Length <= remaining) { output.AppendLine(code); remaining -= code.Length; }
            else
            {
                output.AppendLine(code[..remaining]);
                output.AppendLine("[以降のスクリプト本文は省略]");
                break;
            }
        }
        output.AppendLine($"=== {title}HTML診断 終了 ===");
        return output.ToString();
    }

    private static void CheckAllowed(Func<bool> mayOperate)
    {
        if (!mayOperate()) throw new BridgeException("operations_disabled", "実操作が無効になりました。");
    }

    private static void Run(string phase, Action action)
    {
        try { action(); }
        catch (Exception ex) when (ex is COMException or InvalidCastException or Microsoft.CSharp.RuntimeBinder.RuntimeBinderException)
        {
            OperationalLog.Write("assignment_dom_failed", new { phase, error = ex.GetType().Name, hresult = ex.HResult });
            throw new BridgeException("assignment_dom_failed", $"IEのHTML操作に失敗しました。[dom-v1 phase={phase}] HRESULT=0x{ex.HResult:X8}");
        }
    }

    internal void Search(string patientId, Func<bool> mayOperate) => Run("search", () =>
    {
        VerifyDocument();
        dynamic input = Input();
        dynamic search = Button("患者検索");
        CheckAllowed(mayOperate);
        input.value = patientId;
        if ((string)input.value != patientId)
            throw new BridgeException("patient_input_mismatch", "HTML内の入力値を確認できません。[dom-v1]");
        // Notify page-side code as well as changing the actual HTML value.
        CheckAllowed(mayOperate);
        input.fireEvent("onchange");
        VerifyDocument();
        if ((string)input.value != patientId)
            throw new BridgeException("patient_input_mismatch", "変更通知後にHTML内の入力値が変わりました。[dom-v1]");
        CheckAllowed(mayOperate);
        OperationalLog.Write("assignment_button_dispatch", new { button = "患者検索", method = "MSHTML.click", inputVerified = true });
        search.click();
    });

    internal void ValidateCommit(string patientId)
    {
        VerifyDocument();
        string query = Convert.ToString(Input().value) ?? "";
        if (query.Length > 0 && query != patientId)
            throw new BridgeException("patient_input_mismatch", "HTML内の診察券番号入力欄が別の番号に変わりました。[dom-v1]");
        dynamic button = Button("割当する");
        VerifyAssignmentHandler(Convert.ToString(button.outerHTML) ?? "");
        object? formObject = button.form;
        if (formObject is null)
            throw new BridgeException("assignment_handler_unverified", "割当ボタンのフォームを確認できません。");
        dynamic form = Own(formObject);
        if ((string?)form.name != "dayrsv_add")
            throw new BridgeException("assignment_handler_unverified", "割当ボタンのフォームがdayrsv_addではありません。");
    }

    internal static void VerifyAssignmentHandler(string html)
    {
        var attribute = Regex.Match(html, """\bonmousedown\s*=\s*(?:"([^"]*)"|'([^']*)'|([^\s>]+))""", RegexOptions.IgnoreCase);
        string handler = System.Net.WebUtility.HtmlDecode(attribute.Groups.Cast<Group>().Skip(1).FirstOrDefault(g => g.Success)?.Value ?? "");
        if (!attribute.Success || !Regex.IsMatch(handler, """\A\s*Action\s*\(\s*(['"])insert\1\s*\)\s*;?\s*\z"""))
            throw new BridgeException("assignment_handler_unverified", "割当ボタンのonmousedown=Action('insert')を確認できません。HTML診断を確認してください。");
    }

    internal void Commit(string patientId, Func<bool> mayOperate, Action? beforeDispatch = null) => Run("commit", () =>
    {
        ValidateCommit(patientId);
        dynamic button = Button("割当する");
        dynamic doc = document;
        dynamic mouseEvent = Own((object)doc.createEventObject());
        mouseEvent.button = 1;
        CheckAllowed(mayOperate);
        beforeDispatch?.Invoke();
        OperationalLog.Write("assignment_button_dispatch", new { button = "割当する", method = "MSHTML.fireEvent(onmousedown)", handler = "Action('insert')" });
        // Action('insert') validates and submits the form itself. A subsequent
        // click()/submit() would bypass its cancellation or submit twice.
        button.fireEvent("onmousedown", (object)mouseEvent);
    });

    internal static string NativeClassName(nint hwnd)
    {
        var name = new StringBuilder(256);
        GetClassName(hwnd, name, name.Capacity);
        return name.ToString();
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern uint RegisterWindowMessage(string name);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetClassName(nint hwnd, StringBuilder name, int maxCount);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern nint SendMessageTimeout(nint hwnd, uint message, nuint wParam, nint lParam, uint flags, uint timeout, out nint result);
    [DllImport("oleacc.dll")]
    private static extern int ObjectFromLresult(nint result, ref Guid iid, nuint wParam, [MarshalAs(UnmanagedType.Interface)] out object value);
}
