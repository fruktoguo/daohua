import { useEffect, useState } from 'react'
import { ConfirmDialog } from '../../components/ConfirmDialog'
import { opsGetAccountAbuse, opsUpdateAccountAbusePolicy } from '../opsClient'
import type {
  OpsAccountAbuseOverview,
  OpsAccountAbusePolicy,
} from '../opsTypes'
import {
  OpsAsyncView,
  OpsCard,
  OpsPill,
  OpsStat,
  OpsStatGrid,
  OpsTable,
  formatNumber,
  formatTime,
  useOpsData,
} from '../opsUi'

const SOFT_RISK_MODULES = new Set(['deviceConfigHash', 'ipAddress', 'network', 'browserFingerprint', 'registrationVelocity'])
const QUOTA_MODULES = new Set(['deviceId', 'accountAssociation', 'onlineDeviceQuota'])

/** 账号检测视图：策略开关、风险计数、脱敏关联和近期事件。 */
export function OpsAccountAbuseView({ intervalMs }: { intervalMs: number }): React.JSX.Element {
  const { data, error, loading, reload } = useOpsData<OpsAccountAbuseOverview>(
    signal => opsGetAccountAbuse(signal),
    [],
    intervalMs,
  )
  const [draft, setDraft] = useState<OpsAccountAbusePolicy | null>(null)
  const [dirty, setDirty] = useState(false)
  const [pending, setPending] = useState(false)
  const [busy, setBusy] = useState(false)
  const [saveError, setSaveError] = useState<string | null>(null)
  const [saveResult, setSaveResult] = useState<string | null>(null)

  useEffect(() => {
    if (data && !dirty) setDraft(clonePolicy(data.policy))
  }, [data, dirty])

  const updateDraft = (update: (policy: OpsAccountAbusePolicy) => OpsAccountAbusePolicy): void => {
    setDraft(current => current ? update(current) : current)
    setDirty(true)
    setSaveError(null)
    setSaveResult(null)
  }

  const savePolicy = async (): Promise<void> => {
    if (!draft) return
    setBusy(true)
    setSaveError(null)
    setSaveResult(null)
    try {
      await opsUpdateAccountAbusePolicy(toPolicyPayload(draft))
      setSaveResult('账号检测策略已更新。')
      setPending(false)
      setDirty(false)
      reload()
    } catch (cause) {
      setSaveError(cause instanceof Error ? cause.message : String(cause))
    } finally {
      setBusy(false)
    }
  }

  return (
    <>
      <OpsAsyncView loading={loading} error={error} hasData={data !== null} onRetry={reload}>
        {data && draft && (
          <>
            <OpsCard
              title="账号检测策略"
              extra={<OpsPill tone={draft.enabled ? 'jade' : 'muted'}>{draft.enabled ? '检测已启用' : '检测已停用'}</OpsPill>}
            >
              <div className="ops-toolbar ops-account-policy-toolbar">
                <label>
                  <input
                    type="checkbox"
                    checked={draft.enabled}
                    onChange={event => updateDraft(policy => ({ ...policy, enabled: event.target.checked }))}
                  />
                  启用账号检测
                </label>
                <label>
                  <input
                    type="checkbox"
                    checked={draft.registrationEnabled}
                    onChange={event => updateDraft(policy => ({ ...policy, registrationEnabled: event.target.checked }))}
                  />
                  注册时检测
                </label>
                <label>
                  <input
                    type="checkbox"
                    checked={draft.requireDeviceSignal}
                    onChange={event => updateDraft(policy => ({ ...policy, requireDeviceSignal: event.target.checked }))}
                  />
                  必须提供设备信号
                </label>
                <label>
                  关联账号上限
                  <input
                    className="ops-number-input"
                    type="number"
                    min={1}
                    max={32}
                    value={draft.maxAccountsPerPerson}
                    onChange={event => updateDraft(policy => ({ ...policy, maxAccountsPerPerson: parseBoundedNumber(event.target.value, 1, 32, policy.maxAccountsPerPerson) }))}
                  />
                </label>
                <label>
                  设备注册上限
                  <input
                    className="ops-number-input"
                    type="number"
                    min={1}
                    max={16}
                    value={draft.maxAccountsPerDevice}
                    onChange={event => updateDraft(policy => ({ ...policy, maxAccountsPerDevice: parseBoundedNumber(event.target.value, 1, 16, policy.maxAccountsPerDevice) }))}
                  />
                </label>
                <label>
                  高风险阈值
                  <input
                    className="ops-number-input"
                    type="number"
                    min={0}
                    max={100}
                    value={draft.riskThreshold}
                    onChange={event => updateDraft(policy => ({ ...policy, riskThreshold: parseBoundedNumber(event.target.value, 0, 100, policy.riskThreshold) }))}
                  />
                </label>
                <label>
                  高风险处置
                  <select value={draft.highRiskAction} onChange={event => updateDraft(policy => ({ ...policy, highRiskAction: event.target.value }))}>
                    <option value="allow">放行并记录</option>
                    <option value="review">人工复核</option>
                    <option value="deny">拒绝请求</option>
                  </select>
                </label>
                <button type="button" disabled={busy} onClick={() => setPending(true)}>保存策略</button>
              </div>
              <OpsTable
                rows={draft.modules}
                rowKey={module => module.id}
                empty="暂无检测模块。"
                columns={[
                  { header: '检测模块', cell: module => module.name || module.id },
                  {
                    header: '状态',
                    cell: module => (
                      <label className="ops-inline-check">
                        <input type="checkbox" checked={module.enabled} onChange={event => toggleModule(draft, module.id, event.target.checked, updateDraft)} />
                        {module.enabled ? <OpsPill tone="jade">启用</OpsPill> : <OpsPill tone="muted">停用</OpsPill>}
                      </label>
                    ),
                  },
                  {
                    header: '权重',
                    numeric: true,
                    cell: module => SOFT_RISK_MODULES.has(module.id) ? (
                      <input
                        className="ops-number-input"
                        type="number"
                        min={0}
                        max={100}
                        value={module.weight}
                        onChange={event => updateModule(draft, module.id, 'weight', parseBoundedNumber(event.target.value, 0, 100, module.weight), updateDraft)}
                      />
                    ) : '—',
                  },
                  {
                    header: '命中阈值',
                    numeric: true,
                    cell: module => SOFT_RISK_MODULES.has(module.id) ? (
                      <input
                        className="ops-number-input"
                        type="number"
                        min={1}
                        max={100}
                        value={module.threshold}
                        onChange={event => updateModule(draft, module.id, 'threshold', parseBoundedNumber(event.target.value, 1, 100, module.threshold), updateDraft)}
                      />
                    ) : QUOTA_MODULES.has(module.id) ? '额度限制' : '—',
                  },
                ]}
              />
              {saveError && <p className="ops-error-text">保存失败：{saveError}</p>}
              {saveResult && <p className="ops-note">{saveResult}</p>}
              {draft.updatedAt && <p className="ops-note">上次更新：{formatTime(draft.updatedAt)}</p>}
            </OpsCard>

            <OpsStatGrid>
              <OpsStat label="账号总数" value={formatNumber(data.stats.accountCount)} />
              <OpsStat label="关联主体" value={formatNumber(data.stats.linkedPersonCount)} />
              <OpsStat label="关联设备" value={formatNumber(data.stats.linkedDeviceCount)} />
              <OpsStat label="今日成功注册" value={formatNumber(data.stats.registrationsToday)} />
              <OpsStat label="今日放行判定次数" value={formatNumber(data.stats.allowedToday)} tone="jade" />
              <OpsStat label="今日复核判定次数" value={formatNumber(data.stats.reviewedToday)} tone={data.stats.reviewedToday > 0 ? 'warn' : 'muted'} />
              <OpsStat label="今日拦截判定次数" value={formatNumber(data.stats.blockedToday)} tone={data.stats.blockedToday > 0 ? 'danger' : 'muted'} />
              <OpsStat label="今日高风险判定次数" value={formatNumber(data.stats.highRiskToday)} tone={data.stats.highRiskToday > 0 ? 'warn' : 'muted'} />
            </OpsStatGrid>

            <OpsCard title="近期风险事件">
              <div className="ops-account-event-table">
              <OpsTable
                rows={data.recentEvents}
                rowKey={event => event.eventId}
                empty="近期没有账号检测事件。"
                columns={[
                  { header: '时间', cell: event => formatTime(event.occurredAt) },
                  { header: '类型', cell: event => formatEventKind(event.kind) },
                  { header: '处置', cell: event => <ActionPill action={event.action} /> },
                  { header: '风险分', numeric: true, cell: event => formatNumber(event.riskScore, 1) },
                  { header: '账号', cell: event => event.accountKey ?? '-' },
                  { header: '设备', cell: event => event.deviceKey ?? '-' },
                  { header: '网络', cell: event => event.networkKey ?? '-' },
                  { header: '命中原因', cell: event => event.reasons.join('、') || '-' },
                ]}
              />
              </div>
            </OpsCard>

            <OpsCard title="脱敏关联摘要">
              <div className="ops-account-association-table">
              <OpsTable
                rows={data.associations}
                rowKey={association => association.associationKey}
                empty="当前没有可展示的关联摘要。"
                columns={[
                  { header: '关联键', cell: association => association.associationKey },
                  { header: '账号', cell: association => association.accountKeys.join('、') || '-', numeric: false },
                  { header: '账号数', cell: association => formatNumber(association.accountCount), numeric: true },
                  { header: '设备数', cell: association => formatNumber(association.deviceCount), numeric: true },
                  { header: '网络数', cell: association => formatNumber(association.networkCount), numeric: true },
                  { header: '最高风险', cell: association => formatNumber(association.maxRiskScore, 1), numeric: true },
                  { header: '最近出现', cell: association => formatTime(association.lastSeenAt) },
                ]}
              />
              </div>
            </OpsCard>
          </>
        )}
      </OpsAsyncView>
      <ConfirmDialog
        open={pending}
        title="保存账号检测策略"
        description="保存后立即生效，影响后续注册、登录与连接。"
        confirmLabel="确认保存"
        danger
        onConfirm={() => void savePolicy()}
        onClose={() => setPending(false)}
      />
    </>
  )
}

/** 深拷贝策略，避免编辑控件直接改变服务端快照。 */
function clonePolicy(policy: OpsAccountAbusePolicy): OpsAccountAbusePolicy {
  return { ...policy, modules: policy.modules.map(module => ({ ...module })) }
}

/** 提交策略时只携带服务端需要的可变字段。 */
function toPolicyPayload(policy: OpsAccountAbusePolicy): Omit<OpsAccountAbusePolicy, 'updatedAt'> {
  return {
    enabled: policy.enabled,
    registrationEnabled: policy.registrationEnabled,
    requireDeviceSignal: policy.requireDeviceSignal,
    maxAccountsPerPerson: policy.maxAccountsPerPerson,
    maxAccountsPerDevice: policy.maxAccountsPerDevice,
    riskThreshold: policy.riskThreshold,
    highRiskAction: policy.highRiskAction,
    modules: policy.modules,
  }
}

function parseBoundedNumber(value: string, minimum: number, maximum: number, fallback: number): number {
  const parsed = Number(value)
  if (!Number.isFinite(parsed)) return fallback
  return Math.min(maximum, Math.max(minimum, Math.round(parsed)))
}

function toggleModule(
  policy: OpsAccountAbusePolicy,
  moduleId: string,
  enabled: boolean,
  updateDraft: (update: (policy: OpsAccountAbusePolicy) => OpsAccountAbusePolicy) => void,
): void {
  updateDraft(current => current === policy ? {
    ...current,
    modules: current.modules.map(module => module.id === moduleId ? { ...module, enabled } : module),
  } : current)
}

function updateModule(
  policy: OpsAccountAbusePolicy,
  moduleId: string,
  key: 'weight' | 'threshold',
  value: number,
  updateDraft: (update: (policy: OpsAccountAbusePolicy) => OpsAccountAbusePolicy) => void,
): void {
  updateDraft(current => current === policy ? {
    ...current,
    modules: current.modules.map(module => module.id === moduleId ? { ...module, [key]: value } : module),
  } : current)
}

function ActionPill({ action }: { action: string }): React.JSX.Element {
  const tone = action === 'deny' ? 'danger' : action === 'review' ? 'warn' : action === 'allow' ? 'jade' : 'muted'
  const label = action === 'deny' ? '拦截' : action === 'review' ? '复核' : action === 'allow' ? '放行' : action
  return <OpsPill tone={tone}>{label}</OpsPill>
}

function formatEventKind(kind: string): string {
  if (kind === 'register') return '注册'
  if (kind === 'login') return '登录'
  if (kind === 'handshake') return '连接'
  return kind
}
