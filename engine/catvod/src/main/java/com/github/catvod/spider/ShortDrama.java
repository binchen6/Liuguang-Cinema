package com.github.catvod.spider;

import com.github.catvod.bean.Result;
import com.github.catvod.crawler.Spider;
import com.github.catvod.net.OkHttp;
import com.github.catvod.utils.Util;

import java.net.URLEncoder;
import java.nio.charset.StandardCharsets;
import java.util.HashMap;
import java.util.List;
import java.util.Map;

public class ShortDrama extends Spider {
    private static final String API = "http://zzdj.cc/api.php/provide/vod?ac=detail";
    private static final Map<String, String> HEADERS = Map.of("User-Agent", Util.CHROME);

    @Override
    public String homeContent(boolean filter) {
        return OkHttp.string(API, HEADERS);
    }

    @Override
    public String categoryContent(String tid, String pg, boolean filter, HashMap<String, String> extend) {
        return OkHttp.string(API + "&t=" + tid + "&pg=" + pg, HEADERS);
    }

    @Override
    public String detailContent(List<String> ids) {
        return OkHttp.string(API + "&ids=" + URLEncoder.encode(ids.get(0), StandardCharsets.UTF_8), HEADERS);
    }

    @Override
    public String searchContent(String key, boolean quick, String pg) {
        return OkHttp.string(API + "&wd=" + URLEncoder.encode(key, StandardCharsets.UTF_8) + "&pg=" + pg, HEADERS);
    }

    @Override
    public String playerContent(String flag, String id, List<String> vipFlags) {
        return (Util.isVideoFormat(id) ? Result.get().url(id) : Result.get().parse().url(id)).header(HEADERS).string();
    }
}
