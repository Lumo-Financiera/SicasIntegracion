# Reglas de alerta en Sentry

Esta es la parte que **no vive en el código**: hay que configurarla una vez en la consola de
Sentry, en *Alerts → Create Alert*. El código ya pone las etiquetas sobre las que se apoyan.

## La etiqueta que lo decide todo: `alerta`

Cada evento sale con `alerta` = `critica` o `informativa`. Las reglas se escriben contra esta
etiqueta y **no contra el nivel**, a propósito: si mañana cambia el criterio de qué es grave, se
ajusta el clasificador en un solo sitio y las reglas siguen valiendo sin tocarlas.

| Etiqueta | Qué significa | Notifica |
|---|---|---|
| `alerta:critica` | Afecta a todo el flujo: nada se sincroniza | Sí, correo inmediato |
| `alerta:informativa` | Afecta a un registro concreto | No, solo queda en el dashboard |

## Reglas a crear

### 1. Fallos críticos — correo inmediato

```
Condición : The event's tags match  alerta  equals  critica
Filtro    : (ninguno)
Acción    : Send a notification to <correo del equipo>
Frecuencia: 30 minutos
```

Cubre: base de datos caída, SICAS o SFleet sin autenticar, el FTP rechazando todos los
documentos, defectos de programación (`NullReference`, `OutOfMemory`) y la vinculación de la
bitácora rota.

### 2. Degradación sostenida — resumen

```
Condición : The issue is seen more than 50 times in one hour
Filtro    : The event's tags match  alerta  equals  informativa
Acción    : Send a notification to <correo del equipo>
Frecuencia: 24 horas
```

Un fallo informativo aislado no interesa; que el mismo se repita cincuenta veces en una hora sí,
porque deja de ser un registro y empieza a ser un patrón.

### 3. El ETL no se ejecutó — correo inmediato

Esto no es una regla de alerta sino un **monitor de Cron**, que el código ya da de alta solo
(ver `SentryMonitoreoEtl`). En *Crons* aparecen `etl-seguros-diario` y `etl-siniestros-diario`.
Solo hay que asignarles destinatario.

Es la alerta más importante del conjunto: cubre el caso en que no llega ningún error **porque el
proceso no corrió**, que es el único fallo que ninguna otra regla puede detectar.

### 4. Todo lo demás

Sin regla. Queda en *Issues* para consulta y auditoría.

## Verificación

Las reglas del clasificador están cubiertas por pruebas; se ejecutan con el proyecto
`probar_clasificador` del scratchpad y validan 16 escenarios, entre ellos:

- `ErrorDeNegocio` y `PolizaNoRegistradaException` → `Warning` / informativa
- SFleet 422 y SICAS 404 → `Warning` / informativa
- `DbUpdateException`, `NullReferenceException`, `OutOfMemoryException` → `Fatal` / crítica
- SICAS 503, 429 y sin respuesta → `Error` / crítica
- Un evento sin excepción (los `log.LogError`) conserva `Error` / crítica
- Un evento ya marcado como crítico desde el código manda sobre cualquier regla

## Qué esperar después del despliegue

Antes, una corrida con el FTP caído producía **más de 300 eventos de nivel Error** —uno por
documento— y otros tantos por los rechazos de SFleet. Ahora cada fallo individual queda como
rastro (*breadcrumb*) y al cerrar la corrida se emite **un solo evento** con el conteo, crítico
solo si falló el 100%.

Si el volumen de notificaciones sigue siendo alto después de esto, el problema ya no es de
configuración: significa que hay varias integraciones caídas a la vez, que es exactamente lo que
las alertas deben decir.
