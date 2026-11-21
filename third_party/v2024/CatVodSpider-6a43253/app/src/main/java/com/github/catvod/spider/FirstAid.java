package com.github.catvod.spider;

import com.github.catvod.bean.Class;
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
import java.util.ArrayList;
import java.util.HashMap;
import java.util.List;
import java.util.Map;

/**
 * @author Qile
 */
public class FirstAid extends Spider {

    private final String siteUrl = "https://m.youlai.cn";
    private static final List<String> categoryNames = List.of("急救技能", "家庭生活", "急危重症", "常见损伤",
            "动物致伤", "海洋急救", "中毒急救", "意外事故");

    private Map<String, String> getHeader() {
        Map<String, String> header = new HashMap<>();
        header.put("User-Agent", Util.CHROME);
        return header;
    }

    @Override
    public String homeContent(boolean filter) throws Exception {
        Elements sections = readSections("jijiu");
        List<Class> classes = new ArrayList<>();
        List<Vod> cards = new ArrayList<>();
        for (int i = 0; i < Math.min(categoryNames.size(), sections.size()); i++) {
            classes.add(new Class("jijiu|" + i, categoryNames.get(i)));
            cards.addAll(readCards(sections.get(i), categoryNames.get(i)));
        }
        return Result.string(classes, cards);
    }

    @Override
    public String categoryContent(String tid, String pg, boolean filter, HashMap<String, String> extend) throws Exception {
        int page = pg.isEmpty() ? 1 : Math.max(1, Integer.parseInt(pg));
        String[] item = tid.split("\\|", 2);
        if (page > 1 || item.length != 2 || !item[1].matches("[0-7]")) {
            return Result.get().page(page, 1, 0, 0).vod(List.of()).string();
        }
        int section = Integer.parseInt(item[1]);
        Elements sections = readSections(extend.getOrDefault("cateId", item[0]));
        List<Vod> cards = section < sections.size() ? readCards(sections.get(section), categoryNames.get(section)) : List.of();
        return Result.get().page(1, 1, cards.size(), cards.size()).vod(cards).string();
    }

    private Elements readSections(String guide) {
        String url = siteUrl + "/" + guide;
        Elements sections = Jsoup.parse(OkHttp.string(url, getHeader()), url).select(".jj-list-wrap");
        if (sections.isEmpty()) throw new IllegalStateException("有来医生未返回急救指南目录。");
        return sections;
    }

    private List<Vod> readCards(Element section, String category) {
        String picture = section.select("img.block100").attr("abs:src");
        List<Vod> cards = new ArrayList<>();
        for (Element link : section.select(".jj-title-li .list-br3 a[href]")) {
            String id = link.absUrl("href");
            String name = link.text();
            if (!id.startsWith(siteUrl + "/") || name.isEmpty()) continue;
            Element group = link.closest("dl");
            String remark = group == null ? "" : group.select("dt h5").text();
            Vod card = new Vod(id, name, picture, remark);
            card.setTypeName(category);
            cards.add(card);
        }
        return cards;
    }

    @Override
    public String detailContent(List<String> ids) throws Exception {
        Document doc = Jsoup.parse(OkHttp.string(ids.get(0), getHeader()));
        String title = doc.select(".video-title.h1-title").text();
        String pic = doc.select(".video-cover.list-flex-in img").attr("src");
        String actor = doc.select("span.doc-name").text();
        String area = "中国";
        String brief = doc.select(".img-text-con").text();
        String play_url = doc.select("#video source").attr("src");
        if (play_url.isEmpty()) play_url = doc.select("#video").attr("src");
        String vod_play_url = title + "$" + play_url;
        Vod vod = new Vod();
        vod.setVodId(ids.get(0));
        vod.setVodPic(pic);
        vod.setVodName(title);
        vod.setVodActor(actor);
        vod.setVodArea(area);
        vod.setVodContent(brief);
        vod.setVodPlayFrom("Qile");
        vod.setVodPlayUrl(vod_play_url);
        return Result.string(vod);
    }

    @Override
    public String searchContent(String key, boolean quick) throws Exception {
        String searchUrl = siteUrl + "/cse/search?q=" + URLEncoder.encode(key);
        Elements lis = Jsoup.parse(OkHttp.string(searchUrl, getHeader())).select(".search-video-li.list-br2");
        List<Vod> list = new ArrayList<>();
        for (Element li : lis) {
            String vid = siteUrl + li.select("a").attr("href");
            String name = li.select("h5.line-clamp1").text();
            String pic = li.select("dt.logo-bg img").attr("src");
            if (!pic.startsWith("https")) pic = "https:" + pic;
            list.add(new Vod(vid, name, pic));
        }
        return Result.string(list);
    }

    @Override
    public String playerContent(String flag, String id, List<String> vipFlags) throws Exception {
        return Result.get().url(id).header(getHeader()).string();
    }
}
