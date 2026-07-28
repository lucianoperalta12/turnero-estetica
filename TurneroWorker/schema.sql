-- Script SQL para la base de datos PostgreSQL 'gymadmin'
-- Crear esquema para la app de turnos y clientes

CREATE SCHEMA IF NOT EXISTS turnero;

-- Tabla de Clientes (Nombre, Teléfono, Email, Notas)
CREATE TABLE IF NOT EXISTS turnero.clientes (
    id SERIAL PRIMARY KEY,
    nombre VARCHAR(100) NOT NULL,
    telefono VARCHAR(30) NOT NULL,
    email VARCHAR(100),
    notas TEXT,
    fecha_creacion TIMESTAMP DEFAULT CURRENT_TIMESTAMP
);

-- Tabla de Turnos (relacionado con Clientes, fecha inicio y fin, estado y control de recordatorio)
CREATE TABLE IF NOT EXISTS turnero.turnos (
    id SERIAL PRIMARY KEY,
    cliente_id INT REFERENCES turnero.clientes(id) ON DELETE CASCADE,
    titulo VARCHAR(150) NOT NULL,
    fecha_inicio TIMESTAMP NOT NULL,
    fecha_fin TIMESTAMP NOT NULL,
    estado VARCHAR(20) DEFAULT 'confirmado', -- 'confirmado', 'cancelado', 'completado'
    recordatorio_enviado BOOLEAN DEFAULT FALSE,
    notas TEXT,
    fecha_creacion TIMESTAMP DEFAULT CURRENT_TIMESTAMP
);

-- Índices para optimizar búsquedas por fecha y cliente
CREATE INDEX IF NOT EXISTS idx_turnos_fecha_inicio ON turnero.turnos(fecha_inicio);
CREATE INDEX IF NOT EXISTS idx_turnos_cliente_id ON turnero.turnos(cliente_id);
CREATE INDEX IF NOT EXISTS idx_turnos_recordatorio ON turnero.turnos(fecha_inicio, recordatorio_enviado);
