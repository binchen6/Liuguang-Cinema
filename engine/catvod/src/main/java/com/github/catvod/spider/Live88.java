package com.github.catvod.spider;

import android.text.TextUtils;
import com.github.catvod.bean.Class;
import com.github.catvod.bean.Result;
import com.github.catvod.bean.Vod;
import com.github.catvod.crawler.Spider;
import com.github.catvod.net.OkHttp;
import com.github.catvod.utils.Util;
import org.json.JSONArray;
import org.json.JSONObject;
import org.jsoup.Jsoup;
import org.jsoup.nodes.Document;
import org.jsoup.nodes.Element;

import java.nio.charset.StandardCharsets;
import java.util.ArrayList;
import java.util.Base64;
import java.util.HashMap;
import java.util.List;
import java.util.Map;
import java.util.regex.Matcher;
import java.util.regex.Pattern;

public class Live88 extends Spider {
    private static final String HOST = "http://www.88kanqiu.bar";
    private static final Map<String, String> HEADERS = Map.of("User-Agent", Util.CHROME);

    @Override
    public String homeContent(boolean filter) {
        Document doc = Jsoup.parse(OkHttp.string(HOST, HEADERS), HOST);
        List<Class> classes = new ArrayList<>();
        Pattern pattern = Pattern.compile("/match/(\\d+)/live");
        for (Element link : doc.select(".nav-pills li a[href]")) {
            Matcher matcher = pattern.matcher(link.attr("href"));
            if (matcher.find()) classes.add(new Class(matcher.group(1), link.text()));
        }
        return Result.string(classes, items(doc));
    }

    @Override
    public String categoryContent(String tid, String pg, boolean filter, HashMap<String, String> extend) {
        int page = Integer.parseInt(pg);
        // The public match page lists the whole category in one response.
        List<Vod> matches = page == 1
                ? items(Jsoup.parse(OkHttp.string(HOST + "/match/" + tid + "/live", HEADERS), HOST))
                : List.of();
        return Result.get().vod(matches).page(page, 1, matches.size(), matches.size()).string();
    }

    private List<Vod> items(Document doc) {
        List<Vod> result = new ArrayList<>();
        for (Element item : doc.select(".list-group .group-game-item")) {
            Element link = item.selectFirst("a[href]");
            if (link == null) continue;
            String name = item.select(".d-none").text();
            if (name.isEmpty()) name = item.text();
            Element image = item.selectFirst("img");
            result.add(new Vod(link.absUrl("href"), name, image == null ? "" : image.absUrl("src"), item.select(".btn").text(), Vod.Style.list()));
        }
        return result;
    }

    @Override
    public String detailContent(List<String> ids) {
        String id = ids.get(0);
        Document doc = Jsoup.parse(OkHttp.string(id, HEADERS), id);
        Vod vod = new Vod(id, doc.select(".game-info-container").text(), doc.select("img").attr("src"));
        vod.setVodContent(doc.select("div.game-time").text());
        List<String> episodes = new ArrayList<>();
        String json = OkHttp.string(id.replace("play", "play-url"), HEADERS);
        String encoded = new JSONObject(json).optString("data");
        if (encoded.length() > 8) {
            String decoded = new String(Base64.getDecoder().decode(encoded.substring(6, encoded.length() - 2)), StandardCharsets.UTF_8);
            JSONArray links = new JSONObject(decoded).optJSONArray("links");
            if (links != null) for (int i = 0; i < links.length(); i++) {
                JSONObject link = links.optJSONObject(i);
                if (link != null) episodes.add(link.optString("name") + "$" + link.optString("url"));
            }
        }
        vod.setVodPlayFrom("实时直播");
        vod.setVodPlayUrl(TextUtils.join("#", episodes));
        return Result.string(vod);
    }

    @Override
    public String playerContent(String flag, String id, List<String> vipFlags) {
        if (id.contains("embed=")) {
            String value = id.split("embed=")[1].split("&")[0];
            return Result.get().url(new String(Base64.getDecoder().decode(value), StandardCharsets.UTF_8).split("#")[0]).string();
        }
        if (id.contains("?url=")) return Result.get().url(id.split("\\?url=")[1].split("#")[0]).string();
        return (Util.isVideoFormat(id) ? Result.get().url(id) : Result.get().parse().url(id)).header(HEADERS).string();
    }
}
