# 反多开与反小号模块设计规约

> TASK-20261004-10 的实现真源。服务端裁决，前端采集，数据库保存关联与策略。
> 自 TASK-20261024-04 起，在线准入实现（服务端 `AntiMultiOpen/` 与前端指纹/守卫实现）已迁入兄弟私有仓库 `DaoHua-Private`，拼装机制与兼容矩阵见 [private-modules.md](private-modules.md)；本文保留裁决语义与策略口径的完整设计。

## 1. 目标与边界

默认同一设备安装标识只能注册一个账号，同一稳定设备主体最多关联两个账号；同账号只保留一个在线连接。配置、网络、指纹与注册速度联合识别批量小号。浏览器无法证明自然人身份，上限是设备关联约束；换设备、清理所有存储或伪造环境仍可能绕过，目标是增加批量操作成本。

前端执行的代码不能通过加密永久隐藏。生产构建对 `antiMulti/*.ts` 执行字符串 RC4 编码与控制流混淆，关闭 source map；客户端不持有服务端 HMAC 密钥，不进行权威账号裁决。

## 2. 模块与默认策略

所有策略集中在 `platform.account_risk_policy`，不再绑定启动期 `AntiMultiOpen:*` 配置。

| 模块 ID | 默认行为 | 性质 |
| --- | --- | --- |
| `headless` | 识别为无头浏览器或自动化脚本运行环境时直接拒绝 | 硬限制（直接阻断） |
| `deviceId` | 相同安装标识最多注册 1 个账号 | 硬限制 |
| `deviceConfigHash` | 关联账号数达到 2 后加 45 分 | 软风险，同型号设备可能相同 |
| `ipAddress` | 关联账号数达到 2 后加 40 分 | 软风险 |
| `network` | 同网段关联数达到 2 后加 15 分 | 软风险 |
| `browserFingerprint` | 指纹关联数达到 2 后加 40 分 | 软风险 |
| `registrationVelocity` | 同 IP 一小时内成功注册达到 3 次后加 80 分 | 软风险，拒绝事件不计入速度 |
| `accountAssociation` | 相同稳定设备主体最多关联 2 个账号 | 硬限制 |
| `tabGuard` | 同浏览器标签页互斥，提供接管入口 | 客户端交互约束 |
| `singleSession` | 新连接顶替同账号旧连接 | 服务端在线所有权 |
| `onlineDeviceQuota` | 同设备最多 2 个不同账号在线 | 服务端在线配额 |
配置、IP、网段、指纹的评分覆盖注册、登录和握手；速度仅作用于注册。风险分数累加后截断至 100，默认阈值 80、处置 `deny`。`allow` 放行并记录，`review` 放行并标记复核，`deny` 拒绝本次请求。设备注册与主体关联额度直接拒绝，不受软风险处置影响。总开关、注册检测、设备信号必填、两类上限、软模块权重/阈值及所有模块开关均可动态调整。

关闭设备 ID 模块只关闭安装标识注册额度；账号关联模块仍独立使用稳定主体限制账号数。关闭配置模块只移除配置评分，不残留配置派生的主体限制。单账号互踢关闭时仍拒绝第二个同时连接，避免破坏玩家单写者所有权。

## 3. 信号边界与权威存储

- `AccountRiskRequestContext` 严格校验 64 位十六进制摘要和 UUID。稳定主体由受保护 Cookie 中的设备 ID 或稳定物理/配置指纹（`fingerprint:{hash}`）推导，禁止在请求缺少设备 Cookie 时生成随机无关联 UUID 导致主体硬限制被隐身/无痕模式穿透。默认设备 ID 模块开启且必填时，单独配置摘要不能替代设备 ID。
- 自动化与无头环境防御：前端多维检测 `navigator.webdriver`、软件光栅化 GPU（SwiftShader/llvmpipe/Mesa 等）、空插件列表与注入特征；服务端校验 User-Agent 与 Sec-CH-UA 客户端 Hints，命中自动化特征时直接拒绝注册并关闭连接（返回 `HEADLESS_BROWSER_BLOCKED`）。
- 服务端签发 `daohua.device` 的 Data Protection Cookie（HttpOnly、SameSite=Lax、HTTPS 时 Secure），有效 Cookie 优先于客户端重报的 ID。前端普通 Cookie 与 localStorage 双份保存安装 UUID，均属于可伪造信号，不是认证凭据。
- 来源 IP 只读取可信代理处理后的 `RemoteIpAddress`，IPv4-mapped 归一化；网段由服务端推导为 IPv4 `/24` 或 IPv6 `/64`。客户端上报的网络类型摘要不决定权威网段。
- `AccountRiskHasher` 使用数据库首次生成的 32 字节 HMAC 密钥。`identity.account_risk_signals` 只保存 HMAC 与关联时间，`identity.account_risk_events` 保存脱敏判定、理由和分数；原始 IP、设备配置和浏览器摘要不落库。
- 密钥存于 `platform.account_risk_key`，与关联数据一同备份。密钥与数据库同时泄露仍可能对低熵信号进行枚举，因此不能把 HMAC 当作数据库泄露后的匿名化保证。

## 4. 注册与登录事务

注册使用 ReadCommitted 事务，按种类/摘要固定顺序取得 PostgreSQL advisory xact lock，再查询关联和速度。等待锁后读取前一事务提交的结果，防止并发突破额度。通过后账号、玩家、会话、信号、审计与 outbox 同事务提交；拒绝只提交风险审计。

登录先校验口令，再在同一 ReadCommitted 事务执行关联额度、写风险事件；通过后才更新最近登录、签发会话与成功 outbox。拒绝不产生会话或成功登录事件。密码错误返回 401，风险拒绝返回 403。

每次注册、登录和 Hello 从数据库取得策略快照；同一请求的观测、准入和 HelloAck 使用同一份快照。策略仓储实例串行读写，避免旧读结果覆盖当前进程的新策略。非法信号也记录脱敏拒绝事件。事件种类为 `register`、`login`、`handshake`；运维统计是判定次数，不是独立登录人数。

## 5. WebSocket 在线准入

实现位于私有仓库 `DaoHua-Private/server/AntiMultiOpen/`（构建期注入 `DaoHua.Server.Clean` 程序集），经主仓库中性扩展点 `IHelloAdmissionHook`（`src/Server/Clean/Admission/`）接入 Hello 流程；主仓库默认实现为开放语义（多连接并存）：

- `DeviceQuotaRegistry` 单锁覆盖设备桶生命周期，按不同玩家计数，同账号重连使用引用计数。
- Hello 到达后先执行账号关联观测，再以设备 ID 优先的规范化键进行在线准入；摘要统一大小写，避免拆分配额桶。
- 完整版后端无条件校验 Hello 的 `ModuleDeclaration`（模块声明）：开源前端（恒 null）被 4010 拒绝，与策略开关无关。
- 互踢开启时先检查设备额度，再原子顶替旧连接；配额失败不踢旧会话。互踢关闭时先登记单会话，配额失败回滚登记。
- 拆除按会话实例配对，旧连接不卸载新连接的玩家实体；新连接等待旧会话拆除后加载快照。
- 首个合法 Hello 有 10 秒期限；未发送则关闭 1008。发送关闭帧最多等待 2 秒，不因客户端不响应无限占用连接。
- 关闭码 4005（被顶替）、4006（额度超限）、4007（设备信息缺失/无效）、4008（重复连接）、4010（要求专用客户端）禁止自动重连；用户可手动重试。

## 6. 前端接缝

- `network/accountSignals.ts`（主仓库）统一采集安装 ID、配置摘要、网络类型及辅助指纹；音频指纹最多等待 2 秒，不可用时返回缺失值。浏览器指纹合成算法经 `src/private/` 契约出口取自私有实现（开源 stub 恒返回 null）。
- `account.ts` 注册/登录发送 `riskSignals`；`gameClient.ts` Hello 发送 `deviceId`、`deviceConfigHash`、`deviceFingerprint` 与 `moduleDeclaration`（私有实现内嵌模块 ID，stub 恒 null）。
- 私有实现（挂载于 `src/private-impl/`）：`tabGuard.ts` 使用 BroadcastChannel 竞逐，无该能力时使用 localStorage 租约。被阻断页可接管；拆除停止心跳并清除自身租约。
- 私有 `index.ts` 安装/释放守卫，读取 HelloAck，并每 30 秒读取 `GET /api/v1/auth/client-policy`。关闭后已阻断标签页自动解除并连接；客户端只收到标签互斥布尔值，不获得服务端评分策略。
- `App.tsx` 负责阻断页与解除事件，仅经 `src/private/` 稳定出口交互。无需新增游戏全局状态。

## 7. 运维与留存

“账号检测”页复用 OpsCard、OpsTable、OpsStat 与 ConfirmDialog，显示策略、成功注册数、判定次数、最近 100 个事件、最近 100 组脱敏关联。写操作需要现有运维令牌、私有来源和二次确认，并产生策略更新审计；编辑草稿不会被自动刷新覆盖。

接口为 `GET /ops/api/account-abuse` 与 `POST /ops/api/account-abuse/policy`。沿用面板 Development-only 边界；生产不装配 Ops 路由，生产 Caddy 拦截 `/ops`。当前生产部署中修改策略需另行授权受控运维流程，本任务没有将开发面板开放到公网。

风险事件按 `Storage:AccountRiskEventRetentionDays`（默认 30 天）分批清理，稳定账号关联和 HMAC 密钥不随事件清理。schema 与分段标签统一为 v11（v11 重建 `account_risk_events.event_kind` 约束纳入 `handshake`，修复开发中间态 v10 库的旧约束；载荷结构未改变）；一次性迁移位于 `migrations/0010_v9_to_v10.sql` 与 `migrations/0011_v10_to_v11.sql`，不参与运行时。独立压测环境若需要大量注册，应显式调整策略，禁止在生产绕过检测。

## 8. 即时监听与交易层熔断防御

针对小号异步采伐、高价挂单、离线倒金洗钱的特征，确立基于原生事件持续监听与撮合即时熔断的纵深防御：

### 8.1 前端交互持续监听探针
- **`event.isTrusted` 全局捕获**：在 `window` 捕获阶段注册核心操作交互监听；凡由脚本 `element.click()` 或 `dispatchEvent` 触发的合成事件（`isTrusted === false`），直接就地阻断并上报异常。
- **前驱轨迹时钟监测**：持续监听 `pointermove`/`touchmove` 记录时间戳；点击发生时比对最后移动时间，若无前驱移动（原地瞬移点击），判定为自动化注入点击。
- **移动端触控面积与压力**：在触屏环境下检测 `radiusX/radiusY` 与 `force`，捕获虚拟点击器与模拟器特征的死板零面积触控。

### 8.2 坊市交易即时熔断
- **挂单价格倍率强校验**：普通材料（`category == "material"`）挂单单价不得超过其系统回收价（`sellPrice`）的 50 倍（`MaxMaterialPriceMultiplier`），从源头禁止低级材料充当天价倒金运钞车。
- **撮合关联即时熔断**：在订单撮合成交（`ApplyFillAsync`）瞬间，即时核验买卖双方关系：
  1. 买卖双方不可为同一玩家（`BuyerId == SellerId`）；
  2. 买卖双方不可属于同一设备主体（`Buyer.PersonKey == Seller.PersonKey`）；
  3. 买卖双方不可存在邀请上下级关联关系（`IsInviterOrInvitee`，查 `social.player_invitations`）；
  命中上述任一关联，撮合事务直接拒绝该成交，杜绝自买自卖定向套现。

### 8.3 WebSocket 发包间隔即时监听
- 对每个在线会话维护最近指令到达时差序列，若连续多笔指令发包间隔的标准差极小（小于 5ms，明显的 `setInterval` 机械循环），触发发包限流阻断。
