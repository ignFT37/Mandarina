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

Solo Windows 10/11, y joven: habrá asperezas. Las versiones compiladas no están firmadas (ver Instalar); también puedes construirla desde el código.

## Instalar

Descarga **`Mandarina-<versión>-win-x64-setup.exe`** (instalador, solo para ti, sin permisos de administrador) o
**`Mandarina-<versión>-win-x64.zip`** (portátil: descomprímelo donde quieras y abre `Mandarina.exe`) de la
[última versión](https://github.com/ignFT37/Mandarina/releases). Windows 10/11 de 64 bits; no hace falta instalar nada más. El instalador crea el acceso directo en
Inicio y, si dejas la casilla marcada, añade el comando `mandarina` al PATH.

Comprueba la descarga con `SHA256SUMS.txt`, de la misma versión (lee antes el aviso de descargas sin firmar de más abajo):

```powershell
Get-FileHash .\Mandarina-0.1.0-win-x64-setup.exe -Algorithm SHA256
```

> **Las descargas no están firmadas.** Todavía no hay certificado: el gratuito de [SignPath Foundation](https://signpath.org) se ha
> denegado por ahora, hasta que el proyecto sea más conocido, y Mandarina se firmará cuando se pueda (ver la
> [Code signing policy](#code-signing-policy-política-de-firma-de-código)). Lo que verás:
> - *SmartScreen* («Windows protegió su PC»): pulsa **Más información → Ejecutar de todas formas**. Tu navegador también puede avisar
>   al descargar el archivo. Con el zip, haz clic derecho → Propiedades → **Desbloquear** antes de extraer; si no, el aviso se repite
>   en cada `.exe` y `.dll`.
> - *Smart App Control*, si está activado, no deja que Windows abra Mandarina, ni siquiera una compilada por ti desde el código. Para
>   usarla hay que desactivar Smart App Control (Seguridad de Windows → Control de aplicaciones y navegador → Smart App Control).
>   Esa decisión es tuya.
>
> **Comprueba lo que has descargado:** compara su SHA-256 (`Get-FileHash <archivo> -Algorithm SHA256`) con `SHA256SUMS.txt`, de la
> misma versión. Las versiones las compila GitHub Actions desde el código de este repositorio. Mandarina se ofrece tal cual, bajo la
> licencia MIT, sin garantía.

FFmpeg, Pandoc y los modelos de Whisper no van incluidos: Mandarina descarga los que elijas en Ajustes → Motores.
Las licencias de todo lo que lleva la descarga están en `THIRD-PARTY-NOTICES.md`.

## Compilar desde el código

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

## Code signing policy (política de firma de código)

La firma de código gratuita de [SignPath Foundation](https://signpath.org) se solicitó para las versiones de Windows y se ha denegado
por ahora: su programa gratuito es para proyectos que ya tienen visibilidad pública (estrellas, forks, colaboradores, artículos o
actividad sostenida). Se puede volver a pedir cuando Mandarina sea más conocida; por ahora no se prevé una firma de pago. **Hasta
entonces las descargas van sin firmar** (ver [Instalar](#instalar)).
<!-- Si SignPath la aprueba más adelante, sustituye el párrafo de arriba por: Free code signing provided by [SignPath.io](https://about.signpath.io), certificate by [SignPath Foundation](https://signpath.org) -->

Lo que cubrirá esta política cuando haya firma:

- **Qué se firmará:** solo el instalador y los programas y bibliotecas que el [flujo de publicación](.github/workflows/release.yml) de
  este repositorio compila desde su código (`Mandarina.exe`, la línea de comandos `mandarina` y las DLL propias de Mandarina). Lo de
  terceros que viaja en la descarga (7-Zip, rhwp, bibliotecas de .NET) no lo firma este proyecto.
- **Roles:** autor, revisor y aprobador son Ignacio Insua ([@ignFT37](https://github.com/ignFT37)), que también aprueba cada
  solicitud de firma.
- **Cambios en el sistema:** el instalador pregunta, con las dos casillas marcadas por defecto, si Mandarina arranca con Windows y si
  añade «Convertir con Mandarina» al menú contextual y a «Enviar a». Los dos se cambian después en Ajustes → General, y el
  desinstalador quita todo lo que añadió.

### Privacidad

«This program will not transfer any information to other networked systems unless specifically requested by the user or the person installing or operating it»
(este programa no enviará información a otros sistemas en red salvo que lo pida el usuario o quien lo instala o maneja).

Mandarina solo se conecta para descargar los motores y modelos que elijas instalar (FFmpeg, Pandoc, modelos de Whisper…), desde las
direcciones fijadas con SHA-256 en [`engines.json`](src/Filee.Engines/Infrastructure/engines.json). No envía telemetría, no comprueba
actualizaciones y no sube ninguno de tus archivos: las conversiones, la transcripción y la detección de texto y caras se hacen en tu
PC. Los motores que descarga son programas aparte que se ejecutan en local; tampoco se les pide que envíen nada.

## Licencia y créditos

MIT, © 2026 Ignacio Insua (Mandarina) y © 2026 Myeonggu Kim (KnifeLemon) y colaboradores de Filee; ver [LICENSE](LICENSE). Los
componentes de terceros conservan sus licencias: [THIRD-PARTY-NOTICES.md](THIRD-PARTY-NOTICES.md). Tangerine y sus autores no tienen
ninguna relación con este proyecto.

<!-- sponsors: src/Filee.App/Assets/Sponsors/sponsors.json -->
