import { createServer } from "node:http";
import { randomBytes, timingSafeEqual } from "node:crypto";
import { readFile } from "node:fs/promises";
import path from "node:path";
import { limits } from "./limits.mjs";
import { saveAndReveal } from "./save.mjs";

export async function startServer(chat, { saveFile = saveAndReveal } = {}) {
    const capability = randomBytes(32).toString("hex");
    const assets = new Map(await Promise.all([
        ["/", "index.html", "text/html; charset=utf-8"],
        ["/app.js", "app.js", "text/javascript; charset=utf-8"],
        ["/markdown.mjs", "markdown.mjs", "text/javascript; charset=utf-8"],
        ["/markdown-it.bundle.mjs", "markdown-it.bundle.mjs", "text/javascript; charset=utf-8"],
        ["/limits.mjs", "limits.mjs", "text/javascript; charset=utf-8"],
        ["/state.mjs", "state.mjs", "text/javascript; charset=utf-8"],
        ["/style.css", "style.css", "text/css; charset=utf-8"],
    ].map(async ([route, file, type]) => [route, { body: await readFile(new URL(file, import.meta.url)), type }])));
    let origin;
    const server = createServer((request, response) => {
        response.setHeader("Cache-Control", "no-store");
        response.setHeader("X-Content-Type-Options", "nosniff");
        response.setHeader("Referrer-Policy", "no-referrer");
        response.setHeader("Content-Security-Policy", "default-src 'none'; script-src 'self'; style-src 'self' 'unsafe-inline'; connect-src 'self'; img-src 'none'; object-src 'none'; base-uri 'none'; form-action 'none'");
        const reply = (code, data) => {
            response.writeHead(code, { "Content-Type": "application/json; charset=utf-8" });
            response.end(JSON.stringify(data));
        };
        const route = request.url;
        if (request.headers.host !== new URL(origin).host ||
            (request.headers.origin && request.headers.origin !== origin)) {
            request.resume();
            return reply(403, { error: "Origin or host rejected." });
        }
        if (request.method === "GET" && assets.has(route)) {
            const asset = assets.get(route);
            response.writeHead(200, { "Content-Type": asset.type });
            return response.end(asset.body);
        }
        const authorization = request.headers.authorization ?? "";
        const expected = `Bearer ${capability}`;
        if (Buffer.byteLength(authorization) !== Buffer.byteLength(expected) ||
            !timingSafeEqual(Buffer.from(authorization), Buffer.from(expected))) {
            request.resume();
            return reply(403, { error: "Local capability required." });
        }
        if (request.method === "GET" && route === "/state") return reply(200, chat.snapshot());
        if (request.method === "GET" && route === "/debug") return reply(200, chat.debugSnapshot());
        const download = /^\/files\/([a-f0-9]{32})$/.exec(route);
        if (request.method === "GET" && download) {
            if (!chat.files) return reply(400, { error: "Choose an agent before downloading files." });
            const controller = new AbortController();
            response.once("close", () => controller.abort());
            void chat.files.download(download[1], controller.signal).then(file => {
                response.writeHead(200, {
                    "Content-Type": "application/octet-stream", "Content-Length": file.content.length,
                    "Content-Disposition": `attachment; filename*=UTF-8''${encodeURIComponent(file.name).replace(/['()*]/g, character => `%${character.charCodeAt(0).toString(16)}`)}`,
                });
                response.end(file.content);
            }).catch(error => { if (!response.destroyed) reply(400, { error: error.message }); });
            return;
        }
        const save = /^\/files\/([a-f0-9]{32})\/save$/.exec(route);
        if (request.method !== "POST" || request.headers.origin !== origin ||
            request.headers["content-type"] !== "application/json" ||
            (!save && !["/setup", "/check", "/connect", "/send", "/reset", "/cancel"].includes(route))) {
            request.resume();
            return reply(400, { error: "Unsupported request." });
        }
        let body = "";
        let bytes = 0;
        const limit = route === "/send" ? limits.sendRequestBytes : limits.controlRequestBytes;
        request.setEncoding("utf8");
        request.on("data", chunk => {
            bytes += Buffer.byteLength(chunk);
            if (bytes <= limit) body += chunk;
        });
        request.on("end", () => {
            if (bytes > limit) return reply(413, { error: "Request too large." });
            try {
                const input = JSON.parse(body);
                if (!input || typeof input !== "object" || Array.isArray(input)) throw new Error("Invalid request.");
                if (save) {
                    if (!chat.files) throw new Error("Choose an agent before saving files.");
                    const controller = new AbortController();
                    response.once("close", () => controller.abort());
                    void chat.files.download(save[1], controller.signal).then(async file => {
                        try {
                            const saved = await saveFile(file);
                            const savedName = path.basename(saved.path);
                            try {
                                if (!await chat.markFileSaved(save[1], savedName)) {
                                    throw new Error("The returned file is no longer in the transcript.");
                                }
                            }
                            catch {
                                saved.warning = [saved.warning, "The file was saved, but its saved status could not be recorded in the transcript."]
                                    .filter(Boolean).join(" ");
                            }
                            return { ...saved, savedName };
                        }
                        catch { throw new Error("The file could not be saved to your Downloads folder."); }
                    }).then(saved => { if (!response.destroyed) reply(200, saved); },
                        error => { if (!response.destroyed) reply(400, { error: error.message }); });
                    return;
                }
                if (route === "/cancel") chat.cancel();
                else chat.start(route.slice(1), input);
                reply(202, { accepted: true });
            } catch (error) {
                reply(400, { error: error instanceof SyntaxError ? "Invalid JSON." : error.message });
            }
        });
        request.on("error", () => { if (!response.writableEnded) reply(400, { error: "Request interrupted." }); });
    });
    server.requestTimeout = 10_000;
    server.headersTimeout = 10_000;
    server.maxConnections = 32;
    await new Promise((resolve, reject) => {
        server.once("error", reject);
        server.listen(0, "127.0.0.1", resolve);
    });
    origin = `http://127.0.0.1:${server.address().port}`;
    return {
        url: `${origin}/#${capability}`,
        async close() {
            const closed = new Promise((resolve, reject) => server.close(error => error ? reject(error) : resolve()));
            server.closeAllConnections();
            await chat.close();
            await closed;
        },
    };
}
