#!/usr/bin/env python3
"""
道化太初 · 美术资产批处理与转码工具
功能：
1. 图标批处理：自动将生成的黑白单彩极简折面原始图高质量重采样缩放至 64x64 WebP，落盘至 frontend/src/assets/items/<id>.webp
2. 立绘批处理：将角色立绘处理为 256x256 WebP / PNG，支持外部纯黑背景透明化，落盘至 frontend/public/avatars/<id>.webp
"""

import os
import sys
import json
from PIL import Image

def process_item_icon(src_path: str, item_id: str, dest_dir: str = "frontend/src/assets/items"):
    os.makedirs(dest_dir, exist_ok=True)
    out_file = os.path.join(dest_dir, f"{item_id}.webp")
    
    with Image.open(src_path) as im:
        # 确保 RGB 模式
        im_rgb = im.convert("RGB")
        # Lanczos 高精度重采样至 64x64
        im_thumb = im_rgb.resize((64, 64), Image.Resampling.LANCZOS)
        im_thumb.save(out_file, "WEBP", quality=95, method=6)
        
    print(f"[ICON] 成功压制: {item_id} -> {out_file} ({os.path.getsize(out_file)} bytes)")
    return out_file

def process_avatar_image(src_path: str, avatar_id: str, dest_dir: str = "frontend/public/avatars"):
    os.makedirs(dest_dir, exist_ok=True)
    out_webp = os.path.join(dest_dir, f"{avatar_id}.webp")
    out_png = os.path.join(dest_dir, f"{avatar_id}.png")
    
    with Image.open(src_path) as im:
        im_rgba = im.convert("RGBA")
        
        # 洪水填充或纯黑边缘色透明化（背景纯黑 #000000 抠除）
        # 检查四个角的颜色
        corner_pixels = [im_rgba.getpixel((0, 0)), im_rgba.getpixel((im_rgba.width - 1, 0)),
                         im_rgba.getpixel((0, im_rgba.height - 1)), im_rgba.getpixel((im_rgba.width - 1, im_rgba.height - 1))]
        
        # 如果是纯黑背景（RGB均 <= 12），执行背景透明化
        datas = im_rgba.getdata()
        new_data = []
        for item in datas:
            # 纯黑容差
            if item[0] <= 12 and item[1] <= 12 and item[2] <= 12:
                new_data.append((0, 0, 0, 0))
            else:
                new_data.append(item)
        im_rgba.putdata(new_data)
        
        # 重采样到 256x256
        im_256 = im_rgba.resize((256, 256), Image.Resampling.LANCZOS)
        im_256.save(out_webp, "WEBP", quality=95, method=6)
        im_256.save(out_png, "PNG", optimize=True)
        
    print(f"[AVATAR] 成功生成立绘: {avatar_id} -> {out_webp} & {out_png}")
    return out_webp

if __name__ == "__main__":
    if len(sys.argv) > 1 and sys.argv[1] == "--batch":
        # 接收 json 参数: {"items": [{"src": "...", "id": "..."}, ...], "avatars": [...]}
        config_path = sys.argv[2]
        with open(config_path, "r", encoding="utf-8") as f:
            data = json.load(f)
            
        for item in data.get("items", []):
            process_item_icon(item["src"], item["id"])
            
        for avatar in data.get("avatars", []):
            process_avatar_image(avatar["src"], avatar["id"])
            
        print("[BATCH] 批量处理全部完成！")
