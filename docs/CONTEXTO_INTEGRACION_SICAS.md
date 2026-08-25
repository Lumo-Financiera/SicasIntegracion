# Contexto — Integración SICAS ↔ LumoSys (`SicasIntegracion`)

> Documento de contextualización. Léelo antes de tocar cualquier cosa de este módulo.
> Repositorio: `github.com/Lumo-Financiera/SicasIntegracion` — este documento vive en `docs/` del propio repo.
> Última revisión del código: commit `2b8decf` (04/08/2026). Documento escrito el 24/08/2026.

---

## 1. Resumen ejecutivo

`SicasIntegracion` (nombre real de la solución: **LumoSys.Integraciones**) es un **servicio ETL en .NET 9** que baja de **SICAS** —el sistema de la correduría/aseguradora, operado bajo la licencia `wsSicurezza`— dos flujos de información y los deja escritos en los sistemas propios de Lumo:

| Flujo | Origen | Destinos |
|---|---|---|
| **Seguros** (pólizas de la flotilla) | SICAS REST | `dbLumoSys` (SQL Server) + **SFleet** + FTP de documentos |
| **Siniestros** (reclamaciones de esas pólizas) | SICAS REST | `dbLumoSys` + FTP de documentos |

No es un módulo del Hub. Es un **servicio de Windows independiente** que corre en el servidor `10.100.102.6` y alimenta a LumoSys (el ERP legado en ASP.NET MVC). Está en esta carpeta como *referencia a incluir en el hub*, es decir: es candidato a migrarse o a exponerse desde el Hub, pero hoy vive fuera.

Fusiona **tres proyectos anteriores** que quedaron obsoletos:

| Proyecto anterior | Tecnología | Rol que cumplía |
|---|---|---|
| `C:\LumoSysGit\Seguros\Seguros` | .NET 6 consola, WCF SOAP | ETL SICAS → LumoSys + SFleet |
| `C:\LumoSysGit\Siniestros\Siniestros` | .NET 6 consola, WCF SOAP | ETL SICAS → LumoSys |
| `B:\Dessaarollos\API.Lumosys` (solo módulos Seguros/Siniestros) | .NET 6 ASP.NET Core | Receptor HTTP → `dbLumoSys` |

⚠️ El ETL legacy de `C:\Desarrollo\Programas\PROD\Integraciones` **sigue programado y corriendo en el servidor**, en paralelo a este proyecto. Genera su propio log diario, aunque en la práctica casi no procesa nada (sufre el mismo bug de formato de fecha que aquí ya se corrigió). Al desmontarlo, hay que apagarlo explícitamente.

---

## 2. Relación con el resto del ecosistema

```
        ┌──────────────────────────────────────────────┐
        │  SICAS  (security-services.sicasonline.info) │  ← sistema de la correduría
        │  Pólizas · Siniestros · Centro Digital       │
        └───────────────────┬──────────────────────────┘
                            │ REST (token 3 min)
                ┌───────────▼─────────────┐
                │  LumoSys.Integraciones  │  ← ESTE PROYECTO (.NET 9, servicio Windows)
                │  4 BackgroundServices   │
                │  + API REST sin auth    │
                └──┬───────────┬───────┬──┘
                   │           │       │
       ┌───────────▼──┐  ┌─────▼────┐  └──────────┐
       │  dbLumoSys   │  │  SFleet  │      ┌──────▼───────┐
       │ SQL Server   │  │ (flotillas)     │  FTP         │
       │ 10.100.102.7 │  └──────────┘      │ 10.100.102.6 │
       └──────────────┘                    └──────────────┘
```

- **SICÚRIKA** es la correduría de seguros del grupo (ver `project_sicurika_crm_seguros`). SICAS es el sistema que la correduría usa; este ETL es el puente entre ese sistema y LumoSys. El broker por defecto que se escribe cuando SICAS no lo trae es literalmente `"Sicurika"`, y la administración de cartera se fija en `"SICÚRIKA AGENTE DE SEGUROS"`.
- **LumoSys** es el ERP/CRM legado (ver `project_lumosys_legado`). Este ETL escribe directo en su base, respetando sus catálogos y sus reglas de negocio.
- **SFleet** (`fleetsoluciones.com`) es el sistema de flotillas de terceros al que también se replica la póliza.

---

## 3. Stack y arquitectura

- **.NET 9 / C# 13**, Clean Architecture en 4 proyectos.
- **EF Core 9** contra SQL Server (`dbLumoSys`, única base — `dbIntegraciones` fue decomisionado el 24/07/2026).
- **RestSharp 112** para SICAS REST (reemplazó el proxy WCF SOAP del legacy).
- **FluentFTP 53** para subir documentos (el `System.Net.FtpClient` del legacy es solo .NET Framework).
- **Newtonsoft.Json 13** — SICAS a veces devuelve JSON malformado; hay reparación con reintentos.
- **Swashbuckle 7** (Swagger, solo en Development).
- **Sin autenticación**: servicio interno de red local. No hay JWT ni `[Authorize]` en ningún controller. Cualquiera que alcance el puerto 5096 puede disparar el ETL.

```
src/
├── Domain/          interfaces, modelos de dominio. NUNCA depende de capas externas.
├── Application/     handlers (*Command → *Result). No referencia EF Core ni HTTP.
├── Infrastructure/  EF Core, repositorios, clientes SICAS/SFleet/FTP, logging.
└── API/             controllers thin, BackgroundServices, middleware, Program.cs.
```

Registro de dependencias: `Infrastructure/Extensions/InfrastructureServiceExtensions.cs` + `API/Program.cs`.
`SICASRestClient` es **singleton** (gestiona el token con `SemaphoreSlim`); todo lo demás es scoped.

---

## 4. Flujo de datos — Seguros

Handler: `Application/Seguros/UseCases/ProcesarLoteSeguros/ProcesarLoteSeguroHandler.cs`

```
1. H03117  BuscarPolizasVigentes(desde, hasta, página)   → lista de pólizas capturadas en el rango
   (paginado de 100, tope duro de 30 páginas = 3,000 registros; si se alcanza, avisa en bitácora)

2. Por cada póliza (con Task.Delay(300) entre una y otra, para no ametrallar a SICAS):
   a. HWS_DDETAIL  BuscarDetalle(IDDocto)          → serie (VIN), uso, ocupantes, adaptaciones
   b. FILTRO: ¿la serie existe en COMPRAS_DETALLES o VEHICULOS?
      → si NO, se omite. NO es un error: es una póliza de un tercero, no de la flotilla propia.
   c. H03400      BuscarPrimas(IDDocto)            → primas, vigencia, beneficiario, ejecutivo
   d. H03400_019  BuscarCoberturas(IDDocto)        → coberturas y deducibles
   e. H03120      BuscarCobranza(IDDocto)          → recibos (1° recibo y subsecuente)
   f. GetFiles    identity "H02", ValuePK=IDDocto  → documentos del Centro Digital

3. GuardarPolizaHandler → PolizaRepository:
   - UpsertPolizaAsync      → SEGUROS            (resuelve ASEGURADORAS, TIPOS_FORMAS_PAGOS_SEGUROS,
                                                  BENEFICIARIOS_PREFERENTES, EMPRESAS)
   - UpsertVehiculoAsync    → SEGUROS_DETALLES   (resuelve ~8 catálogos más)
   - ReasignarEstatusAsync  → marca pólizas anteriores como SUSTITUCIÓN y la nueva como VIGENTE

4. Documentos: descarga en memoria → RegistrarArchivoAsync (ARCHIVOS_REPOSITORIOS, ARC_ID vía
   SP_ACTUALIZAR_SECUENCIAS) → sube al FTP como {ARC_ID}.pdf → VincularDocumentoUnidadAsync
   (DOCUMENTOS_UNIDADES, DUN_TDW_ID=4 = "POLIZA SEGURO")

5. SFleet: busca el vehículo por serie, busca si la póliza ya existe (POST vs PATCH) y la replica.
   Si SFleet falla, se registra el error pero NO se aborta el proceso de la póliza.
```

**Constantes de estatus** (`PolizaRepository`): `VIGENTE=177`, `SUSTITUCIÓN=497`, `PENDIENTE=454`.
**Usuario del sistema**: `USU_ID = 3` en todas las escrituras automáticas.

### Traducción de coberturas
`ObtenerCobertura` replica literalmente `Conversiones.cs` del ETL legacy. Tres comportamientos según el nombre de la cobertura:
- **Booleanas** (Robo parcial, Adaptaciones, RC Pasajeros, RC USA/Canadá) → `"AMPARADA"` / `"NO APLICA"`.
- **Numéricas** (Accidentes al conductor, Gastos médicos ocupantes, Responsabilidad civil) → se toma la suma asegurada.
- **Resto** → texto crudo, con dos parches heredados: corrige el typo `"Amaprada"` → `"Amparada"`, y para Daño Material / Robo Total fuerza `"Valor comercial"` cuando no está amparada.

Los catálogos son **estrictos**: si un valor de SICAS no existe en el catálogo de LumoSys, se lanza excepción y la póliza no se guarda (`ASEGURADORAS`, `TIPOS_USOS`, `TIPOS_POLIZAS`, `TIPOS_COBERTURAS`, `TIPOS_GESTION_PAGOS_SEGURO`, `TIPOS_VALORES_SEGURO`, `TIPOS_ADMINISTRACIONES_CARTERA`, `USUARIOS`). La única excepción es `TIPOS_NO_PASAJEROS`, que se **da de alta sola** si no existe.

---

## 5. Flujo de datos — Siniestros

Handler: `Application/Siniestros/UseCases/ProcesarLoteSiniestros/ProcesarLoteSiniestroHandler.cs`

**Fase 1 — siniestros del rango**
```
HDS00009  BuscarSiniestrosVigentes(desde, hasta, página)
  → HDS00009 NO trae la serie del vehículo: sale de HWS_DDETAIL con el mismo IDDocto
  → mismo filtro de flotilla propia (COMPRAS_DETALLES); lo ajeno se omite sin error
  → GetFiles identity "H04", ValuePK = IDSiniestro  (¡NO IDDocto!)
  → GuardarSiniestroHandler → SINIESTROS + documentos a FTP `Seguros/Siniestros/`
```

**Fase 2 — bitácora de comentarios** (corre al final del lote, sobre el mismo rango)
```
H03314011  BuscarBitacoraPorFecha(desde, hasta, página)   [filtro fijo Procedencia="Reclamaciones"]
  → solo comentarios con IsAutom == 0 (los automáticos se descartan)
  → vincula por IDSiniestro contra SINIESTROS.SIN_FOLIO_SICAS
  → inserta en SINIESTROS_ESTATUS con estatus fijo "EN TRAMITE" (TES_ID=45)
```

⚠️ **Dos consecuencias de esta fase que hay que tener presentes siempre:**
1. **El vínculo por `SIN_FOLIO_SICAS` es el único que existe, y falla en silencio.** Si esa columna está NULL —lo está en 33,783 de 34,289 siniestros— `BuscarIdPorFolioSicas` devuelve null, el `continue` descarta el comentario y **no se registra nada en ningún log**. Por eso el defecto pasó meses inadvertido. Ver `DIAGNOSTICO_FALLOS_TEAMS.md`.
2. **`SIN_FOLIO_SICAS` sí se rellena en updates**, no solo en inserts. Reprocesar un siniestro viejo (por folio o por rango de `FCaptura`) le escribe el campo que le faltaba — es el paso 1 de cualquier remediación.

⚠️ **La Fase 2 solo corre en el barrido por rango.** `ProcesarLoteSiniestroHandler.Handle` hace `return` tras `ProcesarPorReporte`, así que **reprocesar un folio individual nunca actualiza estatus ni comentarios**: solo la cabecera y los documentos.

### Reglas peculiares de Siniestros
- **`SIN_SEG_ID` es obligatorio en la práctica**: el siniestro se ata a su póliza por `SEG_NO_POLIZA` + inciso (fallback inciso 1, prioriza `SEG_ACTIVO`). **Si la póliza no existe en `SEGUROS`, se lanza error y el siniestro no se guarda.** Es decir: el ETL de Seguros debe haber corrido antes.
- **`SIN_CDE_ID` es NOT NULL**: no existe el concepto de "vehículo externo" para siniestros (a diferencia de Seguros, que tiene el fallback `VHC_ID`).
- **`TOR_ID` siempre resuelve a `"SISTEMA"`** — literal fijo heredado del legacy, no viene de SICAS.
- **El ETL nunca cierra un siniestro.** Todo comentario de bitácora se guarda como `EN TRAMITE` (`TES_ID=45`), incluso cuando el texto de SICAS empieza con `"CERRADO:"`. En agosto 2026, 144 de 252 comentarios que decían "CERRADO" quedaron formalmente en trámite. Los estatus terminales (`FECHA DE RESOLUCIÓN`=47, `SOLICITUD`=43) **solo los escribe gente a mano**. Es una decisión de diseño del 04/08/2026, no un bug — pero implica que el cierre es necesariamente manual mientras el negocio no defina el mapeo.
- **Cómo distinguir lo automático de lo manual en `SINIESTROS_ESTATUS`:** los registros del ETL llevan hora real (`18:14:00`); los capturados a mano llevan `00:00:00`. Es el truco de diagnóstico más útil de este módulo.
- **`SES_USU_ID`** tiene dos caminos: si el estatus es `"SOLICITUD"` se busca al ejecutivo por nombre en `USUARIOS`; para cualquier otro estatus se usa un **mapeo hardcodeado** `IdUser` (SICAS) → `USU_ID` (LumoSys) llamado `EjecutivoSicasMap` en `SiniestroRepository`, con default `416`. No hay catálogo en BD que lo derive.
- **`MontoIndemnizable` = 0 solo si el tipo contiene "ROBO"**, si no queda null. `MontoDeducible` nunca se llena (el legacy tampoco lo hacía).

---

## 6. Contrato de SICAS REST — lo que hay que saber sí o sí

Base URL real: **`https://security-services.sicasonline.info/api`**
(la URL `lumo.intelisiscloud.com:9081` que aparecía en documentación vieja es incorrecta e inalcanzable).

### Token
- `POST /Security/GetToken?sUserName={usuario}&sPassword={contrasena}` — **autenticación básica, NO ApiKey**.
- TTL de **3 minutos**; el cliente lo renueva proactivamente a los 2.5 min.
- El endpoint **exige `Content-Length` aunque el body vaya vacío** (HTTP 411 si se omite) → por eso ese llamado usa `HttpClient` con `StringContent` vacío en vez de RestSharp.
- El token se manda en el header `Authorization` **sin el prefijo `Bearer`**.

### Reportes (`POST /Report/ReadData`)
- El **KeyCode va en el header `Prop_KeyCode`**, no en el body.
- El body son parámetros de formulario: `PageRequested`, `ItemsForPages`, `SortFields`, `Conditions`, `FormatResponse=2` (JSON).
- `Conditions` es un string con el formato
  `Letrero;TipoFiltro;SubFiltro;Valores;Texto;PosTitle;ChangeTable;Tabla.Campo`, separado por `!` si hay varias.
  `SubFiltro`/`PosTitle`/`ChangeTable` son enteros: van `0`/`0`/`-1` cuando no aplican — **nunca vacíos**, SICAS truena con *"La cadena de entrada no tiene el formato correcto"*.
- La respuesta tiene forma `{"Response":[{"<NombreTabla>":{"Data":[...]}}]}` y el nombre de la tabla varía por KeyCode → se toma siempre la **primera propiedad** del objeto.

### ⚠️ Las dos trampas de fechas (ya corregidas, no reintroducirlas)
1. **Formato `dd/MM/yyyy`, nunca ISO.** Con `yyyy-MM-dd` el filtro no truena: simplemente devuelve `Data:[]` en silencio.
2. **El límite superior de un filtro de rango (`FilterType=3`) es EXCLUSIVO del día indicado.** Un rango `27/07 → 28/07` NO trae nada capturado el 28/07. Por eso los tres métodos con filtro de rango suman `+1 día` a `Hasta` antes de formatear. Consecuencia: **cualquier consulta con `Desde` = `Hasta` devuelve siempre 0 registros.**

### Documentos digitales
- **Usar `POST /DigitalCenter/GetFiles`, NUNCA `GetFilesAdv`.** `GetFilesAdv` depende de una configuración por agente/corredor que **no está dada de alta para esta licencia** y devuelve `"Internal error server. Referencia a objeto no establecida..."` incluso para pólizas con documentos reales. Ese error **no significa "sin documentos"**.
- Body: `{"FolderRead":1, "Identity":"H02"|"H04", "ValuePK":<IDDocto|IDSiniestro>, "TypeReadBasic":true, "ReadRecursive":false, "URLSecurity":0}`.
- `Identity`: **`H02` = pólizas** (ValuePK = IDDocto), **`H04` = siniestros** (ValuePK = **IDSiniestro**, no IDDocto).
- Existe un tercer endpoint `GetFilesByDocto` pensado para un checklist formal de documentación; se probó y no encontró nada, así que no se usa.

### Robustez
- `EjecutarConReintentos`: SICAS a veces corta la conexión (StatusCode 0) ante ráfagas → 3 intentos con backoff 2s/4s. **No** reintenta 4xx/5xx reales.
- `ReadData<T>`: si el JSON viene malformado, elimina el carácter ofensor y reintenta hasta 5 veces **sobre el mismo contenido**, sin volver a golpear la red.

### KeyCodes
| KeyCode | Datos |
|---|---|
| `H03117` | Pólizas vigentes |
| `HWS_DDETAIL` | Detalle del vehículo (serie/VIN) — usado por Seguros **y** Siniestros |
| `H03400` | Primas |
| `H03400_019` | Coberturas |
| `H03120` | Cobranza |
| `HDS00009` | Siniestros |
| `H04270_O` | Bitácora por `ClaveBit` — **no usable vía REST tal cual**, ver limitación abajo |
| `H03314011` | Bitácora por fecha — sí funciona vía REST |

---

## 7. Modos de ejecución del ETL

Cuatro `BackgroundServices` registrados en `Program.cs`. **Dos capas que se complementan, no se sustituyen.**

| Servicio | Cuándo corre | Ventana | ¿Condicional? |
|---|---|---|---|
| `SegurosEtlBackgroundService` | diario a `EtlSchedule:Seguros` (default 00:05) | ayer → hoy | **No, siempre corre** |
| `SiniestrosEtlBackgroundService` | diario a `EtlSchedule:Siniestros` (default 00:10) | ayer → hoy | **No, siempre corre** |
| `SegurosEtlIntervaloBackgroundService` | cada `IntervaloMinutosSeguros` min | últimos `VentanaMinutosSeguros` min | Sí, solo si el intervalo es > 0 |
| `SiniestrosEtlIntervaloBackgroundService` | cada `IntervaloMinutosSiniestros` min | últimos `VentanaMinutosSiniestros` min | Sí, solo si el intervalo es > 0 |

**El diario es la red de seguridad** (peor caso: 24 h de rezago). El de intervalo solo baja la latencia durante el día. Intervalo y ventana son configs **independientes a propósito**: con ventana > intervalo hay traslape entre corridas, lo que aguanta caídas cortas del servicio sin dejar huecos. Probado con intervalo 20 / ventana 60 (3× de traslape, tolera ~40-50 min de caída). En `appsettings.json` versionado ambos van en `0` — hay que activarlos por ambiente.

Todo el ETL es **idempotente por upsert**: reprocesar el mismo rango dos veces no duplica nada.

### Reprocesamiento manual
```http
POST /api/Etl/Seguros/Procesar    { "Desde": "2026-07-01", "Hasta": "2026-07-22" }
POST /api/Etl/Seguros/Procesar    { "Poliza": "20260000144709" }
POST /api/Etl/Seguros/Procesar    { "Serie": "JN8BT27T7MW128187" }
POST /api/Etl/Siniestros/Procesar { "FolioSiniestro": "1-202-2026-R-4295" }
```
⚠️ Para rangos históricos largos, **dividir en sub-rangos semanales**. Si el cliente HTTP cierra la conexión por timeout, el `CancellationToken` del request cancela el proceso en el servidor a media ejecución. No corrompe datos (cada registro va en su propia transacción), pero la Fase 2 de siniestros queda sin correr porque va al final del lote.

### Endpoints completos
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

---

## 8. Base de datos — tablas tocadas y sus trampas

Todo vive en **`dbLumoSys`** (`10.100.102.7\LUMODBPROD22`). No hay migraciones EF: el esquema es el del ERP legado y este proyecto solo lo mapea.

| Tabla | Uso |
|---|---|
| `SEGUROS` / `SEGUROS_DETALLES` | póliza y su detalle por vehículo |
| `SINIESTROS` / `SINIESTROS_ESTATUS` | siniestro y su bitácora de estatus |
| `ARCHIVOS_REPOSITORIOS` | registro de cada archivo subido |
| `DOCUMENTOS_UNIDADES` | vínculo archivo ↔ unidad (pólizas, `DUN_TDW_ID=4`) |
| `DOCUMENTOS_SINIESTROS` | vínculo archivo ↔ siniestro |
| `COMPRAS` / `COMPRAS_DETALLES` / `VEHICULOS` | fuente de verdad de "esto es de la flotilla propia" |
| `LOG_ERRORES` | log de errores compartido con todo LumoSys |
| catálogos | `ASEGURADORAS`, `EMPRESAS`, `USUARIOS`, `TIPOS_*` |

### ⚠️ `SIN_ID` no es identity
Confirmado por tres vías (`sys.columns.is_identity=0`, `COLUMNPROPERTY`, `sys.identity_columns` vacío) y por un INSERT de prueba. Tampoco tiene default ni SEQUENCE. Algún otro proceso de producción, no identificado, calcula `MAX+1` manualmente — por eso el historial de IDs no tiene huecos.
**Consecuencia real:** hasta el 03/08/2026 el ETL **nunca pudo insertar un siniestro genuinamente nuevo**. Los casos que "funcionaban" en realidad encontraban el siniestro ya creado por ese otro proceso y solo hacían UPDATE. Hoy `ObtenerSiguienteSinIdAsync` calcula `SELECT ISNULL(MAX(SIN_ID),0)+1 FROM SINIESTROS WITH (UPDLOCK, HOLDLOCK)` dentro de la misma transacción, y `SIN_ID` está marcado `ValueGeneratedNever()`.

### `ARC_ID` tampoco es identity
Se obtiene con `SP_ACTUALIZAR_SECUENCIAS`. **Firma real** (verificada en `INFORMATION_SCHEMA.PARAMETERS`, no coincide con nombres intuitivos): `@TABLA varchar (IN)`, `@ID int (IN)`, `@FolioSQ int (INOUT)`.

### `UseSqlOutputClause(false)`
`SEGUROS`, `SEGUROS_DETALLES`, `SINIESTROS`, `SINIESTROS_ESTATUS`, `ARCHIVOS_REPOSITORIOS`, `DOCUMENTOS_UNIDADES`, `DOCUMENTOS_SINIESTROS` y `TIPOS_NO_PASAJEROS` **tienen triggers**: SQL Server no permite `OUTPUT` sin `INTO` sobre ellas. `LOG_ERRORES` no tiene triggers y por eso no lo necesita.

### `DOCUMENTOS_UNIDADES` no admite vehículo externo
`DUN_CDE_ID` y `DUN_CLI_ID` son NOT NULL. Si la serie no tiene `COMPRAS`/`COMPRAS_DETALLES`, el archivo **ya quedó subido al FTP pero no se vincula**. Es una limitación conocida del esquema, no un error.

### Deuda conocida
El orden actual es **registrar-luego-subir**: si el FTP falla después de `RegistrarArchivoAsync`, queda una fila huérfana en `ARCHIVOS_REPOSITORIOS`. Vale la pena invertir el orden en una futura pasada. Huérfana conocida pendiente de limpieza: `ARC_ID=2466930`.

---

## 9. FTP de documentos

- Host `10.100.102.6`, sitio IIS FTP `FtpSicasDocumentos`, ruta física `C:\inetpub\wwwroot\LumoSys\Content\Documentos`, autenticación de paso a través.
- **Pólizas → `Fleet/Documentos Unidades/2/0/`**. ⚠️ **NO es `1/0`**, aunque tanto el legacy como `API.LumoSys` dicen `1/0` — ese valor está desactualizado en ambas fuentes. Verificado empíricamente el 24/07/2026: `1/0` no recibía archivos genuinos desde el 28/05/2026. **No existe columna en la BD que determine "1" vs "2"**; simplemente es el destino que usa hoy la aplicación real de producción (que no se identificó en ningún repo accesible).
  - Si vuelve a parecer desactualizado: listar ambas carpetas por FTP y cruzar los `ARC_ID` más recientes contra `DOCUMENTOS_UNIDADES.DUN_TDW_ID=4` ordenado por `ARC_FECHA_REGISTRO DESC`.
- **Siniestros → `Seguros/Siniestros/`**.
- Nombre de archivo: pólizas `{ARC_ID}.pdf`; siniestros `{nombreOriginal}_3_{ddMMyyHHmmss}.{ext}`. El nombre original se conserva en `ARC_NOMBRE_ARCHIVO`.
- **Trampa de la cuenta de servicio:** la cuenta de Windows usada por el FTP tenía marcado *"El usuario debe cambiar la contraseña en el siguiente inicio de sesión"*, lo que produce `530 User cannot log in` sin más detalle y no se puede resolver por FTP. Se corrigió desmarcándolo y marcando "la contraseña nunca expira". Si reaparece, revisar en orden: (1) permisos NTFS, (2) reglas de autorización FTP, (3) autenticación básica habilitada, (4) estado de la cuenta de Windows.

---

## 10. Configuración y credenciales

- **`appsettings.json`** — versionable, **solo PLACEHOLDER**. Nunca escribir credenciales aquí.
- **`appsettings.Local.json`** — en `.gitignore`, credenciales reales, se carga automáticamente.
- Origen de las credenciales reales: `C:\Desarrollo\Programas\PROD\Integraciones\appSettings.json` y `B:\Dessaarollos\API.Lumosys\API.LumoSys\appsettings.json`.

```
ConnectionStrings.LumoSys       → dbLumoSys (10.100.102.7\LUMODBPROD22)
SICAS.BaseUrl/Usuario/Contrasena
SFleet.BaseUrl/Email/Password   → https://fleetsoluciones.com/api/v1
Ftp.Host/Usuario/Contrasena
Ftp.RutaDestinoPolizas          → Fleet/Documentos Unidades/2/0/   (⚠ ver §9)
Ftp.RutaDestinoSiniestros       → Seguros/Siniestros/
EtlSchedule.Seguros / Siniestros                 → hora del barrido diario
EtlSchedule.IntervaloMinutosX / VentanaMinutosX  → capa de intervalo (0 = apagada)
Aplicaciones.Seguros = 11  ·  Aplicaciones.Siniestros = 12
Urls                            → http://0.0.0.0:5096
```

`Aplicaciones` alimenta `LER_ORIGEN` en `LOG_ERRORES` (`LumoSys.Integraciones.Seguros` / `.Siniestros`). Hubo un bug: `AplicacionOptions` nunca se registraba en DI, así que siempre resolvía a sus defaults (5/6) en vez de 11/12. Corregido en `Program.cs`, pero **lo escrito antes de esa corrección quedó con los IDs equivocados**.

---

## 11. Observabilidad

Dos destinos, siempre en paralelo:
1. **`dbLumoSys.dbo.LOG_ERRORES`** — la misma tabla que usa el resto de LumoSys. Mapeo: `LER_ORIGEN = "LumoSys.Integraciones.{Seguros|Siniestros}"`, `LER_MENSAJE_ERROR = "[Nivel] descripción"` (hasta 4000 chars), `LER_TLG_ID = 2` (PROCESO AUTOMATICO) **fijo para todo** —asunción no confirmada contra el negocio; si algún día se quiere distinguir barridos automáticos de reprocesos manuales, habría que usar `3` (PROCESO MANUAL)—, `LER_NO_ERROR = 0`, `LER_REVISADO = false`.
2. **Archivo diario** `C:\LumoSys\Programas\Sicas\Log dd-MM-yyyy.txt` (`LogErroresArchivoService`, singleton con `SemaphoreSlim`).

Catálogo `TIPOS_LOG`: `1=CARGA DE LAYOUTS, 2=PROCESO AUTOMATICO, 3=PROCESO MANUAL, 4=REPORTE, 6=TRIGGER, 7=SISTEMA`.

---

## 12. Despliegue

Corre como **servicio de Windows**, no bajo IIS: la app es esencialmente 4 `BackgroundServices`, e IIS recicla/duerme el App Pool por inactividad salvo configuración especial, lo cual es más frágil.

```bash
cd src/API
dotnet publish -c Release -r win-x64 --self-contained true -o C:\Publish\LumoSysIntegraciones
```
⚠️ **El publish copia `appsettings.Local.json` con credenciales reales tal cual a la salida** — tratar esa carpeta como contenido sensible.

```
sc create LumoSysIntegraciones binPath= "C:\LumoSys\Programas\Sicas\API\LumoSys.Integraciones.API.exe" start= auto DisplayName= "LumoSys Integraciones"
sc failure LumoSysIntegraciones reset= 86400 actions= restart/60000/restart/60000/restart/60000
sc start LumoSysIntegraciones
```
Servidor destino: `10.100.102.6` (el mismo del IIS/FTP de LumoSys), carpeta `C:\LumoSys\Programas\Sicas\API\`.

---

## 13. Estado actual y pendientes

**Historial de commits:** solo dos.
- `bdcbb09` (29/07/2026) — integración inicial.
- `2b8decf` (04/08/2026) — corrige `SIN_ID` sin identity y la Fase 2 de siniestros.

### ✅ El despliegue SÍ se hizo (verificado 24/08/2026)
El `CLAUDE.md` del repo dice que el publish del 04/08/2026 quedó pendiente de copiarse al servidor. **Eso ya no es cierto.** Verificado contra `dbLumoSys`: hay siniestros creados el 24/08/2026 con `SIN_FOLIO_SICAS` poblado y estatus escritos automáticamente hasta las 18:14 del mismo día. El servicio en `10.100.102.6` corre la versión con los fixes de agosto.

### ⚠️ Pendiente principal — `SIN_FOLIO_SICAS` en NULL (33,783 siniestros)
`SIN_FOLIO_SICAS` es el **único** vínculo que la Fase 2 usa para asociar un comentario de bitácora con su siniestro, y esa columna **solo empezó a poblarse el 04/08/2026**. Resultado: **98.5 % de los siniestros son invisibles para la Fase 2 de forma permanente** — sus comentarios se descartan en silencio (`BuscarIdPorFolioSicas` devuelve null y el `continue` no registra nada).

De ellos, **1,348 siguen vivos** (con actividad en 2026) y son los que el área está capturando a mano vía el hilo de Teams. Diagnóstico completo, evidencia y plan de remediación en **`DIAGNOSTICO_FALLOS_TEAMS.md`**.

### ⚠️ Módulo Seguros en bucle de error
32 pólizas fallan en cada corrida del modo intervalo (~21 min) por catálogos faltantes —sobre todo ejecutivos que no existen en `USUARIOS`— y generaron **5,449 filas en `LOG_ERRORES` en 60 días** (una sola póliza, 3,231). No hay backoff ni supresión de duplicados. Esas pólizas nunca se guardan, y el ruido entierra los errores del resto de LumoSys en una tabla compartida.

### Otros pendientes
- Limpiar la fila huérfana `ARC_ID=2466930` en `ARCHIVOS_REPOSITORIOS`.
- Invertir el orden subir→registrar de documentos de póliza.
- Apagar el ETL legacy de `C:\Desarrollo\Programas\PROD\Integraciones` cuando este proyecto quede confirmado en producción.
- Decidir si `LER_TLG_ID` debe distinguir 2 (automático) de 3 (manual).

### Limitación conocida sin resolver
No se puede traer el **historial completo de estatus de un siniestro puntual**. El legacy obtenía el `ClaveBit` con una operación SOAP (`WS_Siniestros`/`Param6`, parseo de pseudo-XML por regex) **sin equivalente confirmado en REST**. Los intentos de filtrar `H04270_O` por `DatBitacora.IDSiniestro` fallan con "nombre de columna no válido". Por eso `SINIESTROS_ESTATUS` solo se llena **incrementalmente día a día** vía la Fase 2. Si se necesita el historial completo de un siniestro, hay que investigar si existe un KeyCode REST equivalente.

### Validaciones ya hechas (no repetir)
- Rango `27/07 → 29/07/2026` reprocesado tras el fix de fechas: 79 pólizas y 48 siniestros en SICAS, **0 pérdidas reales** — lo omitido eran series de terceros, correctamente descartadas.
- Backfill de bitácora `15/06 → 04/08/2026` en 7 sub-rangos semanales: `SINIESTROS_ESTATUS` pasó de 251,903 a 252,620 filas (+717).
- Documentos end-to-end verificados con pólizas reales `L0000019478-0` y `L0000019601-0`, sin duplicar al reprocesar.

---

## 14. Notas operativas sueltas

- **Cómo consultar `dbLumoSys` desde el repo del Hub (solo lectura).** El `.env` de `cepademrh` trae `LUMOSYS_SQL_HOST`, `LUMOSYS_SQL_PORT`, `LUMOSYS_SQL_DB`, `LUMOSYS_SQL_USER`, `LUMOSYS_SQL_PASSWORD` con la cuenta de lectura `claude_read`. **No hay `sqlcmd` ni `bcp` instalados**; sí hay Python con `pyodbc`, `pymssql` y `sqlalchemy`.
  - **`pymssql` no sirve**: falla con `Logon failed ... due to trigger execution` (hay un trigger de logon en el servidor que lo rechaza).
  - **Usar `pyodbc` con `ODBC Driver 18 for SQL Server`**, host y puerto separados por coma y `TrustServerCertificate=yes`:
    `DRIVER={ODBC Driver 18 for SQL Server};SERVER={host},{port};DATABASE=dbLumoSys;UID=...;PWD=...;TrustServerCertificate=yes`
- **`sqlcmd` desde Git Bash** (si algún día se instala) falla con `Named Pipes Provider: Could not open a connection` contra `10.100.102.7\LUMODBPROD22`. Forzar TCP: `-S "tcp:10.100.102.7\LUMODBPROD22"`.
- El tope de **30 páginas** en los barridos (3,000 registros de pólizas/siniestros, 30,000 comentarios) **no es silencioso**: si se alcanza, se escribe un aviso en bitácora. Si aparece ese aviso, hay registros sin procesar y hay que partir el rango.
- El mensaje `"no pertenece a la flotilla propia; se omite"` en los logs **es normal y esperado**, no un error: son pólizas y siniestros de terceros gestionados por la misma correduría.
