using TerminalNotes.Services;

internal static class Program
{
    private static int Main()
    {
        try
        {
            VerifyEvidenceBasedTransitions();
            VerifyTerminalStates();
            VerifyRenderedEvidence();
            VerifyStartupIdle();
            VerifyCompletedWorkEvidence();
            VerifyWorkStaysActiveUntilEvidence();
            VerifyTuiCompletionEvidence();
            Console.WriteLine("PASS: terminal activity classification is evidence-based and deterministic.");
            return 0;
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine("FAIL: " + exception);
            return 1;
        }
    }

    private static void VerifyEvidenceBasedTransitions()
    {
        var clock = new TestTimeProvider();
        var tracker = new TerminalActivityTracker(
            clock,
            activeWindow: TimeSpan.FromSeconds(1),
            startupWindow: TimeSpan.FromSeconds(3));
        int changes = 0;
        tracker.ActivityChanged += () => changes++;

        Expect(tracker, TerminalActivity.Starting, "시작 중");
        clock.Advance(TimeSpan.FromSeconds(4));
        tracker.Tick();
        Expect(tracker, TerminalActivity.Quiet, "대기 중 · 출력 없음");

        tracker.ObserveOutput("building");
        Expect(tracker, TerminalActivity.Active, "진행 중");
        clock.Advance(TimeSpan.FromSeconds(2));
        tracker.Tick();
        Expect(tracker, TerminalActivity.Quiet, "대기 중 · 출력 없음");

        tracker.ObserveRenderedTail("PS C:\\work>\ncompiling\nstill running");
        Expect(tracker, TerminalActivity.Quiet, "대기 중 · 출력 없음");
        tracker.ObserveRenderedTail("compiling\nPS C:\\work> ");
        Expect(tracker, TerminalActivity.Idle, "대기 중");

        tracker.ObserveUserInput("x");
        Expect(tracker, TerminalActivity.Quiet, "대기 중 · 출력 없음");
        tracker.ObserveRenderedTail("\u001b[33mDo you want to proceed?\u001b[0m\n[y/N]");
        Expect(tracker, TerminalActivity.NeedsInput, "확인 필요");

        tracker.ObserveUserInput("n");
        tracker.MarkNeedsInput();
        Expect(tracker, TerminalActivity.NeedsInput, "확인 필요 (수동)");
        tracker.ObserveOutput("background output does not clear a manual signal");
        Expect(tracker, TerminalActivity.NeedsInput, "확인 필요 (수동)");
        tracker.ObserveUserInput("x");
        Expect(tracker, TerminalActivity.Active, "진행 중");
        tracker.ObserveUserInput("\r");
        Expect(tracker, TerminalActivity.Active, "진행 중");

        tracker.ObserveOutput("new output");
        tracker.MarkNeedsInput();
        tracker.ClearActivityOverride();
        Expect(tracker, TerminalActivity.Active, "진행 중");
        clock.Advance(TimeSpan.FromSeconds(2));
        tracker.Tick();
        Expect(tracker, TerminalActivity.Active, "진행 중");

        Require(changes >= 10, "ActivityChanged did not report meaningful transitions.");
    }

    private static void VerifyRenderedEvidence()
    {
        var clock = new TestTimeProvider();
        var tracker = new TerminalActivityTracker(clock);
        tracker.ObserveUserInput("run\r");
        tracker.ObserveRenderedScreen("answer", "answer", "80x24");
        Expect(tracker, TerminalActivity.Active, "진행 중");
        clock.Advance(TimeSpan.FromSeconds(2));
        tracker.ObserveRenderedScreen("answer", "answer", "80x24");
        Expect(tracker, TerminalActivity.Active, "진행 중");
        for (int i=0; i<10; i++)
        {
            clock.Advance(TimeSpan.FromMilliseconds(200));
            tracker.ObserveRenderedScreen("answer", "answer", "80x24");
            Expect(tracker, TerminalActivity.Active, "진행 중");
        }
        tracker.ObserveRenderedScreen("cropped", "cropped", "40x12");
        Expect(tracker, TerminalActivity.Active, "진행 중");
        tracker.ObserveRenderedScreen("PS C:\\work> ", "answer\nPS C:\\work> ", "40x12");
        Expect(tracker, TerminalActivity.Idle, "대기 중");
        tracker.ObserveUserInput("\u001b[12;4R");
        tracker.ObserveUserInput("\u001b[?1;2c");
        Expect(tracker, TerminalActivity.Idle, "대기 중");
        tracker.ObserveRenderedScreen("❯", "Working (esc to interrupt)\n❯", "40x12");
        Expect(tracker, TerminalActivity.Active, "진행 중");
        clock.Advance(TimeSpan.FromSeconds(10)); tracker.Tick();
        Expect(tracker, TerminalActivity.Active, "진행 중");
        tracker.ObserveRenderedScreen("❯", "answer finished\n❯", "40x12");
        Expect(tracker, TerminalActivity.Active, "진행 중");
        tracker.ObserveRenderedScreen("›", "answer finished\n›", "40x12");
        Expect(tracker, TerminalActivity.Active, "진행 중");
        tracker.ObserveUserInput("run\r");
        tracker.ObserveRenderedScreen("next output", "next output", "40x12");
        Expect(tracker, TerminalActivity.Active, "진행 중");
        tracker.ObserveRenderedScreen("Which file should I change?\n❯", "Which file should I change?\n❯", "40x12");
        Expect(tracker, TerminalActivity.NeedsInput, "확인 필요");
        tracker.ObserveRenderedScreen("Do you want to proceed?\n[y/N]", "Do you want to proceed?\n[y/N]\nesc to cancel", "40x12");
        Expect(tracker, TerminalActivity.NeedsInput, "확인 필요");
        tracker.ObserveRenderedScreen("You've hit your usage limit\n❯", "You've hit your usage limit\n❯", "40x12");
        Expect(tracker, TerminalActivity.RateLimited, "토큰·사용량 한도");
        clock.Advance(TimeSpan.FromSeconds(20)); tracker.Tick();
        Expect(tracker, TerminalActivity.RateLimited, "토큰·사용량 한도");
        tracker.ObserveRenderedScreen("토큰 한도에 도달했습니다\n❯", "토큰 한도에 도달했습니다\n❯", "40x12");
        Expect(tracker, TerminalActivity.RateLimited, "토큰·사용량 한도");
        tracker.ObserveRenderedScreen("Tokens used: 1200\n❯", "Tokens used: 1200\n❯", "40x12");
        Expect(tracker, TerminalActivity.Active, "진행 중");
    }
    private static void VerifyStartupIdle()
    {
        var clock = new TestTimeProvider();
        var tracker = new TerminalActivityTracker(clock);
        tracker.MarkStarted();
        int greenTransitions = 0;
        tracker.ActivityChanged += () => { if (tracker.Activity == TerminalActivity.Active) greenTransitions++; };
        tracker.ObserveRenderedScreen("Welcome", "Welcome", "80x24");
        Expect(tracker, TerminalActivity.Idle, "대기 중");
        tracker.ObserveRenderedScreen("Shortcut help: esc to interrupt", "Shortcut help: esc to interrupt", "80x24");
        Expect(tracker, TerminalActivity.Idle, "대기 중");
        for (int i=0;i<20;i++)
        {
            clock.Advance(TimeSpan.FromSeconds(1));
            tracker.ObserveRenderedScreen("› Try a suggestion " + i, "Welcome\n› Try a suggestion " + i, "80x24", true);
            Expect(tracker, TerminalActivity.Idle, "대기 중");
        }
        tracker.ObserveUserInput("typing");
        tracker.ObserveRenderedScreen("› typing", "Welcome\n› typing", "80x24");
        Expect(tracker, TerminalActivity.Idle, "대기 중");
        Require(greenTransitions == 0, "Startup banner or typing flashed green.");
        tracker.ObserveUserInput("\r");
        tracker.ObserveRenderedScreen("building", "building", "80x24");
        Expect(tracker, TerminalActivity.Active, "진행 중");
        tracker.ObserveRenderedScreen("› New suggestion", "Done\n› New suggestion", "80x24", true);
        Expect(tracker, TerminalActivity.Active, "진행 중");
        tracker.ObserveRenderedScreen("idle status updated", "idle status updated", "80x24");
        Expect(tracker, TerminalActivity.Active, "진행 중");
        tracker.MarkStarted();
        tracker.ObserveRenderedScreen("Working", "Working (esc to interrupt)", "80x24");
        Expect(tracker, TerminalActivity.Active, "진행 중");
        tracker.MarkStarted(startupWork: true);
        tracker.ObserveRenderedScreen("custom build", "custom build", "80x24");
        Expect(tracker, TerminalActivity.Active, "진행 중");
        Require(!tracker.HasCompletedWork, "Custom startup output was mistaken for completion before the shell returned.");
        tracker.ObserveRenderedScreen(
            "custom build\nPS C:\\work> ",
            "custom build\nPS C:\\work> ",
            "80x24");
        Expect(tracker, TerminalActivity.Idle, "대기 중");
        Require(tracker.HasCompletedWork, "A custom startup command did not complete when its PowerShell prompt returned.");
        tracker.MarkStarted();
        tracker.ObserveRenderedScreen("Allow this command?\n[y/N]", "Allow this command?\n[y/N]", "80x24");
        Expect(tracker, TerminalActivity.NeedsInput, "확인 필요");
        tracker.ObserveRenderedScreen("Usage limit reached", "Usage limit reached", "80x24");
        Expect(tracker, TerminalActivity.RateLimited, "토큰·사용량 한도");
    }

    private static void VerifyTerminalStates()
    {
        var exited = new TerminalActivityTracker();
        exited.MarkExited();
        Expect(exited, TerminalActivity.Exited, "종료됨");
        exited.ObserveOutput("late output");
        Expect(exited, TerminalActivity.Exited, "종료됨");

        var error = new TerminalActivityTracker();
        error.MarkError();
        Expect(error, TerminalActivity.Error, "오류");

        Require(
            !TerminalActivityTracker.LooksLikeInputPrompt("PS C:\\work>\nnew output is still arriving"),
            "A historical prompt was mistaken for the current cursor line.");
        Require(
            !TerminalActivityTracker.LooksLikeInputPrompt("status\nPS C:\\work> "),
            "A current PowerShell prompt was not recognized.");
    }

    private static void VerifyCompletedWorkEvidence()
    {
        var clock = new TestTimeProvider();
        var tracker = new TerminalActivityTracker(clock);
        tracker.MarkStarted();

        tracker.ObserveRenderedScreen("PS C:\\work> ", "PS C:\\work> ", "80x24");
        Require(!tracker.HasCompletedWork, "The initial shell prompt was mistaken for completed work.");

        tracker.ObserveUserInput("Get-Date\r");
        tracker.ObserveRenderedScreen("PS C:\\work> ", "PS C:\\work> ", "80x24");
        Require(!tracker.HasCompletedWork, "An identical redraw was mistaken for a response.");
        tracker.ObserveRenderedScreen("PS C:\\work> ", "PS C:\\work> ", "100x30");
        Require(!tracker.HasCompletedWork, "A resize was mistaken for a response.");
        tracker.ObserveRenderedScreen("Friday", "Get-Date\nFriday", "100x30");
        Require(!tracker.HasCompletedWork, "Output without a returned shell prompt was marked complete.");
        tracker.ObserveRenderedScreen(
            "Get-Date\nFriday\nPS C:\\work> ",
            "Get-Date\nFriday\nPS C:\\work> ",
            "100x30");
        Require(tracker.HasCompletedWork, "A submitted command with output and a returned PowerShell prompt was not marked complete.");

        tracker.ObserveUserInput("n");
        Require(!tracker.HasCompletedWork, "New typing did not clear the completed-work flag.");
        tracker.ObserveUserInput("silent-command\r");
        clock.Advance(TimeSpan.FromSeconds(10));
        tracker.Tick();
        Require(!tracker.HasCompletedWork, "Silence was mistaken for completed work.");

        tracker.ObserveUserInput("\x03");
        tracker.ObserveRenderedScreen("cancelled", "silent-command\n^C", "100x30");
        tracker.ObserveRenderedScreen(
            "^C\nPS C:\\work> ",
            "silent-command\n^C\nPS C:\\work> ",
            "100x30");
        Require(tracker.HasCompletedWork, "Ctrl+C prevented a returned PowerShell prompt from completing the response.");
        tracker.ObserveUserInput("next-command\r");

        tracker.ObserveRenderedScreen("❯", "answer\n❯", "100x30", promptAtCursor: true);
        Require(!tracker.HasCompletedWork, "A persistent TUI input row was mistaken for a returned shell prompt.");

        int completionOnlyChanges = 0;
        var explicitlyCompleted = new TerminalActivityTracker(clock);
        explicitlyCompleted.ObserveRenderedScreen("PS C:\\work> ", "PS C:\\work> ", "80x24");
        explicitlyCompleted.ActivityChanged += () => completionOnlyChanges++;
        explicitlyCompleted.MarkWorkCompleted();
        Require(explicitlyCompleted.HasCompletedWork, "Explicit completion was not retained.");
        Require(completionOnlyChanges == 1, "Explicit completion did not raise ActivityChanged exactly once.");
        explicitlyCompleted.MarkWorkCompleted();
        Require(completionOnlyChanges == 1, "Repeated explicit completion raised a duplicate change.");
        explicitlyCompleted.ObserveRenderedScreen(
            "Working (esc to interrupt)",
            "Working (esc to interrupt)",
            "80x24");
        Require(!explicitlyCompleted.HasCompletedWork, "An explicit busy signal did not clear completion.");

        explicitlyCompleted.MarkWorkCompleted();
        explicitlyCompleted.MarkStarted();
        Require(!explicitlyCompleted.HasCompletedWork, "Starting new work did not reset completion.");

        explicitlyCompleted.MarkWorkCompleted();
        explicitlyCompleted.MarkError();
        Require(!explicitlyCompleted.HasCompletedWork, "An error did not clear completion.");

        var deviceReports = new TerminalActivityTracker(clock);
        deviceReports.MarkWorkCompleted();
        int deviceReportChanges = 0;
        deviceReports.ActivityChanged += () => deviceReportChanges++;
        string[] automaticReplies =
        [
            "\x1b[?1;2c",          // primary device attributes
            "\x1b[>0;276;0c",      // secondary device attributes
            "\x1b[12;40R",         // cursor position report
            "\x1b[0n",             // device status report
            "\x1b[?25;1$y",        // mode report
            "\x1b[4;768;1024t",    // window report
            "\x1b[2;1;1;112;112;1;0x",
            "\x1b[I",              // focus in report
            "\x1b[O"               // focus out report
        ];
        foreach (string reply in automaticReplies)
        {
            deviceReports.ObserveUserInput(reply);
            Require(deviceReports.HasCompletedWork, $"Automatic terminal reply {Escape(reply)} cleared completion.");
        }
        deviceReports.ObserveUserInput("\x1b[?1;2c\x1b[12;40R\x1b[0n");
        Require(deviceReports.HasCompletedWork, "A batch of automatic terminal replies cleared completion.");
        Require(deviceReportChanges == 0, "Automatic terminal replies raised a completion change.");
        deviceReports.ObserveUserInput("\x1b[A");
        Require(!deviceReports.HasCompletedWork, "A user history/navigation key did not clear completion.");

        var exited = new TerminalActivityTracker(clock);
        exited.MarkWorkCompleted();
        exited.MarkExited();
        Require(!exited.HasCompletedWork, "Process exit did not clear completion.");
    }

    private static void VerifyWorkStaysActiveUntilEvidence()
    {
        var clock = new TestTimeProvider();
        var tracker = new TerminalActivityTracker(
            clock,
            activeWindow: TimeSpan.FromSeconds(1),
            startupWindow: TimeSpan.FromSeconds(3));

        tracker.MarkStarted();
        tracker.ObserveRenderedScreen("›", "Welcome\n›", "80x24", promptAtCursor: true);
        Expect(tracker, TerminalActivity.Idle, "대기 중");

        tracker.ObserveUserInput("build\r");
        tracker.ObserveRenderedScreen("Working", "Working", "80x24");
        Expect(tracker, TerminalActivity.Active, "진행 중");

        clock.Advance(TimeSpan.FromMinutes(10));
        tracker.Tick();
        Expect(tracker, TerminalActivity.Active, "진행 중");

        tracker.ObserveRenderedScreen("Working", "Working", "80x24");
        Expect(tracker, TerminalActivity.Active, "진행 중");
        tracker.ObserveRenderedScreen("Working", "Working", "120x40");
        Expect(tracker, TerminalActivity.Active, "진행 중");
        tracker.ObserveRenderedScreen("›", "Still waiting\n›", "120x40", promptAtCursor: true);
        Expect(tracker, TerminalActivity.Active, "진행 중");

        tracker.ObserveRenderedScreen("Which target should I use?\n❯", "Which target should I use?\n❯", "120x40");
        Expect(tracker, TerminalActivity.NeedsInput, "확인 필요");
        clock.Advance(TimeSpan.FromMinutes(10));
        tracker.Tick();
        Expect(tracker, TerminalActivity.NeedsInput, "확인 필요");

        tracker.ObserveUserInput("all");
        Expect(tracker, TerminalActivity.Active, "진행 중");
        clock.Advance(TimeSpan.FromMinutes(10));
        tracker.Tick();
        Expect(tracker, TerminalActivity.Active, "진행 중");

        tracker.MarkWorkCompleted();
        Expect(tracker, TerminalActivity.Idle, "대기 중");
        Require(tracker.HasCompletedWork, "Authoritative completion did not set the completion flag.");
    }

    private static void VerifyTuiCompletionEvidence()
    {
        var clock = new TestTimeProvider();
        var tracker = new TerminalActivityTracker(clock, activeWindow: TimeSpan.FromSeconds(1));
        tracker.MarkStarted();
        tracker.ObserveUserInput("build\r");

        tracker.ObserveRenderedScreen(
            "Working (esc to interrupt)",
            "Working (esc to interrupt)\n›",
            "80x24",
            promptAtCursor: true);
        Expect(tracker, TerminalActivity.Active, "진행 중");
        clock.Advance(TimeSpan.FromMinutes(5));
        tracker.Tick();
        Expect(tracker, TerminalActivity.Active, "진행 중");

        tracker.ObserveRenderedScreen(
            "Working (esc to interrupt)",
            "Working (esc to interrupt)\n›",
            "80x24",
            promptAtCursor: true);
        Expect(tracker, TerminalActivity.Active, "진행 중");

        tracker.ObserveRenderedScreen("Final response\n›", "Final response\n›", "80x24", promptAtCursor: true);
        Expect(tracker, TerminalActivity.Active, "진행 중");
        clock.Advance(TimeSpan.FromMilliseconds(600));
        tracker.ObserveRenderedScreen("Final response\n›", "Final response\n›", "80x24", promptAtCursor: true);
        Expect(tracker, TerminalActivity.Active, "진행 중");
        clock.Advance(TimeSpan.FromMilliseconds(399));
        tracker.Tick();
        Expect(tracker, TerminalActivity.Active, "진행 중");
        clock.Advance(TimeSpan.FromMilliseconds(1));
        tracker.Tick();
        Expect(tracker, TerminalActivity.Idle, "대기 중");
        Require(!tracker.HasCompletedWork, "TUI prompt restoration was incorrectly reported as confirmed success.");

        var noBusyEvidence = new TerminalActivityTracker(clock, activeWindow: TimeSpan.FromSeconds(1));
        noBusyEvidence.MarkStarted();
        noBusyEvidence.ObserveUserInput("build\r");
        noBusyEvidence.ObserveRenderedScreen("Final response\n›", "Final response\n›", "80x24", promptAtCursor: true);
        clock.Advance(TimeSpan.FromMinutes(5));
        noBusyEvidence.Tick();
        Expect(noBusyEvidence, TerminalActivity.Active, "진행 중");
        Require(!noBusyEvidence.HasCompletedWork, "Quiet plus a prompt without observed busy evidence was treated as completion.");

        var outputAfterReturn = new TerminalActivityTracker(clock, activeWindow: TimeSpan.FromSeconds(1));
        outputAfterReturn.MarkStarted();
        outputAfterReturn.ObserveUserInput("build\r");
        outputAfterReturn.ObserveRenderedScreen("Working (esc to interrupt)", "Working (esc to interrupt)\n›", "80x24", promptAtCursor: true);
        outputAfterReturn.ObserveRenderedScreen("Final response\n›", "Final response\n›", "80x24", promptAtCursor: true);
        clock.Advance(TimeSpan.FromMilliseconds(700));
        outputAfterReturn.ObserveRenderedScreen("More final output\n›", "More final output\n›", "80x24", promptAtCursor: true);
        clock.Advance(TimeSpan.FromMilliseconds(700));
        outputAfterReturn.Tick();
        Expect(outputAfterReturn, TerminalActivity.Active, "진행 중");
        clock.Advance(TimeSpan.FromMilliseconds(300));
        outputAfterReturn.Tick();
        Expect(outputAfterReturn, TerminalActivity.Idle, "대기 중");

        var approval = new TerminalActivityTracker(clock, activeWindow: TimeSpan.FromSeconds(1));
        approval.MarkStarted();
        approval.ObserveUserInput("build\r");
        approval.ObserveRenderedScreen("Working (esc to interrupt)", "Working (esc to interrupt)\n›", "80x24", promptAtCursor: true);
        approval.ObserveRenderedScreen("May I edit this file?\n[y/N]", "May I edit this file?\n[y/N]", "80x24");
        Expect(approval, TerminalActivity.NeedsInput, "확인 필요");
        clock.Advance(TimeSpan.FromMinutes(5));
        approval.Tick();
        Expect(approval, TerminalActivity.NeedsInput, "확인 필요");

        var openCode = new TerminalActivityTracker(clock, activeWindow: TimeSpan.FromSeconds(1));
        openCode.MarkStarted();
        openCode.ObserveUserInput("build\r");
        openCode.ObserveRenderedScreen(
            "┃  Ask anything… \"What is the tech stack of this project?\"",
            "┃  Ask anything… \"What is the tech stack of this project?\"\nprogress ▓ esc interrupt",
            "40x15");
        Expect(openCode, TerminalActivity.Active, "진행 중");
        openCode.ObserveRenderedScreen(
            "┃  Ask anything… \"What is the tech stack of this project?\"",
            "┃  Ask anything… \"What is the tech stack of this project?\"\n+p commands",
            "40x15");
        Expect(openCode, TerminalActivity.Active, "진행 중");
        clock.Advance(TimeSpan.FromSeconds(1));
        openCode.Tick();
        Expect(openCode, TerminalActivity.Idle, "대기 중");
        Require(!openCode.HasCompletedWork, "OpenCode idle composer was treated as authoritative success.");

        var openCodeQuestion = new TerminalActivityTracker(clock, activeWindow: TimeSpan.FromSeconds(1));
        openCodeQuestion.MarkStarted();
        openCodeQuestion.ObserveUserInput("build\r");
        openCodeQuestion.ObserveRenderedScreen(
            "Thinking…",
            "Thinking… esc interrupt\n┃ Ask anything…",
            "40x15");
        openCodeQuestion.ObserveRenderedScreen(
            "Which option should I use?\n┃ Ask anything…",
            "Which option should I use?\n┃ Ask anything…\n+p commands",
            "40x15");
        Expect(openCodeQuestion, TerminalActivity.NeedsInput, "확인 필요");
        clock.Advance(TimeSpan.FromMinutes(5));
        openCodeQuestion.Tick();
        Expect(openCodeQuestion, TerminalActivity.NeedsInput, "확인 필요");

        var noBusyFrame = new TerminalActivityTracker(clock, activeWindow: TimeSpan.FromSeconds(1));
        noBusyFrame.MarkStarted();
        noBusyFrame.ObserveRenderedScreen(
            "┃ Ask anything…\nesc interrupt\n+p commands",
            "┃ Ask anything…\nesc interrupt\n+p commands",
            "40x15");
        Expect(noBusyFrame, TerminalActivity.Idle, "대기 중");
        noBusyFrame.ObserveUserInput("What is 2+2?\r");
        noBusyFrame.ObserveRenderedScreen(
            "The answer is 4\n┃ Ask anything…\nesc interrupt\n+p commands",
            "The answer is 4\n┃ Ask anything…\nesc interrupt\n+p commands",
            "40x15");
        Expect(noBusyFrame, TerminalActivity.Active, "진행 중");
        clock.Advance(TimeSpan.FromSeconds(1));
        noBusyFrame.Tick();
        Expect(noBusyFrame, TerminalActivity.Idle, "대기 중");
        Require(!noBusyFrame.HasCompletedWork, "OpenCode composer return without a busy frame was treated as confirmed success.");

        var composerWithoutResponse = new TerminalActivityTracker(clock, activeWindow: TimeSpan.FromSeconds(1));
        composerWithoutResponse.MarkStarted();
        composerWithoutResponse.ObserveRenderedScreen(
            "┃ Ask anything…\n+p commands",
            "┃ Ask anything…\n+p commands",
            "40x15");
        composerWithoutResponse.ObserveUserInput("build\r");
        composerWithoutResponse.ObserveRenderedScreen(
            "┃ Ask anything…\n+p commands",
            "┃ Ask anything…\n+p commands",
            "40x15");
        clock.Advance(TimeSpan.FromMinutes(5));
        composerWithoutResponse.Tick();
        Expect(composerWithoutResponse, TerminalActivity.Active, "진행 중");

        var echoedPrompt = new TerminalActivityTracker(clock, activeWindow: TimeSpan.FromSeconds(1));
        echoedPrompt.MarkStarted();
        echoedPrompt.ObserveRenderedScreen("┃ Ask anything…\n+p commands", "┃ Ask anything…\n+p commands", "40x15");
        echoedPrompt.ObserveUserInput("hello\r");
        echoedPrompt.ObserveRenderedScreen(
            "┃ hello\n┃ Ask anything…\n+p commands",
            "┃ hello\n┃ Ask anything…\n+p commands",
            "40x15");
        clock.Advance(TimeSpan.FromMinutes(5));
        echoedPrompt.Tick();
        Expect(echoedPrompt, TerminalActivity.Active, "진행 중");

        var wrappedEcho = new TerminalActivityTracker(clock, activeWindow: TimeSpan.FromSeconds(1));
        wrappedEcho.MarkStarted();
        wrappedEcho.ObserveRenderedScreen("┃ Ask anything…\n+p commands", "┃ Ask anything…\n+p commands", "40x15");
        wrappedEcho.ObserveUserInput("please make a very long request\r");
        wrappedEcho.ObserveRenderedScreen(
            "┃ please make a very\n┃ long request\n┃ Ask anything…\n+p commands",
            "┃ please make a very\n┃ long request\n┃ Ask anything…\n+p commands",
            "40x15");
        clock.Advance(TimeSpan.FromMinutes(5));
        wrappedEcho.Tick();
        Expect(wrappedEcho, TerminalActivity.Active, "진행 중");

        var resizeOnly = new TerminalActivityTracker(clock, activeWindow: TimeSpan.FromSeconds(1));
        resizeOnly.MarkStarted();
        resizeOnly.ObserveRenderedScreen(
            "Project instructions remain visible\nstable transcript line\n┃ Ask anything…\n+p commands",
            "Project instructions remain visible\nstable transcript line\n┃ Ask anything…\n+p commands",
            "40x15");
        resizeOnly.ObserveUserInput("run\r");
        resizeOnly.ObserveRenderedScreen(
            "Project instructions\nremain visible\nstable transcript\nline\n┃ Ask anything…\n+p commands",
            "Project instructions\nremain visible\nstable transcript\nline\n┃ Ask anything…\n+p commands",
            "80x25");
        resizeOnly.ObserveRenderedScreen(
            "Project instructions\nremain visible\nstable transcript\nline\n┃ Ask anything…\n+p commands",
            "Project instructions\nremain visible\nstable transcript\nline\n┃ Ask anything…\n+p commands",
            "80x25");
        clock.Advance(TimeSpan.FromMinutes(5));
        resizeOnly.Tick();
        Expect(resizeOnly, TerminalActivity.Active, "진행 중");

        var responseAfterResize = new TerminalActivityTracker(clock, activeWindow: TimeSpan.FromSeconds(1));
        responseAfterResize.MarkStarted();
        responseAfterResize.ObserveRenderedScreen("┃ Ask anything…\n+p commands", "┃ Ask anything…\n+p commands", "40x15");
        responseAfterResize.ObserveUserInput("answer\r");
        const string resizedAnswer = "The answer is 42\n┃ Ask anything…\n+p commands";
        responseAfterResize.ObserveRenderedScreen(resizedAnswer, resizedAnswer, "80x25");
        responseAfterResize.ObserveRenderedScreen(resizedAnswer, resizedAnswer, "80x25");
        clock.Advance(TimeSpan.FromSeconds(1));
        responseAfterResize.Tick();
        Expect(responseAfterResize, TerminalActivity.Idle, "대기 중");

        var quietOpenCodeWork = new TerminalActivityTracker(clock, activeWindow: TimeSpan.FromSeconds(1));
        quietOpenCodeWork.MarkStarted();
        quietOpenCodeWork.ObserveRenderedScreen(
            "┃ Ask anything…\n+p commands",
            "┃ Ask anything…\n+p commands",
            "40x15");
        quietOpenCodeWork.ObserveUserInput("research\r");
        quietOpenCodeWork.ObserveRenderedScreen(
            "Thinking… esc interrupt\n┃ Ask anything…\n+p commands",
            "Thinking… esc interrupt\n┃ Ask anything…\n+p commands",
            "40x15");
        quietOpenCodeWork.ObserveRenderedScreen(
            "Found a file\nThinking… esc interrupt\n┃ Ask anything…\n+p commands",
            "Found a file\nThinking… esc interrupt\n┃ Ask anything…\n+p commands",
            "40x15");
        clock.Advance(TimeSpan.FromMinutes(10));
        quietOpenCodeWork.Tick();
        Expect(quietOpenCodeWork, TerminalActivity.Active, "진행 중");
    }

    private static string Escape(string value) => value.Replace("\x1b", "<ESC>", StringComparison.Ordinal);

    private static void Expect(
        TerminalActivityTracker tracker,
        TerminalActivity expectedActivity,
        string expectedLabel)
    {
        Require(tracker.Activity == expectedActivity, $"Expected {expectedActivity}, received {tracker.Activity}.");
        Require(tracker.ActivityLabel == expectedLabel, $"Expected '{expectedLabel}', received '{tracker.ActivityLabel}'.");
    }

    private static void Require(bool condition, string message)
    {
        if (!condition)
        {
            throw new InvalidOperationException(message);
        }
    }

    private sealed class TestTimeProvider : TimeProvider
    {
        private DateTimeOffset _now = new(2026, 9, 14, 0, 0, 0, TimeSpan.Zero);

        public override DateTimeOffset GetUtcNow() => _now;

        public void Advance(TimeSpan duration) => _now += duration;
    }
}

