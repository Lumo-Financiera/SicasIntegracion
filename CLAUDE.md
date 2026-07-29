# CLAUDE.md — LumoSys Integraciones

## Contexto del proyecto

Integrador ETL que sincroniza datos de pólizas de seguros y siniestros desde SICAS (sistema de aseguradora) hacia `dbLumoSys` (base de datos interna de Lumo) y SFleet (sistema de flotillas).

Fusiona tres proyectos anteriores en uno solo:
- `C:\LumoSysGit\Seguros\Seguros` — ETL de seguros (console, .NET 6, WCF SOAP)
- `C:\LumoSysGit\Siniestros\Siniestros` — ETL de siniestros (console, .NET 6, WCF SOAP)
- `B:\Dessaarollos\API.Lumosys` — solo los módulos Seguros y Siniestros (los demás controllers siguen en esa API)

## Stack

- .NET 9, C# 13
- EF Core 9 con SQL Server
- RestSharp 112 para SICAS REST (reemplaza proxy WCF SOAP)
- FluentFTP 53 para subida de documentos
- Newtonsoft.Json 13 (SICAS puede devolver JSON malformado; hay retry con JsonReaderException)
- Swashbuckle 7 para Swagger
- **Sin autenticación** — servicio interno de red local, sin JWT, sin [Authorize] en ningún controller

## Estructura de capas (Clean Architecture)

```
Domain      — interfaces, entidades, modelos de dominio. NUNCA depende de capas externas.
Application — handlers y commands. Depende de Domain. NO referencia EF Core ni HTTP.
Infrastructure — EF Core, repositorios, clientes externos (SICAS, SFleet, FTP).
API         — controllers (thin), background services, middleware, Program.cs.
```

## Despliegue

- Corre como **servicio de Windows** (`builder.Host.UseWindowsService(opts => opts.ServiceName = "LumoSysIntegraciones")` en `Program.cs`, paquete `Microsoft.Extensions.Hosting.WindowsServices`), no bajo IIS — se eligió así porque la app es principalmente 4 `BackgroundServices` corriendo el ETL programado, y IIS recicla/duerme el App Pool por inactividad salvo config especial ("Always Running Mode" + desactivar idle timeout), lo cual es más frágil que un servicio de Windows standalone.
- `appsettings.json` trae `"Urls": "http://0.0.0.0:5096"` para que el binding no dependa de `--urls`/launch profile al correr como servicio.
- Publish: `dotnet publish -c Release -r win-x64 --self-contained true -o <carpeta>` (self-contained: el servidor no necesita .NET instalado). ⚠️ El publish copia `appsettings.Local.json` (credenciales reales) tal cual a la salida — la carpeta publicada debe tratarse como contenido sensible.
- Registro del servicio (en el servidor, consola admin): `sc create LumoSysIntegraciones binPath= "...\LumoSys.Integraciones.API.exe" start= auto` + `sc failure LumoSysIntegraciones reset= 86400 actions= restart/60000/restart/60000/restart/60000` (reinicio automático ante fallas) + `sc start LumoSysIntegraciones`.
- Servidor destino: `10.100.102.6` (mismo servidor donde ya corre IIS/FTP de LumoSys), carpeta sugerida `C:\LumoSys\Programas\Sicas\API\` (junto al log diario de errores, ver sección correspondiente).
- `appsettings.Local.json` traía una sección `Jwt` sobrante (leftover de un scaffold anterior — el proyecto no tiene autenticación, ver Stack) — removida.

## Configuración de credenciales

**`appsettings.json`** — versionable, solo PLACEHOLDERs. Nunca escribir credenciales aquí.

**`appsettings.Local.json`** — en `.gitignore`, contiene las credenciales reales. Se carga automáticamente por `Program.cs`. Este archivo NO se versiona.

Credenciales reales origen: `C:\Desarrollo\Programas\PROD\Integraciones\appSettings.json` y `B:\Dessaarollos\API.Lumosys\API.LumoSys\appsettings.json`.

## IDs de aplicación (Seguros/Siniestros)

- `Aplicaciones:Seguros` = **11**
- `Aplicaciones:Siniestros` = **12**

(Estos IDs se usan para construir `LER_ORIGEN` en `LOG_ERRORES` — ver sección "dbIntegraciones decomisionado" en "Base de datos" — y en el log diario de archivo.)

- **Bug encontrado y corregido**: `AplicacionOptions` (que expone estos IDs a los handlers vía `IOptions<AplicacionOptions>`) nunca se registraba en el contenedor de DI — no había ningún `services.Configure<AplicacionOptions>(...)` en ningún lado. Esto significa que `IOptions<AplicacionOptions>` siempre resolvía a los valores por default de la clase (`Seguros=5`, `Siniestros=6`), **nunca** a los 11/12 reales de `appsettings`. Todo lo que se había escrito hasta ahora en la bitácora de errores (entonces `dbIntegraciones.Bitacora`, ya decomisionado) quedó con `IdAplicacion=5` (Seguros) o `6` (Siniestros) en vez de 11/12. Corregido agregando `builder.Services.Configure<AplicacionOptions>(opts => cfg.GetSection("Aplicaciones").Bind(opts));` en `Program.cs`. Detectado al verificar el nuevo log diario de archivo (ver sección siguiente) — el archivo mostraba `(Aplicacion 5)` en vez de `(Aplicacion 11)`.

## Secciones de appsettings

```
ConnectionStrings.LumoSys        → dbLumoSys (producción)
ConnectionStrings.Integraciones  → dbIntegraciones
SICAS.BaseUrl                    → https://security-services.sicasonline.info/api
SICAS.Usuario/Contrasena         → autenticación básica (no ApiKey, ver sección SICAS REST)
SFleet.BaseUrl/Email/Password    → https://fleetsoluciones.com/api/v1
Ftp.Host/Usuario/Contrasena      → 10.100.102.6 / Jesus / (en Local.json)
Ftp.RutaDestinoPolizas           → Fleet/Documentos Unidades/2/0/ (⚠️ ver nota abajo — NO es 1/0)
Ftp.RutaDestinoSiniestros        → Seguros/Siniestros/
EtlSchedule.Seguros              → hora diaria del ETL de seguros (default 00:05)
EtlSchedule.Siniestros           → hora diaria del ETL de siniestros (default 00:10)
EtlSchedule.IntervaloMinutosSeguros    → si es >0, corre cada N minutos en vez de 1 vez al día (ver nota abajo)
EtlSchedule.IntervaloMinutosSiniestros → idem, para Siniestros
Aplicaciones.Seguros/Siniestros  → 11 / 12
```

## Convenciones de código

- Los handlers en Application reciben un `*Command` y retornan un `*Result` o `int`.
- Los controllers son thin: reciben HTTP, llaman al handler, retornan `Ok`/`BadRequest`.
- Las interfaces de repositorio viven en Domain; las implementaciones en Infrastructure.
- Los modelos EF Core están en `Infrastructure/Persistence/Models/` con prefijos de columna (`SEG_`, `SIN_`, etc.).

## Base de datos

- `dbLumoSys` — SEGUROS, SEGUROS_DETALLES, SINIESTROS, SINIESTROS_ESTATUS, DOCUMENTOS_SINIESTROS, DOCUMENTOS_UNIDADES, ARCHIVOS_REPOSITORIOS, LOG_ERRORES.
- **`dbIntegraciones` decomisionado (24/07/2026)** — a petición del usuario, el proyecto ya NO se conecta a `dbIntegraciones` en absoluto. Se eliminaron `IntegracionesContext`, `IntegracionesModels.cs`, su registro en DI y la connection string `ConnectionStrings:Integraciones` de ambos `appsettings`. `IBitacoraRepository`/`BitacoraRepository` (mismo nombre de interfaz, para no tocar los call-sites en `ProcesarLoteSeguroHandler`/`ProcesarLoteSiniestroHandler`) ahora escribe en **`dbLumoSys.dbo.LOG_ERRORES`** — la misma tabla de log de errores que ya usa el resto de LumoSys (`slnLumoSys.Controllers.*` y varios `SP_*` como `SP_ACTUALIZAR_CLIENTES_UNIDADES_SFLEET`), confirmada por inspección directa de la tabla y su catálogo `TIPOS_LOG` (`1=CARGA DE LAYOUTS, 2=PROCESO AUTOMATICO, 3=PROCESO MANUAL, 4=REPORTE, 6=TRIGGER, 7=SISTEMA`).
  - Mapeo usado: `LER_ORIGEN = "LumoSys.Integraciones.{Seguros|Siniestros}"` (según `idAplicacion` 11/12), `LER_MENSAJE_ERROR = "[Nivel] descripcion"` (mismo texto que antes iba a `DescripcionBitacora`, ahora con hasta 4000 caracteres en vez de 500), `LER_TLG_ID = 2` (PROCESO AUTOMATICO) **fijo para todo** — asunción no confirmada contra el negocio; si se quiere distinguir barridos automáticos (nocturnos) de reprocesamientos manuales (`Poliza`/`Serie`/`FolioSiniestro`), habría que plomear esa distinción hasta `GuardarAsync` y usar `3` (PROCESO MANUAL) cuando aplique. `LER_NO_ERROR = 0` siempre (se investigó la columna: su significado real varía por origen y no tiene un catálogo propio: la mayoría de los orígenes existentes en la tabla también usan `0`). `LER_REVISADO = false` siempre al insertar (mismo patrón que otros orígenes, presumiblemente hay un flujo operativo en algún UI de LumoSys para marcarlo revisado).
  - `LOG_ERRORES` no tiene triggers (confirmado vía `sys.triggers`), por lo que no necesita `UseSqlOutputClause(false)` en el mapeo EF Core (a diferencia de `SEGUROS`/`SINIESTROS`/etc.).
- Secuencias: SP `SP_ACTUALIZAR_SECUENCIAS` retorna el siguiente ID para `ARCHIVOS_REPOSITORIOS`. **Firma real** (confirmada vía `INFORMATION_SCHEMA.PARAMETERS`, no coincide con nombres "intuitivos"): `@TABLA varchar (IN)`, `@ID int (IN)`, `@FolioSQ int (INOUT)` — el código anterior usaba nombres/tipos inventados (`@Tabla`/`@Numero`/`@Result` con `@Numero` mal tipado) y fallaba con `"expects the parameter '@Numero', which was not supplied"` la primera vez que este camino se ejecutó de verdad (documento real subido). Corregido en `LumoSysContext.ObtenerSecuenciaAsync`.
- **Pendiente/conocido**: si el FTP falla después de haber llamado `RegistrarArchivoAsync`, queda una fila huérfana en `ARCHIVOS_REPOSITORIOS` sin vincular en `DOCUMENTOS_UNIDADES` (el orden actual es registrar-luego-subir). Bajo impacto pero vale la pena invertir el orden (subir primero, registrar después) en una futura pasada. Fila huérfana conocida generada durante la corrección del FTP: `ARC_ID=2466930` (pendiente de limpieza, bajo impacto).
- `DOCUMENTOS_UNIDADES.DUN_CDE_ID` y `DUN_CLI_ID` son **NOT NULL** — a diferencia de `SEGUROS_DETALLES`, esta tabla no tiene fallback para "vehículo externo" (solo en `VEHICULOS`, sin `CDE_ID`); si no hay `COMPRAS_DETALLES`/`COMPRAS` para la serie, `VincularDocumentoUnidadAsync` omite el registro (el archivo ya quedó subido al FTP, solo no se vincula). `DUN_PUBLICO` también es NOT NULL y no tenía columna en el modelo — corregido, se guarda `true` por defecto (asunción, no confirmada contra el legacy).

## ETL — modo de intervalo (cada N minutos) COMPLEMENTA al diario, no lo reemplaza

- `SegurosEtlBackgroundService`/`SiniestrosEtlBackgroundService` (barrido diario amplio, `ayer→hoy`, hora fija `EtlSchedule:Seguros`/`Siniestros`) **corren siempre**, sin condición — son la red de seguridad.
- `SegurosEtlIntervaloBackgroundService`/`SiniestrosEtlIntervaloBackgroundService` son servicios **separados y adicionales**: solo hacen algo si `EtlSchedule:IntervaloMinutosSeguros`/`IntervaloMinutosSiniestros` está configurado (>0); si no, su `ExecuteAsync` retorna de inmediato sin loop. Cuando están activos, corren cada N minutos con una ventana angosta `Desde/Hasta = [ahora - (N + 10 min), ahora]` (10 min de margen fijo, `MargenSeguridadMinutos`, para cubrir drift de reloj/corridas lentas) — para bajar el tiempo de sincronización sin esperar al barrido diario completo.
- **⚠️ Bug real encontrado y corregido (28/07/2026):** la primera implementación hacía que el modo de intervalo **reemplazara** al diario (un solo `if/else` en el mismo servicio). Esto significa que cualquier captura de SICAS ocurrida mientras el servicio angosto no estaba corriendo (o antes de su primer arranque) se perdía **para siempre** — el modo angosto nunca vuelve a mirar más atrás de su propia ventana, a diferencia del diario que siempre revisa el día completo. Confirmado con un caso real: la póliza `L0000019660-0` (`IDDocto=264680`, `FCaptura=27/07/2026 16:33`) nunca apareció en el log de una instancia de prueba corriendo en modo intervalo (cero menciones en ~7h de logs, 22+ ciclos), aunque sí terminó guardada en `dbLumoSys` (`SEG_ID=230883`) por otro proceso. Corregido separando en 2 servicios independientes por módulo: el diario (sin condición) + el de intervalo (opcional, complementario) — ambos registrados como `AddHostedService` en `Program.cs`.
- **Intervalo y ventana son configs independientes a propósito** (`IntervaloMinutosX` = cada cuánto corre, `VentanaMinutosX` = cuánto mira hacia atrás — idea del usuario, mejor que el margen fijo original de 10 min). Si `VentanaMinutosX` no se configura, usa `IntervaloMinutosX + 10` como default (compatibilidad hacia atrás). Con `Ventana > Intervalo` cada corrida se traslapa con las anteriores — ej. intervalo=20/ventana=60 da 3x de traslape, aguanta caídas del servicio de hasta ~40-50 min sin perder nada, sin esperar al barrido diario.
- Habilitado en `appsettings.Local.json` de este entorno de pruebas con `IntervaloMinutosSeguros=20`/`IntervaloMinutosSiniestros=20` y `VentanaMinutosSeguros=60`/`VentanaMinutosSiniestros=60`. En `appsettings.json` (versionado) quedan en `0` por default — hay que activarlos explícitamente por ambiente.

## FTP — carpeta destino de pólizas (1/0 vs 2/0)

- **La ruta correcta para documentos de póliza es `Fleet/Documentos Unidades/2/0/`, NO `1/0`.** El valor `1/0` venía heredado sin cuestionar tanto de `Seguros`/`Siniestros` (legacy) como de `API.LumoSys` (`B:\Dessaarollos\API.Lumosys\API.LumoSys\appsettings.json`, clave `DocumentosDestinoPolizas`) — ambos coinciden en `1/0`, pero **ese valor está desactualizado en las dos fuentes**: la producción real ya no escribe ahí.
- Evidencia (verificada por FTP + `dbLumoSys`, 24/07/2026): el último documento de tipo `POLIZA SEGURO` (`DOCUMENTOS_UNIDADES.DUN_TDW_ID=4`) genuino antes de esta sesión (`ARC_ID 2466241/2466242`, registrado el 22/07/2026, dos días antes) quedó físicamente en `2/0`. `1/0` no recibía archivos genuinos desde el 28/05/2026 — dos meses de silencio hasta que esta sesión subió ahí 2 archivos de prueba por el valor viejo del config (ya movidos a `2/0` vía FTP `RNFR`/`RNTO`).
- **No existe ninguna columna en la BD que determine "1" vs "2"** — se descartó `DUN_TDW_ID`, `COM_EMP_ID` y `COM_CLI_ID` como posible regla (los tres aparecen mezclados en ambas carpetas para el mismo tipo de documento). `ARCHIVOS_REPOSITORIOS` no tiene columna de ruta; `RUTAS`/`TIPOS_REPOSITORIOS` (`REP_ID=34` "Documentos Unidades" → `RUT_ID=3` → `...\Fleet`) solo dan el prefijo base común a ambas carpetas. Es decir, el "2" no es calculable — es simplemente el destino que usa hoy la aplicación real de producción (no identificada en ninguno de los repos fuente accesibles: ni este, ni `API.LumoSys`, ambos siguen diciendo `1/0`).
- Si en el futuro este valor vuelve a parecer "desactualizado", repetir la verificación empírica: listar ambas carpetas por FTP (`curl --list-only`), y cruzar los `ARC_ID` más recientes de cada una contra `DOCUMENTOS_UNIDADES.DUN_TDW_ID=4` en `dbLumoSys` ordenado por `ARC_FECHA_REGISTRO DESC` — la carpeta con la fecha más reciente es la correcta.
- Nota de red: `sqlcmd` desde esta terminal (Git Bash) falla con `Named Pipes Provider: Could not open a connection` contra `10.100.102.7\LUMODBPROD22` — forzar transporte TCP con `-S "tcp:10.100.102.7\LUMODBPROD22"` lo resuelve (el driver intenta Named Pipes primero por defecto y esa ruta de red no está disponible desde aquí).

## FTP — cuenta de servicio

- Host `10.100.102.6`, sitio IIS FTP `FtpSicasDocumentos`, ruta física `C:\inetpub\wwwroot\LumoSys\Content\Documentos`, conecta con autenticación de paso a través (usa las credenciales del propio usuario FTP contra el sistema de archivos).
- La cuenta de Windows `Jesus` (usada por `Ftp.Usuario`/`Ftp.Contrasena` en `appsettings.Local.json`) tenía marcado **"El usuario debe cambiar la contraseña en el siguiente inicio de sesión"**, lo cual bloquea el login FTP con `530 User cannot log in` (sin mensaje de detalle) — no hay forma de cambiar la contraseña vía FTP. Se corrigió desmarcando esa opción y marcando "La contraseña nunca expira" (para que no se repita cuando la contraseña expire).
- Si este error vuelve a aparecer, revisar en este orden: (1) permisos NTFS de la cuenta sobre la ruta física del sitio, (2) reglas de autorización FTP (`Jesus` debe estar en la lista con Leer+Escribir), (3) autenticación FTP habilitada (básica sí, anónima no), (4) estado de la cuenta de Windows (deshabilitada/bloqueada/contraseña por cambiar).

## SICAS REST

- Base URL real: `https://security-services.sicasonline.info/api` (la URL `lumo.intelisiscloud.com:9081` documentada antes era incorrecta/inalcanzable).
- Autenticación **básica** (usuario/contraseña), NO ApiKey: `POST /Security/GetToken?sUserName={usuario}&sPassword={contrasena}` — TTL 3 min, renovar a 2.5 min. El endpoint exige `Content-Length` aunque el body vaya vacío (HTTP 411 si se omite); por eso `SICASRestClient` usa `HttpClient` con `StringContent` vacío en vez de RestSharp para este llamado puntual.
- El token se envía en el header `Authorization` **sin** el prefijo `Bearer`.
- `SICASRestClient` es **singleton** — gestiona el token con `SemaphoreSlim` para thread safety.
- Reportes: `POST /Report/ReadData` — el `KeyCode` va en el header `Prop_KeyCode` (no en el body), y el body son parámetros de formulario (`PageRequested`, `ItemsForPages`, `SortFields`, `Conditions` como string `Letrero;TipoFiltro;SubFiltro;Valores;Texto;PosTitle;ChangeTable;Tabla.Campo` separado por `!` si hay varias condiciones, `FormatResponse=2` para JSON). `SubFiltro`/`PosTitle`/`ChangeTable` son enteros — deben ir `0`/`0`/`-1` si no aplican, **nunca vacíos** (SICAS truena con "La cadena de entrada no tiene el formato correcto" si se manda el string vacío). La respuesta tiene forma `{"Response":[{"<NombreTabla>":{"Data":[...]}}]}` — el nombre de la tabla varía según el KeyCode, por eso se toma la primera propiedad del objeto.
- **⚠️ CRÍTICO — el límite superior (`Hasta`) del filtro de rango (`FilterType=3`) es EXCLUSIVO del día indicado.** Confirmado empíricamente el 28/07/2026: un rango `27/07/2026|28/07/2026` en `HDS00009` (siniestros) devuelve 0 registros capturados el 28/07 — solo trae lo del 27/07. Un rango `28/07/2026|29/07/2026` sí los trae. Esto significa que **cualquier consulta donde `Desde` y `Hasta` sean el mismo día calendario (o donde `Hasta` sea "hoy") siempre regresa 0, sin importar cuántos registros reales existan ese día** — no es que no haya datos, es que el filtro los excluye por diseño. El legacy original de `BuscarBitacoraPorFecha` ya sabía esto: usaba literalmente `"hoy|mañana"` en vez de `"hoy|hoy"`. Corregido (28/07/2026) sumando `hasta.Date.AddDays(1)` al formatear el límite superior en los 3 métodos que usan este filtro: `SICASSeguroClient.BuscarPolizasVigentes`, `SICASSiniestroClient.BuscarSiniestrosVigentes`, `SICASSiniestroClient.BuscarBitacoraPorFecha`.
  - **Impacto real:** el barrido diario original (`ayer→hoy`, corre a las 00:05/00:10) casi no lo sufría — a esa hora "hoy" apenas empieza, casi no hay nada que perder. Pero el modo de intervalo nuevo (corre varias veces al día, ventana `[ahora-N, ahora]` casi siempre dentro del mismo día) quedó **100% no-funcional desde que se implementó** hasta este fix — todas las corridas de intervalo de esta sesión (¿20+ ciclos?) devolvieron "0 registros" no porque no hubiera captura, sino por este bug. Confirmado con 2 casos reales que el usuario reportó manualmente: siniestro `1-205-2026-R-87` (`FCaptura` 28/07/2026 12:33) y `042666518` (mismo día) — ninguno de los dos aparecía con el filtro roto; ambos se procesaron correctamente en cuanto se corrigió.
  - **Nota:** capturas de días *anteriores* (no el día de la consulta) sí se seguían encontrando bien incluso con el bug — ej. `20260000167081` (`FCaptura` 27/07/2026) ya estaba en la base porque el barrido diario sí incluye completo el día de `Desde`.
  - **Validación post-fix (29/07/2026):** se reprocesó manualmente todo el rango `27/07→29/07` para confirmar que nada se perdió por el bug mientras estuvo activo. Seguros: 79 pólizas en SICAS, 71 ya en `dbLumoSys`, 8 correctamente omitidas (`"no pertenece a la flotilla propia"` — series de terceros, no error). Siniestros: 48 en SICAS, 21 ya en `dbLumoSys` (con documentos subidos), 27 correctamente omitidos (mismo motivo). **0 pérdidas reales** — todo lo que pertenece a la flotilla propia quedó migrado.
- **Fechas en `Conditions` (filtros tipo rango, `FilterType=3`): formato `dd/MM/yyyy`, no ISO.** Confirmado contra datos reales de `FCaptura` (`"18/04/2023 18:19:00"`). Con `yyyy-MM-dd` el filtro no truena pero tampoco matchea nada — SICAS regresa `Data:[]` silenciosamente. Este bug afectaba tanto `SICASSeguroClient.BuscarPolizasVigentes` como `SICASSiniestroClient.BuscarSiniestrosVigentes`/`BuscarBitacoraPorFecha`, es decir **el ETL automático nocturno nunca procesaba nada** (solo el reprocesamiento manual por Serie/Poliza/FolioSiniestro funcionaba, porque no usa filtros de fecha). Corregido: `DateFmt = "dd/MM/yyyy"` en ambos clientes.
- **Documentos — usar `POST /DigitalCenter/GetFiles`, NO `/DigitalCenter/GetFilesAdv`.** `GetFilesAdv` depende de una configuración especial por agente/corredor ("C. Digital - Configuración Documentos Link", ver manual "Servicios REST.pdf") que **no está dada de alta para esta licencia** (`wsSicurezza`), y por eso truena con `{"Sucess":false,"Error":"Internal error server. Referencia a objeto no establecida como instancia de un objeto."}` incluso para pólizas con documentos reales confirmados (verificado con la póliza real `L0000019478-0`, IDDocto `263235`). El error NO significa "sin documentos" — esa conclusión de una sesión anterior era incorrecta.
  - Endpoint correcto: `POST /DigitalCenter/GetFiles` con body `{"FolderRead":1, "Identity":"H02"|"H04", "ValuePK":<IDDocto o IDSiniestro>, "TypeReadBasic":true, "ReadRecursive":false, "URLSecurity":0}`. `FolderRead=1` = Storage (Centro Digital); no depende de ninguna configuración especial, solo lee el árbol real de documentos de esa entidad.
  - Respuesta con `TypeReadBasic=true`: `{"Sucess":true,"ListData":[{"FileName":...,"Ext":...,"PathWWW":...}]}` — misma forma que `ArchivoSICAS`, sin cambios de parseo.
  - Implementado en `SICASRestClient.BuscarArchivosDigitales` (antes se llamaba `GetFilesAdv`, mismo nombre engañoso que el endpoint roto — se renombró para evitar confusión).
  - Existe un tercer endpoint, `POST /DigitalCenter/GetFilesByDocto` (`{"TipoEntidad":0,"ValuePK":...,"IncludeCDigital":true}`), pensado para un checklist formal de "Documentación" (Pólizas/Endosos/Recibos/Siniestros) — probado contra la misma póliza real y no encontró nada (`"NO se ubico el documento solicitado"`), así que por ahora no se usa; podría valer la pena revisarlo si `GetFiles` alguna vez no trae algo esperado.
- SICAS puede devolver JSON malformado; `SICASRestClient.ReadData<T>` reintenta hasta 5 veces ante `JsonReaderException`, reparando el mismo contenido (sin volver a golpear la red).
- Credenciales reales: ver `appsettings.Local.json` (`SICAS.Usuario` / `SICAS.Contrasena`).

## KeyCodes SICAS

| KeyCode | Datos |
|---|---|
| H03117 | Pólizas vigentes |
| HWS_DDETAIL | Detalle vehículo |
| H03400 | Primas |
| H03400_019 | Coberturas |
| H03120 | Cobranza |
| HDS00009 | Siniestros |
| H04270_O | Bitácora siniestro por IDSiniestro (⚠️ ver nota abajo — no usable vía REST tal cual) |
| H03314011 | Bitácora siniestro por fecha (sí funciona vía REST) |

## Siniestros — hallazgos de la reconstrucción

- Esquema real confirmado en vivo: `SINIESTROS.SIN_CDE_ID` es **NOT NULL** (a diferencia de Seguros, no hay equivalente a `VHC_ID` — un vehículo "externo" no puede tener siniestro), `SIN_TSI_ID` NOT NULL, `SIN_USU_ID` NOT NULL (fijo en `3`). No existen `SIN_EJECUTIVO`, `SES_FECHA_EVENTO` ni `SES_EJECUTIVO` (columnas inventadas en versiones previas del código, ya removidas).
- Catálogos: `TIPOS_SINIESTROS` (`TSI_ID/TSI_DESCRIPCION`, resuelto desde `CobAfectada` normalizado vía `ObtenerTipoSiniestro`), `TIPOS_ORIGENES` (`TOR_ID/TOR_DESCRIPCION`, siempre resuelve a `"SISTEMA"` — literal fijo del ETL legacy, no viene de SICAS), `TIPOS_ESTATUS` (mismo catálogo físico que Seguros pero filtrado por `TES_TMO_ID=11`).
- `SIN_SEG_ID` se resuelve por `SEG_NO_POLIZA` + `Inciso` (join contra `SEGUROS_DETALLES.SDE_INCISO`, fallback inciso=1, prioriza `SEG_ACTIVO`) — es un campo **obligatorio en la práctica**: si la póliza no existe, se lanza error (no se guarda el siniestro).
- `SES_USU_ID` tiene dos caminos: si el estatus es `"SOLICITUD"`, se resuelve por nombre de ejecutivo contra `USUARIOS` (`NOMBRE+" "+APELLIDO_PATERNO+" "+APELLIDO_MATERNO`, `Contains`); para cualquier otro estatus se usa una tabla fija de traducción `IdUser` (SICAS) → `USU_ID` (LumoSys) llamada `EjecutivoSICAS` en el legacy — no hay catálogo en BD, es un mapeo hardcodeado (ver `SiniestroRepository.EjecutivoSicasMap`), default `416` si no matchea.
- Documentos: identity `"H04"` en `GetFilesAdv`, `ValuePK` = **`IDSiniestro`** (no `IDDocto` — error encontrado y corregido).
- HDS00009 (listado de siniestros) **no trae la serie del vehículo** — sale de una llamada aparte a `HWS_DDETAIL` con el mismo `IDDocto` (mismo patrón que Seguros).
- **⚠️ Limitación conocida sin resolver:** el legacy obtiene el `ClaveBit` (necesario para pedir la bitácora completa de UN siniestro específico vía `H04270_O`) con una operación SOAP distinta (`WS_Siniestros`/`Param6`, parseo de texto pseudo-XML por regex) que no tiene equivalente confirmado en la API REST. Por eso, al guardar/reprocesar un siniestro individual **no se trae su historial completo de estatus** — el `SINIESTROS_ESTATUS` se llena de forma incremental día a día vía la Fase 2 (`ProcesarBitacoraDia`, KeyCode `H03314011`, que sí funciona por REST con un simple filtro de fecha). Si se necesita el historial completo de un siniestro puntual, habría que investigar si existe un KeyCode REST equivalente a `H04270_O` que acepte `IDSiniestro` como condición (los intentos con `Conditions=...;DatBitacora.IDSiniestro` fallan con "nombre de columna no válido").

## Verificación end-to-end (documentos de pólizas)

Corregido y probado dos veces contra SICAS real, con FTP real y BD real:
- Serie `8AFWR5CP7K6127942` / póliza `L0000019478-0` (IDDocto `263235`) → 1 documento real detectado, subido a FTP (`2467002.pdf`) y vinculado en `DOCUMENTOS_UNIDADES`.
- Serie `JS1VU51A5J2101033` / póliza `L0000019601-0` (IDDocto `263270`) → 1 documento real detectado, subido a FTP (`2467014.pdf`) y vinculado en `DOCUMENTOS_UNIDADES`.

Ambos casos confirmados como correctamente escopados (documento vinculado exclusivamente al `CDE_ID` de su propia serie, sin duplicar registros al reprocesar la misma póliza dos veces — `ExisteDocumentoAsync` evita el duplicado).

## Log diario de errores en archivo

Además de `dbIntegraciones.Seguridad.Bitacora`, cada aviso/error que pasa por `IBitacoraRepository.GuardarAsync` (Seguros y Siniestros) y cada excepción no controlada (`ExceptionHandlingMiddleware`) se escribe también en un archivo de texto diario en `C:\LumoSys\Programas\Sicas\Log dd-MM-yyyy.txt` (un archivo por día, se va agregando línea por línea; la carpeta se crea sola si no existe). Mismo patrón que los ETL legacy en `C:\Desarrollo\Programas\PROD\Integraciones\Logs\Seguros|Siniestros\Log dd-MM-yyyy.txt` (que siguen corriendo en producción, en paralelo a este proyecto — ver hallazgo abajo). Implementado en `LogErroresArchivoService` (Infrastructure/Notifications), inyectado como singleton con `SemaphoreSlim` para escritura concurrente segura.

- **Hallazgo relacionado**: el ETL legacy (`C:\Desarrollo\Programas\PROD\Integraciones`) sigue activo y programado en el servidor (genera su log diario incluso en fechas muy recientes), pero la mayoría de sus archivos diarios son mínimos (~45-175 bytes, solo "Inicio de proceso") — consistente con que también sufre el mismo bug de formato de fecha (`dd/MM/yyyy` vs ISO) que se corrigió en este proyecto, es decir, tampoco ha estado procesando nada en el día a día. Los pocos archivos grandes (ej. `Log 22-06-2026.txt`, `Log 30-06-2026.txt`) corresponden a ejecuciones manuales/reprocesamiento puntual.

## Reprocesamiento individual

Si una póliza o siniestro no se procesó correctamente:

```http
POST /api/Etl/Seguros/Procesar   { "Poliza": "20260000144709" }
POST /api/Etl/Seguros/Procesar   { "Serie": "JN8BT27T7MW128187" }
POST /api/Etl/Siniestros/Procesar { "FolioSiniestro": "1-202-2026-R-4295" }
```

## Decisiones tomadas en el diseño

| Decisión | Razón |
|---|---|
| Sin JWT ni autenticación | Servicio interno de red local, no hay clientes externos |
| `appsettings.Local.json` para credenciales | Evita commitear secretos a git accidentalmente |
| `SICASRestClient` singleton | El token tiene ciclo de vida de 3 min; singleton lo gestiona con semáforo |
| FTP con FluentFTP 53 | `System.Net.FtpClient` solo es .NET Framework, incompatible con .NET 9 |
| JToken `.ToObject<int>()` en SFleetClient | `.Value<int>()` sin key causa ambigüedad en Newtonsoft.Json 13 |
| `Microsoft.Extensions.Configuration.Binder` en Infrastructure | Necesario para `.Bind()` en clases de biblioteca (no web) |

## Bugs corregidos respecto a proyectos anteriores

1. `ReasignarEstatus` ahora corre dentro de la transacción EF Core.
2. Descarga de documentos desde SICAS en memoria (sin disco), retorna `null` si falla HTTP.
3. `GuardarSiniestroHandler` lanza excepción real en lugar de devolver `Ok(true)` con error oculto.
4. FTP con FluentFTP (compatible con .NET 9).
5. `SICASRestClient` singleton con renovación proactiva de token (evita expiración durante lote grande).
