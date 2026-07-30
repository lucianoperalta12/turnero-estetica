using Dapper;
using Npgsql;
using TurneroWorker.Models;

namespace TurneroWorker.Services;

public class DatabaseService
{
    private readonly string _connectionString;

    public DatabaseService(IConfiguration configuration)
    {
        _connectionString = configuration.GetConnectionString("DefaultConnection") 
            ?? throw new InvalidOperationException("DefaultConnection string is missing.");
    }

    private NpgsqlConnection CreateConnection() => new(_connectionString);

    public async Task InicializarTablasAsync()
    {
        using var conn = CreateConnection();
        var sql = @"
            CREATE SCHEMA IF NOT EXISTS turnero;

            CREATE TABLE IF NOT EXISTS turnero.clientes (
                id SERIAL PRIMARY KEY,
                nombre VARCHAR(100) NOT NULL,
                telefono VARCHAR(30) NOT NULL,
                email VARCHAR(100),
                notas TEXT,
                fecha_creacion TIMESTAMP DEFAULT CURRENT_TIMESTAMP
            );

            CREATE TABLE IF NOT EXISTS turnero.turnos (
                id SERIAL PRIMARY KEY,
                cliente_id INT REFERENCES turnero.clientes(id) ON DELETE CASCADE,
                titulo VARCHAR(150) NOT NULL,
                fecha_inicio TIMESTAMP NOT NULL,
                fecha_fin TIMESTAMP NOT NULL,
                estado VARCHAR(20) DEFAULT 'confirmado',
                recordatorio_enviado BOOLEAN DEFAULT FALSE,
                notas TEXT,
                fecha_creacion TIMESTAMP DEFAULT CURRENT_TIMESTAMP
            );

            CREATE INDEX IF NOT EXISTS idx_turnos_fecha_inicio ON turnero.turnos(fecha_inicio);
            CREATE INDEX IF NOT EXISTS idx_turnos_cliente_id ON turnero.turnos(cliente_id);
            CREATE INDEX IF NOT EXISTS idx_turnos_recordatorio ON turnero.turnos(fecha_inicio, recordatorio_enviado);

            CREATE TABLE IF NOT EXISTS turnero.whatsapp_error_log (
                id                  SERIAL PRIMARY KEY,
                turno_id            INT REFERENCES turnero.turnos(id) ON DELETE SET NULL,
                cliente_id          INT REFERENCES turnero.clientes(id) ON DELETE SET NULL,
                telefono_destino    VARCHAR(30),
                tipo_error          VARCHAR(30) NOT NULL,
                http_status_code    INT,
                message_id          VARCHAR(100),
                raw_response        TEXT,
                error_message       TEXT,
                stack_trace         TEXT,
                fecha_ocurrencia    TIMESTAMP NOT NULL DEFAULT CURRENT_TIMESTAMP,
                resuelto            BOOLEAN NOT NULL DEFAULT FALSE,
                notas_resolucion    TEXT
            );

            CREATE INDEX IF NOT EXISTS idx_wa_errlog_turno_id  ON turnero.whatsapp_error_log(turno_id);
            CREATE INDEX IF NOT EXISTS idx_wa_errlog_fecha      ON turnero.whatsapp_error_log(fecha_ocurrencia DESC);
            CREATE INDEX IF NOT EXISTS idx_wa_errlog_tipo_error ON turnero.whatsapp_error_log(tipo_error);
            CREATE INDEX IF NOT EXISTS idx_wa_errlog_resuelto   ON turnero.whatsapp_error_log(resuelto) WHERE resuelto = FALSE;
        ";
        await conn.ExecuteAsync(sql);
    }

    // ── CLIENTES ─────────────────────────────────────────────────────────────

    public async Task<IEnumerable<Cliente>> GetClientesAsync(string? busqueda = null)
    {
        using var conn = CreateConnection();
        if (string.IsNullOrWhiteSpace(busqueda))
        {
            var sql = "SELECT id, nombre, telefono, email, notas, fecha_creacion as FechaCreacion FROM turnero.clientes ORDER BY nombre ASC";
            return await conn.QueryAsync<Cliente>(sql);
        }
        else
        {
            var sql = @"SELECT id, nombre, telefono, email, notas, fecha_creacion as FechaCreacion 
                        FROM turnero.clientes 
                        WHERE nombre ILIKE @Busqueda OR telefono ILIKE @Busqueda 
                        ORDER BY nombre ASC";
            return await conn.QueryAsync<Cliente>(sql, new { Busqueda = $"%{busqueda}%" });
        }
    }

    public async Task<Cliente?> GetClienteByIdAsync(int id)
    {
        using var conn = CreateConnection();
        var sql = "SELECT id, nombre, telefono, email, notas, fecha_creacion as FechaCreacion FROM turnero.clientes WHERE id = @Id";
        return await conn.QuerySingleOrDefaultAsync<Cliente>(sql, new { Id = id });
    }

    public async Task<int> CrearClienteAsync(Cliente cliente)
    {
        using var conn = CreateConnection();
        var sql = @"INSERT INTO turnero.clientes (nombre, telefono, email, notas) 
                    VALUES (@Nombre, @Telefono, @Email, @Notas) 
                    RETURNING id;";
        return await conn.ExecuteScalarAsync<int>(sql, cliente);
    }

    public async Task UpdateClienteAsync(Cliente cliente)
    {
        using var conn = CreateConnection();
        var sql = @"UPDATE turnero.clientes 
                    SET nombre = @Nombre, telefono = @Telefono, email = @Email, notas = @Notas 
                    WHERE id = @Id";
        await conn.ExecuteAsync(sql, cliente);
    }

    public async Task DeleteClienteAsync(int id)
    {
        using var conn = CreateConnection();
        var sql = "DELETE FROM turnero.clientes WHERE id = @Id";
        await conn.ExecuteAsync(sql, new { Id = id });
    }

    // ── TURNOS ───────────────────────────────────────────────────────────────

    public async Task<IEnumerable<Turno>> GetTurnosRangoAsync(DateTime inicio, DateTime fin)
    {
        using var conn = CreateConnection();
        var sql = @"
            SELECT t.id, t.cliente_id as ClienteId, t.titulo, t.fecha_inicio as FechaInicio, 
                   t.fecha_fin as FechaFin, t.estado, t.recordatorio_enviado as RecordatorioEnviado, 
                   t.notas, t.fecha_creacion as FechaCreacion,
                   c.id, c.nombre, c.telefono, c.email, c.notas
            FROM turnero.turnos t
            LEFT JOIN turnero.clientes c ON t.cliente_id = c.id
            WHERE t.fecha_inicio >= @Inicio AND t.fecha_inicio <= @Fin
            ORDER BY t.fecha_inicio ASC";

        return await conn.QueryAsync<Turno, Cliente, Turno>(
            sql,
            (turno, cliente) =>
            {
                turno.Cliente = cliente;
                return turno;
            },
            new { Inicio = inicio, Fin = fin },
            splitOn: "id");
    }

    public async Task<int> CrearTurnoAsync(Turno turno)
    {
        using var conn = CreateConnection();
        var sql = @"INSERT INTO turnero.turnos (cliente_id, titulo, fecha_inicio, fecha_fin, estado, notas)
                    VALUES (@ClienteId, @Titulo, @FechaInicio, @FechaFin, @Estado, @Notas)
                    RETURNING id;";
        return await conn.ExecuteScalarAsync<int>(sql, turno);
    }

    public async Task UpdateTurnoAsync(Turno turno)
    {
        using var conn = CreateConnection();
        var sql = @"UPDATE turnero.turnos
                    SET cliente_id = @ClienteId, titulo = @Titulo, fecha_inicio = @FechaInicio, 
                        fecha_fin = @FechaFin, estado = @Estado, notas = @Notas
                    WHERE id = @Id";
        await conn.ExecuteAsync(sql, turno);
    }

    public async Task DeleteTurnoAsync(int id)
    {
        using var conn = CreateConnection();
        var sql = "DELETE FROM turnero.turnos WHERE id = @Id";
        await conn.ExecuteAsync(sql, new { Id = id });
    }

    // ── RECORDATORIOS PENDIENTES ──────────────────────────────────────────────

    public async Task<IEnumerable<Turno>> GetTurnosPendientesRecordatorioAsync(DateTime fecha)
    {
        using var conn = CreateConnection();
        var inicioDia = fecha.Date;
        var finDia = fecha.Date.AddDays(1).AddTicks(-1);

        var sql = @"
            SELECT t.id, t.cliente_id as ClienteId, t.titulo, t.fecha_inicio as FechaInicio, 
                   t.fecha_fin as FechaFin, t.estado, t.recordatorio_enviado as RecordatorioEnviado, 
                   t.notas, t.fecha_creacion as FechaCreacion,
                   c.id, c.nombre, c.telefono, c.email, c.notas
            FROM turnero.turnos t
            LEFT JOIN turnero.clientes c ON t.cliente_id = c.id
            WHERE t.fecha_inicio >= @InicioDia AND t.fecha_inicio <= @FinDia
              AND t.recordatorio_enviado = FALSE
              AND t.estado != 'cancelado'
            ORDER BY t.fecha_inicio ASC";

        return await conn.QueryAsync<Turno, Cliente, Turno>(
            sql,
            (turno, cliente) =>
            {
                turno.Cliente = cliente;
                return turno;
            },
            new { InicioDia = inicioDia, FinDia = finDia },
            splitOn: "id");
    }

    public async Task MarcarRecordatorioEnviadoAsync(int turnoId)
    {
        using var conn = CreateConnection();
        var sql = "UPDATE turnero.turnos SET recordatorio_enviado = TRUE WHERE id = @Id";
        await conn.ExecuteAsync(sql, new { Id = turnoId });
    }

    // ── ERROR LOG WHATSAPP ─────────────────────────────────────────────────────

    /// <summary>
    /// Registra un error de envío de WhatsApp en la tabla de log.
    /// </summary>
    public async Task RegistrarErrorWhatsAppAsync(
        int? turnoId,
        int? clienteId,
        string? telefonoDestino,
        Models.TipoErrorWhatsApp tipoError,
        int? httpStatusCode,
        string? rawResponse,
        string? errorMessage,
        string? stackTrace)
    {
        using var conn = CreateConnection();
        var sql = @"
            INSERT INTO turnero.whatsapp_error_log
                (turno_id, cliente_id, telefono_destino, tipo_error,
                 http_status_code, raw_response, error_message, stack_trace)
            VALUES
                (@TurnoId, @ClienteId, @TelefonoDestino, @TipoError,
                 @HttpStatusCode, @RawResponse, @ErrorMessage, @StackTrace);";

        await conn.ExecuteAsync(sql, new
        {
            TurnoId         = (object?)turnoId ?? DBNull.Value,
            ClienteId       = (object?)clienteId ?? DBNull.Value,
            TelefonoDestino = telefonoDestino,
            TipoError       = tipoError.ToString(),
            HttpStatusCode  = (object?)httpStatusCode ?? DBNull.Value,
            RawResponse     = rawResponse,
            ErrorMessage    = errorMessage,
            StackTrace      = stackTrace
        });
    }
}
