# Contributing to Mandarina

Thanks for helping! Mandarina is a fork of [Filee](https://github.com/KnifeLemon/Filee) and keeps its structure, so most
contributions touch one file plus a test. This guide is Filee's, adapted. What Mandarina adds (the wheels, the tool panels,
Whisper transcription, the Office engine) is described in [CLAUDE.md](CLAUDE.md) and [docs/mandarina/](docs/mandarina/); changes to
Filee's own engines and converters are best sent upstream as well. Every change inside a Filee file carries a `Mandarina:` comment.

## Setup

1. Install the .NET 10 SDK (the exact feature band is pinned in `global.json`, newer patches roll forward).
2. `pwsh build/fetch-fonts.ps1` (optional, for the Noto Sans UI fonts).
3. `pwsh build/fetch-engines.ps1 -Only rhwp,pandoc` (optional: rhwp renders HWP/HWPX, and everything converted
   through HWPX — DOCX, XLSX, CSV, PPTX, Markdown — to PDF, and converts HWP ↔ HWPX; Pandoc adds HTML / Markdown ↔
   DOCX. XLSX ↔ CSV and anything → HWPX work without either). Without `-Only` it also downloads LibreOffice with
   H2Orestart and Java (~420 MB), which only older formats such as DOC, XLS and PPT need. Engines in the
   repository's `engines/` folder are picked up by debug builds; users download the large ones from the app.
   Filee can also discover supported engines on `PATH` and in their standard installation folders. Settings >
   Engines lets you choose a specific copy. Filee never uses Microsoft Office; Mandarina's optional Office engine does, through
   COM, and only when the user has Office installed and the switch in Settings > Engines is on.
4. `dotnet run --project src/Filee.App`, `dotnet test`.

Any IDE works (Visual Studio 2022+, Rider, VS Code with C# Dev Kit).

## Linux and macOS development

These ports are development builds until native desktop tests are complete. Install .NET 10 and PowerShell 7,
then build on the target OS. Supported package targets are `linux-x64`, `linux-arm64`, `osx-x64` and `osx-arm64`.

```sh
pwsh build/fetch-fonts.ps1
pwsh build/fetch-portable-engines.ps1 -Runtime linux-x64 -Destination engines
dotnet test Filee.slnx -c Release
pwsh build/build-portable.ps1 -Runtime linux-x64
```

Use the matching runtime on a Mac or ARM64 Linux machine. Packaging runs on Linux/macOS so archives retain
executable permissions. Each archive contains the app, CLI, rhwp, 7-Zip, installation instructions and per-user
install/uninstall scripts. `Filee-<version>-<rid>-SHA256SUMS.txt` verifies the archive. macOS bundles use
`com.filee.app` and a development ad-hoc signature by default; public distribution still needs Developer ID signing
and notarization. `-SigningIdentity` can supply a signing identity, but does not perform notarization.

- **Linux:** drag gestures require X11. Install the Avalonia desktop dependencies (fontconfig, FreeType, X11,
  Xrandr, Xi, Xtst, XkbCommon, ICE and SM) and CJK fonts for document comparisons. File actions are installed for
  Thunar, Nautilus, Nemo and Dolphin. The first enabled keyboard shortcut uses Thunar's native custom action;
  close Thunar before changing or pausing that shortcut and reopen it afterwards. Global selected-file queries
  and hold gestures are not supported. Wayland can use file actions, the drop zone, watch folders and CLI.
- **macOS:** grant Filee Accessibility permission for gestures and Finder Automation permission for selection.
  Keep the development `.app` at a stable path while testing these permissions. Finder integration is a Quick
  Action under Services; auto-start uses a per-user LaunchAgent.
- **Engines:** packages are selected by OS and architecture. Linux LibreOffice/Ghostscript and macOS
  FFmpeg/Ghostscript currently use system or user-selected installations. Linux ARM64 has no Unhwp native binary.
  The macOS LibreOffice package does not include H2Orestart; native rhwp/Unhwp provide the supported HWP routes.
  Use Settings > Engines to inspect actual availability before comparing formats.

Filee's `portable.yml` workflow (not part of Mandarina) built and tested all four targets; native platform tests skip on other
OSes. Mandarina's wheels, tool panels and Office engine are Windows only and have only been tried there. Before calling a port
ready, verify the installed package, CLI conversion, Finder/Thunar selection with Unicode and spaced paths, drag and keyboard
gestures, permissions denied/granted, Retina/scaling, tray, login startup, engine downloads, watch folders, and uninstall.

## Where things live

Read [docs/ARCHITECTURE.md](docs/ARCHITECTURE.md) first. In short:

- **New file format or engine** → `src/Filee.Engines`, see [docs/ADDING-A-CONVERTER.md](docs/ADDING-A-CONVERTER.md).
- **New language** → `src/Filee.App/Assets/i18n`, see [docs/ADDING-A-LANGUAGE.md](docs/ADDING-A-LANGUAGE.md).
- **UI** → `src/Filee.App/Views` (XAML) + `ViewModels` (CommunityToolkit.Mvvm).
- **Domain rules** (routes, output names, settings) → `src/Filee.Core` — no UI or OS code here.

## Code style

- `dotnet format` must pass (CI runs `dotnet format --verify-no-changes`). `.editorconfig` has the rules.
- Comments are in English so every contributor can read them.
  - Every file starts with a short comment saying what it is for.
  - Public types and members get `///` XML docs.
  - Explain *why* for anything non-obvious (workarounds, engine quirks) — see `LibreOfficeConverter.cs` for the tone.
- Keep UI strings out of code: add a key to every `i18n/*.json` file (en, es, ko, zh-CN; a test checks they match). Write your
  own wording: do not copy text from other products.
- Prefer small, focused pull requests.

## Tests

- `tests/Filee.Core.Tests` – pure logic (fast).
- `tests/Filee.Engines.Tests` – real conversions on generated input files. Tests needing an engine that is not
  installed skip automatically. XLSX and PPTX inputs are generated by `OfficeBuilders.cs`, DOCX by
  `DocxBuilder.cs`.
- `tests/Filee.App.Tests` – gesture state machine, donut geometry, translations and headless UI rendering.
  Screenshots of every page land in `tests/Filee.App.Tests/bin/<config>/net10.0/screenshots` — attach them to
  UI pull requests.

## Pull requests

1. Fork, create a branch, make your change with tests.
2. `dotnet format && dotnet test`.
3. Open a PR and fill in the template. Screenshots for UI changes, please.

## Building and installing Mandarina (maintainers)

Mandarina has no installer or release workflow yet: it is built from source. `tools/mandarina/publish-mandarina.ps1` publishes
the app and the `mandarina` command into `%USERPROFILE%\.mandarina\publish\win-x64` (with the bundled rhwp and 7-Zip), and
`tools/mandarina/install-mandarina.ps1` installs that build for the current user (Start menu entry, engines it finds in
`%USERPROFILE%\.mandarina\engines`). Filee's own installer (`installer/Filee.iss`, `build/build-installer.ps1`) and its
`Filee-<version>-win-Setup.exe` packaging are still in the tree but are not used by Mandarina; its engine page and some of its
texts are generated from `EngineDownloads` and the app's translations (`build/tools/make-installer-engines.cs`).
