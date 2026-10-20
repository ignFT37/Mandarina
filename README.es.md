<p align="center">
  <img src="src/Filee.App/Assets/Icons/filee.svg" alt="Mandarina" width="96">
</p>

<h1 align="center">Mandarina</h1>

<p align="center">
  <b>Convierte y retoca archivos arrastrándolos.</b><br>
  Empieza a arrastrar archivos, toca una tecla y se abre una rueda junto al cursor.
</p>

<p align="center"><a href="README.md">English</a> · <b>Español</b></p>

> Mandarina está inspirada en Tangerine para Mac (Leap Studio); proyecto independiente, sin relación con ellos: su código, sus
> textos y sus recursos son propios. Se apoya en [Filee](https://github.com/KnifeLemon/Filee) (MIT), cuyo motor de conversión usa.

## Cómo funciona

Arrastra uno o varios archivos desde el Explorador (o desde donde sea) y toca una tecla. La rueda aparece alrededor del cursor.

| Tecla | Rueda | Qué ofrece |
|---|---|---|
| **Shift** | Convertir | Los formatos a los que pueden pasar los archivos |
| **Ctrl** | Herramientas | Comprimir, recortar, tapar, unir… (según el tipo de archivo) |
| **Alt** | Acciones | Compartir, flotar (un estante del que arrastrar después), copiar, mover, copiar ruta, terminal |

Suelta los archivos sobre un pétalo. El resultado es un archivo nuevo junto al original (`foto recortado.jpg`): los originales no se
tocan y nada se sobrescribe. Las teclas y las ruedas se cambian en Ajustes → Atajos, y **Ctrl+Alt+Espacio** abre una rueda para lo
que esté seleccionado en el Explorador.

## Qué hace

- **Convierte** cientos de formatos (imágenes, audio, vídeo, PDF, documentos de Office, libros electrónicos, archivos comprimidos…)
  con los motores de Filee, sin conexión. Las rutas de varios pasos se encuentran solas.
- **Herramientas con su propia ventana**, que guardan una copia junto al original:
  fotos (comprimir, metadatos, editar, anotar, fondo, recortar, tapar, crear PDF, collage), audio (normalizar, recortar, canales,
  pitido, visualizar), vídeo (silenciar, recortar, encuadrar, velocidad, capturas, dividir, unir, tapar), PDF (dividir, unir,
  organizar) y archivos comprimidos (extraer). Tapar busca caras y texto en el propio PC, con los reconocedores de Windows.
- **Transcribe y subtitula** audio y vídeo con Whisper, en local: en la tarjeta gráfica si hay una dedicada, y en el procesador si
  no.
- **Usa Microsoft Office si lo tienes** (opcional, con interruptor): Word, Excel y PowerPoint convierten documentos por COM, sin
  tocar el Office que tengas abierto.
- **Se queda en tu PC.** No sube nada, no envía telemetría ni comprueba actualizaciones. Solo se conecta para descargar los motores
  y modelos que elijas instalar, desde las direcciones oficiales fijadas (con SHA-256) en
  [`engines.json`](src/Filee.Engines/Infrastructure/engines.json).

Qué hace cada rueda y cada herramienta, con detalle: [docs/mandarina/ESPECIFICACION.md](docs/mandarina/ESPECIFICACION.md).

## Estado

Solo Windows 10/11, y joven: habrá asperezas. Todavía no hay versiones compiladas; se construye desde el código.

## Compilar e instalar

Necesitas Git, el [SDK de .NET 10](https://dotnet.microsoft.com/download) y PowerShell 7 (`pwsh`).

```powershell
git clone https://github.com/ignFT37/Mandarina.git
cd Mandarina
pwsh build/fetch-engines.ps1 -Only rhwp,7zip     # los dos motores que van dentro del programa
pwsh tools/mandarina/publish-mandarina.ps1       # la app y el comando `mandarina`, en %USERPROFILE%\.mandarina\publish\win-x64
powershell -NoProfile -ExecutionPolicy Bypass -File tools/mandarina/install-mandarina.ps1 -AddCliToPath
```

Al arrancar por primera vez, Mandarina pregunta qué motores opcionales descargar (FFmpeg para audio y vídeo, Pandoc para formatos
de texto, un modelo de Whisper para transcribir…). Para ejecutarla sin instalar: `dotnet run --project src/Filee.App`.

## Línea de comandos

```
mandarina convert foto.heic --to jpg
mandarina convert *.png --to webp -o convertidos
```

`mandarina help` enseña todas las opciones (es la línea de comandos de Filee con otro nombre).

## Desarrollo

```powershell
dotnet build Filee.slnx
dotnet test            # unos 13 minutos; los tests que necesitan ventanas reales, Office o descargas grandes son opcionales
```

Empieza por [CLAUDE.md](CLAUDE.md) y [CONTRIBUTING.md](CONTRIBUTING.md). Documentación: la de [Mandarina](docs/mandarina/) (el contrato de
los paneles, el motor de Office, la especificación) y la de Filee: [arquitectura](docs/ARCHITECTURE.md),
[motores](docs/ENGINES.md) y [cómo añadir un conversor](docs/ADDING-A-CONVERTER.md) (en inglés).

## Licencia y créditos

MIT, © 2026 Ignacio Insua (Mandarina) y © 2026 Myeonggu Kim (KnifeLemon) y colaboradores de Filee; ver [LICENSE](LICENSE). Los
componentes de terceros conservan sus licencias: [THIRD-PARTY-NOTICES.md](THIRD-PARTY-NOTICES.md). Tangerine y sus autores no tienen
ninguna relación con este proyecto.

<!-- sponsors: src/Filee.App/Assets/Sponsors/sponsors.json -->
