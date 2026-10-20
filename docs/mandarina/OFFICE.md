# Motor de Microsoft Office

Convierte con el Word, Excel y PowerPoint que haya instalados en el PC, por COM con enlace tardío (`Word.Application`,
`Excel.Application`, `PowerPoint.Application`; sin ensamblados de interoperabilidad ni `dynamic`). Es una excepción
deliberada de Mandarina a la regla de Filee de que los motores nunca usan software del sistema: es opcional, sale siempre
en Ajustes → Motores («Microsoft Office», instalado o no), tiene su interruptor y solo añade rutas cuando Office está y
funciona. Cada cambio dentro de un archivo de Filee lleva un comentario «Mandarina:».

## Qué convierte

| Aplicación | Pares | Coste |
|---|---|---|
| Word | DOC, DOT, DOCX, DOCM, DOTX, DOTM, RTF, ODT → PDF; DOC, DOT → DOCX; DOCX → DOC | 10 |
| Word | PDF → DOCX con el reflujo de Word (`DirectOnly`) | 10 |
| Excel | XLS, XLSX, XLSM, XLT, XLTX, ODS → PDF; XLSX → XLS | 10 |
| Excel | XLS → XLSX (conserva gráficos, imágenes y formatos condicionales que el lector interno pierde) | 5 |
| PowerPoint | PPT, PPTX, PPTM, PPSX, POTX, ODP → PDF | 0 |
| PowerPoint | PPT → PPTX, PPTX → PPT | 10 |

- Las imágenes de las diapositivas salen del PDF (PDFium a 300 DPI), como en el resto de rutas: no hay un PPT → PNG propio.
- CSV, TXT, HTML y Markdown se quedan con los motores internos: ya los hacen bien y con todas las hojas.
- PDF/A: Word y PowerPoint lo hacen (`UseISO19005_1`); Excel no tiene esa opción y falla con un mensaje claro.
- Los costes ganan a la ruta interna (HWPX + rhwp, unos 31) y al «respaldo» de LibreOffice (25); el motor se registra
  antes que LibreOffice, así que un empate (diapositivas → PDF, coste 0 en ambos) es de Office. Además se suma la
  penalización 0–9 de su sitio en Ajustes → Motores (lo que no está en la lista va al final: sin migración de ajustes).
- **Todas las aristas son `FirstStepOnly`** (nueva marca de `ConversionEdge`, junto a `DirectOnly`, que viene de la rama
  `feature/pdf-to-pptx` de Filee): Office convierte archivos que SON documentos de Word, Excel o PowerPoint. Sin eso, donde falta
  rhwp la ruta más barata a PDF para un Markdown era «→ DOCX → Word» y para un DOCX con Word roto era
  «→ Markdown → diapositivas → PowerPoint»: un PDF con aspecto de presentación.

## Cómo trabaja con Office (y qué nunca hace)

- **Copia privada** (`OfficeFiles.PrivateCopy`): Office nunca abre el archivo del usuario sino una copia hecha solo con sus
  bytes, en una carpeta propia del trabajo y con el mismo nombre. Así no lleva la marca de Internet (nada de Vista protegida),
  da igual que el usuario lo tenga abierto en su Word y PowerPoint no puede devolver «su» presentación abierta para cerrarla.
- **Sin ventana, sin avisos, sin macros**: `Visible:=False` / `WithWindow:=False`, solo lectura, `DisplayAlerts` apagado,
  `AutomationSecurity` en «forzar desactivar» (no corre ni `AutoOpen` ni `Workbook_Open`), vínculos sin actualizar. No se tocan
  las opciones de Office (`Application.Options` se guarda en el perfil del usuario).
- **Contraseñas**: un paquete OOXML cifrado (contenedor OLE con `EncryptedPackage`) se rechaza antes de abrir (`office.error.password`);
  los antiguos (DOC, XLS) se abren con una contraseña que no puede ser buena, y Office falla de inmediato en vez de pedirla.
  Excel exige **15 caracteres como máximo** en la de escritura (`WriteResPassword`): una más larga hace que no abra NADA (error 1004).
  Nunca se pide ni se guarda una contraseña.
- **Un hilo STA por aplicación** (`OfficeSession`): se crea con el primer trabajo y acaba tras 30 s sin trabajo (se cierra Office).
  Word, Excel y PowerPoint trabajan a la vez; dos Word se turnan.
- **El Office del usuario no se toca.** Tras crear la aplicación se compara la lista de procesos de antes y después: un proceso nuevo
  con `-Embedding` en su línea de comandos es NUESTRO (se cierra con `Quit` y, si no acaba, se mata por id y hora de inicio, nunca por
  nombre); si no aparece ninguno, la aplicación ya estaba abierta (PowerPoint es de instancia única) y es COMPARTIDA: se usa sin
  ventana, solo se cierra nuestra copia y nunca se hace `Quit` ni `Kill`; lo que un trabajo cambia (alertas, seguridad) se deja como
  estaba. Los procesos de Office que lanza Mandarina van a un objeto de trabajo (job) que Windows cierra con Mandarina, también si
  se cae.
- **Nunca colgado** (`OfficeSession.Supervise`): pasado el tiempo del trabajo (150 s), o cuando NUESTRO Office enseña varios segundos
  seguidos un diálogo (`#32770`, `NUIDialog`…) que lo bloquea (modal: sin ventana propietaria o con la propietaria desactivada; la
  ventana de progreso de una exportación larga no cuenta), el trabajo falla con un mensaje (`office.error.timeout`, o
  `office.error.dialog` con el título de la ventana) y se mata nuestro proceso, que también libera la llamada bloqueada. Con Office
  compartido solo caduca el trabajo: nunca se toca el diálogo de otro. Una llamada que Office rechaza por ocupado se repite hasta 10 s.
- **Prueba de salud** (`OfficeSelfTest`): antes de la primera conversión con una aplicación se convierte a PDF un archivo mínimo
  propio (25 s como mucho). Si Office se cuelga o falla, esa aplicación sale de las rutas (`Edges` se recalcula en cada plan) en vez de
  hacer fallar cada archivo tras la misma espera. Un resultado «funciona» vale una hora en memoria; uno «roto», diez minutos, y se guarda
  un día en `office-self-test.json` (carpeta de datos) solo si Windows dice que Office no está activado. Caduca al cambiar la versión de
  Office o su licencia; «Comprobar de nuevo» en Motores lo olvida todo.
- **Licencia**: `OfficeNative.QueryOfficeLicense` pregunta a la plataforma de protección de software de Windows (`slc.dll`,
  lo mismo que `OSPP.VBS /dstatus`). «Sin activar» (solo licencias en estado de notificación) se enseña en la tarjeta, no bloquea.

## Ajustes

- `AppSettings.OfficeEnabled` (por defecto sí): interruptor de la tarjeta «Convertir con Microsoft Office». `App.ApplySettings` y
  `CliHost` lo copian a `EngineEnvironment.OfficeEnabled`, que en código vale **no** hasta entonces: así ninguna prueba depende de
  si la máquina tiene Office.
- La tarjeta dice «Instalado en el PC», «Instalado, sin activar», «Desactivado» o «No instalado»; sin Office no hay interruptor.
- Errores con texto en los cuatro idiomas: `office.error.*` (`ConversionFailureException` lleva la clave; `JobQueue` la pone como
  `ErrorKey` del archivo).

## Pruebas

- Siempre (sin Office): `OfficeEngineTests` de Engines.Tests (rutas y costes con una instalación falsa, marcas `FirstStepOnly` y
  `DirectOnly`, interruptor, licencia, prueba de salud y su memoria, contraseñas, copia privada, procesos y objeto de trabajo) y
  `MandarinaOfficeEngineTests` de App.Tests (la tarjeta en los cuatro idiomas y su captura `mandarina-engines-office-*.png`).
- Con Office real: `MANDARINA_OFFICE_TESTS=1` (abren Office oculto; se saltan si ya hay uno abierto del tipo que usan). Cubren Excel,
  PowerPoint y Word de verdad (PDF, DOC ↔ DOCX, PPT ↔ PPTX, el reflujo PDF → DOCX, PDF/A), el Office del usuario que sigue abierto con
  su archivo, y un trabajo que caduca (se mata solo lo nuestro). Con `MANDARINA_UI_TESTS=1` además: el diálogo de Word «Abrir» que
  se ve en pantalla un momento (el vigilante lo detecta, mata nuestro Word y la sesión se recupera) y una ventana de mensaje.

## Lo que se aprendió

- Un Office **sin activar** (licencia de volumen cuyo servidor de activación falla, modo de funcionalidad reducida) convierte aun así
  las tres aplicaciones. Con sondas de PowerShell lanzadas desde algunos entornos aislados Word se cuelga al exportar (Excel y
  PowerPoint no); desde C# no. Para probar COM usa pruebas de C#, no scripts de PowerShell.
- Medido con una primera instancia creada por automatización: una segunda creación de Word o de Excel arranca un proceso NUEVO; la de
  PowerPoint devuelve el que ya hay (compartido). Por eso el motor comprueba los procesos nuevos en vez de suponerlo.
- `Dialogs.Item` de Word es un método (`CallChild`), no una propiedad.
- `AssignProcessToJobObject` falla para un proceso que ya está en otro objeto de trabajo (los hijos de una ejecución de pruebas dentro
  de algunos entornos aislados); el Office que arranca COM no está en ninguno.

## Límites

- Visio y Project no se usan. No hay CSV, TXT ni HTML con Office.
- Un DOC, XLS o PPT con contraseña de apertura lo dice Office (Word y Excel con la contraseña falsa; el `.ppt` antiguo cifrado enseña un
  diálogo y lo corta el vigilante tras unos segundos).
- Los vínculos relativos de un documento (imágenes enlazadas, `INCLUDETEXT`) pueden romperse al abrir la copia en otra carpeta.
- PowerPoint compartido (el del usuario): si tiene un diálogo abierto, el trabajo espera hasta 10 s y falla con `office.error.busy`.
