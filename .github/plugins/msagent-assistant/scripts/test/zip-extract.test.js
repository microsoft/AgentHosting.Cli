const assert = require("node:assert/strict");
const fs = require("node:fs");
const os = require("node:os");
const path = require("node:path");
const test = require("node:test");
const { after } = require("node:test");

const { DEFAULT_LIMITS, extractZip, safeSegments } = require("../src/zip-extract");
const { SYMLINK, makeZip } = require("./zip-fixture");

const roots = [];
after(() => {
  for (const root of roots) fs.rmSync(root, { recursive: true, force: true });
});

function tempDir() {
  const root = fs.mkdtempSync(path.join(os.tmpdir(), "zip-extract-test-"));
  roots.push(root);
  return root;
}

function listFiles(dir) {
  if (!fs.existsSync(dir)) return [];
  return fs.readdirSync(dir, { recursive: true, withFileTypes: true })
    .filter((d) => d.isFile())
    .map((d) => path.relative(dir, path.join(d.parentPath ?? d.path, d.name)).split(path.sep).join("/"))
    .sort();
}

test("extracts stored and deflated entries and skips directory entries", () => {
  const root = tempDir();
  const dest = path.join(root, "out");
  const zip = makeZip([
    { name: "skill/", data: "", method: 0 },
    { name: "skill/SKILL.md", data: "---\nname: demo\n---\nBody.\n" },
    { name: "skill/scripts/run.py", data: "print('hi')\n", method: 0 },
  ]);
  const rels = extractZip(zip, dest);
  assert.deepEqual(rels.sort(), ["skill/SKILL.md", "skill/scripts/run.py"]);
  assert.equal(fs.readFileSync(path.join(dest, "skill", "SKILL.md"), "utf8"), "---\nname: demo\n---\nBody.\n");
  assert.equal(fs.readFileSync(path.join(dest, "skill", "scripts", "run.py"), "utf8"), "print('hi')\n");
});

test("normalizes backslash separators to folders", () => {
  const dest = path.join(tempDir(), "out");
  assert.deepEqual(extractZip(makeZip([{ name: "a\\b.txt", data: "x" }]), dest), ["a/b.txt"]);
  assert.ok(fs.existsSync(path.join(dest, "a", "b.txt")));
});

for (const [label, name] of [
  ["a parent-directory segment", "../evil.txt"],
  ["a nested parent-directory segment", "skill/../../evil.txt"],
  ["an absolute POSIX path", "/etc/evil"],
  ["a drive-letter path", "C:/evil.txt"],
  ["an NTFS stream name", "skill/SKILL.md:stream"],
  ["a reserved device name", "skill/con.txt"],
  ["a trailing dot", "skill/evil."],
]) {
  test(`refuses ${label} without writing anything`, () => {
    const root = tempDir();
    const dest = path.join(root, "out");
    const zip = makeZip([{ name: "ok.txt", data: "fine" }, { name, data: "bad" }]);
    assert.throws(() => extractZip(zip, dest), /Refusing archive entry/);
    assert.deepEqual(listFiles(root), []);
  });
}

test("refuses symbolic links", () => {
  const dest = path.join(tempDir(), "out");
  const zip = makeZip([{ name: "link", data: "/etc/passwd", method: 0, ...SYMLINK }]);
  assert.throws(() => extractZip(zip, dest), /symbolic link/);
  assert.equal(fs.existsSync(dest), false);
});

test("refuses encrypted entries", () => {
  const dest = path.join(tempDir(), "out");
  assert.throws(() => extractZip(makeZip([{ name: "a.txt", data: "x", flags: 1 }]), dest), /encrypted/);
});

test("refuses duplicate paths compared case-insensitively", () => {
  const dest = path.join(tempDir(), "out");
  const zip = makeZip([{ name: "SKILL.md", data: "a" }, { name: "skill.md", data: "b" }]);
  assert.throws(() => extractZip(zip, dest), /duplicate entry/);
  assert.equal(fs.existsSync(dest), false);
});

test("refuses a file that is also used as a folder", () => {
  const dest = path.join(tempDir(), "out");
  const zip = makeZip([{ name: "a", data: "file" }, { name: "a/b.txt", data: "nested" }]);
  assert.throws(() => extractZip(zip, dest), /nested under a file/);
});

test("refuses entries whose CRC-32 does not match", () => {
  const dest = path.join(tempDir(), "out");
  assert.throws(() => extractZip(makeZip([{ name: "a.txt", data: "x", crc: 1 }]), dest), /CRC-32/);
  assert.equal(fs.existsSync(dest), false);
});

test("enforces entry-count and size limits before writing", () => {
  const dest = path.join(tempDir(), "out");
  const zip = makeZip([{ name: "a.txt", data: "aaaa" }, { name: "b.txt", data: "bbbb" }]);
  assert.throws(() => extractZip(zip, dest, { maxEntries: 1 }), /entries; the limit is 1/);
  assert.throws(() => extractZip(zip, dest, { maxEntryBytes: 3 }), /per-file limit/);
  assert.throws(() => extractZip(zip, dest, { maxTotalBytes: 6 }), /6-byte limit/);
  assert.equal(fs.existsSync(dest), false);
});

test("deduplicates implicit parent directories and allows the exact directory limit", () => {
  const dest = path.join(tempDir(), "out");
  const zip = makeZip([
    { name: "skill/", data: "" },
    { name: "skill/references/a.md", data: "a" },
    { name: "skill/references/b.md", data: "b" },
    { name: "skill/scripts/run.txt", data: "c" },
  ]);
  const rels = extractZip(zip, dest, {
    maxDirectories: 4,
    beforeExtract: (summary) => {
      assert.deepEqual(summary, { totalBytes: 3, fileCount: 3, directoryCount: 4 });
    },
  });
  assert.equal(rels.length, 3);
  const directories = fs.readdirSync(dest, { recursive: true, withFileTypes: true }).filter((d) => d.isDirectory());
  assert.equal(directories.length + 1, 4);
});

test("counts case-variant parent paths separately to bound case-sensitive filesystems", () => {
  const dest = path.join(tempDir(), "out");
  extractZip(makeZip([
    { name: "skill/references/a.txt", data: "a" },
    { name: "Skill/references/b.txt", data: "b" },
  ]), dest, {
    beforeExtract: (summary) => {
      assert.equal(summary.directoryCount, 5);
    },
  });
});

test("refuses unbudgeted implicit directories before writing anything", () => {
  for (const maxDirectories of [0, 2]) {
    const dest = path.join(tempDir(), "out");
    assert.throws(() => extractZip(makeZip([{ name: "a/b/file.txt", data: "x" }]), dest, {
      maxDirectories,
    }), /directory limit/);
    assert.equal(fs.existsSync(dest), false);
  }
});

test("enforces the default directory limit even for a single tiny file", () => {
  assert.equal(DEFAULT_LIMITS.maxDirectories, 2000);
  const dest = path.join(tempDir(), "out");
  const zip = makeZip([{ name: "d/".repeat(2000) + "file.txt", data: "x" }]);
  assert.throws(() => extractZip(zip, dest), /2000-directory limit/);
  assert.equal(fs.existsSync(dest), false);
});

test("checks a shared budget after validation but before decompression or writes", () => {
  const dest = path.join(tempDir(), "out");
  const zip = makeZip([{ name: "a.txt", data: "aaaa" }, { name: "b.txt", data: "bbbb" }]);
  const exhausted = new Error("shared budget exhausted");
  assert.throws(() => extractZip(zip, dest, {
    beforeExtract: (summary) => {
      assert.deepEqual(summary, { totalBytes: 8, fileCount: 2, directoryCount: 1 });
      throw exhausted;
    },
  }), (e) => e === exhausted);
  assert.equal(fs.existsSync(dest), false);
});

test("rejects data that is not a ZIP archive", () => {
  const dest = path.join(tempDir(), "out");
  assert.throws(() => extractZip(Buffer.from("not a zip"), dest), /corrupt or unsupported/);
  assert.throws(() => extractZip(Buffer.alloc(64), dest), /end-of-central-directory/);
});

test("splits safe names into segments", () => {
  assert.deepEqual(safeSegments("./skill//references/a.md"), ["skill", "references", "a.md"]);
});
