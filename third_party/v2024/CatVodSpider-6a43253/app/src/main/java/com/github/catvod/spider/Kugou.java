package com.github.catvod.spider;

import android.text.TextUtils;

import com.github.catvod.bean.Class;
import com.github.catvod.bean.Result;
import com.github.catvod.bean.Vod;
import com.github.catvod.crawler.Spider;
import com.github.catvod.net.OkHttp;
import com.github.catvod.utils.Util;

import org.jsoup.Jsoup;
import org.jsoup.nodes.Document;
import org.jsoup.nodes.Element;

import java.net.URI;
import java.nio.charset.StandardCharsets;
import java.util.ArrayList;
import java.util.Base64;
import java.util.HashMap;
import java.util.List;
import java.util.Map;
import java.util.regex.Matcher;
import java.util.regex.Pattern;

/**
 * @author Qile
 */
public class Kugou extends Spider {
    private static final URI ORIGIN = URI.create("https://www.kugou.com/");
    private static final String PLAYER_ID = "kugou:";
    private static final Pattern RANK_PATH = Pattern.compile("^/yy/rank/home/1-([0-9]+)\\.html$");

    private Map<String, String> getHeader() {
        Map<String, String> header = new HashMap<>();
        header.put("User-Agent", Util.CHROME);
        return header;
    }

    @Override
    public String homeContent(boolean filter) throws Exception {
        List<RankGroup> groups = readRankGroups(readPage(rankPage("6666")));
        List<Class> classes = new ArrayList<>();
        List<Vod> list = new ArrayList<>();
        for (int i = 0; i < groups.size(); i++) {
            RankGroup group = groups.get(i);
            classes.add(new Class(group.id() + "|" + i, group.name()));
            list.addAll(group.cards());
        }
        return Result.string(classes, list);
    }

    @Override
    public String categoryContent(String tid, String pg, boolean filter, HashMap<String, String> extend) throws Exception {
        int page = pg.isEmpty() ? 1 : Math.max(1, Integer.parseInt(pg));
        String[] parts = tid.split("\\|", 2);
        if (page > 1 || parts.length != 2 || !parts[0].matches("[0-9]+") || !parts[1].matches("[0-9]{1,9}")) {
            return Result.get().vod(List.of()).page(page, 1, 0, 0).string();
        }
        String cateId = extend == null ? null : extend.get("cateId");
        if (cateId == null) cateId = parts[0];
        if (!cateId.matches("[0-9]+")) throw new IllegalArgumentException("酷狗榜单分类无效。");
        List<RankGroup> groups = readRankGroups(readPage(rankPage(cateId)));
        int groupIndex = Integer.parseInt(parts[1]);
        List<Vod> cards = groupIndex < groups.size() ? groups.get(groupIndex).cards() : List.of();
        return Result.get().vod(cards).page(1, 1, cards.size(), cards.size()).string();
    }

    @Override
    public String detailContent(List<String> ids) throws Exception {
        URI address = sitePage(ids.get(0));
        if (address == null || !RANK_PATH.matcher(address.getPath()).matches())
            throw new IllegalArgumentException("酷狗榜单地址无效。");
        return readRankDetail(ids.get(0), readPage(address)).toString();
    }

    private static Result readRankDetail(String id, Document doc) {
        String title = doc.select(".pc_temp_title h3").text();
        if (title.isEmpty()) throw new IllegalStateException("酷狗未返回榜单详情。");
        List<String> vodItems = new ArrayList<>();
        for (Element song : doc.select(".pc_temp_songlist li a.pc_temp_songname[href]")) {
            URI link = sitePage(song.absUrl("href"));
            String name = song.text();
            if (link == null || name.isEmpty()) continue;
            String address = link.toString();
            if (address.indexOf('#') >= 0 || address.indexOf('$') >= 0) {
                address = PLAYER_ID + Base64.getUrlEncoder().encodeToString(address.getBytes(StandardCharsets.UTF_8));
            }
            vodItems.add(name.replace('$', '＄').replace('#', '＃') + "$" + address);
        }
        Vod vod = new Vod();
        vod.setVodId(id);
        vod.setVodName(title);
        vod.setVodRemarks(doc.select(".rank_update").text());
        if (!vodItems.isEmpty()) {
            vod.setVodPlayFrom("Qile");
            vod.setVodPlayUrl(TextUtils.join("#", vodItems));
        }
        return Result.get().vod(List.of(vod));
    }

    @Override
    public String playerContent(String flag, String id, List<String> vipFlags) throws Exception {
        String address = id.startsWith(PLAYER_ID)
                ? new String(Base64.getUrlDecoder().decode(id.substring(PLAYER_ID.length())), StandardCharsets.UTF_8)
                : id;
        URI page = sitePage(address);
        if (page == null) throw new IllegalArgumentException("酷狗歌曲地址无效。");
        return Result.get().url(page.toString()).parse().header(getHeader()).string();
    }

    private Document readPage(URI page) {
        return Jsoup.parse(OkHttp.string(page.toString(), getHeader()), page.toString());
    }

    private static URI rankPage(String id) {
        return ORIGIN.resolve("/yy/rank/home/1-" + id + ".html?from=rank");
    }

    private static List<RankGroup> readRankGroups(Document doc) {
        List<RankGroup> groups = new ArrayList<>();
        for (Element group : doc.select(".pc_rank_sidebar")) {
            String name = group.select("h3").text();
            List<Vod> cards = new ArrayList<>();
            String id = "";
            for (Element link : group.select("ul li a[href]")) {
                URI uri = sitePage(link.absUrl("href"));
                if (uri == null) continue;
                Matcher match = RANK_PATH.matcher(uri.getPath());
                String title = link.attr("title");
                if (title.isEmpty()) title = link.text();
                if (!match.matches() || title.isEmpty()) continue;
                if (cards.isEmpty()) id = match.group(1);
                Vod card = new Vod(uri.toString(), title, "");
                card.setTypeName(name);
                cards.add(card);
            }
            if (!cards.isEmpty() && !name.isEmpty()) groups.add(new RankGroup(id, name, cards));
        }
        if (groups.isEmpty()) throw new IllegalStateException("酷狗未返回排行榜目录。");
        return groups;
    }

    private static URI sitePage(String address) {
        if (address.isBlank()) return null;
        try {
            URI page = ORIGIN.resolve(address);
            if (!"www.kugou.com".equalsIgnoreCase(page.getHost()) || page.getRawUserInfo() != null || page.getPort() != -1)
                return null;
            if ("https".equalsIgnoreCase(page.getScheme())) return page;
            return "http".equalsIgnoreCase(page.getScheme()) ? URI.create("https:" + page.getRawSchemeSpecificPart() +
                    (page.getRawFragment() == null ? "" : "#" + page.getRawFragment())) : null;
        } catch (IllegalArgumentException ignored) {
            return null;
        }
    }

    private record RankGroup(String id, String name, List<Vod> cards) { }
}
