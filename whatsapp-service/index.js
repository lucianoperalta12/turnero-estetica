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
import { writeFileSync, existsSync } from 'fs';
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
            connectionStatus = 'connecting';
            console.log('\n[WhatsApp] ── QR generado ──────────────────────────────');
            console.log('[WhatsApp] Escaneá desde WhatsApp > Dispositivos vinculados > Vincular dispositivo');
            console.log('[WhatsApp] También disponible en: GET http://localhost:' + PORT + '/qr (imagen PNG)\n');

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

    // Baileys usa el formato: NÚMERO@s.whatsapp.net
    const jid = `${phone}@s.whatsapp.net`;

    try {
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
 * Devuelve el QR actual como imagen PNG para escanear desde el iPhone.
 * Si ya está conectado devuelve 200 con mensaje de texto.
 */
app.get('/qr', async (_req, res) => {
    if (connectionStatus === 'open') {
        return res.status(200).send('WhatsApp ya está conectado. No necesitás escanear el QR.');
    }
    if (!currentQR) {
        return res.status(202).send('Todavía no hay QR disponible. Esperá unos segundos y recargá.');
    }
    try {
        const buffer = await QRCode.toBuffer(currentQR, { scale: 8 });
        res.setHeader('Content-Type', 'image/png');
        res.setHeader('Content-Disposition', 'inline; filename="qr.png"');
        return res.send(buffer);
    } catch (err) {
        return res.status(500).json({ ok: false, error: err.message });
    }
});

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
