using System.IO;
using System.Security.Cryptography;
using System.Text;

namespace TerminalNotes.Services;

/// <summary>Owns the editable user-wide Markdown instructions used by OpenCode processes.</summary>
public static class GlobalAgentInstructions
{
    private static readonly UTF8Encoding Utf8 = new(encoderShouldEmitUTF8Identifier: false);
    private static readonly string DirectoryPath = Path.Combine(OpenCodeProjectContext.DataDirectory, "Instructions");
    private static readonly string LockPath = Path.Combine(DirectoryPath, "global-agent-instructions.lock");
    private const string ApprovedPreviousInstructionsHash = "407CFA4E18EE27695A3BE67CD85C7588C8BBD5B9C1000E162ACC9C181B90B106";

    public static string FilePath => Path.Combine(DirectoryPath, "global-agent-instructions.md");

    /// <summary>Creates the default file and upgrades only the user-approved previous contents.</summary>
    public static string EnsureFile()
    {
        Directory.CreateDirectory(DirectoryPath);
        try
        {
            using var stream = new FileStream(FilePath, FileMode.CreateNew, FileAccess.Write, FileShare.Read);
            using var writer = new StreamWriter(stream, Utf8);
            writer.Write(DefaultContents);
        }
        catch (IOException) when (File.Exists(FilePath))
        {
            // Another process created it, or the user already has customized instructions.
        }
        UpgradeApprovedPreviousInstructions();
        return FilePath;
    }

    private static void UpgradeApprovedPreviousInstructions()
    {
        try
        {
            using var lockStream = new FileStream(LockPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
            using var instructions = new FileStream(FilePath, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
            byte[] originalBytes = ReadAllBytes(instructions);
            if (!IsApprovedPreviousInstructions(originalBytes)) return;

            string backupPath = FilePath + ".before-approved-upgrade." + Guid.NewGuid().ToString("N") + ".md";
            bool backupComplete = false;
            bool backupCreated = false;
            bool mutationStarted = false;
            try
            {
                using (var backup = new FileStream(backupPath, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                {
                    backupCreated = true;
                    backup.Write(originalBytes);
                    backup.Flush(flushToDisk: true);
                }
                backupComplete = true;

                // Keep the target exclusively open from the hash
                // check through the durable backup and replacement write.
                if (!IsApprovedPreviousInstructions(ReadAllBytes(instructions)))
                {
                    TryDelete(backupPath);
                    return;
                }
                byte[] replacement = Utf8.GetBytes(DefaultContents);
                instructions.Position = 0;
                mutationStarted = true;
                instructions.SetLength(0);
                instructions.Write(replacement);
                instructions.Flush(flushToDisk: true);
                if (!ReadAllBytes(instructions).AsSpan().SequenceEqual(replacement))
                    throw new IOException("The global instruction replacement could not be verified.");
            }
            catch (IOException)
            {
                if (mutationStarted) RestoreOriginalInstructions(instructions, originalBytes);
                if (backupCreated && !backupComplete) TryDelete(backupPath);
            }
            catch (UnauthorizedAccessException)
            {
                if (mutationStarted) RestoreOriginalInstructions(instructions, originalBytes);
                if (backupCreated && !backupComplete) TryDelete(backupPath);
            }
        }
        catch (IOException)
        {
            // A busy or unavailable instruction file must not prevent startup.
        }
        catch (UnauthorizedAccessException)
        {
            // A read-only or externally locked instruction file is left untouched.
        }
    }

    private static bool IsApprovedPreviousInstructions(byte[] bytes) =>
        string.Equals(Convert.ToHexString(SHA256.HashData(bytes)), ApprovedPreviousInstructionsHash, StringComparison.Ordinal);

    private static byte[] ReadAllBytes(FileStream stream)
    {
        stream.Position = 0;
        using var buffer = new MemoryStream();
        stream.CopyTo(buffer);
        return buffer.ToArray();
    }

    private static void RestoreOriginalInstructions(FileStream stream, byte[] originalBytes)
    {
        try
        {
            stream.Position = 0;
            stream.SetLength(0);
            stream.Write(originalBytes);
            stream.Flush(flushToDisk: true);
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    private static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    public static string ReadCurrent() => File.ReadAllText(EnsureFile(), Encoding.UTF8);

    /// <summary>
    /// Saves only if the file still matches the text the editor originally loaded.
    /// A persistent exclusive lock coordinates multiple TerminalNotes processes.
    /// </summary>
    public static SaveResult TrySave(string expectedContents, string newContents)
    {
        ArgumentNullException.ThrowIfNull(expectedContents);
        ArgumentNullException.ThrowIfNull(newContents);
        EnsureFile();

        FileStream lockStream;
        try
        {
            lockStream = new FileStream(LockPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        }
        catch (IOException)
        {
            return SaveResult.Busy;
        }

        using (lockStream)
        {
            try
            {
                // Keep the current file open without sharing write access until the
                // replacement is committed. This also detects edits made by an
                // external Markdown editor between the UI read and save.
                using var currentFile = new FileStream(FilePath, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete);
                string current;
                using (var reader = new StreamReader(currentFile, Encoding.UTF8, detectEncodingFromByteOrderMarks: true, leaveOpen: true))
                    current = reader.ReadToEnd();
                if (!string.Equals(current, expectedContents, StringComparison.Ordinal))
                    return SaveResult.Conflict;

                string temporaryPath = FilePath + "." + Guid.NewGuid().ToString("N") + ".tmp";
                try
                {
                    File.WriteAllText(temporaryPath, newContents, Utf8);
                    File.Replace(temporaryPath, FilePath, destinationBackupFileName: null);
                    return SaveResult.Saved;
                }
                finally
                {
                    if (File.Exists(temporaryPath)) File.Delete(temporaryPath);
                }
            }
            catch (IOException)
            {
                return SaveResult.Busy;
            }
            catch (UnauthorizedAccessException) { return SaveResult.Busy; }
        }
    }

    public enum SaveResult { Saved, Conflict, Busy }

    private const string DefaultContents = """
        # 프로젝트 터미널 작업 지침
        
        ## 목표와 범위
        
        사용자의 가장 최근 요청을 기준으로 작업한다. 연결된 프로젝트 `plan.md`가 있으면 현재 요청과 함께 읽고, 요청·계획·현재 코드·이미 적용된 변경·유효한 검증 결과를 대조한다. 완료 표시가 없거나 TODO가 남았다는 이유만으로 이미 구현된 기능을 다시 만들지 않는다. 계획과 요청이 다르면 최근 사용자의 명시적 지시를 우선하고, 계획은 확인된 미완료 작업과 근거만 갱신한다.
        
        질문에는 답하고, 사용자가 구현이나 파일 수정을 요청하지 않았다면 파일을 바꾸지 않는다. 구현 요청을 받으면 필요한 작업을 끝까지 수행하고 검증 결과와 남은 제한을 알린다. 기존 동작과 사용자 데이터를 보존하며 요청과 직접 관련된 확인된 결함만 최소 범위로 수정한다. 요청되지 않은 정리, 재설계, 포맷 변경, 로그 수집, 시간 측정은 추가하지 않는다.
        
        ## 진행 방법
        
        시작 전에 관련 지침과 요청에 필요한 코드·설정·문서를 선택적으로 조사한다. 먼저 `rg`로 파일명이나 심볼을 찾고, 해당 심볼의 정의·호출자·관련 상태 전이와 저장·읽기·재열기·실행 경로를 따라간다. 저장소 전체를 무차별적으로 읽거나 무관한 사용자 문서를 열지 않는다. 증상을 재현할 수 있으면 먼저 재현하고, 관련 호출 흐름과 생명주기를 따라 원인을 확인한다. 원인을 확인하지 못하면 추측을 사실처럼 보고하지 말고, 확인된 증거와 남은 불확실성을 구분한다.
        
        사용자가 요청했거나 명시적으로 허용한 행동은 실행에 필요한 범위에서 자율적으로 진행한다. 정상 완료, 실패, 차단을 구분할 수 있는 실제 신호를 관찰한다. 출력이 없거나 오래 걸린다는 이유만으로 완료·실패·정체를 단정하지 않는다. 느린 시작, 대기, 장시간 실행은 현재 단계와 관찰 가능한 근거를 바탕으로 판단하며, 진행을 지어내거나 작업을 성급히 중단하지 않는다. 같은 검증이나 검색에서 새 증거가 두 번 연속 나오지 않으면 반복을 멈추고 독립적인 관련 경로 한 곳을 추가 확인한다. 그래도 진전이 없으면 확인한 사실, 막힌 이유, 필요한 입력을 정리해 보고한다. 승인이나 외부 상태 변화가 꼭 필요한 경우에만 구체적인 이유를 설명하고 필요한 입력을 요청한다.
        
        변경은 요청된 파일과 동작에 한정한다. 요청에 꼭 필요한 범위에서 기존 로직을 수정하거나 교체할 수 있으며, 그 이유를 확인 가능하게 남긴다. 요청과 무관한 기존 동작은 제거하지 않는다. 저장 파일·설정·세션·초안·기록을 정리한다는 명목으로 삭제하지 않는다. 사용자 파일을 덮어쓰기 전에 기존 데이터와 충돌 가능성을 확인하고, 안전하게 보존할 수 있는 기존 동작을 유지한다. 비밀정보와 터미널 원문 입력을 불필요하게 출력·저장·복사하지 않는다.
        
        검증은 수정 위험과 영향 범위에 맞는 기존 테스트나 직접적인 확인으로 한다. 테스트가 실제 코드 경로를 검증하는지 살피고, 목이나 모의 결과를 실제 외부 서비스·모델·사용자 세션의 검증으로 표현하지 않는다. 관련 검증이 실패하면 원인을 확인하고 수정 후 다시 확인한다. 테스트를 실행하지 못했으면 실행하지 못한 이유와 그에 따른 한계를 보고한다.
        
        앱 동작을 확인할 때는 저장소의 실제 실행·게시 경로와 현재 변경 산출물을 확인한다. 오래된 실행 파일이나 이전 게시 폴더의 결과를 현재 소스의 결과라고 하지 않는다. 사용자가 금지한 실행·외부 접근·계정 조작은 하지 않는다.
        
        ## 입력과 기록
        
        사용자의 최초 목표와 수락된 제약을 유지하면서 최신 메시지를 현재 작업의 추가 지시·수정·질문으로 반영한다. 과거 터미널 입력 기록이나 보관된 프롬프트는 새 요청으로 추정하거나 재생하지 않는다. 화면 출력과 승인 응답도 새 요청으로 간주하지 않는다. 사용자가 별도로 요청하지 않으면 원문 입력, 메시지 내용, 세션 식별자 또는 성능 측정용 시간 기록을 새로 만들지 않는다.
        
        ## 단일 세션과 도구
        
        이 앱에서 실행되는 OpenCode는 프로젝트별 단일 에이전트 세션으로 작업한다. 지휘·검토·구현 팀, 하위 에이전트, 자동 순환 스케줄러를 만들지 않는다. 현재 OpenCode 프로세스에 주입된 모델·도구·권한·승인 설정을 따른다. 모델을 강제 변경하거나 Codex 전용 실행 규칙을 적용하지 않는다. 사용자가 금지한 외부 통신이나 도구를 우회하지 않는다.
        
        ## 완료 조건
        
        작업을 끝내기 전에 다음 항목을 확인한다.
        
        1. 최초 목표, 최신 수정, 수락된 제약과 연결된 계획을 모두 반영했다.
        2. 관련 실제 호출자와 저장·읽기·재열기·실행 진입 경로를 확인하고, 변경된 동작이 그 경로에서 어떻게 작동하는지 검증했다.
        3. 영향받는 기존 동작과 데이터를 보존했고, 변경은 요청된 동작에 필요한 범위로 제한했다.
        4. 변경 위험에 맞는 빌드와 의미 있는 테스트를 실행했거나, 실행하지 못한 사유와 미확인 범위를 기록했다. 빌드 성공만으로 런타임 동작이 검증되었다고 하지 않는다.
        5. 테스트 더블이나 시뮬레이션 결과와 실제 서비스·모델·앱 실행 결과를 구분하고, 하지 않은 검증을 했다고 보고하지 않는다.
        6. 사용자에게 변경 경로, 검증 명령과 결과, 남은 제한을 간결하고 정확하게 알렸다.
        
        ## 마무리와 이어하기
        
        작업을 마칠 때 변경한 경로와 이유, 실행한 검증 및 실제 결과, 확인하지 못한 사항을 간결하고 사실대로 알린다. 변경이 없으면 기존 구현과 완료 근거를 설명한다. 계획의 미완료 항목을 완료했다고 표시하려면 코드 변경 또는 유효한 검증 등 확인 가능한 근거가 있어야 한다.
        
        다음 작업으로 넘길 때는 현재 요청, 이미 완료한 범위, 남은 구체 항목, 관련 경로, 마지막 검증과 결과만 기록한다. 완료한 일을 다시 실행하라고 지시하거나, 존재하지 않는 증거를 만들거나, 사용자의 원문 프롬프트를 불필요하게 복제하지 않는다.
        """;
}
