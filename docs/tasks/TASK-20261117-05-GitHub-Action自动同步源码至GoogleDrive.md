# TASK-20261117-05：GitHub Action 自动同步源码快照至 Google Drive

- **提出时间**：2026-11-17 03:19:18
- **需求分析完成时间**：2026-11-17 03:20:00
- **状态**：✅ 已完成（待用户配置 GitHub Secrets 并推送生效）

## 1. 需求理解与目标边界

- **核心需求**：在 GitHub 仓库中添加自动化工作流，每次向 `main` 推送代码以及每天中午前定时将 `DaoHua` 最新纯净源码（含可选 `vendor/YuoCore` 子模块）打包为 `daohua-latest.zip` 并覆盖上传至 Google Drive，供外部定时任务模型每日中午 12:00 拉取执行全仓前后端扫描与报告生成。
- **实现边界**：
  1. 仅新增 `.github/workflows/sync-to-gdrive.yml`（不改动业务代码、运行时镜像或持久化结构）。
  2. 打包剔除 `.git`、构建产物（`bin`/`obj`/`node_modules`/`dist`/`artifacts`）与纯设计母模 `art/`，并附带 `SNAPSHOT_META.txt` 记录提交 SHA 与生成时间。
  3. 凭据零硬编码，完全通过 GitHub Repository Secrets（`RCLONE_CONFIG`，可选 `GDRIVE_FOLDER_ID` 与 `SUBMODULE_TOKEN`）注入；自动读取 `rclone.conf` 首条 remote 名称降低配置失误。
  4. 兄弟私有仓库 `../DaoHua-Private` 不在公开仓库内，GitHub Actions 侧仅同步主仓（及可访问时的 `vendor/YuoCore`）。

## 2. 子任务拆解

1. 编写 `.github/workflows/sync-to-gdrive.yml`（支持 `push` 到 `main`、每日 UTC 02:30 定时与 `workflow_dispatch` 手动触发）。
2. 验证 YAML 语法合法性并核对 `zip` 排除规则与 `rclone` 上传参数。
3. 整理用户在 GitHub 仓库与本地 `rclone` 侧需要完成的最小配置步骤。

## 3. 进度记录

- `2026-11-17 03:20:00`：完成需求确认与技术选型（采用 `rclone` OAuth 方案规避个人 Google Drive Service Account 无配额问题），建档并创建工作流文件。
- `2026-11-17 03:21:30`：完成 `.github/workflows/sync-to-gdrive.yml` 编写与静态语法核验；待用户配置 GitHub Secrets 并提交推送后即可在 Actions 页面手动触发验证。
