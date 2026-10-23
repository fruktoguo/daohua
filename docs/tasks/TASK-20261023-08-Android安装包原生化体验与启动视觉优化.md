# TASK-20261023-08 Android安装包原生化体验与启动视觉优化

## 基本信息

| 项 | 内容 |
| :--- | :--- |
| **任务编号** | TASK-20261023-08 |
| **任务名称** | Android安装包原生化体验与启动视觉优化 |
| **分类** | Android壳工程 / 前端交互体验 / 视觉与启动屏优化 |
| **提出时间** | 2026-10-23 21:10:00 |
| **需求分析完成** | 2026-10-23 21:15:00 |
| **开始实现** | 2026-10-23 21:18:00 |
| **完成时间** | 2026-10-23 21:40:00 |
| **状态** | ✅ 已完成 |

## 一、需求原文与核心理解

### 1.1 用户需求原文
1. 顶部栏和游戏内容重叠了，相当于是完全全屏，导致不是很好；
2. 当前长摁划选还是会出浏览器的复制，就很不像个 app，一眼网页；
3. 没有启动 logo 或者啥的，能不能伪装的更像真实的 app 一样？

### 1.2 核心理解与影响范围
- **业务目标**：消除 Android APK 打包产物中的各类 Web 痕迹，建立与商业级移动端原生游戏同等水准的沉浸感、启动表现和操作触感。
- **核心逻辑预期**：
  1. **状态栏避让与安全区适配（针对问题 1）**：
     - 在 Android 原生层（`MainActivity.java`）针对 Android 15（API 35 强制 Edge-to-Edge）与各厂商异形屏，通过 `WindowInsets` 准确避让状态栏及刘海/挖孔高度（`WindowInsets.Type.statusBars() | WindowInsets.Type.displayCutout()`），消除游戏角色头像、姓名和体力等顶部内容被系统电量、时间、Wi-Fi 遮挡的问题。
     - 在前端 CSS 层对 `.topbar` 增加 `padding-top: calc(... + env(safe-area-inset-top, 0px))` 双重保险。
  2. **消除文本划选与浏览器长按菜单（针对问题 2）**：
     - 前端全局样式增加 `-webkit-touch-callout: none`、`user-select: none`，移除点击半透明闪烁 `-webkit-tap-highlight-color: transparent`，禁止下拉回弹 `overscroll-behavior-y: none`；保留 `input` 与 `textarea` 的文本编辑能力。
     - Android 原生 WebView 层拦截长按事件（`setOnLongClickListener`）、禁用长按震动反馈，拦截文本选中的系统浮动工具栏（`ActionMode.Callback2` 返回 `false`），禁用边缘波纹（`OVER_SCROLL_NEVER`）。
  3. **真实 App 级启动屏与伪装优化（针对问题 3）**：
     - 去除原先具有明显“浏览器加载条”特征的微型 `ProgressBar`。
     - 引入深邃仙侠暗黑墨色与太初金印主题的冷启动瞬时占位图（`windowBackground` / Layer-list），实现 0ms 闪电首帧。
     - 在 Java 层构建沉浸式 Native Splash View：包含道门灵珠道印 Logo、书法体「道化太初」金色标题、修仙箴言「凡躯求索 · 逆命登仙」及呼吸微光加载提示。
     - 当 WebView 首屏加载完成（`onPageFinished`）且经过最小舒适呈现时长后，执行丝滑的 Fade-out 渐隐过渡动画，平滑切入游戏。
- **实现边界与协同策略**：
  - 范围收敛于 `tools/android-shell/` 及 `frontend/src/styles.css`，保持纯单 Agent 闭环，不涉及任何服务端领域与协议模型。
  - 产物通过 `scripts/build-apk.sh` 进行端到端构建验证，并跑通前端门禁。

## 二、子任务分解与执行状态

- [x] 1. 前端全局禁用文本长按划选、触摸高亮与安全区双保险适配 (`frontend/src/styles.css`)
- [x] 2. Android 原生层 WindowInsets 适配解决状态栏重叠 (`tools/android-shell/.../MainActivity.java`)
- [x] 3. Android 原生层拦截长按与文本选择 ActionMode 上下文工具栏 (`MainActivity.java`)
- [x] 4. Android 原生沉浸式启动屏 Splash 与 Fade-out 渐隐动效实现 (`MainActivity.java`, `res/drawable/`, `styles.xml`)
- [x] 5. 前端门禁测试与 `scripts/build-apk.sh` 产物打包验证

## 三、验证记录

1. **前端门禁验证**：
   - `tsc -b && vite build`：通过，模块构建成功。
   - `node --test "frontend/src/**/*.test.mjs"`：112/112 单测全部通过。
   - `check-i18n-keys.mjs`：2656 条词条比对通过。
   - `check-log-markup.mjs`：富文本转义标记通过。
2. **Android 产物打包验证**：
   - 执行 `scripts/build-apk.sh`：Release assemble 成功。
   - 产物路径：`artifacts/daohua.apk` (2.7MB)，包含超清 Retina 资产注入与原生沉浸启动屏。
