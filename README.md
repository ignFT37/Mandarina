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

Check the download against `SHA256SUMS.txt` from the same release (read the note on unsigned downloads below first):

```powershell
Get-FileHash .\Mandarina-0.1.0-win-x64-setup.exe -Algorithm SHA256
```

> **The downloads are not code-signed.** There is no certificate yet: the free one from [SignPath Foundation](https://signpath.org)
> was denied for now, until the project is better known, and Mandarina will be signed when that becomes possible (see the
> [Code signing policy](#code-signing-policy)). What you will see:
> - *SmartScreen* ("Windows protected your PC"): click **More info → Run anyway**. Your browser may also warn when you download
>   the file. For the zip, right-click it → Properties → **Unblock** before extracting, or the warning is repeated for every `.exe`
>   and `.dll`.
> - *Smart App Control*, when it is on, does not let Windows open Mandarina, not even one you build from the source. To use it you
>   have to turn Smart App Control off (Windows Security → App & browser control → Smart App Control). That decision is yours.
>
> **Check what you downloaded:** compare its SHA-256 (`Get-FileHash <file> -Algorithm SHA256`) with `SHA256SUMS.txt` from the
> same release. The releases are built by GitHub Actions from the code of this repository. Mandarina is offered as is, under the
> MIT license, without warranty.

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

## Code signing policy

Free code signing from [SignPath Foundation](https://signpath.org) was requested for the Windows releases and denied for now: its
free program is for projects that are already publicly known (stars, forks, contributors, articles or sustained activity). It can
be requested again when Mandarina is better known; paid signing is not planned for now. **Until then the downloads are unsigned**
(see [Install](#install)).
<!-- If SignPath approves it later, replace the paragraph above with: Free code signing provided by [SignPath.io](https://about.signpath.io), certificate by [SignPath Foundation](https://signpath.org) -->

What this policy will cover once signing is in place:

- **What will be signed:** only the installer and the programs and libraries that the [release workflow](.github/workflows/release.yml)
  of this repository builds from its source (`Mandarina.exe`, the `mandarina` command line and Mandarina's own DLLs). Third-party
  files that travel in the download (7-Zip, rhwp, .NET runtime libraries) are not signed by this project.
- **Roles:** authors, reviewers and approvers are all Ignacio Insua ([@ignFT37](https://github.com/ignFT37)), who also approves
  every signing request.
- **System changes:** the installer asks, with both boxes ticked by default, whether to start Mandarina with Windows and whether to add
  "Convert with Mandarina" to the context menu and to "Send to". Both can be changed later in Settings → General, and the
  uninstaller removes everything it added.

### Privacy

This program will not transfer any information to other networked systems unless specifically requested by the user or the person installing or operating it.

Mandarina connects to the network only to download the engines and models you choose to install (FFmpeg, Pandoc, Whisper models…),
from the addresses pinned with SHA-256 in [`engines.json`](src/Filee.Engines/Infrastructure/engines.json). It sends no telemetry,
checks for no updates and uploads none of your files: conversions, transcription and text and face detection run on your PC.
The engines it downloads are separate programs that run locally; they are not asked to send anything either.

## License and credits

MIT, © 2026 Ignacio Insua (Mandarina) and © 2026 Myeonggu Kim (KnifeLemon) and Filee contributors, see [LICENSE](LICENSE).
Third-party components keep their own licenses: [THIRD-PARTY-NOTICES.md](THIRD-PARTY-NOTICES.md). Tangerine and its makers are not
affiliated with this project.

<!-- sponsors: src/Filee.App/Assets/Sponsors/sponsors.json -->
