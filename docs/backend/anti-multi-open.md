# 防多开模块（AntiMultiOpen）设计规约

> 状态：已随 TASK-20260917-04 落地。本文档是该模块的唯一权威设计真源。

## 1. 目标与定位

防止同一玩家通过多标签页、多端登录或多账号同机挂机获得不当并发收益。模块按 ROI 分三道防线，**全部实现收拢在独立模块中**，与游戏主逻辑仅保留窄接缝：

| 防线 | 拦截目标 | 开关配置 | 实现位置 |
| :--- | :--- | :--- | :--- |
| 标签页互斥 | 同浏览器误开/多开标签页 | `AntiMultiOpen:TabGuardEnabled` | `frontend/src/antiMulti/` |
| 单账号会话唯一 | 同账号跨设备/跨窗口重复登录 | `AntiMultiOpen:SingleSessionKickEnabled` | `src/Server/Clean/AntiMultiOpen/` |
| 设备配额 | 同设备多账号并发挂机 | `AntiMultiOpen:DeviceQuotaEnabled` + `DeviceQuotaMaxAccounts` | 服务端模块 + 前端指纹采集 |

**不引入 Redis**（遵守 `commercial-persistence-architecture.md` 选型决策）：单实例部署下所有注册表均为进程内 `ConcurrentDictionary` 实现；socket 生命周期即在线真值，不需要心跳续期。

## 2. 服务端子系统 `src/Server/Clean/AntiMultiOpen/`

### 2.1 组件

- `AntiMultiOpenOptions`：`AntiMultiOpen:*` 配置节绑定与启动期校验（配额 1..64）。
- `AntiMultiOpenConstants`：自定义关闭码 `4005`（被顶替）/ `4006`（配额超限）/ `4007`（缺指纹）/ `4008`（重复连接被拒）与对应协议错误码；与 `frontend/src/antiMulti/codes.ts` 严格同步。
- `DeviceQuotaRegistry`：`指纹 → (PlayerId → 引用计数)`。同账号重复登记只占一个名额；连接断开引用归零即移除，设备为空即整条清除。
- `AntiMultiOpenAdmission`：准入仲裁器，`Admit(session, fingerprint)` 在 **Hello 握手时**调用一次，`Release(session)` 在会话拆除时调用。

### 2.2 关键裁决顺序

```
Hello 到达 → 配表指纹校验 → AntiMultiOpenAdmission.Admit
  ├─ 互踢开启：先设备配额校验（失败直接拒绝，不动旧会话）→ AddOrKick 原子替换 → 踢出旧会话
  └─ 互踢关闭：先 TryAdd 登记（失败即拒绝新连接）→ 再配额校验（失败回滚登记）
```

准入必须推迟到 Hello 的原因：设备指纹只随 `HelloRequest.DeviceFingerprint` 到达，accept 阶段无法裁决配额；若在 accept 期先踢旧会话，配额失败的登录会无谓踢掉在线连接。

### 2.3 顶替会话的安全拆除链

1. 新会话 `Admit` 成功 → 旧会话 `RequestClose(4005, …, SESSION_KICKED 通知帧)`：通知帧先入队再完成输出通道，发送循环冲刷后退出。
2. 新会话 `await kicked.TeardownCompleted`（10s 超时降级继续）→ 旧会话 finally：事件退订 → `Release` 按实例配对移除（返回 false，**不触发 `runtime.RemoveAsync`**，避免误删新会话正在加载的玩家实体）→ 按 `CloseHint` 发 4005 关闭帧 → `MarkTeardownCompleted`。
3. 新会话继续 `LoadAsync` → 全量快照。

旧会话 `Remove` 返回 false 是玩家实体卸载的否决条件——这是防多开引入顶替语义后必须守住的不变量。

### 2.4 主动终止的关闭路径

`WebSocketSession.RequestClose` 设置 `CloseHint` 并完成输出通道；`HandleAsync` 在 `WhenAny` 返回后给发送循环 500ms 冲刷窗口，确保通知帧先于关闭帧到达；finally 中按 `CloseHint` 发送 WebSocket 关闭帧。

## 3. 前端子系统 `frontend/src/antiMulti/`

- `tabGuard.ts`：BroadcastChannel 竞逐（claim/held/takeover 三消息 + bornAt/tabId 决胜）；无 BC 时降级 localStorage 租约（心跳续期 + 接管凭据）。被阻断页提供「在本页继续」接管入口。
- `deviceFingerprint.ts`：只采集**跨浏览器稳定**的硬件/系统特征（WebGL 渲染器、CPU 线程数、屏幕规格、时区、音频渲染微差），SHA-256 摘要；不采 Canvas/UA 等浏览器相关维度。进程内记忆化。
- `store.ts`：模块独立 zustand store（`tabBlocked` / `tabGuardEnabled`，后者持久化 localStorage 供连接建立前读取）。
- `codes.ts`：与服务端常量同步的关闭码表与提示词映射。
- `MultiTabBlockedPage.tsx`：阻断页（复用 status-screen 样式族）。

### 3.1 主逻辑接缝（全部）

- `App.tsx`：挂载时 `installAntiMultiOpen()`；`tabBlocked` 时渲染阻断页。
- `gameClient.ts`：握手携带 `deviceFingerprint`、HelloAck 应用 `tabGuardEnabled`、close 4005–4008 不重连并提示、`registerBlockedTeardown(dispose)`。
- `protocol.ts`：`HelloRequest.deviceFingerprint` / `HelloResponse.antiMultiOpen` 类型对齐。

## 4. 客户端行为约定

- 4005–4008 与既有 1008/4001–4004 一样**禁止自动重连**，防止乒乓互踢风暴；提示文案按码区分。
- 被顶替端显示「账号已在其他窗口或设备登录」，用户手动重连会把对方顶回——用户主动行为，允许。

## 5. 已知边界

- 指纹是软对抗：可被伪造插件/虚拟机绕过；Safari 会掩蔽 WebGL renderer 降低熵。定位为「提高小号成本」，不承载资产安全断言。
- 配额按「不同账号数」计；同账号同设备重连不占新名额（引用计数保证顶替不漂移）。
- 标签守卫开关经 HelloAck 下发并缓存 localStorage；服务端改开关后已打开页面需刷新生效。
