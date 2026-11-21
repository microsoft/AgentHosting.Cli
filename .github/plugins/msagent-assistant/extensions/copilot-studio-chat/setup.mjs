export class SetupChat {
    constructor(requestSetup, createChat) {
        this.requestSetup = requestSetup;
        this.createChat = createChat;
        this.state = { status: "setup-required", setupRequired: true, setupRequested: false,
            busy: false, operation: null, target: {}, messages: [], connected: false, error: null };
    }

    snapshot() {
        return this.chat ? this.chat.snapshot() : structuredClone(this.state);
    }

    debugSnapshot() {
        return this.chat ? this.chat.debugSnapshot() : { revision: 0, messages: [], responses: [], truncated: false };
    }

    get files() {
        return this.chat?.files;
    }

    get pending() {
        return this.chat ? this.chat.pending : this.setupPending;
    }

    async configure(input) {
        if (this.closed) throw new Error("This canvas is closed.");
        if (this.chat || this.configuring) throw new Error("This canvas already has an agent or is being configured.");
        this.configuring = (async () => {
            await this.setupPending;
            if (this.closed) throw new Error("This canvas is closed.");
            this.state.busy = true;
            this.state.operation = "configure";
            this.state.error = null;
            try {
                const chat = await this.createChat(input, () => this.closed);
                if (this.closed) {
                    await chat.close();
                    throw new Error("This canvas was closed during setup.");
                }
                this.chat = chat;
            } catch (error) {
                this.state.setupRequested = false;
                this.state.error = `Could not connect the selected agent: ${error.message}`;
                throw error;
            } finally {
                this.state.busy = false;
                this.state.operation = null;
            }
        })();
        try {
            await this.configuring;
        } finally {
            this.configuring = undefined;
        }
    }

    start(operation, input) {
        if (this.closed) throw new Error("This canvas is closed.");
        if (this.chat) return this.chat.start(operation, input);
        if (operation !== "setup") throw new Error("Choose an agent with Copilot before starting a chat.");
        if (this.state.busy || this.state.setupRequested) throw new Error("Continue choosing an agent in the Copilot chat.");
        this.state.busy = true;
        this.state.operation = "setup";
        this.state.error = null;
        this.setupPending = Promise.resolve().then(() => this.requestSetup()).then(() => {
            this.state.setupRequested = true;
        }).catch(error => {
            this.state.error = `Could not ask Copilot to choose an agent: ${error.message}`;
        }).finally(() => {
            this.state.busy = false;
            this.state.operation = null;
        });
        return this.setupPending;
    }

    cancel() {
        if (this.chat) return this.chat.cancel();
        throw new Error("Continue setup in the main Copilot chat.");
    }

    async close() {
        this.closed = true;
        await this.setupPending;
        await this.configuring?.catch(() => {});
        await this.chat?.close();
    }
}
