# Observabilidad y logs — `SicasIntegracion`

> Trabajo del 25/08/2026. Complementa a `CONTEXTO_INTEGRACION_SICAS.md` y `DIAGNOSTICO_FALLOS_TEAMS.md`.
> Estado: **implementado y compilando; probado en local con SICAS y BD simulados.** No probado aún contra
> producción real.

---

## 1. El problema real

El reporte era "la API no deja registro". La causa no era falta de llamadas a `ILogger` —el proyecto ya tenía
decenas— sino **que no iban a ningún lado**:

| Proveedor | Qué pasaba corriendo como servicio de Windows |
|---|---|
| Console | **No hay consola.** Todo se descartaba. |
| Debug | Solo con depurador conectado. Nada. |
| EventLog | `UseWindowsService()` lo agrega, pero **filtra desde `Warning`**. Todos los `LogInformation` se perdían. |

Resultado: de todo el log del ETL —inicio de lote, páginas, registros procesados, omitidos, documentos subidos—
**no quedaba rastro de nada**. Lo único que persistía era lo que pasaba por `IBitacoraRepository.GuardarAsync`,
que son ~6 puntos del código, todos de error.

Y el archivo diario que sí existía (`LogErroresArchivoService`) recibía exclusivamente esos ~6 puntos, no el
`ILogger`.

---

## 2. Qué se agregó

### 2.1 Un destino real para el `ILogger`

**`ArchivoLoggerProvider`** (`Infrastructure/Notifications/`) — un `ILoggerProvider` propio que manda **todo** el
`ILogger` del proyecto al archivo diario.

Se eligió un proveedor propio en vez de Serilog por tres razones: el proyecto ya tenía su convención de archivo
diario que el equipo conoce y consulta (mismo patrón que los ETL legacy), el publish es *self-contained* y conviene
no sumar dependencias, y así no hay riesgo de restore de NuGet en el servidor.

Detalles de diseño:
- **Alias `Archivo`** → permite afinar niveles por proveedor desde `appsettings`.
- **Escritura en cola** (`Channel` acotado con `DropOldest`): el ETL nunca se frena esperando el disco, y la memoria
  no crece sin límite (relevante tras el incidente OOM de julio). Si el consumidor se atrasa, se pierde log viejo
  antes que tumbar el servicio.
- **Los errores de bitácora se escriben de inmediato**, sin pasar por la cola: son pocos y se quiere garantía de
  que queden en disco aunque el proceso muera.
- **Retención configurable** (90 días por defecto). Antes el historial crecía sin límite en el disco del servidor.
- **Filtra el ruido de ASP.NET** (`TraceId`, `SpanId`, `RequestPath`, `ActionName`…). Sin esto cada línea era tres
  veces más larga que el mensaje real.

### 2.2 Correlación por corrida

**`ResumenLote`** (`Domain/Shared/Models/`) genera un id corto por corrida (`lote=8e5199`) que se abre como scope
de logging. Todas las líneas de esa corrida quedan marcadas con él, así se pueden aislar cuando el barrido diario y
el de intervalo se traslapan en el mismo archivo.

### 2.3 Resumen de lote

Antes el log **nunca decía cuántos registros se procesaron**. Ahora cada corrida cierra con una línea:

```
RESUMEN Siniestros (diario) en 84.3s :: comentarios_leidos=412 comentarios_nuevos=37
  comentarios_sin_vinculo=93 leidos=48 omitidos_flotilla=27 paginas=1 procesados=21
```

Solo aparecen los contadores con valor. Si hubo incidencias, el resumen sube a `WRN` para que salte a la vista.

### 2.4 Supresión de errores repetidos

**`SupresorErroresRepetidos`** — resuelve el bucle documentado en el diagnóstico: 32 pólizas generaron
**5,449 filas en `LOG_ERRORES` en 60 días** (una sola, 3,231), porque el modo intervalo las reprocesa cada ~21 min
y vuelven a fallar por catálogos faltantes.

Reparto nuevo de responsabilidades:
- **Archivo diario** → recibe *todo*, con folio y detalle. Es la fuente para diagnosticar.
- **`LOG_ERRORES`** → recibe la señal, con supresión por ventana (6 h por defecto). Al reabrirse la ventana el
  mensaje informa cuántas repeticiones se callaron, para no perder la magnitud.

Esto importa porque `LOG_ERRORES` es la tabla compartida por **todo LumoSys**: el ruido del ETL le tapaba los
errores a los demás módulos.

---

## 3. Los silencios que se cerraron

Esto es el corazón del trabajo. Había rutas de código que descartaban datos **sin dejar rastro alguno** — la razón
por la que defectos graves pasaron meses sin detectarse.

| Dónde | Qué se perdía en silencio | Ahora |
|---|---|---|
| `ProcesarBitacoraDia` → `BuscarIdPorFolioSicas` null | **Cada comentario de un siniestro con `SIN_FOLIO_SICAS` NULL.** Es el defecto que mantuvo la bitácora vacía: 33,783 siniestros afectados | `WRN` por caso + contador + aviso a `LOG_ERRORES` con los `IDSiniestro` afectados y qué hacer |
| `ProcesarPorReporte` → `resultados[0]` | Las filas 2..N cuando SICAS devuelve varias para un folio | `WRN` listando los `IDSiniestro` descartados |
| `VincularDocumentoUnidadAsync` → `return` | Documentos subidos al FTP que quedaban sin ligar a la unidad | `WRN` explicando que el archivo está en el FTP pero no aparecerá en la ficha |
| `ReadData` → `null` | Un fallo de consulta se veía igual que "0 registros" | `ERR` explícito: *"los registros de esta consulta NO se procesaron"* |
| `EnsureToken` → excepción de red | Stack trace de sockets sin contexto | `ERR`: *"No se pudo contactar a SICAS… Ningun dato se va a sincronizar hasta que responda"* |
| `EjecutarConReintentos` | Caídas de conexión con SICAS (eran `Debug`, apagado en producción) | `WRN` por reintento + `ERR` al agotarlos |
| `GetFiles` | Archivos listados por SICAS sin `PathWWW` (no descargables) | `WRN` con el conteo |
| Fase 2 tras reproceso por folio | Que el reproceso **no actualiza estatus ni comentarios** | `WRN` explícito diciéndolo y qué hacer en su lugar |

---

## 4. Cómo leer el log

Ubicación: `C:\LumoSys\Programas\Sicas\Log dd-MM-yyyy.txt` (un archivo por día).

```
25/08/2026 09:08:01.324 [INF] ProcesarLoteSiniestroHandler {lote=8e5199 modo=folio} Procesando siniestro por folio: 1-202-2026-R-304
└─ fecha y hora ──────┘ └nivel┘ └─ clase que lo emitió ──┘ └── correlación ──┘ └─ mensaje ─┘
```

**Niveles:** `TRC` `DBG` `INF` `WRN` `ERR` `CRI`.
**`modo`:** `diario` · `intervalo` · `rango` · `folio` · `poliza` · `serie`.

### Recetas de diagnóstico

```bash
# Seguir una corrida completa
grep "lote=8e5199" "Log 25-08-2026.txt"

# Solo problemas
grep -E "\[WRN\]|\[ERR\]|\[CRI\]" "Log 25-08-2026.txt"

# Resúmenes del día (¿cuánto se procesó?)
grep "RESUMEN" "Log 25-08-2026.txt"

# Comentarios que no se pudieron ligar (el defecto de SIN_FOLIO_SICAS)
grep "no se pudo ligar" "Log 25-08-2026.txt"

# ¿Está viva la conexión con SICAS?
grep "Token SICAS" "Log 25-08-2026.txt"
```

| Síntoma en el log | Qué significa |
|---|---|
| No aparece `Token SICAS` durante un lote | El ETL está atorado **antes** de SICAS |
| `comentarios_sin_vinculo` > 0 en el resumen | Hay siniestros con `SIN_FOLIO_SICAS` NULL; hay que reprocesarlos |
| `ReadData ... FALLO` | Ese pedazo de datos **no se procesó**; no es "no había registros" |
| `RESUMEN ... sin actividad` | La corrida no encontró nada — revisar arriba si fue por fallo o porque no había datos |
| `docs_sin_vincular` > 0 | Documentos en el FTP que no aparecen en la ficha de la unidad |
| `alcanzó el límite de 30 páginas` | Hay registros sin procesar: partir el rango |

---

## 5. Configuración

Sección nueva en `appsettings.json`:

```json
"LogArchivo": {
  "Carpeta": "C:\\LumoSys\\Programas\\Sicas",
  "RetencionDias": 90,
  "CapacidadCola": 20000,
  "IncluirCategoria": true,
  "SupresionMinutos": 360
}
```

Niveles por proveedor:

```json
"Logging": {
  "Archivo": {
    "LogLevel": { "Default": "Information", "Microsoft": "Warning" }
  }
}
```

Para diagnosticar a fondo, subir `Default` a `Debug`: agrega latencia y conteo de cada llamada a SICAS
(`ReadData {KeyCode} pagina {N}: {Count} registros en {Ms} ms`). **No dejarlo en `Debug` de forma permanente.**

### Log de arranque

Al iniciar, el servicio deja constancia de su configuración efectiva — versión, fecha del binario, BD destino,
URLs de SICAS y SFleet, rutas FTP, horarios y modo intervalo, carpeta y retención del log. **Nunca imprime
credenciales** (solo si están o no configuradas; la cadena de conexión se reduce a servidor/catálogo).

Esto responde de entrada la primera pregunta de cualquier diagnóstico: *¿qué binario corre y contra qué apunta?*
—exactamente lo que no se pudo verificar al analizar el hilo de Teams.

---

## 6. Archivos tocados

**Nuevos**
- `Infrastructure/Notifications/ArchivoLoggerProvider.cs` — proveedor de `ILogger` a archivo
- `Infrastructure/Notifications/LogArchivoOptions.cs` — configuración
- `Infrastructure/Notifications/SupresorErroresRepetidos.cs` — anti-bucle
- `Domain/Shared/Models/ResumenLote.cs` — métricas y correlación

**Modificados**
- `API/Program.cs` — engancha el proveedor, apaga el tracking de Activity, log de arranque
- `API/appsettings.json` — sección `LogArchivo` + niveles del proveedor
- `Infrastructure/Notifications/LogErroresArchivoService.cs` — cola, retención, dos rutas de escritura
- `Infrastructure/Notifications/BitacoraRepository.cs` — supresión
- `Infrastructure/Extensions/InfrastructureServiceExtensions.cs` — nuevo `AddLogArchivo()`
- `Infrastructure/SICAS/SICASRestClient.cs` — latencia, token, errores explícitos
- `Infrastructure/Persistence/Repositories/PolizaRepository.cs` — avisos al no poder vincular
- `Application/.../ProcesarLoteSeguroHandler.cs` — resumen, contadores, SFleet
- `Application/.../ProcesarLoteSiniestroHandler.cs` — resumen, contadores, **los silencios de la Fase 2**
- Los 4 `BackgroundServices` — hora absoluta de la próxima corrida

⚠️ **`AddLogArchivo()` debe llamarse antes de `AddInfrastructure()`**: `BitacoraRepository` depende de
`LogErroresArchivoService`, y el `LoggerFactory` recoge los `ILoggerProvider` del contenedor al inicializarse.

---

## 7. Verificación hecha

- **Compila** con .NET SDK 10 sobre `net9.0`: 0 errores. Las 2 advertencias de nullability son **preexistentes**
  (líneas que no se tocaron: `Poliza = resumen.Documento` y `existente.SDE_INCISO`).
- **Arranque probado** con SICAS y BD apuntando a puertos muertos locales (sin tocar producción ni sistemas de
  terceros). El archivo se crea y se escribe correctamente, con acentos intactos.
- **Correlación y resumen probados** invocando los dos endpoints del ETL: `{lote=8e5199 modo=folio}` y
  `RESUMEN Siniestros (folio) en 2.1s`.
- **Cadena de diagnóstico probada** con SICAS caído — se lee de corrido:
  ```
  [ERR] No se pudo contactar a SICAS ... Ningun dato se va a sincronizar hasta que responda.
  [ERR] ReadData HDS00009: sin token, no se pudo consultar SICAS.
  [WRN] Siniestro 1-202-2026-R-304 no encontrado en SICAS.
  ```

### Lo que falta probar
- **Un lote real** contra SICAS y `dbLumoSys` de producción: es lo único que ejercita los contadores del resumen,
  la Fase 2 y el aviso de `comentarios_sin_vinculo`.
- **La supresión de repetidos** con el bucle real de las 32 pólizas (necesita 6+ h de corrida para cruzar la ventana).
- **La retención**: se ejecuta al arrancar y solo borra archivos con más de 90 días.

### Notas del entorno
- En esta máquina **no hay runtime .NET 9**, solo SDK 10. Para ejecutar en local hace falta
  `DOTNET_ROLL_FORWARD=Major`. En el servidor no aplica: el publish es *self-contained*.
- El repo apunta a `github.com/Lumo-Financiera/SicasIntegracion.git`. **Los cambios están solo en esta copia
  local y sin commitear.**

---

## 8. Pendiente al desplegar

1. **Revisar `LogArchivo:Carpeta`** en el `appsettings.Local.json` del servidor — si no se define, usa el default
   `C:\LumoSys\Programas\Sicas`, que es donde ya escribe hoy.
2. **Vigilar el tamaño del archivo** la primera semana. Con `Information` y el modo intervalo activo el volumen
   sube bastante respecto a hoy (que es casi cero). Si molesta, subir el nivel a `Warning` en la sección `Archivo`
   sin tocar código.
3. **Confirmar la retención**: 90 días de archivos diarios en el disco del servidor.
