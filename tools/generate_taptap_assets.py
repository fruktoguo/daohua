import math
import os
from PIL import Image, ImageDraw, ImageFont, ImageFilter

OUTPUT_DIR = "artifacts/taptap-materials"
os.makedirs(OUTPUT_DIR, exist_ok=True)

# 字体路径
FONT_BOLD = r"C:\Windows\Fonts\msyhbd.ttc"
FONT_REGULAR = r"C:\Windows\Fonts\msyh.ttc"
FONT_SERIF = r"C:\Windows\Fonts\simsun.ttc"

def create_icon(size=512):
    """生成 TapTap 官方规范的 512x512 / 1024x1024 游戏图标"""
    img = Image.new("RGB", (size, size), "#0d1017")
    draw = ImageDraw.Draw(img)
    
    cx, cy = size / 2, size / 2
    
    # 1. 径向渐变背景 (深墨玄青微渐变，非纯黑，具质感)
    for r in range(int(size * 0.75), 0, -2):
        factor = r / (size * 0.75)
        # 从中心的 #1c2331 渐变到边缘的 #0b0e14
        red = int(11 + (28 - 11) * (1 - factor))
        green = int(14 + (35 - 14) * (1 - factor))
        blue = int(20 + (49 - 20) * (1 - factor))
        draw.ellipse([cx - r, cy - r, cx + r, cy + r], fill=(red, green, blue))
        
    # 2. 绘制先天太极外环星轨 (暗金与真金)
    outer_r = size * 0.44
    draw.ellipse([cx - outer_r, cy - outer_r, cx + outer_r, cy + outer_r], outline="#4A3F25", width=max(2, int(size * 0.006)))
    
    mid_r = size * 0.39
    draw.ellipse([cx - mid_r, cy - mid_r, cx + mid_r, cy + mid_r], outline="#8C7945", width=max(2, int(size * 0.008)))
    
    # 3. 八方星位刻度线
    for angle in range(0, 360, 45):
        rad = math.radians(angle)
        x1 = cx + (mid_r - size * 0.03) * math.cos(rad)
        y1 = cy + (mid_r - size * 0.03) * math.sin(rad)
        x2 = cx + (outer_r + size * 0.01) * math.cos(rad)
        y2 = cy + (outer_r + size * 0.01) * math.sin(rad)
        draw.line([x1, y1, x2, y2], fill="#C9B46D", width=max(2, int(size * 0.008)))
        
    # 4. 菱形化境地元框与正方天元框 (金印)
    box_r = size * 0.28
    draw.rectangle([cx - box_r, cy - box_r, cx + box_r, cy + box_r], outline="#A38B4A", width=max(2, int(size * 0.008)))
    
    # 45度旋转菱形框
    diamond_r = size * 0.33
    diamond_pts = [
        (cx, cy - diamond_r),
        (cx + diamond_r, cy),
        (cx, cy + diamond_r),
        (cx - diamond_r, cy)
    ]
    draw.polygon(diamond_pts, outline="#D4AF37", width=max(2, int(size * 0.009)))
    
    # 5. 四隅太虚阵眼微光
    dot_r = size * 0.016
    for angle in range(0, 360, 90):
        rad = math.radians(angle)
        dx = cx + diamond_r * math.cos(rad)
        dy = cy + diamond_r * math.sin(rad)
        draw.ellipse([dx - dot_r, dy - dot_r, dx + dot_r, dy + dot_r], fill="#FFDF73", outline="#8C7945")

    # 6. 中央苍劲大印文「道」
    font_size = int(size * 0.42)
    font = ImageFont.truetype(FONT_SERIF, font_size)
    text = "道"
    
    # 获取文字包围盒以精确居中
    bbox = draw.textbbox((0, 0), text, font=font)
    tw = bbox[2] - bbox[0]
    th = bbox[3] - bbox[1]
    tx = cx - tw / 2 - bbox[0]
    ty = cy - th / 2 - bbox[1]
    
    # 金光微晕渲染 (阴影与微光)
    glow_color = (130, 105, 30)
    for ox in [-3, -2, 0, 2, 3]:
        for oy in [-3, -2, 0, 2, 3]:
            if ox != 0 or oy != 0:
                draw.text((tx + ox, ty + oy), text, fill=glow_color, font=font)
                
    # 正金文字
    draw.text((tx, ty), text, fill="#F7E298", font=font)
    
    # 7. 顶部四字篆铭微款 (道化太初)
    sub_font_size = int(size * 0.055)
    sub_font = ImageFont.truetype(FONT_REGULAR, sub_font_size)
    sub_text = "道 化 太 初"
    sub_bbox = draw.textbbox((0, 0), sub_text, font=sub_font)
    sub_w = sub_bbox[2] - sub_bbox[0]
    draw.text((cx - sub_w / 2, cy + box_r * 0.65), sub_text, fill="#C9B46D", font=sub_font)
    
    filename = f"{OUTPUT_DIR}/icon_{size}x{size}.png"
    img.save(filename, "PNG", quality=95)
    print(f"Icon generated: {filename}")
    return filename

def create_banner():
    """生成 TapTap 官方规范的 1920x1080 16:9 宣传图"""
    w, h = 1920, 1080
    img = Image.new("RGB", (w, h), "#080a0f")
    draw = ImageDraw.Draw(img)
    
    # 1. 广袤星海背景与深邃太虚微晕
    cx, cy = w / 2, h / 2
    for r in range(int(w * 0.7), 0, -4):
        factor = r / (w * 0.7)
        red = int(8 + (24 - 8) * (1 - factor))
        green = int(10 + (32 - 10) * (1 - factor))
        blue = int(15 + (48 - 15) * (1 - factor))
        draw.ellipse([cx - r, cy - r * 0.6, cx + r, cy + r * 0.6], fill=(red, green, blue))
        
    # 2. 远山与水墨层峦剪影 (底部山势)
    mountain_pts = [
        (0, h),
        (0, h * 0.82),
        (w * 0.15, h * 0.76),
        (w * 0.3, h * 0.85),
        (w * 0.48, h * 0.78),
        (w * 0.65, h * 0.86),
        (w * 0.82, h * 0.74),
        (w, h * 0.8),
        (w, h)
    ]
    draw.polygon(mountain_pts, fill="#0c1017")
    
    # 近处更深邃的山峦
    mountain_pts_front = [
        (0, h),
        (0, h * 0.9),
        (w * 0.22, h * 0.86),
        (w * 0.4, h * 0.93),
        (w * 0.6, h * 0.87),
        (w * 0.78, h * 0.92),
        (w, h * 0.88),
        (w, h)
    ]
    draw.polygon(mountain_pts_front, fill="#07090d")

    # 3. 巨幅先天八卦太极星轨法阵 (居中，避开边缘裁剪)
    r_outer = 420
    draw.ellipse([cx - r_outer, cy - r_outer, cx + r_outer, cy + r_outer], outline="#2A2415", width=2)
    
    r_mid = 380
    draw.ellipse([cx - r_mid, cy - r_mid, cx + r_mid, cy + r_mid], outline="#6E5E35", width=3)
    
    r_inner = 330
    draw.ellipse([cx - r_inner, cy - r_inner, cx + r_inner, cy + r_inner], outline="#A38B4A", width=2)
    
    # 4. 八方星轨刻度与天元地元印
    for angle in range(0, 360, 30):
        rad = math.radians(angle)
        x1 = cx + (r_mid - 20) * math.cos(rad)
        y1 = cy + (r_mid - 20) * math.sin(rad)
        x2 = cx + (r_outer + 10) * math.cos(rad)
        y2 = cy + (r_outer + 10) * math.sin(rad)
        color = "#C9B46D" if angle % 90 == 0 else "#6E5E35"
        width = 4 if angle % 90 == 0 else 2
        draw.line([x1, y1, x2, y2], fill=color, width=width)
        
    diamond_r = 310
    draw.polygon([
        (cx, cy - diamond_r),
        (cx + diamond_r, cy),
        (cx, cy + diamond_r),
        (cx - diamond_r, cy)
    ], outline="#8C7945", width=2)
    
    # 5. 主视觉文字：《道化太初》（严格遵守规范：不得出现除了游戏名外的宣传广告文字）
    title_font = ImageFont.truetype(FONT_SERIF, 150)
    title_text = "道 化 太 初"
    
    bbox = draw.textbbox((0, 0), title_text, font=title_font)
    tw = bbox[2] - bbox[0]
    th = bbox[3] - bbox[1]
    tx = cx - tw / 2 - bbox[0]
    ty = cy - th / 2 - bbox[1] - 20
    
    # 烫金阴影金光
    for ox in [-6, -4, -2, 0, 2, 4, 6]:
        for oy in [-6, -4, -2, 0, 2, 4, 6]:
            if ox != 0 or oy != 0:
                draw.text((tx + ox, ty + oy), title_text, fill=(70, 50, 15), font=title_font)
                
    # 烫金色字面
    draw.text((tx, ty), title_text, fill="#F7E298", font=title_font)
    
    # 6. 下方小字拼音印记
    pinyin_font = ImageFont.truetype(FONT_REGULAR, 26)
    pinyin_text = "D A O H U A   T A I C H U"
    p_bbox = draw.textbbox((0, 0), pinyin_text, font=pinyin_font)
    pw = p_bbox[2] - p_bbox[0]
    draw.text((cx - pw / 2, ty + th + 45), pinyin_text, fill="#C9B46D", font=pinyin_font)
    
    # 7. 红色印章「太初」
    seal_size = 56
    sx = tx + tw + 30
    sy = ty + 30
    draw.rectangle([sx, sy, sx + seal_size, sy + seal_size], fill="#8A2323", outline="#D4AF37", width=2)
    seal_font = ImageFont.truetype(FONT_SERIF, 20)
    draw.text((sx + 8, sy + 6), "太\n初", fill="#F7E298", font=seal_font, spacing=2)

    filename = f"{OUTPUT_DIR}/banner_1920x1080.png"
    img.save(filename, "PNG", quality=95)
    print(f"Banner generated: {filename}")
    return filename

if __name__ == "__main__":
    create_icon(512)
    create_icon(1024)
    create_banner()
