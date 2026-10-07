using System.Globalization;
using System.Text.RegularExpressions;
using SmartAgenda.Models;

namespace SmartAgenda.Services;

/// <summary>
/// Interpreta texto en español o inglés (escrito o transcrito de audio) y extrae uno o varios
/// eventos. Es la única puerta de entrada para "convertir lenguaje natural en eventos": tanto el
/// flujo de texto como el de audio pasan por aquí, para no duplicar la lógica de interpretación.
/// El idioma se decide en <see cref="ReglasIdioma"/>: toda palabra/patrón específico de un idioma
/// vive ahí, mientras que el algoritmo de interpretación en sí (más abajo) es el mismo para ambos.
/// </summary>
public class ParserLenguajeNaturalService
{
    /// <param name="entrada">Texto en lenguaje natural (escrito o ya transcrito de audio). Puede describir uno o varios eventos.</param>
    /// <param name="idioma">Idioma en el que está escrita/dictada la entrada.</param>
    /// <param name="hoy">Referencia para "hoy"/"mañana"/etc. Por defecto, la fecha real de hoy.</param>
    /// <param name="semanaInicio">Lunes de la semana que se está viendo en la app (se usa como año/mes de referencia implícito).</param>
    public List<EventoPropuesto> ParsearMultiple(string entrada, Idioma idioma = Idioma.Es, DateOnly? hoy = null, DateOnly? semanaInicio = null)
    {
        var r = idioma == Idioma.En ? ReglasIdioma.Ingles : ReglasIdioma.Espanol;
        var fechaHoy = hoy ?? DateOnly.FromDateTime(DateTime.Now);
        _ = semanaInicio; // reservado por si en el futuro se usa para desambiguar más casos

        var clausulas = r.DividirEnClausulas(entrada);
        var eventos = new List<EventoPropuesto>();

        int? mesContexto = null;
        var añoContexto = fechaHoy.Year;
        DateOnly? fechaContexto = null;

        foreach (var clausula in clausulas)
        {
            var evento = r.ParsearClausula(clausula, fechaHoy, fechaContexto, ref mesContexto, ref añoContexto);
            if (evento is not null)
            {
                eventos.Add(evento);
                fechaContexto = evento.Fecha;
            }
        }

        // Una fecha "adivinada" solo a partir de un día de la semana suelto es razonable cuando es
        // el único evento (se asume la próxima ocurrencia, como antes). Si hay varios eventos en la
        // misma entrada, ese mismo supuesto es más arriesgado y se pide confirmación explícita.
        // Un número suelto sin respaldo (ni mes, ni día de la semana) nunca se suprime: es la fuente
        // de fechas inventadas más probable, así que siempre pide confirmación, sea o no el único evento.
        if (eventos.Count == 1 && eventos[0].PermiteSupresionSiUnico)
        {
            eventos[0].EsAmbiguo = false;
            eventos[0].MotivoAmbiguedad = null;
        }

        return eventos;
    }

    /// <summary>
    /// Todo lo específico de un idioma (vocabulario, patrones, mensajes) además del algoritmo que
    /// los usa. Dos instancias estáticas (<see cref="Espanol"/>, <see cref="Ingles"/>) cubren los
    /// idiomas soportados; añadir un idioma nuevo es, en principio, solo añadir una tercera.
    /// </summary>
    private sealed class ReglasIdioma
    {
        public required Dictionary<string, DayOfWeek> Dias { get; init; }
        public required Dictionary<string, int> Meses { get; init; }
        public required Dictionary<string, int> NumerosTexto { get; init; }
        public required string[] Muletillas { get; init; }
        public required string Conjuncion { get; init; }

        public required Regex RegexHora { get; init; }
        public required Regex RegexDuracion { get; init; }
        public required Regex RegexFechaExplicita { get; init; }
        public required Regex RegexPasadoManana { get; init; }
        public required Regex RegexDentroDeDias { get; init; }
        public required Regex RegexProximoDia { get; init; }
        public required Regex RegexEsteDia { get; init; }
        public required Regex RegexManana { get; init; }
        public required Regex RegexHoy { get; init; }
        public required Regex RegexDiaSemana { get; init; }
        public required Regex RegexMesSuelto { get; init; }
        public required Regex RegexSeparadorClausulas { get; init; }

        // Solo en español: "mañana" significa tanto "tomorrow" como "morning", así que un
        // "mañana" dentro de "de la mañana" no debe interpretarse como la fecha de mañana.
        public Regex? RegexMananaEsMomentoDelDia { get; init; }

        public required string EventoSinTitulo { get; init; }
        public required string MotivoFechaHeredada { get; init; }
        public required string MotivoSinFechaExacta { get; init; }
        public required string MotivoRevisaDia { get; init; }
        public required string MotivoNumeroSueltoSinRespaldo { get; init; }

        public bool TryParseNumero(string texto, out int valor)
        {
            // Quita un sufijo ordinal inglés ("5th", "2nd"...) antes de intentar leerlo como dígitos.
            var limpio = Regex.Replace(texto.Trim(), @"(?<=\d)(st|nd|rd|th)$", "", RegexOptions.IgnoreCase);
            if (int.TryParse(limpio, out valor)) return true;
            return NumerosTexto.TryGetValue(texto.Trim(), out valor);
        }

        // Carácter de control que nunca aparece en texto real: se usa para "proteger" temporalmente
        // el punto interno de "a.m."/"p.m." mientras se divide por puntos de fin de frase (ver abajo).
        // Es el mismo patrón para ambos idiomas: "am"/"pm" se escriben igual en los dos.
        private const string MarcadorPunto = "\u0001";
        private static readonly Regex RegexPuntoInternoPeriodo = new(@"(?<=[ap])\.(?=\s?m)", RegexOptions.IgnoreCase);

        public List<string> DividirEnClausulas(string entrada)
        {
            // El dictado por voz separa varias tareas con pausas que Whisper transcribe como puntos
            // de fin de frase, no solo con comas o "y"/"and" — así que también se divide por puntos.
            // El riesgo es partir "p.m."/"a.m." por la mitad, así que su punto interno se protege
            // primero (no se puede usar mayúscula-tras-punto como pista: el dictado casi nunca capitaliza).
            var protegido = RegexPuntoInternoPeriodo.Replace(entrada.Trim(), MarcadorPunto);

            var partes = RegexSeparadorClausulas.Split(protegido)
                .Select(p => p.Replace(MarcadorPunto, ".").Trim())
                .Where(p => p.Length > 0)
                .ToList();

            if (partes.Count <= 1) return new List<string> { entrada.Trim() };

            var resultado = new List<string>();
            foreach (var parte in partes)
            {
                if (resultado.Count > 0 && !TieneSenalTemporal(parte))
                {
                    // No parece el inicio de un evento nuevo (p. ej. "...mis padres y mi hermano"):
                    // se trata como continuación del evento anterior, no como uno independiente.
                    resultado[^1] = resultado[^1] + Conjuncion + parte;
                }
                else
                {
                    resultado.Add(parte);
                }
            }
            return resultado;
        }

        private bool TieneSenalTemporal(string texto) =>
            RegexDiaSemana.IsMatch(texto) || RegexHora.IsMatch(texto) || RegexMesSuelto.IsMatch(texto) ||
            RegexPasadoManana.IsMatch(texto) || RegexDentroDeDias.IsMatch(texto) ||
            RegexProximoDia.IsMatch(texto) || RegexEsteDia.IsMatch(texto) ||
            RegexManana.IsMatch(texto) || RegexHoy.IsMatch(texto);

        public EventoPropuesto? ParsearClausula(string clausula, DateOnly fechaHoy, DateOnly? fechaContexto, ref int? mesContexto, ref int añoContexto)
        {
            var texto = clausula.Trim();
            if (texto.Length == 0) return null;

            // 1. Duración ("durante 1 hora" / "for 1 hour")
            TimeSpan? duracion = null;
            var matchDuracion = RegexDuracion.Match(texto);
            if (matchDuracion.Success)
            {
                var cantidad = int.Parse(matchDuracion.Groups["cantidad"].Value);
                var unidad = matchDuracion.Groups["unidad"].Value.ToLowerInvariant();
                duracion = unidad.StartsWith("min") ? TimeSpan.FromMinutes(cantidad) : TimeSpan.FromHours(cantidad);
                texto = texto.Remove(matchDuracion.Index, matchDuracion.Length);
            }

            // 2. Hora ("a las 10" / "at 10", "a las siete de la mañana" / "at seven in the morning", "10am")
            TimeOnly? hora = null;
            var matchHora = RegexHora.Match(texto);
            if (matchHora.Success)
            {
                var hGroup = matchHora.Groups["h"].Success ? matchHora.Groups["h"] : matchHora.Groups["h2"];
                var mGroup = matchHora.Groups["m"].Success ? matchHora.Groups["m"] : matchHora.Groups["m2"];
                var periodoGroup = matchHora.Groups["periodo"].Success ? matchHora.Groups["periodo"] : matchHora.Groups["periodo2"];

                if (TryParseNumero(hGroup.Value, out var h))
                {
                    var min = mGroup.Success && int.TryParse(mGroup.Value, out var mm) ? mm : 0;
                    if (h <= 23 && min <= 59)
                    {
                        if (periodoGroup.Success)
                        {
                            // Se quitan espacios y puntos para comparar ("p. m." y "p m" deben
                            // reconocerse igual que "pm") y luego se mira solo la letra inicial.
                            var periodo = Regex.Replace(periodoGroup.Value.ToLowerInvariant(), @"[.\s]", "");
                            var esPm = periodo.StartsWith('p') || periodo.Contains("tarde") || periodo.Contains("noche") ||
                                       periodo.Contains("afternoon") || periodo.Contains("evening") || periodo.Contains("night");
                            var esAm = periodo.StartsWith('a') || periodo.Contains("mañana") || periodo.Contains("manana") ||
                                       periodo.Contains("morning");
                            if (esPm && h < 12) h += 12;
                            if (esAm && h == 12) h = 0;
                        }
                        hora = new TimeOnly(h % 24, min);
                        texto = texto.Remove(matchHora.Index, matchHora.Length);
                    }
                }
            }

            // 3. Fecha: relativas primero (más específicas), luego explícita o día de la semana suelto.
            DateOnly? fecha = null;
            var bajaConfianza = false;
            // Si la fecha queda ambigua, ¿es razonable asumirla igualmente cuando es el único evento
            // de la entrada (como "el jueves ...", un modismo claro), o nunca debe darse por buena sin
            // confirmación explícita (como un número suelto que podría ser cualquier cosa)?
            var permiteSupresionSiUnico = true;

            var mPasado = RegexPasadoManana.Match(texto);
            if (mPasado.Success)
            {
                fecha = fechaHoy.AddDays(2);
                texto = texto.Remove(mPasado.Index, mPasado.Length);
            }
            else
            {
                var mDentro = RegexDentroDeDias.Match(texto);
                if (mDentro.Success && TryParseNumero(mDentro.Groups["n"].Value, out var n))
                {
                    fecha = fechaHoy.AddDays(n);
                    texto = texto.Remove(mDentro.Index, mDentro.Length);
                }
                else
                {
                    var mProximo = RegexProximoDia.Match(texto);
                    if (mProximo.Success && Dias.TryGetValue(mProximo.Groups["dia"].Value, out var diaProx))
                    {
                        var dif = ((int)diaProx - (int)fechaHoy.DayOfWeek + 7) % 7;
                        if (dif == 0) dif = 7;
                        fecha = fechaHoy.AddDays(dif);
                        texto = texto.Remove(mProximo.Index, mProximo.Length);
                    }
                    else
                    {
                        var mEste = RegexEsteDia.Match(texto);
                        if (mEste.Success && Dias.TryGetValue(mEste.Groups["dia"].Value, out var diaEste))
                        {
                            var dif = ((int)diaEste - (int)fechaHoy.DayOfWeek + 7) % 7;
                            fecha = fechaHoy.AddDays(dif);
                            texto = texto.Remove(mEste.Index, mEste.Length);
                        }
                        else
                        {
                            var mManana = RegexManana.Match(texto);
                            if (mManana.Success && (RegexMananaEsMomentoDelDia is null || !RegexMananaEsMomentoDelDia.IsMatch(texto)))
                            {
                                fecha = fechaHoy.AddDays(1);
                                texto = texto.Remove(mManana.Index, mManana.Length);
                            }
                            else
                            {
                                var mHoy = RegexHoy.Match(texto);
                                if (mHoy.Success)
                                {
                                    fecha = fechaHoy;
                                    texto = texto.Remove(mHoy.Index, mHoy.Length);
                                }
                                else
                                {
                                    (fecha, texto, bajaConfianza, permiteSupresionSiUnico) = ResolverFechaExplicitaODiaSemana(texto, fechaHoy, ref mesContexto, ref añoContexto);
                                }
                            }
                        }
                    }
                }
            }

            string? motivo = null;
            if (fecha is null)
            {
                // Sin ninguna señal propia: se hereda la fecha del evento anterior en la misma entrada
                // (p. ej. "mañana a las nueve... y a las seis gimnasio" → el gimnasio también es mañana).
                // Si es el primer evento y no hay contexto previo, se asume hoy.
                fecha = fechaContexto ?? fechaHoy;
                bajaConfianza = true;
                motivo = fechaContexto.HasValue ? MotivoFechaHeredada : MotivoSinFechaExacta;
            }
            else if (bajaConfianza)
            {
                motivo = permiteSupresionSiUnico ? MotivoRevisaDia : MotivoNumeroSueltoSinRespaldo;
            }

            texto = LimpiarTexto(texto);
            if (texto.Length == 0) texto = EventoSinTitulo;

            return new EventoPropuesto
            {
                Texto = texto,
                Fecha = fecha.Value,
                Hora = hora,
                Duracion = duracion,
                PermiteSupresionSiUnico = permiteSupresionSiUnico,
                EsAmbiguo = bajaConfianza,
                MotivoAmbiguedad = motivo
            };
        }

        private (DateOnly? fecha, string texto, bool bajaConfianza, bool permiteSupresionSiUnico) ResolverFechaExplicitaODiaSemana(
            string texto, DateOnly fechaHoy, ref int? mesContexto, ref int añoContexto)
        {
            Match? mDiaSemana = RegexDiaSemana.Match(texto) is { Success: true } msem ? msem : null;
            DayOfWeek? diaSemana = mDiaSemana != null && Dias.TryGetValue(mDiaSemana.Groups["dia"].Value, out var dsVal) ? dsVal : null;

            Match? mFecha = RegexFechaExplicita.Match(texto) is { Success: true } mf && TryParseNumero(mf.Groups["dia"].Value, out var diaNum) && diaNum is >= 1 and <= 31
                ? mf
                : null;

            // Quita primero el tramo que aparece más tarde en el texto, para no invalidar el índice del otro.
            var tramos = new List<(int Index, int Length)>();
            if (mFecha != null) tramos.Add((mFecha.Index, mFecha.Length));
            if (mDiaSemana != null) tramos.Add((mDiaSemana.Index, mDiaSemana.Length));
            foreach (var (index, length) in tramos.OrderByDescending(t => t.Index))
            {
                texto = texto.Remove(index, length);
            }

            if (mFecha != null)
            {
                TryParseNumero(mFecha.Groups["dia"].Value, out var dia);
                int? mesExplicito = mFecha.Groups["mes"].Success && Meses.TryGetValue(mFecha.Groups["mes"].Value, out var mesVal)
                    ? mesVal
                    : null;

                // Un mes "con respaldo" es uno explícito en esta cláusula o heredado de una cláusula
                // anterior en la misma entrada (p. ej. "...dos de octubre... el primero..." — el
                // "primero" hereda octubre). Solo cuando NO hay ningún indicio de mes (se usó el mes de
                // hoy a ciegas) el número suelto se considera poco fiable por sí solo.
                int? mesConRespaldo = mesExplicito ?? mesContexto;
                var mes = mesConRespaldo ?? fechaHoy.Month;

                if (mes is >= 1 and <= 12 && dia >= 1 && dia <= DateTime.DaysInMonth(añoContexto, mes))
                {
                    var fechaPropuesta = new DateOnly(añoContexto, mes, dia);

                    // Si el número no tiene un mes que lo respalde y además hay un día de la semana
                    // mencionado que NO coincide con esa fecha, el número es sospechoso — probablemente
                    // no era una fecha (p. ej. una hora que no se reconoció como tal,
                    // "domingo las 6" → "6" sin mes). En ese caso se prioriza el día de la semana.
                    if (mesConRespaldo is null && diaSemana is { } dsComprobar && fechaPropuesta.DayOfWeek != dsComprobar)
                    {
                        var difDescarte = ((int)dsComprobar - (int)fechaHoy.DayOfWeek + 7) % 7;
                        return (fechaHoy.AddDays(difDescarte), texto, false, true);
                    }

                    // Un número suelto sin ningún mes ni día de la semana que lo respalde es la fuente
                    // de error más probable (una hora mal reconocida, una cantidad, etc.): nunca se da
                    // por buena en silencio, ni siquiera cuando es el único evento de la entrada.
                    if (mesConRespaldo is null && diaSemana is null)
                    {
                        return (fechaPropuesta, texto, true, false);
                    }

                    if (mesExplicito.HasValue) mesContexto = mesExplicito.Value;
                    return (fechaPropuesta, texto, false, true);
                }

                return (null, texto, true, true);
            }

            if (diaSemana is { } ds)
            {
                var dif = ((int)ds - (int)fechaHoy.DayOfWeek + 7) % 7;
                return (fechaHoy.AddDays(dif), texto, true, true);
            }

            return (null, texto, true, true);
        }

        private string LimpiarTexto(string texto)
        {
            var resultado = texto.Trim();
            resultado = Regex.Replace(resultado, @"\s+", " ").Trim();
            resultado = Regex.Replace(resultado, @"^[,.;]\s*", "");
            resultado = Regex.Replace(resultado, @"\s*[,.;]\s*$", "");

            bool cambiado;
            do
            {
                cambiado = false;
                foreach (var m in Muletillas)
                {
                    var patron = new Regex($@"^{Regex.Escape(m)}\b", RegexOptions.IgnoreCase);
                    var nuevo = patron.Replace(resultado, "").TrimStart();
                    if (nuevo != resultado)
                    {
                        resultado = nuevo;
                        cambiado = true;
                    }
                }
            } while (cambiado);

            resultado = Regex.Replace(resultado, @"\s+", " ").Trim();
            resultado = Regex.Replace(resultado, @"^[,.;]\s*", "").Trim();
            resultado = Regex.Replace(resultado, @"\s*[,.;]\s*$", "").Trim();

            if (resultado.Length > 0)
            {
                resultado = char.ToUpper(resultado[0], CultureInfo.CurrentCulture) + resultado[1..];
            }

            return resultado;
        }

        // ---- Construcción de las reglas de cada idioma ----

        public static readonly ReglasIdioma Espanol = CrearEspanol();
        public static readonly ReglasIdioma Ingles = CrearIngles();

        private static ReglasIdioma CrearEspanol()
        {
            var dias = new Dictionary<string, DayOfWeek>(StringComparer.OrdinalIgnoreCase)
            {
                ["lunes"] = DayOfWeek.Monday,
                ["martes"] = DayOfWeek.Tuesday,
                ["miercoles"] = DayOfWeek.Wednesday,
                ["miércoles"] = DayOfWeek.Wednesday,
                ["jueves"] = DayOfWeek.Thursday,
                ["viernes"] = DayOfWeek.Friday,
                ["sabado"] = DayOfWeek.Saturday,
                ["sábado"] = DayOfWeek.Saturday,
                ["domingo"] = DayOfWeek.Sunday,
            };

            var meses = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase)
            {
                ["enero"] = 1, ["febrero"] = 2, ["marzo"] = 3, ["abril"] = 4, ["mayo"] = 5, ["junio"] = 6,
                ["julio"] = 7, ["agosto"] = 8, ["septiembre"] = 9, ["setiembre"] = 9, ["octubre"] = 10,
                ["noviembre"] = 11, ["diciembre"] = 12,
            };

            // "una" se excluye a propósito: es casi siempre el artículo ("una cita"), no el número 1.
            var numeros = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase)
            {
                ["cero"] = 0, ["uno"] = 1, ["primero"] = 1, ["dos"] = 2, ["tres"] = 3, ["cuatro"] = 4,
                ["cinco"] = 5, ["seis"] = 6, ["siete"] = 7, ["ocho"] = 8, ["nueve"] = 9, ["diez"] = 10,
                ["once"] = 11, ["doce"] = 12, ["trece"] = 13, ["catorce"] = 14, ["quince"] = 15,
                ["dieciseis"] = 16, ["dieciséis"] = 16, ["diecisiete"] = 17, ["dieciocho"] = 18, ["diecinueve"] = 19,
                ["veinte"] = 20, ["veintiuno"] = 21, ["veintidos"] = 22, ["veintidós"] = 22,
                ["veintitres"] = 23, ["veintitrés"] = 23, ["veinticuatro"] = 24, ["veinticinco"] = 25,
                ["veintiseis"] = 26, ["veintiséis"] = 26, ["veintisiete"] = 27, ["veintiocho"] = 28,
                ["veintinueve"] = 29, ["treinta"] = 30,
            };

            var patronNumeroPalabra = string.Join("|", numeros.Keys.OrderByDescending(k => k.Length).Select(Regex.Escape));
            var patronMes = string.Join("|", meses.Keys.Select(Regex.Escape));

            // am/pm tolerante a variaciones típicas del dictado por voz: con o sin puntos, con o sin
            // espacio entre letras ("p.m.", "p.m", "pm", "p m" son todas equivalentes).
            const string patronPeriodoDia = @"[ap]\.?\s?m\.?|de la ma[nñ]ana|de la tarde|de la noche";

            return new ReglasIdioma
            {
                Dias = dias,
                Meses = meses,
                NumerosTexto = numeros,
                Conjuncion = " y ",
                Muletillas = new[]
                {
                    "tengo que", "tengo", "debo", "necesito", "quiero", "voy a", "recuerdame que",
                    "recuérdame que", "recuerdame", "recuérdame", "recordar", "luego", "despues", "después",
                    "entonces", "tambien", "también", "para", "del", "el", "las", "la",
                    "unos", "unas", "una", "un", "a", "que"
                },
                // La hora exige un prefijo "la(s)" (con o sin la "a" delante, porque el dictado por voz a
                // veces la recorta: "a las 6" → "las 6") o un sufijo am/pm/periodo del día: así nunca se
                // confunde con el número de un día del mes ("domingo cuatro" no tiene ninguna de esas marcas).
                // Importante: ninguna de las dos alternativas termina en \b — un \b final falla cuando el
                // periodo acaba en punto ("p.m."), porque un punto no es un carácter de "palabra".
                RegexHora = new Regex(
                    $@"\b(?:a\s+)?las?\s+(?<h>\d{{1,2}}|{patronNumeroPalabra})(?::(?<m>\d{{2}}))?(?:\s*(?<periodo>{patronPeriodoDia}))?" +
                    $@"|\b(?<h2>\d{{1,2}})(?::(?<m2>\d{{2}}))?\s*(?<periodo2>{patronPeriodoDia})\b",
                    RegexOptions.IgnoreCase),
                RegexDuracion = new Regex(
                    @"\b(?:durante|por|de)\s+(?<cantidad>\d+)\s*(?<unidad>horas?|h|minutos?|min)\b",
                    RegexOptions.IgnoreCase),
                RegexFechaExplicita = new Regex(
                    $@"\b(?:el\s+|del\s+)?(?<dia>\d{{1,2}}|{patronNumeroPalabra})\s*(?:de\s+(?<mes>{patronMes}))?\b",
                    RegexOptions.IgnoreCase),
                RegexPasadoManana = new Regex(@"\bpasado\s+ma[nñ]ana\b", RegexOptions.IgnoreCase),
                RegexDentroDeDias = new Regex(
                    $@"\bdentro\s+de\s+(?<n>\d+|{patronNumeroPalabra})\s+d[ií]as?\b", RegexOptions.IgnoreCase),
                RegexProximoDia = new Regex(
                    @"\bel\s+pr[oó]ximo\s+(?<dia>lunes|martes|mi[eé]rcoles|jueves|viernes|s[aá]bado|domingo)\b",
                    RegexOptions.IgnoreCase),
                RegexEsteDia = new Regex(
                    @"\beste\s+(?<dia>lunes|martes|mi[eé]rcoles|jueves|viernes|s[aá]bado|domingo)\b",
                    RegexOptions.IgnoreCase),
                RegexManana = new Regex(@"\bma[nñ]ana\b", RegexOptions.IgnoreCase),
                RegexHoy = new Regex(@"\bhoy\b", RegexOptions.IgnoreCase),
                RegexDiaSemana = new Regex(
                    @"\b(el\s+|del\s+)?(?<dia>lunes|martes|mi[eé]rcoles|jueves|viernes|s[aá]bado|domingo)\b",
                    RegexOptions.IgnoreCase),
                RegexMesSuelto = new Regex($@"\b({patronMes})\b", RegexOptions.IgnoreCase),
                RegexSeparadorClausulas = new Regex(@",|;|\by\b|\.", RegexOptions.IgnoreCase),
                RegexMananaEsMomentoDelDia = new Regex(@"de la ma[nñ]ana", RegexOptions.IgnoreCase),
                EventoSinTitulo = "(Evento sin título)",
                MotivoFechaHeredada = "No repitió una fecha propia: se usó la del evento anterior.",
                MotivoSinFechaExacta = "No se indicó una fecha exacta.",
                MotivoRevisaDia = "No se indicó una fecha exacta: revisa el día.",
                MotivoNumeroSueltoSinRespaldo = "Se interpretó un número suelto como día del mes, pero nada lo confirma (¿era en realidad una hora?). Revisa la fecha.",
            };
        }

        private static ReglasIdioma CrearIngles()
        {
            var dias = new Dictionary<string, DayOfWeek>(StringComparer.OrdinalIgnoreCase)
            {
                ["monday"] = DayOfWeek.Monday,
                ["tuesday"] = DayOfWeek.Tuesday,
                ["wednesday"] = DayOfWeek.Wednesday,
                ["thursday"] = DayOfWeek.Thursday,
                ["friday"] = DayOfWeek.Friday,
                ["saturday"] = DayOfWeek.Saturday,
                ["sunday"] = DayOfWeek.Sunday,
            };

            var meses = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase)
            {
                ["january"] = 1, ["february"] = 2, ["march"] = 3, ["april"] = 4, ["may"] = 5, ["june"] = 6,
                ["july"] = 7, ["august"] = 8, ["september"] = 9, ["october"] = 10,
                ["november"] = 11, ["december"] = 12,
            };

            // Cardinales (para horas, "in 2 days"...) y ordinales (para fechas: "the fifth", "March 3rd").
            var numeros = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase)
            {
                ["zero"] = 0, ["one"] = 1, ["two"] = 2, ["three"] = 3, ["four"] = 4, ["five"] = 5,
                ["six"] = 6, ["seven"] = 7, ["eight"] = 8, ["nine"] = 9, ["ten"] = 10,
                ["eleven"] = 11, ["twelve"] = 12, ["thirteen"] = 13, ["fourteen"] = 14, ["fifteen"] = 15,
                ["sixteen"] = 16, ["seventeen"] = 17, ["eighteen"] = 18, ["nineteen"] = 19, ["twenty"] = 20,
                ["twenty-one"] = 21, ["twenty-two"] = 22, ["twenty-three"] = 23, ["twenty-four"] = 24,
                ["twenty-five"] = 25, ["twenty-six"] = 26, ["twenty-seven"] = 27, ["twenty-eight"] = 28,
                ["twenty-nine"] = 29, ["thirty"] = 30,
                ["first"] = 1, ["second"] = 2, ["third"] = 3, ["fourth"] = 4, ["fifth"] = 5,
                ["sixth"] = 6, ["seventh"] = 7, ["eighth"] = 8, ["ninth"] = 9, ["tenth"] = 10,
                ["eleventh"] = 11, ["twelfth"] = 12, ["thirteenth"] = 13, ["fourteenth"] = 14, ["fifteenth"] = 15,
                ["sixteenth"] = 16, ["seventeenth"] = 17, ["eighteenth"] = 18, ["nineteenth"] = 19,
                ["twentieth"] = 20, ["twenty-first"] = 21, ["twenty-second"] = 22, ["twenty-third"] = 23,
                ["twenty-fourth"] = 24, ["twenty-fifth"] = 25, ["twenty-sixth"] = 26, ["twenty-seventh"] = 27,
                ["twenty-eighth"] = 28, ["twenty-ninth"] = 29, ["thirtieth"] = 30, ["thirty-first"] = 31,
            };

            var patronNumeroPalabra = string.Join("|", numeros.Keys.OrderByDescending(k => k.Length).Select(Regex.Escape));
            var patronMes = string.Join("|", meses.Keys.Select(Regex.Escape));
            var patronDia = @"\d{1,2}(?:st|nd|rd|th)?|" + patronNumeroPalabra;

            // am/pm tolerante a variaciones típicas del dictado por voz: con o sin puntos, con o sin
            // espacio entre letras ("p.m.", "p.m", "pm", "p m" son todas equivalentes).
            const string patronPeriodoDia = @"[ap]\.?\s?m\.?|in the morning|in the afternoon|in the evening|at night";

            return new ReglasIdioma
            {
                Dias = dias,
                Meses = meses,
                NumerosTexto = numeros,
                Conjuncion = " and ",
                Muletillas = new[]
                {
                    "i have to", "i have", "i need to", "i need", "i must", "i want to", "i want",
                    "i'm going to", "im going to", "remind me to", "remind me", "remember to", "remember",
                    "then", "also", "too", "for", "the", "an", "a", "to", "that"
                },
                // La hora exige el prefijo "at" (igual que el español exige "la(s)") o un sufijo
                // am/pm/periodo del día: así nunca se confunde con el número de un día del mes.
                RegexHora = new Regex(
                    $@"\bat\s+(?<h>\d{{1,2}}|{patronNumeroPalabra})(?::(?<m>\d{{2}}))?(?:\s*(?<periodo>{patronPeriodoDia}))?" +
                    $@"|\b(?<h2>\d{{1,2}})(?::(?<m2>\d{{2}}))?\s*(?<periodo2>{patronPeriodoDia})\b",
                    RegexOptions.IgnoreCase),
                RegexDuracion = new Regex(
                    @"\bfor\s+(?<cantidad>\d+)\s*(?<unidad>hours?|hrs?|h|minutes?|mins?|min)\b",
                    RegexOptions.IgnoreCase),
                // Dos órdenes posibles: "March 5th" (mes primero) y "the 5th of March" / "the 5th" (día primero).
                RegexFechaExplicita = new Regex(
                    $@"\b(?:on\s+)?(?<mes>{patronMes})\s+(?:the\s+)?(?<dia>{patronDia})\b" +
                    $@"|\b(?:on\s+)?(?:the\s+)?(?<dia>{patronDia})(?:\s+of\s+(?<mes>{patronMes}))?\b",
                    RegexOptions.IgnoreCase),
                RegexPasadoManana = new Regex(@"\bday\s+after\s+tomorrow\b", RegexOptions.IgnoreCase),
                RegexDentroDeDias = new Regex(
                    $@"\bin\s+(?<n>\d+|{patronNumeroPalabra})\s+days?\b", RegexOptions.IgnoreCase),
                RegexProximoDia = new Regex(
                    @"\b(?:the\s+)?next\s+(?<dia>monday|tuesday|wednesday|thursday|friday|saturday|sunday)\b",
                    RegexOptions.IgnoreCase),
                RegexEsteDia = new Regex(
                    @"\bthis\s+(?<dia>monday|tuesday|wednesday|thursday|friday|saturday|sunday)\b",
                    RegexOptions.IgnoreCase),
                RegexManana = new Regex(@"\btomorrow\b", RegexOptions.IgnoreCase),
                RegexHoy = new Regex(@"\btoday\b", RegexOptions.IgnoreCase),
                RegexDiaSemana = new Regex(
                    @"\b(?:on\s+)?(?<dia>monday|tuesday|wednesday|thursday|friday|saturday|sunday)\b",
                    RegexOptions.IgnoreCase),
                RegexMesSuelto = new Regex($@"\b({patronMes})\b", RegexOptions.IgnoreCase),
                RegexSeparadorClausulas = new Regex(@",|;|\band\b|\.", RegexOptions.IgnoreCase),
                RegexMananaEsMomentoDelDia = null, // en inglés "tomorrow" y "morning" son palabras distintas: no hace falta.
                EventoSinTitulo = "(Untitled event)",
                MotivoFechaHeredada = "No date was given: the previous event's date was used.",
                MotivoSinFechaExacta = "No exact date was given.",
                MotivoRevisaDia = "No exact date was given: check the day.",
                MotivoNumeroSueltoSinRespaldo = "A standalone number was read as a day of the month, but nothing confirms it (was it actually meant as a time?). Check the date.",
            };
        }
    }
}
