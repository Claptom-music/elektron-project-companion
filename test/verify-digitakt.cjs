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
  const sampleTable = 0xdc870b;
  const sampleSlot = 1;
  const selector = kit(active.at(-1)) + 0x3c + 0x64;
  const first = { ...source, image: source.image.slice() };
  // Give the two sources different A001 samples and make A002 the first free destination.
  Archive.view(first.image).setUint32(sampleTable + 16, 0xffffffff);
  const firstData = Archive.view(first.image), secondData = Archive.view(different.image);
  for (const [data, hash] of [[firstData, 0x55aa0001], [secondData, 0x55aa0002]]) {
    data.setUint32(sampleTable, 1);
    data.setUint32(sampleTable + 4, hash);
    data.setUint32(sampleTable + 8, 0x200);
  }
  Archive.view(different.image).setUint16(selector, sampleSlot);
  different.image[selector - 0x64 + 0xd2] = 0;
  const combined = await Digitakt.compose(template, [first, different], layout);
  const merged = await Digitakt.parse(new Uint8Array(await combined.bytes.arrayBuffer()), "merged-banks.dt2prj");
  const moved = Archive.view(merged.image).getUint16(kit(127) + 0x3c + 0x64);
  assert.equal(moved, 2);
  assert.equal(Archive.view(merged.image).getUint32(sampleTable + 4), 0x55aa0001);
  assert.equal(Archive.view(merged.image).getUint32(sampleTable + (moved - 1) * 16 + 4), 0x55aa0002);
  assert.equal(Archive.view(different.image).getUint16(selector), sampleSlot);
  // Two different presets in the first slot must both survive the merge.
  const pool = 0xda5a04, presetSize = 1109;
  const presetA = source.image.slice(kit(active[0]) + 0x3c, kit(active[0]) + 0x3c + presetSize);
  presetA[8] = 65;
  presetA[0xd2] = 0;
  const presetB = presetA.slice();
  presetB[8] = 66;
  const withA = { ...source, image: source.image.slice() };
  const withB = { ...other, image: source.image.slice() };
  withA.image.set(presetA, pool);
  withB.image.set(presetB, pool);
  const lock = pattern(active.at(-1)) + 4 + 0x400;
  withB.image[lock] = 0;
  const presets = await Digitakt.compose(template, [withA, withB], layout);
  const presetExport = await Digitakt.parse(new Uint8Array(await presets.bytes.arrayBuffer()), "merged-presets.dt2prj");
  assert.deepEqual(presetExport.image.subarray(pool, pool + presetSize), presetA);
  const targetPreset = presetExport.image[pattern(127) + 4 + 0x400];
  assert(targetPreset > 0 && targetPreset < 128);
  const targetPresetAt = pool + targetPreset * presetSize;
  assert.deepEqual(presetExport.image.subarray(targetPresetAt, targetPresetAt + presetSize), presetB);
  assert.equal(withB.image[lock], 0);
  withB.image[lock] = 128;
  await assert.rejects(Digitakt.compose(template, [withA, withB], layout), /missing or unsupported preset slot/);
  const kits = await Digitakt.kitBackup([source]);
  assert.equal(kits.count, active.length);
  console.log("Digitakt II archive round trip, paired kits, sample packing, preset lock remapping and kit catalog passed.");
}

verify(process.argv[2]).catch(error => { console.error(error); process.exitCode = 1; });

