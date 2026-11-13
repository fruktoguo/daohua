---
name: gityx-publish
description: >-
  Git游戏（gityx.com）用户中心发帖工作流：基于本地 Cookie 鉴权校验、动态拉取专栏/分类与表单字段、上传封面图、将 Markdown 或 HTML 转换为富文本并发布文章，内置《道化太初》更新日志与攻略预设。适用于「gityx 发帖」「在 Git游戏发更新日志」「发布攻略/公告到 gityx」等请求。
---

# Git游戏（gityx.com）发帖 Skill

本 Skill 封装了 `https://gityx.com/user/release.html` 的真实表单提交、分类/字段查询与图片上传接口，用于在本地命令行直接向 Git游戏发布文章、更新日志与攻略。

> **安全红线**：
> 1. 登录凭证（`PHPSESSID`、`gtyx_did`）仅存于本机环境变量或被 `.gitignore` 忽略的 `.env.gityx`，**严禁**将任何 Cookie 写入 Git 跟踪文件、任务文档或代码。
> 2. 正式执行 `publish`（不带 `--dry-run`）前，**必须先运行 `--dry-run` 向用户展示标题、分类、字段与 HTML 预览，获得用户明确确认后再传入 `--confirm` 真正发布**；禁止私自向线上站点发测试帖。

---

## 一、Cookie 鉴权准备

`gityx.com/user/release.html` 依赖 `HttpOnly` 会话 Cookie，脚本按以下优先级读取凭证：

1. `--cookie "PHPSESSID=...; gtyx_did=..."`
2. 环境变量 `GITYX_COOKIE`
3. 环境变量 `GITYX_PHPSESSID`（及可选 `GITYX_DID`、`GITYX_EVER_LOGIN`、`GITYX_HOME_VER`）
4. 仓库根目录 `.env.gityx`（已被 `.gitignore` 规则 `.env.*` 忽略）：

```dotenv
GITYX_PHPSESSID=你的PHPSESSID
GITYX_DID=你的gtyx_did
GITYX_EVER_LOGIN=1
GITYX_HOME_VER=2
```

当 `check` 返回重定向至登录页时，从浏览器 DevTools → Application → Cookies (`https://gityx.com`) 重新拷贝最新 `PHPSESSID` 即可（有效期通常约 3 天）。

---

## 二、CLI 命令速查

脚本入口（零第三方依赖，Node.js 22+ 直接运行）：

```bash
node .agents/skills/gityx-publish/scripts/gityx-publish.mjs <command> [options]
```

### 1. 检查登录态与近期文章（`check`）

```bash
node .agents/skills/gityx-publish/scripts/gityx-publish.mjs check
```

### 2. 拉取专栏分类列表（`categories`）

```bash
node .agents/skills/gityx-publish/scripts/gityx-publish.mjs categories --molds article
```

常用 `article` 分类 ID（`tid`）速查：

| `tid` | 分类名称 | 路由 | 常见用途 |
| :--- | :--- | :--- | :--- |
| `65` | 更新日志 | `help/changelog` | 游戏版本更新日志（`--preset daohua-changelog` 默认） |
| `9` | 教程/攻略 | `help/faq` | 玩法攻略、新手指南（`--preset daohua-guide` 默认） |
| `37` | 网络放置 | `web/online` | 网络放置游戏投稿（《道化太初》主条目为 `1575.html`） |
| `47` | 原创激情 | `web/yuanchuang` | 原创网页游戏投稿 |
| `63` | 存档分享 | `help/savebank` | 存档分享 |

### 3. 查询指定分类动态字段（`fields`）

```bash
node .agents/skills/gityx-publish/scripts/gityx-publish.mjs fields --tid 65
```

### 4. 上传封面图（`upload-image`）

向 `https://gityx.com/common/uploads.html` 上传 `.png`/`.jpg`/`.webp`/`.gif` 并返回站内 URL：

```bash
node .agents/skills/gityx-publish/scripts/gityx-publish.mjs upload-image path/to/cover.png
```

### 5. 预览与发布文章（`publish`）

**第一步：先跑 `--dry-run` 检查最终载荷与转换后的 HTML（不发上传和发帖请求）：**

```bash
node .agents/skills/gityx-publish/scripts/gityx-publish.mjs publish \
  --preset daohua-changelog \
  --title "道化太初 v0.3.2 更新日志：组队消耗品调度优化与凝气归元修炼" \
  --keywords "道化太初,更新日志,修仙放置" \
  --description "新增凝气归元三级修炼行动，修复组队消耗品冻结，优化战斗模拟器一致性。" \
  --body-file path/to/changelog.md \
  --dry-run
```

**第二步：向用户展示预览并获确认后，追加 `--confirm` 执行真实发布：**

```bash
node .agents/skills/gityx-publish/scripts/gityx-publish.mjs publish \
  --preset daohua-changelog \
  --title "道化太初 v0.3.2 更新日志：组队消耗品调度优化与凝气归元修炼" \
  --keywords "道化太初,更新日志,修仙放置" \
  --description "新增凝气归元三级修炼行动，修复组队消耗品冻结，优化战斗模拟器一致性。" \
  --body-file path/to/changelog.md \
  --confirm
```

---

## 三、预设与常用扩展参数

- `--preset daohua-changelog`：自动填充 `molds=article`、`tid=65`（更新日志）、`short_title=道化太初`、`keywords=更新日志,道化太初`、`litpic`（官方封面图）、`game_only=3`（联网）、`zuozhe=尐妖`、`qqun=940886387`、`gameurl=https://gityx.com/web/online/1575.html`。
- `--preset daohua-guide`：同上，将 `tid` 设为 `9`（教程/攻略）、`keywords=教程攻略,道化太初`。
- `--id <postId>`：编辑**管理员已分配维护权限**的已有文章（自动附带 `id=<postId>` 与 `isshow=1`；保存后站点会重新进入审核）。
- 任意 `--field key=value` 或快捷选项（`--zuozhe`、`--version`、`--cover-file`、`--litpic`、`--format markdown|html`）均可覆盖预设。
- 提示：站内表单提示 `description` 150 字以内、`body` 1500 字以内；脚本会在 `--dry-run` 与结果中自动提示超长摘要，但不做静默截断。
