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

const logger = pino({ level: 'info' });

let sock = null;
let connectionStatus = 'desconectado'; // 'esperando QR', 'conectado', 'desconectado'
let currentQR = null;
let qrTimestamp = null;
let authenticatedNumber = null;

let shouldReconnect = false; // Por defecto no reconecta solo, solo reconecta bajo demanda
let inactivityTimeout = null;
let connectionPromise = null; // Para sincronizar peticiones concurrentes de conexion

// Configura un temporizador de inactividad de 30 segundos
function resetInactivityTimeout() {
    if (inactivityTimeout) {
        clearTimeout(inactivityTimeout);
    }
    inactivityTimeout = setTimeout(async () => {
        if (connectionStatus === 'conectado' && sock) {
            console.log('[WhatsApp] Desconectando por inactividad para que la cuenta NO quede En Línea.');
            shouldReconnect = false;
            try {
                sock.ev.removeAllListeners('connection.update'); // Evitar reintentos automáticos
                sock.end();
            } catch (err) {
                console.error('[WhatsApp] Error al cerrar socket:', err.message);
            }
            connectionStatus = 'desconectado';
            sock = null;
            currentQR = null;
        }
    }, 30000); // 30 segundos de inactividad
}

async function connectToWhatsApp() {
    if (connectionPromise) return connectionPromise;

    connectionPromise = (async () => {
        try {
            const { state, saveCreds } = await useMultiFileAuthState(AUTH_DIR);
            const { version } = await fetchLatestBaileysVersion();

            console.log(`[WhatsApp] Iniciando conexión (Protocolo: ${version.join('.')})`);
            shouldReconnect = true;

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

            return new Promise((resolve, reject) => {
                const connectionHandler = async (update) => {
                    const { connection, lastDisconnect, qr } = update;

                    if (qr) {
                        currentQR = qr;
                        qrTimestamp = Date.now();
                        connectionStatus = 'esperando QR';
                        console.log(`[WhatsApp] Nuevo código QR disponible.`);
                        try {
                            await QRCode.toFile(QR_PATH, qr, { scale: 8 });
                        } catch (err) {
                            console.error('[WhatsApp] Error guardando QR:', err.message);
                        }
                        resolve(false); // No se conectó todavía (espera QR)
                    }

                    if (connection === 'open') {
                        currentQR = null;
                        connectionStatus = 'conectado';
                        authenticatedNumber = sock.user?.id?.split(':')[0] || sock.user?.id;
                        console.log(`[WhatsApp] Conectado exitosamente con el número: ${authenticatedNumber}`);

                        // Forzar estado invisible/offline
                        sock.sendPresenceUpdate('unavailable').catch(() => {});
                        resetInactivityTimeout();
                        
                        sock.ev.off('connection.update', connectionHandler); // Remover este handler temporal
                        setupRegularConnectionHandler(); // Poner el handler persistente
                        resolve(true);
                    }

                    if (connection === 'close') {
                        connectionStatus = 'desconectado';
                        authenticatedNumber = null;
                        currentQR = null;

                        const error = lastDisconnect?.error;
                        const statusCode = (error instanceof Boom) ? error.output.statusCode : null;
                        const isLoggedOut = statusCode === DisconnectReason.loggedOut;

                        console.log(`[WhatsApp] Conexión cerrada (Código: ${statusCode})`);
                        sock.ev.off('connection.update', connectionHandler);

                        if (isLoggedOut) {
                            console.error('[WhatsApp] Sesión cerrada o inválida. Eliminar auth_session/.');
                            reject(new Error('Sesión cerrada por WhatsApp.'));
                        } else if (shouldReconnect) {
                            console.log('[WhatsApp] Reintentando conectar...');
                            resolve(connectToWhatsApp());
                        } else {
                            resolve(false);
                        }
                    }
                };

                sock.ev.on('connection.update', connectionHandler);
            });

        } catch (err) {
            console.error('[WhatsApp] Error al conectar:', err);
            connectionPromise = null;
            throw err;
        } finally {
            connectionPromise = null;
        }
    })();

    return connectionPromise;
}

// Configura los eventos persistentes una vez conectado
function setupRegularConnectionHandler() {
    if (!sock) return;

    sock.ev.on('connection.update', async (update) => {
        const { connection, lastDisconnect } = update;

        if (connection === 'open') {
            connectionStatus = 'conectado';
            sock.sendPresenceUpdate('unavailable').catch(() => {});
            resetInactivityTimeout();
        }

        if (connection === 'close') {
            connectionStatus = 'desconectado';
            authenticatedNumber = null;
            const error = lastDisconnect?.error;
            const statusCode = (error instanceof Boom) ? error.output.statusCode : null;

            if (statusCode !== DisconnectReason.loggedOut && shouldReconnect) {
                console.log('[WhatsApp] Conexión caída. Intentando reconectar...');
                setTimeout(connectToWhatsApp, 3000);
            } else {
                sock = null;
            }
        }
    });
}

// Función de ayuda para asegurar que estamos conectados antes de enviar
async function asegurarConexion() {
    if (connectionStatus === 'conectado' && sock) {
        resetInactivityTimeout();
        return true;
    }
    console.log('[WhatsApp] Petición recibida. Conectando a WhatsApp bajo demanda...');
    return await connectToWhatsApp();
}

// ── Servidor HTTP ─────────────────────────────────────────────────────────────
const app = express();
app.use(express.json());

app.post('/send', async (req, res) => {
    const { phone, message } = req.body;

    if (!phone || !message) {
        return res.status(400).json({ ok: false, error: 'Faltan campos: phone y message.' });
    }

    try {
        const conectado = await asegurarConexion();
        if (!conectado || !sock) {
            return res.status(503).json({ 
                ok: false, 
                error: `No se pudo conectar a WhatsApp. Estado: ${connectionStatus}. Ve a /qr para vincular.` 
            });
        }

        const jid = `${phone}@s.whatsapp.net`;
        const result = await sock.sendMessage(jid, { text: message });
        
        // Mantener la conexión activa durante el lote
        resetInactivityTimeout();

        const messageId = result?.key?.id ?? 'n/a';
        console.log(`[WhatsApp] Mensaje enviado a ${phone}. Id: ${messageId}`);
        return res.json({ ok: true, messageId });
    } catch (err) {
        console.error(`[WhatsApp] Error al enviar a ${phone}:`, err.message);
        return res.status(500).json({ ok: false, error: err.message });
    }
});

app.get('/qr', async (req, res) => {
    // Si entran a ver el QR y está desconectado, forzar intento de conexión para generarlo
    if (connectionStatus === 'desconectado') {
        connectToWhatsApp().catch(() => {});
    }

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

app.get('/status', (_req, res) => {
    res.json({
        ok: true,
        status: connectionStatus,
        number: authenticatedNumber
    });
});

app.listen(PORT, () => {
    console.log(`[HTTP] Servicio de WhatsApp bajo demanda escuchando en puerto ${PORT}`);
});
