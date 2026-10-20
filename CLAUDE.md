# Mandarina: convertir y tocar archivos arrastrando, para Windows

- Es un fork de [Filee](https://github.com/KnifeLemon/Filee) (licencia MIT, C#/.NET 10 + Avalonia 12) con una interfaz de
  ruedas encima, inspirado en Tangerine para Mac (Leap Studio); proyecto independiente, sin relación con ellos. Arrastras
  archivos y tocas una tecla: **Shift** abre la rueda de convertir, **Ctrl** la de herramientas y **Alt** la de acciones. La
  rueda aparece en el cursor.
- Lo que hace Mandarina (gesto, ruedas, conversiones, herramientas, acciones y estilo) está en
  `docs/mandarina/ESPECIFICACION.md`.
- Notas privadas de quien mantiene el fork: si existe la carpeta `local/` (la ignora git), léela antes de empezar
  (`local/ESTADO.md` y `local/TRAMPAS.md`).

## Cómo se trabaja
- Trabajamos en la rama `mandarina` (la predeterminada); `main` se queda como en Filee. El remoto `origin` es este repositorio y
  `upstream` es KnifeLemon/Filee; para traer sus mejoras, desde `mandarina`: `git fetch upstream` y después
  `git merge upstream/main`.
- Lo nuestro va en archivos propios y toca lo mínimo de Filee:
  - `src/Filee.App/Mandarina/` (las ruedas, el marco de los paneles en `Panels/` y un panel por herramienta en `Tools/`)
  - `src/Filee.App/Controls/DonutMenu.Glass.cs`
  - `src/Filee.App/Services/PlatformLayers.cs`
  - `src/Filee.Platform.Windows/MandarinaShell.cs` y `MandarinaShare.cs`
  - El motor de Microsoft Office: `src/Filee.Engines/Office/OfficeConverter.cs` (más `OfficeFiles.cs`, `OfficeSelfTest.cs` y la
    carpeta `Com/`) y `src/Filee.Core/Conversion/ConversionFailureException.cs`; diseño y trampas en `docs/mandarina/OFFICE.md`.
  - Los tests `Mandarina*` de `tests/Filee.App.Tests` y `OfficeEngineTests` de `tests/Filee.Engines.Tests`.
  - `tools/mandarina/` (scripts de prueba con la app instalada y de textos, el banco de memoria `soak/`, `whisper-bench.ps1` y
    los scripts de publicación e instalación).

  Cada cambio dentro de un archivo de Filee lleva un comentario «Mandarina:».
- Necesitas el SDK de .NET 10 (`global.json`).
- Las compilaciones van fuera del repo, a `%USERPROFILE%\.mandarina\build\<proyecto>\bin|obj` (lo fija
  `Directory.Build.props`; `-p:MandarinaBuildRoot=<carpeta>/` la cambia, con barra final).
- Comandos:
  - Compilar: `dotnet build Filee.slnx`.
  - Tests propios: `dotnet test tests/Filee.App.Tests --filter "FullyQualifiedName~Mandarina"`.
  - Todos los tests: `dotnet test` (la suite de App, ~1.600 pruebas en un solo proceso, tarda ~13 minutos). Los tests de Motores
    que necesitan un programa (Pandoc, rhwp, FFmpeg…) se saltan si no lo encuentran; el de rutas de Markdown a PDF
    (`PandocMarkupTests.More_markup_languages_and_slides`) solo corre si están Pandoc y rhwp. Las de transcripción con voz real se
    saltan si falta el modelo Whisper (el pequeño tiene las suyas); las de caras se saltan sin una
    foto con caras en `%USERPROFILE%\.mandarina\e2e\fotos\cara.jpg` (o `MANDARINA_FACE_PHOTO`); y las que abren ventanas de
    verdad (la hoja de Compartir de Windows, el Explorador) solo corren con `MANDARINA_UI_TESTS=1`. Las de `OfficeEngineTests`
    que usan el Office real (abren Word, Excel y PowerPoint ocultos) solo corren con `MANDARINA_OFFICE_TESTS=1`, y la descarga
    real de un modelo con `MANDARINA_DOWNLOAD_TESTS=1`.
  - Ejecutar: `%USERPROFILE%\.mandarina\build\Filee.App\bin\Debug\net10.0\Filee.exe`.
  - Abrir una herramienta sin la rueda: `Mandarina.exe --panel trimAudio <archivos>`; `--tools` y `--actions` abren esa rueda.
  - Publicar e instalar: `tools/mandarina/publish-mandarina.ps1` y `tools/mandarina/install-mandarina.ps1`.
  - Versiones para descargar: `tools/mandarina/package-mandarina.ps1` (zip portable + instalador de `installer/Mandarina.iss`
    con Inno Setup + `SHA256SUMS.txt`, en `artifacts\release`, con su propia raíz de compilación `.mandarina\build-release`).
    Las crea `.github/workflows/release.yml` al empujar una etiqueta `vX.Y.Z` (la versión sale de la etiqueta; la de
    `Directory.Build.props` es la de los builds locales y la que enseña «Acerca de»). Nunca instales/desinstales el instalador en
    este PC para probarlo: su desinstalador borra el inicio con Windows, el menú contextual y los motores descargados reales.
  - Firma de código (SignPath Foundation): el README, `README.es.md` y las notas de `release.yml` llevan la «Code signing policy»
    (`MandarinaSigningTests` la vigila, y que todos los .exe/.dll propios digan «Mandarina» en FileDescription y ProductName y tengan
    la misma ProductVersion). El instalador pregunta, con casillas marcadas, si arrancar con Windows y si añadir «Convertir con
    Mandarina» al menú contextual y a «Enviar a»; lo apunta en `setup-options.json` (junto al programa) y el primer arranque lo
    aplica y lo borra (`InstallerChoices.cs`). Cuando SignPath apruebe, se cambia la línea de estado del README (hay un comentario
    HTML con la frase de atribución).
- Los textos de la interfaz están en `src/Filee.App/Assets/i18n/*.json`. Cada clave nueva va en todos los idiomas
  (en, es, ko, zh-CN); un test lo comprueba. `python tools/mandarina/i18n_tool.py add . spec.json` añade claves a los cuatro.
  Textos propios: no copies frases de otros productos.

## Herramientas con panel
- Las herramientas de la rueda que necesitan opciones (recortar, editar, tapar, comprimir…) abren una ventana y guardan
  «nombre sufijo.ext» junto al original. El contrato para escribir una (API, estilo, textos, pruebas) está en
  `docs/mandarina/PANELES.md`; los modelos a copiar son `Tools/TrimAudioPanel.cs` y `Tools/CropVideoPanel.cs`.
- Un panel con trabajo hecho a mano (marcas, zonas, un recorte…) implementa `HasUnsavedWork` (un solo hueco en `ToolPanel`): Esc, ✕ y
  Alt+F4 preguntan «Cambios sin guardar» antes de cerrar. Los fallos de FFmpeg y de los compresores llevan clave de i18n
  (`PanelException.Key`) y salen en el idioma del usuario; si muestras tú un fallo, usa `Context.Explain(ex)`, no `ex.Message`.
- Un panel nuevo es una clase `[ToolPanel("id")]` en `Tools/`: `WheelCatalog.cs` ya lista las herramientas de las ruedas y solo
  muestra las que tienen clase. Mandarina añade dos propias para audio y vídeo, TRANSCRIBIR (`transcribe`, un clic) y SUBTÍTULOS
  (`subtitles`, con panel), con Whisper en local: hay dos modelos, paquetes de Ajustes → Motores (`whisper`, el grande, y
  `whisper-small`, el pequeño para PCs sin tarjeta dedicada; `WhisperModels.For` elige) y el motor es `Tools/WhisperEngine.cs` (filtro
  `whisper` de FFmpeg; usa la tarjeta gráfica dedicada y, si no hay, la CPU con un hilo por núcleo físico). Cada panel lleva su
  `Mandarina<Herramienta>Tests.cs` (render a PNG en la carpeta `screenshots` de los binarios de test + el motor de verdad: FFmpeg,
  Magick.NET, PdfSharp o PDFtoImage).
- Las fotos se guardan con `PanelImages.Save`, que quita las miniaturas ocultas de la foto original (si no, un recorte llevaría
  dentro la foto entera). Magick.NET no escribe HEIC: una foto HEIC (también `.hif`: `PanelImages.IsHeif`) sale como JPG. Las
  muestras HEIC de los tests salen de `HeicSamples` (codificador HEIF de Windows, si hay) y `HeicBuilder` (contenedor escrito a
  mano); un error de Magick sale por `PanelErrors` (`panel.error.damaged`), no con `ex.Message`.
- Para probar con la app instalada: `tools/mandarina/e2e-tool.ps1` (abre un panel, pulsa botones por su nombre, aplica y lista lo
  que aparece; sin ratón salvo `-Drag`) y `e2e-gesture.ps1` (el gesto real en el Explorador). Con ratón y teclado reales solo
  cuando el usuario no esté usando el PC (se puede comprobar con `GetLastInputInfo`; los scripts con entrada real cargan `e2e-guard.ps1`,
  que se niega si el usuario toca el PC y espera 60 s de calma). Lo detallado de cada script está en `tools/mandarina/README.md`.

## Memoria y tests: lo que ya mordió
- `new DispatcherTimer(intervalo, prioridad, callback)` ARRANCA el temporizador y el despachador lo retiene para siempre (con la
  vista, la ventana y el panel). Usa `new DispatcherTimer(prioridad) { Interval = … }` y `Start()` solo mientras haga falta.
- Un cliente de UI Automation retiene todo lo que ha leído de una ventana aunque se cierre (Avalonia no desconecta sus nodos):
  `ToolPanelWindow` lo suelta al cerrar (`AccessibilityRelease`). Para medir memoria de la app usa `tools/mandarina/soak`
  (ventana real, fuera de pantalla, con el PC en marcha) o `open-close-loop.ps1` (sin UIA salvo `-Uia`, y espera a que el PC esté
  libre); no midas con un script UIA que se queda vivo: te enseña la retención del cliente.
- En los tests, `MainWindow.Close()` solo la oculta (vive en la bandeja): `TestServices.EnsureInitialized` cierra de verdad las que
  dejaron los tests anteriores y no vuelve a poner el idioma ni el tema si ya están. Sin eso cada test dejaba una ventana viva y la
  suite se volvía cuadrática. Libera también los `CaptureRenderedFrame()` (memoria nativa) y cierra tus ventanas.

## Trampas conocidas
- Mandarina no se autoactualiza: las comprobaciones de Filee miran sus versiones y reemplazarían el fork. Tampoco enseña las diapositivas
  «Novedades» de Filee (`WhatsNewService` no se llama al arrancar y el enlace de «Acerca de» está oculto).
- Idiomas: solo en, es, ko y zh-CN (`AppLanguages`). Los archivos ja, pl, hi y ru de Filee se quedan en `Assets/i18n` sin usarse: no tienen
  los textos de Mandarina. Cada texto nuevo de Filee que diga «Filee» para la propia app se renombra al traer cambios
  (`MandarinaBrandingTests` lo vigila).
- Al fusionar Filee: Windows nunca debe cargar `Filee.Platform.MacOS.dll` ni `Filee.Platform.Linux.dll` (Smart App Control); todo uso de
  esos tipos va por `PlatformLayers`. La rueda de convertir conserva la cara trasera de Filee (Tab o la rueda del ratón); las de
  herramientas y acciones no tienen.
- Un `dotnet test` interrumpido deja procesos `Filee.App.Tests` y `testhost` que bloquean la compilación siguiente: ciérralos.
- FFmpeg 9 ya no tiene `-filter_complex_script`: se usa `-/filter_complex <archivo>`.
- Whisper por Vulkan: una tarjeta integrada puede devolver texto basura y es mucho más lenta; el motor elige la tarjeta con memoria
  propia, no el índice 0. whisper.cpp no abre un modelo cuya ruta tenga letras fuera de ASCII: FFmpeg arranca en la carpeta del
  modelo y recibe solo el nombre del archivo.
- Smart App Control de Windows puede bloquear al azar una DLL sin firmar recién compilada (visor de eventos,
  `Microsoft-Windows-CodeIntegrity/Operational`, eventos 3077 y 3033): reintenta la compilación.
- Windows empaqueta algunas apps con un `AppData` virtualizado (por ejemplo las de la Tienda): lo que escriben ahí no lo ve el
  resto. Instala, publica y compila fuera de `AppData` si lanzas todo desde una de ellas.
- Office por COM: ver `docs/mandarina/OFFICE.md` (contraseñas, procesos propios, diálogos, sondas de salud).
