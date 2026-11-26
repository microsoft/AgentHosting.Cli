import MarkdownIt from "./markdown-it.bundle.mjs";
import { limits } from "./limits.mjs";

const parser = new MarkdownIt({ html: false, linkify: false, typographer: false, maxNesting: limits.markdownNesting + 1 });
parser.validateLink = value => URL.canParse(value) && ["http:", "https:"].includes(new URL(value).protocol);
const tags = new Set(["p", "h1", "h2", "h3", "h4", "h5", "h6", "strong", "em", "del",
    "a", "ul", "ol", "li", "blockquote", "table", "thead", "tbody", "tr", "th", "td"]);

export function renderMarkdown(text, document = globalThis.document) {
    const root = document.createElement("div");
    root.className = "markdown";
    const limit = new Error("Markdown rendering limit reached.");
    let nodes = 0;
    const make = (tag, content) => {
        if (++nodes > limits.markdownElements) throw limit;
        const element = document.createElement(tag);
        if (content !== undefined) element.textContent = content;
        return element;
    };
    const render = (tokens, parent) => {
        const parents = [parent];
        for (const token of tokens) {
            if (token.level >= limits.markdownNesting) throw limit;
            if (token.hidden) continue;
            const current = parents.at(-1);
            if (token.type === "inline") render(token.children, current);
            else if (token.type === "text") current.append(token.content);
            else if (token.type === "softbreak") current.append("\n");
            else if (token.type === "hardbreak") current.append(make("br"));
            else if (token.type === "code_inline") current.append(make("code", token.content));
            else if (token.type === "image") current.append(make("span", `[Image: ${token.content}]`));
            else if (token.type === "fence" || token.type === "code_block") {
                const pre = make("pre");
                pre.append(make("code", token.content.replace(/\n$/, "")));
                current.append(pre);
            } else if (token.type === "hr") current.append(make("hr"));
            else {
                const tag = token.tag === "s" ? "del" : token.tag;
                if (!tags.has(tag)) throw limit;
                if (token.nesting === -1) {
                    parents.pop();
                } else if (token.nesting === 1) {
                    const element = make(tag);
                    if (tag === "a") {
                        element.href = token.attrGet("href");
                        element.target = "_blank";
                        element.rel = "noopener noreferrer";
                        element.referrerPolicy = "no-referrer";
                    } else if (tag === "ol") {
                        element.start = Number(token.attrGet("start") ?? 1);
                    }
                    if (tag === "table") {
                        const wrapper = make("div");
                        wrapper.className = "markdown-table";
                        wrapper.append(element);
                        current.append(wrapper);
                    } else current.append(element);
                    parents.push(element);
                } else throw limit;
            }
        }
    };
    try {
        if (text.length > limits.markdownCharacters) throw limit;
        render(parser.parse(text, {}), root);
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
