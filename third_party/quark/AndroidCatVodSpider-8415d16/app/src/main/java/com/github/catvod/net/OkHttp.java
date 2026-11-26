package com.github.catvod.net;

import com.github.catvod.crawler.Spider;

import java.io.IOException;
import java.util.List;
import java.util.Map;
import java.util.concurrent.TimeUnit;

import okhttp3.Dns;
import okhttp3.Headers;
import okhttp3.OkHttpClient;
import okhttp3.Request;
import okhttp3.Response;

public class OkHttp {

    public static final String POST = "POST";
    public static final String GET = "GET";

    private static final OkHttpClient CLIENT = new OkHttpClient.Builder()
        .dns(Dns.SYSTEM)
        .connectTimeout(8, TimeUnit.SECONDS)
        .readTimeout(15, TimeUnit.SECONDS)
        .writeTimeout(15, TimeUnit.SECONDS)
        .connectionPool(new okhttp3.ConnectionPool(32, 5, TimeUnit.MINUTES))
        .addNetworkInterceptor(chain -> {
            Request request = chain.request();
            Request initial = chain.call().request();
            if (initial.url().isHttps() && !request.url().isHttps())
                throw new IOException("HTTPS redirect to an insecure destination");
            if (!sameOrigin(initial, request)) {
                if (request.body() != null) throw new IOException("Cross-origin request body");
                Request.Builder scoped = request.newBuilder();
                for (String name : request.headers().names()) {
                    switch (name.toLowerCase(java.util.Locale.ROOT)) {
                        case "host", "connection", "accept-encoding", "user-agent", "accept", "range" -> { }
                        default -> scoped.removeHeader(name);
                    }
                }
                request = scoped.build();
            }
            return chain.proceed(request);
        })
        .build();
    private static final OkHttpClient NO_REDIRECTS = CLIENT.newBuilder().followRedirects(false).build();

    private static boolean sameOrigin(Request left, Request right) {
        return left.url().scheme().equals(right.url().scheme()) &&
            left.url().host().equals(right.url().host()) && left.url().port() == right.url().port();
    }

    public static Response newCall(Request request) throws IOException {
        return client().newCall(request).execute();
    }

    public static Response newCall(String url) throws IOException {
        return client().newCall(new Request.Builder().url(url).build()).execute();
    }

    public static Response newCall(String url, Map<String, String> header) throws IOException {
        return client().newCall(new Request.Builder().url(url).headers(Headers.of(header)).build()).execute();
    }

    public static String string(String url) {
        return string(url, null);
    }

    public static String string(String url, Map<String, String> header) {
        return string(url, null, header);
    }

    public static String string(String url, Map<String, String> params, Map<String, String> header) {
        return url.startsWith("http") ? new OkRequest(GET, url, params, header).execute(client()).getBody() : "";
    }

    public static OkResult get(String url, Map<String, String> params, Map<String, String> header) {
        return new OkRequest(GET, url, params, header).execute(client());
    }

    public static String post(String url, Map<String, String> params) {
        return post(url, params, null).getBody();
    }

    public static OkResult post(String url, Map<String, String> params, Map<String, String> header) {
        return new OkRequest(POST, url, params, header).execute(client());
    }

    public static String post(String url, String json) {
        return post(url, json, null).getBody();
    }

    public static OkResult post(String url, String json, Map<String, String> header) {
        return new OkRequest(POST, url, json, header).execute(client());
    }

    public static String getLocation(String url, Map<String, String> header) throws IOException {
        try (Response response = NO_REDIRECTS.newCall(new Request.Builder().url(url).headers(Headers.of(header)).build()).execute()) {
            return getLocation(response.headers().toMultimap());
        }
    }

    public static String getLocation(Map<String, List<String>> headers) {
        if (headers == null) return null;
        if (headers.containsKey("location")) return headers.get("location").get(0);
        if (headers.containsKey("Location")) return headers.get("Location").get(0);
        return null;
    }

    private static OkHttpClient client() {
        return CLIENT;
    }
}
