package com.github.catvod.spider;

import android.content.Context;
import android.text.TextUtils;
import com.github.catvod.api.UCApi;
import com.github.catvod.bean.Result;
import com.github.catvod.bean.quark.ShareData;
import com.github.catvod.crawler.Spider;

import java.util.ArrayList;
import java.util.List;
import java.util.Locale;

/**
 * @author ColaMint & Adam & FongMi
 */
public class UC extends Spider {


    @Override
    public void init(Context context, String extend) throws Exception {

        com.google.gson.JsonObject ext = com.github.catvod.utils.Json.safeObject(extend);
        String cookie = ext.has("cookie") ? ext.get("cookie").getAsString() : extend;
        if (cookie == null || cookie.isEmpty()) UCApi.get().setRefreshToken("");
        else UCApi.get().initQuark(cookie);
    }

    @Override
    public String detailContent(List<String> ids) throws Exception {

        ShareData shareData = UCApi.get().getShareData(ids.get(0));
        return Result.string(UCApi.get().getVod(shareData));
    }


    @Override
    public String playerContent(String flag, String id, List<String> vipFlags) throws Exception {
        return UCApi.get().playerContent(id.split("\\+\\+"), flag);

    }

    /**
     * 獲取詳情內容視頻播放來源（多 shared_link）
     *
     * @param ids share_link 集合
     * @return 詳情內容視頻播放來源
     */
    public String detailContentVodPlayFrom(List<String> ids) {
        List<String> playFrom = new ArrayList<>();
       /* if (ids.size() < 2){
            return TextUtils.join("$$$",  UCApi.get().getPlayFormatList());
        }*/
        for (int i = 1; i <= ids.size(); i++) {
            for (String s : UCApi.get().getPlayFormatList()) {
                playFrom.add(String.format(Locale.getDefault(), "uc" + s + "#%02d", i));

            }

        }
        return TextUtils.join("$$$", playFrom);
    }

    /**
     * 獲取詳情內容視頻播放地址（多 share_link）
     *
     * @param ids share_link 集合
     * @return 詳情內容視頻播放地址
     */
    public String detailContentVodPlayUrl(List<String> ids) throws Exception {
        List<String> playUrl = new ArrayList<>();
        for (String id : ids) {
            ShareData shareData = UCApi.get().getShareData(id);
            playUrl.add(UCApi.get().getVod(shareData).getVodPlayUrl());
        }
        return TextUtils.join("$$$", playUrl);
    }

}
