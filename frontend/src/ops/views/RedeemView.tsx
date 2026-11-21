import { useCallback, useEffect, useRef, useState } from 'react'
import { ConfirmDialog } from '../../components/ConfirmDialog'
import { copyTextToClipboard } from '../../utils/clipboard'
import { opsGet, opsPost } from '../opsClient'
import { OpsCard, OpsPill, formatNumber } from '../opsUi'

interface RewardDraft {
  itemId: string
  quantity: string
  enhancementLevel: string
}

interface OpsRedeemReward {
  itemId: string
  quantity: number
  enhancementLevel?: number
}

interface OpsRedeemBatchItem {
  batchId: number
  title: string
  mode: 'universal' | 'single_use'
  universalCode: string | null
  rewards: OpsRedeemReward[]
  maxClaims: number | null
  claimedCount: number
  createdAt: string
  expiresAt: string | null
  disabledAt: string | null
  isActive: boolean
}

interface OpsRedeemCodeRow {
  code: string
  redeemedBy: string | null
  redeemedByName: string | null
  redeemedAt: string | null
}

interface OpsCreateBatchResult {
  batch: OpsRedeemBatchItem
  codes: string[]
}

interface PendingConfirm {
  title: string
  description: string
  danger: boolean
  run: () => Promise<void>
}

function downloadCodesTextFile(filename: string, codes: string[]): void {
  const blob = new Blob([codes.join('\n') + '\n'], { type: 'text/plain;charset=utf-8' })
  const url = URL.createObjectURL(blob)
  const anchor = document.createElement('a')
  anchor.href = url
  anchor.download = filename
  document.body.appendChild(anchor)
  anchor.click()
  document.body.removeChild(anchor)
  window.setTimeout(() => URL.revokeObjectURL(url), 1000)
}

function formatRewardsSummary(rewards: OpsRedeemReward[]): string {
  return rewards
    .map(r => `${r.itemId}${(r.enhancementLevel ?? 0) > 0 ? `+${r.enhancementLevel}` : ''} ×${formatNumber(r.quantity)}`)
    .join('，')
}

/**
 * 运维后台兑换码管理视图（通用口令码 / 批次单次码创建、码表导出、核销进度监控与停用恢复）。
 */
export function OpsRedeemView(): React.JSX.Element {
  const [mode, setMode] = useState<'universal' | 'single_use'>('universal')
  const [title, setTitle] = useState('')
  const [universalCode, setUniversalCode] = useState('')
  const [maxClaims, setMaxClaims] = useState('')
  const [singleUseCount, setSingleUseCount] = useState('50')
  const [prefix, setPrefix] = useState('')
  const [expireDays, setExpireDays] = useState('30')
  const [neverExpire, setNeverExpire] = useState(false)
  const [rewards, setRewards] = useState<RewardDraft[]>([
    { itemId: 'item.gongde-bound', quantity: '100', enhancementLevel: '0' },
  ])

  const [batches, setBatches] = useState<OpsRedeemBatchItem[]>([])
  const [loadingBatches, setLoadingBatches] = useState(false)
  const [busy, setBusy] = useState(false)
  const [error, setError] = useState<string | null>(null)
  const [notice, setNotice] = useState<string | null>(null)
  const [pending, setPending] = useState<PendingConfirm | null>(null)

  const [latestCreated, setLatestCreated] = useState<OpsCreateBatchResult | null>(null)

  const [selectedBatch, setSelectedBatch] = useState<OpsRedeemBatchItem | null>(null)
  const [codeFilter, setCodeFilter] = useState<'all' | 'unused' | 'used'>('all')
  const [batchCodes, setBatchCodes] = useState<OpsRedeemCodeRow[]>([])
  const [loadingCodes, setLoadingCodes] = useState(false)
  const batchesRequestRef = useRef<{ seq: number }>({ seq: 0 })
  const codesRequestRef = useRef<{ seq: number; controller: AbortController | null }>({
    seq: 0,
    controller: null,
  })

  const loadBatches = useCallback(async (signal?: AbortSignal) => {
    const seq = ++batchesRequestRef.current.seq
    setLoadingBatches(true)
    try {
      const data = await opsGet<{ items: OpsRedeemBatchItem[] }>('/redeem/batches', signal)
      if (signal?.aborted || seq !== batchesRequestRef.current.seq) return
      setBatches(data.items)
    } catch (cause) {
      if (signal?.aborted || seq !== batchesRequestRef.current.seq) return
      setError(cause instanceof Error ? cause.message : String(cause))
    } finally {
      if (!signal?.aborted && seq === batchesRequestRef.current.seq) setLoadingBatches(false)
    }
  }, [])

  const loadBatchCodes = useCallback(async (batch: OpsRedeemBatchItem, filter: 'all' | 'unused' | 'used') => {
    codesRequestRef.current.controller?.abort()
    const controller = new AbortController()
    const seq = ++codesRequestRef.current.seq
    codesRequestRef.current.controller = controller
    setLoadingCodes(true)
    setError(null)
    try {
      const query = new URLSearchParams({ status: filter, limit: '5000' })
      const data = await opsGet<{ batchId: number; items: OpsRedeemCodeRow[] }>(
        `/redeem/batches/${batch.batchId}/codes?${query.toString()}`,
        controller.signal,
      )
      if (seq !== codesRequestRef.current.seq) return
      setBatchCodes(data.items)
    } catch (cause) {
      if (controller.signal.aborted || seq !== codesRequestRef.current.seq) return
      setError(cause instanceof Error ? cause.message : String(cause))
    } finally {
      if (seq === codesRequestRef.current.seq) {
        setLoadingCodes(false)
        codesRequestRef.current.controller = null
      }
    }
  }, [])

  useEffect(() => {
    const controller = new AbortController()
    void loadBatches(controller.signal)
    return () => {
      controller.abort()
      codesRequestRef.current.controller?.abort()
      codesRequestRef.current.seq += 1
    }
  }, [loadBatches])

  const execute = async (fn: () => Promise<void>): Promise<void> => {
    setBusy(true)
    setError(null)
    setNotice(null)
    try {
      await fn()
    } catch (cause) {
      setError(cause instanceof Error ? cause.message : String(cause))
    } finally {
      setBusy(false)
    }
  }

  const handlePrepareCreate = (): void => {
    setError(null)
    setNotice(null)
    const trimmedTitle = title.trim()
    if (!trimmedTitle) {
      setError('请填写礼包/批次名称。')
      return
    }
    if (rewards.length === 0) {
      setError('请至少添加 1 项奖励物品。')
      return
    }

    const parsedRewards: OpsRedeemReward[] = []
    for (const row of rewards) {
      const itemId = row.itemId.trim()
      const quantity = Number(row.quantity)
      const enhancementLevel = Number(row.enhancementLevel || '0')
      if (!itemId || !Number.isFinite(quantity) || quantity < 1 || Math.floor(quantity) !== quantity) {
        setError('奖励配置无效：请填写合法的物品 ID 与正整数数量。')
        return
      }
      if (!Number.isFinite(enhancementLevel) || enhancementLevel < 0 || enhancementLevel > 20 || Math.floor(enhancementLevel) !== enhancementLevel) {
        setError('装备强化等级须为 0~20 的整数。')
        return
      }
      parsedRewards.push({ itemId, quantity, enhancementLevel })
    }

    const parsedExpireDays = !neverExpire ? Number(expireDays) : null
    if (!neverExpire && (parsedExpireDays === null || !Number.isFinite(parsedExpireDays) || parsedExpireDays < 1 || parsedExpireDays > 365)) {
      setError('有效天数必须在 1~365 天之间。')
      return
    }

    if (mode === 'universal') {
      const code = universalCode.trim().toUpperCase()
      if (code.length < 4 || code.length > 32) {
        setError('通用口令长度须为 4~32 位字母、数字、连字符或下划线。')
        return
      }
      const trimmedMax = maxClaims.trim()
      let parsedMaxClaims: number | null = null
      if (trimmedMax.length > 0) {
        const num = Number(trimmedMax)
        if (!Number.isFinite(num) || num < 1 || Math.floor(num) !== num) {
          setError('全服限量次数须为正整数（留空表示不限总人数）。')
          return
        }
        parsedMaxClaims = num
      }

      setPending({
        title: '确认创建通用口令码',
        description: `即将创建口令「${code}」（礼包名：${trimmedTitle}，全服限量：${parsedMaxClaims ?? '不限'}，有效期：${neverExpire ? '永不过期' : `${Math.floor(parsedExpireDays!)} 天`}，奖励：${formatRewardsSummary(parsedRewards)}）。`,
        danger: false,
        run: async () => {
          const created = await opsPost<OpsCreateBatchResult>('/actions/redeem-create-universal', {
            code,
            title: trimmedTitle,
            rewards: parsedRewards,
            maxClaims: parsedMaxClaims,
            expireDays: parsedExpireDays !== null ? Math.floor(parsedExpireDays) : null,
            neverExpire,
          })
          setLatestCreated(created)
          setUniversalCode('')
          setNotice(`已创建通用口令码「${created.batch.universalCode}」（批次 #${created.batch.batchId}）。`)
          await loadBatches()
        },
      })
      return
    }

    const count = Number(singleUseCount)
    if (!Number.isFinite(count) || count < 1 || count > 5000 || Math.floor(count) !== count) {
      setError('单批生成数量须为 1~5000 的正整数。')
      return
    }
    const cleanPrefix = prefix.trim().toUpperCase()
    if (cleanPrefix.length > 8) {
      setError('可选前缀不能超过 8 个字符。')
      return
    }

    setPending({
      title: '确认批量生成单次兑换码',
      description: `即将生成 ${count} 枚一码一用兑换码（礼包名：${trimmedTitle}${cleanPrefix ? `，前缀：${cleanPrefix}` : ''}，同批次每人限兑 1 次，有效期：${neverExpire ? '永不过期' : `${Math.floor(parsedExpireDays!)} 天`}，奖励：${formatRewardsSummary(parsedRewards)}）。`,
      danger: false,
      run: async () => {
        const created = await opsPost<OpsCreateBatchResult>('/actions/redeem-create-single-use', {
          count,
          prefix: cleanPrefix || null,
          title: trimmedTitle,
          rewards: parsedRewards,
          expireDays: parsedExpireDays !== null ? Math.floor(parsedExpireDays) : null,
          neverExpire,
        })
        setLatestCreated(created)
        setNotice(`已成功生成 ${created.codes.length} 枚单次兑换码（批次 #${created.batch.batchId}）。`)
        await loadBatches()
      },
    })
  }

  return (
    <>
      <OpsCard title="创建兑换码工坊（通用口令码 / 批次单次码）">
        <p className="ops-note">
          玩家在「设置 → 账号 → 礼包兑换」输入口令后直接入账背包并实时刷新；同批次每名玩家永久限领 1 次，机器人账号禁止兑换。
        </p>

        <div className="ops-toolbar">
          <label>
            兑换码类型
            <select value={mode} onChange={e => setMode(e.target.value as 'universal' | 'single_use')}>
              <option value="universal">通用口令码（全服同口令）</option>
              <option value="single_use">批次单次码（一码一用）</option>
            </select>
          </label>
          <label>
            礼包名称
            <input
              value={title}
              placeholder="例如：道化初开庆典礼"
              onChange={e => setTitle(e.target.value)}
            />
          </label>
          {mode === 'universal' ? (
            <>
              <label>
                固定口令（4~32位）
                <input
                  value={universalCode}
                  placeholder="例如：DAOHUA666"
                  onChange={e => setUniversalCode(e.target.value)}
                />
              </label>
              <label>
                全服总限量（留空不限）
                <input
                  type="number"
                  min={1}
                  style={{ width: 120 }}
                  value={maxClaims}
                  placeholder="不限人数"
                  onChange={e => setMaxClaims(e.target.value)}
                />
              </label>
            </>
          ) : (
            <>
              <label>
                生成数量（1~5000）
                <input
                  type="number"
                  min={1}
                  max={5000}
                  style={{ width: 110 }}
                  value={singleUseCount}
                  onChange={e => setSingleUseCount(e.target.value)}
                />
              </label>
              <label>
                可选前缀（≤8位）
                <input
                  style={{ width: 120 }}
                  value={prefix}
                  placeholder="例如：TAP-"
                  onChange={e => setPrefix(e.target.value)}
                />
              </label>
            </>
          )}
          <label>
            有效天数
            <input
              type="number"
              min={1}
              max={365}
              style={{ width: 90 }}
              disabled={neverExpire}
              value={expireDays}
              onChange={e => setExpireDays(e.target.value)}
            />
          </label>
          <label>
            <input
              type="checkbox"
              checked={neverExpire}
              onChange={e => setNeverExpire(e.target.checked)}
            />
            永不过期
          </label>
        </div>

        <div className="ops-toolbar">
          <button
            type="button"
            disabled={busy || rewards.length >= 12}
            onClick={() => setRewards(prev => [...prev, { itemId: 'item.gongde-bound', quantity: '100', enhancementLevel: '0' }])}
          >
            + 绑定功德
          </button>
          <button
            type="button"
            disabled={busy || rewards.length >= 12}
            onClick={() => setRewards(prev => [...prev, { itemId: 'currency.spirit-stone', quantity: '1000', enhancementLevel: '0' }])}
          >
            + 灵石
          </button>
          <button
            type="button"
            disabled={busy || rewards.length >= 12}
            onClick={() => setRewards(prev => [...prev, { itemId: '', quantity: '1', enhancementLevel: '0' }])}
          >
            + 自定义物品/装备
          </button>
        </div>

        <div className="ops-toolbar" style={{ flexDirection: 'column', alignItems: 'flex-start', gap: 8 }}>
          {rewards.map((row, idx) => (
            <div key={idx} style={{ display: 'flex', flexWrap: 'wrap', alignItems: 'center', gap: 8 }}>
              <input
                style={{ width: 240 }}
                placeholder="itemId（如 item.storage-bag.damaged）"
                value={row.itemId}
                onChange={e => setRewards(prev => prev.map((r, i) => (i === idx ? { ...r, itemId: e.target.value } : r)))}
              />
              <label style={{ display: 'inline-flex', alignItems: 'center', gap: 4 }}>
                数量
                <input
                  type="number"
                  min={1}
                  style={{ width: 100 }}
                  value={row.quantity}
                  onChange={e => setRewards(prev => prev.map((r, i) => (i === idx ? { ...r, quantity: e.target.value } : r)))}
                />
              </label>
              <label style={{ display: 'inline-flex', alignItems: 'center', gap: 4 }}>
                强化+
                <input
                  type="number"
                  min={0}
                  max={20}
                  style={{ width: 68 }}
                  value={row.enhancementLevel}
                  title="仅装备有效（0~20），普通物品保持 0"
                  onChange={e => setRewards(prev => prev.map((r, i) => (i === idx ? { ...r, enhancementLevel: e.target.value } : r)))}
                />
              </label>
              <button
                type="button"
                disabled={rewards.length <= 1}
                onClick={() => setRewards(prev => prev.filter((_, i) => i !== idx))}
              >
                移除
              </button>
            </div>
          ))}
        </div>

        <div className="ops-toolbar">
          <button type="button" disabled={busy} onClick={handlePrepareCreate}>
            {mode === 'universal' ? '创建通用口令码' : '批量生成单次码'}
          </button>
          {busy && <OpsPill tone="warn">执行中…</OpsPill>}
        </div>

        {error && <p className="ops-error-text">{error}</p>}
        {notice && <p style={{ color: '#6fc9a8', margin: '8px 0 0' }}>{notice}</p>}
      </OpsCard>

      {latestCreated && (
        <OpsCard
          title={`最新创建结果 · 批次 #${latestCreated.batch.batchId}「${latestCreated.batch.title}」（共 ${latestCreated.codes.length} 枚）`}
          extra={(
            <div style={{ display: 'inline-flex', gap: 8 }}>
              <button
                type="button"
                onClick={async () => {
                  const ok = await copyTextToClipboard(latestCreated.codes.join('\n'))
                  setNotice(ok ? `已复制全部 ${latestCreated.codes.length} 枚兑换码到剪贴板。` : '复制失败，请手动全选文本框复制。')
                }}
              >
                一键复制全部
              </button>
              <button
                type="button"
                onClick={() => downloadCodesTextFile(`redeem-batch-${latestCreated.batch.batchId}.txt`, latestCreated.codes)}
              >
                下载 .txt
              </button>
              <button type="button" onClick={() => setLatestCreated(null)}>收起</button>
            </div>
          )}
        >
          <textarea
            readOnly
            rows={Math.min(10, Math.max(2, latestCreated.codes.length))}
            style={{ width: '100%', fontFamily: 'monospace' }}
            value={latestCreated.codes.join('\n')}
          />
        </OpsCard>
      )}

      <OpsCard
        title="兑换码批次列表与核销进度"
        extra={(
          <button type="button" disabled={loadingBatches} onClick={() => void loadBatches()}>
            {loadingBatches ? '刷新中…' : '刷新列表'}
          </button>
        )}
      >
        {batches.length === 0 ? (
          <p className="ops-empty">暂无兑换码批次记录。</p>
        ) : (
          <div className="ops-table-wrap">
            <table className="ops-table">
              <thead>
                <tr>
                  <th>批次ID</th>
                  <th>礼包名称</th>
                  <th>模式 / 口令</th>
                  <th className="ops-numeric">已兑 / 上限</th>
                  <th>奖励内容</th>
                  <th>创建时间</th>
                  <th>过期时间</th>
                  <th>状态</th>
                  <th>操作</th>
                </tr>
              </thead>
              <tbody>
                {batches.map(item => {
                  const isExhausted = item.maxClaims !== null && item.claimedCount >= item.maxClaims
                  const statusLabel = item.disabledAt
                    ? '已停用'
                    : !item.isActive
                      ? isExhausted
                        ? '已领完'
                        : '已过期'
                      : '生效中'
                  const statusTone = item.disabledAt ? 'danger' : item.isActive ? 'jade' : 'muted'

                  return (
                    <tr key={item.batchId}>
                      <td className="ops-numeric">{item.batchId}</td>
                      <td>{item.title}</td>
                      <td>
                        {item.mode === 'universal' ? (
                          <span>通用：<code>{item.universalCode}</code></span>
                        ) : (
                          <span>单次码池</span>
                        )}
                      </td>
                      <td className="ops-numeric">
                        {formatNumber(item.claimedCount)} / {item.maxClaims !== null ? formatNumber(item.maxClaims) : '∞'}
                      </td>
                      <td>{formatRewardsSummary(item.rewards)}</td>
                      <td>{new Date(item.createdAt).toLocaleString()}</td>
                      <td>{item.expiresAt ? new Date(item.expiresAt).toLocaleString() : '永不过期'}</td>
                      <td>
                        <OpsPill tone={statusTone}>{statusLabel}</OpsPill>
                      </td>
                      <td style={{ display: 'flex', gap: 6, flexWrap: 'wrap' }}>
                        <button
                          type="button"
                          onClick={() => {
                            const defaultFilter = item.mode === 'universal' ? 'used' : 'unused'
                            setSelectedBatch(item)
                            setCodeFilter(defaultFilter)
                            void loadBatchCodes(item, defaultFilter)
                          }}
                        >
                          {item.mode === 'universal' ? '领取记录' : '查看/导出码'}
                        </button>
                        {item.disabledAt ? (
                          <button
                            type="button"
                            disabled={busy}
                            onClick={() => setPending({
                              title: '恢复兑换码批次',
                              description: `将恢复批次 #${item.batchId}「${item.title}」的可用状态（若未过期且未满额则可继续兑换）。`,
                              danger: false,
                              run: async () => {
                                await opsPost('/actions/redeem-set-disabled', { batchId: item.batchId, disabled: false })
                                setNotice(`已恢复批次 #${item.batchId}。`)
                                await loadBatches()
                              },
                            })}
                          >
                            恢复
                          </button>
                        ) : (
                          <button
                            type="button"
                            disabled={busy}
                            onClick={() => setPending({
                              title: '停用兑换码批次',
                              description: `将立即停用批次 #${item.batchId}「${item.title}」，未兑换的口令将无法继续使用（已入账资产不回退）。`,
                              danger: true,
                              run: async () => {
                                await opsPost('/actions/redeem-set-disabled', { batchId: item.batchId, disabled: true })
                                setNotice(`已停用批次 #${item.batchId}。`)
                                await loadBatches()
                              },
                            })}
                          >
                            停用
                          </button>
                        )}
                      </td>
                    </tr>
                  )
                })}
              </tbody>
            </table>
          </div>
        )}
      </OpsCard>

      {selectedBatch && (
        <OpsCard
          title={`批次 #${selectedBatch.batchId}「${selectedBatch.title}」明细（显示 ${batchCodes.length} 条）`}
          extra={(
            <div style={{ display: 'inline-flex', gap: 8, alignItems: 'center' }}>
              <select
                value={codeFilter}
                onChange={e => {
                  const next = e.target.value as 'all' | 'unused' | 'used'
                  setCodeFilter(next)
                  void loadBatchCodes(selectedBatch, next)
                }}
              >
                <option value="all">全部</option>
                <option value="unused">仅未使用</option>
                <option value="used">仅已核销</option>
              </select>
              {batchCodes.length > 0 && (
                <>
                  <button
                    type="button"
                    onClick={async () => {
                      const text = batchCodes.map(r => r.code).join('\n')
                      const ok = await copyTextToClipboard(text)
                      setNotice(ok ? `已复制当前列表 ${batchCodes.length} 枚兑换码。` : '复制失败。')
                    }}
                  >
                    复制码列
                  </button>
                  <button
                    type="button"
                    onClick={() => downloadCodesTextFile(
                      `redeem-batch-${selectedBatch.batchId}-${codeFilter}.txt`,
                      batchCodes.map(r => r.code),
                    )}
                  >
                    导出 .txt
                  </button>
                </>
              )}
              <button type="button" onClick={() => setSelectedBatch(null)}>关闭</button>
            </div>
          )}
        >
          {loadingCodes ? (
            <p className="ops-empty">加载中…</p>
          ) : batchCodes.length === 0 ? (
            <p className="ops-empty">当前筛选条件下无记录。</p>
          ) : (
            <div className="ops-table-wrap">
              <table className="ops-table">
                <thead>
                  <tr>
                    <th>兑换码</th>
                    <th>状态</th>
                    <th>领取角色</th>
                    <th>角色 UUID</th>
                    <th>核销时间</th>
                  </tr>
                </thead>
                <tbody>
                  {batchCodes.slice(0, 300).map((row, index) => (
                    <tr key={`${row.code}-${row.redeemedBy ?? index}`}>
                      <td><code>{row.code}</code></td>
                      <td>
                        <OpsPill tone={row.redeemedAt ? 'muted' : 'jade'}>
                          {row.redeemedAt ? '已核销' : '可用'}
                        </OpsPill>
                      </td>
                      <td>{row.redeemedByName ?? '-'}</td>
                      <td><code>{row.redeemedBy ?? '-'}</code></td>
                      <td>{row.redeemedAt ? new Date(row.redeemedAt).toLocaleString() : '-'}</td>
                    </tr>
                  ))}
                </tbody>
              </table>
              {batchCodes.length > 300 && (
                <p className="ops-note">表格仅预览前 300 条；点击上方「复制码列」或「导出 .txt」可获取全部 {batchCodes.length} 条兑换码。</p>
              )}
            </div>
          )}
        </OpsCard>
      )}

      <ConfirmDialog
        open={pending !== null}
        title={pending?.title ?? ''}
        description={pending?.description ?? ''}
        danger={pending?.danger ?? false}
        confirmLabel="确认执行"
        cancelLabel="取消"
        onConfirm={() => {
          const action = pending
          setPending(null)
          if (action) void execute(action.run)
        }}
        onClose={() => setPending(null)}
      />
    </>
  )
}
