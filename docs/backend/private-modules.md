# 私有模块拼装机制设计真源

> TASK-20261024-04 的架构真源。主仓库开源化拆分的权威规范：兄弟私有仓库、构建期拼装、模块声明强制配对。

## 1. 目标与拓扑

主仓库保持可开源（不含反多开等私密实现），私密子系统独立存放于兄弟私有仓库 `DaoHua-Private`：

```
<工作区>/
  DaoHua/           主仓库（开源；只保留中性接口与开源默认实现）
  DaoHua-Private/   私有仓库（反多开前后端实现与私有测试）
```

不注册 submodule、不发布私有 NuGet/npm 包：主仓库 Git 内对私有仓库零引用痕迹。拼装遵循「**存在即完整，缺失即开源**」——构建工具探测兄弟目录（后端 `DaoHuaPrivateRoot` MSBuild 属性，默认 `$(仓库根)../DaoHua-Private`；前端探测 `frontend/src/private-impl/` 挂载点），探测结果决定两种口径之一：

| 口径 | 后端 | 前端 | 行为 |
| --- | --- | --- | --- |
| 开源 | 不注入私有源码，`OpenHelloAdmissionHook` 生效 | `private/impl.ts` = stub | 无反多开，同账号多连接并存 |
| 完整 | 私有源码编入 `DaoHua.Server.Clean` 程序集 | vite alias 重定向到私有实现 | 反多开全套裁决 + 模块声明强制 |

## 2. 服务端拼装

- **注入机制**：`Server.Clean.csproj` 以 `DaoHuaPrivateRoot` 属性条件 `Compile Include="$(DaoHuaPrivateRoot)/server/**/*.cs"` 把私有源码编进 `DaoHua.Server.Clean` 程序集（该工程本就显式列举 Compile 项）；私有代码与主仓库类型同程序集，零循环引用、无需接口桥接膨胀。
- **扩展点**（主仓库 `src/Server/Clean/Admission/`，开源可见且中性）：
  - `IHelloAdmissionHook`：Hello 握手准入三面——`Admit`（裁决）、`Release`（会话释放配对）、`TabGuardEnabled`（HelloAck 开关下发）。
  - `HelloAdmissionVerdict`：裁决结果含踢线通知字段（错误码/文案/关闭码由实现提供，主仓库不持有任何防线常量）。
  - `OpenHelloAdmissionHook`：默认开放语义——无条件放行、`SessionRegistry.TryAdd` 登记首会话（广播与运维可见性）、同账号后续连接静默共存。
  - `IHostModuleStartup` + `HostModuleDiscovery`：`Program.cs` 启动期反射发现注入程序集内的模块装配入口，由私有模块注册覆盖默认钩子。
- **协议**：`HelloRequest.ModuleDeclaration`（可选，中性字段）。完整版后端在准入内**无条件**校验（与策略开关无关）：缺失或不匹配 → 关闭码 **4010** 拒绝（`common.net.dedicatedClientRequired`），保证「完整后端拒绝开源前端」。开源后端忽略该字段。前端 stub 恒上报 null。
- **关闭码占用**：4001-4004 鉴权、4005-4008 反多开（私有）、4009 慢消费者（主仓库）、**4010 专用客户端要求**、4011 运维踢线（主仓库，前端默认路径自动重连）。

## 3. 前端拼装

- **稳定出口** `frontend/src/private/`：`contract.ts`（契约类型 `PrivateClientModuleContract`）→ `stub.ts`（开源默认实现）→ `impl.ts`（默认 re-export stub）→ `index.ts`（主仓库调用点唯一 import 路径）。
- **挂载点** `frontend/src/private-impl/`（.gitignore）：`scripts/sync-private-modules.mjs` 在 dev/verify 启动时把 `../DaoHua-Private/frontend/antiMulti` 以 junction（Windows）/ symlink（POSIX）挂载于此。目录深度与原 `src/antiMulti` 一致，私有实现内部相对 import 零改动。
  - **跨环境约束（易踩）**：挂载必须由「将来消费它的那套工具链」创建。Windows junction 在 WSL 内可正常解析，但 WSL（node 跑在 `/mnt/d`）创建的 symlink 指向 `/mnt/d/...` 这类 WSL 专属路径，Windows 侧完全无法解析——`vite.config.ts` 的 `existsSync` 判定私有模块缺失而回退开源 stub，握手不带 `moduleDeclaration`，完整版服务端随即以 4010 拒绝，前端只显示「该服务器要求使用专用客户端」。因此脚本以「能否解析到挂载入口 `index.ts`」为唯一判据：可解析即原样保留（不论谁创建），不可解析才重建。同一工作区同时用 Windows `dev.sh` 与 WSL `verify.sh` 时不会再互相破坏。
- **构建切换**：`vite.config.ts` 探测挂载点存在时以正则 alias（`/private\/impl$/`）把 impl 重定向到 `private-impl/index.ts`；构建混淆插件路径同步匹配 `/src/private-impl/`。tsc 始终按 stub 类型检查主仓库调用点；私有实现自带 tsconfig（`DaoHua-Private/frontend/tsconfig.json`）在完整口径 verify 中独立 `tsc --noEmit` 检查，并以契约对象赋值校验导出面。
- **留存边界**：`accountSignals.ts`（登录/注册风险信号采集框架）留在主仓库 `frontend/src/network/`（服务于开源的账号风控）；仅浏览器指纹合成（`getBrowserFingerprint`）经契约出口取自私有实现。关闭码 4005-4008 的用户提示词条保留主词典（文案不含实现细节，开源前端连官方服被拒时也需要展示）。

## 4. 构建与部署口径

| 入口 | 口径 | 机制 |
| --- | --- | --- |
| `dotnet build` / `dev.sh` | 自动（探测） | MSBuild 默认探测兄弟目录 |
| `verify.sh` | 双口径 | 开源口径必跑不变；检测到私有仓库时追加私有后端测试与完整版前端构建/类型检查 |
| `docker compose -f compose.yaml` | 开源 | 构建上下文无私有内容，`DaoHuaPrivateRoot=/src/Private` 路径不存在自动跳过 |
| `docker-build-prod.sh` / `release-prod.sh` | 完整（强制） | staging 复制：主仓库源码 + `../DaoHua-Private` → `src/Private` 与 `frontend/src/private-impl` 真实目录；私有仓库缺失时快速失败 |

生产镜像始终完整版；开源用户以主仓库自建永远得到开源版。`Dockerfile` 单一双口径：publish 传 `-p:DaoHuaPrivateRoot=/src/Private`，MSBuild 通配在路径缺失时零匹配自动降级。

## 5. 兼容矩阵与安全边界

| 组合 | 结果 |
| --- | --- |
| 开源后端 + 开源前端 | 正常连接 |
| 完整后端 + 开源前端 | 拒绝（4010，禁止自动重连） |
| 完整后端 + 完整前端 | 正常连接 |
| 完整前端 + 开源后端 | 优雅降级放行（开源后端忽略声明） |

模块声明是客户端自声明，挡无心之失与普通用户；对抗价值来自指纹算法与裁决策略本身移出开源仓库。服务端权威裁决（配额/互踢）不依赖前端诚实。

## 6. 开源发布前置（独立任务，不在日常开发内）

1. 完整历史备份（`git bundle` + 私有远端），验证可恢复。
2. 主仓库 squash 重发（orphan 初始提交）或 `git filter-repo` 清洗（已裁定倾向 squash）。
3. 全文审计遗留泄露面：`AGENTS.md`、`docs/tasks/`、任务面板、历史文档中的反多开实现描述。
4. 公开远端只跑开源口径 CI。
