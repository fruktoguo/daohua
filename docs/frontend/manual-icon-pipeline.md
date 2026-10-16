# 《道化太初》古风线装秘籍与残卷道具图标制作规范

本文档为《道化太初》全量内功秘籍（Tome Book）与心法残卷（Fragment Scroll）美术资产的权威工业级制作流水线。

---

## 1. 设计哲学：为什么拒绝“AI 直接画整本书”？

在游戏的 64×64 / 32×32 小尺寸图标及背包/行动列表展示中，如果让 AI 直接端到端生成整本书：
1. **书体结构失控**：不同批次生成的书脊厚度、视角角度、左上题签尺寸、4 根缝装白线各不相同，UI 瓦片内整齐度崩溃；
2. **小图糊死**：AI 在封面绘制的复杂花纹带有大量灰阶渐变与细小噪点，缩放到 64×64（封面中心有效可视区仅 24×24 像素）时完全糊成一团，丧失辨识度。

**权威流水线方案**：
**“权威母模底座 + 五行古典分色 + AI 镂空矢量二值 Logo + 脚本去黑/微斜/宣纸古色融合”**。

```
[权威 128px 线装书母模] ──> [古典保留明度染色] ──┐
                                                 ├──> [LANCZOS 下采样] ──> 64×64 权威 WebP 秘籍
[MOSS-Image 2×2 田字格] ──> [去黑透空 + 宣纸古色 + 微斜] ─┘
```

---

## 2. AI 提示词规范 (ComfyUI MOSS-Image)

生图统一采用 **2×2 田字格（单张大图 4 个道具，中心间距 160px）**。

### 2.1 全局风格提示 (Summary Caption)
```text
a 2x2 grid sprite sheet of 4 minimalist bold vector logo icons, stencil cut silhouette graphic symbol marks, strictly solid pure white icons on solid pure black background, high contrast binary black and white, zero gradients, zero shading, zero colors, crisp clean stencil cut lines and distinct negative space contours, sharp graphic edges, flat 2D vector clip-art.
```

### 2.2 单道具提示词纪律 (负空间切割)
- 必须明确使用 `stencil cut` 与 `negative black space / lines`；
- 严禁实心大色块糊成一坨，必须通过镂空缝隙切出结构：
  - **兽首/蛮牛**：犄角与头骨必须由黑色负空间缝隙切开；眼眶、鼻梁有明显镂空；
  - **飞禽/燕羽**：展翅羽翼各羽片分立，剪刀分尾清晰；
  - **水火/莲花**：花瓣瓣瓣独立，火舌腾跃分立；
  - **太极/星轨**：S 曲线深切阴阳，两极核点端正。

---

## 3. 程序化合成关键规约 (`composite_manuals.py`)

### 3.1 彻底去除黑色杂质（严禁人工黑描边）
- 所有亮度 `< 90` 的暗色/黑线必须强制清空为完全透明（`Alpha = 0`）；
- 严禁盲目添加黑色描边（`binary_dilation`），否则狭小镂空内部会被黑线吞没塞死；
- 镂空处必须完全透空，自然透出书籍封皮原本的古典底色。

### 3.2 图腾色彩对齐原版宣纸古色（带棕带黄）
原版标杆实测采样值（`art/legacy_128/item.tome.heavy-slash.png`）：
- **中位数色号**：**`#edd8b5`**（RGB: `237, 216, 181`）；
- **质感特征**：中国古代典籍白芨/象牙宣纸古色，带微弱的黄棕暖色调；
- **规约**：严禁使用现代塑料纯死白（`#FFFFFF`），必须统一着色为 `IVORY_BUFF = (237, 216, 181)`。

### 3.3 顺应书面透视的自然微斜
原版书籍母模书脊倾斜度为 `+6.82°`，封面大剑倾斜度为 `-15.2°`。
- 秘籍封面图腾必须执行 **`-6°` 至 `-11°` 的轻微逆时针旋转**；
- 旋转后执行二值阈值保护（`Alpha > 60` 留存，防止插值带来灰黑毛边）；
- 居中偏右贴在封面黄金受光区（`pos_x = 72 - rw//2, pos_y = 60 - rh//2`）。

---

## 4. 资产脚本与目录映射

| 资产环节 | 对应脚本 / 目录 | 产物规范 |
|---|---|---|
| 高清母模库 | `art/legacy_128/` | 120×128 RGBA PNG（重斩原版底模） |
| 图腾母版库 | `art/emblems/` | 128×128 RGBA WebP（纯净暖米棕镂空 Logo） |
| AI 生成器 | `.agents/skills/moss-art/scripts/moss_generator.py` | 驱动 ComfyUI MOSS-Image 批量出图 |
| 自动合成工具 | `.agents/skills/moss-art/scripts/composite_manuals.py` | 封皮分色、去黑、倾斜、下采样导出 |
| 最终游戏资源 | `frontend/src/assets/items/` | 64×64 无损 WebP (`item.tome.*`, `item.fragment.*`) |
