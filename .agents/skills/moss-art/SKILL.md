---
name: moss-art
description: "通过本地 ComfyUI 的 MOSS-Image (SFT + Qwen 3.5 9B) 与内置抠图节点 (RMBG-1.4)，按照官方《用户指南》的结构化 Layout JSON 规范，批量生成东方古典仙侠 2D 手绘厚涂风格的游戏道具、装备、副手与功法图标，并自动执行透明去底与 64px WebP 下采样落盘。"
---

# MOSS-Image 国风仙侠手绘游戏美术规范与自动化管线

本技能定义了基于本地 **MOSS-Image (SFT + Qwen 3.5 9B)** 的仙侠游戏道具、法宝与图标生成规范。适用于成组道具、同系列防具、副手法宝及技能图标的精细化局部控制生成与一键工程化落盘。

在撰写提示词前，请查阅 [layout-templates.md](references/layout-templates.md) 与 [xianxia-item-vocabulary.md](references/xianxia-item-vocabulary.md)。

---

## 1. 核心美术风格定位（对齐游戏原版）

1. **画风介质**：**东方古典玄幻修仙 2D 手绘厚涂（Painterly Thick-Coating with Subtle Ink Wash）**。
   - 拒绝现代低多边形折面（Low-Poly/Planar）；
   - 拒绝欧美魔兽暗黑 3D 次世代渲染；
   - 拒绝日漫高光平涂与二次元卡通感。
2. **骨骼特征：深墨色硬边手绘勾线（Dark Ink Contour Outline）**：
   - 每一个物件在透明背景边缘，必须保留一道 **RGB [15~35] 左右的深墨色手绘勾线**，确保图标在浅色（平色黄纸底）与深色（暗墨玄石底）UI 瓦片中均清晰分明。
3. **构图三大规约**：
   - **兵刃长物（刀/剑/杖/匕首/工具）**：**严格采用 45° 对角线贯穿构图**；
   - **防具衣冠（盔/巾/甲/裳/裤）**：**正面略带 15° 微俯视/微侧透视**，展示对称穿戴感；
   - **法宝副手（盾牌/宝珠/净瓶/法铃）**：**微俯角 30° 悬浮视角**，展示器型全貌。
4. **色彩沉稳古朴**：
   - 玄铁墨灰、暖赭木纹、暗哑古铜金、朱砂赤红、苍青温润白玉；
   - 灵光极其克制，仅作材质点睛之笔，绝无大面积弥散霓虹光晕。

---

## 2. 官方推荐推理参数配置（黄金组合）

依据 MOSS-Image 官方用户指南 (`USERGUIDE_ZH.md`) 与本地 AMD Radeon RX 7900 XT 实测：

- **分辨率 (Resolution)**：`1024×1024`（1:1 正方形）
- **采样步数 (Steps)**：**35 ~ 45 步**（官方要求 ≥50 最佳，35~40 步质感完全成熟）
- **CFG Scale**：**3.0 ~ 3.5**（长 Layout JSON 必须使用低 CFG，防止过饱和噪点）
- **Shift**：**5.0**（ModelSamplingSD3）
- **SLG (Skip Layer Guidance)**：**Layer 22**（0-index，关闭 SLG 细节会崩坏）
- **采样器 (Sampler)**：**Euler** + **SGM Uniform**

---

## 3. 标准 Layout JSON 空间布局提示词范式

MOSS-Image 采用中心原点坐标系（`abs_centralized`，范围 -512 ~ +512）与 `yxyx` 顺序（`[y_min, x_min, y_max, x_max]`）：

```json
{
  "summary": {
    "canvas": "single",
    "caption": "best quality, highres, a clean 2x2 grid sprite sheet of four Chinese Xianxia Daoist off-hand magic treasures on a pure solid white background. Traditional Chinese ink-wash hand-drawn painterly thick-coating art style, distinct dark ink outline contours, ancient Qin and Han dynasty aesthetic, muted antique colors, no modern elements, perfectly centered isolated assets."
  },
  "items": [
    {
      "label": "object",
      "caption": "Top-left item: [赤牙匕 - Crimson Fang Dagger]. A sharp curved throwing dagger forged from crimson beast fangs and dark iron, 45-degree diagonal dynamic composition, blade edge gleaming with cold silver light, handle wrapped in coarse black cord, dark ink hand-drawn outline.",
      "bbox": [-450, -450, -60, -60]
    },
    {
      "label": "object",
      "caption": "Top-right item: [曜灵珠 - Sun Radiance Spirit Bead]. A spherical floating celestial pearl carved from ancient solar crystal, translucent deep crimson and warm gold amber hues, faint inner spiritual radiance, tied with dark red braided tassels, dark ink hand-drawn outline.",
      "bbox": [-450, 60, -60, 450]
    },
    {
      "label": "object",
      "caption": "Bottom-left item: [赤铜盾 - Crimson Bronze Shield]. A sturdy Chinese Han-dynasty square kite shield made of oxidized crimson bronze and meteor iron, embossed with antique Daoist cloud-thunder patterns and a bronze beast-mask boss, dark ink hand-drawn outline, heavy defensive armor texture.",
      "bbox": [60, -450, 450, -60]
    },
    {
      "label": "object",
      "caption": "Bottom-right item: [回春铃 - Spring Rejuvenation Bell]. An ancient bronze Daoist ritual bell with emerald jade and pale green patina accents, hung with auspicious silk ribbon, symbolizing healing and vitality, dark ink hand-drawn outline.",
      "bbox": [60, 60, 450, 450]
    }
  ],
  "bbox_coord_space": "abs_centralized",
  "bbox_format": "yxyx"
}
```

### 核心负面提示词（精炼有效）：
```text
worst quality, bad anatomy, watermark, jpeg artifact, modern weapons, guns, western fantasy, 3d render, plastic, neon glow, blurry, human, face, messy background, text
```

---

## 4. 自动化生成与一键切片落盘流水线

本地脚本封装位于 `scripts/moss_generator.py`，支持直接一键执行：

```bash
# 查看所有可用预设批次
python .agents/skills/moss-art/scripts/moss_generator.py --help

# 试跑组装提示词（不提交）
python .agents/skills/moss-art/scripts/moss_generator.py --batch offhands_b --dry-run

# 实际提交 ComfyUI、等待 GPU 出图并自动切片落盘到 frontend/src/assets/items/
python .agents/skills/moss-art/scripts/moss_generator.py --batch offhands_b --steps 35 --cfg 3.5
```

### 自动化处理细节：
1. 自动提取 ComfyUI 产出的 1024×1024 大图；
2. 依据各 item 的 bbox 进行安全外扩（10%）切块；
3. 自动剔除纯白底，提取透明主体；
4. 按 **54px 边界等比居中** 缩放至 **64×64 RGBA WebP**（约 3~4 KB/张）；
5. 以真实游戏资产 ID（如 `equipment.xuantie-dart.webp`）直接落盘到游戏目录，瞬间在前端生效。


### 资产分流与工程红线：
- **母模与全拼图存档**：ComfyUI 产出的 1024×1024 完整原图（Sprite Sheet）与高分辨率原件切片，统一归档入 `art/` 目录（如 `art/items/`、`art/avatars/`、`art/buildings/`），留作设计源素材，不进入生产构建上下文；
- **前端生产运行时发布**：前端目录（`frontend/src/assets/items/`、`frontend/public/`）只承接压缩优化后的 64×64 RGBA WebP（图标道具）或透明 WebP（怪物立绘），严禁将大体积 PNG 源图直接塞入前端。