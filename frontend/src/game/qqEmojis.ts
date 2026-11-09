/**
 * 经典 QQ 风格官方动态表情定义与双源分发映射 (qqEmojis)
 *
 * 物理隔离与双源梯级分发设计：
 * 1. 纯 TypeScript 数据契约，不包含 JSX 语法，确保 Node.js ESM 单测与构建工具链零障碍原生加载；
 * 2. 一级优先源：自建 Cloudflare Worker + R2 边缘图床 (https://img.yuohira.com/emotes/s{qsid}.gif)；
 * 3. 二级保底源：游戏服务器前端本地静态资源 (/emotes/s{qsid}.gif)；
 * 4. 离线缓存层：结合客户端 IndexedDB (daohua_image_cache)，启动时在空闲阶段全量预加载，秒开脱机可用。
 */

export interface QqEmojiMeta {
  /** 经典中文名称，如 "微笑" */
  name: string
  /** 官方 QSid 标识，如 "14" */
  qsid: string
  /** 英文标识，如 "smile" */
  id: string
}

/** 官方 CDN / 自建图床基础地址 */
export const CF_EMOTE_BASE_URL = 'https://img.yuohira.com/emotes'
export const LOCAL_EMOTE_BASE_URL = '/emotes'

/** 精选 205 个经典常用 QQ 官方动态表情列表 */
export const QQ_EMOJI_LIST: readonly QqEmojiMeta[] = [
  {
    "name": "微笑",
    "qsid": "14",
    "id": "weixiao"
  },
  {
    "name": "呲牙",
    "qsid": "13",
    "id": "ziya"
  },
  {
    "name": "偷笑",
    "qsid": "20",
    "id": "touxiao"
  },
  {
    "name": "发呆",
    "qsid": "3",
    "id": "fadai"
  },
  {
    "name": "得意",
    "qsid": "4",
    "id": "deyi"
  },
  {
    "name": "流泪",
    "qsid": "5",
    "id": "liulei"
  },
  {
    "name": "害羞",
    "qsid": "6",
    "id": "haixiu"
  },
  {
    "name": "闭嘴",
    "qsid": "7",
    "id": "bizui"
  },
  {
    "name": "睡",
    "qsid": "8",
    "id": "shui"
  },
  {
    "name": "大哭",
    "qsid": "9",
    "id": "daku"
  },
  {
    "name": "尴尬",
    "qsid": "10",
    "id": "ganga"
  },
  {
    "name": "发怒",
    "qsid": "11",
    "id": "fanu"
  },
  {
    "name": "调皮",
    "qsid": "12",
    "id": "tiaopi"
  },
  {
    "name": "酷",
    "qsid": "16",
    "id": "ku"
  },
  {
    "name": "抓狂",
    "qsid": "18",
    "id": "zhuakuang"
  },
  {
    "name": "吐",
    "qsid": "19",
    "id": "tu"
  },
  {
    "name": "可爱",
    "qsid": "21",
    "id": "keai"
  },
  {
    "name": "白眼",
    "qsid": "22",
    "id": "baiyan"
  },
  {
    "name": "傲慢",
    "qsid": "23",
    "id": "aoman"
  },
  {
    "name": "饥饿",
    "qsid": "24",
    "id": "jie"
  },
  {
    "name": "困",
    "qsid": "25",
    "id": "kun"
  },
  {
    "name": "惊恐",
    "qsid": "26",
    "id": "jingkong"
  },
  {
    "name": "流汗",
    "qsid": "27",
    "id": "liuhan"
  },
  {
    "name": "憨笑",
    "qsid": "28",
    "id": "hanxiao"
  },
  {
    "name": "悠闲",
    "qsid": "29",
    "id": "youxian"
  },
  {
    "name": "奋斗",
    "qsid": "30",
    "id": "fendou"
  },
  {
    "name": "咒骂",
    "qsid": "31",
    "id": "zhouma"
  },
  {
    "name": "疑问",
    "qsid": "32",
    "id": "yiwen"
  },
  {
    "name": "嘘",
    "qsid": "33",
    "id": "xu"
  },
  {
    "name": "晕",
    "qsid": "34",
    "id": "yun"
  },
  {
    "name": "折磨",
    "qsid": "35",
    "id": "zhemo"
  },
  {
    "name": "敲打",
    "qsid": "38",
    "id": "qiaoda"
  },
  {
    "name": "再见",
    "qsid": "39",
    "id": "zaijian"
  },
  {
    "name": "坏笑",
    "qsid": "101",
    "id": "huaixiao"
  },
  {
    "name": "委屈",
    "qsid": "106",
    "id": "weiqu"
  },
  {
    "name": "快哭了",
    "qsid": "107",
    "id": "kuaikule"
  },
  {
    "name": "阴险",
    "qsid": "108",
    "id": "yinxian"
  },
  {
    "name": "吓",
    "qsid": "110",
    "id": "xia"
  },
  {
    "name": "可怜",
    "qsid": "111",
    "id": "kelian"
  },
  {
    "name": "吃瓜",
    "qsid": "271",
    "id": "chigua"
  },
  {
    "name": "捂脸",
    "qsid": "264",
    "id": "wulian"
  },
  {
    "name": "点赞",
    "qsid": "201",
    "id": "dianzan"
  },
  {
    "name": "托腮",
    "qsid": "212",
    "id": "tuosai"
  },
  {
    "name": "抱拳",
    "qsid": "118",
    "id": "baoquan"
  },
  {
    "name": "勾引",
    "qsid": "119",
    "id": "gouyin"
  },
  {
    "name": "拳头",
    "qsid": "120",
    "id": "quantou"
  },
  {
    "name": "菜刀",
    "qsid": "112",
    "id": "caidao"
  },
  {
    "name": "啤酒",
    "qsid": "113",
    "id": "pijiu"
  },
  {
    "name": "心碎",
    "qsid": "67",
    "id": "xinsui"
  },
  {
    "name": "咖啡",
    "qsid": "60",
    "id": "kafei"
  },
  {
    "name": "便便",
    "qsid": "59",
    "id": "bianbian"
  },
  {
    "name": "惊讶",
    "qsid": "0",
    "id": "jingya"
  },
  {
    "name": "撇嘴",
    "qsid": "1",
    "id": "piezui"
  },
  {
    "name": "色",
    "qsid": "2",
    "id": "se"
  },
  {
    "name": "爱情",
    "qsid": "42",
    "id": "aiqing"
  },
  {
    "name": "跳跳",
    "qsid": "43",
    "id": "tiaotiao"
  },
  {
    "name": "拥抱",
    "qsid": "49",
    "id": "yongbao"
  },
  {
    "name": "蛋糕",
    "qsid": "53",
    "id": "dangao"
  },
  {
    "name": "刀",
    "qsid": "56",
    "id": "dao"
  },
  {
    "name": "足球",
    "qsid": "57",
    "id": "zuqiu"
  },
  {
    "name": "赞",
    "qsid": "76",
    "id": "zan"
  },
  {
    "name": "踩",
    "qsid": "77",
    "id": "cai"
  },
  {
    "name": "握手",
    "qsid": "78",
    "id": "woshou"
  },
  {
    "name": "胜利",
    "qsid": "79",
    "id": "shengli"
  },
  {
    "name": "怄火",
    "qsid": "86",
    "id": "ouhuo"
  },
  {
    "name": "冷汗",
    "qsid": "96",
    "id": "lenghan"
  },
  {
    "name": "擦汗",
    "qsid": "97",
    "id": "cahan"
  },
  {
    "name": "抠鼻",
    "qsid": "98",
    "id": "koubi"
  },
  {
    "name": "鼓掌",
    "qsid": "99",
    "id": "guzhang"
  },
  {
    "name": "糗大了",
    "qsid": "100",
    "id": "qiudale"
  },
  {
    "name": "左哼哼",
    "qsid": "102",
    "id": "zuohengheng"
  },
  {
    "name": "右哼哼",
    "qsid": "103",
    "id": "youhengheng"
  },
  {
    "name": "哈欠",
    "qsid": "104",
    "id": "haqian"
  },
  {
    "name": "鄙视",
    "qsid": "105",
    "id": "bishi"
  },
  {
    "name": "左亲亲",
    "qsid": "109",
    "id": "zuoqinqin"
  },
  {
    "name": "篮球",
    "qsid": "114",
    "id": "lanqiu"
  },
  {
    "name": "乒乓",
    "qsid": "115",
    "id": "pingpang"
  },
  {
    "name": "示爱",
    "qsid": "116",
    "id": "shiai"
  },
  {
    "name": "瓢虫",
    "qsid": "117",
    "id": "piaochong"
  },
  {
    "name": "差劲",
    "qsid": "121",
    "id": "chajin"
  },
  {
    "name": "爱你",
    "qsid": "122",
    "id": "aini"
  },
  {
    "name": "NO",
    "qsid": "123",
    "id": "no"
  },
  {
    "name": "转圈",
    "qsid": "125",
    "id": "zhuanquan"
  },
  {
    "name": "磕头",
    "qsid": "126",
    "id": "ketou"
  },
  {
    "name": "回头",
    "qsid": "127",
    "id": "huitou"
  },
  {
    "name": "跳绳",
    "qsid": "128",
    "id": "tiaosheng"
  },
  {
    "name": "挥手",
    "qsid": "129",
    "id": "huishou"
  },
  {
    "name": "激动",
    "qsid": "130",
    "id": "jidong"
  },
  {
    "name": "街舞",
    "qsid": "131",
    "id": "jiewu"
  },
  {
    "name": "献吻",
    "qsid": "132",
    "id": "xianwen"
  },
  {
    "name": "左太极",
    "qsid": "133",
    "id": "zuotaiji"
  },
  {
    "name": "右太极",
    "qsid": "134",
    "id": "youtaiji"
  },
  {
    "name": "茶",
    "qsid": "171",
    "id": "cha"
  },
  {
    "name": "眨眼睛",
    "qsid": "172",
    "id": "zhayanjing"
  },
  {
    "name": "泪奔",
    "qsid": "173",
    "id": "leiben"
  },
  {
    "name": "无奈",
    "qsid": "174",
    "id": "wunai"
  },
  {
    "name": "卖萌",
    "qsid": "175",
    "id": "maimeng"
  },
  {
    "name": "小纠结",
    "qsid": "176",
    "id": "xiaojiujie"
  },
  {
    "name": "喷血",
    "qsid": "177",
    "id": "penxie"
  },
  {
    "name": "斜眼笑",
    "qsid": "178",
    "id": "xieyanxiao"
  },
  {
    "name": "doge",
    "qsid": "179",
    "id": "doge"
  },
  {
    "name": "惊喜",
    "qsid": "180",
    "id": "jingxi"
  },
  {
    "name": "骚扰",
    "qsid": "181",
    "id": "saorao"
  },
  {
    "name": "笑哭",
    "qsid": "182",
    "id": "xiaoku"
  },
  {
    "name": "我最美",
    "qsid": "183",
    "id": "wozuimei"
  },
  {
    "name": "河蟹",
    "qsid": "184",
    "id": "hexie"
  },
  {
    "name": "羊驼",
    "qsid": "185",
    "id": "yangtuo"
  },
  {
    "name": "幽灵",
    "qsid": "187",
    "id": "youling"
  },
  {
    "name": "蛋",
    "qsid": "188",
    "id": "dan"
  },
  {
    "name": "菊花",
    "qsid": "190",
    "id": "juhua"
  },
  {
    "name": "红包",
    "qsid": "192",
    "id": "hongbao"
  },
  {
    "name": "不开心",
    "qsid": "194",
    "id": "bukaixin"
  },
  {
    "name": "冷漠",
    "qsid": "197",
    "id": "lengmo"
  },
  {
    "name": "呃",
    "qsid": "198",
    "id": "e"
  },
  {
    "name": "好棒",
    "qsid": "199",
    "id": "haobang"
  },
  {
    "name": "拜托",
    "qsid": "200",
    "id": "baituo"
  },
  {
    "name": "无聊",
    "qsid": "202",
    "id": "wuliao"
  },
  {
    "name": "托脸",
    "qsid": "203",
    "id": "tuolian"
  },
  {
    "name": "吃",
    "qsid": "204",
    "id": "chi"
  },
  {
    "name": "送花",
    "qsid": "205",
    "id": "songhua"
  },
  {
    "name": "害怕",
    "qsid": "206",
    "id": "haipa"
  },
  {
    "name": "花痴",
    "qsid": "207",
    "id": "huachi"
  },
  {
    "name": "小样儿",
    "qsid": "208",
    "id": "xiaoyanger"
  },
  {
    "name": "飙泪",
    "qsid": "210",
    "id": "biaolei"
  },
  {
    "name": "我不看",
    "qsid": "211",
    "id": "wobukan"
  },
  {
    "name": "啵啵",
    "qsid": "214",
    "id": "bobo"
  },
  {
    "name": "糊脸",
    "qsid": "215",
    "id": "hulian"
  },
  {
    "name": "拍头",
    "qsid": "216",
    "id": "paitou"
  },
  {
    "name": "扯一扯",
    "qsid": "217",
    "id": "cheyiche"
  },
  {
    "name": "舔一舔",
    "qsid": "218",
    "id": "tianyitian"
  },
  {
    "name": "蹭一蹭",
    "qsid": "219",
    "id": "cengyiceng"
  },
  {
    "name": "拽炸天",
    "qsid": "220",
    "id": "zhuaizhatian"
  },
  {
    "name": "顶呱呱",
    "qsid": "221",
    "id": "dingguagua"
  },
  {
    "name": "抱抱",
    "qsid": "222",
    "id": "baobao"
  },
  {
    "name": "暴击",
    "qsid": "223",
    "id": "baoji"
  },
  {
    "name": "开枪",
    "qsid": "224",
    "id": "kaiqiang"
  },
  {
    "name": "撩一撩",
    "qsid": "225",
    "id": "liaoyiliao"
  },
  {
    "name": "拍桌",
    "qsid": "226",
    "id": "paizhuo"
  },
  {
    "name": "拍手",
    "qsid": "227",
    "id": "paishou"
  },
  {
    "name": "恭喜",
    "qsid": "228",
    "id": "gongxi"
  },
  {
    "name": "干杯",
    "qsid": "229",
    "id": "ganbei"
  },
  {
    "name": "嘲讽",
    "qsid": "230",
    "id": "chaofeng"
  },
  {
    "name": "哼",
    "qsid": "231",
    "id": "heng"
  },
  {
    "name": "佛系",
    "qsid": "232",
    "id": "foxi"
  },
  {
    "name": "掐一掐",
    "qsid": "233",
    "id": "qiayiqia"
  },
  {
    "name": "惊呆",
    "qsid": "234",
    "id": "jingdai"
  },
  {
    "name": "颤抖",
    "qsid": "235",
    "id": "chandou"
  },
  {
    "name": "啃头",
    "qsid": "236",
    "id": "kentou"
  },
  {
    "name": "偷看",
    "qsid": "237",
    "id": "toukan"
  },
  {
    "name": "扇脸",
    "qsid": "238",
    "id": "shanlian"
  },
  {
    "name": "原谅",
    "qsid": "239",
    "id": "yuanliang"
  },
  {
    "name": "喷脸",
    "qsid": "240",
    "id": "penlian"
  },
  {
    "name": "生日快乐",
    "qsid": "241",
    "id": "shengrikuaile"
  },
  {
    "name": "头撞击",
    "qsid": "242",
    "id": "touzhuangji"
  },
  {
    "name": "甩头",
    "qsid": "243",
    "id": "shuaitou"
  },
  {
    "name": "扔狗",
    "qsid": "244",
    "id": "renggou"
  },
  {
    "name": "加油必胜",
    "qsid": "245",
    "id": "jiayoubisheng"
  },
  {
    "name": "加油抱抱",
    "qsid": "246",
    "id": "jiayoubaobao"
  },
  {
    "name": "忙到飞起",
    "qsid": "261",
    "id": "mangdaofeiqi"
  },
  {
    "name": "脑阔疼",
    "qsid": "262",
    "id": "naokuoteng"
  },
  {
    "name": "沧桑",
    "qsid": "263",
    "id": "cangsang"
  },
  {
    "name": "辣眼睛",
    "qsid": "265",
    "id": "layanjing"
  },
  {
    "name": "哦哟",
    "qsid": "266",
    "id": "oyo"
  },
  {
    "name": "头秃",
    "qsid": "267",
    "id": "toutu"
  },
  {
    "name": "问号脸",
    "qsid": "268",
    "id": "wenhaolian"
  },
  {
    "name": "暗中观察",
    "qsid": "269",
    "id": "anzhongguancha"
  },
  {
    "name": "emm",
    "qsid": "270",
    "id": "emm"
  },
  {
    "name": "呵呵哒",
    "qsid": "272",
    "id": "heheda"
  },
  {
    "name": "汗",
    "qsid": "278",
    "id": "han"
  },
  {
    "name": "打脸",
    "qsid": "279",
    "id": "dalian"
  },
  {
    "name": "击掌",
    "qsid": "280",
    "id": "jizhang"
  },
  {
    "name": "无眼笑",
    "qsid": "281",
    "id": "wuyanxiao"
  },
  {
    "name": "敬礼",
    "qsid": "282",
    "id": "jingli"
  },
  {
    "name": "狂笑",
    "qsid": "283",
    "id": "kuangxiao"
  },
  {
    "name": "面无表情",
    "qsid": "284",
    "id": "mianwubiaoqing"
  },
  {
    "name": "摸鱼",
    "qsid": "285",
    "id": "moyu"
  },
  {
    "name": "魔鬼笑",
    "qsid": "286",
    "id": "moguixiao"
  },
  {
    "name": "哦",
    "qsid": "287",
    "id": "o"
  },
  {
    "name": "请",
    "qsid": "288",
    "id": "qing"
  },
  {
    "name": "睁眼",
    "qsid": "289",
    "id": "zhengyan"
  },
  {
    "name": "敲开心",
    "qsid": "290",
    "id": "qiaokaixin"
  },
  {
    "name": "震惊",
    "qsid": "291",
    "id": "zhenjing"
  },
  {
    "name": "让我康康",
    "qsid": "292",
    "id": "rangwokangkang"
  },
  {
    "name": "摸锦鲤",
    "qsid": "293",
    "id": "mojinli"
  },
  {
    "name": "期待",
    "qsid": "294",
    "id": "qidai"
  },
  {
    "name": "拿到红包",
    "qsid": "295",
    "id": "nadaohongbao"
  },
  {
    "name": "真好",
    "qsid": "296",
    "id": "zhenhao"
  },
  {
    "name": "拜谢",
    "qsid": "297",
    "id": "baixie"
  },
  {
    "name": "元宝",
    "qsid": "298",
    "id": "yuanbao"
  },
  {
    "name": "牛啊",
    "qsid": "299",
    "id": "niua"
  },
  {
    "name": "胖三斤",
    "qsid": "300",
    "id": "pangsanjin"
  },
  {
    "name": "好闪",
    "qsid": "301",
    "id": "haoshan"
  },
  {
    "name": "左拜年",
    "qsid": "302",
    "id": "zuobainian"
  },
  {
    "name": "右拜年",
    "qsid": "303",
    "id": "youbainian"
  },
  {
    "name": "红包包",
    "qsid": "304",
    "id": "hongbaobao"
  },
  {
    "name": "右亲亲",
    "qsid": "305",
    "id": "youqinqin"
  },
  {
    "name": "求红包",
    "qsid": "308",
    "id": "qiuhongbao"
  },
  {
    "name": "谢红包",
    "qsid": "309",
    "id": "xiehongbao"
  },
  {
    "name": "新年烟花",
    "qsid": "310",
    "id": "xinnianyanhua"
  },
  {
    "name": "打call",
    "qsid": "311",
    "id": "dacall"
  },
  {
    "name": "变形",
    "qsid": "312",
    "id": "bianxing"
  },
  {
    "name": "嗑到了",
    "qsid": "313",
    "id": "kedaole"
  },
  {
    "name": "仔细分析",
    "qsid": "314",
    "id": "zixifenxi"
  },
  {
    "name": "加油",
    "qsid": "315",
    "id": "jiayou"
  },
  {
    "name": "我没事",
    "qsid": "316",
    "id": "womeishi"
  }
] as const

export const QQ_EMOJI_MAP: ReadonlyMap<string, QqEmojiMeta> = new Map(
  QQ_EMOJI_LIST.map((item) => [item.name, item])
)

export function isQqEmoji(name: string): boolean {
  return QQ_EMOJI_MAP.has(name)
}

/**
 * 获取指定表情的双源分发地址
 */
export function getQqEmojiUrls(qsid: string): { cfUrl: string; localUrl: string } {
  return {
    cfUrl: `${CF_EMOTE_BASE_URL}/s${qsid}.gif`,
    localUrl: `${LOCAL_EMOTE_BASE_URL}/s${qsid}.gif`,
  }
}
