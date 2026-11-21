export function renderMarkdown(text, document = globalThis.document) {
    const root = document.createElement("div");
    root.className = "markdown";
    const limit = new Error("Markdown rendering limit reached.");
    let nodes = 0;
    const make = (tag, content) => {
        if (++nodes > 2000) throw limit;
        const element = document.createElement(tag);
        if (content !== undefined) element.textContent = content;
        return element;
    };
    const inline = (parent, source, depth = 0) => {
        if (depth >= 8) return parent.append(source);
        const tokens = /\\([\\`*_[\]{}()#+.!|>-])|(`+)([^`]*?)\2|!\[([^\]\n]*)\]\(([^)\n]*)\)|\[([^\]\n]*)\]\(([^)\n]*)\)|(\*\*|__|~~|\*|_)([^\n]+?)\8/g;
        let offset = 0;
        for (const match of source.matchAll(tokens)) {
            parent.append(source.slice(offset, match.index));
            if (match[1]) parent.append(match[1]);
            else if (match[2]) parent.append(make("code", match[3]));
            else if (match[4] !== undefined) parent.append(make("span", `[Image: ${match[4]}]`));
            else if (match[6] !== undefined) {
                const url = URL.canParse(match[7]) ? new URL(match[7]) : null;
                if (url && ["http:", "https:"].includes(url.protocol)) {
                    const link = make("a");
                    link.href = url.href;
                    link.target = "_blank";
                    link.rel = "noopener noreferrer";
                    link.referrerPolicy = "no-referrer";
                    inline(link, match[6], depth + 1);
                    parent.append(link);
                } else parent.append(match[0]);
            } else {
                const element = make(match[8] === "~~" ? "del" : match[8].length === 2 ? "strong" : "em");
                inline(element, match[9], depth + 1);
                parent.append(element);
            }
            offset = match.index + match[0].length;
        }
        parent.append(source.slice(offset));
    };
    const cells = line => line.trim().replace(/^\||\|$/g, "").split(/(?<!\\)\|/).map(cell => cell.trim());
    const tableHeader = (line, next) => line.includes("|") && next !== undefined &&
        cells(next).length === cells(line).length && cells(next).every(cell => /^:?-{3,}:?$/.test(cell));
    const listItem = line => /^ {0,3}([-+*]|\d+[.)])\s+(.+)$/.exec(line);
    const special = (line, next) => /^ {0,3}(?:`{3,}|~{3,}|#{1,6}\s|>|(?:-{3,}|\*{3,}|_{3,})\s*$)/.test(line) ||
        listItem(line) || tableHeader(line, next);
    try {
        if (text.length > 100_000) throw limit;
        const lines = text.replace(/\r\n?/g, "\n").split("\n");
        for (let index = 0; index < lines.length;) {
            const line = lines[index];
            if (!line.trim()) { index++; continue; }
            const fence = /^ {0,3}(`{3,}|~{3,})/.exec(line);
            if (fence) {
                const closing = new RegExp(`^ {0,3}${fence[1][0]}{${fence[1].length},}\\s*$`);
                const content = [];
                index++;
                while (index < lines.length && !closing.test(lines[index])) content.push(lines[index++]);
                if (index < lines.length) index++;
                const pre = make("pre");
                pre.append(make("code", content.join("\n")));
                root.append(pre);
                continue;
            }
            const heading = /^ {0,3}(#{1,6})\s+(.+?)(?:\s+#+\s*)?$/.exec(line);
            if (heading) {
                const title = make(`h${heading[1].length}`);
                inline(title, heading[2]);
                root.append(title);
                index++;
                continue;
            }
            if (/^ {0,3}(?:-{3,}|\*{3,}|_{3,})\s*$/.test(line)) {
                root.append(make("hr"));
                index++;
                continue;
            }
            if (tableHeader(line, lines[index + 1])) {
                const wrapper = make("div");
                wrapper.className = "markdown-table";
                const table = make("table");
                const appendRow = (values, tag) => {
                    const row = make("tr");
                    for (const value of values) {
                        const cell = make(tag);
                        inline(cell, value);
                        row.append(cell);
                    }
                    table.append(row);
                };
                appendRow(cells(line), "th");
                index += 2;
                while (index < lines.length && lines[index].includes("|") && lines[index].trim()) {
                    appendRow(cells(lines[index++]), "td");
                }
                wrapper.append(table);
                root.append(wrapper);
                continue;
            }
            const item = listItem(line);
            if (item) {
                const ordered = /^\d/.test(item[1]);
                const list = make(ordered ? "ol" : "ul");
                if (ordered) list.start = Number.parseInt(item[1], 10);
                let current = item;
                while (current && /^\d/.test(current[1]) === ordered) {
                    const entry = make("li");
                    inline(entry, current[2]);
                    list.append(entry);
                    index++;
                    current = index < lines.length ? listItem(lines[index]) : null;
                }
                root.append(list);
                continue;
            }
            if (/^ {0,3}>/.test(line)) {
                const quote = make("blockquote");
                const content = [];
                while (index < lines.length && /^ {0,3}>/.test(lines[index])) {
                    content.push(lines[index++].replace(/^ {0,3}> ?/, ""));
                }
                inline(quote, content.join("\n"));
                root.append(quote);
                continue;
            }
            const content = [line];
            index++;
            while (index < lines.length && lines[index].trim() && !special(lines[index], lines[index + 1])) {
                content.push(lines[index++]);
            }
            const paragraph = make("p");
            inline(paragraph, content.join("\n"));
            root.append(paragraph);
        }
    } catch (error) {
        if (error !== limit) throw error;
        const notice = document.createElement("p");
        notice.textContent = "This reply exceeds the Markdown formatting limit; showing its full text instead.";
        const plain = document.createElement("p");
        plain.textContent = text;
        root.replaceChildren(notice, plain);
    }
    return root;
}
