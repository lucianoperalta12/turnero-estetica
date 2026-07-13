# TurneroWorker

Worker Service en .NET 8 que lee Google Calendar dos veces por día, busca turnos del día siguiente y envía recordatorios automáticos por WhatsApp Cloud API.

## Configuración

### 1. Google Calendar — Service Account

1. Ir a [Google Cloud Console](https://console.cloud.google.com/).
2. Crear un proyecto → habilitar **Google Calendar API**.
3. Crear una **Service Account** → descargar el archivo JSON de credenciales.
4. Guardar el archivo como `credentials.json` en la raíz del proyecto (junto al `.csproj`).
5. En Google Calendar, compartir el calendario (`victoriacalloni1996@gmail.com`) con el email de la Service Account (p.ej. `turnero@mi-proyecto.iam.gserviceaccount.com`) con permiso **"Hacer cambios en eventos"**.

### 2. WhatsApp Cloud API

Completar en `appsettings.json`:

```json
"WhatsApp": {
  "AccessToken": "EAAxxxxx...",
  "PhoneNumberId": "123456789012345",
  "ApiVersion": "v19.0",
  "TemplateName": "recordatorio_turno",  // dejar vacío para texto libre
  "TemplateLanguage": "es"
}
```

#### Opciones de envío

| Modo | Configuración | Requisito |
|------|--------------|-----------|
| **Template Message** | `TemplateName = "nombre_template"` | Template aprobado por Meta. El template debe tener 3 variables: `{{1}}` nombre, `{{2}}` fecha, `{{3}}` hora. |
| **Texto libre** | `TemplateName = ""` | Solo funciona si el destinatario inició conversación en las últimas 24hs. |

### 3. Formato de eventos en Calendar

Cada evento debe tener en la **descripción**:

```
Telefono: 3456 562288
```

El número se convierte automáticamente a E.164: `5493456562288`.

Cuando el recordatorio es enviado, se agrega al final de la descripción:

```
[RecordatorioEnviado]
```

Esto previene duplicados en ejecuciones posteriores.

### 4. Horarios de ejecución

En `appsettings.json`:

```json
"Schedule": {
  "TimeZone": "America/Argentina/Buenos_Aires",
  "ExecutionTimes": [ "09:00", "18:00" ]
}
```

En Windows se usa automáticamente `"Argentina Standard Time"` como alias.

## Smoke Test

1. Editar `appsettings.Development.json` y poner un horario 2-3 minutos en el futuro.
2. Correr:

```bash
cd TurneroWorker
dotnet run
```

3. Verificar en logs: conexión a Calendar, lectura de eventos, envío (o skip si ya marcado).

## Build

```bash
dotnet build TurneroWorker/TurneroWorker.csproj --configuration Release
```
