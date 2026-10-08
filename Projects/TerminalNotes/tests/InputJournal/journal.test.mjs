import assert from "node:assert/strict";
import { mkdtempSync, readFileSync, writeFileSync, existsSync } from "node:fs";
import { tmpdir } from "node:os";
import { join, resolve } from "node:path";
import { spawn } from "node:child_process";
import { TerminalNotesInputJournal } from "../../Assets/terminal-input-journal.mjs";

const sessionClient = { get: async ({ path }) => ({ data: { id: path.id } }) };
const submit = (hooks, id, text, extras = {}) => hooks["chat.message"]({ sessionID: "ses_test" }, {
  message: { role: "user", sessionID: "ses_test", id }, parts: [{ type: "text", text, ...extras }],
});
if (process.argv[2] === "--writer") {
  const [folder, writer] = process.argv.slice(3);
  const hooks = await TerminalNotesInputJournal({ directory: folder, client: { session: sessionClient } });
  for (let i = 0; i < 10; i++) await submit(hooks, `msg_${writer}_${i}`, `${writer}:${i}:` + "한글😀".repeat(40000));
} else {
  const folder = mkdtempSync(join(tmpdir(), "TerminalNotes input 한글 "));
  const file = join(folder, "terminal-notes", "terminal-inputs.md");
  let toast = false;
  const hooks = await TerminalNotesInputJournal({ directory: folder, client: { session: sessionClient, tui: { showToast: async () => { toast = true; } } } });
  assert.equal(existsSync(file), false, "opening a terminal does not write a journal");
  const text = "추가 요청\n```md\n## 원문 보존\n```\n입력😀 & %PATH% ' ";
  await submit(hooks, "msg_first", text);
  await submit(hooks, "msg_first", text);
  await submit(hooks, "msg_repeat", text);
  let md = readFileSync(file, "utf8");
  assert.equal(md.split(text).length - 1, 2, "same ID is deduplicated; intentional repeated text is retained");
  assert.ok(md.includes("````text\n" + text + "\n````"));
  await submit(hooks, "msg_synthetic", "synthetic hidden", { synthetic: true });
  await submit(hooks, "msg_ignored", "ignored hidden", { ignored: true });
  for (const prefix of ["Terminal Notes 자동화 · ", "TerminalNotes 자동 진행: ", "다음 MD 파일을 지금 직접 읽고 최신 내용을 이 작업의 지침으로 사용하세요: "])
    await submit(hooks, "msg_auto" + prefix, prefix + "must not become a manual request");
  assert.equal(readFileSync(file, "utf8"), md);
  await hooks["chat.message"]({}, { message: { role: "assistant" }, parts: [{ type: "text", text: "not user input" }] });
  assert.equal(readFileSync(file, "utf8"), md);
  const childHooks = await TerminalNotesInputJournal({ directory: folder, client: { session: { get: async ({ path }) => ({ data: { id: path.id, parentID: "ses_parent" } }) } } });
  await submit(childHooks, "msg_child", "delegated agent request");
  assert.equal(readFileSync(file, "utf8"), md, "child-agent prompts are not user input");
  console.log("PASS Unicode, multiline, Markdown fences, duplicate IDs, repeated requests, generated-input filtering");

  // Independent role/CMD processes append sizable records to the same project.
  await Promise.all(Array.from({ length: 4 }, (_, i) => new Promise((ok, fail) => {
    const child = spawn(process.execPath, [resolve(process.argv[1]), "--writer", folder, String(i)], { windowsHide: true, stdio: "pipe" });
    let error = ""; child.stderr.on("data", data => error += data);
    child.on("error", fail); child.on("exit", code => code === 0 ? ok() : fail(new Error(error)));
  })));
  md = readFileSync(file, "utf8");
  assert.equal((md.match(/<!-- terminalnotes-input:/g) ?? []).length, 42);
  for (let writer = 0; writer < 4; writer++) for (let i = 0; i < 10; i++)
    assert.ok(md.includes(`${writer}:${i}:` + "한글😀".repeat(40000)), `whole concurrent record ${writer}:${i}`);
  console.log("PASS concurrent append from four independent processes preserves every complete large input");

  const blocked = mkdtempSync(join(tmpdir(), "TerminalNotes blocked input "));
  writeFileSync(join(blocked, "terminal-notes"), "existing file must survive");
  const badHooks = await TerminalNotesInputJournal({ directory: blocked, client: { session: sessionClient, tui: { showToast: async () => { toast = true; } } } });
  await assert.rejects(() => submit(badHooks, "msg_error", "request"), /저장하지 못했습니다/);
  assert.ok(toast); assert.equal(readFileSync(join(blocked, "terminal-notes"), "utf8"), "existing file must survive");
  console.log("PASS write errors are visible and existing data is preserved");
  console.log("INPUT JOURNAL TESTS PASSED: " + folder);
}