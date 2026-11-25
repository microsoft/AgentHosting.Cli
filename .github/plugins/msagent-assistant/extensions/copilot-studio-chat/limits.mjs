const queryCharacters = 16_000;
const fileBytes = 10 * 1024 * 1024;
const uploadFiles = 5;
const fileNameCharacters = 200;
const base64FileCharacters = Math.ceil(fileBytes / 3) * 4;

export const limits = Object.freeze({
    queryCharacters,
    fileBytes,
    uploadFiles,
    fileNameCharacters,
    base64FileCharacters,
    sendRequestBytes: base64FileCharacters + 4 * (uploadFiles - 1) +
        6 * (queryCharacters + uploadFiles * fileNameCharacters) + 4096,
    controlRequestBytes: 100_000,
    downloadReferenceBytes: base64FileCharacters + 4096,
    transcriptEntries: 100,
    transcriptCharacters: 1_000_000,
    debugBytes: 1_000_000,
    commandTimeoutMs: 120_000,
    setupOutputBytes: 4 * 1024 * 1024,
    liveOutputBytes: 16 * 1024 * 1024,
    markdownCharacters: 100_000,
    markdownElements: 2000,
    markdownNesting: 8,
});
