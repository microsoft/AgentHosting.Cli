import { execFile, spawn } from "node:child_process";
import { randomUUID } from "node:crypto";
import { mkdir, writeFile } from "node:fs/promises";
import { homedir } from "node:os";
import path from "node:path";

const maxNameAttempts = 100;
const commandTimeout = 10_000;

export async function downloadsDirectory({ home = homedir(), platform = process.platform, execute = execFile } = {}) {
    const paths = platform === "win32" ? path.win32 : path.posix;
    const fallback = paths.join(home, "Downloads");
    if (platform !== "win32" && platform !== "linux") return { directory: fallback };
    const command = platform === "win32" ? "powershell.exe" : "xdg-user-dir";
    const args = platform === "win32" ?
        ["-NoProfile", "-NonInteractive", "-Command",
            "[Console]::OutputEncoding = [System.Text.Encoding]::UTF8; (New-Object -ComObject Shell.Application).NameSpace('shell:Downloads').Self.Path"] :
        ["DOWNLOAD"];
    const { error, stdout } = await run(execute, command, args);
    const directory = typeof stdout === "string" ? stdout.trim() : "";
    if (!error && paths.isAbsolute(directory)) return { directory };
    return { directory: fallback, warning: "Could not determine your configured Downloads folder; saved to the default Downloads folder instead." };
}

export async function writeUnique(directory, name, content) {
    await mkdir(directory, { recursive: true, mode: 0o700 });
    const { name: stem, ext } = path.parse(name);
    for (let index = 0; index <= maxNameAttempts; index++) {
        const suffix = index === maxNameAttempts ? randomUUID() : index;
        const destination = path.join(directory, index ? `${stem} (${suffix})${ext}` : name);
        try {
            await writeFile(destination, content, { flag: "wx", mode: 0o600 });
            return destination;
        } catch (error) {
            if (error.code !== "EEXIST") throw error;
        }
    }
    throw new Error(`Too many files named ${name} already exist in ${directory}.`);
}

export function revealCommand(file, platform = process.platform) {
    if (platform === "darwin") return { command: "open", args: ["-R", file] };
    if (platform === "win32") return { command: "explorer.exe", args: [`/select,"${file}"`],
        options: { windowsVerbatimArguments: true, windowsHide: false } };
    return { command: "xdg-open", args: [path.posix.dirname(file)] };
}

function run(execute, command, args, options = {}) {
    return new Promise(resolve => {
        execute(command, args, { windowsHide: true, timeout: commandTimeout, ...options },
            (error, stdout) => resolve({ error: error ?? null, stdout }));
    });
}

function launchExplorer(launch, command, args, options) {
    return new Promise(resolve => {
        let child;
        try {
            child = launch(command, args, { shell: false, stdio: "ignore", detached: true, ...options });
        } catch (error) {
            resolve({ error });
            return;
        }
        child.once("error", error => resolve({ error }));
        child.once("spawn", () => {
            // Explorer can return a nonzero broker exit code after successfully revealing the file.
            child.unref();
            resolve({ error: null });
        });
    });
}

export async function saveAndReveal(file, {
    directory, home = homedir(), platform = process.platform, execute = execFile, launch = spawn, now = Date.now, writeZone = writeFile,
} = {}) {
    const resolved = directory === undefined ? await downloadsDirectory({ home, platform, execute }) : { directory };
    const destination = await writeUnique(resolved.directory, file.name, file.content);
    const warnings = resolved.warning ? [resolved.warning] : [];
    if (platform === "darwin") {
        const stamp = Math.floor(now() / 1000).toString(16);
        const { error } = await run(execute, "xattr", ["-w", "com.apple.quarantine", `0081;${stamp};msagent;`, destination]);
        if (error) warnings.push("macOS could not mark the file as downloaded; system security checks might not run.");
    } else if (platform === "win32") {
        try {
            await writeZone(`${destination}:Zone.Identifier`, "[ZoneTransfer]\r\nZoneId=3\r\n");
        } catch {
            warnings.push("Windows could not mark the file as downloaded; system security checks might not run.");
        }
    }
    const { command, args, options } = revealCommand(destination, platform);
    const { error } = platform === "win32" ? await launchExplorer(launch, command, args, options)
        : await run(execute, command, args, options);
    return { path: destination, revealed: !error, ...(warnings.length ? { warning: warnings.join(" ") } : {}) };
}
