// The dev:live proxy (phase2-design.md section 5). Node's resolver does not map *.localhost to
// loopback the way browsers do, so the proxy targets 127.0.0.1 and sends the editor's host name in
// the Host header. The browser still sends `Origin: http://localhost:5173` on POST, PUT and DELETE,
// which the sign-in gate refuses (403 forbidden-origin), so the proxy rewrites Origin to the
// editor's own origin when the request has one. The gate is unchanged: the rewrite happens only in
// the developer's own dev server, whose requests reach the container with a local Host.

/**
 * @param {{ target?: string, host?: string }} [options]
 * @returns {Record<string, import("vite").ProxyOptions>}
 */
export function liveProxy(options = {}) {
  const target = options.target ?? process.env.MAQUETTISTE_LIVE_TARGET ?? "http://127.0.0.1:8080";
  const host = options.host ?? process.env.MAQUETTISTE_LIVE_HOST ?? "maquettiste.localhost:8080";
  const origin = `http://${host}`;

  /** @type {import("vite").ProxyOptions} */
  const common = {
    target,
    changeOrigin: false,
    ws: true,
    headers: { host },
    configure(proxy) {
      proxy.on("proxyReq", (proxyReq, req) => {
        proxyReq.setHeader("host", host);
        if (req.headers.origin) proxyReq.setHeader("origin", origin);
      });
      proxy.on("proxyReqWs", (proxyReq, req) => {
        proxyReq.setHeader("host", host);
        if (req.headers.origin) proxyReq.setHeader("origin", origin);
      });
    },
  };
  return { "/api": { ...common }, "/_host": { ...common } };
}
