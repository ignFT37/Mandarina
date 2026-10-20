<p align="center">
  <img src="src/Filee.App/Assets/Icons/filee.svg" alt="Mandarina" width="96">
</p>

<h1 align="center">Mandarina</h1>

<p align="center">
  <b>Convert and tweak files by dragging them.</b><br>
  Start dragging files, tap a key, and a wheel opens at your cursor.
</p>

<p align="center"><b>English</b> · <a href="README.es.md">Español</a></p>

> Mandarina is inspired by Tangerine for Mac (Leap Studio). It is an independent project with no relationship with them:
> its code, texts and assets are its own. It is built on [Filee](https://github.com/KnifeLemon/Filee) (MIT), whose conversion
> engine it uses.

## How it works

Drag one or more files from the Explorer (or anywhere) and tap a key. The wheel appears around the cursor.

| Key | Wheel | What it offers |
|---|---|---|
| **Shift** | Convert | The formats the files can become |
| **Ctrl** | Tools | Compress, trim, crop, redact, merge… (depends on the kind of file) |
| **Alt** | Actions | Share, float (a shelf to drag from later), copy, move, copy path, terminal |

Drop the files on a petal. The result is a new file next to the original (`photo cropped.jpg`): originals are never touched and
nothing is overwritten. Keys and wheels can be changed in Settings → Shortcuts, and **Ctrl+Alt+Space** opens a wheel for whatever
is selected in the Explorer.

## What it does

- **Converts** hundreds of formats (images, audio, video, PDF, Office documents, e-books, archives…) with Filee's engines,
  offline. Routes of several steps are found automatically.
- **Tools with their own window**, saving a copy beside the original:
  photos (compress, metadata, edit, annotate, background, crop, redact, create PDF, collage), audio (normalize, trim, channels,
  bleep, visualize), video (mute, trim, crop, speed, snapshots, split, join, redact), PDF (split, merge, organize) and archives
  (extract). Redact finds faces and text on the PC, with Windows' own recognizers.
- **Transcribes and subtitles** audio and video with Whisper, locally: on the graphics card if there is a dedicated one, on the
  processor otherwise.
- **Uses Microsoft Office when you have it** (optional, with a switch): Word, Excel and PowerPoint convert documents through COM,
  without touching the Office you have open.
- **Stays on your PC.** No uploads, no telemetry, no update checks. It goes online only to download the engines and models you
  choose to install, from the official addresses pinned (with SHA-256) in
  [`engines.json`](src/Filee.Engines/Infrastructure/engines.json).

What each wheel and tool does, in detail: [docs/mandarina/ESPECIFICACION.md](docs/mandarina/ESPECIFICACION.md) (Spanish).

## Status

Windows 10/11 only, and young: expect rough edges. Binary releases are unsigned (see Install); you can also build it from source.

## Install

Download **`Mandarina-<version>-win-x64-setup.exe`** (installer, for you only, no administrator rights) or
**`Mandarina-<version>-win-x64.zip`** (portable: unzip anywhere and run `Mandarina.exe`) from the
[latest release](https://github.com/ignFT37/Mandarina/releases). Windows 10/11, 64-bit; nothing else to install. The installer adds a Start menu shortcut and, if you
leave the box ticked, the `mandarina` command to your PATH.

Check the download against `SHA256SUMS.txt` from the same release:

```powershell
Get-FileHash .\Mandarina-0.1.0-win-x64-setup.exe -Algorithm SHA256
```

**Unsigned downloads.** The files are not code-signed (no certificate yet), so Windows is wary of them:
- *SmartScreen* ("Windows protected your PC") appears on files downloaded from the web: click **More info → Run anyway**. For the
  zip, right-click it → Properties → **Unblock** before extracting, or the warning is repeated for every `.exe` and `.dll`.
- *Smart App Control* (Windows 11, on a clean install) can block unsigned programs and DLLs without an "run anyway" button. If it
  does, Mandarina cannot run on that PC unless you turn it off (Windows Security → App & browser control → Smart App Control);
  Windows only lets you turn it off, not back on without reinstalling Windows. That is your call: if you prefer not to, build it
  from source (below).

FFmpeg, Pandoc and the Whisper models are not included: Mandarina downloads the ones you choose from Settings → Engines.
The license texts of everything inside the download are in `THIRD-PARTY-NOTICES.md`.

## Build from source

You need Git, the [.NET 10 SDK](https://dotnet.microsoft.com/download) and PowerShell 7 (`pwsh`).

```powershell
git clone https://github.com/ignFT37/Mandarina.git
cd Mandarina
pwsh build/fetch-engines.ps1 -Only rhwp,7zip     # the two engines that ship inside the program
pwsh tools/mandarina/publish-mandarina.ps1       # the app and the `mandarina` command, into %USERPROFILE%\.mandarina\publish\win-x64
powershell -NoProfile -ExecutionPolicy Bypass -File tools/mandarina/install-mandarina.ps1 -AddCliToPath
```

On first start Mandarina asks which optional engines to download (FFmpeg for audio and video, Pandoc for text formats, a Whisper
model for transcription…). To run it without installing: `dotnet run --project src/Filee.App`.

## Command line

```
mandarina convert photo.heic --to jpg
mandarina convert *.png --to webp -o converted
```

`mandarina help` lists every option (it is Filee's command line, under another name).

## Development

```powershell
dotnet build Filee.slnx
dotnet test            # about 13 minutes; the tests that need real windows, Office or big downloads are opt-in
```

Start with [CLAUDE.md](CLAUDE.md) and [CONTRIBUTING.md](CONTRIBUTING.md). Docs: [Mandarina's](docs/mandarina/) (the tool panels'
contract, the Office engine, the specification) and Filee's [architecture](docs/ARCHITECTURE.md),
[engines](docs/ENGINES.md) and [how to add a converter](docs/ADDING-A-CONVERTER.md).

## License and credits

MIT, © 2026 Ignacio Insua (Mandarina) and © 2026 Myeonggu Kim (KnifeLemon) and Filee contributors, see [LICENSE](LICENSE).
Third-party components keep their own licenses: [THIRD-PARTY-NOTICES.md](THIRD-PARTY-NOTICES.md). Tangerine and its makers are not
affiliated with this project.

<!-- sponsors: src/Filee.App/Assets/Sponsors/sponsors.json -->
