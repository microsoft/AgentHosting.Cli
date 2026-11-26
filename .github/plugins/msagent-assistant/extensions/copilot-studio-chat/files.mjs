import { randomBytes } from "node:crypto";
import { lookup } from "node:dns";
import { mkdtemp, mkdir, writeFile, rm } from "node:fs/promises";
import { get } from "node:https";
import { BlockList, isIP } from "node:net";
import { tmpdir } from "node:os";
import path from "node:path";
import { limits } from "./limits.mjs";

export const fileLimit = limits.fileBytes;
const blocked = new BlockList();
for (const [address, prefix] of [
    ["0.0.0.0", 8], ["10.0.0.0", 8], ["100.64.0.0", 10], ["127.0.0.0", 8],
    ["169.254.0.0", 16], ["172.16.0.0", 12], ["192.0.0.0", 24], ["192.0.2.0", 24],
    ["192.168.0.0", 16], ["198.18.0.0", 15], ["198.51.100.0", 24], ["203.0.113.0", 24],
    ["224.0.0.0", 4], ["240.0.0.0", 4],
]) blocked.addSubnet(address, prefix);
const publicV6 = new BlockList();
publicV6.addSubnet("2000::", 3, "ipv6");
for (const [address, prefix] of [["2001::", 23], ["2001:db8::", 32], ["2002::", 16]]) {
    blocked.addSubnet(address, prefix, "ipv6");
}

export function publicAddress(address) {
    const family = isIP(address);
    return family === 4 ? !blocked.check(address) :
        family === 6 && publicV6.check(address, "ipv6") && !blocked.check(address, "ipv6");
}

function safeUrl(address) {
    const url = new URL(address);
    const host = url.hostname.replace(/^\[|\]$/g, "");
    if (url.protocol !== "https:" || url.username || url.password || (url.port && url.port !== "443") ||
        (isIP(host) && !publicAddress(host))) throw new Error("Only public HTTPS file URLs are supported.");
    return url;
}

export async function downloadPublicFile(address, signal, redirects = 0) {
    const url = safeUrl(address);
    if (redirects > 3) throw new Error("The file has too many redirects.");
    const deadline = AbortSignal.any([...(signal ? [signal] : []), AbortSignal.timeout(30_000)]);
    return new Promise((resolve, reject) => {
        const request = get(url, {
            signal: deadline,
            headers: { "Accept-Encoding": "identity" },
            lookup(host, options, callback) {
                lookup(host, { all: true }, (error, addresses) => {
                    if (error) return callback(error);
                    if (!addresses.length || addresses.some(entry => !publicAddress(entry.address))) {
                        return callback(new Error("Private download destinations are not allowed."));
                    }
                    if (options.all) callback(null, addresses);
                    else callback(null, addresses[0].address, addresses[0].family);
                });
            },
        }, response => {
            if ([301, 302, 303, 307, 308].includes(response.statusCode) && response.headers.location) {
                response.destroy();
                try { resolve(downloadPublicFile(new URL(response.headers.location, url).href, deadline, redirects + 1)); }
                catch { reject(new Error("The agent returned an invalid file redirect.")); }
                return;
            }
            if (response.statusCode !== 200 || Number(response.headers["content-length"]) > fileLimit ||
                (response.headers["content-encoding"] && response.headers["content-encoding"] !== "identity")) {
                response.destroy();
                return reject(new Error("The file is unavailable, requires sign-in, or exceeds 10 MiB."));
            }
            const chunks = [];
            let bytes = 0;
            response.on("data", chunk => {
                bytes += chunk.length;
                if (bytes > fileLimit) response.destroy(new Error("The file exceeds 10 MiB."));
                else chunks.push(chunk);
            });
            response.on("error", reject);
            response.on("end", () => resolve(Buffer.concat(chunks)));
        });
        request.on("error", () => reject(new Error("The returned file could not be downloaded. Its link may have expired or require sign-in.")));
    });
}

function validName(name) {
    return typeof name === "string" && name.length > 0 && name.length <= limits.fileNameCharacters &&
        !/[\\/:<>?"*|\x00-\x1f\x7f]/.test(name) && !/^[. ]+$|[. ]$/.test(name) &&
        !/^(?:con|prn|aux|nul|conin\$|conout\$|com[1-9\u00b9\u00b2\u00b3]|lpt[1-9\u00b9\u00b2\u00b3])(?: *\.|$)/i.test(name);
}

export function uploads(input = []) {
    if (!Array.isArray(input) || input.length > limits.uploadFiles) throw new Error("Attach at most five files.");
    let total = 0;
    return input.map(file => {
        if (!file || !validName(file.name) || typeof file.base64 !== "string" ||
            file.base64.length > limits.base64FileCharacters || file.base64.length % 4 !== 0 ||
            !/^[A-Za-z0-9+/]*={0,2}$/.test(file.base64)) {
            throw new Error("An attachment has an invalid filename or file data.");
        }
        const content = Buffer.from(file.base64, "base64");
        total += content.length;
        if (total > fileLimit) throw new Error("Attachments exceed the 10 MiB total limit.");
        return { name: file.name, size: content.length, content };
    });
}

export async function withUploadedFiles(files, action, { remove = rm, warn = message => process.emitWarning(message) } = {}) {
    if (!files.length) return action([]);
    const directory = await mkdtemp(path.join(tmpdir(), "msagent-canvas-"));
    try {
        const paths = [];
        for (const [index, file] of files.entries()) {
            const folder = path.join(directory, String(index));
            await mkdir(folder, { mode: 0o700 });
            const destination = path.join(folder, file.name);
            await writeFile(destination, file.content, { mode: 0o600, flag: "wx" });
            paths.push(destination);
        }
        return await action(paths);
    } finally {
        try {
            await remove(directory, { recursive: true, force: true, maxRetries: 3 });
        } catch {
            warn("Copilot Studio chat could not remove temporary upload files. They may remain in the system temporary directory.");
        }
    }
}

export class FileDownloads {
    constructor(fetchFile = downloadPublicFile) {
        this.fetchFile = fetchFile;
        this.clear();
    }

    clear() {
        this.entries = new Map();
        this.turns = new Map();
        this.bytes = 0;
    }

    add(turnId, attachment) {
        const url = attachment?.contentUrl;
        if (typeof url !== "string" || (!url.startsWith("data:") && !url.startsWith("https:")) ||
            (typeof attachment.contentType === "string" && attachment.contentType.startsWith("application/vnd.microsoft.card."))) return null;
        const existing = this.turns.get(turnId)?.get(url);
        if (existing) return { id: existing, name: this.entries.get(existing).name };
        const name = validName(attachment.name) ? attachment.name : "download";
        const bytes = Buffer.byteLength(url);
        if (bytes > limits.downloadReferenceBytes) throw new Error("Returned file data exceeds the 10 MiB limit.");
        while (this.entries.size && this.bytes + bytes > limits.downloadReferenceBytes) {
            const [id, oldest] = this.entries.entries().next().value;
            this.bytes -= Buffer.byteLength(oldest.url);
            this.entries.delete(id);
            const turn = this.turns.get(oldest.turnId);
            turn.delete(oldest.url);
            if (!turn.size) this.turns.delete(oldest.turnId);
        }
        const id = randomBytes(16).toString("hex");
        this.entries.set(id, { turnId, url, name });
        if (!this.turns.has(turnId)) this.turns.set(turnId, new Map());
        this.turns.get(turnId).set(url, id);
        this.bytes += bytes;
        return { id, name };
    }

    async download(id, signal) {
        const file = this.entries.get(id);
        if (!file) throw new Error("This file is no longer available. Ask the agent to provide it again.");
        let content;
        if (file.url.startsWith("data:")) {
            const match = /^data:[^,]*;base64,([A-Za-z0-9+/]*={0,2})$/.exec(file.url);
            if (!match) throw new Error("The agent returned unsupported file data.");
            content = uploads([{ name: file.name, base64: match[1] }])[0].content;
        } else content = await this.fetchFile(file.url, signal);
        if (content.length > fileLimit) throw new Error("The file exceeds 10 MiB.");
        return { name: file.name, content };
    }
}
