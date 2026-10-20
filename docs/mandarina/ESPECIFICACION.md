# Especificación de Mandarina

Qué hace Mandarina y cómo se comporta, descrito desde la propia aplicación. Mandarina nace de [Filee](https://github.com/KnifeLemon/Filee)
(licencia MIT), cuyo motor de conversión usa, y se inspira en la idea de Tangerine para Mac (Leap Studio): una rueda que aparece
junto al cursor mientras arrastras archivos. Es un proyecto independiente y sin relación con ellos; este documento, su código, sus
textos y sus recursos son propios.

## Gesto

- Empiezas a arrastrar uno o varios archivos y tocas la tecla de una rueda. La rueda se abre alrededor del cursor, con los
  pétalos que tienen sentido para lo que llevas.
- Teclas por defecto en Windows: **Shift** abre la rueda de **convertir**, **Ctrl** la de **herramientas** y **Alt** la de
  **acciones**. Se cambian en Ajustes → Atajos, que también deja elegir qué rueda abre cada gesto. Tener la tecla pulsada antes de
  empezar a arrastrar sirve igual, y **Ctrl+Alt+Espacio** abre una rueda para lo que esté seleccionado en el Explorador.
- Tocar otra tecla de rueda cambia de rueda. Al soltar la tecla, la rueda sigue abierta hasta que sueltas los archivos sobre un
  pétalo; **Esc** la cierra sin hacer nada.
- Cada resultado es un archivo nuevo junto al original. Si el nombre ya existe se añade « 2», « 3»… y nada se sobrescribe.

## Las ruedas

- **Convertir.** Cada pétalo es un formato de salida, escrito en mayúsculas. A un archivo nunca se le ofrece su propio formato. El
  hueco central dice el nombre del pétalo señalado o, si no hay ninguno, el tamaño total de lo que arrastras.
- **Herramientas.** Cada pétalo lleva un icono y su nombre. Qué herramientas salen depende del tipo de archivo y de si arrastras uno
  o varios (tabla más abajo). Las que necesitan opciones abren una ventana propia; el resto se ejecuta al soltar.
- **Acciones.** Compartir, Flotar, Copiar, Mover, Copiar ruta y Terminal. Valen para archivos, carpetas y selecciones mixtas.
- **Aspecto.** Un anillo de cristal esmerilado (la pantalla de detrás, desenfocada) con pétalos redondeados de cristal claro. El
  pétalo bajo el puntero se vuelve naranja con la etiqueta en blanco y suena un tic suave (generado por el programa, sin archivos de
  sonido). La rueda se abre con una animación corta que crece desde el centro. Si no hay nada que ofrecer, el centro lo dice y
  recuerda las otras ruedas.
- Las ventanas de las herramientas comparten estilo: marco con degradado de naranja a lila, ✕ redonda arriba a la izquierda,
  tarjetas blancas para las opciones y un botón «Aplicar» abajo a la derecha. El contrato para escribir una está en
  [PANELES.md](PANELES.md).

## Conversiones

Mandarina hereda el grafo de conversión de Filee (cientos de formatos, con rutas de varios pasos) y le pone encima las salidas por
defecto de cada rueda; las salidas exactas están en `src/Filee.Core/Presets/BuiltInData.cs`.

| Archivo de entrada | Pétalos de salida |
|---|---|
| Imágenes (JPG, PNG, WebP, HEIC, TIFF, SVG, AVIF, BMP) | las otras imágenes y PDF |
| Audio (MP3, M4A, WAV, FLAC, OGG, Opus, AIFF, WMA) | los otros formatos de audio |
| Vídeo (MP4, MOV, MKV, WebM, AVI, WMV) | los otros vídeos, GIF y MP3 |
| GIF | vídeo |
| PDF | DOCX, PPTX, JPG, PNG (una imagen por página, a 300 DPI), EPUB, TXT y Markdown |
| Presentaciones (PPT, PPTX) | PDF, JPG y PNG |
| Hojas de cálculo (XLS, XLSX, ODS, CSV, TSV) | las otras hojas y PDF |
| Documentos (DOC, DOCX, RTF, ODT, Markdown) | DOCX, PDF, TXT, Markdown, HTML, EPUB y imágenes |
| EPUB | PDF, DOCX, TXT, Markdown y HTML |
| TXT | DOCX, PDF, Markdown, HTML, EPUB e imágenes |
| Subtítulos (SRT, VTT…) | entre sí, TXT y PDF |
| Archivos comprimidos (ZIP, TAR, GZ, RAR) | ZIP, TAR y TAR.GZ (RAR solo se lee) |

Con Microsoft Office instalado, el motor opcional de Office (ver [OFFICE.md](OFFICE.md)) pasa a PDF y entre formatos antiguos y
modernos los documentos, hojas y presentaciones de Office.

## Herramientas

| Archivo | Uno | Varios |
|---|---|---|
| Foto | Comprimir, Metadatos, Editar, Anotar, Fondo, Recortar, Tapar | Comprimir, Metadatos, Recortar, Crear PDF, Collage |
| Audio | Comprimir, Metadatos, Normalizar, Visualizar, Recortar, Canales, Pitido | Comprimir, Metadatos, Normalizar |
| Vídeo | Comprimir, Metadatos, Silenciar, Recortar, Encuadrar, Velocidad, Capturas, Dividir, Tapar | Comprimir, Metadatos, Silenciar, Unir |
| PDF | Comprimir, Metadatos, Dividir, Organizar | Comprimir, Metadatos, Dividir, Unir PDF |
| GIF | Metadatos | Metadatos |
| ZIP, TAR, GZ, RAR | Extraer | Extraer |

Mandarina añade dos propias, para audio y vídeo, uno o varios archivos: **Transcribir** y **Subtítulos** (más abajo).

**Fotos**
- *Comprimir*: por preajuste, por porcentaje o por tamaño objetivo, con o sin cambio de tamaño.
- *Metadatos*: muestra las etiquetas del archivo (ubicación, cámara, autor, fechas…) y deja cambiarlas o quitarlas sin volver a
  comprimir.
- *Editar*: luz (exposición, contraste, sombras, altas luces…), color, detalle y efectos, con una vista previa que sigue a los
  deslizadores.
- *Anotar*: flechas, líneas, formas, trazo libre, texto y números; siempre guarda PNG.
- *Fondo*: pone la foto sobre un lienzo de color, degradado o imagen, con margen, esquinas redondeadas, sombra y proporción.
- *Recortar*: por proporción o por medidas exactas, moviendo el recuadro sobre la foto.
- *Tapar*: busca caras y texto en local y los cubre con relleno, desenfoque o pixelado; también se marcan zonas a mano. Lo tapado
  no se puede recuperar.
- *Crear PDF*: una página por foto. *Collage*: varias fotos en una, en cuadrícula, fila, columna o con una destacada.

**Audio**
- *Normalizar*: iguala el volumen. *Canales*: mono o estéreo y nivel de cada lado.
- *Recortar*: elige el tramo sobre la onda, con asas, escucha previa y opción de quitar el silencio de los extremos.
- *Pitido*: marca tramos sobre la onda y los sustituye por un tono.
- *Visualizar*: crea un MP4 (horizontal, vertical o cuadrado) que dibuja el audio como onda o como barras, con los colores que elijas.

**Vídeo**
- *Silenciar*; *Recortar* (corta en el fotograma exacto, con vista previa); *Encuadrar* (qué parte de la imagen se queda);
  *Velocidad* (mantiene el tono); *Capturas* (fotogramas a PNG, en una carpeta); *Dividir* (puntos de corte, los clips van a una
  carpeta); *Unir* (varios clips en un MP4, en el orden que elijas); *Tapar* (zonas fijas, o caras y texto detectados).

**PDF**
- *Dividir*: una página por PDF, en una carpeta. *Unir PDF*: varios en uno, en el orden de la selección. *Organizar*: reordenar,
  girar, duplicar y quitar páginas con miniaturas.

**Archivos comprimidos**
- *Extraer*: a una carpeta junto al archivo.

**Comprimir** funciona igual en foto, audio, vídeo y PDF y nunca devuelve algo más grande que el original: si no mejora, lo dice.

### Transcribir y Subtítulos (Whisper, en local)

- *Transcribir*: un clic. Detecta el idioma y guarda «nombre.txt» y «nombre.srt» junto al original, con progreso real.
- *Subtítulos*: elige idioma y formatos (TXT, SRT, VTT) y, en vídeo, puede grabar los subtítulos en la imagen.
- Hay dos modelos de voz, que se descargan desde Ajustes → Motores: uno grande, para PCs con tarjeta gráfica dedicada, y uno pequeño
  (más rápido en el procesador y de menos tamaño) para los que no la tienen. Con tarjeta dedicada se usa el grande y sin ella el
  pequeño; si solo hay uno, ese. El panel dice qué modelo usa y dónde corre, y el procesador usa un hilo por núcleo físico. Nada sale
  del PC. Sin ningún modelo, el pétalo sale en gris y dice cómo instalarlo.
- El modelo pequeño se equivoca más con ruido y palabras raras, y sobre ruido sin voz puede inventar texto; el grande no.

## Acciones (Windows)

- **Compartir**: abre la hoja de Compartir de Windows con los archivos. Como la hoja no admite carpetas, cada carpeta se comprime
  antes a un ZIP temporal (con su nombre; se borra poco después de cerrar la hoja y, si la app cae, al arrancar la siguiente vez).
  Si un archivo de la carpeta no se puede leer, la acción falla con un aviso y no queda nada a medias; la × del anillo cancela.
- **Flotar**: un estante flotante donde dejar archivos para arrastrarlos después a otra aplicación; se puede pegar a un borde.
- **Copiar**: pone los archivos en el Portapapeles. **Copiar ruta**: una ruta por línea.
- **Mover**: pide una carpeta, mueve allí los originales y enseña el destino en el Explorador: si ya hay una ventana con esa
  carpeta, la trae al frente (aunque esté minimizada) en vez de abrir otra. Si la carpeta solo está en una pestaña de detrás, se
  abre una ventana nueva: Mandarina no cambia de pestaña en el Explorador del usuario.
- **Terminal**: abre la carpeta en la terminal.

## Lo que se guarda

- Una copia nueva junto al original con el nombre «nombre sufijo.ext»; el sufijo cuenta qué se hizo («recortado», «comprimido»…).
  Si una herramienta produce muchos archivos (capturas, clips, páginas), van a una carpeta «nombre sufijo».
- El original no se toca ni se sobrescribe nada. Con varios archivos es todo o nada: si uno falla se borra lo escrito en esa
  tanda. Si el trabajo se cancela o falla a medias no queda ningún archivo parcial.
- Todo ocurre en el PC: no hay telemetría ni se sube nada. La única conexión es la descarga de motores y modelos que el usuario
  elige instalar, desde las direcciones fijadas en `engines.json`.

## Decisiones y límites

- **HEIC** (y `.hif`): se lee en todas las herramientas, con teselas, 10 bits y perfil Display P3, pero se guarda como JPG (Magick.NET
  no escribe HEIC) y puede pesar más que el original. Un HEIC con color solo en `nclx` (sin perfil ICC) o en HDR no se convierte
  de color.
- **PDF → XLSX** no se ofrece: ningún método sencillo extrae tablas de un PDF con fiabilidad. La única vía razonable sería la
  función «Obtener datos de PDF» de Excel, por COM y solo en PDF con tablas bien formadas; frágil, por lo que se deja fuera.
- **OCR**: Windows lee con los idiomas de reconocimiento que tenga instalados. Coreano, chino y japonés necesitan su paquete de
  Windows; Mandarina no instala paquetes del sistema.
- **Caras**: el detector de Windows no ve las muy pequeñas (menos de un 2 % del lado largo) ni las de perfil; esas se marcan a mano.
- **Sin autoactualización**: Mandarina no comprueba versiones de Filee ni se sustituye por ellas.
- **Solo Windows** en la interfaz de ruedas; el resto del código de Filee conserva sus capas de Linux y macOS, sin probar aquí.
