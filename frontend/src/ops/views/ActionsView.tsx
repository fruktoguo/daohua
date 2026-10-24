import { useState } from 'react'
import { ConfirmDialog } from '../../components/ConfirmDialog'
import { opsPost } from '../opsClient'
import { OpsCard, OpsPill } from '../opsUi'

/** 待确认的运维写操作。 */
interface PendingAction {
  title: string
  description: string
  danger: boolean
  run: () => Promise<unknown>
}

/**
 * 运维操作视图：全部写操作先经二次确认，再携带一次性令牌提交。
 * 结果与失败原因都留在页面上，不做静默处理。
 */
export function OpsActionsView(): React.JSX.Element {
  const [playerId, setPlayerId] = useState('')
  const [nodeId, setNodeId] = useState('')
  const [pending, setPending] = useState<PendingAction | null>(null)
  const [result, setResult] = useState<string | null>(null)
  const [error, setError] = useState<string | null>(null)
  const [busy, setBusy] = useState(false)

  const execute = async (action: () => Promise<unknown>): Promise<void> => {
    setBusy(true)
    setError(null)
    setResult(null)
    try {
      setResult(JSON.stringify(await action(), null, 2))
    } catch (cause) {
      setError(cause instanceof Error ? cause.message : String(cause))
    } finally {
      setBusy(false)
    }
  }

  const requirePlayerId = (): string | null => {
    const value = playerId.trim()
    if (!value) {
      setError('请先填写玩家 ID。')
      return null
    }
    return value
  }

  return (
    <>
      <OpsCard title="运维操作（全部需要二次确认）">
        <p className="ops-note">
          写操作需要面板令牌：未填写令牌或令牌错误会返回 401。操作记录会写入服务端审计日志（不含令牌）。
        </p>
        <div className="ops-toolbar">
          <label>
            玩家 ID
            <input value={playerId} placeholder="玩家 UUID（踢下线 / 存档 / 卸载）" onChange={event => setPlayerId(event.target.value)} />
          </label>
          <label>
            计算节点 ID
            <input value={nodeId} placeholder="留空表示全部在线节点" onChange={event => setNodeId(event.target.value)} />
          </label>
        </div>
        <div className="ops-toolbar">
          <button
            type="button"
            disabled={busy}
            onClick={() => {
              const target = requirePlayerId()
              if (!target) return
              setPending({
                title: '踢下线',
                description: `将立即断开玩家 ${target} 的在线连接（关闭码 4011），客户端会自行重连。`,
                danger: true,
                run: () => opsPost('/actions/kick', { playerId: target }),
              })
            }}
          >
            踢下线
          </button>
          <button
            type="button"
            disabled={busy}
            onClick={() => setPending({
              title: '强制存档（全部）',
              description: '立即把所有已加载玩家的存档分段落盘（忽略落盘节流）。',
              danger: false,
              run: () => opsPost('/actions/flush', { playerId: null, force: true }),
            })}
          >
            强制存档（全部）
          </button>
          <button
            type="button"
            disabled={busy}
            onClick={() => {
              const target = requirePlayerId()
              if (!target) return
              setPending({
                title: '强制存档（单个玩家）',
                description: `立即把玩家 ${target} 的存档分段落盘（忽略落盘节流）。`,
                danger: false,
                run: () => opsPost('/actions/flush', { playerId: target, force: true }),
              })
            }}
          >
            强制存档（单个）
          </button>
          <button
            type="button"
            disabled={busy}
            onClick={() => {
              const target = requirePlayerId()
              if (!target) return
              setPending({
                title: '卸载玩家运行时',
                description: `将把玩家 ${target} 从运行时卸载（先落盘再卸载）。若该玩家仍在线，客户端会因失去会话而重连。`,
                danger: true,
                run: () => opsPost('/actions/unload', { playerId: target, flush: true }),
              })
            }}
          >
            卸载玩家
          </button>
          <button
            type="button"
            disabled={busy}
            onClick={() => {
              const target = nodeId.trim()
              setPending({
                title: target ? '排空指定计算节点' : '排空全部计算节点',
                description: target
                  ? `节点 ${target} 将停止接收新批次，在飞批次跑完后自然退出。`
                  : '全部在线计算节点将停止接收新批次（节点组排空）。',
                danger: false,
                run: () => opsPost('/actions/compute-drain', { nodeId: target || null }),
              })
            }}
          >
            排空计算节点
          </button>
        </div>
      </OpsCard>

      <OpsCard title="最近一次操作结果">
        {error && <p className="ops-error-text">失败：{error}</p>}
        {result && <pre className="ops-result">{result}</pre>}
        {!error && !result && <p className="ops-empty">尚未执行任何写操作。</p>}
        {busy && <OpsPill tone="warn">执行中…</OpsPill>}
      </OpsCard>

      <ConfirmDialog
        open={pending !== null}
        title={pending?.title ?? ''}
        description={pending?.description ?? ''}
        danger={pending?.danger ?? false}
        confirmLabel="确认执行"
        cancelLabel="取消"
        onConfirm={() => {
          const action = pending
          if (action) void execute(action.run)
        }}
        onClose={() => setPending(null)}
      />
    </>
  )
}
