package com.github.catvod.spider;

import com.github.catvod.crawler.Spider;
import fi.iki.elonen.NanoHTTPD;

import java.io.ByteArrayInputStream;
import java.io.IOException;
import java.util.Map;

public class Proxy extends Spider {
    private static final int PORT = 9978;
    private static NanoHTTPD server;

    public static Object[] proxy(Map<String, String> params) throws Exception {
        return switch (params.getOrDefault("do", "")) {
            case "ck" -> new Object[]{200, "text/plain; charset=utf-8", new ByteArrayInputStream("ok".getBytes("UTF-8"))};
            case "ali" -> Ali.proxy(params);
            case "bili" -> Bili.proxy(params);
            case "proxy" -> {
                String url = new String(java.util.Base64.getDecoder().decode(params.get("url")), java.nio.charset.StandardCharsets.UTF_8);
                String headerJson = new String(java.util.Base64.getDecoder().decode(params.get("header")), java.nio.charset.StandardCharsets.UTF_8);
                Map<String, String> headers = new com.google.gson.Gson().fromJson(headerJson, new com.google.gson.reflect.TypeToken<Map<String, String>>() { }.getType());
                yield com.github.catvod.utils.ProxyVideo.proxy(url, headers);
            }
            default -> null;
        };
    }

    public static int getPort() {
        startServer();
        return PORT;
    }

    public static String getUrl() {
        startServer();
        return "http://127.0.0.1:" + PORT + "/proxy";
    }

    private static synchronized void startServer() {
        if (server != null) return;
        try {
            server = new NanoHTTPD("127.0.0.1", PORT) {
                @Override public Response serve(IHTTPSession session) {
                    try {
                        if ("proxy".equals(session.getParms().get("do"))) return serveMedia(session);
                        Object[] result = proxy(session.getParms());
                        if (result == null) return newFixedLengthResponse(Response.Status.NOT_FOUND, "text/plain", "not found");
                        int status = result[0] instanceof Number ? ((Number) result[0]).intValue() : 200;
                        Response.Status code = status == 200 ? Response.Status.OK : Response.Status.lookup(status);
                        return newFixedLengthResponse(code, (String) result[1], (java.io.InputStream) result[2], -1);
                    } catch (Exception error) {
                        return newFixedLengthResponse(Response.Status.INTERNAL_ERROR, "text/plain", error.toString());
                    }
                }

                private Response serveMedia(IHTTPSession session) throws Exception {
                    Map<String, String> params = session.getParms();
                    String url = new String(java.util.Base64.getDecoder().decode(params.get("url")), java.nio.charset.StandardCharsets.UTF_8);
                    String headerJson = new String(java.util.Base64.getDecoder().decode(params.get("header")), java.nio.charset.StandardCharsets.UTF_8);
                    Map<String, String> headers = new com.google.gson.Gson().fromJson(headerJson, new com.google.gson.reflect.TypeToken<Map<String, String>>() { }.getType());
                    String range = session.getHeaders().get("range");
                    if (range != null) headers.put("Range", range);
                    okhttp3.Response upstream = com.github.catvod.net.OkHttp.newCall(url, headers);
                    String mime = upstream.header("Content-Type", "application/octet-stream");
                    Response response = newFixedLengthResponse(Response.Status.lookup(upstream.code()), mime, upstream.body().byteStream(), upstream.body().contentLength());
                    for (String name : new String[]{"Content-Range", "Accept-Ranges", "ETag", "Last-Modified", "Cache-Control", "Expires", "Content-Disposition"}) {
                        String value = upstream.header(name);
                        if (value != null) response.addHeader(name, value);
                    }
                    return response;
                }
            };
            server.start(NanoHTTPD.SOCKET_READ_TIMEOUT, false);
        } catch (IOException error) {
            throw new IllegalStateException("Cannot start local media proxy", error);
        }
    }
}
