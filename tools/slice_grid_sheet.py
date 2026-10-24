#!/usr/bin/env python3
"""
道化太初 · 网格素材大图切图工具 (tools/slice_grid_sheet.py)
按照 art/sheets.json 规格与 art/icon_ids.json 映射，将网格大图自动切块、抠图/保留外边距，
并使用 LANCZOS 算法缩放压制为 64x64 WebP 图标，直接落盘至 frontend/src/assets/items/<id>.webp。
"""

import os
import sys
import json
from PIL import Image

REPO_ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
SHEETS_JSON = os.path.join(REPO_ROOT, "art", "sheets.json")
ICON_IDS_JSON = os.path.join(REPO_ROOT, "art", "icon_ids.json")
OUTPUT_DIR = os.path.join(REPO_ROOT, "frontend", "src", "assets", "items")

import numpy as np
from collections import deque

def remove_outer_background(img_rgba, tolerance=30, feather=8):
    """
    使用边缘连通域泛洪（Flood Fill from Borders）剔除纯白/近白外部背景，
    保留道具内部的白色高光/白色材质，并对边缘进行抗锯齿羽化。
    """
    arr = np.array(img_rgba, dtype=np.float32)
    h, w, _ = arr.shape
    
    # 计算每个像素与纯白 (255, 255, 255) 的最大通道距离
    diff = np.max(np.abs(arr[:, :, :3] - 255.0), axis=2)
    is_candidate = diff <= (tolerance + feather)
    
    visited = np.zeros((h, w), dtype=bool)
    queue = deque()
    
    # 从四条外边界中与白色相近的点初始化泛洪队列
    for x in range(w):
        if is_candidate[0, x]:
            queue.append((0, x))
            visited[0, x] = True
        if is_candidate[h - 1, x]:
            queue.append((h - 1, x))
            visited[h - 1, x] = True
            
    for y in range(h):
        if is_candidate[y, 0] and not visited[y, 0]:
            queue.append((y, 0))
            visited[y, 0] = True
        if is_candidate[y, w - 1] and not visited[y, w - 1]:
            queue.append((y, w - 1))
            visited[y, w - 1] = True
            
    # BFS 泛洪
    while queue:
        cy, cx = queue.popleft()
        for dy, dx in [(-1, 0), (1, 0), (0, -1), (0, 1)]:
            ny, nx = cy + dy, cx + dx
            if 0 <= ny < h and 0 <= nx < w and not visited[ny, nx]:
                if is_candidate[ny, nx]:
                    visited[ny, nx] = True
                    queue.append((ny, nx))
                    
    alpha = arr[:, :, 3].copy()
    
    # 纯外部背景设为完全透明
    pure_bg_mask = visited & (diff <= tolerance)
    alpha[pure_bg_mask] = 0.0
    
    # 边缘抗锯齿线性羽化
    feather_mask = visited & (diff > tolerance) & (diff <= tolerance + feather)
    feather_factor = (diff[feather_mask] - tolerance) / max(1e-5, feather)
    alpha[feather_mask] = alpha[feather_mask] * feather_factor
    
    arr[:, :, 3] = np.clip(alpha, 0, 255)
    return Image.fromarray(arr.astype(np.uint8), mode="RGBA")

def center_and_pad(img_rgba, canvas_size=64, inner_size=54):
    """
    提取不透明像素真实包围盒，等比缩放到 inner_size 内，居中放置在 canvas_size x canvas_size 的透明画布中。
    四周自然留出 (canvas_size - inner_size)/2 约 5~7px 的安全呼吸边距，与原有手绘图标规格完全契合。
    """
    bbox = img_rgba.getbbox()
    if not bbox:
        return Image.new("RGBA", (canvas_size, canvas_size), (0, 0, 0, 0))
        
    cropped = img_rgba.crop(bbox)
    cw, ch = cropped.size
    
    scale = min(inner_size / cw, inner_size / ch)
    target_w = max(1, int(round(cw * scale)))
    target_h = max(1, int(round(ch * scale)))
    
    resized = cropped.resize((target_w, target_h), Image.Resampling.LANCZOS)
    
    canvas = Image.new("RGBA", (canvas_size, canvas_size), (0, 0, 0, 0))
    paste_x = (canvas_size - target_w) // 2
    paste_y = (canvas_size - target_h) // 2
    canvas.paste(resized, (paste_x, paste_y), resized)
    return canvas

def slice_sheet(sheet_config, icon_map, web_size=64, pad_percent=0.06, inner_size=54):
    src_path = sheet_config["src"]
    base_name = os.path.basename(src_path)
    real_src = os.path.join(REPO_ROOT, "art", "source", base_name)
    if not os.path.exists(real_src) and os.path.exists(src_path):
        real_src = src_path
        
    if not os.path.exists(real_src):
        print(f"[WARN] 素材源图不存在: {real_src}")
        return 0
        
    cols = sheet_config["cols"]
    rows = sheet_config["rows"]
    limit = sheet_config.get("limit", cols * rows)
    names = sheet_config.get("names", [])
    sheet_name = os.path.splitext(base_name)[0]
    sheet_icon_ids = icon_map.get(sheet_name, {})
    
    print(f"\n=== 开始切图与自动抠图: {sheet_name} ({cols}列 x {rows}行, 共 {len(names)} 项) ===")
    
    with Image.open(real_src) as img:
        img_rgba = img.convert("RGBA")
        width, height = img_rgba.size
        cell_w = width / cols
        cell_h = height / rows
        
        sliced_count = 0
        for idx, name in enumerate(names):
            if idx >= limit:
                break
            
            c = idx % cols
            r = idx // cols
            
            left = c * cell_w
            top = r * cell_h
            right = left + cell_w
            bottom = top + cell_h
            
            pad_x = cell_w * pad_percent
            pad_y = cell_h * pad_percent
            
            box = (int(left + pad_x), int(top + pad_y), int(right - pad_x), int(bottom - pad_y))
            cell_img = img_rgba.crop(box)
            
            # 获取对应的 item ID
            item_id = sheet_icon_ids.get(name)
            if not item_id:
                for s_dict in icon_map.values():
                    if name in s_dict:
                        item_id = s_dict[name]
                        break
                        
            if not item_id:
                print(f"  [SKIP] 找不到条目 ID 映射: {name} (格 [{r},{c}])")
                continue
                
            # 1. 外部连通域抠图（剔除背景白色）
            transparent_cell = remove_outer_background(cell_img)
            
            # 2. 居中、等比缩放并内缩边距压制为 64x64 透明画布
            out_img = center_and_pad(transparent_cell, canvas_size=web_size, inner_size=inner_size)
            
            os.makedirs(OUTPUT_DIR, exist_ok=True)
            out_path = os.path.join(OUTPUT_DIR, f"{item_id}.webp")
            out_img.save(out_path, "WEBP", quality=95, method=6)
            sliced_count += 1
            print(f"  [{sliced_count:02d}] {name:8s} -> {item_id}.webp ({os.path.getsize(out_path)} bytes)")
            
    return sliced_count

def main():
    if not os.path.exists(SHEETS_JSON) or not os.path.exists(ICON_IDS_JSON):
        print("[ERROR] 缺少 art/sheets.json 或 art/icon_ids.json")
        sys.exit(1)
        
    with open(SHEETS_JSON, "r", encoding="utf-8") as f:
        sheets = json.load(f)
        
    with open(ICON_IDS_JSON, "r", encoding="utf-8") as f:
        icon_map = json.load(f)
        
    total_sliced = 0
    target_sheet = sys.argv[1] if len(sys.argv) > 1 else None
    
    for sheet in sheets:
        base_name = os.path.basename(sheet["src"])
        sheet_name = os.path.splitext(base_name)[0]
        if target_sheet and target_sheet not in sheet_name:
            continue
        total_sliced += slice_sheet(sheet, icon_map)
        
    print(f"\n=== 全部切图完成，共成功生成 {total_sliced} 个 WebP 运行时图标！ ===")

if __name__ == "__main__":
    main()
