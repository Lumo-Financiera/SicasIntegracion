# Diagnóstico — fallos reportados en el hilo de Teams (LUMOSYS / SICAS)

> Análisis del hilo operativo de Teams contra el código de `SicasIntegracion` **y contra datos reales de `dbLumoSys`**.
> Verificado el 24/08/2026 mediante consultas de solo lectura (usuario `claude_read`).
> Complementa a `CONTEXTO_INTEGRACION_SICAS.md`.

---

## 1. Resumen ejecutivo

**Causa raíz encontrada:** de los **34,289 siniestros** en `dbLumoSys`, **33,783 (98.5 %) tienen
`SIN_FOLIO_SICAS` en NULL**. Ese campo es el **único** vínculo que la Fase 2 del ETL usa para asociar un comentario
de bitácora de SICAS con su siniestro en LumoSys. Si está NULL, **los comentarios de ese siniestro nunca se
guardan y nunca se guardarán** — el ETL no tiene forma de encontrarlo.

Todos los folios que aparecen en el hilo de Teams son siniestros con `SIN_FOLIO_SICAS` NULL. El equipo los está
capturando a mano porque **el sistema literalmente no puede actualizarlos**.

El hilo de Teams no es un canal de incidencias: es el proceso manual que compensa este defecto.

| # | Hallazgo | Severidad | Estado |
|---|---|---|---|
| **H0** | **33,783 siniestros con `SIN_FOLIO_SICAS` NULL → invisibles para la Fase 2, de forma permanente** | 🔴 Crítica | ✅ **CONFIRMADO con datos** |
| **H1** | El reproceso individual de un siniestro no ejecuta la Fase 2 | 🟡 Media | ✅ Confirmado en código |
| **H2** | Ningún siniestro se cierra solo: el ETL siempre escribe `EN TRAMITE` | 🔴 Alta | ✅ **CONFIRMADO con datos** |
| **H7** | **Módulo Seguros en bucle de error: 32 pólizas fallan cada ~21 min, 5,449 filas de log en 60 días** | 🔴 Alta | ✅ **CONFIRMADO con datos** |
| **H5** | `ProcesarPorReporte` procesa solo el primer resultado de SICAS | 🟡 Media | ✅ Confirmado en código |
| **H4** | `BuscarPorReporte` filtra por columna sin calificar | 🟡 Media | ⏳ Por verificar contra SICAS |
| **H6** | El incidente inicial ("error al generar reportes") | ⚪ N/D | ⏳ Falta la imagen |

### ⚠️ Correcciones a la primera versión de este diagnóstico

La versión anterior (basada solo en lectura de código) contenía **dos afirmaciones equivocadas**, ya corregidas:

- **~~H3: "los fixes de agosto no se desplegaron"~~ → REFUTADO.** El servidor **sí** está actualizado. Hay
  siniestros creados hoy (24/08) con `SIN_FOLIO_SICAS` poblado y estatus escritos automáticamente hasta las 18:14.
  El despliegue se hizo; el `CONTEXTO` estaba desactualizado en ese punto.
- **~~"Los folios de 14 dígitos son pólizas"~~ → REFUTADO.** Ninguno de los 9 existe en `SEGUROS`; los 9 existen en
  `SINIESTROS`. **Los tres formatos del hilo son `NumReporte` de siniestros**, de tres aseguradoras distintas.
  La recomendación anterior de mandarlos al endpoint de Seguros era incorrecta.

---

## 2. H0 — La causa raíz: `SIN_FOLIO_SICAS` en NULL 🔴

### El mecanismo

`ProcesarBitacoraDia` (Fase 2) vincula cada comentario de SICAS con su siniestro así:

```csharp
// H03314011 no trae NumReporte (folio) — solo IDSiniestro
int? siniestroId = await siniestroRepo.BuscarIdPorFolioSicas(item.IDSiniestro.Value, ct);
if (siniestroId is null) continue;        // ← sale en silencio
```

Y `BuscarIdPorFolioSicas` busca `WHERE SIN_FOLIO_SICAS == idSiniestroSicas`. **Si el siniestro tiene NULL en esa
columna, no hay match, y el comentario se descarta sin log ni aviso.**

`SIN_FOLIO_SICAS` solo se llena en `UpsertSiniestroAsync`, y esa columna **empezó a poblarse apenas el 04/08/2026**
(fue parte del fix de ese día). Todo lo anterior quedó NULL para siempre.

### La evidencia

**Dimensión del problema:**

| Estado | Siniestros | Rango de fechas |
|---|---|---|
| `SIN_FOLIO_SICAS` poblado | **506** | 05/03/2026 → 01/01/2027 |
| **`SIN_FOLIO_SICAS` NULL** | **33,783** | 20/07/2017 → 10/08/2026 |

El corte coincide con el backfill del 04/08/2026 (que cubrió 15/06→04/08): junio-agosto 2026 tienen folio,
todo lo anterior no.

| Mes de creación | Sin folio | Con folio |
|---|---|---|
| 2026-08 | 2 | **221** |
| 2026-07 | 1 | **209** |
| 2026-06 | 81 | 74 |
| 2026-05 | **222** | 0 |
| 2026-04 | **200** | 0 |
| 2026-03 y anteriores | **todos** | ~0 |

**Prueba definitiva — correlación 9 de 9.** Se clasificaron los estatus de cada folio del hilo según si fueron
escritos por el ETL (llevan hora real) o capturados a mano (llevan `00:00:00`):

| Folio del hilo | `SIN_FOLIO_SICAS` | Estatus manuales | Estatus del ETL |
|---|---|---|---|
| `1-202-2026-R-304` | **NULL** | 23 | **0** |
| `1-202-2026-R-4251` | **NULL** | 7 | **0** |
| `1-205-2026-R-26` | **NULL** | 6 | **0** |
| `1-211-2026-R-5141` | **NULL** | 18 | **0** |
| `448210` | **NULL** | 42 | **0** |
| `459923` | **NULL** | 7 | **0** |
| `1-202-2026-R-4702` | 34483 | 2 | **3** ✅ |
| `20260000148505` | 34596 | 2 | **3** ✅ |
| `1-202-2026-R-5895` | 34995 | 2 | **2** ✅ |

**Sin excepciones:** los folios con NULL tienen **cero** actualizaciones automáticas; los que tienen folio SICAS
sí las reciben. Los tres folios que el propio hilo marca como reincidentes
(`1-202-2026-R-304`, `1-205-2026-R-26`, `1-211-2026-R-5141`) están los tres en el grupo NULL.

**El mismo patrón a escala global:**

| Mes | Estatus manuales (00:00) | Estatus del ETL (con hora) |
|---|---|---|
| 2026-08 | 394 | 419 |
| 2026-07 | 1,845 | 725 |
| 2026-06 | 1,627 | 269 |
| 2026-03 | 1,890 | **1** |

La automatización solo empezó a funcionar de verdad en julio-agosto, y **solo para siniestros nuevos**.

**Quién está capturando a mano** (agosto 2026): `CHRISTOFER RENATO DE LEÓN` (160), `OMAR JAEN RESENDIZ` (111),
`DAVID MORENO` (87), `JEAN PAUL ZEPEDA` (36). Son las mismas personas que aparecen pidiendo folios en el hilo.

### Alcance real de la remediación

No hay que rescatar los 33,783. Solo los que **siguen vivos** (con actividad en 2026): **1,348 siniestros**.

| Año de creación | Huérfanos vivos |
|---|---|
| 2026 | 1,118 |
| 2025 | 216 |
| 2024 y anteriores | 14 |

Acotar el backfill a **2025-2026 cubre 1,334 de 1,348 (98.9 %)**.

---

## 3. H2 — Ningún siniestro se cierra solo 🔴 CONFIRMADO

`ProcesarBitacoraDia` escribe siempre `Estatus = "EN TRAMITE"` (`TES_ID = 45`). Los datos lo confirman:

**Estatus escritos en agosto 2026, por tipo:**

| `TES_ID` | Descripción | N | Último registro |
|---|---|---|---|
| 45 | EN TRAMITE | **591** | 24/08 **18:14** ← ETL |
| 47 | FECHA DE RESOLUCIÓN | 115 | 18/08 **00:00** ← manual |
| 43 | SOLICITUD | 107 | 18/08 **00:00** ← manual |

**El ETL solo produce `EN TRAMITE`.** Los estatus terminales llevan todos hora `00:00:00`, es decir, los pone
una persona.

Y el costo concreto: en agosto hubo **252 comentarios cuyo texto empieza con "CERRADO"**, de los cuales
**144 quedaron marcados formalmente como `EN TRAMITE`**. Ejemplos reales de hoy:

```
CERRADO: TERCERO RESPONSABLE AL SALIR SU PERRO CRUZANDO...   → TES_ID 45 (EN TRAMITE)
CERRADO: SERVICIO REALIZADO CON EXITO.                        → TES_ID 45 (EN TRAMITE)
```

Esto explica con precisión la categoría *"me ayudan con **cerrar** este reporte"*: **el cierre automático no
existe**, por decisión de diseño del 04/08/2026 (no inferir el estatus del texto libre).

**Es una definición de negocio pendiente, no un bug.** El catálogo `TIPOS_ESTATUS` no tiene un valor "CERRADO";
hay que decidir si un comentario que empieza con "CERRADO" corresponde a `FECHA DE RESOLUCIÓN` (47) —que es lo que
la gente está poniendo a mano— o a otro valor.

---

## 4. H7 — Módulo Seguros en bucle de error 🔴 CONFIRMADO (hallazgo nuevo)

En los últimos 60 días, `LOG_ERRORES` acumuló **5,449 filas** del módulo Seguros, generadas por apenas
**32 pólizas distintas**. Una sola póliza (`867-2640`) produjo **3,231 filas**.

**Causa de los errores:**

| Causa | N | Último |
|---|---|---|
| Ejecutivo no existe en `USUARIOS` | **5,286** | 13/08 |
| Tipo de Uso no existe | 138 | 12/08 |
| Tipo de Valor de Seguro no existe | 23 | 18/08 |
| Otro | 25 | 10/08 |

Ejemplo: `Error guardando póliza 867-3676: El Ejecutivo 'HERNANDEZ MIRAN...' no se encontró registrado en USUARIOS`.

**El mecanismo:** el modo intervalo corre cada ~21 minutos sobre una ventana solapada. Las mismas pólizas vuelven
a entrar, vuelven a fallar por catálogo faltante, y vuelven a escribir en el log. **No hay backoff ni supresión de
errores repetidos.**

**Doble impacto:**
1. **Esas 32 pólizas nunca se guardan** — están permanentemente fuera de LumoSys.
2. **`LOG_ERRORES` es la tabla compartida por todo LumoSys.** 5,449 filas de ruido en 60 días entierran los
   errores reales de otros módulos.

**Solución de fondo, barata:** dar de alta los catálogos faltantes (el ejecutivo "HERNANDEZ MIRAN..." en `USUARIOS`,
los tipos de uso y de valor). Son ~32 casos concretos. Complementariamente, no re-loguear un error idéntico para la
misma póliza dentro de la misma ventana.

---

## 5. H1 — El reproceso individual no ejecuta la Fase 2 🟡

Sigue siendo cierto a nivel de código. En `ProcesarLoteSiniestroHandler.Handle` (líneas 23-32), cuando llega un
`FolioSiniestro` el método hace `return` **antes** de `ProcesarLote`, que es el único punto donde se invoca
`ProcesarBitacoraDia` (línea 63).

**Pero ahora se sabe que es secundario:** aunque se corrigiera, no serviría para los folios del hilo, porque
`SIN_FOLIO_SICAS` está NULL y la Fase 2 no podría vincular nada de todas formas.

**Lo importante — y esto sí es accionable hoy:** el reproceso individual **sí rellena `SIN_FOLIO_SICAS`**, porque
`UpsertSiniestroAsync` lo escribe también en la rama de update. Es decir, **el reproceso por folio es el paso 1 de
la cura, pero por sí solo no trae los comentarios.** Hacen falta los dos pasos (§7).

Es muy probable que soporte esté haciendo solo el paso 1, viendo que el estatus no cambia, y capturando a mano.

---

## 6. Hallazgos menores

**H5 — solo se procesa el primer resultado.** `ProcesarPorReporte` (línea 81) usa `resultados[0]` con
`ItemForPage = 5`. Si un folio devuelve varias filas en `HDS00009`, el resto se descarta **sin log**.

**H4 — filtro sin calificar.** `BuscarPorReporte` usa `ColumnName = "NumReporte"`, mientras todos los demás filtros
del proyecto usan `Tabla.Campo`. Requiere una llamada real a SICAS para confirmar si funciona.

**H6 — el incidente inicial.** *"validar la generación de reportes, marca error"* es cualitativamente distinto al
resto del hilo. Este ETL es headless y no genera reportes. En `LOG_ERRORES` no aparece nada con origen
`LumoSys.Integraciones.*` que corresponda a "generar un reporte". **Casi seguramente es del UI de LumoSys, no de
este módulo.** Sin la imagen no es diagnosticable.

---

## 7. Plan de remediación

### Paso 1 — Rellenar `SIN_FOLIO_SICAS` de los huérfanos vivos 🔴
Reprocesar por **rangos históricos de `FCaptura`** en sub-rangos semanales. La Fase 1 encuentra cada siniestro y
`UpsertSiniestroAsync` le escribe el `SIN_FOLIO_SICAS` que le faltaba:

```http
POST /api/Etl/Siniestros/Procesar { "Desde": "2025-01-01", "Hasta": "2025-01-08" }
...
```
Acotar a **2025-01-01 → 2026-06-15** cubre el 98.9 % de los huérfanos vivos. Usar timeout de cliente de 30+ min.

*Alternativa para casos urgentes:* reprocesar folio por folio
(`{"FolioSiniestro": "1-202-2026-R-304"}`), que también rellena el campo.

### Paso 2 — Traer los comentarios atrasados 🔴
Una vez poblado el campo, correr la Fase 2 sobre el periodo del que faltan comentarios:
```http
POST /api/Etl/Siniestros/Procesar { "Desde": "2026-06-01", "Hasta": "2026-08-24" }
```
Ahora sí vinculará. **Este paso debe ir después del Paso 1, nunca antes.**

### Paso 3 — Dar de alta los catálogos faltantes 🔴
Las 32 pólizas en bucle (§4). Detiene 5,449 filas de ruido y recupera pólizas que hoy no existen en LumoSys.

### Paso 4 — Definir la regla de cierre con el negocio 🟡
Decidir a qué `TES_ID` corresponde un comentario que empieza con "CERRADO". Mientras no se defina, el cierre
seguirá siendo manual **por diseño**, y el hilo de Teams seguirá existiendo aunque todo lo demás se arregle.

### Paso 5 — Correcciones de código 🟡
- Que `ProcesarPorReporte` ejecute también la Fase 2 sobre una ventana alrededor del siniestro (H1).
- Registrar un aviso cuando `BuscarIdPorFolioSicas` devuelva null — hoy los comentarios se pierden **en silencio**,
  que es la razón por la que este defecto pasó meses sin detectarse.
- Iterar todos los resultados en `ProcesarPorReporte`, o al menos loguear cuando hay más de uno (H5).
- Verificar H4 con una llamada real.

### Paso 6 — Proceso ⚪
Sustituir el seguimiento por Teams por un registro con folio, solicitante, acción, responsable y estatus final.
Encaja con el módulo de Mesa de Servicio del Hub.

---

## 8. Verificación posterior

Para confirmar que la remediación funcionó, esta consulta debe mostrar **crecimiento en la columna de la derecha**:

```sql
SELECT FORMAT(SES_FECHA_REGISTRO,'yyyy-MM') AS MES,
       SUM(CASE WHEN CAST(SES_FECHA_REGISTRO AS TIME)='00:00:00' THEN 1 ELSE 0 END) AS MANUAL,
       SUM(CASE WHEN CAST(SES_FECHA_REGISTRO AS TIME)<>'00:00:00' THEN 1 ELSE 0 END) AS AUTOMATICO
FROM SINIESTROS_ESTATUS WHERE SES_FECHA_REGISTRO >= '2026-01-01'
GROUP BY FORMAT(SES_FECHA_REGISTRO,'yyyy-MM') ORDER BY MES DESC;
```

Y esta debe tender a cero para los siniestros vivos:
```sql
SELECT COUNT(DISTINCT s.SIN_ID) AS HUERFANOS_VIVOS
FROM SINIESTROS s JOIN SINIESTROS_ESTATUS e ON e.SES_SIN_ID = s.SIN_ID
WHERE s.SIN_FOLIO_SICAS IS NULL AND e.SES_FECHA_REGISTRO >= '2026-01-01';
-- Valor al 24/08/2026: 1,348
```

---

## 9. Límites de este análisis

- **No se verificó el binario del servidor directamente** (no hay acceso a `10.100.102.6` desde aquí). Que el
  despliegue se hizo se infiere de datos: `SIN_FOLIO_SICAS` poblándose y estatus automáticos escritos hoy.
- **H4 no se probó contra SICAS** — requiere credenciales de la API, no de la base.
- **No se vieron las imágenes** del hilo, así que H6 queda sin diagnosticar.
- **Las fechas del hilo se desconocen**; el análisis se ancló en las fechas de los datos, no en las de los mensajes.
- Todas las consultas fueron **de solo lectura**. No se modificó ningún dato.

---

## 10. Nota sobre el archivo de origen

El `.md` del hilo llegó con **mojibake** (`Ã`, `Â`, `â€`): fue guardado interpretando UTF-8 como Windows-1252.
No afecta el análisis —los folios son ASCII— pero conviene corregir la exportación. La política de encoding del
repositorio exige UTF-8 sin BOM.
