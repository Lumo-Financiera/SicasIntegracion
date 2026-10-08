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
- Sentry 6.10 para monitoreo/APM (`Sentry.AspNetCore` en API, `Sentry` en Infrastructure) — ver seccion "Monitoreo con Sentry"
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

**`appsettings.Local.json`** — en `.gitignore`, contiene las credenciales reales. Se carga automáticamente por `Program.cs`. Este archivo NO se versiona. **Confirmado que `dotnet publish` sí lo copia a la carpeta de salida**, así que poner las credenciales aquí no obliga a editar el `appsettings.json` del servidor.

- ⚠️ **11/09/2026**: se detectaron credenciales reales (SQL, SICAS, SFleet, FTP) escritas directamente en `appsettings.json`, que sí se versiona — a un `git commit` de quedar en el historial para siempre. Se movieron a `appsettings.Local.json` y el versionado volvió a `PLACEHOLDER`. Si el objetivo era desplegar, el camino correcto es `appsettings.Local.json`: el publish lo copia igual y git no lo ve.

- **`appsettings.json` pasó a estar en `.gitignore`** (11/09/2026), por ser el archivo que la app lee de verdad y, por tanto, donde acaban las credenciales cuando alguien las escribe a mano en el servidor. La plantilla con la lista completa de claves y sus `PLACEHOLDER` es **`appsettings.example.json`**, que sí se versiona y está excluida del publish (`Content Remove` en el csproj) para no confundirla con el archivo real en la carpeta de despliegue.
  - ⚠️ **La regla de `.gitignore` no basta por sí sola**: `appsettings.json` ya estaba rastreado y git sigue versionando lo que ya tiene en el índice. Para que surta efecto hay que sacarlo una vez con `git rm --cached src/API/appsettings.json` (no borra el archivo del disco) y commitear esa eliminación. Mientras eso no se haga, el archivo se sigue versionando con normalidad.
  - Al clonar el repo hay que copiar `appsettings.example.json` a `appsettings.json` y llenar los valores, o la app arranca sin configuración base (URLs, `Aplicaciones` 11/12, `EtlSchedule`, DSN de Sentry).

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
Sentry.Dsn                       → DSN del proyecto en Sentry (vacio = monitoreo deshabilitado)
Sentry.TracesSampleRate          → muestreo de APM (0.2 en appsettings.json, 0.8 en Development)
Sentry.Environment/Release       → opcionales; si faltan se derivan del ambiente y del ensamblado
Sentry.Debug                     → log del propio SDK en consola (para diagnosticar la integracion)
Sentry.Excludes                  → rutas extra a excluir del APM, separadas por coma
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
- **⚠️ CRÍTICO — `SINIESTROS.SIN_ID` NO es identity** (confirmado por 3 vías: `sys.columns.is_identity=0`, `COLUMNPROPERTY(...,'IsIdentity')=0`, `sys.identity_columns` vacío para esa tabla). No tiene default constraint ni una `SEQUENCE` dedicada (a diferencia de `ARC_ID`, que sí tiene `SEQ_ARC_ID`). Confirmado con un INSERT crudo de prueba (dentro de una transacción revertida): omitir `SIN_ID` falla con `"Cannot insert the value NULL into column 'SIN_ID'"`. Sin embargo, el historial de `SIN_ID` en la tabla **no tiene ningún hueco** en miles de registros — evidencia de que algún otro proceso (no identificado, probablemente el sistema legacy real de producción) siempre calcula `MAX(SIN_ID)+1` manualmente antes de insertar.
  - **Impacto real (encontrado 03/08/2026):** `SiniestroRepository.UpsertSiniestroAsync` nunca seteaba `SIN_ID` explícitamente (dependía, incorrectamente, de que la BD lo autogenerara). Esto significa que el ETL **nunca pudo insertar un siniestro genuinamente nuevo desde cero** — todos los casos que parecían "funcionar" en pruebas anteriores en realidad encontraban un `SINIESTROS` ya creado por ese otro proceso (por eso el `UPDATE` sí funcionaba) y solo fallaba cuando el siniestro no existía todavía por ningún lado (caso real: folio `042653077`, capturado en SICAS desde el 16/06/2026, nunca llegó a `dbLumoSys` — la póliza y el vehículo sí eran de la flotilla, así que no era un "omitido legítimo", era este bug).
  - **Corregido**: `SiniestroRepository.ObtenerSiguienteSinIdAsync` calcula `SELECT ISNULL(MAX(SIN_ID),0)+1 FROM SINIESTROS WITH (UPDLOCK, HOLDLOCK)` dentro de la misma transacción antes del insert (el lock evita colisiones si dos instancias — ej. local + servidor — insertan al mismo tiempo). `SiniestrosModel.SIN_ID` marcado `ValueGeneratedNever()` en `LumoSysContext`. Verificado con el folio real `042653077` → `SiniestroId=34413`, documentos subidos correctamente.
  - **Pendiente**: este fix estaba corriendo solo en local al momento de encontrarlo — el servicio ya desplegado en el servidor (`10.100.102.6`) sigue con el bug hasta que se vuelva a publicar y reemplazar.
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

## Siniestros — Fase 2 (bitácora de comentarios) nunca guardaba nada (bug crítico, 04/08/2026)

- **Causa raíz**: `ProcesarBitacoraDia` (H03314011) vinculaba cada comentario con su siniestro por `NumReporte` (folio), pero la respuesta real de ese KeyCode **no trae ese campo** — confirmado por inspección directa de la respuesta JSON real de SICAS (`Table_Bitacora`: `IDSiniestro, IDBit, ClaveBit, FechaHora, Prioridad, Procedencia, IsAutom, Comentario, IDUser, ...`, sin `NumReporte` ni `Estatus`). El código evaluaba `item.NumReporte` como `null` siempre y hacía `continue` — **cero comentarios se guardaron jamás para ningún siniestro** desde que se implementó esta fase, no solo para el caso puntual reportado (`042653077`, capturado 16/06/2026).
- Tampoco existe un campo de estatus formal en la respuesta — los comentarios son texto libre de taller/reparación (`"CERRADO: unidad reparada y entregada"`, `"EN PROCESO DE REPARACION: ..."`) que no corresponden a ningún valor del catálogo `TIPOS_ESTATUS` (`SOLICITUD/DOCUMENTOS FALTANTES/EN TRAMITE/SATISFACCION DE CLIENTE/FECHA DE RESOLUCIÓN/CANCELACIÓN`).
- **Corregido**:
  - `SINIESTROS.SIN_FOLIO_SICAS` (columna que ya existía en el modelo pero nunca se llenaba) ahora se guarda con el `IDSiniestro` real de SICAS (viene de `HDS00009`, Fase 1) en cada creación/actualización de siniestro — `SiniestroRepository.UpsertSiniestroAsync`.
  - `ProcesarBitacoraDia` ahora vincula cada comentario contra `SIN_FOLIO_SICAS` (nuevo `ISiniestroRepository.BuscarIdPorFolioSicas`) en vez de `NumReporte`.
  - `SiniestroBitacoraSICAS` corregido para reflejar los campos reales (`IDSiniestro`, `Comentario`, `FechaHora`) — se quitaron `NumReporte`/`Estatus`/`FechaEvento`/`Ejecutivo`, que nunca venían poblados.
  - **Decisión del usuario (04/08/2026)**: el estatus de estos comentarios de bitácora se guarda fijo como `"EN TRAMITE"` (`TES_ID=45`) — no se intenta inferir del texto del comentario, ya que los prefijos reales ("CERRADO", "PENDIENTE...", etc.) no mapean a ningún valor del catálogo existente.
- **Verificado con datos reales**: folio `042653077` (`SIN_ID=34413`, `SIN_FOLIO_SICAS=34330`) → 3 comentarios de bitácora guardados correctamente tras el fix (antes: 0). Confirmado también con otros folios del mismo periodo (`042668625`, `042653161`, `1-202-2026-R-5975`, etc.).
- **Barrido de backfill ejecutado (04/08/2026)**: rango `15/06/2026 → 04/08/2026` reprocesado en 7 sub-rangos semanales (ver nota operativa abajo sobre por qué no en una sola llamada). `SINIESTROS_ESTATUS` pasó de 251,903 a 252,620 filas (+717). Ningún sub-rango alcanzó el límite de 30 páginas de la Fase 2.
- **⚠️ Nota operativa — rangos largos y timeout del cliente HTTP**: `POST /api/Etl/Siniestros/Procesar` con un rango de varias semanas puede tardar más que el timeout del cliente que lo invoque (ej. `curl --max-time`). Si el cliente cierra la conexión, el `CancellationToken` del request (ligado al ciclo de vida HTTP en ASP.NET Core) se cancela y el proceso se corta a medias en el servidor — no corrompe datos (cada siniestro se guarda en su propia transacción, lo ya procesado queda bien), pero la Fase 2 de ese rango puede quedar sin ejecutarse porque corre al final de `ProcesarLote`. Para rangos históricos largos, dividir en sub-rangos semanales con un timeout de cliente generoso (30+ min) en vez de mandar todo el rango de una sola vez.
- **Pendiente**: este fix solo corrió el backfill en la instancia local — falta publicar y redesplegar en el servidor (`10.100.102.6`) para que el ETL automático (diario + intervalo) también lo tenga. Publish ya generado el 04/08/2026 en `C:\Publish\LumoSysIntegraciones\`, pendiente de copiar al servidor.

## Verificación end-to-end (documentos de pólizas)

Corregido y probado dos veces contra SICAS real, con FTP real y BD real:
- Serie `8AFWR5CP7K6127942` / póliza `L0000019478-0` (IDDocto `263235`) → 1 documento real detectado, subido a FTP (`2467002.pdf`) y vinculado en `DOCUMENTOS_UNIDADES`.
- Serie `JS1VU51A5J2101033` / póliza `L0000019601-0` (IDDocto `263270`) → 1 documento real detectado, subido a FTP (`2467014.pdf`) y vinculado en `DOCUMENTOS_UNIDADES`.

Ambos casos confirmados como correctamente escopados (documento vinculado exclusivamente al `CDE_ID` de su propia serie, sin duplicar registros al reprocesar la misma póliza dos veces — `ExisteDocumentoAsync` evita el duplicado).

## Log diario de errores en archivo

Además de `dbIntegraciones.Seguridad.Bitacora`, cada aviso/error que pasa por `IBitacoraRepository.GuardarAsync` (Seguros y Siniestros) y cada excepción no controlada (`ExceptionHandlingMiddleware`) se escribe también en un archivo de texto diario en `C:\LumoSys\Programas\Sicas\Log dd-MM-yyyy.txt` (un archivo por día, se va agregando línea por línea; la carpeta se crea sola si no existe). Mismo patrón que los ETL legacy en `C:\Desarrollo\Programas\PROD\Integraciones\Logs\Seguros|Siniestros\Log dd-MM-yyyy.txt` (que siguen corriendo en producción, en paralelo a este proyecto — ver hallazgo abajo). Implementado en `LogErroresArchivoService` (Infrastructure/Notifications), inyectado como singleton con `SemaphoreSlim` para escritura concurrente segura.

- **Hallazgo relacionado**: el ETL legacy (`C:\Desarrollo\Programas\PROD\Integraciones`) sigue activo y programado en el servidor (genera su log diario incluso en fechas muy recientes), pero la mayoría de sus archivos diarios son mínimos (~45-175 bytes, solo "Inicio de proceso") — consistente con que también sufre el mismo bug de formato de fecha (`dd/MM/yyyy` vs ISO) que se corrigió en este proyecto, es decir, tampoco ha estado procesando nada en el día a día. Los pocos archivos grandes (ej. `Log 22-06-2026.txt`, `Log 30-06-2026.txt`) corresponden a ejecuciones manuales/reprocesamiento puntual.

## Monitoreo con Sentry (07/09/2026)

Integrado replicando el patrón ya probado en `lumo-system` (`slnLumoSys/Global.asax.cs`), trasladado del modelo de hosting de ASP.NET MVC/net462 al de ASP.NET Core/.NET 9.

- **Paquetes**: `Sentry.AspNetCore` 6.10.0 en API, `Sentry` 6.10.0 en Infrastructure. (lumo-system usa 5.1.1 con `Sentry.AspNet` + `Sentry.EntityFramework`, que son los equivalentes de .NET Framework.)
- **DSN**: en `appsettings.json`, sección `Sentry`. No es un secreto (viaja en cada evento), por eso va en el archivo versionado. Un DSN vacío deja el SDK deshabilitado y el ETL corre igual — todas las llamadas de monitoreo son no-op.
- **Inicialización**: `SentryStartupExtensions.ConfigurarSentry(builder)` (`src/API/Extensions/`), llamada en `Program.cs` antes de registrar los demás servicios para que también cubra las fallas de arranque.

### Abstracción por capas (por qué no se usa `SentrySdk` directo en Application)

`lumo-system` es un monolito MVC sin capas y llama `SentrySdk` directo. Aquí eso acoplaría Application a un SDK de terceros, contra la misma regla que ya prohíbe EF Core y HTTP en esa capa. En su lugar hay dos puertos en `Domain/Shared/Interfaces/`:

| Interfaz | Implementación | Usada por |
|---|---|---|
| `IMonitoreoErrores` | `Infrastructure/Monitoring/SentryMonitoreoErrores.cs` | Handlers de Application, clientes de Infrastructure, `ExceptionHandlingMiddleware` |
| `IMonitoreoEtl` | `Infrastructure/Monitoring/SentryMonitoreoEtl.cs` | Los 4 BackgroundServices |

Domain y Application no referencian el paquete `Sentry`. Ambas implementaciones son singleton: el SDK es estático por diseño y el aislamiento entre unidades de trabajo lo da el ámbito (`PushScope`, propagado por `AsyncLocal`), no la instancia.

### Qué NO hace falta instrumentar a mano

`MinimumEventLevel = LogLevel.Error` hace que **todo `log.LogError(...)` que ya existía se convierta en evento de Sentry**, con los parámetros estructurados del mensaje como datos extra. Por eso no se agregaron try-catch nuevos en repositorios, `SICASSeguroClient`/`SICASSiniestroClient`, `GuardarPolizaHandler`, `GuardarSiniestroHandler` ni `SubirDocumentoPolizaHandler`: ya reportan solos. Las integraciones automáticas del SDK cubren además:

- `AppDomainUnhandledExceptionIntegration` — excepciones no controladas del proceso (crítico corriendo como servicio de Windows).
- `UnobservedTaskExceptionIntegration` — excepciones de `Task` en segundo plano que nadie observa.
- `SentryDiagnosticListenerIntegration` — instrumenta EF Core y `HttpClient` (equivale al `AddEntityFramework()` de lumo-system).
- Middleware de request + `AutoRegisterTracing` — transacciones HTTP (equivale a `Application_BeginRequest`/`EndRequest` con `Start`/`FinishSentryTransaction`).

`DeduplicateMode` se fija explícitamente a `SameEvent | SameExceptionInstance | AggregateException`: es lo que evita el evento duplicado cuando un punto de entrada reporta la excepción a mano (para adjuntar contexto) y además la loguea. Verificado en vivo — el log de debug muestra `Event dropped by processor DuplicateEventDetectionEventProcessor`.

### Puntos donde sí se instrumentó, y por qué

Solo dos criterios: (a) puntos de entrada del flujo, y (b) `catch` que ya silenciaban el error por regla de negocio, donde el flujo no se toca pero el fallo era invisible.

| Archivo | Qué se agregó | Motivo |
|---|---|---|
| `SegurosEtlBackgroundService`, `SiniestrosEtlBackgroundService` y sus dos variantes de intervalo | Corrida de ETL (`IMonitoreoEtl`) por ciclo | Punto de entrada del flujo automático |
| `ExceptionHandlingMiddleware` | `Capturar` con método/ruta/traza | Equivalente de `Application_Error`; el middleware consume la excepción, así que sin captura explícita el middleware de Sentry nunca la vería |
| `ProcesarLoteSeguroHandler` | Ámbito por póliza + captura en `ProcesarPolizaCompleta` y `SincronizarSFleet` | Ambos `catch` silencian a propósito (un ítem malo no debe abortar el lote; SFleet caído no debe deshacer lo ya guardado) |
| `ProcesarLoteSiniestroHandler` | Ámbito por siniestro + captura en `ProcesarSiniestroCompleto` y `ProcesarBitacoraDia` | Igual; la Fase 2 es complementaria y su fallo no debe invalidar la Fase 1 |
| `FtpDocumentService` | Captura con host/ruta/archivo, rastros de subida | Devuelve `0` por contrato; sin host y ruta un `530 User cannot log in` no se distingue de un archivo corrupto (ver "FTP — cuenta de servicio") |
| `SICASRestClient` | Rastros de token/ReadData, captura en `DownloadFile`, `GetFiles` y JSON irreparable | Devuelven `null`/`[]` por contrato: un documento que nunca llega es la falla más difícil de notar, porque la póliza sí queda guardada |
| `SFleetClient` | `LogError` + rastro cuando la autenticación falla | **Antes devolvía `null` sin registrar nada**: con las credenciales caídas cada póliza salía como "serie no encontrada en SFleet" (un aviso normal) y nada indicaba que ninguna se sincronizaba |

Ningún `catch` intermedio nuevo silencia nada: los cuatro puntos con captura ya silenciaban antes por decisión de negocio y conservan su comportamiento. En los BackgroundServices se agregó un `catch (OperationCanceledException)` que marca la corrida como cancelada y **re-lanza** (`throw;`) — el filtro `when (ex is not OperationCanceledException)` original tampoco la atrapaba, así que el flujo es idéntico.

### Monitores programados (Sentry Crons) — lo que el log no puede detectar

Cada ciclo de ETL emite un check-in (`in_progress` seguido de `ok`/`error`) contra un monitor con horario declarado desde el propio código, en `DescribirCorrida`:

| Monitor | Horario | Duración máx. |
|---|---|---|
| `etl-seguros-diario` | crontab derivado de `EtlSchedule:Seguros` | 180 min |
| `etl-siniestros-diario` | crontab derivado de `EtlSchedule:Siniestros` | 180 min |
| `etl-seguros-intervalo` | cada `IntervaloMinutosSeguros` min | máx(2× intervalo, 30) min |
| `etl-siniestros-intervalo` | cada `IntervaloMinutosSiniestros` min | máx(2× intervalo, 30) min |

**Esta es la parte que resuelve un problema real ya documentado arriba**: cuando el servicio de Windows está detenido no hay excepciones ni logs, así que el silencio es indistinguible del funcionamiento normal — es exactamente cómo se perdió la póliza `L0000019660-0` (ver "ETL — modo de intervalo"). El check-in ausente es la única señal capaz de detectarlo. El horario se traduce a crontab desde la misma config que gobierna el `Task.Delay`, para que no se desincronice al cambiar `EtlSchedule`; `TimeZone` se fija a la del servidor o Sentry evaluaría los horarios nocturnos en UTC.

Los slugs de monitor **no deben cambiar entre despliegues**: son la llave con la que Sentry sabe qué corrida esperaba.

### Rendimiento (APM)

`TracesSampler` da **1.0 a las transacciones `etl.run`** (son pocas al día y su duración es justo el dato a vigilar) y aplica la tasa configurada al tráfico HTTP, excluyendo `/swagger`, `/health` y `/favicon.ico` (más lo que se agregue en `Sentry:Excludes`, equivalente al `SENTRY_EXCLUDES` de lumo-system). `TracesSampleRate`: 0.8 en `appsettings.Development.json`, 0.2 en `appsettings.json` — mismo criterio que el `#if DEBUG` de lumo-system.

`SetBeforeSend` descarta `OperationCanceledException`/`TaskCanceledException`: al detener el servicio, los `Task.Delay` de los cuatro BackgroundServices y las peticiones en vuelo las lanzan, y reportarlas convertiría cada reinicio o despliegue en una ráfaga de alertas falsas.

### Etiquetas disponibles para filtrar en Sentry

`servicio` (fijo, `LumoSysIntegraciones`), `modulo` (Seguros/Siniestros), `disparador` (`programado-diario`, `programado-intervalo`, `manual-poliza`, `manual-serie`, `manual-folio`), `operacion`, `monitor`, `poliza`, `iddocto`, `serie`, `folio_siniestro`, `idsiniestro`, `rango_desde`/`rango_hasta`, `ventana_minutos`, `http_metodo`/`http_ruta`, y `consecuencia` en los casos silenciados (`poliza-omitida-lote-continua`, `guardada-en-lumosys-sin-sincronizar-sfleet`, `siniestro-omitido-lote-continua`, `comentarios-de-bitacora-no-actualizados`, `documento-no-subido`, `documento-no-descargado`, entre otros) — esta última es la que dice qué quedó pendiente de arreglar a mano.

### Verificación realizada

Prueba de humo con `Sentry:Debug=true`: arranque y apagado limpios (DSN leído, integraciones registradas, `Disposing the Hub` al detener), y un `GET /api/Siniestros/Estado` con la BD inaccesible produjo `Capturing event`, luego la deduplicación del evento del `ILogger`, y `HttpTransport: Envelope '...' successfully sent`. La respuesta HTTP siguió siendo el mismo JSON de 500 de antes — contrato de la API sin cambios.

También se validó un ciclo completo del ETL programado (con `EtlSchedule:Seguros` puesto 2 min adelante): check-in `in_progress` enviado, lote ejecutado, transacción `etl.run` enviada, check-in `ok` enviado, y el servicio siguió agendando la corrida del día siguiente sin quedar en un estado raro. El evento intermedio de ese ciclo fue el rechazo de autenticación de SICAS (`Usuario o Contraseña Incorrecta`, esperado con los `PLACEHOLDER` de este entorno) capturado **automáticamente** por la integración de `ILogger` en `SICASRestClient`, sin try-catch agregado — confirmación práctica de que el enfoque de no instrumentar lo que ya loguea funciona.

**Pendientes**:
1. Los cuatro monitores programados aparecerán en Sentry al primer check-in real de cada uno (los dos de intervalo, solo si `IntervaloMinutosX` está configurado en ese ambiente).
2. La prueba anterior dio de alta `etl-seguros-diario` con el crontab de la hora de prueba (`21 16 * * *`). **Se corrige solo**: `ConfigurarMonitor` reenvía el horario en cada check-in, así que la primera corrida real en el servidor lo deja en el valor de `EtlSchedule:Seguros`. Si molesta antes de eso, se puede borrar el monitor en Sentry y se vuelve a crear correctamente.

### Auditoría previa a producción (11/09/2026) — hallazgos y correcciones

Revisión de si la integración cubría el ciclo completo del sistema. Se encontraron dos fallos que
habrían dejado ciega la instalación justo en su modo de falla más probable, y se corrigieron.

#### 1. CRÍTICO — un timeout de SICAS tumbaba el servicio y Sentry reportaba "todo bien"

`HttpClient` lanza `TaskCanceledException` cuando vence su `Timeout`, y `TaskCanceledException`
hereda de `OperationCanceledException`. Verificado en banco de pruebas: en ese caso
`ex.CancellationToken.IsCancellationRequested` también vale `true` (lo cancela el temporizador
interno del propio `HttpClient`), así que **el token no permite distinguir un timeout de red del
apagado del servicio**. Lo único que los diferencia es `InnerException` (`TimeoutException` en el
timeout, `null` en el apagado) o el estado del host.

Con la versión original eso encadenaba tres efectos a la vez, todos en el mismo escenario —SICAS
en mantenimiento o red caída:

1. `SetBeforeSend` descartaba toda `OperationCanceledException` ⇒ **el error nunca llegaba a Sentry**.
2. El `catch (OperationCanceledException)` de los cuatro BackgroundServices lo tomaba por apagado y
   cerraba el check-in como **`Ok`** ⇒ **el monitor programado reportaba la corrida como correcta**.
3. Ese mismo `catch` hacía `throw;` ⇒ moría el `ExecuteAsync`, y con
   `BackgroundServiceExceptionBehavior.StopHost` (el valor por omisión de .NET 6+) **se detenía el
   host completo**, es decir, el servicio de Windows.

Resultado: el servicio se caía en bucle (reiniciado por `sc failure`), el ETL dejaba de sincronizar,
y en Sentry no aparecía ni un evento ni un check-in fallido. Exactamente el escenario que el
monitoreo debía detectar.

**Corrección:**

- `ApagadoEnCurso` (`src/API/Extensions/`): bandera alimentada por
  `IHostApplicationLifetime.ApplicationStopping` desde `Program.cs`. `SetBeforeSend` ahora descarta
  cancelaciones **solo mientras el host se está deteniendo**; fuera de eso, una cancelación es un
  timeout real y se reporta.
- Los cuatro BackgroundServices usan `catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)`.
  Una cancelación que no venga del apagado cae ahora en el `catch` general: check-in `error`, evento
  en Sentry, **y sin `throw;`** — una caída temporal de SICAS ya no tumba el servicio, el ciclo
  siguiente vuelve a intentar.
- `ExceptionHandlingMiddleware` distingue por `context.RequestAborted.IsCancellationRequested`, no
  por el tipo de excepción: un cliente que se desconecta de un lote largo de `/api/Etl/*/Procesar`
  no alerta, pero un timeout contra SICAS dentro de esa misma petición sí.

**Verificado en ejecución** (ETL apuntado a un host que no responde, `ASPNETCORE_ENVIRONMENT=Production`):
el ciclo entró al `catch` general (`ETL Seguros (intervalo): error en ejecución programada`), el
evento se envió con el stack completo hasta `SICASRestClient.EnsureToken:69`, el check-in salió
como fallo, y el proceso **siguió vivo** (sin `Unhandled exception`, sin `Application is shutting down`).

#### 2. Fallas de arranque no se reportaban

Corriendo como servicio de Windows, un fallo al levantar es la falla más silenciosa que existe: no
hay petición que devuelva 500 ni ciclo de ETL que falle, solo un servicio que no está y un ETL que
deja de correr. `Program.cs` ahora envuelve todo el arranque y delega en
`SentryStartupExtensions.ReportarFallaDeArranque`, que inicializa el SDK de emergencia si la falla
ocurrió antes de que `UseSentry` alcanzara a hacerlo, vacía la cola a mano (el host nunca llegará a
hacerlo) y re-lanza para que el Visor de eventos de Windows siga viendo el error original.

#### 3. Cobertura de controladores — `MonitoreoActionFilter`

`src/API/Filters/MonitoreoActionFilter.cs`, registrado como filtro global en `Program.cs`. Etiqueta
cada petición con `controlador`, `accion` y los identificadores de negocio que vengan en sus
argumentos, normalizados a los mismos nombres que usan los handlers del ETL (`poliza`, `serie`,
`folio_siniestro`, `inciso`, `no_siniestro`, `idsiniestro`, `iddocto`). Así un mismo registro se
rastrea en Sentry sin importar si entró por el barrido automático o por la API.

Se hizo como filtro global y no instrumentando cada acción para que los controllers sigan siendo
thin y para que cualquier endpoint futuro quede cubierto sin tocar nada. Extrae por **lista blanca**
de nombres, nunca "todo el modelo": los comandos incluyen datos personales (beneficiario,
contratante, ejecutivo) que no tienen por qué salir hacia un servicio externo.

#### 4. `Release` fijo en todos los despliegues

Sin `<Version>` en el csproj, `Assembly.GetName().Version` siempre daba `1.0.0` y todos los
despliegues se veían iguales en Sentry — imposible marcar regresiones o decir "esto empezó con la
versión de ayer". Se agregó `<Version>1.1.0</Version>` a `LumoSys.Integraciones.API.csproj`.
**Hay que subirla en cada despliegue** (o fijar `Sentry:Release` por ambiente).

#### 5. Fallo de FTP sin excepción no generaba evento

`FtpStatus.Failed` no lanza: quedaba en `LogWarning` + un rastro que nadie llegaría a ver, pese a
que para el negocio el resultado es idéntico al de una excepción (el documento no se subió). Elevado
a `LogError`, que sí produce evento.

#### Verificaciones que salieron limpias

- **Repositorios y clientes SICAS por dominio**: sin `try-catch` — las excepciones propagan hasta los
  handlers y el middleware, que ya reportan. No hacía falta tocarlos.
- **Asincronía**: cero `async void`, cero `.Result`/`.Wait()`, cero fire-and-forget en todo `src/`.
  No hay excepciones asíncronas que se puedan perder.
- **Privacidad**: `MaxRequestBodySize` queda en `None` (valor por omisión), así que **los cuerpos de
  las peticiones no se envían a Sentry** — los datos personales de los comandos de póliza no salen
  del servidor. `SendDefaultPii = true` (igual que lumo-system) hace que sí viajen IP y cabeceras;
  aceptable en un servicio interno de red local, pero es una decisión consciente, no un descuido.

#### Riesgo conocido que NO se tocó (requiere decisión de negocio)

`bitacora.GuardarAsync(...)` se invoca **dentro** de los `catch` de `ProcesarPolizaCompleta`,
`ProcesarSiniestroCompleto` y `ProcesarBitacoraDia`, y `BitacoraRepository` no captura nada. Si lo
caído es justamente `dbLumoSys`, el primer error de póliza intenta dejar constancia, la escritura
falla, y **esa excepción secundaria sale del `catch` y aborta el barrido completo**, perdiendo todas
las pólizas que faltaban del lote. Es un comportamiento anterior a la integración de Sentry.

Con las correcciones de arriba el caso ya queda **visible** (el BackgroundService lo reporta como
corrida fallida), pero la pérdida de registros del lote sigue ocurriendo. El arreglo es envolver esas
tres llamadas en un método que no propague — pendiente de confirmar, porque cambia cómo se comporta
el ETL ante una caída de base de datos.

#### Recomendación operativa

Activar **Spike Protection** en el proyecto de Sentry. Con el modo de intervalo a 20 min son ~72
corridas al día; si una caída prolongada hace fallar cada póliza de cada corrida, el volumen de
eventos puede consumir la cuota del plan. Sentry agrupa todo en un mismo issue, pero la cuota se
consume por evento.

### Fallos silenciosos: barrido completo del proyecto (11/09/2026)

Revisión de todos los puntos donde el código devuelve `null`, `0`, `false` o lista vacía ante un
error de un sistema externo, dejando al llamador sin forma de distinguir el fallo de un resultado
legítimo. Se auditaron los 88 retornos de ese tipo que hay en `src/`.

#### La raíz: `resp ?? []` en los clientes de dominio

`SICASRestClient.ReadData<T>` devuelve `null` cuando la consulta falla, y todos los métodos de
`SICASSeguroClient` / `SICASSiniestroClient` lo colapsan con `return resp ?? []` o
`resp?.FirstOrDefault()`. A partir de ahí **"SICAS respondió 500" y "ese día no hubo pólizas" son el
mismo valor**: una lista vacía. El barrido la lee como fin de datos, corta el recorrido de páginas,
escribe *"Lote finalizado"* y cierra su check-in como correcto.

No es teórico: los dos incidentes documentados más arriba en este archivo —el del formato de fecha
(`dd/MM/yyyy` vs ISO) y el del límite superior exclusivo del filtro— se manifestaron exactamente
así, y ambos pasaron semanas sin detectarse porque un lote vacío no falla ni escribe nada raro.

**Demostrado en banco de pruebas**: con un SICAS simulado que autentica bien y responde `500` a
`/Report/ReadData`, `POST /api/Etl/Seguros/Procesar` devuelve
`HTTP 200 {"estatus":true,"mensaje":"Procesamiento de seguros completado."}`.

#### `IMonitoreoErrores.ReportarFalloSilencioso(...)`

`Capturar` exige una `Exception`, y la mayoría de estos fallos no lanzan nada — ese es justamente el
problema. Se añadió al puerto de monitoreo:

```csharp
void ReportarFalloSilencioso(string operacion, string motivo, string consecuencia,
                             params (string Clave, string? Valor)[] etiquetas);
```

Emite un `CaptureMessage` de nivel `Error` titulado `[operacion] motivo`, de modo que Sentry agrupa
en un mismo issue todos los eventos del mismo punto de fallo. Cada evento lleva:

- `operacion` — **dónde** falló (`sicas.readdata`, `sfleet.guardar-poliza`, …).
- `motivo` — **por qué**, en una línea legible con el código HTTP real.
- `consecuencia` — **qué se perdió**, que es lo que dice si hay que reprocesar algo a mano.
- `fallo_silencioso = si` — permite filtrar en Sentry justo esta clase de falla.

#### Puntos instrumentados

| Punto | Qué se silenciaba | Consecuencia reportada |
|---|---|---|
| `SICASRestClient.ReadData` (HTTP ≠ 2xx) | **El más grave.** Toda consulta a SICAS pasa por aquí | `consulta-descartada-se-lee-como-sin-registros` |
| `SICASRestClient.ReadData` (JSON irreparable tras 5 intentos) | Caía al `return null` final, sin una sola línea | idem |
| `SICASRestClient.BuscarArchivosDigitales` (HTTP ≠ 2xx) | Lista vacía = "no tiene documentos" | `documentos-no-listados-se-lee-como-sin-documentos` |
| `SICASRestClient.DownloadFile` (HTTP ≠ 2xx) | Solo `LogWarning` | `documento-no-descargado` |
| `SFleetClient.ObtenerToken` | Sin `access_token` pese a HTTP 200 | `ninguna-poliza-se-sincroniza-con-sfleet` |
| `SFleetClient.BuscarVehiculo` | `return null` mudo: arriba se leía como "serie no dada de alta", un aviso rutinario | `poliza-no-sincronizada-se-lee-como-serie-inexistente` |
| `SFleetClient.BuscarPoliza` | `return null` mudo | `se-tratara-como-alta-nueva-riesgo-de-duplicado` |
| `SFleetClient.GuardarPoliza` | `LogWarning`; el `0` que devuelve no lo revisa nadie | `guardada-en-lumosys-sin-sincronizar-sfleet` |
| `SFleetClient.SubirDocumento` | **Completamente mudo**: `return resp.IsSuccessStatusCode` sin log ni evento | `documento-no-disponible-en-sfleet` |
| `PolizaRepository.VincularDocumentoUnidadAsync` (×2) | Silencio absoluto. El PDF queda en el FTP pero sin fila en `DOCUMENTOS_UNIDADES`: invisible desde LumoSys, y la fila de `ARCHIVOS_REPOSITORIOS` queda huérfana | `documento-subido-al-ftp-pero-invisible-en-lumosys` |
| `ProcesarLoteSeguroHandler` — póliza sin `IDDocto` | `continue` mudo | `poliza-omitida-sin-procesar` |
| `ProcesarLoteSeguroHandler` — sin detalle / sin primas | `LogWarning`; en el caso de primas ya se confirmó que la serie **sí** es de la flotilla propia | `poliza-no-guardada` |
| `ProcesarLoteSiniestroHandler` — sin `IDDocto` / sin serie | `LogWarning` | `siniestro-no-guardado` |
| Documentos de póliza y de siniestro que no se descargan o no suben | `LogWarning` + `continue` | `…-guardado-sin-este-documento` |

`SFleetClient` además quedó envuelto en `try-catch` por método: sus `JObject.Parse` / `JArray.Parse`
no tenían protección y, si SFleet devuelve HTML de error con un 200, la excepción se reportaba desde
`SincronizarSFleet` sin decir en qué llamada había ocurrido. El contrato de retorno
(`null`/`0`/`false`) se conserva intacto.

#### Lo que deliberadamente NO se reporta

Reportar de más es tan inútil como no reportar: un canal con ruido se ignora.

- **"No pertenece a la flotilla propia"** (Seguros y Siniestros) — es el filtro de negocio normal y
  la mayoría de los registros de SICAS caen ahí. Queda en `LogInformation`.
- **Sin token, en cada consulta** (`ReadData`, `BuscarArchivosDigitales`) — `EnsureToken` ya reportó
  la causa raíz; emitir otro evento por consulta multiplicaría un solo fallo en decenas. Queda como
  rastro, que además se adjunta al evento raíz.
- **Comentarios de bitácora que no vinculan** (Fase 2) — la bitácora del día trae los comentarios de
  *todos* los siniestros de SICAS, así que lo normal es que la mayoría no esté en `dbLumoSys`. En su
  lugar se cuentan y **solo se reporta si, habiendo 20 o más candidatos, ninguno vinculó**: ese es el
  patrón exacto del bug de Fase 2 del 04/08/2026, cuyo único síntoma era que el historial de estatus
  dejaba de crecer.
- **Resolvers de catálogo** (`ResolverEmpresaIdAsync`, `ResolverFormaPagoIdAsync`…) — devuelven `null`
  por diseño cuando no hay match y el campo queda vacío; es una decisión de negocio ya documentada.
  `ResolverAseguradoraIdAsync` sí lanza, que es lo correcto porque el dato es obligatorio.

#### Señal agregada: barridos que terminan en cero

`ProcesarLote` (ambos módulos) ahora etiqueta la corrida con `registros_encontrados`. Es lo que
permite crear en Sentry una alerta sobre el patrón que causó los dos incidentes históricos: un
barrido diario que termina "correctamente" con cero registros. Para el barrido de intervalo un cero
es normal; para el diario, en día hábil, no lo es.

#### Verificación

Con el SICAS simulado devolviendo `500` en `/Report/ReadData`, el log de Sentry en modo debug
muestra la secuencia completa: `ReadData H03117 falló: InternalServerError` → `Capturing event` →
`Lote Seguros finalizado. 0 pólizas encontradas en el rango.` → `Envelope successfully sent`. El ETL
se comporta igual que antes —la petición sigue devolviendo `HTTP 200`— pero el fallo ya no es
invisible.

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
| Sentry detras de `IMonitoreoErrores`/`IMonitoreoEtl` en Domain | Application no debe acoplarse a un SDK de terceros (misma regla que EF Core y HTTP); ademas deja el monitoreo desactivable y sustituible |
| Check-ins de Sentry Crons en el ETL | Un servicio de Windows detenido no lanza excepciones ni escribe logs: la ausencia de check-in es la unica senal que detecta ese caso |

## Bugs corregidos respecto a proyectos anteriores

1. `ReasignarEstatus` ahora corre dentro de la transacción EF Core.
2. Descarga de documentos desde SICAS en memoria (sin disco), retorna `null` si falla HTTP.
3. `GuardarSiniestroHandler` lanza excepción real en lugar de devolver `Ok(true)` con error oculto.
4. FTP con FluentFTP (compatible con .NET 9).
5. `SICASRestClient` singleton con renovación proactiva de token (evita expiración durante lote grande).
