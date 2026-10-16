#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""
自动化功法秘籍与残卷合成工具
基于《道化太初》原版线装古籍与撕裂卷轴高清母版，
融合 MOSS-Image 神经网络生成的象牙米白手绘简笔画图腾，
自动化产出 100% 像素级统一、具古朴手绘质感的 64x64 WebP 秘籍与残卷图标。
"""

import os
import sys
import numpy as np
from PIL import Image, ImageFilter

REPO_ROOT = os.path.abspath(os.path.join(os.path.dirname(__file__), "..", "..", "..", ".."))
LEGACY_DIR = os.path.join(REPO_ROOT, "art", "legacy_128")
EMBLEMS_DIR = os.path.join(REPO_ROOT, "art", "emblems")
OUTPUT_DIR = os.path.join(REPO_ROOT, "frontend", "src", "assets", "items")

INTERNAL_CONFIGS = [
    ("tuna-gong", "《吐纳功秘籍》", (26, 68, 115)),
    ("manniu-jin", "《蛮牛劲秘籍》", (105, 52, 32)),
    ("tiebu-shan", "《铁布衫秘籍》", (62, 68, 76)),
    ("feiyan-gong", "《飞燕功秘籍》", (28, 92, 65)),
    ("qingxin-jue", "《清心诀秘籍》", (35, 95, 125)),
    ("fenxue-gong", "《焚血功秘籍》", (115, 30, 28)),
    ("xiaozhoutian-gong", "《小周天功秘籍》", (155, 110, 25)),
    ("cunshen-jue", "《存神诀秘籍》", (85, 45, 115)),
    ("lianjing-huaqi-jue", "《炼精化气诀秘籍》", (25, 90, 88)),
    ("xuanyuan-yiqi-gong", "《玄元一气功秘籍》", (32, 36, 42))
]

def synthesize_all():
    os.makedirs(OUTPUT_DIR, exist_ok=True)
    
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
    for slug, name, target_color in INTERNAL_CONFIGS:
        # A. 合成秘籍
        book_arr = clean_tome_arr.copy()
        tr, tg, tb = target_color
        lum = (r.astype(float) + g.astype(float) + b.astype(float)) / (3.0 * orig_mean)
        
        book_arr[is_cover, 0] = np.clip(tr * lum[is_cover], 0, 255).astype(np.uint8)
        book_arr[is_cover, 1] = np.clip(tg * lum[is_cover], 0, 255).astype(np.uint8)
        book_arr[is_cover, 2] = np.clip(tb * lum[is_cover], 0, 255).astype(np.uint8)
        book_img = Image.fromarray(book_arr)
        
        # 读取 MOSS-Image 简笔画图腾
        emblem_path = os.path.join(EMBLEMS_DIR, f"emblem.{slug}.webp")
        if not os.path.exists(emblem_path):
            print(f"Warning: Emblem not found: {emblem_path}")
            continue
            
        emblem_raw = Image.open(emblem_path).convert("RGBA")
        ebbox = emblem_raw.getbbox()
        emblem_crop = emblem_raw.crop(ebbox) if ebbox else emblem_raw
        
        cw, ch = emblem_crop.size
        scale_tome = 58.0 / max(cw, ch)
        nw, nh = max(1, int(cw * scale_tome)), max(1, int(ch * scale_tome))
        emblem_tome = emblem_crop.resize((nw, nh), Image.Resampling.LANCZOS)
        
        # 彻底二值化为 100% 纯白色，绝无渐变
        earr = np.array(emblem_tome)
        alpha_mask = earr[:, :, 3] > 60
        earr[:, :, :3] = 255
        earr[:, :, 3] = np.where(alpha_mask, 255, 0).astype(np.uint8)
        emblem_styled = Image.fromarray(earr)
        
        # 生成清晰的 1-2px 深墨描边，确保纯白符号在任何封皮上锐利无比
        pos_x = 72 - nw // 2
        pos_y = 60 - nh // 2
        
        from scipy.ndimage import binary_dilation
        stroke_mask = binary_dilation(alpha_mask, iterations=2) & (~alpha_mask)
        esarr = np.zeros_like(earr)
        esarr[stroke_mask] = [20, 20, 20, 240]
        
        book_img.paste(Image.fromarray(esarr), (pos_x, pos_y), Image.fromarray(esarr))
        book_img.paste(emblem_styled, (pos_x, pos_y), emblem_styled)
        tome_64 = book_img.resize((64, 64), Image.Resampling.LANCZOS)
        out_tome = os.path.join(OUTPUT_DIR, f"item.tome.{slug}.webp")
        tome_64.save(out_tome, format="WEBP", lossless=True)
        
        # B. 合成残卷
        frag_arr = clean_frag_arr.copy()
        lum_f = (rf.astype(float) + gf.astype(float) + bf.astype(float)) / (3.0 * orig_mean)
        frag_arr[is_frag_body, 0] = np.clip(tr * lum_f[is_frag_body] * 0.95, 0, 255).astype(np.uint8)
        frag_arr[is_frag_body, 1] = np.clip(tg * lum_f[is_frag_body] * 0.95, 0, 255).astype(np.uint8)
        frag_arr[is_frag_body, 2] = np.clip(tb * lum_f[is_frag_body] * 0.95, 0, 255).astype(np.uint8)
        frag_img = Image.fromarray(frag_arr)
        
        scale_frag = 50.0 / max(cw, ch)
        nfw, nfh = max(1, int(cw * scale_frag)), max(1, int(ch * scale_frag))
        emblem_frag = emblem_crop.resize((nfw, nfh), Image.Resampling.LANCZOS)
        
        efarr = np.array(emblem_frag)
        alpha_f_mask = efarr[:, :, 3] > 60
        efarr[:, :, :3] = 255
        efarr[:, :, 3] = np.where(alpha_f_mask, 255, 0).astype(np.uint8)
        emblem_f_styled = Image.fromarray(efarr)
        
        fpos_x = 52 - nfw // 2
        fpos_y = 62 - nfh // 2
        
        stroke_f_mask = binary_dilation(alpha_f_mask, iterations=2) & (~alpha_f_mask)
        efsarr = np.zeros_like(efarr)
        efsarr[stroke_f_mask] = [20, 20, 20, 240]
        
        frag_img.paste(Image.fromarray(efsarr), (fpos_x, fpos_y), Image.fromarray(efsarr))
        frag_img.paste(emblem_f_styled, (fpos_x, fpos_y), emblem_f_styled)
        frag_64 = frag_img.resize((64, 64), Image.Resampling.LANCZOS)
        out_frag = os.path.join(OUTPUT_DIR, f"item.fragment.{slug}.webp")
        frag_64.save(out_frag, format="WEBP", lossless=True)
        
        count += 2
        print(f"[OK] {name} -> item.tome.{slug}.webp & item.fragment.{slug}.webp")
        
    print(f"\nAll {count} items synthesized successfully!")
    return 0

if __name__ == "__main__":
    sys.exit(synthesize_all())
