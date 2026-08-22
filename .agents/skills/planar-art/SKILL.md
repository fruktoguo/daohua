---
name: planar-art
description: "Design, prompt, review, and evaluate visual game sprites, unit graphics, and 2D assets in the Black-White-Accent Angular-Planar (黑白单彩极简折面) art style. Enforces planar cut geometry, color budget hierarchy (near-black, bone-white, saturated accent), pure solid black background, dark/ominous tone, semantic shape symbols, breathing space, and 96px readability."
---

# Planar Art Direction（黑白单彩极简折面美术规范）

本规范定义了一套**冷峻、硬朗、具压迫感、暗黑修仙妖煞风格的 2D 扁平折面（Angular-Planar）视觉系统**。适用于怪物、BOSS、元素生灵、角色立绘与战斗资产的 AI 提示词组装与视觉验收。

在进行任何设计、审核或提示词撰写前，请查阅 [art-direction-bible.md](references/art-direction-bible.md) 与 [prompt-assembly-guide.md](references/prompt-assembly-guide.md)。

---

## 1. 核心美学哲学与暗黑调性

- **少而精的折面（Broad Chamfers & Planar Cuts）**：采用大色块折角与锐利硬边切角，构建冷酷、凶煞、充满张力的形体结构。
- **暗黑压迫感与恐怖张力（Dark / Ominous / Sinister）**：
  - 拒绝一切温和呆萌、泡泡圆角的宠物感（No Cute Mascot / Fluffy Blobs）。
  - 强调骨骼感、棱角张力、锐利目光与妖异能量核，呈现修仙世界中凶残、诡谲、煞气逼人的妖魔与异灵形态。
- **背景强制为纯黑色（Pure Solid Black Background, #000000）**：
  - 画面背景**必须严格为纯黑实色**，与外层的暖骨白纸板描边形成极高反差。
- **三层视觉尺度**：
  1. **宏观（Outer Silhouette）**：极具压迫感的主体轮廓、重心与姿态轴线。
  2. **中观（Medium Structural Cuts）**：3~5 块高价值的黑/白/彩闭合结构面，交代骨架、面具、外甲与能量源。
  3. **微观（Small Identity Signals）**：至多 1~2 个微小识别符号（如冷冽的菱形凶光眼）。

---

## 2. 视觉预算与色彩语义规范

每个单位严格遵循三色层级分配，背景统一为纯黑色：

| 颜色角色 | 面积预算 | 视觉职责 | 规则与约束 |
|---|---|---|---|
| **纯黑背景 (Pure Black BG)** | **画布底色** | 纯色实底 `#000000` | 绝不使用灰色、彩色或渐变背景，凸显纸板外描边。 |
| **近黑主体 (Near-Black)** | **60% ~ 80%** | 主体肉身、重甲、煞气质心 | 连续的主体深色质量；提供骨架间的负空间与压迫感。 |
| **骨白结构 (Bone-White)** | **12% ~ 28%** | 骨刺、面具面盘、獠牙、外甲壳与接触利爪 | 封闭大面，通常 1~3 块；具有骨骼硬度和导向感。 |
| **单饱和强调色 (Accent)** | **5% ~ 14%** | 妖核、幽冥灵火、煞气回路、魔纹动力链 | 单一高饱和主色（如血赤、妖紫、幽蓝、碧绿、玄金）。 |

### 强制间隙规则（Breathing Slot）
- 独立的白色骨骼面与彩色能量面之间**必须保留近黑色隔离槽（Breathing Slot）**。
- 仅当两者在物理/逻辑上构成同一块复合结构（如骨架内嵌核心）且交界分明时才允许贴合。

---

## 3. 结构分类与质量几何

### 承重与运动模式 (Support & Locomotion)
- **G (Grounded 贴地多足)**：四足或多点伏地，着重刻画压低扑杀、沉重底盘或后蹬弹跳。
- **V (Vertical / Upright 直立双足)**：双足挺拔立立，如人立巨魔、狂暴猿魔、虎首人身妖王，着重刻画高大压迫感与战斗前迎面。
- **W (Airborne / Floating 悬浮推进)**：浮空小精灵/飞灵/气煞，着重刻画飘逸聚拢的流线、精灵灵火与下垂灵须。
- **L (Low-slung 线性低伏)**：蛇形、长轴脊椎匍匐，强调中轴扭动与利爪。
- **R (Radial 放射聚拢)**：浮空核心环绕少数不对称气焰或触角。
- **X (Segmented 节段多足)**：甲壳分段与尖刺武器端。

### 质量几何形态 (Mass Geometries)
- **C (Compact 紧凑重装)**：宽肩厚体重心低，力量感集中。
- **D (Domed 穹顶重甲)**：上方大壳/驼峰隆起，坚不可摧。
- **T (Tall 高挑立轴)**：高大威严、双足站立或树冠立轴。
- **F (Flat 扁平流线)**：横向开阔，极速冲锋。
- **W (Wisp 精灵小体)**：轻盈浮空、聚拢灵体，具有精灵/小异灵特征。

---

## 4. 标准 AI 提示词组装流水线 (Pure Black BG & Dark Tone)

生成提示词时严格拆分为两部分：

### A. 固定公共段 (Common Rendering Block - Pure Black Background)
```text
Create exactly one finished 2D game sprite of [ENTITY_NAME]. Use an ominous dark fantasy cut-shape visual language: pure solid pitch-black background (#000000), near-black continuous main mass (60-80%), a few broad bone-white structural planes (12-28%), and one saturated accent color (5-14%). Use hard-edged planar shapes with controlled chamfers, sharp angles, solid fills, no texture, no gradients, no soft shading, no glow, no blur, no 3D rendering, no text, no watermark. Keep the whole entity visible, centered, and readable at 96 px. Use a clear right-facing, near-orthographic side-dominant paper-card camera with slight three-quarter volume. Add a thin warm-white paperboard outline outside the entity, separated by a narrow ink-black groove. The background must be pure solid black.
```

### B. 实体定制段 (Custom Structural Block - Terrifying & Ominous)
```text
Architecture and Stance:
[ENTITY_NAME] is a [SUPPORT_MODE] ominous entity with a [MASS_GEOMETRY] mass geometry. Pose: [SPECIFIC_STANCE_AND_AXIS]. Design one continuous near-black menacing silhouette solving [SILHOUETTE_PROBLEM].

Bone-White Structures:
Add [1-3] sharp closed bone-white planes: [PLANE_1 for SKELETAL_MASK/CLAWS]; [PLANE_2 for ARMOR/RIDGE]. Use one sharp diamond mark for the chilling [EYE_COLOR] eye. No soft rounded cartoon features.

Accent Topology:
Use [ACCENT_COLOR] in a [ACCENT_TOPOLOGY] shape located at [ACCENT_LOCATION]. It functions as [DEMONIC_CORE/ENERGY_CHAIN], starting at [START] and ending at [END]. Keep black breathing slots between white bone planes and accent energy. Do not color [FORBIDDEN_AREAS].

Avoid:
Avoid cute mascot features, soft fluffy roundness, cluttered fine spikes, multiple accent colors, and non-black background.
```

---

## 5. 验收红线清单 (Checklist)

1. **纯黑背景**：背景完全为纯黑实色（#000000），外层暖骨白纸板描边边缘锐利反差强烈。
2. **暗黑压迫感**：造型冷酷硬朗，杜绝圆润呆萌与宠物化。
3. **96px 极简辨识度**：缩至小图标依然清晰辨认出轮廓 + 骨白大面 + 单色核心。
4. **姿态准确**：双足站立者（如直立虎魔、立立猿魔）必须呈现挺拔双足立姿；元素小精灵必须呈现浮空灵动体态。
