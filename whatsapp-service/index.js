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

// ─── Estado global del socket persistente ────────────────────────────────────

let sock            = null;
let isReady         = false;   // true cuando connection === 'open'
let isReconnecting  = false;
let currentQR       = null;
let qrTimestamp     = null;
let connectionStatus = 'desconectado';

// Promesa que se resuelve cuando el socket pasa a 'open'
let readyResolvers = [];

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

// ─── Iniciar / reconectar socket ─────────────────────────────────────────────

async function startSocket() {
    if (isReconnecting) return;
    isReconnecting = true;
    isReady        = false;
    connectionStatus = 'conectando';

    console.log(`[Baileys] [${new Date().toISOString()}] Iniciando conexión persistente...`);

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
        browser:            ['Mac OS', 'Chrome', '121.0.0'],
        markOnlineOnConnect: false,
        syncFullHistory:    false,
        // Mantiene el socket vivo con pings internos
        keepAliveIntervalMs: 30_000
    });

    sock.ev.on('creds.update', saveCreds);

    sock.ev.on('connection.update', async (update) => {
        const { connection, lastDisconnect, qr } = update;

        if (qr) {
            currentQR    = qr;
            qrTimestamp  = Date.now();
            connectionStatus = 'esperando QR';
            console.log(`[Baileys] Nuevo QR generado.`);
            try { await QRCode.toFile(QR_PATH, qr, { scale: 8 }); } catch (_) {}
        }

        if (connection === 'open') {
            console.log(`[Baileys] [${new Date().toISOString()}] ✅ Conectado. Socket persistente listo.`);
            isReady         = true;
            isReconnecting  = false;
            currentQR       = null;
            connectionStatus = 'conectado';
            flushReadyResolvers();
        }

        if (connection === 'close') {
            isReady = false;
            const error      = lastDisconnect?.error;
            const statusCode = (error instanceof Boom) ? error.output.statusCode : null;
            console.warn(`[Baileys] [${new Date().toISOString()}] Conexión cerrada. StatusCode=${statusCode}`);

            // Sesión revocada → limpiar credenciales y esperar QR
            if (statusCode === DisconnectReason.loggedOut || statusCode === 401) {
                console.error('[Baileys] Sesión expirada. Limpiando credenciales...');
                connectionStatus = 'desconectado';
                flushReadyResolvers(new Error('REQUERIDA_VINCULACION'));
                try { fs.rmSync(AUTH_DIR, { recursive: true, force: true }); } catch (_) {}
                isReconnecting = false;
                return;
            }

            // Cualquier otro cierre → reconectar con backoff
            connectionStatus = 'reconectando';
            flushReadyResolvers(new Error('Reconectando...'));
            isReconnecting = false;
            const delay = statusCode === 408 ? 10_000 : 5_000;
            console.log(`[Baileys] Reconectando en ${delay / 1000}s...`);
            setTimeout(startSocket, delay);
        }
    });
}

// ─── Arrancar socket al inicio (si ya hay credenciales) ──────────────────────

if (fs.existsSync(join(AUTH_DIR, 'creds.json'))) {
    startSocket().catch(err => {
        console.error('[Baileys] Error en startSocket inicial:', err);
    });
} else {
    console.log('[Baileys] Sin credenciales. Accedé a /qr para vincular.');
    connectionStatus = 'desconectado';
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

    console.log(`[HTTP] [${new Date().toISOString()}] Enviar a ${phone}`);

    try {
        // Si no hay credenciales, rechazar de inmediato
        if (!fs.existsSync(join(AUTH_DIR, 'creds.json'))) {
            return res.status(503).json({ ok: false, error: 'REQUERIDA_VINCULACION' });
        }

        // Esperar hasta que el socket esté listo (máx 30 s)
        await waitUntilReady(30_000);

        // Resolver JID
        let jid;
        const isSpecial = (phone.toLowerCase() === 'admin' || phone.toLowerCase() === 'self');

        if (isSpecial) {
            const selfNumber = sock.user?.id?.split(':')[0] || sock.user?.id;
            if (!selfNumber) throw new Error('No se pudo determinar el número propio de la sesión.');
            jid = `${selfNumber.split('@')[0].split(':')[0]}@s.whatsapp.net`;
            console.log(`[Baileys] Destinatario especial → ${jid}`);
        } else {
            const cleanPhone = phone.replace(/\D/g, '');
            jid = `${cleanPhone}@s.whatsapp.net`;
        }

        // Validar número
        if (!isSpecial) {
            console.log(`[Baileys] [${new Date().toISOString()}] Validando ${jid}...`);
            const [onWaResult] = await sock.onWhatsApp(jid);
            if (!onWaResult?.exists) {
                const cleanPhone = phone.replace(/\D/g, '');
                console.warn(`[Baileys] Número ${cleanPhone} no registrado en WhatsApp.`);
                throw new Error(`El número ${cleanPhone} no existe en WhatsApp.`);
            }
            console.log(`[Baileys] [${new Date().toISOString()}] Número validado.`);
        }

        // Enviar
        console.log(`[Baileys] [${new Date().toISOString()}] Enviando mensaje a ${jid}...`);
        const sendResponse = await sock.sendMessage(jid, { text: message });
        const messageId    = sendResponse?.key?.id;
        console.log(`[Baileys] [${new Date().toISOString()}] ✅ Enviado. messageId=${messageId}`);

        // Marcar la cuenta como "desconectada / offline" inmediatamente
        try {
            await sock.sendPresenceUpdate('unavailable');
        } catch (_) {}

        return res.json({ ok: true, messageId });

    } catch (err) {
        console.error('[HTTP] Error durante el envío:', err.stack || err);
        if (err.message === 'REQUERIDA_VINCULACION') {
            return res.status(503).json({ ok: false, error: 'Requerida vinculación. Accedé a /qr.' });
        }
        return res.status(500).json({ ok: false, error: err.message, stack: err.stack });
    }
});

// GET /qr  — inicia flujo de vinculación si no hay credenciales
app.get('/qr', async (req, res) => {
    const hasCreds = fs.existsSync(join(AUTH_DIR, 'creds.json'));

    if (hasCreds && connectionStatus !== 'esperando QR') {
        return res.send('<h2>✅ WhatsApp ya está conectado o tiene una sesión activa en el VPS.</h2>');
    }

    // Si todavía no se está conectando, arrancar
    if (!isReconnecting && !isReady) {
        startSocket().catch(err => console.error('[QR] Error startSocket:', err));
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
    res.json({
        ok:     true,
        status: connectionStatus,
        ready:  isReady,
        number: sock?.user?.id?.split(':')[0] ?? null
    });
});

app.listen(PORT, () => {
    console.log(`[HTTP] Servicio WhatsApp persistente escuchando en puerto ${PORT}`);
});
