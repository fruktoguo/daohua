# Angular-Planar 提示词组装与生产规范指南

本指南提供了将设计需求转化为高质量 AI 图像生成提示词的标准方法。适用于 Midjourney、DALL-E 3、Stable Diffusion 及定制图像生成模型。

---

## 1. 组装总公式

所有提示词必须严格拆分为两部分：
$$\mathbf{Prompt} = \mathbf{固定公共渲染段\ (Part\ A)} + \mathbf{实体定制结构段\ (Part\ B)}$$

- **Part A（固定公共段）**：锁定画风、镜头视角、媒介属性、色彩比例预算与全局负面约束。
- **Part B（实体定制段）**：定义实体的承重模式、质量几何、轮廓张力、骨白结构面、单彩拓扑与专属避坑清单。

---

## 2. 标准模板库

### Part A: 固定公共渲染段 (Universal Common Block)

```text
Create exactly one finished 2D game sprite of [ENTITY_NAME]. Use a clean flat cut-shape visual language: near-black continuous main mass (60-80%), a few broad bone-white structural planes (12-28%), and one saturated accent color (5-14%). Use hard-edged planar shapes with controlled chamfers, solid fills, no texture, no gradients, no soft shading, no glow, no blur, no 3D rendering, no text, no watermark. Keep the whole entity visible, centered, and readable at 96 px. Use a clear right-facing, near-orthographic side-dominant paper-card camera with slight three-quarter volume; no dramatic perspective shortening, extreme tilt, or mixed camera angles. Add a thin warm-white paperboard outline outside the entity, separated by a narrow ink-black groove.
```

### Part B: 实体定制结构段 (Custom Structural Block)

```text
Architecture and Silhouette:
[ENTITY_NAME] is a [SUPPORT_MODE] entity with a [MASS_GEOMETRY] mass geometry. Its primary visual axis is [AXIS], and its mass tension is [MASS_TENSION]. Design one continuous near-black silhouette solving [SILHOUETTE_PROBLEM].

Bone-White Structures:
Add [1-3] broad closed bone-white planes: [PLANE_1 explaining STRUCTURAL_JOB_1]; [PLANE_2 explaining STRUCTURAL_JOB_2]. Use one small sharp diamond mark for [EYE_OR_FOCAL_POINT]. No white face mask or random white fills.

Accent Topology:
Use [ACCENT_COLOR] in a [ACCENT_TOPOLOGY] shape located at [ACCENT_LOCATION]. It functions as [STRUCTURAL_FUNCTION], starting at [START_POINT] and ending at [END_POINT]. Keep intentional black breathing space between white and accent planes. Do not color [FORBIDDEN_AREAS].

Avoid:
Avoid [FAILURE_MODE_1], [FAILURE_MODE_2], and generic decorative clutter.
```

---

## 3. 标准实体组装范例

### 范例 1：重装前锋机甲 (Heavy Vanguard Construct)

```text
[Part A: 固定公共渲染段]

Architecture and Silhouette:
Heavy Vanguard Construct is a Grounded Footed (G) entity with a Compact (C) mass geometry. Its primary visual axis is the shoulder-to-ground load line, and its mass tension is an oversized reinforced shoulder versus a planted stance. Design one continuous near-black silhouette solving the stability of a massive armored frontal hull.

Bone-White Structures:
Add 2 broad closed bone-white planes: one heavy chest keel plane providing vertical structural stability; one broad angled forearm contact plane. Use one small sharp diamond mark for the central sensor eye. No white mask or decorative white trims.

Accent Topology:
Use Saturated Crimson in a Core topology shape located inside the upper chest and scapula. It functions as the central power housing, enclosed firmly within the shoulder mass. Keep intentional black breathing space between the bone-white keel and crimson core. Do not color the outer shoulder rim, back contour, or foot edges.

Avoid:
Avoid continuous lightning stripes across the torso, thin decorative circuitry lines, and spiky armor plates.
```

### 范例 2：敏捷后掠侦察机 (Agile Sweep Scout)

```text
[Part A: 固定公共渲染段]

Architecture and Silhouette:
Agile Sweep Scout is an Airborne Thrust (W) entity with a Flat (F) mass geometry. Its primary visual axis is the forward-leading nose to swept-tail axis, and its mass tension is a sharp leading prow versus broad negative space beneath the wing. Design one continuous near-black silhouette solving high-speed horizontal thrust.

Bone-White Structures:
Add 2 broad closed bone-white planes: one leading-edge intake wedge defining forward direction; one lower keel plane for aerodynamic balance. Use one small sharp diamond mark for the prow navigation point.

Accent Topology:
Use Saturated Cyan in a Sweep topology shape located along the internal wing root and primary thrust conduit. It functions as the propulsion impulse plane, fanning out towards the rear and terminating cleanly before the trailing wing tip. Keep intentional black breathing space between the white intake and cyan sweep. Do not color the entire outer wing contour or trailing edges.

Avoid:
Avoid glowing exhaust smoke, soft energy trails, symmetrical circular badges, and low-poly faceted shards.
```

### 范例 3：多足开采漫游者 (Hexapod Mining Crawler)

```text
[Part A: 固定公共渲染段]

Architecture and Silhouette:
Hexapod Mining Crawler is a Segmented Modular (X) entity with a Low-slung (L) mass geometry. Its primary visual axis is a long low horizontal chassis supported by three selected rigid leg pairs, and its mass tension is a heavy front-mounted cutter versus a rear cargo hull. Design one continuous near-black silhouette solving clear modular articulation without micro-leg clutter.

Bone-White Structures:
Add 2 broad closed bone-white planes: one front chassis armor plate; one heavy rear counterbalance plate. Use one small sharp diamond mark for the cockpit orientation cue.

Accent Topology:
Use Saturated Amber Orange in a Split topology shape located at the front cutter hinge and the rear power generator. It functions as two distinct functional nodes operating in visual balance. Keep intentional black breathing space between the chassis white planes and amber nodes. Do not color all legs, joints, or individual wheel/tread segments.

Avoid:
Avoid coloring every leg joint, mechanical pipe spaghetti, tiny screws/rivets, and rounded bubble cockpits.
```

---

## 4. 提示词生成前检查清单 (Pre-flight Checklist)

在将提示词提交给 AI 渲染前，逐项自检以下 6 项：

1. **[ ] 架构与几何匹配**：承重模式（G/V/W/L/R/X）与质量形态（C/D/T/F/S/R）是否准确描述该实体？
2. **[ ] 剔除抽象工程词**：是否已剔除 `force path`、`energy pipeline`、`transmission` 等会导致模型发散的理论词，全部转化为可见的几何切面描述？
3. **[ ] 强调色拓扑唯一**：强调色是否明确指定为 Plane / Brace / Sweep / Core / Fan / Split / Channel 之一，且指定了起始与闭合边界？
4. **[ ] 骨白职责明确**：每一块白色是否都有具体的承重、铰接、朝向或外甲职责？
5. **[ ] 明确禁止区域**：是否清晰指明了哪些外沿尖端、轮廓线必须保持近黑？
6. **[ ] 96px 图标预估**：该结构在 96px 下是否依然能清晰读出大轮廓与主色块？
