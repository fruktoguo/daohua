import { useId, useState } from 'react'
import { Modal } from '../../components/Modal'
import { opsGet } from '../opsClient'
import type { OpsPlayerDetail, OpsPlayerSummary, OpsSession, OpsStorage } from '../opsTypes'
import {
  OpsAsyncView,
  OpsCard,
  OpsKeyValues,
  OpsPill,
  OpsStat,
  OpsStatGrid,
  OpsTable,
  formatBytes,
  formatDuration,
  formatMillis,
  formatNumber,
  formatTime,
  presenceLabel,
  useOpsData,
} from '../opsUi'

/** 玩家视图：已加载玩家列表与单玩家详情弹层。 */
export function OpsPlayersView({ intervalMs }: { intervalMs: number }): React.JSX.Element {
  const [selected, setSelected] = useState<string | null>(null)
  const { data, error, loading, reload } = useOpsData<OpsPlayerSummary[]>(signal => opsGet('/players', signal), [], intervalMs)

  return (
    <>
      <OpsAsyncView loading={loading} error={error} hasData={data !== null} onRetry={reload}>
        <OpsCard title={`已加载玩家（${formatNumber(data?.length ?? 0)}）`}>
          <OpsTable
            rows={data ?? []}
            rowKey={player => player.playerId}
            empty="当前没有已加载玩家。"
            columns={[
              {
                header: '序号',
                numeric: true,
                cell: player => (player.sequenceId ? `#${player.sequenceId}` : '-'),
              },
              { header: '道号', cell: player => player.displayName },
              { header: '登录账号', cell: player => player.account || '-' },
              {
                header: '在线状态',
                cell: player => <OpsPill tone={player.presence === 'online' ? 'jade' : 'muted'}>{presenceLabel(player.presence)}</OpsPill>,
              },
              { header: '状态版本', numeric: true, cell: player => formatNumber(player.stateVersion) },
              { header: '上次模拟', cell: player => formatTime(player.lastSimulatedAt) },
              { header: '队列动作', numeric: true, cell: player => formatNumber(player.actionCount) },
              { header: '资产条目', numeric: true, cell: player => formatNumber(player.assetCount) },
              {
                header: '玩家 ID',
                cell: player => <span title={player.playerId} style={{ fontFamily: 'monospace' }}>{player.playerId.slice(0, 8)}…</span>,
              },
              { header: '操作', cell: player => <button type="button" onClick={() => setSelected(player.playerId)}>详情</button> },
            ]}
          />
        </OpsCard>
      </OpsAsyncView>
      {selected && <OpsPlayerDetailModal playerId={selected} onClose={() => setSelected(null)} />}
    </>
  )
}

/** 单玩家详情弹层：进度、战斗、世界、队列、资产与最近日志。 */
function OpsPlayerDetailModal({ playerId, onClose }: { playerId: string; onClose: () => void }): React.JSX.Element {
  const titleId = useId()
  const { data, error, loading, reload } = useOpsData<OpsPlayerDetail>(
    signal => opsGet(`/players/${playerId}`, signal),
    [playerId],
  )

  return (
    <Modal open labelledBy={titleId} onClose={onClose} size="large" closeLabel="关闭">
      <div className="ops-modal">
        <h2 id={titleId}>玩家详情</h2>
        <OpsAsyncView loading={loading} error={error} hasData={data !== null} onRetry={reload}>
          {data && (
            <>
              <OpsKeyValues
                items={[
                  ['角色编号', data.sequenceId ? `#${data.sequenceId}` : '-'],
                  ['道号', data.displayName],
                  ['登录账号', data.account || '-'],
                  ['在线状态', presenceLabel(data.presence)],
                  ['玩家 ID', data.playerId],
                  ['账号 ID', data.accountId],
                  ['状态版本', formatNumber(data.stateVersion)],
                  ['创建时刻', formatTime(data.createdAt)],
                  ['上次模拟', formatTime(data.lastSimulatedAt)],
                  ['配表指纹', data.gameDataFingerprint],
                  ['已触达分段', data.touchedSections?.join('、') || '-'],
                ]}
              />
              <OpsStatGrid>
                <OpsStat label="境界等级" value={formatNumber(data.progression.realmLevel)} tone="gold" />
                <OpsStat label="修为" value={formatNumber(data.progression.experience, 2)} />
                <OpsStat label="完成行动" value={formatNumber(data.progression.completedActions)} />
                <OpsStat label="修炼时长" value={formatDuration(data.progression.cultivationSeconds)} />
                <OpsStat label="普通/精英/首领击杀" value={`${formatNumber(data.progression.normalKills)}/${formatNumber(data.progression.eliteKills)}/${formatNumber(data.progression.bossKills)}`} />
                <OpsStat label="累计败北" value={formatNumber(data.progression.totalDefeats)} tone={data.progression.totalDefeats > 0 ? 'warn' : 'muted'} />
                <OpsStat label="气血" value={`${formatNumber(data.combat.hitpoints, 1)} / ${formatNumber(data.combat.maxHitpoints, 1)}`} />
                <OpsStat label="灵力" value={`${formatNumber(data.combat.manapoints, 1)} / ${formatNumber(data.combat.maxManapoints, 1)}`} />
                <OpsStat label="攻击/防御" value={`${formatNumber(data.combat.attackPower, 1)} / ${formatNumber(data.combat.defense, 1)}`} />
                <OpsStat label="所在节点" value={data.world.nodeId} />
                <OpsStat label="坐标" value={`${formatNumber(data.world.x, 1)}, ${formatNumber(data.world.y, 1)}`} />
                <OpsStat label="行旅边" value={data.world.edgeId ? `${data.world.edgeId}（${formatNumber(data.world.edgeProgress * 100, 1)}%）` : '-'} />
                <OpsStat label="Buff / 功法 / 装备" value={`${formatNumber(data.counts.buffs)} / ${formatNumber(data.counts.learnedAbilities)} / ${formatNumber(data.counts.equippedInstances)}`} />
              </OpsStatGrid>
              <OpsCard title={`行动队列（${formatNumber(data.actions.length)}）`}>
                <OpsTable
                  rows={data.actions}
                  rowKey={action => action.id}
                  empty="行动队列为空。"
                  columns={[
                    { header: '行动', cell: action => action.actionId },
                    { header: '状态', cell: action => action.status },
                    { header: '入队时刻', cell: action => formatTime(action.enqueuedAt) },
                    { header: '进度(秒)', numeric: true, cell: action => formatNumber(action.progressSeconds, 1) },
                    { header: '剩余/完成次数', numeric: true, cell: action => `${formatNumber(action.remainingRepeats)} / ${formatNumber(action.completedRepeats)}` },
                    { header: '目标节点', cell: action => action.targetNodeId ?? '-' },
                    { header: '锚点节点', cell: action => action.anchorNodeId ?? '-' },
                  ]}
                />
              </OpsCard>
              <OpsCard title={`资产（${formatNumber(data.assets.length)}）`}>
                <OpsTable
                  rows={data.assets}
                  rowKey={asset => `${asset.assetId}/${asset.variant}`}
                  empty="没有资产条目。"
                  columns={[
                    { header: '资产 ID', cell: asset => asset.assetId },
                    { header: '变体', cell: asset => asset.variant || '-' },
                    { header: '数量', numeric: true, cell: asset => formatNumber(asset.quantity, 2) },
                    { header: '版本', numeric: true, cell: asset => formatNumber(asset.version) },
                  ]}
                />
              </OpsCard>
              <OpsCard title={`最近日志（${formatNumber(data.recentLogs.length)}）`}>
                <OpsTable
                  rows={data.recentLogs}
                  rowKey={log => String(log.sequence)}
                  empty="没有最近日志。"
                  columns={[
                    { header: '时刻', cell: log => formatMillis(log.timestampUnixMilliseconds) },
                    { header: '频道', cell: log => log.channel },
                    { header: '模板', cell: log => log.template },
                    { header: '计数', numeric: true, cell: log => formatNumber(log.count) },
                  ]}
                />
              </OpsCard>
            </>
          )}
        </OpsAsyncView>
      </div>
    </Modal>
  )
}

/** 会话视图：在线会话与设备指纹、关闭流程标记。 */
export function OpsSessionsView({ intervalMs }: { intervalMs: number }): React.JSX.Element {
  const { data, error, loading, reload } = useOpsData<OpsSession[]>(signal => opsGet('/sessions', signal), [], intervalMs)
  return (
    <OpsAsyncView loading={loading} error={error} hasData={data !== null} onRetry={reload}>
      <OpsCard title={`在线会话（${formatNumber(data?.length ?? 0)}）`}>
        <OpsTable
          rows={data ?? []}
          rowKey={session => session.playerId}
          empty="当前没有在线会话。"
          columns={[
            { header: '玩家 ID', cell: session => session.playerId },
            { header: '设备指纹', cell: session => session.deviceFingerprint ?? '-' },
            { header: '关闭流程', cell: session => (session.closing ? <OpsPill tone="warn">关闭中</OpsPill> : <OpsPill tone="jade">正常</OpsPill>) },
          ]}
        />
      </OpsCard>
    </OpsAsyncView>
  )
}

/** 存储视图：实体计数、投递与落盘积压、存档规模、表体量。 */
export function OpsStorageView({ intervalMs }: { intervalMs: number }): React.JSX.Element {
  const { data, error, loading, reload } = useOpsData<OpsStorage>(signal => opsGet('/storage', signal), [], intervalMs)
  return (
    <OpsAsyncView loading={loading} error={error} hasData={data !== null} onRetry={reload}>
      {data && (
        <>
          <OpsStatGrid>
            <OpsStat label="账号数" value={formatNumber(data.accountCount)} />
            <OpsStat label="角色数" value={formatNumber(data.playerCount)} />
            <OpsStat label="状态头" value={formatNumber(data.stateHeadCount)} />
            <OpsStat label="活跃租约" value={formatNumber(data.activeLeaseCount)} tone={data.activeLeaseCount > 0 ? 'jade' : 'muted'} />
            <OpsStat label="Outbox 待投递" value={formatNumber(data.outboxPending)} tone={data.outboxPending > 0 ? 'warn' : 'muted'} />
            <OpsStat label="Outbox 已租约" value={formatNumber(data.outboxLeased)} />
            <OpsStat label="Outbox 已投递" value={formatNumber(data.outboxDelivered)} />
            <OpsStat label="Outbox 重试中" value={formatNumber(data.outboxRetrying)} tone={data.outboxRetrying > 0 ? 'warn' : 'muted'} />
            <OpsStat label="最老待投递" value={formatDuration(data.outboxOldestPendingSeconds)} />
            <OpsStat label="落盘队列深度" value={formatNumber(data.flushQueueDepth)} tone={data.flushQueueDepth > 0 ? 'warn' : 'muted'} />
            <OpsStat label="落盘重试中" value={formatNumber(data.flushQueueRetrying)} tone={data.flushQueueRetrying > 0 ? 'warn' : 'muted'} />
            <OpsStat label="最早到期等待" value={formatDuration(data.flushQueueOldestDueSeconds)} />
            <OpsStat label="存档分段行数" value={formatNumber(data.sectionRowCount)} />
            <OpsStat label="存档原始字节" value={formatBytes(data.sectionRawBytes)} />
            <OpsStat label="存档落盘字节" value={formatBytes(data.sectionStoredBytes)} />
          </OpsStatGrid>
          <OpsCard title="存档分段 Schema 版本分布">
            <OpsTable
              rows={data.sectionSchemaVersions}
              rowKey={bucket => String(bucket.schemaVersion)}
              empty="没有存档分段。"
              columns={[
                { header: 'Schema 版本', numeric: true, cell: bucket => formatNumber(bucket.schemaVersion) },
                { header: '分段行数', numeric: true, cell: bucket => formatNumber(bucket.sectionCount) },
              ]}
            />
          </OpsCard>
          <OpsCard title="核心表体量">
            <OpsTable
              rows={data.tableSizes}
              rowKey={table => table.table}
              empty="没有可统计的表。"
              columns={[
                { header: '表', cell: table => table.table },
                { header: '总占用', numeric: true, cell: table => formatBytes(table.totalBytes) },
              ]}
            />
          </OpsCard>
        </>
      )}
    </OpsAsyncView>
  )
}
