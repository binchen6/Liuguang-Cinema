package com.github.catvod.spider;

import com.github.catvod.bean.Class;
import com.github.catvod.bean.Result;
import com.github.catvod.bean.Vod;
import com.github.catvod.crawler.Spider;
import com.github.catvod.net.OkHttp;
import com.github.catvod.utils.Util;
import org.json.JSONArray;
import org.json.JSONObject;

import java.net.URLEncoder;
import java.nio.charset.StandardCharsets;
import java.util.ArrayList;
import java.util.Arrays;
import java.util.HashMap;
import java.util.List;
import java.util.Map;

public class Huya extends Spider {
    private static final Map<String, String> HEADERS = Map.of("User-Agent", Util.CHROME, "Referer", "https://www.huya.com/");
    private static final Map<String, String> DEFAULT_GAMES = Map.of("8", "2168", "1", "1", "2", "1732", "3", "2336");

    private List<Class> classes() {
        return Arrays.asList(new Class("8", "娱乐"), new Class("1", "网游"), new Class("2", "单机"), new Class("3", "手游"));
    }

    @Override
    public String homeContent(boolean filter) {
        return Result.string(classes(), rooms(category("8", "1"))).toString();
    }

    @Override
    public String categoryContent(String tid, String pg, boolean filter, HashMap<String, String> extend) {
        String game = extend == null ? null : extend.get("cateId");
        if (game == null || game.isEmpty()) game = DEFAULT_GAMES.getOrDefault(tid, tid);
        return Result.get().vod(rooms(category(game, pg))).page(Integer.parseInt(pg), Integer.parseInt(pg) + 1, 20, Integer.parseInt(pg) * 20).string();
    }

    private String category(String game, String page) {
        return OkHttp.string("https://www.huya.com/cache.php?m=LiveList&do=getLiveListByPage&gameId=" + game + "&tagAll=0&page=" + page, HEADERS);
    }

    private List<Vod> rooms(String json) {
        List<Vod> result = new ArrayList<>();
        JSONObject data = new JSONObject(json).optJSONObject("data");
        JSONArray items = data == null ? null : data.optJSONArray("datas");
        if (items == null) return result;
        for (int i = 0; i < items.length(); i++) {
            JSONObject item = items.optJSONObject(i);
            if (item == null) continue;
            String id = item.optString("profileRoom", item.optString("roomId"));
            String name = item.optString("introduction", item.optString("roomName", item.optString("nick")));
            String pic = item.optString("screenshot", item.optString("game_imgUrl"));
            String remarks = item.optString("nick");
            if (!id.isEmpty() && !name.isEmpty()) result.add(new Vod(id, name, pic, remarks, Vod.Style.rect(1.755f)));
        }
        return result;
    }

    @Override
    public String detailContent(List<String> ids) {
        Vod vod = new Vod(ids.get(0), "虎牙直播 " + ids.get(0), "", "直播");
        vod.setTypeName("直播");
        vod.setVodPlayFrom("虎牙");
        vod.setVodPlayUrl("直播$" + ids.get(0));
        return Result.string(vod);
    }

    @Override
    public String searchContent(String key, boolean quick, String pg) {
        String url = "https://search.cdn.huya.com/?m=Search&do=getSearchContent&q=" + URLEncoder.encode(key, StandardCharsets.UTF_8) + "&uid=0&v=4&typ=-5&livestate=0&rows=40&start=0";
        JSONObject response = new JSONObject(OkHttp.string(url, HEADERS)).optJSONObject("response");
        JSONObject room = response == null ? null : response.optJSONObject("3");
        JSONArray docs = room == null ? null : room.optJSONArray("docs");
        List<Vod> result = new ArrayList<>();
        if (docs != null) for (int i = 0; i < docs.length(); i++) {
            JSONObject item = docs.optJSONObject(i);
            if (item == null) continue;
            String id = item.optString("room_id");
            String name = item.optString("game_nick", item.optString("gameName"));
            if (!id.isEmpty()) result.add(new Vod(id, name, item.optString("game_screenshot"), "直播", Vod.Style.rect(1.755f)));
        }
        return Result.string(result);
    }

    @Override
    public String playerContent(String flag, String id, List<String> vipFlags) {
        String json = OkHttp.string("https://mp.huya.com/cache.php?m=Live&do=profileRoom&roomid=" + URLEncoder.encode(id, StandardCharsets.UTF_8), HEADERS);
        JSONObject stream = new JSONObject(json).optJSONObject("data");
        stream = stream == null ? null : stream.optJSONObject("stream");
        JSONObject flv = stream == null ? null : stream.optJSONObject("flv");
        JSONArray lines = flv == null ? null : flv.optJSONArray("multiLine");
        if (lines != null) for (int i = 0; i < lines.length(); i++) {
            JSONObject line = lines.optJSONObject(i);
            String url = line == null ? null : line.optString("url");
            if (url != null && url.startsWith("http")) return Result.get().url(url).header(HEADERS).string();
        }
        return Result.get().parse().url("https://m.huya.com/" + id).header(HEADERS).string();
    }
}
