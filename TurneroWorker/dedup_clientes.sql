-- ============================================================
-- Eliminar clientes duplicados por teléfono
-- Conserva el registro con menor id.
-- NO elimina duplicados si alguno tiene turno pendiente
-- (estado = 'confirmado' y fecha_inicio >= NOW()).
-- ============================================================

BEGIN;

-- 1. Ver qué se va a eliminar antes de borrar (opcional, comentar en prod)
SELECT
    c.id,
    c.nombre,
    c.telefono,
    c.fecha_creacion,
    'SERÁ ELIMINADO' AS accion
FROM turnero.clientes c
WHERE c.telefono IN (
    -- Teléfonos duplicados SIN turno pendiente en ninguna de sus copias
    SELECT telefono
    FROM turnero.clientes
    GROUP BY telefono
    HAVING COUNT(*) > 1
    -- Excluir teléfonos donde CUALQUIER cliente tiene turno pendiente
    AND telefono NOT IN (
        SELECT DISTINCT cl.telefono
        FROM turnero.clientes cl
        JOIN turnero.turnos t ON t.cliente_id = cl.id
        WHERE t.estado = 'confirmado'
          AND t.fecha_inicio >= NOW()
    )
)
-- Excluir el que tiene el menor id (ese se conserva)
AND c.id NOT IN (
    SELECT MIN(id)
    FROM turnero.clientes
    GROUP BY telefono
)
ORDER BY c.telefono, c.id;

-- ============================================================
-- 2. Eliminar los duplicados
-- Descomentá el DELETE cuando estés seguro con el SELECT de arriba.
-- ============================================================

/*
DELETE FROM turnero.clientes
WHERE telefono IN (
    SELECT telefono
    FROM turnero.clientes
    GROUP BY telefono
    HAVING COUNT(*) > 1
    AND telefono NOT IN (
        SELECT DISTINCT cl.telefono
        FROM turnero.clientes cl
        JOIN turnero.turnos t ON t.cliente_id = cl.id
        WHERE t.estado = 'confirmado'
          AND t.fecha_inicio >= NOW()
    )
)
AND id NOT IN (
    SELECT MIN(id)
    FROM turnero.clientes
    GROUP BY telefono
);
*/

-- Si estás conforme con el resultado, ejecutá COMMIT; de lo contrario ROLLBACK;
ROLLBACK; -- cambiá a COMMIT cuando quieras aplicar el DELETE real
