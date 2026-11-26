import assert from "node:assert/strict";
import path from "node:path";
import { Chat } from "../plugins/msagent-assistant/extensions/copilot-studio-chat/chat.mjs";
import { resolveExecutable } from "../plugins/msagent-assistant/extensions/copilot-studio-chat/client.mjs";

const [installed, expectedNative] = process.argv.slice(2);
assert.equal(process.platform, "win32");
assert(path.isAbsolute(installed) && path.isAbsolute(expectedNative));
const executable = await resolveExecutable(undefined, { PATH: installed });
assert.equal(executable, expectedNative);
const chat = new Chat(executable, {
    schemaName: "offline_fixture", environmentId: "offline-environment",
    tenantId: "11111111-1111-1111-1111-111111111111", deploymentName: "offline",
});
try {
    await chat.checkCapabilities();
    assert.equal(chat.capable, true);
    assert.equal(chat.conversationId, undefined);
} finally {
    await chat.close();
}
console.log("tier=package check=windows-canvas-launcher status=PASS discovery=isolated capabilities=PASS shell=false live=false");
