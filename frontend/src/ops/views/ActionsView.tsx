import { useCallback, useEffect, useState } from 'react'
import { ConfirmDialog } from '../../components/ConfirmDialog'
import { opsGet, opsPost } from '../opsClient'
import { OpsCard, OpsPill } from '../opsUi'

/** 运维邮件附件草稿项。 */
interface MailAttachmentDraft {
  itemId: string
  quantity: string
}

/** 运维全服群发广播条目投影。 */
interface OpsBroadcastMailItem {
  broadcastId: number
  title: string
  senderName: string
  attachmentCount: number
  onlyExistingPlayers: boolean
  createdAt: string
  expiresAt: string | null
  revokedAt?: string | null
  isActive: boolean
}

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

  // 传书发放工坊与全服广播管理状态
  const [mailMode, setMailMode] = useState<'personal' | 'broadcast'>('personal')
  const [targetPlayerId, setTargetPlayerId] = useState('')
  const [targetDisplayName, setTargetDisplayName] = useState('')
  const [onlyExistingPlayers, setOnlyExistingPlayers] = useState(true)
  const [mailTitle, setMailTitle] = useState('')
  const [mailSender, setMailSender] = useState('仙门执事')
  const [mailExpireDays, setMailExpireDays] = useState('30')
  const [mailNeverExpire, setMailNeverExpire] = useState(false)
  const [mailBody, setMailBody] = useState('')
  const [mailAttachments, setMailAttachments] = useState<MailAttachmentDraft[]>([])
  const [broadcasts, setBroadcasts] = useState<OpsBroadcastMailItem[]>([])
  const [loadingBroadcasts, setLoadingBroadcasts] = useState(false)
  const [broadcastError, setBroadcastError] = useState<string | null>(null)

  const loadBroadcasts = useCallback(async (signal?: AbortSignal) => {
    setLoadingBroadcasts(true)
    setBroadcastError(null)
    try {
      const data = await opsGet<{ items: OpsBroadcastMailItem[] }>('/mails/broadcasts', signal)
      setBroadcasts(data.items)
    } catch (cause) {
      if (signal?.aborted) return
      setBroadcastError(cause instanceof Error ? cause.message : String(cause))
    } finally {
      if (!signal?.aborted) setLoadingBroadcasts(false)
    }
  }, [])

  useEffect(() => {
    const controller = new AbortController()
    void loadBroadcasts(controller.signal)
    return () => controller.abort()
  }, [loadBroadcasts])

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
          <button
            type="button"
            disabled={busy}
            onClick={() => setPending({
              title: '重建全服排行榜快照',
              description: '立即重算 12 张全服榜单并覆写 PostgreSQL 整点派生快照（不会影响玩家权威存档）。',
              danger: false,
              run: () => opsPost('/leaderboards/rebuild', {}),
            })}
          >
            重建排行榜
          </button>
        </div>
      </OpsCard>

      <OpsCard title="传书发放工坊（个人定向 / 全服群发）">
        <div className="ops-toolbar">
          <label>
            发放模式
            <select
              value={mailMode}
              onChange={event => setMailMode(event.target.value as 'personal' | 'broadcast')}
            >
              <option value="personal">个人定向传书</option>
              <option value="broadcast">全服群发公告</option>
            </select>
          </label>
          {mailMode === 'personal' ? (
            <>
              <label>
                目标玩家 UUID
                <input
                  value={targetPlayerId}
                  placeholder="可选，优先于道号"
                  onChange={event => setTargetPlayerId(event.target.value)}
                />
              </label>
              <label>
                目标修士道号
                <input
                  value={targetDisplayName}
                  placeholder="未填 UUID 时按道号精确查找"
                  onChange={event => setTargetDisplayName(event.target.value)}
                />
              </label>
            </>
          ) : (
            <label>
              <input
                checked={onlyExistingPlayers}
                type="checkbox"
                onChange={event => setOnlyExistingPlayers(event.target.checked)}
              />
              仅当前已注册存量玩家可见（默认勾选，避免新号重复领补偿）
            </label>
          )}
        </div>

        <div className="ops-toolbar">
          <label>
            传书标题
            <input
              value={mailTitle}
              placeholder="例如：系统停服维护补偿"
              onChange={event => setMailTitle(event.target.value)}
            />
          </label>
          <label>
            发件署名为
            <input
              value={mailSender}
              placeholder="仙门执事"
              onChange={event => setMailSender(event.target.value)}
            />
          </label>
          <label>
            有效天数
            <input
              type="number"
              min={1}
              max={365}
              disabled={mailNeverExpire}
              value={mailExpireDays}
              onChange={event => setMailExpireDays(event.target.value)}
            />
          </label>
          <label>
            <input
              checked={mailNeverExpire}
              type="checkbox"
              onChange={event => setMailNeverExpire(event.target.checked)}
            />
            永不过期
          </label>
        </div>

        <div className="ops-toolbar">
          <label style={{ flex: '1 1 100%', alignItems: 'stretch', flexDirection: 'column' }}>
            传书正文（支持 {'{{item:id|名称}}'} 等富文本引用）
            <textarea
              rows={3}
              value={mailBody}
              placeholder="道友安好，本次发放的修行物资已随信奉上…"
              onChange={event => setMailBody(event.target.value)}
            />
          </label>
        </div>

        <div className="ops-toolbar">
          <button
            type="button"
            disabled={busy || mailAttachments.length >= 12}
            onClick={() => setMailAttachments(prev => [...prev, { itemId: 'currency.spirit-stone', quantity: '100' }])}
          >
            + 添加附件
          </button>
          {mailAttachments.map((att, index) => (
            <span key={index} style={{ display: 'inline-flex', alignItems: 'center', gap: 6 }}>
              <input
                placeholder="itemId"
                value={att.itemId}
                onChange={event => setMailAttachments(prev => prev.map((row, i) => (i === index ? { ...row, itemId: event.target.value } : row)))}
              />
              <input
                type="number"
                min={1}
                style={{ width: 90 }}
                placeholder="数量"
                value={att.quantity}
                onChange={event => setMailAttachments(prev => prev.map((row, i) => (i === index ? { ...row, quantity: event.target.value } : row)))}
              />
              <button
                type="button"
                onClick={() => setMailAttachments(prev => prev.filter((_, i) => i !== index))}
              >
                删
              </button>
            </span>
          ))}
        </div>

        <div className="ops-toolbar">
          <button
            type="button"
            disabled={busy}
            onClick={() => {
              const title = mailTitle.trim()
              const body = mailBody.trim()
              if (!title || !body) {
                setError('请先填写传书标题与正文。')
                return
              }
              const parsedAttachments: Array<{ itemId: string; quantity: number }> = []
              for (const row of mailAttachments) {
                const itemId = row.itemId.trim()
                const quantity = Number(row.quantity)
                if (!itemId || !Number.isFinite(quantity) || quantity <= 0) {
                  setError('存在无效附件：请填写合法的物品 ID 与大于 0 的数量。')
                  return
                }
                parsedAttachments.push({ itemId, quantity: Math.floor(quantity) })
              }
              const expireNum = Number(mailExpireDays)
              const expireDays = !mailNeverExpire && Number.isFinite(expireNum) && expireNum > 0 ? Math.floor(expireNum) : null
              const senderName = mailSender.trim() || null

              if (mailMode === 'personal') {
                const pId = targetPlayerId.trim() || null
                const dName = targetDisplayName.trim() || null
                if (!pId && !dName) {
                  setError('个人传书必须填写目标玩家 UUID 或修士道号。')
                  return
                }
                setPending({
                  title: '确认发送个人传书',
                  description: `即将向玩家 ${pId ?? dName} 发送「${title}」（${mailNeverExpire ? '永不过期，' : ''}附带 ${parsedAttachments.length} 种物品附件）。`,
                  danger: false,
                  run: () => opsPost('/actions/mail-send-player', {
                    targetPlayerId: pId,
                    targetDisplayName: dName,
                    title,
                    body,
                    senderName,
                    attachments: parsedAttachments,
                    expireDays,
                    neverExpire: mailNeverExpire,
                  }),
                })
                return
              }

              setPending({
                title: '确认发布全服群发传书',
                description: `即将向全服发布公告「${title}」（范围：${onlyExistingPlayers ? '仅当前已注册存量玩家' : '全员含未来新号'}，${mailNeverExpire ? '永不过期，' : ''}附带 ${parsedAttachments.length} 种附件）。`,
                danger: true,
                run: async () => {
                  const res = await opsPost('/actions/mail-send-broadcast', {
                    title,
                    body,
                    senderName,
                    attachments: parsedAttachments,
                    onlyExistingPlayers,
                    expireDays,
                    neverExpire: mailNeverExpire,
                  })
                  await loadBroadcasts()
                  return res
                },
              })
            }}
          >
            {mailMode === 'personal' ? '发送个人传书' : '发布全服群发'}
          </button>
        </div>
      </OpsCard>

      <OpsCard
        title="全服群发公告管理"
        extra={(
          <button type="button" disabled={loadingBroadcasts} onClick={() => void loadBroadcasts()}>
            {loadingBroadcasts ? '刷新中…' : '刷新列表'}
          </button>
        )}
      >
        {broadcastError && <p className="ops-error-text">加载失败：{broadcastError}</p>}
        {broadcasts.length === 0 ? (
          <p className="ops-empty">暂无群发公告记录。</p>
        ) : (
          <div className="ops-table-wrap">
            <table className="ops-table">
              <thead>
                <tr>
                  <th>ID</th>
                  <th>标题</th>
                  <th>发件人</th>
                  <th>范围</th>
                  <th className="ops-numeric">附件种数</th>
                  <th>创建时间</th>
                  <th>过期时间</th>
                  <th>状态</th>
                  <th>操作</th>
                </tr>
              </thead>
              <tbody>
                {broadcasts.map(item => (
                  <tr key={item.broadcastId}>
                    <td className="ops-numeric">{item.broadcastId}</td>
                    <td>{item.title}</td>
                    <td>{item.senderName}</td>
                    <td>{item.onlyExistingPlayers ? '仅存量老号' : '全服含新号'}</td>
                    <td className="ops-numeric">{item.attachmentCount}</td>
                    <td>{new Date(item.createdAt).toLocaleString()}</td>
                    <td>{item.expiresAt ? new Date(item.expiresAt).toLocaleString() : '永不过期'}</td>
                    <td>
                      <OpsPill tone={item.isActive ? 'jade' : item.revokedAt ? 'danger' : 'muted'}>
                        {item.isActive ? '生效中' : item.revokedAt ? '已撤销' : '已过期'}
                      </OpsPill>
                    </td>
                    <td>
                      <button
                        type="button"
                        disabled={busy || !item.isActive}
                        onClick={() => setPending({
                          title: '撤销全服公告',
                          description: `将立即撤销全服公告 #${item.broadcastId}「${item.title}」，尚未领取玩家将立即失去入口（已领资产不回溯）。`,
                          danger: true,
                          run: async () => {
                            const res = await opsPost('/actions/mail-revoke-broadcast', { broadcastId: item.broadcastId })
                            await loadBroadcasts()
                            return res
                          },
                        })}
                      >
                        撤销
                      </button>
                    </td>
                  </tr>
                ))}
              </tbody>
            </table>
          </div>
        )}
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
