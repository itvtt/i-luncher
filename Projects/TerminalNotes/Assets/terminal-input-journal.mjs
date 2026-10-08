import { mkdirSync, openSync, closeSync, writeSync, fsyncSync, readFileSync } from "node:fs";
import { join } from "node:path";
import { createHash } from "node:crypto";

// Runs inside OpenCode, including an independent CMD after TerminalNotes exits.
// Record accepted message text, not keystrokes, screen output, or approval answers.
export const TerminalNotesInputJournal = async ({ directory, client }) => {
  const folder = join(directory, "terminal-notes");
  const file = join(folder, "terminal-inputs.md");
  let queue = Promise.resolve();
  return {
    "chat.message": async (input, output) => {
      if (output.message?.role !== "user") return;
      const text = output.parts.filter(p => p.type === "text" && !p.synthetic && !p.ignored)
        .map(p => p.text).filter(t => typeof t === "string").join("\n");
      if (!text.trim()) return;
      // App-generated work already has its own persisted plan/dispatch records.
      if (/^(Terminal Notes 자동화 · |TerminalNotes 자동 진행: |다음 MD 파일을 지금 직접 읽고 최신 내용을 이 작업의 지침으로 사용하세요: )/.test(text)) return;
      const session = output.message.sessionID ?? input.sessionID;
      const message = output.message.id ?? input.messageID;
      if (!session || !message) throw new Error("터미널 입력 기록: 세션/메시지 ID를 확인하지 못했습니다.");
      // Delegated child-session prompts are agent work, not additional user input.
      const info = await client.session.get({ path: { id: session } });
      if (info.error || !info.data || info.data.id !== session)
        throw new Error("터미널 입력 기록: 원본 세션을 확인하지 못했습니다.");
      if (info.data.parentID) return;
      const id = createHash("sha256").update(session + "\0" + message).digest("hex");
      const marker = `<!-- terminalnotes-input:${id} -->`;
      const save = queue.then(() => {
        mkdirSync(folder, { recursive: true });
        let existing = "";
        try { existing = readFileSync(file, "utf8"); }
        catch (error) { if (error.code !== "ENOENT") throw error; }
        // Native retries/replayed hooks retain the message ID. Repeated user text
        // with a new message ID remains a distinct request.
        if (existing.includes(marker)) return;
        const fence = "`".repeat(Array.from(text.matchAll(/`+/g)).reduce((length, match) => Math.max(length, match[0].length + 1), 3));
        const header = existing.length ? "" : "# 터미널 추가 입력\n\n제출된 요청의 기록입니다. 실행 또는 완료를 증명하지 않습니다. 재개 시 기존 코드와 완료 내역을 확인하고 남은 작업만 진행하세요. 과거 요청을 순서대로 다시 실행하지 마세요.\n";
        const entry = `\n## ${new Date().toISOString()}\n\n- 세션: ${session}\n- 메시지: ${message}\n\n${fence}text\n${text}\n${fence}\n${marker}\n`;
        const bytes = Buffer.from(header + entry, "utf8");
        const fd = openSync(file, "a");
        try {
          // One OS append write per record; concurrent role processes never
          // read/replace the document or overwrite one another's additions.
          const count = writeSync(fd, bytes, 0, bytes.length);
          if (count !== bytes.length) throw new Error("입력 기록 일부만 저장되었습니다. MD와 원본 세션을 확인하세요.");
          fsyncSync(fd);
        } finally { closeSync(fd); }
      });
      queue = save.catch(() => {});
      try { await save; }
      catch (error) {
        // Fail visibly before proceeding; never report an unsaved input as saved.
        try { await client.tui.showToast({ body: { title: "입력 기록 저장 실패", message: "terminal-notes/terminal-inputs.md의 쓰기 권한과 디스크를 확인하세요.", variant: "error" } }); } catch {}
        throw new Error("터미널 입력 MD를 저장하지 못했습니다: " + (error.code ?? error.message));
      }
    },
  };
};