#!/usr/bin/env python3
"""
道化太初 · 极简折面美术全量图标生成器
严格遵守 .agents/skills/planar-art/SKILL.md 规范：
- 画布背景：纯黑色 (#000000)
- 纸板外描边：暖骨白色 (#efe8dc)
- 墨黑隔离槽：纯黑色 (#000000)
- 近黑连续主体：60% ~ 80% (RGB: 22, 24, 28 ~ 38, 42, 48)
- 骨白结构硬边面：12% ~ 28% (RGB: 220, 214, 202 ~ 245, 240, 230)
- 单一高饱和强调色：5% ~ 14% (血赤/乙木翠绿/幽冥海蓝/大日玄金/幽紫)
- 64x64 Lanczos 抗锯齿压制导出为 WebP
"""

import os
from PIL import Image, ImageDraw

DEST_DIR = "frontend/src/assets/items"
SIZE = 512

def create_base_canvas():
    im = Image.new("RGB", (SIZE, SIZE), (0, 0, 0))
    draw = ImageDraw.Draw(im)
    return im, draw

def draw_card_frame(draw, contour, margin=14):
    """绘制外部暖骨白纸板描边与极窄内缩黑色凹槽"""
    # 1. 暖骨白外描边多边形
    draw.polygon(contour, fill=(239, 232, 220))
    
    # 2. 几何向中心内缩构建墨黑凹槽
    center_x = sum(p[0] for p in contour) / len(contour)
    center_y = sum(p[1] for p in contour) / len(contour)
    
    gap_contour = []
    for x, y in contour:
        dx = x - center_x
        dy = y - center_y
        dist = (dx**2 + dy**2)**0.5
        if dist > 0:
            scale = (dist - margin) / dist
            gap_contour.append((center_x + dx * scale, center_y + dy * scale))
        else:
            gap_contour.append((x, y))
            
    draw.polygon(gap_contour, fill=(0, 0, 0))
    return gap_contour

def save_icon(im, item_id):
    os.makedirs(DEST_DIR, exist_ok=True)
    out_path = os.path.join(DEST_DIR, f"{item_id}.webp")
    thumb = im.resize((64, 64), Image.Resampling.LANCZOS)
    thumb.save(out_path, "WEBP", quality=95, method=6)
    print(f"Generated: {item_id} -> {out_path} ({os.path.getsize(out_path)} bytes)")

# =========================================================================
# 1. 副手装备组 (Offhand)
# =========================================================================

def gen_ganlu_flask():
    """甘露瓶 (equipment.ganlu-flask)"""
    im, draw = create_base_canvas()
    # 瓶身外轮廓
    outer = [(256, 45), (310, 80), (300, 160), (370, 260), (385, 380), (310, 465), (202, 465), (127, 380), (142, 260), (212, 160), (202, 80)]
    draw_card_frame(draw, outer, 12)
    
    # 近黑宝瓶身
    draw.polygon([(256, 75), (290, 160), (355, 255), (370, 370), (256, 450), (256, 75)], fill=(30, 36, 44))
    draw.polygon([(256, 75), (256, 450), (142, 370), (157, 255), (222, 160), (256, 75)], fill=(20, 24, 30))
    
    # 骨白瓶口与云耳
    draw.polygon([(215, 65), (297, 65), (280, 110), (232, 110)], fill=(240, 235, 225))
    draw.polygon([(202, 160), (160, 200), (180, 240), (222, 200)], fill=(225, 218, 208))
    draw.polygon([(310, 160), (290, 200), (332, 240), (352, 200)], fill=(235, 230, 220))
    draw.polygon([(256, 420), (295, 455), (217, 455)], fill=(225, 218, 208))
    
    # 幽蓝甘露水核
    draw.polygon([(256, 210), (320, 310), (256, 380), (192, 310)], fill=(6, 182, 212))
    draw.polygon([(256, 210), (320, 310), (256, 380)], fill=(8, 145, 178))
    draw.polygon([(256, 250), (290, 310), (256, 350), (222, 310)], fill=(165, 243, 252))
    save_icon(im, "equipment.ganlu-flask")

def gen_xuantie_dart():
    """赤牙匕 (equipment.xuantie-dart)"""
    im, draw = create_base_canvas()
    # 匕首斜向外轮廓
    outer = [(430, 60), (465, 95), (350, 240), (380, 260), (240, 470), (130, 440), (160, 330), (270, 210), (330, 210)]
    draw_card_frame(draw, outer, 12)
    
    # 近黑赤铜柄
    draw.polygon([(420, 75), (450, 105), (340, 240), (310, 210)], fill=(32, 24, 26))
    draw.polygon([(340, 240), (370, 260), (320, 310), (290, 280)], fill=(44, 30, 32))
    
    # 骨白巨兽弯牙逆刃
    draw.polygon([(320, 260), (340, 320), (240, 450), (180, 420), (160, 340), (270, 220)], fill=(239, 232, 220))
    draw.polygon([(270, 220), (320, 260), (240, 450)], fill=(220, 212, 198))
    
    # 血赤火红破甲刀芒核
    draw.polygon([(260, 280), (295, 330), (235, 410), (200, 360)], fill=(239, 68, 68))
    draw.polygon([(260, 280), (295, 330), (235, 410)], fill=(185, 28, 28))
    draw.polygon([(245, 320), (265, 345), (235, 385), (215, 360)], fill=(254, 202, 202))
    save_icon(im, "equipment.xuantie-dart")

def gen_yaoling_bead():
    """曜灵珠 (equipment.yaoling-bead)"""
    im, draw = create_base_canvas()
    outer = [(256, 40), (330, 80), (430, 150), (430, 280), (370, 390), (256, 470), (142, 390), (82, 280), (82, 150), (182, 80)]
    draw_card_frame(draw, outer, 12)
    
    # 近黑八角曜日托
    draw.polygon([(256, 65), (410, 160), (410, 270), (256, 450), (102, 270), (102, 160)], fill=(26, 28, 32))
    draw.polygon([(256, 65), (410, 160), (410, 270), (256, 450)], fill=(38, 40, 46))
    
    # 骨白尖锐日珥轮廓齿
    rays = [
        [(256, 65), (275, 120), (237, 120)],
        [(410, 215), (355, 200), (355, 230)],
        [(256, 450), (237, 395), (275, 395)],
        [(102, 215), (157, 230), (157, 200)],
        [(365, 110), (335, 155), (310, 130)],
        [(365, 320), (310, 300), (335, 275)],
        [(147, 320), (177, 275), (202, 300)],
        [(147, 110), (202, 130), (177, 155)]
    ]
    for r in rays:
        draw.polygon(r, fill=(240, 235, 225))
        
    # 大日真火金芒耀阳核
    draw.polygon([(256, 150), (340, 215), (256, 280), (172, 215)], fill=(245, 158, 11))
    draw.polygon([(256, 215), (340, 215), (256, 280)], fill=(217, 119, 6))
    draw.polygon([(256, 280), (320, 320), (256, 360), (192, 320)], fill=(245, 158, 11))
    draw.polygon([(256, 180), (300, 215), (256, 250), (212, 215)], fill=(254, 240, 138))
    save_icon(im, "equipment.yaoling-bead")

def gen_xuanjia_guard():
    """赤铜盾 (equipment.xuanjia-guard)"""
    im, draw = create_base_canvas()
    outer = [(256, 45), (420, 100), (455, 300), (360, 440), (256, 475), (152, 440), (57, 300), (92, 100)]
    draw_card_frame(draw, outer, 14)
    
    # 近黑暗赤重装盾甲
    draw.polygon([(256, 65), (400, 115), (435, 290), (345, 425), (256, 455)], fill=(42, 30, 32))
    draw.polygon([(256, 65), (256, 455), (167, 425), (77, 290), (112, 115)], fill=(26, 20, 22))
    
    # 骨白两翼破阵撞角
    draw.polygon([(256, 65), (320, 95), (256, 125), (192, 95)], fill=(240, 235, 225))
    draw.polygon([(112, 115), (155, 160), (95, 220), (77, 175)], fill=(225, 218, 208))
    draw.polygon([(400, 115), (435, 175), (417, 220), (357, 160)], fill=(235, 230, 220))
    draw.polygon([(256, 455), (310, 410), (256, 380), (202, 410)], fill=(225, 218, 208))
    
    # 血赤玄火盾心核
    draw.polygon([(256, 160), (350, 255), (256, 350), (162, 255)], fill=(220, 38, 38))
    draw.polygon([(256, 160), (350, 255), (256, 350)], fill=(153, 27, 27))
    draw.polygon([(256, 210), (300, 255), (256, 300), (212, 255)], fill=(254, 202, 202))
    save_icon(im, "equipment.xuanjia-guard")

def gen_huichun_bell():
    """回春铃 (equipment.huichun-bell)"""
    im, draw = create_base_canvas()
    outer = [(256, 40), (305, 85), (325, 170), (410, 360), (360, 430), (256, 470), (152, 430), (102, 360), (187, 170), (207, 85)]
    draw_card_frame(draw, outer, 12)
    
    # 近黑古铜铃身
    draw.polygon([(256, 95), (310, 170), (390, 350), (340, 415), (256, 430)], fill=(32, 35, 38))
    draw.polygon([(256, 95), (256, 430), (172, 415), (122, 350), (202, 170)], fill=(22, 25, 28))
    
    # 骨白吊纽与铃口云浪
    draw.polygon([(225, 55), (287, 55), (305, 100), (207, 100)], fill=(240, 235, 225))
    draw.polygon([(142, 390), (370, 390), (350, 430), (162, 430)], fill=(225, 218, 208))
    draw.polygon([(256, 430), (275, 460), (237, 460)], fill=(240, 235, 225))
    
    # 碧玉回春生机灵气核
    draw.polygon([(256, 180), (320, 270), (256, 360), (192, 270)], fill=(16, 185, 129))
    draw.polygon([(256, 180), (320, 270), (256, 360)], fill=(5, 150, 105))
    draw.polygon([(256, 225), (288, 270), (256, 315), (224, 270)], fill=(167, 243, 208))
    save_icon(im, "equipment.huichun-bell")

# =========================================================================
# 2. 物理输出防具组 (Armor)
# =========================================================================

def gen_jinfeng_scarf():
    """劲风巾 (equipment.jinfeng-scarf)"""
    im, draw = create_base_canvas()
    outer = [(256, 60), (380, 130), (410, 240), (360, 420), (230, 470), (140, 430), (130, 260), (150, 150)]
    draw_card_frame(draw, outer, 12)
    
    # 近黑头巾主体折面
    draw.polygon([(256, 80), (360, 140), (385, 235), (256, 310)], fill=(32, 36, 42))
    draw.polygon([(256, 80), (256, 310), (150, 240), (165, 150)], fill=(22, 25, 30))
    # 飘带
    draw.polygon([(256, 310), (345, 410), (230, 455), (200, 340)], fill=(26, 30, 36))
    
    # 骨白羽翎硬角骨面
    draw.polygon([(256, 80), (310, 120), (265, 160), (225, 120)], fill=(240, 235, 225))
    draw.polygon([(360, 140), (400, 210), (350, 210)], fill=(225, 218, 208))
    
    # 青苍风息强调色 (导风纹)
    draw.polygon([(220, 175), (292, 175), (256, 245)], fill=(6, 182, 212))
    draw.polygon([(256, 245), (292, 175), (256, 210)], fill=(8, 145, 178))
    draw.polygon([(230, 330), (280, 380), (250, 430), (220, 370)], fill=(6, 182, 212))
    save_icon(im, "equipment.jinfeng-scarf")

def gen_qingpi_armor():
    """轻皮甲 (equipment.qingpi-armor)"""
    im, draw = create_base_canvas()
    outer = [(180, 50), (332, 50), (430, 130), (390, 310), (370, 465), (142, 465), (122, 310), (82, 130)]
    draw_card_frame(draw, outer, 14)
    
    # 近黑轻皮铠身
    draw.polygon([(256, 80), (395, 135), (365, 300), (350, 445), (256, 445)], fill=(34, 38, 44))
    draw.polygon([(256, 80), (256, 445), (162, 445), (147, 300), (117, 135)], fill=(24, 27, 32))
    
    # 骨白护心骨板与斜肋骨甲
    draw.polygon([(215, 65), (297, 65), (320, 120), (256, 150), (192, 120)], fill=(240, 235, 225))
    draw.polygon([(117, 135), (170, 180), (155, 240), (130, 210)], fill=(225, 218, 208))
    draw.polygon([(395, 135), (382, 210), (357, 240), (342, 180)], fill=(235, 230, 220))
    
    # 苍绿游丝风回路
    draw.polygon([(256, 180), (320, 255), (256, 330), (192, 255)], fill=(16, 185, 129))
    draw.polygon([(256, 180), (320, 255), (256, 330)], fill=(5, 150, 105))
    draw.polygon([(256, 225), (285, 255), (256, 285), (227, 255)], fill=(167, 243, 208))
    save_icon(im, "equipment.qingpi-armor")

def gen_jixing_pants():
    """疾行裤 (equipment.jixing-pants)"""
    im, draw = create_base_canvas()
    outer = [(170, 50), (342, 50), (385, 160), (355, 465), (275, 465), (256, 270), (237, 465), (157, 465), (127, 160)]
    draw_card_frame(draw, outer, 12)
    
    # 近黑战裤折面
    draw.polygon([(185, 65), (327, 65), (365, 160), (256, 260), (147, 160)], fill=(32, 36, 42))
    draw.polygon([(256, 260), (365, 160), (340, 445), (275, 445), (256, 270)], fill=(38, 42, 48))
    draw.polygon([(256, 260), (256, 270), (237, 445), (172, 445), (147, 160)], fill=(22, 25, 30))
    
    # 骨白护膝扣面
    draw.polygon([(170, 260), (230, 260), (215, 330), (185, 330)], fill=(235, 230, 220))
    draw.polygon([(282, 260), (342, 260), (327, 330), (297, 330)], fill=(240, 235, 225))
    
    # 青风导流疾行线
    draw.polygon([(185, 350), (215, 350), (200, 430), (185, 430)], fill=(6, 182, 212))
    draw.polygon([(297, 350), (327, 350), (327, 430), (312, 430)], fill=(6, 182, 212))
    save_icon(im, "equipment.jixing-pants")

def gen_zhanfeng_scarf():
    """斩风巾 (equipment.zhanfeng-scarf)"""
    im, draw = create_base_canvas()
    outer = [(256, 45), (390, 110), (425, 230), (370, 430), (256, 470), (142, 430), (87, 230), (122, 110)]
    draw_card_frame(draw, outer, 14)
    
    # 近黑战冠
    draw.polygon([(256, 75), (370, 130), (395, 230), (256, 320)], fill=(36, 34, 32))
    draw.polygon([(256, 75), (256, 320), (117, 230), (142, 130)], fill=(24, 22, 20))
    draw.polygon([(256, 320), (350, 420), (256, 450), (162, 420)], fill=(28, 26, 24))
    
    # 骨白刀芒折角利翎
    draw.polygon([(256, 60), (280, 125), (256, 145), (232, 125)], fill=(245, 240, 230))
    draw.polygon([(117, 130), (170, 160), (130, 230)], fill=(230, 225, 215))
    draw.polygon([(395, 130), (382, 230), (342, 160)], fill=(240, 235, 225))
    
    # 流金刀罡玄金角面
    draw.polygon([(256, 165), (320, 235), (256, 305), (192, 235)], fill=(245, 158, 11))
    draw.polygon([(256, 165), (320, 235), (256, 305)], fill=(217, 119, 6))
    draw.polygon([(256, 205), (285, 235), (256, 265), (227, 235)], fill=(254, 240, 138))
    save_icon(im, "equipment.zhanfeng-scarf")

def gen_liegang_armor():
    """裂罡甲 (equipment.liegang-armor)"""
    im, draw = create_base_canvas()
    outer = [(180, 45), (332, 45), (440, 120), (410, 300), (380, 470), (132, 470), (102, 300), (72, 120)]
    draw_card_frame(draw, outer, 14)
    
    # 近黑破罡战铠
    draw.polygon([(256, 75), (405, 130), (385, 290), (360, 450), (256, 450)], fill=(38, 36, 32))
    draw.polygon([(256, 75), (256, 450), (152, 450), (127, 290), (107, 130)], fill=(26, 24, 20))
    
    # 骨白裂罡肩甲与破罡戟尖
    draw.polygon([(215, 60), (297, 60), (325, 125), (256, 155), (187, 125)], fill=(245, 240, 230))
    draw.polygon([(72, 120), (145, 160), (120, 240), (95, 200)], fill=(230, 225, 215))
    draw.polygon([(440, 120), (417, 200), (392, 240), (367, 160)], fill=(240, 235, 225))
    
    # 玄金破罡晶核
    draw.polygon([(256, 185), (335, 265), (256, 345), (177, 265)], fill=(245, 158, 11))
    draw.polygon([(256, 185), (335, 265), (256, 345)], fill=(217, 119, 6))
    draw.polygon([(256, 230), (290, 265), (256, 300), (222, 265)], fill=(254, 240, 138))
    save_icon(im, "equipment.liegang-armor")

def gen_tafeng_pants():
    """踏风裤 (equipment.tafeng-pants)"""
    im, draw = create_base_canvas()
    outer = [(170, 45), (342, 45), (395, 150), (365, 470), (280, 470), (256, 260), (232, 470), (147, 470), (117, 150)]
    draw_card_frame(draw, outer, 12)
    
    # 近黑踏风战裤
    draw.polygon([(185, 60), (327, 60), (375, 150), (256, 250), (137, 150)], fill=(36, 34, 30))
    draw.polygon([(256, 250), (375, 150), (350, 450), (280, 450), (256, 260)], fill=(42, 38, 34))
    draw.polygon([(256, 250), (256, 260), (232, 450), (162, 450), (137, 150)], fill=(24, 22, 18))
    
    # 骨白破空胫刃板
    draw.polygon([(160, 250), (230, 250), (210, 330), (175, 330)], fill=(235, 230, 220))
    draw.polygon([(282, 250), (352, 250), (337, 330), (302, 330)], fill=(245, 240, 230))
    
    # 玄金破风足芒
    draw.polygon([(175, 350), (210, 350), (195, 440), (175, 440)], fill=(245, 158, 11))
    draw.polygon([(302, 350), (337, 350), (337, 440), (317, 440)], fill=(245, 158, 11))
    save_icon(im, "equipment.tafeng-pants")

# =========================================================================
# 3. 生活技艺专属工具组 (Tools)
# =========================================================================

def gen_tool_herb_hoe():
    """桃木铲 (equipment.tool-herb-hoe)"""
    im, draw = create_base_canvas()
    outer = [(420, 60), (460, 100), (280, 280), (330, 330), (256, 465), (140, 350), (190, 300), (380, 100)]
    draw_card_frame(draw, outer, 12)
    
    # 近黑桃木铲柄
    draw.polygon([(410, 75), (445, 110), (265, 290), (230, 255)], fill=(38, 28, 24))
    
    # 骨白平口掘土铲刃
    draw.polygon([(230, 255), (315, 340), (245, 445), (155, 355)], fill=(240, 235, 225))
    draw.polygon([(230, 255), (315, 340), (245, 445)], fill=(220, 214, 202))
    
    # 草木翠绿灵芽晶核
    draw.polygon([(245, 300), (280, 335), (245, 370), (210, 335)], fill=(16, 185, 129))
    draw.polygon([(245, 300), (280, 335), (245, 370)], fill=(5, 150, 105))
    draw.polygon([(245, 320), (265, 335), (245, 350), (225, 335)], fill=(167, 243, 208))
    save_icon(im, "equipment.tool-herb-hoe")

def gen_tool_prospect_compass():
    """精铁镐 (equipment.tool-prospect-compass)"""
    im, draw = create_base_canvas()
    outer = [(430, 60), (465, 95), (280, 280), (380, 380), (320, 440), (256, 300), (140, 360), (80, 300), (180, 200)]
    draw_card_frame(draw, outer, 12)
    
    # 近黑精铁长柄
    draw.polygon([(420, 75), (450, 105), (245, 310), (215, 280)], fill=(30, 34, 40))
    
    # 骨白双头尖锐矿镐
    draw.polygon([(100, 290), (210, 220), (270, 280), (160, 350)], fill=(240, 235, 225))
    draw.polygon([(270, 280), (360, 370), (310, 420), (220, 330)], fill=(225, 218, 208))
    
    # 玄金矿脉灵晶核
    draw.polygon([(245, 260), (275, 290), (245, 320), (215, 290)], fill=(245, 158, 11))
    draw.polygon([(245, 275), (260, 290), (245, 305), (230, 290)], fill=(254, 240, 138))
    save_icon(im, "equipment.tool-prospect-compass")

def gen_tool_flame_ding():
    """灵火炉 (equipment.tool-flame-ding)"""
    im, draw = create_base_canvas()
    outer = [(256, 40), (340, 90), (410, 220), (390, 400), (330, 465), (256, 440), (182, 465), (122, 400), (102, 220), (172, 90)]
    draw_card_frame(draw, outer, 14)
    
    # 近黑重装丹炉身
    draw.polygon([(256, 80), (390, 210), (370, 390), (256, 420)], fill=(36, 30, 28))
    draw.polygon([(256, 80), (256, 420), (142, 390), (122, 210)], fill=(24, 20, 18))
    
    # 骨白炉盖折角与兽纽三足
    draw.polygon([(215, 60), (297, 60), (330, 115), (182, 115)], fill=(245, 240, 230))
    draw.polygon([(142, 390), (180, 450), (210, 410)], fill=(230, 225, 215))
    draw.polygon([(370, 390), (302, 410), (332, 450)], fill=(240, 235, 225))
    
    # 纯阳真火真红炉膛
    draw.polygon([(256, 175), (325, 255), (256, 335), (187, 255)], fill=(239, 68, 68))
    draw.polygon([(256, 175), (325, 255), (256, 335)], fill=(185, 28, 28))
    draw.polygon([(256, 220), (285, 255), (256, 290), (227, 255)], fill=(254, 202, 202))
    save_icon(im, "equipment.tool-flame-ding")

def gen_tool_forge_hammer():
    """百炼铁钳 (equipment.tool-forge-hammer)"""
    im, draw = create_base_canvas()
    outer = [(420, 50), (460, 90), (280, 270), (340, 330), (240, 470), (130, 410), (190, 310), (250, 310), (370, 110)]
    draw_card_frame(draw, outer, 12)
    
    # 近黑长柄锻钳
    draw.polygon([(410, 65), (445, 100), (265, 280), (230, 245)], fill=(32, 34, 38))
    
    # 骨白咬合平口钳头
    draw.polygon([(240, 280), (320, 330), (230, 450), (150, 400), (180, 320)], fill=(240, 235, 225))
    draw.polygon([(240, 280), (320, 330), (230, 450)], fill=(220, 214, 202))
    
    # 炽热淬火红晶核
    draw.polygon([(230, 320), (265, 355), (230, 390), (195, 355)], fill=(249, 115, 22))
    draw.polygon([(230, 335), (250, 355), (230, 375), (210, 355)], fill=(254, 215, 170))
    save_icon(im, "equipment.tool-forge-hammer")

def gen_tool_talisman_brush():
    """桃木笔 (equipment.tool-talisman-brush)"""
    im, draw = create_base_canvas()
    outer = [(440, 45), (475, 80), (280, 275), (240, 330), (130, 470), (110, 430), (210, 300), (390, 95)]
    draw_card_frame(draw, outer, 12)
    
    # 近黑桃木笔杆
    draw.polygon([(430, 60), (460, 90), (275, 275), (245, 245)], fill=(38, 28, 24))
    
    # 骨白笔斗与束箍
    draw.polygon([(245, 245), (275, 275), (240, 330), (210, 300)], fill=(245, 240, 230))
    
    # 朱砂血红聚灵笔锋
    draw.polygon([(210, 300), (240, 330), (130, 450), (120, 420)], fill=(220, 38, 38))
    draw.polygon([(210, 300), (240, 330), (130, 450)], fill=(153, 27, 27))
    draw.polygon([(180, 340), (200, 360), (145, 430), (135, 410)], fill=(254, 202, 202))
    save_icon(im, "equipment.tool-talisman-brush")

def gen_tool_formation_flag():
    """桃晶旗 (equipment.tool-formation-flag)"""
    im, draw = create_base_canvas()
    outer = [(130, 45), (170, 45), (430, 180), (170, 320), (170, 470), (130, 470)]
    draw_card_frame(draw, outer, 12)
    
    # 近黑旗杆
    draw.polygon([(140, 60), (160, 60), (160, 455), (140, 455)], fill=(34, 36, 40))
    
    # 骨白三角阵旗面
    draw.polygon([(160, 75), (405, 180), (160, 295)], fill=(240, 235, 225))
    draw.polygon([(160, 180), (405, 180), (160, 295)], fill=(220, 214, 202))
    
    # 幽紫星斗阵眼星芒
    draw.polygon([(210, 150), (260, 180), (210, 210), (170, 180)], fill=(168, 85, 247))
    draw.polygon([(210, 150), (260, 180), (210, 210)], fill=(126, 34, 206))
    draw.polygon([(210, 165), (235, 180), (210, 195), (190, 180)], fill=(233, 213, 255))
    save_icon(im, "equipment.tool-formation-flag")

def gen_tool_quench_anvil():
    """淬灵锤 (equipment.tool-quench-anvil)"""
    im, draw = create_base_canvas()
    outer = [(430, 55), (465, 90), (290, 265), (370, 310), (330, 440), (190, 370), (140, 420), (105, 385), (235, 265)]
    draw_card_frame(draw, outer, 12)
    
    # 近黑锤柄
    draw.polygon([(420, 70), (450, 100), (275, 275), (245, 245)], fill=(32, 34, 38))
    
    # 骨白八棱方重锤头
    draw.polygon([(245, 245), (350, 310), (310, 420), (190, 350)], fill=(240, 235, 225))
    draw.polygon([(245, 245), (350, 310), (310, 420)], fill=(220, 214, 202))
    
    # 冰蓝淬火晶核
    draw.polygon([(260, 290), (300, 325), (260, 360), (220, 325)], fill=(56, 189, 248))
    draw.polygon([(260, 290), (300, 325), (260, 360)], fill=(2, 132, 199))
    draw.polygon([(260, 305), (280, 325), (260, 345), (240, 325)], fill=(186, 230, 253))
    save_icon(im, "equipment.tool-quench-anvil")

# =========================================================================
# 4. 道具/符箓/货币组 (Consumables & Currencies)
# =========================================================================

def gen_juling_formation_disk():
    """聚灵阵盘 (item.juling-formation-disk)"""
    im, draw = create_base_canvas()
    outer = [(256, 40), (390, 95), (455, 230), (410, 380), (256, 470), (102, 380), (57, 230), (122, 95)]
    draw_card_frame(draw, outer, 14)
    
    # 近黑八角青石阵盘
    draw.polygon([(256, 65), (375, 115), (435, 235), (395, 370), (256, 450)], fill=(34, 38, 42))
    draw.polygon([(256, 65), (256, 450), (117, 370), (77, 235), (137, 115)], fill=(22, 26, 30))
    
    # 骨白乾坤卦位刻面
    draw.polygon([(256, 75), (275, 120), (237, 120)], fill=(240, 235, 225))
    draw.polygon([(256, 440), (237, 395), (275, 395)], fill=(240, 235, 225))
    draw.polygon([(87, 235), (132, 215), (132, 255)], fill=(225, 218, 208))
    draw.polygon([(425, 235), (380, 215), (380, 255)], fill=(235, 230, 220))
    
    # 碧绿生机聚灵漩涡阵心
    draw.polygon([(256, 170), (330, 245), (256, 320), (182, 245)], fill=(16, 185, 129))
    draw.polygon([(256, 170), (330, 245), (256, 320)], fill=(5, 150, 105))
    draw.polygon([(256, 215), (285, 245), (256, 275), (227, 245)], fill=(167, 243, 208))
    save_icon(im, "item.juling-formation-disk")

def gen_gongde():
    """功德 (item.gongde)"""
    im, draw = create_base_canvas()
    outer = [(256, 40), (380, 90), (455, 215), (435, 360), (320, 465), (192, 465), (77, 360), (57, 215), (132, 90)]
    draw_card_frame(draw, outer, 14)
    
    # 近黑法相金轮
    draw.polygon([(256, 65), (365, 110), (435, 225), (415, 350), (256, 445)], fill=(36, 32, 26))
    draw.polygon([(256, 65), (256, 445), (97, 350), (77, 225), (147, 110)], fill=(24, 20, 16))
    
    # 骨白祥云功德光晕
    clouds = [
        [(256, 75), (275, 120), (237, 120)],
        [(415, 225), (370, 205), (370, 245)],
        [(256, 435), (237, 390), (275, 390)],
        [(97, 225), (142, 205), (142, 245)]
    ]
    for c in clouds:
        draw.polygon(c, fill=(245, 240, 230))
        
    # 大日玄金浩然正气金轮
    draw.polygon([(256, 155), (345, 245), (256, 335), (167, 245)], fill=(245, 158, 11))
    draw.polygon([(256, 155), (345, 245), (256, 335)], fill=(217, 119, 6))
    draw.polygon([(256, 200), (300, 245), (256, 290), (212, 245)], fill=(254, 240, 138))
    save_icon(im, "item.gongde")

def gen_gongde_bound():
    """功德·绑定 (item.gongde-bound)"""
    im, draw = create_base_canvas()
    outer = [(256, 40), (380, 90), (455, 215), (435, 360), (320, 465), (192, 465), (77, 360), (57, 215), (132, 90)]
    draw_card_frame(draw, outer, 14)
    
    # 近黑法相金轮
    draw.polygon([(256, 65), (365, 110), (435, 225), (415, 350), (256, 445)], fill=(36, 32, 26))
    draw.polygon([(256, 65), (256, 445), (97, 350), (77, 225), (147, 110)], fill=(24, 20, 16))
    
    # 骨白金刚锁环与封印锁扣 (代表绑定)
    draw.polygon([(215, 80), (297, 80), (310, 140), (202, 140)], fill=(245, 240, 230))
    draw.polygon([(230, 95), (282, 95), (290, 125), (222, 125)], fill=(0, 0, 0)) # 锁孔
    draw.polygon([(202, 380), (310, 380), (295, 435), (217, 435)], fill=(235, 230, 220))
    
    # 大日玄金金芒
    draw.polygon([(256, 170), (330, 250), (256, 330), (182, 250)], fill=(245, 158, 11))
    draw.polygon([(256, 170), (330, 250), (256, 330)], fill=(217, 119, 6))
    draw.polygon([(256, 215), (290, 250), (256, 285), (222, 250)], fill=(254, 240, 138))
    save_icon(im, "item.gongde-bound")

def main():
    print("=== 开始批量生成极简折面装备与道具图标 ===")
    # 副手
    gen_ganlu_flask()
    gen_xuantie_dart()
    gen_yaoling_bead()
    gen_xuanjia_guard()
    gen_huichun_bell()
    
    # 防具
    gen_jinfeng_scarf()
    gen_qingpi_armor()
    gen_jixing_pants()
    gen_zhanfeng_scarf()
    gen_liegang_armor()
    gen_tafeng_pants()
    
    # 工具
    gen_tool_herb_hoe()
    gen_tool_prospect_compass()
    gen_tool_flame_ding()
    gen_tool_forge_hammer()
    gen_tool_talisman_brush()
    gen_tool_formation_flag()
    gen_tool_quench_anvil()
    
    # 道具与货币
    gen_juling_formation_disk()
    gen_gongde()
    gen_gongde_bound()
    
    print("=== 全量 21 项图标生成完毕！ ===")

if __name__ == "__main__":
    main()
