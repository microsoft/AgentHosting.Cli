import { renderMarkdown } from "./markdown.mjs";
import { limits } from "./limits.mjs";
import { Status } from "./state.mjs";

const capability = location.hash.slice(1);
const element = id => document.getElementById(id);
element("query").maxLength = limits.queryCharacters;
let state;
let rendered = "";
let draftSent;
let draftVersion = 0;
let stopped = false;
let acting = false;
let actingOperation;
let debugRevision = -1;
let debugUpdatedAt = 0;
let debugView = false;
let debugSelection;
let debugTargets = new Map();
let debugStreams = new Map();
let debugExpandChunks = false;
let debugRevealedKey;
let debugSegments = [];
let debugText = "";
let debugMarkedSegments = [];
let debugMatchIndex = 0;
let debugMatchCount = 0;
let pendingFiles = [];
let preparingFiles = false;
let messageMenus = new Map();

function agentName() {
    return state.agentName ?? state.target.schemaName ?? state.target.agentId;
}

function renderPendingFiles() {
    element("pending-files").replaceChildren(...pendingFiles.map((file, index) => {
        const item = document.createElement("span");
        item.textContent = file.name;
        const remove = document.createElement("button");
        remove.type = "button";
        remove.textContent = "Remove";
        remove.setAttribute("aria-label", `Remove ${file.name}`);
        remove.addEventListener("click", () => {
            pendingFiles.splice(index, 1);
            draftVersion++;
            renderPendingFiles();
            controls();
        });
        item.append(remove);
        return item;
    }));
}

const savedFiles = new Map();
const fileControls = new Map();

function showFileStatus(node, result) {
    node.hidden = !result?.text;
    if (!result?.text) return;
    node.className = result.isError ? "file-status file-error" : "file-status";
    node.setAttribute("role", result.isError ? "alert" : "status");
    node.setAttribute("aria-live", result.isError ? "assertive" : "polite");
    node.title = result.title ?? "";
    node.textContent = result.text;
}

function updateFileControls(id) {
    const result = savedFiles.get(id);
    for (const { button, status } of fileControls.get(id) ?? []) {
        button.disabled = Boolean(result?.pending);
        showFileStatus(status, result);
    }
}

async function saveFile(file) {
    if (savedFiles.get(file.id)?.pending) return;
    savedFiles.set(file.id, { ...savedFiles.get(file.id), pending: true });
    updateFileControls(file.id);
    let result;
    try {
        const saved = await api(`/files/${file.id}/save`, {});
        const savedName = saved.savedName ?? saved.path.split(/[\\/]/).pop();
        result = {
            text: `✓ Saved to Downloads${savedName === file.name ? "" : ` as ${savedName}`}` +
                (saved.revealed ? "" : ". Could not confirm it was shown in your file manager.") +
                (saved.warning ? `. ${saved.warning}` : ""),
            title: saved.path ?? "",
            isError: Boolean(saved.warning) || !saved.revealed,
        };
    } catch (error) {
        result = { text: error.message, isError: true };
    } finally {
        savedFiles.set(file.id, result);
        updateFileControls(file.id);
    }
}

function highlightDebug(scroll = false) {
    const { turnId, role } = debugSelection ?? {};
    const target = (role === "assistant" && debugTargets.get(`response-${turnId}`)) ||
        debugTargets.get(`message-${turnId}-${role}`);
    for (const node of debugTargets.values()) node.className = node === target ? "debug-entry debug-selected" : "debug-entry";
    if (scroll) {
        if (!target) throw new Error("Debug data for this message is no longer available.");
        target.scrollIntoView({ block: "start" });
        target.focus({ preventScroll: true });
    }
    return target;
}

function searchDebug({ reset = false, direction = 0, scroll = false } = {}) {
    for (const segment of debugMarkedSegments) {
        segment.node.textContent = !segment.details || segment.details.open ? segment.text : "";
    }
    debugMarkedSegments = [];
    const revealed = debugStreams.get(debugRevealedKey);
    if (reset) debugMatchIndex = 0;
    else if (direction && debugMatchCount) {
        debugMatchIndex = (debugMatchIndex + direction + debugMatchCount) % debugMatchCount;
    }

    const query = element("debug-search").value;
    let selected;
    let lastStart;
    let lastEnd;
    let count = 0;
    if (query) {
        const pattern = new RegExp(query.replace(/[.*+?^${}()|[\]\\]/g, "\\$&"), "giu");
        let match;
        while ((match = pattern.exec(debugText)) !== null) {
            lastStart = match.index;
            lastEnd = match.index + match[0].length;
            if (count === debugMatchIndex) selected = { start: lastStart, end: lastEnd };
            count++;
        }
    }
    debugMatchCount = count;
    if (count && !selected) {
        debugMatchIndex = count - 1;
        selected = { start: lastStart, end: lastEnd };
    } else if (!count) debugMatchIndex = 0;
    element("debug-search-count").textContent = !query ? "" : count ? `${debugMatchIndex + 1} of ${count}` : "No matches";
    element("debug-previous").disabled = !count;
    element("debug-next").disabled = !count;
    const release = () => {
        debugRevealedKey = undefined;
        if (revealed?.open && !debugExpandChunks) revealed.open = false;
    };
    if (!selected) {
        if (!query) release();
        return;
    }

    let first;
    for (const segment of debugSegments) {
        const start = Math.max(0, selected.start - segment.start);
        const end = Math.min(segment.text.length, selected.end - segment.start);
        if (start >= end) continue;
        if (segment.details && !segment.details.open) {
            if (!scroll) continue;
            if (revealed !== segment.details) release();
            segment.details.open = true;
            debugRevealedKey = [...debugStreams].find(([, details]) => details === segment.details)?.[0];
        } else if (scroll && segment.details !== revealed) release();
        const mark = document.createElement("mark");
        mark.className = "debug-search-match";
        mark.textContent = segment.text.slice(start, end);
        segment.node.replaceChildren(segment.text.slice(0, start), mark, segment.text.slice(end));
        debugMarkedSegments.push(segment);
        first ??= mark;
    }
    if (scroll) first?.scrollIntoView({ block: "center", inline: "nearest" });
}

function updateExpandChunks() {
    const button = element("debug-expand");
    const expanded = [...debugStreams.values()].some(details => details.open);
    button.disabled = debugStreams.size === 0;
    button.textContent = expanded ? "Collapse chunks" : "Expand chunks";
    button.ariaPressed = String(expanded);
}

function renderDebug(debug) {
    const focused = document.activeElement?.className === "debug-entry debug-selected";
    const previousStreams = debugStreams;
    debugStreams = new Map();
    debugTargets = new Map();
    debugSegments = [];
    debugMarkedSegments = [];
    const parts = [];
    let offset = 0;
    const add = (text, parent, details) => {
        const node = document.createElement("span");
        if (!details || details.open) node.textContent = text;
        debugSegments.push({ node, text, start: offset, details });
        offset += text.length;
        if (parent) parent.append(node);
        else parts.push(node);
        return node;
    };
    const lineBreak = parent => {
        add("\n", parent).className = "debug-break";
    };
    const streaming = activity => {
        if (!["message", "typing"].includes(activity?.type) || !Array.isArray(activity.entities)) return false;
        const info = activity.entities.find(entity => typeof entity?.type === "string" && entity.type.toLowerCase() === "streaminfo");
        const type = info?.streamType ?? info?.streamtype;
        return typeof type === "string" && type.toLowerCase() === "streaming";
    };
    add("{\n");
    for (const name of ["messages", "responses"]) {
        if (name === "responses") add(",\n");
        add(`  "${name}": [`);
        let previous;
        debug[name].forEach((value, index) => {
            if (previous) add(",", previous);
            lineBreak();
            const node = document.createElement("div");
            previous = node;
            node.id = `debug-${name === "messages" ? `message-${value.turnId}-${value.role}` : `response-${value.turnId}`}`;
            node.tabIndex = -1;
            node.className = "debug-entry";
            debugTargets.set(node.id.slice(6), node);
            parts.push(node);
            const activities = name === "responses" ? value.payload?.activities : undefined;
            if (!Array.isArray(activities) || !activities.some(streaming)) {
                add(JSON.stringify(value, null, 2).replace(/^/gm, "    "), node);
                return;
            }
            const indent = depth => "  ".repeat(depth);
            const format = (entry, depth) => JSON.stringify(entry, null, 2).replace(/\n/g, `\n${indent(depth)}`);
            let group = 0;
            const write = (entry, depth) => {
                if (entry === activities) {
                    add("[", node);
                    let afterGroup = false;
                    for (let first = 0; first < entry.length;) {
                        let end = first + 1;
                        if (streaming(entry[first])) {
                            while (end < entry.length && streaming(entry[end])) end++;
                            if (first) add(",", node);
                            lineBreak(node);
                            const key = `${value.turnId ?? index}-${group++}`;
                            const details = document.createElement("details");
                            details.className = "debug-stream";
                            details.open = previousStreams.get(key)?.open ?? debugExpandChunks;
                            const summary = document.createElement("summary");
                            summary.textContent = `${end - first} streaming ${end - first === 1 ? "chunk" : "chunks"} hidden`;
                            summary.tabIndex = -1;
                            summary.addEventListener("click", event => event.preventDefault());
                            details.append(summary);
                            node.append(details);
                            debugStreams.set(key, details);
                            const text = entry.slice(first, end).map(activity =>
                                `${indent(depth + 1)}${format(activity, depth + 1)}`).join(",\n") + (end < entry.length ? "," : "");
                            const content = add(text, details, details);
                            details.addEventListener("toggle", () => {
                                if (debugStreams.get(key) !== details) return;
                                content.textContent = details.open ? text : "";
                                updateExpandChunks();
                                searchDebug();
                            });
                            afterGroup = true;
                        } else if (afterGroup) {
                            lineBreak(node);
                            add(`${indent(depth + 1)}${format(entry[first], depth + 1)}`, node);
                            afterGroup = false;
                        } else {
                            add(`${first ? ",\n" : "\n"}${indent(depth + 1)}${format(entry[first], depth + 1)}`, node);
                        }
                        first = end;
                    }
                    if (afterGroup) {
                        lineBreak(node);
                        add(`${indent(depth)}]`, node);
                    } else add(entry.length ? `\n${indent(depth)}]` : "]", node);
                } else if (entry === value || entry === value.payload) {
                    add("{", node);
                    Object.entries(entry).forEach(([key, child], index) => {
                        add(`${index ? "," : ""}\n${indent(depth + 1)}${JSON.stringify(key)}: `, node);
                        write(child, depth + 1);
                    });
                    add(`\n${indent(depth)}}`, node);
                } else add(format(entry, depth), node);
            };
            add("    ", node);
            write(value, 2);
        });
        if (debug[name].length) {
            lineBreak();
            add("  ]");
        } else add("]");
    }
    add(`,\n  "truncated": ${JSON.stringify(debug.truncated)}\n}`);
    debugText = debugSegments.map(segment => segment.text).join("");
    element("debug-output").replaceChildren(...parts);
    updateExpandChunks();
    searchDebug();
    const target = highlightDebug();
    if (focused) target?.focus({ preventScroll: true });
}

async function api(route, input) {
    let response;
    try {
        response = await fetch(route, {
            method: input === undefined ? "GET" : "POST",
            headers: { Authorization: `Bearer ${capability}`, ...(input === undefined ? {} : { "Content-Type": "application/json" }) },
            ...(input === undefined ? {} : { body: JSON.stringify(input) }),
        });
    } catch {
        throw new LocalServiceUnavailableError("The canvas could not reach its local chat service. Reopen this canvas to reconnect.");
    }
    const data = await response.json();
    if (!response.ok) throw new Error(data.error ?? "The local chat service failed.");
    return data;
}

class LocalServiceUnavailableError extends Error {}

function showError(message) {
    element("error").textContent = message ?? "";
    element("error").hidden = !message;
}

function finishDraft(failed) {
    if (failed && draftSent && draftSent.version === draftVersion && !element("query").value) {
        element("query").value = draftSent.text;
        pendingFiles = draftSent.files;
        renderPendingFiles();
    }
    draftSent = undefined;
}

function controls() {
    const busy = stopped || acting || preparingFiles || (state?.busy ?? true);
    const setup = Boolean(state?.setupRequired);
    element("setup").hidden = !setup;
    element("choose-agent").disabled = busy || Boolean(state?.setupRequested);
    element("setup-description").textContent = state?.setupRequested
        ? "Continue in the main Copilot chat. This panel will update after you choose an agent."
        : "Choose your Copilot Studio agent in the main Copilot chat.";
    element("connection").hidden = setup;
    element("view-controls").hidden = setup;
    const conversationBlocked = Boolean(state?.conversationIssue);
    const canConnect = [Status.Ready, Status.AuthenticationRequired].includes(state?.status);
    element("files").disabled = preparingFiles || conversationBlocked;
    element("query").disabled = conversationBlocked;
    element("query").placeholder = conversationBlocked ? "Start a new chat to send a message." : "Ask your agent…";
    element("send").disabled = busy || conversationBlocked || !canConnect || !state?.connected || !element("query").value.trim();
    element("new-chat").disabled = busy;
    element("new-chat").hidden = setup;
    element("start-new-chat").disabled = busy;
    element("start-new-chat").hidden = !conversationBlocked;
    element("conversation-issue").hidden = !conversationBlocked || debugView;
    element("conversation-issue-message").textContent = state?.conversationIssue ?? "";
    element("view-chat").setAttribute("aria-pressed", String(!debugView));
    element("view-debug").setAttribute("aria-pressed", String(debugView));
    element("chat-view").hidden = setup || debugView;
    element("debug-view").hidden = setup || !debugView;
    element("composer").hidden = setup || debugView;
    const replying = (acting && ["send", "connect", "reset"].includes(actingOperation)) ||
        (state?.busy && ["send", "connect"].includes(state.operation));
    element("typing").hidden = !replying;
    const typingText = state?.messages.at(-1)?.streaming ? "Copilot Studio is replying" : "Waiting for Copilot Studio";
    if (element("typing-text").textContent !== typingText) element("typing-text").textContent = typingText;
}

async function refreshDebug(force = false) {
    if (!debugView || !state) return;
    if (!force && (debugRevision === state.debugRevision || (state.busy && Date.now() - debugUpdatedAt < 500))) return;
    const { revision, ...debug } = await api("/debug");
    const status = [
        state.error ? `Last operation: ${state.error}` : "",
        debug.truncated ? "Some raw response details exceeded the 1 MB limit and were omitted. The current chat messages are included."
            : !state.busy && debug.responses.some(response => !response.complete) ? "Conversation data includes an incomplete CLI response."
            : "",
    ].filter(Boolean).join("\n");
    element("debug-status").textContent = status;
    element("debug-status").hidden = !status;
    renderDebug(debug);
    debugRevision = revision;
    debugUpdatedAt = Date.now();
}

async function refresh() {
    try {
        const next = await api("/state");
        if (!next.busy && draftSent?.accepted && (next.error || next.messages.length > draftSent.messageCount)) {
            finishDraft(Boolean(next.error));
        }
        state = next;
        const target = state.target;
        const identifier = target.schemaName ?? target.agentId;
        element("agent").textContent = agentName() ?? "No agent selected";
        element("agent").title = identifier && identifier !== agentName() ? `${agentName()} (${identifier})` : element("agent").textContent;
        for (const [id, value] of [
            ["conn-account", state.account ?? "Not signed in"],
            ["conn-agent", target.schemaName ?? target.agentId],
            ["conn-deployment", target.deploymentName ?? target.deploymentId],
            ["conn-environment", target.environmentId],
            ["conn-tenant", target.tenantId],
        ]) element(id).textContent = value || "—";
        const statusText = !state.busy && state.status === Status.AuthenticationRequired ? "Signed out"
            : !state.busy && !state.conversationIssue && !state.saveError && state.status === Status.Error ? "Connection problem"
            : "";
        element("status").textContent = statusText;
        element("status").hidden = !statusText;
        element("status").className = state.status;
        element("status").title = state.status === Status.AuthenticationRequired
            ? "Run `msagent auth create`, then return here."
            : state.status === Status.Error ? state.error ?? "Check your CLI connection and selected agent." : "";
        const key = JSON.stringify([agentName(), state.messages]);
        const inspecting = state.messages.some(message => messageMenus.get(`${message.turnId}-${message.role}`)?.open);
        if (rendered !== key && !inspecting) {
            messageMenus = new Map();
            fileControls.clear();
            element("messages").replaceChildren(...state.messages.map(message => {
                const article = document.createElement("article");
                article.className = `message ${message.role}`;
                const label = document.createElement("strong");
                label.textContent = `${message.role === "user" ? "You" : agentName()}${!message.streaming && message.partial ? " · Incomplete" : ""}`;
                const text = message.role === "assistant" ? renderMarkdown(message.text, document) : document.createElement("p");
                if (message.role !== "assistant") text.textContent = message.text;
                const heading = document.createElement("div");
                heading.className = "message-heading";
                heading.append(label);
                if (Number.isSafeInteger(message.turnId)) {
                    const menu = document.createElement("details");
                    menu.className = "message-menu";
                    messageMenus.set(`${message.turnId}-${message.role}`, menu);
                    const summary = document.createElement("summary");
                    summary.textContent = "...";
                    summary.setAttribute("aria-label", "Message options");
                    const inspect = document.createElement("button");
                    inspect.type = "button";
                    inspect.textContent = "View debug";
                    inspect.addEventListener("click", () => {
                        menu.open = false;
                        return selectView(true, { turnId: message.turnId, role: message.role });
                    });
                    menu.append(summary, inspect);
                    heading.append(menu);
                }
                article.append(heading, text);
                if (message.files?.length) {
                    const files = document.createElement("div");
                    files.className = "message-files";
                    for (const file of message.files) {
                        const returned = message.role === "assistant";
                        const item = document.createElement(returned ? "button" : "span");
                        let entry = item;
                        item.textContent = returned ? file.name : `Attached: ${file.name}`;
                        if (returned) {
                            item.type = "button";
                            item.disabled = !file.id || Boolean(savedFiles.get(file.id)?.pending);
                            item.title = file.id ? "Save to your Downloads folder" : file.savedName
                                ? "Previously saved to Downloads. This file is unavailable after transcript restoration."
                                : "This file is unavailable after the transcript was restored. Ask the agent to provide it again.";
                            item.setAttribute("aria-label", file.id ? `Save ${file.name} to Downloads` : file.savedName
                                ? `${file.name} (previously saved to Downloads)`
                                : `${file.name} (unavailable after transcript restoration)`);
                            const status = document.createElement("small");
                            status.setAttribute("aria-live", "polite");
                            const saveResult = file.id && savedFiles.get(file.id);
                            showFileStatus(status, saveResult?.text ? saveResult : file.savedName ? {
                                text: `✓ Saved to Downloads${file.savedName === file.name ? "" : ` as ${file.savedName}`}`,
                                isError: false,
                            } : undefined);
                            if (file.id) {
                                const controls = fileControls.get(file.id) ?? [];
                                controls.push({ button: item, status });
                                fileControls.set(file.id, controls);
                                item.addEventListener("click", () => saveFile(file));
                            }
                            entry = document.createElement("span");
                            entry.className = "message-file";
                            entry.append(item, status);
                        }
                        files.append(entry);
                    }
                    article.append(files);
                }
                return article;
            }));
            rendered = key;
            element("messages").lastElementChild?.scrollIntoView({ block: "end" });
        }
        showError([state.conversationIssue ? null : state.error, state.saveError].filter(Boolean).join(" "));
        await refreshDebug();
        controls();
    } catch (error) {
        stopped = true;
        finishDraft(true);
        element("typing").hidden = true;
        const message = error instanceof LocalServiceUnavailableError
            ? error.message
            : "This canvas could not load chat updates. Reopen this canvas to reconnect.";
        const details = error instanceof LocalServiceUnavailableError ? "" : `\nDetails: ${error.message}`;
        showError(`${message}${details}`);
        for (const id of ["send", "new-chat", "start-new-chat", "choose-agent"]) element(id).disabled = true;
    }
}

async function act(operation, input = {}) {
    if (acting) return;
    acting = true;
    actingOperation = operation;
    controls();
    try {
        await api(`/${operation}`, input);
        if (operation === "send" && draftSent) draftSent.accepted = true;
        await refresh();
    } catch (error) {
        if (operation === "send") finishDraft(true);
        showError(error.message);
    }
    finally {
        acting = false;
        actingOperation = undefined;
        if (!stopped) controls();
    }
}

element("new-chat").addEventListener("click", () => act("reset", { reconnect: true }));
element("start-new-chat").addEventListener("click", () => act("reset", { reconnect: true }));
element("choose-agent").addEventListener("click", () => act("setup"));
function closeOverlays(target) {
    for (const overlay of [element("connection"), ...messageMenus.values()]) {
        if (overlay.open && (!target || !overlay.contains(target))) overlay.open = false;
    }
}
document.addEventListener("click", event => closeOverlays(event.target));
window.addEventListener("blur", () => closeOverlays());
const refreshConnectionOnFocus = () => {
    if (state && !state.setupRequired && !stopped && !acting && !state.busy && state.status !== Status.Ready) return act("check");
};
window.addEventListener("focus", refreshConnectionOnFocus);
element("query").addEventListener("input", () => { draftVersion++; controls(); });
element("files").addEventListener("change", async () => {
    const selected = Array.from(element("files").files);
    element("files").value = "";
    if (pendingFiles.length + selected.length > limits.uploadFiles ||
        [...pendingFiles, ...selected].reduce((bytes, file) => bytes + file.size, 0) > limits.fileBytes) {
        showError("Attach at most five files, at most 10 MiB total.");
        return;
    }
    preparingFiles = true;
    controls();
    try {
        const files = await Promise.all(selected.map(file => new Promise((resolve, reject) => {
            const reader = new FileReader();
            reader.onload = () => resolve({ name: file.name, size: file.size, base64: reader.result.slice(reader.result.indexOf(",") + 1) });
            reader.onerror = () => reject(new Error("A selected file could not be read."));
            reader.onabort = reader.onerror;
            reader.readAsDataURL(file);
        })));
        pendingFiles.push(...files);
        draftVersion++;
        renderPendingFiles();
        showError();
    } catch (error) { showError(error.message); }
    finally { preparingFiles = false; controls(); }
});
element("query").addEventListener("keydown", event => {
    if (event.key !== "Enter" || event.shiftKey || event.isComposing || event.keyCode === 229) return;
    event.preventDefault();
    if (!event.repeat && !element("send").disabled) element("composer").requestSubmit();
});
async function selectView(debug, selection) {
    debugSelection = debug ? selection : undefined;
    highlightDebug();
    debugView = debug;
    controls();
    try { await refreshDebug(true); if (selection) highlightDebug(true); }
    catch (error) { showError(error.message); }
}
element("view-chat").addEventListener("click", () => selectView(false));
element("view-debug").addEventListener("click", () => selectView(true));
element("debug-search").addEventListener("input", () => searchDebug({ reset: true, scroll: true }));
element("debug-search").addEventListener("keydown", event => {
    if (event.isComposing || event.keyCode === 229) return;
    if (event.key === "Enter") {
        event.preventDefault();
        searchDebug({ direction: event.shiftKey ? -1 : 1, scroll: true });
    } else if (event.key === "Escape") {
        event.preventDefault();
        element("debug-search").value = "";
        searchDebug({ reset: true });
    }
});
element("debug-expand").addEventListener("click", () => {
    debugExpandChunks = ![...debugStreams.values()].some(details => details.open);
    debugRevealedKey = undefined;
    for (const details of debugStreams.values()) details.open = debugExpandChunks;
    updateExpandChunks();
});
element("debug-previous").addEventListener("click", () => searchDebug({ direction: -1, scroll: true }));
element("debug-next").addEventListener("click", () => searchDebug({ direction: 1, scroll: true }));
element("composer").addEventListener("submit", async event => {
    event.preventDefault();
    if (element("send").disabled) return;
    const text = element("query").value;
    const files = pendingFiles;
    draftSent = { text, files, messageCount: state.messages.length, version: draftVersion, accepted: false };
    element("query").value = "";
    pendingFiles = [];
    renderPendingFiles();
    await act("send", { query: text, files: files.map(({ name, base64 }) => ({ name, base64 })) });
});

async function poll() {
    await refresh();
    if (!stopped) setTimeout(poll, state?.busy ? 100 : 750);
}
poll();
