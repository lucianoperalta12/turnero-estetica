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
import { existsSync, writeFileSync } from 'fs';
import { fileURLToPath } from 'url';
import { dirname, join } from 'path';

const __dirname = dirname(fileURLToPath(import.meta.url));
const AUTH_DIR = join(__dirname, 'auth_session');
const QR_PATH  = join(__dirname, 'qr.png');
const PORT     = process.env.PORT || 3000;

// Logger silencioso para producción
const logger = pino({ level: 'silent' });

let sock = null;
let connectionStatus = 'esperando QR'; // 'esperando QR', 'autenticando', 'conectado', 'desconectado'
let currentQR = null;
let qrTimestamp = null;
let authenticatedNumber = null;

async function connectToWhatsApp() {
    const { state, saveCreds } = await useMultiFileAuthState(AUTH_DIR);
    const { version } = await fetchLatestBaileysVersion();

    console.log(`[WhatsApp] Iniciando cliente (versión del protocolo Baileys: ${version.join('.')})`);

    sock = makeWASocket({
        version,
        auth: {
            creds: state.creds,
            keys: makeCacheableSignalKeyStore(state.keys, logger)
        },
        printQRInTerminal: false,
        logger,
        browser: ['Mac OS', 'Chrome', '121.0.0'],
        markOnlineOnConnect: false,
        syncFullHistory: false
    });

    sock.ev.on('creds.update', saveCreds);

    sock.ev.on('connection.update', async (update) => {
        const { connection, lastDisconnect, qr } = update;

        if (qr) {
            currentQR = qr;
            qrTimestamp = Date.now();
            connectionStatus = 'esperando QR';
            console.log(`[WhatsApp] [${new Date().toLocaleTimeString()}] Nuevo código QR generado. Disponible en http://localhost:${PORT}/qr`);
            
            try {
                await QRCode.toFile(QR_PATH, qr, { scale: 8 });
            } catch (err) {
                // Ignorar errores al guardar archivo local
            }
        }

        if (connection === 'open') {
            currentQR = null;
            connectionStatus = 'conectado';
            authenticatedNumber = sock.user?.id?.split(':')[0] || sock.user?.id;
            console.log(`[WhatsApp] Estado: Conectado. Número: ${authenticatedNumber}`);

            sock.sendPresenceUpdate('unavailable').catch(() => {});

            if (existsSync(QR_PATH)) {
                try { writeFileSync(QR_PATH, ''); } catch (_) {}
            }
        }

        if (connection === 'close') {
            connectionStatus = 'desconectado';
            authenticatedNumber = null;
            currentQR = null;

            const statusCode = (lastDisconnect?.error instanceof Boom)
                ? lastDisconnect.error.output.statusCode
                : null;
            
            const isLoggedOut = statusCode === DisconnectReason.loggedOut;
            console.warn(`[WhatsApp] Conexión cerrada. Código: ${statusCode}.`);

            if (isLoggedOut) {
                console.error('[WhatsApp] Sesión cerrada. Por favor elimina auth_session/ y reinicia.');
            } else {
                console.log('[WhatsApp] Intentando reconectar en 3 segundos...');
                setTimeout(connectToWhatsApp, 3000);
            }
        }
    });
}

// Iniciar conexión inicial
connectToWhatsApp().catch(console.error);

// ── Servidor HTTP ─────────────────────────────────────────────────────────────
const app = express();
app.use(express.json());

// Endpoint POST /send
app.post('/send', async (req, res) => {
    const { phone, message } = req.body;

    if (!phone || !message) {
        return res.status(400).json({ ok: false, error: 'Faltan campos: phone y message son requeridos.' });
    }

    if (connectionStatus !== 'conectado' || !sock) {
        return res.status(503).json({ ok: false, error: `El servicio de WhatsApp no está conectado. Estado actual: ${connectionStatus}` });
    }

    const jid = `${phone}@s.whatsapp.net`;

    try {
        // Simular typing
        await sock.sendPresenceUpdate('composing', jid);
        await new Promise(r => setTimeout(r, 2000));
        
        const result = await sock.sendMessage(jid, { text: message });
        await sock.sendPresenceUpdate('unavailable', jid);

        const messageId = result?.key?.id ?? 'n/a';
        console.log(`[WhatsApp] Mensaje enviado a ${phone}. Id: ${messageId}`);
        return res.json({ ok: true, messageId });
    } catch (err) {
        console.error(`[WhatsApp] Error al enviar a ${phone}:`, err.message);
        return res.status(500).json({ ok: false, error: err.message });
    }
});

// Endpoint GET /qr
app.get('/qr', async (req, res) => {
    if (connectionStatus === 'conectado') {
        return res.send('<h2>✅ WhatsApp ya está conectado y listo para usar.</h2>');
    }
    
    if (!currentQR) {
        return res.send('<h2>⏳ Generando QR... Espera unos segundos y recarga la página.</h2>');
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
                    <p>Escanea este código QR desde tu celular (Dispositivos vinculados -> Vincular un dispositivo):</p>
                    <img src="${dataUrl}" alt="Código QR de WhatsApp" />
                    <p style="font-size: 12px; color: #888;">QR generado hace ${qrAgeSeconds}s. La página se actualiza automáticamente cada 10 segundos.</p>
                </div>
            </body>
            </html>
        `);
    } catch (err) {
        return res.status(500).send(`Error generando código QR: ${err.message}`);
    }
});

// Endpoint GET /status
app.get('/status', (_req, res) => {
    res.json({
        ok: true,
        status: connectionStatus,
        number: authenticatedNumber
    });
});

app.listen(PORT, () => {
    console.log(`[HTTP] Servicio de WhatsApp (Baileys) escuchando en puerto ${PORT}`);
    console.log(`[HTTP] Accede a http://localhost:${PORT}/qr para vincular tu cuenta`);
});
