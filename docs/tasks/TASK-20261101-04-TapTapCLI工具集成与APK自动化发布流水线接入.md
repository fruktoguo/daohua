# TASK-20261101-04 · TapTap CLI 工具集成与 APK 自动化发布流水线接入

## 1. 任务背景与核心需求

### 1.1 业务目标
将 TapTap 官方开发者中心命令行工具 `@taptap/cli`（v3）正式引入《道化太初》的工程化工具链与发布流水线，使团队能够通过命令行和 AI 自动化直接完成：
1. 本地环境自检与开发者/应用上下文状态检查（`taptap-cli doctor` / `overview`）。
2. Android APK 构建产物（`daohua.apk`）的一键上传与包体管理（`upload-apk` / `package-management`）。
3. 游戏上架资料检查与提审预检流程自动化打通（`app analyze-app-status` / `precheck-app-review`）。
4. 规范化脚本封装与防误操作机制（`--dry-run` 预览与 `--yes` 确认机制集成）。

### 1.2 核心逻辑预期
- 安装并验证官方 `@taptap/cli` 工具及其内置 AI Skills 说明。
- 封装高可靠、幂等、防误操作的本地辅助发布脚本 `scripts/taptap-publish.sh`，支持：
  - `status`：检查 CLI 安装状态、网络连通性、本地认证凭证与上下文绑定。
  - `inspect`：检查构建出的本地 APK 包体物料完整性（对齐 `materials +inspect` 规约）。
  - `upload`：执行 APK 安全上传（支持 `--dry-run` 试跑防空转，支持 `--yes` 生产提交，透传 `--idempotency-key`）。
  - `review-check`：运行提审前资质与资料完整度预检。
- 在 `scripts/build-apk.sh`、`scripts/release-prod.sh` 及项目相关运维文档中建立清晰的自动化调用引导。

### 1.3 影响范围与实现边界
- **影响范围**：
  - 新增管理脚本：`scripts/taptap-publish.sh`。
  - 更新脚本清单：`AGENTS.md` 脚本速查表。
  - 任务追踪：`docs/tasks.md` 与本详情文档。
- **实现边界**：
  - 不侵入游戏运行时领域逻辑（Domain/Ecs/Server 无改动）。
  - 认证登录属于需外接浏览器交互的人工作业（`taptap-cli auth login`），脚本仅做状态检查与未登录引导，不存储敏感凭据。

---

## 2. 任务时间节点

- 任务提出时间：`2026-11-01 19:35:00`
- 需求分析与方案设计完成：`2026-11-01 19:38:00`
- 完成时间：`2026-11-01 19:55:00`
- 当前状态：`✅ 已完成`

---

## 3. 子任务与进度追踪

- [x] **Subtask 1: 工具链安装与环境就绪验证**（安装 `@taptap/cli`，检查 `version`、`doctor` 与 `skills list`）
- [x] **Subtask 2: 封装自动化脚本 `scripts/taptap-publish.sh`**（实现环境检查、包体检查、APK 上传与预检逻辑，集成 `--dry-run` 保护）
- [x] **Subtask 3: 更新工程规范与脚本索引**（更新 `AGENTS.md` 脚本表）
- [x] **Subtask 4: 本地回归验证与执行测试**（测试脚本 `status`、`inspect`、`upload --dry-run` 与参数校验分支）
- [x] **Subtask 5: 归档与 Git 提交**

---

## 4. 实施日志

- **2026-11-01 19:35:00**：完成用户「开始」指令的需求复述与方案规划，启动单 Agent 闭环开发。
- **2026-11-01 19:42:00**：完成 `@taptap/cli` 全局安装与 offline doctor 验证，核验 15 组随包 AI Skills。
- **2026-11-01 19:48:00**：完成 `scripts/taptap-publish.sh` 脚本编写与权限配置，支持 `status`/`login`/`inspect`/`upload`/`review-check` 全套子命令。
- **2026-11-01 19:50:00**：完成本地物料扫描与 `--dry-run` 试跑验证，成功识别 `artifacts/daohua.apk`。
- **2026-11-01 19:52:00**：同步更新 `AGENTS.md` 常用脚本清单。
- **2026-11-01 19:55:00**：完成本地全部子命令验证与工程清单同步，准备提交 Git。
- **2026-11-01 20:05:00**：根据用户授权结果打通 WSL 凭据跨平台委托，开发者主体绑定为 `446202`（尐妖的工作室）。
- **2026-11-01 20:15:00**：通过 CLI 成功创建《道化太初》游戏草稿，生成专属应用 ID `1017706`（角色扮演 RPG，Android APK）。
- **2026-11-01 20:25:00**：将 Android 安装包实际包名 `applicationId` 规范对齐为 `com.yuohira.daohua`，重新打包出 2.2M 高清离线包。
- **2026-11-01 20:30:00**：执行 `scripts/taptap-publish.sh upload --yes` 成功上传包体（`apkId: 1074247`），并通过 `app select-package` 成功绑定为主包体槽位（消除 MISSING_PACKAGE 阻断项）。
- **2026-11-01 20:45:00**：通过 `app save-changes` 将游戏简介（description）、首页推荐语（promotion_text）、首页素材偏好（promotion_material_type: BANNER_4）、开发者的话（developer_message）与开发商名称（author_name）全量写入草稿库，所有建议项清空，必填项仅剩截图与宣传图素材。
- **2026-11-01 21:05:00**：启动 moss-art 技能管线，按用户指导完成「极简矢量多边形（Planar Polygonal Vector）· 仙山绝顶一剑开天」官方游戏图标设计与高精度 MOSS-Image（全量 35 步 + SLG Layer 22）生成。
- **2026-11-01 21:15:00**：完成玄青深邃夜空、上古周天星轨道盘、孤峰断崖绝壁与碎金开天裂隙的背景重构渲染，导出 512×512 标准 PNG 归档于 `art/icons/icon_daohua_cleave_bg_512.png`。
- **2026-11-01 21:20:00**：通过 `taptap-cli upload` 上传图标至素材库并绑定为 App Icon（消除 DEFAULT_ICON 告警，警告项与建议项全部清空为 0）。
- **2026-11-01 21:35:00**：按用户指令将剑修角色全面重构为「女性剑仙」，并完成顶部居中（Top-Centered）玄金书法「道化太初」Logo 字标的黄金比例复合排版，导出 `art/icons/icon_daohua_female_cleave_top_512.png`。
- **2026-11-01 21:40:00**：执行 `taptap-cli upload` 上传新图标（Asset ID: `745322`）并同步更新至草稿资料库，零告警提审健康度继续保持。
