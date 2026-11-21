package com.github.catvod.spider;

import com.github.catvod.bean.Class;
import com.github.catvod.bean.Result;
import com.github.catvod.bean.Vod;
import com.github.catvod.crawler.Spider;
import com.github.catvod.net.OkHttp;
import org.json.JSONArray;
import org.json.JSONObject;
import org.jsoup.Jsoup;
import org.jsoup.nodes.Document;
import org.jsoup.nodes.Element;

import java.net.URI;
import java.net.URLEncoder;
import java.nio.charset.StandardCharsets;
import java.util.ArrayList;
import java.util.Arrays;
import java.util.HashMap;
import java.util.List;
import java.util.Map;
import java.util.regex.Matcher;
import java.util.regex.Pattern;

public class Tuxiaobei extends Spider {
    private static final String HOST = "https://www.tuxiaobei.com";
    private static final Map<String, String> HEADERS = Map.of("User-Agent", "Mozilla/5.0 (iPhone; CPU iPhone OS 17_0 like Mac OS X) AppleWebKit/605.1.15 Mobile/15E148 Safari/604.1");
    private static final Pattern PLAY_PATH = Pattern.compile("^/(?:v2024/)?play/([0-9]+)/?$");
    private static final int PAGE_SIZE = 30;

    private List<Class> classes() {
        return Arrays.asList(new Class("2", "儿歌"), new Class("3", "故事"), new Class("4", "国学"), new Class("25", "启蒙"));
    }

    @Override
    public String homeContent(boolean filter) {
        List<Vod> cards = readHomeCards(readPage(HOST + "/animate"));
        return Result.string(classes(), cards.isEmpty() ? categoryItems("2", 1) : cards);
    }

    private static List<Vod> readHomeCards(Document doc) {
        List<Vod> cards = new ArrayList<>();
        for (Element entry : doc.select(".animateUnitBox ul.animateTab.on a[href]")) {
            String id = videoId(entry.attr("href"));
            String name = entry.select(".tit").text();
            if (id.isEmpty() || name.isEmpty()) continue;
            Element count = entry.selectFirst("em.num");
            cards.add(new Vod(id, name, entry.select("mip-img, img").attr("abs:src"), count == null ? "" : count.ownText()));
        }
        return cards;
    }

    @Override
    public String categoryContent(String tid, String pg, boolean filter, HashMap<String, String> extend) {
        int page = pg.isEmpty() ? 1 : Math.max(1, Integer.parseInt(pg));
        List<Vod> result = tid.matches("[0-9]+") ? categoryItems(tid, page) : List.of();
        int lastPage = !result.isEmpty() && result.size() < PAGE_SIZE ? page : 0;
        return Result.get().vod(result).page(page, lastPage, PAGE_SIZE, 0).string();
    }

    private List<Vod> categoryItems(String tid, int page) {
        String text = OkHttp.string(HOST + "/list/mip-data?typeId=" + tid + "&page=" + page + "&callback=", HEADERS);
        return readCategoryCards(text);
    }

    private static List<Vod> readCategoryCards(String text) {
        int start = text.indexOf('{');
        int end = text.lastIndexOf('}');
        if (start < 0 || end <= start) throw new IllegalStateException("兔小贝未返回分类目录。");
        JSONObject packet = new JSONObject(text.substring(start, end + 1));
        if (packet.optInt("status", -1) != 0) throw new IllegalStateException("兔小贝分类服务暂不可用。");
        JSONObject data = packet.optJSONObject("data");
        JSONArray items = data == null ? null : data.optJSONArray("items");
        if (items == null) throw new IllegalStateException("兔小贝未返回分类条目。");
        List<Vod> result = new ArrayList<>();
        for (int i = 0; i < items.length(); i++) {
            JSONObject item = items.optJSONObject(i);
            if (item == null) continue;
            String id = item.optString("video_id");
            String name = item.optString("name");
            if (id.isEmpty() || name.isEmpty()) continue;
            Vod card = new Vod(id, name, item.optString("image"), item.optString("duration_string"));
            card.setTypeName(item.optString("category_name"));
            result.add(card);
        }
        return result;
    }

    @Override
    public String detailContent(List<String> ids) {
        String originalId = ids.get(0);
        String id = requireVideoId(originalId);
        Document doc = readPage(HOST + "/play/" + id);
        Element heading = doc.selectFirst("h1");
        if (heading == null || heading.text().isEmpty()) throw new IllegalStateException("兔小贝未返回视频详情。");
        String title = heading.text();
        Element player = doc.getElementById("videoWrap");
        Element coverLink = doc.selectFirst(".items .pic a[href=/play/" + id + "]");
        String cover = coverLink == null ? "" : coverLink.select("mip-img, img").attr("abs:src");
        if (cover.isEmpty() && player != null) cover = player.attr("abs:poster");
        Element card = coverLink == null ? null : coverLink.closest(".items");
        Vod vod = new Vod(originalId, title, cover, card == null ? "" : card.select(".time").text());
        vod.setTypeName(heading.parent().select(".sort").text());
        vod.setVodContent(doc.select("meta[name=description]").attr("content"));
        if (player != null || doc.selectFirst("video") != null) {
            vod.setVodPlayFrom("兔小贝");
            vod.setVodPlayUrl(title.replace('#', '＃').replace('$', '＄') + "$" + id);
        }
        return Result.string(vod);
    }

    @Override
    public String searchContent(String key, boolean quick, String pg) {
        int page = pg.isEmpty() ? 1 : Math.max(1, Integer.parseInt(pg));
        if (page > 1) return Result.get().vod(List.of()).page(page, 1, 0, 0).string();
        Document doc = readPage(HOST + "/search/index?key=" + URLEncoder.encode(key, StandardCharsets.UTF_8));
        List<Vod> result = readSearchCards(doc);
        return Result.get().vod(result).page(1, 1, result.size(), result.size()).string();
    }

    private static List<Vod> readSearchCards(Document doc) {
        List<Vod> result = new ArrayList<>();
        for (Element item : doc.select(".list-con .items, .list-con.items")) {
            Element link = item.selectFirst(".pic a[href], .text a[href]");
            String name = item.select(".text .title").text();
            String id = link == null ? "" : videoId(link.attr("href"));
            if (id.isEmpty() || name.isEmpty()) continue;
            Vod card = new Vod(id, name, item.select("mip-img, img").attr("abs:src"), item.select(".time").text());
            card.setTypeName(item.select(".text .sort").text());
            result.add(card);
        }
        return result;
    }

    @Override
    public String playerContent(String flag, String id, List<String> vipFlags) {
        String page = HOST + "/play/" + requireVideoId(id);
        Document doc = readPage(page);
        Element player = doc.getElementById("videoWrap");
        String url = player == null ? "" : player.attr("abs:video-src");
        if (url.isEmpty()) url = doc.select("video[src], video source[src]").attr("abs:src");
        if (url.startsWith("https://") || url.startsWith("http://")) return Result.get().url(url).header(HEADERS).string();
        return Result.get().parse().url(page).header(HEADERS).string();
    }

    private static Document readPage(String page) {
        return Jsoup.parse(OkHttp.string(page, HEADERS), page);
    }

    private static String requireVideoId(String value) {
        String id = videoId(value);
        if (id.isEmpty()) throw new IllegalArgumentException("兔小贝视频地址无效。");
        return id;
    }

    private static String videoId(String value) {
        if (value.matches("[0-9]+")) return value;
        try {
            URI page = URI.create(HOST).resolve(value);
            if (!"www.tuxiaobei.com".equalsIgnoreCase(page.getHost()) || page.getRawUserInfo() != null ||
                    !("https".equalsIgnoreCase(page.getScheme()) || "http".equalsIgnoreCase(page.getScheme()))) return "";
            Matcher match = PLAY_PATH.matcher(page.getPath());
            return match.matches() ? match.group(1) : "";
        } catch (IllegalArgumentException ignored) {
            return "";
        }
    }
}
