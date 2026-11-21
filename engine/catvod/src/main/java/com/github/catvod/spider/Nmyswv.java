package com.github.catvod.spider;

import android.content.Context;
import java.util.HashMap;

public class Nmyswv extends XPath {
    private static final String RULE = """
        {
          "ua":"Mozilla/5.0 (Linux; Android 13) AppleWebKit/537.36 Chrome/122.0.0.0 Mobile Safari/537.36",
          "homeUrl":"https://m.wwgz.cn",
          "cateManual":{"电影":"1","连续剧":"2","动漫":"4","短剧":"26","综艺":"3"},
          "homeVodNode":"//div[@class='mod_a globalPadding']//li",
          "homeVodName":"//div[@class='con_bottom']/span[@class='sTit']/text()",
          "homeVodId":"//a/@href",
          "homeVodIdR":"/vod-detail-id-([0-9]+)[.]html",
          "homeVodImg":"//img/@data-src",
          "homeVodMark":"//div[@class='con_bottom']/span[@class='sDes']/text()",
          "cateUrl":"https://m.wwgz.cn/vod-list-id-{cateId}-pg-{catePg}-order--by-time-class-0-year--letter--area--lang-.html",
          "cateVodNode":"//ul[@class='picTxt picTxtA clearfix']//li/div/a",
          "cateVodName":"/@title",
          "cateVodId":"/@href",
          "cateVodIdR":"/vod-detail-id-([0-9]+)[.]html",
          "cateVodImg":"//img/@data-src",
          "cateVodMark":"//em/text()",
          "dtUrl":"https://m.wwgz.cn/vod-detail-id-{vid}.html",
          "dtNode":"//body",
          "dtName":"//div[@class='introTxt']//td/text()",
          "dtImg":"//div[@class='posterPic']/a/img/@src",
          "dtCate":"//span[contains(text(), '类型：')]//em/text()",
          "dtYear":"//span[contains(text(), '年代')]/text()",
          "dtYearR":"年代:([0-9]+)",
          "dtActor":"//span[contains(text(), '主演')]/text()",
          "dtActorR":"主演:(.*)",
          "dtDirector":"//span[contains(text(), '导演')]/text()",
          "dtDirectorR":"导演:(.*)",
          "dtDesc":"//article[@class='detail-con']/p/text()",
          "dtFromNode":"//div[@class='tabCon']",
          "dtFromName":"//div[@class='boxConWidth clearfix']/p/span/text()",
          "dtUrlNode":"//ul[contains(@class,'dramaNumList')]",
          "dtUrlSubNode":"/li/a",
          "dtUrlId":"/@data-href",
          "dtUrlName":"/text()",
          "playUrl":"https://m.wwgz.cn/{playUrl}",
          "searchUrl":"https://m.wwgz.cn/index.php?m=vod-search&wd={wd}",
          "scVodNode":"//ul[@class='ulPicTxt clearfix']/li",
          "scVodName":"//span[@class='sTit']/text()",
          "scVodId":"//span[@class='sBtn']/a/@href",
          "scVodIdR":"/vod-detail-id-([0-9]+)",
          "scVodImg":"//img/@data-src",
          "scVodMark":"//span[@class='sDes'][2]/text()"
        }
        """;

    @Override
    public void init(Context context, String extend) {
        super.init(context, RULE);
    }
}
