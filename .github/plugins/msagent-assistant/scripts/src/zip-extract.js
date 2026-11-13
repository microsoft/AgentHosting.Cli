/**
 * zip-extract.js — dependency-free, defensive ZIP extraction.
 *
 * Supports the archives skill catalogs actually publish (stored or deflated
 * entries in a single-disk, non-ZIP64 archive) and refuses anything that could
 * write outside the destination or exhaust the disk:
 *   - absolute paths, drive letters, `..` segments, NUL/control characters and
 *     Windows-invalid characters (`:` would otherwise create an NTFS stream);
 *   - Windows reserved device names (CON, NUL, COM1, ...);
 *   - symbolic links, encrypted entries, ZIP64 and multi-disk archives;
 *   - duplicate paths (compared case-insensitively, as on Windows/macOS);
 *   - more entries, directories or uncompressed bytes than the configured limits.
 * Every entry's CRC-32 and size are verified after decompression.
 */

const fs = require("fs");
const path = require("path");
const zlib = require("zlib");

const SIG_EOCD = 0x06054b50;
const SIG_CENTRAL = 0x02014b50;
const SIG_LOCAL = 0x04034b50;

const DEFAULT_LIMITS = Object.freeze({
  maxEntries: 2000,
  maxDirectories: 2000,
  maxEntryBytes: 25 * 1024 * 1024,
  maxTotalBytes: 50 * 1024 * 1024,
});

const RESERVED_DEVICE_NAMES = new Set([
  "con", "prn", "aux", "nul",
  "com1", "com2", "com3", "com4", "com5", "com6", "com7", "com8", "com9",
  "lpt1", "lpt2", "lpt3", "lpt4", "lpt5", "lpt6", "lpt7", "lpt8", "lpt9",
]);

function isReservedDeviceName(name) {
  return RESERVED_DEVICE_NAMES.has(name.split(".")[0].toLowerCase());
}

const CRC_TABLE = (() => {
  const table = new Uint32Array(256);
  for (let n = 0; n < 256; n++) {
    let c = n;
    for (let k = 0; k < 8; k++) c = c & 1 ? 0xedb88320 ^ (c >>> 1) : c >>> 1;
    table[n] = c >>> 0;
  }
  return table;
})();

function crc32(buf) {
  let c = 0xffffffff;
  for (let i = 0; i < buf.length; i++) c = CRC_TABLE[(c ^ buf[i]) & 0xff] ^ (c >>> 8);
  return (c ^ 0xffffffff) >>> 0;
}

function corrupt(detail) {
  return new Error(`The ZIP archive is corrupt or unsupported: ${detail}.`);
}

function findEocd(buf) {
  if (buf.length < 22) throw corrupt("it is too small to be a ZIP archive");
  const min = Math.max(0, buf.length - 22 - 0xffff);
  for (let i = buf.length - 22; i >= min; i--) {
    if (buf.readUInt32LE(i) === SIG_EOCD) return i;
  }
  throw corrupt("the end-of-central-directory record was not found");
}

// Split an archive entry name into safe path segments, or throw.
function safeSegments(name) {
  const normalized = String(name).replace(/\\/g, "/");
  if (/^\//.test(normalized) || /^[A-Za-z]:/.test(normalized)) {
    throw new Error(`Refusing archive entry with an absolute path: ${JSON.stringify(name)}`);
  }
  const segments = normalized.split("/").filter((s) => s !== "" && s !== ".");
  for (const segment of segments) {
    if (segment === "..") {
      throw new Error(`Refusing archive entry that escapes the destination: ${JSON.stringify(name)}`);
    }
    if (/[\u0000-\u001f<>:"|?*]/.test(segment) || /[. ]$/.test(segment)) {
      throw new Error(`Refusing archive entry with an invalid file name: ${JSON.stringify(name)}`);
    }
    if (isReservedDeviceName(segment)) {
      throw new Error(`Refusing archive entry with a reserved device name: ${JSON.stringify(name)}`);
    }
  }
  return segments;
}

function readCentralDirectory(buf, limits) {
  const eocd = findEocd(buf);
  const diskNumber = buf.readUInt16LE(eocd + 4);
  const cdDisk = buf.readUInt16LE(eocd + 6);
  const diskEntries = buf.readUInt16LE(eocd + 8);
  const totalEntries = buf.readUInt16LE(eocd + 10);
  const cdSize = buf.readUInt32LE(eocd + 12);
  const cdOffset = buf.readUInt32LE(eocd + 16);

  if (totalEntries === 0xffff || cdSize === 0xffffffff || cdOffset === 0xffffffff) {
    throw corrupt("ZIP64 archives are not supported");
  }
  if (diskNumber !== 0 || cdDisk !== 0 || diskEntries !== totalEntries) {
    throw corrupt("multi-disk archives are not supported");
  }
  if (totalEntries > limits.maxEntries) {
    throw new Error(`The archive has ${totalEntries} entries; the limit is ${limits.maxEntries}.`);
  }
  if (cdOffset + cdSize > eocd) throw corrupt("the central directory is out of bounds");

  const entries = [];
  let p = cdOffset;
  for (let i = 0; i < totalEntries; i++) {
    if (p + 46 > eocd || buf.readUInt32LE(p) !== SIG_CENTRAL) {
      throw corrupt("a central directory record is malformed");
    }
    const madeBy = buf.readUInt16LE(p + 4);
    const flags = buf.readUInt16LE(p + 8);
    const method = buf.readUInt16LE(p + 10);
    const crc = buf.readUInt32LE(p + 16);
    const compressedSize = buf.readUInt32LE(p + 20);
    const size = buf.readUInt32LE(p + 24);
    const nameLength = buf.readUInt16LE(p + 28);
    const extraLength = buf.readUInt16LE(p + 30);
    const commentLength = buf.readUInt16LE(p + 32);
    const externalAttributes = buf.readUInt32LE(p + 38);
    const localOffset = buf.readUInt32LE(p + 42);
    const nameEnd = p + 46 + nameLength;
    if (nameEnd > eocd) throw corrupt("an entry name is out of bounds");
    const name = buf.subarray(p + 46, nameEnd).toString("utf8");
    p = nameEnd + extraLength + commentLength;
    if (p > eocd) throw corrupt("a central directory record is out of bounds");

    if (compressedSize === 0xffffffff || size === 0xffffffff || localOffset === 0xffffffff) {
      throw corrupt("ZIP64 entries are not supported");
    }
    const isDirectory = name.endsWith("/") || name.endsWith("\\");
    const unixMode = (externalAttributes >>> 16) & 0o170000;
    if (madeBy >> 8 === 3 && unixMode === 0o120000) {
      throw new Error(`Refusing symbolic link in archive: ${JSON.stringify(name)}`);
    }
    if (flags & 0x1) {
      throw new Error(`Refusing encrypted archive entry: ${JSON.stringify(name)}`);
    }
    entries.push({
      name, isDirectory, method, crc, compressedSize, size, localOffset,
      segments: safeSegments(name),
    });
  }
  return entries;
}

function entryData(buf, entry, limits) {
  const p = entry.localOffset;
  if (p + 30 > buf.length || buf.readUInt32LE(p) !== SIG_LOCAL) {
    throw corrupt(`the local header for ${JSON.stringify(entry.name)} is malformed`);
  }
  const start = p + 30 + buf.readUInt16LE(p + 26) + buf.readUInt16LE(p + 28);
  const end = start + entry.compressedSize;
  if (end > buf.length) throw corrupt(`the data for ${JSON.stringify(entry.name)} is out of bounds`);
  const raw = buf.subarray(start, end);

  let data;
  if (entry.method === 0) {
    data = raw;
  } else if (entry.method === 8) {
    try {
      data = zlib.inflateRawSync(raw, { maxOutputLength: Math.max(1, entry.size) });
    } catch (e) {
      throw corrupt(`${JSON.stringify(entry.name)} could not be decompressed (${e && e.message ? e.message : e})`);
    }
  } else {
    throw corrupt(`${JSON.stringify(entry.name)} uses unsupported compression method ${entry.method}`);
  }
  if (data.length !== entry.size) {
    throw corrupt(`${JSON.stringify(entry.name)} does not match its declared size`);
  }
  if (crc32(data) !== entry.crc) {
    throw corrupt(`${JSON.stringify(entry.name)} failed its CRC-32 check`);
  }
  if (data.length > limits.maxEntryBytes) {
    throw new Error(`${JSON.stringify(entry.name)} exceeds the per-file limit of ${limits.maxEntryBytes} bytes.`);
  }
  return data;
}

/**
 * Extract `buffer` (a ZIP archive) into `destDir`, which must not already
 * contain files. Validates the whole archive before writing anything.
 * Returns the extracted file paths, POSIX-style and relative to `destDir`.
 */
function extractZip(buffer, destDir, options = {}) {
  const limits = { ...DEFAULT_LIMITS, ...options };
  const buf = Buffer.isBuffer(buffer) ? buffer : Buffer.from(buffer);
  const entries = readCentralDirectory(buf, limits);

  const destRoot = path.resolve(destDir);
  const seen = new Map();
  let totalBytes = 0;
  const files = [];
  for (const entry of entries) {
    if (entry.isDirectory || entry.segments.length === 0) continue;
    const rel = entry.segments.join("/");
    const key = rel.toLowerCase();
    if (seen.has(key)) {
      throw new Error(`Refusing archive with duplicate entry ${JSON.stringify(rel)}.`);
    }
    seen.set(key, rel);
    totalBytes += entry.size;
    if (entry.size > limits.maxEntryBytes) {
      throw new Error(`${JSON.stringify(rel)} exceeds the per-file limit of ${limits.maxEntryBytes} bytes.`);
    }
    if (totalBytes > limits.maxTotalBytes) {
      throw new Error(`The archive expands beyond the ${limits.maxTotalBytes}-byte limit.`);
    }
    const target = path.resolve(destRoot, ...entry.segments);
    if (!target.startsWith(destRoot + path.sep)) {
      throw new Error(`Refusing archive entry that escapes the destination: ${JSON.stringify(entry.name)}`);
    }
    files.push({ entry, rel, target });
  }
  const directories = new Set();
  const addDirectory = (dir) => {
    directories.add(dir);
    if (directories.size > limits.maxDirectories) {
      throw new Error(`The archive creates more than the ${limits.maxDirectories}-directory limit.`);
    }
  };
  addDirectory("");
  // Count case variants separately so case-sensitive filesystems cannot exceed the budget.
  for (const { rel, entry } of files) {
    let parent = "";
    for (const segment of entry.segments.slice(0, -1)) {
      parent = parent ? `${parent}/${segment}` : segment;
      if (seen.has(parent.toLowerCase())) {
        throw new Error(`Refusing archive where ${JSON.stringify(rel)} is nested under a file.`);
      }
      addDirectory(parent);
    }
  }

  if (options.beforeExtract) {
    options.beforeExtract({ totalBytes, fileCount: files.length, directoryCount: directories.size });
  }
  const decoded = files.map((f) => ({ ...f, data: entryData(buf, f.entry, limits) }));

  fs.mkdirSync(destRoot, { recursive: true });
  for (const { target, data } of decoded) {
    fs.mkdirSync(path.dirname(target), { recursive: true });
    fs.writeFileSync(target, data, { flag: "wx" });
  }
  return decoded.map((f) => f.rel);
}

module.exports = { DEFAULT_LIMITS, crc32, extractZip, isReservedDeviceName, safeSegments };
