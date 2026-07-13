import makeWASocket, {
    useMultiFileAuthState,
    DisconnectReason,
    fetchLatestBaileysVersion,
    makeCacheableSignalKeyStore
} from '@whiskeysockets/baileys';
import { Boom } from '@hapi/boom';
import express from 'express';
import pino from 'pino';
import QRCode from 'qrcode';
import { writeFileSync, existsSync, rmSync } from 'fs';
import { fileURLToPath } from 'url';
import { dirname, join } from 'path';

const __dirname = dirname(fileURLToPath(import.meta.url));
const AUTH_DIR = join(__dirname, 'auth_session');
const QR_PATH  = join(__dirname, 'qr.png');
const PORT     = process.env.PORT || 3000;

// Logger silencioso para producción (solo errores en consola)
const logger = pino({ level: 'silent' });

let sock = null;
let connectionStatus = 'disconnected'; // 'disconnected' | 'connecting' | 'open'
let currentQR = null;
let qrTimestamp = null;  // cuándo se generó el QR actual

// ── Conexión a WhatsApp ─────────────────────────────────────────────────────

async function connectToWhatsApp() {
    const { state, saveCreds } = await useMultiFileAuthState(AUTH_DIR);
    const { version } = await fetchLatestBaileysVersion();

    console.log(`[WhatsApp] Usando Baileys versión de protocolo: ${version.join('.')}`);

    sock = makeWASocket({
        version,
        auth: {
            creds: state.creds,
            keys: makeCacheableSignalKeyStore(state.keys, logger)
        },
        printQRInTerminal: false,   // lo manejamos nosotros
        logger,
        // Sin store en memoria, sin browser fingerprint pesado
        browser: ['Mac OS', 'Chrome', '121.0.0'],
        markOnlineOnConnect: false,  // no mostrar "en línea" en el celular
        syncFullHistory: false
    });

    // Guardar credenciales cada vez que cambian
    sock.ev.on('creds.update', saveCreds);

    sock.ev.on('connection.update', async (update) => {
        const { connection, lastDisconnect, qr } = update;

        if (qr) {
            currentQR = qr;
            qrTimestamp = Date.now();
            connectionStatus = 'connecting';
            console.log('\n[WhatsApp] ── QR generado ──────────────────────────────');
            console.log('[WhatsApp] Escaneá desde WhatsApp > Dispositivos vinculados > Vincular dispositivo');
            console.log('[WhatsApp] También disponible en: GET http://localhost:' + PORT + '/qr\n');

            // Guardar QR como imagen PNG para escanear desde el celular
            try {
                await QRCode.toFile(QR_PATH, qr, { scale: 8 });
            } catch (err) {
                console.error('[WhatsApp] Error guardando QR PNG:', err.message);
            }
        }

        if (connection === 'open') {
            currentQR = null;
            connectionStatus = 'open';
            console.log('[WhatsApp] ✓ Conectado como:', sock.user?.id);

            // Limpiar QR PNG ya no necesario
            if (existsSync(QR_PATH)) {
                try { writeFileSync(QR_PATH, ''); } catch (_) {}
            }
        }

        if (connection === 'close') {
            connectionStatus = 'disconnected';
            const statusCode = (lastDisconnect?.error instanceof Boom)
                ? lastDisconnect.error.output.statusCode
                : null;
            const shouldReconnect = statusCode !== DisconnectReason.loggedOut;

            console.warn(`[WhatsApp] Conexión cerrada. Código: ${statusCode}. Reconectar: ${shouldReconnect}`);

            if (shouldReconnect) {
                console.log('[WhatsApp] Reconectando en 3 segundos...');
                setTimeout(connectToWhatsApp, 3000);
            } else {
                console.error('[WhatsApp] Sesión cerrada por el usuario (logged out). Borrá auth_session/ y reiniciá.');
            }
        }
    });
}

// ── Express HTTP API ────────────────────────────────────────────────────────

const app = express();
app.use(express.json());

/**
 * POST /send
 * Body: { "phone": "5491112345678", "message": "Hola!" }
 * 
 * El número debe incluir código de país sin '+', sin espacios.
 * Argentina: 549 + número de 10 dígitos (ej: 5491112345678)
 */
app.post('/send', async (req, res) => {
    const { phone, message } = req.body;

    if (!phone || !message) {
        return res.status(400).json({ ok: false, error: 'Faltan campos: phone y message son requeridos.' });
    }

    if (connectionStatus !== 'open') {
        return res.status(503).json({
            ok: false,
            error: `WhatsApp no está conectado. Estado: ${connectionStatus}. Escaneá el QR en GET /qr`
        });
    }

    const jid = `${phone}@s.whatsapp.net`;

    try {
        // Simular "escribiendo..." por un tiempo proporcional al largo del mensaje + ruido aleatorio
        const ruidoAleatorio = Math.floor(Math.random() * 801) - 400; // -400 a 400 ms
        let typingMs = 1500 + (message.length * 30) + ruidoAleatorio;
        // Clamp entre 1.5s y 7s
        typingMs = Math.min(Math.max(typingMs, 1500), 7000);

        await sock.sendPresenceUpdate('composing', jid);
        await new Promise(r => setTimeout(r, typingMs));
        await sock.sendPresenceUpdate('paused', jid);

        const result = await sock.sendMessage(jid, { text: message });
        const messageId = result?.key?.id ?? 'n/a';
        console.log(`[WhatsApp] ✓ Enviado a ${phone} | ID: ${messageId}`);
        return res.json({ ok: true, messageId });
    } catch (err) {
        console.error(`[WhatsApp] Error enviando a ${phone}:`, err.message);
        return res.status(500).json({ ok: false, error: err.message });
    }
});

/**
 * GET /qr
 * Muestra página HTML con el QR y auto-refresh cada 15 segundos.
 * GET /qr?raw=1  → devuelve solo la imagen PNG (para automatización).
 */
app.get('/qr', async (req, res) => {
    if (connectionStatus === 'open') {
        return res.status(200).send('<h2>✅ WhatsApp ya está conectado.</h2>');
    }

    if (!currentQR) {
        // Sin QR aún: devolver página que recarga sola
        return res.status(202).send(`
            <!DOCTYPE html><html><head><meta charset="utf-8">
            <meta http-equiv="refresh" content="5">
            <title>QR WhatsApp</title></head><body>
            <h2>⏳ Generando QR...</h2>
            <p>Esta página se recargará automáticamente en 5 segundos.</p>
            </body></html>`);
    }

    // QR disponible
    const qrAgeSeconds = Math.floor((Date.now() - qrTimestamp) / 1000);

    if (req.query.raw === '1') {
        // Modo imagen pura
        try {
            const buffer = await QRCode.toBuffer(currentQR, { scale: 8 });
            res.setHeader('Content-Type', 'image/png');
            return res.send(buffer);
        } catch (err) {
            return res.status(500).json({ ok: false, error: err.message });
        }
    }

    // Modo HTML con auto-refresh
    try {
        const dataUrl = await QRCode.toDataURL(currentQR, { scale: 8 });
        res.setHeader('Content-Type', 'text/html; charset=utf-8');
        return res.send(`
            <!DOCTYPE html><html><head><meta charset="utf-8">
            <meta http-equiv="refresh" content="15">
            <title>QR WhatsApp</title>
            <style>body{font-family:sans-serif;text-align:center;padding:2rem;background:#111;color:#eee}
            img{border:8px solid white;border-radius:12px;max-width:300px}</style></head><body>
            <h2>📱 Escaneá con WhatsApp</h2>
            <p>WhatsApp → Dispositivos vinculados → Vincular dispositivo</p>
            <img src="${dataUrl}" />
            <p style="color:#aaa;font-size:.85rem">QR generado hace ${qrAgeSeconds}s · página se recarga cada 15s</p>
            <p><a href="/reset" onclick="return confirm('¿Borrar sesión y generar nuevo QR?')" 
               style="color:#f88">🔄 Resetear sesión y generar nuevo QR</a></p>
            </body></html>`);
    } catch (err) {
        return res.status(500).json({ ok: false, error: err.message });
    }
});

/**
 * POST /reset
 * Borra auth_session y fuerza reconexión completa generando un QR nuevo.
 * Útil cuando el QR expiró o la sesión quedó en estado inválido.
 */
app.post('/reset', (_req, res) => doReset(res));

/**
 * GET /reset  (acceso desde browser con confirmación en /qr)
 */
async function doReset(res) {
    console.log('[WhatsApp] 🔄 Reset solicitado — borrando sesión...');

    try {
        if (sock) {
            sock.ev.removeAllListeners();
            await sock.logout().catch(() => {});
            sock = null;
        }
    } catch (_) {}

    currentQR = null;
    qrTimestamp = null;
    connectionStatus = 'disconnected';

    try {
        if (existsSync(AUTH_DIR)) {
            rmSync(AUTH_DIR, { recursive: true, force: true });
            console.log('[WhatsApp] Sesión borrada.');
        }
    } catch (err) {
        console.error('[WhatsApp] Error borrando sesión:', err.message);
        return res.status(500).json({ ok: false, error: err.message });
    }

    setTimeout(() => connectToWhatsApp().catch(console.error), 500);
    return res.json({ ok: true, message: 'Sesión reseteada. Nuevo QR disponible en GET /qr en ~5 segundos.' });
}

app.get('/reset', (_req, res) => doReset(res));

/**
 * GET /status
 * Estado actual de la conexión.
 */
app.get('/status', (_req, res) => {
    res.json({
        ok: true,
        status: connectionStatus,
        number: sock?.user?.id ?? null
    });
});

// ── Inicio ──────────────────────────────────────────────────────────────────

app.listen(PORT, () => {
    console.log(`[HTTP] Servicio WhatsApp (Baileys) escuchando en http://localhost:${PORT}`);
    console.log(`[HTTP] Endpoints: POST /send | GET /qr | GET /status`);
});

connectToWhatsApp().catch((err) => {
    console.error('[WhatsApp] Error fatal al iniciar:', err);
    process.exit(1);
});
