# EPC: Electron Project Companion

**Beta 0.1**

[Download EPC Beta 0.1 for Windows x64](https://github.com/Claptom-music/elektron-project-companion/raw/refs/heads/main/downloads/EPC-Beta-0.1-Windows-x64.zip). Extract the ZIP and open **EPC - Electron Project Companion.exe**. Keep **project-core.cjs** beside it. EPC requires Node.js 24.

A Windows project editor for arranging live sets from local Elektron backups. Browse patterns, place them in banks A–H, and export a new project for Elektron Transfer. The long-term goal is one editor with a separate format adapter for every Elektron machine that can back up editable projects.

## Latest Beta 0.1 update

- **Digitakt II pool merging.** EPC keeps the starting project's sample slots, reuses matching audio, and fills free slots in order from bank A through H. It merges preset pools and updates track assignments, sample locks, and preset locks when slots move.
- **Verified with OS 1.15C backups.** Four projects merged into 91 consecutive sample slots in bank A. The export preserved the sample references on all 15 sample locks. A separate test with a preset slot conflict confirmed the preset lock still pointed to the intended preset.
- **Hardware tested on Digitakt II.** The maintainer confirmed the Beta 0.1 merge workflow on the instrument. The firmware version for this hardware test was not recorded.

## What works today

| Device | Files | Current coverage |
| --- | --- | --- |
| Analog Rytm MKII | .arprj and whole-project .syx | Move patterns with their parameter locks, saved kits and sounds used by sound locks. Update their slot references and bundle available sample audio in .arprj. Export linked kits as separate SysEx files. |
| Digitakt II | .dt2prj, project image v4 | Move patterns with their parameter locks and paired kits. Merge sample and preset pools, update track assignments and sample and preset locks, and bundle available audio. Export raw kit records for archival use. File tests used real OS 1.15C backups. The maintainer confirmed the merge workflow on Digitakt II hardware. OS 1.17 compatibility is still unverified. |

Each arrangement uses one device and one file format. The template supplies songs and project-wide settings. For Digitakt II, the source of the first placed pattern supplies the starting sample pool. EPC reuses matching samples and adds new ones to the next free slot in banks A–H. Export stops if the merged pool exceeds 1,016 sample slots or 128 preset slots, if a preset lock points to an unsupported or missing slot, or if EPC detects modulation of sample selection in a merged pool.

## Run on Windows

Build with Node.js 24 and the Windows .NET Framework C# compiler.

1. From the repository root, run **powershell -File build.ps1**.
2. Open **dist/EPC - Electron Project Companion.exe**. Keep **project-core.cjs** beside it. On first launch, select **Transfer.exe** in the link prompt. You can skip this and link it later from the **Project** menu.
3. Choose the **Analog Rytm MKII** or **Digitakt II** tab. Use its **Project** menu to import backups. Each tab keeps its own arrangement and workspace.
4. Drag a source pattern onto a slot to place or replace it. Use **Undo** to recover a replaced pattern. Drag or select placed patterns to move or swap them.
5. Save a workspace or export a project from the **Project** menu. If you haven't loaded a template, select a backup of a saved, empty project from the same device. Complete any firmware prompts, then choose a new filename in **Save merged project**. EPC confirms the output path after it writes the file. Choose **Project → Open Transfer** and import the export into a free project slot. Check playback, kits and samples on the machine before relying on the set.

When closing EPC, you can save or discard unsaved changes for each device workspace, or cancel and keep editing.

The app reads and writes local project files. Transfer handles the connection to your instrument. The Windows launcher finds Node on PATH or beside the app. You can also point **ELEKTRON_COMPANION_NODE** at a Node.js 24 executable.

**Project → Firmware compatibility** sets the destination firmware for the workspace. The companion asks before the first export when the backup does not identify a mod layout. For Modwerk or Elekloader builds that use the standard project layout, confirm that layout in the dialog.

The companion detects tagged Analog Rytm MKII mod storage. Use a blank **.arprj** from the matching firmware build and load its storage lockfile or a build manifest containing the lockfile. The exporter checks the layout ID and track and pattern generations before moving mod data to the new slots. Octabam targets Octatrack, whose projects do not yet have an export adapter. [Elekloader mod storage](https://github.com/elekloader/elekloader/blob/main/docs/ADAPTING.md#mod-storage-core-ar2-02-analog-rytm-mkii) · [Octabam](https://github.com/sambanks/octabam)

EPC accepts version 9 Rytm backups with a version 4 settings record, as used by MF06. A blank template with empty mod storage can be used to merge stock patterns. To move patterns with tagged mod data, load and save the blank project on the Rytm before backing it up so its layout can be matched.

**Project → Find Rytm lockfile** can check the companion's GitHub registry by layout ID, load a public JSON link from GitHub, Modwerk or Elekloader, or open a local file. Online lookup is opt-in. It sends the file request only, with no project or firmware contents. Save the workspace to keep the accepted lockfile data for offline use.

The upstream mod catalogues list modules. Elekloader generates the storage lockfile when a firmware configuration is built, so a custom build may have no public match. For a private build, use the local JSON from the build output. The [companion registry](https://github.com/Claptom-music/elektron-project-companion/tree/main/firmware/analog-rytm-mk2) accepts verified public layouts.

To run the Digitakt II archive test with your own disposable fixture, run **node test/verify-digitakt.cjs PATH_TO_YOUR_DT2PRJ**. The test derives a temporary blank image in memory and checks pattern and kit bytes, audio bundling, sample packing and preset lock remapping.

## Help build this

I’m building EPC to make it easier to pull together a live set from the projects you already have. The aim is to move patterns and their sounds between backups, arrange them on one bank grid, and load the result with Elektron Transfer.

**Have an Elektron machine? [Open an issue](https://github.com/Claptom-music/elektron-project-companion/issues/new) with your model, firmware version, and what you’d like to test or build.** Useful contributions include:

- Restore a small export to a spare project slot and report whether patterns, kits, sample locks and playback match.
- Help map a new machine’s format with a blank project and a second project containing one known edit.
- Improve the Windows UI or add validation around an existing device adapter.

Only attach fixtures you created and can share publicly. A description and reproducible steps are useful even when you keep your music and samples private.

When the maintainer confirms a new feature has been tested on hardware, update this README with what changed and what was tested.

## Format notes

The Windows UI uses a local JSON-line service. The format registry in **src/project-core.cjs** selects a parser and writer by file type. Transfer archives share ZIP and LZ4 handling, while the Rytm and Digitakt II adapters own their device-specific layouts. Relevant references include [Elektron Transfer documentation](https://www.elektron.se/wp-content/uploads/2026/03/Transfer-User-Manual_ENG_OS1.10_260304.pdf), [libanalogrytm](https://github.com/bsp2/libanalogrytm), [DNX container research](https://github.com/NoiseAndMatter/DNX), and [Digitakt II project format notes](https://github.com/zhelnov/dt_song_mgmt/blob/main/SPEC.md).

This is a community project and is unaffiliated with Elektron.

## License

EPC is licensed under the [MIT License](LICENSE).
