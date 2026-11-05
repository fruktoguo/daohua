import math
import os
from PIL import Image, ImageDraw, ImageFont, ImageFilter

OUTPUT_DIR = "artifacts/taptap-materials"
os.makedirs(OUTPUT_DIR, exist_ok=True)

W, H = 1080, 1920

FONT_BOLD = r"C:\Windows\Fonts\msyhbd.ttc"
FONT_REGULAR = r"C:\Windows\Fonts\msyh.ttc"
FONT_SERIF = r"C:\Windows\Fonts\simsun.ttc"

def get_font(path, size):
    return ImageFont.truetype(path, size)

def draw_header(draw, title, subtitle):
    # 顶部装饰与主题标语
    draw.text((W/2 - 200, 100), title, fill="#F7E298", font=get_font(FONT_SERIF, 56))
    draw.text((W/2 - 160, 180), subtitle, fill="#A38B4A", font=get_font(FONT_REGULAR, 28))
    # 装饰线
    draw.line([120, 240, W - 120, 240], fill="#3A321E", width=2)
    draw.ellipse([W/2 - 8, 236, W/2 + 8, 244], fill="#D4AF37")

def create_screenshot_1():
    """截图 1: 灵根修真"""
    img = Image.new("RGB", (W, H), "#0b0d13")
    draw = ImageDraw.Draw(img)
    
    # 背景太虚微光
    for r in range(800, 0, -10):
        factor = r / 800
        red = int(11 + 18 * (1 - factor))
        green = int(13 + 24 * (1 - factor))
        blue = int(19 + 36 * (1 - factor))
        draw.ellipse([W/2 - r, 700 - r*0.7, W/2 + r, 700 + r*0.7], fill=(red, green, blue))
        
    draw_header(draw, "修仙悟道 · 灵根筑基", "—— 纯正东方修仙 · 放置挂机历练 ——")
    
    # 角色主卡片 (仿游戏真实界面)
    cx = W / 2
    # 顶部状态条
    draw.rounded_rectangle([80, 280, W - 80, 460], radius=16, fill="#131822", outline="#2F394D", width=2)
    draw.text((120, 310), "道号：太虚道人", fill="#EAEAEA", font=get_font(FONT_BOLD, 36))
    draw.text((120, 370), "境界：筑基期 · 圆满", fill="#F7E298", font=get_font(FONT_BOLD, 32))
    draw.text((540, 310), "气血：12,850 / 12,850", fill="#E06A6A", font=get_font(FONT_REGULAR, 26))
    draw.text((540, 360), "真元：4,600 / 4,600", fill="#6AA7E0", font=get_font(FONT_REGULAR, 26))
    draw.text((540, 410), "寿元：142 / 300 载", fill="#99D98C", font=get_font(FONT_REGULAR, 26))
    
    # 灵根与修行面板
    draw.rounded_rectangle([80, 490, W - 80, 1260], radius=16, fill="#11151F", outline="#283244", width=2)
    draw.text((120, 520), "【 先天五行灵根 】", fill="#D4AF37", font=get_font(FONT_BOLD, 32))
    
    # 五行数值条
    elements = [
        ("金灵根", 86, "#F0D680"),
        ("木灵根", 92, "#80D695"),
        ("水灵根", 78, "#80B3D6"),
        ("火灵根", 64, "#D68080"),
        ("土灵根", 80, "#D6A980")
    ]
    for i, (name, val, col) in enumerate(elements):
        y = 590 + i * 75
        draw.text((130, y), name, fill="#CCCCCC", font=get_font(FONT_REGULAR, 28))
        # 背景进度条
        draw.rounded_rectangle([260, y + 8, W - 240, y + 32], radius=8, fill="#1E2533")
        # 实际进度条
        bar_w = (W - 500) * (val / 100)
        draw.rounded_rectangle([260, y + 8, 260 + bar_w, y + 32], radius=8, fill=col)
        draw.text((W - 200, y), f"{val} 点", fill=col, font=get_font(FONT_BOLD, 26))
        
    # 功法装配展示
    draw.line([120, 1000, W - 120, 1000], fill="#283244", width=2)
    draw.text((120, 1030), "【 运功心法与神通 】", fill="#D4AF37", font=get_font(FONT_BOLD, 32))
    skills = [
        ("玄元一气功", "黄阶上品 · 气血真元+35%"),
        ("奔雷引", "黄阶中品 · 附带雷劫破甲"),
        ("甘露化雨诀", "黄阶上品 · 战中持续愈创"),
        ("破阵子", "黄阶极品 · 贯穿暴击破防")
    ]
    for i, (sk_name, sk_desc) in enumerate(skills):
        sy = 1090 + i * 40
        draw.text((140, sy), f"•  {sk_name}", fill="#F7E298", font=get_font(FONT_BOLD, 26))
        draw.text((360, sy), sk_desc, fill="#8899B0", font=get_font(FONT_REGULAR, 24))

    # 底部挂机行动
    draw.rounded_rectangle([80, 1290, W - 80, 1780], radius=16, fill="#131822", outline="#2F394D", width=2)
    draw.text((120, 1320), "【 历练与打坐队列 】", fill="#D4AF37", font=get_font(FONT_BOLD, 32))
    
    actions = [
        ("吐纳聚气 · 洞府静修", "剩余 124 轮", "修为 +1,840/分  真元充沛", "#80D695"),
        ("万宝楼 · 炼制凝气丹", "剩余 50 轮", "丹道经验 +45  成丹率 95%", "#80B3D6"),
        ("黑风渊 · 讨伐妖猿", "计划 1,000 轮", "掉落：妖兽内丹、千年灵木", "#E0A36A")
    ]
    for i, (act_name, act_status, act_desc, act_color) in enumerate(actions):
        ay = 1380 + i * 125
        draw.rounded_rectangle([110, ay, W - 110, ay + 105], radius=12, fill="#0F131C", outline="#252D3D")
        draw.text((130, ay + 15), act_name, fill="#EAEAEA", font=get_font(FONT_BOLD, 28))
        draw.text((W - 320, ay + 15), act_status, fill=act_color, font=get_font(FONT_BOLD, 26))
        draw.text((130, ay + 60), act_desc, fill="#7A8B9E", font=get_font(FONT_REGULAR, 22))

    filename = f"{OUTPUT_DIR}/screenshot_1_cultivation.png"
    img.save(filename, "PNG", quality=95)
    print(f"Saved: {filename}")

def create_screenshot_2():
    """截图 2: 战斗阵型与协同讨伐"""
    img = Image.new("RGB", (W, H), "#0b0d13")
    draw = ImageDraw.Draw(img)
    
    draw_header(draw, "天地伏魔 · 九宫战阵", "—— 策略构筑 · 多人动态讨伐 ——")
    
    # 战斗顶部对峙卡片
    draw.rounded_rectangle([80, 280, W - 80, 440], radius=16, fill="#151218", outline="#4A2F3D", width=2)
    draw.text((120, 310), "遭遇强敌：【啸月苍狼王 · 首领】", fill="#E86666", font=get_font(FONT_BOLD, 34))
    draw.text((120, 370), "战况：第 3 回合  |  全队威胁度：145% (3人组队)", fill="#C9B46D", font=get_font(FONT_REGULAR, 26))
    
    # 3x3 九宫格战场阵型图
    draw.rounded_rectangle([80, 470, W - 80, 1140], radius=16, fill="#11141D", outline="#283244", width=2)
    draw.text((120, 500), "【 队伍九宫战阵 (我方 3人 vs 敌群 6只) 】", fill="#D4AF37", font=get_font(FONT_BOLD, 30))
    
    grid_start_y = 560
    cell_w, cell_h = 280, 160
    for r in range(3):
        for c in range(3):
            gx = 110 + c * (cell_w + 15)
            gy = grid_start_y + r * (cell_h + 15)
            # 默认空槽位
            draw.rounded_rectangle([gx, gy, gx + cell_w, gy + cell_h], radius=12, fill="#0F131C", outline="#1F2636")
            
    # 填充我方角色与敌方
    # 前排中：铁骨武修 (承伤)
    draw.rounded_rectangle([110 + 295, grid_start_y, 110 + 295 + cell_w, grid_start_y + cell_h], radius=12, fill="#1E2838", outline="#D4AF37", width=2)
    draw.text((110 + 320, grid_start_y + 25), "【主坦】铁甲真君", fill="#F7E298", font=get_font(FONT_BOLD, 26))
    draw.text((110 + 320, grid_start_y + 70), "气血 100% | 护盾 2,400", fill="#80D695", font=get_font(FONT_REGULAR, 20))
    draw.text((110 + 320, grid_start_y + 110), "状态：[金刚不坏] [嘲讽]", fill="#D6A980", font=get_font(FONT_REGULAR, 20))
    
    # 中排左：输出飞剑
    draw.rounded_rectangle([110, grid_start_y + 175, 110 + cell_w, grid_start_y + 175 + cell_h], radius=12, fill="#1E2838", outline="#6AA7E0", width=2)
    draw.text((130, grid_start_y + 200), "【剑修】青锋道人", fill="#EAEAEA", font=get_font(FONT_BOLD, 26))
    draw.text((130, grid_start_y + 245), "气血 92% | 真元 78%", fill="#80B3D6", font=get_font(FONT_REGULAR, 20))
    draw.text((130, grid_start_y + 285), "蓄势：[天罡剑阵 Lv.3]", fill="#C9B46D", font=get_font(FONT_REGULAR, 20))

    # 后排右：辅助甘露
    draw.rounded_rectangle([110 + 590, grid_start_y + 350, 110 + 590 + cell_w, grid_start_y + 350 + cell_h], radius=12, fill="#1E2838", outline="#80D695", width=2)
    draw.text((110 + 610, grid_start_y + 375), "【医修】素问仙子", fill="#EAEAEA", font=get_font(FONT_BOLD, 26))
    draw.text((110 + 610, grid_start_y + 420), "气血 100% | 真元 85%", fill="#80D695", font=get_font(FONT_REGULAR, 20))
    draw.text((110 + 610, grid_start_y + 460), "释法：[甘露生肌]", fill="#80D695", font=get_font(FONT_REGULAR, 20))

    # 战斗播报流 (Combat Log)
    draw.rounded_rectangle([80, 1170, W - 80, 1780], radius=16, fill="#121620", outline="#2F394D", width=2)
    draw.text((120, 1200), "【 实时战场决算日志 】", fill="#D4AF37", font=get_font(FONT_BOLD, 30))
    
    logs = [
        ("00:03.2", "【青锋道人】运转《太乙分光剑》，对【苍狼王】造成 4,820 点穿透暴击伤害！", "#EAEAEA"),
        ("00:03.8", "【铁甲真君】施展《嘲讽》，强制吸引前排 3 只妖狼仇恨！", "#F0D680"),
        ("00:04.1", "【苍狼王】爪击命中【铁甲真君】，触发护盾吸收 1,200 伤害，反震 340 点反伤！", "#E0A36A"),
        ("00:04.5", "【素问仙子】挥洒甘露，为全队恢复 1,650 点生命值，净化流血异常！", "#80D695"),
        ("00:05.0", "首领【苍狼王】气血跌破 30%，进入狂暴阶段，威胁度激增！", "#E86666")
    ]
    for i, (t_str, l_msg, l_col) in enumerate(logs):
        ly = 1260 + i * 100
        draw.text((120, ly), f"[{t_str}]", fill="#6A7A90", font=get_font(FONT_REGULAR, 22))
        draw.text((220, ly), l_msg, fill=l_col, font=get_font(FONT_REGULAR, 24))

    filename = f"{OUTPUT_DIR}/screenshot_2_combat.png"
    img.save(filename, "PNG", quality=95)
    print(f"Saved: {filename}")

def create_screenshot_3():
    """截图 3: 天工百艺与装备强化"""
    img = Image.new("RGB", (W, H), "#0b0d13")
    draw = ImageDraw.Draw(img)
    
    draw_header(draw, "天工百艺 · 炼丹铸宝", "—— 自由锻造 · 法宝强化升阶 ——")
    
    # 顶部强化展示主卡片
    draw.rounded_rectangle([80, 280, W - 80, 840], radius=16, fill="#151722", outline="#3A3855", width=2)
    draw.text((120, 310), "【 本命法宝强化炉 】", fill="#D4AF37", font=get_font(FONT_BOLD, 32))
    
    # 强化目标装备
    draw.rounded_rectangle([120, 370, W - 120, 560], radius=12, fill="#0F121B", outline="#D4AF37", width=2)
    draw.text((160, 400), "玄铁重剑  +12  [极品法宝]", fill="#FFDF73", font=get_font(FONT_BOLD, 36))
    draw.text((160, 460), "物理攻击：+1,480 (强化 +840)   穿透率：+24.5%", fill="#EAEAEA", font=get_font(FONT_REGULAR, 26))
    draw.text((160, 505), "专属词条：【破山】暴击时 35% 几率粉碎敌方 50% 护甲，持续 6 秒", fill="#80D695", font=get_font(FONT_REGULAR, 22))
    
    # 强化操作区
    draw.text((140, 600), "强化成功率：65.0%   (注入定灵香 +15% 护符)", fill="#F7E298", font=get_font(FONT_REGULAR, 26))
    draw.text((140, 650), "消耗材料：极品玄铁 x8、地火精魄 x2、灵石 5,000", fill="#A3B4C8", font=get_font(FONT_REGULAR, 24))
    draw.rounded_rectangle([140, 710, W - 140, 800], radius=12, fill="#8A5A1A", outline="#FFDF73", width=2)
    draw.text((W/2 - 120, 735), "立即淬火强化", fill="#FFFFFF", font=get_font(FONT_BOLD, 32))

    # 百艺技艺等级一览
    draw.rounded_rectangle([80, 870, W - 80, 1780], radius=16, fill="#121620", outline="#2F394D", width=2)
    draw.text((120, 900), "【 宗门四大修仙百艺 】", fill="#D4AF37", font=get_font(FONT_BOLD, 32))
    
    arts = [
        ("炼丹技艺 (四阶)", "熟练度：4,820 / 6,000", "可炼制：九转金丹、降尘丹、辟谷散", "#80D695"),
        ("炼器天工 (五阶)", "熟练度：7,200 / 10,000", "可铸造：飞剑、锁子宝甲、踏云靴", "#F0D680"),
        ("符箓大道 (三阶)", "熟练度：2,150 / 3,000", "可绘制：太阴神雷符、神行金刚符", "#80B3D6"),
        ("玄天阵法 (四阶)", "熟练度：5,400 / 6,000", "可布置：天雷轰顶阵、迷踪聚灵阵", "#D680D6")
    ]
    for i, (art_name, art_prog, art_desc, art_col) in enumerate(arts):
        ay = 970 + i * 190
        draw.rounded_rectangle([110, ay, W - 110, ay + 160], radius=12, fill="#0F131C", outline="#252D3D")
        draw.text((130, ay + 20), art_name, fill=art_col, font=get_font(FONT_BOLD, 28))
        draw.text((W - 380, ay + 20), art_prog, fill="#8899B0", font=get_font(FONT_REGULAR, 22))
        draw.text((130, ay + 75), art_desc, fill="#EAEAEA", font=get_font(FONT_REGULAR, 24))
        # 进度小条
        draw.rounded_rectangle([130, ay + 125, W - 130, ay + 140], radius=6, fill="#1E2533")
        draw.rounded_rectangle([130, ay + 125, 130 + (W - 260) * 0.75, ay + 140], radius=6, fill=art_col)

    filename = f"{OUTPUT_DIR}/screenshot_3_artisan.png"
    img.save(filename, "PNG", quality=95)
    print(f"Saved: {filename}")

def create_screenshot_4():
    """截图 4: 坊市自由交易行"""
    img = Image.new("RGB", (W, H), "#0b0d13")
    draw = ImageDraw.Draw(img)
    
    draw_header(draw, "四海互市 · 自由贸易", "—— 实时盘口深度 · 装备道具自由流通 ——")
    
    # 顶部行情综述
    draw.rounded_rectangle([80, 280, W - 80, 420], radius=16, fill="#141824", outline="#2F394D", width=2)
    draw.text((120, 310), "【 万仙大市 · 实时买卖盘口 】", fill="#D4AF37", font=get_font(FONT_BOLD, 32))
    draw.text((120, 365), "标的：九转降尘丹 (四阶突破丹药)   今日均价：3,850 灵石", fill="#EAEAEA", font=get_font(FONT_REGULAR, 26))

    # 深度盘口列表 (买盘 vs 卖盘)
    draw.rounded_rectangle([80, 450, W - 80, 1260], radius=16, fill="#11141D", outline="#283244", width=2)
    draw.text((120, 480), "挂单方向       价格 (灵石)       数量       交易状态", fill="#8899B0", font=get_font(FONT_REGULAR, 24))
    draw.line([110, 520, W - 110, 520], fill="#252D3D", width=2)

    orders = [
        ("卖出 [档五]", "4,200", "15 颗", "挂单中", "#E06A6A"),
        ("卖出 [档四]", "4,100", "8 颗", "挂单中", "#E06A6A"),
        ("卖出 [档三]", "4,050", "20 颗", "挂单中", "#E06A6A"),
        ("卖出 [档二]", "3,980", "5 颗", "挂单中", "#E06A6A"),
        ("卖出 [卖一]", "3,900", "12 颗", "最优卖单", "#FF8888"),
        ("------------------ 实时撮合基准线 3,850 ------------------", "", "", "", "#667788"),
        ("求购 [买一]", "3,820", "10 颗", "最优买单", "#80D695"),
        ("求购 [档二]", "3,750", "30 颗", "挂单中", "#80D695"),
        ("求购 [档三]", "3,700", "50 颗", "挂单中", "#80D695"),
        ("求购 [档四]", "3,650", "100 颗", "挂单中", "#80D695"),
        ("求购 [档五]", "3,500", "200 颗", "挂单中", "#80D695"),
    ]
    for i, (dir_str, price, qty, status, col) in enumerate(orders):
        oy = 545 + i * 62
        if "基准线" in dir_str:
            draw.text((150, oy), dir_str, fill=col, font=get_font(FONT_REGULAR, 20))
        else:
            draw.text((130, oy), dir_str, fill=col, font=get_font(FONT_BOLD, 24))
            draw.text((400, oy), price, fill="#F7E298", font=get_font(FONT_BOLD, 24))
            draw.text((640, oy), qty, fill="#EAEAEA", font=get_font(FONT_REGULAR, 24))
            draw.text((850, oy), status, fill=col, font=get_font(FONT_REGULAR, 22))

    # 底部玩家自身订单与资产
    draw.rounded_rectangle([80, 1290, W - 80, 1780], radius=16, fill="#131822", outline="#2F394D", width=2)
    draw.text((120, 1320), "【 我的活动挂单 (2/5) 】", fill="#D4AF37", font=get_font(FONT_BOLD, 30))
    
    my_orders = [
        ("出售 · 玄铁重剑 +9", "单价：18,000 灵石", "数量：1 件", "可立即撤单"),
        ("求购 · 千年雷击木", "单价：2,500 灵石", "已成交：3/10 根", "撮合中")
    ]
    for i, (title, p_str, q_str, c_str) in enumerate(my_orders):
        my = 1380 + i * 140
        draw.rounded_rectangle([110, my, W - 110, my + 120], radius=12, fill="#0F131C", outline="#252D3D")
        draw.text((130, my + 20), title, fill="#EAEAEA", font=get_font(FONT_BOLD, 26))
        draw.text((130, my + 70), f"{p_str}   |   {q_str}", fill="#8899B0", font=get_font(FONT_REGULAR, 22))
        draw.rounded_rectangle([W - 280, my + 30, W - 140, my + 90], radius=8, fill="#2F394D")
        draw.text((W - 250, my + 45), "撤销挂单", fill="#F7E298", font=get_font(FONT_BOLD, 22))

    filename = f"{OUTPUT_DIR}/screenshot_4_market.png"
    img.save(filename, "PNG", quality=95)
    print(f"Saved: {filename}")

if __name__ == "__main__":
    create_screenshot_1()
    create_screenshot_2()
    create_screenshot_3()
    create_screenshot_4()
