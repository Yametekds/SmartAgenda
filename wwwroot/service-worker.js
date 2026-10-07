// Service worker mínimo: solo existe para que Chrome considere la app "instalable"
// (criterio técnico de las PWA). Esta app necesita conectarse siempre a tu servidor,
// así que no cachea nada ni funciona offline a propósito — cada petición va directa a la red.
self.addEventListener('install', () => {
    self.skipWaiting();
});

self.addEventListener('activate', (event) => {
    event.waitUntil(self.clients.claim());
});

self.addEventListener('fetch', (event) => {
    event.respondWith(fetch(event.request));
});
