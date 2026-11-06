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
    private const string ClaveLocalStorage = "smartagenda_idioma";
    // Clave de cuando la app se llamaba "TareasSemanales": se sigue leyendo para no perder la preferencia guardada.
    private const string ClaveLocalStorageAntigua = "ts_idioma";

    public Idioma Actual { get; private set; } = Idioma.Es;

    public event Action? OnCambio;

    /// <summary>Recupera el idioma guardado en el navegador (si lo hay) la primera vez que se renderiza la página.</summary>
    public async Task InicializarAsync(IJSRuntime js)
    {
        try
        {
            var guardado = await js.InvokeAsync<string?>("localStorage.getItem", ClaveLocalStorage)
                ?? await js.InvokeAsync<string?>("localStorage.getItem", ClaveLocalStorageAntigua);
            if (guardado == "en")
            {
                Actual = Idioma.En;
                OnCambio?.Invoke();
            }
            await ActualizarLangDocumentoAsync(js);
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
            await ActualizarLangDocumentoAsync(js);
        }
        catch
        {
            // La preferencia simplemente no sobrevive a un recargo de página; no es crítico.
        }
        OnCambio?.Invoke();
    }

    /// <summary>
    /// Mantiene &lt;html lang&gt; al día: lo usan los lectores de pantalla para elegir la voz y el
    /// navegador para ofrecer traducir la página.
    /// </summary>
    private Task ActualizarLangDocumentoAsync(IJSRuntime js) =>
        js.InvokeVoidAsync("document.documentElement.setAttribute", "lang", CodigoWhisper).AsTask();

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

    /// <summary>
    /// Elige entre "clave.uno" y "clave.varios" según la cantidad, para no escribir "evento(s)".
    /// La cantidad se pasa como {0}; los argumentos extra siguen a partir de {1}.
    /// </summary>
    public string Plural(string clave, int cantidad, params object[] args) =>
        T(cantidad == 1 ? clave + ".uno" : clave + ".varios", [cantidad, .. args]);

    /// <summary>Une varias partes como en una frase: "a, b y c" / "a, b and c".</summary>
    public string Lista(IReadOnlyList<string> partes)
    {
        if (partes.Count <= 1) return partes.Count == 0 ? string.Empty : partes[0];
        var y = Actual == Idioma.En ? " and " : " y ";
        return string.Join(", ", partes.Take(partes.Count - 1)) + y + partes[^1];
    }

    private static readonly Dictionary<string, (string Es, string En)> Textos = new()
    {
        ["hero.subtitulo"] = (
            "Escribe o dicta tus planes en una frase. No se guarda nada hasta que confirmes lo que la app ha entendido.",
            "Type or dictate your plans in one sentence. Nothing is saved until you confirm what the app understood."),

        ["entrada.placeholder"] = (
            "Ej: dentista el viernes 2 a las 7, y gimnasio mañana a las 18:00",
            "E.g.: dentist Friday the 2nd at 7, and gym tomorrow at 6pm"),

        ["entrada.etiqueta"] = ("¿Qué tienes pendiente?", "What's coming up?"),
        ["entrada.atajo"] = ("Ctrl + Enter para revisar", "Ctrl + Enter to review"),

        ["mic.iniciar"] = ("Dictar por voz", "Dictate by voice"),
        ["mic.detener"] = ("Detener grabación", "Stop recording"),

        ["aviso.permisoBloqueado"] = (
            "Bloqueaste el acceso al micrófono. Haz clic en el icono junto a la URL de tu navegador para permitirlo y recarga la página.",
            "You blocked microphone access. Click the icon next to your browser's address bar to allow it, then reload the page."),

        ["boton.revisarEventos"] = ("Revisar eventos", "Review events"),
        ["boton.analizando"] = ("Analizando...", "Analyzing..."),

        ["aviso.grabando"] = (
            "Grabando... habla con normalidad, se detiene sola al dejar de oír voz.",
            "Recording... speak normally, it stops automatically once it stops hearing your voice."),
        ["aviso.transcribiendo"] = (
            "Transcribiendo el audio (puede tardar unos segundos la primera vez)...",
            "Transcribing the audio (may take a few seconds the first time)..."),

        ["previa.singular"] = ("He entendido este evento", "I understood this event"),
        ["previa.plural"] = ("He entendido estos {0} eventos", "I understood these {0} events"),
        ["previa.avisoAmbiguo"] = ("Revisa los marcados", "Check the flagged ones"),
        ["campo.titulo"] = ("Título", "Title"),
        ["campo.fecha"] = ("Fecha", "Date"),
        ["campo.hora"] = ("Hora", "Time"),
        ["previa.quitarTitle"] = ("Quitar este evento", "Remove this event"),
        ["boton.confirmarCrear"] = ("Confirmar y crear ({0})", "Confirm and create ({0})"),
        ["boton.creando"] = ("Creando...", "Creating..."),
        ["boton.cancelar"] = ("Cancelar", "Cancel"),
        ["boton.cerrar"] = ("Cerrar", "Close"),

        ["google.noConfigurado"] = (
            "Google Calendar no está configurado. Las tareas se guardan aquí igualmente; para enviarlas a tu calendario, sigue «Conectar con Google Calendar» en el README.",
            "Google Calendar isn't set up. Tasks are still saved here; to send them to your calendar, follow \"Google Calendar setup\" in the README."),
        ["google.noConectado"] = (
            "Conecta tu cuenta de Google para que las tareas se envíen a tu calendario automáticamente.",
            "Connect your Google account so tasks are sent to your calendar automatically."),
        ["boton.conectarGoogle"] = ("Conectar con Google", "Connect with Google"),
        ["boton.conectando"] = ("Conectando...", "Connecting..."),
        ["google.conectado"] = ("Conectado a Google Calendar", "Connected to Google Calendar"),
        ["boton.importarGoogle"] = ("Sincronizar ahora", "Sync now"),
        ["boton.importando"] = ("Sincronizando...", "Syncing..."),
        ["boton.desconectar"] = ("Desconectar", "Disconnect"),

        ["semana.navegacion"] = ("Navegación por semanas", "Week navigation"),
        ["semana.numero"] = ("Semana {0}", "Week {0}"),
        ["semana.anteriorTitle"] = ("Semana anterior", "Previous week"),
        ["semana.siguienteTitle"] = ("Semana siguiente", "Next week"),
        ["semana.renombrarTitle"] = ("Clic para renombrar esta semana", "Click to rename this week"),
        ["boton.hoy"] = ("Hoy", "Today"),

        ["dia.sinTareas"] = ("Sin tareas", "No tasks"),
        ["tarea.editarTitle"] = ("Editar", "Edit"),
        ["tarea.enviarGoogleTitle"] = ("Enviar a Google Calendar", "Send to Google Calendar"),
        ["tarea.actualizarGoogleTitle"] = ("Actualizar en Google Calendar", "Update in Google Calendar"),
        ["tarea.eliminarTitle"] = ("Eliminar", "Delete"),
        ["tarea.confirmarEliminar"] = ("¿Eliminar?", "Delete?"),
        ["tarea.sinHora"] = ("Sin hora", "No time"),
        ["tarea.sinTitulo"] = ("(Sin título)", "(Untitled)"),
        ["tarea.insigniaGoogleTitle"] = ("Sincronizada con Google Calendar", "Synced with Google Calendar"),

        ["edicion.descripcionPlaceholder"] = ("Descripción", "Description"),
        ["edicion.duracionTitle"] = ("Duración en minutos", "Duration in minutes"),
        ["boton.guardar"] = ("Guardar", "Save"),

        ["mensaje.sinEventos"] = (
            "No se pudo identificar ningún evento, intenta reformular.",
            "No event could be identified, try rephrasing."),
        ["mensaje.eventosCreados.uno"] = ("Se creó 1 evento.", "Created 1 event."),
        ["mensaje.eventosCreados.varios"] = ("Se crearon {0} eventos.", "Created {0} events."),
        ["mensaje.eventosCreadosConGoogle.uno"] = (
            "Se creó 1 evento y ya está en Google Calendar.",
            "Created 1 event; it's already in Google Calendar."),
        ["mensaje.eventosCreadosConGoogle.varios"] = (
            "Se crearon {0} eventos y ya están en Google Calendar.",
            "Created {0} events; they're already in Google Calendar."),
        ["mensaje.tareaSincronizada"] = (
            "'{0}' sincronizada con Google Calendar.",
            "'{0}' synced with Google Calendar."),
        ["mensaje.errorSincronizar"] = ("Error al sincronizar: {0}", "Sync error: {0}"),
        ["mensaje.conectadoGoogle"] = ("Conectado a Google Calendar.", "Connected to Google Calendar."),
        ["mensaje.errorConectarGoogle"] = (
            "No se pudo conectar con Google: {0}",
            "Couldn't connect with Google: {0}"),
        ["mensaje.errorConectarGoogleRedireccion"] = (
            "No se pudo completar la conexión con Google. Si Google mostró \"redirect_uri_mismatch\", añade la dirección de esta app a tus credenciales (ver README).",
            "Couldn't finish connecting to Google. If Google showed \"redirect_uri_mismatch\", add this app's address to your credentials (see README)."),
        ["mensaje.desconectadoGoogle"] = (
            "Se cerró la sesión de Google en esta app.",
            "Signed out of Google in this app."),
        ["mensaje.importacion"] = ("Sincronizado con Google: {0}.", "Synced with Google: {0}."),
        ["importacion.enviadas.uno"] = ("1 cambio enviado", "1 change sent"),
        ["importacion.enviadas.varios"] = ("{0} cambios enviados", "{0} changes sent"),
        ["mensaje.pendientesDeEnviar"] = (
            "Algunos cambios no han llegado a Google todavía; se enviarán solos en cuanto haya conexión.",
            "Some changes haven't reached Google yet; they'll be sent automatically once you're back online."),
        ["mensaje.importacionSinCambios"] = (
            "Todo estaba ya al día con Google Calendar.",
            "Everything was already up to date with Google Calendar."),
        ["importacion.nuevas.uno"] = ("1 tarea nueva", "1 new task"),
        ["importacion.nuevas.varios"] = ("{0} tareas nuevas", "{0} new tasks"),
        ["importacion.actualizadas.uno"] = ("1 actualizada", "1 updated"),
        ["importacion.actualizadas.varios"] = ("{0} actualizadas", "{0} updated"),
        ["importacion.eliminadas.uno"] = ("1 borrada porque ya no está en Google", "1 removed because it's no longer in Google"),
        ["importacion.eliminadas.varios"] = ("{0} borradas porque ya no están en Google", "{0} removed because they're no longer in Google"),
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
