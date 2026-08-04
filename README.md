# LumoSys Integraciones — Seguros & Siniestros

Proyecto unificado que fusiona tres proyectos anteriores:

| Proyecto anterior | Tecnología | Rol |
|---|---|---|
| `Seguros` (console) | .NET 6, WCF SOAP | ETL SICAS → LumoSys + SFleet |
| `Siniestros` (console) | .NET 6, WCF SOAP | ETL SICAS → LumoSys |
| `API.LumoSys` (módulos Seguros/Siniestros) | .NET 6, ASP.NET Core | Receptor HTTP → dbLumoSys |

## Tecnologías

- **.NET 9** — framework objetivo
- **ASP.NET Core** — API REST + Background Services (también corre como servicio de Windows, ver "Despliegue")
- **EF Core 9** — acceso a `dbLumoSys` (única base de datos del proyecto — `dbIntegraciones` fue decomisionado, ver `CLAUDE.md`)
- **RestSharp** — cliente SICAS REST (reemplaza proxy WCF)
- **FluentFTP** — subida de documentos al servidor FTP
- **Sin autenticación** — servicio interno de red local, sin JWT ni `[Authorize]`
- **Swashbuckle** — documentación Swagger

## Arquitectura

```
src/
├── Domain/          ← Entidades, interfaces, modelos (sin dependencias)
├── Application/     ← Casos de uso, handlers, comandos
├── Infrastructure/  ← EF Core, SICAS REST, SFleet, FTP, repositorios
└── API/             ← Controllers, BackgroundServices, Program.cs
```

## Configuración inicial

1. Copiar `src/API/appsettings.json` y reemplazar todos los valores `PLACEHOLDER`.
2. Completar credenciales:
   - `ConnectionStrings:LumoSys`
   - `SICAS:Usuario` / `SICAS:Contrasena` — autenticación básica REST (no ApiKey)
   - `SFleet:Email` / `SFleet:Password`
   - `Ftp:Usuario` / `Ftp:Contrasena` — cuenta de servicio Windows con acceso al sitio FTP

## Ejecución

```bash
cd src/API
dotnet run
```

Swagger disponible en `http://localhost:{puerto}/` al correr en Development.

## ETL — Modos de ejecución

### Automático — dos capas que se complementan

**Diario amplio** (siempre activo, es la red de seguridad — revisa `ayer→hoy` completo):
- **Seguros**: hora configurada en `EtlSchedule:Seguros` (por defecto `00:05`)
- **Siniestros**: hora configurada en `EtlSchedule:Siniestros` (por defecto `00:10`)

**Intervalo** (opcional, para no esperar hasta la noche — datos actualizados cada N minutos durante el día):
- Se activa configurando `EtlSchedule:IntervaloMinutosSeguros`/`IntervaloMinutosSiniestros` (minutos, ej. `20`) — en `0` o ausente, esta capa no hace nada.
- `EtlSchedule:VentanaMinutosSeguros`/`VentanaMinutosSiniestros` controla cuánto mira hacia atrás cada corrida (independiente del intervalo — con ventana mayor al intervalo hay traslape entre corridas, lo que da margen ante caídas cortas del servicio sin perder información).
- Ejemplo probado: intervalo `20` min / ventana `60` min → 3x de traslape, tolera caídas de hasta ~40-50 min sin dejar huecos.
- No reemplaza al barrido diario — corren en paralelo, ambos con upsert (reprocesar el mismo rango dos veces no duplica nada).

### Manual por rango de fechas
```http
POST /api/Etl/Seguros/Procesar
{ "Desde": "2026-07-01", "Hasta": "2026-07-22" }
```

⚠️ Para rangos históricos largos (varias semanas), dividir en sub-rangos semanales en vez de mandar todo de una sola llamada — un rango muy largo puede tardar más que el timeout del cliente HTTP que lo invoque, y si el cliente cierra la conexión el proceso se corta a medias en el servidor (ver `CLAUDE.md`, sección "Fase 2").

### Reprocesar póliza individual
```http
POST /api/Etl/Seguros/Procesar
{ "Poliza": "20260000144709" }
```

### Reprocesar por número de serie (VIN)
```http
POST /api/Etl/Seguros/Procesar
{ "Serie": "JN8BT27T7MW128187" }
```

### Reprocesar siniestro individual
```http
POST /api/Etl/Siniestros/Procesar
{ "FolioSiniestro": "1-202-2026-R-4295" }
```

## Documentos digitales (Seguros/Siniestros)

El integrador recupera documentos desde el Centro Digital de SICAS (`POST /DigitalCenter/GetFiles`) y los sube por FTP:

- **Seguros**: carpeta destino `Fleet/Documentos Unidades/2/0/` → físicamente `C:\inetpub\wwwroot\LumoSys\Content\Documentos\Fleet\Documentos Unidades\2\0\` en el servidor FTP (verificado contra la actividad real de producción — ver `CLAUDE.md`).
- **Siniestros**: carpeta destino `Seguros/Siniestros/` → físicamente `C:\inetpub\wwwroot\LumoSys\Content\Documentos\Seguros\Siniestros\`.

Cada archivo se guarda con el nombre `{ARC_ID}.pdf` (ID real de `ARCHIVOS_REPOSITORIOS`); el nombre original se conserva en la base de datos (`ARC_NOMBRE_ARCHIVO`). Ver `CLAUDE.md` (sección "SICAS REST") para el detalle de por qué se usa `GetFiles` y no `GetFilesAdv`.

## Endpoints de la API

| Método | Ruta | Descripción |
|---|---|---|
| `POST` | `/api/Seguros/Guardar` | Guarda/actualiza una póliza |
| `POST` | `/api/Seguros/SubirPoliza/{serie}` | Sube documento de póliza |
| `GET` | `/api/Seguros/BuscarPolizaCargada` | Verifica si ya existe documento |
| `POST` | `/api/Siniestros/Guardar` | Guarda/actualiza un siniestro |
| `POST` | `/api/Siniestros/GuardarComentario` | Agrega comentario/estatus |
| `POST` | `/api/Siniestros/SubirDocumentos/{reporte}` | Sube documento de siniestro |
| `GET` | `/api/Siniestros/BuscarSiniestroCargado` | Verifica si ya existe siniestro |
| `POST` | `/api/Etl/Seguros/Procesar` | Dispara ETL de seguros |
| `POST` | `/api/Etl/Siniestros/Procesar` | Dispara ETL de siniestros |

## Despliegue en producción (servicio de Windows)

La app corre como servicio de Windows continuo (no bajo IIS — evita el idle-timeout/reciclado de App Pool, que interrumpiría los `BackgroundServices` del ETL programado).

1. **Publicar** (self-contained, no requiere .NET instalado en el servidor):
   ```bash
   cd src/API
   dotnet publish -c Release -r win-x64 --self-contained true -o C:\Publish\LumoSysIntegraciones
   ```
   ⚠️ El publish copia `appsettings.Local.json` (con credenciales reales) a la salida — tratar esa carpeta como contenido sensible.

2. **Copiar** la carpeta publicada al servidor (ej. `C:\LumoSys\Programas\Sicas\API\`).

3. **Registrar el servicio** (consola de administrador, en el servidor):
   ```
   sc create LumoSysIntegraciones binPath= "C:\LumoSys\Programas\Sicas\API\LumoSys.Integraciones.API.exe" start= auto DisplayName= "LumoSys Integraciones"
   sc failure LumoSysIntegraciones reset= 86400 actions= restart/60000/restart/60000/restart/60000
   sc start LumoSysIntegraciones
   ```

4. **Verificar**: `sc query LumoSysIntegraciones` → `RUNNING`, y revisar `C:\LumoSys\Programas\Sicas\Log dd-MM-yyyy.txt` (log diario de avisos/errores) o `LOG_ERRORES` en `dbLumoSys`.

## Mejoras respecto a los proyectos anteriores

- Eliminado round-trip HTTP innecesario (ETL ya no hace POST a la API propia)
- Token SICAS con renovación automática a los 2.5 minutos (ciclo de 3 min)
- Documentos procesados en memoria, sin escrituras en disco
- `ReasignarEstatus` ejecutado dentro de transacción
- Errores en guardado de siniestros retornan HTTP 500 real (no `Estatus:true` con error oculto)
- Retry configurable por póliza, serie VIN o folio de siniestro
- Recuperación de documentos digitales corregida: `GetFiles` en vez de `GetFilesAdv` (este último depende de una configuración por agente/corredor no dada de alta en esta licencia y fallaba silenciosamente)
- `SP_ACTUALIZAR_SECUENCIAS` invocado con la firma real de parámetros (verificada contra `INFORMATION_SCHEMA.PARAMETERS`)
- Bug crítico corregido en los filtros de fecha de SICAS: el límite superior (`Hasta`) es exclusivo del día indicado — un rango con `Desde`/`Hasta` en el mismo día calendario (como usa el modo de intervalo) siempre regresaba 0 registros aunque hubiera datos reales. Corregido sumando 1 día al límite superior antes de mandarlo a SICAS.
- `dbIntegraciones` decomisionado — el log de errores ahora escribe en `LOG_ERRORES` de `dbLumoSys` (misma tabla que usa el resto de LumoSys), más un log diario redundante en archivo de texto (`C:\LumoSys\Programas\Sicas\Log dd-MM-yyyy.txt`)
- Modo de intervalo opcional para actualizar datos cada N minutos durante el día, en vez de esperar al barrido nocturno completo
- Corre como servicio de Windows con reinicio automático ante fallas
- Bug crítico corregido en Fase 2 de Siniestros (bitácora de comentarios): nunca guardaba ningún comentario porque el campo usado para vincularlo con su siniestro (`NumReporte`) no existe en la respuesta real de SICAS — se corrigió vinculando por `IDSiniestro`/`SIN_FOLIO_SICAS` (ver `CLAUDE.md`)
