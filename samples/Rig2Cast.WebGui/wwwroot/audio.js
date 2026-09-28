(() => {
    let socket, context, mediaStream, source, processor, nextPlayTime = 0;
    const byId = id => document.getElementById(id);

    function badge(text, online = false) {
        const element = byId('audio-badge');
        element.textContent = text;
        element.className = `badge ${online ? 'online' : 'offline'}`;
    }

    async function start() {
        byId('audio-connect').disabled = true;
        try {
            context = new AudioContext({ sampleRate: 48000 });
            await context.resume();
            if (byId('audio-mic').checked) {
                mediaStream = await navigator.mediaDevices.getUserMedia({ audio: { channelCount: 1, echoCancellation: false, noiseSuppression: false, autoGainControl: false } });
                source = context.createMediaStreamSource(mediaStream);
                processor = context.createScriptProcessor(2048, 1, 1);
                const silence = context.createGain();
                silence.gain.value = 0;
                processor.onaudioprocess = event => sendPcm(event.inputBuffer.getChannelData(0), event.inputBuffer.sampleRate);
                source.connect(processor);
                processor.connect(silence).connect(context.destination);
            }
            const protocol = location.protocol === 'https:' ? 'wss' : 'ws';
            socket = new WebSocket(`${protocol}://${location.host}/api/v1/audio/stream`);
            socket.binaryType = 'arraybuffer';
            socket.onopen = () => socket.send(JSON.stringify({
                host: byId('audio-host').value.trim(),
                txPort: Number(byId('audio-tx-port').value),
                rxPort: Number(byId('audio-rx-port').value),
                bitrate: Number(byId('audio-bitrate').value)
            }));
            socket.onmessage = event => typeof event.data === 'string' ? handleStatus(event.data) : playPcm(event.data);
            socket.onerror = () => badge('ERROR');
            socket.onclose = stop;
            byId('audio-disconnect').disabled = false;
            badge('CONNECTING');
        } catch (error) {
            stop();
            badge('ERROR');
            globalThis.log?.(`Audio: ${error.message}`, 'ERROR');
        }
    }

    function handleStatus(value) {
        const message = JSON.parse(value);
        if (message.state === 'connected') {
            badge('STREAMING', true);
            globalThis.log?.('Audio stream connected.');
        } else if (message.state === 'error') {
            badge('ERROR');
            globalThis.log?.(`Audio: ${message.message}`, 'ERROR');
        }
    }

    function sendPcm(samples, inputRate) {
        if (!socket || socket.readyState !== WebSocket.OPEN) return;
        const ratio = inputRate / 48000;
        const count = Math.floor(samples.length / ratio);
        const bytes = new ArrayBuffer(count * 2);
        const view = new DataView(bytes);
        for (let i = 0; i < count; i++) {
            const value = Math.max(-1, Math.min(1, samples[Math.floor(i * ratio)]));
            view.setInt16(i * 2, value < 0 ? value * 32768 : value * 32767, true);
        }
        socket.send(bytes);
    }

    function playPcm(bytes) {
        if (!context) return;
        const data = new DataView(bytes);
        const buffer = context.createBuffer(1, data.byteLength / 2, 48000);
        const channel = buffer.getChannelData(0);
        for (let i = 0; i < channel.length; i++) channel[i] = data.getInt16(i * 2, true) / 32768;
        const player = context.createBufferSource();
        player.buffer = buffer;
        player.connect(context.destination);
        const now = context.currentTime;
        if (nextPlayTime < now || nextPlayTime > now + 0.3) nextPlayTime = now + 0.06;
        player.start(nextPlayTime);
        nextPlayTime += buffer.duration;
    }

    function stop() {
        if (socket) { socket.onclose = null; socket.close(); socket = null; }
        if (processor) { processor.disconnect(); processor = null; }
        if (source) { source.disconnect(); source = null; }
        if (mediaStream) { mediaStream.getTracks().forEach(track => track.stop()); mediaStream = null; }
        if (context) { context.close(); context = null; }
        nextPlayTime = 0;
        byId('audio-connect').disabled = false;
        byId('audio-disconnect').disabled = true;
        badge('OFFLINE');
    }

    addEventListener('DOMContentLoaded', () => {
        byId('audio-connect').onclick = start;
        byId('audio-disconnect').onclick = stop;
        addEventListener('beforeunload', stop);
    });
})();
