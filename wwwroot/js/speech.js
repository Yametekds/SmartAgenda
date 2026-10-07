// Graba audio con APIs estándar del navegador (getUserMedia + AudioContext), soportadas
// prácticamente en cualquier navegador moderno, y lo sube al servidor para transcribirlo con
// Whisper (en vez de depender del reconocimiento de voz propio de cada navegador, que solo
// funciona bien en Chrome/Edge). El texto resultante entra por el mismo flujo que si se
// hubiera escrito a mano.
window.smartAgendaSpeech = {
    _ctx: null,
    _source: null,
    _processor: null,
    _stream: null,
    _muestras: [],
    _dotNetRef: null,
    _idioma: 'es',
    _timeoutId: null,
    _silencioIntervalId: null,
    _ultimoSonido: 0,
    _sonidoDetectado: false,

    // Umbral de volumen (RMS, 0-1) por encima del cual se considera que hay voz, y cuánto
    // silencio seguido hace falta tras detectar voz para asumir que el usuario terminó de hablar.
    UMBRAL_VOZ: 0.01,
    SILENCIO_MAX_MS: 1500,

    // Solo cancelación de eco (segura, estándar). noiseSuppression/autoGainControl se probaron y
    // se quitaron: en pruebas reales, la voz llegaba cada vez más débil al VAD tras el primer uso
    // — típico de que la supresión de ruido adaptativa del móvil se vuelve más agresiva con el
    // tiempo y termina atenuando la propia voz, no solo el ruido de fondo.
    RESTRICCIONES_AUDIO: {
        echoCancellation: true
    },

    soportado: function () {
        return !!(navigator.mediaDevices && navigator.mediaDevices.getUserMedia) &&
            !!(window.AudioContext || window.webkitAudioContext);
    },

    // Comprueba el permiso de micrófono y, si aún no se ha decidido ("prompt"), lo solicita
    // activamente ahora mismo. Si el usuario ya lo bloqueó antes, el navegador no vuelve a
    // preguntar (es una restricción de seguridad del propio navegador, no se puede forzar);
    // en ese caso devolvemos 'denied' para que la app pueda avisar con un mensaje claro.
    comprobarPermiso: async function () {
        if (!this.soportado()) return 'no-soportado';

        try {
            if (navigator.permissions && navigator.permissions.query) {
                const estado = await navigator.permissions.query({ name: 'microphone' });
                if (estado.state === 'granted') return 'granted';
                if (estado.state === 'denied') return 'denied';
            }

            const stream = await navigator.mediaDevices.getUserMedia({ audio: this.RESTRICCIONES_AUDIO });
            stream.getTracks().forEach(t => t.stop());
            return 'granted';
        } catch (e) {
            return 'denied';
        }
    },

    iniciar: async function (dotNetRef, idioma) {
        this._dotNetRef = dotNetRef;
        this._idioma = idioma === 'en' ? 'en' : 'es';
        this._muestras = [];
        this._sonidoDetectado = false;
        this._ultimoSonido = Date.now();

        try {
            this._stream = await navigator.mediaDevices.getUserMedia({ audio: this.RESTRICCIONES_AUDIO });
        } catch (e) {
            dotNetRef.invokeMethodAsync('OnErrorReconocimiento', 'not-allowed');
            return;
        }

        const AudioCtx = window.AudioContext || window.webkitAudioContext;
        this._ctx = new AudioCtx();
        this._source = this._ctx.createMediaStreamSource(this._stream);
        // ScriptProcessorNode está obsoleto a favor de AudioWorklet, pero sigue soportado en
        // todos los navegadores (incluidos los que no soportan AudioWorklet todavía), y para
        // una grabación corta de dictado es más que suficiente.
        this._processor = this._ctx.createScriptProcessor(4096, 1, 1);

        this._processor.onaudioprocess = (e) => {
            const datos = e.inputBuffer.getChannelData(0);
            this._muestras.push(new Float32Array(datos));

            let sumaCuadrados = 0;
            for (let i = 0; i < datos.length; i++) sumaCuadrados += datos[i] * datos[i];
            const rms = Math.sqrt(sumaCuadrados / datos.length);
            if (rms > this.UMBRAL_VOZ) {
                this._ultimoSonido = Date.now();
                this._sonidoDetectado = true;
            }
        };

        this._source.connect(this._processor);
        this._processor.connect(this._ctx.destination);

        // Detiene sola la grabación cuando lleva un rato de silencio tras haber oído voz,
        // así no hace falta pulsar el botón de nuevo para terminar.
        this._silencioIntervalId = setInterval(() => {
            if (this._sonidoDetectado && Date.now() - this._ultimoSonido > this.SILENCIO_MAX_MS) {
                this.detener();
            }
        }, 200);

        // Límite de seguridad: 60s máximo por grabación, por si nunca se detecta silencio.
        this._timeoutId = setTimeout(() => this.detener(), 60000);
    },

    detener: async function () {
        if (this._timeoutId) {
            clearTimeout(this._timeoutId);
            this._timeoutId = null;
        }
        if (this._silencioIntervalId) {
            clearInterval(this._silencioIntervalId);
            this._silencioIntervalId = null;
        }

        const dotNetRef = this._dotNetRef;
        if (!dotNetRef || !this._ctx) return;

        const sampleRateOrigen = this._ctx.sampleRate;
        const muestras = this._muestras;
        this._dotNetRef = null;

        if (this._processor) { this._processor.disconnect(); this._processor = null; }
        if (this._source) { this._source.disconnect(); this._source = null; }
        if (this._stream) { this._stream.getTracks().forEach(t => t.stop()); this._stream = null; }
        const ctx = this._ctx;
        this._ctx = null;
        await ctx.close();

        if (muestras.length === 0) {
            dotNetRef.invokeMethodAsync('OnErrorReconocimiento', 'sin-audio');
            return;
        }

        dotNetRef.invokeMethodAsync('OnTranscribiendo');

        try {
            const wavBlob = this._codificarWav(muestras, sampleRateOrigen, 16000);
            const respuesta = await fetch('/api/transcribir?idioma=' + this._idioma, { method: 'POST', body: wavBlob });

            if (!respuesta.ok) {
                dotNetRef.invokeMethodAsync('OnErrorReconocimiento', 'error-servidor');
                return;
            }

            const json = await respuesta.json();
            const texto = (json.texto || '').trim();
            if (texto.length === 0) {
                dotNetRef.invokeMethodAsync('OnErrorReconocimiento', 'sin-voz');
            } else {
                dotNetRef.invokeMethodAsync('OnTranscripcion', texto);
            }
        } catch (e) {
            dotNetRef.invokeMethodAsync('OnErrorReconocimiento', 'red');
        }
    },

    _codificarWav: function (bloques, sampleRateOrigen, sampleRateDestino) {
        let total = 0;
        for (const b of bloques) total += b.length;
        const muestras = new Float32Array(total);
        let offset = 0;
        for (const b of bloques) { muestras.set(b, offset); offset += b.length; }

        const ratio = sampleRateOrigen / sampleRateDestino;
        const longitudDestino = Math.max(1, Math.floor(muestras.length / ratio));
        const destino = new Float32Array(longitudDestino);
        // Downsampling por promedio (no por "vecino más cercano"): tomar 1 de cada N muestras sin
        // filtrar primero introduce aliasing (ruido de alta frecuencia que no existía) y degrada
        // la señal que llega al detector de voz y a Whisper. Promediar cada bloque de muestras
        // actúa como un filtro paso-bajo simple que lo evita.
        for (let i = 0; i < longitudDestino; i++) {
            const centro = i * ratio;
            const inicio = Math.max(0, Math.floor(centro - ratio / 2));
            const fin = Math.min(muestras.length, Math.ceil(centro + ratio / 2));
            let suma = 0, cuenta = 0;
            for (let k = inicio; k < fin; k++) { suma += muestras[k]; cuenta++; }
            destino[i] = cuenta > 0 ? suma / cuenta : 0;
        }

        const pcm = new Int16Array(destino.length);
        for (let i = 0; i < destino.length; i++) {
            const s = Math.max(-1, Math.min(1, destino[i]));
            pcm[i] = s < 0 ? s * 0x8000 : s * 0x7fff;
        }

        const bytesPorMuestra = 2;
        const byteRate = sampleRateDestino * bytesPorMuestra;
        const dataSize = pcm.length * bytesPorMuestra;
        const buffer = new ArrayBuffer(44 + dataSize);
        const view = new DataView(buffer);

        const escribirTexto = (offset, texto) => {
            for (let i = 0; i < texto.length; i++) view.setUint8(offset + i, texto.charCodeAt(i));
        };

        escribirTexto(0, 'RIFF');
        view.setUint32(4, 36 + dataSize, true);
        escribirTexto(8, 'WAVE');
        escribirTexto(12, 'fmt ');
        view.setUint32(16, 16, true);
        view.setUint16(20, 1, true); // PCM
        view.setUint16(22, 1, true); // mono
        view.setUint32(24, sampleRateDestino, true);
        view.setUint32(28, byteRate, true);
        view.setUint16(32, bytesPorMuestra, true);
        view.setUint16(34, 16, true);
        escribirTexto(36, 'data');
        view.setUint32(40, dataSize, true);

        let o = 44;
        for (let i = 0; i < pcm.length; i++, o += 2) {
            view.setInt16(o, pcm[i], true);
        }

        return new Blob([buffer], { type: 'audio/wav' });
    }
};
