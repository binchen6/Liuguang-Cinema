package com.github.catvod.spider;

import com.github.catvod.crawler.Spider;
import fi.iki.elonen.NanoHTTPD;

import java.io.ByteArrayInputStream;
import java.io.IOException;
import java.io.InputStream;
import java.lang.reflect.Field;
import java.lang.reflect.Method;
import java.nio.charset.StandardCharsets;
import java.security.SecureRandom;
import java.util.HexFormat;
import java.util.List;
import java.util.Map;
import java.util.concurrent.CopyOnWriteArrayList;

public class Proxy extends Spider {
    private static final String PATH = "/proxy/" + HexFormat.of().formatHex(new SecureRandom().generateSeed(24));
    private static final List<Class<?>> EXTERNAL_PROXIES = new CopyOnWriteArrayList<>();
    private static NanoHTTPD server;

    public static void registerExternalProxy(Class<?> proxyClass) {
        if (proxyClass == null || proxyClass == Proxy.class) return;
        if (!EXTERNAL_PROXIES.contains(proxyClass)) {
            EXTERNAL_PROXIES.add(0, proxyClass);
        }
        int port = getPort();
        try {
            Field portField = proxyClass.getDeclaredField("port");
            portField.setAccessible(true);
            portField.setInt(null, port);
        } catch (Throwable ignored) { }
    }

    private static Object[] proxy(Map<String, String> params) throws Exception {
        String action = params.getOrDefault("do", "");
        if ("ck".equals(action)) {
            return new Object[]{200, "text/plain; charset=utf-8", new ByteArrayInputStream("ok".getBytes(StandardCharsets.UTF_8))};
        }
        for (Class<?> external : EXTERNAL_PROXIES) {
            try {
                Method method = external.getDeclaredMethod("proxy", Map.class);
                method.setAccessible(true);
                Object result = method.invoke(null, params);
                if (result instanceof Object[] arr && arr.length >= 3) {
                    return arr;
                }
            } catch (Throwable ignored) { }
        }
        return switch (action) {
            case "ali" -> Ali.proxy(params);
            case "bili" -> Bili.proxy(params);
            default -> null;
        };
    }

    public static int getPort() {
        return startServer().getListeningPort();
    }

    public static String getUrl() {
        return "http://127.0.0.1:" + getPort() + PATH;
    }

    private static synchronized NanoHTTPD startServer() {
        if (server != null) return server;
        try {
            NanoHTTPD listener = new NanoHTTPD("127.0.0.1", 0) {
                @Override public Response serve(IHTTPSession session) {
                    String uri = session.getUri();
                    boolean allowedPath = PATH.equals(uri) || "/proxy".equals(uri);
                    if (!allowedPath ||
                        !("127.0.0.1:" + getListeningPort()).equals(session.getHeaders().get("host")) ||
                        session.getMethod() != Method.GET && session.getMethod() != Method.HEAD)
                        return newFixedLengthResponse(Response.Status.NOT_FOUND, "text/plain", "not found");
                    try {
                        if ("proxy".equals(session.getParms().get("do"))) return serveMedia(session);
                        Object[] result = proxy(session.getParms());
                        if (result == null) return newFixedLengthResponse(Response.Status.NOT_FOUND, "text/plain", "not found");
                        int status = result[0] instanceof Number ? ((Number) result[0]).intValue() : 200;
                        Response.Status code = status == 200 ? Response.Status.OK : Response.Status.lookup(status);
                        if (code == null) code = Response.Status.OK;
                        String mime = result[1] instanceof String s ? s : "application/octet-stream";
                        InputStream body = result[2] instanceof InputStream is
                            ? is
                            : new ByteArrayInputStream(String.valueOf(result[2]).getBytes(StandardCharsets.UTF_8));
                        Response response = newFixedLengthResponse(code, mime, body, -1);
                        if (result.length >= 4 && result[3] instanceof Map<?, ?> extraHeaders) {
                            for (Map.Entry<?, ?> entry : extraHeaders.entrySet()) {
                                if (entry.getKey() != null && entry.getValue() != null) {
                                    response.addHeader(String.valueOf(entry.getKey()), String.valueOf(entry.getValue()));
                                }
                            }
                        }
                        return response;
                    } catch (Exception error) {
                        return newFixedLengthResponse(Response.Status.INTERNAL_ERROR, "text/plain", "media unavailable");
                    }
                }

                private Response serveMedia(IHTTPSession session) throws Exception {
                    Map<String, String> params = session.getParms();
                    String url = new String(java.util.Base64.getDecoder().decode(params.get("url")), StandardCharsets.UTF_8);
                    String headerJson = new String(java.util.Base64.getDecoder().decode(params.get("header")), StandardCharsets.UTF_8);
                    Map<String, String> headers = new com.google.gson.Gson().fromJson(headerJson, new com.google.gson.reflect.TypeToken<Map<String, String>>() { }.getType());
                    headers.keySet().removeIf(name -> name.equalsIgnoreCase("Host") || name.equalsIgnoreCase("Range"));
                    String range = session.getHeaders().get("range");
                    if (range != null) headers.put("Range", range);
                    okhttp3.Response upstream = com.github.catvod.net.OkHttp.newCall(url, headers);
                    String mime = upstream.header("Content-Type", "application/octet-stream");
                    Response.Status status = Response.Status.lookup(upstream.code());
                    if (status == null || upstream.body() == null) {
                        upstream.close();
                        return newFixedLengthResponse(Response.Status.INTERNAL_ERROR, "text/plain", "media unavailable");
                    }
                    Response response = newFixedLengthResponse(status, mime, upstream.body().byteStream(), upstream.body().contentLength());
                    for (String name : new String[]{"Content-Range", "Accept-Ranges", "ETag", "Last-Modified", "Cache-Control", "Expires", "Content-Disposition"}) {
                        String value = upstream.header(name);
                        if (value != null) response.addHeader(name, value);
                    }
                    return response;
                }
            };
            listener.start(NanoHTTPD.SOCKET_READ_TIMEOUT, true);
            return server = listener;
        } catch (IOException error) {
            throw new IllegalStateException("Cannot start local media proxy", error);
        }
    }
}
