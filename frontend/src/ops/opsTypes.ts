/**
 * 运维面板服务端响应类型（与 `src/Server/Clean/Ops/OpsPanel.cs` 的端点一一对应）。
 *
 * 这些类型只描述面板只读视图与写操作回执，不参与玩家协议；
 * 字段名保持服务端 camelCase 序列化口径。
 */

/** 市场运行指标（与 MarketRuntimeMetrics 对齐）。 */
export interface MarketMetrics {
  partitionCount: number
  activeOrderCount: number
  placementCount: number
  matchCount: number
  cancelCount: number
  rejectedCount: number
  tradedQuantity: number
  tradedGrossAmount: number
}

/** 概览视图。 */
export interface OpsOverview {
  service: string
  version: string
  environment: string
  machineName: string
  processId: number
  startedAt: string
  uptimeSeconds: number
  workingSetBytes: number
  gcTotalMemoryBytes: number
  processorCount: number
  threadCount: number
  protocolVersion: number
  schemaVersion: number
  gameDataVersion: string
  gameDataFingerprint: string
  loadedPlayers: number
  sessions: number
  market: MarketMetrics
}

/** 健康检查单项。 */
export interface OpsHealthCheck {
  name: string
  status: string
  durationMs: number
  description: string | null
  exception: string | null
  tags: string[]
}

/** 健康视图。 */
export interface OpsHealth {
  status: string
  totalDurationMs: number
  checks: OpsHealthCheck[]
}

/** 玩家列表条目。 */
export interface OpsPlayerSummary {
  playerId: string
  accountId: string
  displayName: string
  stateVersion: number
  presence: string
  lastSimulatedAt: string
  actionCount: number
  assetCount: number
}

/** 玩家详情：进度、战斗、世界、队列、资产与最近日志。 */
export interface OpsPlayerDetail {
  playerId: string
  accountId: string
  displayName: string
  stateVersion: number
  presence: string
  createdAt: string
  lastSimulatedAt: string
  gameDataFingerprint: string
  progression: {
    realmLevel: number
    experience: number
    completedActions: number
    cultivationSeconds: number
    normalKills: number
    eliteKills: number
    bossKills: number
    totalDefeats: number
  }
  combat: {
    hitpoints: number
    maxHitpoints: number
    manapoints: number
    maxManapoints: number
    attackPower: number
    defense: number
  }
  world: { nodeId: string; x: number; y: number; edgeId: string | null; edgeProgress: number }
  counts: { actions: number; assets: number; buffs: number; learnedAbilities: number; equippedInstances: number }
  actions: Array<{
    id: string
    actionId: string
    status: string
    enqueuedAt: string
    progressSeconds: number
    remainingRepeats: number
    completedRepeats: number
    targetNodeId: string | null
    anchorNodeId: string | null
  }>
  assets: Array<{ assetId: string; variant: string; quantity: number; version: number }>
  touchedSections: string[] | null
  recentLogs: Array<{ sequence: number; timestampUnixMilliseconds: number; template: string; count: number; channel: string }>
}

/** 在线会话条目。 */
export interface OpsSession {
  playerId: string
  deviceFingerprint: string | null
  closing: boolean
}

/** 盘口档位。 */
export interface OpsBookLevel {
  price: number
  quantity: number
  orders: number
}

/** 盘口视图。 */
export interface OpsBook {
  assetId: string
  variant: string | null
  version: number
  capturedAt: string
  asks: OpsBookLevel[]
  bids: OpsBookLevel[]
}

/** 市场指标视图。 */
export interface OpsMarketMetricsView {
  ready: boolean
  partitionId: number
  partitionCount: number
  algorithm: string
  metrics: MarketMetrics
}

/** 挂单条目（与 MarketOrderDto 对齐）。 */
export interface OpsOrder {
  orderId: string
  ownerId: string
  assetId: string
  variant: string | null
  side: string
  price: number
  originalQuantity: number
  remainingQuantity: number
  status: string
  sequence: number
  createdAt: string
  expiresAt: string | null
}

/** 成交流水条目。 */
export interface OpsTrade {
  tradeId: string
  assetId: string
  variant: string
  price: number
  quantity: number
  buyerId: string
  sellerId: string
  tradedAt: string
}

/** 存档分段 schema 版本分布桶。 */
export interface OpsSchemaVersionBucket {
  schemaVersion: number
  sectionCount: number
}

/** 表体量条目。 */
export interface OpsTableSize {
  table: string
  totalBytes: number
}

/** 存储视图。 */
export interface OpsStorage {
  accountCount: number
  playerCount: number
  stateHeadCount: number
  activeLeaseCount: number
  outboxPending: number
  outboxLeased: number
  outboxDelivered: number
  outboxRetrying: number
  outboxOldestPendingSeconds: number | null
  flushQueueDepth: number
  flushQueueRetrying: number
  flushQueueOldestDueSeconds: number | null
  sectionRowCount: number
  sectionRawBytes: number
  sectionStoredBytes: number
  sectionSchemaVersions: OpsSchemaVersionBucket[]
  tableSizes: OpsTableSize[]
}

/** 调度视图。 */
export interface OpsScheduler {
  loadedPlayers: number
  nextAdvanceBoundaryUtc: string | null
  secondsToNextBoundary: number | null
  lag: { p50Seconds: number; p95Seconds: number; maxSeconds: number }
  queuedActions: { total: number; p50: number; max: number }
  byPresence: Array<{ presence: string; count: number }>
  slowest: Array<{
    playerId: string
    displayName: string
    lastSimulatedAt: string
    lagSeconds: number
    queuedActions: number
  }>
}

/** 计算节点视图条目。 */
export interface OpsComputeNode {
  name: string
  nodeId: string
  capacity: number
  inFlight: number
  available: number
  outboundDepth: number
  pendingResults: number
  draining: boolean
  uptimeSeconds: number
  runtimeFingerprint: string | null
  batchesSent: number
  batchesCompleted: number
  batchesFailed: number
  jobsApplied: number
  jobsFailed: number
  bytesSent: number
  lastRoundTripMs: number
  avgRoundTripMs: number
  maxRoundTripMs: number
}

/** 计算链路计数快照（字段按需读取，允许缺省）。 */
export interface OpsComputeMetrics {
  nodesConnected: number
  nodesDisconnected: number
  batchesWritten: number
  batchesCompleted: number
  batchesFailedWrite: number
  batchesFailedTimeout: number
  batchesFailedDisconnect: number
  batchesFailedContract: number
  batchesFailedError: number
  jobsClaimed: number
  jobsDispatched: number
  jobsApplied: number
  jobsReleased: number
  jobsDriftDiscarded: number
  jobsNodeFailed: number
  jobsPersistFailed: number
  jobsDead: number
  tokenRejected: number
  nodeLimitRejected: number
  fingerprintRejected: number
  handshakeRejected: number
  lateResultsDropped: number
  recentBatches?: {
    sampleCount: number
    roundTripP50Ms?: number
    roundTripP95Ms?: number
    roundTripMaxMs?: number
    roundTripAvgMs?: number
    waitAgeP50Ms?: number
    waitAgeP95Ms?: number
    waitAgeMaxMs?: number
    jobsP50?: number
    jobsMax?: number
    bytesP50?: number
    bytesMax?: number
    jobsHistogram?: Record<string, number>
  }
}

/** 计算视图。 */
export interface OpsCompute {
  enabled: boolean
  contractVersion: string
  dispatchContractVersion: string
  totals: { nodeCount: number; capacity: number; inFlight: number; pendingResults: number; outboundDepth: number }
  nodes: OpsComputeNode[]
  metrics: OpsComputeMetrics | null
  backlog: {
    pending: number
    pendingDue: number
    leased: number
    succeeded: number
    dead: number
    oldestPendingAgeSeconds: number | null
  } | null
}

/** 配置视图（连接串已由服务端脱敏为 host:port/db@user）。 */
export interface OpsConfig {
  service: string
  environment: string
  processRole: string
  protocolVersion: number
  schemaVersion: number
  gameDataVersion: string
  gameDataFingerprint: string
  storage: Record<string, string | number | boolean>
  auth: { sessionLifetimeDays: number }
  server: { maxWebSocketMessageBytes: number; webSocketOutboundCapacity: number; ecsShardCount: number }
  market: { partitionCount: number; partitionId: number }
  compute: Record<string, string | number | boolean>
  antiMultiOpen: Record<string, string | number | boolean>
}

/** 日志条目。 */
export interface OpsLogEntry {
  timestamp: string
  level: string
  category: string
  message: string
  exception: string | null
  sequence: number
}
