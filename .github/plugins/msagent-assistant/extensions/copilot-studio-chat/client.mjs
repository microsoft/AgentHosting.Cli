import { spawn } from "node:child_process";
import { constants } from "node:fs";
import { access, open, stat } from "node:fs/promises";
import path from "node:path";

export async function resolveExecutable(configured, environment = process.env) {
    const windows = process.platform === "win32";
    const name = windows ? "msagent.cmd" : "msagent";
    const candidates = configured ? [configured] : (environment.PATH ?? "")
        .split(path.delimiter).filter(directory => path.isAbsolute(directory))
        .map(directory => path.join(directory, name));
    for (const candidate of candidates) {
        if (!path.isAbsolute(candidate)) throw new Error("The msagent executable path must be absolute.");
        if (windows && !/\.(?:exe|cmd)$/i.test(candidate)) {
            throw new Error("Supply a native .exe or the standard .NET 10 msagent.cmd launcher.");
        }
        try {
            await access(candidate, constants.X_OK);
        } catch (error) {
            if (!["ENOENT", "ENOTDIR", "EACCES"].includes(error.code)) throw error;
            continue;
        }
        return windows && /\.cmd$/i.test(candidate) ? resolveWindowsLauncher(candidate) : candidate;
    }
    throw new Error("msagent was not found. Install a compatible CLI or supply its absolute executable path.");
}

async function resolveWindowsLauncher(launcher) {
    const file = await open(launcher, "r");
    let source;
    try {
        const buffer = Buffer.alloc(8193);
        let length = 0;
        while (length < buffer.length) {
            const { bytesRead } = await file.read(buffer, length, buffer.length - length, length);
            if (!bytesRead) break;
            length += bytesRead;
        }
        source = length <= 8192 ? buffer.subarray(0, length).toString("utf8") : "";
    } finally {
        await file.close();
    }
    // Interpret only the current SDK's forwarding template; never execute batch source.
    const match = /^@echo off\r?\n"%~dp0(\.store\\microsoft\.agents\.hosting\.cli\\([0-9][0-9A-Za-z.+-]*)\\microsoft\.agents\.hosting\.cli\.(win-(?:x64|x86|arm64))\\\2\\tools\\net10\.0\\\3\\Microsoft\.Agents\.Hosting\.Cli\.exe)" %\*(?:\r?\n)?$/i.exec(source);
    if (!match) throw new Error("msagent.cmd is not a standard .NET 10 launcher. Supply the native CLI executable path.");
    const executable = path.join(path.dirname(launcher), match[1]);
    try {
        await access(executable, constants.X_OK);
        if (!(await stat(executable)).isFile()) throw new Error("Not a file.");
    } catch {
        throw new Error("The msagent launcher target is unavailable. Repair this installation or supply its native executable path.");
    }
    return executable;
}

export function runCommand(executable, args, { signal, timeout = 120_000, limit = 4 * 1024 * 1024, onStdout } = {}) {
    return new Promise((resolve, reject) => {
        let child;
        let failure;
        let output = "";
        let bytes = 0;
        let killTimer;
        const stop = message => {
            failure ??= new Error(message);
            if (child && child.exitCode === null && child.signalCode === null) {
                child.kill();
                killTimer ??= setTimeout(() => child.kill("SIGKILL"), 2000);
            }
        };
        if (signal?.aborted) return reject(new Error("Cancelled. Remote effects may continue if a message was sent."));
        try {
            // Windows tool-store paths can exceed CreateProcess's ordinary MAX_PATH limit.
            const command = process.platform === "win32" ? path.toNamespacedPath(executable) : executable;
            child = spawn(command, args, { shell: false, windowsHide: true, stdio: ["ignore", "pipe", "pipe"] });
        } catch {
            return reject(new Error("msagent could not be started."));
        }
        child.stdout.setEncoding("utf8");
        child.stdout.on("data", text => {
            bytes += Buffer.byteLength(text);
            if (bytes > limit) stop("CLI output exceeded the limit. No message was retried.");
            else {
                output += text;
                if (!failure && onStdout) {
                    try { onStdout(text); }
                    catch { stop("The CLI activity stream could not be processed. No message was retried."); }
                }
            }
        });
        child.stderr.on("data", text => {
            bytes += text.length;
            if (bytes > limit) stop("CLI diagnostics exceeded the limit. No message was retried.");
        });
        const onAbort = () => stop("Cancelled. Remote effects may continue if a message was sent.");
        signal?.addEventListener("abort", onAbort, { once: true });
        const timer = setTimeout(() => stop("CLI operation timed out. Remote effects may continue; no message was retried."), timeout);
        child.on("error", () => { failure ??= new Error("msagent could not be started."); });
        child.on("close", code => {
            clearTimeout(timer);
            clearTimeout(killTimer);
            signal?.removeEventListener("abort", onAbort);
            if (failure) return reject(failure);
            resolve({ code, output });
        });
        if (signal?.aborted) onAbort();
    });
}

export function parseResult(result) {
    let data;
    try { data = JSON.parse(result.output); }
    catch { throw new Error("The CLI returned incomplete or invalid JSON. No message was retried."); }
    if (!data || typeof data !== "object" || Array.isArray(data)) throw new Error("Invalid CLI result.");
    if (result.code !== 0 || data.success !== true) {
        const auth = result.code === 3 || ["authentication-required", "authentication-failed"].includes(data.errorKind);
        const code = Number.isInteger(data.errorCode) ? ` (${data.errorCode})` : "";
        const detail = cliDetail(data, auth);
        const error = new Error(detail
            ? `${detail}${code}`
            : auth
                ? "Sign-in or Copilot Studio consent is required."
                : `CLI operation failed${code}. Check the selected target and CLI setup. No message was retried.`);
        error.authRequired = auth;
        error.data = data;
        throw error;
    }
    return data;
}

const displayableKinds = new Set(["authentication-required", "authentication-failed", "credential-storage-unavailable", "identity-mismatch"]);
const isAuthenticationCode = code => Number.isInteger(code) && code >= 12000 && code < 13000;
const secretPatterns = [
    /\bBearer\s+[A-Za-z0-9._~+/=-]+/gi,
    /\beyJ[A-Za-z0-9_-]{5,}\.[A-Za-z0-9_-]{5,}\.[A-Za-z0-9_-]*/g,
    /\b(access_token|refresh_token|id_token|client_secret|password|secret|code|assertion|sig|signature|key)\s*[=:]\s*[^\s&"',;]+/gi,
    /\bhttps?:\/\/[^\s?#"']+\?[^\s"']*/gi,
    /\b[A-Za-z0-9+/_-]{40,}={0,2}/g,
];

function redact(value) {
    return secretPatterns.reduce((text, pattern) => text.replace(pattern, "[redacted]"), value);
}

function cliDetail(data, auth) {
    if (!auth && !displayableKinds.has(data.errorKind) && !isAuthenticationCode(data.errorCode)) return "";
    return [data.errorMessage, data.remediation]
        .filter(value => typeof value === "string" && value.trim())
        .map(value => redact(value.replace(/[\x00-\x1f\x7f]+/g, " ")).trim().slice(0, 500).trim())
        .join(" ");
}

const identifier = /^[a-zA-Z0-9_-]{1,256}$/;
const guid = /^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$/i;
const same = (left, right) => typeof left === "string" && typeof right === "string" && left.toLowerCase() === right.toLowerCase();

export function validateTarget(target) {
    if (!target || typeof target !== "object" || Array.isArray(target)) throw new Error("Select an explicit target.");
    if (target.project !== undefined) {
        if (Object.keys(target).some(key => !["project", "agentId", "deploymentId"].includes(key)) ||
            typeof target.project !== "string" || !path.isAbsolute(target.project) || /[\x00-\x1f]/.test(target.project) ||
            !identifier.test(target.agentId ?? "") || !identifier.test(target.deploymentId ?? "")) {
            throw new Error("A registered target requires an absolute project path, local agent ID and deployment ID.");
        }
    } else {
        if (Object.keys(target).some(key => !["schemaName", "environmentId", "tenantId", "deploymentName"].includes(key)) ||
            !["schemaName", "environmentId", "deploymentName"].every(key => identifier.test(target[key] ?? "")) ||
            !guid.test(target.tenantId ?? "")) {
            throw new Error("An explicit target requires schema name, environment ID, tenant GUID and deployment label.");
        }
    }
    return { ...target };
}

export function validateConfiguredInput(input) {
    if (!input || typeof input !== "object" || Array.isArray(input) ||
        Object.keys(input).some(key => !["chatId", "target", "agentName", "executablePath"].includes(key)) ||
        typeof input.chatId !== "string" || !/^[a-zA-Z0-9_-]{1,128}$/.test(input.chatId) ||
        (input.agentName !== undefined && (typeof input.agentName !== "string" || input.agentName.length > 256 || !/\S/.test(input.agentName))) ||
        (input.executablePath !== undefined && (typeof input.executablePath !== "string" || input.executablePath.length > 4096 ||
            !path.isAbsolute(input.executablePath) || /[\x00-\x1f]/.test(input.executablePath)))) {
        throw new Error("Invalid configured canvas input.");
    }
    validateTarget(input.target);
    return input;
}

export async function resolveTarget(target, command) {
    validateTarget(target);
    if (!target.project) return { ...target };
    const result = parseResult(await command(["agent", "show", "--project", target.project,
        "--agent-id", target.agentId, "--json", "--non-interactive"]));
    const agents = result.agents?.filter(agent => same(agent.agentId, target.agentId));
    const deployments = result.deployments?.filter(deployment =>
        same(deployment.deploymentId, target.deploymentId) && same(deployment.agentId, target.agentId));
    if (agents?.length !== 1 || deployments?.length !== 1 ||
        agents[0].agentType !== "MCSAgent" || deployments[0].agentType !== "MCSAgent") {
        throw new Error("The selected Copilot Studio agent/deployment association could not be verified.");
    }
    const resolved = {
        schemaName: agents[0].mcsSchemaName,
        environmentId: deployments[0].environmentId,
        tenantId: deployments[0].tenantId,
        deploymentName: deployments[0].deploymentName,
    };
    validateTarget(resolved);
    return { ...target, ...resolved };
}

export function askArguments(target, query, conversationId) {
    const args = ["test", "ask", "--schema-name", target.schemaName, "--environment-id", target.environmentId,
        "--tenant", target.tenantId, "--json", "--non-interactive"];
    if (query === undefined) args.push("--start-conversation");
    else args.push(`--query=${query}`);
    if (target.project) args.push("--project", target.project, "--agent-id", target.agentId, "--deployment-id", target.deploymentId);
    else args.push("--deployment-name", target.deploymentName);
    if (conversationId) args.push("--conversation-id", conversationId);
    return args;
}

export class ReplyStream {
    constructor(activities) {
        if (!Array.isArray(activities)) throw new Error("The CLI response has no activities array.");
        this.parts = [];
        this.streams = new Map();
        for (const activity of activities) this.write(activity);
    }

    write(activity) {
        if (!activity || typeof activity !== "object" || !["message", "typing"].includes(activity.type) ||
            (activity.from?.role && activity.from.role !== "bot") || activity.delta !== undefined ||
            activity.channelData?.delta !== undefined) return;
        const stream = Array.isArray(activity.entities) ? activity.entities.find(entity =>
            typeof entity?.type === "string" && entity.type.toLowerCase() === "streaminfo") : null;
        const streamType = stream?.streamType ?? stream?.streamtype;
        const type = typeof streamType === "string" ? streamType.toLowerCase() : undefined;
        if (activity.type === "typing" && type !== "streaming") return;
        if (stream && !["streaming", "final"].includes(type)) return;
        const id = stream?.streamId ?? stream?.streamID;
        const identified = typeof id === "string" && id.trim().length > 0;
        const value = stream?.streamSequence;
        const sequence = typeof value === "number" || (typeof value === "string" && /^-?\d+$/.test(value))
            ? Number(value) : NaN;
        const ordered = Number.isSafeInteger(sequence);
        const chunkType = activity.channelData?.chunkType ?? "replace";
        if (type === "streaming" && (!["delta", "replace"].includes(chunkType) || (!identified && !ordered))) return;
        let part = stream && identified ? this.streams.get(id) : type === "streaming" ? this.unidentified : undefined;
        if (!part && stream && identified && this.unidentified) {
            part = this.unidentified;
            this.streams.set(id, part);
            this.unidentified = undefined;
        }
        if (part && (part.finalized || (ordered && part.sequence !== undefined && sequence <= part.sequence))) return;
        if (!part) {
            part = {};
            this.parts.push(part);
            if (stream && identified) this.streams.set(id, part);
            else if (type === "streaming") this.unidentified = part;
        }
        const messages = [];
        const text = (type === "streaming" && chunkType === "delta" ? part.text ?? "" : "") +
            (typeof activity.text === "string" ? activity.text : "");
        if (text.trim()) messages.push(text);
        const attachments = Array.isArray(activity.attachments) ? activity.attachments : [];
        if (attachments.some(attachment => typeof attachment?.contentUrl !== "string" ||
            !/^(?:https:|data:)/.test(attachment.contentUrl) ||
            (typeof attachment.contentType === "string" && attachment.contentType.startsWith("application/vnd.microsoft.card.")))) {
            messages.push("[This response contains cards or attachments. Interactive content is not supported in this text canvas.]");
        }
        Object.assign(part, { text, messages, attachments, finalized: type !== "streaming", sequence: ordered ? sequence : undefined });
    }

    snapshot() {
        return {
            text: this.parts.flatMap(part => part.messages).join("\n\n"),
            partial: this.parts.some(part => !part.finalized),
            attachments: this.parts.flatMap(part => part.attachments ?? []),
        };
    }
}

export function visibleMessages(activities) {
    return new ReplyStream(activities).parts.filter(part => part.finalized).flatMap(part => part.messages);
}

export function identity(status) {
    if (!status.signedIn || typeof status.account !== "string" || !status.account.trim() ||
        typeof status.tenantId !== "string" || !status.tenantId.trim() || status.cloud !== "prod") {
        throw new Error("Sign in to a production-cloud CLI account before chatting.");
    }
    return JSON.stringify([status.account.toLowerCase(), status.tenantId.toLowerCase(), status.cloud]);
}
