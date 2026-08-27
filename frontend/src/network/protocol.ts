/**
 * daohua wire.v1 客户端协议。
 *
 * 传输层只有一个固定的 DHW1 二进制信封；业务载荷当前使用 JSON 编码，
 * 这样可以在协议字段冻结的同时保持浏览器端可诊断性。任何未知版本、消息
 * 类型、编码或长度都会被拒绝，不尝试解析旧的 JSON 包格式。
 */

export const PROTOCOL_VERSION = 1 as const
export const MAX_FRAME_BYTES = 1_048_576
export const WIRE_HEADER_BYTES = 36

export const WireMessageType = {
  Hello: 1,
  HelloAck: 2,
  Snapshot: 3,
  SnapshotDelta: 4,
  ActionIntent: 5,
  ActionResult: 6,
  AssetView: 7,
  MarketBook: 8,
  MarketOrder: 9,
  Error: 10,
  Ping: 11,
  Pong: 12,
  ComputeAdvanceRequest: 13,
  ComputeAdvanceResponse: 14,
  Command: 15,
  CommandResult: 16,
} as const
export type WireMessageType = (typeof WireMessageType)[keyof typeof WireMessageType]

export const WireCodec = {
  None: 0,
  Json: 1,
  Protobuf: 2,
} as const
export type WireCodec = (typeof WireCodec)[keyof typeof WireCodec]

export interface WireEnvelope {
  protocolVersion: number
  messageType: WireMessageType
  requestId: string
  sequence: number
  codec: WireCodec
  payload: Uint8Array
}

export interface HelloRequest {
  clientVersion: string
  gameDataFingerprint?: string | null
}

export interface HelloResponse {
  serverVersion: string
  protocolVersion: number
  gameDataFingerprint: string
  serverSequence: number
}

export interface PlayerHeadDto {
  playerId: string
  stateVersion: number
  schemaVersion: number
  lastSimulatedAt: string
  gameDataFingerprint: string
  checksum: string
  updatedAt: string
}

export interface PlayerSectionDto {
  section: string
  sectionVersion: number
  schemaVersion: number
  codec: string
  payloadBase64: string
  rawBytes: number
  storedBytes: number
  checksum: string
  updatedAt: string
}

export interface AssetBalanceDto {
  assetId: string
  variant?: string | null
  quantity: number
  version: number
}

export interface PlayerSnapshotDto {
  playerId: string
  displayName: string
  lastSimulatedAt: string
  stateVersion: number
  head: PlayerHeadDto
  sections: PlayerSectionDto[]
  assets: AssetBalanceDto[]
}

export interface PlayerSnapshotDeltaDto {
  playerId: string
  stateVersion: number
  changedSections: PlayerSectionDto[]
  changedAssets?: AssetBalanceDto[] | null
}

export interface ActionIntentRequest {
  requestId: string
  actionId: string
  repeatCount?: number
  targetNodeId?: string | null
  parameters?: Record<string, number> | null
}

export interface ActionIntentResponse {
  requestId: string
  actionInstanceId: string
  actionId: string
  enqueuedAt: string
  remainingRepeats: number
}

export interface CommandRequest {
  requestId: string
  command: string
  payload: unknown
  expectedStateVersion?: number
}

export interface CommandResultDto {
  requestId: string
  succeeded: boolean
  code: string
  message: string
  stateVersion: number
  payload?: unknown | null
}

export interface ErrorDto {
  code: string
  message: string
  requestId?: string | null
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
  return Number.isInteger(value) && value >= WireMessageType.Hello && value <= WireMessageType.CommandResult
}

function isWireCodec(value: number): value is WireCodec {
  return value === WireCodec.None || value === WireCodec.Json || value === WireCodec.Protobuf
}

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
  const requestBytes = bytes.slice(8, 24)
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
    payload: bytes.slice(WIRE_HEADER_BYTES),
  }
}

export function decodeJsonPayload<T>(envelope: WireEnvelope): T {
  if (envelope.codec !== WireCodec.Json) throw new Error('当前客户端只接受 JSON 业务载荷。')
  const value: unknown = JSON.parse(textDecoder.decode(envelope.payload))
  return value as T
}

export function createRequestId(): string {
  return crypto.randomUUID()
}
