package com.github.catvod.spider;

import android.text.TextUtils;

import com.github.catvod.bean.Class;
import com.github.catvod.bean.Filter;
import com.github.catvod.bean.Result;
import com.github.catvod.bean.Vod;
import com.github.catvod.crawler.Spider;
import com.github.catvod.net.OkHttp;
import com.github.catvod.utils.Util;

import org.jsoup.Jsoup;
import org.jsoup.nodes.Document;
import org.jsoup.nodes.Element;
import org.jsoup.select.Elements;

import java.net.URLDecoder;
import java.net.URLEncoder;
import java.nio.charset.StandardCharsets;
import java.util.ArrayList;
import java.util.HashMap;
import java.util.LinkedHashMap;
import java.util.List;
import java.util.Map;
import java.util.regex.Matcher;
import java.util.regex.Pattern;

/**
 * @author FongMi
 */
public class Dm84 extends Spider {

    private static final String siteUrl = "https://dm84.tv";
    private static final Pattern categoryPath = Pattern.compile("^/list-([0-9]+)\\.html$");
    private static final Pattern videoPath = Pattern.compile("^/v/([0-9]+\\.html)$");
    private static final Pattern pageSuffix = Pattern.compile("-([0-9]{1,9})\\.html$");

    private HashMap<String, String> getHeaders() {
        HashMap<String, String> headers = new HashMap<>();
        headers.put("User-Agent", Util.CHROME);
        headers.put("Accept", Util.ACCEPT);
        return headers;
    }

    private Document readPage(String path) {
        String url = siteUrl + path;
        Document doc = Jsoup.parse(OkHttp.string(url, getHeaders()), url);
        if (doc.selectFirst("ul.nav_row") == null) throw new IllegalStateException("动漫巴士未返回可识别的页面。");
        return doc;
    }

    private static String encodePathValue(String text) {
        return URLEncoder.encode(text, StandardCharsets.UTF_8).replace("+", "%20").replace("-", "%2D");
    }

    private static List<Vod> readCards(Document doc) {
        List<Vod> items = new ArrayList<>();
        for (Element element : doc.select("div.item")) {
            Element title = element.selectFirst("a.title[href]");
            if (title == null || title.text().isEmpty()) continue;
            Matcher id = videoPath.matcher(title.attr("href"));
            if (!id.matches()) continue;
            String img = element.select("a.cover").attr("abs:data-bg");
            items.add(new Vod(id.group(1), title.text(), img, element.select("span.desc").text()));
        }
        return items;
    }

    private static List<Filter> readFilters(Document doc, String categoryId) {
        List<Filter> filters = new ArrayList<>();
        for (Element group : doc.select("ul.list_filter > li > div")) {
            String name = group.select("span").text();
            String key = switch (name) {
                case "类型" -> "type";
                case "时间" -> "year";
                case "排序" -> "by";
                default -> "";
            };
            if (key.isEmpty()) continue;
            int index = switch (key) {
                case "type" -> 3;
                case "year" -> 5;
                default -> 2;
            };
            List<Filter.Value> values = new ArrayList<>();
            for (Element link : group.select("a[href^=/show-][href$=.html]")) {
                String path = link.attr("href");
                String[] parts = path.substring("/show-".length(), path.length() - ".html".length()).split("-", -1);
                if (parts.length != 7 || !categoryId.equals(parts[0]) || link.text().isEmpty()) continue;
                values.add(new Filter.Value(link.text(), URLDecoder.decode(parts[index], StandardCharsets.UTF_8)));
            }
            if (!values.isEmpty()) filters.add(new Filter(key, name, values));
        }
        return filters;
    }

    private static Result readPagedCards(Document doc, int page) {
        int lastPage = 1;
        for (Element link : doc.select(".pages a[href^=/][href$=.html]")) {
            Matcher match = pageSuffix.matcher(link.attr("href"));
            if (match.find()) lastPage = Math.max(lastPage, Integer.parseInt(match.group(1)));
        }
        List<Vod> cards = page <= lastPage ? readCards(doc) : List.of();
        return Result.get().vod(cards).page(page, lastPage, cards.size(), 0);
    }

    @Override
    public String homeContent(boolean filter) {
        List<Class> classes = new ArrayList<>();
        Document doc = readPage("/");
        for (Element element : doc.select("ul.nav_row > li > a[href]")) {
            Matcher id = categoryPath.matcher(element.attr("href"));
            if (id.matches() && !element.text().isEmpty()) classes.add(new Class(id.group(1), element.text()));
        }
        if (doc.selectFirst("a[href=/timeline.html]") != null) classes.add(new Class("timeline", "追番日历"));
        Element ranking = doc.selectFirst("h2 a[href=/top.html], h3 a[href=/top.html]");
        if (ranking != null && !ranking.text().isEmpty()) classes.add(new Class("top", ranking.text()));
        return Result.string(classes, readCards(doc));
    }

    @Override
    public String categoryContent(String tid, String pg, boolean filter, HashMap<String, String> extend) {
        int page = pg.isEmpty() ? 1 : Math.max(1, Integer.parseInt(pg));
        if (tid.equals("timeline") || tid.equals("top")) {
            if (page > 1) return Result.get().page(page, 1, 0, 0).vod(List.of()).string();
            if (tid.equals("timeline")) return readTimeline(extend.get("day"), filter);
            Element ranking = readPage("/top.html").selectFirst("ul.top_v_list");
            if (ranking == null) throw new IllegalStateException("动漫巴士未返回热播榜。");
            return singlePage(readTextList(ranking)).string();
        }
        if (!tid.matches("[0-9]+")) return Result.get().page(page, 1, 0, 0).vod(List.of()).string();
        String by = extend.getOrDefault("by", "");
        String type = extend.getOrDefault("type", "");
        String year = extend.getOrDefault("year", "");
        String path;
        if (by.isEmpty() && type.isEmpty() && year.isEmpty()) {
            path = "/list-" + tid + (page == 1 ? "" : "-" + page) + ".html";
        } else {
            path = String.format("/show-%s--%s-%s--%s-%d.html", tid, encodePathValue(by), encodePathValue(type),
                    encodePathValue(year), page);
        }
        Document doc = readPage(path);
        LinkedHashMap<String, List<Filter>> filters = new LinkedHashMap<>();
        if (filter && page == 1) filters.put(tid, readFilters(doc, tid));
        return readPagedCards(doc, page).filters(filters).string();
    }

    private String readTimeline(String day, boolean filter) {
        Document doc = readPage("/timeline.html");
        Elements tabs = doc.select("ul.tab_control > li");
        Elements panels = doc.select("ul.tab_v_list");
        if (tabs.isEmpty() || tabs.size() != panels.size())
            throw new IllegalStateException("动漫巴士未返回完整的追番日历。");
        int today = 0;
        for (int i = 0; i < tabs.size(); i++) {
            if (tabs.get(i).hasClass("current")) {
                today = i;
                break;
            }
        }
        int selected = day != null && day.matches("[0-6]") ? Integer.parseInt(day) : today;
        if (selected >= panels.size()) selected = today;
        Result page = singlePage(readTextList(panels.get(selected)));
        if (filter) {
            List<Filter.Value> days = new ArrayList<>();
            days.add(new Filter.Value(tabs.get(today).text(), Integer.toString(today)));
            for (int i = 0; i < tabs.size(); i++) {
                if (i != today) days.add(new Filter.Value(tabs.get(i).text(), Integer.toString(i)));
            }
            LinkedHashMap<String, List<Filter>> definitions = new LinkedHashMap<>();
            definitions.put("timeline", List.of(new Filter("day", "星期", days)));
            page.filters(definitions);
        }
        return page.string();
    }

    private static List<Vod> readTextList(Element list) {
        List<Vod> cards = new ArrayList<>();
        for (Element row : list.children()) {
            Element title = row.selectFirst("a[href]");
            if (title == null || title.text().isEmpty()) continue;
            Matcher id = videoPath.matcher(title.attr("href"));
            if (!id.matches()) continue;
            Element status = title.nextElementSibling();
            String remark = status == null ? "" : status.text();
            String rank = row.select("span.num").text();
            if (!rank.isEmpty()) remark = remark.isEmpty() ? rank : rank + " · " + remark;
            cards.add(new Vod(id.group(1), title.text(), "", remark));
        }
        return cards;
    }

    private static Result singlePage(List<Vod> cards) {
        return Result.get().vod(cards).page(1, 1, cards.size(), cards.size());
    }

    @Override
    public String detailContent(List<String> ids) {
        Document doc = Jsoup.parse(OkHttp.string(siteUrl.concat("/v/").concat(ids.get(0)), getHeaders()));
        String name = doc.select("h1.v_title").text();
        String remarks = doc.select("p.v_desc > span.desc").text();
        String img = doc.select("meta[property=og:image]").attr("content");
        String area = doc.select("meta[name=og:video:area]").attr("content");
        String type = doc.select("meta[name=og:video:class]").attr("content");
        String actor = doc.select("meta[name=og:video:actor]").attr("content");
        String content = doc.select("meta[property=og:description]").attr("content");
        String year = doc.select("meta[name=og:video:release_date]").attr("content");
        String director = doc.select("meta[name=og:video:director]").attr("content");

        Vod vod = new Vod();
        vod.setVodId(ids.get(0));
        vod.setVodPic(img);
        vod.setVodYear(year);
        vod.setVodName(name);
        vod.setVodArea(area);
        vod.setVodActor(actor);
        vod.setVodRemarks(remarks);
        vod.setVodContent(content);
        vod.setVodDirector(director);
        vod.setTypeName(type);

        Map<String, String> sites = new LinkedHashMap<>();
        Elements sources = doc.select("ul.tab_control > li");
        Elements sourceList = doc.select("ul.play_list");
        for (int i = 0; i < sources.size(); i++) {
            Element source = sources.get(i);
            String sourceName = source.text();
            Elements playList = sourceList.get(i).select("a");
            List<String> vodItems = new ArrayList<>();
            for (int j = 0; j < playList.size(); j++) {
                Element e = playList.get(j);
                vodItems.add(e.text() + "$" + e.attr("href"));
            }
            if (vodItems.size() > 0) {
                sites.put(sourceName, TextUtils.join("#", vodItems));
            }
        }
        if (sites.size() > 0) {
            vod.setVodPlayFrom(TextUtils.join("$$$", sites.keySet()));
            vod.setVodPlayUrl(TextUtils.join("$$$", sites.values()));
        }
        return Result.string(vod);
    }

    @Override
    public String searchContent(String key, boolean quick) {
        return searchContent(key, quick, "1");
    }

    @Override
    public String searchContent(String key, boolean quick, String pg) {
        int page = pg.isEmpty() ? 1 : Math.max(1, Integer.parseInt(pg));
        String path = page == 1 ? "/s----------.html?wd=" + URLEncoder.encode(key, StandardCharsets.UTF_8) :
                "/s-" + encodePathValue(key) + "---------" + page + ".html";
        return readPagedCards(readPage(path), page).string();
    }

    @Override
    public String playerContent(String flag, String id, List<String> vipFlags) {
        Document doc = Jsoup.parse(OkHttp.string(siteUrl.concat(id), getHeaders()));
        String url = doc.select("iframe").attr("src");
        return Result.get().url(url).parse().header(getHeaders()).string();
    }
}
