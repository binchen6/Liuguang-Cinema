package com.github.catvod.spider;

import com.github.catvod.crawler.Spider;
import fi.iki.elonen.NanoHTTPD;

import java.io.ByteArrayInputStream;
import java.io.IOException;
import java.nio.charset.StandardCharsets;
import java.security.SecureRandom;
import java.util.HexFormat;
import java.util.Map;

public class Proxy extends Spider {
    private static final String PATH = "/proxy/" + HexFormat.of().formatHex(new SecureRandom().generateSeed(24));
    private static NanoHTTPD server;

    private static Object[] proxy(Map<String, String> params) throws Exception {
        return switch (params.getOrDefault("do", "")) {
            case "ck" -> new Object[]{200, "text/plain; charset=utf-8", new ByteArrayInputStream("ok".getBytes("UTF-8"))};
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
                    if (!PATH.equals(session.getUri()) ||
                        !("127.0.0.1:" + getListeningPort()).equals(session.getHeaders().get("host")) ||
                        session.getMethod() != Method.GET && session.getMethod() != Method.HEAD)
                        return newFixedLengthResponse(Response.Status.NOT_FOUND, "text/plain", "not found");
                    try {
                        if ("proxy".equals(session.getParms().get("do"))) return serveMedia(session);
                        Object[] result = proxy(session.getParms());
                        if (result == null) return newFixedLengthResponse(Response.Status.NOT_FOUND, "text/plain", "not found");
                        int status = result[0] instanceof Number ? ((Number) result[0]).intValue() : 200;
                        Response.Status code = status == 200 ? Response.Status.OK : Response.Status.lookup(status);
                        return newFixedLengthResponse(code, (String) result[1], (java.io.InputStream) result[2], -1);
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
