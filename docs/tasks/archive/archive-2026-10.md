# 任务归档 · 2026-10（2026-10-02 ~ 2026-10-11）

> 本文件由 2026-10-11 任务面板深度归档（TASK-20261011-07）生成：`docs/tasks/` 下 2026-10-11 及更早的任务详情文档已提炼合并至本档并删除原文件，完整原文可从 Git 历史找回。
> 本档共 92 项任务（含 3 项因同日编号冲突无独立详情文档者，条目已标注）；条目按提出时间从新到旧排列。检索方式：按 `TASK-YYYYMMDD-XX` 编号全文搜索定位。

---

### TASK-20261011-06 「身价」更名「身家」
- 分类：文案治理 / i18n / 设计文档
- 时间：提出 2026-10-11 18:40:00｜完成：无
- 状态：🚀 进行中（归档时；实施已落地）
- 摘要：招募用「装备+功法折灵石估值」由「身价」更名「身家」——修仙网文惯用词，指随身资财之值而非人身价码。协议字段 `valuation`/`minValuation`/`PartyValuationService`/DB 列 `min_valuation` 为设计刻意中性键名（§21.3 不锁中文名）保持不变；全仓中文展示文案/注释/设计文档统一改名：总纲 §21 全篇、§19.8 对照表行（§19.6 记更名历史）、§14.5.2 索引句、前端 i18n `social.*` 词条与注释、服务端失败文案「身价未达到队伍门槛」→「身家」、C#/TS/CSS 注释及任务面板在途描述；`index.html` 与 `template.csv` 经脚本重生成（19 个源文件）。验证：`dotnet build Game.slnx` 0 警 0 错、`tsc --noEmit`、`check-frontend-contracts.mjs` 全过；grep 残留仅限 §19.6 历史行与已完成任务档。

### TASK-20261011-05 玩家三态生命周期落地：在线 / 离线挂机 / 彻底离线
- 分类：架构重构 / 离线补算 / 组队 / 核心主旨
- 时间：提出 2026-10-11 15:40:49｜完成：无
- 状态：🧪 验证完成（verify.sh + capacity-target.sh 全过；归档时待提交裁定）
- 摘要：用户裁定玩家三态为核心主旨（总纲 §1.2 第 5 条）：在线全实时；离线挂机期间队伍有人在线则全员强制实时、全员离线则小时级批次冷算（主服本地优先、节点只承接溢出），未到批算点回线则主服即时完全补算；彻底离线（超上限）退队+隐式暂停+进入时补算「断线→上限边界」全部收益；连续离线 5 分钟才进入离线挂机，48h 封顶改锚断线时刻。落地：`OfflineSinceAt` 贯通 Domain/ECS/分段/投影；`DisconnectGraceRegistry` 宽限到期注销在线位并以断线时刻落盘卸载；`ReconcileResidentMembersAsync` 常驻离线队友、`AdvanceOfflineDueAsync`/`AdvanceOfflineSessionsAsync` 全员离线会话批算；冷扫描改 `offline_since`+批算间隔+终末批（不再受节点配置门控）；`ComputeResultCommitter` 收拢唯一写回事务；`LocalComputeExecutorWorker` 本地串行执行；节点派发按 `ComputeNode:OverflowAfterSeconds` 只领溢出；配置迁至 `OfflineIdle:*`；schema v31 + `migrations/0031`。验证：verify.sh 全绿（Domain 312 / Ecs 104 / Server 299+）、capacity-target 通过；实机确认常驻成员、收件箱逐拍消费至 0。注：本任务登记时曾占用 -04 编号，与「队伍成员角色信息卡」撞号后改号 -05。

### TASK-20261011-04 队伍成员角色信息卡
- 分类：功能补全 / 组队 / 全栈
- 时间：提出 2026-10-11 15:45:07｜完成：无
- 状态：🧪 验证中（角色卡 5 用例+社交 28 用例/前端 build+契约/tsc 全绿；待 UI 目检）
- 摘要：同道页点击队伍成员弹出角色信息卡（道号/立绘/序号 player_seq/境界/身家/六维+经验进度/战斗经验/战斗装备六部位+技艺装备/功法槽+已学功法/丹药栏与通用消耗品栏配置）。协议新增请求-应答对 `PlayerCardRequest=27`/`PlayerCard=28`（沿用 PartyLobby 范式，`PlayerCardDto` 携带 error 字段）；服务端 `ISocialRepository.GetPlayerIdentityAsync` 单查 `identity.players`，`SocialRuntime.BuildPlayerCardAsync` 授权边界为本人或同队（否则 forbidden/not-found），数据源已装载读运行时内存态、未装载读持久化分段真源（与身家估值同口径，不投影未消费会话结算）。验证：角色卡 5 用例、社交套件 28 用例、tsc/契约全过；并行任务（TASK-20261011-05 接口签名变更）合并后重测全绿。

### TASK-20261011-03 特殊属性口径纠偏：玩家取消成长保送、怪物基准保留、杖琴技能增幅
- 分类：数值模型调整 / 领域+配表 / 设计文档
- 时间：提出 2026-10-11 15:31:00｜完成：无
- 状态：🧪 验证中（Domain 312 / Ecs 101 / Server 净库全绿；build 0 警告；DataTool/tsc/契约全过）
- 摘要：用户裁定玩家特殊属性（攻速/行动速度/冷却缩减/普攻增幅/技能增幅/暴伤成长分量等）仅经装备/内功/特质/Buff 等手段获取，不由境界成长保送；「成长保送点数」本质是怪物同构建模基准、只对怪物保留。`PlayerAttributeResolver` 移除 6 项成长注入（保留暴伤基础 50 点=150% 与 `ResistanceScale=Tier`）；法杖与琴系武器新增 `skillBonusPoints`（T1 九节杖/素心琴 +5、T2 朝元杖/松风琴 +15；`frontend/src/game/ids.ts` 兜底镜像同步）；测试 `PlayerAndMonsterShareTheSameGrowthCurve` 改 `MonsterBaselineGrowthCurve_PlayerReceivesNoInjection`（玩家侧断言零保送）。文档先行：`07-战斗数值与内视规约.md` §7.7.5 重写为获取型/怪物基准口径并内嵌各等阶特殊属性期望表，11/08/04 篇、`game-data.md`、`common-components.md`、`expected-attributes.json` 口径同步，index.html 重生成。验证：build 0 警告、Domain 312 / Ecs 101 / Server 275+2（2 败为共享测试库脏数据，净库全绿）、DataTool strict / tsc / 契约全过。

### TASK-20261011-02 队伍目标地点选择改「区域 + 地点」级联双下拉
- 分类：前端体验 / 组队 / 交互
- 时间：提出 2026-10-11 15:25:08｜完成：无
- 状态：🧪 验证中（前端 build+契约全绿；待 UI 目检）
- 摘要：组队表单「地点」单一下拉（全图节点平铺）拆为「区域」「地点」级联双下拉。协议约束 `nodeId` 空串=不限、无区域级不限语义：`regionId` 由 `value.nodeId` 反查所属地图派生（派生态不新增表单字段）；区域=不限→地点固定「不限」；区域=某图→切区域自动落到该区域首节点（避免协议表达不了的「区域任意地点」虚假语义）。`PartyProfileFields` 为匹配/大厅/创建/修改队伍四处共用控件一次生效；`useCurrentNodeField` 未手选跟随当前地点机制零改动；i18n 增 `field.region`/`field.anyRegion`；§21 目标节点行同步级联口径。验证：vite build 与契约检查全绿。

### TASK-20261011-01 队伍信息纠偏：斗法复选框（仅队长）、准备改名、移除改右上角 ×；并落地完整队伍斗法撮合
- 分类：组队共战 / 斗法 PVP / 前端体验 / 架构补齐
- 时间：提出 2026-10-11 09:30:00｜完成：无
- 状态：🚀 进行中（阶段 A 斗法开关全链路+前端纠偏、阶段 B 生产撮合运行时恢复已完成；阶段 C 斗法模型多方化+会话斗法相位待开工）
- 摘要：纠正「斗法/参战就绪」混用语义：斗法=队伍级 PVP 撮合开关（仅讨伐目标显示、仅队长可改、会话冻结），准备=成员个人就绪；并让勾选斗法的队伍真正作为一个匹配单位进撮合池。勘察发现四项缺口：UI 复用 `member.ready` 无队伍级开关；旧 `PlayerActor` 撮合器（`TryMatchDuelAsync`）在 ECS 重构中被删未移植致生产单人斗法永不撮合；`DuelScript`/`DuelPlaybackState` 为 1v1 模型；会话 spec 无斗法字段。阶段 A：`social.parties.pvp_enabled` 档案级字段+schema v29+仲裁按 spec↔档案漂移散场、`ActionParticipationSpec.PvpEnabled` 冻结、前端斗法复选框/「准备」改名/移除改右上角 ×。阶段 B：`DuelAdvanceOptions` 全链注入、`EcsPlayerRuntimeAdapter.MatchDuelsOwned`（寻求者→候选池±PowerBandRatio 过滤→双预约→镜像→剧本→Attach+推版本，失败回滚）、主服 `MatchingEnabled:true`（ComputeNode/补算保持 null）、版本链对齐 schema 24→29+3 条撮合测试。设计口径（§7.10.4/§21.13）：匹配单位分单人/队伍、单位实力=出战六维等级总和、剧本多方化名单+单位下标、败方暂存物按胜方成员数均分、斗法中散场=整队弃战判负。回归基线 Domain 305/Ecs 100-101/Server 268-270（失败项均为在途改动，与本任务无关）。

### TASK-20261010-01 dev.sh TUI 升级为运维控制台
- 分类：开发工具链 / 本地开发体验
- 时间：提出 2026-10-10 18:23:07｜完成：无
- 状态：🧪 验证中（无头烟测全绿；待真实终端体验确认）
- 摘要：dev.sh TUI 三连跳升级：①日志 8000 行滚动回看（↑↓/j/k/PgUp/PgDn/Space/g/G/鼠标滚轮）+e/w/i 级别过滤+长行折行+底部自动跟随+向上翻阅钉住视口；②`:` 指令模式直连 `/ops/api`（overview/health/players/sessions/scheduler/storage/compute/logs/errors 查询+kick 等写操作，令牌自动读 `appsettings.Development.json` 的 `Ops:Token`、y 二次确认、历史/Tab 补全）；③性能重构——dev.sh 启动流程入后台子 shell、TUI 前台直读 `/dev/tty` 原始字节流（删除 stty/转发文件/轮询，输入延迟 ~0.4s→即时），渲染改事件驱动+`setImmediate` 合帧+行级 diff 只写变化行（弃 200ms 全屏重绘）；TTY 退出改 TUI 自行退出并 SIGINT 通知 dev.sh，cleanup 用 pid 文件兜底。验证：PTY 直连实测字节流→渲染零中转、指令真实命中 5128 后端（令牌自动加载、SESSION_NOT_FOUND 正确映射）、无头回归全绿。

### TASK-20261009-08 本地真实运行 500+ 角色持续压测
- 分类：压测 / 运维验证 / 配表
- 时间：提出 2026-10-09 22:58:00｜完成：无
- 状态：🧪 长跑中（本轮 500 满编在线，持续监控不停止；已修监控协程超时退出/并发集合损坏/组队自愈/SnapshotDelta 丢帧永久漂移等缺陷）
- 摘要：新增 `src/LoadTests/FleetScenario.cs`——每角色独立账号/设备指纹/XFF/WebSocket，状态机驱动 bootstrap（跳引导→定点采集→锻造演武套→loadout.replace）后按分工常驻：采集者/制造者循环产线、单人战斗驻溪谷池无限讨伐（全员斗法）、组队 5 人一队驻桃林/岩滩、巡游者全图轮转；配表新增 `equipment/trials.json`+`actions/trials.json` 演武六件套（材料取真实采集链，Lv1 单挑溪谷 100% 胜率）。压测暴露并修复：监控协程 `OnEdge` 判反、loop 队列永不排空、bootstrap 断线恢复迟缓、`SOCIAL_NOT_IN_PARTY` 噪音、dev-hot 拉起 `setTimeout().unref()` 致崩溃后不再拉起、市场结算 SSI 竞争（新增 `market.settlement_cursors` 游标锁 v28、`PartitionGates` 分区写事务进程内串行、`PlayerCommitGates` 条带闸提取共享、SnapshotDelta 丢帧永久漂移）。验证：实例 #47 稳态 `online=500`、flushQ ~2K、trades ~150/min、`40001=0/40P01=0`、市场瞬态错误=0。

### TASK-20261009-07 战斗单位卡灵力条下实时威胁值
- 分类：前端体验 / 战斗信息呈现
- 时间：提出 2026-10-09 18:35:00｜完成：无
- 状态：🧪 验证中（前端 build+契约全绿；待 UI 目检）
- 摘要：战斗详情各单位卡蓝条下方新增「威」徽+实时威胁数值。链路本就全程就绪（领域 `CombatActionRules` 维护威胁轮盘、`ProtocolSnapshotMapper` 每帧常驻下发 `playerThreat`/`enemies[].threat`/`allies[].threat`），仅缺上屏：`ArenaActor` 增 `threat` prop 在灵力条后渲染 `UnitNumber` 行；`EnemyDisplayState`/`AllyDisplayState` 增 threat 字段；斗法镜像无威胁语义传 null 不渲染；`.arena-threat` 紧凑样式（crimson 敌意色调、tabular-nums）+ i18n `threatLabel`/`threatAria`。逐帧快照驱动自然实时刷新。

### TASK-20261009-06 本地库数据修复：白青蘅技能槽位序/松风琴 + 岳千钧物理防具套
- 分类：数据修复 / 本地存档 / 组队共战
- 时间：提出 2026-10-09 13:35:13（武器/防具追加 2026-10-10）｜完成 2026-10-10 ~14:10
- 状态：✅ 已完成
- 摘要：三修本地库 `daohua`（仅数据，9+条 UPDATE 全 CAS 护栏命中）：①白青蘅掌心雷（0CD 填充技）占位技能槽 0 压制全部技能（槽序即优先级），重排为 `[轰炎,清平乐,雷光引,掌心雷]`——loadout 分段、进行中会话冻结 spec（三份 action_queue+flush 队列+party_sessions.spec）、盟友镜像 `pendingSkillIndex`/`skillCooldownUntil` 同步重映射，SHA-256/提交头校验全量重算；②白青蘅武器朝元杖换同 tier/quality 松风琴（healingBonusPoints:50，+20 保留），活跃会话 spec/runtime_state 内嵌成员同步换装；③岳千钧法系布装四件换物理 T2 套（+20 保留），冻结 stats 按 `PlayerAttributeResolver` 精确补丁（defense 22.05→208.45、magicResistance 164.91→5.25、maxHitpoints→633.23、maxManapoints→220）。schema_version 不动（23），校验脚本全绿，角色/队伍/会话完整。

### TASK-20261009-05 组队讨伐队伍级权威会话（战斗只算一次）
- 分类：架构重构 / 组队共战 / 执行模型
- 时间：提出 2026-10-09 11:20:00｜完成 2026-10-09 21:06:18
- 状态：✅ 已完成（提交 `4926210`）
- 摘要：废除「共享剧本、每名成员各自确定性重放」（成本 N×单场），改一次组队讨伐会话=队伍级权威计算单元：`ActionParticipationRuntime` 按会话串行持有唯一 `ActionParticipationSessionEngine`（独立于队长/成员/玩家分片）；会话检查点唯一推演+成员个人收件箱原子结算（资产/分段/回执同事务）；成员侧只保留会话引用+结束时刻+消费序号游标；任一成员在线实时推进、全员离线挂起记录时刻、回线按墙钟对齐补齐（离线成员按冻结面板继续被模拟）；散场按权威时刻给每名成员结算终态（倒地→1 血复活口径）；崩溃重启从检查点恢复。规则层不变量全保持（开战条件/冻结面板/散场规则/收益口径/复活口径/前端表现）。攻坚记录：容量三轮调优（未参战玩家会话查询热路径过滤、成员回复余数、精确入场扣蓝/吟唱、seq0 开场事件、故障隔离、MVCC 身份版本、积压批次可恢复、主服托管堆预算 512MiB）、社交状态广播重复合并、退休行动绑定补投、队首重复入队修复。验证：verify.sh 全绿（Domain 300/Ecs 100/Server 266）、capacity-target errors=0（100/500 握手+2000 冷算，峰值 301.86MiB）、linux/amd64 双镜像健康+WS 首帧、尐妖三人真实 WebSocket 同刻帧逐位一致+全离线挂起重启回线+散场 1 血复活目检（PDF→PNG）。

### TASK-20261009-04 战斗单位详情弹窗「装配」Tab：功法与装备一并可查
- 分类：功能补全 / 详情弹窗 / 全栈
- 时间：提出 2026-10-09 11:08:01｜完成：无
- 状态：🚀 进行中（代码与离线验证完成；浏览器目检与 DB 集成测试待补）
- 摘要：`CharacterAttributeModal` 新增「装配」Tab 同屏展示功法与装备（怪物无装备概念只显示功法）。数据源分层：玩家读本地常驻 `combat.abilitySlots`/`combat.equipment`；怪物读静态目录（`abilityIds` 升级为 `skills[]` 带配表等级）；盟友读 `ActionParticipantSnapshot` 开打冻结（新增 `Equipment` 冻结字段 v23）；斗法镜像读 `DuelPlaybackState.OpponentSkills`/`OpponentEquipment`（v23）。盟友/斗法装配与属性面板同走 `UnitInspect` 按需一次性投影通道，前端按身份键缓存（与 `inspectedAttributesCacheRef` 同模式）不占常驻带宽；存档 schema v22→v23。

### TASK-20261009-03 组队散场倒地成员即刻复活（1 点气血）
- 分类：Bug 修复 / 组队共战 / 领域
- 时间：提出 2026-10-09 10:50:00｜完成：无
- 状态：🧪 验证中（Domain 290 / Ecs 84 全绿；待 UI 目检）
- 摘要：单人路径散场倒地本就即刻复活（队列变更管线 `RecoverHitpoints` 气血回 1），组队特有缺陷：`DissolveSessionAsync` 只调 `EndActionParticipation` 写散场时刻，参与条目实际移除（携带复活结算）推迟到成员下次推进——离线成员强制装载写完即卸气血停 0、快速再入场以倒地定格进新场。修复：复活时机提前到 `EndActionParticipation`（写权威散场时刻同时气血≤0 恢复到 1 并标脏 Combat 分段，散场/看门狗/装载对账三路径共用）；不恢复灵力、不移除绑定（正式出队仍在推进到散场时刻进行，`RecoverHitpoints` 复查自然变 no-op）；§21.13.1 补设计口径。新增领域测试含灵力不恢复断言。

### TASK-20261009-02 组队表单默认值跟随 + 讨伐中「停止」改为取消准备散场
- 分类：前端体验 / 组队 / 交互
- 时间：提出 2026-10-09 10:33:25｜完成：无
- 状态：🧪 验证中（前端 build+契约全绿；待 UI 目检）
- 摘要：①创建/匹配表单节点默认值改脏标记跟随（`useCurrentNodeField`）：未手选时始终跟随 `world.nodeId`（修复挂载时 nodeId 为 null 永停「不限」与移动后不更新），手选后锁手选、选回当前节点恢复跟随；`LeaderProfileModal` 快照初始化与 `LobbySection` 默认「不限」不动。②参与行动条目的停止/移除入口（顶栏停止钮/队列行 ×/战斗终止钮）由弹提示改走 `pendingQuitSession` 散场确认弹窗（复用 `social.confirm.quitSession*` 文案 danger），确认派发 `party.unready`——散场恢复为纯服务端链路（仲裁散场→EndActionParticipation 写时刻→推进移除参与条目→ActivateHead 恢复挂起个人行动）；↑↓ 移动钮禁用与 queue.clear 会话中拒绝口径不变。

### TASK-20261009-01 组队界面收拢：队伍信息编辑收进「修改队伍」弹窗
- 分类：前端体验 / 组队 / 信息密度
- 时间：提出 2026-10-09 08:25:08｜完成：无
- 状态：🧪 验证中（前端 build+契约全绿；待 UI 目检）
- 摘要：对齐银河奶牛式参考三轮重做：①档案只读摘要常驻（队名+`·`分隔信息行：目标/地点/公开性/身家门槛/人数），编辑表单收进 `LeaderProfileModal`（复用 Modal+modal-form 范式）；顶部操作条左（修改队伍+本人参战开关）/右（离开+解散）双组，删除常驻编辑卡/分享卡/底部危险卡。②成员纵列改 `party-slot-grid` 槽位横排卡（立绘+队长标+在线态+身家+本人就绪勾选/他人就绪标签+队长移出+空槽虚线占位）。③在队态收拢为纯队伍面板：删除会话横幅、分享卡、页签导航与大厅在队分支（组队大厅/匹配/创建仅无队伍态）；`DropdownSelect` 选项层改经 `FloatingPopover` Portal 挂载（fixed 定位、视口夹持、上下翻转、外点/Esc 关闭，修复 absolute 撑开 modal 被裁剪），`FloatingPopover` Esc 补 stopPropagation。同步清理大量死样式与死词条。

### TASK-20261008-15 自动消耗品物品栏域模型收拢（栏域标识取代行动大类）
- 分类：数据模型 / 物品体系 / 全栈
- 时间：提出 2026-10-08 22:51:00｜完成 2026-10-08（verify.sh 全绿）
- 状态：✅ 已完成
- 摘要：物品层级定为「可使用物品→消耗品→自动使用（入栏）/手动使用（不入栏）」；自动使用槽按「物品栏类型」分支收拢：历练栏（修炼/采集/挖掘/探索等非制造行动共栏）6 槽、制造技艺栏（炼丹/炼器/符箓/阵法/强化各独立一栏）各 3 槽、战斗栏（药品 3+通用 3 不变）。落地：Domain `AutoUseBar` 栏域词表、`ConsumableDefinition.UsableInBars` 取代 `usableInActionTypes`、行动→栏域权威解析（含 technique 归属）、`queue.auto_use.configure` 栏域校验、持续/即时/耐久三路径与战斗参与快照统一口径、前端 `AutoUseBarType` 与单历练栏 6 槽渲染、引气丹/通脉丹迁历练栏、DataTool 词表+校验、config-editor 与文档同步；持续消耗品按行动大类倍率≤1 不充能不扣时长。

### TASK-20261008-14 六维经验曲线改为境界修为表派生（一级三点、平滑均摊）
- 分类：数值模型调整 / 领域+ECS / 前端
- 时间：提出 2026-10-08 22:15:00｜完成 2026-10-08
- 状态：✅ 已完成
- 摘要：废除立方曲线 `floor(cbrt(exp/100))`（Lv18 单维 1574 万经验为境界累计修为 7 倍、期望点数比值 Lv54 塌到 0.06×）：六维成本表由 `realmProgression.expToNext` 派生——每维 381 点上限（127 级×3），第 p 点经验 = `B` 在 `t=(p-1)/3` 处线性插值（`B[i]=expToNext[max(0,i-1)]` 跨入第 i+1 级那格修为条），单维累计至 3Lv 点≈3× 境界累计修为（Lv18 后稳定 3.05~3.3×，Lv127 收敛≈3.05×）。期望点数同步恢复全线性 `ExpectPoints=3×Level`（取代 TASK-20261008-13 的 Lv²/6 二次分布；`levelExpected[]` 每级锚点机制保留）。击杀 `experience` 同额入账各归属维度 1:1 不变（期望线为满培养基准，差额由内功乘率等通道消化）。验证：Server.Tests 240/240（净库）、Domain/Ecs 全绿、DataTool strict、前端 build。

### TASK-20261008-13 期望六维分布前 18 级二次化 + 每级期望锚点
- 分类：数值模型 / 配表 / 领域+测试
- 时间：提出 2026-10-08 19:45:00｜完成 2026-10-08 19:58
- 状态：⚠️ 部分被 TASK-20261008-14 取代（二次分布回退为 3×Level；levelExpected 锚点保留）
- 摘要：两项改动：①expectedPoints 由 level×3 改 Lv≤18 取 lv²/6（18 级收敛 54，解决前期怪面板虚高）；②怪物底座 `ExpectedAttributes.At(level)` 在 12 个等阶锚点线性插值因二次曲线凸性弦高于曲线（Lv3 插值 362 vs 公式 170），新增 `levelExpected` 每级期望锚点数组（Lv1–127）把插值粒度降到相邻整级。效果：T1 等阶锚点 finalExpected 下降（HP 157→约 114），Lv2–17 全部怪物面板回落；木灵 Lv3 HP 652→305，坠星兽 Lv24 Boss HP 45170→41285。后续 TASK-14 用户决策把期望点数恢复全线性 3×Level，levelExpected[] 锚点机制与 ParseExpected/TierAt 修正保留生效。

### TASK-20261008-12 部署后验收与尐妖账号 001 调整
- 分类：生产验收 / 账号数据 / 故障修复 / 观测
- 时间：提出 2026-10-08 19:00:00｜完成 2026-10-08 19:10
- 状态：✅ 已完成
- 摘要：①生产库清理两个烟测账号（smoke_probe/deploy_smoke 全域清理：游戏域+会话+风控+审计+身份行），`player_seq` 经 DROP/ADD IDENTITY 重置使尐妖=1 号。②备份链路复测：`backup.sh` 随新 compose 正常出 dump+SHA256。③发现并修复线上故障：`appsettings.Production.json` 的 `AllowedHosts` 未含 Compose 内部主机名 `app`，compute-link 自首次部署起被 400 拒绝（节点 30s 重连刷日志、冷挂机零容量）——compose.prod.yaml 注入 AllowedHosts env 后节点接入成功（capacity=2 maxJobs=128）。④存储/内存三轮采样（T0/T1/T2 约 15 分钟）：磁盘 20G 持平、四容器内存 30~70MB 稳定、DB 14MB、pg_wal 129M、wal-archive 正常速率、各队列全 0、应用日志 0 错误——无异常积攒。

### TASK-20261008-11 讨伐战斗单位数据记录
- 分类：战斗数据 / 领域+ECS / 持久化
- 时间：提出 2026-10-08 16:12:00｜完成：无
- 状态：📋 需求分析中（归档时；技术方案定稿）
- 摘要：行动实例内战斗数据记录：单位=本机玩家 player、组队盟友 ally:{i}、怪物按 `monster:{definitionId}` 聚合同类；斗法只记本方不记对方玩家。指标口径：伤害（造成）/受到伤害（实收）/减免伤害三项全按物理/术法分列（减免=命中时 `CombatHitContext.RawDamage`−实落账伤害）；治疗分「自动回复」（战内 5 秒 tick+行动期间战外自然回复）与「其他治疗」（技能/汲取/药品瞬回）；倒地复活满血不计入治疗。生命周期：随行动实例跨波次/跨搜寻相位累计，行动出队即弃、归档战报不保留。展示层两轮修订：战斗统计从账本小节拆出为独立按钮+`CombatStatsPanel` 弹窗（我方/敌方阵营页签 × 输出/承伤/减免/治疗指标页签，当前侧全单位降序横向对比条形图，物理红/术法蓝分段色收拢面板级 CSS 变量）。

### TASK-20261008-10 项目审计问题全部修复
- 分类：全面修复 / 安全 / 持久化 / 全栈
- 时间：实施登记 2026-10-08 15:38:01｜完成：无
- 状态：进行中（代码与完整隔离验证通过；真实容量资源门禁失败关闭；不提交、不发布）
- 摘要：按批准的七阶段方案修复 TASK-20261008-04 审计确认缺陷：①学习扣账等异常统一恢复状态（冻结 before/dirty，资产+sections+receipt 同事务，提交结果未知按稳定幂等键消歧 fail-closed）；②模拟资产与消费来源分离、成功事务立即确认实写脏段；③建筑贡献改行锁封顶+经济请求+回执+建筑更新同 Serializable 事务（requested/applied 重放）；④跨玩家变更统一租约续期、丢失拒绝并逐出；⑤Compute 主服冻结完整基线独立重演逐字节校验、只有可信结果入账（源漂移/上线/钱包版本/任务租约漂移全回滚）；⑥前端坏帧/会话隔离/拆包修复；⑦真实容量门禁及交付文档。保留 TASK-20261008-09 的 WAL/PITR 与部署改动，不改 vendor/YuoCore。验证：全新隔离库 Server.Tests 240/240 通过（污染库曾有 1 项冷挂机筛选失败）；未执行生产更新、CCR、远端迁移或公网验收。

### TASK-20261008-09 yuohira 正式部署
- 分类：生产部署 / 数据安全 / 运维修复
- 时间：提出 2026-10-08 14:31:22｜完成 2026-10-08 18:55:00
- 状态：✅ 已完成（`eeb90aa` 按 digest 成对部署，回环+公网烟测通过）
- 摘要：恢复磁盘满导致的数据库停机并部署当前版本：根分区 59G 满（33G 为 pg_wal 积压，archive_command 拒绝 .backup 文件致 `pg_stat_archiver` 持续失败）——经授权清理 Docker 构建缓存/systemd 旧日志/无关容器日志后数据库与旧版恢复；253 段 WAL 归档归一化 .gz；生产库级 v4、存档标签 v3，经用户单独授权执行迁移 v4→v21（不可迁移漂移按规约清零该玩家游戏域数据、保留账号/角色身份）；权限收敛执行 `postgres-local-roles.sql`（五权限组+六 schema 授权+序列 USAGE/DEFAULT PRIVILEGES，创建 `daohua_app_login`/`daohua_worker_login`）；deploy-prod.sh 上传资产→ccr-auto-update 按 digest 成对更新→双容器 healthy+`/readyz` ok；Caddy 校验、5 个 timer 全 enabled；回环全量（注册通道）与公网 HTTPS/WS 烟测通过。遗留：persistence-worker 无健康检查 docker 侧显 unhealthy（既有表象）；`postgres-local-roles.sql` 序列授权项在生产以补充 SQL 应用待补登。

### TASK-20261008-08 详情页盟友单位可点查看属性
- 分类：功能补全 / 组队共战 / 前后端联动
- 时间：提出 2026-10-08 12:10:32｜完成：无
- 状态：🧪 验证中（verify.sh 全绿：Domain 275/Ecs 69/Server 224；待 UI 目检）
- 摘要：落地协议预留的 `side:'ally'` 观察通道：`ProtocolSnapshotMapper` 穿线参数由 `int? inspectedEnemyIndex` 泛化为 `UnitInspectDto? inspectedUnit`，`ToExpeditionAlliesDto` 命中 memberIndex 时投影 `member.Stats`（开打冻结面板，共享剧本唯一公共输入，走 `ToAttributesDto(CombatantStats)` 统一投影重载）；`WebSocketSession` 改 `PendingInspectedUnit` 返回完整观察目标、`MarkInspectedAttributesDelivered` 按 record 值相等解除武装；前端 `ExpeditionAlly.attributes` 解析+`inspectedActor` 扩展 `ally:{memberIndex}`+`ally:{playerId}` 身份缓存+盟友 `onInspect` 接 `CharacterAttributeModal`；本机位仍走 `'player'` 本地详情。不新增协议类型、不留兼容分支；新增 Server.Tests 盟友投影用例。

### TASK-20261008-07 本地开发 TUI 视觉升级
- 分类：开发工具链 / 本地开发体验
- 时间：提出 2026-10-08 11:14:25｜完成 2026-10-08 11:52:44（提交 `40ea3c8`）
- 状态：✅ 已完成
- 摘要：dev.sh TUI 由单一日志列表升级为固定网格控制台布局：品牌标题栏、阶段/运行时长状态卡、服务状态卡（后端/前端）、带活动标签的分区日志面板、底部快捷键栏；保留无第三方依赖、ANSI alternate screen、原始键盘输入和日志文件轮询；`q`/`Ctrl+C`/`a`/`b`/`f`/`r` 快捷键语义不变，新增日志级别颜色与空状态提示；窄终端自动收缩、中文宽度与日志长行不破坏边框。验证：`node --check`/`bash -n`/`git diff --check` 通过；PTY 宽屏/窄屏烟测无 `undefined` 文本且边框完整。

### TASK-20261008-06 本地开发脚本 TUI 化
- 分类：开发工具链 / 本地开发体验
- 时间：提出 2026-10-08 09:53:20｜完成 2026-10-08 11:04:33（提交 `d387ce8`、`709c07e`）
- 状态：✅ 已完成
- 摘要：`dev.sh` 从持续刷屏的纯命令行输出改为 TUI：保留数据库自检、端口清理、后端热重启、前端 Vite、本地域名入口和 Ctrl+C 级联清理的既有启动链；新增无第三方依赖 Node.js TUI（ANSI alternate screen+终端原始输入+日志文件轮询），后端监督进程与前端 Vite 输出重定向到本次运行临时目录；快捷键 `q`/`Ctrl+C` 停止、`a` 全部/`b` 后端/`f` 前端/`r` 刷新；非 TTY 退化为普通日志流便于脚本/CI。本任务只提交 `scripts/dev.sh` 与 `scripts/dev-tui.mjs`，用户已有审计修复 WIP 保持未暂存。

### TASK-20261008-05 全面审计风险修复
- 分类：全面修复 / 安全 / 持久化 / 全栈（对应审计 TASK-20261008-04）
- 时间：提出 2026-10-08 01:49:49｜完成 2026-10-08 12:35:56（提交 `0072f03`）
- 状态：✅ 已完成（未执行远程生产操作）
- 摘要：完整修复上一轮审计确认的安全/持久化/协议/部署/门禁缺陷：生产数据库权限、镜像完整性、ComputeNode 传输安全、烟测凭据与本地数据脚本安全；ComputeNode 结果约束（不得未经主服可验证约束直接产生资产结算）；组队参与散场、公共建筑投影、快照字段与存档恢复的跨版本/刷盘安全；WebSocket 握手状态、请求回执、战报技艺收益与真实载荷烟测闭环；schema/迁移/部署文档同步。验证：verify.sh 全量通过（Release、Domain 275、ECS 69、Server 224、DataTool strict、Compose、前端 build）；脚本语法、前端契约、本地健康/API/显式鉴权 WebSocket 烟测通过。

### TASK-20261008-04 项目潜在问题全面分析
- 分类：架构审计 / 风险盘点 / 交付门禁
- 时间：提出 2026-10-08 00:40:49｜完成 2026-10-08（静态审计结论形成）
- 状态：📄 分析完成（只读审计；未执行生产/远端/Docker/PITR）
- 摘要：全面只读审计覆盖架构边界、确定性模拟、ECS/社交、持久化/并发、协议/前端、认证、测试、容量、备份与部署链路，输出 17 条按严重性排序的 P1/P2 风险清单（只写当前代码/配置可复核的风险，区分已确认静态缺陷、门禁覆盖缺口与待实环境证据）；建议把备份恢复、真实冷挂机/协议 payload 和公网发布烟测纳入可执行门禁而非只保留静态/合成检查。修复实施由 TASK-20261008-05/-10 承接。

### TASK-20261008-03 组队模块化重构：通用行动参与承接组织接入
- 分类：架构重构 / 组队 / 行动 / 全栈
- 时间：提出 2026-10-08 00:21:34｜完成 2026-10-08 01:20:00（提交 `4abe5af`）
- 状态：✅ 已完成
- 摘要：组织系统只负责把多个玩家连接到同一个既有行动，不独立实现讨伐规则或表现——删除 `party.expedition`/`IsPartySession` 专用行动，社交开战选择真实讨伐行动并写 `ActionParticipationSpec.ActionId`；队列/聚合/ECS/讨伐执行/协议投影/前端改通用 `participation` 字段；暂停不阻挡到期散场；装载前先会话权威对账、看门狗补发活跃会话漏投成员（保留原行动支持同 ActionId 插队恢复）；schema v21+迁移+契约+单人/多人确定性回归对齐。未来 Team/Guild 只需提供组织成员/准入/容量/准备与开战散场裁决并生成同形参与规格；宗门累计任务应另建贡献聚合订阅成员行动结果而非伪装多人同场行动。验证：verify.sh 全绿（Domain 275/ECS 69/Server 221/DataTool/Compose/前端 build）。

### TASK-20261008-02 万级数值取整失真修复（1.8万 显示成 1万）
- 分类：Bug 修复 / 数值显示
- 时间：提出 2026-10-08 00:12:00｜完成：无
- 状态：🧪 验证中（前端 build 全绿；待 UI 目检）
- 摘要：`formatUnitNumber` 的 `maxDecimals` 误应用在万进制换算后的值上：18000→1.8→floor(1.8)=1→「1万」，万以下尾数全丢（<10000 分支对原值取整故正常）。修复为原值先取整再按默认精度缩写。波及面：战场血条徽章（`ArenaResource`）、顶栏战斗血条文本、附近/生态面板怪物属性、地图怪物卡、掉落数量徽标及全部 `formatInteger` 调用点（跳字/经验/掉落数量/建筑用量/预算区间）共 8 处恢复默认精度；悬浮气泡完整分段不受影响。

### TASK-20261008-01 战场单位卡去「我」标记 + 敌方普攻弹道方向修复
- 分类：前端体验 / 战斗动画 / Bug 修复
- 时间：提出 2026-10-08 00:04:01｜完成：无
- 状态：🧪 验证中（前端 build 全绿+画廊 Playwright 实测；待 UI 目检）
- 摘要：①删除组队讨伐己方阵列中本人位名称后 `arena-self-tag`「我」徽标及关联 marker 链路与词条。②竖屏纵列下敌方普攻弹道方向反：敌方弹道容器 `scaleX(-1)` 镜像后 `rotate(-angle)` 双重否定致倾斜角符号反——改回正角度，镜像侧量取的 `--arena-aim-angle`（`measureArenaAim`，仅 ≤760px 竖屏纵列启用）恢复「目标在下朝左下偏」正确方位。验证：画廊 Playwright 实测敌方弹道盒偏移 −53.2（与己方 +53.2 镜像对称）、突进 dx −17.9 向左、竖屏 35.5° 瞄准弹道朝左下正确。

### TASK-20261007-08 移动端长按显示悬浮详情（触屏 contextmenu 分流）
- 分类：前端体验 / 移动端交互 / FloatingPopover
- 时间：提出 2026-10-07 23:11:09｜完成：无
- 状态：🧪 验证中（前端 build 全绿；待 UI 目检）
- 摘要：移动端无 hover，物品/技能详情气泡不可达；且浏览器原生长按派发 `contextmenu` 被业务右键回调消费（背包格长按误触「快捷使用」高危）。按用户口径不自建双击/长按手势映射：`FloatingPopover` 触发器触屏 contextmenu 统一分流——长按（原生右键事件）改为展示本触发器 hover 详情气泡并屏蔽系统菜单，不再误触业务右键快捷操作；桌面端 hover/左键/右键行为不变。

### TASK-20261007-07 战斗页队伍面板 + 参战开关（组队共战 UI 补全 + 快照导出修复）
- 分类：前端体验 / 组队共战 / Bug 修复
- 时间：提出 2026-10-07 22:20:00｜完成：无
- 状态：🧪 验证中（verify.sh 全绿：Domain 274/Ecs 69/Server 221；待 UI 目检）
- 摘要：盟友阵列恒空根因：`EcsWorldShard.ExportSnapshotCore` 重建 `QueuedActionState` 漏传 `PartySession`（聚合 ExportActions 路径有传、ECS 路径没有）→快照 PartySession=null→spec 为 null→allies 恒 null；连带装载对账「陈旧绑定识别」也读这份导出快照而漏检（只能靠看门狗兜底）。修复导出后新增 PartyPanel（名册/在线/参战状态/会话实时血蓝/本人「参与斗法」开关/空槽占位）参考银河奶牛式成员卡片条；SocialPage 会话中本人可「退出斗法」散场（danger 确认，解除前端 disabled 锁死）；盟友 DTO 补 `playerId` 配对键。后续用户裁定详情页不挂队伍卡片条——PartyPanel/`.party-strip*`/`.party-slot*`/join-toggle 及 `social.strip.*` 词条整体移除（expedition.css 还原两行布局），参战管理保留在「同道→队伍」页；`qc-current` 旋转描边轨道底色改 `var(--gold-faint)` 修浅色主题不可见。

### TASK-20261007-06 斗法属性面板精简：伤害增减并入特殊 + 默认值隐藏
- 分类：前端体验 / 信息密度
- 时间：提出 2026-10-07 16:47:00｜完成：无
- 状态：🚀 进行中（实施完成，归档时未标收口）
- 摘要：斗法属性面板（内视 Tab 与单位详情弹窗共用）：「伤害增减」整组并入「特殊」Tab 最下面；所有等于默认值（点数 0/乘区 ×1）的属性不再显示，暴击伤害例外恒显。顺带完成 TASK-20261007-05 面板侧诉求：乘区排删除、暴伤按 `1+点数/100` 显示（`criticalDamagePoints` 点数折算）。common-components.md §25 同步；前端 tsc+build 全绿。

### TASK-20261007-05 成长乘区收敛为点数通道（废除独立因子属性）
- 分类：数值模型调整 / 领域+ECS / 协议+前端 / 文档
- 时间：提出 2026-10-07 16:03:00｜完成 2026-10-07（verify.sh 全绿；未提交随 WIP 一并）
- 状态：✅ 已完成
- 摘要：废除吟唱/冷却/普攻倍率/技能强度/暴击倍率独立因子字段，`CombatGrowthCurve` 改为按境界/等阶向既有点数通道注入「等级保送点数」（与装备/Buff 点数同池相加过同一条曲线）；暴击伤害新设点数通道、基础 50 点=150%；普攻/技能增幅并入原始威力乘位；全部折算保持与旧因子曲线逐位等价（benchmark 期望表数值不变）。Domain/Ecs/Server/前端/配表/文档全链路切换；`SectionCodec`/`PostgresSchema` 升 v18+`migrations/0018_v17_to_v18.sql` 标签迁移（本地库 platform=18，63 头+504 段推进）。验证：verify.sh 全绿（Release 0 警告、Domain 266/Ecs 68/Server 221、DataTool strict、前端构建、契约）。

### TASK-20261007-04 顶部主行动栏进度可读性与战斗血条样式优化
- 分类：前端体验 / 主行动栏 / 视觉可读性
- 时间：提出 2026-10-07 14:28:25｜完成：无
- 状态：🧪 验证中（前端 build 全绿；待 UI 目检）
- 摘要：①`.progress-text` 浅色文字压在亮金填充上对比度不足：方案取舍选暗色描边（多层 text-shadow）优于背景光晕，提升填充段可读性。②战斗状态敌我血量由「16px 标签列+细条+右文字」三段网格改两条完整进度条、文字内嵌条内居中（与其他进度文本一致），保留斜切倾斜轮廓，含竖屏两行适配。注：本任务原登记 -03 与「同图共战」撞号改号 -04。

### TASK-20261007-03 同图共战（组队战斗）系统设计
- 分类：系统设计 / 战斗 / 多人
- 时间：提出 2026-10-07 12:30:00｜完成 2026-10-07（verify.sh 全绿：Domain 274+Ecs 68+Server 221）
- 状态：✅ 已完成
- 摘要：补齐总纲 §21.1「本期不含同图并肩作战」空缺，设计落地 §21.13：整体制（成员数=开启槽位、全员就绪、全员位于目标节点）/槽位/共享掉落加权/经验平分+等级惩罚/离线语义/节点约束/全灭沿用遭遇单人口径；架构收敛为「共享剧本各自推演」（斗法同构）。实现：契约层 PartyExpeditionSpec/PartyMemberSnapshot/party.expedition 内置行动/dropBonusPoints/schema v20；Domain 盟友化（PlayerAllies/SelfIndex/逐盟友倒地复活/RestoreAllied）；ECS 推进盟友化（相位派发/AllySide 随机侧位/盟友实体）；CombatActionRules 组队路径（遭遇钉死/冻结面板/经验平分+malus/共享掉率+归属掷点/波次链镜像）；Server 社交层开打散场裁决/投递/48h 看门狗；前端盟友阵列/会话横幅/槽位 UI。修复 EnqueuePriority 归一化把会话行动 RepeatCount 压成 1 致波次恒 1（组队行动豁免）。新增 CombatPartyExpeditionTests 8 条。

### TASK-20261007-02 怪物击杀经验重新设计
- 分类：配表 / 击杀收益 / 数值
- 时间：提出 2026-10-07 11:43:44｜完成：无
- 状态：🧪 验证中（Domain 266/Ecs 68/Server 221/DataTool 全绿；待 UI 目检）
- 摘要：怪物配表 `experience` 由恒 10 改公式派生：`expToNext[level]/1000×0.9^(lv-1)×层次倍率`（精英 3×/妖王 10×）；六维改全额口径（总量 6×）；配表保留覆盖口；组队平分语义写入总纲 §7.10.3。经两轮澄清定稿（份额衰减方向、层次倍率、六维 6×、组队平分文档化）。

### TASK-20261007-01 节点公共建筑拥挤衰减：按等级承载人数 + 超编线性衰减
- 分类：全栈新功能 / 建筑 / 数值
- 时间：提出 2026-10-07 09:03:12｜完成 2026-10-07（verify.sh 全绿）
- 状态：✅ 已完成
- 摘要：公共建筑增益由无视人数改等级承载：一级上限 100 人、每级 +10% 基数；超编每 100 人 −10% 线性衰减封顶 −50%。「正在使用」权威判据=行动队列 `Running` 行动 `ActionKind` 归属 `current_node_id`（在边上赶路不计）；读模型复用 `game.player_action_queue`×`game.player_projection`（两写路径事务性全量重建）`JOIN WHERE status='running' GROUP BY node,action` 得 (节点,行动)→人数，无需新增写路径；ComputeNode 回写补挂 `UpsertProjectionAsync` 修复补算迁地投影滞留。注册表内 15s TTL 使用计数缓存（对齐脏龄去抖），每拍投影携带衰减后效果集+每座建筑 usage（使用数/承载/效率）；建筑 tab 显示「使用中/超载」。衰减为软指标非资产结算（秒级最终一致，单人模拟只消费持久化投影，确定性与建筑等级投影同口径）。

### TASK-20261006-08 内功心法体系设计：以六维经验分配为核心的首批内功配表
- 分类：系统设计 / 功法 / 数值
- 时间：提出 2026-10-06 20:33:57｜完成：无
- 状态：📋 规划中（设计定稿落档，待实现）
- 摘要：内功定位为经验获取调整器（区别于术法）：`rootExpMultipliers` 非守恒六维重分配（主升+副修+削弱细粒度配比，取代现「全修/单修」两极端均分语义）+参悟总量效率+修为增减点+常驻被动；首批 9 门（T1 武侠拳谱 5/T2 真修功法 4），命名分层。勘察现状：`discipline` 字段已分 internal（0 号内功位）/spell（术法位）双向互斥；3 门伪内功（嘲讽/清平乐/破阵子）为过渡态；六维 `CoreAspectKind`（体魄/经脉/力道/根骨/身法/神识）`DistributeRootExp` 总量守恒均分；秘籍 `item.tome.X↔ability.X`（习得+10000、残卷+500 需先习得）；`game-data.md` §7 的 rootAttribute 枚举过时（stamina/intelligence/... vs 真实 stamina/meridians/attack/bone/agility/consciousness）待修。设计文档已落地待实现。

### TASK-20261006-07 星陨异境节点生态拆分：讨伐遭遇按地貌分池
- 分类：配表 / 遭遇生态 / 数值
- 时间：提出 2026-10-06 20:06:27｜完成：无
- 状态：🧪 验证中（verify.sh 全绿；待 UI 目检）
- 摘要：第二张图全部 7 节点 `encounterIds` 同指 `encounter.twilight-valley`——`randomSpawns`+`eliteEnemyIds`+`bossEnemyIds` 把 T2 全 6 怪装同一怪池，偏离 §8.3.2「按地理风貌分布 1~3 种」设计。纯配表修复（零代码）：单一遭遇拆为 7 个地貌遭遇（狼踪/尸气/鸦巢/灼穴/祭仪/煞坑/天坑领主），预算随深入递增；`encounter.twilight-valley` 保留原 ID 收窄为天坑领主遭遇（行动兜底与前端引用不变）；各节点 `encounterIds` 分池每节点恰 1 个；`combat.sweep-twilight-valley` 描述改分生态表述、`availableNodeIds` 维持全 7 节点。验证：DataTool strict、Domain 258/Ecs 68/Server 220、前端 build 全绿。

### TASK-20261006-06 选择器空选项文案统一为「无」
- 分类：前端体验 / 文案统一 / i18n
- 时间：提出 2026-10-06 19:41:48｜完成：无
- 状态：🧪 验证中（前端 build+契约全绿；待 UI 目检）
- 摘要：物品/功法/装备等待选列表首格空选项原随场景显示「卸下装备」「不使用物品」「卸下 / 不使用」等不同文案，统一收拢为「无」：`common.pickerEmpty` 改为唯一文案；删除 `CandidatePicker`/`ItemGridPicker`/`AbilityGridPicker`/`CatalogPicker` 的 `emptyLabel` prop 与全部透传/回退链路；清理死词条并 template.csv 重导出。

### TASK-20261006-05 讨伐行动迁地重锚：移动后跟随驻留地点续猎
- 分类：战斗机制 / 领域+ECS / Bug 修复
- 时间：提出 2026-10-06 16:52:00｜完成：无
- 状态：🚀 进行中（实现完成并全门禁通过；二次修复后归档时未收口）
- 摘要：移动后仍在旧地点怪群续打且行动统计显示旧地点名：远征遭遇只在首次准入解析一次，跨合法节点后旧遭遇原样续跑。落地 `CombatExpeditionState.IsPinned`（true=钉死/CreateEncounter 手动入战地点无关恒续；false=池选跨异池节点须重锚；缺省 false 恰好覆盖 v15 及更早旧存档天然走重锚口径，schema v15→v16）+ `OriginNodeId` 诊断字段；`CombatActionRules` 新增 `ExpeditionServedHere`（驻留节点池是否仍供给当前遭遇；斗法相位恒可续）与 `ReanchorExpeditionIfMoved`（普通相位作废重开搜寻；Respawning 相位保留「待起身」语义改挂新地点遭遇复活时长）；地点校验失败不报「地点不符」改报「条件不足」且不走自动移动；StatsPanel 标题改按 `actionId` 回查目录定义名再本地化（免疫存档滞留旧名）。验证：Domain 256/Ecs 68/Server 220/DataTool/前端构建全绿。

### TASK-20261006-04 顶栏主行动卡片右上角常驻「停止」按钮
- 分类：前端体验 / 操作效率
- 时间：提出 2026-10-06 16:09:04｜完成：无
- 状态：🧪 验证中（前端 build+契约全绿；待 UI 目检）
- 摘要：顶栏当前行动卡右上角新增常驻「停止」按钮，直接停止当前行动不必进详情/展开列表；沿用 `QueueCenter` 既有 `pendingRemoval` 确认链路（移除队首=停止当前行动语义等价）防误触；原各处停止/移除入口保留。

### TASK-20261006-03 坊市物品网格左右留白均衡 + 详情视图整面板滚动修正
- 分类：前端体验 / 布局优化
- 时间：提出 2026-10-06 14:46:12｜完成：无
- 状态：🧪 验证中（build+CDP 实测滚动链通过；待 UI 目检）
- 摘要：①坊市浏览页网格余量单侧堆积（`auto-fill` 空轨道不折叠+justify-content 缺省 start）三轮修正：先 `auto-fit+space-between`，再按「非满行也按满行列位排列」改 `auto-fill+justify-content:center`，终按「左右边距与列间距完全一致」改 `space-evenly`+padding=gap（6px）——实测三种宽度边距与间距逐像素相等。②详情/委托视图整面板滚动违反「滚动内容收进子窗口」约定：`.market-instrument-detail-view`/`.market-own-panel` 改钉住导航与工具条、内容子窗口内滚、表头吸顶（CDP 实测 1400×650 滚动链）。

### TASK-20261006-02 消耗品自动/手动二分与自动使用槽准入门禁
- 分类：物品体系 / 数据模型 / 全栈
- 时间：提出 2026-10-06 14:30:00｜完成：无
- 状态：🧪 验证中（DataTool strict/Domain 250/Ecs 67/前端 build+契约全绿；待 UI 目检）
- 摘要：消耗品顶层二分：自动消耗品装配进各行动域自动使用槽（内部按持续/即时、倍率/buffs/strike、适用行动细分）；手动消耗品背包即点即结算（技能书/后续宝箱礼包；不设通用 `item.use`，秘籍走 `ability.learn` 专属命令）。现状缺陷：20 个秘籍条目 `consumable` 块只声明 `usableInActionTypes` 无生效机制字段，被前后端当自动消耗品放行。修复：秘籍删除空 consumable 块（`category:"manual"` 保留）；服务端 `queue.auto_use.configure` 与前端选择器同口径加 `category=="consumable"` 准入；详情页通用物品栏及各行动域自动槽只收自动消耗品；DataTool 补不变量、Ecs 边界用例补齐；设计文档 §6.2 二分口径先行。

### TASK-20261006-01 竖屏物品格子优化
- 分类：前端体验 / 竖屏适配
- 时间：提出 2026-10-06 11:27:21｜完成：无
- 状态：🧪 验证中（前端 build 通过；待 UI 目检）
- 摘要：五轮迭代：①竖屏详情页使用栏格子缩至桌面 2/3（64→42px，`--item-slot-size`/`--item-tile-size` 托盘作用域覆写），取消 `flex:1 1 0` 整行拉伸；战斗详情消耗品/通用消耗品两组并一行、功法组独占一行。②42px 下隐藏角标（丝带保留）。③角色横条改版被否后还原原版竖向卡片仅缩小纵排。④竖屏纵列下攻击/受击按方位偏转（突进纵向分量、弹道倾斜、受击退散与刀光角度按目标上下方位量取；桌面九宫方位角 0 与原版一致）。⑤战报等级提升按 `kind+targetId` 合并（重云 Lv.1→Lv.4、境界 Lv.9→Lv.13 收敛为一条）。附带教训：vite 模块图缓存失效致第二次编辑未入产物——改完应 curl 服务产物核对调用点。

### TASK-20261005-09 引导任务化增强：跳过确认/补发/重学
- 分类：全栈新功能 / 新手引导
- 时间：提出 2026-10-05 23:20:00｜完成 2026-10-05（Domain 250/Ecs 66/Server 220 全绿；skip/restart 与全流程 e2e 双 ALL PASS）
- 状态：✅ 已完成
- 摘要：①跳过引导二次确认（明示剩余奖励直接发放+可重学）；②跳过补发未领奖励：奖励游标 `TutorialRewardsStep`（settings 分段持久化，推进仅当 nextStep>游标才入账），`tutorial.skip` 合并游标+1..Done 未领赠送/终奖走命令内经济事务发放+`tutorial.grant` 入账日志同帧推送（事务拒绝 `RestoreUncommittedPlayer` 回滚）；③设置页「引导」分类重学入口：`tutorial.restart` 只重置 TutorialStep 游标不动（不重复发奖）；前端跳过改引导层内确认卡（业务 Modal z70 够不到引导层）、Esc 开合、restart 复活靠「步骤递减检测」（不能用 milestone 非空判断——确认跳过瞬间 patch 未回会误复活）。schema v15+`migrations/0015`（v14 载荷按 tutorialStep 回填游标下限防重复领取）；连带修复前端 `SNAPSHOT_SCHEMA_VERSION` 14→15（否则快照 head 校验抛错被静默吞卡「正在同步」）。新增 3 例领域测试+隔离修复 4 例 fixture；全流程七环 e2e 13 项 ALL PASS。

### TASK-20261005-08 战斗胜利后敌方尸体留存与寻敌收尾消散动画
- 分类：前端体验 / 战斗表现
- 时间：提出 2026-10-05 22:30（登记 22:34:30）｜完成：无
- 状态：🧪 验证中（前端构建+契约全绿；待 UI 目检）
- 摘要：战斗胜利敌方单位立即移除致观感像没发生战斗：combat→search 相位迁移时前端缓存末帧敌方阵容，寻敌读条期间以尸体形态（is-dead 置灰）继续渲染；倒数最后 1s 统一播残魂萤火+淡出消散（末帧即新一波入场）。边界：队列暂停时寻敌时钟冻结、消散定时器到点复核 `queue.paused` 跳过触发恢复后由补丁重排；断线重连/刷新落在搜寻相位无缓存可回放退回原空阵型。

### TASK-20261005-07 前端能耗全面优化：消除常驻 rAF/逐帧写 DOM，统一走 WAAPI 合成器动画
- 分类：性能优化 / 前端能耗
- 时间：提出 2026-10-05 18:10（登记 18:43:07）｜完成：无
- 状态：🧪 验证中（实测功耗大幅下降；待 UI 目检动效观感）
- 摘要：实测 Edge 开 `daohua.localhost` 时 Renderer 常驻 ~45% CPU、GPU 进程 ~62%（三条光栅线程满载+CVDisplayLink 满刷推帧）、WindowServer ~45 能耗分——根因前端存在多个常驻 60fps JS 渲染循环且挂机游戏永远有进行中行动循环永不退出。改造：useSmoothProgressRef 等 5 处常驻 rAF 改 WAAPI 合成器动画+低频文本；冷却遮罩 `--cooldown-ratio` 注册 @property 走 WAAPI；progress-slide 关键帧 left→transform。实测：GPU 进程 61.6%→0.3%、Renderer 45.5→4.3、WindowServer 44.5→2.5，主线程 60fps 渲染循环全部消除。

### TASK-20261005-06 文案风格统一：仙侠韵文 + 游戏化数值两段式
- 分类：文案风格 / 前端展示 / 配表
- 时间：提出 2026-10-05 13:05:00｜完成：无
- 状态：🚀 进行中（落地完成，归档时未标收口）
- 摘要：全部玩家可见介绍文案统一为「仙侠韵文首行+游戏化具体描述/数值」两段式（诗句或双四字/双五字/单七字工整句式）：物品悬浮卡 `description`（韵文\n散句）+`effectLines`（数值）；技能悬浮卡 `describeAbility` 拼接改换行分隔；属性 tooltip 对齐六维范本「力可拔山，势能断岳。\n每点提升…」；技能书完本/残卷悬浮卡补机制提示行（完本可习得、残卷仅增经验且需先习得）。覆盖技能/物品/装备/属性/行动/建筑描述；前端 build、DataTool strict、git diff --check 全过。

### TASK-20261005-05 邀请系统与功德物品占位
- 分类：全栈新功能 / 社交裂变 / 经济占位
- 时间：提出 2026-10-05 12:41:00｜完成：无
- 状态：🧪 验证中（verify.sh 全绿：Domain 246/Ecs 66/Server 220 含 InvitationTests 6 例/DataTool strict/前端构建+契约；待 UI 目检）
- 摘要：注册可选邀请码（链接 `?invite=` 预填/手动输入）；同道「邀请」子页签（专属链接+邀请码复制+受邀人里程碑进度）；新增功德占位物品 `item.gongde`（可交易占位）与 `item.gongde-bound`（绑定 isTradable=false、sellPrice=0）。奖励：被邀请者注册 +1000 绑定功德+10000 灵石、邀请者 +1000 绑定功德；被邀请者突破练气（Lv19）/筑基（Lv31）双方各 +1000/+2000 绑定功德。schema v13+`social.player_invitations`；注册邀请码校验/关系与注册奖励同事务落库、突破里程碑幂等发放、GET /api/v1/invitation；InvitationTests 6 例全绿。

### TASK-20261005-04 新手引导系统
- 分类：前端新功能 / 新手体验
- 时间：提出 2026-10-05 11:50:30｜完成 2026-10-05（verify.sh 全绿）
- 状态：✅ 已完成
- 摘要：参考 `daojie-yusheng` 引导体系落地七环服务端权威里程碑引导（采集/炼丹/炼器/旅行/修炼/讨伐）：领域 `TutorialRules`+`PlayerStateDocument` 教程字段+`SectionCodec` v14+`migrations/0014`；行动/战斗规则在各里程碑触发推进；前端 `tutorial/` 模块（steps+TutorialOverlay）React 版遮罩高亮气泡层、快照链路+i18n；终奖功德×1000+灵石×10000；`tutorial.skip` 命令通道（后续 TASK-20261005-09 增强为补发/重学）；`TutorialProgressionTests` 回归。

### TASK-20261005-03 破阵子增伤修正与贯星击杀连锁（含连环补修）
- 分类：战斗机制 / 技能效果 / 领域+ECS
- 时间：提出 2026-10-05 11:50:00｜完成：无
- 状态：🚀 进行中（实现完成，归档时未标收口）
- 摘要：十门技能核对发现三处与设计意图不符：破阵子配表误写 `damageAbsorptionPoints:15/30s` 改全体存活友方伤害 +40% 10s（走 `TotalDamageBonusPoints` 总增伤 Linear 乘区，覆盖普攻与技能）；贯星补击杀连锁——击杀目标后对另一存活敌方再结算一次完整技能伤害（同面板独立结算链），最多连锁 2 次共至多 3 目标；连环 `100% AD+15` 单段改对同一目标连续 3 段 `80% AD+10`。Domain Buff 字段+结算接入+连锁循环+配表+校验+前端+测试全链落地。

### TASK-20261005-02 战斗日志降噪与技能施放合并
- 分类：战斗体验 / 日志呈现 / 服务端+前端
- 时间：提出 2026-10-05 11:20:36｜完成：无
- 状态：🧪 验证中（构建与战斗域测试全绿；实测普攻消失、AoE 合并成行、多目标分行对齐）
- 摘要：讨伐战报被每秒普攻帧刷满且技能行漏出功法 ID：普攻帧不进日志；技能行改 `{{ability:id|名称}}`/`{{enemy}}` 引用标记（前端悬浮卡已支持）；同一次施放多目标命中合并为一条——服务端子句分隔符 `\n`，前端 `LogRow` 按 `\n` 拆行+续行隐藏前缀克隆占位使「对」字列对齐；尾窗门控改按可产行帧计；斗法回放同口径。附带修复线上故障：Caddy `encode zstd` 把 ETag 改写为 `"<指纹>-zstd"` 变体，客户端原样回传 Hello 握手指纹恒不匹配→每帧 GAME_DATA_MISMATCH→关连接重拉配表再握手死循环；`apiClient.ts` 取 ETag 剥 `W/`、引号与压缩后缀还原权威指纹后链路恢复。

### TASK-20261005-01 七技艺工具系统全栈落地
- 分类：全栈新功能 / 技艺体系 / 装备扩展
- 时间：提出 2026-10-05 00:11:10｜完成：无
- 状态：🧪 验证中（verify.sh 全绿：Domain 237/Ecs 66/Server 214/DataTool strict/前端构建+契约；待 UI 目检与工具图标补图）
- 摘要：七技艺（炼丹/炼器/符箓/强化/阵法/采集/挖掘）各配一件可装备工具：`tool-<技艺>` 专属槽位互斥、`techniqueBonus` 配表字段 +10% 速度（与技艺等级加成同除数加算，对齐 `TimedActionDuration` 结算点）；炼器页新增「技艺」分组承载 7 条锻造配方；强化裁决 `EnhancementRules.Resolve` 拦截工具槽位（否则白烧灵石零收益）；前端装备面板 4 占位槽改 7 工具槽穿脱、行动耗时预览补技艺等级加成口径；数据结构为后续品阶工具预留（加成配表化/tier/槽位命名）。遗留：7 个工具暂无 webp 切图图标以 glyph 兜底。

### TASK-20261004-18 运维面板日志快捷复制
- 分类：前端体验 / 运维面板
- 时间：提出 2026-10-04 23:05:19｜完成：无
- 状态：🧪 验证中（前端 tsc/build 与契约检查全绿；待 UI 目检）
- 摘要：运维面板 `OpsLogsView` 新增「复制最近100条」「复制最近10条错误」按钮；相邻重复错误压缩为一条加 ×N（10 条全同即 ×10）；剪贴板降级 execCommand。

### TASK-20261004-17 触发条件跟随技能维度迁移 + 框外条件入口 + 编辑器抗快照刷新修复
- 分类：前端体验 / 交互通用化 / 数据模型调整 / Bug 修复
- 时间：提出 2026-10-04 21:39:20｜完成 2026-10-05（verify.sh 全绿；待 UI 目检）
- 状态：✅ 已完成
- 摘要：两轮交付：①条件入口由槽位常驻小按钮改 item hover 框外浮现（`ConditionedTile` 封装、按悬浮卡落位上下避让），覆盖功法神通面板/战斗面板/ItemUseSlot 三处；修复 `CombatTriggerEditor` 因 `useEffect` 依赖 `initialTriggers` 引用被快照刷新反复重置编辑状态（ref 中转+移除 `?? []` 每次渲染新建空数组放大问题）。②触发条件由装配槽位维度迁移到功法 ID 维度（卸下再装配/跨槽位移动条件保留、已学功法目录瓦片可直接配置）：`PlayerAbilitySlot` 去 Triggers、`Loadout` 新增 `AbilityTriggers` map、schema v12、`loadout.replace` 顶层 abilityTriggers；附带修复契约脚本硬编码 schema v11 与斗法镜像 `BuildDuelMirror` 遗漏 `AbilityTriggers`。

### TASK-20261004-16 内视斗法 Buff 分区精简
- 分类：前端体验 / 信息密度
- 时间：提出 2026-10-04 21:33:07｜完成 2026-10-04 21:35（前端 tsc/build 全绿）
- 状态：✅ 已完成
- 摘要：内视「斗法」Tab 与单位详情弹窗 Buff 分区：分组标题「当前运转丹药与神通加持」改「增益」；条目直接显示「名称×层数」（1 层也显示）；悬浮气泡由「加持运转中」无效信息改显具体效果行（复用 `describeBuffEffectLines`：每 5 秒回复/回复效率/属性比率等）；i18n 词条增删与 template.csv 同步。

### TASK-20261004-15 强化模型对齐 1.05^N 指数 + 装备 +N 徽标 + 背包装备消失回归修复
- 分类：数值模型修正 / 前端体验 / Bug 修复
- 时间：提出 2026-10-04｜完成：无
- 状态：🧪 验证中（Domain/Ecs 235+62、前端 build/契约全绿；待 UI 目检）
- 摘要：三项：①强化效果对齐权威基准 1.05^N 指数——`expected-attributes.json` 各阶位 `enhancementMultiplier` 实为 1.05^期望等级（+3→1.1576、+20→2.6533），但公式标签、总纲 §4.2、`PlayerAttributeResolver` 与前端悬浮卡均按「每级 +2% 线性」旧口径，实现与文档双双偏离统一回指数。②强化等级显示在物品左下角 +N 徽标（+0 不显示）、悬浮卡标题同步。③修复背包装备消失回归：`RightSidebar`「已装备隐藏」过滤按整个 `itemId` 剔除，引入 +N 变体后同物所有强化等级行被株连隐藏——改按 (itemId,+N) 精确扣减已装备件数。

### TASK-20261004-14 合并门禁审计与精简执行
- 分类：开发工作流 / 门禁治理
- 时间：提出 2026-10-04 17:35:49｜完成 2026-10-04
- 状态：✅ 已完成（verify.sh 28.7s 全绿，Server.Tests 213/213 含真实 PG 集成；capacity-target 独立 2.9s gatePassed）
- 摘要：按 gate-audit Skill 盘点全部门禁（verify.sh 逐步骤/独立脚本/frontend scripts/git hooks），逐条评估真实故障覆盖/重复/成本，输出删除/精简/保留三清单经用户确认执行：①capacity-target 移出合并必跑（由 `scripts/capacity-target.sh` 独立承载）；②verify.sh 自动供给 `daohua_market_test` 集成测试库并导出 `DAOHUA_MARKET_TEST_CONNECTION`（PG 可达→幂等建库→导出连接串；三分支明确提示）；修复 `capacity-target.sh` bash 3.2 `set -u` 下 `"${@}"` 空参 unbound 崩溃（该门禁在本机此前完全不可用）；同步 AGENTS.md §5、deployment.md、SKILL.md 枚举。移交发现：Ecs.Tests `DirectEnqueueUsesTheCommandCapacityAndRepeatRules` 低频 flake（1 败/后 3 次全过），疑似 mutation sink 派发与断言时序竞态。

### TASK-20261004-13 前端连接状态面板文案正经化
- 分类：前端体验 / 文案治理 / i18n
- 时间：提出 2026-10-04 17:16:00｜完成：无
- 状态：🚀 进行中（实施完成，build/契约全绿；待 UI 目检）
- 摘要：顶号测试发现断连全屏面板只显「仙途阻隔·未能连通」「感应天地法则…」古风装饰文本不透真实原因：`gameStore` 断连路径记录断连原因词条 key，`ConnectionErrorPage` 优先展示真实原因（顶号/会话失效/配额拒绝）；全量排查 i18n 中对玩法/信息误导的古风修辞一并修正（仙途阻隔/感应天地法则/道心唯一/灵台受扰/还魂中/神识探微/灵觉索敌/御风而行/气定神闲/已臻化境等 0 残留），template.csv 重生成。

### TASK-20261004-12 药品回复走 Buff 5 秒口径与聚灵阵盘转普通持续消耗品
- 分类：数值模型调整 / 消耗品机制 / 全栈
- 时间：提出 2026-10-04 17:00:00｜完成 2026-10-04
- 状态：✅ 已完成
- 摘要：①药品回复数值语义为「每 5 秒结算点回复量」：Buff 通道本就已实现（`consumable.buffs` 的 `hpRegen`/`mpRegen` 注入 `ActiveBuff`/`CombatBuffState`，战外 `CompleteResourceRegeneration`/战内 `CombatBuffTickSystem` 同口径 5 秒边界结算），唯一错误是前端「每秒回复」误标——修正文案。②聚灵阵盘由 `durable` 永驻部署改普通持续消耗品：入场自动消耗 1 件充入 300 秒（战斗时钟扣减），耗竭后进入 300 秒冷却才允许再次补能；持续消耗品冷却复用槽位 `CooldownUntilUnixSeconds`。验证：Domain 235/Ecs 62、build 0 错、DataTool strict、前端 tsc/build/契约全过（Server.Tests 56 失败为既有环境门禁）。

### TASK-20261004-11 移除顶栏右上角 Buff 显示列表
- 分类：前端体验 / 信息密度
- 时间：提出 2026-10-04 16:57:22｜完成 2026-10-04 17:02（前端 tsc/build 与契约全绿）
- 状态：✅ 已完成
- 摘要：删除 TopBar 中 `BuffBar` 组件（图标 chip+层数角标+悬浮卡+灵脉地点增益 chip）：顶栏 4 列改 3 列、`Icon` `'buff'` 字形、`.buff-*` 死样式（含浅色主题与媒体查询残留）、`app.buff` 词条分区与 template.csv 行全清；buff 仅保留详情展示（`CombatAttributePanel` 内视斗法 Tab/单位详情弹窗与战场 `ArenaActorBuffs` 不受影响）。

### TASK-20261004-10 反多开与反小号完整模块
- 分类：全栈新功能 / 账号安全 / 运维面板
- 时间：提出 2026-10-04 14:40:33｜完成 2026-10-04 17:19:48
- 状态：✅ 已完成（独立提交候选全门禁、HTTP/WS/浏览器验收通过）
- 摘要：同一自然人最多两个账号（可配置上限）、单设备最多注册一个账号；注册事务内执行设备/网络风险判定与服务端权威拒绝。风险信号：设备配置哈希、设备 ID、IP、网络特征与浏览器设备指纹（原始敏感值不回传运维页，持久化哈希/脱敏摘要+命中原因）。动态策略集中服务端配置/数据库（独立开关/阈值/处置动作），注册登录路径读取当前策略；前端仅采集必要信号+构建混淆（不宣称防调试）。运维面板新增「账号检测」页签（策略开关/风险命中统计/账号设备关联摘要/近期拒绝记录，写操作沿用运维令牌+二次确认）。新增 schema 由 `PostgresSchema.Sql` 直建、兼容 SQL 收 `migrations/`；风险事件独立 30 天保留期。验收：WS 无设备 Hello 4007、非法信号 4007 并审计、无 Hello 10s 关闭 1008；浏览器首注册 201、同设备二注册 400 明确提示、390px 无溢出。

### TASK-20261004-09 坊市装备强化等级盘口与系统挂单装备补齐
- 分类：全栈新功能 / 坊市 / 装备变体盘口
- 时间：提出 2026-10-04 14:00:00｜完成 2026-10-04
- 状态：✅ 已完成
- 摘要：①系统挂单无装备根因：种子脚本与 `/ops/api/market/catalog` 共用「非货币+可交易+sellPrice>0」口径，装备无回收价被排除——装备按锻造材料成本定价补挂。②装备点击进强化等级选择视图（MWI 式布局：每行一个强化等级，最佳出售价+购买按钮|最佳收购价+出售按钮|查看全部），只列有挂单等级、全无则只显 +0；按 "+N" 变体独立盘口买卖；`market.place` 变体校验与同级已装备拦截修正。③连带修复 `fetchMarketBook` 发 `variant={level}`（"3"）与服务端 `"+3"` 不符的潜伏 bug；`handshakeDeadline` 解除点前移至「收到合法 Hello 帧」（修快照构建超期触发 RequestClose 与 QueueJsonAsync 竞态 ChannelClosedException）；本地库 v9/v10 迁移文件 `UPDATE` 撞主键改 `INSERT ON CONFLICT` 约定并补 `account_risk_events` 列漂移。`daohua_market_test` 专用测试库由本次测试创建。

### TASK-20261004-08 全仓注释审计与补全（comment-audit Skill 首次全量执行）
- 分类：开发工作流 / 注释治理 / 全仓审计
- 时间：提出 2026-10-04 12:41:44｜完成 2026-10-04 14:40:00
- 状态：✅ 已完成（纯注释变更 0 逻辑改动；多 Agent 并行 Domain/Ecs×2/Server/前端）
- 摘要：CS1591 编译扫描盘点缺失后全仓补齐：新增 XML/JSDoc 注释 476 条、行间注释 36 条、修正低质注释 35 条；修复 CS1587/1573/1574 坏注释 82 处；`tools/` combat-simulator.html 英文注释全量中文化；tests/vendor 按规约跳过。src 全量带文档构建 0 警 0 错、测试门禁通过（42 个 Server.Tests 失败为既有 PG 环境门禁）。审计发现仅报告不改代码：疑似死代码/预留字段 5 处、冗余双检 1 处、combat-simulator 物理系 S2 复用 S1 参数疑似占位、ActionsEditor 文案过期、`h-13` 非 Tailwind 标准刻度。

### TASK-20261004-07（B）special-effects Skill 与 CombatTriggerMath 注释对齐现状
- 分类：开发工作流 / 技能治理 / 文档准确性
- 时间：提出 2026-10-04 12:15:40｜完成 2026-10-04
- 状态：✅ 已完成（纯注释/文档修改；编号与「门禁审计精简 Skill」冲突，独立详情文档被覆盖，本条目据主看板登记）
- 摘要：Skill 第 6 条「三个处理器」修正为四个（力微饭否为后增）；`CombatTriggerMath` 头注释「实体路径读组件快照」修正为两路径参数均实时读 `Stats.Effects`、标记组件仅为无参能力开关。

### TASK-20261004-07 门禁审计精简 Skill
- 分类：开发工作流 / 门禁治理 / Skill
- 时间：提出 2026-10-04 17:30:43｜完成 2026-10-04 17:32:00
- 状态：✅ 已完成（Skill 被环境识别，格式校验通过）
- 摘要：新增 `.agents/skills/gate-audit`：本仓库无 CI，「合并门禁」即交付/合并前必须通过的本地检查集合——verify.sh 各步骤、三套测试、前端契约检查、DataTool strict、Compose 校验、容量门禁、烟测及 AGENTS.md 流程性要求；技能要求逐条评估「防什么真实故障、是否重复、成本多少」，删除或精简不必要门禁并同步门禁文档；删除清单必须经用户确认；数据安全/鉴权/备份/容量/生产部署类门禁默认保留。后续首次真实执行见 TASK-20261004-14。

### TASK-20261004-06（B）任务面板深度归档（8 月/9 月档）
- 分类：文档治理 / 归档 / 任务面板
- 时间：提出 2026-10-04 12:14:26｜完成 2026-10-04
- 状态：✅ 已完成（190 项并入 8 月档 73+9 月档 117；外部 12 处引用改指归档；全仓断链检查 0 残留；编号与「注释检查分析 Skill」冲突，独立详情文档被覆盖，本条目据主看板与归档文件登记）
- 摘要：2026-10-01 及更早全部任务详情文档（190 份，含进行中停滞任务）按月深度合并为 `archive-2026-08.md`（73 项）与 `archive-2026-09.md`（117 项）两个归档文件并删除原文件；条目含编号/名称/分类/时间/状态/摘要，完整原文可从 Git 历史找回；主看板历史行收拢为归档索引；外部 12 处 `tasks/TASK-*.md` 引用改指归档文件；全仓断链检查 0 残留。

### TASK-20261004-06 注释检查分析 Skill
- 分类：开发工作流 / 注释治理 / Skill
- 时间：提出 2026-10-04 12:37:49｜完成 2026-10-04 12:39:00
- 状态：✅ 已完成（Skill 被环境识别，格式校验通过）
- 摘要：新增 `.agents/skills/comment-audit`：全局注释检查、分析与生成——覆盖全仓（可指定范围收窄），所有注释维持中等详细度中文口径（说清「做什么+为什么+关键约束」）；C# 用 `///` XML 注释、前端 TS 用 JSDoc 同标准；为无法一眼看出作用的方法内实现（魔法数/顺序依赖/确定性约束/绕行方案）补 `// 中文` 行间注释解释「为什么」；只改注释不改代码逻辑，注释与行为矛盾时以代码为准；英文注释中文化而非重写。默认扫描顺序 src/Domain→Ecs→Server→Protocol→ComputeNode→frontend/src→tools；tests/ 只补复杂段行间注释；vendor 永不修改。配套 `agents/openai.yaml`。首次全量执行见 TASK-20261004-08。

### TASK-20261004-05 项目技能全面中文化
- 分类：开发工作流 / 技能治理 / 中文化
- 时间：提出 2026-10-04 12:20:00｜完成 2026-10-04 12:26:00
- 状态：✅ 已完成
- 摘要：`.agents/skills/` 下 4 个技能全面中文化：frontmatter 描述、openai.yaml 元数据、planar-art 两份 references 与 AI 图像生成提示词模板、data-designer 残留英文注释全部改中文（占位符/代码标识符保留英文）；special-effects 已全中文无需改动。验证：连续 3 个以上英文单词正则全量扫描（排除代码标识符/路径/占位符白名单）0 残留；frontmatter YAML 结构未变。

### TASK-20261004-04 Git 原子化提交 Skill
- 分类：开发工作流 / Git 提交 / Skill
- 时间：提出 2026-10-04 12:10:12｜完成 2026-10-04 12:12:00
- 状态：✅ 已完成（Skill 被环境识别，格式校验通过）
- 摘要：新增 `.agents/skills/git-atomic-commit`：把工作区全部改动按逻辑单元拆成原子提交——单个文件的全部改动只能进入一个提交（禁止 `git add -p` 式 hunk 级拆分，原子性最小粒度=文件）；提交说明使用与仓库历史一致的中文 Conventional Commits（`type(scope): 中文描述`）。

### TASK-20261004-03 历练页分组重组与行动地点黑名单（excludedNodeIds）
- 分类：前端体验重组 / 行动地点约束 / 全栈
- 时间：提出 2026-10-04 02:20:00｜完成 2026-10-04 10:15（全门禁通过；待 UI 目检）
- 状态：✅ 已完成
- 摘要：①撤「修炼」页签，修炼行动并入历练页为常驻组；探索行动按技艺分组（组名=技艺名+当前等级）；讨伐组更名「讨伐」。②行动新增 `excludedNodeIds` 黑名单（与 `availableNodeIds` 互斥）：引气入体默认全图可行、需禁地点才配黑名单。总纲 §8.5.1 补「历练页行动分组与地点约束」规约、`game-data.md` 校验口径同步。验证：build 0 警 0 错、Domain 233（AutoTravel 5/5 含新用例）/Ecs 62、DataTool strict（168 ID）、前端 tsc+build+契约全过。

### TASK-20261004-02 移除强化护符：强化失败保护改为消耗保护材料
- 分类：数值模型调整 / 强化规则 / 全栈
- 时间：提出 2026-10-04 02:00:00｜完成 2026-10-04 02:10（全门禁通过）
- 状态：✅ 已完成
- 摘要：删除 `item.enhance-charm`：强化失败保护改消耗材料——装备配 `enhanceProtectionItems` 列表则从列表任选其一，未配置则用同装备无强化基础件（+0 以自身裸件代护时须持有 ≥2），每轮强化消耗 1 个。`EnhancementRules`：`Preparation.UsesCharm` 改携带 `ProtectionItemId`、`Resolve` 读 `enhanceProtectionItems` 数组校验 auxItemId；ECS 结算扣件改 `prep.ProtectionItemId`；装备 wire DTO 增 `enhanceProtectionItems` 透传。验证：Domain 226（+1 新用例）/Ecs 56、EnhancementContractTests 5/5、DataTool strict（169 ID −1）、前端 tsc+build+契约全过。

### TASK-20261004-01 内视页头部移除洞天与血蓝展示
- 分类：前端体验 / 内视页信息密度
- 时间：提出 2026-10-04 09:50:47｜完成：无
- 状态：🧪 验证中（前端 tsc/build 全绿；待 UI 目检）
- 摘要：`InsightPage` 删除「所在洞天」行与「生命值/灵力值」快照区，头部只保留道号+境界徽章与突破修为进度（洞天信息看世界地图、血蓝当前值战斗页实时可见）；同步清理死词条/死样式/媒体查询引用；「层境突破修为」标签精简为「修为」。过程勘误：首轮误解为「加到全局顶栏」完成实现后，用户澄清为内视页内移除——TopbarVitals 组件/词条/样式全部还原撤销。

### TASK-20261003-14 特质系统首批落地：看不到我 + 力微饭否，敌方出手目标接通威胁轮盘
- 分类：战斗特殊效果 / 领域模型 / 特质系统奠基 / schema v9
- 时间：提出 2026-10-03 23:04:24｜完成：无
- 状态：🚀 进行中（实现完成验证通过；归档时未提交收口）
- 摘要：特质系统三分流（纯属性增益/常驻修饰/条件触发）首批落地两个特质：`trait.bukandaowo`（看不到我：开战起持续 10 秒威胁始终为 0，按用户裁定以开战 buff 实现）；`trait.liweifanfou`（力微饭否：常规威胁「1 伤害→1 威胁、1 治疗→3 威胁」之外，玩家受到伤害时按减免前原始伤害等额增加自身威胁，另类嘲讽）。玩家特质字段全链路+敌方出手目标接通威胁轮盘；schema v9。实现修正：初版 handler 以 `AttackPower×BasicAttackRatio` 重算原始伤害与结算口径脱节——改 `ResolveHit` 写入 `CombatHitContext.RawDamage`、handler 读字段消除公式漂移风险。验证：Domain/Ecs Debug+Release 全绿、FrontendContractTests（含 v9 对齐）与 DataTool strict 通过。

### TASK-20261003-13 特殊效果实现 Skill
- 分类：开发工作流 / 战斗效果 / Skill
- 时间：提出 2026-10-03 22:21:14｜完成 2026-10-03 22:33:30
- 状态：✅ 已完成（纯文档未提交）
- 摘要：新增 `.agents/skills/special-effects`：统一装备被动、技能附加效果、Buff/Debuff、怪物特性与命中/施法/击杀/死亡/到期反应及持续伤害/控制/护盾/汲取/反伤等机制的新增、修改、修复与评审规约；纯视觉特效归美术/前端、纯数值配平归 `data-designer`，机制+数值同变时协作。沿用已落地轻量继承反射流程（启动收集、固定事件接口、热路径缓存数组），明确 ECS Tag 实体路径与内联热路径两条派发路径共用效果本体与等价验证；总纲 18 登记工作流入口。

### TASK-20261003-12 轻量战斗触发器反射处理流程
- 分类：服务端架构 / ECS 触发器 / 确定性派发
- 时间：提出 2026-10-03 20:25:51｜完成 2026-10-03（Ecs 58/58、Domain 232/232、Release 构建通过）
- 状态：✅ 已完成
- 摘要：保留 YuoCore ECS Tag System 实体派发路径前提下，为无实体战斗路径增加轻量触发器处理流程：处理器继承基类+实现固定事件接口自动被反射收集，启动时完成实例化与排序，战斗热路径只执行缓存后的接口数组调用（不继承 `ISystemTag`、不创建 YuoSystem/组件/实体信箱、不用 `MethodInfo.Invoke`）；事件接口按触发器种类固定方法参数，新增同类效果只新增处理器类不改派发器；处理器只接收 `PendingCombatTrigger` 等领域上下文、不持有 `YuoEntity`/`YuoWorld`。

### TASK-20261003-11 移除强化精华（物品/市场数据/全部逻辑）
- 分类：数值模型调整 / 物品与市场清理 / 全栈
- 时间：提出 2026-10-03 18:42:10｜完成 2026-10-03 18:55:00
- 状态：✅ 已完成
- 摘要：强化精华（`item.enhance-essence`）整链移除，强化成本只剩灵石 `t×10`：总纲 §20.4.2 成本公式同步（index.html 重生成）；配表 `materials.json` 删条目、`enhancement.json` 描述去「强化精华」；领域 `EnhancementRules` 删 `EssenceItemId`/`EssenceCostFor`/`EssenceCost` 与余额检查；ECS 结算删精华扣减与 ItemLosses；前端预览/i18n/ids/template.csv 清理；本地库删系统卖单 1 张、流水 2 行、流水头 1 条、冻结 1 条（无玩家余额）。验证：Release 0 警 0 错、Domain 232/Ecs 56、EnhancementContractTests 5/5、DataTool strict（168 ID）、前端 build（Server.Tests 环境性失败与本次无关）。

### TASK-20261003-10 运维面板市场快捷操作：系统身份挂售/收购下单与撤单
- 分类：开发工具链 / 全栈新功能 / 运维面板 / 坊市
- 时间：提出 2026-10-03 18:39:40｜完成：无
- 状态：🧪 验证中（Ops 测试 34/34、端到端冒烟全通；待 UI 目检）
- 摘要：运维面板新增市场快捷操作：以系统身份（TASK-20261003-09 同一固定 UUID 身份「系统坊市」，不可登录）挂单/收购下单——范围支持单个物品/按品类/全部物品、默认回收价数量可改；系统挂单列表与逐条/一键撤销；走 `MarketRuntime` 即时上架免重启。端到端冒烟：无令牌 401、挂单即时上架（伴灵花 100 份 @15）、按品种撤销、以种子同参数补回 1 亿份 @7.5；修复自定义幂等键 `market-cancel:{playerId}:{orderId}` 被 `CloseOrderAsync` 拒绝 500。验证：Server Release 零告警、Ops 测试 34/34（新增 4 项边界）、前端 tsc/build/契约全过。

### TASK-20261003-09 本地库坊市系统挂售种子：系统身份全可回收物品 1 亿份半价卖单
- 分类：本地开发工具 / 数据种子 / 坊市
- 时间：提出 2026-10-03 16:57:36｜完成 2026-10-03 18:41
- 状态：✅ 已完成（54 品种半价货架生效；旧官商 2× 货架经用户裁定硬清理）
- 摘要：本地库新建不可登录系统账号+玩家（固定 UUID 幂等）作坊市货架卖方，为 `sellPrice>0` 的非货币物品（54 个；灵石为货币排除、装备无回收价排除）挂 1 亿份卖单、价格=回收价一半；新增 `scripts/seed-market-system-listings.sh` 幂等种子脚本；旧「官商」2× 货架经用户裁定硬清理。最终核验：活跃卖单 54/54 全为系统半价单（价格 2.5~125、数量 1 亿）、盘口每品种单一半价卖档、旧官商单零残留。种子直写 SQL 落库、盘口为内存投影须重启后端生效；顺带发现 dev-hot 拉起 `setTimeout().unref()` 事件循环排空缺陷（后于 TASK-20261009-08 修复）。

### TASK-20261003-08 行动队列展开面板底部操作区：暂停/继续 与 移除全部
- 分类：全栈新功能 / 领域模型 / 协议扩展 / 前端体验
- 时间：提出 2026-10-03 16:10:00｜完成 2026-10-03（待 UI 目检）
- 状态：✅ 已完成
- 摘要：顶栏 QueueCenter 展开下拉底部新增整排操作区：「暂停/继续」队列级开关（`queuePaused` 服务端权威、免确认、暂停徽标与进度冻结）；「移除全部」一键清空（`queue.clear` 命令、ConfirmDialog 二次确认 danger）；schema v8。验证：Domain/Ecs 56/56、Server.Tests 新增 `QueuePauseClearCommandTests` 3/3（paused 校验三键规则、QUEUE_EMPTY、清空回执快照+ActionQueue 触及段）、前端 tsc+build+契约全过。

### TASK-20261003-07 技艺界面优化：导航技艺等级前缀 + 内视技艺 Tooltip 精简
- 分类：前端体验 / 技艺展示
- 时间：提出 2026-10-03 15:50:22｜完成：无
- 状态：🧪 验证中（前端 tsc/build 通过、template.csv 重导出；待 UI 目检）
- 摘要：左侧导航技艺子菜单项名称前显示对应技艺等级数字（先 Lv 前缀后按反馈改纯数字）+ 底边分色细经验条（复用 pill-tone 分色，随 combat 快照实时刷新），hover 气泡显示等级/本级经验/行动速度加成；内视技艺 Tab 七技艺 hover 提示精简为仅开头两句诗意描述（移除「共用同一曲线」机制说明）。

### TASK-20261003-06 同道（组队）系统：队伍档案、招募匹配、组队大厅与聊天队伍链接
- 分类：全栈新功能 / 社交系统 / 协议扩展 / 持久化（schema v7）/ 前端体验
- 时间：提出 2026-10-03 14:13:44｜完成 2026-10-03
- 状态：✅ 已完成
- 摘要：参考 MWI 组队体验把「邀请制基础队伍」升级为完整组队系统（设计真源总纲 §21）：左侧新增顶级 Tab「同道」+「队伍」子页；无队伍时 4 入口（自主选择模式/队伍匹配/组队大厅/创建队伍）；队伍档案（名称/目标/最低身家门槛/公开性）；队伍匹配多下拉过滤+自动并入或散人池；组队大厅公开队伍+匹配中散人列表可自行加入；已在队成员视图+队长管理（队名/目标/门槛/公开性/踢人/解散）；聊天队伍超链接；身家（估值）服务端口径。schema v7+`migrations/0006`（dry-run 对账通过）；Domain 226/Ecs 56、前端 tsc+build+契约全过；设计文档 21 篇新立+14/19 篇与 README/index.html 联动。

### TASK-20261003-05 大世界地图区域化显示
- 分类：前端体验 / 世界地图 / 配表坐标
- 时间：提出 2026-10-03 14:13:44｜完成：无
- 状态：🧪 验证中（前端 build/契约/DataTool strict 全过；catalog 实测已下发新坐标；待 UI 目检）
- 摘要：全图共用单一世界坐标系（星陨异境平移至青云仙山东侧）；地图默认只渲染玩家所在区域；驻留关口节点（青云山门 `node.cloud-gate`，跨域边由云巅草庐改挂山门→裂天界隙 `edge.gate-rift`）时并显邻接区域；区域界框/名牌/跨域虚线可视化；世界图连通性保持（gate-rift 维持星陨可达）。实测 `GET /api/v1/catalog/maps` 已下发新坐标。

### TASK-20261003-04 技艺等级体系、符箓/阵法制造与强化系统
- 分类：新功能 / 数值体系 / 领域模型 / 前后端联动
- 时间：提出 2026-10-03 12:00:00｜完成 2026-10-03 14:00:00（全门禁非库全绿；待 UI 目检）
- 状态：🧪 验证中（四 Agent 并行+主线集成）
- 摘要：大功能集一次落地：`TechniqueCatalog`（七技艺、修为曲线 0 级起、+5%/级速度）、`EnhancementRules`（成功率表/指数衰减/技艺修正/确定性 Roll 独立通道）、`PlayerLoadoutState.TechniqueExperiences`+`ActionIntent.TargetItemId/AuxItemId`、技艺经验按轮入账；雷击符 `strike` 打击链（复用 `ResolveHit` 减伤语义 `ResolveStrikeDamage`）、聚灵阵 `durable` 消耗品+`SyncDurableConsumableBuffs` 注入/清除、后段回复乘区 `HpRegenPostRatio/MpRegenPostRatio` 战内外同构收口；协议 `targetItemId`/`auxItemId`/loadout wire `techniqueExperiences`、装备持有校验改 `AssetKey("+N")` 变体键；schema v5→v6+`migrations/0005`；配表补 `technique` 字段+新增 talismans/formations/enhancement 行动与新物品；前端内视技艺 Tab、技艺导航增符箓/阵法/强化子页、`EnhancementPage` 独立强化界面（物品/保护材料选择器、费用/成功率/耗时预览）、`ItemGridPicker` 变体复合键、AutoUse 增 enhancement 域。

### TASK-20261003-03 破境突破独立确认弹窗
- 分类：前端体验 / 交互防误触
- 时间：提出 2026-10-03 11:40:46｜完成：无
- 状态：🧪 验证中（前端 build 通过；待 UI 目检）
- 摘要：突破按钮点击先弹独立确认弹窗（防误触直接发送 `progression.breakthrough`）；弹窗内展示境界跃迁与基础属性逐项「当前值 → +增量 → 突破后值」对比，预览计算复用前端 `calculateBaseAttributes` 锚点分段线性插值（与服务端 `PlayerAggregate.Breakthrough` 突破后本层修为清零、境界 +1 语义对齐）。

### TASK-20261003-02 内视页移除玩家形象展示
- 分类：前端体验
- 时间：提出 2026-10-03 10:05:00｜完成：无
- 状态：🧪 验证中（前端 build 通过；待 UI 目检）
- 摘要：`InsightPage` 删除 `insight-avatar-stage` JSX 块（CharacterAvatar 玩家立绘）与仅服务于形象的 `playerId`/`visual` store 订阅和 import；styles.css 删除 `.insight-avatar-stage` 全套样式（约 60 行）与 600px 窄屏竖排规则；theme.css 清理浅色主题三处引用。

### TASK-20261003-01 战斗页生活行动总目标卡紧凑化
- 分类：前端体验
- 时间：提出 2026-10-03 09:11:28｜完成：无
- 状态：🧪 验证中（仅尺寸紧凑化、信息构成保留；build 通过，待 UI 目检）
- 摘要：战斗页右侧生活行动总目标卡（`ArenaLifeGoalCard`，修炼/采集/炼丹时替换敌方九宫格）收拢尺寸：宽度 clamp(180px,26vw,290px) 收窄、去强制 200px 最小高度、56px 图标缩 42px、glyph 26→19、meta 区 padding/空闲卡同规格、竖屏 max-width 同步收拢（生活总目标/赶路/空闲三卡同规格）。过程波折：曾做一版「信息精简」（砍大图标、meta 三行合一）被用户要求撤回——最终方案仅尺寸紧凑化，信息构成（大图标+三行 meta）保留原样。

### TASK-20261003-01（B）浅色主题字重提升
- 分类：前端体验 / 显示主题
- 时间：提出 2026-10-03 08:36:20｜完成 2026-10-03 08:58:54
- 状态：✅ 已完成（编号与「战斗页生活行动总目标卡紧凑化」冲突，独立详情文档被覆盖，本条目据主看板登记）
- 摘要：浅色主题正文/辅助文字/表单控件与原 400/500 字重标签标题统一提升至 600，原更粗字重保留，深色沿用原字重。验证：前端 build 通过；CDP 断言浅色样本 600/深色回原值、正式前端未登录页实测一致。

### TASK-20261002-02 顶栏太极开关交互修正：静止常态、点击 180° 翻转、盘底偏差色
- 分类：前端体验
- 时间：提出 2026-10-02 23:37:11｜完成：无
- 状态：🧪 验证中（前端 build 通过；待 UI 目检）
- 摘要：对 TASK-20261001-09（已归档 9 月档）太极主题开关修正：太极图取消常态自转仅点击翻转 180°；盘底增加与阴阳双色有偏差的中间色（阳盘 `#e7dcba` 略沉于纯阳、深色盘底 `#252017` 略浮于纯阴）避免双鱼融底；二轮反馈后改对称正置双鱼太极（鱼眼取对色），主题仅靠整体 180° 翻转表达（浅阳上/深阴上）。设计真源 §13.1.1 先行修订；`.taiji-icon` 删 `taiji-drift` 动画，点击旋转增量 540°→180°。

### TASK-20261002-01 行动队列展开下拉精简：图标+名称+次数、宽度收拢 60%
- 分类：前端体验
- 时间：提出 2026-10-02 23:36:00｜完成：无
- 状态：🧪 验证中（tsc/build/契约全过；待 UI 目检）
- 摘要：顶栏行动队列展开下拉条目仅留「行动图标+行动名称+剩余次数」与下移/上移/移除操作按钮：移除序号列、行动描述行与进行中/资源不足/斗法状态徽章（阻塞态仍由红边框、首条仍由 .active 高亮表达）；下拉由全宽改 `width:60%` 右缘对齐展开按钮；`.queue-name` 直接承担 flex:1 省略截断；清理 `.queue-order`/`.queue-main`/`.queue-title`/`.queue-sub` 死样式与 ≤960px 隐藏覆盖（图标已成为关键信息不再隐藏）。
