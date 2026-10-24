# TASK-20261024-08 prod发布技能增加完整版APK自动发布GitHub-Releases

## 一、任务信息

| 项目 | 内容 |
| :--- | :--- |
| 任务编号 | TASK-20261024-08 |
| 任务性质 | 基础设施建设 / 发布流水线增强 / 自动化运维 |
| 提出时间 | 2026-10-24 18:10:00 |
| 需求分析完成 | 2026-10-24 18:15:00 |
| 发布完成 | 待定 |
| 涉及模块 | `scripts/build-apk.sh`、`scripts/upload-release-apk.sh`、`scripts/release-prod.sh`、`prod-release` Skill |

## 二、需求复述与明确理解

1. **业务目标**：
   在正式服发布流水线（`prod-release` Skill）中，将编译打包出的 Android 离线安装包（APK）自动发布到 GitHub 仓库（`fruktoguo/daohua`）的 Releases 资产列表中，供玩家和测试人员直接下载最新客户端。
2. **核心业务与技术约束**：
   - **完全版本硬闸门**：正式服发布的 APK 必须包含兄弟私有仓库（`DaoHua-Private`）中的反多开子系统与专有设备指纹防御逻辑。若私有模块未挂载，打包必须硬失败，严禁打出开源 stub 版导致玩家连接正式服遭遇 4010 拒绝。
   - **流水线编排优化**：修正过去流 D（APK 构建）与流 A（镜像构建）并发争抢前端工作区的冲突隐患（Pitfall 2），改为在流 A/B/C 及主服部署完成后串行独占执行，随后立即自动上传至 GitHub Releases。
   - **网络韧性**：上传脚本必须自动探测本地 Clash 代理端口（7890）并配置 `HTTPS_PROXY`，防止因 GitHub API 连接重置或超时中断发布。
   - **产物与 Git 隔离**：APK 文件继续严格保持在 `artifacts/daohua.apk`，受 Git 忽略红线保护。

## 三、详细设计方案

### 1. 独立上传脚本 `scripts/upload-release-apk.sh`
- 输入参数：`--tag <TAG>`、`--file <PATH>`、`--title <TITLE>`、`--notes <NOTES>`。
- 代理检测：检测 `127.0.0.1:7890` 是否开放，自动设置 `HTTPS_PROXY`。
- 上传逻辑：
  - 判断 Tag 对应的 Release 是否已存在；
  - 存在时通过 `gh release upload <tag> <file>#daohua.apk --clobber` 覆盖资产；
  - 不存在时通过 `gh release create <tag> <file>#daohua.apk --title <title> --notes <notes>` 创建 Release；
  - 输出可公开访问的 Releases 页面地址及 APK 直接下载直链。

### 2. 打包脚本 `scripts/build-apk.sh` 改造
- 新增 `--require-full` 开关：前置调用 `scripts/sync-private-modules.mjs`，若 `frontend/src/private-impl/index.ts` 不存在则立即报错中止；
- 新增 `--release` 开关：打包成功后自动调用 `scripts/upload-release-apk.sh`；
- 输出横幅明确标识构建口径：`[口径: 完整版 (含反多开子系统与专有防护)]`。

### 3. 一键发布脚本 `scripts/release-prod.sh` 改造
- 时序调优：默认不在阶段 1 并发流 D，而在阶段 3（主服部署与 odin 热重载）完成之后，独占执行流 D（携带 `--require-full` 与 `--release`）；
- 增加 `--skip-apk-upload` 选项（仅本地打包不上传）；
- 在阶段 4 验证总结中打印 GitHub Releases 下载链接。

### 4. 技能与规范同步
- 更新 `.agents/skills/prod-release/SKILL.md`：规范化流 D 与 Releases 上传流程；
- 更新 managed-skill `daohua-prod-release-pitfalls`。

## 四、子任务分解

- [x] S1 编写 `scripts/upload-release-apk.sh` 自动化上传脚本
- [x] S2 改造 `scripts/build-apk.sh`：注入完整版私有模块守卫与 `--release` 选项
- [x] S3 改造 `scripts/release-prod.sh`：时序解耦为后置独占并打通 Releases 自动上传
- [x] S4 更新 `prod-release` Skill 与 `daohua-prod-release-pitfalls`
- [x] S5 实测验证：将当前已生成的完整版 APK 发布到 GitHub Releases，验证直链可用性
- [x] S6 代码验证与 Git 自动提交收尾

## 五、进度日志

- 2026-10-24 18:10:00 任务提出，完成需求分析与架构设计。
- 2026-10-24 18:15:00 任务详情文档登记完成，进入实施。
- 2026-10-24 18:16:00 完成 `scripts/upload-release-apk.sh` 编写，支持本地 Clash 代理自适应探测与覆盖上传。
- 2026-10-24 18:17:30 改造 `scripts/build-apk.sh` 与 `scripts/release-prod.sh`，增加 `--require-full` 强校验与 Releases 自动发布闭环。
- 2026-10-24 18:18:40 同步更新仓库 Skill `prod-release` 与本地托管 Skill `daohua-prod-release-pitfalls`。
- 2026-10-24 18:20:00 实测将当前完整版 APK 发布至 GitHub Releases `v0.1.0-sha-041398c2`，直链验证通过，任务闭环。
