const { build } = require("esbuild");
const { readFile, readdir, writeFile } = require("node:fs/promises");
const path = require("node:path");

async function buildCanvasMarkdown(directory = path.resolve(__dirname, "../../extensions/copilot-studio-chat")) {
    const scripts = path.resolve(__dirname, "..");
    const result = await build({
        absWorkingDir: scripts,
        entryPoints: [require.resolve("markdown-it")],
        outfile: path.join(directory, "markdown-it.bundle.mjs"),
        bundle: true,
        platform: "browser",
        format: "esm",
        target: "es2022",
        legalComments: "eof",
        metafile: true,
        banner: { js: "/* Third-party licenses: see markdown-it.NOTICES.txt. */" },
    });
    const packages = new Set(Object.keys(result.metafile.inputs).map(file =>
        file.match(/(?:^|\/)node_modules\/((?:@[^/]+\/)?[^/]+)/)?.[1]).filter(Boolean));
    const notices = [];
    for (const name of [...packages].sort()) {
        const root = path.join(scripts, "node_modules", name);
        const license = (await readdir(root)).find(file => /^licen[sc]e(?:[-.].+)?$/i.test(file));
        if (!license) throw new Error(`Missing license for bundled dependency ${name}.`);
        notices.push(`${name}\n\n${await readFile(path.join(root, license), "utf8")}`);
    }
    await writeFile(path.join(directory, "markdown-it.NOTICES.txt"), notices.join("\n\n"));
    return result;
}

module.exports = { buildCanvasMarkdown };

if (require.main === module) {
    buildCanvasMarkdown().catch(error => {
        console.error(error);
        process.exitCode = 1;
    });
}
