export class ActivityReader {
    constructor(onActivity) {
        this.onActivity = onActivity;
        this.prefix = "";
        this.started = false;
        this.done = false;
        this.depth = 0;
        this.record = "";
        this.quoted = false;
        this.escaped = false;
        this.separator = false;
    }

    push(text) {
        if (this.done) return;
        if (!this.started) {
            this.prefix += text;
            const start = this.prefix.indexOf("[");
            if (start < 0) {
                if (this.prefix.length > 1024) throw new Error("Invalid activity stream header.");
                return;
            }
            if (!/^\s*\{\s*"schemaVersion"\s*:\s*"1"\s*,\s*"activities"\s*:\s*\[$/.test(this.prefix.slice(0, start + 1))) {
                throw new Error("Invalid activity stream header.");
            }
            text = this.prefix.slice(start + 1);
            this.prefix = "";
            this.started = true;
        }
        let recordStart = this.depth > 0 ? 0 : -1;
        for (let index = 0; index < text.length; index++) {
            const character = text[index];
            if (this.depth === 0) {
                if (/\s/.test(character)) continue;
                if (character === "]") {
                    this.done = true;
                    return;
                }
                if (this.separator) {
                    if (character !== ",") throw new Error("Invalid activity separator.");
                    this.separator = false;
                    continue;
                }
                if (character !== "{") throw new Error("Invalid activity.");
                recordStart = index;
                this.depth = 1;
                continue;
            }
            if (this.quoted) {
                if (this.escaped) this.escaped = false;
                else if (character === "\\") this.escaped = true;
                else if (character === '"') this.quoted = false;
            } else if (character === '"') this.quoted = true;
            else if (character === "{" || character === "[") this.depth++;
            else if (character === "}" || character === "]") this.depth--;
            if (this.depth === 0) {
                this.record += text.slice(recordStart, index + 1);
                const activity = JSON.parse(this.record);
                this.record = "";
                recordStart = -1;
                this.separator = true;
                this.onActivity(activity);
            }
        }
        if (recordStart >= 0) this.record += text.slice(recordStart);
    }
}
