#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""
自动化功法秘籍与残卷合成工具
基于《道化太初》原版线装古籍与撕裂卷轴高清母版，
融合 MOSS-Image 神经网络生成的纯白二值镂空矢量 Logo，
彻底剥离所有黑色杂质（内部镂空完全透明透底），
并顺应书面与卷轴微斜透视进行自然倾斜，
产出 100% 像素级统一、灵动自然的 64x64 WebP 秘籍与残卷图标。
"""

import os
import sys
import numpy as np
from PIL import Image

REPO_ROOT = os.path.abspath(os.path.join(os.path.dirname(__file__), "..", "..", "..", ".."))
LEGACY_DIR = os.path.join(REPO_ROOT, "art", "legacy_128")
EMBLEMS_DIR = os.path.join(REPO_ROOT, "art", "emblems")
OUTPUT_DIR = os.path.join(REPO_ROOT, "frontend", "src", "assets", "items")

# 功法配置: (slug, 名称, 封皮RGB颜色, 封面Logo自然微倾斜角度度数)
INTERNAL_CONFIGS = [
    ("tuna-gong", "《吐纳功秘籍》", (26, 68, 115), -6),
    ("manniu-jin", "《蛮牛劲秘籍》", (105, 52, 32), -8),
    ("tiebu-shan", "《铁布衫秘籍》", (62, 68, 76), -6),
    ("feiyan-gong", "《飞燕功秘籍》", (28, 92, 65), -11),
    ("qingxin-jue", "《清心诀秘籍》", (35, 95, 125), -6),
    ("fenxue-gong", "《焚血功秘籍》", (115, 30, 28), -9),
    ("xiaozhoutian-gong", "《小周天功秘籍》", (155, 110, 25), -6),
    ("cunshen-jue", "《存神诀秘籍》", (85, 45, 115), -7),
    ("lianjing-huaqi-jue", "《炼精化气诀秘籍》", (25, 90, 88), -6),
    ("xuanyuan-yiqi-gong", "《玄元一气功秘籍》", (32, 36, 42), -7),
    ("ganlu", "《甘露秘籍》", (40, 108, 138), -8),
    ("tide", "《潮汐秘籍》", (22, 85, 130), -7),
    ("sui-gang", "《碎罡秘籍》", (120, 38, 30), -8),
]

# 原版同款带棕带黄的宣纸象牙古色 (暖黄米棕 #edd8b5: RGB 237, 216, 181)
IVORY_BUFF = np.array([237, 216, 181], dtype=np.uint8)

def synthesize_all():
    os.makedirs(OUTPUT_DIR, exist_ok=True)
    art_items_dir = os.path.join(REPO_ROOT, "art", "items")
    os.makedirs(art_items_dir, exist_ok=True)
    
    # 1. 线装书母模
    src_tome = Image.open(os.path.join(LEGACY_DIR, "item.tome.heavy-slash.png")).convert("RGBA")
    arr_tome = np.array(src_tome)
    
    cover_mask = np.zeros(arr_tome.shape[:2], dtype=bool)
    cover_mask[18:110, 38:105] = True
    sword_mask = cover_mask & (arr_tome[:, :, 0] > 130) & (arr_tome[:, :, 1] > 120) & (arr_tome[:, :, 2] > 90)
    base_cover_color = arr_tome[95, 50, :4]
    clean_tome_arr = arr_tome.copy()
    clean_tome_arr[sword_mask] = base_cover_color
    
    r, g, b, a = clean_tome_arr[:, :, 0], clean_tome_arr[:, :, 1], clean_tome_arr[:, :, 2], clean_tome_arr[:, :, 3]
    is_cover = (a > 200) & (b.astype(int) - r.astype(int) > 15) & (r < 120) & (g < 130)
    orig_mean = np.mean([28, 66, 109])
    
    # 2. 残卷母模
    src_frag = Image.open(os.path.join(LEGACY_DIR, "item.fragment.heavy-slash.png")).convert("RGBA")
    arr_frag = np.array(src_frag)
    
    frag_center_mask = np.zeros(arr_frag.shape[:2], dtype=bool)
    frag_center_mask[20:110, 20:85] = True
    f_sword_mask = frag_center_mask & (arr_frag[:, :, 0] > 130) & (arr_frag[:, :, 1] > 120) & (arr_frag[:, :, 2] > 90)
    base_frag_color = arr_frag[95, 45, :4]
    clean_frag_arr = arr_frag.copy()
    clean_frag_arr[f_sword_mask] = base_frag_color
    
    rf, gf, bf, af = clean_frag_arr[:, :, 0], clean_frag_arr[:, :, 1], clean_frag_arr[:, :, 2], clean_frag_arr[:, :, 3]
    is_frag_body = (af > 200) & (bf.astype(int) - rf.astype(int) > 10) & (rf < 120)
    
    count = 0
    for slug, name, target_color, tilt_angle in INTERNAL_CONFIGS:
        # A. 合成秘籍
        book_arr = clean_tome_arr.copy()
        tr, tg, tb = target_color
        lum = (r.astype(float) + g.astype(float) + b.astype(float)) / (3.0 * orig_mean)
        
        book_arr[is_cover, 0] = np.clip(tr * lum[is_cover], 0, 255).astype(np.uint8)
        book_arr[is_cover, 1] = np.clip(tg * lum[is_cover], 0, 255).astype(np.uint8)
        book_arr[is_cover, 2] = np.clip(tb * lum[is_cover], 0, 255).astype(np.uint8)
        book_img = Image.fromarray(book_arr)
        
        # 读取 MOSS-Image 矢量图腾
        emblem_path = os.path.join(EMBLEMS_DIR, f"emblem.{slug}.webp")
        if not os.path.exists(emblem_path):
            print(f"Warning: Emblem not found: {emblem_path}")
            continue
            
        emblem_raw = Image.open(emblem_path).convert("RGBA")
        earr = np.array(emblem_raw)
        
        # 彻底去除所有黑色/暗色像素 (亮度<90或透明的全部变为0透明，绝不留任何黑边或黑底)
        lum_e = (earr[:, :, 0].astype(int) + earr[:, :, 1].astype(int) + earr[:, :, 2].astype(int)) // 3
        is_white = (earr[:, :, 3] > 40) & (lum_e >= 90)
        clean_arr = np.zeros_like(earr)
        clean_arr[is_white, :3] = IVORY_BUFF
        clean_arr[is_white, 3] = 255
        clean_emblem = Image.fromarray(clean_arr)
        # 覆写保存母版，使 art/emblems/ 里的原版也完全纯净
        clean_emblem.save(emblem_path, format="WEBP", lossless=True)
        
        ebbox = clean_emblem.getbbox()
        emblem_crop = clean_emblem.crop(ebbox) if ebbox else clean_emblem
        
        # 缩放至书籍封面最佳比例 (54x54)
        cw, ch = emblem_crop.size
        scale_tome = 54.0 / max(cw, ch)
        nw, nh = max(1, int(cw * scale_tome)), max(1, int(ch * scale_tome))
        emblem_tome = emblem_crop.resize((nw, nh), Image.Resampling.LANCZOS)
        
        # 贴合书本透视进行微幅倾斜旋转
        emblem_rotated = emblem_tome.rotate(tilt_angle, resample=Image.Resampling.BICUBIC, expand=True)
        
        # 旋转插值保护：确保边缘只有纯白和完全透明，绝无任何黑边/灰边
        rarr = np.array(emblem_rotated)
        rmask = rarr[:, :, 3] > 60
        rarr[rmask, :3] = IVORY_BUFF
        rarr[rmask, 3] = 255
        rarr[~rmask] = [0, 0, 0, 0]
        emblem_final = Image.fromarray(rarr)
        
        # 居中贴在封面 (中心点 x=72, y=60)，不加任何人工黑描边，镂空透底纯净通透
        rw, rh = emblem_final.size
        pos_x = 72 - rw // 2
        pos_y = 60 - rh // 2
        
        book_img.paste(emblem_final, (pos_x, pos_y), emblem_final)
        tome_48 = book_img.resize((48, 48), Image.Resampling.LANCZOS)
        out_tome = os.path.join(OUTPUT_DIR, f"item.tome.{slug}.webp")
        tome_48.save(out_tome, format="WEBP", quality=90)
        book_img.save(os.path.join(art_items_dir, f"item.tome.{slug}.webp"), format="WEBP", lossless=True)
        
        # B. 合成残卷
        frag_arr = clean_frag_arr.copy()
        lum_f = (rf.astype(float) + gf.astype(float) + bf.astype(float)) / (3.0 * orig_mean)
        frag_arr[is_frag_body, 0] = np.clip(tr * lum_f[is_frag_body] * 0.95, 0, 255).astype(np.uint8)
        frag_arr[is_frag_body, 1] = np.clip(tg * lum_f[is_frag_body] * 0.95, 0, 255).astype(np.uint8)
        frag_arr[is_frag_body, 2] = np.clip(tb * lum_f[is_frag_body] * 0.95, 0, 255).astype(np.uint8)
        frag_img = Image.fromarray(frag_arr)
        
        scale_frag = 46.0 / max(cw, ch)
        nfw, nfh = max(1, int(cw * scale_frag)), max(1, int(ch * scale_frag))
        emblem_frag = emblem_crop.resize((nfw, nfh), Image.Resampling.LANCZOS)
        
        # 残卷也顺应微斜
        frag_rotated = emblem_frag.rotate(int(tilt_angle * 0.75), resample=Image.Resampling.BICUBIC, expand=True)
        frarr = np.array(frag_rotated)
        frmask = frarr[:, :, 3] > 60
        frarr[frmask, :3] = IVORY_BUFF
        frarr[frmask, 3] = 255
        frarr[~frmask] = [0, 0, 0, 0]
        emblem_f_final = Image.fromarray(frarr)
        
        frw, frh = emblem_f_final.size
        fpos_x = 52 - frw // 2
        fpos_y = 62 - frh // 2
        
        frag_img.paste(emblem_f_final, (fpos_x, fpos_y), emblem_f_final)
        frag_48 = frag_img.resize((48, 48), Image.Resampling.LANCZOS)
        out_frag = os.path.join(OUTPUT_DIR, f"item.fragment.{slug}.webp")
        frag_48.save(out_frag, format="WEBP", quality=90)
        frag_img.save(os.path.join(art_items_dir, f"item.fragment.{slug}.webp"), format="WEBP", lossless=True)
        
        count += 2
        print(f"[OK] {name} (tilt {tilt_angle} deg) -> item.tome.{slug}.webp & item.fragment.{slug}.webp")
        
    print(f"\nAll {count} items synthesized successfully!")
    return 0

if __name__ == "__main__":
    sys.exit(synthesize_all())
