---
name: moss-art
description: "基于本地 ComfyUI MOSS-Image (SFT + Qwen 3.5 9B) 与内置抠图节点 (RMBG-1.4)，按照官方结构化 Layout JSON 空间布局范式，全流程支持游戏视觉资产（暗黑折面妖兽怪物立绘、修仙人物道貌、2D水墨手绘厚涂装备道具图标、宗门建筑地标、二值矢量图腾、大地图场景横幅）的精细化局部解耦控制生成与工程化落盘。"
---

# MOSS-Image 全量仙侠视觉资产自动化生成规范与工程管线

本技能定义了基于本地 **MOSS-Image (SFT + Qwen 3.5 9B)** 的全域仙侠游戏视觉资产生产规范。通过结构化 **Layout JSON** 实现多区域精准空间注意力控制（Spatial Attention Guidance），彻底解决纯文本提示词在形体失控、写实水流/水汽污染、构图混乱和细节杂碎的问题。

---

## 1. 核心视觉资产矩阵与美术风格双轨制

《道化太初》包含两大核心视觉体系，MOSS-Image 均提供标准 Layout JSON 与参数支持：

### 体系 A：冷峻极简折面妖煞生灵与角色立绘（Planar Dark Fantasy）
- **适用标的**：妖兽怪物立绘（小怪/精英/关底Boss）、异灵生灵、修仙者道貌形象。
- **画风美学**：
  1. **少而精的折面**：采用大色块折角与锐利硬边切角，构建凶残、冷酷、暗黑压迫感形体；绝无圆润呆萌宠物感，绝无低模碎线杂乱。
  2. **严格色彩预算**：
     - **纯黑实底（#000000）**：提供极高对比与纸板装裱感；
     - **近黑主体（60% ~ 75%）**：主体深色玄墨肉身/骨架（`#121212`），承载实体绝大部分质量；
     - **骨白结构（15% ~ 25%）**：1~2 块宽大锐利的闭合骨白面具、骨刺、獠牙或外甲板（`#f5f5f4`）；
     - **单饱和强调色（5% ~ 10%）**：单一高饱和纯色晶核/法力回路（幽深群青蓝、血赤、碧翠、玄金），**严禁漫射发光与渐变水汽**；
     - **墨黑呼吸槽**：骨白面与强调色之间严格保留一道深墨黑的隔离缝隙。
  3. **纸板装裱与描边**：在实体最外轮廓附带一圈闭合的细暖骨白纸板描边，描边与主体之间以一道极窄墨黑阴刻凹槽物理分隔。
  4. **96px 高辨识度**：缩至小尺寸依然清晰辨识出「剪影大体态 + 骨白面具 + 单彩晶核」。

### 体系 B：东方古典玄幻修仙 2D 手绘厚涂（Painterly Thick-Coating）
- **适用标的**：武器、防具、副手、法宝、丹药材料、技能战技图标、宗门建筑设施。
- **画风美学**：
  1. **深墨色硬边手绘勾线**：透明背景边缘保留一道 RGB [15~35] 左右的深墨色手绘勾线，确保浅色与深色 UI 瓦片下均清晰穿透。
  2. **三大构图规约**：
     - 兵刃长物（刀/剑/杖/工具）：**严格 45° 对角线贯穿构图**；
     - 防具衣冠（盔/巾/甲/袍/裤）：**正面微俯 15° 对称透视**，展示穿戴感；
     - 法宝副手（盾牌/宝珠/净瓶/法铃）：**微俯角 30° 悬浮视角**，展示器型全貌；
     - 宗门建筑（道庐/工坊/阵法）：**微仰角或近正交透视**，檐角飞扬、道韵深远。
  3. **沉稳古朴色调**：玄铁墨灰、暖赭木纹、暗哑古铜金、朱砂赤红、苍青温润白玉，绝无大面积弥散霓虹光晕。

### 体系 C：纯白二值镂空矢量图腾（Hard Binary Stencil Vector）
- **适用标的**：线装秘籍与心法残卷封面徽标、角色属性小图标（CSS mask 动态染色）。
- **画风美学**：纯黑实底（#000000）+ 纯白二值实心矢量剪影，剪纸镂空（Stencil Cut）与负空间黑线（Negative Black Contour），零渐变、零阴影、零灰阶。

---

## 2. 官方推荐推理参数配置（黄金组合）

依据 MOSS-Image 官方用户指南 (`USERGUIDE_ZH.md`) 与本地 AMD Radeon RX 7900 XT 实测：

- **分辨率 (Resolution)**：`1024×1024`（1:1 正方形）或 `1024×576`（16:9 横屏）
- **采样步数 (Steps)**：**35 ~ 45 步**
- **CFG Scale**：**3.0 ~ 3.5**（长 Layout JSON 必须使用低 CFG，防止过饱和噪点）
- **Shift**：**5.0**（ModelSamplingSD3）
- **SLG (Skip Layer Guidance)**：**Layer 22**（0-index，关闭 SLG 细节会崩坏）
- **采样器 (Sampler)**：**Euler** + **SGM Uniform**

---

## 3. 结构化 Layout JSON 空间布局范式库

MOSS-Image 采用中心原点坐标系（`abs_centralized`，范围 -512 ~ +512）与 `yxyx` 顺序（`[y_min, x_min, y_max, x_max]`）。

### 范式 1：单体多部件解耦布局（怪物/Boss/角色立绘精细控制）
**适用**：单只暗黑折面怪物立绘生成。将单体拆分为 `body silhouette`、`bone mask`、`demonic core`，用局部 BBox 强行锚定构图，彻底根除流体与写实水汽失控：

```json
{
  "summary": {
    "canvas": "single",
    "caption": "A minimalist 2D game sprite of [潮汐之灵 - Spirit of the Tides] in Chinese Xianxia dark fantasy paper-cut planar polygonal silhouette art style on a solid pure black background (#000000). High contrast flat 2D graphic, sharp angular hard edges, distinct warm bone-white cardboard outer contour outline with a narrow inner black incised groove, pure flat solid color fills, strictly zero gradients, zero soft shading, zero blur, zero realistic fluid water splash, zero modern 3D rendering, perfectly centered isolated game asset, highly readable at 96px."
  },
  "items": [
    {
      "label": "character body silhouette",
      "bbox": [-380, -240, 360, 240],
      "caption": "The main body mass of the floating water spirit. A sleek, imposing, continuous solid near-black planar polygonal silhouette (#121212) shaped like a tapered inverted triangle, ending below in two sharp rigid downward-pointing dark tails. Clean sharp faceted angular cutouts, solid opaque dark mass taking 70% area, strictly no gradients, no mist trails."
    },
    {
      "label": "bone-white armor mask",
      "bbox": [-280, -80, -90, 180],
      "caption": "A prominent sharp bone-white Daoist water-talisman faceplate armor mask mounted on the upper front of the dark body. Crisp flat ivory-white angular geometric facet plate with a tiny sharp glowing cyan diamond eye slit. Bold graphic shape, 20% area, distinct hard edges, strictly solid flat white."
    },
    {
      "label": "demonic energy core",
      "bbox": [-40, -70, 110, 70],
      "caption": "A single solid saturated pure deep ocean blue (#0284c7) hexagonal water spirit energy core crystal embedded firmly in the center chest, isolated single accent color (8% area), strictly flat solid color with no diffuse glow, completely surrounded by a thin near-black breathing groove separating it from the white mask above."
    }
  ],
  "bbox_coord_space": "abs_centralized",
  "bbox_format": "yxyx"
}
```

### 范式 2：2×2 四宫格网格布局（成组副手/法宝/材料/技能）
**适用**：一次性批量产出 4 件成套道具，自动切片为 64px WebP：

```json
{
  "summary": {
    "canvas": "single",
    "caption": "simplified stylized 2D Chinese Xianxia game item icon sprite sheet, clean bold shapes, simplified painterly ink-wash style, clear silhouette, restrained details, distinct dark ink outline, compact icons floating on solid pure white background, generous empty white spacing between items, strictly no overflow."
  },
  "items": [
    {
      "label": "object",
      "bbox": [-420, -420, -80, -80],
      "caption": "Top-left item: [玄铁长剑 - Dark Iron Longsword]. A straight double-edged Chinese Daoist Jian sword forged from deep black meteor iron, cold sharp silver-white polished blade edge, 45-degree diagonal composition, dark ink outline."
    },
    {
      "label": "object",
      "bbox": [-420, 80, -80, 420],
      "caption": "Top-right item: [劈岩重刀 - Rock-Cleaving Heavy Dao]. A massive single-edged Chinese heavy broadsword saber, thick notched dark steel blade, 45-degree diagonal composition, dark ink outline."
    },
    {
      "label": "object",
      "bbox": [80, -420, 420, -80],
      "caption": "Bottom-left item: [枯藤法杖 - Withered Vine Staff]. An ancient Daoist gnarled wooden walking staff carved from thousand-year spirit vines, 45-degree diagonal composition, dark ink outline."
    },
    {
      "label": "object",
      "bbox": [80, 80, 420, 420],
      "caption": "Bottom-right item: [鸣泉古琴 - Spring-Resonating Guqin]. An elegant Chinese classical seven-string Guqin zither musical weapon, 45-degree diagonal composition, dark ink outline."
    }
  ],
  "bbox_coord_space": "abs_centralized",
  "bbox_format": "yxyx"
}
```

### 范式 3：2×3 六宫格布局（防具三件套×2、材料组）
- 行 1（顶部 3 件）：`[-400, -440, -120, -180]`, `[-400, -130, -120, 130]`, `[-400, 180, -120, 440]`
- 行 2（底部 3 件）：`[120, -440, 400, -180]`, `[120, -130, 400, 130]`, `[120, 180, 400, 440]`

### 范式 4：纯黑底纯白形二值图腾（Logos 4 选 1）
**适用**：秘籍封面图腾与属性小图标母模，取未抠图原图执行硬二值化阈值切片：
```json
{
  "summary": {
    "canvas": "single",
    "caption": "a 2x2 grid sprite sheet of 4 minimalist bold vector logo icons, stencil cut silhouette graphic symbol marks, strictly solid pure white icons on solid pure black background, high contrast binary black and white, zero gradients, zero shading, zero colors, crisp clean stencil cut lines and distinct negative space contours, sharp graphic edges, flat 2D vector clip-art."
  },
  "items": [
    {
      "label": "object",
      "bbox": [-420, -420, -80, -80],
      "caption": "Top-left item: [潮汐图腾候选 - Tidal Wave Emblem Candidate]. A solid filled pure white minimalist vector silhouette logo of three stylized ocean wave crests surging upwards into a spiral vortex, crisp clean stencil cut negative black contour lines separating wave layers, strictly solid pure white on solid pure black."
    }
  ],
  "bbox_coord_space": "abs_centralized",
  "bbox_format": "yxyx"
}
```

---

## 4. 全能自动化生成脚本与管线 (`scripts/moss_generator.py`)

本地封装脚本已全面升级为全能视觉生成管线，支持预设批次、自定义 Layout JSON、单体多部件解耦整合与大类智能路由分流：

```bash
# 查看帮助与支持的全部大类
python .agents/skills/moss-art/scripts/moss_generator.py --help

# 1. 运行内置预设批次 (例如武器4件套、防具6件套、建筑组)
python .agents/skills/moss-art/scripts/moss_generator.py --batch weapons_trail_4 --steps 35 --cfg 3.5

# 2. 运行单体多部件解耦 Layout JSON (例如潮汐之灵单体怪物立绘)
python .agents/skills/moss-art/scripts/moss_generator.py \
  --layout path/to/tide_spirit_layout.json \
  --single-entity enemy.tide-spirit \
  --category monster \
  --steps 35 --cfg 3.5

# 3. 运行宗门建筑单体或批次
python .agents/skills/moss-art/scripts/moss_generator.py \
  --layout path/to/building_layout.json \
  --single-entity building.cloud-pavilion \
  --category building

# 4. 纯白二值矢量图腾批量生成
python .agents/skills/moss-art/scripts/moss_generator.py --batch logos_tide
# 配套执行线装书程序化合成：
python .agents/skills/moss-art/scripts/composite_manuals.py
```

### 参数说明表：
| 参数 | 说明 | 示例 |
|---|---|---|
| `--batch` | 预设批次名（28 个常用批次） | `--batch weapons_trail_4` |
| `--layout` | 外部结构化 Layout JSON 文件路径或 JSON 字符串 | `--layout layout.json` |
| `--single-entity` | 单体资产 ID。指定后不执行九宫切片，自动提取整体透明主体并整合导出 | `--single-entity enemy.tide-spirit` |
| `--category` | 资产大类：`monster` / `character` / `item` / `equipment` / `ability` / `building` / `emblem` / `scene` | `--category monster` |
| `--aspect-ratio` | 画布长宽比（默认 `1:1 (Square)`，可选 `16:9 (Landscape)`、`4:3`） | `--aspect-ratio "16:9 (Landscape)"` |
| `--negative` | 自定义负向提示词覆盖 | `--negative "..."` |
| `--dry-run` | 仅组装并打印 Layout JSON 与参数，不向 ComfyUI 提交推理 | `--dry-run` |

---

## 5. 资产分流与工程交付红线（必须遵守）

1. **设计真源母模统一归档至 `art/` 资产库**：
   - 怪物/角色母模：512px / 1024px WebP 归档至 `art/avatars/`；
   - 道具/装备/技能母模：128px WebP 归档至 `art/items/`；
   - 建筑母模：128px / 256px WebP 归档至 `art/buildings/`；
   - 图腾与小图标：128px WebP 归档至 `art/emblems/` 与 `art/glyphs/`；
   - `art/` 目录被 `.dockerignore` 全局忽略，不进入生产镜像，留作设计源素材。
2. **生产运行时发布目录（`frontend/`）只承接单一最优轻量化格式**：
   - 怪物立绘：高压缩透明 WebP（`frontend/public/avatars/`，标准 128×128 或 256×256）；
   - 角色道貌立绘：纯矢量 `.svg`（`frontend/public/avatars/`）；
   - 道具/装备/技能图标：64×64 RGBA WebP（`frontend/src/assets/items/`，单张 ≤ 4 KB）；
   - 建筑瓦片：64×64 RGBA WebP（`frontend/src/assets/buildings/`）；
   - **严禁大体积 PNG 源图直入前端**。
