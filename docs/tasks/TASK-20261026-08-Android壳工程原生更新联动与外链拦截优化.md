# TASK-20261026-08 Android壳工程原生更新联动与外链拦截优化

## 元信息

| 项 | 值 |
| :--- | :--- |
| 任务编号 | TASK-20261026-08 |
| 分类 | 移动端优化 / Android壳工程 / 前端体验 / 交互闭环 |
| 提出时间 | 2026-10-26 20:03:42 |
| 完成时间 | 2026-10-26 20:20:00 |
| 状态 | ✅ 已完成 |
| 关联任务 | TASK-20261025-01（Android自动更新与应用内下载安装）、TASK-20261026-04（正式服发布） |
| 代码提交 | `01da87a9`（`feat(mobile): Android壳工程原生更新联动与外链下载拦截优化`） |

## 需求背景与核心问题

用户在手机端点击“下载安装包”时，页面跳出了 GitHub 网页或触发了系统打开 GitHub App，未如预期原地直接开始下载安装包。
排查根因包含两层面：
1. **HTML5 跨域同源限制**：`<a download="daohua.apk">` 仅对同源 URL 有效；外链 `github.com` 会被现代移动浏览器忽略 `download` 属性，直接触发页面导航或系统 Intent DeepLink 拦截；
2. **Android 壳工程（App）内缺乏联动与拦截**：
   - 前端 `SettingsPage.tsx` 在 Android 壳环境下未检测 `window.DaoHuaNative` 原生桥接，机械渲染了裸 `<a href="https://github.com/..." download>`；
   - 原生 `MainActivity.java` 的 `shouldOverrideUrlLoading` 未设置 `DownloadListener`，遇到 http/https 均返回 `false`（在当前 WebView 内加载），导致直接在游戏全屏窗口渲染了 GitHub 网页；且外部链接（如 GitHub 仓库）未调起系统外部浏览器。

## 改进方案（方案 B 完整实装）

1. **Android 原生层拦截（双重兜底保障）**：
   - 在 `MainActivity.java` 的 `shouldOverrideUrlLoading` 与 `setDownloadListener` 中增加对 `.apk` 及 GitHub Releases 下载链接的拦截：识别到安装包请求时，直接切到主线程调用 `mUpdateManager.checkUpdate(true)`，由原生墨黑仙侠风格更新弹窗接管（展示更新说明、多源断点下载带进度条、调起 FileProvider 覆盖安装），返回 `true` 阻止 WebView 加载网页；
   - 增加非本域 host 拦截（如 `github.com` 等外部链接）：通过 `Intent.ACTION_VIEW` 调起系统外部默认浏览器打开，避免外部网页直接污染和覆盖游戏全屏 WebView。
2. **前端设置面板（SettingsPage）原生感知**：
   - 显式感知 `window.DaoHuaNative`：
     - 若处于 App 壳环境：展示当前 App 版本（调用 `getAppVersion()`），操作按钮切换为「检查新版本」，点击触发 `DaoHuaNative.checkUpdate()` 并给出太初仙道提示；
     - 若处于外部 Web 浏览器：保留 APK 下载入口，增加 `target="_blank" rel="noopener noreferrer"` 并补充国内加速提示，避免直接破坏当前游戏会话。
3. **i18n 词条闭环与门禁验证**：
   - 补齐相关文案与国际化翻译，导出 `template.csv`，通过 `check-i18n-keys.mjs` 与前端 127 项单测；
   - 重新构建完全版 APK（`artifacts/daohua.apk`，2.7 MB）并自动覆盖发布至 GitHub Releases（Tag: `v0.1.0-sha-f3a9457a`）。
