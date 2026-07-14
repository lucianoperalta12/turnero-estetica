'use strict';

const { Client, LocalAuth } = require('whatsapp-web.js');
const QRCode = require('qrcode');
const express = require('express');
const path = require('path');
const fs = require('fs');

const PORT = process.env.PORT || 3000;
const QR_PATH = path.join(__dirname, 'qr.png');

const client = new Client({
    authStrategy: new LocalAuth({
        dataPath: './.wwebjs_auth'
    }),
    puppeteer: {
        headless: true,
        args: ['--no-sandbox', '--disable-setuid-sandbox', '--disable-dev-shm-usage']
    }
});

let connectionStatus = 'esperando QR'; // 'esperando QR', 'autenticando', 'conectado', 'desconectado'
let authenticatedNumber = null;
let currentQRDataUrl = null;

client.on('qr', async (qr) => {
    connectionStatus = 'esperando QR';
    try {
        // Generar y almacenar localmente la imagen qr.png
        await QRCode.toFile(QR_PATH, qr, { scale: 8 });
        
        // Generar la versión DataURL para renderizar en el endpoint /qr
        currentQRDataUrl = await QRCode.toDataURL(qr, { scale: 8 });
        
        console.log(`[WhatsApp] [${new Date().toLocaleTimeString()}] Nuevo código QR generado. Disponible en http://localhost:${PORT}/qr`);
    } catch (err) {
        console.error('[WhatsApp] Error al procesar el código QR:', err.message);
    }
});

client.on('ready', () => {
    connectionStatus = 'conectado';
    authenticatedNumber = client.info.wid.user;
    currentQRDataUrl = null;
    console.log(`[WhatsApp] Estado: Conectado. Número: ${authenticatedNumber}`);
    
    if (fs.existsSync(QR_PATH)) {
        try {
            fs.unlinkSync(QR_PATH);
        } catch (err) {
            // Ignorar errores al borrar
        }
    }
});

client.on('authenticated', () => {
    connectionStatus = 'autenticando';
    console.log('[WhatsApp] Estado: Autenticando...');
});

client.on('auth_failure', (msg) => {
    connectionStatus = 'desconectado';
    console.error('[WhatsApp] Fallo de autenticación:', msg);
});

client.on('disconnected', (reason) => {
    connectionStatus = 'desconectado';
    authenticatedNumber = null;
    currentQRDataUrl = null;
    console.warn('[WhatsApp] Desconectado del celular:', reason);
});

client.initialize();

const app = express();
app.use(express.json());

// Endpoint POST /send
app.post('/send', async (req, res) => {
    const { phone, message } = req.body;

    if (!phone || !message) {
        return res.status(400).json({ ok: false, error: 'Faltan campos: phone y message son requeridos.' });
    }

    if (connectionStatus !== 'conectado') {
        return res.status(503).json({ ok: false, error: `El servicio de WhatsApp no está conectado. Estado actual: ${connectionStatus}` });
    }

    const chatId = `${phone}@c.us`;

    try {
        const msg = await client.sendMessage(chatId, message);
        console.log(`[WhatsApp] Mensaje enviado a ${phone}. Id: ${msg.id.id}`);
        return res.json({ ok: true, messageId: msg.id.id });
    } catch (err) {
        console.error(`[WhatsApp] Error al enviar a ${phone}:`, err.message);
        return res.status(500).json({ ok: false, error: err.message });
    }
});

// Endpoint GET /qr para ver el QR desde el navegador
app.get('/qr', (req, res) => {
    if (connectionStatus === 'conectado') {
        return res.send('<h2>✅ WhatsApp ya está conectado y listo para usar.</h2>');
    }
    
    if (!currentQRDataUrl) {
        return res.send('<h2>⏳ Generando QR... Espera unos segundos y recarga la página.</h2>');
    }
    
    res.send(`
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
                <img src="${currentQRDataUrl}" alt="Código QR de WhatsApp" />
                <p style="font-size: 12px; color: #888;">La página se actualiza automáticamente cada 10 segundos.</p>
            </div>
        </body>
        </html>
    `);
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
    console.log(`[HTTP] Servicio de WhatsApp escuchando en puerto ${PORT}`);
    console.log(`[HTTP] Accede a http://localhost:${PORT}/qr para vincular tu cuenta`);
});
