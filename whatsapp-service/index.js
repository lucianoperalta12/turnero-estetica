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

// ─── Estado del flujo QR (único socket que puede estar "vivo" fuera de un envío) ─
let qrSocket     = null;
let currentQR    = null;
let qrTimestamp  = null;
let qrFlowStatus = 'idle'; // 'idle' | 'connecting' | 'waiting_qr' | 'authenticated'

// ─── Crear socket one-shot ────────────────────────────────────────────────────
async function createSocket() {
    const { state, saveCreds } = await useMultiFileAuthState(AUTH_DIR);
    const { version }          = await fetchLatestBaileysVersion();

    const s = makeWASocket({
        version,
        auth: {
            creds: state.creds,
            keys:  makeCacheableSignalKeyStore(state.keys, logger)
        },
        printQRInTerminal: false,
        logger,
        browser:            ['Turnero', 'Chrome', '121.0.0'],
        markOnlineOnConnect: false,
        syncFullHistory:    false
        // Sin keepAliveIntervalMs → sin reconexión automática
    });

    s.ev.on('creds.update', saveCreds);
    return s;
}

// ─── Enviar mensaje (connect → send → disconnect) ─────────────────────────────
async function sendWhatsAppMessage(phone, message) {
    const s = await createSocket();

    try {
        // Esperar que el socket quede 'open'
        await new Promise((resolve, reject) => {
            const timeout = setTimeout(() => {
                reject(new Error('Timeout esperando conexión con WhatsApp.'));
            }, 30_000);

            s.ev.on('connection.update', ({ connection, lastDisconnect }) => {
                if (connection === 'open') {
                    clearTimeout(timeout);
                    resolve();
                }
                if (connection === 'close') {
                    clearTimeout(timeout);
                    const statusCode = (lastDisconnect?.error instanceof Boom)
                        ? lastDisconnect.error.output.statusCode
                        : null;
                    if (statusCode === DisconnectReason.loggedOut || statusCode === 401) {
                        try { fs.rmSync(AUTH_DIR, { recursive: true, force: true }); } catch (_) {}
                        reject(new Error('REQUERIDA_VINCULACION'));
                    } else {
                        reject(new Error(`Conexión cerrada. StatusCode=${statusCode}`));
                    }
                }
            });
        });

        // Resolver JID
        let jid;
        const isSpecial = ['admin', 'self'].includes(phone.toLowerCase());

        if (isSpecial) {
            const selfNumber = s.user?.id?.split(':')[0] || s.user?.id;
            if (!selfNumber) throw new Error('No se pudo determinar el número propio de la sesión.');
            jid = `${selfNumber.split('@')[0].split(':')[0]}@s.whatsapp.net`;
            console.log(`[Baileys] Destinatario especial → ${jid}`);
        } else {
            jid = `${phone.replace(/\D/g, '')}@s.whatsapp.net`;
        }

        // Validar número
        if (!isSpecial) {
            console.log(`[Baileys] [${new Date().toISOString()}] Validando ${jid}...`);
            const [onWaResult] = await s.onWhatsApp(jid);
            if (!onWaResult?.exists) {
                throw new Error(`El número ${phone.replace(/\D/g, '')} no existe en WhatsApp.`);
            }
        }

        // Enviar
        console.log(`[Baileys] [${new Date().toISOString()}] Enviando mensaje a ${jid}...`);
        const sendResponse = await s.sendMessage(jid, { text: message });
        const messageId    = sendResponse?.key?.id;
        console.log(`[Baileys] [${new Date().toISOString()}] ✅ Enviado. messageId=${messageId}`);

        return messageId;

    } finally {
        // Siempre cerrar el socket al terminar
        try { s.end(undefined); } catch (_) {}
    }
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

    if (!fs.existsSync(join(AUTH_DIR, 'creds.json'))) {
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

// GET /qr  — inicia flujo de vinculación si no hay credenciales
app.get('/qr', async (req, res) => {
    const hasCreds = fs.existsSync(join(AUTH_DIR, 'creds.json'));

    if (hasCreds && qrFlowStatus !== 'waiting_qr') {
        return res.send('<h2>✅ WhatsApp ya está conectado o tiene una sesión activa en el VPS.</h2>');
    }

    // Arrancar socket QR si no hay uno activo
    if (!qrSocket && qrFlowStatus === 'idle') {
        qrFlowStatus = 'connecting';

        const { state, saveCreds } = await useMultiFileAuthState(AUTH_DIR);
        const { version }          = await fetchLatestBaileysVersion();

        qrSocket = makeWASocket({
            version,
            auth: {
                creds: state.creds,
                keys:  makeCacheableSignalKeyStore(state.keys, logger)
            },
            printQRInTerminal: false,
            logger,
            browser:         ['Turnero', 'Chrome', '121.0.0'],
            markOnlineOnConnect: false,
            syncFullHistory: false
        });

        qrSocket.ev.on('creds.update', saveCreds);

        qrSocket.ev.on('connection.update', async ({ connection, qr }) => {
            if (qr) {
                currentQR    = qr;
                qrTimestamp  = Date.now();
                qrFlowStatus = 'waiting_qr';
                try { await QRCode.toFile(QR_PATH, qr, { scale: 8 }); } catch (_) {}
            }

            if (connection === 'open') {
                console.log(`[Baileys] [${new Date().toISOString()}] ✅ QR escaneado. Sesión guardada.`);
                qrFlowStatus = 'authenticated';
                currentQR    = null;
                // Desconectar limpiamente luego de guardar las creds
                setTimeout(() => {
                    try { qrSocket?.end(undefined); } catch (_) {}
                    qrSocket     = null;
                    qrFlowStatus = 'idle';
                }, 2000);
            }

            if (connection === 'close') {
                qrSocket     = null;
                qrFlowStatus = 'idle';
                currentQR    = null;
            }
        });
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
        ok:     true,
        status: hasCreds ? 'credenciales_ok' : 'desconectado',
        ready:  hasCreds,
        qrFlow: qrFlowStatus
    });
});

app.listen(PORT, () => {
    console.log(`[HTTP] Servicio WhatsApp escuchando en puerto ${PORT}`);
});
