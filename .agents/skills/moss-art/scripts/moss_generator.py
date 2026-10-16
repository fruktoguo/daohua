#!/usr/bin/env python3
"""
道化太初 · MOSS-Image 本地自动化生图与资产切片工具
通过本地 ComfyUI REST API (MOSS-Image + Qwen 3.5 9B + RMBG 抠图) 
实现多区域精细布局生成、自动切片、去底与 64px WebP 一键落盘。
"""

import os
import sys
import json
import time
import random
import argparse
import urllib.request
import urllib.parse
from PIL import Image
import numpy as np

COMFY_HOST = "http://127.0.0.1:8188"
CURRENT_DIR = os.path.dirname(os.path.abspath(__file__))
# 从 .agents/skills/moss-art/scripts 向上回退 4 层到达仓库根目录
REPO_ROOT = os.path.abspath(os.path.join(CURRENT_DIR, "..", "..", "..", ".."))
OUTPUT_DIR = os.path.join(REPO_ROOT, "frontend", "src", "assets", "items")
COMFY_OUTPUT_DIR = r"D:\Tools\AI\ComfyUI-Shared\output"

# 预设系列道具定义
PRESET_BATCHES = {
    # 副手装备 B 组 (4件套, 2x2 网格)
    # 副手装备 A 组 (4件套, 2x2 网格)
    "offhands_a": [
        {
            "id": "equipment.fan-tie-dart",
            "name": "兽骨匕",
            "bbox": [-400, -400, -130, -130], # Top-Left
            "caption": "Top-left item: [兽骨匕 - Bone Dagger]. A stylized curved dagger carved from ancient pale beast bone, 45-degree diagonal dynamic composition, clean bold silhouette, wrapped dark cord hilt, compact and neat, dark ink outline."
        },
        {
            "id": "equipment.qingmu-bead",
            "name": "桃灵珠",
            "bbox": [-400, 130, -130, 400], # Top-Right
            "caption": "Top-right item: [桃灵珠 - Peach-wood Spirit Bead]. A smooth floating celestial bead carved from centuries-old peach wood, warm amber wood grain, simple red cinnabar braided tassel below, clean spherical silhouette, dark ink outline."
        },
        {
            "id": "equipment.tiepi-guard",
            "name": "精铁盾",
            "bbox": [130, -400, 400, -130], # Bottom-Left
            "caption": "Bottom-left item: [精铁盾 - Heavy Iron Shield]. A sturdy Chinese Han-dynasty square kite shield made of dark meteor iron, clean bold defensive shape, subtle bronze boss in center, compact and sturdy, dark ink outline."
        },
        {
            "id": "equipment.ganlu-flask",
            "name": "甘露瓶",
            "bbox": [130, 130, 400, 400], # Bottom-Right
            "caption": "Bottom-right item: [甘露瓶 - Jade Dew Flask]. An elegant celadon jade vase flask, slender neck, tied with a simple gold silk ribbon, clean peaceful Daoist bottle silhouette, dark ink outline."
        }
    ],
    "offhands_b": [
        {
            "id": "equipment.xuantie-dart",
            "name": "赤牙匕",
            "bbox": [-400, -400, -130, -130], # Top-Left: 紧凑居中，外围大间隙
            "caption": "Top-left item: [赤牙匕 - Crimson Fang Dagger]. A stylized curved beast-fang dagger, 45-degree diagonal dynamic composition, clean bold silhouette, crimson red blade with cold edge, dark wrapped hilt, compact and neat, dark ink outline."
        },
        {
            "id": "equipment.yaoling-bead",
            "name": "曜灵珠",
            "bbox": [-400, 130, -130, 400], # Top-Right: 紧凑居中，外围大间隙
            "caption": "Top-right item: [曜灵珠 - Sun Radiance Bead]. A simple smooth floating celestial pearl, warm amber and crimson red hues, minimalist red cord tassel below, clean spherical shape, dark ink outline."
        },
        {
            "id": "equipment.xuanjia-guard",
            "name": "赤铜盾",
            "bbox": [130, -400, 400, -130], # Bottom-Left: 紧凑居中，外围大间隙
            "caption": "Bottom-left item: [赤铜盾 - Crimson Bronze Shield]. A clean Chinese Han-dynasty square kite shield, bold oxidized red-bronze surface, simple bronze beast-mask in center, sturdy and compact, dark ink outline."
        },
        {
            "id": "equipment.huichun-bell",
            "name": "回春铃",
            "bbox": [130, 130, 400, 400], # Bottom-Right: 紧凑居中，外围大间隙
            "caption": "Bottom-right item: [回春铃 - Spring Rejuvenation Bell]. A stylized ancient bronze Daoist bell, simple elegant bell silhouette, jade green patina accents, clean and compact, dark ink outline."
        }
    ],
    # 物理输出防具 6 件套 (T1 三件套 + T2 三件套, 2x3 六宫格网格)
    "physical_armors_6": [
        # 行 1: T1 物理输出三件套 (巾/甲/裤)
        {
            "id": "equipment.jinfeng-scarf",
            "name": "劲风巾",
            "bbox": [-400, -440, -120, -180],
            "caption": "Top-left item: [劲风巾 - Jinfeng Wind Headband]. A stylized navy-blue martial artist headband, bold simple silhouette, flowing sharp ribbon tails, dark ink outline."
        },
        {
            "id": "equipment.qingpi-armor",
            "name": "轻皮甲",
            "bbox": [-400, -130, -120, 130],
            "caption": "Top-center item: [轻皮甲 - Light Leather Armor]. A stylized sleeveless combat cuirass sewn from dark cured leather, bronze shoulder rivets, simple symmetrical frontal view, dark ink outline."
        },
        {
            "id": "equipment.jixing-pants",
            "name": "疾行裤",
            "bbox": [-400, 180, -120, 440],
            "caption": "Top-right item: [疾行裤 - Swift Strider Pants]. Stylized dark martial artist pants with brown leather gaiter wraps around shins, clean silhouette, dark ink outline."
        },
        # 行 2: T2 物理输出三件套 (巾/甲/裤)
        {
            "id": "equipment.zhanfeng-scarf",
            "name": "斩风巾",
            "bbox": [120, -440, 400, -180],
            "caption": "Bottom-left item: [斩风巾 - Windcleaver Headband]. An elite martial headband with silver wind-crest emblem and flying dark ribbons, sharp agile silhouette, dark ink outline."
        },
        {
            "id": "equipment.liegang-armor",
            "name": "裂罡甲",
            "bbox": [120, -130, 400, 130],
            "caption": "Bottom-center item: [裂罡甲 - Astral Breaker Armor]. A heavy battle harness made of laminated black iron plates and demon beast carapace, symmetrical frontal view, dark ink outline."
        },
        {
            "id": "equipment.tafeng-pants",
            "name": "踏风裤",
            "bbox": [120, 180, 400, 440],
            "caption": "Bottom-right item: [踏风裤 - Windstrider Pants]. Dark martial trousers reinforced with segmented iron shin guards, clean sharp silhouette, dark ink outline."
        }
    ],
    # 生产生活工具组 1 (采药/采矿/炼丹/锻造 4件套, 2x2 网格)
    "life_tools_4": [
        {
            "id": "equipment.tool-herb-hoe",
            "name": "桃木铲",
            "bbox": [-400, -400, -130, -130],
            "caption": "Top-left item: [桃木铲 - Peach-wood Herbal Spade]. A compact traditional herbalist hand spade carved from aged peach wood, smooth wooden handle, clean blade for digging spirit herbs, dark ink outline."
        },
        {
            "id": "equipment.tool-prospect-compass",
            "name": "精铁镐",
            "bbox": [-400, 130, -130, 400],
            "caption": "Top-right item: [精铁镐 - Iron Mining Pick]. A durable compact hand pickaxe forged from dark refined iron, angled pointed head for mining ore, wrapped wooden grip, dark ink outline."
        },
        {
            "id": "equipment.tool-flame-ding",
            "name": "灵火炉",
            "bbox": [130, -400, 400, -130],
            "caption": "Bottom-left item: [灵火炉 - Spirit Flame Cauldron]. A small portable bronze alchemy crucible burner, tripod legs, gentle warm spiritual flame glow inside, dark ink outline."
        },
        {
            "id": "equipment.tool-forge-hammer",
            "name": "百炼铁钳",
            "bbox": [130, 130, 400, 400],
            "caption": "Bottom-right item: [百炼铁钳 - Master Blacksmith Tongs]. Sturdy Chinese blacksmith iron tongs for holding red-hot metals, simple strong mechanical hinge, dark ink outline."
        }
    ],
    # 生活工具与阵法道具组 2 (制符/布阵/淬灵 + 符箓/阵盘/功德 6件套, 2x3 网格)
    "talisman_formation_6": [
        {
            "id": "equipment.tool-talisman-brush",
            "name": "桃木笔",
            "bbox": [-400, -440, -120, -180],
            "caption": "Top-left item: [桃木笔 - Talisman Calligraphy Brush]. An ancient calligraphy brush carved from peach wood, fine dark animal hair tip, red cinnabar accents, dark ink outline."
        },
        {
            "id": "equipment.tool-formation-flag",
            "name": "桃晶旗",
            "bbox": [-400, -130, -120, 130],
            "caption": "Top-center item: [桃晶旗 - Formation Banner]. A small triangular Daoist spiritual formation flag made of yellow silk, attached to a peach-wood pole, simple trigram symbol, dark ink outline."
        },
        {
            "id": "equipment.tool-quench-anvil",
            "name": "淬灵锤",
            "bbox": [-400, 180, -120, 440],
            "caption": "Top-right item: [淬灵锤 - Quenching Hammer]. A heavy square blacksmith hammer forged from meteoric black iron, polished striking face, dark ink outline."
        },
        {
            "id": "item.leiji-talisman",
            "name": "雷击符",
            "bbox": [120, -440, 400, -180],
            "caption": "Bottom-left item: [雷击符 - Thunderstrike Talisman]. A traditional Daoist paper talisman on yellow spirit paper, vermilion cinnabar lightning runes drawn boldly, dark ink outline."
        },
        {
            "id": "item.juling-formation-disk",
            "name": "聚灵阵盘",
            "bbox": [120, -130, 400, 130],
            "caption": "Bottom-center item: [聚灵阵盘 - Spirit Gathering Formation Disk]. An octagonal bronze Daoist formation plate engraved with concentric bagua trigrams, azure spiritual glow in center, dark ink outline."
        },
        {
            "id": "item.gongde",
            "name": "功德",
            "bbox": [120, 180, 400, 440],
            "caption": "Bottom-right item: [功德 - Merit Coin]. A glowing circular celestial coin forged from golden merit light, carved with cloud patterns, pure auspicious aura, dark ink outline."
        }
    ],
    # 内功体系批次 A (5门基础心法技能 + 基础秘籍线装书, 2x3 网格)
    "internal_abilities_a": [
        {
            "id": "ability.tuna-gong",
            "name": "吐纳功",
            "bbox": [-400, -440, -120, -180],
            "caption": "Top-left item: [吐纳功 - Breathing Qi Art]. A stylized Daoist breath cultivation icon, swirling white and cyan mist forming a serene circular vortex, peaceful meditative aura, dark ink outline."
        },
        {
            "id": "ability.manniu-jin",
            "name": "蛮牛劲",
            "bbox": [-400, -130, -120, 130],
            "caption": "Top-center item: [蛮牛劲 - Raging Bull Force]. A stylized ferocious bronze ox bull head emblem, crimson red and earthy brown hues, raw physical brute force, dark ink outline."
        },
        {
            "id": "ability.tiebu-shan",
            "name": "铁布衫",
            "bbox": [-400, 180, -120, 440],
            "caption": "Top-right item: [铁布衫 - Iron Shirt Defense]. A stylized ancient iron bell shield aura enclosing an iron silhouette, deep slate iron gray and dull bronze, impenetrable defense, dark ink outline."
        },
        {
            "id": "ability.feiyan-gong",
            "name": "飞燕功",
            "bbox": [120, -440, 400, -180],
            "caption": "Bottom-left item: [飞燕功 - Flying Swallow Grace]. A stylized agile swallow bird in dynamic soaring flight, emerald green and swift wind arcs, graceful agility, dark ink outline."
        },
        {
            "id": "ability.qingxin-jue",
            "name": "清心诀",
            "bbox": [120, -130, 400, 130],
            "caption": "Bottom-center item: [清心诀 - Clear Heart Art]. A stylized luminous pale blue lotus flower floating in spiritual water, ice blue dewdrop in center, serene mental clarity, dark ink outline."
        },
        {
            "id": "base_tome",
            "name": "基础秘籍线装书",
            "bbox": [120, 180, 400, 440],
            "caption": "Bottom-right item: [基础秘籍 - Daoist Manual Tome]. An antique traditional Chinese thread-bound martial arts book with blue-indigo fabric cover, white label strip on left, clean front 3/4 perspective, dark ink outline."
        }
    ],
    # 内功体系批次 B (5门进阶心法技能 + 基础残卷古页, 2x3 网格)
    "internal_abilities_b": [
        {
            "id": "ability.fenxue-gong",
            "name": "焚血功",
            "bbox": [-400, -440, -120, -180],
            "caption": "Top-left item: [焚血功 - Blood Burning Art]. A stylized demonic flame heart emblem, boiling crimson blood vapors and violent fiery orange tendrils, explosive sacrifice power, dark ink outline."
        },
        {
            "id": "ability.xiaozhoutian-gong",
            "name": "小周天功",
            "bbox": [-400, -130, -120, 130],
            "caption": "Top-center item: [小周天功 - Microcosmic Orbit]. A stylized golden meridian circulation circle with glowing acupoints, pure solar gold and amber Daoist energy stream, harmonious balance, dark ink outline."
        },
        {
            "id": "ability.cunshen-jue",
            "name": "存神诀",
            "bbox": [-400, 180, -120, 440],
            "caption": "Top-right item: [存神诀 - Soul Enshrining Art]. A stylized mystical all-seeing third eye glowing with violet spiritual consciousness, radiant purple mist, profound Daoist meditation, dark ink outline."
        },
        {
            "id": "ability.lianjing-huaqi-jue",
            "name": "炼精化气诀",
            "bbox": [120, -440, 400, -180],
            "caption": "Bottom-left item: [炼精化气诀 - Essence Transmutation]. A stylized internal cinnabar tripod crucible refining scarlet vital essence into translucent white spiritual vapor, alchemical sublimation, dark ink outline."
        },
        {
            "id": "ability.xuanyuan-yiqi-gong",
            "name": "玄元一气功",
            "bbox": [120, -130, 400, 130],
            "caption": "Bottom-center item: [玄元一气功 - Primordial Origin Qi]. A grand cosmic primordial taiji vortex of deep azure and profound gold, swirling starry primordial vapor, supreme orthodox Daoism, dark ink outline."
        },
        {
            "id": "base_fragment",
            "name": "基础残卷古页",
            "bbox": [120, 180, 400, 440],
            "caption": "Bottom-right item: [基础残卷 - Ancient Scroll Fragment]. A weathered torn fragment of ancient golden silk parchment, frayed edges, faint mysterious cinnabar calligraphy runes, dark ink outline."
        }
    ],
    # 首领怪物立绘 (筑基期 Lv18 玄元残魄)
    "boss_xuanyuan_remnant": [
        {
            "id": "xuanyuan-remnant",
            "name": "玄元残魄",
            "bbox": [-450, -420, 450, 420],
            "caption": "Centered character: [玄元残魄 - Xuanyuan Sect Remnant Soul]. A powerful ancient Taoist sect master's ethereal ghostly apparition phantom, floating in mid-air, translucent azure spiritual ghost robes torn at edges, stern ethereal spectral bearded face with glowing eyes, floating ancient cracked bronze talisman sword by his side, surrounded by gentle dark cyan spiritual ectoplasm flame mist, dark ink outline, majestic and tragic Daoist boss aura."
        }
    ]
}

def get_latest_workflow():
    with urllib.request.urlopen(f"{COMFY_HOST}/history") as resp:
        history = json.loads(resp.read().decode())
    if not history:
        raise RuntimeError("No history found in ComfyUI.")
    
    # 优先从历史中获取包含 Node 27 (easy imageRemBg) 的工作流
    for pid in reversed(list(history.keys())):
        p = history[pid].get("prompt", [None, None, {}])[2]
        if "27" in p and p["27"].get("class_type") == "easy imageRemBg":
            wf = json.loads(json.dumps(p))
            wf["18"]["inputs"]["images"] = ["27", 0]
            return wf
            
    # 若历史中未找到，则取最新并显式注入 Node 27 抠图节点
    latest_id = list(history.keys())[-1]
    wf = json.loads(json.dumps(history[latest_id]["prompt"][2]))
    wf["27"] = {
        "inputs": {
            "rem_mode": "RMBG-1.4",
            "image_output": "Preview",
            "save_prefix": "ComfyUI",
            "torchscript_jit": False,
            "add_background": "none",
            "refine_foreground": False,
            "images": ["26", 0] if "26" in wf else ["17", 0]
        },
        "class_type": "easy imageRemBg",
        "_meta": {"title": "图像背景移除"}
    }
    if "18" in wf:
        wf["18"]["inputs"]["images"] = ["27", 0]
    return wf

def build_layout_prompt(items_spec, summary_caption=None):
    if not summary_caption:
        summary_caption = "simplified stylized 2D Chinese Xianxia game item icon sprite sheet, clean bold shapes, simplified painterly ink-wash style, clear silhouette, restrained details, bold brushwork, flat clean colors with subtle shading, distinct dark ink outline, compact icons floating on solid pure white background, generous empty white spacing between items, strictly no overflow."
    
    layout = {
        "summary": {
            "canvas": "single",
            "caption": summary_caption
        },
        "items": [
            {
                "label": "object",
                "caption": it["caption"],
                "bbox": it["bbox"]
            }
            for it in items_spec
        ],
        "bbox_coord_space": "abs_centralized",
        "bbox_format": "yxyx"
    }
    return json.dumps(layout, ensure_ascii=False, indent=2)

def submit_task(workflow, prompt_str, seed=None, steps=35, cfg=3.5, prefix="daohua"):
    prompt = json.loads(json.dumps(workflow)) # deepcopy
    
    # Node 4: 正向提示词
    prompt["4"]["inputs"]["text"] = prompt_str
    
    # Node 5: 负向提示词
    prompt["5"]["inputs"]["text"] = "overcomplicated, hyper-detailed, intricate noise, realistic photo, 3d render, overflow, touching borders, crowded, clutter, modern, western fantasy, blurry, bad anatomy, worst quality, watermark, text"
    
    # Node 19: 分辨率 1:1
    if "19" in prompt:
        prompt["19"]["inputs"]["aspect_ratio"] = "1:1 (Square)"
        prompt["19"]["inputs"]["megapixels"] = 1.0
        
    # Node 16: KSampler 参数调优 (按官方推荐: 35步, CFG 3.5)
    if "16" in prompt:
        prompt["16"]["inputs"]["steps"] = steps
        prompt["16"]["inputs"]["cfg"] = cfg
        prompt["16"]["inputs"]["seed"] = seed if seed is not None else random.randint(100000000000000, 999999999999999)
        prompt["16"]["inputs"]["sampler_name"] = "euler"
        prompt["16"]["inputs"]["scheduler"] = "sgm_uniform"
        
    # Node 18: SaveImage
    if "18" in prompt:
        prompt["18"]["inputs"]["filename_prefix"] = prefix

    # 提交
    payload = json.dumps({"prompt": prompt, "client_id": "daohua_moss_gen"}).encode("utf-8")
    req = urllib.request.Request(f"{COMFY_HOST}/prompt", data=payload, headers={"Content-Type": "application/json"})
    with urllib.request.urlopen(req) as resp:
        res = json.loads(resp.read().decode())
    return res["prompt_id"]

def wait_for_completion(prompt_id, timeout=120):
    start = time.time()
    while time.time() - start < timeout:
        time.sleep(2)
        try:
            with urllib.request.urlopen(f"{COMFY_HOST}/history/{prompt_id}") as resp:
                data = json.loads(resp.read().decode())
                if prompt_id in data and data[prompt_id].get("status", {}).get("completed", False):
                    outputs = data[prompt_id].get("outputs", {})
                    images = []
                    for nid, nout in outputs.items():
                        for img in nout.get("images", []):
                            images.append(img["filename"])
                    return images
        except Exception:
            pass
    raise TimeoutError(f"Task {prompt_id} timed out after {timeout}s.")

def process_and_slice(image_path, items_spec, output_dir=OUTPUT_DIR, target_size=64, inner_size=54):
    os.makedirs(output_dir, exist_ok=True)
    im = Image.open(image_path).convert("RGBA")
    w_full, h_full = im.size
    cx, cy = w_full / 2.0, h_full / 2.0
    
    saved_paths = []
    for it in items_spec:
        # bbox 格式: [y_min, x_min, y_max, x_max] (相对中心点)
        ymin, xmin, ymax, xmax = it["bbox"]
        box = (int(cx + xmin), int(cy + ymin), int(cx + xmax), int(cy + ymax))
        # 外扩 12% 避免边缘轻微裁切，由于已有 RMBG 抠图透明背景，各道具天然独立
        pad_x = int((box[2] - box[0]) * 0.12)
        pad_y = int((box[3] - box[1]) * 0.12)
        safe_box = (
            max(0, box[0] - pad_x),
            max(0, box[1] - pad_y),
            min(w_full, box[2] + pad_x),
            min(h_full, box[3] + pad_y)
        )
        crop = im.crop(safe_box)
        
        # 自动去白底
        arr = np.array(crop)
        r, g, b, a = arr[:, :, 0], arr[:, :, 1], arr[:, :, 2], arr[:, :, 3]
        is_bg = (r > 235) & (g > 235) & (b > 235)
        arr[:, :, 3] = np.where(is_bg, 0, a)
        clean_crop = Image.fromarray(arr)
        
        # 提取真实内容 BBox
        c_bbox = clean_crop.getbbox()
        if c_bbox:
            clean_crop = clean_crop.crop(c_bbox)
            
        cw, ch = clean_crop.size
        scale = inner_size / max(cw, ch)
        new_w, new_h = max(1, int(cw * scale)), max(1, int(ch * scale))
        resized = clean_crop.resize((new_w, new_h), Image.Resampling.LANCZOS)
        
        canvas = Image.new("RGBA", (target_size, target_size), (0, 0, 0, 0))
        canvas.paste(resized, ((target_size - new_w) // 2, (target_size - new_h) // 2), resized)
        
        out_name = f"{it['id']}.webp"
        out_path = os.path.join(output_dir, out_name)
        canvas.save(out_path, format="WEBP", lossless=True)
        saved_paths.append((it["name"], out_path))
        print(f"  [EXPORTED] {it['name']} ({it['id']}) -> {out_path} ({os.path.getsize(out_path)} bytes)")
        
    return saved_paths

def main():
    parser = argparse.ArgumentParser(description="MOSS-Image 本地修仙图标批量生成与切片工具")
    parser.add_argument("--batch", choices=list(PRESET_BATCHES.keys()), help="预设批次名称")
    parser.add_argument("--steps", type=int, default=35, help="推理步数 (推荐 35-45)")
    parser.add_argument("--cfg", type=float, default=3.5, help="CFG 引导比例 (推荐 3.0-4.0)")
    parser.add_argument("--seed", type=int, default=None, help="随机种子")
    parser.add_argument("--dry-run", action="store_true", help="仅组装提示词，不实际提交")
    args = parser.parse_args()
    
    if not args.batch:
        parser.print_help()
        return 0
        
    items = PRESET_BATCHES[args.batch]
    prompt_str = build_layout_prompt(items)
    
    print(f"\n=== MOSS-Image 任务组装: 批次 [{args.batch}] (包含 {len(items)} 个道具) ===")
    print(prompt_str)
    
    if args.dry_run:
        print("\n[DRY RUN] 提示词已组装完毕，未提交到 ComfyUI。")
        return 0
        
    print("\n正在获取 ComfyUI 最新工作流...")
    wf = get_latest_workflow()
    print("正在向 ComfyUI (127.0.0.1:8188) 提交生图任务...")
    pid = submit_task(wf, prompt_str, seed=args.seed, steps=args.steps, cfg=args.cfg, prefix=f"daohua-{args.batch}")
    print(f"任务已排队，Prompt ID: {pid}")
    
    print("正在等待 GPU 推理完成...")
    images = wait_for_completion(pid)
    print(f"推理完成！产出大图: {images}")
    
    # 找到最新产物物理路径
    latest_img = os.path.join(COMFY_OUTPUT_DIR, images[-1])
    if args.batch.startswith("boss_"):
        out_dir = os.path.join(REPO_ROOT, "frontend", "public", "avatars")
        t_size, in_size = 256, 230
        print(f"开始自动切片、去底与 256px WebP 怪物立绘下采样: {latest_img} -> {out_dir}")
    else:
        out_dir = OUTPUT_DIR
        t_size, in_size = 64, 54
        print(f"开始自动切片、去底与 64px WebP 道具图标下采样: {latest_img} -> {out_dir}")
    saved = process_and_slice(latest_img, items, output_dir=out_dir, target_size=t_size, inner_size=in_size)
    print(f"\n=== 批次 [{args.batch}] 全部切片成功并落盘至工程目录！ ===")
    return 0

if __name__ == "__main__":
    sys.exit(main())
