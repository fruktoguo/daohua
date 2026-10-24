# MOSS-Image 空间布局（Layout）标准模板库与多部件解耦范式

MOSS-Image 原生支持空间布局控制（Layout to Image）。在 1024×1024 正方形画布下，坐标系为中心原点绝对像素坐标（`bbox_coord_space: "abs_centralized"`），格式为 `[y_min, x_min, y_max, x_max]`（`bbox_format: "yxyx"`）。

---

## 一、坐标换算原理 (1024×1024 画布)

- 画布几何中心为 `(0, 0)`。
- X 轴向右为正（范围 `-512` ~ `+512`）。
- Y 轴向下为正（范围 `-512` ~ `+512`）。
- 四个极端角：左上 `(-512, -512)`，右上 `(-512, +512)`，左下 `(+512, -512)`，右下 `(+512, +512)`。
- **安全绘制边界**：建议保留外围 60~80px 留白（即有效坐标范围在 `-450` ~ `+450` 之间），杜绝边缘截断与物体溢出。

---

## 二、单体多部件解耦布局模板（怪物、Boss、角色高精立绘）

当生成单体角色或怪物时，**严禁将全部描述写成一段笼统长句**。必须使用多部件 BBox 将「主体剪影」、「结构面具/外甲」与「能量核心/武器」在空间上物理锚定：

### 1. 悬浮型异灵 / 灵生灵（如潮汐之灵、水灵、火灵）
```json
{
  "summary": {
    "canvas": "single",
    "caption": "A minimalist 2D game sprite of an elemental spirit in Chinese Xianxia dark fantasy paper-cut planar polygonal silhouette art style on a solid pure black background (#000000). High contrast flat 2D graphic, sharp angular hard edges, distinct warm bone-white cardboard outer contour outline with a narrow inner black incised groove, pure flat solid color fills, strictly zero gradients, zero soft shading, zero blur, zero realistic fluid water splash, zero modern 3D rendering, perfectly centered isolated game asset, highly readable at 96px."
  },
  "items": [
    {
      "label": "character body silhouette",
      "bbox": [-380, -240, 360, 240],
      "caption": "The main body mass of the floating spirit. A continuous solid near-black planar polygonal silhouette (#121212) shaped like a tapered inverted triangle, ending below in two sharp rigid downward-pointing dark tails. Clean sharp faceted angular cutouts, solid opaque dark mass taking 70% area, strictly no gradients."
    },
    {
      "label": "bone-white armor mask",
      "bbox": [-280, -80, -90, 180],
      "caption": "A prominent sharp bone-white Daoist talisman faceplate armor mask mounted on the upper front of the dark body. Crisp flat ivory-white angular geometric facet plate with a tiny sharp glowing cyan diamond eye slit. Bold graphic shape, 20% area, distinct hard edges, strictly solid flat white."
    },
    {
      "label": "demonic energy core",
      "bbox": [-40, -70, 110, 70],
      "caption": "A single solid saturated pure deep blue (#0284c7) hexagonal spirit energy core crystal embedded firmly in the center chest, isolated single accent color (8% area), strictly flat solid color with no diffuse glow, completely surrounded by a thin near-black breathing groove separating it from the white mask above."
    }
  ],
  "bbox_coord_space": "abs_centralized",
  "bbox_format": "yxyx"
}
```

### 2. 贴地四足伏地兽 / 巨型重甲巨獠（如青脊妖狼、铁甲岩獠）
- **主体兽躯剪影 (`beast torso & legs`)**：`[-260, -420, 320, 420]`（压低重心的四足扑杀或站桩横向剪影，近黑折面）
- **骨白头首与巨角獠牙 (`bone headplate & tusks/claws`)**：`[-220, 80, 180, 440]`（前突的骨白面盘与粗壮尖刺獠牙，朝右）
- **脊背异宝/煞气回路 (`demonic spine crystal`)**：`[-360, -220, -140, 160]`（中轴脊背排列的单饱和色妖晶骨刺）

### 3. 直立双足霸王 / 关底魔王（如撼地金背猿、伥虎妖王）
- **伟岸胸臂剪影 (`bipedal muscular torso & limbs`)**：`[-400, -320, 380, 320]`（挺拔宽肩人立、双拳垂地，近黑重甲）
- **凶神面谱与骨刺面甲 (`demonic skull mask & shoulder plates`)**：`[-380, -120, -150, 160]`（骨白獠牙面具与肩胛甲板）
- **金刚金甲脊板 (`golden vajra dorsal armor`)**：`[-320, -260, 200, 40]`（背部与颈后覆盖的夺目纯金甲壳或金色烈焰回路）

---

## 三、常用网格切片布局模板（成组道具、装备、材料）

### 1. 2×2 四宫格布局（适合副手4件套、四系灵宝、四象法宝）
```json
[
  { "name": "左上 Top-Left", "bbox": [-420, -420, -80, -80] },
  { "name": "右上 Top-Right", "bbox": [-420, 80, -80, 420] },
  { "name": "左下 Bottom-Left", "bbox": [80, -420, 420, -80] },
  { "name": "右下 Bottom-Right", "bbox": [80, 80, 420, 420] }
]
```

### 2. 2×3 六宫格布局（适合防具三件套×2、材料合集）
```json
[
  { "name": "第1行第1个 (头部)", "bbox": [-400, -440, -120, -180] },
  { "name": "第1行第2个 (胸甲)", "bbox": [-400, -130, -120, 130] },
  { "name": "第1行第3个 (下装)", "bbox": [-400, 180, -120, 440] },
  { "name": "第2行第1个", "bbox": [120, -440, 400, -180] },
  { "name": "第2行第2个", "bbox": [120, -130, 400, 130] },
  { "name": "第2行第3个", "bbox": [120, 180, 400, 440] }
]
```

### 3. 2×4 八宫格布局（适合生活工具组、丹药组）
```json
[
  {"name": "R1-C1", "bbox": [-450, -460, -60, -260]},
  {"name": "R1-C2", "bbox": [-450, -210, -60, -10]},
  {"name": "R1-C3", "bbox": [-450, 40, -60, 240]},
  {"name": "R1-C4", "bbox": [-450, 290, -60, 490]},
  {"name": "R2-C1", "bbox": [60, -460, 450, -260]},
  {"name": "R2-C2", "bbox": [60, -210, 450, -10]},
  {"name": "R2-C3", "bbox": [60, 40, 450, 240]},
  {"name": "R2-C4", "bbox": [60, 290, 450, 490]}
]
```

---

## 四、宽屏大地图场景与区域横幅模板 (16:9 画布)

在 1024×576 横屏分辨率下，Y 轴范围为 `-288` ~ `+288`，X 轴范围为 `-512` ~ `+512`：

```json
{
  "summary": {
    "canvas": "single",
    "caption": "A widescreen Chinese Xianxia mystical mountain trail landscape illustration. Traditional ink-wash painterly thick-coating art style, ancient Daoist atmosphere, muted antique atmospheric colors, 16:9 banner composition."
  },
  "items": [
    {
      "label": "distant mountains and cloud sea",
      "bbox": [-240, -480, 0, 480],
      "caption": "Majestic distant towering jagged mountain peaks shrouded in swirling sea of white spirit clouds, pale ink wash gradient, soft celestial horizon."
    },
    {
      "label": "ancient stone path and shrine",
      "bbox": [-40, -260, 240, 260],
      "caption": "An ancient weathered flagstone mountain path winding through misty forest, flanked by gnarled pines and a small weathered stone Daoist altar pagoda."
    }
  ],
  "bbox_coord_space": "abs_centralized",
  "bbox_format": "yxyx"
}
```
