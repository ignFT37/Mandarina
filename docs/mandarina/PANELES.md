# Paneles de herramientas de Mandarina (fase 2)

Las herramientas de la rueda que no se pueden lanzar «a ciegas» (recortar, editar, anotar, tapar…) abren una
ventana con sus opciones. Este documento es el contrato para escribir una: el código de referencia es
`src/Filee.App/Mandarina/Tools/TrimAudioPanel.cs` + `TrimAudioView.axaml(.cs)` y sus tests en
`tests/Filee.App.Tests/MandarinaPanelTests.cs`. Léelos antes de empezar.

## Cómo funciona
1. En la rueda de herramientas, un pétalo con `WheelEntry.OpensPanel` (lista en `WheelCatalog.cs`, ya completa para las
   herramientas) aparece solo si hay una clase `ToolPanel` con `[ToolPanel("<id>")]` en el ensamblado
   (`PanelRegistry`, por reflexión). **No hay que registrar nada más.**
2. `RadialController.Invoke` cierra la rueda y llama a `PanelHost.Open(id, ficheros, punto)`, que crea el panel
   (`new TuPanel(PanelContext)`) y su ventana `ToolPanelWindow`: marco degradado naranja → lila, ✕ redondo arriba a la
   izquierda, título centrado (`wheel.<id>`), tus opciones en medio y «Aplicar» abajo a la derecha.
3. «Aplicar» llama a `ToolPanel.RunAsync()`, que ejecuta tu `ApplyAsync`, muestra el progreso y el error o el ✓ con
   el nombre guardado, y cierra la ventana 1,6 s después. Tú solo escribes las opciones y `ApplyAsync`.

## Un panel nuevo
- Archivos (y solo estos): `src/Filee.App/Mandarina/Tools/<Nombre>Panel.cs`, `<Nombre>View.axaml` +
  `<Nombre>View.axaml.cs` (o una vista en código), controles propios de esa herramienta en `Tools/` también, y el test
  `tests/Filee.App.Tests/Mandarina<Nombre>Tests.cs`.
- Clase: `[ToolPanel("trimAudio", NeedsFfmpeg = true)] public sealed partial class TrimAudioPanel : ToolPanel` con un
  constructor `(PanelContext context) : base(context)`. `NeedsFfmpeg = true` si ejecuta FFmpeg (el pétalo sale gris
  mientras falte) y `NeedsWhisper = true` si necesita algún modelo de voz (Ajustes → Motores; sale en gris solo si no hay ninguno). El `id` es el de la herramienta y ya
  tiene título y sufijo en i18n (`wheel.<id>`, `wheel.<id>.suffix`); los dos propios de Mandarina (`transcribe`,
  `subtitles`) los tienen también.
- `AutoRun => true` (virtual) para una herramienta sin opciones que tarda lo bastante como para enseñar progreso: la ventana
  empieza a aplicar al abrirse (ejemplo: `Tools/TranscribePanel.cs`).
- Estado: `[ObservableProperty]` de CommunityToolkit (la clase es `partial`); si cambia algo que afecta a si se puede
  aplicar, llama a `NotifyCanApplyChanged()` (p. ej. en `partial void OnXChanged`).
- `CreateView()` devuelve el control con `DataContext = this`. `PreferredSize` es el tamaño del contenido de la ventana
  (el marco añade 36 px); el test de render **comprueba que las opciones no quedan cortadas** (`ContentRoom`).
- `CanApply`: false mientras no haya nada válido que guardar (el botón se desactiva).
- `HasUnsavedWork` (virtual, `false` por defecto): true cuando cerrar ahora tiraría algo que el usuario hizo a mano (marcas, zonas tapadas,
  páginas reordenadas, un recorte, ajustes…). Con Esc o ✕ la ventana pregunta «Cambios sin guardar» (Seguir editando / Cerrar sin guardar);
  una ventana con el ✓ (ya guardado) nunca pregunta. Impleméntalo solo si el trabajo cuesta rehacerlo: un panel de solo opciones (comprimir,
  velocidad…) no pregunta. Alt+F4 y la barra de tareas cierran sin preguntar (Avalonia no distingue `Close()` de un cierre del usuario).
- `ApplyAsync(progress, cancel)`: hace el trabajo y devuelve los archivos escritos. Corre en el hilo de la interfaz:
  el trabajo pesado va en `Task.Run` o en procesos (FFmpeg). Informa `progress` de 0 a 1. Para fallar, lanza
  `PanelException("mensaje corto y legible")` (se muestra tal cual). Los fallos de FFmpeg (`MediaTools`) y de los compresores ya llevan
  una clave de i18n (`PanelException.Key`/`Args`, causas en `PanelErrors`: archivo dañado, sin espacio, códec no admitido, sin permiso, no
  existe, sin audio ni vídeo, sin memoria) y salen en el idioma del usuario, con el texto original de FFmpeg en el registro; lo que no se
  reconoce sale tal cual. Si muestras tú un fallo (un `LoadError`, un aviso), usa `Context.Explain(ex)` (traduce y registra), no `ex.Message`.
- Cargas en segundo plano (miniaturas, onda, detección): `_ = LoadAsync()` desde el constructor **capturando toda
  excepción** y dejándola en una propiedad que la vista muestra; si no, el panel se queda «cargando» para siempre.
  Cancela con un `CancellationTokenSource` que se cancela en `Dispose()`.
- Vistas previas que se recalculan al mover un control: un `CancellationTokenSource` por solicitud (cancela la
  anterior) y un pequeño retardo (~60 ms) antes de calcular; trabaja sobre una copia reducida (≤ 1200 px) y solo la
  aplicación final usa la resolución completa.

## Qué hay ya hecho (no lo reescribas)
- `PanelContext` (`context`): `Files`, `Loc`, `Platform`, `context["clave"]`, `Format(clave, args)`,
  `OutputFor(origen, "<id>", ".ext"?)` → ruta libre «nombre sufijo.ext» junto al original (« 2», « 3» si existe),
  `FolderFor(origen, "<id>")` → carpeta libre «nombre sufijo» (no se crea), `Media` / `RequireMedia()` (FFmpeg).
- `MediaTools` (FFmpeg): `ProbeAsync(archivo)` → `MediaProbe` (duración, vídeo/audio, ancho × alto tal como se ve,
  fps, canales, códecs, etiquetas), `RunAsync(args, duraciónSalida, progress, cancel)` (añade `-y`, progreso por
  `-progress`, lanza `PanelException` con el mensaje de FFmpeg si falla), `PeaksAsync` (onda), `FrameAsync(archivo,
  segundos, anchoMax)` → `Bitmap`, `PreviewWavAsync`, `Seconds(double)`, `FilterPath(ruta)` (para rutas dentro de
  filtros), `Summarize`. Ffprobe: `media.Ffprobe`.
- `PanelImages` (Magick.NET): `Read(ruta)` (derecha, primer fotograma), `LoadAsync(ruta, ladoMax)` → `PreviewImage`
  (`Bitmap` para mostrar + píxeles originales), `Fit`, `ToBitmap(MagickImage)`, `OutputExtension(origen)` (HEIC → .jpg:
  Magick.NET no escribe HEIC), `Save(imagen, ruta)` (JPG/WebP a calidad 92).
- `PanelFormat`: `Time(seg)` «1:05.30», `TryParseTime`, `Size(bytes)`. `OutputPaths.Temp(".ext")` da un temporal;
  bórralo tú. `OutputPaths.BesideAll(origen, "", ".txt", ".srt")` da varias rutas con el mismo nombre libre («clip.txt» y
  «clip.srt», o «clip 2.txt» y «clip 2.srt» si alguna existe).
- Controles: `WaveformView` (onda con asas, marcas y cursor), `SliderRow` (etiqueta + deslizador + valor; doble toque en
  la etiqueta restablece): `<p:SliderRow Label="…" Minimum="-100" Maximum="100" Value="{Binding Exposure}" />`.
- Estilos (en `PanelStyles.axaml`, ya incluidos por la ventana): `Border.pcard` (tarjeta blanca para un grupo de
  opciones; el texto dentro va en tinta oscura), `Border.pwell` (hueco claro para una vista previa),
  `TextBlock.plabel` (etiqueta gris pequeña), `TextBlock.pvalue` (valor en negrita), `TextBlock.pwhite` (texto blanco
  directamente sobre el degradado), `Button.pchip` / `Button.pchip.primary` (píldora gris / naranja),
  `ToggleButton.pchip` (naranja si está activado). Naranja de énfasis `#FF5200`, tinta `#2B2430`. Los iconos son
  `PathIcon` con geometría propia. Estructura habitual: una o varias `Border.pcard` apiladas en un `StackPanel`.
  La ventana fuerza el tema claro: los controles de Fluent (TextBox, ComboBox, Slider, CheckBox…) ya se ven bien
  dentro de una tarjeta.

## Reglas de lo que se guarda
- Una copia nueva **junto al original**, «nombre sufijo.ext» con `context.OutputFor(...)`; nunca se toca el original
  y nunca se sobrescribe nada.
- Varios archivos: se procesan todos y, si uno falla, se borran las salidas escritas en esta ejecución y se lanza
  `PanelException` (todo o nada). Con varios archivos el progreso es `(i + parcial) / n`.
- Si el trabajo falla o se cancela a medias, borra el archivo parcial (`try/catch` + `File.Delete`).
- Las herramientas que producen muchos archivos (capturas, clips, páginas) los guardan en una carpeta
  `context.FolderFor(origen, "<id>")` y devuelven la carpeta como salida (un solo elemento).
- Todo local: sin red, sin descargas, sin telemetría.
- Magick.NET: no escribe HEIC; los motores incluidos son FFmpeg (`MediaTools`), Magick.NET, PdfSharp y PDFtoImage.

## Textos (i18n)
- Los títulos (`wheel.<id>`) y sufijos (`wheel.<id>.suffix`) ya existen en los cuatro idiomas. Tus textos van como
  `panel.<id>.<algo>` en **en, es, ko y zh-CN** (traducciones naturales y breves; en español de tú, como el resto).
- No edites los JSON a mano: escribe `i18n-spec-<nombre>.json` en la raíz del repo, con la forma
  `{"panel.<id>.<algo>": {"en": "…", "es": "…", "ko": "…", "zh-CN": "…"}}`, y aplícalo con
  `python tools/mandarina/i18n_tool.py add . i18n-spec-<nombre>.json`
  (añade las claves que falten y deja los JSON válidos y alineados; repetirlo es inofensivo; borra el spec cuando lo hayas
  aplicado). Los marcadores
  `{0}`, `{1}` deben coincidir en los cuatro idiomas. Todo `{DynamicResource clave}` de un `.axaml` debe existir en
  `en.json` (un test lo comprueba). En código: `context["clave"]`, `context.Format("clave", args)`.

## Pruebas
- Un archivo `tests/Filee.App.Tests/Mandarina<Nombre>Tests.cs` por herramienta (clase `public sealed class
  Mandarina<Nombre>Tests : IDisposable`, un directorio temporal propio y `[AvaloniaFact]`).
- Ayudas en `MandarinaTestKit` (`using static Filee.App.Tests.MandarinaTestKit;`): `Context(archivos)`,
  `WaitAsync(() => condición)` (deja correr a los `await` del panel), `Render(panel, "nombre")` (guarda
  `mandarina-panel-nombre.png` en la carpeta `screenshots` junto a los binarios del test y comprueba que nada queda
  cortado), `MakeMediaAsync(ruta, args de ffmpeg…)` (genera un archivo real con FFmpeg; `null` si falta FFmpeg →
  `Assert.Skip`), `MakePhoto`, `MakePdf`.
- Cada panel necesita, como mínimo: (1) un test de **render** a PNG con datos reales (míralo: `Read` del PNG) y
  (2) un test de **aplicar** con el motor de verdad (FFmpeg / Magick.NET / PdfSharp) que compruebe el nombre
  «nombre sufijo.ext», que el original no cambia y que el contenido es el esperado (duración con
  `MediaTools.ProbeAsync`, tamaño de la imagen con Magick.NET, nº de páginas con PdfSharp…), más los casos límite
  de la herramienta (varios archivos, valores extremos, archivo sin audio o sin vídeo, nombre ya existente).
  Los motores de FFmpeg y 7-Zip están junto a los binarios de test (`engines\`, un enlace que crea el build).
- `TestServices.EnsureInitialized("es")` antes de tocar el localizador (lo hace `Context`). Solo aplica el idioma y el tema
  si no están ya puestos: cambiarlos publica unos mil recursos, cada uno avisa a todos los controles suscritos (también los de
  ventanas ya cerradas que aún no ha recogido el GC) y hecho en cada test volvía lenta la suite entera.
- Cada `Render` cierra su ventana y libera el fotograma capturado (memoria nativa): hazlo también en tus propios helpers. Una
  ventana que un test deja abierta, o un fotograma sin liberar, se paga en todos los tests siguientes del proceso.
- Añade tu id a la lista `Panels` de `MandarinaMemoryTests`: su ventana tiene que poder recogerse al cerrarse.

## Compilar y probar
`Directory.Build.props` manda `bin` y `obj` a `%USERPROFILE%\.mandarina\build\<proyecto>`, fuera del repo. Si trabajas con varias
copias a la vez, da a cada una su propia raíz con `-p:MandarinaBuildRoot=<carpeta>/` (con barra final). Desde Git Bash, escribe
las rutas con barras normales:

```bash
dotnet build src/Filee.App/Filee.App.csproj -nologo -v q -nodeReuse:false 2>&1 | grep -E " error |rror\(s\)"
dotnet test tests/Filee.App.Tests -nologo -nodeReuse:false --filter "FullyQualifiedName~Mandarina<Nombre>" 2>&1 | tail -30
```
- Los mensajes del compilador salen en el idioma del sistema. `grep` recorta el ruido: si hay errores, mira el resto de la salida.
- Mientras desarrollas, ejecuta tus tests y `FullyQualifiedName~MandarinaPanel` (el marco). La suite completa tarda ~13 min.
- Los PNG de los renders quedan en `<raíz de build>/Filee.App.Tests/bin/Debug/net10.0/screenshots/`.
- Los archivos con barras invertidas (rutas de Windows en C#) escríbelos con un editor, no con `echo` ni heredoc de Bash: la
  barra invertida se pierde.
- Un cambio en lo compartido (`Panels/`, `WheelCatalog.cs`, los `.csproj`) va aparte de la herramienta nueva y se explica en el
  commit.

## Estilo del código
- Inglés en código y comentarios; los comentarios explican el porqué, breves, como los de `TrimAudioPanel`.
- C# moderno con `Nullable`, `ImplicitUsings`, llaves y nombres como el resto de Filee; sin `#region`; sin
  dependencias nuevas (los paquetes están fijados en `Directory.Packages.props`).
- Un archivo propio por herramienta; si dos herramientas comparten un control (p. ej. recorte de imagen y de vídeo),
  el control va en su propio archivo.

## Trampas conocidas
- Avalonia 12.1: `WindowDecorations` (no `SystemDecorations`), `PlaceholderText` (no `Watermark`), enlaces compilados
  (`x:DataType` obligatorio en cada vista y plantilla), `Border` no tiene `Foreground` (usa `TextElement.Foreground`).
- `[ObservableProperty]` genera la propiedad `Nombre` desde el campo `_nombre`; los métodos parciales
  `partial void OnNombreChanged(T value)` reaccionan al cambio.
- **`new DispatcherTimer(intervalo, prioridad, callback)` ARRANCA el temporizador** (así lo dice su documentación): el despachador
  lo retiene para siempre, y con él la vista, la ventana y el panel (y sigue disparando para nadie). Crea el temporizador con
  `new DispatcherTimer(prioridad) { Interval = … }`, ponle el `Tick` y llama a `Start()` solo mientras haga falta; páralo en
  `Dispose()` / al salir del árbol visual. Lo pilla `MandarinaMemoryTests` (cada panel y su ventana deben poder recogerse:
  añade tu id a su lista `Panels`) y `tools/mandarina/soak` (una ventana real, con medida de memoria, GDI y USER).
- Un cliente de UI Automation (lector de pantalla, script de pruebas) que haya leído una ventana retiene todo su árbol aunque
  se cierre: Avalonia no desconecta sus nodos. `ToolPanelWindow` ya lo suelta al cerrar (`AccessibilityRelease`); no dejes en
  el panel nada que dependa de que lo recojan antes de cerrar.
- Un `Process` de FFmpeg no se lanza a mano: `MediaTools.RunAsync` (salvo para leer binario por stdout, como
  `PeaksAsync`). Los argumentos van como lista, sin comillas manuales.
- Los filtros de FFmpeg usan «:» y «,» como separadores: escapa lo necesario o escribe el grafo en un fichero temporal y
  pásalo con `-/filter_complex <archivo>` (el FFmpeg incluido es el 9.0.2: `-filter_complex_script` ya no existe); para rutas
  dentro de un filtro, `MediaTools.FilterPath`.
- Guardar fotos: `PanelImages.Save` ya quita las copias de la foto original que una cámara esconde en los metadatos (miniatura
  EXIF, vista previa de la nota del fabricante, miniaturas XMP) y conserva el perfil de color y el EXIF de la cámara; no escribas
  fotos con otra ruta que no pase por ahí.
- Magick.NET 14 / Avalonia 12 marcan como obsoletos `Clone(IMagickGeometry)`, `ColorProfile.SRGB` y `Bitmap.Save(string)`: usa
  `CloneArea(x, y, ancho, alto)`, `ColorProfiles.SRGB` y `Save(stream, new PngBitmapEncoderOptions())`.
- Los `Slider` de Fluent ocupan 50 px por fila: `p:SliderRow` ya los compacta (24 px); no uses márgenes negativos.
- Nombres de tipos auxiliares: con prefijo propio de la herramienta (`Crop…`, `Redact…`, `Organize…`), para no chocar al fusionar.
- `LocalDetector.FindTextAsync(png)` / `FindFacesAsync(png)` (OCR y caras de Windows, en local) están disponibles para cualquier panel.
- Los errores de ImageMagick (una foto rota o que no es lo que dice) no se muestran con `ex.Message`: `PanelErrors.Describe` los traduce
  («El archivo está dañado…», `panel.error.damaged`) y deja el texto original en el registro; `PanelErrors.FromPicture` añade el nombre.
- Para saber si una foto sale como JPG por ser HEIC, `PanelImages.IsHeif(extensión)` (cuenta `.heic`, `.heif` y `.hif`), no una comparación a mano.
- Las imágenes con rotación EXIF: `PanelImages.Read` ya las endereza. Los vídeos de móvil con rotación: `MediaProbe`
  ya da ancho × alto tal como se ven, y FFmpeg endereza solo al decodificar.
- Un `[AvaloniaFact]` con `await` sigue en el hilo de la interfaz: usa `MandarinaTestKit.WaitAsync` para esperar al
  panel, no `Thread.Sleep`.
