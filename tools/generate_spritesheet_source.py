#!/usr/bin/env python3
"""
道化太初 · 5x5 仙侠法宝副手与生活道具网格素材大图生成器
直接将 25 个图标绘制并拼接为单张 5x5 网格素材大图 (1280x1280)，
输出至 art/source/仙侠法宝副手与生活道具图标集.png，严格符合游戏现存切图管线。
"""

import os
from PIL import Image
import generate_all_planar_icons as gen

REPO_ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
OUT_SHEET = os.path.join(REPO_ROOT, "art", "source", "仙侠法宝副手与生活道具图标集.png")

GRID_COLS = 5
GRID_ROWS = 5
CELL_SIZE = 256
SHEET_W = GRID_COLS * CELL_SIZE
SHEET_H = GRID_ROWS * CELL_SIZE

# 25 项按 5x5 顺序对应的生成函数与名称
ITEMS_5X5 = [
    # 第 1 行: 副手装备 5 件
    ("兽骨匕", "equipment.fan-tie-dart"),
    ("桃灵珠", "equipment.qingmu-bead"),
    ("甘露瓶", "equipment.ganlu-flask"),
    ("精铁盾", "equipment.tiepi-guard"),
    ("赤牙匕", "equipment.xuantie-dart"),
    
    # 第 2 行: 副手后 3 件 + 头部 2 件
    ("曜灵珠", "equipment.yaoling-bead"),
    ("赤铜盾", "equipment.xuanjia-guard"),
    ("回春铃", "equipment.huichun-bell"),
    ("劲风巾", "equipment.jinfeng-scarf"),
    ("斩风巾", "equipment.zhanfeng-scarf"),
    
    # 第 3 行: 衣服与裤子 4 件 + 桃木铲
    ("轻皮甲", "equipment.qingpi-armor"),
    ("裂罡甲", "equipment.liegang-armor"),
    ("疾行裤", "equipment.jixing-pants"),
    ("踏风裤", "equipment.tafeng-pants"),
    ("桃木铲", "equipment.tool-herb-hoe"),
    
    # 第 4 行: 生活技艺工具 5 件
    ("精铁镐", "equipment.tool-prospect-compass"),
    ("灵火炉", "equipment.tool-flame-ding"),
    ("百炼铁钳", "equipment.tool-forge-hammer"),
    ("桃木笔", "equipment.tool-talisman-brush"),
    ("桃晶旗", "equipment.tool-formation-flag"),
    
    # 第 5 行: 工具 1 件 + 符箓阵盘货币 4 件
    ("淬灵锤", "equipment.tool-quench-anvil"),
    ("雷击符", "item.leiji-talisman"),
    ("聚灵阵盘", "item.juling-formation-disk"),
    ("功德", "item.gongde"),
    ("功德·绑定", "item.gongde-bound")
]

def main():
    print(f"=== 开始合成 5x5 网格素材大图 ({SHEET_W}x{SHEET_H}) ===")
    sheet_im = Image.new("RGBA", (SHEET_W, SHEET_H), (0, 0, 0, 0))
    
    # 首先确保 21 个图标都由生成器生成到了内存或本地
    gen.main()
    
    items_dir = os.path.join(REPO_ROOT, "frontend", "src", "assets", "items")
    brain_dir = r"C:\Users\YuoHira\.gemini\antigravity\brain\1ac47f80-6150-43ea-b494-c9c688aaeb70"
    
    for idx, (name, item_id) in enumerate(ITEMS_5X5):
        c = idx % GRID_COLS
        r = idx // GRID_COLS
        pos_x = c * CELL_SIZE
        pos_y = r * CELL_SIZE
        
        # 寻找最高清的原图
        icon_path = os.path.join(items_dir, f"{item_id}.webp")
        # 如果是 AI 生成的 1024 高清原图优先使用
        ai_map = {
            "equipment.tiepi-guard": os.path.join(brain_dir, "equipment_tiepi_guard_1792065264587.jpg"),
            "equipment.fan-tie-dart": os.path.join(brain_dir, "equipment_fan_tie_dart_1792065817572.jpg"),
            "item.leiji-talisman": os.path.join(brain_dir, "item_leiji_talisman_1792065814821.jpg")
        }
        
        src_file = ai_map.get(item_id, icon_path)
        if not os.path.exists(src_file):
            print(f"[WARN] 找不到图标源: {item_id}")
            continue
            
        with Image.open(src_file) as cell_img:
            # 缩放到单元格尺寸 (例如 240x240 并居中留 8px pad)
            pad = 8
            inner_size = CELL_SIZE - pad * 2
            cell_resized = cell_img.convert("RGBA").resize((inner_size, inner_size), Image.Resampling.LANCZOS)
            sheet_im.paste(cell_resized, (pos_x + pad, pos_y + pad))
            print(f"  [{idx+1:02d}/25] 放置格 [{r},{c}]: {name:8s} ({item_id})")
            
    os.makedirs(os.path.dirname(OUT_SHEET), exist_ok=True)
    sheet_im.save(OUT_SHEET, "PNG", optimize=True)
    print(f"\n=== 5x5 网格素材大图已保存至: {OUT_SHEET} ({os.path.getsize(OUT_SHEET)} bytes) ===")

if __name__ == "__main__":
    main()
