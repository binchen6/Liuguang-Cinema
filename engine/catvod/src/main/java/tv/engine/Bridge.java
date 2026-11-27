package tv.engine;

import com.github.catvod.crawler.Spider;
import com.github.catvod.spider.Init;
import com.google.gson.JsonElement;
import com.google.gson.JsonObject;
import com.google.gson.JsonParser;

import java.io.BufferedReader;
import java.io.FileDescriptor;
import java.io.FileOutputStream;
import java.io.InputStreamReader;
import java.io.PrintStream;
import java.io.PrintWriter;
import java.net.URLDecoder;
import java.nio.charset.StandardCharsets;
import java.util.ArrayList;
import java.util.HashMap;
import java.util.List;
import java.util.Locale;
import java.util.Map;
import java.util.Objects;
import java.util.concurrent.ConcurrentHashMap;
import java.util.concurrent.ExecutorService;
import java.util.concurrent.Executors;
import java.util.concurrent.TimeUnit;

public final class Bridge {
    private record SpiderKey(String site, String api, String jar) { }
    private static final Map<SpiderKey, Object> SPIDERS = new ConcurrentHashMap<>();
    private static final Map<Object, String> EXTENSIONS = new ConcurrentHashMap<>();

    public static void main(String[] args) throws Exception {
        PrintWriter ipc = new PrintWriter(new FileOutputStream(FileDescriptor.out), true, StandardCharsets.UTF_8);
        System.setOut(new PrintStream(new FileOutputStream(FileDescriptor.err), true, StandardCharsets.UTF_8));
        DesktopApplication app = new DesktopApplication();
        Init.init(app);
        ExternalJarLoader jarLoader = new ExternalJarLoader(app);
        ExecutorService workers = Executors.newFixedThreadPool(8, runnable -> {
            Thread thread = new Thread(runnable, "catvod-request");
            thread.setDaemon(true);
            return thread;
        });
        BufferedReader input = new BufferedReader(new InputStreamReader(new java.io.FileInputStream(FileDescriptor.in), StandardCharsets.UTF_8));
        String line;
        while ((line = input.readLine()) != null) {
            try {
                JsonObject request = JsonParser.parseString(line).getAsJsonObject();
                workers.submit(() -> handle(app, jarLoader, request, ipc));
            } catch (Throwable error) {
                writeError(ipc, "", error);
            }
        }
        workers.shutdown();
        workers.awaitTermination(1, TimeUnit.MINUTES);
    }

    private static void handle(DesktopApplication app, ExternalJarLoader jarLoader, JsonObject request, PrintWriter ipc) {
        String requestId = text(request, "requestId");
        try {
            String api = request.get("api").getAsString();
            String ext = request.has("ext") && !request.get("ext").isJsonNull() ? request.get("ext").getAsString() : "";
            String jar = text(request, "jar");
            String site = text(request, "key");
            SpiderKey spiderKey = new SpiderKey(site, api, jar);
            Object spider = SPIDERS.computeIfAbsent(spiderKey, key -> create(jarLoader, api, ext, jar));
            String raw;
            try {
                raw = invokeSpider(app, spider, ext, request);
            } catch (Throwable ex) {
                if (spider instanceof ExternalJarLoader.ReflectiveSpider && isNativeLibraryFailure(ex)) {
                    Spider builtinFallback = tryCreateBuiltin(api, ext);
                    if (builtinFallback != null) {
                        SPIDERS.put(spiderKey, builtinFallback);
                        raw = invokeSpider(app, builtinFallback, ext, request);
                    } else {
                        throw new IllegalStateException("该站点依赖 Android ARM 原生库（.so），桌面端无法直接执行；请检查仓库资源包或切换其他站点。", ex);
                    }
                } else {
                    throw ex;
                }
            }
            JsonObject response = new JsonObject();
            response.addProperty("requestId", requestId);
            response.addProperty("success", true);
            try { response.add("data", raw == null ? null : JsonParser.parseString(raw)); }
            catch (Exception ignored) { response.addProperty("data", raw); }
            synchronized (ipc) {
                ipc.println(response);
                ipc.flush();
            }
        } catch (Throwable error) {
            writeError(ipc, requestId, error);
            error.printStackTrace();
        }
    }

    private static String invokeSpider(DesktopApplication app, Object spider, String ext, JsonObject request) throws Exception {
        synchronized (spider) {
            if (!Objects.equals(EXTENSIONS.get(spider), ext)) {
                if (spider instanceof ExternalJarLoader.ReflectiveSpider external) {
                    external.init(app, ext);
                } else if (spider instanceof Spider builtin) {
                    builtin.init(app, ext);
                }
                EXTENSIONS.put(spider, ext);
            }
            String action = request.get("action").getAsString();
            if (spider instanceof ExternalJarLoader.ReflectiveSpider external) {
                return dispatchExternal(external, action, request);
            }
            return dispatchBuiltin((Spider) spider, action, request);
        }
    }

    private static boolean isNativeLibraryFailure(Throwable error) {
        Throwable cur = error;
        for (int depth = 0; cur != null && depth < 10; depth++) {
            if (cur instanceof UnsatisfiedLinkError || cur instanceof ExceptionInInitializerError) return true;
            String msg = cur.getMessage();
            if (msg != null && (msg.contains(".so") || msg.contains("UnsatisfiedLinkError") || msg.contains("FishNative"))) return true;
            cur = cur.getCause();
        }
        return false;
    }

    private static String dispatchBuiltin(Spider spider, String action, JsonObject request) throws Exception {
        return switch (action) {
            case "home" -> homeBuiltin(spider);
            case "category" -> spider.categoryContent(text(request, "tid"), text(request, "pg"), true, extend(request));
            case "detail" -> spider.detailContent(List.of(text(request, "id")));
            case "search" -> searchBuiltin(spider,
                    request.has("keyword") ? text(request, "keyword") : text(request, "key"),
                    text(request, "pg"));
            case "play" -> spider.playerContent(text(request, "flag"), text(request, "id"), new ArrayList<>());
            case "live" -> invokeLiveBuiltin(spider, text(request, "url"));
            default -> throw new IllegalArgumentException("Unknown action: " + action);
        };
    }

    private static String invokeLiveBuiltin(Spider spider, String url) throws Exception {
        try {
            var m = spider.getClass().getMethod("liveContent", String.class);
            Object res = m.invoke(spider, url);
            return res == null ? "" : String.valueOf(res);
        } catch (NoSuchMethodException ignored) {
            return "";
        }
    }

    private static String dispatchExternal(ExternalJarLoader.ReflectiveSpider spider, String action, JsonObject request) throws Exception {
        return switch (action) {
            case "home" -> homeExternal(spider);
            case "category" -> spider.categoryContent(text(request, "tid"), text(request, "pg"), true, extend(request));
            case "detail" -> spider.detailContent(List.of(text(request, "id")));
            case "search" -> spider.searchContent(
                    request.has("keyword") ? text(request, "keyword") : text(request, "key"),
                    false,
                    text(request, "pg"));
            case "play" -> spider.playerContent(text(request, "flag"), text(request, "id"), new ArrayList<>());
            case "live" -> spider.liveContent(text(request, "url"));
            default -> throw new IllegalArgumentException("Unknown action: " + action);
        };
    }

    private static void writeError(PrintWriter ipc, String id, Throwable error) {
        JsonObject response = new JsonObject();
        response.addProperty("requestId", id);
        response.addProperty("success", false);
        response.addProperty("error", error.toString());
        synchronized (ipc) {
            ipc.println(response);
            ipc.flush();
        }
    }

    private static String homeBuiltin(Spider spider) throws Exception {
        String raw = spider.homeContent(true);
        if (raw == null || raw.isBlank()) return spider.homeVideoContent();
        if (spider.getClass().getMethod("homeVideoContent").getDeclaringClass() == Spider.class) return raw;
        return mergeHomeVideos(raw, spider::homeVideoContent);
    }

    private static String homeExternal(ExternalJarLoader.ReflectiveSpider spider) throws Exception {
        String raw = spider.homeContent(true);
        if (raw == null || raw.isBlank()) return spider.homeVideoContent();
        return mergeHomeVideos(raw, spider::homeVideoContent);
    }

    private static String mergeHomeVideos(String raw, ThrowingStringSupplier supplementalLoader) {
        try {
            JsonElement primary = JsonParser.parseString(raw);
            if (!primary.isJsonObject() || hasVideos(primary.getAsJsonObject())) return raw;
            String extra = supplementalLoader.get();
            if (extra == null || extra.isBlank()) return raw;
            JsonElement supplemental = JsonParser.parseString(extra);
            if (supplemental.isJsonObject() && hasVideos(supplemental.getAsJsonObject())) {
                primary.getAsJsonObject().add("list", supplemental.getAsJsonObject().get("list"));
                return primary.toString();
            }
        } catch (Exception ignored) {
            // A supplemental response must not discard the primary site's categories.
        }
        return raw;
    }

    private static boolean hasVideos(JsonObject response) {
        JsonElement list = response.get("list");
        return list != null && list.isJsonArray() && list.getAsJsonArray().size() > 0;
    }

    private static String searchBuiltin(Spider spider, String keyword, String page) throws Exception {
        if (spider.getClass().getMethod("searchContent", String.class, boolean.class, String.class)
                .getDeclaringClass() != Spider.class) {
            return spider.searchContent(keyword, false, page);
        }
        if (page.isEmpty() || page.equals("1")) return spider.searchContent(keyword, false);
        return "{\"list\":[]}";
    }

    private static Spider tryCreateBuiltin(String api, String ext) {
        try {
            if (api.startsWith("http")) {
                String script = ext;
                try { script = URLDecoder.decode(ext, StandardCharsets.UTF_8); } catch (Exception ignored) { }
                if (script.contains("虎牙.js")) return new com.github.catvod.spider.Huya();
                if (script.contains("斗鱼直播.js")) return new com.github.catvod.spider.Douyu();
                if (script.contains("兔小贝.js")) return new com.github.catvod.spider.Tuxiaobei();
                return null;
            }
            String name = api.startsWith("csp_") ? api.substring(4) : api;
            if (name.endsWith("Guard")) name = name.substring(0, name.length() - 5);
            name = switch (name.toLowerCase(Locale.ROOT)) {
                case "wogg" -> "Wogg";
                case "sixv" -> "Xb6v";
                case "jpj", "jianpian" -> "Jianpian";
                case "newcz", "cz" -> "ChangZhang";
                case "doudou", "douban", "piandan" -> "Douban";
                case "alllive", "livegz" -> "Live88";
                case "music" -> "Kugou";
                case "sssp" -> "ShortVideo";
                case "yueting" -> "AudioBook";
                case "bpanso" -> "PanTa";
                case "aidj" -> "ShortDrama";
                case "tuxiaobei" -> "Tuxiaobei";
                case "wwys", "nmyswv" -> "Nmyswv";
                default -> name;
            };
            Class<?> type = Class.forName("com.github.catvod.spider." + name);
            return (Spider) type.getDeclaredConstructor().newInstance();
        } catch (Throwable ignored) {
            return null;
        }
    }

    private static Object create(ExternalJarLoader jarLoader, String api, String ext, String jar) {
        // Prefer desktop-verified built-in Java spiders first to avoid downloading/DEX-translating
        // multi-MB Android ARM (.so) JARs when a fast native desktop implementation already exists.
        Spider builtin = tryCreateBuiltin(api, ext);
        if (builtin != null) return builtin;

        Throwable externalError = null;
        if (jar != null && !jar.isBlank() && api.startsWith("csp_")) {
            try {
                return jarLoader.createSpider(jar, api);
            } catch (Throwable ex) {
                externalError = ex;
                System.err.println("[Bridge] External jar load failed for " + api + " (" + jar + "): " + ex);
            }
        }

        if (externalError != null) {
            if (isNativeLibraryFailure(externalError)) {
                throw new IllegalStateException("该站点依赖 Android ARM 原生库（.so），桌面端无法直接执行；请检查仓库资源包或切换其他站点。", externalError);
            }
            throw new IllegalStateException("External jar and built-in fallback both failed for " + api + ": " + externalError.getMessage(), externalError);
        }
        throw new IllegalStateException("No compatible public spider implementation for " + api);
    }

    private static String text(JsonObject object, String name) {
        return object.has(name) && !object.get(name).isJsonNull() ? object.get(name).getAsString() : "";
    }

    private static HashMap<String, String> extend(JsonObject object) {
        HashMap<String, String> result = new HashMap<>();
        if (object.has("extend") && object.get("extend").isJsonObject()) {
            for (Map.Entry<String, JsonElement> entry : object.getAsJsonObject("extend").entrySet()) {
                result.put(entry.getKey(), entry.getValue().getAsString());
            }
        }
        return result;
    }

    @FunctionalInterface
    private interface ThrowingStringSupplier {
        String get() throws Exception;
    }
}
