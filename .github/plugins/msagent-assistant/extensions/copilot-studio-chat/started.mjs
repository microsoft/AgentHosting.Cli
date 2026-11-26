import { randomBytes } from "node:crypto";
import { lstat, mkdir, readdir, readFile, rename, rmdir, unlink, utimes, writeFile } from "node:fs/promises";
import { homedir } from "node:os";
import path from "node:path";

const sessionPattern = /^[A-Za-z0-9_-]{1,128}$/;
const instancePattern = /^[A-Za-z0-9][A-Za-z0-9._-]{0,127}$/;
const temporaryPattern = /\.[0-9a-f]{12}\.tmp$/;
const day = 24 * 60 * 60 * 1000;

export function defaultStartedDirectory() {
    return path.join(process.env.COPILOT_HOME || path.join(homedir(), ".copilot"),
        "extensions", "copilot-studio-chat", "artifacts", "started");
}

export class StartedChats {
    constructor(directory = defaultStartedDirectory()) {
        this.directory = directory;
    }

    marker(sessionId, instanceId) {
        if (typeof sessionId !== "string" || !sessionPattern.test(sessionId) ||
            typeof instanceId !== "string" || !instancePattern.test(instanceId)) {
            throw new Error("Invalid canvas session or instance identifier.");
        }
        return path.join(this.directory, sessionId, instanceId);
    }

    async claim(sessionId, instanceId) {
        const file = this.marker(sessionId, instanceId);
        await mkdir(path.dirname(file), { recursive: true, mode: 0o700 });
        try {
            await writeFile(file, "", { flag: "wx", mode: 0o600 });
            return true;
        } catch (error) {
            if (error.code === "EEXIST") return false;
            throw error;
        }
    }

    async load(sessionId, instanceId) {
        try {
            const text = await readFile(this.marker(sessionId, instanceId), "utf8");
            return text ? JSON.parse(text) : null;
        } catch (error) {
            if (error.code === "ENOENT" || error instanceof SyntaxError) return null;
            throw error;
        }
    }

    async save(sessionId, instanceId, data) {
        const file = this.marker(sessionId, instanceId);
        const temporary = `${file}.${randomBytes(6).toString("hex")}.tmp`;
        await writeFile(temporary, JSON.stringify(data), { flag: "wx", mode: 0o600 });
        await rename(temporary, file);
    }

    async release(sessionId, instanceId) {
        try {
            await unlink(this.marker(sessionId, instanceId));
            return true;
        } catch (error) {
            if (error.code === "ENOENT") return false;
            throw error;
        }
    }
}

export async function pruneStarted(store, sessionId, isOpen, { now = Date.now(), maxAge = 30 * day, temporaryAge = 60 * 60 * 1000 } = {}) {
    let sessions;
    try {
        sessions = await readdir(store.directory, { withFileTypes: true });
    } catch (error) {
        if (error.code === "ENOENT") return [];
        throw error;
    }
    const removed = [];
    for (const session of sessions) {
        if (!session.isDirectory() || !sessionPattern.test(session.name)) continue;
        const current = session.name === sessionId;
        const directory = path.join(store.directory, session.name);
        for (const name of await readdir(directory).catch(() => [])) {
            const file = path.join(directory, name);
            const info = await lstat(file).catch(() => null);
            if (!info?.isFile()) continue;
            const age = now - info.mtimeMs;
            const stale = temporaryPattern.test(name) ? age > temporaryAge
                : age > maxAge && !(current && isOpen(name));
            if (stale && await unlink(file).then(() => true, () => false)) removed.push(path.join(session.name, name));
        }
        if (!current) await rmdir(directory).catch(() => {});
    }
    return removed;
}

export async function attachChat(chat, store, sessionId, instanceId, openInput, isClosed = () => false) {
    chat.persist = data => store.save(sessionId, instanceId, openInput ? { ...data, openInput } : data);
    if (await store.claim(sessionId, instanceId)) {
        if (openInput) {
            try {
                await chat.persist({ ...chat.saved(),
                    conversationIssue: "The chat may not have finished starting. Check the agent before starting a new chat." });
            } catch (error) {
                await store.release(sessionId, instanceId);
                throw error;
            }
        }
        if (isClosed()) {
            await store.release(sessionId, instanceId);
            throw new Error("This canvas was closed during setup.");
        }
        return { pending: chat.start("connect") };
    }
    const saved = await store.load(sessionId, instanceId);
    if (!chat.restore(saved)) await chat.persist(chat.saved());
    const now = new Date();
    await utimes(store.marker(sessionId, instanceId), now, now).catch(() => {});
    return {};
}
