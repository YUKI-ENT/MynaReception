using iCallManager.Core;
using iCallManager.ICall;

internal static class IEAssignmentDomTests
{
    internal static void Run(Action<bool, string> check)
    {
        const string url = "http://example.invalid/admin/forward.php?fid=daylist&vid=fmdmytrn&rsv_sn=90001";
        check(IEAssignmentDom.MatchesReservationUrl(url, "90001"), "DOM attachment accepts exact assignment reservation URL");
        check(IEAssignmentDom.MatchesListUrl("http://example.invalid/admin/forward.php?fid=daylist&vid=daylist06&nm_sn=test"),
            "link diagnosis accepts exact reservation list frame");
        foreach (var invalidList in new[] { url, "about:blank", "file:///test?fid=daylist&vid=daylist06",
            "http://example.invalid/?fid=daylist&vid=daylist06&vid=daylist06", "http://example.invalid/?fid=first&vid=daylist06" })
            check(!IEAssignmentDom.MatchesListUrl(invalidList), "link diagnosis rejects unrelated or ambiguous list URL");
        foreach (string invalid in new[]
        {
            url.Replace("90001", "90002"), url + "&rsv_sn=90001", url.Replace("fmdmytrn", "daymodal"),
            url.Replace("daylist", "first"), "about:blank", "file:///C:/test.html?fid=daylist&vid=fmdmytrn&rsv_sn=90001"
        })
            check(!IEAssignmentDom.MatchesReservationUrl(invalid, "90001"), "DOM attachment rejects wrong or ambiguous document: " + invalid);
        IEAssignmentDom.VerifyAssignmentHandler("""<INPUT onmouseover="this.className='btnBigRegOver'" onmousedown="Action('insert');" onmouseout="this.className='btnBigReg'" class=btnBigReg type=submit value=割当する valign="center">""");
        check(true, "actual supplied assignment HTML identifies Action insert on mousedown");
        foreach (string html in new[] { "<input onclick=\"Action('insert');\">", "<input onmousedown=\"Action('delete');\">", "<input onmousedown=\"Action('insert'); otherAction();\">" })
        {
            try { IEAssignmentDom.VerifyAssignmentHandler(html); check(false, "unknown assignment handler rejected"); }
            catch (BridgeException ex) { check(ex.Code == "assignment_handler_unverified", "unknown assignment handler rejected"); }
        }

        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try { RunMshtml(check); RunLinkMshtml(check); RunNativeAttachment(check); }
            catch (Exception ex) { failure = ex; }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();
        if (failure is not null) System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(failure).Throw();
    }

    private static void RunNativeAttachment(Action<bool, string> check)
    {
        // Serve only loopback test HTML, including a child frame, to verify the
        // production WM_HTML_GETOBJECT / ObjectFromLresult attachment path.
        var listener = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, 0);
        listener.Start();
        using var stop = new CancellationTokenSource();
        int port = ((System.Net.IPEndPoint)listener.LocalEndpoint).Port;
        int posts = 0;
        string submittedBody = "";
        string serverPhase = "accept";
        var server = Task.Run(async () =>
        {
            try
            {
                while (!stop.IsCancellationRequested)
                {
                    using var client = await listener.AcceptTcpClientAsync(stop.Token);
                    using var stream = client.GetStream();
                    using var reader = new StreamReader(stream, leaveOpen: true);
                    string request = await reader.ReadLineAsync(stop.Token) ?? "";
                    serverPhase = request;
                    int contentLength = 0;
                    bool expectContinue = false;
                    string? line;
                    while (!string.IsNullOrEmpty(line = await reader.ReadLineAsync(stop.Token)))
                    {
                        if (line.StartsWith("Content-Length:", StringComparison.OrdinalIgnoreCase)) contentLength = int.Parse(line[15..].Trim());
                        if (line.Equals("Expect: 100-continue", StringComparison.OrdinalIgnoreCase)) expectContinue = true;
                    }
                    if (expectContinue) await stream.WriteAsync(System.Text.Encoding.ASCII.GetBytes("HTTP/1.1 100 Continue\r\n\r\n"), stop.Token);
                    if (request.StartsWith("POST /receipt ", StringComparison.Ordinal))
                    {
                        var posted = new char[contentLength];
                        serverPhase += $" reading {contentLength}";
                        await reader.ReadBlockAsync(posted.AsMemory(), stop.Token);
                        submittedBody = new string(posted);
                        Interlocked.Increment(ref posts);
                    }
                    string html = request.Contains("/outer ", StringComparison.Ordinal)
                        ? "<html><body><iframe src='/assign?fid=daylist&amp;vid=fmdmytrn&amp;rsv_sn=90001'></iframe></body></html>"
                        : """
                          <html><head><meta charset="utf-8">
                          <script>var exed=true; function Action(val){if(val=='insert' && exed){exed=false; var f=document.dayrsv_add; f.act.value=val; document.getElementById('assigned').innerText='done'; f.submit();}}
                          function changeChkFlag(ID){document.getElementById('linkState').innerText='toggled';document.getElementById('chk'+ID).value='〆';}</script>
                          </head><body>
                          <input type="text" id="findword">
                          <input type="button" value="患者検索" onclick="document.getElementById('result').innerText=document.getElementById('findword').value;">
                          <form name="dayrsv_add" method="post" action="/receipt" target="receipt" onsubmit="document.getElementById('defaults').innerText='unexpected'; return false;">
                          <input type="hidden" name="act"><input type="submit" value="割当する" onmousedown="Action('insert');">
                          </form><iframe name="receipt" src="about:blank" style="display:none"></iframe>
                          <span id="result"></span><span id="assigned"></span>
                          <span id="defaults">none</span>
                          <input id="chk90001" type="button" class="btCheckOff" value="" onmousedown='changeChkFlag("90001","0","90001","1","11","test","test","date","date","na1","","","","date");'>
                          <span id="linkState">untouched</span>
                          </body></html>
                          """;
                    byte[] body = System.Text.Encoding.UTF8.GetBytes(html);
                    byte[] header = System.Text.Encoding.ASCII.GetBytes($"HTTP/1.1 200 OK\r\nContent-Type: text/html; charset=utf-8\r\nContent-Length: {body.Length}\r\nConnection: close\r\n\r\n");
                    await stream.WriteAsync(header, stop.Token);
                    await stream.WriteAsync(body, stop.Token);
                }
            }
            catch (OperationCanceledException) when (stop.IsCancellationRequested) { }
        });
        try
        {
            using var browser = new System.Windows.Forms.WebBrowser { ScriptErrorsSuppressed = true };
            browser.CreateControl();
            browser.Navigate($"http://127.0.0.1:{port}/outer");
            var loading = System.Diagnostics.Stopwatch.StartNew();
            while (browser.ReadyState != System.Windows.Forms.WebBrowserReadyState.Complete || browser.Document?.Window?.Frames.Count != 1 ||
                browser.Document.Window.Frames[0].Document?.GetElementById("findword") is null)
            {
                if (loading.Elapsed > TimeSpan.FromSeconds(10)) throw new TimeoutException("Local framed MSHTML document did not load.");
                System.Windows.Forms.Application.DoEvents();
                Thread.Sleep(10);
            }
            nint htmlServer = 0;
            EnumChildWindows(browser.Handle, (hwnd, _) =>
            {
                if (IEAssignmentDom.NativeClassName(hwnd) != "Internet Explorer_Server") return true;
                htmlServer = hwnd; return false;
            }, 0);
            check(htmlServer != 0, "local browser exposes native IE HTML server");
            bool usedMta = false;
            bool domUsedSta = false;
            int commitAttempts = 0;
            string nativeDiagnostic = "";
            string nativePhase = "inspect";
            var operations = Task.Run(() =>
            {
                usedMta = Thread.CurrentThread.GetApartmentState() == ApartmentState.MTA;
                nativeDiagnostic = IEAssignmentDom.Inspect(htmlServer, "90001");
                nativePhase = "search";
                bool Allowed() { domUsedSta = Thread.CurrentThread.GetApartmentState() == ApartmentState.STA; return true; }
                IEAssignmentDom.Execute(htmlServer, "90001", "11", "search", Allowed);
                nativePhase = "commit";
                IEAssignmentDom.Execute(htmlServer, "90001", "11", "commit", Allowed, () => commitAttempts++);
                nativePhase = "reject_wrong";
                try { IEAssignmentDom.Execute(htmlServer, "999", "11", "search", Allowed); return false; }
                catch (BridgeException ex) { return ex.Code == "assignment_dom_not_unique"; }
            });
            var dispatch = System.Diagnostics.Stopwatch.StartNew();
            while (!operations.IsCompleted)
            {
                if (dispatch.Elapsed > TimeSpan.FromSeconds(20)) throw new TimeoutException($"MTA native DOM dispatch did not complete: {nativePhase}, server={serverPhase}, posts={posts}");
                System.Windows.Forms.Application.DoEvents();
                Thread.Sleep(10);
            }
            bool wrongRejected = operations.GetAwaiter().GetResult();
            while (Volatile.Read(ref posts) < 1 || browser.ReadyState != System.Windows.Forms.WebBrowserReadyState.Complete)
            {
                if (dispatch.Elapsed > TimeSpan.FromSeconds(10)) throw new TimeoutException("Native assignment form did not submit.");
                System.Windows.Forms.Application.DoEvents(); Thread.Sleep(10);
            }
            check(usedMta && domUsedSta, "MTA UIA worker delegates HTML operations to dedicated STA without passing COM objects");
            check(browser.Document.Window.Frames[0].Document!.GetElementById("result")!.InnerText?.Trim() == "11",
                "native DOM attachment locates correct child frame and executes real search handler");
            check(browser.Document.Window.Frames[0].Document!.GetElementById("assigned")!.InnerText?.Trim() == "done",
                "native DOM attachment executes actual mousedown assignment handler in verified frame");
            check(commitAttempts == 1 && posts == 1 && submittedBody.Contains("act=insert"),
                "Action insert submits real form exactly once from the MTA-to-STA native path");
            check(browser.Document.Window.Frames[0].Document!.GetElementById("defaults")!.InnerText?.Trim() == "none",
                "event dispatch does not trigger a second default submit or bypass site Action");
            check(wrongRejected, "wrong native reservation never attaches");
            check(nativeDiagnostic.Contains("割当する outerHTML") &&
                nativeDiagnostic.Contains("assigned") && commitAttempts == 1,
                "native read-only inspection retrieves actual assignment markup through STA");

            using var listBrowser = new System.Windows.Forms.WebBrowser { ScriptErrorsSuppressed = true };
            listBrowser.CreateControl();
            listBrowser.Navigate($"http://127.0.0.1:{port}/list?fid=daylist&vid=daylist06");
            var listClock = System.Diagnostics.Stopwatch.StartNew();
            while (listBrowser.ReadyState != System.Windows.Forms.WebBrowserReadyState.Complete ||
                listBrowser.Document?.GetElementById("chk90001") is null)
            {
                if (listClock.Elapsed > TimeSpan.FromSeconds(10)) throw new TimeoutException("Local list did not load");
                System.Windows.Forms.Application.DoEvents(); Thread.Sleep(10);
            }
            nint listServer = 0;
            EnumChildWindows(listBrowser.Handle, (hwnd, _) =>
            {
                if (IEAssignmentDom.NativeClassName(hwnd) != "Internet Explorer_Server") return true;
                listServer = hwnd; return false;
            }, 0);
            var inspectList = Task.Run(() => IEAssignmentDom.InspectLink(listServer, "90001"));
            while (!inspectList.IsCompleted)
            {
                if (listClock.Elapsed > TimeSpan.FromSeconds(20)) throw new TimeoutException("Native list inspection did not complete");
                System.Windows.Forms.Application.DoEvents(); Thread.Sleep(10);
            }
            string listDiagnostic = inspectList.GetAwaiter().GetResult();
            check(listDiagnostic.Contains("連携HTML診断") && listDiagnostic.Contains("chk90001") && listDiagnostic.Contains("onmousedown:"),
                "MTA to STA native attachment reads verified list-frame link HTML");
            check(listBrowser.Document!.GetElementById("linkState")!.InnerText == "untouched" && posts == 1,
                "native link inspection never toggles link or submits a form");
            int linkAttempts = 0;
            var nativeLink = Task.Run(() =>
            {
                var expected = new Reservation("1", "11", "test", "90001", true, true);
                IEAssignmentDom.ExecuteLink(listServer, expected, () => true, () => linkAttempts++);
                IEAssignmentDom.ExecuteLink(listServer, expected, () => true, () => linkAttempts++);
            });
            listClock.Restart();
            while (!nativeLink.IsCompleted)
            {
                if (listClock.Elapsed > TimeSpan.FromSeconds(20)) throw new TimeoutException("Native link dispatch did not complete");
                System.Windows.Forms.Application.DoEvents(); Thread.Sleep(10);
            }
            nativeLink.GetAwaiter().GetResult();
            check(listBrowser.Document.GetElementById("chk90001")!.GetAttribute("value") == "〆" && linkAttempts == 1 && posts == 1,
                "native MTA to STA link dispatch fires verified page handler exactly once and never toggles ON back OFF");
        }
        finally
        {
            stop.Cancel();
            server.GetAwaiter().GetResult();
            listener.Stop();
        }
    }

    private delegate bool EnumWindow(nint hwnd, nint state);
    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern bool EnumChildWindows(nint parent, EnumWindow callback, nint state);

    private static void RunLinkMshtml(Action<bool, string> check)
    {
        using var browser = new System.Windows.Forms.WebBrowser { ScriptErrorsSuppressed = true };
        browser.CreateControl();
        browser.DocumentText = """
            <html><head><meta charset="utf-8"><script>
            var clientCalls=0, flagCalls=0, clientResult=0, resultStr=1, eventButton=0;
            function RunClientCmdWrapper(mode,args){clientCalls++; return clientResult;}
            function sendChangeFlag(action,rsv_no,flag){flagCalls++;}
            function changeChkFlag(ID,tmpFlag,rsv_no,rsv_wait_no,mem_code,mem_kanji,mem_name,rsv_reg_date,rsv_sort_date,rsv_nm_sn,opt_sn1,opt_sn2,opt_sn3,come_time){
              eventButton=event.button;
              var flag=document.getElementById('chk'+ID).value==''?0:1;
              var result=RunClientCmdWrapper(flag,rsv_wait_no+','+mem_code+','+rsv_no);
              if(result>=0){sendChangeFlag('chk',rsv_no,flag); if(resultStr==1){
                var b=document.getElementById('chk'+ID); b.value=flag==0?'〆':''; b.className=flag==0?'btCheckOn':'btCheckOff';}}
            }
            </script></head><body>
            <input id="chk90001" type="button" class="btCheckOff" value=""
              onmousedown='changeChkFlag("90001","0","90001","1","11","試験患者","しけんかんじゃ","2026/10/18 18:10:56","2026/10/18 18:10:56","na000001","","","","2026/10/18 18:19:26");'>
            </body></html>
            """;
        var clock = System.Diagnostics.Stopwatch.StartNew();
        while (browser.ReadyState != System.Windows.Forms.WebBrowserReadyState.Complete || browser.Document?.GetElementById("chk90001") is null)
        {
            if (clock.Elapsed > TimeSpan.FromSeconds(10)) throw new TimeoutException("Local link document did not load");
            System.Windows.Forms.Application.DoEvents(); Thread.Sleep(10);
        }
        dynamic document = browser.Document!.DomDocument;
        dynamic button = document.getElementById("chk90001");
        using var dom = new IEAssignmentDom((object)document, (string)document.URL);
        var expected = new Reservation("1", "11", "別の入力氏名", "90001", true, true) { LinkButtonName = "" };
        int Count(string name) => Convert.ToInt32(browser.Document.InvokeScript("eval", [name]));
        string Value() => Convert.ToString(button.value) ?? "";
        void Reject(Action action, string code, string title)
        {
            try { action(); check(false, title); }
            catch (BridgeException ex) { check(ex.Code == code, title); }
        }
        button.click();
        check(Count("clientCalls") == 0 && Value() == "", "regression: click alone never calls actual mousedown link handler");
        int attempted = 0;
        Reject(() => dom.Link(expected, () => false, () => attempted++), "operations_disabled", "disabled DOM link never dispatches");
        check(attempted == 0 && Count("clientCalls") == 0, "disabled link does not touch integration client");
        dom.Link(expected, () => true, () => attempted++);
        check(Value() == "〆" && Count("clientCalls") == 1 && Count("flagCalls") == 1 && Count("eventButton") == 1 && attempted == 1,
            "captured changeChkFlag handler runs once on left mousedown and updates link ON through site logic");
        dom.Link(expected, () => true, () => attempted++);
        check(Value() == "〆" && Count("clientCalls") == 1 && attempted == 1,
            "DOM becoming ON before dispatch never gets toggled OFF");
        button.value = ""; button.className = "btCheckOff"; button.disabled = true;
        Reject(() => dom.Link(expected, () => true), "action_unavailable", "disabled HTML link button is rejected");
        button.disabled = false;
        string original = (string)button.outerHTML;
        IEAssignmentDom.VerifyLinkHandler(original, expected);
        Reject(() => IEAssignmentDom.VerifyLinkHandler(original, expected with { ReceptionNo = "2" }), "identity_changed", "handler's reception must match requested reception");
        Reject(() => IEAssignmentDom.VerifyLinkHandler(original, expected with { InternalId = "90002" }), "identity_changed", "handler's two internal reservation IDs must match");
        Reject(() => IEAssignmentDom.VerifyLinkHandler(original, expected with { PatientId = "12" }), "identity_changed", "known chart must match link handler argument");
        IEAssignmentDom.VerifyLinkHandler(original, expected with { PatientId = "" });
        check(true, "missing chart does not prevent verified reception-based link handler");
        Reject(() => IEAssignmentDom.VerifyLinkHandler(original.Replace("changeChkFlag(", "otherAction("), expected), "link_handler_unverified", "unknown link function is rejected");
        Reject(() => IEAssignmentDom.VerifyLinkHandler(original.Replace("onmousedown", "onclick"), expected), "link_handler_unverified", "different event cannot substitute for captured mousedown definition");
        button.insertAdjacentHTML("afterEnd", original);
        Reject(() => dom.Link(expected, () => true), "link_dom_not_unique", "duplicate target HTML IDs never dispatch link");
        document.getElementsByTagName("input").item(1).removeNode(true);
        browser.Document.InvokeScript("eval", ["clientResult=-1"]);
        dom.Link(expected, () => true, () => attempted++);
        check(Value() == "" && Count("clientCalls") == 2 && Count("flagCalls") == 1 && attempted == 2,
            "site integration rejection stays OFF with no fallback click or second event");
    }

    private static void RunMshtml(Action<bool, string> check)
    {
        // Real, local MSHTML document: exercise HTML input values and JavaScript
        // onclick/onchange handlers rather than a mock Invoke implementation.
        using var browser = new System.Windows.Forms.WebBrowser { ScriptErrorsSuppressed = true };
        browser.CreateControl();
        browser.DocumentText = """
            <html><head><title>Local assignment test</title>
            <script type="text/javascript">
            function assignmentHelper(){return 'diagnostic-only';}
            function Action(val){
              if(val!='insert' || document.getElementById('valid').value!='yes')return false;
              document.getElementById('eventButton').innerText=event.button;
              document.getElementById('commits').innerText=Number(document.getElementById('commits').innerText)+1;
              document.dayrsv_add.act.value=val;
              return true;
            }
            </script>
            </head><body>
            <input id="findword" type="text" value=""
              onchange="document.getElementById('changed').innerText='changed';">
            <input id="search" type="button" value="患者検索"
              onclick="document.getElementById('result').innerText=document.getElementById('findword').value; document.getElementById('searches').innerText=Number(document.getElementById('searches').innerText)+1;">
            <form id="assignment-form" name="dayrsv_add" action="about:blank" method="post"
              onsubmit="document.getElementById('defaults').innerText=Number(document.getElementById('defaults').innerText)+1; return false;">
            <input type="hidden" name="act"><input id="commit" type="submit" value="割当する" onmousedown="Action('insert');">
            </form>
            <span id="changed"></span><span id="result"></span>
            <span id="searches">0</span><span id="commits">0</span>
            <span id="defaults">0</span><span id="eventButton"></span><input type="hidden" id="valid" value="yes">
            <input id="chk90001" type="button" value="" onmousedown="document.getElementById('linkEvents').innerText='fired';">
            <span id="linkEvents">untouched</span>
            </body></html>
            """;
        var loading = System.Diagnostics.Stopwatch.StartNew();
        while (browser.ReadyState != System.Windows.Forms.WebBrowserReadyState.Complete || browser.Document?.GetElementById("findword") is null)
        {
            if (loading.Elapsed > TimeSpan.FromSeconds(10)) throw new TimeoutException("Local MSHTML test document did not load.");
            System.Windows.Forms.Application.DoEvents();
            Thread.Sleep(10);
        }
        dynamic document = browser.Document!.DomDocument;
        using var dom = new IEAssignmentDom((object)document, (string)document.URL);
        string Text(string id) => (Convert.ToString(document.getElementById(id).innerText) ?? "").Trim();
        document.getElementById("commit").disabled = true;
        string diagnostic = dom.Describe();
        string linkDiagnostic = dom.DescribeLink("90001");
        check(linkDiagnostic.Contains("連携HTML診断（読取専用）") && linkDiagnostic.Contains("chk90001") &&
            linkDiagnostic.Contains("onmousedown:") && linkDiagnostic.Contains("linkEvents"),
            "real MSHTML link diagnosis retrieves exact button markup and script without needing InvokePattern");
        check(Text("linkEvents") == "untouched", "link HTML diagnosis never fires a toggle event");
        try { dom.DescribeLink("90002"); check(false, "wrong link ID rejected"); }
        catch (BridgeException ex) { check(ex.Code == "link_dom_unavailable", "wrong link ID never describes another button"); }
        check(diagnostic.Contains("割当する outerHTML") && diagnostic.Contains("onmousedown:") && diagnostic.Contains("commits"),
            "read-only diagnosis reads disabled assignment HTML and event definition");
        check(diagnostic.Contains("assignment-form") && diagnostic.Contains("onsubmit:") &&
            diagnostic.Contains("function assignmentHelper()") && diagnostic.Contains("diagnostic-only"),
            "read-only diagnosis includes parent form attributes and inline function source");
        check(Text("searches") == "0" && Text("commits") == "0" && string.IsNullOrEmpty((string?)document.getElementById("findword").value),
            "HTML diagnosis never changes query or executes search and assignment handlers");
        document.getElementById("commit").disabled = false;
        document.getElementById("commit").click();
        check(Text("commits") == "0" && Text("defaults") == "1",
            "regression: click alone misses the actual mousedown handler and triggers only default submit");
        void Reject(Action action, string code, string name)
        {
            try { action(); check(false, name); }
            catch (BridgeException ex) { check(ex.Code == code, name); }
        }

        Reject(() => dom.Search("11", () => false), "operations_disabled", "disabled DOM search never changes input or clicks");
        check(string.IsNullOrEmpty((string?)document.getElementById("findword").value) && Text("searches").Trim() == "0",
            "disabled real HTML search leaves document untouched");
        dom.Search("11", () => true);
        check(Text("result") == "11" && Text("changed") == "changed" && Text("searches") == "1",
            $"real MSHTML search click executes page JavaScript with actual entered chart number (result={Text("result")}, change={Text("changed")}, searches={Text("searches")})");
        dom.Commit("11", () => true);
        check(Text("commits") == "1" && Text("eventButton") == "1" && Text("defaults") == "1",
            "mousedown dispatch executes Action insert once with left-button event and no extra click");
        Reject(() => dom.Commit("999", () => true), "patient_input_mismatch", "DOM commit rejects a changed chart query");
        check(Text("commits") == "1", "mismatched DOM commit does not click again");
        Reject(() => dom.Commit("11", () => false), "operations_disabled", "disabled DOM commit never clicks");
        int calls = 0;
        Reject(() => dom.Search("22", () => ++calls == 1), "operations_disabled",
            "operation disable between HTML input and click cancels search dispatch");
        check(Text("searches") == "1", "cancelled search does not execute page search handler");
        document.getElementById("findword").value = "11";
        document.getElementById("search").disabled = true;
        Reject(() => dom.Search("11", () => true), "assignment_button_unavailable", "disabled HTML search button is never clicked");
        document.getElementById("search").disabled = false;
        document.body.insertAdjacentHTML("beforeEnd", "<input type='button' value='患者検索'>");
        Reject(() => dom.Search("11", () => true), "assignment_button_unavailable", "duplicate HTML search buttons are rejected");
        check(Text("searches") == "1" && Text("commits") == "1", "failed HTML button selection preserves dispatch counts");
        document.getElementById("valid").value = "no";
        dom.Commit("11", () => true);
        check(Text("commits") == "1" && Text("defaults") == "1", "site validation can cancel Action without fallback click or form submit");
        document.getElementById("valid").value = "yes";
        document.getElementById("commit").outerHTML = "<input id='commit' type='image' alt='割当する' onmousedown=\"Action('insert');\">";
        int attempts = 0;
        dom.Commit("11", () => true, () => attempts++);
        check(attempts == 1 && Text("commits") == "2", "image input with alt label dispatches verified mousedown handler once");
        document.getElementById("commit").disabled = true;
        Reject(() => dom.Commit("11", () => true, () => attempts++), "assignment_button_unavailable", "disabled image assignment is rejected before dispatch");
        check(attempts == 1 && Text("commits") == "2", "disabled image button never marks a commit attempt");
        document.getElementById("commit").disabled = false;
        document.body.insertAdjacentHTML("beforeEnd", "<input type='image' alt='割当する'>");
        Reject(() => dom.Commit("11", () => true, () => attempts++), "assignment_button_unavailable", "duplicate image assignment buttons are rejected");
        check(attempts == 1, "ambiguous image assignment never marks a commit attempt");
        dynamic inputs = document.getElementsByTagName("input");
        inputs.item((int)inputs.length - 1).disabled = true;
        document.getElementById("commit").setAttribute("onmousedown", "Action('delete');");
        Reject(() => dom.Commit("11", () => true, () => attempts++), "assignment_handler_unverified",
            "unexpected actual event handler is rejected before dispatch");
        document.getElementById("commit").setAttribute("onmousedown", "Action('insert');");
        document.getElementById("assignment-form").name = "other-form";
        Reject(() => dom.Commit("11", () => true, () => attempts++), "assignment_handler_unverified",
            "button attached to another form is rejected before dispatch");
        check(attempts == 1 && Text("commits") == "2", "handler or form mismatch never dispatches assignment");
    }
}
