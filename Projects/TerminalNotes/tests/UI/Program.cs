using System.IO;
using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Interop;
using System.Windows.Input;
using Microsoft.Web.WebView2.Wpf;
using TerminalNotes;
using TerminalNotes.Services;

internal static class Program
{
    private static int _fail;
    private static bool _runCompleted;
    [STAThread]
    public static int Main()
    {
        // This existing suite inspects xterm.js internals. Native rendering has
        // a separate real-PTY smoke suite in tests/NativeTerminalSurface.
        bool nativeAddOnly=Environment.GetEnvironmentVariable("TERMINALNOTES_UI_NATIVE_ADD_ONLY")=="1";
        Environment.SetEnvironmentVariable("TERMINALNOTES_TERMINAL_RENDERER",nativeAddOnly||Environment.GetEnvironmentVariable("TERMINALNOTES_UI_INDEPENDENT_ONLY")=="1"?null:"webview");
        // This automation harness exercises mouse and keyboard only. WPF's tablet
        // worker can block while tearing down synthetic UI windows on this host.
        AppContext.SetSwitch("Switch.System.Windows.Input.Stylus.DisableStylusAndTouchSupport", true);
        var app=new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        app.Startup+=async (_,_)=> { await Run(app); _runCompleted = true; };
        app.Run();
        if (!_runCompleted)
        {
            Console.WriteLine("FAIL UI dispatcher exited before the test run completed");
            return 1;
        }
        return _fail;
    }
    private static T Field<T>(object o,string name)=>(T)o.GetType().GetField(name,BindingFlags.Instance|BindingFlags.NonPublic)!.GetValue(o)!;
    private static T Property<T>(object o,string name)=>(T)o.GetType().GetProperty(name,BindingFlags.Instance|BindingFlags.Public|BindingFlags.NonPublic)!.GetValue(o)!;
    private static string TaskActionStatus(object row)
    {
        object content = Property<Button>(row, "Start").Content;
        return content.GetType().GetProperty("Kind", BindingFlags.Instance|BindingFlags.Public|BindingFlags.NonPublic)?.GetValue(content)?.ToString()
            ?? (content.GetType().Name == "AutomationActivityIndicator" ? "Active" : content.GetType().Name);
    }
    private static string TaskActionHelp(object row) =>
        System.Windows.Automation.AutomationProperties.GetHelpText(Property<Button>(row, "Start")) ?? "";
    private static string[] TaskOrder(MainWindow window) => Field<TreeViewItem>(window,"_taskOrderGroup").Items.Cast<TreeViewItem>().Select(node=>(string)node.Tag).ToArray();
    private static TerminalPane Pane(TabItem tab)=>(TerminalPane)tab.Tag.GetType().GetProperty("Pane")!.GetValue(tab.Tag)!;
    private static object? Call(object o,string name,params object?[] args)=>o.GetType().GetMethod(name,BindingFlags.Instance|BindingFlags.NonPublic)!.Invoke(o,args);
    private static async Task<string> BrowserState(WebView2 browser)
    {
        try
        {
            if(browser.CoreWebView2 is null) return "CoreWebView2=null";
            return await browser.ExecuteScriptAsync("JSON.stringify({state:document.readyState,terminal:typeof terminal,cols:typeof terminal==='undefined'?0:terminal.cols})");
        }
        catch(Exception exception) { return "browser access failed "+exception.GetType().Name+": "+exception.Message; }
    }
    private static void Assert(bool value,string name) {if(!value) throw new Exception(name);Console.WriteLine("PASS "+name);}
    private static async Task Until(Func<Task<bool>> condition,string name,int seconds=25)
    {
        var end=DateTime.UtcNow.AddSeconds(seconds);
        while(DateTime.UtcNow<end) {if(await condition()) return;await Task.Delay(100);}
        throw new TimeoutException(name);
    }
    private static async Task RunNativeTerminalAddSmoke(string fixture)
    {
        string workspace=Path.Combine(fixture,"native-terminal-add-workspace");
        Directory.CreateDirectory(workspace);
        string settings=Path.Combine(fixture,"native-terminal-add-settings.json");
        File.WriteAllText(settings,JsonSerializer.Serialize(new { Workspace=workspace, WorkspaceDefaultsVersion=1, SetupCompleted=true }));
        var window=new MainWindow(settings,autoStartCodex:false,initialWorkspace:workspace);
        window.GetType().GetField("_openCodeReady",BindingFlags.Instance|BindingFlags.NonPublic)!.SetValue(window,true);
        var extras=new List<TabItem>();
        try
        {
            window.Show();
            var tabs=(TabControl)window.FindName("TerminalTabs");
            await Until(()=>Task.FromResult(tabs.Items.Count==1&&Pane((TabItem)tabs.Items[0]).IsRunning&&Pane((TabItem)tabs.Items[0]).IsReady),"native task owner starts");
            var ownerTab=(TabItem)tabs.Items[0];
            var ownerPane=Pane(ownerTab);
            Assert(Field<bool>(ownerPane,"_useNativeTerminalSurface"),"native add fixture uses NativeTerminalSurface for its MainWindow owner");
            object runtime=Call(window,"RuntimeFor",ownerPane.WorkingDirectory)!;
            int originalCapacity=Field<int>(window,"_splitCapacity");

            void AssertVisibleLayout(params TerminalPane[] panes)
            {
                window.UpdateLayout();
                var rects=panes.Select(pane=>pane.TransformToAncestor(window).TransformBounds(new System.Windows.Rect(0,0,pane.ActualWidth,pane.ActualHeight))).ToArray();
                Assert(panes.All(pane=>pane.IsVisible&&pane.ActualWidth>0&&pane.ActualHeight>0)&&
                    !rects.Where((first,index)=>rects.Skip(index+1).Any(second=>System.Windows.Rect.Intersect(first,second) is var overlap&&!overlap.IsEmpty&&overlap.Width>1&&overlap.Height>1)).Any(),
                    "native task and extra panes have separate visible bounds");
                Assert(((FrameworkElement)window.FindName("NotesPanel")).IsVisible&&Field<FrameworkElement>(window,"_taskSidebarSurface").IsVisible,
                    "native extra panes keep the task sidebar and plan panel visible");
            }

            await ownerPane.SendTextAsync("Set-PSReadLineOption -HistorySaveStyle SaveNothing; Write-Output ('NATIVE_TASK_' + 'OUTPUT_BEFORE_EXTRAS')",true);
            const string marker="NATIVE_TASK_OUTPUT_BEFORE_EXTRAS";
            await Until(()=>Task.FromResult(((string)Call(Field<object>(ownerPane,"_nativeTerminalSurface"),"GetEntireBufferText")!).Contains(marker,StringComparison.Ordinal)),"native owner outputs a pre-add marker");
            var panes=new List<TerminalPane>{ownerPane};
            for(int i=0;i<2;i++)
            {
                ((Button)window.FindName("NewTerminalButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                await Until(()=>Task.FromResult(tabs.Items.Count==i+2&&tabs.SelectedItem is TabItem selected&&Pane(selected).IsReady&&Pane(selected).IsRunning&&Pane(selected).IsVisible&&Pane(selected).ActualWidth>0&&Pane(selected).ActualHeight>0),"native + terminal creates ready visible pane "+(i+1));
                var newTab=(TabItem)tabs.SelectedItem;
                extras.Add(newTab);
                panes.Add(Pane(newTab));
                Assert(ReferenceEquals(runtime.GetType().GetField("SingleTerminal")!.GetValue(runtime),ownerTab),"native + terminal preserves original task owner");
                AssertVisibleLayout(panes.ToArray());
            }

            foreach(var tab in tabs.Items.Cast<TabItem>().ToArray())
            {
                Call(window,"SelectTerminal",tab,false);
                await Until(()=>Task.FromResult(ReferenceEquals(tabs.SelectedItem,tab)&&panes.All(pane=>pane.IsVisible&&pane.ActualWidth>0&&pane.ActualHeight>0)),"native selection keeps mixed panes visible");
                AssertVisibleLayout(panes.ToArray());
            }
            Assert(ownerPane.IsRunning&&ReferenceEquals(runtime.GetType().GetField("SingleTerminal")!.GetValue(runtime),ownerTab)&&
                ((string)Call(Field<object>(ownerPane,"_nativeTerminalSurface"),"GetEntireBufferText")!).Contains(marker,StringComparison.Ordinal),
                "native owner process and its output survive adding and selecting extra panes");
            window.UpdateLayout();
            await Task.Delay(250);
            CaptureWindow(window,"native-add-terminal.png");

            foreach(var tab in extras.AsEnumerable().Reverse())
            {
                var pane=Pane(tab);
                await pane.SendTextAsync("Set-PSReadLineOption -HistorySaveStyle SaveNothing; exit",true);
                await Until(()=>Task.FromResult(!pane.IsRunning),"native extra shell exits cleanly");
                Call(window,"CloseTerminal",tab,pane);
            }
            extras.Clear();
            AssertVisibleLayout(ownerPane);
            Assert(tabs.Items.Count==1&&tabs.Items.Contains(ownerTab)&&ownerPane.IsRunning&&Field<int>(window,"_splitCapacity")==originalCapacity&&
                ReferenceEquals(runtime.GetType().GetField("SingleTerminal")!.GetValue(runtime),ownerTab),
                "closing native extra panes restores the original task pane and layout capacity");
            await ownerPane.SendTextAsync("Write-Output ('NATIVE_TASK_' + 'AFTER_EXTRA_CLOSE')",true);
            await Until(()=>Task.FromResult(((string)Call(Field<object>(ownerPane,"_nativeTerminalSurface"),"GetEntireBufferText")!).Contains("NATIVE_TASK_AFTER_EXTRA_CLOSE",StringComparison.Ordinal)),"native owner still executes commands after extra panes close");
        }
        finally
        {
            Call(window,"DisposeResources");
            window.Close();
            foreach(var tab in extras)
            {
                var pane=Pane(tab);
                if(pane.IsRunning) pane.Dispose();
            }
        }
    }
    private static async Task RunTwoPaneInputIsolationSmoke(string fixture)
    {
        var firstDirectory=Path.Combine(fixture,"terminal-input-first");
        var secondDirectory=Path.Combine(fixture,"terminal-input-second");
        Directory.CreateDirectory(firstDirectory);
        Directory.CreateDirectory(secondDirectory);
        var first=new TerminalPane(firstDirectory,startupCommand:"Write-Output TERMINAL_INPUT_ISOLATION_READY");
        var second=new TerminalPane(secondDirectory);
        var host=new Window { Title="Terminal input isolation fixture", Width=760, Height=500, ShowInTaskbar=false };
        var grid=new Grid();
        grid.RowDefinitions.Add(new RowDefinition());
        grid.RowDefinitions.Add(new RowDefinition());
        grid.Children.Add(first);
        var secondHost=new ContentControl { Content=second };
        Grid.SetRow(secondHost,1);
        grid.Children.Add(secondHost);
        host.Content=grid;
        var firstOutput=new StringBuilder();
        var secondOutput=new StringBuilder();
        object firstGate=new(),secondGate=new(),diagnosticGate=new();
        var diagnostics=new List<string>();
        void Record(string value) { lock(diagnosticGate) diagnostics.Add(value); }
        first.StatusChanged+=value=>Record("first status: "+value);
        second.StatusChanged+=value=>Record("second status: "+value);
        first.Ready+=()=>Record("first pane Ready event");
        second.Ready+=()=>Record("second pane Ready event");
        var firstSession=Field<ConPtySession>(first,"_session");
        var secondSession=Field<ConPtySession>(second,"_session");
        firstSession.OutputReceived+=value=>{lock(firstGate) firstOutput.Append(value);};
        secondSession.OutputReceived+=value=>{lock(secondGate) secondOutput.Append(value);};
        try
        {
            host.Show();
            var firstBrowser=Field<WebView2>(first,"_browser");
            var secondBrowser=Field<WebView2>(second,"_browser");
            void ObserveBrowser(WebView2 browser,string name)
            {
                browser.CoreWebView2InitializationCompleted+=(_,args)=>
                {
                    if(!args.IsSuccess) { Record(name+" initialization failed: "+args.InitializationException?.ToString()); return; }
                    try
                    {
                        var core=browser.CoreWebView2;
                        core.ProcessFailed+=(_,failure)=>Record(name+" WebView process failed: kind="+failure.ProcessFailedKind+" description="+failure.ProcessDescription+" exit="+failure.ExitCode);
                        core.NavigationCompleted+=(_,navigation)=>Record(name+" navigation completed: success="+navigation.IsSuccess+" error="+navigation.WebErrorStatus);
                    }
                    catch(Exception exception) { Record(name+" WebView initialization getter failed: "+exception.GetType().Name+": "+exception.Message); }
                };
            }
            ObserveBrowser(firstBrowser,"first");
            ObserveBrowser(secondBrowser,"second");
            try { await Until(()=>Task.FromResult(first.IsReady&&second.IsReady),"isolated terminal panes ready"); }
            catch(TimeoutException exception)
            {
                string firstScript=await BrowserState(firstBrowser);
                string secondScript=await BrowserState(secondBrowser);
                string recent;
                lock(diagnosticGate) recent=string.Join(" | ",diagnostics.TakeLast(12));
                string firstNotice=Field<TextBlock>(first,"_notice").Text;
                string secondNotice=Field<TextBlock>(second,"_notice").Text;
                throw new InvalidOperationException($"{exception.Message}; first loaded={first.IsLoaded} visible={first.IsVisible} size={first.ActualWidth:0.##}x{first.ActualHeight:0.##} running={first.IsRunning} notice={firstNotice} browser={firstScript}; second loaded={second.IsLoaded} visible={second.IsVisible} size={second.ActualWidth:0.##}x{second.ActualHeight:0.##} running={second.IsRunning} notice={secondNotice} browser={secondScript}; statuses={recent}",exception);
            }
            await Until(async ()=>await firstBrowser.ExecuteScriptAsync("document.readyState==='complete'")=="true","first isolated WebView ready");
            await Until(async ()=>await secondBrowser.ExecuteScriptAsync("document.readyState==='complete'")=="true","second isolated WebView ready");
            first.FocusTerminal();
            await Until(async ()=>host.IsActive&&await firstBrowser.ExecuteScriptAsync("document.activeElement===terminal.textarea")=="true","first pane owns keyboard focus");

            const string backgroundCommand="1..60 | ForEach-Object { Write-Output ('BACKGROUND_ACTIVITY_' + $_); Start-Sleep -Milliseconds 20 }";
            string firstPayload="Write-Output 'ORDERED_INPUT_FIRST'; Write-Output 'ORDERED_INPUT_SECOND'";
            string encoded=Convert.ToBase64String(Encoding.UTF8.GetBytes(firstPayload));
            string primaryCommand=$"Invoke-Expression ([Text.Encoding]::UTF8.GetString([Convert]::FromBase64String('{encoded}')))";
            await secondBrowser.ExecuteScriptAsync($"terminal.input({JsonSerializer.Serialize(backgroundCommand+"\r")})");
            await Until(()=>
            {
                string output;
                lock(secondGate) output=secondOutput.ToString();
                return Task.FromResult(output.Contains("BACKGROUND_ACTIVITY_5",StringComparison.Ordinal));
            },"background pane is emitting output before foreground typing");
            await SendBrowserKeyboardTextAsync(firstBrowser,primaryCommand+"\r");
            await Until(()=>
            {
                string output;
                lock(firstGate) output=firstOutput.ToString();
                return Task.FromResult(output.Contains("ORDERED_INPUT_FIRST",StringComparison.Ordinal)&&output.Contains("ORDERED_INPUT_SECOND",StringComparison.Ordinal));
            },"focused pane receives input while another pane emits output");
            await Until(()=>
            {
                string output;
                lock(secondGate) output=secondOutput.ToString();
                return Task.FromResult(output.Contains("BACKGROUND_ACTIVITY_60",StringComparison.Ordinal));
            },"background pane completes output activity");
            string primaryOutput,secondaryOutput;
            lock(firstGate) primaryOutput=firstOutput.ToString();
            lock(secondGate) secondaryOutput=secondOutput.ToString();
            Assert(primaryOutput.IndexOf("ORDERED_INPUT_FIRST",StringComparison.Ordinal)<primaryOutput.IndexOf("ORDERED_INPUT_SECOND",StringComparison.Ordinal)&&
                Count(primaryOutput,"ORDERED_INPUT_FIRST")==1&&Count(primaryOutput,"ORDERED_INPUT_SECOND")==1,
                "focused pane receives isolated ordered input exactly once during background terminal activity");
            Assert(!secondaryOutput.Contains("ORDERED_INPUT_FIRST",StringComparison.Ordinal)&&!secondaryOutput.Contains("ORDERED_INPUT_SECOND",StringComparison.Ordinal),
                "background pane receives no foreground typed command output");
            Assert(host.IsActive&&await firstBrowser.ExecuteScriptAsync("document.activeElement===terminal.textarea")=="true",
                "background terminal output does not steal focused pane keyboard input");

            using var resizeStarted=new ManualResetEventSlim();
            using var releaseResize=new ManualResetEventSlim();
            object lifecycleWorker=Field<object>(firstSession,"_lifecycleWorker");
            var requestResize=lifecycleWorker.GetType().GetMethod("RequestResize",BindingFlags.Instance|BindingFlags.Public)!;
            var requestTimer=System.Diagnostics.Stopwatch.StartNew();
            requestResize.Invoke(lifecycleWorker,new object?[] {(short)111,(short)31,(Action<short,short>)((_,_)=>
            {
                resizeStarted.Set();
                releaseResize.Wait(TimeSpan.FromSeconds(30));
            })});
            requestTimer.Stop();
            Assert(requestTimer.Elapsed<TimeSpan.FromMilliseconds(250),
                "blocked resize request returns promptly to its caller");
            Assert(resizeStarted.Wait(TimeSpan.FromSeconds(5)),"first pane native resize entered the blocked test operation");
            Task? closeTask=null;
            try
            {
                const string duringResize="PANE_B_DURING_BLOCKED_RESIZE";
                await secondBrowser.ExecuteScriptAsync($"terminal.input({JsonSerializer.Serialize("Write-Output '"+duringResize+"'\r")})");
                await Until(()=>
                {
                    string output;
                    lock(secondGate) output=secondOutput.ToString();
                    return Task.FromResult(output.Contains(duringResize,StringComparison.Ordinal));
                },"second PowerShell pane accepts input while first pane resize is blocked");
                Assert(true,"second pane executes a real shell command while first pane resize is blocked");

                int? firstProcessId=Property<uint?>(firstSession,"ProcessId") is uint pid?(int)pid:null;
                var processExited=Field<TaskCompletionSource>(firstSession,"_processExited").Task;
                var closeTimer=System.Diagnostics.Stopwatch.StartNew();
                closeTask=(Task)firstSession.GetType().GetMethod("CloseAsync",BindingFlags.Instance|BindingFlags.Public)!.Invoke(firstSession,null)!;
                closeTimer.Stop();
                Assert(closeTimer.Elapsed<TimeSpan.FromMilliseconds(250),"first pane close request returns promptly during blocked resize");
                await processExited.WaitAsync(TimeSpan.FromSeconds(10));
                Assert(true,"first pane owned PowerShell process exits before blocked resize is released");
                if(firstProcessId is int ownedPid)
                {
                    bool stillAlive;
                    try { using var process=System.Diagnostics.Process.GetProcessById(ownedPid); stillAlive=!process.HasExited; }
                    catch(ArgumentException) { stillAlive=false; }
                    Assert(!stillAlive,"closed pane no longer owns a live PowerShell process");
                }

                const string afterClose="PANE_B_AFTER_OTHER_PANE_CLOSE";
                await secondBrowser.ExecuteScriptAsync($"terminal.input({JsonSerializer.Serialize("Write-Output '"+afterClose+"'\r")})");
                await Until(()=>
                {
                    string output;
                    lock(secondGate) output=secondOutput.ToString();
                    return Task.FromResult(output.Contains(afterClose,StringComparison.Ordinal));
                },"second PowerShell pane continues accepting input after first pane closes");
                Assert(true,"second pane executes another shell command after first pane closes");
            }
            finally
            {
                releaseResize.Set();
                closeTask??=(Task)firstSession.GetType().GetMethod("CloseAsync",BindingFlags.Instance|BindingFlags.Public)!.Invoke(firstSession,null)!;
                try { await closeTask.WaitAsync(TimeSpan.FromSeconds(10)); }
                catch(Exception exception) { Record("first pane close cleanup after releasing test resize: "+exception.GetType().Name); }
            }
        }
        finally
        {
            host.Close();
            first.Dispose();
            second.Dispose();
        }
    }
    private static async Task SendBrowserKeyboardTextAsync(WebView2 browser,string text)
    {
        foreach(char character in text)
        {
            string key,code,unmodifiedText;
            int virtualKey;
            bool shift=false;
            if(char.IsAsciiLetter(character))
            {
                char upper=char.ToUpperInvariant(character);
                key=character.ToString(); code="Key"+upper; virtualKey=upper; unmodifiedText=char.ToLowerInvariant(character).ToString();
                shift=char.IsUpper(character);
            }
            else if(char.IsAsciiDigit(character))
            {
                key=code=character.ToString(); virtualKey=character; unmodifiedText=character.ToString();
            }
            else
            {
                (key,code,virtualKey,unmodifiedText,shift)=character switch
                {
                    '\r' => ("Enter","Enter",13,"\r",false), ' '=> (" ","Space",32," ",false), '-' => ("-","Minus",189,"-",false), '_' => ("_","Minus",189,"-",true),
                    '.' => (".","Period",190,".",false), '(' => ("(","Digit9",57,"9",true), ')' => (")","Digit0",48,"0",true),
                    '[' => ("[","BracketLeft",219,"[",false), ']' => ("]","BracketRight",221,"]",false),
                    '\'' => ("'","Quote",222,"'",false), '+' => ("+","Equal",187,"=",true), '=' => ("=","Equal",187,"=",false),
                    '/' => ("/","Slash",191,"/",false), ':' => (":","Semicolon",186,";",true), ';' => (";","Semicolon",186,";",false),
                    _ => throw new NotSupportedException($"Input test character is not mapped: U+{(int)character:X4}")
                };
            }
            string keyDown=character=='\r'
                ? JsonSerializer.Serialize(new {type="keyDown",key,code,windowsVirtualKeyCode=virtualKey,nativeVirtualKeyCode=virtualKey,shiftKey=shift})
                : JsonSerializer.Serialize(new {type="keyDown",key,code,windowsVirtualKeyCode=virtualKey,nativeVirtualKeyCode=virtualKey,text=character.ToString(),unmodifiedText,shiftKey=shift});
            await browser.CoreWebView2.CallDevToolsProtocolMethodAsync("Input.dispatchKeyEvent",keyDown);
            string keyUp=JsonSerializer.Serialize(new {type="keyUp",key,code,windowsVirtualKeyCode=virtualKey,nativeVirtualKeyCode=virtualKey,shiftKey=shift});
            await browser.CoreWebView2.CallDevToolsProtocolMethodAsync("Input.dispatchKeyEvent",keyUp);
        }
    }
    private static async Task SendBrowserCtrlShiftTAsync(WebView2 browser)
    {
        async Task Dispatch(string type,string key,string code,int virtualKey,int modifiers)
        {
            string payload=JsonSerializer.Serialize(new
            {
                type,key,code,windowsVirtualKeyCode=virtualKey,nativeVirtualKeyCode=virtualKey,modifiers
            });
            await browser.CoreWebView2.CallDevToolsProtocolMethodAsync("Input.dispatchKeyEvent",payload);
        }
        await Dispatch("rawKeyDown","Control","ControlLeft",17,2);
        await Dispatch("rawKeyDown","Shift","ShiftLeft",16,10);
        await Dispatch("rawKeyDown","T","KeyT",84,10);
        await Dispatch("keyUp","T","KeyT",84,10);
        await Dispatch("keyUp","Shift","ShiftLeft",16,2);
        await Dispatch("keyUp","Control","ControlLeft",17,0);
    }
    private static async Task RunExplicitTerminalCreationSmoke(MainWindow window,TabControl tabs,TabItem ownerTab,TerminalPane ownerPane,WebView2 ownerBrowser)
    {
        int originalCount=tabs.Items.Count;
        int originalCapacity=Field<int>(window,"_splitCapacity");
        void AssertVisibleLayout(params TerminalPane[] panes)
        {
            window.UpdateLayout();
            var bounds=panes.Select(pane=>pane.TransformToAncestor(window).TransformBounds(new System.Windows.Rect(0,0,pane.ActualWidth,pane.ActualHeight))).ToArray();
            Assert(panes.All(pane=>pane.IsVisible&&pane.ActualWidth>0&&pane.ActualHeight>0)&&
                !bounds.Where((first,index)=>bounds.Skip(index+1).Any(second=>System.Windows.Rect.Intersect(first,second) is var overlap&&!overlap.IsEmpty&&overlap.Width>1&&overlap.Height>1)).Any(),
                "task and added terminals occupy separate visible areas");
            Assert(((FrameworkElement)window.FindName("NotesPanel")).IsVisible&&Field<FrameworkElement>(window,"_taskSidebarSurface").IsVisible,
                "adding terminals retains the task sidebar and plan panel");
        }
        object runtime=Call(window,"RuntimeFor",ownerPane.WorkingDirectory)!;
        Assert(ReferenceEquals(runtime.GetType().GetField("SingleTerminal")!.GetValue(runtime),ownerTab),
            "explicit terminal fixture starts with the original task pane as its owner");
        var extras=new List<TabItem>();
        try
        {
            const string existingOutput="ORIGINAL_TASK_PANE_OUTPUT_BEFORE_EXTRAS";
            await ownerBrowser.ExecuteScriptAsync("send({type:'input',data:"+JsonSerializer.Serialize("Set-PSReadLineOption -HistorySaveStyle SaveNothing; Write-Output ('ORIGINAL_TASK_PANE_' + 'OUTPUT_BEFORE_EXTRAS')\r")+"})");
            await Until(async ()=>
            {
                string before=JsonSerializer.Deserialize<string>(await ownerBrowser.ExecuteScriptAsync(
                    "Array.from({length:terminal.buffer.active.length},(_,i)=>terminal.buffer.active.getLine(i).translateToString(true)).join('\\n')"))??"";
                return before.Contains(existingOutput,StringComparison.Ordinal);
            },"original task process prints output before extra panes");
            ownerPane.FocusTerminal();
            await Until(async ()=>await ownerBrowser.ExecuteScriptAsync("document.activeElement===terminal.textarea")=="true",
                "original pane receives WebView keyboard focus");
            await SendBrowserCtrlShiftTAsync(ownerBrowser);
            await Until(()=>Task.FromResult(tabs.Items.Count>originalCount),"Ctrl+Shift+T adds an independent terminal");
            extras.AddRange(tabs.Items.Cast<TabItem>().Where(tab=>!ReferenceEquals(tab,ownerTab)));
            Assert(tabs.Items.Count==originalCount+1,"Ctrl+Shift+T creates exactly one additional pane");
            var shortcutTab=tabs.Items.Cast<TabItem>().Single(tab=>!ReferenceEquals(tab,ownerTab));
            var shortcutPane=Pane(shortcutTab);
            await Until(()=>Task.FromResult(shortcutTab.IsSelected&&shortcutPane.IsVisible&&shortcutPane.IsReady&&
                shortcutPane.ActualWidth>0&&shortcutPane.ActualHeight>0&&ownerPane.IsVisible&&ownerPane.ActualWidth>0&&ownerPane.ActualHeight>0),
                "new shortcut pane is selected, visible, and ready");
            Assert(tabs.Items.Count==originalCount+1&&shortcutTab.IsSelected&&shortcutPane.IsVisible&&
                ownerPane.IsVisible&&ownerPane.ActualWidth>0&&ownerPane.ActualHeight>0&&
                shortcutPane.ActualWidth>0&&shortcutPane.ActualHeight>0&&
                string.Equals(shortcutPane.WorkingDirectory,ownerPane.WorkingDirectory,StringComparison.OrdinalIgnoreCase)&&
                ReferenceEquals(runtime.GetType().GetField("SingleTerminal")!.GetValue(runtime),ownerTab),
                "Ctrl+Shift+T keeps the task pane visible with bounds while adding a ready pane without replacing task ownership");
            AssertVisibleLayout(ownerPane,shortcutPane);

            ((Button)window.FindName("NewTerminalButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await Until(()=>Task.FromResult(tabs.Items.Count>originalCount+1),"the explicit + terminal action creates another pane");
            extras.AddRange(tabs.Items.Cast<TabItem>().Where(tab=>!ReferenceEquals(tab,ownerTab)&&!extras.Contains(tab)));
            Assert(tabs.Items.Count==originalCount+2,"the + terminal action creates exactly one additional pane");
            var buttonTab=tabs.Items.Cast<TabItem>().Single(tab=>!ReferenceEquals(tab,ownerTab)&&!ReferenceEquals(tab,shortcutTab));
            var buttonPane=Pane(buttonTab);
            await Until(()=>Task.FromResult(buttonTab.IsSelected&&buttonPane.IsVisible&&buttonPane.IsReady&&
                buttonPane.ActualWidth>0&&buttonPane.ActualHeight>0&&ownerPane.IsVisible&&ownerPane.ActualWidth>0&&ownerPane.ActualHeight>0&&
                shortcutPane.IsVisible&&shortcutPane.ActualWidth>0&&shortcutPane.ActualHeight>0),
                "button-created pane is selected, visible, and ready");
            Assert(tabs.Items.Count==originalCount+2&&buttonTab.IsSelected&&buttonPane.IsVisible&&
                ownerPane.IsVisible&&ownerPane.ActualWidth>0&&ownerPane.ActualHeight>0&&
                shortcutPane.IsVisible&&shortcutPane.ActualWidth>0&&shortcutPane.ActualHeight>0&&
                buttonPane.ActualWidth>0&&buttonPane.ActualHeight>0&&
                string.Equals(buttonPane.WorkingDirectory,ownerPane.WorkingDirectory,StringComparison.OrdinalIgnoreCase)&&
                ReferenceEquals(runtime.GetType().GetField("SingleTerminal")!.GetValue(runtime),ownerTab),
                "the + terminal action keeps the task pane and prior extra visible with bounds while adding a ready pane");
            AssertVisibleLayout(ownerPane,shortcutPane,buttonPane);
            Call(window,"SelectTerminal",ownerTab,false);
            await Until(()=>Task.FromResult(ReferenceEquals(tabs.SelectedItem,ownerTab)&&ownerPane.IsVisible&&ownerPane.ActualWidth>0&&ownerPane.ActualHeight>0&&
                shortcutPane.IsVisible&&shortcutPane.ActualWidth>0&&shortcutPane.ActualHeight>0&&buttonPane.IsVisible&&buttonPane.ActualWidth>0&&buttonPane.ActualHeight>0),
                "selecting the original task pane keeps every additional pane visible with bounds");
            AssertVisibleLayout(ownerPane,shortcutPane,buttonPane);
            string wrappedOutput=JsonSerializer.Deserialize<string>(await ownerBrowser.ExecuteScriptAsync(
                "Array.from({length:terminal.buffer.active.length},(_,i)=>terminal.buffer.active.getLine(i).translateToString(true)).join('\\n')"))??"";
            // Resizing wraps one logical output line across several xterm rows.
            // Rejoin only rows marked as continuations, preserving real line breaks.
            string logicalOutput=JsonSerializer.Deserialize<string>(await ownerBrowser.ExecuteScriptAsync(
                "Array.from({length:terminal.buffer.active.length},(_,i)=>{const line=terminal.buffer.active.getLine(i);return (line.isWrapped?'':'\\n')+line.translateToString(true)}).join('')"))??"";
            Console.WriteLine($"Original output after resize: physicalRowsContainMarker={wrappedOutput.Contains(existingOutput,StringComparison.Ordinal)}, logicalLinesContainMarker={logicalOutput.Contains(existingOutput,StringComparison.Ordinal)}, running={ownerPane.IsRunning}");
            Assert(ownerPane.IsRunning&&ReferenceEquals(runtime.GetType().GetField("SingleTerminal")!.GetValue(runtime),ownerTab)&&
                logicalOutput.Contains(existingOutput,StringComparison.Ordinal),
                "the original task session and its existing shell output survive adding and selecting extra panes");
        }
        finally
        {
            bool allExtraShellsExited=true;
            foreach(var extraTab in extras.AsEnumerable().Reverse())
            {
                var extraPane=Pane(extraTab);
                if(extraPane.IsRunning)
                {
                    if(extraPane.IsReady)
                    {
                        try
                        {
                            var extraBrowser=Field<WebView2>(extraPane,"_browser");
                            await extraBrowser.ExecuteScriptAsync("send({type:'input',data:"+JsonSerializer.Serialize("exit\r")+"})");
                            await Until(()=>Task.FromResult(!extraPane.IsRunning),"temporary shell pane exits for cleanup",10);
                        }
                        catch { allExtraShellsExited=false; }
                    }
                    else allExtraShellsExited=false;
                }
                if(tabs.Items.Contains(extraTab)&&!extraPane.IsRunning) Call(window,"CloseTerminal",extraTab,extraPane);
                else
                {
                    if(tabs.Items.Contains(extraTab))
                    {
                        Call(window,"DetachTaskTerminal",extraTab);
                        tabs.Items.Remove(extraTab);
                    }
                    extraPane.Dispose();
                }
            }
            if(!allExtraShellsExited) throw new Exception("A temporary shell pane required silent disposal after graceful exit timed out.");
        }
        Assert(tabs.Items.Count==originalCount&&ReferenceEquals(runtime.GetType().GetField("SingleTerminal")!.GetValue(runtime),ownerTab)&&ownerPane.IsRunning&&
            ownerPane.IsVisible&&ownerPane.ActualWidth>0&&ownerPane.ActualHeight>0,
            "closing unowned panes preserves the original task pane and session");
        AssertVisibleLayout(ownerPane);
        Assert(Field<int>(window,"_splitCapacity")==originalCapacity,"closing extra panes restores the previous task layout capacity");
        ownerPane.FocusTerminal();
        const string proof="ORIGINAL_TASK_PANE_AFTER_EXTRA_CLOSE";
        await ownerBrowser.ExecuteScriptAsync("send({type:'input',data:"+JsonSerializer.Serialize("Write-Output ('ORIGINAL_TASK_PANE_' + 'AFTER_EXTRA_CLOSE')\r")+"})");
        await Until(async ()=>
        {
            string screen=JsonSerializer.Deserialize<string>(await ownerBrowser.ExecuteScriptAsync(
                "Array.from({length:terminal.buffer.active.length},(_,i)=>terminal.buffer.active.getLine(i).translateToString(true)).join('\\n')"))??"";
            return screen.Contains(proof,StringComparison.Ordinal);
        },"original task pane accepts shell input after extra panes close");
        Assert(true,"original task pane remains usable after extra panes close");
        Call(window,"ShowProjectTask",ownerPane.WorkingDirectory);
        await Until(()=>Task.FromResult(Field<bool>(window,"_taskWorkspaceVisible")&&
            Field<RadioButton>(window,"_normalTasksTab").IsVisible&&Field<RadioButton>(window,"_archivedTasksTab").IsVisible),
            "restore the original task workspace view after explicit-terminal smoke");
        Assert(ReferenceEquals(tabs.SelectedItem,ownerTab),"explicit-terminal smoke restores the original task owner selection");
    }
    private static int Count(string value,string fragment)
    {
        int count=0,index=0;
        while((index=value.IndexOf(fragment,index,StringComparison.Ordinal))>=0){count++;index+=fragment.Length;}
        return count;
    }
    private static async Task RunExitPreflightTimeoutSmoke(string fixture)
    {
        var window=new MainWindow(Path.Combine(fixture,"exit-settings.json"),autoStartCodex:false,initialWorkspace:fixture);
        var unfinishedJournal=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Task unfinishedJournalTask=unfinishedJournal.Task;
        var panes=new List<TerminalPane>();
        object? runtime=null;
        try
        {
            window.Show();
            var tabs=(TabControl)window.FindName("TerminalTabs");
            await Until(()=>Task.FromResult(tabs.Items.Count>0),"exit preflight fixture terminal pane");
            panes.AddRange(tabs.Items.Cast<TabItem>().Select(Pane));
            runtime=Call(window,"RuntimeFor",fixture);
            runtime!.GetType().GetField("Polling")!.SetValue(runtime,true);
            Field<HashSet<Task>>(window,"_journalWrites").Add(unfinishedJournalTask);

            Task exitAttempt=(Task)Call(window,"RequestExitAsync")!;
            await Until(()=>Task.FromResult(Field<bool>(window,"_closePending")),"exit preflight pending state");
            Assert(panes.All(pane=>!Property<bool>(pane,"IsUserInputBlocked")&&pane.IsEnabled),
                "exit preflight timeout leaves every terminal pane enabled and accepting input");
            await exitAttempt.WaitAsync(TimeSpan.FromSeconds(20));

            Assert(!Field<bool>(window,"_closePending")&&!Field<bool>(window,"_resourcesDisposed"),
                "timed-out exit preflight restores the window and keeps resources alive");
            Assert(Field<HashSet<Task>>(window,"_journalWrites").Contains(unfinishedJournalTask)&&!unfinishedJournal.Task.IsCompleted,
                "timed-out exit preflight preserves unfinished journal work");
            Assert(panes.All(pane=>!Property<bool>(pane,"IsUserInputBlocked")&&pane.IsEnabled),
                "terminal panes remain usable after exit preflight times out");
        }
        finally
        {
            if(runtime is not null) runtime.GetType().GetField("Polling")!.SetValue(runtime,false);
            Field<HashSet<Task>>(window,"_journalWrites").Remove(unfinishedJournalTask);
            unfinishedJournal.TrySetResult();
            Call(window,"DisposeResources");
            window.Close();
            foreach(var pane in panes) pane.Dispose();
        }
    }
    private static async Task Run(Application app)
    {
        var fixture=Path.Combine(Path.GetTempPath(),"TerminalNotes UI "+Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(fixture);
        AppContext.SetData("TerminalNotes.DataDirectory", Path.Combine(fixture,"app-data"));
        Environment.CurrentDirectory=fixture;
        if(Environment.GetEnvironmentVariable("TERMINALNOTES_UI_INDEPENDENT_ONLY")=="1")
        {
            try { await IndependentTerminalSmoke.RunAsync(fixture); Console.WriteLine("UI INDEPENDENT TERMINAL SMOKE PASSED"); }
            catch(Exception ex) { _fail=1; Console.WriteLine("FAIL "+ex); }
            finally { app.Shutdown(); Console.WriteLine("Fixture: "+fixture); }
            return;
        }
        if(Environment.GetEnvironmentVariable("TERMINALNOTES_UI_NATIVE_ADD_ONLY")=="1")
        {
            try { await RunNativeTerminalAddSmoke(fixture); Console.WriteLine("UI NATIVE TERMINAL ADD SMOKE PASSED"); }
            catch(Exception ex) { _fail=1; Console.WriteLine("FAIL "+ex); }
            finally { app.Shutdown(); Console.WriteLine("Fixture: "+fixture); }
            return;
        }
        if(Environment.GetEnvironmentVariable("TERMINALNOTES_UI_STARTUP_PICKER_ONLY")=="1")
        {
            try { await RunStartupProjectPickerSmoke(fixture); Console.WriteLine("UI STARTUP PROJECT PICKER SMOKE PASSED"); }
            catch(Exception ex) { _fail=1; Console.WriteLine("FAIL "+ex); }
            finally { app.Shutdown(); Console.WriteLine("Fixture: "+fixture); }
            return;
        }
        if(Environment.GetEnvironmentVariable("TERMINALNOTES_UI_WORKAREA_ONLY")=="1")
        {
            MainWindow? boundsWindow=null;
            try
            {
                File.WriteAllText(Path.Combine(fixture,"settings.json"),JsonSerializer.Serialize(new { Workspace=fixture, SetupCompleted=true, Width=10000, Height=10000 }));
                boundsWindow=new MainWindow(Path.Combine(fixture,"settings.json"),autoStartCodex:false,initialWorkspace:fixture);
                boundsWindow.Show();
                await Task.Delay(500);
                var initial=GetWindowWorkAreaBounds(boundsWindow);
                Console.WriteLine($"Window bounds oversized startup: native={FormatRect(initial.Window)} visible={FormatRect(initial.Visible)} work={FormatRect(initial.Work)}");
                Assert(RectFitsWorkArea(initial.Visible,initial.Work,1),"oversized saved window fits work area");
                Assert(Field<object?>(boundsWindow,"_windowBoundsSource") is not null,"native window bounds hook attached");
                boundsWindow.Width=Math.Min(1000,boundsWindow.Width);
                boundsWindow.Height=Math.Min(700,boundsWindow.Height);
                await Task.Delay(100);
                initial=GetWindowWorkAreaBounds(boundsWindow);
                ((Button)boundsWindow.FindName("MaximizeWindowButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                await Task.Delay(500);
                var maximized=GetWindowWorkAreaBounds(boundsWindow);
                Console.WriteLine($"Window bounds focused maximize: content={FormatRect(GetWindowContentBounds(boundsWindow))} native={FormatRect(maximized.Window)} work={FormatRect(maximized.Work)}");
                Assert(MaximizedBoundsMatchWorkArea(boundsWindow),"focused maximum fills work area without covering taskbar");
                ((Button)boundsWindow.FindName("MaximizeWindowButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                await Task.Delay(300);
                Assert(RestoredBoundsMatch(boundsWindow,initial.Window),"focused restore preserves fitted normal size");
                Assert(((FrameworkElement)boundsWindow.Content).Margin==new Thickness(0),"restore removes maximized content inset");
                boundsWindow.WindowState=WindowState.Maximized;
                await Task.Delay(500);
                var directMaximum=GetWindowWorkAreaBounds(boundsWindow);
                Console.WriteLine($"Window bounds direct maximize: content={FormatRect(GetWindowContentBounds(boundsWindow))} native={FormatRect(directMaximum.Window)} work={FormatRect(directMaximum.Work)}");
                Assert(MaximizedBoundsMatchWorkArea(boundsWindow),"direct maximize also fits work area");
                boundsWindow.WindowState=WindowState.Normal;
                await Task.Delay(300);
                Assert(RestoredBoundsMatch(boundsWindow,initial.Window),"direct restore preserves fitted normal size");
                Console.WriteLine("UI WORK AREA SMOKE PASSED");
            }
            catch(Exception ex) { _fail=1; Console.WriteLine("FAIL "+ex); }
            finally
            {
                if(boundsWindow is not null) { Call(boundsWindow,"DisposeResources"); boundsWindow.Close(); }
                app.Shutdown();
                Console.WriteLine("Fixture: "+fixture);
            }
            return;
        }
        if(Environment.GetEnvironmentVariable("TERMINALNOTES_UI_NOTIFICATIONS_ONLY")=="1")
        {
            try { RunTaskNotificationOptionsSmoke(fixture); Console.WriteLine("UI TASK NOTIFICATION SMOKE PASSED"); }
            catch(Exception ex) { _fail=1; Console.WriteLine("FAIL "+ex); }
            finally { app.Shutdown(); Console.WriteLine("Fixture: "+fixture); }
            return;
        }
        if(Environment.GetEnvironmentVariable("TERMINALNOTES_UI_COMPOSER_ONLY")=="1")
        {
            try
            {
                await RunComposerSmoke(fixture);
                Console.WriteLine(string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("TERMINALNOTES_UI_DESIGN_PREVIEW"))
                    ? "UI COMPOSER SMOKE PASSED" : "UI DESIGN PREVIEW CAPTURED");
            }
            catch(Exception ex) { _fail=1; Console.WriteLine("FAIL "+ex); }
            finally { app.Shutdown(); Console.WriteLine("Fixture: "+fixture); }
            return;
        }
        if(Environment.GetEnvironmentVariable("TERMINALNOTES_UI_INPUT_ONLY")=="1")
        {
            try
            {
                await RunTwoPaneInputIsolationSmoke(fixture);
                Console.WriteLine("UI TWO-PANE INPUT ISOLATION PASSED");
            }
            catch(Exception ex) { _fail=1; Console.WriteLine("FAIL "+ex); }
            finally { app.Shutdown(); Console.WriteLine("Fixture: "+fixture); }
            return;
        }
        if(Environment.GetEnvironmentVariable("TERMINALNOTES_UI_EXIT_ONLY")=="1")
        {
            try { await RunExitPreflightTimeoutSmoke(fixture); Console.WriteLine("UI EXIT PREFLIGHT TIMEOUT SMOKE PASSED"); }
            catch(Exception ex) { _fail=1; Console.WriteLine("FAIL "+ex); }
            finally { app.Shutdown(); Console.WriteLine("Fixture: "+fixture); }
            return;
        }
        // Shell-only test windows skip application startup discovery. Use the
        // same installed-command resolution before probing OpenCode in ConPTY.
        var installation = await new OpenCodeInstallationService().CheckAsync();
        Assert(installation.IsInstalled, "installed OpenCode discovery");
        var window=new MainWindow(Path.Combine(fixture,"settings.json"),autoStartCodex:false,initialWorkspace:fixture);
        // Discovery was verified above. The shell fixture must not rerun first-use setup
        // when it checks that plan input is saved and rejected by a non-OpenCode pane.
        window.GetType().GetField("_openCodeReady",BindingFlags.Instance|BindingFlags.NonPublic)!.SetValue(window,true);
        var nativeOutput=new StringBuilder();
        try
        {
            window.Show();
            var tabs=(TabControl)window.FindName("TerminalTabs");
            if(tabs.Items.Count>0)
            {
                var initialPane=Pane((TabItem)tabs.Items[0]);
                var session=Field<object>(initialPane,"_session");
                session.GetType().GetEvent("OutputReceived")!.AddEventHandler(session,(Action<string>)(text=>
                {
                    lock(nativeOutput)
                    {
                        nativeOutput.Append(text);
                        if(nativeOutput.Length>12000) nativeOutput.Remove(0,nativeOutput.Length-12000);
                    }
                }));
            }
            await Until(()=>Task.FromResult(tabs.Items.Count==1 && Pane((TabItem)tabs.Items[0]).IsRunning),"terminal startup");
            var pane=Pane((TabItem)tabs.Items[0]);
            var startupBounds=GetWindowWorkAreaBounds(window);
            Assert(RectFitsWorkArea(startupBounds.Visible,startupBounds.Work,1),
                $"startup visible window fits current monitor work area (native={FormatRect(startupBounds.Window)}, visible={FormatRect(startupBounds.Visible)}, work={FormatRect(startupBounds.Work)})");
            Console.WriteLine($"Window bounds startup: native={FormatRect(startupBounds.Window)} visible={FormatRect(startupBounds.Visible)} work={FormatRect(startupBounds.Work)}");
            Assert(Field<int>(window,"_splitCapacity")==1,"one terminal automatically uses one-pane layout");
            var browser=Field<WebView2>(pane,"_browser");
            async Task<string> Screen()=>JsonSerializer.Deserialize<string>(await browser.ExecuteScriptAsync("Array.from({length:terminal.buffer.active.length},(_,i)=>terminal.buffer.active.getLine(i).translateToString(true)).join('\\n')"))??"";
            try { await Until(async ()=>(await Screen()).Contains("PS "),"PowerShell prompt"); }
            catch
            {
                Console.WriteLine("Terminal screen: "+await Screen());
                var session=Field<ConPtySession>(pane,"_session");
                var pending=(System.Collections.ICollection)Field<object>(pane,"_pending");
                Console.WriteLine("Terminal diagnostics: "+JsonSerializer.Serialize(new
                {
                    pane.IsRunning,
                    pane.IsReady,
                    Failed=Field<bool>(pane,"_failed"),
                    Ready=Field<bool>(pane,"_ready"),
                    QueuedCharacters=Field<int>(pane,"_queuedCharacters"),
                    PendingCount=pending.Count,
                    WriteInFlight=Field<bool>(pane,"_writeInFlight"),
                    RetryOutput=Field<string?>(pane,"_retryOutput"),
                    FlushTimerEnabled=((System.Windows.Threading.DispatcherTimer)Field<object>(pane,"_flushTimer")).IsEnabled,
                    SessionRunning=session.IsRunning,
                    WebViewSource=browser.Source?.ToString(),
                    WebViewInitialized=browser.CoreWebView2 is not null,
                    BrowserTerminal=await browser.ExecuteScriptAsync("JSON.stringify({ready:document.readyState,terminal:typeof terminal,cols:typeof terminal==='undefined'?null:terminal.cols,rows:typeof terminal==='undefined'?null:terminal.rows,lines:typeof terminal==='undefined'?null:terminal.buffer.active.length})"),
                    Status=((TextBlock)window.FindName("StatusLabel")).Text,
                    NativeOutput=TrimDiagnostic(nativeOutput.ToString(),4000)
                }));
                throw;
            }
            Assert(true,"WPF / WebView2 / xterm / ConPTY startup");
            await RunTwoPaneInputIsolationSmoke(fixture);
            window.Activate();
            pane.FocusTerminal();
            var settingsButton=(Button)window.FindName("WorkSettingsButton");
            var minimizeButton=(Button)window.FindName("MinimizeWindowButton");
            var maximizeButton=(Button)window.FindName("MaximizeWindowButton");
            var settingsPoint=settingsButton.TranslatePoint(new Point(0,0),window);
            var minimizePoint=minimizeButton.TranslatePoint(new Point(0,0),window);
            Assert(settingsButton.Content is TextBlock && settingsPoint.Y==minimizePoint.Y &&
                Math.Abs(settingsPoint.X+settingsButton.ActualWidth-minimizePoint.X)<1,
                "settings icon is directly left of minimize in the title bar");
            maximizeButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await Until(()=>Task.FromResult(window.WindowState==WindowState.Maximized),"maximize from caption");
            try { await Until(()=>Task.FromResult(MaximizedBoundsMatchWorkArea(window)),"maximized content bounds settle to work area"); }
            catch
            {
                var failedBounds=GetWindowWorkAreaBounds(window);
                Console.WriteLine($"Window bounds maximize failure: content={FormatRect(GetWindowContentBounds(window))} native={FormatRect(failedBounds.Window)} visible={FormatRect(failedBounds.Visible)} work={FormatRect(failedBounds.Work)}");
                throw;
            }
            var maximizedBounds=GetWindowWorkAreaBounds(window);
            Assert(RectMatchesWorkArea(GetWindowContentBounds(window),maximizedBounds.Work,1),
                $"maximized content reaches but does not cross monitor work area (content={FormatRect(GetWindowContentBounds(window))}, native={FormatRect(maximizedBounds.Window)}, work={FormatRect(maximizedBounds.Work)})");
            Console.WriteLine($"Window bounds maximized: content={FormatRect(GetWindowContentBounds(window))} native={FormatRect(maximizedBounds.Window)} visible={FormatRect(maximizedBounds.Visible)} work={FormatRect(maximizedBounds.Work)}");
            maximizeButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await Until(()=>Task.FromResult(window.WindowState==WindowState.Normal),"restore from caption");
            await Until(()=>Task.FromResult(RestoredBoundsMatch(window,startupBounds.Window)),"restored native bounds settle");
            var restoredBounds=GetWindowWorkAreaBounds(window);
            Assert(RestoredBoundsMatch(restoredBounds,startupBounds.Window) &&
                RectFitsWorkArea(restoredBounds.Visible,restoredBounds.Work,1) && pane.IsRunning,
                $"restore returns to normal geometry within work area and keeps terminal alive (startup={FormatRect(startupBounds.Window)}, native={FormatRect(restoredBounds.Window)}, visible={FormatRect(restoredBounds.Visible)}, work={FormatRect(restoredBounds.Work)}, running={pane.IsRunning})");
            Console.WriteLine($"Window bounds restored: native={FormatRect(restoredBounds.Window)} visible={FormatRect(restoredBounds.Visible)} work={FormatRect(restoredBounds.Work)}");
            minimizeButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await Until(()=>Task.FromResult(window.WindowState==WindowState.Minimized && pane.IsRunning),"minimize preserves terminal");
            Call(window,"RestoreFromTray");
            await Until(()=>Task.FromResult(window.WindowState==WindowState.Normal && window.IsVisible),"restore minimized window");
            CaptureWindow(window,"caption-smoke.png");
            // Each phase emits its own output marker before exercising its layout changes.
            await RunExplicitTerminalCreationSmoke(window,tabs,(TabItem)tabs.Items[0],pane,browser);
            // The fixture must not save shell commands into the user's PSReadLine history.
            var command="Set-PSReadLineOption -HistorySaveStyle SaveNothing; Write-Output ('UI_' + '한글_OK'); if((Get-Location).Path -eq '"+fixture.Replace("'","''")+"'){Write-Output ('CWD_'+'OK')}\r";
            await browser.ExecuteScriptAsync("send({type:'input',data:"+JsonSerializer.Serialize(command)+"})");
            await Until(async ()=>(await Screen()).Contains("UI_한글_OK"),"Korean terminal roundtrip");
            await Until(async ()=>(await Screen()).Contains("CWD_OK"),"terminal working directory with spaces");
            Assert(true,"terminal working directory with spaces");
            Assert(true,"Korean text through browser/native terminal bridge");
            // Exercise the command discovery actually verified, even when a stale
            // npm shim shadows the working native binary in the shell profile.
            var versionCommand = "& '" + installation.Path!.Replace("'", "''") + "' --version\r";
            await browser.ExecuteScriptAsync("send({type:'input',data:" + JsonSerializer.Serialize(versionCommand) + "})");
            try { await Until(async ()=>System.Text.RegularExpressions.Regex.IsMatch(await Screen(), @"(?m)^\s*\d+\.\d+\.\d+(?:[-+][\w.-]+)?\s*$"),"OpenCode CLI invocation"); }
            catch
            {
                Console.WriteLine("CLI probe path: " + installation.Path);
                Console.WriteLine("CLI probe screen: " + TrimDiagnostic(await Screen(), 3000));
                lock(nativeOutput) Console.WriteLine("CLI native output: " + TrimDiagnostic(nativeOutput.ToString(), 3000));
                throw;
            }
            Assert(true,"OpenCode CLI runs inside embedded terminal");
            var flushTimer=(System.Windows.Threading.DispatcherTimer)Field<object>(pane,"_flushTimer");
            var flushPriority=(System.Windows.Threading.DispatcherPriority)typeof(System.Windows.Threading.DispatcherTimer)
                .GetField("_priority",BindingFlags.Instance|BindingFlags.NonPublic)!.GetValue(flushTimer)!;
            Assert(flushPriority >= System.Windows.Threading.DispatcherPriority.Loaded,
                "terminal output flush timer uses a foreground dispatcher priority");
            var conversationPanel=(FrameworkElement)window.FindName("NotesPanel");
            var workbenchHost=(ContentControl)window.FindName("WorkbenchHost");
            var commandInput=Field<TextBox>(window,"_tabDocumentEditor");
            var commandSend=Field<Button>(window,"_tabDocumentSave");
            await Until(()=>Task.FromResult(commandInput.IsEnabled),"MD editor loaded");
            var focusButton=(Button)window.FindName("FocusButton");
            object firstTerminalState=((TabItem)tabs.Items[0]).Tag;
            Assert(conversationPanel.Visibility==Visibility.Visible && conversationPanel.IsAncestorOf(workbenchHost) &&
                workbenchHost.IsAncestorOf(commandInput) && workbenchHost.IsAncestorOf(commandSend) &&
                ((FrameworkElement)window.FindName("LegacyDocuments")).Visibility==Visibility.Collapsed,
                "right conversation panel contains plan document editor");
            var inputComposerPanel = (Grid)Field<TextBox>(window,"_projectInputEditor").Parent;
            Assert(!Descendants<RadioButton>(inputComposerPanel).Any() && !inputComposerPanel.IsAncestorOf(commandInput) && !Descendants<ComboBox>(inputComposerPanel).Any(),
                "plan input composer has no mode tabs and remains separate from the plan editor");
            Assert(!Descendants<RadioButton>(window).Any(button=>button.IsVisible && button.Content as string == "터미널") &&
                Field<RadioButton>(window,"_normalTasksTab").IsVisible && Field<RadioButton>(window,"_archivedTasksTab").IsVisible,
                "workspace exposes task and archive tabs without the old terminal tab");
            var planActionButtons=new[]{Field<Button>(window,"_tabDocumentSave"),Field<Button>(window,"_tabDocumentExecute")};
            Assert(planActionButtons.Select(button=>button.Content as string).SequenceEqual(new[]{"저장","실행"}) &&
                !Descendants<Button>(workbenchHost).Any(button=>button.IsVisible &&
                    button.Content as string is "다시 읽기" or "저장 후 실행" or "MD 선택" or "새 MD" or "저장 후 요청 보내기") &&
                planActionButtons.All(button=>button.IsVisible && button.ActualWidth>0 && button.ActualHeight>0 && inputComposerPanel.IsAncestorOf(button)) &&
                Descendants<Button>(inputComposerPanel).Where(button=>button.IsVisible).SequenceEqual(planActionButtons) &&
                planActionButtons[0].Parent is StackPanel actionButtonGroup &&
                ReferenceEquals(planActionButtons[1].Parent,actionButtonGroup) &&
                actionButtonGroup.HorizontalAlignment==HorizontalAlignment.Right &&
                Math.Abs(actionButtonGroup.TranslatePoint(new Point(actionButtonGroup.ActualWidth,0),inputComposerPanel).X-
                    inputComposerPanel.ActualWidth)<=1,
                "document and plan additions share one right-aligned Save and Execute pair below the plan input");
            var autoResume = Field<CheckBox>(window,"_autoResumeToggle");
            Assert(autoResume.IsChecked == true && autoResume.IsVisible && autoResume.ToolTip is string explanation && explanation.Contains("미완료"),
                "automatic progress is enabled by default and explains unfinished-work recovery");
            double editorTop = commandInput.TranslatePoint(new Point(0, 0), workbenchHost).Y;
            Assert(autoResume.IsVisible &&
                    autoResume.TranslatePoint(new Point(0, autoResume.ActualHeight), workbenchHost).Y <= editorTop,
                "automatic progress control sits above the plan editor");
            var composerEditor=Field<TextBox>(window,"_projectInputEditor");
            double composerEditorBottom = composerEditor.TranslatePoint(new Point(0, composerEditor.ActualHeight), workbenchHost).Y;
            Assert(planActionButtons.All(button =>
                    button.TranslatePoint(new Point(0, 0), workbenchHost).Y >= composerEditorBottom),
                "Save and Execute sit below the plan input");
            Assert(autoResume.Foreground is System.Windows.Media.SolidColorBrush foreground && foreground.Color.R >= 200,
                "automatic progress label keeps a readable foreground on the dark panel");
            var sidebarArrow = focusButton;
            var sidebarSplitter = (GridSplitter)window.FindName("NotesSplitter");
            var animatedSidebarColumn = Field<ColumnDefinition>(window, "_taskSidebarColumn");
            double sidebarWidthBefore = animatedSidebarColumn.Width.Value;
            Assert(sidebarArrow.Content is System.Windows.Shapes.Path && ReferenceEquals(sidebarArrow.Parent, sidebarSplitter.Parent) &&
                Grid.GetColumn(sidebarArrow) == Grid.GetColumn(sidebarSplitter) && sidebarArrow.VerticalAlignment == VerticalAlignment.Center &&
                Field<Button>(window, "_taskSidebarToggle").Visibility == Visibility.Collapsed,
                "one combined-panel arrow overlays the centered plan-terminal splitter rail");
            sidebarArrow.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await Until(() => Task.FromResult(animatedSidebarColumn.Width.Value <= 40 &&
                conversationPanel.Visibility == Visibility.Collapsed &&
                Field<RadioButton>(window, "_normalTasksTab").Visibility == Visibility.Collapsed &&
                Field<RadioButton>(window, "_archivedTasksTab").Visibility == Visibility.Collapsed),
                "one toggle closes task tabs and the plan together");
            Assert(sidebarArrow.IsVisible && pane.IsRunning,
                "the expand arrow remains reachable while the running terminal is preserved");
            sidebarArrow.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await Task.Delay(30);
            sidebarArrow.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await Task.Delay(30);
            sidebarArrow.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await Until(() => Task.FromResult(Math.Abs(animatedSidebarColumn.Width.Value - sidebarWidthBefore) < 0.5 &&
                conversationPanel.Visibility == Visibility.Visible &&
                Field<RadioButton>(window, "_normalTasksTab").Visibility == Visibility.Visible &&
                Field<RadioButton>(window, "_archivedTasksTab").Visibility == Visibility.Visible),
                "rapid combined-panel reversals restore the original task width, tabs, and plan");
            Assert(pane.IsRunning && ReferenceEquals(Pane((TabItem)tabs.Items[0]), pane),
                "animated sidebar folding preserves the terminal session");
            if(Environment.GetEnvironmentVariable("TERMINALNOTES_UI_STARTUP_ONLY")=="1")
            {
                Console.WriteLine("UI STARTUP AND ROUNDTRIP CHECKS PASSED");
                return;
            }
            autoResume.IsChecked = false;
            Assert(!Field<bool>(window,"_autoResumeEnabled") && pane.IsRunning,
                "automatic progress toggle changes preference without restarting the terminal");
            Assert(focusButton.Content is System.Windows.Shapes.Path &&
                System.Windows.Automation.AutomationProperties.GetName(focusButton).Contains("플랜 패널",StringComparison.Ordinal) &&
                (focusButton.ToolTip as string)?.Contains("플랜",StringComparison.Ordinal)==true,
                "combined task-plan toggle uses an accessible vector arrow");
            commandInput.Text = "# 프로젝트 계획\n\n## 목표\n\n사용자가 문서를 쉽게 작성한다.\n\n## 요구사항\n\n- Markdown 한 파일에서 제목으로 구분\n- 작성한 내용을 저장\n\n## 제약 사항\n\n기존 문서와 초안을 보존한다.\n\n## 완료 기준\n\n저장한 계획으로 작업을 실행한다.\n\n## 참고 자료\n\n```md\n## 코드 내부의 제목도 그대로 보존\n```\n";
            Assert(commandInput.IsVisible && Descendants<TextBox>(workbenchHost).Count(input => input.IsVisible && input.AcceptsReturn) == 2,
                "Markdown document and append-request composer are both visible");
            string firstDraft=commandInput.Text;
            await (Task)Call(window,"SaveTabDocumentAsync",false)!;
            Assert(File.ReadAllText(Path.Combine(fixture,"terminal-notes","plan.md"))==firstDraft,"single editor saves headings, source and code fences exactly to the execution document");
            var inputComposer = Field<TextBox>(window,"_projectInputEditor");
            const string additionalRequest = "추가 목표: 원문 보존\n```cs\nvar sample = 1;\n```";
            inputComposer.Text = additionalRequest;
            await (Task)Call(window,"SaveTabDocumentAsync",false)!;
            string savedAppend = File.ReadAllText(Path.Combine(fixture,"terminal-notes","plan.md"));
            Assert(savedAppend.StartsWith(firstDraft) && savedAppend.Contains(additionalRequest) && inputComposer.Text == additionalRequest,
                "shared Save appends plan input verbatim and retains the pending draft");
            Assert(!((TextBlock)window.FindName("StatusLabel")).Text.Contains("OpenCode", StringComparison.OrdinalIgnoreCase),
                "Save completes without attempting an OpenCode-only dispatch on the shell fixture");
            await (Task)Call(window,"SaveTabDocumentAsync",false)!;
            Assert(File.ReadAllText(Path.Combine(fixture,"terminal-notes","plan.md")) == savedAppend,
                "repeating shared Save does not append the pending request twice");
            await (Task)Call(window,"SaveTabDocumentAsync",true)!;
            Assert(File.ReadAllText(Path.Combine(fixture,"terminal-notes","plan.md")) == savedAppend && inputComposer.Text == additionalRequest,
                "shared Execute reuses the saved append and preserves the request on shell rejection");
            await (Task)Call(window,"SubmitProjectInputAsync")!;
            Assert(File.ReadAllText(Path.Combine(fixture,"terminal-notes","plan.md")) == savedAppend,
                "retry after failed send does not append the request twice");
            string appendedSection = savedAppend[firstDraft.Length..].Trim();
            Assert(appendedSection == additionalRequest && !appendedSection.Contains("terminal-notes:plan-input:"),
                "plan additions contain the raw request without generated headings, timestamps, or markers");
            Assert(inputComposer.Text == additionalRequest && commandInput.IsVisible,
                "failed terminal delivery retains the plan request draft");
            inputComposer.Clear();
            firstDraft = savedAppend;
            foreach(int capacity in new[]{1,2,3,4,6,8})
            {
                Call(window,"SetTerminalLayout",capacity);
                Assert(Field<int>(window,"_splitCapacity")==capacity && ((RadioButton)window.FindName("Layout"+capacity)).IsVisible &&
                    tabs.Items.Count==1 && ReferenceEquals(Pane((TabItem)tabs.Items[0]),pane) && pane.IsRunning,
                    $"{capacity}-pane layout reuses the existing single terminal and leaves empty project slots");
                if (capacity == 4)
                {
                    window.UpdateLayout();
                    await Task.Delay(250);
                    CaptureWindow(window,"terminal-four-pane-borders.png");
                }
            }
            Call(window,"SetTerminalLayout",1);
            var planPanel = (FrameworkElement)window.FindName("NotesPanel");
            var taskTerminalArea = Field<Grid>(window,"_taskTerminalArea");
            var planTerminalGrid = (Grid)planPanel.Parent;
            int planColumn = Grid.GetColumn(planPanel);
            int taskTerminalColumn = Grid.GetColumn(taskTerminalArea);
            double PlanShare() => planPanel.ActualWidth / (planPanel.ActualWidth + taskTerminalArea.ActualWidth);
            Assert(Math.Abs(PlanShare() - 0.3) < 0.01,"plan and task terminals start at 3:7");
            var splitter = (GridSplitter)window.FindName("NotesSplitter");
            Assert(splitter.IsEnabled && splitter.ResizeBehavior == System.Windows.Controls.GridResizeBehavior.PreviousAndNext &&
                ReferenceEquals(focusButton.Parent, splitter.Parent) && Grid.GetColumn(focusButton) == Grid.GetColumn(splitter) &&
                focusButton.VerticalAlignment == VerticalAlignment.Center,
                "the plan arrow overlays its centered, resizable splitter rail");
            splitter.RaiseEvent(new System.Windows.Input.KeyEventArgs(System.Windows.Input.Keyboard.PrimaryDevice,
                PresentationSource.FromVisual(splitter)!, 0, System.Windows.Input.Key.Right) { RoutedEvent = System.Windows.Input.Keyboard.KeyDownEvent });
            await Task.Delay(100);
            Assert(PlanShare() > 0.3,"the actual splitter changes the task/plan ratio");
            double adjustedShare = PlanShare();
            Call(window,"SaveSettings");
            using (var widthSettings = JsonDocument.Parse(File.ReadAllText(Path.Combine(fixture,"settings.json"))))
            {
                double savedPlanRatio = widthSettings.RootElement.GetProperty("TaskPlanWidth").GetDouble();
                Assert(Math.Abs(savedPlanRatio / (savedPlanRatio + 1) - adjustedShare) < 0.01,
                    "plan width persistence stores the actual divider ratio relative to the terminal width");
                Call(window,"LoadTaskPlanWidthSetting",(double?)savedPlanRatio);
                window.UpdateLayout();
                Assert(Math.Abs(PlanShare() - adjustedShare) < 0.01,
                    "loading the saved plan width restores the divider ratio");
            }
            focusButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            focusButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await Task.Delay(100);
            Assert(Math.Abs(PlanShare() - adjustedShare) < 0.01,"hiding and restoring the plan preserves the adjusted divider position");
            focusButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Call(window,"SetTaskWorkspaceVisible",false);
            window.UpdateLayout();
            Call(window,"SetTaskWorkspaceVisible",true);
            focusButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await Task.Delay(100);
            Assert(Math.Abs(PlanShare() - adjustedShare) < 0.01 && commandInput.Text == firstDraft && pane.IsRunning,
                "switching workspace modes while the plan is collapsed preserves its divider, draft and terminal");
            planTerminalGrid.ColumnDefinitions[planColumn].Width = new GridLength(3, GridUnitType.Star);
            planTerminalGrid.ColumnDefinitions[taskTerminalColumn].Width = new GridLength(7, GridUnitType.Star);
            Call(window,"SaveSettings");
            using(var savedSettings=JsonDocument.Parse(File.ReadAllText(Path.Combine(fixture,"settings.json"))))
                Assert(savedSettings.RootElement.GetProperty("CodexWorkspace").GetString()==fixture,"project working path persisted");
            Call(window,"AddTerminal");
            Assert(tabs.Items.Count==1 && ReferenceEquals(Pane((TabItem)tabs.Items[0]),pane),"automatic single-mode acquisition reuses the existing project terminal");

            string defaultPlanPath=Path.GetFullPath(Path.Combine(fixture,"terminal-notes","plan.md"));
            Assert(tabs.Items.Cast<TabItem>().All(tab=>string.Equals(Property<string>(tab.Tag,"PlanDocumentPath"),defaultPlanPath,StringComparison.OrdinalIgnoreCase)),
                "same-project terminals use one default plan path");
            var sameProjectTabs=tabs.Items.Cast<TabItem>().ToArray();
            var sameProjectPanes=sameProjectTabs.Select(Pane).ToArray();
            var sameProjectPlanPaths=sameProjectTabs.Select(tab=>Property<string>(tab.Tag,"PlanDocumentPath")).ToArray();
            var sessionButtons=Field<Dictionary<TabItem,Button>>(window,"_sessionButtons");
            var originalSessionButtons=sameProjectTabs.ToDictionary(tab=>tab,tab=>sessionButtons[tab]);
            Call(window,"ShowProjectTaskList");
            var taskRows=Field<System.Collections.IDictionary>(window,"_projectTaskRows");
            object taskRow=taskRows[fixture]!;
            Call(window,"OpenProjectTask",fixture);
            window.UpdateLayout();
            await Task.Delay(100);
            Assert(Property<CheckBox>(taskRow,"Loop").IsVisible,
                "project card directly exposes its continuous improvement checkbox");
            var improvementDeadline = DateTimeOffset.Now.AddMinutes(15);
            Assert(await (Task<bool>)Call(window,"ApplyProjectImprovementOptionsAsync",fixture,true,improvementDeadline)!,
                "single continuous-improvement options save successfully");
            var improvementPreference = SingleProjectContinuousImprovementStore.Read(fixture);
            Assert(improvementPreference is { Enabled: true } && improvementPreference.Until == improvementDeadline &&
                Property<TextBlock>(taskRow,"Deadline").Text != "—", "single deadline persists and updates its schedule state");
            bool singleScheduleOpened = false;
            _ = window.Dispatcher.BeginInvoke(new Action(() =>
            {
                var schedule = Application.Current.Windows.OfType<Window>().Single(item=>item.Title=="Loop 종료 시간");
                singleScheduleOpened = true;
                schedule.Close();
            }));
            Call(window,"ShowImprovementSchedule",fixture);
            Assert(singleScheduleOpened,"single mode opens the deadline editor");
            await (Task<bool>)Call(window,"ApplyProjectImprovementOptionsAsync",fixture,false,null)!;
            Call(window,"ShowProjectTaskList");
            var taskSelect=Property<Button>(taskRow,"Select");
            void Key(UIElement target, Key key, bool preview = false) => target.RaiseEvent(
                new KeyEventArgs(Keyboard.PrimaryDevice, PresentationSource.FromVisual(window), Environment.TickCount, key)
                { RoutedEvent = preview ? Keyboard.PreviewKeyDownEvent : Keyboard.KeyDownEvent });
            taskSelect.Focus();
            Key(taskSelect, System.Windows.Input.Key.F2);
            var projectTitleEditor=Field<TextBox?>(window,"_titleEditor");
            Assert(projectTitleEditor is { IsVisible: true },"F2 on the task name opens a visible inline editor");
            projectTitleEditor!.Text="Shared UI Project";
            Call(window,"UpdateProjectTaskActivity");
            await Task.Delay(900);
            Assert(ReferenceEquals(Field<TextBox?>(window,"_titleEditor"),projectTitleEditor) && projectTitleEditor.Text=="Shared UI Project",
                "task status refresh preserves the title being edited");
            Key(projectTitleEditor, System.Windows.Input.Key.Enter, true);
            Assert(Field<TextBox?>(window,"_titleEditor") is null && Property<TextBlock>(taskRow,"Title").Text=="Shared UI Project",
                "Enter saves the task title and restores its name button");
            Key(taskSelect, System.Windows.Input.Key.F2);
            projectTitleEditor=Field<TextBox>(window,"_titleEditor");
            projectTitleEditor.Text="취소할 제목";
            Key(projectTitleEditor, System.Windows.Input.Key.Escape, true);
            Assert(Property<TextBlock>(taskRow,"Title").Text=="Shared UI Project","Escape cancels task title changes");
            var doubleClick = new MouseButtonEventArgs(Mouse.PrimaryDevice, Environment.TickCount, MouseButton.Left)
                { RoutedEvent = UIElement.PreviewMouseLeftButtonDownEvent };
            typeof(MouseButtonEventArgs).GetProperty(nameof(MouseButtonEventArgs.ClickCount))!.SetValue(doubleClick,2);
            taskSelect.RaiseEvent(doubleClick);
            projectTitleEditor=Field<TextBox?>(window,"_titleEditor");
            Assert(projectTitleEditor is { IsVisible: true },"double-click on the task name opens its editor");
            projectTitleEditor!.Text=" ";
            Key(projectTitleEditor, System.Windows.Input.Key.Enter, true);
            Assert(ReferenceEquals(Field<TextBox?>(window,"_titleEditor"),projectTitleEditor),"blank task title is rejected without closing the editor");
            Key(projectTitleEditor, System.Windows.Input.Key.Escape, true);
            ((MenuItem)taskSelect.ContextMenu.Items[0]).RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
            await Until(()=>Task.FromResult(Field<TextBox?>(window,"_titleEditor") is not null),"task rename context menu");
            projectTitleEditor=Field<TextBox>(window,"_titleEditor");
            projectTitleEditor.Text="Shared UI Project";
            window.Activate();
            projectTitleEditor.Focus();
            await Until(()=>Task.FromResult(projectTitleEditor.IsKeyboardFocusWithin),"task title owns keyboard focus before blur");
            commandInput.Focus();
            await Until(()=>Task.FromResult(Field<TextBox?>(window,"_titleEditor") is null),"leaving task title commits it");
            Assert(sameProjectTabs.Select(Pane).SequenceEqual(sameProjectPanes) &&
                sameProjectTabs.Select(tab=>Property<string>(tab.Tag,"PlanDocumentPath")).SequenceEqual(sameProjectPlanPaths) &&
                sameProjectTabs.All(tab=>ReferenceEquals(sessionButtons[tab],originalSessionButtons[tab])),
                "renaming a project preserves its sessions, plan path, and picker button identities");
            var projectHeading=((StackPanel)originalSessionButtons[sameProjectTabs[0]].Content).Children.OfType<TextBlock>()
                .FirstOrDefault(text=>text.Text.Contains("Shared UI Project",StringComparison.Ordinal));
            Assert(projectHeading is not null && projectHeading.Text.Contains("1\uAC1C \uC138\uC158",StringComparison.Ordinal) &&
                (projectHeading.ToolTip as string)?.Contains(fixture,StringComparison.OrdinalIgnoreCase)==true,
                "session picker groups same-project terminals under a named header with count and path");
            Call(window,"ShowProjectTaskList");
            var multi=Property<RadioButton>(taskRow,"Multi");
            var single=Property<RadioButton>(taskRow,"Single");
            Property<Button>(taskRow,"Select").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Assert(Field<string>(window,"_selectedProjectPath")==fixture && pane.IsRunning,"project-name button selects its plan without replacing the terminal");
            multi.IsChecked=true;
            Assert(!(bool)Call(window,"IsMultiProjectTask",fixture)! && pane.IsRunning,"legacy mode switch cannot enable a multi-role process");
            await (Task)Call(window,"RefreshProjectTasksAsync")!;
            Assert(multi.IsChecked==false && single.IsChecked==true,"list refresh retains single mode");
            single.IsChecked=true;
            Call(window,"AssignProjectToSlot",fixture,0);
            Assert(tabs.Items.Count==1 && ReferenceEquals(Pane((TabItem)tabs.Items[0]),pane),"switching back and reopening reuses the single pane");
            Call(window,"BeginTitleEdit",tabs.Items[0]);
            var titleEditor=Field<TextBox?>(window,"_titleEditor");
            Assert(titleEditor is not null,"single task heading supports inline title editing");
            titleEditor!.Text="지휘자";
            Call(window,"EndTitleEdit",true);
            Assert(((TextBlock)((StackPanel)((TabItem)tabs.Items[0]).Header).Children[0]).Text.Contains("지휘자") && pane.IsRunning,"rename live terminal without restarting process");
            var terminalTaskTab = (TabItem)tabs.Items[0];
            var terminalTaskLabel = Field<Dictionary<TabItem,TextBlock>>(window,"_taskTerminalLabels")[terminalTaskTab];
            var terminalTaskHeading = (ContentControl)((Grid)terminalTaskLabel.Parent).Parent;
            window.UpdateLayout();
            Assert(terminalTaskLabel.IsVisible && terminalTaskLabel.Text=="Shared UI Project" && terminalTaskHeading.ActualHeight<=32,
                "each terminal shows a compact task name independently of its session alias");
            var taskNameDoubleClick = new MouseButtonEventArgs(Mouse.PrimaryDevice,Environment.TickCount,MouseButton.Left)
                { RoutedEvent=Control.MouseDoubleClickEvent };
            terminalTaskHeading.RaiseEvent(taskNameDoubleClick);
            var terminalNameEditor = Field<TextBox?>(window,"_titleEditor");
            Assert(terminalNameEditor is { IsVisible:true } && ReferenceEquals(terminalNameEditor.Parent,terminalTaskLabel.Parent),
                "double-clicking the terminal task name edits it in place");
            terminalNameEditor!.Text=" ";
            Key(terminalNameEditor,System.Windows.Input.Key.Enter,true);
            Assert(ReferenceEquals(Field<TextBox?>(window,"_titleEditor"),terminalNameEditor),
                "terminal task rename rejects an empty name");
            terminalNameEditor.Text="Task renamed from terminal";
            terminalNameEditor.RaiseEvent(new MouseButtonEventArgs(Mouse.PrimaryDevice,Environment.TickCount,MouseButton.Left)
                { RoutedEvent=UIElement.PreviewMouseLeftButtonDownEvent });
            terminalTaskHeading.RaiseEvent(new MouseButtonEventArgs(Mouse.PrimaryDevice,Environment.TickCount,MouseButton.Left)
                { RoutedEvent=Control.MouseDoubleClickEvent });
            Assert(ReferenceEquals(Field<TextBox?>(window,"_titleEditor"),terminalNameEditor) && terminalNameEditor.Text=="Task renamed from terminal",
                "clicking and double-clicking an active task-name editor preserves its draft");
            Call(window,"UpdateDispatcher");
            Call(window,"UpdateProjectTaskActivity");
            Assert(ReferenceEquals(Field<TextBox?>(window,"_titleEditor"),terminalNameEditor) && terminalNameEditor.Text=="Task renamed from terminal",
                "terminal activity refresh preserves the task-name editing draft");
            Key(terminalNameEditor,System.Windows.Input.Key.Enter,true);
            Assert(Field<TextBox?>(window,"_titleEditor") is null && terminalTaskLabel.Text=="Task renamed from terminal" &&
                Property<TextBlock>(taskRow,"Title").Text=="Task renamed from terminal" && Property<string>(terminalTaskTab.Tag,"Title")=="지휘자" && pane.IsRunning,
                "terminal task rename updates the sidebar while preserving the session alias and live process");
            using (var taskNameSettings=JsonDocument.Parse(File.ReadAllText(Path.Combine(fixture,"settings.json"))))
                Assert(taskNameSettings.RootElement.GetProperty("ProjectNames").EnumerateArray().Any(binding=>
                    binding.GetProperty("Project").GetString()==fixture && binding.GetProperty("Name").GetString()=="Task renamed from terminal"),
                    "terminal task rename persists through the existing project-name settings");
            Key(terminalTaskHeading,System.Windows.Input.Key.F2);
            terminalNameEditor=Field<TextBox>(window,"_titleEditor");
            terminalNameEditor.Text="취소할 터미널 태스크명";
            Key(terminalNameEditor,System.Windows.Input.Key.Escape,true);
            Assert(terminalTaskLabel.Text=="Task renamed from terminal" && Property<TextBlock>(taskRow,"Title").Text==terminalTaskLabel.Text,
                "F2 opens terminal task renaming and Escape preserves its previous name");
            Key(terminalTaskHeading,System.Windows.Input.Key.F2);
            terminalNameEditor=Field<TextBox>(window,"_titleEditor");
            terminalNameEditor.Text="Shared UI Project";
            window.Activate();
            terminalNameEditor.Focus();
            await Until(()=>Task.FromResult(terminalNameEditor.IsKeyboardFocusWithin),"terminal task-name editor focus");
            commandInput.Focus();
            await Until(()=>Task.FromResult(Field<TextBox?>(window,"_titleEditor") is null),"leaving terminal task name commits it");
            Assert(terminalTaskLabel.Text=="Shared UI Project" && pane.IsRunning && Property<string>(terminalTaskTab.Tag,"PlanDocumentPath")==defaultPlanPath,
                "terminal task-name edits retain the project plan binding");
            Assert((await Screen()).Contains("UI_한글_OK"),"terminal output survives tab switching");
            Assert(commandInput.Text==firstDraft,
                "project plan draft survives terminal selection and automatic layout changes");
            Assert(tabs.Items.Cast<TabItem>().All(tab=>string.Equals(Property<string>(tab.Tag,"PlanDocumentPath"),defaultPlanPath,StringComparison.OrdinalIgnoreCase)),
                "all open sessions stay bound to the project's fixed plan.md");
            focusButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Assert(conversationPanel.Visibility==Visibility.Collapsed && Field<bool>(window,"_taskSidebarCollapsed"),
                "the combined toggle hides both task and plan panels");
            focusButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Assert(conversationPanel.Visibility==Visibility.Visible && workbenchHost.IsVisible && !Field<bool>(window,"_taskSidebarCollapsed"),
                "the combined toggle restores both task and plan panels");
            window.Width=1024;window.Height=700;await Task.Delay(500);
            Assert((await browser.ExecuteScriptAsync("terminal.cols"))!="0","terminal resize");
            Call(window,"ShowProjectTaskList");
            window.Width=820;window.Height=480;await Task.Delay(500);
            var surface=Property<Border>(taskRow,"Surface");
            var loopCheck=Property<CheckBox>(taskRow,"Loop");
            Assert(Math.Abs(surface.ActualHeight - 40) < 1 && loopCheck.IsVisible && loopCheck.Content is null && !Property<Button>(taskRow,"Record").IsVisible,
                "single-row task card retains essential actions at minimum window size");
            double planTerminalWidth = planPanel.ActualWidth + taskTerminalArea.ActualWidth;
            double expectedPlanShare = Math.Max(240, planTerminalWidth * 0.3) / planTerminalWidth;
            Assert(commandInput.ExtentHeight>commandInput.ViewportHeight && planPanel.ActualWidth>=240 && Math.Abs(PlanShare()-expectedPlanShare)<0.01,"Markdown editor stays scrollable and preserves the plan minimum and 3:7 preference at minimum size");
            CaptureWindow(window,"task-list-minimum.png");
            var taskButtons = new FrameworkElement[]
            {
                Property<Button>(taskRow,"Start"),
                Property<Button>(taskRow,"Folder"), loopCheck
            };
            var singlePositions = taskButtons.Select(button => button.TranslatePoint(new Point(), surface)).ToArray();
            multi.IsChecked = true;
            await Task.Delay(100);
            Assert(!Property<FrameworkElement>(taskRow,"RolePanel").IsVisible && !multi.IsVisible && !single.IsVisible && Math.Abs(surface.ActualHeight - 40) < 1 &&
                taskButtons.Select(button => button.TranslatePoint(new Point(), surface)).SequenceEqual(singlePositions),
                "legacy multi selection exposes no role team and preserves single-row action positions");
            var taskTitle = Property<TextBlock>(taskRow,"Title");
            Assert(taskTitle.ActualWidth >= 48 && taskButtons.All(button =>
                Math.Abs(button.TranslatePoint(new Point(button.ActualWidth/2, button.ActualHeight/2), surface).Y - taskTitle.TranslatePoint(new Point(0, taskTitle.ActualHeight/2), surface).Y) <= 1),
                "single-row card keeps title, start, folder, and improvement toggle on the same row");
            var taskScroll = Field<ScrollViewer>(window,"_taskListScroll");
            taskScroll.ScrollToRightEnd();
            await Task.Delay(100);
            Assert(taskScroll.ScrollableWidth == 0 && taskButtons.All(button => button.IsVisible && button.TranslatePoint(new Point(button.ActualWidth,0),taskScroll).X <= taskScroll.ViewportWidth + 1),
                "sidebar actions remain reachable without horizontal scrolling at minimum width");
            var primary = (System.Windows.Media.SolidColorBrush)Property<Button>(taskRow,"Start").Background;
            Assert(primary.Color.B > primary.Color.G && primary.Color.G > primary.Color.R,
                "primary buttons use blue instead of completion purple");
            CaptureWindow(window,"task-multi-minimum.png");
            window.Width=1480;window.Height=920;await Task.Delay(500);
            Assert(taskScroll.ScrollableWidth==0,"the full task row fits without horizontal scrolling at default width");
            CaptureWindow(window,"task-markdown-design.png");
            single.IsChecked = true;
            Call(window,"OpenProjectTask",fixture);
            // Capture our own WPF visual; HWND WebView2 surfaces may be blank in this image.
            var bitmap=new System.Windows.Media.Imaging.RenderTargetBitmap((int)window.ActualWidth,(int)window.ActualHeight,96,96,System.Windows.Media.PixelFormats.Pbgra32);
            bitmap.Render(window);
            var encoder=new System.Windows.Media.Imaging.PngBitmapEncoder();encoder.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(bitmap));
            var output=Path.Combine(AppContext.BaseDirectory,"ui-smoke.png");using(var stream=File.Create(output)) encoder.Save(stream);
            Console.WriteLine("Screenshot: "+output);
            using(var capture=File.Create(Path.Combine(AppContext.BaseDirectory,"terminal-smoke.png"))) await browser.CoreWebView2.CapturePreviewAsync(Microsoft.Web.WebView2.Core.CoreWebView2CapturePreviewImageFormat.Png,capture);
            var pickerTabsBefore=tabs.Items.Cast<TabItem>().ToArray();
            Field<Button>(window,"SessionPickerButton").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await Task.Delay(150);
            var sessionPopup=Field<Popup>(window,"SessionPickerPopup");
            var pickerRows=Field<Panel>(window,"DispatcherSessions").Children.OfType<Button>().ToArray();
            var pickerScroll=Descendants<ScrollViewer>(sessionPopup.Child).Single();
            Assert(sessionPopup.IsOpen && pickerRows.Length>1 &&
                pickerRows.All(button=>button.IsVisible && button.ActualHeight>0 &&
                    Math.Abs(button.TransformToAncestor(pickerScroll).Transform(new Point()).X)<1),
                "the real terminal popup renders multiple projects in one visible column");
            pickerScroll.ScrollToEnd();
            await Task.Delay(100);
            double lastPickerY=pickerRows[^1].TransformToAncestor(pickerScroll).Transform(new Point()).Y;
            Assert(lastPickerY>=-1 && lastPickerY+pickerRows[^1].ActualHeight<=pickerScroll.ViewportHeight+1 &&
                tabs.Items.Cast<TabItem>().SequenceEqual(pickerTabsBefore),
                "the last project is reachable by scrolling without opening or replacing terminals");
            var popupVisual=(FrameworkElement)sessionPopup.Child;
            var popupBitmap=new System.Windows.Media.Imaging.RenderTargetBitmap((int)Math.Ceiling(popupVisual.ActualWidth),
                (int)Math.Ceiling(popupVisual.ActualHeight),96,96,System.Windows.Media.PixelFormats.Pbgra32);
            popupBitmap.Render(popupVisual);
            var popupEncoder=new System.Windows.Media.Imaging.PngBitmapEncoder();
            popupEncoder.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(popupBitmap));
            using(var popupCapture=File.Create(Path.Combine(AppContext.BaseDirectory,"project-picker-smoke.png"))) popupEncoder.Save(popupCapture);
            sessionPopup.IsOpen=false;
            var restored=new MainWindow(Path.Combine(fixture,"settings.json"),autoStartCodex:false);
            try
            {
                restored.Show();
                await Until(()=>Task.FromResult(Field<string>(restored,"_workspace")==fixture && ((TabControl)restored.FindName("TerminalTabs")).Items.Count==0),"blank fresh launch keeps saved project closed");
                Assert(Field<string?>(restored,"_selectedProjectPath") is null &&
                    Field<FrameworkElement?>(restored,"_projectStartSurface")?.Visibility==Visibility.Visible,
                    "fresh restart shows project choices without selecting the remembered project");
                var restoredPlanEditor=Field<TextBox?>(restored,"_tabDocumentEditor");
                Assert(restoredPlanEditor is not null && restoredPlanEditor.Text.Length==0 && !restoredPlanEditor.IsEnabled &&
                    Field<FrameworkElement?>(restored,"_tabDocumentSurface")?.Visibility==Visibility.Collapsed,
                    "fresh restart leaves the visible plan editor empty and unloaded");
                Assert(Field<CheckBox>(restored,"_autoResumeToggle").IsChecked == false && !Field<bool>(restored,"_autoResumeEnabled"),
                    "automatic progress preference persists across restart independently of Auto Mode");
                var restoredNames=((System.Collections.IEnumerable)Field<object>(restored,"_projectNames")).Cast<object>();
                Assert(restoredNames.Any(name=>Property<string>(name,"Project")==fixture && Property<string>(name,"Name")=="Shared UI Project"),
                    "project display name and grouping path remain in settings without creating a session");
            }
            finally {CloseTestWindow(restored);}
            const string trayDraft="트레이 왕복 중 보존할 MD 초안";
            commandInput.Text=trayDraft;
            await browser.ExecuteScriptAsync("send({type:'input',data:\"Start-Sleep -Milliseconds 400; Write-Output ('TRAY_'+'OK')\\r\"})");
            Assert(Field<object>(window,"_tray") is not null,"notification icon created before hide");
            window.Activate();
            await window.Dispatcher.InvokeAsync(()=>{}, System.Windows.Threading.DispatcherPriority.ApplicationIdle);
            window.Closing += (_,e) => Console.WriteLine($"Tray close event: cancel={e.Cancel}");
            window.IsVisibleChanged += (_,e) => Console.WriteLine($"Tray visibility event: {e.OldValue}->{e.NewValue}");
            HwndSource.FromHwnd(new WindowInteropHelper(window).Handle)!.AddHook((IntPtr hwnd,int msg,IntPtr wParam,IntPtr lParam,ref bool handled)=>
            {
                if(msg is 0x112 or 0x10) Console.WriteLine($"Tray native message: {msg:X}, wParam={wParam:X}, handled={handled}");
                return IntPtr.Zero;
            });
            ((Button)window.FindName("CloseWindowButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            try { await Until(()=>Task.FromResult(!window.IsVisible),"caption close hides window to tray"); }
            catch
            {
                Console.WriteLine($"Tray failure: visible={window.IsVisible}, active={window.IsActive}, closing={Field<bool>(window,"_closing")}, pending={Field<bool>(window,"_closePending")}, hwnd={new WindowInteropHelper(window).Handle}");
                CaptureWindow(window,"tray-failure.png");
                throw;
            }
            Assert(!window.IsVisible && pane.IsRunning && tabs.Items.Count==1,"window close hides to tray without ending sessions");
            Assert(Field<object>(window,"_tray") is not null,"notification icon created");
            await Task.Delay(900);
            Call(window,"RestoreFromTray");
            await Until(async ()=>(await Screen()).Contains("TRAY_OK"),"command continues while hidden");
            Assert(window.IsVisible && tabs.Items.Count==1 && ReferenceEquals(pane,Pane((TabItem)tabs.Items[0])),"restore reuses existing terminal sessions");
            Assert(commandInput.Text==trayDraft,
                "unsent project plan draft survives tray roundtrip in memory");
            await browser.ExecuteScriptAsync("document.dispatchEvent(new MouseEvent('contextmenu',{bubbles:true,cancelable:true}))");
            var menu=pane.ContextMenu;
            await Until(()=>Task.FromResult(menu.IsOpen),"terminal browser right-click opens native menu");
            var terminalMenuLabels=menu.Items.OfType<MenuItem>().Select(item=>item.Header as string).ToArray();
            Assert(new[]{"복사","붙여넣기","전체 선택","선택 해제","화면 지우기"}.All(terminalMenuLabels.Contains) &&
                !terminalMenuLabels.Contains("터미널 모두 닫기"),"terminal context menu exposes terminal actions without app session management");
            Assert(((TabItem)tabs.Items[0]).ContextMenu.Items.OfType<MenuItem>().Any(item=>item.Header as string=="터미널 모두 닫기"),
                "terminal header keeps session management accessible");
            menu.IsOpen=false;
            var allPanes=new[]{pane};
            Call(window,"ShowCompactDashboard");
            var dashboard=Field<Window>(window,"_compactDashboard");
            Assert(!window.IsVisible && dashboard.IsVisible && pane.IsRunning,"status monitor preserves the task session");
            Assert(dashboard.Topmost,"status monitor stays above other windows");
            var sessionButton=Descendants<Button>(dashboard).First(button=>(button.ToolTip as string)?.Contains("대화형 터미널")==true);
            var modalChecked=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            _=dashboard.Dispatcher.BeginInvoke(new Action(() =>
            {
                Window? modal=null;
                try
                {
                    modal=Application.Current.Windows.Cast<Window>().Single(w=>w!=window && w!=dashboard && w.IsVisible);
                    Assert(ReferenceEquals(Window.GetWindow(pane),modal) && pane.IsRunning,"monitor opens the existing task pane in its modal");
                    modal.Close(); modalChecked.SetResult();
                }
                catch(Exception ex) {modal?.Close();modalChecked.SetException(ex);}
            }),System.Windows.Threading.DispatcherPriority.ApplicationIdle);
            sessionButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await modalChecked.Task;
            Assert(ReferenceEquals(Window.GetWindow(pane),window) && pane.IsRunning,"closing monitor modal restores task host identity");
            dashboard.Close();
            await Until(()=>Task.FromResult(window.IsVisible),"status monitor restores main window");
            Call(window,"CloseAllTerminals",false);
            Assert(tabs.Items.Count==0 && !pane.IsRunning,"close all ends task terminal sessions");
            Assert(window.IsVisible && conversationPanel.IsVisible,"closing sessions preserves the plan workspace");
            var slotOneFolder=Path.Combine(fixture,"agent one");
            var slotTwoFolder=Path.Combine(fixture,"agent two");
            Directory.CreateDirectory(slotOneFolder); Directory.CreateDirectory(slotTwoFolder);
            Call(window,"AddTerminal");
            await Until(()=>Task.FromResult(tabs.Items.Count==1 && Pane((TabItem)tabs.Items[0]).IsRunning),"first shared-workspace terminal");
            var existingWorkspaceTab=(TabItem)tabs.Items[0];
            var existingWorkspacePane=Pane(existingWorkspaceTab);
            Assert(existingWorkspacePane.WorkingDirectory==fixture,"existing terminal keeps its project path before global settings change");
            Call(window,"RememberProjectName",fixture,"Shared Workspace Name");

            TerminalProfilesDialog? workspaceSettingsDialog=null;
            _=window.Dispatcher.BeginInvoke(new Action(() =>
            {
                workspaceSettingsDialog=Application.Current.Windows.OfType<TerminalProfilesDialog>().Single();
                Assert(workspaceSettingsDialog.FindName("DefaultWorkspaceInput") is TextBox &&
                    workspaceSettingsDialog.FindName("WorkspaceBrowseButton") is Button &&
                    workspaceSettingsDialog.FindName("GlobalAutoModeToggle") is CheckBox &&
                    workspaceSettingsDialog.FindName("CancelButton") is Button &&
                    workspaceSettingsDialog.FindName("SaveButton") is Button &&
                    typeof(TerminalProfilesDialog).GetProperty("Rows") is null,
                    "settings expose one workspace and one global Auto Mode control");
                ((TextBox)workspaceSettingsDialog.FindName("DefaultWorkspaceInput")).Text=slotOneFolder;
                ((CheckBox)workspaceSettingsDialog.FindName("GlobalAutoModeToggle")).IsChecked=true;
                ((Button)workspaceSettingsDialog.FindName("SaveButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            }),System.Windows.Threading.DispatcherPriority.ApplicationIdle);
            settingsButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Assert(workspaceSettingsDialog is not null && workspaceSettingsDialog.DefaultWorkspace==Path.GetFullPath(slotOneFolder) &&
                workspaceSettingsDialog.UseAutoMode && Field<string>(window,"_workspace")==Path.GetFullPath(slotOneFolder) &&
                Field<bool>(window,"_useAutoMode"),
                "shared workspace and global Auto Mode settings save together");
            Assert(tabs.Items.Count==1 && ReferenceEquals(Pane((TabItem)tabs.Items[0]),existingWorkspacePane) &&
                existingWorkspacePane.IsRunning && existingWorkspacePane.WorkingDirectory==fixture,
                "saving global settings leaves existing terminal sessions untouched");

            Call(window,"RememberProjectName",slotOneFolder,"Shared Workspace Name");
            Call(window,"AddTerminal");
            await Until(()=>Task.FromResult(tabs.Items.Count==2 && Pane((TabItem)tabs.Items[1]).IsRunning),"new terminal uses shared workspace");
            Call(window,"AddTerminal");
            Assert(tabs.Items.Count==2 && Pane((TabItem)tabs.Items[1]).WorkingDirectory==Path.GetFullPath(slotOneFolder),
                "new terminal action reuses the configured project's single session");
            var configuredTabs=tabs.Items.Cast<TabItem>().ToArray();
            var configuredPanes=configuredTabs.Select(Pane).ToArray();
            Call(window,"SetTerminalLayout",2);
            Assert((bool)Call(window,"AssignProjectToSlot",fixture,0)! &&
                (bool)Call(window,"AssignProjectToSlot",slotOneFolder,1)!,
                "both existing projects can be assigned to the visible split slots");
            window.UpdateLayout();
            var projectViews = Field<Dictionary<string,Grid>>(window,"_projectTaskViews");
            Assert(projectViews[fixture].IsVisible && projectViews[Path.GetFullPath(slotOneFolder)].IsVisible &&
                configuredTabs.Select(Pane).SequenceEqual(configuredPanes), "two different projects share the split without recreating their single sessions");
            var splitTaskLabels = Field<Dictionary<TabItem,TextBlock>>(window,"_taskTerminalLabels");
            Call(window,"BeginTerminalTaskNameEdit",configuredTabs[0]);
            var splitTaskEditor = Field<TextBox>(window,"_titleEditor");
            splitTaskEditor.Text="First split task";
            Key(splitTaskEditor,System.Windows.Input.Key.Enter,true);
            Assert(splitTaskLabels[configuredTabs[0]].Text=="First split task" && splitTaskLabels[configuredTabs[1]].Text=="Shared Workspace Name" &&
                configuredTabs.Select(Pane).SequenceEqual(configuredPanes),
                "renaming one split terminal task leaves the other project and both sessions intact");
            Call(window,"RememberProjectName",fixture,"Shared Workspace Name");
            await Task.Delay(250);
            CaptureWindow(window,"terminal-split-borders.png");
            Call(window,"SetTerminalLayout",1);
            var configuredPlanPaths=configuredTabs.Select(tab=>Property<string>(tab.Tag,"PlanDocumentPath")).ToArray();
            var configuredButtons=Field<Dictionary<TabItem,Button>>(window,"_sessionButtons");
            var firstProjectHeading=((StackPanel)configuredButtons[configuredTabs[0]].Content).Children.OfType<TextBlock>()
                .First(text=>text.Text.Contains("Shared Workspace Name",StringComparison.Ordinal));
            var secondProjectHeading=((StackPanel)configuredButtons[configuredTabs[1]].Content).Children.OfType<TextBlock>()
                .First(text=>text.Text.Contains("Shared Workspace Name",StringComparison.Ordinal));
            Assert(firstProjectHeading.Text.Contains("1\uAC1C \uC138\uC158",StringComparison.Ordinal) &&
                secondProjectHeading.Text.Contains("1\uAC1C \uC138\uC158",StringComparison.Ordinal) &&
                (firstProjectHeading.ToolTip as string)?.Contains(fixture,StringComparison.OrdinalIgnoreCase)==true &&
                (secondProjectHeading.ToolTip as string)?.Contains(slotOneFolder,StringComparison.OrdinalIgnoreCase)==true,
                "same display names remain separate groups with path-specific session counts");
            Assert(configuredTabs.Select(Pane).SequenceEqual(configuredPanes) &&
                configuredTabs.Select(tab=>Property<string>(tab.Tag,"PlanDocumentPath")).SequenceEqual(configuredPlanPaths),
                "project-name grouping preserves every pane and its plan identity");
            var profileRestored=new MainWindow(Path.Combine(fixture,"settings.json"),autoStartCodex:false);
            try
            {
                profileRestored.Show();
                await Until(()=>Task.FromResult(Field<string>(profileRestored,"_workspace")==Path.GetFullPath(slotOneFolder) &&
                    ((TabControl)profileRestored.FindName("TerminalTabs")).Items.Count==0),"saved global settings restore to a blank start screen");
                var restoredTabs=(TabControl)profileRestored.FindName("TerminalTabs");
                Assert(restoredTabs.Items.Count==0 && Field<string?>(profileRestored,"_selectedProjectPath") is null &&
                    Field<bool>(profileRestored,"_useAutoMode"),
                    "workspace and global Auto Mode persist without restoring a terminal or selected project");
                var restoredNames=((System.Collections.IEnumerable)Field<object>(profileRestored,"_projectNames")).Cast<object>();
                Assert(restoredNames.Any(name=>Property<string>(name,"Project")==Path.GetFullPath(slotOneFolder) &&
                    Property<string>(name,"Name")=="Shared Workspace Name"),
                    "project name and grouping path persist in settings without creating a session");
            }
            finally {CloseTestWindow(profileRestored);}
            var beforeSettings=tabs.Items.Cast<TabItem>().ToArray();
            var pathsBeforeSettings=beforeSettings.Select(tab=>Property<string>(tab.Tag,"PlanDocumentPath")).ToArray();
            tabs.SelectedItem=beforeSettings[0];
            Call(window,"UpdateWorkbenchTarget");
            await Until(()=>Task.FromResult(commandInput.IsEnabled),"plan editor before settings");
            const string settingsDraft="설정 저장 뒤에도 남을 플랜 초안";
            commandInput.Text=settingsDraft;
            TerminalProfilesDialog? updatedSettingsDialog=null;
            _=window.Dispatcher.BeginInvoke(new Action(() =>
            {
                updatedSettingsDialog=Application.Current.Windows.OfType<TerminalProfilesDialog>().Single();
                ((TextBox)updatedSettingsDialog.FindName("DefaultWorkspaceInput")).Text=slotTwoFolder;
                ((CheckBox)updatedSettingsDialog.FindName("GlobalAutoModeToggle")).IsChecked=false;
                ((Button)updatedSettingsDialog.FindName("SaveButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            }),System.Windows.Threading.DispatcherPriority.ApplicationIdle);
            Call(window,"TerminalProfiles_Click",null,new RoutedEventArgs());
            Assert(updatedSettingsDialog is not null && updatedSettingsDialog.DefaultWorkspace==Path.GetFullPath(slotTwoFolder) &&
                !updatedSettingsDialog.UseAutoMode && Field<string>(window,"_workspace")==Path.GetFullPath(slotTwoFolder) && !Field<bool>(window,"_useAutoMode"),
                "global settings update the one workspace and Auto Mode preference");
            Assert(beforeSettings.SequenceEqual(tabs.Items.Cast<TabItem>()) && beforeSettings.All(tab=>Pane(tab).IsRunning) &&
                beforeSettings.Select(tab=>Property<string>(tab.Tag,"PlanDocumentPath")).SequenceEqual(pathsBeforeSettings),
                "global settings preserve all running terminals and project plan bindings");
            Assert(commandInput.Text==settingsDraft,"global settings preserve the unsaved project plan draft");
            Pane(beforeSettings[1]).Dispose();
            Call(window,"CloseTerminal",beforeSettings[1],Pane(beforeSettings[1]));
            Assert(tabs.Items.Count==1 && !tabs.Items.Contains(beforeSettings[1]) && Pane(beforeSettings[0]).IsRunning,
                "closing one project session preserves the other project");
            Assert((bool)Call(window,"PrepareForSystemShutdown")! && Field<object?>(window,"_tray") is not null && Pane((TabItem)tabs.Items[0]).IsRunning,"shutdown query preserves sessions until actual exit");
            Call(window,"DisposeResources");
            Assert(Field<object?>(window,"_tray") is null && !Pane((TabItem)tabs.Items[0]).IsRunning,"actual exit disposes tray and sessions");
            Console.WriteLine("UI SMOKE PASSED");
        }
        catch(Exception ex) {_fail=1;Console.WriteLine("FAIL "+ex);}
        finally
        {
            CloseTestWindow(window);app.Shutdown();
            // Fixture retained for inspection; no user files are modified.
            Console.WriteLine("Fixture: "+fixture);
        }
    }
    private static void CloseTestWindow(MainWindow window)
    {
        Call(window,"DisposeResources");
        window.Close();
    }
    private static void CaptureWindow(Window window,string filename)
    {
        var handle=new System.Windows.Interop.WindowInteropHelper(window).Handle;
        GetWindowRect(handle,out var rect);
        using var bitmap=new System.Drawing.Bitmap(rect.Right-rect.Left,rect.Bottom-rect.Top);
        using(var graphics=System.Drawing.Graphics.FromImage(bitmap))
        {
            var dc=graphics.GetHdc();
            try { if(!PrintWindow(handle,dc,2)) return; }
            finally { graphics.ReleaseHdc(dc); }
        }
        bitmap.Save(Path.Combine(AppContext.BaseDirectory,filename),System.Drawing.Imaging.ImageFormat.Png);
    }

    private static void RunTaskNotificationOptionsSmoke(string fixture)
    {
        var settingsPath=Path.Combine(fixture,"settings.json");
        File.WriteAllText(settingsPath,JsonSerializer.Serialize(new { Workspace=fixture, Document=(string?)null }));
        var window=new MainWindow(settingsPath,autoStartCodex:false);
        try
        {
            var legacy=Call(window,"ReadSettings")!;
            Assert(Property<bool>(legacy,"TaskNotificationsEnabled"),"legacy settings default task notifications on");
            Assert(Property<DateTimeOffset?>(legacy,"TaskNotificationsSnoozedUntilUtc") is null,"legacy settings default with no snooze");

            Call(window,"InitializeTaskNotificationOptions",true,null);
            var menu=new ContextMenu();
            Call(window,"AddTaskNotificationOptions",menu);
            var root=menu.Items.OfType<MenuItem>().Single();
            Assert((string)root.Header=="작업 알림","notification settings appear in the terminal context menu");
            var items=root.Items.OfType<MenuItem>().ToArray();
            Assert(items.Any(item=>(string)item.Header=="알림 사용"),"menu exposes enable toggle");
            Assert(items.Any(item=>(string)item.Header=="영구 끄기"),"menu exposes permanent opt-out");
            Assert(new[]{5,10,30,60}.All(minutes=>items.Any(item=>(string)item.Header==$"{minutes}분 동안 알림 미루기")),"menu exposes all snooze presets");
            Assert(items.Any(item=>(string)item.Header=="사용자 지정 시간…"),"menu exposes custom snooze");

            Call(window,"SnoozeTaskNotifications",5);
            var prefs=Field<object>(window,"_taskNotificationPreferences");
            var snoozedUntil=Property<DateTimeOffset?>(prefs,"SnoozedUntilUtc");
            Assert(Property<bool>(prefs,"Enabled") && snoozedUntil is { } until && until>DateTimeOffset.UtcNow.AddMinutes(4),"preset persists an enabled snooze deadline");
            var snoozed=Call(window,"ReadSettings")!;
            Assert(Property<DateTimeOffset?>(snoozed,"TaskNotificationsSnoozedUntilUtc") is not null,"snooze deadline is stored in app settings");
            Assert(((string)root.Header).Contains("분 중지"),"context menu header shows remaining snooze time");
            Call(window,"SetTaskNotificationsEnabled",true);
            prefs=Field<object>(window,"_taskNotificationPreferences");
            Assert(Property<bool>(prefs,"Enabled") && Property<DateTimeOffset?>(prefs,"SnoozedUntilUtc") is null && (string)root.Header=="작업 알림","reenabling clears snooze and refreshes menu status");

            var notices=new List<TaskNotificationNotice>();
            typeof(MainWindow).GetProperty("TaskNotificationSinkOverride",BindingFlags.Instance|BindingFlags.NonPublic)!.SetValue(window,(Action<TaskNotificationNotice>)(notice=>notices.Add(notice)));
            var failedProject=Path.Combine(fixture,"failed-project"); Directory.CreateDirectory(failedProject);
            Call(window,"BeginTaskNotificationAttempt",failedProject,false);
            var failedRuntime=Call(window,"RuntimeFor",failedProject)!;
            SetField(failedRuntime,"SingleError","startup failed");
            Call(window,"ObserveTaskNotifications");
            Thread.Sleep(2100);
            Call(window,"ObserveTaskNotifications");
            Assert(notices.Count==1 && notices[0].Kind==TaskNotificationKind.Failed,"current pipeline startup failure reaches injected notice sink once");
            Call(window,"ObserveTaskNotifications");
            Assert(notices.Count==1,"pipeline failure notice is deduplicated");

            var retriedProject=Path.Combine(fixture,"retried-project"); Directory.CreateDirectory(retriedProject);
            var retriedRuntime=Call(window,"RuntimeFor",retriedProject)!;
            SetField(retriedRuntime,"SingleStopped",true);
            Call(window,"BeginTaskNotificationAttempt",retriedProject,false);
            SetField(retriedRuntime,"SingleError","retry startup failed");
            Call(window,"ObserveTaskNotifications");
            Thread.Sleep(2100);
            Call(window,"ObserveTaskNotifications");
            Assert(notices.Count==2 && notices[1].Kind==TaskNotificationKind.Failed,"retry startup failure is not masked by the previous stopped flag");

            var stoppedProject=Path.Combine(fixture,"stopped-project"); Directory.CreateDirectory(stoppedProject);
            Call(window,"BeginTaskNotificationAttempt",stoppedProject,false);
            var stoppedRuntime=Call(window,"RuntimeFor",stoppedProject)!;
            SetField(stoppedRuntime,"SingleError","failure before stop");
            Call(window,"ObserveTaskNotifications");
            SetField(stoppedRuntime,"SingleStopped",true);
            Thread.Sleep(2100);
            Call(window,"ObserveTaskNotifications");
            Assert(notices.Count==2,"stopped task failure does not notify");

            var unarmedProject=Path.Combine(fixture,"unarmed-project"); Directory.CreateDirectory(unarmedProject);
            var unarmedRuntime=Call(window,"RuntimeFor",unarmedProject)!;
            SetField(unarmedRuntime,"SingleError","historical failure");
            Call(window,"ObserveTaskNotifications");
            Assert(notices.Count==2,"unarmed historical failure does not notify");
            Call(window,"SetTaskNotificationsEnabled",false);
            prefs=Field<object>(window,"_taskNotificationPreferences");
            Assert(!Property<bool>(prefs,"Enabled") && Property<DateTimeOffset?>(prefs,"SnoozedUntilUtc") is null,"permanent off clears snooze");
            var saved=JsonSerializer.Deserialize<JsonElement>(File.ReadAllText(settingsPath));
            Assert(!saved.GetProperty("TaskNotificationsEnabled").GetBoolean(),"notification opt-out is persisted");

            var restored=new MainWindow(settingsPath,autoStartCodex:false);
            try
            {
                var roundTrip=Call(restored,"ReadSettings")!;
                Assert(!Property<bool>(roundTrip,"TaskNotificationsEnabled"),"saved opt-out survives settings reload");
                Assert(Property<DateTimeOffset?>(roundTrip,"TaskNotificationsSnoozedUntilUtc") is null,"cleared snooze survives settings reload");
            }
            finally { restored.Close(); }
        }
        finally { window.Close(); }
    }

    private static void SetField(object instance,string name,object? value)
    {
        const BindingFlags flags=BindingFlags.Instance|BindingFlags.Public|BindingFlags.NonPublic;
        var type=instance.GetType();
        var property=type.GetProperty(name,flags);
        if(property is not null && property.SetMethod is not null) { property.SetValue(instance,value); return; }
        var field=type.GetField(name,flags);
        if(field is not null) { field.SetValue(instance,value); return; }
        throw new MissingMemberException(type.FullName,name+" (expected a settable property or field)");
    }

    private static async Task RunRegisteredProjectPickerSmoke(string fixture)
    {
        string workspace=Path.Combine(fixture,"registered-picker");
        string bound=Path.Combine(workspace,"saved-bound");
        string named=Path.Combine(workspace,"saved-named");
        string childNamed=Path.Combine(workspace,"opencode");
        string external=Path.Combine(fixture,"saved-external");
        string archived=Path.Combine(workspace,ProjectArchiveService.FolderName,"saved-archived");
        foreach(string project in new[]{bound,named,childNamed,external,archived}) Directory.CreateDirectory(project);
        string settings=Path.Combine(workspace,"settings.json");
        File.WriteAllText(settings,JsonSerializer.Serialize(new
        {
            Workspace=workspace,
            DocumentBindings=new[]
            {
                new { Project=bound, Path=Path.Combine(bound,"terminal-notes","plan.md") },
                new { Project=archived, Path=Path.Combine(archived,"terminal-notes","plan.md") },
                new { Project=workspace, Path=Path.Combine(workspace,"terminal-notes","plan.md") }
            },
            ProjectNames=new[] {
                new { Project=named, Name="Saved named project" },
                new { Project=workspace, Name="opencode" },
                new { Project=childNamed, Name="opencode" },
                new { Project=external, Name="External saved project" }
            }
        }));
        var window=new MainWindow(settings,autoStartCodex:false,initialWorkspace:workspace);
        window.ShowActivated=false;
        window.ShowInTaskbar=false;
        try
        {
            _=new WindowInteropHelper(window).EnsureHandle();
            Call(window,"Window_Loaded",window,new RoutedEventArgs());
            SetField(window,"_taskScanGeneration",Field<int>(window,"_taskScanGeneration")+1);
            var rows=(System.Collections.IDictionary)Field<object>(window,"_projectTaskRows");
            foreach(string project in new[]{bound,named,childNamed,external,archived}) rows.Remove(Path.GetFullPath(project));
            var terminals=(TabControl)window.FindName("TerminalTabs");
            var existingTerminals=terminals.Items.Cast<TabItem>().ToArray();

            Call(window,"SessionPicker_Click",Field<Button>(window,"SessionPickerButton"),new RoutedEventArgs(Button.ClickEvent));
            var unopened=Field<Dictionary<string,Button>>(window,"_unopenedProjectButtons");
            Assert(new[]{bound,named,childNamed,external}.All(project=>unopened.ContainsKey(Path.GetFullPath(project))) &&
                !unopened.ContainsKey(Path.GetFullPath(workspace)) && !unopened.ContainsKey(Path.GetFullPath(archived)) &&
                terminals.Items.Cast<TabItem>().SequenceEqual(existingTerminals) &&
                new[]{bound,named,archived}.All(project=>!File.Exists(TerminalTabDocumentStore.GetProjectPlanPath(project))),
                "session picker excludes the workspace root and archives while keeping saved external projects and a same-named child without opening terminals or plans");
            Field<Popup>(window,"SessionPickerPopup").IsOpen=false;

            Call(window,"SetTerminalLayout",2);
            Call(window,"ArrangeSingleProjectViews");
            var slots=Field<List<(Border Surface,TextBlock Label)>>(window,"_terminalSlots");
            var empty=(Button)slots[0].Surface.Child;
            empty.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            var picker=empty.ContextMenu!;
            await Until(() => Task.FromResult(picker.Items.OfType<MenuItem>().Any(item=>item.Header?.ToString()=="saved-bound")), "saved project picker ready");
            string[] choices=picker.Items.OfType<MenuItem>().Select(item=>item.Header?.ToString()??string.Empty).ToArray();
            Assert(choices.Contains("saved-bound") && choices.Contains("Saved named project") && choices.Contains("External saved project") &&
                !picker.Items.OfType<MenuItem>().Any(item=>item.ToolTip?.ToString()==Path.GetFullPath(workspace)) &&
                picker.Items.OfType<MenuItem>().Any(item=>item.Header?.ToString()=="opencode" && item.ToolTip?.ToString()==Path.GetFullPath(childNamed)) &&
                !choices.Contains("saved-archived") &&
                terminals.Items.Cast<TabItem>().SequenceEqual(existingTerminals) &&
                new[]{bound,named,archived}.All(project=>!File.Exists(TerminalTabDocumentStore.GetProjectPlanPath(project))),
                "empty-slot picker excludes the workspace root and archives while keeping saved external projects and a same-named child without creating plans or terminals");

            var writes=Field<HashSet<string>>(window,"_projectArchiveWrites");
            writes.Add(archived);
            picker.IsOpen=false;
            empty.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            picker=empty.ContextMenu!;
            bool assigned=(bool)Call(window,"AssignProjectToSlot",archived,0)!;
            Assert(!assigned && !rows.Contains(Path.GetFullPath(archived)) &&
                terminals.Items.Cast<TabItem>().SequenceEqual(existingTerminals),
                "archive-write guard prevents direct slot assignment for a saved missing-row project");
            writes.Remove(archived);
        }
        finally { Call(window,"DisposeResources"); window.Close(); }
        await Task.CompletedTask;
    }

    private static async Task RunStartupProjectPickerSmoke(string fixture)
    {
        string workspace=Path.Combine(fixture,"startup-picker");
        string[] projects=new[]{"alpha-known","beta-unregistered","gamma-unregistered","delta-unregistered","zeta-unregistered"}
            .Select(name=>Path.Combine(workspace,name)).ToArray();
        string archived=Path.Combine(workspace,ProjectArchiveService.FolderName,"epsilon-archived");
        foreach(string project in projects.Append(archived)) Directory.CreateDirectory(project);
        string settings=Path.Combine(workspace,"settings.json");
        File.WriteAllText(settings,JsonSerializer.Serialize(new
        {
            Workspace=workspace, CodexWorkspace=workspace, WorkspaceDefaultsVersion=1, SetupCompleted=true,
            ProjectNames=new[]{new { Project=projects[0], Name="Known alpha" }}
        }));

        var store=new AutomationStore(projects[0]);
        await store.CreateDraftAsync("hold startup project history scan");
        var processGate=(SemaphoreSlim)typeof(AutomationStore).GetField("_processGate",BindingFlags.Instance|BindingFlags.NonPublic)!
            .GetValue(store)!;
        await processGate.WaitAsync();
        MainWindow? window=null;
        try
        {
            window=new MainWindow(settings,autoStartCodex:false);
            window.ShowActivated=false;
            window.ShowInTaskbar=false;
            window.Show();
            await Field<Task<string[]>>(window,"_projectDirectoryScan");
            await Task.Delay(100);

            var terminals=(TabControl)window.FindName("TerminalTabs");
            var existingTerminals=terminals.Items.Cast<TabItem>().ToArray();
            var slots=Field<List<(Border Surface,TextBlock Label)>>(window,"_terminalSlots");
            var empty=(Button)slots[0].Surface.Child;
            empty.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            var picker=empty.ContextMenu!;
            await Task.Delay(100);
            picker.UpdateLayout();
            var bitmap=new System.Windows.Media.Imaging.RenderTargetBitmap(
                Math.Max(1,(int)Math.Ceiling(picker.ActualWidth)),Math.Max(1,(int)Math.Ceiling(picker.ActualHeight)),
                96,96,System.Windows.Media.PixelFormats.Pbgra32);
            bitmap.Render(picker);
            var encoder=new System.Windows.Media.Imaging.PngBitmapEncoder();
            encoder.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(bitmap));
            using(var capture=File.Create(Path.Combine(AppContext.BaseDirectory,"startup-slot-picker.png"))) encoder.Save(capture);

            string[] choices=picker.Items.OfType<MenuItem>().Select(item=>item.Header?.ToString()??string.Empty).ToArray();
            var expected=new[]{"Known alpha","beta-unregistered","gamma-unregistered","delta-unregistered","zeta-unregistered"};
            Assert(existingTerminals.Length==0 && processGate.CurrentCount==0 &&
                ((System.Collections.IDictionary)Field<object>(window,"_projectTaskRows")).Count==0 &&
                expected.All(choices.Contains) && !choices.Contains("epsilon-archived") && terminals.Items.Cast<TabItem>().SequenceEqual(existingTerminals) &&
                projects.Append(archived).All(project=>!File.Exists(TerminalTabDocumentStore.GetProjectPlanPath(project))),
                "normal blank startup slot picker excludes archived projects while history scanning is gated, without creating terminals or plans");
            var projectItems=picker.Items.OfType<MenuItem>().Where(item=>expected.Contains(item.Header?.ToString())).ToArray();
            Console.WriteLine($"Menu open={picker.IsOpen} size={picker.ActualWidth}x{picker.ActualHeight}; "+string.Join("; ",projectItems.Select(item=>$"{item.Header}: visible={item.IsVisible}, y={item.TranslatePoint(new Point(),picker).Y}, h={item.ActualHeight}")));
            Assert(picker.IsOpen && projectItems.All(item=>item.IsVisible && item.ActualWidth>0 && item.ActualHeight>0 &&
                item.TranslatePoint(new Point(),picker).Y>=0 &&
                item.TranslatePoint(new Point(),picker).Y+item.ActualHeight<=picker.ActualHeight),
                "the real blank-startup menu visibly renders every normal project while histories are blocked");
            Console.WriteLine("Startup picker screenshot: "+Path.Combine(AppContext.BaseDirectory,"startup-slot-picker.png"));
            picker.IsOpen=false;
            Field<Button>(window,"SessionPickerButton").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await Task.Delay(100);
            var unopened=Field<Dictionary<string,Button>>(window,"_unopenedProjectButtons");
            Assert(Field<Popup>(window,"SessionPickerPopup").IsOpen && projects.All(project=>
                unopened.TryGetValue(project,out var button) && button.IsVisible && button.ActualHeight>0) &&
                !unopened.ContainsKey(archived) && terminals.Items.Count==0,
                "the real startup heading picker shows normal projects and excludes archived projects before history loading finishes");
            Field<Popup>(window,"SessionPickerPopup").IsOpen=false;

            // Delay completion of another real directory scan to exercise a click while it is still pending.
            var discovered=await Field<Task<string[]>>(window,"_projectDirectoryScan");
            var delayedScan=new TaskCompletionSource<string[]>(TaskCreationOptions.RunContinuationsAsynchronously);
            SetField(window,"_projectDirectoryScan",delayedScan.Task);
            try
            {
                empty.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                var loadingPicker=empty.ContextMenu!;
                Assert(loadingPicker.Items.OfType<MenuItem>().Single().Header?.ToString()=="프로젝트 불러오는 중…",
                    "an immediate click shows loading while directory discovery is incomplete");
                delayedScan.SetResult(discovered);
                await Until(()=>Task.FromResult(expected.All(name=>loadingPicker.Items.OfType<MenuItem>().Any(item=>item.Header?.ToString()==name))),
                    "the already open startup picker receives the completed directory listing");
                Assert(ReferenceEquals(empty.ContextMenu,loadingPicker) && loadingPicker.IsOpen && terminals.Items.Count==0,
                    "directory completion populates the same open menu without a second click or terminal creation");
                loadingPicker.IsOpen=false;
            }
            finally { delayedScan.TrySetResult(discovered); }

            for(int i=0;i<20;i++) Directory.CreateDirectory(Path.Combine(workspace,$"overflow-{i:00}"));
            _=(Task)Call(window,"RefreshProjectTasksAsync")!;
            await Field<Task<string[]>>(window,"_projectDirectoryScan");
            empty.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            var longPicker=empty.ContextMenu!;
            await Task.Delay(100);
            var scroller=Descendants<ScrollViewer>(longPicker).Single();
            Assert(longPicker.Items.OfType<MenuItem>().Count(item=>item.ToolTip is string path && Directory.Exists(path))==25 &&
                scroller.ScrollableHeight>0 && scroller.ViewportHeight<=360,
                "a long startup project list keeps all 25 normal choices in a bounded scrollable menu");
            scroller.ScrollToEnd();
            await Task.Delay(100);
            var last=longPicker.Items.OfType<MenuItem>().Last(item=>item.ToolTip is string path && Directory.Exists(path));
            double lastY=last.TranslatePoint(new Point(),scroller).Y;
            Assert(last.IsVisible && lastY>=0 && lastY+last.ActualHeight<=scroller.ActualHeight && terminals.Items.Count==0,
                "scrolling reaches the last project without opening a terminal or selecting a project");
            longPicker.IsOpen=false;
        }
        finally
        {
            processGate.Release();
            if(window is not null) { Call(window,"DisposeResources"); window.Close(); }
        }
    }

    private static async Task RunComposerSmoke(string fixture)
    {
        await RunRegisteredProjectPickerSmoke(fixture);
        string? designPreview = Environment.GetEnvironmentVariable("TERMINALNOTES_UI_DESIGN_PREVIEW");
        if (!string.IsNullOrWhiteSpace(designPreview))
        {
            Directory.CreateDirectory(fixture);
            File.WriteAllText(Path.Combine(fixture, "settings.json"), JsonSerializer.Serialize(new { Workspace = fixture, CodexWorkspace = fixture }));
        }
        var window = new MainWindow(Path.Combine(fixture, "settings.json"), autoStartCodex: false,
            initialWorkspace: string.IsNullOrWhiteSpace(designPreview) ? null : fixture);
        window.ShowActivated = false;
        window.ShowInTaskbar = false;
        _ = new WindowInteropHelper(window).EnsureHandle();
        var panes = new List<TerminalPane>();
        try
        {
            if (!string.IsNullOrWhiteSpace(designPreview))
            {
                if (!Path.IsPathRooted(designPreview)) throw new ArgumentException("TERMINALNOTES_UI_DESIGN_PREVIEW must be an absolute PNG path.");
                await CaptureDesignPreview(window, fixture, Path.GetFullPath(designPreview), panes);
                return;
            }

            string first = Path.Combine(fixture, "first"), second = Path.Combine(fixture, "second");
            Directory.CreateDirectory(first); Directory.CreateDirectory(second);
            Call(window, "SelectTaskProject", first, true);
            var document = Field<TextBox>(window, "_tabDocumentEditor");
            var input = Field<TextBox>(window, "_projectInputEditor");
            var send = Field<Button>(window, "_projectInputSend");
            await Until(() => Task.FromResult(document.IsEnabled), "hidden plan document load");
            document.Text = "# Existing plan\n\nPreserve this history.";
            await (Task)Call(window, "SaveTabDocumentAsync", false)!;
            string planPath = Path.Combine(first, "terminal-notes", "plan.md");
            string originalPlan = File.ReadAllText(planPath);
            const string shared = "Keep this request as a project draft\nSecond line";
            input.Text = shared;
            var drafts = (System.Collections.IDictionary)Field<object>(window, "_projectInputDrafts");
            object draft = drafts[first]!;
            draft.GetType().GetField("SavedInput")!.SetValue(draft, shared);
            Assert(send.Visibility == Visibility.Collapsed && input.Text == shared &&
                document.Text == originalPlan && document.Visibility == Visibility.Visible,
                "the plan composer keeps its draft while the plan editor stays visible");
            Assert(File.ReadAllText(planPath) == originalPlan, "selecting a project does not append unsent input");
            Call(window, "SelectTaskProject", second, true);
            await Until(() => Task.FromResult(document.IsEnabled), "second hidden document");
            input.Text = "Another project's draft";
            Call(window, "SelectTaskProject", first, true);
            await Until(() => Task.FromResult(document.IsEnabled), "return to hidden document");
            Assert(input.Text == shared && (string?)draft.GetType().GetField("SavedInput")!.GetValue(draft) == shared,
                "project switching preserves the request draft and saved retry state");
            var composer = (Grid)input.Parent;
            Assert(!Descendants<RadioButton>(composer).Any() && !Descendants<ComboBox>(composer).Any() &&
                !Descendants<TextBox>(composer).Contains(document),
                "composer has a single input and no mode tabs while the plan editor remains separate");
            typeof(MainWindow).GetField("_projectInputSending", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(window, true);
            Call(window, "UpdateProjectInputComposer");
            Assert(!input.IsEnabled && !Field<Button>(window,"_tabDocumentSave").IsEnabled &&
                !Field<Button>(window,"_tabDocumentExecute").IsEnabled,
                "plan input and both shared actions are disabled while sending");
            typeof(MainWindow).GetField("_projectInputSending", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(window, false);
            Call(window, "UpdateProjectInputComposer");
            var layout = Field<TerminalSlotLayout>(window, "_terminalSlotLayout");
            Call(window, "SetTerminalLayout", 8);
            var tabs = (TabControl)window.FindName("TerminalTabs");
            TabItem AddFixture(string project)
            {
                Call(window, "EnsureProjectTaskRow", project);
                var pane = new TerminalPane(project, startupCommand: "opencode");
                panes.Add(pane);
                Type stateType = typeof(MainWindow).GetNestedType("TerminalTabState", BindingFlags.NonPublic)!;
                object state = Activator.CreateInstance(stateType)!;
                stateType.GetProperty("Pane")!.SetValue(state, pane);
                stateType.GetProperty("Title")!.SetValue(state, Path.GetFileName(project));
                stateType.GetProperty("Sequence")!.SetValue(state, panes.Count);
                stateType.GetProperty("PlanDocumentPath")!.SetValue(state, Path.Combine(project, "terminal-notes", "plan.md"));
                var header = new StackPanel();
                header.Children.Add(new TextBlock { Text = Path.GetFileName(project) });
                var tab = new TabItem { Tag = state, Header = header };
                Call(window, "AttachSingleProjectTerminal", project, tab);
                tabs.Items.Add(tab);
                return tab;
            }
            TabItem AddPickerFixture(string project)
            {
                Call(window, "EnsureProjectTaskRow", project);
                var pane = new TerminalPane(project);
                panes.Add(pane);
                Type stateType = typeof(MainWindow).GetNestedType("TerminalTabState", BindingFlags.NonPublic)!;
                object state = Activator.CreateInstance(stateType)!;
                stateType.GetProperty("Pane")!.SetValue(state, pane);
                stateType.GetProperty("Title")!.SetValue(state, Path.GetFileName(project));
                stateType.GetProperty("Sequence")!.SetValue(state, panes.Count);
                stateType.GetProperty("PlanDocumentPath")!.SetValue(state, Path.Combine(project, "terminal-notes", "plan.md"));
                var header = new StackPanel();
                header.Children.Add(new TextBlock { Text = Path.GetFileName(project) });
                var tab = new TabItem { Tag = state, Header = header };
                Call(window, "AttachSingleProjectTerminal", project, tab);
                tabs.Items.Add(tab);
                return tab;
            }
            var firstTab = AddFixture(first);
            var secondTab = AddFixture(second);
            foreach (string placed in layout.Slots.Where(project => project is not null).Cast<string>().ToArray()) layout.Remove(placed);
            layout.SetCapacity(8);
            Call(window,"UpdateProjectTaskTree");
            var orderedGroup = Field<TreeViewItem>(window,"_taskOrderGroup");
            var rerunTaskRows = (System.Collections.IDictionary)Field<object>(window, "_projectTaskRows");
            var firstRow = rerunTaskRows[first]!;
            var firstNode = Property<TreeViewItem>(firstRow,"Node");
            var secondNode = Property<TreeViewItem>(rerunTaskRows[second]!,"Node");
            string[] lexicalOrder = { first, second };
            Assert(TaskOrder(window).SequenceEqual(lexicalOrder),
                "initial sidebar order uses alphabetical project names for uncategorized tasks");
            TerminalPane firstPaneBeforeReorder = Pane(firstTab), secondPaneBeforeReorder = Pane(secondTab);
            firstNode.IsSelected = true;
            Field<HashSet<string>>(window,"_startingProjectTasks").Add(second);
            Call(window,"AssignProjectToSlot",second,0);
            Call(window,"AssignProjectToSlot",first,1);
            Call(window,"UpdateProjectTaskActivity");
            object selectedTabBeforeReorder = tabs.SelectedItem;
            Assert(TaskOrder(window).SequenceEqual(lexicalOrder) &&
                orderedGroup.Items[0] == firstNode && orderedGroup.Items[1] == secondNode,
                "slot assignment and active task state preserve alphabetical order and the same row nodes");
            Call(window,"AssignProjectToSlot",first,0);
            Assert(TaskOrder(window).SequenceEqual(new[] { first, second }),
                "swapping terminal slots preserves alphabetical task order");
            Assert(firstNode.IsSelected, "slot changes preserve the selected sidebar row");
            Assert(ReferenceEquals(tabs.SelectedItem,selectedTabBeforeReorder), "slot changes preserve the selected terminal tab");
            Assert(ReferenceEquals(Pane(firstTab),firstPaneBeforeReorder) && ReferenceEquals(Pane(secondTab),secondPaneBeforeReorder),
                "slot changes preserve pane identity");
            Assert(input.Text == shared, "slot changes preserve the unsent composer draft");
            Call(window,"RemoveProjectSlot",first);
            Assert(TaskOrder(window).SequenceEqual(lexicalOrder) && ReferenceEquals(Pane(firstTab),firstPaneBeforeReorder),
                "removing a slot preserves alphabetical order without replacing its terminal");
            Call(window,"AssignProjectToSlot",second,6);
            Call(window,"SetTerminalLayout",2);
            Assert(layout.IndexOf(second) == 6 && TaskOrder(window).SequenceEqual(lexicalOrder),
                "hidden slot assignments preserve alphabetical task order when the visible layout shrinks");
            Call(window,"SetTerminalLayout",8);
            Assert(layout.IndexOf(second) == 6 && TaskOrder(window).SequenceEqual(lexicalOrder),
                "expanding the layout restores the hidden slot without changing task order");
            Call(window,"RemoveProjectSlot",second);
            Assert(TaskOrder(window).SequenceEqual(lexicalOrder),
                "clearing a hidden slot preserves alphabetical task order");
            Call(window,"AssignProjectToSlot",second,0);
            Assert(TaskOrder(window).SequenceEqual(lexicalOrder),
                "assigning a terminal slot never reorders the task list");
            RunTaskCategorySmoke(window, first, second, firstTab, secondTab, firstNode, secondNode);
            var taskStart = (Button)Property<object>(firstRow, "Start");
            Assert(!Descendants<Button>(Property<Border>(firstRow,"Surface")).Any(button =>
                System.Windows.Automation.AutomationProperties.GetAutomationId(button) == "ProjectTaskRerun"),
                "the task card has no separate rerun button");
            Assert(window.FindName("RestartTerminalProcessButton") is null &&
                window.FindName("SingleTerminalRerun") is null &&
                !Descendants<Button>(window).Any(button =>
                    System.Windows.Automation.AutomationProperties.GetAutomationId(button) is "SplitTerminalRerun" or "TaskTerminalRerun"),
                "terminal headers and split panes have no separate rerun buttons");
            Assert(Field<FrameworkElement>(window, "SingleTerminalHeader").Visibility == Visibility.Collapsed &&
                Field<FrameworkElement>(window, "TerminalAreaHeader").Visibility == Visibility.Visible &&
                Field<Button>(window, "SessionPickerButton").Visibility == Visibility.Visible &&
                Field<Button>(window, "NewTerminalButton").Visibility == Visibility.Visible,
                "the shared terminal selector remains visible while per-terminal header controls are hidden");
            foreach (string placed in layout.Slots.Where(project => project is not null).Cast<string>().ToArray())
                layout.Remove(placed);
            Call(window, "SetTerminalLayout", 1);
            Assert((bool)Call(window, "EnsureStartedProjectSlotVisible", first)! &&
                layout.IndexOf(first) == 0 && Field<int>(window, "_splitCapacity") == 1,
                "starting an unassigned task uses the free single terminal slot");
            Assert((bool)Call(window, "EnsureStartedProjectSlotVisible", second)! &&
                layout.IndexOf(first) == 0 && layout.IndexOf(second) == 1 && Field<int>(window, "_splitCapacity") == 2 &&
                ReferenceEquals(Pane(firstTab), panes[0]) && ReferenceEquals(Pane(secondTab), panes[1]),
                "starting another task expands to the next supported capacity without changing terminal identities");
            Call(window, "SetTerminalLayout", 1);
            Assert((bool)Call(window, "EnsureStartedProjectSlotVisible", second)! &&
                layout.IndexOf(first) == 0 && layout.IndexOf(second) == 1 && Field<int>(window, "_splitCapacity") == 2,
                "starting a task in a hidden slot expands the layout and preserves both assignments");
            Call(window, "ShowProjectTask", first);
            window.Left = -32000;
            window.Top = -32000;
            window.Show();
            var projectViews = Field<Dictionary<string, Grid>>(window, "_projectTaskViews");
            Assert(taskStart.ActualWidth >= 28 && taskStart.ActualHeight >= 28,
                "the task start-stop target remains visible at compact sidebar width");
            int firstVisibilityChanges = 0, secondVisibilityChanges = 0;
            projectViews[first].IsVisibleChanged += (_, _) => firstVisibilityChanges++;
            projectViews[second].IsVisibleChanged += (_, _) => secondVisibilityChanges++;
            for (int index = 0; index < 3; index++)
            {
                Call(window, "ArrangeSingleProjectViews");
                Call(window, "SelectTerminal", firstTab, false);
            }
            Assert(projectViews[first].IsVisible && projectViews[second].IsVisible &&
                firstVisibilityChanges == 0 && secondVisibilityChanges == 0,
                "repeated layout and terminal selection preserve both visible project hosts without visibility changes");
            window.Hide();
            Call(window, "SetTerminalLayout", 8);
            Assert((bool)Call(window, "AssignProjectToSlot", first, 7)!, "a task can be assigned directly to the eighth slot: " + ((TextBlock)window.FindName("StatusLabel")).Text);
            Assert((bool)Call(window, "AssignProjectToSlot", second, 2)!, "another task can be assigned to an independent chosen slot");
            Assert(layout.IndexOf(first) == 7 && layout.IndexOf(second) == 2 && tabs.Items.Count == 2 && panes.Count == 2,
                "assigning tasks leaves the other six slots empty without creating sessions (" + layout.IndexOf(first) + ", " + layout.IndexOf(second) + ", tabs=" + tabs.Items.Count + ", panes=" + panes.Count + ")");
            Call(window, "AssignProjectToSlot", first, 2);
            Assert(layout.IndexOf(first) == 2 && layout.IndexOf(second) == 7,
                "dropping an already placed task swaps the two positions");
            Assert(ReferenceEquals(Pane(firstTab), panes[0]) && ReferenceEquals(Pane(secondTab), panes[1]),
                "slot swaps retain both existing terminal instances");
            Call(window, "SetTerminalLayout", 2);
            Call(window, "SetTerminalLayout", 8);
            Assert(layout.IndexOf(first) == 2 && layout.IndexOf(second) == 7,
                "shrinking and expanding the layout preserves hidden positions");
            Assert(Field<ScrollViewer>(window, "_taskListScroll").Visibility == Visibility.Visible &&
                Field<Grid>(window, "_taskDeck").Visibility == Visibility.Visible,
                "task list remains available alongside the terminal workspace");
            Call(window, "RemoveProjectSlot", first);
            Assert(layout.IndexOf(first) < 0 && layout.IndexOf(second) == 7 && tabs.Items.Contains(firstTab),
                "clearing a slot preserves the terminal and does not compact other positions");
            Call(window, "ShowProjectTaskList");
            Assert(layout.IndexOf(second) == 7, "returning to the task list leaves assigned terminals in place");
            var views = Field<Dictionary<string, Grid>>(window, "_projectTaskViews");
            typeof(MainWindow).GetField("_draggingTaskSlot", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(window, true);
            Call(window, "ArrangeSingleProjectViews");
            Assert(views.Values.All(view => view.Visibility == Visibility.Hidden),
                "dragging uncovers the WPF drop targets without detaching terminal hosts");
            typeof(MainWindow).GetField("_draggingTaskSlot", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(window, false);
            Call(window, "ArrangeSingleProjectViews");
            Assert(views[second].Visibility == Visibility.Visible && views[first].Visibility == Visibility.Hidden &&
                ReferenceEquals(Pane(secondTab), panes[1]), "ending a drag restores only assigned views and preserves the session");
            foreach (int capacity in new[] { 1, 2, 3, 4, 6, 8 })
            {
                Call(window, "SetTerminalLayout", capacity);
                var deck = Field<Grid>(window, "_taskDeck");
                Assert(deck.Children.OfType<Border>().Count(border => border.AllowDrop && border.Visibility == Visibility.Visible) == capacity &&
                    layout.IndexOf(second) == 7, $"{capacity} fixed drop targets preserve the hidden eighth-slot assignment");
            }
            Assert((bool)Call(window, "AssignProjectToSlot", first, 7)! && layout.IndexOf(second) == -1 &&
                tabs.Items.Contains(secondTab) && ReferenceEquals(Pane(secondTab), panes[1]),
                "placing an unassigned task replaces one slot while retaining the displaced session");
            var taskSurface = Field<Grid>(window, "_taskSurface");
            taskSurface.Measure(new Size(1024, 600));
            taskSurface.Arrange(new System.Windows.Rect(0, 0, 1024, 600));
            taskSurface.UpdateLayout();
            var taskRows = Field<System.Collections.IDictionary>(window, "_projectTaskRows");
            object row = taskRows[first]!;
            Assert(Math.Abs(Property<Border>(row, "Surface").ActualHeight - 40) < 1 &&
                Field<ScrollViewer>(window, "_taskListScroll").ScrollableWidth == 0 &&
                Property<FrameworkElement>(row, "RolePanel").Visibility == Visibility.Collapsed,
                $"single-row sidebar cards fit without horizontal scrolling or legacy role panels (height={Property<Border>(row, "Surface").ActualHeight}, scroll={Field<ScrollViewer>(window, "_taskListScroll").ScrollableWidth}, roles={Property<FrameworkElement>(row, "RolePanel").Visibility})");
            // Use the production terminal host attachment, without starting a process.
            var titleLabel = Field<Dictionary<TabItem, TextBlock>>(window, "_taskTerminalLabels")[firstTab];
            var titleSurface = (Grid)titleLabel.Parent;
            var heading = (ContentControl)titleSurface.Parent;
            var terminalHeader = (Grid)heading.Parent;
            var terminalBody = (Grid)terminalHeader.Parent;
            var terminalFrame = (Border)terminalBody.Parent;
            Assert(terminalHeader.Visibility == Visibility.Visible && titleLabel.Text == (string)Call(window,"ProjectDisplayName",first)! &&
                terminalFrame.BorderThickness == new Thickness(0) && terminalFrame.Margin == new Thickness(0) &&
                Descendants<Button>(terminalHeader).All(button=>!button.IsVisible),
                "task terminals expose the task name while keeping legacy header actions hidden");
            Call(window, "AssignProjectToSlot", second, 2);
            Func<DependencyObject, object, DragDropEffects, DragDropEffects> move = (source, payload, effects) =>
            {
                Assert(ReferenceEquals(source, window) && views.Values.All(view => view.Visibility == Visibility.Hidden),
                    "OLE drag uses the stable window source while terminal views uncover drop targets");
                Assert(payload is IDataObject data && (string)data.GetData("TerminalNotes.ProjectSlot") == first && effects == DragDropEffects.Move,
                    "terminal drag carries the project identity as a move operation");
                Call(window, "AssignProjectToSlot", first, 2);
                return DragDropEffects.Move;
            };
            Call(window, "RunTaskSlotDrag", first, move);
            Assert(layout.IndexOf(first) == 2 && layout.IndexOf(second) == 7 && views[first].Visibility == Visibility.Visible &&
                views[second].Visibility == Visibility.Visible && ReferenceEquals(Pane(firstTab), panes[0]),
                "moving an existing terminal swaps occupied slots and restores both original hosts");
            var beforeCancel = layout.Slots.ToArray();
            Call(window, "RunTaskSlotDrag", first, (Func<DependencyObject, object, DragDropEffects, DragDropEffects>)((_, _, _) => DragDropEffects.None));
            Assert(layout.Slots.SequenceEqual(beforeCancel) && !Field<bool>(window, "_draggingTaskSlot"),
                "cancelled drags preserve placement and clear the drag state");
            try
            {
                Call(window, "RunTaskSlotDrag", first, (Func<DependencyObject, object, DragDropEffects, DragDropEffects>)((_, _, _) => throw new InvalidOperationException("drag fixture failure")));
                throw new Exception("drag fixture should fail");
            }
            catch (TargetInvocationException ex) when (ex.InnerException is InvalidOperationException { Message: "drag fixture failure" }) { }
            Assert(!Field<bool>(window, "_draggingTaskSlot") && views[first].Visibility == Visibility.Visible && layout.Slots.SequenceEqual(beforeCancel),
                "failed drags restore views without losing sessions or placement");

            var sidebarColumn = Field<ColumnDefinition>(window, "_taskSidebarColumn");
            sidebarColumn.Width = new GridLength(300);
            taskSurface.Measure(new Size(1024, 600));
            taskSurface.Arrange(new System.Windows.Rect(0, 0, 1024, 600));
            taskSurface.UpdateLayout();
            double expandedDeckWidth = Field<Grid>(window, "_taskDeck").ActualWidth;
            var toggle = (Button)window.FindName("FocusButton");
            toggle.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            taskSurface.Measure(new Size(1024, 600));
            taskSurface.Arrange(new System.Windows.Rect(0, 0, 1024, 600));
            taskSurface.UpdateLayout();
            Assert(Field<ScrollViewer>(window, "_taskListScroll").Visibility == Visibility.Collapsed &&
                ((FrameworkElement)window.FindName("NotesPanel")).Visibility == Visibility.Collapsed &&
                sidebarColumn.Width.Value <= 40 &&
                Field<RadioButton>(window, "_normalTasksTab").Visibility == Visibility.Collapsed &&
                Field<RadioButton>(window, "_archivedTasksTab").Visibility == Visibility.Collapsed &&
                toggle.Content is System.Windows.Shapes.Path &&
                Field<Grid>(window, "_taskDeck").ActualWidth > expandedDeckWidth && toggle.Visibility == Visibility.Visible,
                $"collapsing the task panel gives its width to terminals and leaves the expand button available (list={Field<ScrollViewer>(window, "_taskListScroll").Visibility}, min={sidebarColumn.MinWidth}, width={sidebarColumn.Width.Value}, deck={Field<Grid>(window, "_taskDeck").ActualWidth}, previous={expandedDeckWidth}, toggle={toggle.Visibility})");
            Call(window, "ShowProjectTask", first);
            Call(window, "ShowProjectTaskList");
            Call(window, "SetTerminalLayout", 4);
            Call(window, "SetTerminalLayout", 8);
            Assert(Field<ScrollViewer>(window, "_taskListScroll").Visibility == Visibility.Collapsed && layout.Slots.SequenceEqual(beforeCancel),
                "project selection and layout changes respect a collapsed sidebar without changing placements");
            using (var saved = JsonDocument.Parse(File.ReadAllText(Path.Combine(fixture, "settings.json"))))
                Assert(saved.RootElement.GetProperty("TaskSidebarCollapsed").GetBoolean() &&
                    saved.RootElement.GetProperty("TaskSidebarWidth").GetDouble() == 300,
                    "sidebar collapsed state and user width persist in settings");
            toggle.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            taskSurface.Measure(new Size(1024, 600));
            taskSurface.Arrange(new System.Windows.Rect(0, 0, 1024, 600));
            taskSurface.UpdateLayout();
            Assert(sidebarColumn.Width.Value == 300 && Field<ScrollViewer>(window, "_taskListScroll").Visibility == Visibility.Visible &&
                ((FrameworkElement)window.FindName("NotesPanel")).Visibility == Visibility.Visible &&
                Field<RadioButton>(window, "_normalTasksTab").Visibility == Visibility.Visible &&
                Field<RadioButton>(window, "_archivedTasksTab").Visibility == Visibility.Visible &&
                ReferenceEquals(Pane(firstTab), panes[0]) && ReferenceEquals(Pane(secondTab), panes[1]),
                "expanding restores the adjusted sidebar width and preserves both terminal instances");
            Call(window, "LoadTaskSidebarSettings", true, 300d);
            Assert(Field<bool>(window, "_taskSidebarCollapsed") && sidebarColumn.Width.Value <= 40 &&
                ((FrameworkElement)window.FindName("NotesPanel")).Visibility == Visibility.Collapsed,
                "restoring sidebar settings keeps both task and plan panels collapsed");
            Call(window, "LoadTaskSidebarSettings", false, 300d);

            // Reproduce native status using a test-owned liveness process, without OpenCode or model calls.
            void SetPrivate(object target, string field, object? value) => target.GetType()
                .GetField(field, BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(target, value);
            using (var status = new OpenCodeStatusMonitor(new Uri("http://127.0.0.1:43127/"), first))
            using (var recovery = new OpenCodeAutoResumeController(first, null,
                new OpenCodeRecoveryClient(new Uri("http://127.0.0.1:43127/"), first), () => false))
            {
                var testPane = panes[0];
                using var livenessHost = new OpenCodeHeadlessHost(new Uri("http://127.0.0.1:43128/"));
                var livenessProcess = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
                {
                    FileName = "powershell.exe",
                    ArgumentList = { "-NoLogo", "-NoProfile", "-NonInteractive", "-Command", "Start-Sleep -Seconds 300" },
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    WindowStyle = System.Diagnostics.ProcessWindowStyle.Hidden
                }) ?? throw new InvalidOperationException("Could not start the synthetic headless-liveness process.");
                SetPrivate(livenessHost, "_process", livenessProcess);
                SetPrivate(testPane, "_headlessOpenCodeHost", livenessHost);
                void SetHeadlessLiveness(bool running) => SetPrivate(testPane, "_headlessOpenCodeHost", running ? livenessHost : null);
                SetPrivate(testPane, "_usesNativeOpenCodeStatus", true);
                SetPrivate(testPane, "_openCodeStatus", status);
                SetPrivate(testPane, "_autoResume", recovery);
                SetPrivate(Field<object>(testPane, "_session"), "_running", true);
                SetPrivate(recovery, "_enabled", true);
                SetPrivate(recovery, "_hasUnfinishedWork", false);
                void Status(OpenCodeStatusKind kind)
                {
                    var value = new OpenCodeStatusSnapshot(kind, false, "owned-session");
                    SetPrivate(status, "_snapshot", value);
                    SetPrivate(testPane, "_openCodeSnapshot", value);
                    Call(window, "UpdateProjectTaskActivity");
                }
                string statusOnlyProject = Path.Combine(fixture, "task without native pane");
                Call(window, "EnsureProjectTaskRow", statusOnlyProject);
                Call(window, "UpdateProjectTaskActivity");
                var statusRows = Field<System.Collections.IDictionary>(window, "_projectTaskRows");
                object statusOnlyRow = statusRows[statusOnlyProject]!;
                Assert(Property<TextBlock>(statusOnlyRow, "State").Text.StartsWith("○ 확인 안 함") &&
                    Property<TextBlock>(statusOnlyRow, "State").ToolTip?.ToString()?.Contains("실제 작업 진척을 보장하지 않습니다") == true,
                    "a task without a native monitor says status is unchecked and does not imply progress");
                string Accent() => ((System.Windows.Media.SolidColorBrush)Property<Border>(row, "Surface").BorderBrush).Color.ToString();
                object statusRuntime = Call(window, "RuntimeFor", first)!;
                var planInputNeeded = statusRuntime.GetType().GetField("SinglePlanInputNeeded")!;
                var singleStopped = statusRuntime.GetType().GetField("SingleStopped")!;
                var singleSubmitted = statusRuntime.GetType().GetField("SingleSubmitted")!;
                var singleError = statusRuntime.GetType().GetField("SingleError")!;
                var singleSubmittedForRecovery = statusRuntime.GetType().GetField("SingleSubmitted")!;
                var singleTerminalForRecovery = statusRuntime.GetType().GetField("SingleTerminal")!;
                var startingSingleForRecovery = statusRuntime.GetType().GetField("StartingSingle")!;
                var intentVersionForRecovery = statusRuntime.GetType().GetField("IntentVersion")!;
                object? previousTerminal = singleTerminalForRecovery.GetValue(statusRuntime);
                object? previousError = singleError.GetValue(statusRuntime);
                bool previousSubmitted = (bool)singleSubmittedForRecovery.GetValue(statusRuntime)!;
                bool previousStopped = (bool)singleStopped.GetValue(statusRuntime)!;
                bool previousStarting = (bool)startingSingleForRecovery.GetValue(statusRuntime)!;
                long previousIntent = (long)intentVersionForRecovery.GetValue(statusRuntime)!;
                singleTerminalForRecovery.SetValue(statusRuntime, firstTab);
                singleSubmittedForRecovery.SetValue(statusRuntime, true);
                singleStopped.SetValue(statusRuntime, true);
                singleError.SetValue(statusRuntime, "Prior native dispatch was not confirmed");
                Call(window, "UpdateProjectTaskActivity");
                Assert(Property<TextBlock>(row, "State").Text.Contains("\u26A0"),
                    "an unconfirmed native dispatch shows a visible warning");
                var manualObservation = status.ObserveSubmission();
                SetPrivate(testPane, "_lastManualSubmissionObservation", manualObservation);
                object taskTabState = firstTab.Tag;
                startingSingleForRecovery.SetValue(statusRuntime, true);
                intentVersionForRecovery.SetValue(statusRuntime, 42L);
                Call(window, "ObserveManualSubmission", taskTabState, "Please continue");
                Call(window, "UpdateProjectTaskActivity");
                Assert(singleError.GetValue(statusRuntime) is not null && Property<TextBlock>(row, "State").Text.Contains("\u26A0") &&
                    (long)intentVersionForRecovery.GetValue(statusRuntime)! == 43L,
                    "manual Enter alone keeps the warning and supersedes the pending managed start");
                long manualBaseline = manualObservation.ConfirmationVersion;
                long manualGeneration = manualObservation.Generation;
                Call(window, "ObserveNativeSubmissionConfirmation", taskTabState, manualBaseline + 1, manualGeneration);
                Assert(singleError.GetValue(statusRuntime) is null &&
                    (bool)singleSubmittedForRecovery.GetValue(statusRuntime)! && (bool)singleStopped.GetValue(statusRuntime)!,
                    "fresh native user-message confirmation clears only the stale error");
                singleTerminalForRecovery.SetValue(statusRuntime, previousTerminal);
                singleError.SetValue(statusRuntime, previousError);
                singleSubmittedForRecovery.SetValue(statusRuntime, previousSubmitted);
                singleStopped.SetValue(statusRuntime, previousStopped);
                startingSingleForRecovery.SetValue(statusRuntime, previousStarting);
                intentVersionForRecovery.SetValue(statusRuntime, previousIntent);
                Call(window, "UpdateProjectTaskActivity");
                var taskOrderGroup = Field<TreeViewItem>(window, "_taskOrderGroup");
                var taskNode = Property<TreeViewItem>(row, "Node");
                SetPrivate(recovery, "_hasUnfinishedWork", false);
                planInputNeeded.SetValue(statusRuntime, true);
                singleStopped.SetValue(statusRuntime, false);
                singleSubmitted.SetValue(statusRuntime, false);
                singleError.SetValue(statusRuntime, null);
                SetPrivate(status, "_monitorPhase", OpenCodeStatusMonitorPhase.NotMonitoring);
                Status(OpenCodeStatusKind.Unknown);
                Assert(taskOrderGroup.Items.Contains(taskNode) &&
                    Property<TextBlock>(row, "State").Text.Contains("플랜 입력 대기") && !Accent().EndsWith("40D392") &&
                    Property<TextBlock>(row, "State").Visibility == Visibility.Collapsed && TaskActionStatus(row) == "Attention" &&
                    TaskActionHelp(row).Contains("시작") && TaskActionHelp(row).Contains("플랜 입력 대기") &&
                    System.Windows.Automation.AutomationProperties.GetName(Property<Button>(row, "Start")) == "시작",
                    "a live native pane awaiting the user's plan shows an accessible attention icon without footer status text");
                singleStopped.SetValue(statusRuntime, true);
                Status(OpenCodeStatusKind.Unknown);
                Assert(taskOrderGroup.Items.Contains(taskNode) &&
                    Property<TextBlock>(row, "State").Text.Contains("중단") && TaskActionStatus(row) == "Stop" &&
                    !Accent().EndsWith("40D392") &&
                    TaskActionHelp(row).Contains("중단"),
                    "an explicitly stopped plan-input task stays ordered and reports a neutral stop icon");
                singleError.SetValue(statusRuntime, "Native task failed");
                Status(OpenCodeStatusKind.Unknown);
                Assert(TaskActionStatus(row) == "Attention" && !Accent().EndsWith("40D392") &&
                    TaskActionHelp(row).Contains("실행 확인 필요"),
                    "a hard error remains visible when stopped state is also present");
                singleError.SetValue(statusRuntime, null);
                singleStopped.SetValue(statusRuntime, false);
                SetHeadlessLiveness(false);
                SetPrivate(Field<object>(testPane, "_session"), "_running", false);
                Status(OpenCodeStatusKind.Unknown);
                Assert(taskOrderGroup.Items.Contains(taskNode) &&
                    Property<TextBlock>(row, "State").Text.Contains("플랜 입력 대기"),
                    "an exited plan-input pane stays present in the global task list");
                SetPrivate(Field<object>(testPane, "_session"), "_running", true);
                SetHeadlessLiveness(true);
                planInputNeeded.SetValue(statusRuntime, false);
                Status(OpenCodeStatusKind.Unknown);
                singleSubmitted.SetValue(statusRuntime, true);
                SetPrivate(recovery, "_enabled", false);
                SetPrivate(recovery, "_hasUnfinishedWork", false);
                SetPrivate(status, "_monitorPhase", OpenCodeStatusMonitorPhase.Checking);
                Status(OpenCodeStatusKind.Unknown);
                Assert(taskOrderGroup.Items.Contains(taskNode) && Property<TextBlock>(row, "State").Text.Contains("진행 중 · 상태 확인 중") && !Accent().EndsWith("40D392"),
                    "a submitted native task distinguishes a held baseline check from task progress (" + Property<TextBlock>(row, "State").Text + ")");
                SetPrivate(status, "_monitorPhase", OpenCodeStatusMonitorPhase.Monitoring);
                Status(OpenCodeStatusKind.Idle);
                Assert(taskOrderGroup.Items.Contains(taskNode) &&
                    Property<TextBlock>(row, "State").Text.Contains("진행 중") && !Accent().EndsWith("40D392"),
                    "an accepted idle task stays in progress without a false active tone");
                SetPrivate(recovery, "_status", "자동 진행 꺼짐");
                Status(OpenCodeStatusKind.Idle);
                Assert(Property<TextBlock>(row, "State").Text.Contains("진행 중 · 자동 진행 꺼짐") && !Accent().EndsWith("40D392"),
                    "an accepted task remains in progress while retaining a routine auto-resume status");
                SetPrivate(recovery, "_status", "");
                SetPrivate(status, "_monitorPhase", OpenCodeStatusMonitorPhase.NotMonitoring);
                Status(OpenCodeStatusKind.Pending);
                Assert(Property<TextBlock>(row, "State").Text.Contains("진행 중 · 요청 전달 중") && !Accent().EndsWith("40D392"),
                    "an accepted pending task remains in progress while native submission is pending");
                Status(OpenCodeStatusKind.Busy);
                Assert(Property<TextBlock>(row, "State").Text.Contains("진행 중 · 응답 대기") &&
                    !Accent().EndsWith("40D392") && testPane.Activity == TerminalActivity.Quiet,
                    "an accepted native busy task without response activity stays neutral and in progress");
                singleStopped.SetValue(statusRuntime, true);
                Status(OpenCodeStatusKind.Busy);
                Assert(taskOrderGroup.Items.Contains(taskNode) &&
                    Property<TextBlock>(row, "State").Text.Contains("중단") && !Property<TextBlock>(row, "State").Text.Contains("진행 중"),
                    "an explicit stop remains distinct from cached native busy status");
                singleStopped.SetValue(statusRuntime, false);
                Status(OpenCodeStatusKind.Unknown);
                SetPrivate(status, "_monitorPhase", OpenCodeStatusMonitorPhase.Checking);
                Status(OpenCodeStatusKind.Unknown);
                sidebarColumn.Width = new GridLength(200);
                taskSurface.Measure(new Size(1024, 600));
                taskSurface.Arrange(new System.Windows.Rect(0, 0, 1024, 600));
                taskSurface.UpdateLayout();
                var stateLabel = Property<TextBlock>(row, "State");
                Assert(stateLabel.Visibility == Visibility.Collapsed && TaskActionStatus(row) == "Wait" &&
                    TaskActionHelp(row).Contains("상태 확인 중"),
                    "the checking state is represented by the start/stop icon and accessible help, without footer text");
                sidebarColumn.Width = new GridLength(300);
                taskSurface.Measure(new Size(1024, 600));
                taskSurface.Arrange(new System.Windows.Rect(0, 0, 1024, 600));
                taskSurface.UpdateLayout();
                SetPrivate(status, "_monitorPhase", OpenCodeStatusMonitorPhase.Monitoring);
                Status(OpenCodeStatusKind.Unknown);
                Assert(Property<TextBlock>(row, "State").Text.StartsWith("● 상태 감시 중 · 진행 중 · 상태 확인 중"),
                    "a connected native event stream is labeled as monitoring");
                SetPrivate(status, "_monitorPhase", OpenCodeStatusMonitorPhase.ReconnectWaiting);
                Status(OpenCodeStatusKind.Unknown);
                Assert(Property<TextBlock>(row, "State").Text.StartsWith("⚠ 재연결 대기 · 진행 중 · 상태 확인 불가") &&
                    Property<TextBlock>(row, "State").Text.Contains("상태 확인 불가") && TaskActionStatus(row) == "Attention" &&
                    TaskActionHelp(row).Contains("재연결 대기"),
                    "a disconnected native monitor shows an attention icon and accessible reconnect state");
                statusRuntime.GetType().GetField("SingleSubmitted")!.SetValue(statusRuntime, false);
                SetPrivate(recovery, "_enabled", true);
                SetPrivate(recovery, "_hasUnfinishedWork", true);
                SetPrivate(status, "_monitorPhase", OpenCodeStatusMonitorPhase.NotMonitoring);
                Status(OpenCodeStatusKind.Busy);
                Assert(!Accent().EndsWith("40D392") && testPane.Activity == TerminalActivity.Quiet &&
                    Property<TextBlock>(row, "State").Text.Contains("대기"),
                    "native busy without current response activity remains neutral and shows waiting");
                SetPrivate(status, "_lastActivityAt", DateTimeOffset.UtcNow);
                Status(OpenCodeStatusKind.Busy);
                var activityGlyph = Property<Button>(row, "Start").Content;
                Assert(Accent().EndsWith("40D392") && activityGlyph.GetType().Name == "AutomationActivityIndicator" &&
                    (bool)activityGlyph.GetType().GetField("_active", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(activityGlyph)! &&
                    !(bool)activityGlyph.GetType().GetProperty("IsAnimating")!.GetValue(activityGlyph)!,
                    "recent native response activity uses the active status indicator and task color while hidden without running an animation");
                singleStopped.SetValue(statusRuntime, true);
                Status(OpenCodeStatusKind.Busy);
                Assert(!Accent().EndsWith("40D392") && TaskActionStatus(row) == "Stop",
                    "an explicit stop suppresses stale recent activity coloring and animation");
                singleStopped.SetValue(statusRuntime, false);
                Status(OpenCodeStatusKind.Busy);
                SetPrivate(status, "_lastActivityAt", DateTimeOffset.UtcNow.AddSeconds(-9));
                Status(OpenCodeStatusKind.Busy);
                Assert(!Accent().EndsWith("40D392"), "stale native activity cannot leave a busy terminal green");
                Status(OpenCodeStatusKind.Idle);
                Assert(!Accent().EndsWith("40D392") &&
                    System.Windows.Automation.AutomationProperties.GetName(Property<Button>(row, "Start")) == "중단",
                    "unfinished recovery work keeps Stop available but does not pretend idle is active green");
                SetPrivate(recovery, "_isStalled", true);
                SetPrivate(recovery, "_status", "정체 의심 · 복구 대기");
                Status(OpenCodeStatusKind.Busy);
                Assert(testPane.Activity == TerminalActivity.Quiet && !Accent().EndsWith("40D392") &&
                    Property<TextBlock>(row, "State").Text.Contains("정체"),
                    "a stalled native busy turn has a distinct recovery label and neutral color");
                Status(OpenCodeStatusKind.NeedsInput);
                Assert(testPane.Activity == TerminalActivity.NeedsInput && Accent().EndsWith("FFB347"),
                    "approval input retains orange even when a stall was suspected");
                SetHeadlessLiveness(false);
                SetPrivate(Field<object>(testPane, "_session"), "_running", false);
                SetPrivate(testPane, "_ready", true);
                Status(OpenCodeStatusKind.Busy);
                Assert(!((bool)Call(window, "ProjectTaskIsRunning", first)!) && !Accent().EndsWith("40D392") &&
                    Property<TextBlock>(row, "State").Text.EndsWith("터미널 종료"),
                    "a stopped process cannot retain its cached busy state or active task color");
                SetPrivate(testPane, "_ready", false);
                SetHeadlessLiveness(false);
                SetPrivate(testPane, "_usesNativeOpenCodeStatus", false);
                SetPrivate(testPane, "_openCodeStatus", null);
                SetPrivate(testPane, "_autoResume", null);
                Call(window, "UpdateProjectTaskActivity");
            }
            using (var stopPane = new TerminalPane(first))
            {
                var ownedSession = Field<ConPtySession>(stopPane, "_session");
                ownedSession.Start(first, startupCommand: "Start-Sleep -Seconds 300", exitOnStartupCommandCompletion: true);
                SetPrivate(stopPane, "_ready", true);
                bool ownsRequest = false, finalVerification = true;
                var request = new OpenCodeStalledRestart("test-session", "test-request", planPath, "hash", "continue",
                    () => ownsRequest, () => Task.FromResult(finalVerification));
                Assert(!await (Task<bool>)Call(stopPane, "StopStalledProcessAsync", request)! && ownedSession.IsRunning,
                    "a changed request guard prevents termination of the owned ConPTY process");
                ownsRequest = true; finalVerification = false;
                Assert(!await (Task<bool>)Call(stopPane, "StopStalledProcessAsync", request)! && ownedSession.IsRunning,
                    "failed final native verification keeps the real owned process alive");
                finalVerification = true;
                Assert(await (Task<bool>)Call(stopPane, "StopStalledProcessAsync", request)! && !ownedSession.IsRunning,
                    "verified recovery waits for the real owned ConPTY process to exit before replacement");
            }
            using (var handoffPane = new TerminalPane(first))
            {
                var ownedSession = Field<ConPtySession>(handoffPane, "_session");
                ownedSession.Start(first, startupCommand: "Start-Sleep -Seconds 300", exitOnStartupCommandCompletion: true);
                SetPrivate(handoffPane, "_ready", true);
                SetPrivate(handoffPane, "_externalContinuationSession", "ses_verified_handoff");
                await (Task)Call(handoffPane, "StopForExternalContinuationAsync")!;
                Assert(!ownedSession.IsRunning && !Property<bool>(handoffPane, "WasContinuedExternally") &&
                    Property<bool>(handoffPane, "HasContinuationSession"),
                    "retiring the owned process preserves its verified session without claiming external launch success");
                Call(handoffPane, "MarkExternalContinuationFailed");
                Assert(handoffPane.ActivityLabel.Contains("실패") &&
                    await (Task<string>)Call(handoffPane, "CaptureContinuationSessionAsync")! == "ses_verified_handoff",
                    "a failed external launch keeps the original session available for retry after its server exits");
                await (Task)Call(handoffPane, "StopForExternalContinuationAsync")!;
                bool wrongSessionRejected = false;
                try { Call(handoffPane, "CompleteExternalContinuation", "ses_other"); }
                catch (System.Reflection.TargetInvocationException ex) when (ex.InnerException is InvalidOperationException)
                { wrongSessionRejected = true; }
                Assert(wrongSessionRejected && !Property<bool>(handoffPane, "WasContinuedExternally"),
                    "a different confirmed external session cannot claim the retired pane");
                Call(handoffPane, "CompleteExternalContinuation", "ses_verified_handoff");
                Assert(Property<bool>(handoffPane, "WasContinuedExternally") && handoffPane.ActivityLabel == "CMD에서 이어가기",
                    "only a confirmed external launch transitions the pane from pending or failed to handed off");
            }
            using (var crashedPane = new TerminalPane(first))
            using (var recoveryClient = new OpenCodeRecoveryClient(new Uri("http://127.0.0.1:43219/"), first))
            using (var recovery = new OpenCodeAutoResumeController(first, planPath, recoveryClient, () => false))
            {
                SetPrivate(crashedPane, "_failed", true);
                SetPrivate(crashedPane, "_autoResume", recovery);
                SetPrivate(recovery, "_continuationSessionId", "ses_previously_verified");
                Assert(await (Task<string>)Call(crashedPane, "CaptureContinuationSessionAsync")! == "ses_previously_verified",
                    "a crashed server can hand off an exact root session already verified by the recovery client");
                SetPrivate(crashedPane, "_externalContinuationSession", null);
                SetPrivate(recovery, "_continuationSessionId", null);
                SetPrivate(recovery, "_candidate", new OpenCodeRecoverySignal("message.updated", "ses_unverified", "msg_test", DateTimeOffset.UtcNow, 1));
                bool unverifiedRejected = false;
                try { await (Task<string>)Call(crashedPane, "CaptureContinuationSessionAsync")!; }
                catch (InvalidOperationException) { unverifiedRejected = true; }
                Assert(unverifiedRejected, "a cached but unverified native candidate is never guessed after server exit");
            }
            object secondRow = taskRows[second]!;
            var card = Property<Border>(row, "Surface");
            var loop = Property<CheckBox>(row, "Loop");
            var action = Property<Button>(row, "Start");
            Assert(Descendants<CheckBox>(card).SequenceEqual(new[] { loop }) && loop.Content is null &&
                System.Windows.Automation.AutomationProperties.GetName(loop).Contains("반복 실행") &&
                Descendants<Button>(card).Count(button => button.ActualWidth > 0) == 3 && Descendants<Button>(card).Contains(action),
                "the card has one improvement checkbox, one state action, its folder action and title without terminal Open or Settings buttons");
            Assert(!Descendants<Button>(views[first]).Any(button => Equals(button.Content, "⚙")) &&
                Property<Button>(row, "Select").ContextMenu.Items.Contains(Property<MenuItem>(row, "Until")),
                "deadline settings remain in the card context menu without a duplicate terminal settings button");
            Assert(action.IsEnabled && System.Windows.Automation.AutomationProperties.GetName(action) == "시작" &&
                TaskActionStatus(row) == "Play", "an idle task exposes Start on its single action button");
            var starting = Field<HashSet<string>>(window, "_startingProjectTasks");
            var controlWrites = Field<HashSet<string>>(window, "_projectTaskControlWrites");
            var stopping = Field<HashSet<string>>(window, "_stoppingProjectTasks");
            starting.Add(first);
            controlWrites.Add(first);
            Call(window, "UpdateProjectTaskActivity");
            Assert(action.IsEnabled && System.Windows.Automation.AutomationProperties.GetName(action) == "중단" &&
                TaskActionStatus(row) == "Wait" && Property<TextBlock>(row, "State").Text.EndsWith("시작 확인 중") &&
                    Field<TreeViewItem>(window, "_taskOrderGroup").Items.Contains(Property<TreeViewItem>(row, "Node")),
                "the same action can stop startup while its plan is still loading or saving");
            object runtime = Call(window, "RuntimeFor", first)!;
            using var startupCancellation = new CancellationTokenSource();
            runtime.GetType().GetField("SingleStartupCancellation")!.SetValue(runtime, startupCancellation);
            long intent = (long)runtime.GetType().GetField("IntentVersion")!.GetValue(runtime)!;
            action.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await Until(() => Task.FromResult(!stopping.Contains(first)), "startup stop completion");
            Assert(startupCancellation.IsCancellationRequested &&
                (bool)runtime.GetType().GetField("SingleStopped")!.GetValue(runtime)! &&
                (long)runtime.GetType().GetField("IntentVersion")!.GetValue(runtime)! > intent &&
                    tabs.Items.Count == 2 && ReferenceEquals(Pane(firstTab), panes[0]),
                "clicking Stop cancels startup intent while preserving both terminal sessions");
            starting.Remove(first);
            controlWrites.Remove(first);
            runtime.GetType().GetField("SingleStartupCancellation")!.SetValue(runtime, null);
            using (var composerStartupCancellation = new CancellationTokenSource())
            {
                runtime.GetType().GetField("SingleStartupCancellation")!.SetValue(runtime, composerStartupCancellation);
                runtime.GetType().GetField("SingleStopped")!.SetValue(runtime, false);
                runtime.GetType().GetField("SingleStartPhase")!.SetValue(runtime, "◌ 연결 확인 중 · + 46초 · 로딩 지연 확인 중");
                SetPrivate(window, "_tabDocumentSaving", true);
                Call(window, "UpdateProjectTaskActivity");
                Assert(!starting.Contains(first) && action.IsEnabled &&
                    System.Windows.Automation.AutomationProperties.GetName(action) == "중단" &&
                    Property<TextBlock>(row, "State").Text.Contains("로딩 지연 확인 중"),
                    "plan-composer loading keeps Stop enabled and shows the checking phase while document saving is active");
                action.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                await Until(() => Task.FromResult(composerStartupCancellation.IsCancellationRequested && !stopping.Contains(first)),
                    "plan-composer startup stop completion");
                Assert((bool)runtime.GetType().GetField("SingleStopped")!.GetValue(runtime)!,
                    "the plan-composer loading Stop button cancels its registered startup");
                runtime.GetType().GetField("SingleStartupCancellation")!.SetValue(runtime, null);
                runtime.GetType().GetField("SingleStartPhase")!.SetValue(runtime, null);
                SetPrivate(window, "_tabDocumentSaving", false);
            }
            stopping.Add(first);
            Call(window, "UpdateProjectTaskActivity");
            intent = (long)runtime.GetType().GetField("IntentVersion")!.GetValue(runtime)!;
            await (Task)Call(window, "ToggleProjectTaskAsync", first)!;
            Assert(!action.IsEnabled && System.Windows.Automation.AutomationProperties.GetName(action) == "중단 중" &&
                (long)runtime.GetType().GetField("IntentVersion")!.GetValue(runtime)! == intent && !starting.Contains(first),
                "pending stop storage disables the action and rejects repeat clicks or premature restart");
            stopping.Remove(first);
            Call(window, "UpdateProjectTaskActivity");
            Assert(action.IsEnabled && System.Windows.Automation.AutomationProperties.GetName(action) == "시작",
                "the same button returns to Start once stopping is complete");
            var externalWrites = Field<HashSet<string>>(window, "_externalTerminalWrites");
            externalWrites.Add(first);
            Call(window, "UpdateProjectTaskActivity");
            Assert(!action.IsEnabled, "external terminal handoff disables the project action");
            externalWrites.Remove(first);
            Call(window, "UpdateProjectTaskActivity");
            loop.IsChecked = true;
            await Until(() => Task.FromResult(SingleProjectContinuousImprovementStore.Read(first)?.Enabled == true &&
                !Field<HashSet<string>>(window, "_projectTaskOptionWrites").Contains(first)), "project-only improvement save");
            Assert(SingleProjectContinuousImprovementStore.Read(second)?.Enabled != true &&
                !Property<CheckBox>(secondRow, "Loop").IsChecked.GetValueOrDefault() && loop.IsChecked == true &&
                !Property<TextBlock>(row, "State").Text.Contains("∞"),
                "enabling the checkbox affects only its project without a duplicate status indicator");
            var until = DateTimeOffset.Now.AddHours(1);
            await (Task<bool>)Call(window, "ApplyProjectImprovementOptionsAsync", first, true, until)!;
            Assert(loop.ToolTip.ToString()!.Contains(until.LocalDateTime.ToString("MM/dd HH:mm")) &&
                Property<MenuItem>(row, "Until").Header.ToString()!.Contains(until.LocalDateTime.ToString("MM/dd HH:mm")) &&
                SingleProjectContinuousImprovementStore.Read(first)?.Until == until,
                "the saved project deadline appears in the checkbox tooltip and context menu");
            loop.IsChecked = false;
            await Until(() => Task.FromResult(SingleProjectContinuousImprovementStore.Read(first)?.Enabled == false &&
                !Field<HashSet<string>>(window, "_projectTaskOptionWrites").Contains(first)), "project improvement disable");
            Assert(loop.IsChecked == false && !Property<Button>(row, "Select").ContextMenu.IsOpen,
                "unchecking saves improvement off without opening a settings window");
            taskSurface.Measure(new Size(820, 480));
            taskSurface.Arrange(new System.Windows.Rect(0, 0, 820, 480));
            taskSurface.UpdateLayout();
            Assert(loop.ActualWidth > 0 && loop.ActualWidth + loop.Margin.Left + loop.Margin.Right + 0.5 >= loop.DesiredSize.Width && loop.TranslatePoint(new Point(loop.ActualWidth, loop.ActualHeight), card).X <= card.ActualWidth &&
                loop.TranslatePoint(new Point(loop.ActualWidth, loop.ActualHeight), card).Y <= card.ActualHeight,
                "the improvement checkbox fits inside the compact card at minimum window size");
            sidebarColumn.Width = new GridLength(200);
            taskSurface.Measure(new Size(820, 480));
            taskSurface.Arrange(new System.Windows.Rect(0, 0, 820, 480));
            taskSurface.UpdateLayout();
            var folderButton = Property<Button>(row, "Folder");
            var rowTitle = Property<TextBlock>(row, "Title");
            var rowControls = new FrameworkElement[]
            {
                action, folderButton, Property<CheckBox>(row, "Loop")
            };
            Assert(folderButton.ActualWidth >= 24 && folderButton.IsEnabled &&
                folderButton.TranslatePoint(new Point(folderButton.ActualWidth, 0), card).X <= rowTitle.TranslatePoint(new Point(0, 0), card).X &&
                folderButton.TranslatePoint(new Point(folderButton.ActualWidth, 0), card).X <= card.ActualWidth &&
                rowControls.All(control => Math.Abs(control.TranslatePoint(new Point(control.ActualWidth / 2, control.ActualHeight / 2), card).Y -
                    rowTitle.TranslatePoint(new Point(0, rowTitle.ActualHeight / 2), card).Y) <= 1) &&
                System.Windows.Automation.AutomationProperties.GetName(folderButton) == "프로젝트 폴더 열기" &&
                folderButton.ToolTip.ToString()!.Contains(first) && Field<ScrollViewer>(window, "_taskListScroll").ScrollableWidth == 0,
                "the folder precedes the title and all project actions remain reachable on one row at minimum sidebar width");
            string folderTarget = Path.Combine(fixture, "폴더 & space");
            Directory.CreateDirectory(folderTarget);
            System.Diagnostics.ProcessStartInfo? folderLaunch = null;
            var folderProbe = (Button)Call(window, "CreateProjectFolderButton", folderTarget,
                (Action<System.Diagnostics.ProcessStartInfo>)(info => folderLaunch = info))!;
            string selectedBeforeFolder = Field<string>(window, "_selectedProjectPath");
            var slotsBeforeFolder = layout.Slots.ToArray();
            folderProbe.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Assert(folderLaunch is { FileName: "explorer.exe", UseShellExecute: false } &&
                folderLaunch.ArgumentList.SequenceEqual(new[] { folderTarget }) &&
                Field<string>(window, "_selectedProjectPath") == selectedBeforeFolder && layout.Slots.SequenceEqual(slotsBeforeFolder),
                "folder clicks target their own project with spaces and special characters without selecting or moving terminals");
            folderLaunch = null;
            var missingFolder = (Button)Call(window, "CreateProjectFolderButton", Path.Combine(fixture, "missing-folder"),
                (Action<System.Diagnostics.ProcessStartInfo>)(info => folderLaunch = info))!;
            missingFolder.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Assert(folderLaunch is null && ((TextBlock)window.FindName("StatusLabel")).Text.Contains("찾을 수 없습니다"),
                "a missing project folder reports an error without launching Explorer");
            var preview = new System.Windows.Media.Imaging.RenderTargetBitmap((int)Math.Ceiling(card.ActualWidth),
                (int)Math.Ceiling(card.ActualHeight), 96, 96, System.Windows.Media.PixelFormats.Pbgra32);
            preview.Render(card);
            var previewEncoder = new System.Windows.Media.Imaging.PngBitmapEncoder();
            previewEncoder.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(preview));
            using (var stream = File.Create(Path.Combine(fixture, "compact-task-card.png"))) previewEncoder.Save(stream);
            SetPrivate(window, "_workspace", fixture);
            Call(window, "SetTerminalLayout", 8);
            Call(window, "AssignProjectToSlot", first, 0);
            Call(window, "SetTerminalLayout", 1);
            Field<RadioButton>(window, "_archivedTasksTab").IsChecked = true;
            await Until(() => Task.FromResult((bool)Call(window, "CanCreateNewProject")!), "ready to create a project");
            var fullSlots = layout.Slots.ToArray();
            var existingTabs = tabs.Items.Cast<TabItem>().ToArray();
            string freshFull = Path.Combine(fixture, "added-full");
            Call(window, "CreateNewProject", "added-full");
            string freshPlan=File.ReadAllText(Path.Combine(freshFull,"terminal-notes","plan.md"));
            Assert(freshPlan.Contains("## 목표 (필수)",StringComparison.Ordinal)&&
                freshPlan.Contains("## 요구사항 (권장)",StringComparison.Ordinal)&&
                freshPlan.Contains("## 제약 사항 (권장)",StringComparison.Ordinal)&&
                freshPlan.Contains("## 완료 기준 (권장)",StringComparison.Ordinal)&&
                freshPlan.Contains("## 참고 자료 (권장)",StringComparison.Ordinal)&&
                Field<string>(window, "_selectedProjectPath") == freshFull && taskRows.Contains(freshFull) &&
                !Field<bool>(window, "_showArchivedTasks") && layout.Slots.SequenceEqual(fullSlots) &&
                tabs.Items.Cast<TabItem>().SequenceEqual(existingTabs) && Field<int>(window, "_splitCapacity") == 1 &&
                views[first].Visibility == Visibility.Visible && ReferenceEquals(Pane(firstTab), panes[0]),
                "creating a project in a full single layout keeps the existing terminal visible and selects the new task in the normal list");
            await Until(() => Task.FromResult((bool)Call(window, "CanCreateNewProject")!), "new project plan loaded");
            Call(window, "CreateNewProject", "added-full");
            Assert(layout.Slots.SequenceEqual(fullSlots) && tabs.Items.Cast<TabItem>().SequenceEqual(existingTabs),
                "duplicate project creation leaves placements and terminal instances unchanged");
            Call(window, "SelectProject", freshFull);
            Assert(layout.Slots.SequenceEqual(fullSlots) && tabs.Items.Cast<TabItem>().SequenceEqual(existingTabs),
                "loading an unassigned existing project also preserves a full layout");
            Call(window, "SetTerminalLayout", 2);
            Call(window, "SelectProject", second);
            Assert(layout.Slots.SequenceEqual(fullSlots) && views[second].Visibility == Visibility.Hidden,
                "loading a hidden project preserves its assigned slot even when a visible slot is empty");
            await Until(() => Task.FromResult((bool)Call(window, "CanCreateNewProject")!), "ready to create in an empty slot");
            string freshEmpty = Path.Combine(fixture, "added-empty");
            Call(window, "CreateNewProject", "added-empty");
            Assert(layout.IndexOf(first) == 0 && layout.IndexOf(freshEmpty) == 1 && layout.IndexOf(second) == 7 &&
                tabs.Items.Count == existingTabs.Length + 1 && ReferenceEquals(Pane(firstTab), panes[0]) &&
                ReferenceEquals(Pane(secondTab), panes[1]) && views[first].Visibility == Visibility.Visible,
                "a new project uses the next empty visible slot and preserves all existing terminal identities");
            var freshTab = tabs.Items.Cast<TabItem>().Single(tab => Pane(tab).WorkingDirectory == freshEmpty);
            Call(window, "SelectProject", freshEmpty);
            Assert(tabs.Items.Count == existingTabs.Length + 1 && tabs.Items.Contains(freshTab) && layout.IndexOf(freshEmpty) == 1,
                "selecting an already visible project reuses its terminal and position");
            Call(window, "SetTerminalLayout", 4);
            var occupiedPaneIdentities = tabs.Items.Cast<TabItem>().ToDictionary(tab => tab, Pane);
            string targetedNew = Path.Combine(fixture, "created-in-slot-four");
            Call(window, "CreateNewProjectForSlot", "created-in-slot-four", 3);
            Assert(layout.IndexOf(targetedNew) == 3 && tabs.Items.Count == occupiedPaneIdentities.Count + 1 &&
                occupiedPaneIdentities.All(pair => tabs.Items.Contains(pair.Key) && ReferenceEquals(Pane(pair.Key), pair.Value)) &&
                layout.IndexOf(first) == 0 && layout.IndexOf(freshEmpty) == 1 && layout.IndexOf(second) == 7,
                "creating from an empty slot places the new project in that exact non-first slot and preserves existing pane identities");
            string targetedExisting = Path.Combine(fixture, "opened-in-slot-three");
            Directory.CreateDirectory(targetedExisting);
            Call(window, "SelectProjectForSlot", targetedExisting, 2);
            Assert(layout.IndexOf(targetedExisting) == 2 && tabs.Items.Count == occupiedPaneIdentities.Count + 2 &&
                occupiedPaneIdentities.All(pair => tabs.Items.Contains(pair.Key) && ReferenceEquals(Pane(pair.Key), pair.Value)) &&
                layout.IndexOf(targetedNew) == 3,
                "opening an existing folder from an empty slot assigns that exact target without moving occupied panes");
            Call(window, "SetTerminalLayout", 8);
            Call(window, "ArrangeSingleProjectViews");
            var terminalSlots = Field<List<(Border Surface, TextBlock Label)>>(window, "_terminalSlots");
            string unopenedProject = Path.Combine(fixture, "picker-unopened");
            string archivedProject = Path.Combine(fixture, ProjectArchiveService.FolderName, "picker-archived");
            Directory.CreateDirectory(unopenedProject);
            Directory.CreateDirectory(archivedProject);
            Call(window, "EnsureProjectTaskRow", unopenedProject);
            var archivedPickerTab = AddPickerFixture(archivedProject);
            var tabsBeforePickerChecks = tabs.Items.Cast<TabItem>().ToArray();
            var panesBeforePickerChecks = tabsBeforePickerChecks.ToDictionary(tab => tab, Pane);
            Call(window, "UpdateProjectTaskTree");
            Call(window, "UpdateDispatcher");
            var unopenedProjectButtons = Field<Dictionary<string, Button>>(window, "_unopenedProjectButtons");
            var unopenedButton = unopenedProjectButtons[unopenedProject];
            Assert(Field<System.Windows.Controls.Panel>(window, "DispatcherSessions").Children.Contains(unopenedButton) &&
                tabs.Items.Cast<TabItem>().SequenceEqual(tabsBeforePickerChecks) &&
                panesBeforePickerChecks.All(pair => tabs.Items.Contains(pair.Key) && ReferenceEquals(Pane(pair.Key), pair.Value)),
                "the session picker lists a registered project without creating or replacing a terminal tab");
            var existingSessionButtons = Field<Dictionary<TabItem, Button>>(window, "_sessionButtons");
            existingSessionButtons[firstTab].RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Assert(tabs.Items.Cast<TabItem>().SequenceEqual(tabsBeforePickerChecks) &&
                panesBeforePickerChecks.All(pair => tabs.Items.Contains(pair.Key) && ReferenceEquals(Pane(pair.Key), pair.Value)) &&
                ReferenceEquals(tabs.SelectedItem, firstTab),
                "choosing an existing session from the picker keeps its tab and pane instance");
            var newlyOpenedUnopened = AddPickerFixture(unopenedProject);
            Call(window, "UpdateDispatcher");
            Assert(!unopenedProjectButtons.ContainsKey(unopenedProject) && existingSessionButtons.ContainsKey(newlyOpenedUnopened),
                "opening a listed project removes its unopened-project choice instead of duplicating it");
            var emptySlotButton = (Button)terminalSlots[4].Surface.Child;
            emptySlotButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            var slotPicker = emptySlotButton.ContextMenu!;
            string[] slotChoices = slotPicker.Items.OfType<MenuItem>().Select(item => item.Header?.ToString() ?? string.Empty).ToArray();
            Assert(!slotChoices.Contains("새 프로젝트 만들기…") && !slotChoices.Contains("프로젝트 폴더 열기…") &&
                slotPicker.Items.Cast<object>().All(item => item is MenuItem { ToolTip: string }) &&
                slotChoices.Contains(Path.GetFileName(freshEmpty)) &&
                System.Windows.Automation.AutomationProperties.GetName(emptySlotButton).Contains("클릭", StringComparison.Ordinal),
                "an empty terminal click shows only known-task choices with an accessible prompt");
            slotPicker.IsOpen = false;
            Assert(layout.Slots[1] == freshEmpty && layout.Slots[2] == targetedExisting &&
                layout.Slots[3] == targetedNew && occupiedPaneIdentities.All(pair => tabs.Items.Contains(pair.Key) && ReferenceEquals(Pane(pair.Key), pair.Value)),
                "dismissing the empty-slot choices leaves every assignment and existing pane unchanged");
            var knownProjectChoice = slotPicker.Items.OfType<MenuItem>().First(item =>
                string.Equals(item.Header?.ToString(), Path.GetFileName(freshEmpty), StringComparison.Ordinal));
            knownProjectChoice.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
            Assert(layout.IndexOf(freshEmpty) == 4 && layout.Slots[1] is null &&
                occupiedPaneIdentities.All(pair => tabs.Items.Contains(pair.Key) && ReferenceEquals(Pane(pair.Key), pair.Value)),
                "choosing a known project in an empty slot uses existing slot movement and preserves its pane");
            Call(window, "AssignProjectToSlot", freshEmpty, 1);
            Assert(layout.IndexOf(first) == 0 && layout.IndexOf(freshEmpty) == 1,
                "existing project placement can be restored without replacing either pane");
            Field<RadioButton>(window, "_normalTasksTab").IsChecked = true;
            var normalViewEmptyButton = (Button)terminalSlots[5].Surface.Child;
            normalViewEmptyButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            var normalViewPicker = normalViewEmptyButton.ContextMenu!;
            string[] normalViewChoices = normalViewPicker.Items.OfType<MenuItem>().Select(item => item.Header?.ToString() ?? string.Empty).ToArray();
            Assert(normalViewChoices.Contains(Path.GetFileName(freshEmpty)) && !normalViewChoices.Contains(Path.GetFileName(archivedProject)),
                "empty terminal choices include normal projects and exclude archived projects while the normal view is selected");
            normalViewPicker.IsOpen = false;
            Field<RadioButton>(window, "_archivedTasksTab").IsChecked = true;
            var archiveViewEmptyButton = (Button)terminalSlots[5].Surface.Child;
            archiveViewEmptyButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            var archiveViewPicker = archiveViewEmptyButton.ContextMenu!;
            string[] archiveViewChoices = archiveViewPicker.Items.OfType<MenuItem>().Select(item => item.Header?.ToString() ?? string.Empty).ToArray();
            Assert(archiveViewChoices.Contains(Path.GetFileName(freshEmpty)) && !archiveViewChoices.Contains(Path.GetFileName(archivedProject)),
                "empty terminal choices include normal projects and exclude archived projects while the archive view is selected");
            archiveViewPicker.IsOpen = false;
            Assert(Call(window, "AssignProjectToSlot", archivedProject, 5) is true &&
                layout.IndexOf(archivedProject) == 5 && tabs.Items.Contains(archivedPickerTab) &&
                ReferenceEquals(Pane(archivedPickerTab), panesBeforePickerChecks[archivedPickerTab]),
                "an archived project remains assignable directly and keeps its existing pane identity");
            layout.Remove(archivedProject);
            foreach (var pickerTab in new[] { archivedPickerTab, newlyOpenedUnopened })
            {
                var pickerPane = Pane(pickerTab);
                await (Task)Call(pickerPane, "StopForTerminalLifecycleAsync")!;
                Call(window, "CloseTerminal", pickerTab, pickerPane);
            }
            Field<RadioButton>(window, "_normalTasksTab").IsChecked = true;
            Call(window, "SetTerminalLayout", 8);
            for (int slot = 0; slot < 8; slot++)
            {
                if (layout.Slots[slot] is not null) continue;
                string project = Path.Combine(fixture, "occupied-" + slot);
                Directory.CreateDirectory(project);
                AddFixture(project);
                Call(window, "AssignProjectToSlot", project, slot);
            }
            await Until(() => Task.FromResult((bool)Call(window, "CanCreateNewProject")!), "ready with eight occupied terminals");
            fullSlots = layout.Slots.ToArray();
            existingTabs = tabs.Items.Cast<TabItem>().ToArray();
            Assert(!(bool)Call(window, "EnsureStartedProjectSlotVisible", Path.Combine(fixture, "ninth-task"))! &&
                layout.Slots.SequenceEqual(fullSlots) && Field<int>(window, "_splitCapacity") == 8 &&
                ((TextBlock)window.FindName("StatusLabel")).Text.Contains("8칸이 모두 사용 중입니다"),
                "starting a ninth task reports full capacity and preserves all eight slot assignments");
            Call(window, "CreateNewProject", "ninth-task");
            Assert(layout.Slots.SequenceEqual(fullSlots) && tabs.Items.Cast<TabItem>().SequenceEqual(existingTabs) &&
                Field<int>(window, "_splitCapacity") == 8 && existingTabs.All(tab => views[Pane(tab).WorkingDirectory].Visibility == Visibility.Visible) &&
                taskRows.Contains(Path.Combine(fixture, "ninth-task")),
                "adding a ninth task leaves all eight terminals and their visible positions intact");
            await Until(() => Task.FromResult((bool)Call(window, "CanCreateNewProject")!), "ninth task plan loaded");
            Call(window, "CloseAllTerminals", false);
            foreach (string placed in layout.Slots.Where(project => project is not null).Cast<string>().ToArray()) layout.Remove(placed);
            string closeOne = Path.Combine(fixture, "close-one"), closeTwo = Path.Combine(fixture, "close-two"), closeHidden = Path.Combine(fixture, "close-hidden");
            foreach (string project in new[] { closeOne, closeTwo, closeHidden }) Directory.CreateDirectory(project);
            Call(window, "SetTerminalLayout", 8);
            var closeOneTab = AddFixture(closeOne);
            var closeTwoTab = AddFixture(closeTwo);
            var closeHiddenTab = AddFixture(closeHidden);
            Call(window, "AssignProjectToSlot", closeOne, 0);
            Call(window, "AssignProjectToSlot", closeTwo, 1);
            Call(window, "AssignProjectToSlot", closeHidden, 7);
            Call(window, "CloseTerminal", closeTwoTab, Pane(closeTwoTab));
            Assert(Field<int>(window, "_splitCapacity") == 2 && layout.Slots[0] == closeOne && layout.Slots[1] == closeHidden &&
                ReferenceEquals(Pane(closeHiddenTab), panes[^1]) && tabs.Items.Contains(closeHiddenTab),
                "closing one of three placed terminals compacts the hidden survivor and shrinks to two without replacing its pane");
            Call(window, "CloseTerminal", closeOneTab, Pane(closeOneTab));
            Assert(Field<int>(window, "_splitCapacity") == 1 && layout.Slots[0] == closeHidden &&
                ReferenceEquals(Pane(closeHiddenTab), panes[^1]) && tabs.Items.Contains(closeHiddenTab),
                "closing down to one placed terminal shrinks the task workspace while preserving its live pane");
            Call(window, "CloseAllTerminals", false);
            Assert(tabs.Items.Count == 0 && Field<int>(window, "_splitCapacity") == 1 && layout.Slots.All(project => project is null),
                "closing all task terminals leaves an empty one-pane workspace and no stale placements");
            Call(window, "CloseAllTerminals", false);
            Assert(Field<int>(window, "_splitCapacity") == 1,
                "closing all when no terminals remain keeps the empty workspace at its minimum capacity");
            Assert(!window.IsVisible && !window.ShowInTaskbar, "targeted UI checks do not display or activate a window");
        }
        finally
        {
            Call(window, "DisposeResources");
            window.Close();
        foreach (var pane in panes) pane.Dispose();
        }
        var positions = new TerminalSlotLayout();
        positions.SetCapacity(8);
        for (int i = 0; i < 8; i++) positions.Assign("project-" + i, i);
        positions.Assign("PROJECT-0", 7);
        Assert(positions.IndexOf("project-0") == 7 && positions.IndexOf("project-7") == 0,
            "slot identity is case insensitive and existing assignments swap");
        positions.Assign("replacement", 3);
        Assert(positions.IndexOf("project-3") == -1 && positions.IndexOf("replacement") == 3 &&
            positions.IndexOf("project-4") == 4, "a new assignment replaces only its target position");
        positions.Remove("replacement");
        Assert(positions.Slots[3] is null && positions.IndexOf("project-4") == 4, "removal leaves a stable empty position");
        positions.SetCapacity(2);
        positions.EnsureVisible("project-0");
        Assert(positions.IndexOf("project-0") == 0 && positions.IndexOf("project-7") == 7,
            "revealing a hidden task swaps it into a visible slot");
        positions.SetCapacity(8);
        positions.Rename("project-0", "renamed");
        Assert(positions.IndexOf("renamed") == 0, "renaming a project retains its slot");
    }

    private static void RunTaskCategorySmoke(MainWindow window, string first, string second,
        TabItem firstTab, TabItem secondTab, TreeViewItem firstNode, TreeViewItem secondNode)
    {
        var catalog = Field<ProjectCategoryCatalog>(window, "_projectCategoryCatalog");
        string workspace = Field<string>(window, "_workspace");
        var tree = Field<TreeView>(window, "_taskTree");
        var layout = Field<TerminalSlotLayout>(window, "_terminalSlotLayout");
        var originalSlots = layout.Slots.ToArray();
        var firstPane = Pane(firstTab);
        var secondPane = Pane(secondTab);
        var input = Field<TextBox>(window, "_projectInputEditor");
        string inputBefore = input.Text;
        var document = Field<TextBox>(window, "_tabDocumentEditor");
        string planBefore = document.Text;
        TreeViewItem Group(string name) => (TreeViewItem)Call(window, "GetTaskCategoryGroup", name)!;
        string[] Projects(TreeViewItem group) => group.Items.Cast<TreeViewItem>().Select(node => (string)node.Tag).ToArray();
        void DropProject(string project, TreeViewItem target)
        {
            var data = new DataObject("TerminalNotes.ProjectSlot", project);
            var args = (DragEventArgs)Activator.CreateInstance(typeof(DragEventArgs),
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic, null,
                new object[] { data, DragDropKeyStates.LeftMouseButton, DragDropEffects.Move, target, new Point(4, 4) }, null)!;
            args.RoutedEvent = DragDrop.DropEvent;
            target.RaiseEvent(args);
            Assert(args.Handled && args.Effects == DragDropEffects.Move, "category header accepts the task drag payload");
        }

        Assert((bool)Call(window, "AddTaskCategory", "Zebra")! && (bool)Call(window, "AddTaskCategory", "Alpha")! &&
            !(bool)Call(window, "AddTaskCategory", "alpha")! && !(bool)Call(window, "AddTaskCategory", "  ")!,
            "users can create distinct categories and invalid or duplicate names are rejected");
        Assert(tree.Items.Cast<TreeViewItem>().Where(group => group.Tag is string).Select(group => (string)group.Tag).SequenceEqual(new[] { "Alpha", "Zebra", "미분류" }) &&
            Group("Alpha").Items.Count == 0 && Group("Alpha").AllowDrop,
            "empty user categories remain alphabetically sorted drop targets");
        firstNode.IsSelected = true;
        DropProject(first, Group("Alpha"));
        Assert((bool)Call(window, "AssignProjectToCategory", second, "Alpha")! &&
            Projects(Group("Alpha")).SequenceEqual(new[] { first, second }) && Group("미분류").Items.Count == 0,
            "drag and menu assignment move the original task nodes into the selected category in name order");
        Call(window, "UpdateProjectTaskActivity");
        Assert(catalog.GetCategory(workspace, first) == "Alpha" && catalog.GetCategory(workspace, second) == "Alpha" &&
            Group("Alpha").Items[0] == firstNode && Group("Alpha").Items[1] == secondNode && firstNode.IsSelected,
            "activity refresh preserves manual categories, selection and row identity for starting tasks");

        Call(window, "BeginProjectTitleEdit", first);
        var editor = Field<TextBox>(window, "_titleEditor");
        editor.Text = "zz renamed task";
        Call(window, "UpdateProjectTaskActivity");
        Assert(ReferenceEquals(Field<TextBox>(window, "_titleEditor"), editor) && editor.Text == "zz renamed task",
            "category refresh preserves the active title editor and its draft");
        Call(window, "EndTitleEdit", true);
        Assert(Projects(Group("Alpha")).SequenceEqual(new[] { second, first }),
            "committing a task display name updates alphabetical order");
        Call(window, "RememberProjectName", first, Path.GetFileName(first));
        Call(window, "UpdateProjectTaskTree");

        Assert((bool)Call(window, "RenameTaskCategory", "Alpha", "Beta")! &&
            catalog.GetCategory(workspace, first) == "Beta" && Projects(Group("Beta")).SequenceEqual(new[] { first, second }),
            "renaming a category keeps its task nodes and assignments");
        Assert((bool)Call(window, "RenameTaskCategory", "Beta", "BETA")! && (string)Group("BETA").Tag == "BETA",
            "case-only category rename updates the visible group identity");
        DropProject(first, Group("Zebra"));
        DropProject(first, Group("BETA"));
        Assert(catalog.GetCategory(workspace, first) == "BETA" && Group("BETA").Items.Contains(firstNode),
            "a renamed category still accepts drops without stale destination names");
        using (var settings = JsonDocument.Parse(File.ReadAllText(Field<string>(window, "_settingsPath"))))
        {
            var restored = new ProjectCategoryCatalog(settings.RootElement.GetProperty("ProjectTaskCategories")
                .Deserialize<ProjectCategoryWorkspaceState[]>());
            Assert(restored.GetCategory(workspace, first) == "BETA" &&
                restored.GetCategories(workspace).SequenceEqual(new[] { "BETA", "Zebra" }),
                "saved app settings restore category definitions and task assignments");
        }
        DropProject(first, Group("미분류"));
        Assert(catalog.GetCategory(workspace, first) is null && Group("미분류").Items.Contains(firstNode),
            "dropping onto the default category clears the assignment");
        Assert((bool)Call(window, "RemoveTaskCategory", "BETA")! && catalog.GetCategory(workspace, second) is null &&
            TaskOrder(window).SequenceEqual(new[] { first, second }),
            "deleting a category returns its tasks to alphabetical unassigned order");
        Assert((bool)Call(window, "RemoveTaskCategory", "Zebra")!, "the remaining empty category can be deleted");
        Assert(layout.Slots.SequenceEqual(originalSlots), "category operations preserve terminal slots");
        Assert(ReferenceEquals(Pane(firstTab), firstPane) && ReferenceEquals(Pane(secondTab), secondPane),
            "category operations preserve pane identity");
        Assert(document.Text == planBefore, "category operations preserve the displayed plan draft");
        Assert(input.Text == inputBefore, "category operations preserve unsent input");
    }

    private static async Task CaptureDesignPreview(MainWindow window, string fixture, string outputPath, List<TerminalPane> panes)
    {
        // Constructor initializes the WPF chrome. Skip normal Loaded startup: even
        // autoStartCodex:false can create a PowerShell pane for an initial workspace.
        typeof(MainWindow).GetField("_windowInitialized", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(window, true);
        typeof(MainWindow).GetField("_workspace", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(window, fixture);
        string[] projects = [Path.Combine(fixture, "01 - Planning"), Path.Combine(fixture, "02 - Review and delivery")];
        foreach (string project in projects) Directory.CreateDirectory(project);
        int width = int.TryParse(Environment.GetEnvironmentVariable("TERMINALNOTES_UI_DESIGN_WIDTH"), out int requestedWidth)
            ? Math.Max(820, requestedWidth) : 1480;
        int capacity = int.TryParse(Environment.GetEnvironmentVariable("TERMINALNOTES_UI_DESIGN_LAYOUT"), out int requestedCapacity) &&
            new[] { 1, 2, 3, 4, 6, 8 }.Contains(requestedCapacity) ? requestedCapacity : 2;
        if (double.TryParse(Environment.GetEnvironmentVariable("TERMINALNOTES_UI_DESIGN_SIDEBAR_WIDTH"), out double sidebarWidth))
            Call(window, "LoadTaskSidebarSettings", false, sidebarWidth);
        if (Environment.GetEnvironmentVariable("TERMINALNOTES_UI_DESIGN_EMPTY") == "1")
        {
            Call(window, "SetTerminalLayout", capacity);
            Call(window, "ShowProjectTaskList");
            window.Width = width;
            window.Height = 940;
            window.Show();
            await window.Dispatcher.InvokeAsync(window.UpdateLayout, System.Windows.Threading.DispatcherPriority.ApplicationIdle);
            var terminalSlots = Field<List<(Border Surface, TextBlock Label)>>(window, "_terminalSlots");
            var firstEmptySlot = (Button)terminalSlots[0].Surface.Child;
            Assert(terminalSlots[0].Label.Text == "태스크 선택\n클릭해서 목록에서 선택",
                "an empty slot clearly explains how to select a task");
            firstEmptySlot.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await window.Dispatcher.InvokeAsync(window.UpdateLayout, System.Windows.Threading.DispatcherPriority.ApplicationIdle);
            Directory.CreateDirectory(Path.GetDirectoryName(outputPath)!);
            SaveClientAreaPreview(window, outputPath);
            Console.WriteLine("Empty terminal preview: " + outputPath);
            if (firstEmptySlot.ContextMenu is { } emptyPicker)
            {
                emptyPicker.IsOpen = false;
                firstEmptySlot.ContextMenu = null;
            }
            await window.Dispatcher.InvokeAsync(() => { }, System.Windows.Threading.DispatcherPriority.ApplicationIdle);
            return;
        }
        Call(window, "SetTerminalLayout", capacity);
        Call(window, "SelectTaskProject", projects[0], true);
        var document = Field<TextBox>(window, "_tabDocumentEditor");
        var input = Field<TextBox>(window, "_projectInputEditor");
        await Until(() => Task.FromResult(document.IsEnabled), "design preview plan editor");
        document.Text = "# Release preparation\n\n## This task\n- Simplify the settings flow\n- Preserve saving and the current input draft\n\n## Next\nReview the changes, then run the tests.";
        input.Text = "Keep saving intact and reduce the spacing a little.";
        var tabs = (TabControl)window.FindName("TerminalTabs");
        for (int index = 0; index < projects.Length; index++)
        {
            string project = projects[index], title = index == 0 ? "Planning" : "Review and delivery";
            Call(window, "EnsureProjectTaskRow", project);
            var pane = new TerminalPane(project, startupCommand: "opencode");
            panes.Add(pane);
            Type stateType = typeof(MainWindow).GetNestedType("TerminalTabState", BindingFlags.NonPublic)!;
            object state = Activator.CreateInstance(stateType)!;
            stateType.GetProperty("Pane")!.SetValue(state, pane);
            stateType.GetProperty("Title")!.SetValue(state, title);
            stateType.GetProperty("Sequence")!.SetValue(state, index + 1);
            stateType.GetProperty("PlanDocumentPath")!.SetValue(state, Path.Combine(project, "terminal-notes", "plan.md"));
            var header = new StackPanel();
            header.Children.Add(new TextBlock { Text = title });
            var tab = new TabItem { Tag = state, Header = header };
            Call(window, "AttachSingleProjectTerminal", project, tab);
            tabs.Items.Add(tab);
            Field<Dictionary<TabItem, ContentControl>>(window, "_taskTerminalHosts")[tab].Content = new TextBlock
            {
                Text = "$ opencode\n\n  Preparing session...\n  > Check plan.md\n  > Review changes\n\n  Enter the next task",
                FontFamily = new System.Windows.Media.FontFamily("Cascadia Mono, Consolas, Malgun Gothic"), FontSize = 14,
                Foreground = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(194, 199, 208)),
                Background = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(16, 18, 22)), Margin = new Thickness(0)
            };
            pane.Dispose();
        }
        Call(window, "AssignProjectToSlot", projects[0], 0);
        Call(window, "AssignProjectToSlot", projects[1], 1);
        Call(window, "SelectTerminal", tabs.Items[0], false);
        Call(window, "SelectTaskProject", projects[0], true);
        Call(window, "UpdateProjectTaskActivity");
        Call(window, "SetStatus", "화면 미리보기 · 터미널 예시");
        await Until(() => Task.FromResult(document.IsEnabled), "design preview selected plan");
        document.Text = "# Release preparation\n\n## This task\n- Simplify the settings flow\n- Preserve saving and the current input draft\n\n## Next\nReview the changes, then run the tests.";
        input.Text = "Keep saving intact and reduce the spacing a little.";
        window.Width = width; window.Height = 940; window.Show();
        await window.Dispatcher.InvokeAsync(window.UpdateLayout, System.Windows.Threading.DispatcherPriority.ApplicationIdle);
        Directory.CreateDirectory(Path.GetDirectoryName(outputPath)!);
        SaveClientAreaPreview(window, outputPath);
        Console.WriteLine("Design preview: " + outputPath);
    }

    private static void SaveClientAreaPreview(Window window, string outputPath)
    {
        if (window.Content is not FrameworkElement clientRoot)
            throw new InvalidOperationException("The design preview window has no WPF client root.");
        clientRoot.UpdateLayout();
        int width = (int)Math.Ceiling(clientRoot.ActualWidth);
        int height = (int)Math.Ceiling(clientRoot.ActualHeight);
        if (width <= 0 || height <= 0)
            throw new InvalidOperationException("The design preview client area has no rendered size.");
        var bitmap = new System.Windows.Media.Imaging.RenderTargetBitmap(width, height, 96, 96,
            System.Windows.Media.PixelFormats.Pbgra32);
        bitmap.Render(clientRoot);
        var encoder = new System.Windows.Media.Imaging.PngBitmapEncoder();
        encoder.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(bitmap));
        using var stream = File.Create(outputPath);
        encoder.Save(stream);
    }

    private static string TrimDiagnostic(string value,int maximum)=>value.Length<=maximum?value:value[^maximum..];
    private static IEnumerable<T> Descendants<T>(DependencyObject root) where T:DependencyObject
    {
        for(int i=0;i<System.Windows.Media.VisualTreeHelper.GetChildrenCount(root);i++)
        {
            var child=System.Windows.Media.VisualTreeHelper.GetChild(root,i);
            if(child is T match) yield return match;
            foreach(var nested in Descendants<T>(child)) yield return nested;
        }
    }
    private static bool IsInsideWindowWidth(Window window,FrameworkElement element)
    {
        var point=element.TranslatePoint(new Point(0,0),window);
        return point.X>=0 && point.X+element.ActualWidth<=window.ActualWidth;
    }
    [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
    private struct Rect {public int Left,Top,Right,Bottom;}
    [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential, CharSet=System.Runtime.InteropServices.CharSet.Auto)]
    private struct MonitorInfo
    {
        public int Size;
        public Rect Monitor;
        public Rect Work;
        public uint Flags;
    }
    private static (Rect Window,Rect Visible,Rect Work) GetWindowWorkAreaBounds(Window window)
    {
        var handle=new WindowInteropHelper(window).Handle;
        if(handle==IntPtr.Zero || !GetWindowRect(handle,out var bounds))
            throw new InvalidOperationException("Could not read native window bounds.");
        if(DwmGetWindowAttribute(handle,9,out var visible,System.Runtime.InteropServices.Marshal.SizeOf<Rect>())!=0)
            throw new InvalidOperationException("Could not read visible DWM frame bounds.");
        var monitor=MonitorFromWindow(handle,2); // MONITOR_DEFAULTTONEAREST
        var info=new MonitorInfo { Size=System.Runtime.InteropServices.Marshal.SizeOf<MonitorInfo>() };
        if(monitor==IntPtr.Zero || !GetMonitorInfo(monitor,ref info))
            throw new InvalidOperationException("Could not read the current monitor work area.");
        return (bounds,visible,info.Work);
    }
    private static bool RectFitsWorkArea(Rect rect,Rect work,int tolerance)=>
        rect.Left>=work.Left-tolerance && rect.Top>=work.Top-tolerance &&
        rect.Right<=work.Right+tolerance && rect.Bottom<=work.Bottom+tolerance;
    private static bool RectMatchesWorkArea(Rect rect,Rect work,int tolerance)=>
        Math.Abs(rect.Left-work.Left)<=tolerance && Math.Abs(rect.Top-work.Top)<=tolerance &&
        Math.Abs(rect.Right-work.Right)<=tolerance && Math.Abs(rect.Bottom-work.Bottom)<=tolerance;
    private static bool MaximizedBoundsMatchWorkArea(Window window)
    {
        try {var bounds=GetWindowWorkAreaBounds(window);return window.WindowState==WindowState.Maximized && RectMatchesWorkArea(GetWindowContentBounds(window),bounds.Work,1);}
        catch {return false;}
    }
    private static Rect GetWindowContentBounds(Window window)
    {
        var content=(FrameworkElement)window.Content;
        var topLeft=content.PointToScreen(new Point(0,0));
        var bottomRight=content.PointToScreen(new Point(content.ActualWidth,content.ActualHeight));
        return new Rect { Left=(int)Math.Round(topLeft.X), Top=(int)Math.Round(topLeft.Y),
            Right=(int)Math.Round(bottomRight.X), Bottom=(int)Math.Round(bottomRight.Y) };
    }
    private static bool RestoredBoundsMatch(Window window,Rect startup)
    {
        try {return RestoredBoundsMatch(GetWindowWorkAreaBounds(window),startup);}
        catch {return false;}
    }
    private static bool RestoredBoundsMatch((Rect Window,Rect Visible,Rect Work) bounds,Rect startup)=>
        Math.Abs(RectWidth(bounds.Window)-RectWidth(startup))<=1 && Math.Abs(RectHeight(bounds.Window)-RectHeight(startup))<=1;
    private static int RectWidth(Rect rect)=>rect.Right-rect.Left;
    private static int RectHeight(Rect rect)=>rect.Bottom-rect.Top;
    private static string FormatRect(Rect rect)=>$"[{rect.Left},{rect.Top},{rect.Right},{rect.Bottom}]";
    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern IntPtr MonitorFromWindow(IntPtr handle,uint flags);
    [System.Runtime.InteropServices.DllImport("user32.dll",CharSet=System.Runtime.InteropServices.CharSet.Auto,SetLastError=true)]
    [return:System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.Bool)]
    private static extern bool GetMonitorInfo(IntPtr monitor,ref MonitorInfo info);
    [System.Runtime.InteropServices.DllImport("dwmapi.dll",PreserveSig=true)]
    private static extern int DwmGetWindowAttribute(IntPtr handle,uint attribute,out Rect value,int size);
    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern bool GetWindowRect(IntPtr handle,out Rect rect);
    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern bool PrintWindow(IntPtr handle,IntPtr dc,uint flags);
}
