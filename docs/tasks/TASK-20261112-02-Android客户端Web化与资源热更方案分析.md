# TASK-20261112-02 Android 客户端 Web 化与资源热更方案分析

- 提出时间：2026-11-12 01:06:51
- 状态：🔨 实施中（用户裁定「一次性完整实现」，Phase 1+2 合并落地）
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
- 2026-11-12 用户裁定一次性完整实现，进入实施。

## 5. 实施方案（Web 优先 + 双资源包热更）

### 5.1 架构定稿

- **Web 优先**：`index.html`、`/assets/*-HASH.*`（JS/CSS/全部图标 hash 产物）一律走网络；服务端与前端同源发布，前端版本与协议永远对齐，TapTap 审核仅在原生壳变更时需要。
- **清单热更**：`asset-manifest.json`（`{v, generatedAt, files:{"/path":{sha256,size}}, hdAliases}`）由 `scripts/gen-asset-manifest.mjs` 在前端构建末尾自动生成（npm build 已接入），随 `frontend/dist` 进入服务端 wwwroot，原生每小时比对 sha256 差异下载至 `filesDir/hotpatch`，下载后强制 SHA-256 校验、临时文件原子落盘。
- **三级解析**：`shouldInterceptRequest` → `AssetHotpatchManager.resolveLocal(path)` → 热更目录(sha 匹配) → APK 基线 `assets/www|hd`(sha 匹配) → null 回源网络。清单未获取时进入尽力模式（热更优先、基线兜底）。
- **HD 画质包独立化**：废弃 `inject-hd-assets.mjs` 注入式构建（hash 死重根源）；新增 `scripts/gen-hd-pack.mjs` 以「art/ HD 母模优先 + src 低清兜底」生成 `artifacts/hd-pack`（items/buildings/avatars + `/hd` 前缀清单），Caddy `/hd/*` → `/opt/daohua/static/hd`（符号链接原子切换，保留 3 版），`scripts/upload-hd-pack.sh` 负责上传。
- **HD 透明改写（前端零改动）**：vite `manifest:true` 产出 `.vite/manifest.json`；`gen-asset-manifest` 据此生成 `hdAliases`（`/assets/<name>-<hash>.webp` → `/hd/items|buildings/<itemId>.webp`，213 条全命中验证）；原生按别名把 hash 图标请求改写到 HD 包；`/avatars/*` 同名重写 `/hd/avatars/*`（art 与 public 命名一致已核对）。
- **APK 瘦身**：`assets/www` 只保留清单管理的可变资源（avatars/emotes/locales/theme.js，约 5MB），`/assets` hash 产物与入口文件不再进包；HD 包独立内嵌 `assets/hd/` + `hd-manifest.json`；基线清单 `www-manifest.json` 随包生成。

### 5.2 改动清单

| 文件 | 改动 |
| --- | --- |
| `scripts/gen-asset-manifest.mjs` | 新增：可变资源 sha256 清单 + Vite manifest HD 别名 |
| `scripts/gen-hd-pack.mjs` | 新增：HD 画质包生成（art 母模优先 + src 兜底 + /hd 清单） |
| `scripts/upload-hd-pack.sh` | 新增：tar 流上传 + 远端版本化目录 + 符号链接原子切换 |
| `frontend/vite.config.ts` | `build.manifest: true` 产出 `.vite/manifest.json` |
| `frontend/package.json` | build 链末尾追加 gen-asset-manifest |
| `Dockerfile` | 前端构建阶段 COPY gen-asset-manifest.mjs |
| `src/Server/Program.cs` | 静态缓存头：`/assets/*` immutable 长缓存，其余 no-cache 重校验 |
| `deploy/daohua.yuohira.com.caddy` | 新增 `/hd/*` 静态路由（strip_prefix + file_server + no-cache） |
| `tools/android-shell/.../AssetHotpatchManager.java` | 新增：清单拉取/比对/下载/校验/落盘/GC + 三级解析 |
| `tools/android-shell/.../MainActivity.java` | 拦截策略重写：仅接管游戏源站，api/ws/ops 透传，交由 resolveLocal 判定 |
| `scripts/build-apk.sh` | 去 HD 注入与 hash 死重；www 精简同步；HD 独立包内嵌；双基线清单 |
| `scripts/release-prod.sh` | 新增 `--skip-hd-pack` 与阶段 3.6 HD 包上传（失败仅警告） |

### 5.3 验证记录

- `npm --prefix frontend run build`：通过，dist 产出 `asset-manifest.json`（252 资源 + 213 条 hdAliases）。
- 别名命中率：213/213 全部可在 HD 包清单解析（含扩展名回退）。
- `gen-hd-pack.mjs` 独立运行：items 201 + buildings 12 + avatars 37 = 3.02MB。
- www 基线清单模拟：252 资源，非 avatar/emote 项仅 locales/theme.js，符合预期。
- `bash -n` 三个改动脚本语法通过。
- Gradle assembleRelease / 真机联调：待补。
- 2026-11-12 真机首装反馈两处修正：
  1. **HD 图标服务端清单依赖**：生产端未发布新清单时 `/assets/*-HASH` 无法命中 HD。修复——`gen-asset-manifest.mjs` 新增 `--vite-manifest` 参数，`build-apk.sh` 将 dist 的 HD 别名一并写入包内 `www-manifest.json`（213 条）；`AssetHotpatchManager` 别名解析增加包内基线回退 + 无清单时 HD 扩展名探测（assets 目录列举缓存），服务端未发布前 HD 图标即可生效（Vite hash 为内容指纹，未变资源 hash 与线上一致）。
  2. **更新提示误报降级**：`UpdateManager.isNewerVersion` 原逻辑「tag 不同即提示」，本地 v0.2.11 vs GitHub v0.2.9 被误判有更新。修复——改为语义化版本数值比较（支持 v 前缀/- 后缀），远程版本必须严格更高才提示，解析失败回退旧行为。
- 2026-11-12 APK 构建策略调整（用户要求）：`release-prod.sh` APK 构建由默认执行改为**显式 `--with-apk` 才执行**（`--skip-apk` 保留兼容）；HD 画质包与 APK 解耦，每次发布自动重新生成并上传 `/hd/*`（`--skip-hd-pack` 可跳过）；`AGENTS.md` §12 与 `prod-release` 技能同步固化该规则。
