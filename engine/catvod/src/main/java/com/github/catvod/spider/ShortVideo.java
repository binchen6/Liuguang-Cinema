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

import java.net.URI;
import java.util.ArrayList;
import java.util.Arrays;
import java.util.HashMap;
import java.util.List;
import java.util.Map;

public class ShortVideo extends Spider {
    private static final String HOST = "http://www.sharenice.net";
    private static final Map<String, String> HEADERS = Map.of("User-Agent", Util.CHROME);
    private static final String[] IDS = {"douyin", "kuaishou", "weishi", "huoshan", "changku", "meipai", "miaopai", "quanmin", "lishipin", "haotu", "kaiyan", "t-576O5aWz", "t-5pCe56yR", "t-56S+5Lya", "t-5b2x6KeG", "t-6Z+z5LmQ", "t-5aix5LmQ", "t-5q2j6IO96YeP", "t-55Sf5rS7", "t-5Yqo5ryr", "t-5L2T6IKy", "t-576O6aOf", "t-6JCM57O7", "t-5peF5ri4", "t-5ri45oiP", "t-5piO5pif", "t-5pe25bCa"};
    private static final String[] NAMES = {"抖音", "快手", "微视", "火山", "场库", "美拍", "秒拍", "全民", "澎湃", "好兔", "开眼", "美女", "搞笑", "社会", "影视", "音乐", "娱乐", "正能量", "生活", "动漫", "体育", "美食", "萌系", "旅游", "游戏", "明星", "时尚"};

    private List<Class> classes() {
        List<Class> result = new ArrayList<>();
        for (int i = 0; i < IDS.length; i++) result.add(new Class(IDS[i], NAMES[i]));
        return result;
    }

    @Override
    public String homeContent(boolean filter) {
        return Result.string(classes(), items(HOST + "/douyin?page=1"));
    }

    @Override
    public String categoryContent(String tid, String pg, boolean filter, HashMap<String, String> extend) {
        List<Vod> list = items(HOST + "/" + tid + "?page=" + pg);
        return Result.get().vod(list).page(Integer.parseInt(pg), Integer.parseInt(pg) + (list.isEmpty() ? 0 : 1), 20, Integer.parseInt(pg) * 20).string();
    }

    private List<Vod> items(String url) {
        Document doc = Jsoup.parse(OkHttp.string(url, HEADERS), url);
        List<Vod> result = new ArrayList<>();
        for (Element item : doc.select(".item-box ul li, .item-box li")) {
            Element link = item.selectFirst("a[href]");
            if (link == null) continue;
            String name = link.attr("title");
            if (name.isEmpty()) name = link.text();
            Element image = item.selectFirst("img");
            String pic = image == null ? "" : image.attr("data-original");
            if (pic.isEmpty() && image != null) pic = image.attr("src");
            String id = link.absUrl("href");
            if (!id.isEmpty() && !name.isEmpty()) result.add(new Vod(id, name, pic, "", Vod.Style.rect(1.597f)));
        }
        return result;
    }

    @Override
    public String detailContent(List<String> ids) {
        Vod vod = new Vod(ids.get(0), "短视频", "", "短视频");
        vod.setTypeName("短视频");
        vod.setVodPlayFrom("播放");
        vod.setVodPlayUrl("播放$" + ids.get(0));
        return Result.string(vod);
    }

    @Override
    public String playerContent(String flag, String id, List<String> vipFlags) {
        URI uri = URI.create(id);
        String page = "http://m.sharenice.net/mobile" + uri.getRawPath();
        if (uri.getRawQuery() != null) page += "?" + uri.getRawQuery();
        Document doc = Jsoup.parse(OkHttp.string(page, HEADERS), page);
        Element video = doc.selectFirst(".video-play-box video, video");
        String url = video == null ? "" : video.absUrl("src");
        return (url.isEmpty() ? Result.get().parse().url(page) : Result.get().url(url)).header(HEADERS).string();
    }
}
