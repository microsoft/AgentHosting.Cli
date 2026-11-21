import assert from "node:assert/strict";
import { test } from "node:test";
import { request } from "node:http";
import { access, copyFile, mkdir, mkdtemp, readdir, readFile, rm, symlink, utimes, writeFile } from "node:fs/promises";
import { tmpdir } from "node:os";
import path from "node:path";
import { runInNewContext } from "node:vm";
import { Chat, ChatRegistry, openFailureGuidance, replyNotification } from "./chat.mjs";
import { askArguments, parseResult, ReplyStream, resolveExecutable, resolveTarget, runCommand, validateConfiguredInput, validateTarget, visibleMessages } from "./client.mjs";
import { ActivityReader } from "./activity-reader.mjs";
import { renderMarkdown } from "./markdown.mjs";
import { ConversationData } from "./debug.mjs";
import { startServer } from "./server.mjs";
import { attachChat, pruneStarted, StartedChats } from "./started.mjs";
import { FileDownloads, fileLimit, uploads, withUploadedFiles, publicAddress, downloadPublicFile } from "./files.mjs";
import { SetupChat } from "./setup.mjs";

const target = { schemaName: "agent_schema", environmentId: "env-id",
    tenantId: "11111111-1111-1111-1111-111111111111", deploymentName: "development" };
const account = { success: true, signedIn: true, account: "user@example.test", tenantId: target.tenantId, cloud: "prod" };
const help = "--query --file --start-conversation --conversation-id --project --agent-id --deployment-id --schema-name --environment-id --tenant --deployment-name --json --non-interactive";
const outcome = { success: true, schemaVersion: "1", status: "completed", exitCode: 0,
    conversationId: "conversation-1", activities: [{ type: "message", text: "Hello" }] };
const json = (data, code = 0) => ({ code, output: JSON.stringify(data) });
const streamed = (text, sequence, type = "streaming", id = "reply") => ({
    type: "message", text, entities: [{ type: "streaminfo", streamId: id, streamSequence: sequence, streamType: type }],
});

function fixtureScript(source) {
    return source.replace(/^import .*;\r?\n/gm, "");
}

for (const [name, newline] of [["LF", "\n"], ["CRLF", "\r\n"]]) {
    test(`VM fixtures remove module imports with ${name} line endings`, () => {
        const source = [
            'import { renderMarkdown } from "./markdown.mjs";',
            'import { Chat } from "./chat.mjs";',
            'renderMarkdown("Hello");',
        ].join(newline);
        let rendered;
        runInNewContext(fixtureScript(source), { renderMarkdown(text) { rendered = text; } });
        assert.equal(rendered, "Hello");
    });
}

test("conversation data records activities, envelopes and messages verbatim without aliasing them", () => {
    const data = new ConversationData();
    const event = { type: "event", name: "ToolInvocation", value: { tool: "weather", access_token: "token-value",
        url: "https://example.test?sig=signature&city=Paris" } };
    data.begin("connect");
    data.activity(event);
    let response = data.snapshot().responses[0];
    assert.equal(response.complete, false);
    assert.deepEqual(response.payload.activities[0], event);
    event.value.tool = "changed";
    assert.equal(data.snapshot().responses[0].payload.activities[0].value.tool, "weather");
    data.finish({ ...outcome, activities: [event] });
    response = data.snapshot().responses[0];
    assert.equal(response.complete, true);
    assert.equal(response.payload.status, "completed");
    assert.equal(response.payload.activities[0].value.access_token, "token-value");
    const messages = [{ role: "user", text: "Bearer value" }];
    assert.deepEqual(data.snapshot(messages).messages, messages);
});

test("conversation data bounds raw responses and labels oversized or unfinished output", () => {
    const data = new ConversationData(220);
    data.begin("send");
    data.activity({ type: "event", name: "Started" });
    data.activity({ type: "event", text: "x".repeat(1000) });
    data.finish({ ...outcome, text: "x".repeat(1000) });
    const snapshot = data.snapshot();
    assert.equal(snapshot.truncated, true);
    assert.equal(snapshot.responses[0].complete, false);
    assert.equal(snapshot.responses[0].payload.activities.length, 1);
    assert(Buffer.byteLength(JSON.stringify(snapshot.responses)) <= 220);
});

test("conversation data retains the latest raw responses within its total budget without discarding chat messages", () => {
    const data = new ConversationData(550);
    const messages = [];
    for (let turn = 0; turn < 20; turn++) {
        messages.push({ role: "user", text: `Question ${turn}` });
        data.begin("send");
        data.activity({ type: "message", text: "x".repeat(30) });
        data.finish({ ...outcome, turn });
        const snapshot = data.snapshot(messages);
        assert(Buffer.byteLength(JSON.stringify(snapshot.responses)) <= 550);
        assert.equal(snapshot.responses.at(-1).payload.turn, turn);
        assert.deepEqual(snapshot.messages, messages);
    }
    assert.equal(data.snapshot().truncated, true);
    assert(data.snapshot().responses.length < 20);
});

test("chat debug includes received ask data and failure envelopes without activation and clears with a new chat", async () => {
    for (const failed of [false, true]) {
        const event = { type: "event", name: "ToolInvocation", value: { tool: "example" } };
        const data = { ...outcome, activities: [event, ...outcome.activities],
            ...(failed ? { success: false, status: "failed", exitCode: 1, queryDelivery: "attempted" } : {}) };
        const { chat } = fixture((args, { onStdout }) => {
            if (args[1] !== "ask" || args.includes("--help")) return;
            onStdout('{"schemaVersion":"1","activities":[' + JSON.stringify(event));
            assert.equal(chat.debugSnapshot().responses.at(-1).payload.activities[0].name, "ToolInvocation");
            return json(data, failed ? 1 : 0);
        });
        await chat.start("check");
        assert.deepEqual(chat.debugSnapshot().responses, []);
        await chat.start("send", { query: "Hello" });
        const debug = chat.debugSnapshot();
        assert.equal(debug.responses[0].complete, true);
        assert.equal(debug.responses[0].payload.status, failed ? "failed" : "completed");
        assert.equal(debug.responses[0].payload.activities[0].name, "ToolInvocation");
        assert.deepEqual(debug.messages, chat.snapshot().messages);
        assert(!JSON.stringify(chat.snapshot()).includes("ToolInvocation"));
        assert(!JSON.stringify(debug).includes(account.account));
        await chat.start("reset");
        assert.deepEqual(chat.debugSnapshot().responses, []);
        assert.deepEqual(chat.debugSnapshot().messages, []);
    }
});

test("debug projects the existing transcript and all response turns while replies stream and after completion", async () => {
    const { chat } = fixture((args, { onStdout }) => {
        if (args[1] !== "ask" || args.includes("--help")) return;
        const activity = streamed("Partial answer", 1);
        onStdout('{"schemaVersion":"1","activities":[' + JSON.stringify(activity));
        const debug = chat.debugSnapshot();
        assert.deepEqual(debug.messages, chat.snapshot().messages);
        assert.equal(debug.messages.at(-1).streaming, true);
        assert.deepEqual(debug.responses.at(-1).payload.activities, [activity]);
        return json(outcome);
    });
    await chat.start("connect");
    await chat.start("send", { query: "Question one" });
    await chat.start("send", { query: "Question two" });
    const debug = chat.debugSnapshot();
    assert.deepEqual(debug.messages, chat.snapshot().messages);
    assert.equal(debug.messages.length, 5);
    assert.deepEqual(debug.responses.map(response => response.operation), ["connect", "send", "send"]);
    assert(debug.responses.every(response => response.complete));
    assert(debug.responses.every(response => JSON.stringify(response.payload) === JSON.stringify(outcome)));
    assert(debug.messages.every(message => !message.streaming));
    assert.deepEqual(debug.responses.map(response => response.turnId), [1, 2, 3]);
    assert.deepEqual(debug.messages.map(message => message.turnId), [1, 2, 2, 3, 3]);
    await chat.start("reset");
    await chat.start("connect");
    assert.equal(chat.snapshot().messages[0].turnId, 4);
});

test("uploads validate filenames, encoding, count and size and remove staged files even after failure", async () => {
    const files = uploads([{ name: "study.xlsx", base64: "AQID" }]);
    let staged;
    await assert.rejects(withUploadedFiles(files, async paths => {
        staged = paths[0];
        assert.deepEqual(await readFile(staged), Buffer.from([1, 2, 3]));
        assert(staged.endsWith("study.xlsx"));
        throw new Error("Send failed");
    }), /Send failed/);
    await assert.rejects(access(staged));
    assert.equal(uploads([{ name: "maximum.bin", base64: Buffer.alloc(10 * 1024 * 1024).toString("base64") }])[0].size, 10 * 1024 * 1024);
    for (const input of [
        [{ name: "../private", base64: "AQID" }], [{ name: "CON.txt", base64: "AQID" }],
        ...["CON .txt", "CONIN$", "conout$.txt", "COM\u00b9.txt", "LPT\u00b2.log"].map(name => [{ name, base64: "AQID" }]),
        [{ name: "file", base64: "***" }],
        Array(6).fill({ name: "file", base64: "" }),
        [{ name: "large", base64: Buffer.alloc(10 * 1024 * 1024 + 1).toString("base64") }],
    ]) assert.throws(() => uploads(input));
});

test("download destinations reject private networks, credentials and non-HTTPS without network access", async () => {
    for (const address of ["127.0.0.1", "10.1.2.3", "169.254.169.254", "192.168.1.1", "::1", "::ffff:127.0.0.1", "2002:7f00:1::"]) {
        assert.equal(publicAddress(address), false);
    }
    assert.equal(publicAddress("8.8.8.8"), true);
    assert.equal(publicAddress("2606:4700:4700::1111"), true);
    for (const url of ["http://example.test/a", "https://127.0.0.1/a", "https://[::1]/a", "https://user:pass@example.test/a", "https://example.test:444/a"]) {
        await assert.rejects(downloadPublicFile(url), /public HTTPS/);
    }
});

test("returned files are downloaded only on demand and expired references are not replayed", async () => {
    let calls = 0;
    const files = new FileDownloads(async url => { calls++; assert.equal(url, "https://example.test/file"); return Buffer.from("result"); });
    const link = files.add(1, { name: "result.csv", contentUrl: "https://example.test/file" });
    assert.equal(calls, 0);
    assert.deepEqual(files.add(1, { name: "result.csv", contentUrl: "https://example.test/file" }), link);
    assert.equal((await files.download(link.id)).content.toString(), "result");
    assert.equal(calls, 1);
    const inline = files.add(2, { name: "inline.txt", contentUrl: "data:text/plain;base64,aGVsbG8=" });
    assert.equal((await files.download(inline.id)).content.toString(), "hello");
    files.clear();
    await assert.rejects(files.download(link.id), /no longer available/);
    assert.equal(calls, 1);
});

test("inline downloads enforce the decoded 10 MiB boundary", async () => {
    const files = new FileDownloads();
    const atLimit = Buffer.alloc(fileLimit, 97);
    const accepted = files.add(1, { name: "large.txt", contentUrl: `data:text/plain;base64,${atLimit.toString("base64")}` });
    const result = await files.download(accepted.id);
    assert.deepEqual(result.content, atLimit);

    const overLimit = Buffer.alloc(fileLimit + 1, 97);
    const rejected = files.add(2, { name: "too-large.txt", contentUrl: `data:text/plain;base64,${overLimit.toString("base64")}` });
    await assert.rejects(files.download(rejected.id), /10 MiB/);
});

test("a streamed inline file at the download limit remains available", async () => {
    const program = `
        const contentUrl = "data:text/plain;base64," + Buffer.alloc(10 * 1024 * 1024, 97).toString("base64");
        process.stdout.write(JSON.stringify({
            schemaVersion: "1",
            activities: [{ type: "message", attachments: [{ name: "large.txt", contentUrl }] }],
            success: true,
            status: "completed",
            exitCode: 0,
            conversationId: "conversation-1"
        }));
    `;
    const { chat } = fixture((args, options) => {
        if (args[1] === "ask" && !args.includes("--help")) {
            return runCommand(process.execPath, ["-e", program], options);
        }
    });

    await chat.start("send", { query: "Return a file" });
    assert.equal(chat.state.status, "ready", chat.state.error);
    const file = chat.state.messages.at(-1).files[0];
    const downloaded = await chat.files.download(file.id);
    assert.equal(downloaded.content.length, fileLimit);
    assert.equal(downloaded.content[0], 97);
    assert.equal(downloaded.content.at(-1), 97);
});

test("chat sends staged file paths once, keeps only upload metadata and exposes returned file bytes through the protected gateway", async () => {
    let staged;
    const result = { ...outcome, activities: [{ type: "message", attachments: [
        { name: "answer.txt", contentType: "text/plain", contentUrl: "data:text/plain;base64,YW5zd2Vy" },
    ] }] };
    const { chat } = fixture(async args => {
        if (args[1] !== "ask" || args.includes("--help")) return;
        staged = args[args.indexOf("--file") + 1];
        assert.equal((await readFile(staged)).toString(), "private document");
        return json(result);
    });
    await chat.start("send", { query: "Analyze",
        files: [{ name: "study.txt", base64: Buffer.from("private document").toString("base64") }] });
    assert.equal(chat.snapshot().status, "ready");
    await assert.rejects(access(staged));
    assert.deepEqual(chat.snapshot().messages[0].files, [{ name: "study.txt", size: 16 }]);
    assert(!JSON.stringify(chat.debugSnapshot()).includes(Buffer.from("private document").toString("base64")));
    const file = chat.snapshot().messages.at(-1).files[0];
    const server = await startServer(chat);
    const url = new URL(server.url);
    const headers = { Authorization: `Bearer ${url.hash.slice(1)}` };
    try {
        const address = `${url.origin}/files/${file.id}`;
        assert.equal((await fetch(address)).status, 403);
        assert.equal((await fetch(address, { headers: { ...headers, Origin: "https://attacker.test" } })).status, 403);
        const response = await fetch(address, { headers });
        assert.equal(response.status, 200);
        assert.match(response.headers.get("content-disposition"), /attachment;.*answer.txt/);
        assert.equal(await response.text(), "answer");
        await chat.start("reset");
        assert.equal((await fetch(address, { headers })).status, 400);
    } finally { await server.close(); }
});

function markdownDocument() {
    return {
        createElement(tagName) {
            return {
                tagName, children: [],
                append(...children) { this.children.push(...children); },
                replaceChildren(...children) { this.children = children; },
                set textContent(value) { this.children = [String(value)]; },
                get textContent() { return this.children.map(child => typeof child === "string" ? child : child.textContent).join(""); },
                set innerHTML(value) { throw new Error(`Unexpected HTML insertion: ${value}`); },
            };
        },
    };
}

function descendants(node) {
    return [node, ...node.children.filter(child => typeof child !== "string").flatMap(descendants)];
}

function debugJsonText(node) {
    if (node.tagName === "summary") return "";
    return node.children.map(child => typeof child === "string" ? child : debugJsonText(child)).join("");
}

test("Markdown formats common reply blocks and inline text without inserting HTML", () => {
    const root = renderMarkdown([
        "# Study design", "", "**Blind** and *randomized* with `literal <code>`.", "",
        "- Control", "- Prototype", "", "3. First", "4. Next", "",
        "> A cautious claim", "", "---", "",
        "| Condition | Temperature |", "| --- | ---: |", "| **Prototype** | 92 C |", "",
        "```js", "<script>literal code</script>", "```",
    ].join("\n"), markdownDocument());
    const nodes = descendants(root);
    for (const tag of ["h1", "strong", "em", "code", "ul", "ol", "li", "blockquote", "hr", "table", "th", "td", "pre"]) {
        assert(nodes.some(node => node.tagName === tag), `Missing ${tag}`);
    }
    assert.equal(nodes.find(node => node.tagName === "ol").start, 3);
    assert.equal(nodes.find(node => node.tagName === "strong").textContent, "Blind");
    assert(nodes.some(node => node.tagName === "code" && node.textContent === "<script>literal code</script>"));
    assert(!nodes.some(node => node.tagName === "script"));
});

test("Markdown treats raw HTML, images and executable links as inert text", () => {
    const source = '<img src=x onerror=alert(1)><script>alert(1)</script>\n\n' +
        '[bad](javascript:alert(1)) [data](data:text/html,evil) ![remote](https://attacker.test/tracker.png) ' +
        '[source](https://example.test/research)';
    const root = renderMarkdown(source, markdownDocument());
    const nodes = descendants(root);
    const links = nodes.filter(node => node.tagName === "a");
    assert.equal(links.length, 1);
    assert.equal(links[0].href, "https://example.test/research");
    assert.equal(links[0].rel, "noopener noreferrer");
    assert.equal(links[0].target, "_blank");
    assert.equal(links[0].referrerPolicy, "no-referrer");
    assert(!nodes.some(node => ["img", "script", "iframe", "style", "object"].includes(node.tagName)));
    assert(root.textContent.includes("<script>alert(1)</script>"));
    assert(root.textContent.includes("[bad](javascript:alert(1))"));
    assert(root.textContent.includes("[Image: remote]"));
});

test("Markdown handles an unfinished streamed code fence and visibly bounds large or complex replies", () => {
    const document = markdownDocument();
    const partial = renderMarkdown("## Answer\n\n```js\nconst a = '<img>';", document);
    assert.equal(descendants(partial).find(node => node.tagName === "pre").textContent, "const a = '<img>';");
    const final = renderMarkdown("## Answer\n\n```js\nconst a = '<img>';\n```\n\n**Done**", document);
    assert(descendants(final).some(node => node.tagName === "strong" && node.textContent === "Done"));
    for (const source of ["x".repeat(100_001), "- item\n".repeat(2100)]) {
        const root = renderMarkdown(source, document);
        assert(root.children[0].textContent.includes("formatting limit"));
        assert.equal(root.children[1].textContent, source);
    }
});

test("activity reader handles arbitrary boundaries, escaped strings and nested metadata before the result closes", () => {
    const activities = [
        { type: "message", text: 'Braces } ], "quotes", \\ and coffee \u2615 \u{1f4ac}', value: { nested: [{ text: "[]" }] } },
        streamed("Hello", 1),
    ];
    const prefix = '{"schemaVersion":"1","activities":[';
    const body = activities.map(JSON.stringify).join(",");
    for (let split = 0; split <= body.length; split++) {
        const observed = [];
        const reader = new ActivityReader(activity => observed.push(activity));
        for (const character of prefix) reader.push(character);
        reader.push(body.slice(0, split));
        reader.push(body.slice(split));
        assert.deepEqual(observed, activities);
        reader.push('],"status":"completed"}');
        assert.equal(reader.done, true);
    }
    const observed = [];
    const reader = new ActivityReader(activity => observed.push(activity));
    for (const character of prefix + body) reader.push(character);
    assert.deepEqual(observed, activities);
    for (const malformed of ['{"schemaVersion":"2","activities":[', prefix + "null", prefix + "{}{}"]) {
        assert.throws(() => new ActivityReader(() => {}).push(malformed), /Invalid/);
    }
});

test("reply snapshots replace cumulative text, ignore stale sequences and reconcile each final once", () => {
    const reply = new ReplyStream([{ type: "message", text: "Greeting" }]);
    reply.write(streamed("Hel", 1));
    assert.deepEqual(reply.snapshot(), { text: "Greeting\n\nHel", partial: true, attachments: [] });
    reply.write({ ...streamed("Hello", "2"), type: "typing" });
    reply.write({ type: "typing", text: "hidden unrelated typing" });
    reply.write(streamed("stale", 1));
    reply.write(streamed("hidden reasoning", 3, "informative"));
    reply.write({ type: "message", text: "hidden delta", delta: { text: "secret" } });
    assert.deepEqual(reply.snapshot(), { text: "Greeting\n\nHello", partial: true, attachments: [] });
    reply.write(streamed("Final answer", 3, "final"));
    reply.write(streamed("duplicate", 3, "final"));
    reply.write(streamed("late update", 4));
    reply.write(streamed("Other answer", 1, "final", "other"));
    assert.deepEqual(reply.snapshot(), { text: "Greeting\n\nFinal answer\n\nOther answer", partial: false, attachments: [] });
    assert.throws(() => visibleMessages(undefined), /activities array/);
});

test("Copilot Studio delta chunks preserve the first anonymous fragment and whitespace until the final replacement", () => {
    const reply = new ReplyStream([]);
    const first = streamed("# Co", 1);
    delete first.entities[0].streamId;
    reply.write({ ...first, type: "typing", channelData: { chunkType: "delta" } });
    assert.equal(reply.snapshot().text, "# Co");
    for (const [text, sequence] of [["ffee", 2], ["\n\n", 3], ["**Study**", 4]]) {
        reply.write({ ...streamed(text, sequence), type: "typing", channelData: { chunkType: "delta" } });
    }
    reply.write({ ...streamed("stale", 2), type: "typing", channelData: { chunkType: "delta" } });
    assert.deepEqual(reply.snapshot(), { text: "# Coffee\n\n**Study**", partial: true, attachments: [] });
    reply.write(streamed("# Coffee\n\n**Study** complete.", undefined, "final"));
    reply.write(streamed("duplicate", undefined, "final"));
    assert.deepEqual(reply.snapshot(), { text: "# Coffee\n\n**Study** complete.", partial: false, attachments: [] });
    assert.equal(reply.parts.length, 1);
});

test("canvas gateway exposes a provisional reply while ask is still running and replaces it on completion", async () => {
    let release;
    let entered;
    const gate = new Promise(resolve => { release = resolve; });
    const started = new Promise(resolve => { entered = resolve; });
    const activities = [streamed("Hel", 1), streamed("Hello", 2), streamed("Hello world", 3, "final")];
    const result = json({ ...outcome, activities });
    const { chat } = fixture(async (args, { onStdout }) => {
        if (args[1] !== "ask" || args.includes("--help")) return;
        onStdout('{"schemaVersion":"1","activities":[' + JSON.stringify(activities[0]));
        entered();
        await gate;
        onStdout("," + activities.slice(1).map(JSON.stringify).join(",") + '],"status":"completed"}');
        return result;
    });
    const server = await startServer(chat);
    const url = new URL(server.url);
    const headers = { Authorization: `Bearer ${url.hash.slice(1)}` };
    const turn = chat.start("send", { query: "Hello" });
    try {
        await started;
        const during = await (await fetch(`${url.origin}/state`, { headers })).json();
        assert.equal(during.busy, true);
        assert.equal(during.messages.at(-1).text, "Hel");
        assert.equal(during.messages.at(-1).streaming, true);
        assert.equal(during.messages.at(-1).partial, true);
        release();
        await turn;
        const after = await (await fetch(`${url.origin}/state`, { headers })).json();
        assert.equal(after.status, "ready");
        assert.equal(after.messages.length, 2);
        assert.deepEqual(after.messages.at(-1), { role: "assistant", turnId: 1, text: "Hello world", partial: false, streaming: false });
        assert.equal(chat.conversationId, "conversation-1");
    } finally {
        release();
        await server.close();
    }
});

test("truncated, failed, cancelled and invalid terminal results retain partial text without replay", async () => {
    const activities = [streamed("Partial answer", 1)];
    for (const failure of [
        new Error("Cancelled"),
        { code: 1, output: '{"schemaVersion":"1","activities":[' + JSON.stringify(activities[0]) },
        json({ ...outcome, success: false, status: "failed", queryDelivery: "attempted", activities }, 1),
        json({ ...outcome, conversationId: "../unsafe", activities }),
    ]) {
        const { chat, calls } = fixture((args, { onStdout }) => {
            if (args[1] !== "ask" || args.includes("--help")) return;
            onStdout('{"schemaVersion":"1","activities":[' + JSON.stringify(activities[0]));
            if (failure instanceof Error) throw failure;
            return failure;
        });
        await chat.start("send", { query: "Hello" });
        assert.equal(chat.state.status, "uncertain");
        assert.equal(chat.state.messages.length, 2);
        assert.deepEqual(chat.state.messages.at(-1), {
            role: "assistant", turnId: 1, text: "Partial answer", partial: true, streaming: false,
        });
        assert.equal(chat.conversationId, undefined);
        await chat.start("send", { query: "Do not replay" });
        assert.equal(calls.filter(args => args[1] === "ask" && !args.includes("--help")).length, 1);
    }
});

test("a completed CLI result without a final text snapshot remains visibly incomplete", async () => {
    const { chat } = fixture(args => args[1] === "ask" && !args.includes("--help")
        ? json({ ...outcome, activities: [streamed("Unfinished", 1)] }) : undefined);
    await chat.start("send", { query: "Hello" });
    assert.equal(chat.state.messages.at(-1).text, "Unfinished");
    assert.equal(chat.state.messages.at(-1).partial, true);
    assert.equal(chat.state.messages.at(-1).streaming, false);
});

test("streaming transcript overflow retains the last bounded partial reply", async () => {
    const { chat } = fixture((args, { onStdout }) => {
        if (args[1] !== "ask" || args.includes("--help")) return;
        onStdout('{"schemaVersion":"1","activities":[' + JSON.stringify(streamed("Partial answer", 1)));
        onStdout("," + JSON.stringify(streamed("x".repeat(1_000_001), 2)));
        return json(outcome);
    });
    await chat.start("send", { query: "Hello" });
    assert.equal(chat.state.status, "uncertain");
    assert.equal(chat.state.messages.at(-1).text, "Partial answer");
    assert.equal(chat.state.messages.at(-1).partial, true);
    assert(JSON.stringify(chat.state.messages).length <= 1_000_000);
});

test("process stdout callbacks receive activities before process exit and callback failure stops the child", async () => {
    const controller = new AbortController();
    let observed = "";
    await assert.rejects(runCommand(process.execPath, ["-e", "process.stdout.write('early');setInterval(()=>{},1000)"], {
        signal: controller.signal,
        onStdout(text) { observed += text; controller.abort(); },
    }), /Cancelled/);
    assert.equal(observed, "early");
    await assert.rejects(runCommand(process.execPath, ["-e", "process.stdout.write('invalid');setInterval(()=>{},1000)"], {
        onStdout() { throw new Error("sensitive raw data"); },
    }), error => /activity stream/.test(error.message) && !error.message.includes("sensitive"));
});

async function uiFixture(state, respond = () => undefined) {
    state.connected ??= true;
    const nodes = new Map();
    const node = tagName => Object.assign(markdownDocument().createElement(tagName), { value: "", checked: false, disabled: false, events: {},
        addEventListener(event, handler) { this.events[event] = handler; },
        contains(target) { return descendants(this).includes(target); },
        setAttribute(name, value) { this[name] = value; },
        scrollIntoView(options) { this.scrolled = options; },
        focus() { document.activeElement = this; },
        click() { this.clicked = true; },
        requestSubmit() { this.submitted = this.events.submit({ preventDefault() {} }); } });
    const get = id => {
        if (!nodes.has(id)) nodes.set(id, node());
        return nodes.get(id);
    };
    const timers = [];
    const calls = [];
    const windowEvents = {};
    const documentEvents = {};
    const fetch = async (route, options) => {
        calls.push(route);
        const custom = await respond(route, options);
        if (custom !== undefined) return custom;
        return { ok: true, json: async () => structuredClone(route === "/state" ? state : { accepted: true }) };
    };
    const script = fixtureScript(await readFile(new URL("app.js", import.meta.url), "utf8"));
    const document = { getElementById: get, createElement: node,
        addEventListener(event, handler) { documentEvents[event] = handler; } };
    runInNewContext(script, {
        document, window: { addEventListener(event, handler) { windowEvents[event] = handler; } },
        location: { hash: "#fixture" }, fetch,
        FileReader: class {
            readAsDataURL(file) { this.result = `data:application/octet-stream;base64,${file.base64}`; this.onload(); }
        },
        setTimeout(callback) { timers.push(callback); }, renderMarkdown,
    });
    await new Promise(setImmediate);
    return { get, calls, document, async refresh() { await timers.shift()(); },
        focus() { return windowEvents.focus(); }, blur() { return windowEvents.blur(); },
        click(target) { return documentEvents.click({ target }); } };
}

test("unreachable chat updates tell the user to reconnect without browser error details", async () => {
    const { get, calls } = await uiFixture({}, () => { throw new Error("Load failed"); });
    assert.equal(get("error").textContent,
        "The canvas could not reach its local chat service. Reopen this canvas to reconnect.");
    for (const id of ["send", "new-chat", "start-new-chat", "choose-agent"]) assert.equal(get(id).disabled, true);
    assert.deepEqual(calls, ["/state"]);
});

test("chat service errors retain their details when updates fail", async () => {
    const { get } = await uiFixture({}, route => route === "/state"
        ? { ok: false, json: async () => ({ error: "Chat state expired." }) }
        : undefined);
    assert.equal(get("error").textContent,
        "This canvas could not load chat updates. Reopen this canvas to reconnect.\nDetails: Chat state expired.");
});

test("an unreachable service shows reconnect guidance when an action fails", async () => {
    const state = { status: "ready", busy: false, target, error: null, messages: [] };
    const { get } = await uiFixture(state, route => {
        if (route === "/reset") throw new Error("Load failed");
    });
    await get("new-chat").events.click();
    assert.equal(get("error").textContent,
        "The canvas could not reach its local chat service. Reopen this canvas to reconnect.");
});

test("message ellipses jump to the matching raw response or input even for repeated text and pruned response history", async () => {
    const state = { status: "ready", busy: false, target, error: null, debugRevision: 1, messages: [
        { role: "assistant", turnId: 1, text: "Same text" }, { role: "user", turnId: 2, text: "Same text" },
        { role: "assistant", turnId: 2, text: "Same text" },
    ] };
    const data = { revision: 1, messages: state.messages, responses: [
        { turnId: 1, complete: true, payload: outcome }, { turnId: 2, complete: true, payload: outcome },
    ], truncated: false };
    const { get, document, calls, refresh } = await uiFixture(state, (route, options) => {
        assert.equal(options.method, "GET");
        if (route === "/debug") return { ok: true, json: async () => data };
    });
    const inspect = index => descendants(get("messages").children[index]).find(node => node.tagName === "button" && node.textContent === "View debug");
    const menu = descendants(get("messages").children[2]).find(node => node.tagName === "details");
    menu.open = true;
    state.messages[2].streaming = true;
    await refresh();
    assert.equal(descendants(get("messages").children[2]).find(node => node.tagName === "details"), menu);
    await inspect(2).events.click();
    assert.equal(menu.open, false);
    assert.equal(get("debug-view").hidden, false);
    assert.equal(document.activeElement.id, "debug-response-2");
    assert.equal(document.activeElement.scrolled.block, "start");
    assert.deepEqual(JSON.parse(get("debug-output").textContent).messages, state.messages);
    await get("view-chat").events.click();
    assert(!descendants(get("debug-output")).some(node => node.className === "debug-entry debug-selected"));
    await get("view-debug").events.click();
    assert(!descendants(get("debug-output")).some(node => node.className === "debug-entry debug-selected"));
    await get("view-chat").events.click();
    await inspect(1).events.click();
    assert.equal(document.activeElement.id, "debug-message-2-user");
    await get("view-chat").events.click();
    data.responses.pop();
    data.revision = ++state.debugRevision;
    data.truncated = true;
    await inspect(2).events.click();
    assert.equal(document.activeElement.id, "debug-message-2-assistant");
    assert.match(get("debug-status").textContent, /omitted/);
    assert(calls.every(route => route === "/state" || route === "/debug"));
    state.messages = [];
    data.messages = [];
    data.responses = [];
    data.truncated = false;
    data.revision = ++state.debugRevision;
    await refresh();
    assert.equal(get("debug-output").textContent.includes("Same text"), false);
    assert.equal(get("error").hidden, true);
});

test("reply bubbles use the selected agent name and update without changing messages", async () => {
    const state = { status: "ready", busy: false, target, agentName: "Example Agent", messages: [
        { role: "user", text: "Hello" }, { role: "assistant", text: "Welcome", streaming: true, partial: true },
        { role: "assistant", text: "Interrupted", partial: true },
    ], error: null };
    const { get, refresh, calls } = await uiFixture(state);
    const headings = () => get("messages").children.map(article => article.children[0].children[0].textContent);
    assert.deepEqual(headings(), ["You", "Example Agent", "Example Agent · Incomplete"]);
    assert.equal(get("agent").textContent, "Example Agent");
    assert.equal(get("agent").title, `Example Agent (${target.schemaName})`);
    assert.equal(get("conn-agent").textContent, target.schemaName);
    state.agentName = "<img src=x onerror=alert(1)>";
    await refresh();
    assert.equal(headings()[1], "<img src=x onerror=alert(1)>");
    assert.equal(descendants(get("messages").children[1].children[0]).some(node => node.tagName === "img"), false);
    delete state.agentName;
    await refresh();
    assert.equal(headings()[1], target.schemaName);
    assert.equal(get("agent").textContent, target.schemaName);
    state.messages[1].streaming = false;
    await refresh();
    assert.equal(headings()[1], `${target.schemaName} · Incomplete`);
    assert(calls.every(route => route === "/state"));
});

test("connection and message overlays close outside but stay open for inside clicks", async () => {
    const state = { status: "ready", busy: false, target, messages: [
        { role: "assistant", turnId: 1, text: "Hello" },
    ], error: null };
    const { get, document, click, blur } = await uiFixture(state);
    const connection = get("connection");
    const inside = document.createElement("span");
    connection.append(inside);
    const menu = descendants(get("messages")).find(node => node.tagName === "details");
    const summary = descendants(menu).find(node => node.tagName === "summary");
    connection.open = true;
    menu.open = true;
    click(inside);
    assert.equal(connection.open, true);
    assert.equal(menu.open, false);
    menu.open = true;
    click(summary);
    assert.equal(connection.open, false);
    assert.equal(menu.open, true);
    click(get("query"));
    assert.equal(menu.open, false);
    connection.open = true;
    menu.open = true;
    blur();
    assert.equal(connection.open, false);
    assert.equal(menu.open, false);
});

test("selected files stay local until send and rejected sends restore untouched attachments", async () => {
    const html = await readFile(new URL("index.html", import.meta.url), "utf8");
    assert.match(html, /<div class="composer-input">\s*<textarea id="query"[^>]*><\/textarea>\s*<label class="file-picker" for="files"/);
    assert.match(html, /<label class="file-picker" for="files"[^>]*><svg [^>]*aria-hidden="true"[\s\S]*?<input id="files" type="file" aria-label="Attach files" multiple>/);
    const state = { status: "ready", busy: false, target, messages: [], error: null };
    let input;
    const { get, calls } = await uiFixture(state, (route, options) => {
        if (route === "/send") {
            input = JSON.parse(options.body);
            return { ok: false, json: async () => ({ error: "Not delivered" }) };
        }
    });
    get("files").files = [{ name: "study.txt", size: 3, base64: "AQID" }];
    await get("files").events.change();
    assert.equal(get("pending-files").children.length, 1);
    assert(calls.every(route => route === "/state"));
    get("query").value = "Analyze";
    get("query").events.input();
    await get("composer").events.submit({ preventDefault() {} });
    assert.deepEqual(input.files, [{ name: "study.txt", base64: "AQID" }]);
    assert.equal(get("query").value, "Analyze");
    assert.equal(get("pending-files").children.length, 1);
    await descendants(get("pending-files")).find(node => node.tagName === "button").events.click();
    assert.equal(get("pending-files").children.length, 0);
});

test("composer clears immediately and Enter sends once while Shift+Enter and IME keep native behavior", async () => {
    const state = { status: "ready", busy: false, target, messages: [], error: null };
    let sends = 0;
    const { get } = await uiFixture(state, route => {
        if (route === "/send") {
            sends++;
            state.messages.push({ role: "user", text: "Hello" }, { role: "assistant", text: "Hi" });
        }
    });
    get("query").value = "Hello";
    get("query").events.input();
    const enter = get("query").events.keydown;
    for (const modifier of [{ shiftKey: true }, { isComposing: true }, { keyCode: 229 }]) {
        enter({ key: "Enter", ...modifier, preventDefault() { assert.fail("Native multiline/composition behavior should be preserved"); } });
    }
    assert.equal(get("send").disabled, false);
    enter({ key: "Enter", repeat: true, preventDefault() {} });
    assert.equal(sends, 0);
    enter({ key: "Enter", preventDefault() {} });
    assert.equal(get("query").value, "");
    assert.equal(get("send").disabled, true);
    assert.equal(get("typing").hidden, false);
    await get("composer").events.submit({ preventDefault() {} });
    await get("composer").submitted;
    assert.equal(sends, 1);
    assert.equal(get("query").value, "");
    assert.equal(get("typing").hidden, true);
});

test("typing indicator tracks send operations, not status checks, and ends on failure", async () => {
    const state = { status: "ready", busy: false, operation: null, target, messages: [], error: null };
    const { get, refresh } = await uiFixture(state);
    assert.equal(get("typing").hidden, true);
    state.busy = true;
    state.operation = "check";
    await refresh();
    assert.equal(get("typing").hidden, true);
    state.operation = "send";
    await refresh();
    assert.equal(get("typing").hidden, false);
    assert.equal(get("typing-text").textContent, "Waiting for Copilot Studio");
    state.messages.push({ role: "assistant", text: "Partial", streaming: true });
    await refresh();
    assert.equal(get("typing-text").textContent, "Copilot Studio is replying");
    state.busy = false;
    state.operation = null;
    state.error = "Cancelled";
    await refresh();
    assert.equal(get("typing").hidden, true);
});

test("failed sends restore only an untouched draft and never overwrite a new or intentionally emptied draft", async () => {
    for (const edit of [null, "Next message", ""]) {
        let release;
        const gate = new Promise(resolve => { release = resolve; });
        const state = { status: "ready", busy: false, target, messages: [], error: null };
        const { get } = await uiFixture(state, async route => {
            if (route === "/send") {
                await gate;
                return { ok: false, json: async () => ({ error: "Send rejected" }) };
            }
        });
        get("query").value = "Original";
        get("query").events.input();
        const submit = get("composer").events.submit({ preventDefault() {} });
        assert.equal(get("query").value, "");
        if (edit !== null) {
            get("query").value = edit;
            get("query").events.input();
        }
        release();
        await submit;
        assert.equal(get("query").value, edit ?? "Original");
        assert.equal(get("typing").hidden, true);
    }
});

test("a storage failure after delivery is shown without restoring the sent draft", async () => {
    const state = { status: "ready", busy: false, target, messages: [], error: null, saveError: null, debugRevision: 1 };
    const { get, refresh } = await uiFixture(state, route => {
        if (route === "/debug") return { ok: true, json: async () => ({
            revision: state.debugRevision, messages: state.messages, responses: [], truncated: false,
        }) };
        if (route === "/send") {
            state.messages.push({ role: "user", text: "Send once" }, { role: "assistant", text: "Received" });
            state.status = "error";
            state.saveError = "This chat could not be saved and may be lost if the app closes.";
        }
    });
    get("query").value = "Send once";
    get("query").events.input();
    await get("composer").events.submit({ preventDefault() {} });
    assert.equal(get("query").value, "");
    assert.equal(get("status").hidden, true);
    assert.equal(get("error").textContent, state.saveError);
    assert.equal(get("error").hidden, false);
    await get("view-debug").events.click();
    assert.equal(get("debug-status").hidden, true);
    assert.equal(get("error").textContent, state.saveError);

    state.saveError = null;
    state.status = "ready";
    await refresh();
    assert.equal(get("error").hidden, true);
    assert.equal(get("query").value, "");
});

test("New chat and Send remain available when CLI sign-in is required", async () => {
    const state = { status: "authentication-required", busy: false, connected: false, target, messages: [], error: null };
    const { get, refresh, calls } = await uiFixture(state);
    const html = await readFile(new URL("index.html", import.meta.url), "utf8");
    assert(!html.includes('id="login"'));
    assert.equal(get("status").textContent, "Signed out");
    assert.match(get("status").title, /msagent auth create/);
    assert.equal(get("new-chat").disabled, false);
    assert.equal(get("send").disabled, true);
    await get("new-chat").events.click();
    assert(calls.includes("/reset"));
    state.connected = true;
    get("query").value = "Try again";
    get("query").events.input();
    await refresh();
    assert.equal(get("send").disabled, false);
    await get("composer").events.submit({ preventDefault() {} });
    assert(calls.includes("/send"));
    state.status = "ready";
    await refresh();
    assert.equal(get("status").hidden, true);
    assert(!calls.includes("/login"));
});

test("Chat and Debug switch over already received conversation data using only read requests and preserve the draft", async () => {
    const state = { status: "ready", busy: false, target, messages: [{ role: "assistant", text: "Earlier reply" }],
        error: null, debugRevision: 1 };
    const { get, calls } = await uiFixture(state, (route, options) => {
        assert.equal(options.method, "GET");
        if (route === "/debug") return { ok: true, json: async () => ({
            revision: state.debugRevision, messages: state.messages, truncated: false,
            responses: [{ complete: true, payload: { activities: [{ type: "event", text: "<script>alert(1)</script>" }] } }],
        }) };
    });
    assert.equal(get("debug-view").hidden, true);
    assert.equal(get("chat-view").hidden, false);
    assert(!calls.includes("/debug"));
    get("query").value = "My next question";
    await get("view-debug").events.click();
    assert.equal(get("debug-view").hidden, false);
    assert.equal(get("chat-view").hidden, true);
    assert.equal(get("view-debug")["aria-pressed"], "true");
    assert.match(get("debug-output").textContent, /<script>alert\(1\)<\/script>/);
    assert.equal(get("debug-status").hidden, true);
    assert.equal(get("composer").hidden, true);
    assert.deepEqual(JSON.parse(get("debug-output").textContent).messages, state.messages);
    await get("view-chat").events.click();
    assert.equal(get("debug-view").hidden, true);
    assert.equal(get("chat-view").hidden, false);
    assert.equal(get("composer").hidden, false);
    await get("view-debug").events.click();
    assert.equal(get("debug-view").hidden, false);
    assert(calls.every(route => route === "/state" || route === "/debug"));
    assert.equal(get("query").value, "My next question");
});

test("Debug can first open during streaming and follows the same transcript through finalization", async () => {
    const state = { status: "ready", busy: false, target, messages: [], error: null, debugRevision: 1 };
    const { get, refresh, calls } = await uiFixture(state, route => route === "/debug"
        ? { ok: true, json: async () => ({ revision: state.debugRevision, messages: state.messages, truncated: false,
            responses: [{ complete: !state.busy, payload: { activities: [] } }] }) }
        : undefined);
    state.busy = true;
    state.operation = "send";
    state.messages.push({ role: "assistant", text: "Partial", streaming: true });
    await refresh();
    assert.equal(get("view-debug").disabled, false);
    await get("view-debug").events.click();
    assert.equal(get("debug-view").hidden, false);
    assert.equal(get("chat-view").hidden, true);
    assert.deepEqual(JSON.parse(get("debug-output").textContent).messages, state.messages);
    assert.equal(get("debug-status").textContent, "");
    state.busy = false;
    state.messages[0] = { role: "assistant", text: "Final", streaming: false };
    state.debugRevision++;
    await refresh();
    assert.deepEqual(JSON.parse(get("debug-output").textContent).messages, state.messages);
    assert(calls.every(route => route === "/state" || route === "/debug"));
    await get("view-chat").events.click();
    assert.equal(get("chat-view").hidden, false);
});

test("Debug lazily collapses streaming runs while keeping final replies and other events visible in order", async () => {
    const state = { status: "ready", busy: false, target, messages: [], error: null, debugRevision: 1 };
    const debug = { revision: 1, messages: [], responses: [{ turnId: 1, complete: false, payload: {
        activities: [
            streamed("first hidden chunk", 1),
            { ...streamed("second hidden chunk", 2), type: "typing" },
            streamed("Final reply", 3, "final"),
            { ...streamed("Tool details", 1), type: "event", name: "ToolInvocation" },
            { type: "typing", entities: [{ type: "StreamInfo", streamtype: "Streaming" }], text: "third hidden chunk" },
            { type: "message", text: "Other reply" },
            streamed("Informative event", 1, "informative"),
            { type: "message", text: "Malformed stream metadata", entities: [{ type: 42 }, { type: "streaminfo", streamType: 2 }] },
        ],
    } }], truncated: true };
    const original = structuredClone(debug);
    const { get, refresh, calls } = await uiFixture(state, route => route === "/debug"
        ? { ok: true, json: async () => debug } : undefined);
    await get("view-debug").events.click();
    const groups = () => descendants(get("debug-output")).filter(node => node.tagName === "details");
    const [first, second] = groups();
    assert.equal(groups().length, 2);
    assert.equal(first.open, false);
    assert.equal(first.children[0].textContent, "2 streaming chunks hidden");
    assert.equal(second.children[0].textContent, "1 streaming chunk hidden");
    assert(!get("debug-output").textContent.includes("hidden chunk"));
    for (const text of ["Final reply", "ToolInvocation", "Other reply", "Informative event", "Malformed stream metadata"]) {
        assert(get("debug-output").textContent.includes(text));
    }
    assert.match(get("debug-status").textContent, /omitted/);
    for (const group of groups()) {
        const siblings = descendants(get("debug-output")).find(node => node.children.includes(group)).children;
        const next = siblings[siblings.indexOf(group) + 1];
        assert.equal(next.className, "debug-break");
    }

    for (const group of groups()) {
        group.open = true;
        group.events.toggle();
    }
    assert.deepEqual(JSON.parse(debugJsonText(get("debug-output"))), {
        messages: debug.messages, responses: debug.responses, truncated: true,
    });
    assert.deepEqual(debug, original);
    second.open = false;
    second.events.toggle();
    assert(!get("debug-output").textContent.includes("third hidden chunk"));
    await refresh();
    assert.equal(groups()[0], first);

    debug.responses[0].payload.activities.splice(2, 0, streamed("new streamed chunk", 3));
    debug.responses[0].complete = true;
    debug.revision = ++state.debugRevision;
    await refresh();
    assert.equal(groups()[0].open, true);
    assert.equal(groups()[0].children[0].textContent, "3 streaming chunks hidden");
    assert(get("debug-output").textContent.includes("new streamed chunk"));
    assert.equal(groups()[1].open, false);

    const expand = get("debug-expand");
    assert.equal(expand.disabled, false);
    assert.equal(expand.textContent, "Collapse chunks");
    await expand.events.click();
    assert(groups().every(group => !group.open));
    assert.equal(expand.textContent, "Expand chunks");
    await expand.events.click();
    assert(groups().every(group => group.open));
    assert.equal(expand.textContent, "Collapse chunks");
    assert.equal(expand.ariaPressed, "true");
    debug.responses[0].payload.activities.push(streamed("later chunk", 9), streamed("Later reply", 10, "final"));
    debug.revision = ++state.debugRevision;
    await refresh();
    assert.equal(groups().length, 3);
    assert(groups().every(group => group.open));
    await expand.events.click();
    assert(groups().every(group => !group.open));
    assert.equal(expand.textContent, "Expand chunks");
    assert(calls.every(route => route === "/state" || route === "/debug"));
});

test("collapsed Debug keeps a large stream out of the rendered document until expansion", async () => {
    const state = { status: "ready", busy: false, target, messages: [], error: null, debugRevision: 1 };
    const chunks = Array.from({ length: 1200 }, (_, index) => streamed(`chunk-${index} ${"x".repeat(300)}`, index));
    const debug = { revision: 1, messages: [], truncated: false, responses: [
        { turnId: 1, complete: true, payload: { activities: [...chunks, streamed("Done", 1200, "final")] } },
    ] };
    const { get } = await uiFixture(state, route => route === "/debug"
        ? { ok: true, json: async () => debug } : undefined);
    await get("view-debug").events.click();
    const output = get("debug-output");
    const group = descendants(output).find(node => node.tagName === "details");
    assert.equal(group.children[0].textContent, "1200 streaming chunks hidden");
    assert(output.textContent.length < 2000);
    assert(descendants(output).length < 100);
    group.open = true;
    group.events.toggle();
    assert(output.textContent.includes("chunk-1199"));
    assert.equal(JSON.parse(debugJsonText(output)).responses[0].payload.activities.length, 1201);
    group.open = false;
    group.events.toggle();
    assert(output.textContent.length < 2000);
});

test("Debug search finds and reveals collapsed streaming chunks without rendering every match", async () => {
    const state = { status: "ready", busy: false, target, messages: [], error: null, debugRevision: 1 };
    const debug = { revision: 1, messages: [], truncated: false, responses: [
        { turnId: 1, complete: true, payload: { activities: [
            streamed("<script>needle first</script>", 1), streamed("needle second", 2),
            streamed("Final reply", 3, "final"),
        ] } },
    ] };
    const { get, calls } = await uiFixture(state, route => route === "/debug"
        ? { ok: true, json: async () => debug } : undefined);
    await get("view-debug").events.click();
    const group = descendants(get("debug-output")).find(node => node.tagName === "details");
    const marks = () => descendants(get("debug-output")).filter(node => node.tagName === "mark");
    assert.equal(group.open, false);
    assert(!get("debug-output").textContent.includes("needle"));
    const search = get("debug-search");
    search.value = "needle";
    search.events.input();
    assert.equal(get("debug-search-count").textContent, "1 of 2");
    assert.equal(group.open, true);
    assert.equal(marks().length, 1);
    assert.equal(marks()[0].scrolled.block, "center");
    group.events.toggle();
    assert.equal(marks().length, 1);
    get("debug-next").events.click();
    assert.equal(get("debug-search-count").textContent, "2 of 2");
    assert.equal(marks().length, 1);
    assert(!descendants(get("debug-output")).some(node => node.tagName === "script"));
    assert.deepEqual(JSON.parse(debugJsonText(get("debug-output"))).responses, debug.responses);

    group.open = false;
    group.events.toggle();
    assert.equal(group.open, false);
    assert(!get("debug-output").textContent.includes("needle"));
    get("debug-previous").events.click();
    assert.equal(group.open, true);
    assert.equal(get("debug-search-count").textContent, "1 of 2");
    assert.equal(marks().length, 1);
    search.events.keydown({ key: "Escape", preventDefault() {} });
    assert.equal(marks().length, 0);
    assert.equal(group.open, false);
    assert.deepEqual(JSON.parse(debugJsonText(get("debug-output"))).responses, debug.responses);

    search.value = "needle";
    search.events.input();
    assert.equal(group.open, true);
    search.value = "Final reply";
    search.events.input();
    assert.equal(group.open, false);
    search.value = "needle";
    search.events.input();
    await get("debug-expand").events.click();
    assert.equal(group.open, false);
    group.events.toggle();
    assert.equal(group.open, false);
    assert(calls.every(route => route === "/state" || route === "/debug"));
});

test("debug search finds literal text case-insensitively and navigates without changing the JSON or draft", async () => {
    const state = { status: "ready", busy: false, target, messages: [
        { role: "user", turnId: 1, text: "İ Token token <script>" },
    ], error: null, debugRevision: 1 };
    const debug = { revision: 1, messages: state.messages, truncated: false, responses: [
        { turnId: 1, complete: true, payload: { text: "TOKEN", literal: ".*" } },
    ] };
    const { get, calls } = await uiFixture(state, (route, options) => {
        assert.equal(options.method, "GET");
        if (route === "/debug") return { ok: true, json: async () => debug };
    });
    const marks = () => descendants(get("debug-output")).filter(node => node.tagName === "mark");
    const search = get("debug-search");
    get("query").value = "Unsent draft";
    await get("view-debug").events.click();
    const original = get("debug-output").textContent;
    search.value = "tOkEn";
    search.events.input();
    assert.equal(get("debug-search-count").textContent, "1 of 3");
    assert.equal(marks()[0].textContent, "Token");
    assert.equal(marks()[0].scrolled.block, "center");
    assert.equal(get("debug-next").disabled, false);
    get("debug-next").events.click();
    assert.equal(get("debug-search-count").textContent, "2 of 3");
    assert.equal(marks()[0].textContent, "token");
    search.events.keydown({ key: "Enter", preventDefault() {} });
    assert.equal(get("debug-search-count").textContent, "3 of 3");
    assert.equal(marks()[0].textContent, "TOKEN");
    get("debug-next").events.click();
    assert.equal(get("debug-search-count").textContent, "1 of 3");
    search.events.keydown({ key: "Enter", shiftKey: true, preventDefault() {} });
    assert.equal(get("debug-search-count").textContent, "3 of 3");
    get("debug-previous").events.click();
    assert.equal(get("debug-search-count").textContent, "2 of 3");
    search.events.keydown({ key: "Enter", isComposing: true, preventDefault() { assert.fail("IME input should remain native"); } });
    assert.equal(get("debug-search-count").textContent, "2 of 3");
    search.value = ".*";
    search.events.input();
    assert.equal(get("debug-search-count").textContent, "1 of 1");
    assert.equal(marks()[0].textContent, ".*");
    search.value = "<script>";
    search.events.input();
    assert.equal(marks()[0].textContent, "<script>");
    assert(!descendants(get("debug-output")).some(node => node.tagName === "script"));
    search.value = "not present";
    search.events.input();
    assert.equal(get("debug-search-count").textContent, "No matches");
    assert.equal(get("debug-next").disabled, true);
    assert.equal(marks().length, 0);
    search.events.keydown({ key: "Escape", preventDefault() {} });
    assert.equal(search.value, "");
    assert.equal(get("debug-search-count").textContent, "");
    assert.equal(get("debug-output").textContent, original);
    assert.deepEqual(JSON.parse(original), { messages: debug.messages, responses: debug.responses, truncated: false });
    assert.equal(get("query").value, "Unsent draft");
    assert(calls.every(route => route === "/state" || route === "/debug"));
});

test("debug search highlights matches across JSON segments and remains bounded for many results", async () => {
    const state = { status: "ready", busy: false, target, messages: [], error: null, debugRevision: 1 };
    const debug = { revision: 1, messages: [], responses: [], truncated: false };
    const { get, refresh } = await uiFixture(state, route => route === "/debug"
        ? { ok: true, json: async () => debug } : undefined);
    await get("view-debug").events.click();
    const marks = () => descendants(get("debug-output")).filter(node => node.tagName === "mark");
    const search = get("debug-search");
    search.value = "[]";
    search.events.input();
    assert.equal(get("debug-search-count").textContent, "1 of 2");
    assert.equal(marks().map(mark => mark.textContent).join(""), "[]");
    assert.equal(marks().length, 2);
    assert.deepEqual(JSON.parse(get("debug-output").textContent).messages, []);
    get("debug-next").events.click();
    assert.equal(get("debug-search-count").textContent, "2 of 2");
    assert.equal(marks().map(mark => mark.textContent).join(""), "[]");

    debug.responses.push({ turnId: 1, payload: { text: "a".repeat(1_000_000) } });
    debug.revision = ++state.debugRevision;
    await refresh();
    search.value = "a";
    search.events.input();
    const text = get("debug-output").textContent;
    let total = 0;
    for (let index = text.indexOf("a"); index !== -1; index = text.indexOf("a", index + 1)) total++;
    assert.equal(get("debug-search-count").textContent, `1 of ${total}`);
    assert.equal(marks().length, 1);
    get("debug-previous").events.click();
    assert.equal(get("debug-search-count").textContent, `${total} of ${total}`);
    assert.equal(marks().length, 1);
    assert.equal(JSON.parse(get("debug-output").textContent).responses[0].payload.text.length, 1_000_000);
});

test("debug search tracks streamed data and coexists with bubble-to-debug navigation", async () => {
    const state = { status: "ready", busy: true, operation: "send", target,
        messages: [{ role: "assistant", turnId: 1, text: "needle", streaming: true }], error: null, debugRevision: 1 };
    const debug = { revision: 1, messages: state.messages, truncated: false,
        responses: [{ turnId: 1, complete: false, payload: { text: "needle" } }] };
    const { get, document, refresh } = await uiFixture(state, route => route === "/debug"
        ? { ok: true, json: async () => debug } : undefined);
    await get("view-debug").events.click();
    const search = get("debug-search");
    search.value = "needle";
    search.events.input();
    get("debug-next").events.click();
    assert.equal(get("debug-search-count").textContent, "2 of 2");
    state.messages[0].text = "needle needle";
    debug.revision = ++state.debugRevision;
    await new Promise(resolve => setTimeout(resolve, 550));
    await refresh();
    assert.equal(get("debug-search-count").textContent, "2 of 3");
    assert.equal(descendants(get("debug-output")).find(node => node.tagName === "mark").scrolled, undefined);
    assert.deepEqual(JSON.parse(get("debug-output").textContent).messages, state.messages);
    await get("view-chat").events.click();
    const inspect = descendants(get("messages").children[0]).find(node => node.tagName === "button" && node.textContent === "View debug");
    await inspect.events.click();
    assert.equal(document.activeElement.id, "debug-response-1");
    assert.equal(search.value, "needle");
    assert.equal(get("debug-search-count").textContent, "2 of 3");
    state.busy = false;
    state.messages = [];
    debug.messages = [];
    debug.responses = [];
    debug.revision = ++state.debugRevision;
    await refresh();
    assert.equal(get("debug-search-count").textContent, "No matches");
    assert.equal(get("debug-next").disabled, true);
    assert.equal(get("error").hidden, true);
});

test("Connect starts ask without extra approval or a query and only its returned conversation is continued", async () => {
    const { chat, calls } = fixture();
    await chat.start("check");
    assert.equal(chat.snapshot().connected, false);
    assert(!calls.some(args => args[1] === "ask" && !args.includes("--help")));
    await chat.start("connect", { query: "Must not be sent" });
    assert(!calls.some(args => args[0] === "auth" && ["login", "create"].includes(args[1])));
    const start = calls.filter(args => args[1] === "ask" && !args.includes("--help"));
    assert.equal(start.length, 1);
    assert(start[0].includes("--start-conversation"));
    assert(!start[0].includes("--query"));
    assert(!start[0].includes("--conversation-id"));
    assert.equal(chat.snapshot().connected, true);
    assert(chat.snapshot().messages.every(message => message.role === "assistant"));
    assert.throws(() => chat.start("connect"), /Already connected/);
    await chat.start("send", { query: "My first question" });
    const query = calls.filter(args => args[1] === "ask" && !args.includes("--help")).at(-1);
    assert(!query.includes("--start-conversation"));
    assert.equal(query[query.indexOf("--conversation-id") + 1], "conversation-1");
    await chat.start("reset", { reconnect: true });
    const reset = calls.filter(args => args[1] === "ask" && !args.includes("--help")).at(-1);
    assert(reset.includes("--start-conversation"));
    assert(!reset.includes("--conversation-id"));
    assert(chat.snapshot().messages.every(message => message.role === "assistant"));
});

test("failed connection starts are not replayed even when no query was attempted", async () => {
    const { chat, calls } = fixture(args => args[1] === "ask" && !args.includes("--help")
        ? json({ ...outcome, success: false, status: "failed", exitCode: 1, queryDelivery: "not-attempted" }, 1)
        : undefined);
    await chat.start("connect");
    assert.equal(chat.snapshot().status, "uncertain");
    assert.match(chat.snapshot().conversationIssue, /^The chat couldn't start\. CLI operation failed\..* Start a new chat to try again\.$/);
    assert.equal(chat.snapshot().connected, false);
    await chat.start("connect");
    assert.equal(calls.filter(args => args[1] === "ask" && !args.includes("--help")).length, 1);
});

test("New chat preserves composed text and the recovery card replaces it when continuation is unsafe", async () => {
    const html = await readFile(new URL("index.html", import.meta.url), "utf8");
    assert.match(html, /<button id="new-chat"[^>]*>New chat<\/button>\s*<details id="connection">/);
    assert(!html.includes('id="connect"'));
    assert(!html.includes('id="reset"'));
    assert(!html.includes("Refresh status"));
    assert.match(html, /<dt>Account<\/dt><dd id="conn-account">[\s\S]*?<dt>Agent<\/dt><dd id="conn-agent">/);
    assert.match(html, /<dt>Deployment<\/dt><dd id="conn-deployment">[\s\S]*?<dt>Environment ID<\/dt><dd id="conn-environment">[\s\S]*?<dt>Tenant ID<\/dt><dd id="conn-tenant">/);
    assert.match(html, /id="conversation-issue"/);
    assert.match(html, /id="start-new-chat"[^>]*>Start new chat/);
    assert(!html.includes('<label for="query">'));
    const state = { status: "ready", busy: true, operation: "connect", connected: false, target, messages: [], error: null };
    const { get, refresh, calls } = await uiFixture(state, (route, options) => {
        if (route === "/reset") {
            assert.equal(JSON.parse(options.body).reconnect, true);
            state.busy = true;
            state.operation = "connect";
        }
    });
    assert.equal(get("typing").hidden, false);
    assert.equal(get("new-chat").disabled, true);
    get("query").value = "My first question";
    get("query").events.input();
    assert.equal(get("send").disabled, true);
    state.busy = false;
    state.connected = true;
    state.operation = null;
    state.messages.push({ role: "assistant", text: "Welcome" });
    await refresh();
    assert.equal(get("query").value, "My first question");
    assert.equal(get("new-chat").disabled, false);
    assert.equal(get("send").disabled, false);
    assert.equal(get("status").hidden, true);
    await get("new-chat").events.click();
    assert.equal(get("typing").hidden, false);
    assert.equal(calls.filter(route => route === "/reset").length, 1);
    assert(!calls.includes("/connect"));
    state.busy = false;
    state.connected = false;
    state.status = "uncertain";
    state.operation = null;
    await refresh();
    assert.equal(get("new-chat").disabled, false);
});

test("conversation recovery is shown once in chat and the composer is disabled until reset", async () => {
    const state = { status: "uncertain", busy: false, connected: false, target, messages: [
        { role: "user", text: "Did that go through?" }, { role: "assistant", text: "Partial", partial: true },
    ], error: "Remote effects may have occurred.", conversationIssue: "We couldn't confirm whether the agent received the last message. Start a new chat to continue." };
    const { get, calls, refresh } = await uiFixture(state, route => {
        if (route === "/reset") {
            state.status = "ready";
            state.connected = true;
            state.error = null;
            state.conversationIssue = null;
            state.messages = [];
        }
        if (route === "/debug") return { ok: true, json: async () => ({
            revision: 1, messages: state.messages, responses: [], truncated: false,
        }) };
    });
    assert.equal(get("status").hidden, true);
    assert.equal(get("error").hidden, true);
    assert.equal(get("conversation-issue").hidden, false);
    assert.equal(get("conversation-issue-message").textContent, state.conversationIssue);
    assert.equal(get("new-chat").hidden, false);
    assert.equal(get("start-new-chat").hidden, false);
    assert.equal(get("query").disabled, true);
    assert.equal(get("query").placeholder, "Start a new chat to send a message.");
    assert.equal(get("files").disabled, true);
    assert.equal(get("send").disabled, true);
    state.status = "authentication-required";
    await refresh();
    assert.equal(get("status").hidden, false);
    assert.equal(get("status").textContent, "Signed out");
    assert.equal(get("conversation-issue").hidden, false);
    state.status = "uncertain";
    state.error = "Remote effects may have occurred.";
    await refresh();
    await get("view-debug").events.click();
    assert.equal(get("conversation-issue").hidden, true);
    assert.equal(get("new-chat").hidden, false);
    assert.match(get("debug-status").textContent, /Last operation: Remote effects may have occurred/);
    await get("view-chat").events.click();
    assert.equal(get("new-chat").hidden, false);
    await get("start-new-chat").events.click();
    assert(calls.includes("/reset"));
    assert.equal(get("conversation-issue").hidden, true);
    assert.equal(get("new-chat").hidden, false);
    assert.equal(get("query").disabled, false);
    assert.equal(get("query").placeholder, "Ask your agent…");
    assert.equal(get("send").disabled, true);
});

test("connection status is hidden when ready and rechecked automatically when focus returns", async () => {
    const state = { status: "ready", busy: false, target, messages: [], error: null };
    const { get, focus, calls, refresh } = await uiFixture(state, route => {
        if (route === "/check") {
            state.status = "ready";
            state.error = null;
        }
    });
    assert.equal(get("status").hidden, true);
    assert.equal(get("conn-environment").textContent, target.environmentId);
    assert.equal(get("conn-tenant").textContent, target.tenantId);
    state.status = "error";
    state.error = "Agent could not be reached.";
    await refresh();
    await focus();
    assert(calls.includes("/check"));
    assert.equal(get("status").hidden, true);
    state.status = "authentication-required";
    state.error = "The CLI is signed out.";
    await refresh();
    assert.equal(get("status").hidden, false);
    assert.equal(get("status").textContent, "Signed out");
    await focus();
    assert.equal(get("status").hidden, true);
    assert(calls.filter(route => route === "/check").length === 2);
});

test("menu setup hides chat controls and requests agent selection only after a click", async () => {
    const state = new SetupChat(async () => {}).snapshot();
    const { get, calls, focus } = await uiFixture(state, route => {
        if (route === "/setup") state.setupRequested = true;
    });
    assert.equal(get("setup").hidden, false);
    for (const id of ["composer", "connection", "view-controls", "new-chat", "chat-view", "debug-view"]) {
        assert.equal(get(id).hidden, true, id);
    }
    assert.equal(get("choose-agent").disabled, false);
    await focus();
    assert(calls.every(route => route === "/state"));
    await get("choose-agent").events.click();
    assert.equal(calls.filter(route => route === "/setup").length, 1);
    assert.equal(get("choose-agent").disabled, true);
    assert.match(get("setup-description").textContent, /Continue in the main Copilot chat/);
    assert(!calls.some(route => ["/check", "/connect", "/reset", "/send"].includes(route)));
});

test("setup requests are deduplicated and failures remain visible until an explicit retry", async () => {
    let requests = 0;
    let release;
    const chat = new SetupChat(() => {
        requests++;
        return new Promise(resolve => { release = resolve; });
    });
    assert.throws(() => chat.start("connect"), /Choose an agent/);
    assert.throws(() => chat.start("check"), /Choose an agent/);
    const pending = chat.start("setup");
    assert.throws(() => chat.start("setup"), /Continue choosing/);
    await Promise.resolve();
    assert.equal(requests, 1);
    release();
    await pending;
    assert.equal(chat.snapshot().setupRequested, true);
    assert.throws(() => chat.start("setup"), /Continue choosing/);
    await chat.close();
    assert.throws(() => chat.start("setup"), /closed/);

    let fail = true;
    const failed = new SetupChat(async () => {
        requests++;
        if (fail) throw new Error("Copilot is unavailable.");
    });
    await failed.start("setup");
    assert.equal(requests, 2);
    assert.equal(failed.snapshot().setupRequested, false);
    assert.match(failed.snapshot().error, /Copilot is unavailable/);
    const state = failed.snapshot();
    const { get } = await uiFixture(state);
    assert.equal(get("choose-agent").disabled, false);
    assert.match(get("error").textContent, /Copilot is unavailable/);
    assert.equal(get("status").hidden, true);
    fail = false;
    await failed.start("setup");
    assert.equal(requests, 3);
    assert.equal(failed.snapshot().error, null);
    assert.equal(failed.snapshot().setupRequested, true);
    await failed.close();
});

test("the declared canvas opens without input and setup never invokes the CLI", async () => {
    const script = fixtureScript(await readFile(new URL("extension.mjs", import.meta.url), "utf8"));
    let declaration;
    let cliCalls = 0;
    const requests = [];
    await runInNewContext(`(async () => { ${script} })()`, {
        ChatRegistry, SetupChat, startServer, validateConfiguredInput, validateTarget,
        Chat: class { constructor() { throw new Error("Unexpected configured chat."); } },
        StartedChats: class { async load() { return null; } async release() { return false; } },
        resolveExecutable: async () => { cliCalls++; throw new Error("Unexpected CLI invocation."); },
        pruneStarted: async () => [],
        attachChat: async () => { throw new Error("Unexpected conversation start."); },
        createCanvas: options => options,
        CanvasError: class extends Error {},
        joinSession: async options => {
            declaration = options.canvases[0];
            return { sessionId: "menu-session", send: async input => { requests.push(input); }, log() {} };
        },
        process: { once() {} },
    });
    assert.match(declaration.description, /select an agent and deployment first/);
    assert.match(declaration.description, /then open with the complete target/);
    assert(declaration.inputSchema.anyOf.some(schema => schema.type === "null"));
    assert(declaration.inputSchema.anyOf.some(schema => schema.type === "object" && schema.maxProperties === 0));
    for (const [index, input] of [undefined, null, {}].entries()) {
        const context = { sessionId: "menu-session", instanceId: `menu-${index}`, input };
        const opened = await declaration.open(context);
        const url = new URL(opened.url);
        const headers = { Authorization: `Bearer ${url.hash.slice(1)}`, Origin: url.origin, "Content-Type": "application/json" };
        try {
            const state = await (await fetch(`${url.origin}/state`, { headers })).json();
            assert.equal(state.setupRequired, true);
            assert.equal(state.connected, false);
            assert.equal(requests.length, index);
            assert.equal(cliCalls, 0);
            const status = await declaration.actions.find(action => action.name === "get_status").handler(context);
            assert.equal(status.status, "setup-required");
            assert.equal(status.messageCount, 0);
            assert.equal((await fetch(`${url.origin}/setup`, { method: "POST", body: "{}", headers: { Origin: url.origin, "Content-Type": "application/json" } })).status, 403);
            assert.equal((await fetch(`${url.origin}/setup`, { method: "POST", body: "{}", headers: { ...headers, Origin: "https://attacker.test" } })).status, 403);
            assert.equal((await fetch(`${url.origin}/connect`, { method: "POST", body: "{}", headers })).status, 400);
            assert.equal((await fetch(`${url.origin}/files/${"a".repeat(32)}`, { headers })).status, 400);
            assert.equal((await fetch(`${url.origin}/setup`, { method: "POST", body: "{}", headers })).status, 202);
            assert.equal(requests.length, index + 1);
            assert.equal(cliCalls, 0);
            assert.equal(requests[index].source, "system");
            assert.equal(requests[index].mode, "enqueue");
            assert.match(requests[index].prompt, /main chat/);
            assert.match(requests[index].prompt, /msagent auth create/);
            assert.match(requests[index].prompt, /agentName/);
            const requested = await (await fetch(`${url.origin}/state`, { headers })).json();
            assert.equal(requested.setupRequested, true);
            assert.equal((await fetch(`${url.origin}/setup`, { method: "POST", body: "{}", headers })).status, 400);
        } finally {
            await declaration.onClose(context);
        }
        await assert.rejects(fetch(url.origin));
    }
});

test("a menu canvas is configured in place and resumes without starting another conversation", async () => {
    const directory = await mkdtemp(path.join(tmpdir(), "canvas-handoff-"));
    const script = fixtureScript(await readFile(new URL("extension.mjs", import.meta.url), "utf8"));
    const chats = [];
    const requests = [];
    const input = { chatId: "selected", agentName: "Example Agent", target };
    class FixtureChat extends Chat {
        constructor(executable, selected) {
            const { chat, calls } = fixture(() => undefined, selected);
            super(executable, selected, chat.runner);
            chats.push({ chat: this, calls });
        }
    }
    async function provider() {
        let declaration;
        const handlers = {};
        let onExit;
        const exited = new Promise(resolve => { onExit = resolve; });
        await runInNewContext(`(async () => { ${script} })()`, {
            ChatRegistry, SetupChat, startServer, validateConfiguredInput, validateTarget, attachChat,
            Chat: FixtureChat,
            StartedChats: class extends StartedChats { constructor() { super(directory); } },
            resolveExecutable: async () => process.execPath,
            pruneStarted: async () => [],
            createCanvas: options => options,
            CanvasError: class extends Error {
                constructor(code, message) { super(message); this.code = code; }
            },
            joinSession: async options => {
                declaration = options.canvases[0];
                return { sessionId: "session-1", send: async request => { requests.push(request); }, log() {} };
            },
            process: { once: (signal, callback) => { handlers[signal] = callback; }, exit: onExit },
        });
        return { declaration, shutdown: async () => { handlers.SIGTERM(); assert.equal(await exited, 0); } };
    }
    try {
        const first = await provider();
        const context = { sessionId: "session-1", instanceId: "menu-panel" };
        const opened = await first.declaration.open(context);
        const url = new URL(opened.url);
        const headers = { Authorization: `Bearer ${url.hash.slice(1)}`, Origin: url.origin, "Content-Type": "application/json" };
        assert.equal((await (await fetch(`${url.origin}/state`, { headers })).json()).setupRequired, true);
        assert.equal((await fetch(`${url.origin}/setup`, { method: "POST", headers, body: "{}" })).status, 202);
        assert.match(requests[0].prompt, /instanceId "menu-panel"/);
        assert.match(requests[0].prompt, /configure action/);
        assert.match(requests[0].prompt, /Do not open a new canvas/);
        const configure = first.declaration.actions.find(action => action.name === "configure");
        await configure.handler({ ...context, input });
        await chats[0].chat.pending;
        await chats[0].chat.persisting;
        const state = await (await fetch(`${url.origin}/state`, { headers })).json();
        assert.equal(state.connected, true);
        assert.equal(state.agentName, "Example Agent");
        assert.equal(state.setupRequired, undefined);
        assert.equal(chats[0].calls.filter(args => args.includes("--start-conversation")).length, 1);
        await assert.rejects(configure.handler({ ...context, input }), /already has an agent/);
        assert.equal((await new StartedChats(directory).load("session-1", "menu-panel")).openInput.agentName, input.agentName);
        await first.shutdown();
        await assert.rejects(fetch(url.origin));

        const second = await provider();
        const restored = await second.declaration.open(context);
        const restoredUrl = new URL(restored.url);
        const restoredHeaders = { Authorization: `Bearer ${restoredUrl.hash.slice(1)}` };
        const resumed = await (await fetch(`${restoredUrl.origin}/state`, { headers: restoredHeaders })).json();
        assert.equal(resumed.connected, true);
        assert.equal(resumed.agentName, "Example Agent");
        assert.equal(chats[1].calls.filter(args => args.includes("--start-conversation")).length, 0);
        assert.equal(resumed.messages.length, 1);
        await second.declaration.onClose(context);
        assert.equal(await new StartedChats(directory).load("session-1", "menu-panel"), null);
    } finally {
        await rm(directory, { recursive: true, force: true });
    }
});

test("closing while setup is being saved never starts a live conversation", async () => {
    const directory = await mkdtemp(path.join(tmpdir(), "canvas-handoff-close-"));
    try {
        const store = new StartedChats(directory);
        const save = store.save.bind(store);
        let saved, release;
        const waiting = new Promise(resolve => { saved = resolve; });
        const proceed = new Promise(resolve => { release = resolve; });
        store.save = async (...args) => { await save(...args); saved(); await proceed; };
        const { chat, calls } = fixture();
        await chat.verifyOpen();
        const input = { chatId: "selected", agentName: "Selected", target };
        let closed = false;
        const pending = attachChat(chat, store, "session-1", "panel", input, () => closed);
        await waiting;
        const record = await store.load("session-1", "panel");
        assert.equal(record.openInput.agentName, "Selected");
        assert.match(record.conversationIssue, /may not have finished starting/);
        assert.equal(calls.filter(args => args.includes("--start-conversation")).length, 0);
        closed = true;
        release();
        await assert.rejects(pending, /closed during setup/);
        assert.equal(calls.filter(args => args.includes("--start-conversation")).length, 0);
        assert.equal(await store.load("session-1", "panel"), null);
    } finally {
        await rm(directory, { recursive: true, force: true });
    }
});

for (const operation of ["send", "reset"]) {
    test(`queued Copilot ${operation} does not run after the panel closes`, async () => {
        let waiting, finish;
        const waited = new Promise(resolve => { waiting = resolve; });
        const turn = new Promise(resolve => { finish = resolve; });
        let starts = 0;
        const chat = {
            snapshot: () => ({ busy: true, messages: [] }),
            get pending() { waiting(); return turn; },
            start() { starts++; },
        };
        const registry = new ChatRegistry(async () => ({ chat, server: { close: async () => { finish(); } } }));
        await registry.open("panel", { chatId: "closing", target });
        const action = operation === "send"
            ? registry.send("panel", "Do not send", async () => {})
            : registry.newChat("panel");
        const rejected = assert.rejects(action, /closed/);
        await waited;
        await registry.close("panel");
        await rejected;
        assert.equal(starts, 0);
    });
}

for (const greeting of [false, true]) {
    test(`transcript capacity remains restorable ${greeting ? "with" : "without"} an opening greeting`, async () => {
        const { chat, calls } = fixture();
        if (greeting) await chat.start("connect");
        for (let index = 0; index < 49; index++) await chat.start("send", { query: `Message ${index}` });
        if (!greeting) await chat.start("send", { query: "Last permitted exchange" });
        const before = calls.length;
        assert.throws(() => chat.start("send", { query: "Over the limit" }), /reached its limit/);
        assert.equal(calls.length, before);
        assert.equal(chat.snapshot().messages.length, greeting ? 99 : 100);
        const restored = fixture().chat;
        await restored.start("check");
        assert.equal(restored.restore(chat.saved()), true);
        assert.deepEqual(restored.snapshot().messages, chat.saved().messages);
    });
}

for (const active of ["connect", "send"]) {
    for (const operation of ["send", "reset"]) {
        test(`configured menu canvas waits for ${active} before Copilot ${operation}`, async () => {
            let hold = active === "connect";
            let entered, finish;
            const started = new Promise(resolve => { entered = resolve; });
            const current = fixture(args => hold && args[1] === "ask" && !args.includes("--help")
                ? new Promise(resolve => { entered(); finish = () => { hold = false; resolve(json(outcome)); }; })
                : undefined);
            const setup = new SetupChat(async () => {}, async () => {
                current.chat.start("connect");
                return current.chat;
            });
            const registry = new ChatRegistry(async () => ({ chat: setup, server: { close: () => setup.close() } }));
            await registry.open("panel", {});
            await registry.configure("panel", { chatId: "configured", target });
            if (active === "send") {
                await setup.pending;
                hold = true;
                setup.start("send", { query: "Active user message" });
            }
            await started;
            try {
                assert.equal(setup.pending, current.chat.pending);
                const action = operation === "send"
                    ? registry.send("panel", "Queued Copilot message", async () => {})
                    : registry.newChat("panel");
                action.catch(() => {});
                await new Promise(setImmediate);
                finish();
                const result = await action;
                await setup.pending;
                assert.equal(result.status, operation === "send" ? "sending" : "ready");
                assert.equal(setup.snapshot().messages.length, operation === "reset" ? 1 : active === "connect" ? 3 : 5);
            } finally {
                finish();
                await registry.close("panel");
            }
        });
    }
}

test("closing a configured menu canvas cancels its active opening conversation", async () => {
    let entered, finish, signal;
    const started = new Promise(resolve => { entered = resolve; });
    const current = fixture((args, options) => args[1] === "ask" && !args.includes("--help")
        ? new Promise((resolve, reject) => {
            signal = options.signal;
            finish = () => resolve(json(outcome));
            signal.addEventListener("abort", () => reject(new Error("Cancelled")), { once: true });
            entered();
        })
        : undefined);
    const setup = new SetupChat(async () => {}, async () => {
        current.chat.start("connect");
        return current.chat;
    });
    await setup.configure({ chatId: "closing", target });
    await started;
    const closing = setup.close();
    try {
        await new Promise(setImmediate);
        assert.equal(signal.aborted, true);
    } finally {
        finish();
        await closing;
    }
    assert.equal(current.chat.snapshot().busy, false);
});

test("Copilot's sends return at once and push only their own replies", async () => {
    let fail = false;
    const current = fixture(args => fail && args[1] === "ask"
        ? json({ success: false, exitCode: 3, errorKind: "authentication", errorMessage: "Sign in again." }, 3) : undefined);
    const registry = new ChatRegistry(async () => ({ chat: current.chat, server: { close: async () => {} } }));
    const { chat } = await registry.open("panel", { chatId: "copilot", target });
    const notices = [];
    let delivered;
    const notify = result => { notices.push(result); delivered(); };
    const next = () => new Promise(resolve => { delivered = resolve; });
    chat.start("connect");
    let arrived = next();
    assert.deepEqual(await registry.send("panel", "While connecting", notify), { status: "sending" });
    await arrived;
    await chat.start("send", { query: "From the user" });
    arrived = next();
    await registry.send("panel", "From Copilot", notify);
    await arrived;
    assert.deepEqual(notices, [
        { status: "ready", replies: [{ role: "assistant", text: "Hello" }] },
        { status: "ready", replies: [{ role: "assistant", text: "Hello" }] },
    ]);
    const transcript = await registry.transcript("panel");
    assert.deepEqual(transcript.messages.map(message => [message.role, message.text]), [
        ["assistant", "Hello"], ["user", "While connecting"], ["assistant", "Hello"], ["user", "From the user"], ["assistant", "Hello"],
        ["user", "From Copilot"], ["assistant", "Hello"],
    ]);
    fail = true;
    arrived = next();
    await registry.send("panel", "Again", notify);
    await arrived;
    assert.match(notices[2].error, /Sign in again/);
    assert.match(replyNotification("panel", notices[2]), /send failed: .*Sign in again/);
    assert.match(replyNotification("panel", notices[0]), /untrusted agent output[\s\S]*"text":"Hello"/);
    await assert.rejects(registry.send("panel", "", notify), /Enter a message/);
    fail = false;
    chat.start("send", { query: "Still replying" });
    assert.deepEqual(await registry.newChat("panel"), { status: "ready", error: null, messageCount: 1 });
    assert.deepEqual((await registry.transcript("panel")).messages, [{ role: "assistant", text: "Hello" }]);
    await registry.close("panel");
    await assert.rejects(registry.newChat("panel"), /closed/);
    await assert.rejects(registry.send("panel", "Closed", notify), /closed/);
    await assert.rejects(registry.transcript("panel"), /closed/);
    assert.equal(notices.length, 3);
});

function fixture(override = () => undefined, selected = target) {
    const calls = [];
    const runner = async (executable, args, options) => {
        calls.push(args);
        const custom = await override(args, options);
        if (custom !== undefined) return custom;
        if (args.includes("--help")) return { code: 0, output: help };
        if (args[0] === "auth") return json(account);
        return json(outcome);
    };
    return { chat: new Chat(process.execPath, selected, runner), calls };
}

test("panels with the same chat ID keep their conversations independent", async () => {
    const fixtures = [];
    const closed = [];
    const registry = new ChatRegistry(async input => {
        const current = fixture(() => undefined, input.target);
        fixtures.push(current);
        return { chat: current.chat, server: { close: async () => { closed.push(current); } } };
    });
    const input = { chatId: "shared", target };
    const [one, two] = await Promise.all([registry.open("one", input), registry.open("two", input)]);
    assert.equal(fixtures.length, 2);
    assert.notEqual(one, two);
    await Promise.all([one.chat.start("connect"), two.chat.start("connect")]);
    await two.chat.start("send", { query: "Keep this conversation" });
    const before = two.chat.snapshot();
    await one.chat.start("reset", { reconnect: true });
    assert.deepEqual(two.chat.snapshot(), before);
    assert.equal(fixtures[0].calls.filter(args => args[1] === "ask" && !args.includes("--help")).length, 2);
    assert.equal(fixtures[1].calls.filter(args => args[1] === "ask" && !args.includes("--help")).length, 2);
    assert.equal(await registry.open("one", { ...input, agentName: "Test agent" }), one);
    assert.equal(await registry.open("one", { target: Object.fromEntries(Object.entries(input.target).reverse()), chatId: input.chatId }), one);
    assert.equal(one.chat.snapshot().agentName, "Test agent");
    assert.equal(two.chat.snapshot().agentName, undefined);
    const three = await registry.open("three", { ...input, target: { ...target, schemaName: "other" } });
    assert.notEqual(three, one);
    await assert.rejects(registry.open("one", { ...input, chatId: "other" }), /new panel/);
    await registry.close("one");
    assert.deepEqual(closed, [fixtures[0]]);
    assert.equal((await registry.status("two")).messageCount, before.messages.length);
    await registry.close("two");
    await assert.rejects(registry.status("two"), /closed/);
    assert((await registry.closeAll()).every(result => result.status === "fulfilled"));
    assert.deepEqual(closed, fixtures);
});

test("canvas starts are tracked independently across restart and released when closed", async () => {
    const directory = await mkdtemp(path.join(tmpdir(), "started-chats-"));
    try {
        const first = new StartedChats(directory);
        const peer = new StartedChats(directory);
        const claims = await Promise.all([first.claim("session-1", "panel.a"), peer.claim("session-1", "panel.a")]);
        assert.deepEqual(claims.sort(), [false, true]);
        assert.equal(await first.claim("session-1", "panel-b"), true);
        assert.equal(await first.claim("session-2", "panel.a"), true);
        const restarted = new StartedChats(directory);
        assert.equal(await restarted.claim("session-1", "panel.a"), false);
        assert.equal(await restarted.claim("session-1", "panel-b"), false);
        assert.equal(await restarted.claim("session-1", "panel-c"), true);
        assert.equal(await restarted.release("session-1", "panel.a"), true);
        assert.equal(await restarted.claim("session-1", "panel.a"), true);
        assert.equal(await restarted.claim("session-1", "panel-b"), false);
        await assert.rejects(restarted.claim("", "panel-a"), /Invalid canvas/);
        await writeFile(path.join(directory, "blocked-session"), "not a directory");
        await assert.rejects(restarted.claim("blocked-session", "panel-a"));
    } finally {
        await rm(directory, { recursive: true, force: true });
    }
});

test("saved conversations unused for 30 days and interrupted writes are pruned, except open panels", async () => {
    const directory = await mkdtemp(path.join(tmpdir(), "started-chats-"));
    try {
        const store = new StartedChats(directory);
        const old = new Date(Date.now() - 31 * 24 * 60 * 60 * 1000);
        for (const [session, instance, unused] of [["current", "open", true], ["current", "closed", true], ["current", "recent", false], ["old", "panel", true], ["recent", "panel", false]]) {
            await store.claim(session, instance);
            await store.save(session, instance, { messages: [] });
            if (unused) await utimes(path.join(directory, session, instance), old, old);
        }
        await writeFile(path.join(directory, "current", "open.0123456789ab.tmp"), "{}");
        await utimes(path.join(directory, "current", "open.0123456789ab.tmp"), old, old);
        await writeFile(path.join(directory, "blocked-session"), "not a directory");

        const removed = await pruneStarted(store, "current", instanceId => instanceId === "open");
        assert.deepEqual(removed.sort(), ["current/closed", "current/open.0123456789ab.tmp", "old/panel"].map(file => path.join(...file.split("/"))));
        assert.deepEqual((await readdir(directory)).sort(), ["blocked-session", "current", "recent"]);
        assert.deepEqual((await readdir(path.join(directory, "current"))).sort(), ["open", "recent"]);
        assert.deepEqual(await store.load("current", "open"), { messages: [] });

        assert.deepEqual(await readdir(path.join(directory, "recent")), ["panel"]);
    } finally {
        await rm(directory, { recursive: true, force: true });
    }
});

test("new panels start independently, and restarted panels resume their own conversation", async () => {
    const directory = await mkdtemp(path.join(tmpdir(), "started-chats-"));
    try {
        const started = new StartedChats(directory);
        let signedInAs = account;
        const registry = () => new ChatRegistry(async (input, context) => {
            let next = 0;
            const { chat, calls } = fixture(args => {
                if (args[0] === "auth") return json(signedInAs);
                if (args.includes("--start-conversation")) return json({ ...outcome, conversationId: `conversation-${context.instanceId}-${++next}` });
                if (args.includes("--conversation-id")) return json({ ...outcome, conversationId: args[args.indexOf("--conversation-id") + 1] });
            }, input.target);
            await chat.verifyOpen();
            await (await attachChat(chat, started, context.sessionId, context.instanceId)).pending;
            await chat.persisting;
            return { chat, calls, server: { close: async () => chat.close() } };
        }, (instanceId, context) => started.release(context.sessionId, instanceId));
        const input = { chatId: "shared", target };
        const context = { sessionId: "session-1" };
        const starts = resource => resource.calls.filter(args => args[1] === "ask" && !args.includes("--help")).length;
        const initial = registry();
        const [one, two] = await Promise.all([initial.open("one", input, context), initial.open("two", input, context)]);
        assert.equal(starts(one), 1);
        assert.equal(starts(two), 1);
        await one.chat.start("send", { query: "Remember me" });
        await one.chat.persisting;
        await initial.closeAll();

        const restored = registry();
        const reordered = { chatId: "shared", target: Object.fromEntries(Object.entries(target).reverse()) };
        const [oldOne, oldTwo] = await Promise.all([restored.open("one", reordered, context), restored.open("two", input, context)]);
        assert.equal(starts(oldOne), 0);
        assert.equal(starts(oldTwo), 0);
        assert.equal(oldOne.chat.snapshot().connected, true);
        assert.equal(oldOne.chat.snapshot().error, null);
        assert.deepEqual(oldOne.chat.snapshot().messages.map(message => message.text), ["Hello", "Remember me", "Hello"]);
        await oldOne.chat.start("send", { query: "Still there?" });
        assert(oldOne.calls.at(-1).includes("conversation-one-1"));
        assert.equal(starts(oldTwo), 0);
        await restored.closeAll();

        await started.save(context.sessionId, "one", { ...(await started.load(context.sessionId, "one")),
            conversationIssue: "The agent changed. Start a new chat to use the current agent." });
        const recovered = registry();
        assert.equal((await recovered.open("one", input, context)).chat.snapshot().error, null);
        await recovered.closeAll();
        const retargeted = registry();
        const other = await retargeted.open("one", { ...input, target: { ...target, schemaName: "other" } }, context);
        assert.equal(other.chat.snapshot().conversationIssue, "Something went wrong. Start a new chat.");
        await retargeted.closeAll();

        signedInAs = { ...account, account: "different@example.test" };
        const otherAccount = registry();
        const switched = await otherAccount.open("two", input, context);
        assert.equal(switched.chat.snapshot().connected, false);
        assert.equal(switched.chat.snapshot().status, "error");
        assert.match(switched.chat.snapshot().conversationIssue, /CLI account changed/);
        assert.match((await started.load(context.sessionId, "two")).conversationIssue, /CLI account changed/);
        assert.equal(switched.chat.snapshot().messages.length, 1);
        assert.equal(starts(switched), 0);
        await switched.chat.start("reset", { reconnect: true });
        assert.equal(switched.chat.snapshot().status, "ready");
        assert.equal(switched.chat.snapshot().connected, true);
        assert.equal(starts(switched), 1);
        await otherAccount.close("two");
        await otherAccount.close("one");
    } finally {
        await rm(directory, { recursive: true, force: true });
    }
});

test("an interrupted turn is saved as uncertain before the agent is contacted", async () => {
    for (const operation of ["connect", "send"]) {
        const directory = await mkdtemp(path.join(tmpdir(), "started-chats-"));
        let finish, pending, chat;
        try {
            const store = new StartedChats(directory);
            let started;
            const invoked = new Promise(resolve => { started = resolve; });
            const result = new Promise(resolve => { finish = resolve; });
            const isTurn = args => args[1] === "ask" &&
                (operation === "connect" ? args.includes("--start-conversation") : args.some(arg => arg.startsWith("--query=")));
            const current = fixture(args => {
                if (isTurn(args)) {
                    started();
                    return result;
                }
            });
            chat = current.chat;
            await chat.verifyOpen();
            const attached = await attachChat(chat, store, "session-1", "panel-1");
            if (operation === "connect") pending = attached.pending;
            else {
                await attached.pending;
                await chat.persisting;
                pending = chat.start("send", { query: "Important question" });
            }
            await invoked;
            const provisional = await store.load("session-1", "panel-1");
            const issue = operation === "connect" ? /may not have finished starting/ : /confirm whether the agent received/;
            assert.equal(provisional.outcomeUncertain, true);
            assert.match(provisional.conversationIssue, issue);

            const resumed = fixture();
            await resumed.chat.verifyOpen();
            assert.equal(resumed.chat.restore(provisional), false);
            assert.match(resumed.chat.snapshot().conversationIssue, issue);
            assert.equal(resumed.calls.filter(args => args[1] === "ask" && !args.includes("--help")).length, 0);

            finish(json(outcome));
            await pending;
            await chat.persisting;
            const completed = await store.load("session-1", "panel-1");
            assert.equal(completed.outcomeUncertain, undefined);
            assert(completed.messages.some(message => message.text === (operation === "connect" ? "Hello" : "Important question")));
            assert.equal(current.calls.filter(isTurn).length, 1);
            await chat.close();
        } finally {
            finish?.(json(outcome));
            await pending;
            await chat?.persisting;
            await rm(directory, { recursive: true, force: true });
        }
    }
});

test("Debug conversation data is saved with the chat and restored after the extension restarts", async () => {
    const directory = await mkdtemp(path.join(tmpdir(), "started-chats-"));
    try {
        const store = new StartedChats(directory);
        const event = { type: "event", name: "ToolInvocation", value: { tool: "weather" } };
        const first = fixture((args, { onStdout }) => {
            if (args[1] !== "ask" || args.includes("--help")) return;
            onStdout('{"schemaVersion":"1","activities":[' + JSON.stringify(event));
            return json({ ...outcome, activities: [event, ...outcome.activities] });
        });
        await first.chat.verifyOpen();
        await (await attachChat(first.chat, store, "session-1", "panel-1")).pending;
        await first.chat.start("send", { query: "Question" });
        await first.chat.close();
        const before = first.chat.debugSnapshot().responses;
        const record = await store.load("session-1", "panel-1");
        assert.equal(record.debug.responses.length, 2);

        const second = fixture();
        await second.chat.verifyOpen();
        await attachChat(second.chat, store, "session-1", "panel-1");
        assert.deepEqual(second.chat.debugSnapshot().responses, before);
        await second.chat.start("send", { query: "Next" });
        const turnIds = second.chat.debugSnapshot().responses.map(response => response.turnId);
        assert.equal(new Set(turnIds).size, 3);
        assert.equal(second.chat.snapshot().messages.at(-2).turnId, turnIds.at(-1));
        await second.chat.close();
    } finally {
        await rm(directory, { recursive: true, force: true });
    }
});

test("restored Debug data ignores malformed responses and stays within its budget", () => {
    const data = new ConversationData(300);
    const response = turnId => ({ turnId, operation: "send", complete: true, truncated: false,
        payload: { schemaVersion: "1", activities: [{ type: "message", text: "x".repeat(100) }] } });
    assert.equal(data.restore({ responses: [response(1), null, { turnId: 2 }, response(3), response(4)] }), true);
    const snapshot = data.snapshot();
    assert.deepEqual(snapshot.responses.map(entry => entry.turnId), [4]);
    assert.equal(snapshot.truncated, true);
    assert(Buffer.byteLength(JSON.stringify(snapshot.responses)) <= 300);
    assert.equal(data.restore("invalid"), false);
    assert.deepEqual(data.snapshot().responses, []);
});

test("a failed safety save prevents sending and reports the storage problem", async () => {
    for (const operation of ["connect", "send"]) {
        const { chat, calls } = fixture();
        if (operation === "send") await chat.start("connect");
        const before = chat.snapshot().messages;
        const turnsBefore = calls.filter(args => args[1] === "ask" && !args.includes("--help")).length;
        chat.persist = async () => { throw new Error("private filesystem detail"); };

        await chat.start(operation, operation === "send" ? { query: "Do not send" } : {});
        await chat.persisting;

        assert.deepEqual(chat.snapshot().messages, before);
        assert.equal(calls.filter(args => args[1] === "ask" && !args.includes("--help")).length, turnsBefore);
        assert.match(chat.state.error, /could not be saved/i);
        assert.match(chat.state.saveError, /could not be saved/i);
        assert(!JSON.stringify(chat.snapshot()).includes("private filesystem detail"));
    }
});

test("a completed turn is not treated as an unsent draft when saving its result fails", async () => {
    const { chat } = fixture();
    await chat.start("connect");
    let writes = 0;
    chat.persist = async () => {
        if (++writes === 2) throw new Error("private filesystem detail");
    };

    await chat.start("send", { query: "Send once" });
    await chat.persisting;

    assert.equal(chat.state.error, null);
    assert.match(chat.state.saveError, /could not be saved/i);
    assert.equal(chat.state.status, "error");
    assert(chat.state.messages.some(message => message.text === "Send once"));
});

test("legacy records without a conversation ID do not claim a message may have been delivered", async () => {
    const { chat } = fixture();
    await chat.verifyOpen();
    const record = {
        target: chat.target, resolved: chat.resolved, identity: chat.identity,
        conversationId: null, messages: [],
        conversationIssue: "The last message may have reached the agent. Start a new chat to continue.",
    };

    assert.equal(chat.restore(record), false);
    assert.equal(chat.snapshot().conversationIssue, "This conversation couldn't be restored. Start a new chat.");

    assert.equal(chat.restore({ ...record, conversationIssue: undefined, outcomeUncertain: true }), false);
    assert.equal(chat.snapshot().conversationIssue, "We couldn't confirm whether the agent received the last message. Start a new chat to continue.");
});

test("registry passes the open context to chat creation", async () => {
    const { chat } = fixture();
    let received;
    const registry = new ChatRegistry(async (input, context) => { received = context; return { chat, server: { close: async () => {} } }; });
    await registry.open("one", { chatId: "context", target }, { sessionId: "session-1" });
    assert.deepEqual(received, { sessionId: "session-1", instanceId: "one" });
    await registry.closeAll();
});

test("reopening a closed panel waits for its cleanup", async () => {
    let release;
    const blocked = new Promise(resolve => { release = resolve; });
    let created = 0;
    let cleaned = 0;
    const registry = new ChatRegistry(
        async () => { created++; return { server: { close: async () => {} } }; },
        async () => { if (++cleaned === 1) await blocked; });
    const input = { chatId: "same", target };
    await registry.open("one", input);
    const closing = registry.close("one");
    const reopening = registry.open("one", input);
    await Promise.resolve();
    assert.equal(created, 1);
    release();
    await closing;
    await reopening;
    assert.equal(created, 2);
    await registry.closeAll();
});

test("failed startup can retry and closing a starting panel does not leak its backend", async () => {
    let release;
    let closed = 0;
    const resource = { server: { close: async () => { closed++; } } };
    const registry = new ChatRegistry(() => new Promise(resolve => { release = resolve; }));
    const opening = registry.open("one", { chatId: "starting", target });
    const rejected = assert.rejects(opening, /closed during startup/);
    await Promise.resolve();
    const closing = registry.close("one");
    release(resource);
    await Promise.all([closing, rejected]);
    assert.equal(closed, 1);
    let attempts = 0;
    const retry = new ChatRegistry(async () => {
        if (++attempts === 1) throw new Error("Cannot start");
        return resource;
    });
    await assert.rejects(retry.open("one", { chatId: "retry", target }), /Cannot start/);
    await retry.open("one", { chatId: "retry", target });
    await retry.close("one");
    assert.equal(attempts, 2);
    assert.equal(closed, 2);
});

test("asks once per turn with literal arguments and continues only the returned conversation", async () => {
    const { chat, calls } = fixture();
    const query = "'; $(touch not-a-command) \n literal";
    await chat.start("check");
    assert.equal(chat.state.status, "ready");
    for (const invalid of ["", " ", "x".repeat(16_001), "\0"]) {
        assert.throws(() => chat.start("send", { query: invalid }), /message of at most/);
    }
    await chat.start("send", { query });
    await chat.start("send", { query: "Next" });
    const sends = calls.filter(args => args[0] === "test" && !args.includes("--help"));
    assert.equal(sends.length, 2);
    assert(sends[0].includes(`--query=${query}`));
    assert(!sends[0].includes("--conversation-id"));
    assert.equal(sends[1].at(-1), "conversation-1");
    assert.equal(chat.state.messages.length, 4);
    assert(sends.every(args => args.includes("--non-interactive") && args.includes("--json")));
});

test("query arguments preserve response-file and option-like text", () => {
    for (const query of ["@not-a-response-file.txt", "--help", "a=b:c"]) {
        const args = askArguments(target, query);
        assert(args.includes(`--query=${query}`));
        assert(!args.includes(query));
    }
});

test("Connect never runs sign-in and starts after external CLI sign-in", async () => {
    let signedIn = false;
    const { chat, calls } = fixture(args => {
        if (args[0] === "auth" && args[1] === "status") {
            return json(signedIn ? account : { success: true, signedIn: false });
        }
    });
    await chat.start("check");
    assert.equal(chat.state.status, "authentication-required");
    await chat.start("connect");
    assert.equal(chat.state.status, "authentication-required");
    assert.match(chat.state.error, /signed out/);
    assert(!calls.some(args => args[1] === "ask" && !args.includes("--help")));
    signedIn = true;
    await chat.start("connect");
    assert.equal(chat.state.status, "ready");
    assert.equal(chat.snapshot().connected, true);
    assert(!calls.some(args => args[0] === "auth" && ["login", "create"].includes(args[1])));
});

test("identity changes found when the canvas is checked stop continuation and require explicit reset", async () => {
    let current = account;
    const { chat, calls } = fixture(args => args[1] === "status" ? json(current) : undefined);
    await chat.start("send", { query: "Hi" });
    current = { ...account, account: "different@example.test" };
    await chat.start("check");
    assert.equal(chat.state.status, "changed");
    await chat.start("send", { query: "Next" });
    assert.equal(calls.filter(args => args[1] === "ask" && !args.includes("--help")).length, 1);
    await chat.start("reset");
    assert.equal(chat.state.status, "ready");
    assert.equal(chat.conversationId, null);
});

test("failed and malformed results are not retried or replaced with a fresh conversation", async () => {
    for (const response of [
        { code: 1, output: "incomplete secret diagnostics" },
        json({ ...outcome, success: false, status: "failed", queryDelivery: "attempted" }, 1),
        json({ ...outcome, conversationId: "../unsafe" }),
    ]) {
        const { chat, calls } = fixture(args => args[1] === "ask" && !args.includes("--help") ? response : undefined);
        await chat.start("send", { query: "Hi" });
        assert.equal(chat.state.status, "uncertain");
        assert(!chat.state.error.includes("secret diagnostics"));
        await chat.start("check");
        await chat.start("send", { query: "Next" });
        assert.equal(calls.filter(args => args[1] === "ask" && !args.includes("--help")).length, 1);
        await chat.start("reset");
        assert.equal(chat.state.status, "ready");
    }
});

test("a non-delivered auth failure shows signed out and continues on the next Send", async () => {
    let needsConsent = true;
    const { chat, calls } = fixture(args => {
        if (args[1] === "ask" && args.some(arg => arg.startsWith("--query=")) && needsConsent) {
            return json({ success: false, status: "failed", queryDelivery: "not-attempted",
                errorKind: "authentication-required", activities: [] }, 3);
        }
    });
    await chat.start("connect");
    await chat.start("send", { query: "Hi" });
    assert.equal(chat.state.status, "authentication-required");
    needsConsent = false;
    await chat.start("send", { query: "Hi" });
    const asks = calls.filter(args => args[1] === "ask" && !args.includes("--help"));
    assert.equal(asks.length, 3);
    assert.equal(asks.at(-1)[asks.at(-1).indexOf("--conversation-id") + 1], "conversation-1");
    assert.equal(chat.state.status, "ready");
    assert(!calls.some(args => args[0] === "auth" && ["login", "create"].includes(args[1])));
});

test("an uncertain authentication failure during Connect allows connection checks but requires New chat before continuing", async () => {
    let first = true;
    const { chat, calls } = fixture(args => {
        if (args[1] === "ask" && !args.includes("--help") && first) {
            first = false;
            return json({ success: false, status: "failed", queryDelivery: "not-attempted",
                errorKind: "authentication-failed", errorMessage: "Delegated authentication failed.",
                remediation: "Check the selected account.", activities: [] }, 1);
        }
    });
    await chat.start("connect");
    assert.equal(chat.state.status, "uncertain");
    assert.match(chat.state.error, /Remote effects may have occurred/);
    await chat.start("check");
    assert.equal(chat.state.status, "ready");
    assert.equal(chat.snapshot().conversationIssue,
        "The chat couldn't start. Delegated authentication failed. Check the selected account. Start a new chat to try again.");
    await chat.start("reset", { reconnect: true });
    assert.equal(calls.filter(args => args[1] === "ask" && !args.includes("--help")).length, 2);
    assert.equal(chat.snapshot().connected, true);
});

test("sends skip CLI checks, and a reopened canvas blocks a different CLI account", async () => {
    let active = account;
    const { chat, calls } = fixture(args => args[1] === "status" ? json(active) : undefined);
    await chat.start("connect");
    const checks = calls.length;
    await chat.start("send", { query: "Hi" });
    assert.deepEqual(calls.slice(checks).map(args => args.slice(0, 2)), [["test", "ask"]]);
    active = { ...account, account: "different@example.test" };
    await chat.start("check");
    assert.equal(chat.state.status, "changed");
    await chat.start("send", { query: "Again" });
    assert.equal(calls.filter(args => args[1] === "ask" && !args.includes("--help")).length, 2);
});

test("cancellation and close abort only the owned invocation and block concurrent operations", async () => {
    let entered;
    const started = new Promise(resolve => { entered = resolve; });
    const { chat } = fixture((args, { signal }) => {
        if (args[1] === "ask" && !args.includes("--help")) return new Promise((resolve, reject) => {
            entered();
            signal.addEventListener("abort", () => reject(new Error("Cancelled")), { once: true });
        });
    });
    const turn = chat.start("send", { query: "Hi" });
    await started;
    assert.throws(() => chat.start("check"), /current operation/);
    await chat.close();
    await turn;
    assert.equal(chat.state.busy, false);
    assert.equal(chat.state.status, "uncertain");
});

test("registered target uses deployment destination and detects association changes", async () => {
    const selected = { project: process.cwd(), agentId: "local-agent", deploymentId: "deployment" };
    let destination = target.environmentId;
    const { chat, calls } = fixture(args => args[0] === "agent" ? json({
        success: true,
        agents: [{ agentId: selected.agentId, agentType: "MCSAgent", mcsSchemaName: target.schemaName }],
        deployments: [{ agentId: selected.agentId, deploymentId: selected.deploymentId,
            agentType: "MCSAgent", environmentId: destination, tenantId: target.tenantId, deploymentName: "dev" }],
    }) : undefined, selected);
    await chat.start("send", { query: "Hi" });
    const send = calls.find(args => args[1] === "ask" && !args.includes("--help"));
    assert.equal(send[send.indexOf("--environment-id") + 1], target.environmentId);
    destination = "other-environment";
    await chat.start("check");
    assert.equal(chat.state.status, "changed");
    await chat.start("send", { query: "Next" });
    assert.equal(calls.filter(args => args[1] === "ask" && !args.includes("--help")).length, 1);
    await assert.rejects(resolveTarget(selected, async () => json({ success: true, agents: [], deployments: [] })), /association/);
});

test("target validation rejects mixed selectors, unsafe identifiers and missing explicit identity", () => {
    for (const value of [
        { ...target, mcsAgentId: "cloud-id" }, { ...target, tenantId: "not-guid" },
        { ...target, schemaName: "../schema" }, { project: ".", agentId: "a", deploymentId: "b" },
        { ...target, project: process.cwd() },
    ]) assert.throws(() => validateTarget(value));
    assert.equal(askArguments(target, "Hi").includes("--project"), false);
});

test("renderer selects only user-facing final text and flags unsupported cards", () => {
    const activities = [
        { type: "event", text: "hidden", value: { secret: true } },
        { type: "message", text: "delta", delta: { text: "hidden" } },
        { type: "message", text: "reasoning", entities: [{ type: "streaminfo", streamType: "informative" }] },
        { type: "message", text: "partial", entities: [{ type: "streaminfo", streamType: "streaming" }] },
        { type: "message", text: "final", entities: [{ type: "streaminfo", streamType: "final" }] },
        { type: "message", text: "user echo", from: { role: "user" } },
        { type: "message", attachments: [{ content: { secret: "not rendered" } }] },
    ];
    const texts = visibleMessages(activities);
    assert.equal(texts[0], "final");
    assert.equal(texts.length, 2);
    assert.match(texts[1], /not supported/);
    assert(!texts.join().includes("secret"));
});

test("CLI failures and invalid output cannot become successful replies", () => {
    assert.throws(() => parseResult({ code: 0, output: "partial JSON" }), /invalid JSON/);
    assert.throws(() => parseResult(json({ success: true }, 1)), /failed/);
    assert.throws(() => parseResult(json({ success: false, errorMessage: "sensitive raw data" })), error => !error.message.includes("sensitive"));
    assert.throws(() => parseResult(json({ success: false, errorCode: 1001, errorKind: "protocol-error",
        errorMessage: "sensitive raw data", remediation: "sensitive hint" })), error => !error.message.includes("sensitive"));
    assert.throws(() => parseResult(json({ success: false, errorCode: 12006, errorMessage: "Storage\nunavailable.",
        remediation: "x ".repeat(300) })), error => error.message === `Storage unavailable. ${"x ".repeat(250).trim()} (12006)`);
});

test("displayed CLI credential guidance redacts secret-like values", () => {
    const jwt = "eyJhbGciOiJSUzI1NiJ9.eyJzdWIiOiJ1c2VyIn0.c2lnbmF0dXJlLXZhbHVl";
    assert.throws(() => parseResult(json({ success: false, errorCode: 12006,
        errorMessage: `Owned protected storage is unavailable (macOS: denied). Bearer ${jwt} refresh_token=abc123 https://login.example/x?code=xyz`,
        remediation: "Set AH_CLI_ALLOW_UNPROTECTED_CACHE=true only as a last resort." })), error =>
        error.message.startsWith("Owned protected storage is unavailable (macOS: denied).") &&
        error.message.includes("AH_CLI_ALLOW_UNPROTECTED_CACHE=true") && error.message.endsWith("(12006)") &&
        !error.message.includes("eyJ") && !error.message.includes("abc123") && !error.message.includes("xyz"));
});

test("opening fails with the CLI's own error when signed-in credentials cannot be used", async () => {
    const { chat, calls } = fixture(args => args[0] === "env" ? json({ success: false, exitCode: 1, errorCode: 12006,
        errorMessage: "Owned protected storage is unavailable.", remediation: "Check native storage." }, 1) : undefined);
    await assert.rejects(chat.verifyOpen(), error =>
        error.message === `Owned protected storage is unavailable. Check native storage. (12006) ${openFailureGuidance}`);
    assert(calls.some(args => args.join(" ") === "env list --json --non-interactive"));
    assert(!calls.some(args => args[1] === "ask" && !args.includes("--help")));
    assert(!calls.some(args => args[0] === "auth" && ["login", "create"].includes(args[1])));
});

test("opening shows the CLI's guidance when the token needs an explicit sign-in", async () => {
    const { chat } = fixture(args => args[0] === "env" ? json({ success: false, exitCode: 3, errorCode: 12007,
        errorMessage: "The profile requires explicit sign-in to renew its token.", remediation: "Run `msagent auth create`." }, 3) : undefined);
    await assert.rejects(chat.verifyOpen(), error => error.message ===
        `The profile requires explicit sign-in to renew its token. Run \`msagent auth create\`. (12007) ${openFailureGuidance}`);
});

test("process runner bounds output, propagates exit codes and cancels without a shell", async () => {
    const result = await runCommand(process.execPath, ["-e", "process.stdout.write(JSON.stringify({success:true}));process.exitCode=2"]);
    assert.equal(result.code, 2);
    assert.equal(JSON.parse(result.output).success, true);
    await assert.rejects(runCommand(process.execPath, ["-e", "process.stdout.write('x'.repeat(10000))"], { limit: 32 }), /exceeded/);
    const controller = new AbortController();
    const running = runCommand(process.execPath, ["-e", "setInterval(()=>{},1000)"], { signal: controller.signal });
    controller.abort();
    await assert.rejects(running, /Cancelled/);
    await assert.rejects(runCommand(process.execPath, ["-e", "setInterval(()=>{},1000)"], { timeout: 10 }), /timed out/);
    assert.equal(await resolveExecutable(process.execPath), process.execPath);
    await assert.rejects(resolveExecutable("./msagent"), /absolute/);
    await assert.rejects(resolveExecutable(undefined, { PATH: "." }), /not found/);
});

test("Windows discovery resolves only the standard .NET 10 launcher to its exact native apphost", { skip: process.platform !== "win32" }, async () => {
    const root = await mkdtemp(path.join(tmpdir(), "msagent path & literal-"));
    const directory = path.join(root, "isolated installation with a deliberately long path", "tool");
    try {
        const relative = ".store\\microsoft.agents.hosting.cli\\0.2.168-beta-gfixture\\microsoft.agents.hosting.cli.win-x64\\0.2.168-beta-gfixture\\tools\\net10.0\\win-x64\\Microsoft.Agents.Hosting.Cli.exe";
        const native = path.join(directory, relative);
        assert(native.length > 260);
        await mkdir(path.dirname(native), { recursive: true });
        await copyFile(process.execPath, native);
        const launcher = path.join(directory, "msagent.cmd");
        const standard = `@echo off\r\n"%~dp0${relative}" %*\r\n`;
        await writeFile(launcher, standard);
        assert.equal(await resolveExecutable(undefined, { PATH: directory }), native);
        assert.equal(await resolveExecutable(launcher), native);
        assert.equal(await resolveExecutable(native), native);
        const args = ['--query=quotes " & | < > %PATH% !value! ^ $(no-command)\nnext', "C:\\project with spaces\\", "@response.txt"];
        const result = await runCommand(native, ["-e", "process.stdout.write(JSON.stringify(process.argv.slice(1)))", "--", ...args]);
        assert.equal(result.code, 0);
        assert.deepEqual(JSON.parse(result.output), args);
        for (const invalid of [
            standard + "echo unexpected\r\n",
            standard.replace("%*", "extra %*"),
            standard.replace("%~dp0.store", "%~dp0..\\other"),
            standard.replace("net10.0", "net9.0"),
            standard.replace(/0.2.168-beta-gfixture(?=\\tools)/, "0.2.999"),
            standard.replaceAll("0.2.168-beta-gfixture", ".."),
            "x".repeat(8193),
        ]) {
            await writeFile(launcher, invalid);
            await assert.rejects(resolveExecutable(undefined, { PATH: [directory, path.dirname(process.execPath)].join(path.delimiter) }), /standard .NET 10/);
        }
        await writeFile(launcher, standard);
        await rm(native);
        await assert.rejects(resolveExecutable(undefined, { PATH: directory }), /launcher target/);
        await assert.rejects(resolveExecutable(path.join(directory, "other.bat")), /native .exe|standard .NET 10/);
    } finally {
        await rm(root, { recursive: true, force: true });
    }
});

test("staged file paths and query arguments survive native process invocation literally", async () => {
    const query = '--option "quoted" & | %PATH% !variable! ^\nnext';
    const selected = { ...target, project: path.join(tmpdir(), "project with spaces"), agentId: "agent", deploymentId: "draft" };
    let staged;
    await withUploadedFiles(uploads([{ name: "report & notes.txt", base64: "AQID" }]), async paths => {
        staged = paths[0];
        const args = [...askArguments(selected, query, "conversation"), "--file", ...paths];
        const result = await runCommand(process.execPath, ["-e", "process.stdout.write(JSON.stringify(process.argv.slice(1)))", "--", ...args]);
        assert.equal(result.code, 0);
        assert.deepEqual(JSON.parse(result.output), args);
        assert.deepEqual(await readFile(staged), Buffer.from([1, 2, 3]));
    });
    await assert.rejects(access(staged));
});

test("the CLI is launched through its Unix PATH entry, not the resolved symlink target", { skip: process.platform === "win32" }, async () => {
    const directory = await mkdtemp(path.join(tmpdir(), "msagent-path-"));
    try {
        const link = path.join(directory, "msagent");
        await symlink(process.execPath, link);
        assert.equal(await resolveExecutable(undefined, { PATH: directory }), link);
    } finally {
        await rm(directory, { recursive: true, force: true });
    }
});

test("HTTP gateway enforces capability, Host, Origin and content type and closes cleanly", async () => {
    const { chat } = fixture();
    const server = await startServer(chat);
    const url = new URL(server.url);
    const headers = { Authorization: `Bearer ${url.hash.slice(1)}`, Origin: url.origin, "Content-Type": "application/json" };
    try {
        assert.equal((await fetch(url.origin)).status, 200);
        const markdown = await fetch(`${url.origin}/markdown.mjs`);
        assert.equal(markdown.status, 200);
        assert.match(markdown.headers.get("content-type"), /javascript/);
        assert.match(await markdown.text(), /export function renderMarkdown/);
        assert.equal((await fetch(`${url.origin}/state`)).status, 403);
        assert.equal((await fetch(`${url.origin}/debug`)).status, 403);
        assert.equal((await fetch(`${url.origin}/connect`, { method: "POST", headers: { Origin: url.origin, "Content-Type": "application/json" }, body: '{}' })).status, 403);
        assert.equal((await fetch(`${url.origin}/debug`, { method: "POST", headers: { Origin: url.origin, "Content-Type": "application/json" }, body: '{"enabled":true}' })).status, 403);
        assert.equal((await fetch(`${url.origin}/debug`, { method: "POST", headers: { ...headers, Origin: "https://attacker.test" }, body: '{"enabled":true}' })).status, 403);
        assert.equal((await fetch(`${url.origin}/state`, { headers: { ...headers, Origin: "https://attacker.test" } })).status, 403);
        const rejectedHost = await new Promise((resolve, reject) => {
            const req = request(`${url.origin}/state`, { headers: { ...headers, Host: "attacker.test" } }, response => {
                response.resume();
                response.on("end", () => resolve(response.statusCode));
            });
            req.on("error", reject);
            req.end();
        });
        assert.equal(rejectedHost, 403);
        assert.equal((await fetch(`${url.origin}/state`, { headers })).status, 200);
        assert.equal((await fetch(`${url.origin}/check`, { method: "POST", headers: { Authorization: headers.Authorization }, body: "{}" })).status, 400);
        assert.equal((await fetch(`${url.origin}/check`, { method: "POST", headers, body: "{}" })).status, 202);
        await chat.pending;
        assert.equal((await (await fetch(`${url.origin}/state`, { headers })).json()).status, "ready");
        assert.equal((await fetch(`${url.origin}/login`, { method: "POST", headers, body: "{}" })).status, 400);
        assert.equal((await fetch(`${url.origin}/debug`, { method: "POST", headers, body: '{"enabled":"true"}' })).status, 400);
        assert.equal((await fetch(`${url.origin}/debug`, { method: "POST", headers, body: '{"enabled":true}' })).status, 400);
        assert.equal((await fetch(`${url.origin}/connect`, { method: "POST", headers, body: '{}' })).status, 202);
        await chat.pending;
        const connected = await (await fetch(`${url.origin}/state`, { headers })).json();
        assert.equal(connected.connected, true);
        assert(connected.messages.every(message => message.role === "assistant"));
        assert.equal((await fetch(`${url.origin}/send`, { method: "POST", headers, body: '{"query":"Hello"}' })).status, 202);
        await chat.pending;
        const debug = await (await fetch(`${url.origin}/debug`, { headers })).json();
        assert.equal(debug.responses.length, 2);
        assert(debug.responses.every(response => response.complete));
        assert.deepEqual(debug.responses[1].payload, outcome);
        assert.deepEqual(debug.messages, (await (await fetch(`${url.origin}/state`, { headers })).json()).messages);
        assert.equal((await fetch(`${url.origin}/reset`, { method: "POST", headers, body: '{}' })).status, 202);
        await chat.pending;
        const reset = await (await fetch(`${url.origin}/debug`, { headers })).json();
        assert.deepEqual(reset.messages, []);
        assert.deepEqual(reset.responses, []);
        assert.equal((await fetch(`${url.origin}/send`, { method: "POST", headers, body: "{" })).status, 400);
        assert.equal((await fetch(`${url.origin}/check`, { method: "POST", headers, body: JSON.stringify({ query: "x".repeat(100_001) }) })).status, 413);
    } finally { await server.close(); }
    await assert.rejects(fetch(url.origin));
});

test("opening fails for a signed-out CLI without running sign-in and succeeds once signed in", async () => {
    let signedIn = false;
    const { chat, calls } = fixture(args => args[1] === "status" ? json(signedIn ? account : { success: true, signedIn: false }) : undefined);
    await assert.rejects(chat.verifyOpen(), /msagent auth create/);
    assert(!calls.some(args => args[0] === "auth" && ["login", "create"].includes(args[1])));
    signedIn = true;
    await chat.verifyOpen();
    assert.equal(chat.state.status, "ready");
    assert(!calls.some(args => args[1] === "ask" && !args.includes("--help")));
});
