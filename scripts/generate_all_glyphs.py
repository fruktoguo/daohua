#!/usr/bin/env python3
"""
道化太初 · 属性小图标全量生成与切片脚本
批量调用本地 ComfyUI 生成纯黑底纯白二值 Stencil Mask 图标并切片落盘。
"""

import os
import sys
import json
import time
import urllib.request
import numpy as np
from PIL import Image

COMFY_HOST = "http://127.0.0.1:8188"
COMFY_OUTPUT_DIR = r"D:\Tools\AI\ComfyUI-Shared\output"
REPO_ROOT = os.path.abspath(os.path.join(os.path.dirname(__file__), ".."))
ART_GLYPHS_DIR = os.path.join(REPO_ROOT, "art", "glyphs")
FRONTEND_GLYPHS_DIR = os.path.join(REPO_ROOT, "frontend", "src", "assets", "glyphs")

os.makedirs(ART_GLYPHS_DIR, exist_ok=True)
os.makedirs(FRONTEND_GLYPHS_DIR, exist_ok=True)

BATCHES = {
    "b2_defense": [
        ("armor", "物理护甲", "A solid pure white sturdy kite shield silhouette icon, bold geometric defensive shape, clean flat silhouette, floating in center of black quadrant, strictly pure white on solid pure black, zero gradients."),
        ("magic-res", "术法抗性", "A solid pure white Bagua mirror or talismanic ward round ring shield silhouette icon, circular with four cardinal corner projections, clean flat silhouette, floating in center of black quadrant, strictly pure white on solid pure black, zero gradients."),
        ("accuracy", "命中", "A solid pure white target crosshair silhouette icon, concentric circle bullseye with broken crosshairs, clean flat silhouette, floating in center of black quadrant, strictly pure white on solid pure black, zero gradients."),
        ("evasion", "闪避", "A solid pure white phantom evasion dashed wing or swift curved wind trail silhouette icon, dynamic diagonal swooshing curve, clean flat silhouette, floating in center of black quadrant, strictly pure white on solid pure black, zero gradients.")
    ],
    "b3_crit_parry": [
        ("crit", "暴击", "A solid pure white four-pointed diamond sharp burst star silhouette icon, classic four-point sparkle glint, clean flat geometric silhouette, floating in center of black quadrant, strictly pure white on solid pure black, zero gradients."),
        ("crit-avoid", "免暴", "A solid pure white round shield silhouette icon with a small four-pointed star suppressed in its center, clean flat silhouette, floating in center of black quadrant, strictly pure white on solid pure black, zero gradients."),
        ("parry-break", "破招", "A solid pure white broken split cracked shield silhouette icon, sharp diagonal fracture split across the shield, clean flat silhouette, floating in center of black quadrant, strictly pure white on solid pure black, zero gradients."),
        ("parry-resolve", "化解", "A solid pure white deflect curved arc hand silhouette icon, smooth circular parry deflection curve, clean flat silhouette, floating in center of black quadrant, strictly pure white on solid pure black, zero gradients.")
    ],
    "b4_regen_speed": [
        ("hp-regen", "气血回复", "A solid pure white heart silhouette icon with a bold upward arrow cut out or combined inside, healing life regeneration symbol, clean flat silhouette, floating in center of black quadrant, strictly pure white on solid pure black, zero gradients."),
        ("mp-regen", "灵力回复", "A solid pure white water drop droplet silhouette icon with a circular clockwise recycling arrow inside, mana regen symbol, clean flat silhouette, floating in center of black quadrant, strictly pure white on solid pure black, zero gradients."),
        ("attack-speed", "出招速度", "A solid pure white diagonal sword blade with two speed motion trailing ghost lines, fast attack speed icon, clean flat silhouette, floating in center of black quadrant, strictly pure white on solid pure black, zero gradients."),
        ("action-speed", "行动速度", "A solid pure white swift flying wind cloud or winged boot silhouette icon, fast action casting speed symbol, clean flat silhouette, floating in center of black quadrant, strictly pure white on solid pure black, zero gradients.")
    ],
    "b5_cooldown_bonus": [
        ("cooldown-reduction", "冷却缩减", "A solid pure white circular counter-clockwise rotating refresh arrow silhouette icon, cooldown reduction symbol, clean flat silhouette, floating in center of black quadrant, strictly pure white on solid pure black, zero gradients."),
        ("crit-damage", "暴击伤害", "A solid pure white double-layered overlapping sharp burst stars silhouette icon, critical damage multiplier symbol, clean flat silhouette, floating in center of black quadrant, strictly pure white on solid pure black, zero gradients."),
        ("basic-attack-bonus", "普攻增幅", "A solid pure white sword blade silhouette icon with a thick bold upward arrow beside it, basic attack boost symbol, clean flat silhouette, floating in center of black quadrant, strictly pure white on solid pure black, zero gradients."),
        ("skill-bonus", "技能增幅", "A solid pure white magic fireball flame silhouette icon with a thick bold upward arrow beside it, skill damage boost symbol, clean flat silhouette, floating in center of black quadrant, strictly pure white on solid pure black, zero gradients.")
    ],
    "b6_effects_a": [
        ("threat-bonus", "仇恨威胁", "A solid pure white fierce roaring beast demon face mask silhouette icon, aggro threat symbol, clean flat geometric silhouette, floating in center of black quadrant, strictly pure white on solid pure black, zero gradients."),
        ("healing-bonus", "治疗增益", "A solid pure white medical healing cross with a holy halo or dew drop silhouette icon, recovery bonus symbol, clean flat silhouette, floating in center of black quadrant, strictly pure white on solid pure black, zero gradients."),
        ("hp-drain", "气血汲取", "A solid pure white heart silhouette icon with sharp vampire fangs biting or draining energy, lifesteal drain symbol, clean flat silhouette, floating in center of black quadrant, strictly pure white on solid pure black, zero gradients."),
        ("mp-drain", "灵力汲取", "A solid pure white spiraling vortex mana siphon whirlpool silhouette icon, mana drain symbol, clean flat silhouette, floating in center of black quadrant, strictly pure white on solid pure black, zero gradients.")
    ],
    "b7_effects_b": [
        ("damage-reflect", "伤害反弹", "A solid pure white zigzag jagged lightning bolt ricocheting and reflecting backwards off a small bar, damage reflection symbol, clean flat silhouette, floating in center of black quadrant, strictly pure white on solid pure black, zero gradients."),
        ("total-damage", "总伤害增免", "A solid pure white crossed sword and shield emblem silhouette icon, universal total damage bonus reduction symbol, clean flat silhouette, floating in center of black quadrant, strictly pure white on solid pure black, zero gradients."),
        ("phys-damage", "物理增减伤", "A solid pure white broad iron blade and curved armor plate silhouette icon, physical damage modifier symbol, clean flat silhouette, floating in center of black quadrant, strictly pure white on solid pure black, zero gradients."),
        ("spell-damage", "法术增减伤", "A solid pure white magic wand staff with radiating spell arcane ring silhouette icon, spell damage modifier symbol, clean flat silhouette, floating in center of black quadrant, strictly pure white on solid pure black, zero gradients.")
    ],
    "b8_roots_a": [
        ("stamina", "体魄", "A solid pure white heavy ancient three-legged bronze cauldron Ding vessel silhouette icon, physique stamina vitality symbol, clean flat silhouette, floating in center of black quadrant, strictly pure white on solid pure black, zero gradients."),
        ("meridians", "经脉", "A solid pure white three parallel flowing Qi energy wave lines curved silhouette icon, meridians energy channels symbol, clean flat silhouette, floating in center of black quadrant, strictly pure white on solid pure black, zero gradients."),
        ("attack", "力道", "A solid pure white clenched fist punch silhouette icon, raw strength power symbol, clean bold geometric silhouette, floating in center of black quadrant, strictly pure white on solid pure black, zero gradients."),
        ("bone", "根骨", "A solid pure white two cleanly crossed bone skeletal bones X-shape silhouette icon, bone frame constitution symbol, clean flat silhouette, floating in center of black quadrant, strictly pure white on solid pure black, zero gradients.")
    ],
    "b9_roots_b": [
        ("agility", "身法", "A solid pure white sleek swift bird flight feather silhouette icon, agility evasion speed symbol, clean flat silhouette, floating in center of black quadrant, strictly pure white on solid pure black, zero gradients."),
        ("consciousness", "神识", "A solid pure white mystical third eye all-seeing eye with round pupil silhouette icon, consciousness perception mind symbol, clean flat silhouette, floating in center of black quadrant, strictly pure white on solid pure black, zero gradients."),
        ("realm", "境界等阶", "A solid pure white three-tiered blooming lotus throne or Daoist rank seal silhouette icon, cultivation realm tier symbol, clean flat silhouette, floating in center of black quadrant, strictly pure white on solid pure black, zero gradients."),
        ("experience", "修为", "A solid pure white rotating Yin-Yang Qi spiral vortex energy core silhouette icon, cultivation experience Qi symbol, clean flat silhouette, floating in center of black quadrant, strictly pure white on solid pure black, zero gradients.")
    ],
    "b10_foundation_craft_a": [
        ("foundation", "道行底蕴", "A solid pure white ancient Daoist rolled parchment scroll book silhouette icon, foundation Dao reserve symbol, clean flat silhouette, floating in center of black quadrant, strictly pure white on solid pure black, zero gradients."),
        ("combat-exp", "战斗经验", "A solid pure white crossed combatant sabers blades silhouette icon, combat battle experience symbol, clean flat silhouette, floating in center of black quadrant, strictly pure white on solid pure black, zero gradients."),
        ("gathering", "采伐技艺", "A solid pure white single-handed woodcutter hatchet axe silhouette icon, gathering logging skill symbol, clean flat silhouette, floating in center of black quadrant, strictly pure white on solid pure black, zero gradients."),
        ("mining", "采矿技艺", "A solid pure white pointed mining pickaxe silhouette icon, mining mineral excavation skill symbol, clean flat silhouette, floating in center of black quadrant, strictly pure white on solid pure black, zero gradients.")
    ],
    "b11_craft_b": [
        ("alchemy", "炼丹技艺", "A solid pure white round-bellied three-legged alchemy pill furnace cauldron silhouette icon, alchemy pill crafting symbol, clean flat silhouette, floating in center of black quadrant, strictly pure white on solid pure black, zero gradients."),
        ("forging", "炼器技艺", "A solid pure white heavy square blacksmith anvil silhouette icon, weapon equipment forging skill symbol, clean flat silhouette, floating in center of black quadrant, strictly pure white on solid pure black, zero gradients."),
        ("talisman", "制符技艺", "A solid pure white vertical rectangular Daoist paper talisman strip with cut tail, talisman calligraphy crafting symbol, clean flat silhouette, floating in center of black quadrant, strictly pure white on solid pure black, zero gradients."),
        ("formation", "衍阵技艺", "A solid pure white Bagua circular tactical formation array compass plate silhouette icon, formation array mastery symbol, clean flat silhouette, floating in center of black quadrant, strictly pure white on solid pure black, zero gradients.")
    ],
    "b12_craft_enhance_elements": [
        ("enhancement", "淬灵技艺", "A solid pure white vertical sword tip quenching into an elemental basin water anvil, equipment enhancement socketing symbol, clean flat silhouette, floating in center of black quadrant, strictly pure white on solid pure black, zero gradients."),
        ("element-metal", "五行金", "A solid pure white diamond metal sharp sword blade facet silhouette icon, metal elemental symbol, clean flat silhouette, floating in center of black quadrant, strictly pure white on solid pure black, zero gradients."),
        ("element-wood", "五行木", "A solid pure white organic three-leaf sprout seedling silhouette icon, wood life elemental symbol, clean flat silhouette, floating in center of black quadrant, strictly pure white on solid pure black, zero gradients."),
        ("element-water", "五行水", "A solid pure white three parallel undulating ocean river water waves silhouette icon, water wave elemental symbol, clean flat silhouette, floating in center of black quadrant, strictly pure white on solid pure black, zero gradients.")
    ],
    "b13_elements_rest": [
        ("element-fire", "五行火", "A solid pure white rising single stylized flame tongue silhouette icon, fire elemental symbol, clean flat silhouette, floating in center of black quadrant, strictly pure white on solid pure black, zero gradients."),
        ("element-earth", "五行土", "A solid pure white three stacked rectangular sturdy stone masonry blocks silhouette icon, earth mountain elemental symbol, clean flat silhouette, floating in center of black quadrant, strictly pure white on solid pure black, zero gradients."),
        ("lifespan", "寿元", "A solid pure white ancient peach or burning incense vessel hourglass silhouette icon, lifespan longevity symbol, clean flat silhouette, floating in center of black quadrant, strictly pure white on solid pure black, zero gradients."),
        ("speed", "移动速度", "A solid pure white flying sword riding cloud wind streak silhouette icon, travel movement speed symbol, clean flat silhouette, floating in center of black quadrant, strictly pure white on solid pure black, zero gradients.")
    ]
    ,
    "b14_bio_monsters": [
        ("bio-monsters", "斩妖总计", "A solid pure white monster beast skull with a vertical sword piercing through silhouette icon, total monster kills symbol, clean flat silhouette, floating in center of black quadrant, strictly pure white on solid pure black, zero gradients."),
        ("bio-normal", "寻常妖兽", "A solid pure white beast claw paw mark with three sharp talons silhouette icon, normal monster beast kills symbol, clean flat silhouette, floating in center of black quadrant, strictly pure white on solid pure black, zero gradients."),
        ("bio-elite", "精英妖兽", "A solid pure white horned beast head with a radiant glowing crown halo silhouette icon, elite champion monster symbol, clean flat silhouette, floating in center of black quadrant, strictly pure white on solid pure black, zero gradients."),
        ("bio-boss", "首领妖王", "A solid pure white fierce demonic beast sovereign head with huge imposing curved horns and third eye silhouette icon, boss demon king lord symbol, clean flat silhouette, floating in center of black quadrant, strictly pure white on solid pure black, zero gradients.")
    ],
    "b15_bio_pvp_life": [
        ("bio-pvp-win", "击破同道", "A solid pure white crossed dual swords cutting through a battlefield pennant flag silhouette icon, PvP victory rival defeated symbol, clean flat silhouette, floating in center of black quadrant, strictly pure white on solid pure black, zero gradients."),
        ("bio-pvp-loss", "重伤败走", "A solid pure white snapped broken sword blade snapped into two pieces silhouette icon, PvP defeat retreat symbol, clean flat silhouette, floating in center of black quadrant, strictly pure white on solid pure black, zero gradients."),
        ("bio-cultivation", "闭关修炼", "A solid pure white meditating Daoist monk sitting cross-legged in lotus posture on a prayer mat silhouette icon, seclusion cultivation meditating symbol, clean flat silhouette, floating in center of black quadrant, strictly pure white on solid pure black, zero gradients."),
        ("bio-lifespan", "寿元岁数", "A solid pure white celestial longevity peach fruit with gentle leaf silhouette icon, lifespan longevity years symbol, clean flat silhouette, floating in center of black quadrant, strictly pure white on solid pure black, zero gradients.")
    ]
}

def get_latest_workflow():
    with urllib.request.urlopen(f"{COMFY_HOST}/history") as resp:
        history = json.loads(resp.read().decode())
        last_id = list(history.keys())[-1]
        wf = history[last_id]["prompt"][2]
    # 绕过 RMBG，直接让 Node 18 输出 Node 26 (VAE 原图)
    if "26" in wf and "18" in wf:
        wf["18"]["inputs"]["images"] = ["26", 0]
    return wf

def submit_task(wf, prompt_str, seed=None, steps=35, cfg=4.0, prefix="daohua_glyph"):
    prompt = json.loads(json.dumps(wf))
    prompt["4"]["inputs"]["text"] = prompt_str
    prompt["5"]["inputs"]["text"] = "colors, chromatic, gradient, gray, shading, shadows, realistic photo, 3d render, texture, sketch, detailed lines, intricate patterns, noise, blurry, soft edges, border, frame, text, watermark, overflow"
    if "19" in prompt:
        prompt["19"]["inputs"]["aspect_ratio"] = "1:1 (Square)"
        prompt["19"]["inputs"]["megapixels"] = 1.0
    if "16" in prompt:
        prompt["16"]["inputs"]["steps"] = steps
        prompt["16"]["inputs"]["cfg"] = cfg
        prompt["16"]["inputs"]["seed"] = seed if seed is not None else int(time.time() * 1000) % 10000000000
    prompt["18"]["inputs"]["filename_prefix"] = prefix

    payload = json.dumps({"prompt": prompt, "client_id": "daohua_glyph_gen"}).encode("utf-8")
    req = urllib.request.Request(f"{COMFY_HOST}/prompt", data=payload, headers={"Content-Type": "application/json"})
    with urllib.request.urlopen(req) as resp:
        res = json.loads(resp.read().decode())
    return res["prompt_id"]

def wait_for_completion(prompt_id, timeout=300):
    start = time.time()
    while time.time() - start < timeout:
        time.sleep(2)
        try:
            with urllib.request.urlopen(f"{COMFY_HOST}/history/{prompt_id}") as resp:
                data = json.loads(resp.read().decode())
                if prompt_id in data and data[prompt_id].get("status", {}).get("completed", False):
                    outputs = data[prompt_id].get("outputs", {})
                    images = []
                    # 优先取 Node 18 (SaveImage) 输出
                    if "18" in outputs and "images" in outputs["18"]:
                        for img in outputs["18"]["images"]:
                            images.append(img["filename"])
                    else:
                        for nid, nout in outputs.items():
                            for img in nout.get("images", []):
                                if img.get("type") == "output":
                                    images.append(img["filename"])
                    if images:
                        return images
        except Exception:
            pass
    raise TimeoutError(f"Task {prompt_id} timed out.")

def make_glyph_image(crop_arr, target_size, inner_size, transparent_bg=True):
    y_idx, x_idx = np.where(crop_arr > 0)
    if len(y_idx) == 0:
        return Image.new("RGBA" if transparent_bg else "RGB", (target_size, target_size), (0, 0, 0, 0) if transparent_bg else (0, 0, 0))
    ymin, ymax = y_idx.min(), y_idx.max()
    xmin, xmax = x_idx.min(), x_idx.max()
    cropped = crop_arr[ymin:ymax+1, xmin:xmax+1]
    
    pim = Image.fromarray(cropped)
    cw, ch = pim.size
    scale = inner_size / max(cw, ch)
    nw, nh = max(1, int(round(cw * scale))), max(1, int(round(ch * scale)))
    resized = pim.resize((nw, nh), Image.Resampling.NEAREST)
    
    if transparent_bg:
        canvas = Image.new("RGBA", (target_size, target_size), (0, 0, 0, 0))
        r_arr = np.array(resized)
        rgba_arr = np.zeros((nh, nw, 4), dtype=np.uint8)
        rgba_arr[r_arr > 0] = [255, 255, 255, 255]
        fg_im = Image.fromarray(rgba_arr, mode="RGBA")
        offset_x = (target_size - nw) // 2
        offset_y = (target_size - nh) // 2
        canvas.paste(fg_im, (offset_x, offset_y), fg_im)
    else:
        canvas = Image.new("RGB", (target_size, target_size), (0, 0, 0))
        r_arr = np.array(resized)
        rgb_arr = np.zeros((nh, nw, 3), dtype=np.uint8)
        rgb_arr[r_arr > 0] = [255, 255, 255]
        fg_im = Image.fromarray(rgb_arr, mode="RGB")
        offset_x = (target_size - nw) // 2
        offset_y = (target_size - nh) // 2
        canvas.paste(fg_im, (offset_x, offset_y))
    return canvas

def process_batch_image(img_path, items_spec):
    im = Image.open(img_path).convert("RGB")
    arr = np.array(im)
    gray = arr.mean(axis=2)
    binary = (gray > 80).astype(np.uint8) * 255

    w, h = im.size
    hw, hh = w // 2, h // 2
    quad_slices = [
        binary[:hh, :hw],   # Top-Left
        binary[:hh, hw:],   # Top-Right
        binary[hh:, :hw],   # Bottom-Left
        binary[hh:, hw:]    # Bottom-Right
    ]

    for (gid, gname, _), q_arr in zip(items_spec, quad_slices):
        # 1. 128px 母版
        mask_128 = make_glyph_image(q_arr, target_size=128, inner_size=104, transparent_bg=False)
        mask_128.save(os.path.join(ART_GLYPHS_DIR, f"glyph.{gid}.mask.png"), "PNG")
        
        trans_128 = make_glyph_image(q_arr, target_size=128, inner_size=104, transparent_bg=True)
        trans_128.save(os.path.join(ART_GLYPHS_DIR, f"glyph.{gid}.webp"), "WEBP", lossless=True)
        
        # 2. 64px 前端瓦片
        trans_64 = make_glyph_image(q_arr, target_size=64, inner_size=52, transparent_bg=True)
        trans_64.save(os.path.join(FRONTEND_GLYPHS_DIR, f"glyph.{gid}.webp"), "WEBP", lossless=True)
        print(f"  -> Sliced & Saved: glyph.{gid} ({gname})")

def main():
    print(f"=== 道化太初 · 属性小图标自动化批量流水线 ===")
    print(f"待处理批次: {len(BATCHES)} 个 (共 {sum(len(v) for v in BATCHES.values())} 项图标)")
    
    wf = get_latest_workflow()
    
    quad_bboxes = [
        [-400, -400, -130, -130], # Top-Left
        [-400, 130, -130, 400],   # Top-Right
        [130, -400, 400, -130],   # Bottom-Left
        [130, 130, 400, 400]      # Bottom-Right
    ]

    for bname, items in BATCHES.items():
        # 检查是否全部已存在
        all_exist = all(
            os.path.exists(os.path.join(FRONTEND_GLYPHS_DIR, f"glyph.{gid}.webp"))
            for gid, _, _ in items
        )
        if all_exist:
            print(f"[{bname}] 全部图标已存在，跳过。")
            continue

        print(f"\n>>> 正在生成批次: [{bname}] ({', '.join(it[1] for it in items)})")
        
        # 组装 layout
        layout_items = []
        for (gid, gname, caption), bbox in zip(items, quad_bboxes):
            layout_items.append({
                "label": "object",
                "caption": f"Quadrant icon: [{gname} - glyph.{gid}]. {caption}",
                "bbox": bbox
            })

        summary = "a 2x2 grid of 4 minimalist bold vector silhouette icons, strictly solid pure white icon glyphs on solid pure jet black background, simple geometric flat 2D game UI icons, stencil cutout style, zero gradients, zero shading, zero colors, strictly monochrome binary black and white, crisp razor-sharp high contrast silhouette, generous empty black spacing between items, strictly no overflow, clean vector graphic glyph."
        
        layout = {
            "summary": {"canvas": "single", "caption": summary},
            "items": layout_items,
            "bbox_coord_space": "abs_centralized",
            "bbox_format": "yxyx"
        }
        prompt_str = json.dumps(layout, ensure_ascii=False, indent=2)
        import glob
        cached_matches = glob.glob(os.path.join(COMFY_OUTPUT_DIR, f"daohua_glyph_{bname}_*.png"))
        if cached_matches:
            cached_img = sorted(cached_matches)[-1]
            print(f"  命中本地已生成图片: {cached_img}，直接执行切片落盘...")
            latest_img = cached_img
        else:
            pid = submit_task(wf, prompt_str, prefix=f"daohua_glyph_{bname}")
            print(f"  已排队，Prompt ID: {pid}，等待 GPU 推理...")
            imgs = wait_for_completion(pid)
            latest_img = os.path.join(COMFY_OUTPUT_DIR, imgs[-1])
            print(f"  GPU 推理完成: {latest_img}")
        process_batch_image(latest_img, items)
        print(f"  [{bname}] 切片落盘成功！")

    print("\n=== 全部属性图标已批量完成！ ===")

if __name__ == "__main__":
    main()
