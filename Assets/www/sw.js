/**
 * Mchat Service Worker - 离线缓存与后台同步
 * 
 * 缓存策略：
 * - 静态资源：Cache First（CSS、JS、字体、图片）
 * - API 请求：Network First，降级到缓存
 * - 页面导航：Network First，离线时显示缓存页面
 * - 动态缓存：运行时缓存 API 响应
 */

const CACHE_NAME = 'mchat-v2';
const STATIC_CACHE = 'mchat-static-v2';
const DYNAMIC_CACHE = 'mchat-dynamic-v2';
const API_CACHE = 'mchat-api-v2';

// 静态资源白名单 - 预缓存核心资源
const STATIC_ASSETS = [
    '/',
    '/index.html',
    '/css/app.css',
    '/css/spacing-tokens.css',
    '/js/app.js',
    '/js/chat.js',
    '/js/browser.js',
    '/js/media.js',
    '/js/tools.js',
    '/js/bridge.js',
    '/js/floatpanel.js',
    '/js/icons.js',
    '/js/bt-chooser.js',
    '/js/remote-media.js',
    '/js/i18n.js',
    '/manifest.json'
];

// 需要缓存的 API 路径模式
const CACHEABLE_API_PATTERNS = [
    '/api/',
    '/api/media/',
    '/api/reader/'
];

// 不缓存的路径
const NO_CACHE_PATTERNS = [
    '/api/auth/',
    '/api/push/',
    '/ws',
    '/__proxy/',
    '/__btpolyfill__.js'
];

// 安装事件 - 预缓存静态资源
self.addEventListener('install', event => {
    event.waitUntil(
        caches.open(STATIC_CACHE)
            .then(cache => {
                console.log('[SW] 正在预缓存静态资源...');
                return cache.addAll(STATIC_ASSETS);
            })
            .then(() => {
                console.log('[SW] 静态资源预缓存完成');
                return self.skipWaiting();
            })
            .catch(err => {
                console.error('[SW] 预缓存失败:', err);
            })
    );
});

// 激活事件 - 清理旧缓存
self.addEventListener('activate', event => {
    event.waitUntil(
        caches.keys()
            .then(cacheNames => {
                return Promise.all(
                    cacheNames
                        .filter(name => name !== STATIC_CACHE && 
                                       name !== DYNAMIC_CACHE && 
                                       name !== API_CACHE)
                        .map(name => {
                            console.log('[SW] 删除旧缓存:', name);
                            return caches.delete(name);
                        })
                );
            })
            .then(() => {
                console.log('[SW] 旧缓存清理完成');
                return self.clients.claim();
            })
    );
});

// 拦截网络请求
self.addEventListener('fetch', event => {
    const { request } = event;
    const url = new URL(request.url);
    
    // 跳过非 GET 请求
    if (request.method !== 'GET') {
        return;
    }
    
    // 跳过不缓存的路径
    if (NO_CACHE_PATTERNS.some(pattern => url.pathname.startsWith(pattern))) {
        return;
    }
    
    // 判断请求类型
    const isStaticAsset = isStaticAssetRequest(request);
    const isApiRequest = isApiRequest(request);
    const isNavigation = request.mode === 'navigate';
    
    // 根据请求类型选择缓存策略
    if (isStaticAsset) {
        // 静态资源：Cache First
        event.respondWith(cacheFirst(request, STATIC_CACHE));
    } else if (isApiRequest) {
        // API 请求：Network First，带缓存降级
        event.respondWith(networkFirstWithCache(request, API_CACHE));
    } else if (isNavigation) {
        // 页面导航：Network First，离线时显示缓存页面
        event.respondWith(networkFirstWithCache(request, DYNAMIC_CACHE));
    } else {
        // 其他请求：Network First
        event.respondWith(networkFirst(request));
    }
});

// 判断是否为静态资源请求
function isStaticAssetRequest(request) {
    const url = new URL(request.url);
    const pathname = url.pathname;
    const staticExtensions = ['.css', '.js', '.png', '.jpg', '.jpeg', '.webp', '.svg', '.ico', '.woff', '.woff2', '.ttf', '.eot', '.json', '.map', '.wasm'];
    return staticExtensions.some(ext => pathname.endsWith(ext)) ||
           pathname.startsWith('/css/') ||
           pathname.startsWith('/js/') ||
           pathname.startsWith('/fonts/') ||
           pathname.startsWith('/images/') ||
           pathname.startsWith('/assets/') ||
           pathname === '/' || pathname === '/index.html' ||
           pathname === '/manifest.json';
}

// 判断是否为 API 请求
function isApiRequest(request) {
    const url = new URL(request.url);
    return CACHEABLE_API_PATTERNS.some(pattern => url.pathname.startsWith(pattern));
}

// Cache First 策略 - 适合静态资源
async function cacheFirst(request, cacheName) {
    const cache = await caches.open(cacheName);
    const cachedResponse = await cache.match(request);
    
    if (cachedResponse) {
        // 后台更新缓存
        fetch(request).then(response => {
            if (response.ok) {
                cache.put(request, response.clone());
            }
        }).catch(() => {});
        
        return cachedResponse;
    }
    
    try {
        const networkResponse = await fetch(request);
        if (networkResponse.ok) {
            cache.put(request, networkResponse.clone());
        }
        return networkResponse;
    } catch (error) {
        // 离线且无缓存时返回离线页面
        if (request.mode === 'navigate') {
            return caches.match('/offline.html') || new Response('离线', { status: 503 });
        }
        throw error;
    }
}

// Network First 策略 - 适合 API 请求
async function networkFirst(request, cacheName) {
    const cache = await caches.open(cacheName);
    
    try {
        const networkResponse = await fetch(request);
        
        if (networkResponse.ok) {
            // 缓存成功的响应
            cache.put(request, networkResponse.clone());
        }
        
        return networkResponse;
    } catch (error) {
        // 网络失败，尝试从缓存获取
        const cachedResponse = await cache.match(request);
        if (cachedResponse) {
            // 添加标记表示这是缓存响应
            const response = cachedResponse.clone();
            response.headers.set('X-From-Cache', 'true');
            return response;
        }
        
        // 离线且无缓存
        if (request.mode === 'navigate') {
            return caches.match('/offline.html') || new Response('离线', { status: 503 });
        }
        
        throw error;
    }
}

// Network First with Cache 策略 - 适合页面导航和 API
async function networkFirstWithCache(request, cacheName) {
    const cache = await caches.open(cacheName);
    
    try {
        const networkResponse = await fetch(request);
        
        if (networkResponse.ok) {
            // 缓存成功的响应
            cache.put(request, networkResponse.clone());
        }
        
        return networkResponse;
    } catch (error) {
        // 网络失败，尝试从缓存获取
        const cachedResponse = await cache.match(request);
        if (cachedResponse) {
            const response = cachedResponse.clone();
            response.headers.set('X-From-Cache', 'true');
            return response;
        }
        
        // 离线页面
        if (request.mode === 'navigate') {
            return caches.match('/offline.html') || new Response('离线', { status: 503 });
        }
        
        throw error;
    }
}

// 简单的 Network First
async function networkFirst(request) {
    try {
        return await fetch(request);
    } catch (error) {
        return new Response('网络错误', { status: 503 });
    }
}

// 推送通知处理
self.addEventListener('push', event => {
    if (!event.data) return;
    
    const data = event.data.json();
    const options = {
        body: data.body || '新消息',
        icon: '/icons/icon-192.png',
        badge: '/icons/badge-72.png',
        vibrate: [200, 100, 200],
        data: data.data || {},
        actions: [
            { action: 'open', title: '打开' },
            { action: 'close', title: '关闭' }
        ],
        requireInteraction: true
    };
    
    event.waitUntil(
        self.registration.showNotification(data.title || 'Mchat', options)
    );
});

// 通知点击处理
self.addEventListener('notificationclick', event => {
    event.notification.close();
    
    if (event.action === 'close') return;
    
    event.waitUntil(
        clients.matchAll({ type: 'window', includeUncontrolled: true })
            .then(clientList => {
                // 如果已有窗口打开，聚焦它
                for (const client of clientList) {
                    if (client.url.includes('/') && 'focus' in client) {
                        return client.focus();
                    }
                }
                // 否则打开新窗口
                return clients.openWindow('/');
            })
    );
});

// 后台同步
self.addEventListener('sync', event => {
    if (event.tag === 'sync-messages') {
        event.waitUntil(syncMessages());
    }
});

// 后台同步消息
async function syncMessages() {
    // 实现消息同步逻辑
    console.log('[SW] 后台同步消息');
    // 这里可以实现消息队列的重试发送
}

// 周期性后台同步
self.addEventListener('periodicsync', event => {
    if (event.tag === 'periodic-sync-cache') {
        event.waitUntil(cleanupOldCache());
    }
});

// 清理过期缓存
async function cleanupOldCache() {
    const cacheNames = await caches.keys();
    const now = Date.now();
    
    for (const cacheName of cacheNames) {
        if (cacheName === STATIC_CACHE) continue;
        
        const cache = await caches.open(cacheName);
        const keys = await cache.keys();
        
        for (const request of keys) {
            const response = await cache.match(request);
            const cachedAt = response.headers.get('sw-cached-at');
            
            if (cachedAt && (Date.now() - parseInt(cachedAt)) > 7 * 24 * 60 * 60 * 1000) {
                // 超过 7 天未访问，删除
                await cache.delete(request);
            }
        }
    }
}

// 消息通道 - 与主线程通信
self.addEventListener('message', event => {
    if (event.data && event.data.type === 'SKIP_WAITING') {
        self.skipWaiting();
    }
    
    if (event.data && event.data.type === 'CLEAR_CACHE') {
        caches.keys().then(names => {
            names.forEach(name => caches.delete(name));
        });
    }
});

console.log('[SW] Service Worker 已加载');