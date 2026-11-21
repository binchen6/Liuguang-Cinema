package com.github.catvod.spider;

import com.github.catvod.bean.Class;
import com.github.catvod.bean.Result;
import com.github.catvod.bean.Vod;
import com.github.catvod.crawler.Spider;
import com.github.catvod.net.OkHttp;
import org.json.JSONArray;
import org.json.JSONObject;

import java.net.URLEncoder;
import java.nio.charset.StandardCharsets;
import java.util.ArrayList;
import java.util.Arrays;
import java.util.HashMap;
import java.util.List;
import java.util.Map;

public class Douyu extends Spider {
    private static final Map<String, String> HEADERS = Map.of("User-Agent", "Mozilla/5.0 (Linux; Android 13) AppleWebKit/537.36 Chrome/122.0.0.0 Mobile Safari/537.36", "Referer", "https://m.douyu.com/");
    private static final Map<String, String> DEFAULT_TYPES = Map.of("yl", "yqk", "PCgame", "LOL", "djry", "TVgame", "syxx", "wzry", "yz", "yz", "kjwh", "smkj", "yp", "yiqiwan", "voice", "yyzs", "znl", "znl");

    private List<Class> classes() {
        return Arrays.asList(new Class("yl", "娱乐天地"), new Class("PCgame", "网游竞技"), new Class("djry", "单机热游"), new Class("syxx", "手游休闲"), new Class("yz", "颜值"), new Class("kjwh", "科技文化"), new Class("yp", "语音互动"), new Class("voice", "语音直播"), new Class("znl", "正能量"));
    }

    @Override
    public String homeContent(boolean filter) {
        List<Vod> result = new ArrayList<>();
        JSONArray groups = new JSONObject(OkHttp.string("https://m.douyu.com/api/home/mix", HEADERS)).optJSONArray("data");
        if (groups != null) for (int i = 0; i < groups.length() && result.size() < 60; i++) {
            JSONObject group = groups.optJSONObject(i);
            if (group != null) addRooms(result, group.optJSONArray("list"));
        }
        return Result.string(classes(), result);
    }

    @Override
    public String categoryContent(String tid, String pg, boolean filter, HashMap<String, String> extend) {
        String type = extend == null ? null : extend.get("cateId");
        if (type == null || type.isEmpty()) type = DEFAULT_TYPES.getOrDefault(tid, tid);
        String json = OkHttp.string("https://m.douyu.com/api/room/list?page=" + pg + "&type=" + URLEncoder.encode(type, StandardCharsets.UTF_8), HEADERS);
        JSONObject data = new JSONObject(json).optJSONObject("data");
        List<Vod> result = new ArrayList<>();
        if (data != null) addRooms(result, data.optJSONArray("list"));
        return Result.get().vod(result).page(Integer.parseInt(pg), Integer.parseInt(pg) + (result.isEmpty() ? 0 : 1), 20, Integer.parseInt(pg) * 20).string();
    }

    private void addRooms(List<Vod> result, JSONArray items) {
        if (items == null) return;
        for (int i = 0; i < items.length(); i++) {
            JSONObject item = items.optJSONObject(i);
            if (item == null) continue;
            String id = item.optString("rid", item.optString("roomId"));
            String name = item.optString("roomName");
            if (!id.isEmpty() && !name.isEmpty()) result.add(new Vod(id, name, item.optString("roomSrc"), item.optString("nickname"), Vod.Style.rect(1.755f)));
        }
    }

    @Override
    public String detailContent(List<String> ids) {
        Vod vod = new Vod(ids.get(0), "斗鱼直播 " + ids.get(0), "", "直播");
        vod.setTypeName("直播");
        vod.setVodPlayFrom("斗鱼");
        vod.setVodPlayUrl("直播$" + ids.get(0));
        return Result.string(vod);
    }

    @Override
    public String searchContent(String key, boolean quick, String pg) {
        String url = "https://m.douyu.com/api/search/liveRoom";
        Map<String, String> form = new HashMap<>();
        form.put("did", "10000000000000000000000000001501");
        form.put("limit", "20");
        form.put("offset", String.valueOf(Math.max(0, Integer.parseInt(pg) - 1) * 20));
        form.put("sk", key);
        JSONObject data = new JSONObject(OkHttp.post(url, form, HEADERS).getBody()).optJSONObject("data");
        List<Vod> result = new ArrayList<>();
        if (data != null) addRooms(result, data.optJSONArray("list"));
        return Result.string(result);
    }

    @Override
    public String playerContent(String flag, String id, List<String> vipFlags) {
        return Result.get().parse().url("https://m.douyu.com/" + URLEncoder.encode(id, StandardCharsets.UTF_8)).header(HEADERS).string();
    }
}
