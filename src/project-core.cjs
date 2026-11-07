"use strict";

const Rytm = (() => {
  const TYPES = { kit: 0x52, sound: 0x53, pattern: 0x54, song: 0x55, settings: 0x56, global: 0x57 };
  const ORDER = [TYPES.settings, TYPES.global, TYPES.kit, TYPES.sound, TYPES.pattern, TYPES.song];
  const COUNTS = { 82: 128, 83: 128, 84: 128, 85: 16, 86: 1, 87: 4 };
  const LENGTHS = { 82: 0x0a32, 83: 162, 84: 0x332d, 86: 2087, 87: 80 };
  const HEADER = [0xf0, 0, 0x20, 0x3c, 7];
  const KIT_LINK = 0x3325;
  const MAX_FILE = 4 * 1024 * 1024;

  function assert(condition, message) {
    if (!condition) throw new Error(message);
  }

  function label(slot) {
    return String.fromCharCode(65 + Math.floor(slot / 16)) + String(1 + slot % 16).padStart(2, "0");
  }

  function checksum(bytes) {
    return bytes.reduce((sum, byte) => sum + byte, 0) & 0x3fff;
  }

  function decode(bytes) {
    const tail = bytes.length - 5;
    const payload = bytes.subarray(10, tail);
    const type = bytes[6];
    const slot = bytes[9];
    assert(bytes.length > 16 && HEADER.every((byte, i) => byte === bytes[i]),
      "Expected an Analog Rytm project SysEx message.");
    assert(ORDER.includes(type) && slot < COUNTS[type], "Unexpected object type or slot in project dump.");
    assert(bytes[5] < 16 && bytes[7] === 1 && bytes[8] === 1, "Unsupported SysEx header.");
    assert(bytes.subarray(1, -1).every(byte => byte < 128), "Invalid MIDI byte inside SysEx message.");
    assert(payload.length % 8 !== 1, "Invalid SysEx payload packing.");
    assert((bytes[tail] * 128 + bytes[tail + 1]) === checksum(payload), "SysEx checksum mismatch. Capture the dump again.");
    assert((bytes[tail + 2] * 128 + bytes[tail + 3]) === payload.length + 5, "SysEx message length mismatch.");
    const raw = new Uint8Array(payload.length - Math.ceil(payload.length / 8));
    for (let source = 0, target = 0; source < payload.length; source += 8) {
      for (let i = 1; i < 8 && source + i < payload.length; i++) {
        raw[target++] = payload[source + i] | ((payload[source] << i) & 128);
      }
    }
    assert(!LENGTHS[type] || LENGTHS[type] === raw.length,
      "Unsupported object layout. The editor requires Rytm v5-sized project dumps (OS 1.70 layout).");
    return { type, slot, raw, bytes };
  }

  function encode(object, raw) {
    const packedSize = raw.length + Math.ceil(raw.length / 7);
    const bytes = new Uint8Array(packedSize + 15);
    bytes.set(object.bytes.subarray(0, 10));
    for (let source = 0, target = 10; source < raw.length; target += 8) {
      for (let i = 1; i < 8 && source < raw.length; i++, source++) {
        bytes[target] |= (raw[source] & 128) >>> i;
        bytes[target + i] = raw[source] & 127;
      }
    }
    const tail = bytes.length - 5;
    const sum = checksum(bytes.subarray(10, tail));
    bytes.set([sum >> 7, sum & 127, (packedSize + 5) >> 7, (packedSize + 5) & 127, 0xf7], tail);
    return bytes;
  }

  function trackOffset(track) {
    return 4 + track * 0x281;
  }

  function soundLocks(raw, visit) {
    for (let track = 0; track < 12; track++) {
      const start = trackOffset(track) + 0x237;
      for (let i = start; i < start + 64; i++) {
        if (raw[i] !== 255) visit(i);
      }
    }
  }

  function inspectPattern(raw) {
    let trigs = 0;
    let euclid = false;
    let locks = false;
    for (let track = 0; track < 13; track++) {
      const offset = trackOffset(track);
      for (let step = 0; step < 64; step++) {
        const bit = step * 14 + 13;
        trigs += (raw[offset + (bit >> 3)] >> (7 - (bit & 7))) & 1;
      }
      euclid ||= raw[offset + 0x279] === 128;
    }
    for (let i = 0; i < 72; i++) {
      const offset = 0x2091 + i * 66;
      locks ||= raw[offset] !== 255 && raw.subarray(offset + 2, offset + 66).some(value => value !== 255);
    }
    soundLocks(raw, () => { locks = true; });
    return { trigs, active: !!(trigs || euclid || locks) };
  }

  function parse(bytes, name) {
    assert(bytes.length > 0 && bytes.length <= MAX_FILE, "Invalid project size. Choose a whole-project .syx dump.");
    assert(!(bytes[0] === 0x50 && bytes[1] === 0x4b),
      "This is a Transfer archive. Export a whole-project SysEx dump from the Rytm instead.");
    const objects = Object.fromEntries(ORDER.map(type => [type, []]));
    for (let start = 0; start < bytes.length;) {
      const end = bytes.indexOf(0xf7, start);
      assert(end !== -1, "Truncated SysEx dump.");
      const message = decode(bytes.subarray(start, end + 1));
      assert(!objects[message.type][message.slot], "Duplicate project object. Capture one project per file.");
      objects[message.type][message.slot] = message;
      start = end + 1;
    }
    for (const type of ORDER) {
      assert(objects[type].filter(Boolean).length === COUNTS[type],
        "Incomplete dump. Use GLOBAL SETTINGS > SYSEX DUMP > SYSEX SEND > ALL > WHOLE PROJECT.");
    }
    return { name, bytes, objects, patterns: describe(objects), format: "syx" };
  }

  function describe(objects) {
    return objects[TYPES.pattern].map(({ raw }) => {
      const kit = raw[KIT_LINK];
      const name = kit < 128 ? new TextDecoder().decode(objects[TYPES.kit][kit].raw.subarray(4, 19))
        .split("\0")[0].replace(/[^\x20-\x7e]/g, "").trim() : "";
      return { ...inspectPattern(raw), kit, name, bpm: (raw[0x332a] * 256 + raw[0x332b]) / 120 };
    });
  }

  function sampleRow(raw, slot) {
    const start = 0x1f + 16 * slot;
    return raw.subarray(start, start + 16);
  }

  function unused(row) {
    return row.subarray(0, 4).every(byte => byte === 255);
  }

  function sampleTable(project) {
    return project.objects[TYPES.settings][0].raw;
  }

  function assertTemplate(template) {
    assert(!template.patterns.some(pattern => pattern.active), "The template contains pattern data. Dump a newly created, blank project.");
  }

  function arrange(template, projects, grid) {
    assertTemplate(template);
    assert(grid.length === 128 && grid.some(Boolean), "Place at least one pattern before exporting.");
    const objects = Object.fromEntries(ORDER.map(type => [type, []]));
    const settings = sampleTable(template).slice();
    const sampleSlots = Array.from({ length: 128 }, (_, slot) => sampleRow(settings, slot));
    const kits = new Map();
    const sounds = new Map();
    const sampleKey = row => row.join(",");
    const samples = new Map();
    const bindings = new Map();
    let nextSample = 1;
    for (let slot = 1; slot < 128; slot++) {
      if (!unused(sampleSlots[slot])) samples.set(sampleKey(sampleSlots[slot]), slot);
    }

    function bindSample(project, slot) {
      if (slot === 0 || slot === 255) return slot;
      assert(slot < 128, "Invalid sample slot reference.");
      const fingerprint = sampleRow(sampleTable(project), slot);
      assert(!unused(fingerprint), project.name + " references an empty sample slot (" + slot + ").");
      const key = sampleKey(fingerprint);
      let destination = samples.get(key);
      if (destination === undefined) {
        while (nextSample < 128 && !unused(sampleSlots[nextSample])) nextSample++;
        destination = unused(sampleSlots[slot]) ? slot : nextSample;
        assert(destination < 128, "The merge needs more than 127 project sample slots.");
        sampleSlots[destination].set(fingerprint);
        samples.set(key, destination);
      }
      if (!bindings.has(destination)) bindings.set(destination, new Set());
      bindings.get(destination).add(project);
      return destination;
    }

    function copyDependency(type, cache, projectId, sourceSlot, remap) {
      const key = projectId + "/" + sourceSlot;
      if (cache.has(key)) return cache.get(key);
      const source = projects[projectId].objects[type][sourceSlot];
      assert(source, "A referenced kit or sound is missing from the source.");
      const destination = cache.size;
      assert(destination < 128, "The merge needs more than 128 kits or sound-pool slots.");
      const raw = source.raw.slice();
      remap(raw);
      objects[type][destination] = raw;
      cache.set(key, destination);
      return destination;
    }

    grid.forEach((placement, destination) => {
      if (!placement) return;
      const { project: projectId, slot } = placement;
      const project = projects[projectId];
      assert(project && Number.isInteger(slot) && slot >= 0 && slot < 128, "Invalid source pattern in workspace.");
      assert(project.format === template.format, "The blank template and placed sources must use the same file format.");
      const raw = project.objects[TYPES.pattern][slot].raw.slice();
      try {
        assert(raw[KIT_LINK] < 128, "The pattern has no saved kit. Save its kit on the Rytm and capture it again.");
        raw[KIT_LINK] = copyDependency(TYPES.kit, kits, projectId, raw[KIT_LINK], kit => {
          for (let track = 0; track < 12; track++) {
            const offset = 0x2e + track * 162 + 0x30;
            kit[offset] = bindSample(project, kit[offset]);
          }
        });
        soundLocks(raw, offset => {
          raw[offset] = copyDependency(TYPES.sound, sounds, projectId, raw[offset], sound => {
            sound[0x30] = bindSample(project, sound[0x30]);
          });
        });
        for (let row = 0; row < 72; row++) {
          const offset = 0x2091 + row * 66;
          if (raw[offset] !== 0x0a) continue;
          for (let i = offset + 2; i < offset + 66; i++) raw[i] = bindSample(project, raw[i]);
        }
      } catch (error) {
        throw new Error(label(destination) + " from " + project.name + " " + label(slot) + ". " + error.message);
      }
      objects[TYPES.pattern][destination] = raw;
    });
    objects[TYPES.settings][0] = settings;
    return { objects, bindings, kits: kits.size, sounds: sounds.size,
      samples: sampleSlots.slice(1).filter(row => !unused(row)).length };
  }

  function compose(template, projects, grid) {
    const { objects, kits, sounds, samples } = arrange(template, projects, grid);
    const messages = ORDER.flatMap(type => template.objects[type].map((object, slot) => {
      const raw = objects[type][slot];
      return raw ? encode(object, raw) : object.bytes;
    }));
    const bytes = new Uint8Array(messages.reduce((total, message) => total + message.length, 0));
    let cursor = 0;
    for (const message of messages) {
      bytes.set(message, cursor);
      cursor += message.length;
    }
    parse(bytes, "Export validation");
    return { bytes, kits, sounds, samples };
  }

  function kitSyx(slot, raw) {
    return encode({ bytes: Uint8Array.of(...HEADER, 0, TYPES.kit, 1, 1, slot) }, raw);
  }

  return { parse, label, compose, assertTemplate, MAX_FILE, TYPES, describe, arrange, sampleRow, sampleTable, unused, kitSyx };
})();

"use strict";

const Archive = (() => {
  const MAX_FILE = 256 * 1024 * 1024;
  const decoder = new TextDecoder("utf-8", { fatal: true });
  const encoder = new TextEncoder();
  const table = Uint32Array.from({ length: 256 }, (_, index) => {
    let value = index;
    for (let i = 0; i < 8; i++) value = value & 1 ? (value >>> 1) ^ 0xedb88320 : value >>> 1;
    return value;
  });

  function assert(condition, message) {
    if (!condition) throw new Error(message);
  }

  function view(bytes) {
    return new DataView(bytes.buffer, bytes.byteOffset, bytes.byteLength);
  }

  function crc(bytes, initial = 0xffffffff) {
    let value = initial;
    for (const byte of bytes) value = (value >>> 8) ^ table[(value ^ byte) & 255];
    return (value ^ 0xffffffff) >>> 0;
  }

  function path(name) {
    return typeof name === "string" && name.length > 0 && !/[\\:\u0000-\u001f]/.test(name)
      && name.split("/").every(part => part && part !== "." && part !== "..");
  }

  function read(bytes) {
    assert(bytes.length >= 22 && bytes.length <= MAX_FILE, "Invalid archive size.");
    const dv = view(bytes);
    const u16 = offset => dv.getUint16(offset, true);
    const u32 = offset => dv.getUint32(offset, true);
    let footer = bytes.length - 22;
    const firstFooter = Math.max(0, footer - 65535);
    while (footer >= firstFooter && (u32(footer) !== 0x06054b50 || footer + 22 + u16(footer + 20) !== bytes.length)) footer--;
    assert(footer >= firstFooter, "ZIP directory is missing or damaged.");
    const count = u16(footer + 10);
    const directorySize = u32(footer + 12);
    const directoryStart = u32(footer + 16);
    assert(u32(footer + 4) === 0 && count === u16(footer + 8) && count <= 4096
      && directoryStart + directorySize === footer, "Unsupported ZIP directory.");
    const entries = new Map();
    let offset = directoryStart;
    for (let i = 0; i < count; i++) {
      assert(offset + 46 <= footer && u32(offset) === 0x02014b50, "Invalid ZIP directory entry.");
      const flags = u16(offset + 8), method = u16(offset + 10), checksum = u32(offset + 16);
      const compressed = u32(offset + 20), size = u32(offset + 24);
      const nameSize = u16(offset + 28), local = u32(offset + 42);
      const next = offset + 46 + nameSize + u16(offset + 30) + u16(offset + 32);
      assert(next <= footer && local + 30 <= directoryStart && u32(local) === 0x04034b50
        && (flags & 1) === 0 && (method === 0 || method === 8), "Unsupported ZIP entry.");
      const name = decoder.decode(bytes.subarray(offset + 46, offset + 46 + nameSize));
      const start = local + 30 + u16(local + 26) + u16(local + 28);
      assert(start + compressed <= directoryStart && size <= MAX_FILE, "Invalid ZIP entry size.");
      if (!name.endsWith("/")) {
        assert(path(name) && !entries.has(name), "Invalid or duplicate ZIP path.");
        entries.set(name, { name, method, checksum, size, data: bytes.subarray(start, start + compressed) });
      }
      offset = next;
    }
    assert(offset === footer, "ZIP directory length mismatch.");
    return entries;
  }

  async function inflate(entry, maxSize) {
    assert(entry && entry.size <= maxSize, "Missing ZIP entry or invalid uncompressed size.");
    if (entry.method === 0) {
      assert(entry.data.length === entry.size && crc(entry.data) === entry.checksum, "ZIP entry checksum mismatch.");
      return entry.data;
    }
    const reader = new Blob([entry.data]).stream().pipeThrough(new DecompressionStream("deflate-raw")).getReader();
    const bytes = new Uint8Array(entry.size);
    let offset = 0;
    try {
      for (;;) {
        const { value, done } = await reader.read();
        if (done) break;
        assert(offset + value.length <= bytes.length, "ZIP entry expanded beyond its declared size.");
        bytes.set(value, offset);
        offset += value.length;
      }
    } catch (error) {
      await reader.cancel().catch(() => {});
      throw error;
    }
    assert(offset === bytes.length && crc(bytes) === entry.checksum, "ZIP entry checksum mismatch.");
    return bytes;
  }

  async function file(name, bytes, compress = false) {
    const data = compress
      ? new Uint8Array(await new Response(new Blob([bytes]).stream().pipeThrough(new CompressionStream("deflate-raw"))).arrayBuffer())
      : bytes;
    return { name, method: compress ? 8 : 0, checksum: crc(bytes), size: bytes.length, data };
  }

  function build(entries) {
    const names = new Set();
    const local = [], central = [];
    let offset = 0, centralSize = 0;
    assert(entries.length <= 4096, "Too many archive entries.");
    for (const entry of entries) {
      assert(path(entry.name) && !names.has(entry.name.toLowerCase()), "Conflicting archive filename.");
      names.add(entry.name.toLowerCase());
      const name = encoder.encode(entry.name);
      assert(name.length <= 65535, "Archive filename is too long.");
      const head = new Uint8Array(30 + name.length), dir = new Uint8Array(46 + name.length);
      const h = view(head), d = view(dir);
      h.setUint32(0, 0x04034b50, true);
      h.setUint16(4, 20, true);
      h.setUint16(6, 0x0800, true);
      h.setUint16(8, entry.method, true);
      h.setUint16(12, 0x21, true);
      h.setUint32(14, entry.checksum, true);
      h.setUint32(18, entry.data.length, true);
      h.setUint32(22, entry.size, true);
      h.setUint16(26, name.length, true);
      head.set(name, 30);
      d.setUint32(0, 0x02014b50, true);
      d.setUint16(4, 20, true);
      dir.set(head.subarray(4, 28), 6);
      d.setUint32(42, offset, true);
      dir.set(name, 46);
      local.push(head, entry.data);
      central.push(dir);
      offset += head.length + entry.data.length;
      centralSize += dir.length;
      assert(offset + centralSize + 22 <= MAX_FILE, "The resulting archive exceeds 256 MB.");
    }
    const footer = new Uint8Array(22), f = view(footer);
    f.setUint32(0, 0x06054b50, true);
    f.setUint16(8, entries.length, true);
    f.setUint16(10, entries.length, true);
    f.setUint32(12, centralSize, true);
    f.setUint32(16, offset, true);
    return new Blob([...local, ...central, footer], { type: "application/zip" });
  }

  function safeName(name) {
    const clean = name.replace(/[<>:"/\\|?*\u0000-\u001f]/g, "_").trim().replace(/[. ]+$/, "").slice(0, 90);
    return /^(?:con|prn|aux|nul|com[1-9]|lpt[1-9])(?:\.|$)/i.test(clean) ? "_" + clean : clean || "Untitled";
  }

  return { MAX_FILE, assert, view, crc, read, inflate, file, build, encoder, decoder, safeName, path };
})();

const ProjectPayload = (() => {
  const { assert, view, crc } = Archive;
  const BLOCK_SIZE = 32768, STREAM_START = 31;
  function decodeBlock(source, image, at) {
    const start = at;
    let i = 0;
    function length(value) {
      if (value !== 15) return value;
      let next;
      do {
        assert(i < source.length, "Truncated LZ4 block.");
        next = source[i++];
        value += next;
      } while (next === 255);
      return value;
    }
    while (i < source.length) {
      const token = source[i++];
      const literals = length(token >> 4);
      assert(i + literals <= source.length && at + literals <= image.length
        && at + literals - start <= BLOCK_SIZE, "Invalid LZ4 literal count.");
      image.set(source.subarray(i, i + literals), at);
      i += literals;
      at += literals;
      if (i === source.length) return at;
      assert(i + 2 <= source.length, "Truncated LZ4 match.");
      const distance = source[i] + 256 * source[i + 1];
      i += 2;
      const match = length(token & 15) + 4;
      assert(distance > 0 && distance <= at && at + match <= image.length
        && at + match - start <= BLOCK_SIZE, "Invalid LZ4 match.");
      for (let stop = at + match; at < stop; at++) image[at] = image[at - distance];
    }
    throw new Error("Missing LZ4 terminal literals.");
  }

  function decodePayload(bytes, imageSize, versions) {
    const dv = view(bytes);
    const end = bytes.length - 12;
    assert(end > STREAM_START + 4 && dv.getUint32(0) === 0xac11d303 && dv.getUint32(end + 8) === 0xaaa1daaa,
      "Unsupported Elektron project payload container.");
    const version = dv.getUint32(17);
    if (version === 0xffffffff) {
      const error = new Error("This is an unused project slot.");
      error.code = "EMPTY_PROJECT";
      throw error;
    }
    assert(versions.includes(version), "Unsupported saved project version. Re-export a copy from supported device firmware.");
    const stream = bytes.subarray(STREAM_START, end);
    assert(dv.getUint32(end + 4) === stream.length && dv.getUint32(end - 4) === 0
      && dv.getUint32(end) === crc(stream, 0), "Elektron payload length or checksum mismatch.");
    const image = new Uint8Array(imageSize);
    let written = 0;
    for (let cursor = STREAM_START; cursor < end - 4;) {
      const size = dv.getUint32(cursor);
      cursor += 4;
      assert(size > 0 && size <= BLOCK_SIZE + 150 && cursor + size <= end - 4, "Invalid Elektron project block.");
      written = decodeBlock(bytes.subarray(cursor, cursor + size), image, written);
      cursor += size;
      assert(cursor === end - 4 || written % BLOCK_SIZE === 0, "Unexpected Elektron project block size.");
    }
    assert(written === image.length && view(image).getUint32(0) === 0xbeefbace && view(image).getUint32(4) === version,
      "Unsupported Elektron project image.");
    return { image, header: bytes.slice(0, STREAM_START), version };
  }

  function encodeBlock(bytes) {
    const output = [];
    const recent = new Int32Array(65536).fill(-1);
    const dv = view(bytes);
    let start = 0, cursor = 0;
    function extra(length) {
      while (length >= 255) {
        output.push(255);
        length -= 255;
      }
      output.push(length);
    }
    function literal(end) {
      const count = end - start;
      if (count >= 15) extra(count - 15);
      for (let i = start; i < end; i++) output.push(bytes[i]);
    }
    while (cursor + 12 <= bytes.length) {
      const word = dv.getUint32(cursor, true);
      const key = Math.imul(word, 0x9e3779b1) >>> 16;
      const previous = recent[key];
      recent[key] = cursor;
      if (previous < 0 || dv.getUint32(previous, true) !== word) {
        cursor++;
        continue;
      }
      let stop = cursor + 4;
      while (stop < bytes.length - 5 && bytes[stop] === bytes[previous + stop - cursor]) stop++;
      const len = stop - cursor - 4;
      output.push((Math.min(cursor - start, 15) << 4) | Math.min(len, 15));
      literal(cursor);
      const distance = cursor - previous;
      output.push(distance & 255, distance >> 8);
      if (len >= 15) extra(len - 15);
      start = cursor = stop;
    }
    output.push(Math.min(bytes.length - start, 15) << 4);
    literal(bytes.length);
    return Uint8Array.from(output);
  }

  function encodePayload(header, image) {
    const blocks = [];
    let length = 4;
    for (let start = 0; start < image.length; start += BLOCK_SIZE) {
      const block = encodeBlock(image.subarray(start, start + BLOCK_SIZE));
      blocks.push(block);
      length += block.length + 4;
    }
    const bytes = new Uint8Array(STREAM_START + length + 12);
    bytes.set(header);
    const dv = view(bytes);
    let cursor = STREAM_START;
    for (const block of blocks) {
      dv.setUint32(cursor, block.length);
      bytes.set(block, cursor + 4);
      cursor += block.length + 4;
    }
    cursor += 4;
    dv.setUint32(cursor, crc(bytes.subarray(STREAM_START, cursor), 0));
    dv.setUint32(cursor + 4, length);
    dv.setUint32(cursor + 8, 0xaaa1daaa);
    const check = decodePayload(bytes, image.length, [view(image).getUint32(4)]).image;
    assert(check.every((value, i) => value === image[i]), "Rebuilt project failed byte-for-byte verification.");
    return bytes;
  }

  return { decode: decodePayload, encode: encodePayload };
})();

"use strict";

const Native = (() => {
  const { assert, view, crc, encoder, decoder } = Archive;
  const { kit, sound, pattern, settings } = Rytm.TYPES;
  const IMAGE_SIZE = 2095787;
  const SIZES = { [pattern]: 13101, [kit]: 2610, [sound]: 162, [settings]: 2087 };
  const STARTS = { [pattern]: 0x3345, [kit]: 0x19d3fb, [sound]: 0x1f4397, [settings]: 2069655 };
  const MOD_STORE_SIZE = 4352;
  const MOD_HEADER = 10;

  const decodePayload = bytes => ProjectPayload.decode(bytes, IMAGE_SIZE, [8, 9]);
  const encodePayload = ProjectPayload.encode;

  function offset(type, slot) {
    return STARTS[type] + slot * SIZES[type];
  }

  function records(image) {
    const dv = view(image);
    const objects = {};
    const expected = { [pattern]: 5, [kit]: 6, [sound]: 0xbeefbace, [settings]: 3 };
    for (const type of [pattern, kit, sound, settings]) {
      const count = type === settings ? 1 : 128;
      objects[type] = Array.from({ length: count }, (_, slot) => {
        const start = offset(type, slot);
        assert(dv.getUint32(start) === expected[type], "Unsupported Rytm pattern, kit, sound or settings layout.");
        return { raw: image.subarray(start, start + SIZES[type]) };
      });
    }
    return objects;
  }

  function sampleKey(row) {
    const fields = view(row);
    return fields.getUint32(4) + "/" + fields.getUint32(8);
  }

  async function parse(bytes, name) {
    const entries = Archive.read(bytes);
    const manifest = JSON.parse(decoder.decode(await Archive.inflate(entries.get("manifest.json"), 256 * 1024)));
    assert(manifest.FormatVersion === "1.0" && manifest.FileType === "Project"
      && Array.isArray(manifest.ProductType) && manifest.ProductType.includes("8")
      && manifest.ProductType.some(id => id === "20" || id === "16")
      && typeof manifest.FirmwareVersion === "string" && Archive.path(manifest.Payload),
    "Expected an Analog Rytm Transfer project archive.");
    const payload = decodePayload(await Archive.inflate(entries.get(manifest.Payload), 3 * 1024 * 1024));
    const objects = records(payload.image);
    const declaredAudio = new Map();
    assert(manifest.Samples === undefined || Array.isArray(manifest.Samples), "Invalid Transfer sample manifest.");
    for (const sample of manifest.Samples || []) {
      assert(Archive.path(sample.FileName) && sample.FileName.startsWith("Samples/")
        && /^\d+$/.test(sample.Hash) && Number(sample.Hash) <= 0xffffffff
        && Number.isInteger(sample.FileSize) && sample.FileSize > 0, "Invalid Transfer sample entry.");
      const entry = entries.get(sample.FileName);
      if (entry) declaredAudio.set(Number(sample.Hash) + "/" + sample.FileSize, { sample, entry });
    }
    const audio = new Map();
    for (let slot = 1; slot < 128; slot++) {
      const row = Rytm.sampleRow(objects[settings][0].raw, slot);
      if (Rytm.unused(row)) continue;
      const key = sampleKey(row);
      const [hash, size] = key.split("/").map(Number);
      // Some Transfer backups report the sample length without the 16-byte device header.
      const item = declaredAudio.get(key) || declaredAudio.get(hash + "/" + (size - 16));
      if (item) audio.set(key, item);
    }
    return { bytes, name, format: "arprj", manifest, audio, ...payload, objects, patterns: Rytm.describe(objects) };
  }

  function modOffset(index) {
    return 0x1eecfb + 1304 * (index >> 8) + 0x116 + 4 * (index & 255);
  }

  function modBytes(project) {
    return project.version === 9
      ? Uint8Array.from({ length: MOD_STORE_SIZE }, (_, i) => project.image[modOffset(i)])
      : new Uint8Array(MOD_STORE_SIZE);
  }

  function layoutId(store) {
    return view(store).getUint32(0);
  }

  function parseLock(lock) {
    const id = Number(lock?.id);
    const tracks = lock?.split?.track, patterns = lock?.split?.pattern;
    assert(lock?.device === "analog-rytm-mk2" && Number.isInteger(id) && id > 0 && id <= 0xffffffff
      && tracks === 2 && Number.isInteger(patterns) && patterns >= 4
      && MOD_HEADER + 129 * (13 * tracks + patterns) <= MOD_STORE_SIZE, "Expected the elekloader Analog Rytm MKII storage lockfile.");
    return { id, tracks, patterns };
  }

  function copyModSettings(template, projects, grid, target, lock) {
    const destination = modBytes(template);
    const sources = new Map();
    const usedProjects = new Set(grid.filter(Boolean).map(ref => ref.project));
    for (const id of usedProjects) sources.set(id, modBytes(projects[id]));
    if (!layoutId(destination) && ![...sources.values()].some(layoutId)) return;
    assert(lock && template.version === 9 && layoutId(destination) === lock.id,
      "Use a blank .arprj from the same modified firmware and load its elekloader storage lockfile to carry pattern mod settings.");
    const trackSize = 13 * lock.tracks;
    const patternStart = MOD_HEADER + 129 * trackSize;
    grid.forEach((ref, slot) => {
      if (!ref) return;
      const source = sources.get(ref.project);
      const id = layoutId(source);
      assert(!id || (id === lock.id && source[7] === destination[7] && source[8] === destination[8]),
        "The source and template use different mod layouts or pattern generations.");
      const fromTracks = MOD_HEADER + (ref.slot + 1) * trackSize;
      const fromPattern = patternStart + (ref.slot + 1) * lock.patterns;
      const toTracks = MOD_HEADER + (slot + 1) * trackSize;
      const toPattern = patternStart + (slot + 1) * lock.patterns;
      destination.set(source.subarray(fromTracks, fromTracks + trackSize), toTracks);
      destination.set(source.subarray(fromPattern, fromPattern + lock.patterns), toPattern);
    });
    for (let i = 0; i < destination.length; i++) target[modOffset(i)] = destination[i];
  }

  async function compose(template, projects, grid, lock) {
    const arranged = Rytm.arrange(template, projects, grid);
    const image = template.image.slice();
    for (const type of [pattern, kit, sound, settings]) {
      arranged.objects[type].forEach((raw, slot) => image.set(raw, offset(type, slot)));
    }
    copyModSettings(template, projects, grid, image, lock);
    const payloadName = "RYTM MERGED";
    image.fill(0, 8, 24);
    image.set(encoder.encode(payloadName), 8);
    const manifest = { ...template.manifest, Payload: payloadName };
    const packedAudio = new Map();
    const paths = new Set();
    const missing = new Set();
    const resultSettings = arranged.objects[settings][0];
    for (let slot = 1; slot < 128; slot++) {
      const row = Rytm.sampleRow(resultSettings, slot);
      if (Rytm.unused(row)) continue;
      const key = sampleKey(row);
      if (packedAudio.has(key) || missing.has(key)) continue;
      const matchingProjects = [template, ...(arranged.bindings.get(slot) || [])];
      let audio = matchingProjects.map(project => project.audio.get(key)).find(Boolean);
      audio ||= projects.map(project => project.audio?.get(key)).find(Boolean);
      if (!audio) {
        missing.add(key);
        continue;
      }
      const base = Archive.safeName(audio.entry.name.split("/").at(-1));
      let name = audio.entry.name;
      if (paths.has(name.toLowerCase())) name = "Samples/Rytm Merge/" + key.replace("/", "-") + " " + base;
      let suffix = 2;
      while (paths.has(name.toLowerCase())) name = "Samples/Rytm Merge/" + key.replace("/", "-") + "-" + suffix++ + " " + base;
      paths.add(name.toLowerCase());
      await Archive.inflate(audio.entry, Archive.MAX_FILE);
      packedAudio.set(key, { entry: { ...audio.entry, name }, sample: { ...audio.sample, FileName: name } });
    }
    manifest.Samples = [...packedAudio.values()].map(item => item.sample);
    if (!manifest.Samples.length) delete manifest.Samples;
    const payload = encodePayload(template.header, image);
    const parts = await Promise.all([
      Archive.file("manifest.json", encoder.encode(JSON.stringify(manifest, null, 2)), true),
      Archive.file(payloadName, payload, true),
    ]);
    const bytes = Archive.build([...parts, ...[...packedAudio.values()].map(item => item.entry)]);
    return { ...arranged, bytes, bundled: packedAudio.size, missing: missing.size };
  }

  async function kitBackup(projects) {
    const entries = [];
    let count = 0;
    for (const [index, project] of projects.entries()) {
      const folder = String(index + 1).padStart(2, "0") + " " + Archive.safeName(project.name.replace(/\.(arprj|syx)$/i, ""));
      const links = new Map(), unresolved = [];
      project.patterns.forEach((item, slot) => {
        if (!item.active) return;
        if (item.kit >= 128) {
          unresolved.push(Rytm.label(slot));
          return;
        }
        if (!links.has(item.kit)) links.set(item.kit, []);
        links.get(item.kit).push(Rytm.label(slot));
      });
      if (!links.size && !unresolved.length) continue;
      const catalog = { project: project.name, firmware: project.manifest?.FirmwareVersion, kits: [], patternsWithoutSavedKit: unresolved };
      const includedAudio = new Set();
      for (const [slot, patterns] of [...links].sort(([a], [b]) => a - b)) {
        const raw = project.objects[kit][slot].raw;
        const name = project.patterns.find(item => item.kit === slot).name || "Unnamed kit";
        const stem = "Kit " + String(slot + 1).padStart(3, "0") + " - " + Archive.safeName(name);
        const kitInfo = { slot: slot + 1, name, patterns, file: stem + ".syx", samples: [] };
        entries.push(await Archive.file(folder + "/Kits/" + kitInfo.file, Rytm.kitSyx(slot, raw), true));
        for (let track = 0; track < 12; track++) {
          const sampleSlot = raw[46 + 162 * track + 48];
          if (!(sampleSlot > 0 && sampleSlot < 128)) continue;
          const row = Rytm.sampleRow(Rytm.sampleTable(project), sampleSlot);
          const key = Rytm.unused(row) || !project.audio ? null : sampleKey(row);
          const audio = key && project.audio.get(key);
          const audioPath = audio
            ? "Samples/" + key.replace("/", "-") + " " + Archive.safeName(audio.entry.name.split("/").at(-1))
            : null;
          kitInfo.samples.push({ track: track + 1, slot: sampleSlot, ...(key ? { identity: key } : {}), audio: audioPath });
          if (audio && !includedAudio.has(key)) {
            await Archive.inflate(audio.entry, Archive.MAX_FILE);
            entries.push({ ...audio.entry, name: folder + "/" + audioPath });
            includedAudio.add(key);
          }
        }
        catalog.kits.push(kitInfo);
        count++;
      }
      entries.push(await Archive.file(folder + "/Kit index.json", encoder.encode(JSON.stringify(catalog, null, 2)), true));
    }
    assert(entries.length, "None of the imported projects has active patterns to catalog.");
    return { bytes: Archive.build(entries), count };
  }

  return { parse, compose, kitBackup, parseLock, decodePayload, encodePayload };
})();

"use strict";

const Digitakt = (() => {
  const { assert, view, encoder, decoder } = Archive;
  const SIZE = 0xdd9404;
  const PATTERNS = 0x200, PATTERN_SIZE = 0x15c00, CONFIG = 0x15aa4;
  const KITS = 0xae0200, KIT_SIZE = 0x5800, PRESETS = 0xda5a04, PRESET_SIZE = 1109;
  const SAMPLES = 0xdc870b, SAMPLE_SIZE = 16, SAMPLE_COUNT = 1024;

  const patternAt = slot => PATTERNS + slot * PATTERN_SIZE;
  const kitAt = slot => KITS + slot * KIT_SIZE;
  const same = (a, b) => a.length === b.length && a.every((v, i) => v === b[i]);
  const text = bytes => new TextDecoder().decode(bytes).split("\0")[0].replace(/[^ -~]/g, "").trim();

  function describe(image) {
    const data = view(image);
    assert(data.getUint32(SIZE - 4) === 0xbacef00c, "Digitakt II project image footer is invalid.");
    for (let slot = 0; slot < 128; slot++) {
      const p = PRESETS + slot * PRESET_SIZE;
      assert(data.getUint32(p) === 0xbeefbace && data.getUint32(p + 4) === 3
        && data.getUint32(p + PRESET_SIZE - 4) === 0xbacef00c, "Unsupported Digitakt II preset pool layout.");
    }
    return Array.from({ length: 128 }, (_, slot) => {
      const p = patternAt(slot), k = kitAt(slot);
      assert(data.getUint32(p) === 4 && data.getUint32(k) === 0xbeefbace && data.getUint32(k + 4) === 4,
        "Unsupported Digitakt II pattern or kit record layout.");
      let trigs = 0, active = false;
      for (let track = 0; track < 16; track++) {
        const start = p + 4 + track * 1184;
        for (let step = 0; step < 128; step++) {
          const flags = data.getUint16(start + step * 2);
          trigs += flags & 1;
        }
        active ||= image[start + 0x492] !== 255 && image[start + 0x493] > 0;
      }
      for (let row = 0; row < 80 && !active; row++) {
        const start = p + 0x10a04 + row * 258;
        if (image[start] === 255) continue;
        for (let step = 0; step < 128 && !active; step++) active = data.getUint16(start + 2 + step * 2) !== 65535;
      }
      const name = text(image.subarray(p + CONFIG, p + CONFIG + 16));
      const kitName = text(image.subarray(k + 8, k + 24));
      return { name: name || kitName || Rytm.label(slot), kitName, kit: slot, trigs, active: active || !!name || trigs > 0,
        bpm: data.getUint16(p + CONFIG + 18) / 120 };
    });
  }

  async function parse(bytes, name) {
    const entries = Archive.read(bytes);
    const manifest = JSON.parse(decoder.decode(await Archive.inflate(entries.get("manifest.json"), 1024 * 1024)));
    assert(manifest.FormatVersion === "1.0" && manifest.FileType === "Project"
      && Archive.path(manifest.Payload) && Array.isArray(manifest.ProductType)
      && (manifest.ProductType.length === 0 || manifest.ProductType.includes("37")),
    "Expected a Digitakt II Transfer project.");
    const payload = await Archive.inflate(entries.get(manifest.Payload), 20 * 1024 * 1024);
    const decoded = ProjectPayload.decode(payload, SIZE, [4]);
    assert(view(decoded.header).getUint32(25) === SIZE && decoded.header[29] === 1,
      "Unsupported Digitakt II project envelope.");
    assert(manifest.Samples === undefined || Array.isArray(manifest.Samples), "Invalid Digitakt II sample manifest.");
    for (const item of manifest.Samples || []) {
      assert(Archive.path(item.FileName) && item.FileName.startsWith("Samples/") && /^\d+$/.test(item.Hash)
        && Number(item.Hash) <= 0xffffffff && Number.isInteger(item.FileSize) && item.FileSize > 0,
      "Invalid Digitakt II sample entry.");
    }
    return { bytes, entries, manifest, name, format: "dt2prj", ...decoded, patterns: describe(decoded.image) };
  }

  function assertTemplate(template) {
    assert(!template.patterns.some(p => p.active), "Choose a blank Digitakt II project exported from Transfer.");
  }

  function compatiblePools(a, b) {
    return same(a.image.subarray(PRESETS, PRESETS + 128 * PRESET_SIZE), b.image.subarray(PRESETS, PRESETS + 128 * PRESET_SIZE))
      && same(a.image.subarray(SAMPLES, SAMPLES + SAMPLE_COUNT * SAMPLE_SIZE), b.image.subarray(SAMPLES, SAMPLES + SAMPLE_COUNT * SAMPLE_SIZE));
  }

  async function compose(template, sources, grid) {
    assertTemplate(template);
    assert(grid.length === 128 && grid.some(Boolean), "Place at least one pattern before exporting.");
    const used = [...new Set(grid.filter(Boolean).map(ref => sources[ref.project]))];
    assert(used.every(source => source?.format === "dt2prj"), "Digitakt II exports require Digitakt II sources.");
    const first = used[0];
    for (const source of used.slice(1)) {
      assert(compatiblePools(first, source),
        first.name + " and " + source.name + " have different sample or preset pools. This Digitakt II build requires identical slot contents across the projects in an export.");
    }
    const image = template.image.slice();
    image.set(first.image.subarray(PRESETS, PRESETS + 128 * PRESET_SIZE), PRESETS);
    image.set(first.image.subarray(SAMPLES, SAMPLES + SAMPLE_COUNT * SAMPLE_SIZE), SAMPLES);
    grid.forEach((ref, destination) => {
      if (!ref) return;
      const source = sources[ref.project];
      assert(Number.isInteger(ref.slot) && ref.slot >= 0 && ref.slot < 128, "Invalid source pattern slot.");
      const p = patternAt(ref.slot), k = kitAt(ref.slot);
      image.set(source.image.subarray(p, p + PATTERN_SIZE), patternAt(destination));
      image.set(source.image.subarray(k, k + KIT_SIZE), kitAt(destination));
    });
    describe(image);
    const payloadName = "DT2 MERGED";
    image.fill(0, 8, 24);
    image.set(encoder.encode(payloadName), 8);
    const manifest = { ...template.manifest, Payload: payloadName };
    const audio = new Map(), paths = new Set();
    for (const source of used) {
      for (const item of source.manifest.Samples || []) {
        const identity = Number(item.Hash) + "/" + item.FileSize;
        const entry = source.entries.get(item.FileName);
        if (!entry || audio.has(identity)) continue;
        let name = entry.name;
        const base = Archive.safeName(name.split("/").at(-1));
        let suffix = 1;
        while (paths.has(name.toLowerCase())) name = "Samples/DT2 Merge/" + identity.replace("/", "-") + "-" + suffix++ + " " + base;
        paths.add(name.toLowerCase());
        await Archive.inflate(entry, Archive.MAX_FILE);
        audio.set(identity, { sample: { ...item, FileName: name }, entry: { ...entry, name } });
      }
    }
    manifest.Samples = [...audio.values()].map(item => item.sample);
    if (!manifest.Samples.length) delete manifest.Samples;
    const parts = await Promise.all([
      Archive.file("manifest.json", encoder.encode(JSON.stringify(manifest, null, 2)), true),
      Archive.file(payloadName, ProjectPayload.encode(template.header, image), true)
    ]);
    return { bytes: Archive.build([...parts, ...[...audio.values()].map(item => item.entry)]), bundled: audio.size };
  }

  async function kitBackup(projects) {
    const parts = [];
    let count = 0;
    for (const [i, project] of projects.entries()) {
      const patterns = project.patterns.flatMap((p, slot) => p.active ? [{ ...p, slot }] : []);
      if (!patterns.length) continue;
      const folder = String(i + 1).padStart(2, "0") + " " + Archive.safeName(project.name.replace(/\.dt2prj$/i, ""));
      const index = [];
      for (const p of patterns) {
        const name = "Kits/" + Rytm.label(p.slot) + " - " + Archive.safeName(p.kitName || p.name) + ".bin";
        const offset = kitAt(p.slot);
        parts.push(await Archive.file(folder + "/" + name, project.image.subarray(offset, offset + KIT_SIZE), true));
        index.push({ pattern: Rytm.label(p.slot), kit: name });
        count++;
      }
      const included = new Set();
      for (const sample of project.manifest.Samples || []) {
        const entry = project.entries.get(sample.FileName);
        if (!entry || included.has(entry.name.toLowerCase())) continue;
        await Archive.inflate(entry, Archive.MAX_FILE);
        parts.push({ ...entry, name: folder + "/" + entry.name });
        included.add(entry.name.toLowerCase());
      }
      const catalog = { project: project.name, kits: index, samples: [...included],
        note: "These are raw kit records for archival use. Restore kits as part of a .dt2prj project." };
      parts.push(await Archive.file(folder + "/Kit index.json", encoder.encode(JSON.stringify(catalog, null, 2)), true));
    }
    assert(parts.length, "No active Digitakt II patterns to catalog.");
    return { bytes: Archive.build(parts), count };
  }

  return { parse, assertTemplate, compose, kitBackup };
})();

const fs = require("node:fs/promises");
const path = require("node:path");
const readline = require("node:readline");
const adapters = {
  arprj: { ...Native, assertTemplate: Rytm.assertTemplate },
  syx: { ...Rytm, kitBackup: Native.kitBackup },
  dt2prj: Digitakt,
};
let sources = [], template = null, lock = null;

function summarize(project) {
  return project ? { name: project.name, format: project.format, patterns: project.patterns,
    firmware: project.manifest?.FirmwareVersion || "", version: project.version || 0 } : null;
}

function state() {
  return { sources: sources.map(summarize), template: summarize(template), hasLock: !!lock };
}

function parse(bytes, name) {
  const extension = path.extname(name).slice(1).toLowerCase();
  Archive.assert(Object.hasOwn(adapters, extension), "Choose an .arprj, .dt2prj or Rytm .syx file.");
  return adapters[extension].parse(bytes, name);
}

async function fromFile(file) {
  Archive.assert((await fs.stat(file)).size <= Archive.MAX_FILE, "File exceeds 256 MB.");
  return parse(await fs.readFile(file), path.basename(file));
}

function validateGrid(grid, projects) {
  Archive.assert(Array.isArray(grid) && grid.length === 128, "Invalid arrangement.");
  grid.forEach(ref => {
    if (!ref) return;
    Archive.assert(Number.isInteger(ref.project) && Number.isInteger(ref.slot)
      && projects[ref.project]?.patterns[ref.slot], "Invalid pattern placement.");
  });
}

function projectRecord(p) {
  return p && { name: p.name, data: Buffer.from(p.bytes.buffer, p.bytes.byteOffset, p.bytes.byteLength).toString("base64") };
}

function decodeRecord(p) {
  const data = p?.data ?? p?.syx;
  Archive.assert(typeof p?.name === "string" && typeof data === "string", "Invalid workspace project.");
  return parse(Buffer.from(data, "base64"), p.name);
}

async function newFile(file, data) {
  await fs.writeFile(file, data instanceof Blob ? Buffer.from(await data.arrayBuffer()) : data, { flag: "wx" });
}

async function handle(message) {
  switch (message.op) {
    case "import": {
      const errors = [];
      let added = 0, empty = 0;
      for (const file of message.files) {
        try { sources.push(await fromFile(file)); added++; }
        catch (error) {
          if (error.code === "EMPTY_PROJECT") empty++;
          else errors.push(path.basename(file) + " — " + error.message);
        }
      }
      return { ...state(), added, empty, errors };
    }
    case "template": {
      const candidate = await fromFile(message.file);
      adapters[candidate.format].assertTemplate(candidate);
      template = candidate;
      lock = null;
      return state();
    }
    case "lock": {
      lock = Native.parseLock(JSON.parse(await fs.readFile(message.file, "utf8")));
      return state();
    }
    case "save": {
      validateGrid(message.grid, sources);
      const serialLock = lock && { id: lock.id, device: "analog-rytm-mk2", split: { track: lock.tracks, pattern: lock.patterns } };
      const estimatedSize = [template, ...sources].filter(Boolean).reduce((sum, p) => sum + Math.ceil(p.bytes.length / 3) * 4 + 1024, 0);
      Archive.assert(estimatedSize <= Archive.MAX_FILE, "The workspace exceeds 256 MB.");
      await newFile(message.file, JSON.stringify({ format: "rytm-set-builder", version: 2, template: projectRecord(template),
        projects: sources.map(projectRecord), grid: message.grid, lock: serialLock }));
      return {};
    }
    case "open": {
      Archive.assert((await fs.stat(message.file)).size <= Archive.MAX_FILE, "Workspace exceeds 256 MB.");
      const data = JSON.parse(await fs.readFile(message.file, "utf8"));
      Archive.assert(data?.format === "rytm-set-builder" && [1, 2].includes(data.version)
        && Array.isArray(data.projects), "Invalid workspace.");
      const projects = [];
      for (const project of data.projects) projects.push(await decodeRecord(project));
      const candidate = data.template ? await decodeRecord(data.template) : null;
      if (candidate) adapters[candidate.format].assertTemplate(candidate);
      const candidateLock = data.lock ? Native.parseLock(data.lock) : null;
      validateGrid(data.grid, projects);
      sources = projects;
      template = candidate;
      lock = candidateLock;
      return { ...state(), grid: data.grid };
    }
    case "export": {
      validateGrid(message.grid, sources);
      Archive.assert(template && path.extname(message.file).toLowerCase() === "." + template.format,
        "Choose a blank template and save with its project extension.");
      const result = await adapters[template.format].compose(template, sources, message.grid, lock);
      await newFile(message.file, result.bytes);
      return { bundled: result.bundled, missing: result.missing };
    }
    case "kits": {
      const projects = sources.filter(p => message.device === "dt2prj" ? p.format === "dt2prj" : p.format !== "dt2prj");
      const result = await adapters[message.device].kitBackup(projects);
      await newFile(message.file, result.bytes);
      return { count: result.count };
    }
    default:
      throw new Error("Unknown companion operation.");
  }
}

if (require.main === module) {
  (async () => {
    for await (const line of readline.createInterface({ input: process.stdin, crlfDelay: Infinity })) {
      try {
        const value = await handle(JSON.parse(line));
        process.stdout.write(JSON.stringify({ ok: true, value }) + "\n");
      } catch (error) {
        process.stdout.write(JSON.stringify({ ok: false, error: error.code === "EEXIST"
          ? "That output already exists. Choose a new filename." : error.message }) + "\n");
      }
    }
  })();
}

module.exports = { adapters, ProjectPayload, Archive, handle };
