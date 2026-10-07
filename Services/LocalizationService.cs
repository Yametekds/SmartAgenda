using Microsoft.JSInterop;
using SmartAgenda.Models;

namespace SmartAgenda.Services;

/// <summary>
/// Traducción de los textos de la interfaz (español/inglés) y, de paso, el único punto de verdad
/// sobre qué idioma está usando el usuario ahora mismo — el mismo valor que se le pasa al parser
/// de lenguaje natural y a Whisper, para que dictado, interpretación e interfaz vayan siempre
/// coordinados. Es "Scoped": cada circuito de Blazor Server (cada pestaña/usuario) tiene su propia
/// instancia, así que MainLayout (donde está el selector) y Home (donde se usa) siempre comparten
/// el mismo idioma sin necesidad de pasarlo manualmente entre componentes.
/// </summary>
public class LocalizationService
{
    private const string ClaveLocalStorage = "ts_idioma";

    public Idioma Actual { get; private set; } = Idioma.Es;

    public event Action? OnCambio;

    /// <summary>Recupera el idioma guardado en el navegador (si lo hay) la primera vez que se renderiza la página.</summary>
    public async Task InicializarAsync(IJSRuntime js)
    {
        try
        {
            var guardado = await js.InvokeAsync<string?>("localStorage.getItem", ClaveLocalStorage);
            if (guardado == "en")
            {
                Actual = Idioma.En;
                OnCambio?.Invoke();
            }
        }
        catch
        {
            // Si falla (p. ej. localStorage bloqueado), simplemente se queda en el idioma por defecto.
        }
    }

    public async Task EstablecerAsync(IJSRuntime js, Idioma idioma)
    {
        if (idioma == Actual) return;
        Actual = idioma;
        try
        {
            await js.InvokeVoidAsync("localStorage.setItem", ClaveLocalStorage, idioma == Idioma.En ? "en" : "es");
        }
        catch
        {
            // La preferencia simplemente no sobrevive a un recargo de página; no es crítico.
        }
        OnCambio?.Invoke();
    }

    public string CodigoWhisper => Actual == Idioma.En ? "en" : "es";

    public string CodigoCultura => Actual == Idioma.En ? "en-US" : "es-ES";

    /// <summary>Traduce la clave al idioma actual; admite parámetros de formato estilo string.Format.</summary>
    public string T(string clave, params object[] args)
    {
        if (!Textos.TryGetValue(clave, out var par))
        {
            return clave;
        }
        var plantilla = Actual == Idioma.En ? par.En : par.Es;
        return args.Length == 0 ? plantilla : string.Format(plantilla, args);
    }

    private static readonly Dictionary<string, (string Es, string En)> Textos = new()
    {
        ["hero.tituloPrefijo"] = ("Mis tareas", "My"),
        ["hero.tituloAcento"] = ("de la semana", "weekly tasks"),
        ["hero.subtitulo"] = (
            "Escribe o dicta lo que tengas que hacer — el resto lo hace la app.",
            "Write or dictate what you need to do — the app handles the rest."),

        ["entrada.placeholder"] = (
            "Ej: dentista el viernes 2 a las 7, y gimnasio mañana a las 18:00",
            "E.g.: dentist Friday the 2nd at 7, and gym tomorrow at 6pm"),

        ["mic.iniciar"] = ("Dictar por voz", "Dictate by voice"),
        ["mic.detener"] = ("Detener grabación", "Stop recording"),

        ["aviso.permisoBloqueado"] = (
            "🚫 Bloqueaste el acceso al micrófono. Haz clic en el icono junto a la URL de tu navegador para permitirlo y recarga la página.",
            "🚫 You blocked microphone access. Click the icon next to your browser's address bar to allow it, then reload the page."),

        ["boton.revisarEventos"] = ("Revisar eventos", "Review events"),
        ["boton.analizando"] = ("Analizando...", "Analyzing..."),

        ["aviso.grabando"] = (
            "🎤 Grabando... habla con normalidad, se detiene sola al dejar de oír voz.",
            "🎤 Recording... speak normally, it stops automatically once it stops hearing your voice."),
        ["aviso.transcribiendo"] = (
            "⏳ Transcribiendo el audio (puede tardar unos segundos la primera vez)...",
            "⏳ Transcribing the audio (may take a few seconds the first time)..."),

        ["previa.singular"] = ("He entendido este evento", "I understood this event"),
        ["previa.plural"] = ("He entendido estos {0} eventos", "I understood these {0} events"),
        ["previa.avisoAmbiguo"] = ("⚠ revisa los marcados", "⚠ check the flagged ones"),
        ["campo.titulo"] = ("Título", "Title"),
        ["previa.quitarTitle"] = ("Quitar este evento", "Remove this event"),
        ["boton.confirmarCrear"] = ("Confirmar y crear ({0})", "Confirm and create ({0})"),
        ["boton.creando"] = ("Creando...", "Creating..."),
        ["boton.cancelar"] = ("Cancelar", "Cancel"),

        ["google.noConfigurado"] = (
            "La integración con Google Calendar aún no está configurada. Revisa el README.md del proyecto: hay que crear unas credenciales propias en Google Cloud Console una sola vez (requisito técnico de Google, no de la app).",
            "Google Calendar integration isn't configured yet. Check the project's README.md: you need to create your own credentials in Google Cloud Console once (a technical requirement from Google, not from the app)."),
        ["google.noConectado"] = (
            "Conecta tu cuenta de Google para que las tareas se envíen a tu calendario automáticamente.",
            "Connect your Google account so tasks are sent to your calendar automatically."),
        ["boton.conectarGoogle"] = ("Conectar con Google", "Connect with Google"),
        ["boton.conectando"] = ("Conectando...", "Connecting..."),
        ["google.conectado"] = ("● Conectado a Google Calendar", "● Connected to Google Calendar"),
        ["boton.importarGoogle"] = ("Importar desde Google", "Import from Google"),
        ["boton.importando"] = ("Importando...", "Importing..."),
        ["boton.desconectar"] = ("Desconectar", "Disconnect"),

        ["semana.anteriorTitle"] = ("Semana anterior", "Previous week"),
        ["semana.siguienteTitle"] = ("Semana siguiente", "Next week"),
        ["semana.nombrePorDefecto"] = ("Semana", "Week"),
        ["semana.renombrarTitle"] = ("Clic para renombrar esta semana", "Click to rename this week"),
        ["boton.hoy"] = ("Hoy", "Today"),

        ["dia.sinTareas"] = ("Sin tareas", "No tasks"),
        ["tarea.editarTitle"] = ("Editar", "Edit"),
        ["tarea.enviarGoogleTitle"] = ("Enviar a Google Calendar", "Send to Google Calendar"),
        ["tarea.actualizarGoogleTitle"] = ("Actualizar en Google Calendar", "Update in Google Calendar"),
        ["tarea.eliminarTitle"] = ("Eliminar", "Delete"),
        ["tarea.insigniaGoogleTitle"] = ("Sincronizada con Google Calendar", "Synced with Google Calendar"),

        ["edicion.descripcionPlaceholder"] = ("Descripción", "Description"),
        ["edicion.duracionTitle"] = ("Duración en minutos", "Duration in minutes"),
        ["boton.guardar"] = ("Guardar", "Save"),

        ["mensaje.sinEventos"] = (
            "No se pudo identificar ningún evento, intenta reformular.",
            "No event could be identified, try rephrasing."),
        ["mensaje.eventosCreadosConGoogle"] = (
            "Se crearon {0} evento(s), {1} sincronizado(s) con Google Calendar.",
            "Created {0} event(s), {1} synced with Google Calendar."),
        ["mensaje.eventosCreadosSinGoogle"] = ("Se crearon {0} evento(s).", "Created {0} event(s)."),
        ["mensaje.tareaSincronizada"] = (
            "'{0}' sincronizada con Google Calendar.",
            "'{0}' synced with Google Calendar."),
        ["mensaje.errorSincronizar"] = ("Error al sincronizar: {0}", "Sync error: {0}"),
        ["mensaje.tareaActualizadaErrorGoogle"] = (
            "Tarea actualizada, pero no se pudo sincronizar con Google: {0}",
            "Task updated, but it couldn't be synced with Google: {0}"),
        ["mensaje.conectadoGoogle"] = ("Conectado a Google Calendar.", "Connected to Google Calendar."),
        ["mensaje.errorConectarGoogle"] = (
            "No se pudo conectar con Google: {0}",
            "Couldn't connect with Google: {0}"),
        ["mensaje.desconectadoGoogle"] = (
            "Se cerró la sesión de Google en esta app.",
            "Signed out of Google in this app."),
        ["mensaje.importacionCompleta"] = (
            "Importación completa: {0} tarea(s) nueva(s), {1} actualizada(s).",
            "Import complete: {0} new task(s), {1} updated."),
        ["mensaje.errorImportarGoogle"] = (
            "Error al importar desde Google: {0}",
            "Error importing from Google: {0}"),

        ["error.sinAudio"] = (
            "No se grabó nada. Revisa que el micrófono esté funcionando e inténtalo de nuevo.",
            "Nothing was recorded. Check that your microphone is working and try again."),
        ["error.sinVoz"] = (
            "No se detectó ninguna voz en la grabación. Inténtalo de nuevo hablando un poco más alto.",
            "No voice was detected in the recording. Try again speaking a bit louder."),
        ["error.servidor"] = (
            "El servidor no pudo transcribir el audio. Inténtalo de nuevo en unos segundos.",
            "The server couldn't transcribe the audio. Try again in a few seconds."),
        ["error.red"] = (
            "Error de red al subir el audio. Revisa tu conexión e inténtalo de nuevo.",
            "Network error while uploading the audio. Check your connection and try again."),
        ["error.generico"] = (
            "No se pudo transcribir el audio: {0}",
            "The audio couldn't be transcribed: {0}"),
    };
}
