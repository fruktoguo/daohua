# 传音频道与日志分组设计真源

> 本文是「游戏内日志分组 + 聊天（传音）系统」的设计真源。协议字段、频道语义、保留策略与前端展示口径以本文为准。

---

## 1. 面板分组

日志面板共 8 个分组（Tab），顺序固定：

| 分组 | 键 | 数据来源 |
| --- | --- | --- |
| 综合 | `all` | 系统日志 + 好友私聊 + 队伍群聊（三源按时间合并，只读，不提供输入） |
| 世界 | `world` | 世界频道聊天（全服在线修士） |
| 附近 | `nearby` | 附近频道聊天（同一地图节点在线修士） |
| 系统 | `system` | 系统日志（消耗品使用、行动完成、抵达、离线补算、斗法撮合与结算、各类系统通知） |
| 战斗 | `combat` | 战斗过程日志（`combat.*`：搜寻/遭遇/施放/攻击/胜负/复活） |
| 好友 | `friend` | 好友私聊（1:1，需先建立双向好友关系） |
| 宗门 | `sect` | 宗门频道（**当前仅占位**，宗门系统未立项，输入与展示均提示未开放） |
| 队伍 | `party` | 队伍群聊（队伍成员） |

- 综合**不包含**世界、附近、战斗、宗门。
- 系统与战斗由服务端日志事件（`LogBatch`）按 `channel` 字段分流。
- 世界/附近/好友/队伍/宗门为聊天消息（`ChatMessage`）。

---

## 2. 日志事件频道字段

`LogEventDto` 新增 `channel`（`"system"` | `"combat"`，缺省 `"system"`）。

分类规则（服务端 `SimulationLogMapper`）：

- 事件码前缀 `combat.` → `combat`；
- 其余（`action.*`、`world.arrived`、`offline.capped`、`auto-use.*`、`duel.*`）→ `system`。

前端 `gameLogService` 按 `channel` 过滤，系统/战斗两个 Tab 各自独立渲染；综合不消费日志（只消费系统频道）。

---

## 3. 聊天频道模型

聊天频道由「频道类型 + 作用域键」唯一确定：

| 频道 | `channel` | `scopeKey` | 可见范围 |
| --- | --- | --- | --- |
| 世界 | `world` | 空字符串 | 全部在线修士 |
| 附近 | `nearby` | 当前地图节点 ID | 同一节点的在线修士 |
| 好友 | `friend` | 两个玩家 ID 升序拼接 | 私聊双方 |
| 队伍 | `party` | 队伍 ID | 队伍全体成员 |
| 宗门 | `sect` | 宗门 ID | 未开放（占位） |

- 消息体长度上限 200 字符（去除首尾空白后非空）。
- 频道级发送节流：同一玩家同一频道 1 秒内最多 3 条，超出返回 `CHAT_RATE_LIMITED`。
- **不设计历史兼容**：聊天记录只保留最近 100 条/频道作用域，超出即删除。

---

## 4. 保留策略（100 条/作用域）

聊天消息落 PostgreSQL `social.chat_messages`：

- 每条消息插入后，在同一事务内删除该 `(channel, scope_key)` 下超出最新 100 条之外的行；
- 历史回放：玩家握手成功、以及切入某频道时，服务端下发该作用域最近 100 条（`ChatHistory`）；
- 世界频道为单作用域（`scope_key = ''`），全局共享 100 条。

---

## 5. 社交系统（好友 / 队伍）

### 5.1 好友

- 双向关系：申请（`friend.request`）→ 接受（`friend.accept`）/ 拒绝（`friend.reject`）→ 双向 `friendships` 落库；
- 解除（`friend.remove`）双向删除；
- 好友私聊：以双方 ID 升序拼接为 `scopeKey`，只有双方可收发。

### 5.2 队伍

- 邀请（`party.invite`）：邀请人未组队时**自建队伍并任队长**（邀请是组队的唯一入口，不单独暴露「创建队伍」指令）；被邀请人收到待处理邀请；
- 接受（`party.accept`）/ 拒绝（`party.decline`）：作用于自己待处理的邀请；
- 队伍上限 5 人（含队长）；只有队长可以邀请；
- 成员离开（`party.leave`）：队长离开时队长位移交剩余成员中最早加入者；成员全部离开后队伍解散；
- 队伍群聊作用域为 `party_id`，成员变更即时生效。

### 5.3 持久化表（schema v4，新增 `social` schema）

- `social.friend_requests`：好友申请（`pending`/`accepted`/`rejected`/`cancelled`），`(requester_id, target_id)` 的 pending 部分唯一索引防重复申请；
- `social.friendships`：双向好友关系（两行）；
- `social.parties` / `social.party_members`：队伍与成员（`player_id` 作主键，一名玩家最多一队）；
- `social.party_invitations`：待处理队伍邀请（`invitee_id` 作主键，每人最多一条）；
- `social.chat_messages`：聊天消息（含保留清理索引）。

---

## 6. 协议契约

新增 `WireMessageType`：

| 值 | 名称 | 方向 | 载荷 |
| --- | --- | --- | --- |
| 18 | `ChatSend` | C→S | `ChatSendDto` |
| 19 | `ChatMessage` | S→C | `ChatMessageDto` |
| 20 | `ChatHistory` | S→C | `ChatHistoryDto` |
| 21 | `SocialAction` | C→S | `SocialActionDto` |
| 22 | `SocialState` | S→C | `SocialStateDto` |
| 23 | `ChatHistoryRequest` | C→S | `ChatHistoryRequestDto` |

- `ChatSendDto { requestId, channel, body, targetPlayerId? }`：`targetPlayerId` 仅好友频道使用；正文去首尾空白后 1~200 字符。
- `ChatMessageDto { messageId, channel, scopeKey, senderId, senderName, body, sentAtUnixMilliseconds }`。
- `ChatHistoryDto { channel, scopeKey, targetPlayerId?, messages[] }`：`targetPlayerId` 仅好友频道回填，供客户端把会话映射到具体好友。
- `ChatHistoryRequestDto { channel, targetPlayerId? }`。
- `SocialActionDto { requestId, action, targetPlayerId?, targetName? }`，`action` ∈ `friend.request|friend.accept|friend.reject|friend.remove|party.invite|party.accept|party.decline|party.leave`：
  - `friend.request` 可用 `targetPlayerId` 或 `targetName`（道号检索）指定目标；
  - `friend.accept` / `friend.reject` 用 `targetPlayerId` 指向申请人；
  - `party.accept` / `party.decline` 作用于自己待处理的邀请，忽略 `targetPlayerId`；
  - `party.invite` 在自己未组队时**自建队伍并任队长**（邀请是组队的唯一入口）。
- `SocialStateDto { requestId?, friends[], incomingRequests[], outgoingRequests[], party?, partyInvitation?, nearbyPlayers[] }`：
  - `friends[]`：`FriendDto { playerId, displayName, online }`；
  - `incomingRequests[]` / `outgoingRequests[]`：`FriendRequestDto { requestId, playerId, displayName, createdAtUnixMilliseconds }`；
  - `party?`：`PartyDto { partyId, leaderId, members[] }`，`PartyMemberDto { playerId, displayName, online }`，队伍上限 5 人；
  - `partyInvitation?`：`PartyInvitationDto { partyId, inviterId, inviterName }`；
  - `nearbyPlayers[]`：`NearbyPlayerDto { playerId, displayName }`（同节点在线修士，含自己）。

`requestId` 回传用于指令配对；为 null 表示服务端主动推送（好友上线、队伍变更、附近修士变化）。

---

## 7. 服务端组件

- `ChatRuntime`（单例）：频道作用域解析、消息落库与广播、历史回放；
- `SocialRuntime`（单例）：好友与队伍的持久化读写、在线状态、附近修士快照；
- `OnlinePresenceRegistry`：在线会话 → 玩家 ID / 道号 / 当前节点，供世界、附近广播与在线状态判定；
- `WebSocketRuntime`：入站 `ChatSend` / `SocialAction` 分发，出站 `ChatMessage` / `ChatHistory` / `SocialState` 推送；
- 玩家节点变化（`world.travel` 抵达、握手加载快照）触发 `OnlinePresenceRegistry.UpdateNode`，并重算附近修士快照。

聊天与社交**不经过玩家 Actor 单写者锁**：它们不属于玩家聚合状态，由独立单例服务串行化处理，避免跨玩家互相阻塞。
