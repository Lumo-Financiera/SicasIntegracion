# Plan de acción — arreglar la sincronización de siniestros

> Fecha: 25/08/2026 · Basado en el análisis de `DIAGNOSTICO_FALLOS_TEAMS.md`
> Este documento es la lista de pendientes. Se puede leer sin conocer el código.

---

## El problema en tres líneas

El área de Seguros pide por Teams, folio por folio, que se "actualicen" o "cierren" reportes de siniestros.
Lo hacen porque **el sistema no los está actualizando solo**: casi todos los siniestros de LumoSys les falta un
dato interno (`SIN_FOLIO_SICAS`) que es el único que el integrador usa para reconocerlos, así que los comentarios
que llegan de SICAS no encuentran a qué siniestro pegarse y se descartan.

**El hilo de Teams no es una lista de incidencias: es el trabajo manual que está tapando esta falla.**

### Los números

| Dato | Valor |
|---|---|
| Siniestros en LumoSys | 34,289 |
| Siniestros que el integrador **no puede reconocer** | **33,783** (98.5 %) |
| De esos, los que siguen activos y hay que rescatar | **1,348** |
| Comentarios que dicen "CERRADO" pero siguen "EN TRÁMITE" (agosto) | 144 de 252 |
| Filas de error repetido en `LOG_ERRORES` en 60 días | 5,449, de solo 32 pólizas |

---

## Cómo está organizado el plan

Cinco fases. **El orden importa**: la Fase 1 no sirve si no se hizo la Fase 0, y su paso 2 no sirve si no se hizo
el paso 1. Las demás son independientes entre sí.

```
Fase 0  Desplegar los logs        → sin esto se trabaja a ciegas
   │
Fase 1  Rescatar los siniestros   → esto es lo que apaga el hilo de Teams
   │
Fase 2  Catálogos faltantes       ─┐
Fase 3  Definir qué es "cerrado"  ─┼─ independientes, se pueden hacer en paralelo
Fase 4  Correcciones de código    ─┘
```

---

## Fase 0 — Desplegar los logs 🔴 *(primero, siempre)*

La API no dejaba registro de nada: corriendo como servicio de Windows, sus mensajes no tenían a dónde escribirse.
Ya está corregido en el código, falta publicarlo.

- [ ] Revisar los cambios y hacer el commit
- [ ] `dotnet publish -c Release -r win-x64 --self-contained true`
- [ ] Copiar al servidor `10.100.102.6` y reiniciar el servicio `LumoSysIntegraciones`
- [ ] Confirmar que se está escribiendo `C:\LumoSys\Programas\Sicas\Log dd-MM-yyyy.txt`

**Responsable:** TI / Infraestructura · **Esfuerzo:** bajo

**Por qué va primero:** el log nuevo cierra cada corrida con un resumen que incluye el contador
`comentarios_sin_vinculo`. Ese número es el termómetro de todo este plan: si baja, la Fase 1 funcionó. Sin
desplegar esto, no hay forma de saber si la reparación sirvió.

---

## Fase 1 — Rescatar los 1,348 siniestros activos 🔴 *(la que resuelve el problema)*

Son **dos pasos y el orden no es negociable.**

### Paso 1.1 — Rellenar el dato que falta

- [ ] Reprocesar por rangos de fecha, **en tramos semanales**, desde `2025-01-01` hasta `2026-06-15`

```http
POST /api/Etl/Siniestros/Procesar
{ "Desde": "2025-01-01", "Hasta": "2025-01-08" }
```

Son unas 75 llamadas. Cada una necesita un **timeout de cliente de 30 minutos o más**: si el cliente corta la
conexión, el proceso se detiene a medias en el servidor.

Ese rango cubre 1,334 de los 1,348 siniestros a rescatar (98.9 %).

### Paso 1.2 — Traer los comentarios atrasados

- [ ] Solo **después** de terminar el paso 1.1, reprocesar el periodo reciente

```http
POST /api/Etl/Siniestros/Procesar
{ "Desde": "2026-06-01", "Hasta": "2026-08-25" }
```

> ⚠️ **Hacer el 1.2 sin el 1.1 no cambia nada**: si el dato interno sigue faltando, los comentarios siguen sin
> encontrar a qué siniestro pegarse. Es el error más fácil de cometer en este plan.

- [ ] Confirmar en el log que `comentarios_sin_vinculo` bajó y `comentarios_nuevos` subió
- [ ] Avisar en el hilo de Teams qué folios quedaron actualizados

**Responsable:** TI, con autorización para escribir en producción · **Esfuerzo:** medio (son horas de proceso)

---

## Fase 2 — Dar de alta los catálogos faltantes 🔴 *(mejor resultado por esfuerzo)*

Hay **32 pólizas que llevan meses sin poder guardarse** porque traen valores que no existen en los catálogos de
LumoSys. En cada intento fallan otra vez y escriben otra fila de error.

| Causa del error | Veces en 60 días |
|---|---|
| El ejecutivo no existe en `USUARIOS` (sobre todo `HERNANDEZ MIRAN...`) | 5,286 |
| El Tipo de Uso no existe | 138 |
| El Tipo de Valor de Seguro no existe | 23 |

- [ ] Seguros define los valores correctos para esos 32 casos
- [ ] TI los da de alta en los catálogos de `dbLumoSys`
- [ ] Reprocesar esas pólizas y confirmar que ya se guardan

**Responsable:** Seguros (define) + TI (ejecuta) · **Esfuerzo:** bajo

**Doble beneficio:** recupera pólizas que hoy **no existen** en LumoSys, y deja de ensuciar `LOG_ERRORES`, que es
una tabla que comparte **todo** LumoSys — ese ruido está tapando los errores de los demás módulos.

---

## Fase 3 — Decidir qué significa "cerrado" 🔴 *(decisión de negocio)*

Hoy el integrador **nunca cierra un siniestro**. Guarda todos los avances como `EN TRÁMITE`, incluso cuando el
comentario de SICAS dice literalmente *"CERRADO: unidad reparada y entregada"*. Por eso el cierre siempre lo tiene
que hacer una persona a mano.

No es una falla del programa: fue una decisión tomada porque el catálogo `TIPOS_ESTATUS` **no tiene un valor
"CERRADO"** y nadie definió a cuál equivale.

- [ ] Seguros / SICÚRIKA define a qué estatus del catálogo corresponde un comentario que empieza con "CERRADO"
      *(el candidato natural es `FECHA DE RESOLUCIÓN`, que es el que la gente ya pone a mano)*
- [ ] Definir también el resto de prefijos que usa el taller (`EN PROCESO DE REPARACION`, `PEND CARPETA`, `SIN INGRESO A CDR`…)
- [ ] TI lo implementa una vez definido

**Responsable:** Seguros / SICÚRIKA · **Esfuerzo:** bajo para TI, requiere la decisión

> **Esta fase es la que realmente apaga el hilo de Teams.** Aunque todo lo demás quede perfecto, "cerrar reporte"
> es la mitad de las solicitudes, y hoy es imposible automatizarlo.

---

## Fase 4 — Correcciones de código pendientes 🟡

| # | Qué falta | Situación hoy |
|---|---|---|
| 4.1 | Que reprocesar **un folio suelto** también traiga sus comentarios | Solo avisa en el log que no lo hace. Es la causa de que los folios se repitan en Teams |
| 4.2 | Procesar todas las filas que devuelve SICAS para un folio, no solo la primera | Solo avisa que está descartando las demás |
| 4.3 | Verificar un filtro de consulta a SICAS que podría estar mal escrito | Sin comprobar; requiere credenciales de la API de SICAS |
| 4.4 | Subir el documento al FTP **antes** de registrarlo en la base | Al revés puede dejar registros huérfanos. Deuda vieja, bajo impacto |

- [ ] 4.1 — que el reproceso por folio ejecute también la búsqueda de comentarios
- [ ] 4.2 — iterar todos los resultados
- [ ] 4.3 — probar el filtro contra SICAS real
- [ ] 4.4 — invertir el orden y limpiar el registro huérfano `ARC_ID=2466930`

**Responsable:** TI · **Esfuerzo:** bajo cada uno

---

## Fase 5 — Que no vuelva a pasar 🟡

- [ ] Avisar automáticamente cuando `comentarios_sin_vinculo` sea mayor a cero *(el dato ya está en el log; falta que alguien lo vigile)*
- [ ] Reemplazar el seguimiento por Teams con un registro formal: folio, quién pidió, qué se pidió, responsable, estatus final
      *(encaja con el módulo de Mesa de Servicio del Hub)*
- [ ] Apagar el ETL viejo de `C:\Desarrollo\Programas\PROD\Integraciones`, que sigue programado en paralelo

---

## Pendiente sin diagnosticar

El **primer** mensaje del hilo de Teams —*"validar la generación de reportes, marca error"*, con una captura—
no corresponde a este integrador: no genera reportes ni tiene pantallas, y no hay nada suyo en el log de errores
que coincida. Casi seguro es del sistema LumoSys.

- [ ] Conseguir la captura de pantalla original para poder revisarlo

---

## Cómo saber si el plan funcionó

Dos consultas. La primera debe mostrar la columna **AUTOMATICO** creciendo mes con mes:

```sql
SELECT FORMAT(SES_FECHA_REGISTRO,'yyyy-MM') AS MES,
       SUM(CASE WHEN CAST(SES_FECHA_REGISTRO AS TIME)='00:00:00' THEN 1 ELSE 0 END) AS MANUAL,
       SUM(CASE WHEN CAST(SES_FECHA_REGISTRO AS TIME)<>'00:00:00' THEN 1 ELSE 0 END) AS AUTOMATICO
FROM SINIESTROS_ESTATUS WHERE SES_FECHA_REGISTRO >= '2026-01-01'
GROUP BY FORMAT(SES_FECHA_REGISTRO,'yyyy-MM') ORDER BY MES DESC;
```

*(Truco útil: los registros que puso el sistema traen hora real; los que capturó una persona quedan en `00:00:00`.)*

La segunda debe tender a cero:

```sql
SELECT COUNT(DISTINCT s.SIN_ID) AS PENDIENTES_DE_RESCATE
FROM SINIESTROS s JOIN SINIESTROS_ESTATUS e ON e.SES_SIN_ID = s.SIN_ID
WHERE s.SIN_FOLIO_SICAS IS NULL AND e.SES_FECHA_REGISTRO >= '2026-01-01';
-- Valor al 24/08/2026: 1,348
```

---

## Recomendación de arranque

**Esta semana:** Fase 0 y Fase 2. Son las de menor esfuerzo y la Fase 2 da resultado visible de inmediato.

**En paralelo:** iniciar la conversación de la Fase 3, porque depende de gente que no es de TI y es la que
realmente cierra el círculo.

**Cuando haya autorización para escribir en producción:** Fase 1.

---

## Para profundizar

| Documento | Contenido |
|---|---|
| `DIAGNOSTICO_FALLOS_TEAMS.md` | El análisis completo con la evidencia de cada hallazgo |
| `CONTEXTO_INTEGRACION_SICAS.md` | Cómo funciona el integrador por dentro |
| `OBSERVABILIDAD_Y_LOGS.md` | Cómo leer el log nuevo y recetas de diagnóstico |
