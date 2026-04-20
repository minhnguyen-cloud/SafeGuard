const CACHE_NAME = "safeguard-pwa-v2";
const OFFLINE_URL = "/offline.html";
const STATIC_ASSETS = [
    "/",
    "/offline.html",
    "/manifest.webmanifest",
    "/Content/Site.css",
    "/Content/images/Thuonghieu.png",
    "/Content/images/Thuonghieu1.png"
];

self.addEventListener("install", function (event) {
    event.waitUntil(
        caches.open(CACHE_NAME).then(function (cache) {
            return cache.addAll(STATIC_ASSETS);
        })
    );
    self.skipWaiting();
});

self.addEventListener("activate", function (event) {
    event.waitUntil(
        caches.keys().then(function (keys) {
            return Promise.all(
                keys
                    .filter(function (key) { return key !== CACHE_NAME; })
                    .map(function (key) { return caches.delete(key); })
            );
        }).then(function () {
            return self.clients.claim();
        })
    );
});

self.addEventListener("fetch", function (event) {
    if (event.request.method !== "GET") {
        return;
    }

    const requestUrl = new URL(event.request.url);
    const isSameOrigin = requestUrl.origin === self.location.origin;
    const isNavigationRequest = event.request.mode === "navigate";
    const isStaticAsset = isSameOrigin && (
        requestUrl.pathname.startsWith("/Content/") ||
        requestUrl.pathname === "/manifest.webmanifest"
    );

    if (isNavigationRequest) {
        event.respondWith(
            fetch(event.request)
                .then(function (response) {
                    const copy = response.clone();
                    caches.open(CACHE_NAME).then(function (cache) {
                        cache.put(event.request, copy);
                    });
                    return response;
                })
                .catch(function () {
                    return caches.match(event.request).then(function (cachedPage) {
                        return cachedPage || caches.match(OFFLINE_URL);
                    });
                })
        );
        return;
    }

    if (isStaticAsset) {
        event.respondWith(
            caches.match(event.request).then(function (cachedResponse) {
                if (cachedResponse) {
                    return cachedResponse;
                }

                return fetch(event.request).then(function (response) {
                    const copy = response.clone();
                    caches.open(CACHE_NAME).then(function (cache) {
                        cache.put(event.request, copy);
                    });
                    return response;
                });
            })
        );
    }
});
