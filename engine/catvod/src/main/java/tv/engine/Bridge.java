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
    private record SpiderKey(String site, String api) { }
    private static final Map<SpiderKey, Spider> SPIDERS = new ConcurrentHashMap<>();
    private static final Map<Spider, String> EXTENSIONS = new ConcurrentHashMap<>();

    public static void main(String[] args) throws Exception {
        PrintWriter ipc = new PrintWriter(new FileOutputStream(FileDescriptor.out), true, StandardCharsets.UTF_8);
        System.setOut(new PrintStream(new FileOutputStream(FileDescriptor.err), true, StandardCharsets.UTF_8));
        DesktopApplication app = new DesktopApplication();
        Init.init(app);
        ExecutorService workers = Executors.newFixedThreadPool(6, runnable -> {
            Thread thread = new Thread(runnable, "catvod-request");
            thread.setDaemon(true);
            return thread;
        });
        BufferedReader input = new BufferedReader(new InputStreamReader(new java.io.FileInputStream(FileDescriptor.in), StandardCharsets.UTF_8));
        String line;
        while ((line = input.readLine()) != null) {
            try {
                JsonObject request = JsonParser.parseString(line).getAsJsonObject();
                workers.submit(() -> handle(app, request, ipc));
            } catch (Throwable error) {
                writeError(ipc, "", error);
            }
        }
        workers.shutdown();
        workers.awaitTermination(1, TimeUnit.MINUTES);
    }

    private static void handle(DesktopApplication app, JsonObject request, PrintWriter ipc) {
        String requestId = text(request, "requestId");
        try {
            String api = request.get("api").getAsString();
            String ext = request.has("ext") ? request.get("ext").getAsString() : "";
            String site = text(request, "key");
            Spider spider = SPIDERS.computeIfAbsent(new SpiderKey(site, api), key -> create(api, ext));
            String raw;
            synchronized (spider) {
                if (!Objects.equals(EXTENSIONS.get(spider), ext)) {
                    spider.init(app, ext);
                    EXTENSIONS.put(spider, ext);
                }
                String action = request.get("action").getAsString();
                raw = switch (action) {
                    case "home" -> home(spider);
                    case "category" -> spider.categoryContent(text(request, "tid"), text(request, "pg"), true, extend(request));
                    case "detail" -> spider.detailContent(List.of(text(request, "id")));
                    case "search" -> search(spider,
                            request.has("keyword") ? text(request, "keyword") : text(request, "key"),
                            text(request, "pg"));
                    case "play" -> spider.playerContent(text(request, "flag"), text(request, "id"), new ArrayList<>());
                    default -> throw new IllegalArgumentException("Unknown action: " + action);
                };
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

    private static String home(Spider spider) throws Exception {
        String raw = spider.homeContent(true);
        if (raw == null || raw.isBlank()) return spider.homeVideoContent();
        if (spider.getClass().getMethod("homeVideoContent").getDeclaringClass() == Spider.class) return raw;

        try {
            JsonElement primary = JsonParser.parseString(raw);
            if (!primary.isJsonObject() || hasVideos(primary.getAsJsonObject())) return raw;
            String extra = spider.homeVideoContent();
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

    private static String search(Spider spider, String keyword, String page) throws Exception {
        if (spider.getClass().getMethod("searchContent", String.class, boolean.class, String.class)
                .getDeclaringClass() != Spider.class) {
            return spider.searchContent(keyword, false, page);
        }
        if (page.isEmpty() || page.equals("1")) return spider.searchContent(keyword, false);
        return "{\"list\":[]}";
    }

    private static Spider create(String api, String ext) {
        try {
            if (api.startsWith("http")) {
                String script = ext;
                try { script = URLDecoder.decode(ext, StandardCharsets.UTF_8); } catch (Exception ignored) { }
                if (script.contains("虎牙.js")) return new com.github.catvod.spider.Huya();
                if (script.contains("斗鱼直播.js")) return new com.github.catvod.spider.Douyu();
                if (script.contains("兔小贝.js")) return new com.github.catvod.spider.Tuxiaobei();
                throw new IllegalArgumentException("Unsupported JavaScript source: " + ext);
            }
            String name = api.startsWith("csp_") ? api.substring(4) : api;
            if (name.endsWith("Guard")) name = name.substring(0, name.length() - 5);
            name = switch (name.toLowerCase(Locale.ROOT)) {
                case "wogg" -> "Wogg";
                case "sixv" -> "Xb6v";
                case "jpj" -> "Jianpian";
                case "newcz" -> "ChangZhang";
                case "doudou" -> "Douban";
                case "alllive" -> "Live88";
                case "music" -> "Kugou";
                case "sssp" -> "ShortVideo";
                case "yueting" -> "AudioBook";
                case "bpanso" -> "PanTa";
                case "aidj" -> "ShortDrama";
                case "livegz" -> "Live88";
                default -> name;
            };
            Class<?> type = Class.forName("com.github.catvod.spider." + name);
            return (Spider) type.getDeclaredConstructor().newInstance();
        } catch (Throwable error) {
            throw new IllegalStateException("No compatible public spider implementation for " + api, error);
        }
    }

    private static String text(JsonObject object, String name) {
        return object.has(name) && !object.get(name).isJsonNull() ? object.get(name).getAsString() : "";
    }

    private static HashMap<String, String> extend(JsonObject object) {
        HashMap<String, String> result = new HashMap<>();
        if (object.has("extend") && object.get("extend").isJsonObject()) {
            for (Map.Entry<String, JsonElement> entry : object.getAsJsonObject("extend").entrySet()) result.put(entry.getKey(), entry.getValue().getAsString());
        }
        return result;
    }
}
