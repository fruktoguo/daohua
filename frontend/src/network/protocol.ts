/**
 * 道化太初客户端二进制信封协议（DHW1 = DaoHua Wire v1）。
 *
 * 核心机制：
 * 1. 报头固定 36 字节：Magic(4B) + Version(2B) + Type(1B) + Codec(1B) + RequestId(16B) + Sequence(8B) + PayloadLength(4B)；
 * 2. 大端序（Big Endian）数字打包，.NET Guid 混合端序转换以保证跨平台/语言 UUID 标识绝对一致；
 * 3. 业务载荷当前统一使用 UTF-8 JSON 编码，兼顾高性能与浏览器端 DevTools 可观测性。
 */

/** 当前信封协议版本 */
export const PROTOCOL_VERSION = 1 as const
/** 单帧最大允许字节数（1MB） */
export const MAX_FRAME_BYTES = 1_048_576
/** 二进制信封定长报头字节数 */
export const WIRE_HEADER_BYTES = 36

/** 信封消息类型枚举，与服务端 WireMessageType 严格一致 */
export const WireMessageType = {
  /** 客户端初次握手问候 */
  Hello: 1,
  /** 服务端握手确认与指纹应答 */
  HelloAck: 2,
  /** 全量快照下发 */
  Snapshot: 3,
  /** 增量快照下发 */
  SnapshotDelta: 4,
  /** 行动意图请求 */
  ActionIntent: 5,
  /** 行动意图响应 */
  ActionResult: 6,
  /** 资产视图同步 */
  AssetView: 7,
  /** 坊市盘口深度 */
  MarketBook: 8,
  /** 坊市挂单操作 */
  MarketOrder: 9,
  /** 通用协议错误 */
  Error: 10,
  /** 心跳探活 Ping */
  Ping: 11,
  /** 心跳应答 Pong */
  Pong: 12,
  /** 调度冷挂机计算请求 */
  ComputeAdvanceRequest: 13,
  /** 计算节点补算应答 */
  ComputeAdvanceResponse: 14,
  /** 通用玩家指令 */
  Command: 15,
  /** 通用玩家指令执行回执 */
  CommandResult: 16,
  /** 战报日志事件批次推送 */
  LogBatch: 17,
  /** 客户端传音发言 */
  ChatSend: 18,
  /** 服务端广播的单条传音消息 */
  ChatMessage: 19,
  /** 服务端回放频道最近 100 条传音 */
  ChatHistory: 20,
  /** 客户端社交指令（好友申请/队伍邀请等） */
  SocialAction: 21,
  /** 服务端下发社交状态（好友/申请/队伍/附近修士） */
  SocialState: 22,
  /** 客户端请求频道历史回放 */
  ChatHistoryRequest: 23,
  /** 客户端登记/清除单位详情观察目标（火忘帧，无回执） */
  UnitInspect: 24,
  /** 客户端请求组队大厅筛选结果 */
  PartyLobbyRequest: 25,
  /** 服务端下发组队大厅：公开队伍与匹配中散人列表 */
  PartyLobby: 26,
} as const
export type WireMessageType = (typeof WireMessageType)[keyof typeof WireMessageType]

/** 载荷编码格式枚举 */
export const WireCodec = {
  None: 0,
  Json: 1,
  Protobuf: 2,
} as const
export type WireCodec = (typeof WireCodec)[keyof typeof WireCodec]

/** 解码后的统一网络信封结构 */
export interface WireEnvelope {
  /** 信封协议版本（当前恒为 1） */
  protocolVersion: number
  /** 消息类型 */
  messageType: WireMessageType
  /** 请求关联 ID（UUID；服务端回执/推送时原样回传用于配对） */
  requestId: string
  /** 服务端状态版本序号（玩家状态单调递增；心跳类帧为连接级序号） */
  sequence: number
  /** 载荷编码（当前业务恒为 Json） */
  codec: WireCodec
  /** 原始载荷字节 */
  payload: Uint8Array
}

/** Hello 握手请求载荷 */
export interface HelloRequest {
  /** 客户端构建版本标识 */
  clientVersion: string
  /** 客户端持有的配表指纹；与服务端不一致时服务端会要求重拉目录 */
  gameDataFingerprint?: string | null
  /** 防多开设备指纹（硬件特征哈希），服务端启用设备配额时必填 */
  deviceFingerprint?: string | null
}

/** 防多开子系统下发的功能开关 */
export interface AntiMultiOpenConfigDto {
  tabGuardEnabled: boolean
}

/** HelloAck 握手应答载荷 */
export interface HelloResponse {
  /** 服务端构建版本标识 */
  serverVersion: string
  /** 服务端协商确认的信封协议版本 */
  protocolVersion: number
  /** 服务端当前配表指纹 */
  gameDataFingerprint: string
  /** 服务端推送序列基线 */
  serverSequence: number
  /** 防多开开关下发（缺省表示服务端未配置） */
  antiMultiOpen?: AntiMultiOpenConfigDto | null
}

/** 玩家快照头（持久化元数据摘要） */
export interface PlayerHeadDto {
  playerId: string
  /** 玩家整体状态版本（乐观并发与增量对齐基准） */
  stateVersion: number
  /** 快照结构版本 */
  schemaVersion: number
  /** 服务端最近模拟推进时刻（ISO 时间） */
  lastSimulatedAt: string
  /** 快照生成时配表指纹 */
  gameDataFingerprint: string
  /** 头部校验和 */
  checksum: string
  updatedAt: string
}

/** 玩家分段状态单元（聚合内 Progression/World/Combat 等独立版本化的切片） */
export interface PlayerSectionDto {
  /** 分段标识（对应服务端 PlayerSectionKind） */
  section: string
  /** 该分段独立版本号 */
  sectionVersion: number
  /** 分段载荷结构版本 */
  schemaVersion: number
  /**
   * 分段业务载荷：规范化 JSON 直接内嵌（替代旧 Base64 二次编码）。
   * 信封 JSON 中该成员的原始字节区间即 checksum 哈希输入，解码器对线上字节逐位复算。
   */
  data: unknown
  /** 分段校验和（规范化 JSON 字节的 SHA-256 小写 hex） */
  checksum: string
  updatedAt: string
}

/** 资产余额条目（货币/材料等非物品资产） */
export interface AssetBalanceDto {
  /** 资产 ID */
  assetId: string
  /** 资产变体（如强化等级；null 表示无变体） */
  variant?: string | null
  /** 持有数量 */
  quantity: number
  /** 资产版本号 */
  version: number
}

/** 全量玩家快照载荷（Snapshot 帧） */
export interface PlayerSnapshotDto {
  playerId: string
  /** 显示道号 */
  displayName: string
  /** 服务端权威模拟时刻（前端服务器时钟锚点基准） */
  lastSimulatedAt: string
  /** 快照对应的整体状态版本 */
  stateVersion: number
  /** 快照头元数据 */
  head: PlayerHeadDto
  /** 全量分段列表 */
  sections: PlayerSectionDto[]
  /** 全量资产列表 */
  assets: AssetBalanceDto[]
}

/** 增量快照载荷（SnapshotDelta 帧，仅携带变化的分段与资产） */
export interface PlayerSnapshotDeltaDto {
  playerId: string
  /** 增量后的整体状态版本；小于等于本地版本时整包丢弃（乱序保护） */
  stateVersion: number
  /** 本次增量的服务端权威模拟时刻，用于校准前端服务端时钟锚点 */
  lastSimulatedAt?: string
  /** 本次发生变化的分段 */
  changedSections: PlayerSectionDto[]
  /** 本次发生变化的资产（null/缺省表示无资产变更） */
  changedAssets?: AssetBalanceDto[] | null
  /** 当前道号：身份标量不属任何分段，随增量透传使改名即时生效 */
  displayName?: string | null
}

/** ActionIntent 行动入队请求载荷 */
export interface ActionIntentRequest {
  /** 请求关联 ID（与 ActionResult 配对） */
  requestId: string
  /** 配表行动 ID */
  actionId: string
  /** 重复轮次；负值（约定 -1）为无限循环 */
  repeatCount?: number
  /** 目标世界节点（历练/远征类行动使用） */
  targetNodeId?: string | null
  /** 附加数值参数表 */
  parameters?: Record<string, number> | null
  /** 是否勾选斗法（PVP 匹配），仅战斗遭遇行动有效 */
  pvpEnabled?: boolean
}

/** ActionResult 行动入队回执载荷 */
export interface ActionIntentResponse {
  /** 对应请求的关联 ID */
  requestId: string
  /** 服务端分配的队列条目实例 ID（后续移动/取消操作引用它） */
  actionInstanceId: string
  /** 配表行动 ID */
  actionId: string
  /** 入队时刻（ISO 时间） */
  enqueuedAt: string
  /** 剩余轮次；负值为无限循环 */
  remainingRepeats: number
}

/** Command 通用玩家指令请求载荷 */
export interface CommandRequest {
  /** 请求关联 ID（与 CommandResult 配对） */
  requestId: string
  /** 指令名（如 'action.enqueue'、'queue.move'） */
  command: string
  /** 指令业务载荷 */
  payload: unknown
  /** 乐观并发校验的期望状态版本；缺省/0 表示不校验 */
  expectedStateVersion?: number
}

/** 命令回执快照形态：与服务端 CommandResultSnapshotForms 严格一一对应 */
export const CommandResultSnapshotForm = {
  /** 全量快照兜底：snapshot 字段为 PlayerSnapshotDto */
  Snapshot: 'snapshot',
  /** 增量快照：snapshot 字段为 PlayerSnapshotDeltaDto，与推送增量共用合并语义 */
  Delta: 'delta',
} as const
export type CommandResultSnapshotForm = (typeof CommandResultSnapshotForm)[keyof typeof CommandResultSnapshotForm]

/** CommandResult 指令执行回执载荷 */
export interface CommandResultDto {
  /** 对应请求的关联 ID */
  requestId: string
  /** 指令是否执行成功 */
  succeeded: boolean
  /** 结果代码（成功为约定成功码，失败为服务端错误码） */
  code: string
  /** 可展示的结果/错误文案 */
  message: string
  /** 执行后的整体状态版本 */
  stateVersion: number
  /** 回执快照形态：snapshot=全量 / delta=触及分段增量 */
  snapshotForm: CommandResultSnapshotForm
  /** 快照载荷；具体 DTO 由 snapshotForm 判定，不再做模糊探测 */
  snapshot?: unknown | null
}

/** Error 协议级错误帧载荷 */
export interface ErrorDto {
  /** 服务端错误码 */
  code: string
  /** 可展示错误文案 */
  message: string
  /** 关联的请求 ID（能定位时回传） */
  requestId?: string | null
}

/** 单条日志事件（传音道历原始记录） */
export interface LogEventDto {
  /** 服务端日志序号 */
  sequence: number
  /** 发生时刻（Unix 毫秒） */
  timestampUnixMilliseconds: number
  /** 语义模板文本（含 {{kind:id|label}} 引用标记） */
  template: string
  /** 服务端侧已折叠的重复次数 */
  count?: number
  /** 日志分组频道：`system`（系统）或 `combat`（战斗），缺省视为 `system` */
  channel?: LogChannel
}

/** 日志分组频道（服务端 LogEventDto.channel） */
export type LogChannel = 'system' | 'combat'

/** 日志分组频道标识，与服务端 LogChannels 严格一致 */
export const LogChannel = {
  /** 系统分组 */
  System: 'system',
  /** 战斗分组 */
  Combat: 'combat',
} as const

/** LogBatch 日志批次推送载荷 */
export interface LogBatchDto {
  events: LogEventDto[]
}

/** 传音频道标识，与服务端 ChatChannels 严格一致 */
export const ChatChannel = {
  /** 世界频道（全服在线修士） */
  World: 'world',
  /** 附近频道（同一地图节点在线修士） */
  Nearby: 'nearby',
  /** 好友私聊频道 */
  Friend: 'friend',
  /** 队伍频道 */
  Party: 'party',
  /** 宗门频道（占位未开放） */
  Sect: 'sect',
} as const
export type ChatChannel = (typeof ChatChannel)[keyof typeof ChatChannel]

/** ChatSend 传音发言请求载荷 */
export interface ChatSendDto {
  /** 请求唯一标识 */
  requestId: string
  /** 目标频道标识 */
  channel: ChatChannel
  /** 消息正文 */
  body: string
  /** 好友频道私聊对象玩家 ID */
  targetPlayerId?: string | null
}

/** 传音消息内嵌的队伍卡片快照；队伍解散后缺席（party_ref 置空） */
export interface PartyLinkDto {
  /** 队伍唯一标识（点击加入的凭据） */
  partyId: string
  /** 队伍名称 */
  name: string
  /** 目标类型（见 PartyActivityKind） */
  activityKind: PartyActivityKind
  /** 目标节点 ID；空串表示不限 */
  nodeId: string
  /** 加入身价门槛；0 表示不设门槛 */
  minValuation: number
  /** 发布时刻的队伍人数 */
  memberCount: number
}

/** 单条传音消息 */
export interface ChatMessageDto {
  messageId: string
  channel: ChatChannel
  /** 频道作用域键 */
  scopeKey: string
  senderId: string
  senderName: string
  body: string
  sentAtUnixMilliseconds: number
  /** 内嵌队伍卡片；无链接或队伍已解散时为 null */
  partyLink?: PartyLinkDto | null
}

/** ChatHistory 频道历史回放载荷 */
export interface ChatHistoryDto {
  channel: ChatChannel
  scopeKey: string
  /** 好友频道的私聊对象玩家 ID；其余频道为 null */
  targetPlayerId?: string | null
  messages: ChatMessageDto[]
}

/** ChatHistoryRequest 频道历史回放请求载荷 */
export interface ChatHistoryRequestDto {
  channel: ChatChannel
  targetPlayerId?: string | null
}

/** 队伍目标类型标识，与服务端 PartyActivityKinds 严格一致（招募标签口径，不参与战斗结算） */
export const PartyActivityKind = {
  /** 不限目标 */
  Any: 'any',
  /** 讨伐战斗 */
  Combat: 'combat',
  /** 历练采集 */
  Exploration: 'exploration',
  /** 吐纳修炼 */
  Cultivation: 'cultivation',
  /** 炼丹 */
  Alchemy: 'alchemy',
  /** 炼器 */
  Forging: 'forging',
  /** 符箓 */
  Talisman: 'talisman',
  /** 阵法 */
  Formation: 'formation',
  /** 强化 */
  Enhancement: 'enhancement',
} as const
export type PartyActivityKind = (typeof PartyActivityKind)[keyof typeof PartyActivityKind]

/** 社交动作标识，与服务端 SocialActions 严格一致 */
export const SocialAction = {
  FriendRequest: 'friend.request',
  FriendAccept: 'friend.accept',
  FriendReject: 'friend.reject',
  FriendRemove: 'friend.remove',
  PartyInvite: 'party.invite',
  PartyAccept: 'party.accept',
  PartyDecline: 'party.decline',
  PartyLeave: 'party.leave',
  /** 创建队伍并任队长（party 载荷必填） */
  PartyCreate: 'party.create',
  /** 更新队伍档案补丁（队长） */
  PartyUpdate: 'party.update',
  /** 解散队伍（队长） */
  PartyDisband: 'party.disband',
  /** 移出成员（队长；targetPlayerId 必填） */
  PartyKick: 'party.kick',
  /** 按队伍 ID 加入（大厅公开队伍与聊天链接共用；partyId 必填） */
  PartyJoin: 'party.join',
  /** 登记/更新匹配条件并尝试自动并入公开队伍 */
  PartyMatchSet: 'party.match.set',
  /** 撤下匹配登记 */
  PartyMatchClear: 'party.match.clear',
  /** 把当前队伍卡片发布到指定传音频道（channel 必填，好友频道附带 targetPlayerId） */
  PartyShare: 'party.share',
} as const
export type SocialAction = (typeof SocialAction)[keyof typeof SocialAction]

/** 队伍档案补丁：party.create 时五字段语义齐全，party.update 仅应用非空字段 */
export interface PartyProfileDto {
  /** 队伍名称（1~32 字符；创建时空值由服务端生成默认名） */
  name?: string | null
  /** 目标类型 */
  activityKind?: PartyActivityKind | null
  /** 目标节点 ID；空串表示不限 */
  nodeId?: string | null
  /** 身价门槛；0 表示不设门槛 */
  minValuation?: number | null
  /** 是否进入组队大厅 */
  isPublic?: boolean | null
}

/** SocialAction 社交指令请求载荷 */
export interface SocialActionDto {
  requestId: string
  action: SocialAction
  /** 目标玩家 ID；接受/拒绝队伍邀请时忽略；party.kick/party.share(friend) 必填 */
  targetPlayerId?: string | null
  /** 目标道号；仅在好友申请且未提供 targetPlayerId 时用于检索 */
  targetName?: string | null
  /** 目标队伍 ID；仅 party.join 使用 */
  partyId?: string | null
  /** 目标传音频道；仅 party.share 使用 */
  channel?: ChatChannel | null
  /** 队伍档案补丁；party.create/party.update/party.match.set 使用 */
  party?: PartyProfileDto | null
}

/** 好友条目 */
export interface FriendDto {
  playerId: string
  displayName: string
  online: boolean
}

/** 好友申请条目 */
export interface FriendRequestDto {
  requestId: string
  playerId: string
  displayName: string
  createdAtUnixMilliseconds: number
}

/** 队伍成员条目 */
export interface PartyMemberDto {
  playerId: string
  displayName: string
  online: boolean
  /** 成员身价（服务端估值口径，非战斗属性） */
  valuation: number
}

/** 队伍状态 */
export interface PartyDto {
  partyId: string
  leaderId: string
  /** 队伍名称（空名由服务端回落默认名后下发，此处恒非空） */
  name: string
  /** 目标类型 */
  activityKind: PartyActivityKind
  /** 目标节点 ID；空串表示不限 */
  nodeId: string
  /** 身价门槛；0 表示不设门槛 */
  minValuation: number
  /** 是否进入组队大厅 */
  isPublic: boolean
  members: PartyMemberDto[]
}

/** 同节点在线修士条目 */
export interface NearbyPlayerDto {
  playerId: string
  displayName: string
}

/** 待处理的队伍邀请条目 */
export interface PartyInvitationDto {
  partyId: string
  inviterId: string
  inviterName: string
  /** 邀请方队伍名称 */
  partyName?: string | null
}

/** 本人「匹配中」散人登记 */
export interface PartySeekerDto {
  /** 目标类型 */
  activityKind: PartyActivityKind
  /** 目标节点 ID；空串表示不限 */
  nodeId: string
  /** 期望队友的最低身价；0 表示不设要求 */
  minValuation: number
  /** 登记时刻（Unix 毫秒） */
  createdAtUnixMilliseconds: number
}

/** SocialState 社交状态下发载荷 */
export interface SocialStateDto {
  /** 关联的社交指令请求 ID；主动推送时为 null */
  requestId?: string | null
  friends: FriendDto[]
  incomingRequests: FriendRequestDto[]
  outgoingRequests: FriendRequestDto[]
  party?: PartyDto | null
  partyInvitation?: PartyInvitationDto | null
  nearbyPlayers: NearbyPlayerDto[]
  /** 本人匹配中登记；未登记为 null */
  seeking?: PartySeekerDto | null
}

/** PartyLobbyRequest 组队大厅筛选请求载荷 */
export interface PartyLobbyRequestDto {
  /** 请求唯一标识（回执配对键） */
  requestId: string
  /** 目标类型筛选；空/缺省表示不限 */
  activityKind?: PartyActivityKind | null
  /** 目标节点筛选；空/缺省表示不限 */
  nodeId?: string | null
  /** 身价下限筛选：队伍按成员最低身价、散人按本人身价过滤；空/0 表示不限 */
  minValuation?: number | null
}

/** 大厅公开队伍的成员预览条目 */
export interface PartyLobbyMemberDto {
  playerId: string
  displayName: string
  online: boolean
  valuation: number
}

/** 大厅公开队伍条目 */
export interface PartyLobbyEntryDto {
  /** 队伍唯一标识（加入凭据） */
  partyId: string
  name: string
  activityKind: PartyActivityKind
  nodeId: string
  leaderId: string
  leaderName: string
  minValuation: number
  createdAtUnixMilliseconds: number
  members: PartyLobbyMemberDto[]
}

/** 大厅「匹配中」散人条目 */
export interface PartyLobbySeekerDto {
  playerId: string
  displayName: string
  online: boolean
  valuation: number
  activityKind: PartyActivityKind
  nodeId: string
  minValuation: number
  createdAtUnixMilliseconds: number
}

/** PartyLobby 组队大厅下发载荷 */
export interface PartyLobbyDto {
  /** 关联的 PartyLobbyRequest 请求 ID */
  requestId: string
  /** 请求者本人当前身价（判定加入可行性） */
  myValuation: number
  /** 公开队伍列表（按创建时间倒序，至多 50 条） */
  parties: PartyLobbyEntryDto[]
  /** 匹配中散人列表（按登记时间倒序，至多 50 条） */
  seekers: PartyLobbySeekerDto[]
}

const MAGIC = new Uint8Array([0x44, 0x48, 0x57, 0x31]) // DHW1
const textEncoder = new TextEncoder()
const textDecoder = new TextDecoder()

function assertSafeSequence(sequence: number): void {
  if (!Number.isSafeInteger(sequence) || sequence < 0) {
    throw new Error('协议序列号无效。')
  }
}

function normalizeUuid(value: string): string {
  const normalized = value.trim().toLowerCase()
  if (!/^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$/.test(normalized)) {
    throw new Error('协议请求 ID 必须是 UUID。')
  }
  if (/^0{8}-0{4}-0{4}-0{4}-0{12}$/.test(normalized)) {
    throw new Error('协议请求 ID 不能为全零 UUID。')
  }
  return normalized
}

/** 将 RFC 4122 文本 UUID 转换为 .NET Guid.TryWriteBytes 的混合端序布局。 */
function uuidToDotnetBytes(value: string): Uint8Array {
  const uuid = normalizeUuid(value).replaceAll('-', '')
  const bytes = new Uint8Array(16)
  const source = Array.from({ length: 16 }, (_, index) => Number.parseInt(uuid.slice(index * 2, index * 2 + 2), 16))
  bytes.set([source[3], source[2], source[1], source[0], source[5], source[4], source[7], source[6], ...source.slice(8)])
  return bytes
}

/** 将 .NET Guid 字节布局还原为浏览器标准 UUID 文本。 */
function dotnetBytesToUuid(bytes: Uint8Array): string {
  if (bytes.length !== 16) throw new Error('协议请求 ID 长度无效。')
  const source = [bytes[3], bytes[2], bytes[1], bytes[0], bytes[5], bytes[4], bytes[7], bytes[6], ...bytes.slice(8)]
  const hex = source.map(byte => byte.toString(16).padStart(2, '0')).join('')
  return `${hex.slice(0, 8)}-${hex.slice(8, 12)}-${hex.slice(12, 16)}-${hex.slice(16, 20)}-${hex.slice(20)}`
}

function isWireMessageType(value: number): value is WireMessageType {
  return Number.isInteger(value) && value >= WireMessageType.Hello && value <= WireMessageType.PartyLobby
}

function isWireCodec(value: number): value is WireCodec {
  return value === WireCodec.None || value === WireCodec.Json || value === WireCodec.Protobuf
}

/** 打包一帧二进制信封（大端序）；类型/编码/序列号/载荷长度非法时抛错 */
export function encodeEnvelope(
  messageType: WireMessageType,
  requestId: string,
  sequence: number,
  payload: Uint8Array,
  codec: WireCodec = WireCodec.Json,
): ArrayBuffer {
  if (!isWireMessageType(messageType)) throw new Error('协议消息类型无效。')
  if (!isWireCodec(codec)) throw new Error('协议编码类型无效。')
  if (payload.byteLength > MAX_FRAME_BYTES) throw new Error('协议载荷超过大小限制。')
  assertSafeSequence(sequence)
  const output = new ArrayBuffer(WIRE_HEADER_BYTES + payload.byteLength)
  const bytes = new Uint8Array(output)
  bytes.set(MAGIC, 0)
  const view = new DataView(output)
  view.setUint16(4, PROTOCOL_VERSION, false)
  view.setUint8(6, messageType)
  view.setUint8(7, codec)
  bytes.set(uuidToDotnetBytes(requestId), 8)
  view.setBigInt64(24, BigInt(sequence), false)
  view.setUint32(32, payload.byteLength, false)
  bytes.set(payload, WIRE_HEADER_BYTES)
  return output
}

/** 将业务对象序列化为 UTF-8 JSON 并打包为信封帧 */
export function encodeJsonEnvelope(
  messageType: WireMessageType,
  requestId: string,
  sequence: number,
  payload: unknown,
): ArrayBuffer {
  const serialized = JSON.stringify(payload)
  if (serialized === undefined) throw new Error('协议 JSON 载荷不能为空。')
  return encodeEnvelope(messageType, requestId, sequence, textEncoder.encode(serialized), WireCodec.Json)
}

/**
 * 解包一帧二进制信封并做完整边界校验（魔数/版本/类型/UUID/序列号/长度一致性）。
 * 任何字段非法即抛错——调用方负责把解析失败计入渐进熔断统计而非静默吞掉。
 */
export function decodeEnvelope(input: ArrayBuffer | Uint8Array): WireEnvelope {
  const bytes = input instanceof Uint8Array ? input : new Uint8Array(input)
  if (bytes.byteLength < WIRE_HEADER_BYTES || bytes.byteLength > WIRE_HEADER_BYTES + MAX_FRAME_BYTES) {
    throw new Error('协议帧长度无效。')
  }
  for (let index = 0; index < MAGIC.length; index += 1) {
    if (bytes[index] !== MAGIC[index]) throw new Error('协议帧魔数无效。')
  }
  const view = new DataView(bytes.buffer, bytes.byteOffset, bytes.byteLength)
  const protocolVersion = view.getUint16(4, false)
  if (protocolVersion !== PROTOCOL_VERSION) throw new Error(`不支持的协议版本: ${protocolVersion}。`)
  const rawType = view.getUint8(6)
  const rawCodec = view.getUint8(7)
  if (!isWireMessageType(rawType) || !isWireCodec(rawCodec)) throw new Error('协议消息类型或编码无效。')
  // subarray 视图零拷贝：dotnetBytesToUuid 只读消费，WS 每帧独立 ArrayBuffer 无共享写风险。
  const requestBytes = bytes.subarray(8, 24)
  const requestId = dotnetBytesToUuid(requestBytes)
  if (/^0{8}-0{4}-0{4}-0{4}-0{12}$/.test(requestId)) throw new Error('协议请求 ID 不能为空。')
  const sequenceBig = view.getBigInt64(24, false)
  if (sequenceBig < 0n || sequenceBig > BigInt(Number.MAX_SAFE_INTEGER)) throw new Error('协议序列号超出安全范围。')
  const sequence = Number(sequenceBig)
  const payloadLength = view.getUint32(32, false)
  if (payloadLength > MAX_FRAME_BYTES || payloadLength !== bytes.byteLength - WIRE_HEADER_BYTES) {
    throw new Error('协议载荷长度无效。')
  }
  return {
    protocolVersion,
    messageType: rawType,
    requestId,
    sequence,
    codec: rawCodec,
    // subarray 视图零拷贝：下游仅 decodeJsonPayload → TextDecoder.decode 只读消费。
    payload: bytes.subarray(WIRE_HEADER_BYTES),
  }
}

/** 将信封 JSON 载荷解码为业务对象（非 Json 编码直接抛错） */
export function decodeJsonPayload<T>(envelope: WireEnvelope): T {
  if (envelope.codec !== WireCodec.Json) throw new Error('当前客户端只接受 JSON 业务载荷。')
  const value: unknown = JSON.parse(textDecoder.decode(envelope.payload))
  return value as T
}

/** 生成新的请求关联 UUID（Command/ActionIntent/HTTP 命令统一入口） */
export function createRequestId(): string {
  return crypto.randomUUID()
}
