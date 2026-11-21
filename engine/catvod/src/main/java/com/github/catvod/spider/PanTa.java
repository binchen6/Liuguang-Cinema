package com.github.catvod.spider;

import com.github.catvod.bean.Result;
import com.github.catvod.bean.Vod;
import com.github.catvod.crawler.Spider;
import com.github.catvod.net.OkHttp;
import com.github.catvod.utils.Util;
import org.jsoup.Jsoup;
import org.jsoup.nodes.Document;
import org.jsoup.nodes.Element;
import org.jsoup.select.Elements;

import java.net.URLEncoder;
import java.nio.charset.StandardCharsets;
import java.util.ArrayList;
import java.util.List;
import java.util.Map;

public class PanTa extends Spider {
    private static final String HOST = "https://panso.vip";
    private static final Map<String, String> HEADERS = Map.of("User-Agent", Util.CHROME);

    @Override
    public String homeContent(boolean filter) {
        return Result.string(new ArrayList<>());
    }

    @Override
    public String searchContent(String key, boolean quick, String pg) {
        String url = HOST + "/search?exact=false&format=video&page=" + pg + "&q=" + URLEncoder.encode(key, StandardCharsets.UTF_8) + "&share_time=&type=&user=";
        Document doc = Jsoup.parse(OkHttp.string(url, HEADERS), url);
        List<Vod> result = new ArrayList<>();
        java.util.Set<String> seen = new java.util.HashSet<>();
        for (Element link : doc.select("a[href*='/doc/']")) {
            String id = link.absUrl("href");
            String name = link.attr("title").trim();
            if (name.isEmpty()) name = link.text().trim();
            if (id.isEmpty() || name.isEmpty() || name.equals("详情页") || !seen.add(id)) continue;
            result.add(new Vod(id, name, "", "网盘分享"));
        }
        return Result.string(result);
    }

    @Override
    public String detailContent(List<String> ids) {
        String url = ids.get(0);
        Document doc = Jsoup.parse(OkHttp.string(url, HEADERS), url);
        String share = "";
        for (Element link : doc.select("a[href]")) {
            String href = link.absUrl("href");
            if (href.contains("pan.quark.cn") || href.contains("drive.uc.cn") || href.contains("aliyundrive.com") || href.contains("alipan.com")) { share = href; break; }
        }
        Elements values = doc.select(".semi-descriptions-value");
        String type = share.contains("quark") ? "夸克网盘" : share.contains("uc.cn") ? "UC网盘" : "阿里网盘";
        Vod vod = new Vod(url, doc.select("h1").text(), "", values.text());
        vod.setVodContent(share);
        vod.setVodPlayFrom("网盘");
        vod.setVodPlayUrl(type + "$" + share);
        return Result.string(vod);
    }

    @Override
    public String playerContent(String flag, String id, List<String> vipFlags) {
        if (id.startsWith("push://")) id = id.substring(7);
        return Result.get().url(id).string();
    }
}
