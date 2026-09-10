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
import fs from 'fs';
import { fileURLToPath } from 'url';
import { dirname, join } from 'path';

const __dirname = dirname(fileURLToPath(import.meta.url));
const AUTH_DIR  = join(__dirname, 'auth_session');
const QR_PATH   = join(__dirname, 'qr.png');
const PORT      = process.env.PORT || 3000;

const logger = pino({ level: 'silent' });

// ─── Estado global del socket persistente ─────────────────────────────────────
let sock             = null;
let isReady          = false;
let currentQR        = null;
let qrTimestamp      = null;
let connectionStatus = 'desconectado';
let readyResolvers   = [];

function waitUntilReady(timeoutMs = 30000) {
    if (isReady) return Promise.resolve();
    return new Promise((resolve, reject) => {
        const timer = setTimeout(() => {
            readyResolvers = readyResolvers.filter(r => r.resolve !== resolve);
            reject(new Error('Timeout esperando conexión con WhatsApp.'));
        }, timeoutMs);
        readyResolvers.push({
            resolve: () => { clearTimeout(timer); resolve(); },
            reject:  (e) => { clearTimeout(timer); reject(e); }
        });
    });
}

function flushReadyResolvers(error = null) {
    const resolvers = readyResolvers;
    readyResolvers = [];
    for (const r of resolvers) {
        if (error) r.reject(error);
        else r.resolve();
    }
}

// ─── Iniciar / reconectar socket persistente ──────────────────────────────────
async function startSocket() {
    const { state, saveCreds } = await useMultiFileAuthState(AUTH_DIR);
    const { version }          = await fetchLatestBaileysVersion();

    sock = makeWASocket({
        version,
        auth: {
            creds: state.creds,
            keys:  makeCacheableSignalKeyStore(state.keys, logger)
        },
        printQRInTerminal: false,
        logger,
        browser:             ['Turnero', 'Chrome', '121.0.0'],
        markOnlineOnConnect: false,
        syncFullHistory:     false,
        keepAliveIntervalMs: 30_000
    });

    sock.ev.on('creds.update', saveCreds);

    sock.ev.on('connection.update', async ({ connection, lastDisconnect, qr }) => {
        if (qr) {
            currentQR        = qr;
            qrTimestamp      = Date.now();
            connectionStatus = 'esperando_qr';
            try { await QRCode.toFile(QR_PATH, qr, { scale: 8 }); } catch (_) {}
        }

        if (connection === 'open') {
            console.log(`[Baileys] [${new Date().toISOString()}] ✅ Conexión abierta.`);
            isReady          = true;
            currentQR        = null;
            connectionStatus = 'conectado';
            flushReadyResolvers();
            try { await sock.sendPresenceUpdate('unavailable'); } catch (_) {}
        }

        if (connection === 'close') {
            isReady          = false;
            connectionStatus = 'desconectado';
            const statusCode = (lastDisconnect?.error instanceof Boom)
                ? lastDisconnect.error.output.statusCode
                : null;

            console.log(`[Baileys] Conexión cerrada. StatusCode=${statusCode}`);

            if (statusCode === DisconnectReason.loggedOut || statusCode === 401) {
                console.log('[Baileys] Sesión cerrada remotamente. Eliminando credenciales...');
                try { fs.rmSync(AUTH_DIR, { recursive: true, force: true }); } catch (_) {}
                flushReadyResolvers(new Error('REQUERIDA_VINCULACION'));
                connectionStatus = 'desvinculado';
                sock = null;
                // No reconectar — esperar que el usuario escanee QR
            } else {
                flushReadyResolvers(new Error('Conexión perdida, reintentando...'));
                console.log('[Baileys] Reconectando en 3s...');
                sock = null;
                setTimeout(startSocket, 3000);
            }
        }
    });
}

// ─── Enviar mensaje usando el socket persistente ──────────────────────────────
async function sendWhatsAppMessage(phone, message) {
    await waitUntilReady(30_000);

    let jid;
    const isSpecial = ['admin', 'self'].includes(phone.toLowerCase());

    if (isSpecial) {
        const selfNumber = sock.user?.id?.split(':')[0] || sock.user?.id;
        if (!selfNumber) throw new Error('No se pudo determinar el número propio de la sesión.');
        jid = `${selfNumber.split('@')[0].split(':')[0]}@s.whatsapp.net`;
        console.log(`[Baileys] Destinatario especial → ${jid}`);
    } else {
        jid = `${phone.replace(/\D/g, '')}@s.whatsapp.net`;
    }

    if (!isSpecial) {
        console.log(`[Baileys] [${new Date().toISOString()}] Validando ${jid}...`);
        const [onWaResult] = await sock.onWhatsApp(jid);
        if (!onWaResult?.exists) {
            throw new Error(`El número ${phone.replace(/\D/g, '')} no existe en WhatsApp.`);
        }
    }

    console.log(`[Baileys] [${new Date().toISOString()}] Enviando mensaje a ${jid}...`);
    const sendResponse = await sock.sendMessage(jid, { text: message });
    const messageId    = sendResponse?.key?.id;
    console.log(`[Baileys] [${new Date().toISOString()}] ✅ Enviado. messageId=${messageId}`);

    try { await sock.sendPresenceUpdate('unavailable'); } catch (_) {}

    return messageId;
}

// ─── Express ──────────────────────────────────────────────────────────────────
const app = express();
app.use(express.json());

// POST /send
app.post('/send', async (req, res) => {
    const { phone, message } = req.body;

    if (!phone || !message) {
        return res.status(400).json({ ok: false, error: 'Faltan campos obligatorios: phone y message.' });
    }

    if (connectionStatus === 'desvinculado' || !fs.existsSync(join(AUTH_DIR, 'creds.json'))) {
        return res.status(503).json({ ok: false, error: 'REQUERIDA_VINCULACION' });
    }

    console.log(`[HTTP] [${new Date().toISOString()}] Enviar a ${phone}`);

    try {
        const messageId = await sendWhatsAppMessage(phone, message);
        return res.json({ ok: true, messageId });
    } catch (err) {
        console.error('[HTTP] Error durante el envío:', err.stack || err);
        if (err.message === 'REQUERIDA_VINCULACION') {
            return res.status(503).json({ ok: false, error: 'Requerida vinculación. Accedé a /qr.' });
        }
        return res.status(500).json({ ok: false, error: err.message, stack: err.stack });
    }
});

// GET /qr
app.get('/qr', async (req, res) => {
    if (connectionStatus === 'conectado') {
        return res.send('<h2>✅ WhatsApp ya está conectado.</h2>');
    }

    if (!sock) {
        startSocket().catch(console.error);
    }

    if (!currentQR) {
        return res.send('<h2>⏳ Generando QR... Esperá unos segundos y recargá la página.</h2>');
    }

    const qrAgeSeconds = Math.floor((Date.now() - qrTimestamp) / 1000);
    try {
        const dataUrl = await QRCode.toDataURL(currentQR, { scale: 8 });
        res.setHeader('Content-Type', 'text/html; charset=utf-8');
        return res.send(`
            <!DOCTYPE html>
            <html>
            <head>
                <meta charset="utf-8">
                <meta http-equiv="refresh" content="10">
                <title>Vincular WhatsApp</title>
                <style>
                    body { font-family: Arial, sans-serif; text-align: center; margin-top: 50px; background-color: #f0f2f5; }
                    .container { display: inline-block; background: white; padding: 30px; border-radius: 10px; box-shadow: 0 4px 8px rgba(0,0,0,0.1); }
                    img { border: 1px solid #ccc; border-radius: 5px; }
                    h2 { color: #075e54; }
                    p { color: #555; }
                </style>
            </head>
            <body>
                <div class="container">
                    <h2>Vincular Servicio de Turnos</h2>
                    <p>Escaneá este código QR desde tu celular (Dispositivos vinculados → Vincular un dispositivo):</p>
                    <img src="${dataUrl}" alt="Código QR de WhatsApp" />
                    <p style="font-size: 12px; color: #888;">QR generado hace ${qrAgeSeconds}s. La página se actualiza cada 10 segundos.</p>
                </div>
            </body>
            </html>
        `);
    } catch (err) {
        return res.status(500).send(`Error generando QR: ${err.message}`);
    }
});

// GET /status
app.get('/status', (_req, res) => {
    const hasCreds = fs.existsSync(join(AUTH_DIR, 'creds.json'));
    res.json({
        ok:       true,
        status:   connectionStatus,
        ready:    isReady,
        hasCreds
    });
});

// ─── Arrancar ─────────────────────────────────────────────────────────────────
app.listen(PORT, () => {
    console.log(`[HTTP] Servicio WhatsApp escuchando en puerto ${PORT}`);
});

// Conectar automáticamente si ya hay credenciales guardadas
if (fs.existsSync(join(AUTH_DIR, 'creds.json'))) {
    startSocket().catch(console.error);
}
