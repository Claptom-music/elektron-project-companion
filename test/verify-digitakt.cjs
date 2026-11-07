"use strict";
const assert = require("node:assert/strict");
const fs = require("node:fs/promises");
const { adapters, Archive, ProjectPayload } = require("../src/project-core.cjs");
const Digitakt = adapters.dt2prj;
const pattern = slot => 0x200 + 0x15c00 * slot;
const kit = slot => 0xae0200 + 0x5800 * slot;

async function verify(file) {
  const source = await Digitakt.parse(await fs.readFile(file), "fixture.dt2prj");
  const active = source.patterns.flatMap((p, i) => p.active ? [i] : []);
  const blank = source.patterns.findIndex(p => !p.active);
  assert(active.length > 0 && blank >= 0);
  // Make a test-only blank image from an unused record in the fixture.
  const image = source.image.slice();
  for (const slot of active) {
    image.set(source.image.subarray(pattern(blank), pattern(blank) + 0x15c00), pattern(slot));
    image.set(source.image.subarray(kit(blank), kit(blank) + 0x5800), kit(slot));
  }
  const container = ProjectPayload.encode(source.header, image);
  const entry = await Archive.file(source.manifest.Payload, container, true);
  const entries = [...source.entries.values()].filter(item => item.name !== source.manifest.Payload);
  const template = await Digitakt.parse(new Uint8Array(await Archive.build([...entries, entry]).arrayBuffer()), "blank.dt2prj");
  Digitakt.assertTemplate(template);
  const layout = Array(128).fill(null);
  layout[16] = { project: 0, slot: active[0] };
  layout[127] = { project: 1, slot: active.at(-1) };
  const other = { ...source, name: "other.dt2prj" };
  const result = await Digitakt.compose(template, [source, other], layout);
  const reopened = await Digitakt.parse(new Uint8Array(await result.bytes.arrayBuffer()), "merged.dt2prj");
  for (const [slot, ref] of layout.entries()) {
    if (!ref) continue;
    assert.equal(reopened.patterns[slot].trigs, source.patterns[ref.slot].trigs);
    for (const [offset, length] of [[pattern, 0x15c00], [kit, 0x5800]]) {
      assert.deepEqual(reopened.image.subarray(offset(slot), offset(slot) + length),
        source.image.subarray(offset(ref.slot), offset(ref.slot) + length));
    }
  }
  assert.equal(reopened.patterns.filter(p => p.active).length, 2);
  assert.equal(result.bundled, new Set((source.manifest.Samples || []).filter(s => source.entries.has(s.FileName))
    .map(s => Number(s.Hash) + "/" + s.FileSize)).size);
  assert.deepEqual(reopened.image.subarray(0xdcd400), template.image.subarray(0xdcd400));
  const different = { ...other, image: source.image.slice() };
  different.image[0xdc8711] ^= 1;
  await assert.rejects(Digitakt.compose(template, [source, different], layout), /different sample or preset pools/);
  const kits = await Digitakt.kitBackup([source]);
  assert.equal(kits.count, active.length);
  console.log("Digitakt II archive round trip, paired kit records, sample bundling, pool conflict and kit catalog passed.");
}

verify(process.argv[2]).catch(error => { console.error(error); process.exitCode = 1; });

