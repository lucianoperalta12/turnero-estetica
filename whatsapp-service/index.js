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
const AUTH_DIR = join(__dirname, 'auth_session');
const QR_PATH  = join(__dirname, 'qr.png');
const PORT     = process.env.PORT || 3000;

// Configuración de Logger silenciosa de Baileys
const logger = pino({ level: 'silent' });

// Mutex para exclusión mutua
class Mutex {
    constructor() {
        this.queue = Promise.resolve();
    }

    async runExclusive(fn) {
        let resolveNext;
        const nextPromise = new Promise(resolve => {
            resolveNext = resolve;
        });

        const currentQueue = this.queue;
        this.queue = nextPromise;

        try {
            await currentQueue;
            return await fn();
        } finally {
            resolveNext();
        }
    }
}

const lock = new Mutex();

let currentQR = null;
let qrTimestamp = null;
let connectionStatus = 'desconectado';
let authenticatedNumber = null;

const app = express();
app.use(express.json());

// Endpoint POST /send
app.post('/send', async (req, res) => {
    const { phone, message } = req.body;

    if (!phone || !message) {
        return res.status(400).json({ ok: false, error: 'Faltan campos obligatorios: phone y message.' });
    }

    const cleanPhone = phone.replace(/\D/g, '');
    const jid = `${cleanPhone}@s.whatsapp.net`;

    console.log(`[HTTP] [${new Date().toISOString()}] Petición recibida para enviar a ${cleanPhone}`);

    try {
        const sendResult = await lock.runExclusive(async () => {
            console.log(`[Mutex] [${new Date().toISOString()}] Bloqueo adquirido para procesar envío a ${cleanPhone}`);

            if (!fs.existsSync(join(AUTH_DIR, 'creds.json'))) {
                console.warn('[WhatsApp] No existen credenciales guardadas (creds.json). Requiere vinculación.');
                throw new Error('REQUERIDA_VINCULACION');
            }

            let sock = null;
            let isConnected = false;

            try {
                const { state, saveCreds } = await useMultiFileAuthState(AUTH_DIR);
                const { version } = await fetchLatestBaileysVersion();

                console.log(`[Baileys] [${new Date().toISOString()}] Iniciando conexión temporal con WhatsApp...`);

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

                // Esperar resolución de la conexión
                await new Promise((resolve, reject) => {
                    const updateHandler = async (update) => {
                        const { connection, lastDisconnect } = update;

                        if (connection === 'open') {
                            console.log(`[Baileys] [${new Date().toISOString()}] Conexión abierta con éxito (connection=open).`);
                            isConnected = true;
                            sock.ev.off('connection.update', updateHandler);
                            resolve();
                        } else if (connection === 'close') {
                            const error = lastDisconnect?.error;
                            const statusCode = (error instanceof Boom) ? error.output.statusCode : null;
                            console.log(`[Baileys] [${new Date().toISOString()}] Conexión cerrada. Status: ${statusCode}`);

                            sock.ev.off('connection.update', updateHandler);

                            if (statusCode === DisconnectReason.loggedOut || statusCode === 401) {
                                console.error('[Baileys] Sesión expirada o inválida. Limpiando credenciales antiguas...');
                                try {
                                    fs.rmSync(AUTH_DIR, { recursive: true, force: true });
                                } catch (err) {
                                    console.error('[Baileys] Error limpiando credenciales:', err.stack || err);
                                }
                                reject(new Error('REQUERIDA_VINCULACION'));
                            } else {
                                reject(error || new Error('Desconexión antes de abrir.'));
                            }
                        }
                    };
                    sock.ev.on('connection.update', updateHandler);
                });

                // Validar número
                console.log(`[Baileys] [${new Date().toISOString()}] Validando existencia de número en WhatsApp (onWhatsApp): ${jid}`);
                const [onWaResult] = await sock.onWhatsApp(jid);
                if (!onWaResult || !onWaResult.exists) {
                    console.warn(`[Baileys] El número ${cleanPhone} no está registrado en WhatsApp.`);
                    throw new Error(`El número ${cleanPhone} no existe en WhatsApp.`);
                }
                console.log(`[Baileys] [${new Date().toISOString()}] Número validado con éxito.`);

                // Enviar mensaje
                console.log(`[Baileys] [${new Date().toISOString()}] Inicio de llamada sendMessage a ${cleanPhone}...`);
                const sendResponse = await sock.sendMessage(jid, { text: message });
                const messageId = sendResponse?.key?.id;
                console.log(`[Baileys] [${new Date().toISOString()}] Fin de llamada sendMessage. ID asignado: ${messageId}`);

                // En Baileys, una vez que sendMessage resuelve, el mensaje ya fue escrito en el socket.
                // Sin embargo, para evitar el error de "Esperando el mensaje" en el celular receptor
                // (causado porque el receptor pide las llaves de encriptación y el emisor se desconecta antes de responder),
                // mantenemos el socket abierto por un periodo de gracia fijo de 5 segundos.
                console.log(`[Baileys] [${new Date().toISOString()}] Esperando 5 segundos de gracia para asegurar envío de llaves y sincronización...`);
                await new Promise(r => setTimeout(r, 5000));

                return { ok: true, messageId };

            } finally {
                if (sock) {
                    console.log(`[Baileys] [${new Date().toISOString()}] Ejecución de sock.end().`);
                    try {
                        sock.end();
                    } catch (err) {
                        console.error('[Baileys] Error al cerrar socket en block finally:', err.stack || err);
                    }
                }
                console.log(`[Mutex] [${new Date().toISOString()}] Bloqueo liberado para ${cleanPhone}`);
            }
        });

        return res.json(sendResult);

    } catch (err) {
        console.error('[HTTP] Error completo durante el envío:', err.stack || err);
        if (err.message === 'REQUERIDA_VINCULACION') {
            connectionStatus = 'desconectado';
            return res.status(503).json({ ok: false, error: 'Requerida vinculación de dispositivo. Acceda a /qr.' });
        }
        return res.status(500).json({ ok: false, error: err.message, stack: err.stack });
    }
});

// Endpoint GET /qr
app.get('/qr', async (req, res) => {
    // Si ya existe creds.json, asumimos sesión existente y no generamos QR
    if (fs.existsSync(join(AUTH_DIR, 'creds.json')) && connectionStatus !== 'esperando QR') {
        return res.send('<h2>✅ WhatsApp ya está conectado o tiene una sesión activa en el VPS.</h2>');
    }

    if (connectionStatus === 'desconectado') {
        connectionStatus = 'esperando QR';

        // Lanzamos la generación de QR en segundo plano dentro del Mutex
        lock.runExclusive(async () => {
            console.log('[Mutex] Bloqueo adquirido para generar QR de vinculación...');
            let sock = null;

            try {
                const { state, saveCreds } = await useMultiFileAuthState(AUTH_DIR);
                const { version } = await fetchLatestBaileysVersion();

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

                await new Promise((resolve) => {
                    const updateHandler = async (update) => {
                        const { connection, lastDisconnect, qr } = update;

                        if (qr) {
                            currentQR = qr;
                            qrTimestamp = Date.now();
                            console.log('[QR] Nuevo código QR generado.');
                            try {
                                await QRCode.toFile(QR_PATH, qr, { scale: 8 });
                            } catch (err) {
                                console.error('[QR] Error guardando archivo qr.png:', err.stack || err);
                            }
                        }

                        if (connection === 'open') {
                            console.log('[QR] ¡Vinculación exitosa! Cerrando conexión inmediatamente.');
                            connectionStatus = 'desconectado';
                            currentQR = null;
                            sock.ev.off('connection.update', updateHandler);
                            resolve();
                        }

                        if (connection === 'close') {
                            console.log('[QR] Conexión cerrada.');
                            connectionStatus = 'desconectado';
                            currentQR = null;
                            sock.ev.off('connection.update', updateHandler);
                            resolve();
                        }
                    };
                    sock.ev.on('connection.update', updateHandler);
                });

            } catch (err) {
                console.error('[QR] Error en flujo de vinculación:', err.stack || err);
                connectionStatus = 'desconectado';
                currentQR = null;
            } finally {
                if (sock) {
                    try { sock.end(); } catch (e) {}
                }
                console.log('[Mutex] Bloqueo liberado de flujo de QR.');
            }
        }).catch(err => {
            console.error('[Mutex] Error en ejecución Mutex de QR:', err.stack || err);
        });
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
    console.log(`[HTTP] Servicio de WhatsApp bajo demanda pura escuchando en puerto ${PORT}`);
});
