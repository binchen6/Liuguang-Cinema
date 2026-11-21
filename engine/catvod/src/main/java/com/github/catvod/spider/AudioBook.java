package com.github.catvod.spider;

import android.text.TextUtils;
import com.github.catvod.bean.Class;
import com.github.catvod.bean.Result;
import com.github.catvod.bean.Vod;
import com.github.catvod.crawler.Spider;
import com.github.catvod.net.OkHttp;
import org.jsoup.Jsoup;
import org.jsoup.nodes.Document;
import org.jsoup.nodes.Element;
import org.jsoup.select.Elements;

import java.net.URLEncoder;
import java.nio.charset.StandardCharsets;
import java.util.ArrayList;
import java.util.HashMap;
import java.util.List;
import java.util.Map;

public class AudioBook extends Spider {
    private static final String HOST = "http://m.ysxs8.top";
    private static final Map<String, String> HEADERS = Map.of("User-Agent", "Mozilla/5.0 (Linux; Android 13) AppleWebKit/537.36 Chrome/122.0.0.0 Mobile Safari/537.36");
    private static final String[] IDS = {"r52", "r17", "r12", "r13", "r14", "r15", "r45", "r81", "r36", "r3", "r7", "r16", "r18", "r32", "r35", "r41", "r4", "r6"};
    private static final String[] NAMES = {"网络玄幻", "恐怖悬疑", "传统武侠", "都市言情", "官场刑侦", "历史军事", "探险盗墓", "职场商战", "明朝那些事儿", "评书下载", "相声戏曲", "人物传记", "广播剧", "百家讲坛", "外语读物", "有声文学", "儿童读物", "管理营销"};

    private List<Class> classes() {
        List<Class> result = new ArrayList<>();
        for (int i = 0; i < IDS.length; i++) result.add(new Class(IDS[i], NAMES[i]));
        return result;
    }

    @Override
    public String homeContent(boolean filter) {
        return Result.string(classes(), items(HOST + "/downlist/r52_1.html"));
    }

    @Override
    public String categoryContent(String tid, String pg, boolean filter, HashMap<String, String> extend) {
        List<Vod> list = items(HOST + "/downlist/" + tid + "_" + pg + ".html");
        return Result.get().vod(list).page(Integer.parseInt(pg), Integer.parseInt(pg) + (list.isEmpty() ? 0 : 1), 20, Integer.parseInt(pg) * 20).string();
    }

    private List<Vod> items(String url) {
        Document doc = Jsoup.parse(OkHttp.string(url, HEADERS), url);
        List<Vod> result = new ArrayList<>();
        for (Element item : doc.select(".book-ol li")) {
            Element link = item.selectFirst("a[href]");
            if (link == null) continue;
            Element img = item.selectFirst("img");
            result.add(new Vod(link.absUrl("href"), item.select("h4").text(), img == null ? "" : img.attr("data-original"), item.select(".book-meta").text()));
        }
        return result;
    }

    @Override
    public String detailContent(List<String> ids) {
        String url = ids.get(0).startsWith("http") ? ids.get(0) : HOST + ids.get(0);
        Document doc = Jsoup.parse(OkHttp.string(url, HEADERS), url);
        Vod vod = new Vod(url, doc.select("h1").text(), doc.select(".book img").attr("src"));
        vod.setVodContent(doc.select(".book-des--p, .book-des p").text());
        List<String> episodes = new ArrayList<>();
        for (Element item : doc.select("#playlist ul li")) {
            Element link = item.selectFirst("a[href]");
            if (link != null) episodes.add((link.text().isEmpty() ? item.text() : link.text()) + "$" + link.absUrl("href"));
        }
        vod.setVodPlayFrom("有声小说");
        vod.setVodPlayUrl(TextUtils.join("#", episodes));
        return Result.string(vod);
    }

    @Override
    public String searchContent(String key, boolean quick, String pg) {
        return Result.string(items(HOST + "/search.asp?page=" + pg + "&searchword=" + URLEncoder.encode(key, StandardCharsets.UTF_8) + "&searchtype=-1"));
    }

    @Override
    public String playerContent(String flag, String id, List<String> vipFlags) {
        return Result.get().parse().url(id).header(HEADERS).string();
    }
}
