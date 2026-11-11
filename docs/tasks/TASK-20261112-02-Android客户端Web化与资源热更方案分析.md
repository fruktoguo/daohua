# TASK-20261112-02 Android 客户端 Web 化与资源热更方案分析

- 提出时间：2026-11-12 01:06:51
- 状态：📋 规划中（分析已交付，待用户裁定是否立项实施）
- 分类：架构分析 / Android 客户端 / 发布链路 / TapTap 审核解耦

## 1. 需求原文与理解复述

用户诉求：当前安卓端每次更新都要走 TapTap 重新审核，周期长且不可控。希望改为「直接走 Web、和浏览器一样」，仅把图像等大体积资源打进包体；后续 App 不更新时自动从云端拉取新资源，相当于对纯 Web 的补强。

理解边界：

- 业务目标：让前端代码、配表呈现、图片等内容更新不再依赖 APK 重打包与渠道审核，实现服务端发布即全端生效。
- 核心逻辑预期：APK 退化为「壳 + 资源底包」，游戏本体（HTML/JS/CSS/数据）始终走线上最新版本；图像资源本地优先、缺失或过期自动回源云端。
- 影响范围：`tools/android-shell/`（MainActivity 拦截策略、UpdateManager）、`scripts/build-apk.sh`（资源同步与 HD 注入）、服务端静态文件缓存头、`frontend/`（如需资源 hash 化或 manifest 消费）。
- 实现边界：本任务当前仅做可行性分析与方案设计，不动代码；是否实施待用户裁定。

## 2. 现状调研结论

1. 当前 APK 已是混合架构：`MainActivity` 的 `shouldInterceptRequest` 对所有静态路径优先读 `assets/www`（整份 `frontend/dist` 拷贝，约 8.7MB），本地未命中才回源网络；`/api`、`/ws`、`/ops/api` 直通真实服务端。
2. **根因**：`index.html` 本身也走本地拦截 → 整个前端冻结在 APK 构建时刻，任何前端改动都必须重打 APK → TapTap 审核。
3. 资源分层：
   - `/assets/*-HASH.*`：Vite 内容 hash 资源（JS/CSS/图标），URL 即内容指纹，本地优先天然安全；
   - 非 hash 可变文件：`/avatars/*.webp`（38 个，APK 内被 1024px HD 覆盖）、`/emotes/*.gif`（215 个，已有 CF CDN + IndexedDB 双源缓存，包内仅兜底）、`/locales/*.csv`、`/theme.js`、`/privacy.html` —— 同名换内容，本地优先会永久过期，是最大设计坑；
   - 游戏配表 `config/game-data` 在服务端，经 WS/meta 下发，不进前端包体，数值改动本来就不需要 APK。
4. HD 矛盾：APK 构建期 `inject-hd-assets.mjs apply` 在 vite build 前注入高清建筑/道具 → APK 的 hash 文件名与线上不同；`overlay-dist` 用同名 1024px 立绘覆盖 `www/avatars`。线上 index.html 引用的是线上 hash，APK 内 HD hash 文件在 Web 化后将变成不可达死重。
5. 服务端：Kestrel 托管 `wwwroot`，仅 `index.html` 有 `no-cache, no-store`，其余静态文件无显式 `Cache-Control`，WebView 缓存行为依赖启发式规则，不可靠。
6. 版本错配现状：旧 APK 前端 + 新服务端协议存在 skew 风险（ModuleDeclaration、协议字段）；`app-version.json` 仅原生 UpdateManager 消费，前端不读。
7. 反多开：完整版前端含 `private-impl` 编译产物，Hello 握手声明 ModuleDeclaration；线上 bundle 与服务端同源部署，Web 化后声明永远同版本，反而消除冻结前端的声明过期风险。

## 3. 方案结论（详见会话分析）

- 结论：可行，属「web-first + 资源包预缓存/热更」标准模式。
- Phase 1（最小改动）：翻转拦截策略——入口与非 hash 可变文件走网络，`/assets/*` 本地优先回源兜底；服务端补齐缓存头。前端更新即时生效，APK 仅在原生壳变更时更新。
- Phase 2（完整方案）：构建期生成 asset manifest（path→sha256），服务端扫描 wwwroot 暴露；原生启动比对后差异下载至 `filesDir/hotpatch`，拦截顺序 hotpatch → assets → network；HD 可顺势做成独立画质热更包。
- 关键风险：非 hash 文件过期、HD hash 不一致、缓存头缺失、TapTap 首审仍需一次。

## 4. 进度日志

- 2026-11-12 01:06:51 需求登记，开始现状调研（android-shell、build-apk.sh、inject-hd-assets.mjs、Program.cs 静态文件装配、avatarStore/qqEmojis/i18n 加载路径）。
- 2026-11-12 01:10:00 调研完成，输出可行性分析与分阶段建议，状态置为规划中待裁定。
