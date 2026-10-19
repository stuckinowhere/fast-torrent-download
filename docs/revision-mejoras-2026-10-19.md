# Revisión de mejoras de Fast Torrent Download

Fecha: 19 de octubre de 2026. Código revisado: `main`, commit `c864153` (v0.3.9).

Recomendación: corregir primero la fiabilidad de la cola, la respuesta de las acciones y las métricas. Después añadir diagnóstico de velocidad y aclarar la experiencia al completar una descarga.

## Alcance y validación

Revisión de los servicios, ViewModels, XAML, ajustes y pruebas existentes. Las 22 comprobaciones pasan con `dotnet run --project tests/FastTorrentDownload.Tests/FastTorrentDownload.Tests.csproj --configuration Release`.

Durante esta revisión no apareció una ventana de Fast Torrent Download en el inventario de ventanas accesibles. Los hallazgos siguientes proceden del código, no de una verificación visual de la aplicación instalada. No se ha determinado la causa de la lentitud del torrent de la consulta anterior. Los checks actuales no descartan los problemas descritos aquí.

## 1. Alta prioridad: conservar correctamente la cola y sus destinos

**Hallazgos confirmados en código.**

- `CommitAddAsync` incorpora el torrent a la cola, pero no guarda el estado del motor. El guardado ocurre al eliminar un torrent o salir correctamente. Un cierre forzado antes de esos puntos puede perder una incorporación reciente.
- Al restaurar, se asigna el texto `Restored download` como destino en lugar de recuperar la ruta real del manager. `Show folder` interpreta ese texto como una ruta y normalmente informa de que la carpeta no está disponible.

**Cambio propuesto:** guardar la cola confirmada al añadir un torrent, reutilizando el guardado atómico que ya existe; restaurar el destino desde la ruta real del manager. Mantener excluidas las incorporaciones pendientes de confirmación.

**Comprobación:** añadir una descarga y restaurar desde disco sin ejecutar el cierre normal; verificar que sigue presente, conserva sus archivos seleccionados y abre la misma carpeta. Incluir una incorporación pendiente para comprobar que no se restaura.

**Referencias:** `Services/TorrentEngineService.cs:58`, `:342`, `:441`, `:569`, `:586`; `ViewModels/MainViewModel.cs:140`.

## 2. Alta prioridad: Pausar y Eliminar deben mostrar un estado de operación claro

**Hallazgos confirmados en código.** `PauseAsync` y `RemoveAsync` llaman a `StopAsync()` sin limitar explícitamente la espera de los trackers. La finalización automática espera esa eliminación dentro de `GetSnapshotsAsync`; si tarda, también retrasa la actualización de las filas y los totales. La barra de herramientas no deshabilita acciones incompatibles durante una operación.

**Cambio propuesto:** usar el timeout de anuncio de trackers de MonoTorrent, conservando el cierre seguro de archivos; mostrar «Deteniendo…» en la fila afectada, deshabilitar acciones incompatibles y mantener visibles los cambios de las demás descargas. La salida ya utiliza un límite de dos segundos que sirve como patrón existente.

El mensaje de la barra inferior añadido en v0.3.9 es útil, pero no sustituye un estado por torrent cuando hay varias operaciones.

**Comprobación:** un tracker que no responda, una descarga real en curso y varios clics seguidos. Verificar la espera acotada del anuncio, la integridad de los archivos y la actualización del resto de la cola. El timeout del tracker no implica que todas las operaciones de disco deban terminar en ese mismo plazo.

**Referencias:** `Services/TorrentEngineService.cs:412`, `:426`, `:471`, `:568`; `Views/MainWindow.axaml:46`; `ViewModels/MainViewModel.cs:100`, `:212`.

## 3. Alta prioridad: mostrar progreso y velocidades precisos

**Hallazgos confirmados en código.**

- La retirada automática usa `PartialProgress`, pero la fila recibe `Progress`. Si se descarga solo una parte del torrent, la barra representa el torrent completo mientras la finalización representa los archivos seleccionados.
- `TorrentRowViewModel.FormatBytes` realiza divisiones enteras. Por ejemplo, 1536 B/s se presenta como 1,0 KB/s en lugar de 1,5 KiB/s. El selector de archivos ya dispone de una conversión con decimales y unidades binarias coherentes.
- Los bytes recibidos proceden del monitor de transferencias y no expresan necesariamente los bytes verificados de los archivos seleccionados. Conviene distinguir lo recibido en la sesión del avance real de la descarga.

**Cambio propuesto:** mostrar progreso y tamaño de la selección, corregir el formateo y añadir «descargado / total seleccionado» y tiempo restante estimado. Mostrar el tiempo restante como desconocido cuando no haya velocidad suficiente para estimarlo.

**Comprobación:** torrent de varios archivos con una selección parcial, velocidad de 1536 B/s y restauración de una descarga ya avanzada. La barra debe corresponder a lo que realmente se ha solicitado descargar.

**Referencias:** `Services/TorrentEngineService.cs:477`, `:483`, `:492`; `ViewModels/TorrentRowViewModel.cs:20`, `:43`; `Views/SelectTorrentFilesWindow.axaml.cs:22`.

## 4. Prioridad media: diagnóstico de velocidad accesible desde la descarga

**Mejora funcional.** La fila muestra velocidad y peers, pero no permite saber si faltan fuentes útiles, fallan trackers, los peers no envían datos o hay un límite configurado. Los logs de arranque tampoco describen la salud actual del torrent. Un error al consultar peers se representa como cero peers, sin distinguirlo de una consulta válida sin resultados.

**Cambio propuesto:** un panel de detalles por torrent con peers conocidos, conectados y transfiriendo; seeders conectados; disponibilidad de piezas; estado y último error de cada tracker; estado actual de DHT; límites efectivos y errores de disco. Etiquetar los recuentos como conexiones observadas, no como el total de fuentes del swarm.

Empezar con los datos que ya expone MonoTorrent. No aumentar a ciegas los límites de conexiones: el proyecto ya configura 500 conexiones globales, 100 por torrent y 50 intentos simultáneos. Esos valores no garantizan mayor velocidad.

**Comprobación:** distinguir en la UI una descarga con tracker fallido, otra sin fuentes conectadas y otra limitada por configuración. Mostrar una causa solo cuando los datos la respalden; en otro caso, mostrar los indicadores disponibles.

**Referencias:** `Services/TorrentEngineService.cs:54`, `:508`; `Services/EnginePerformanceTuning.cs:9`; `ViewModels/TorrentRowViewModel.cs:22`; `Views/MainWindow.axaml:64`.

## 5. Prioridad media: hacer coherentes la finalización, los ajustes y los mensajes

**Hallazgo confirmado en código:** los ajustes prometen pausar al alcanzar un ratio o seguir compartiendo si se introduce cero, pero el refresco retira los torrents completados antes de aplicar la política de ratio. Esas opciones no pueden ofrecer el comportamiento anunciado con la retirada automática actual.

**Cambio propuesto:** conservar la eliminación automática solicitada como comportamiento por defecto y retirar o deshabilitar los ajustes de ratio que no aplican. Si más adelante se desea compartir tras completar, presentarlo como una opción explícita compatible con la política elegida.

**Mejoras de UI relacionadas:** mostrar una notificación breve de finalización con «Abrir carpeta» y permitir consultar mensajes anteriores. Los textos persistentes de «Removed…» en la barra inferior han generado dudas; la UI debe distinguir una operación en curso de una confirmación ya terminada. Deshabilitar acciones que requieren selección cuando no exista ninguna.

**Comprobación:** completar una descarga con el comportamiento por defecto y comprobar que desaparece, conserva sus archivos y ofrece acceso a la carpeta. Los ajustes visibles deben describir únicamente comportamientos que la aplicación pueda ejecutar.

**Referencias:** `Services/TorrentEngineService.cs:477`, `:501`, `:530`; `Views/SettingsWindow.axaml:48`; `Views/MainWindow.axaml:46`, `:77`.

## Orden sugerido

1. Persistencia al añadir y recuperación del destino real.
2. Operaciones de pausa/eliminación con espera acotada y estado visible por fila.
3. Progreso de archivos seleccionados y formateo de velocidades.
4. Panel de diagnóstico para investigar descargas lentas.
5. Ajustes de finalización coherentes y confirmaciones más claras.

Esta revisión documenta propuestas; no modifica el comportamiento de la aplicación ni publica una release.

## Implementación posterior: v0.4.0

Los cinco bloques se implementaron en `codex/prioritized-client-improvements`, conservando la retirada automática y los archivos descargados.

- Persistencia inmediata de incorporaciones, destinos reales y pausa recuperable desde el estado guardado.
- Espera acotada al detener trackers, estado de operación por fila y retirada automática en segundo plano. Un fallo de persistencia conserva el torrent para poder reintentar.
- Progreso y bytes verificados de la selección, unidades binarias con decimales y tiempo restante cuando es estimable.
- Detalles con métricas de peers, piezas, trackers, DHT, límites y errores. Las observaciones no se presentan como una causa de lentitud demostrada.
- Avisos de finalización con acceso a carpeta, actividad de sesión y retirada de los ajustes de ratio incompatibles.

Validación: 26 checks, incluidas transferencias por loopback con tracker que no responde; pausa, reanudación, eliminación manual y automática; integridad de archivos; persistencia y fallo de guardado. Verificación nativa con cola aislada: selección y acciones, diagnóstico, ajustes, finalización y carpeta, historial, expiración, temas claro/oscuro y tamaño mínimo de escritorio.
