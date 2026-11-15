import { parseLogTemplate } from '../logging/gameLogService'
import type { TranslationKey, TFunction } from '../i18n'
import type { HelpJumpTarget } from '../uiStore'

/** 帮助章标识 */
export type HelpChapterId = 'intro' | 'power' | 'traits' | 'craft' | 'world'

/** 单节帮助条目元数据（文案全数收敛于 i18n，供静态检查与多语言模板同步提取） */
export interface HelpSectionDef {
  id: string
  chapterId: HelpChapterId
  titleKey: TranslationKey
  tagKey: TranslationKey
  bodyKey: TranslationKey
  tipKey: TranslationKey
  jumpLabelKey: TranslationKey
  jumpTarget: HelpJumpTarget
}

/** 单章帮助目录定义 */
export interface HelpChapterDef {
  id: HelpChapterId
  titleKey: TranslationKey
  badgeKey: TranslationKey
  summaryKey: TranslationKey
  sections: readonly HelpSectionDef[]
}

/** 帮助章节目录（5 章 14 节，共创文本教程定稿结构） */
export const HELP_CHAPTERS: readonly HelpChapterDef[] = [
  {
    id: 'intro',
    titleKey: 'app.help.chapters.intro.title',
    badgeKey: 'app.help.chapters.intro.badge',
    summaryKey: 'app.help.chapters.intro.summary',
    sections: [
      {
        id: 'intro-overview',
        chapterId: 'intro',
        titleKey: 'app.help.chapters.intro.sections.overview.title',
        tagKey: 'app.help.chapters.intro.sections.overview.tag',
        bodyKey: 'app.help.chapters.intro.sections.overview.body',
        tipKey: 'app.help.chapters.intro.sections.overview.tip',
        jumpLabelKey: 'app.help.chapters.intro.sections.overview.jumpLabel',
        jumpTarget: { tab: 'exploration' },
      },
      {
        id: 'intro-early-route',
        chapterId: 'intro',
        titleKey: 'app.help.chapters.intro.sections.earlyRoute.title',
        tagKey: 'app.help.chapters.intro.sections.earlyRoute.tag',
        bodyKey: 'app.help.chapters.intro.sections.earlyRoute.body',
        tipKey: 'app.help.chapters.intro.sections.earlyRoute.tip',
        jumpLabelKey: 'app.help.chapters.intro.sections.earlyRoute.jumpLabel',
        jumpTarget: { tab: 'exploration' },
      },
      {
        id: 'intro-queue-efficiency',
        chapterId: 'intro',
        titleKey: 'app.help.chapters.intro.sections.queueEfficiency.title',
        tagKey: 'app.help.chapters.intro.sections.queueEfficiency.tag',
        bodyKey: 'app.help.chapters.intro.sections.queueEfficiency.body',
        tipKey: 'app.help.chapters.intro.sections.queueEfficiency.tip',
        jumpLabelKey: 'app.help.chapters.intro.sections.queueEfficiency.jumpLabel',
        jumpTarget: { tab: 'exploration' },
      },
    ],
  },
  {
    id: 'power',
    titleKey: 'app.help.chapters.power.title',
    badgeKey: 'app.help.chapters.power.badge',
    summaryKey: 'app.help.chapters.power.summary',
    sections: [
      {
        id: 'power-realm-gate',
        chapterId: 'power',
        titleKey: 'app.help.chapters.power.sections.realmAndGate.title',
        tagKey: 'app.help.chapters.power.sections.realmAndGate.tag',
        bodyKey: 'app.help.chapters.power.sections.realmAndGate.body',
        tipKey: 'app.help.chapters.power.sections.realmAndGate.tip',
        jumpLabelKey: 'app.help.chapters.power.sections.realmAndGate.jumpLabel',
        jumpTarget: { tab: 'insight' },
      },
      {
        id: 'power-stats-skills',
        chapterId: 'power',
        titleKey: 'app.help.chapters.power.sections.statsAndSkills.title',
        tagKey: 'app.help.chapters.power.sections.statsAndSkills.tag',
        bodyKey: 'app.help.chapters.power.sections.statsAndSkills.body',
        tipKey: 'app.help.chapters.power.sections.statsAndSkills.tip',
        jumpLabelKey: 'app.help.chapters.power.sections.statsAndSkills.jumpLabel',
        jumpTarget: { tab: 'combat' },
      },
      {
        id: 'power-gear-enhance',
        chapterId: 'power',
        titleKey: 'app.help.chapters.power.sections.gearAndEnhance.title',
        tagKey: 'app.help.chapters.power.sections.gearAndEnhance.tag',
        bodyKey: 'app.help.chapters.power.sections.gearAndEnhance.body',
        tipKey: 'app.help.chapters.power.sections.gearAndEnhance.tip',
        jumpLabelKey: 'app.help.chapters.power.sections.gearAndEnhance.jumpLabel',
        jumpTarget: { tab: 'crafting', craftingMode: 'enhancement', enhancementSubTab: 'enhance' },
      },
    ],
  },
  {
    id: 'traits',
    titleKey: 'app.help.chapters.traits.title',
    badgeKey: 'app.help.chapters.traits.badge',
    summaryKey: 'app.help.chapters.traits.summary',
    sections: [
      {
        id: 'traits-awakening-shop',
        chapterId: 'traits',
        titleKey: 'app.help.chapters.traits.sections.awakeningAndShop.title',
        tagKey: 'app.help.chapters.traits.sections.awakeningAndShop.tag',
        bodyKey: 'app.help.chapters.traits.sections.awakeningAndShop.body',
        tipKey: 'app.help.chapters.traits.sections.awakeningAndShop.tip',
        jumpLabelKey: 'app.help.chapters.traits.sections.awakeningAndShop.jumpLabel',
        jumpTarget: { tab: 'shop' },
      },
      {
        id: 'traits-builds',
        chapterId: 'traits',
        titleKey: 'app.help.chapters.traits.sections.traitBuilds.title',
        tagKey: 'app.help.chapters.traits.sections.traitBuilds.tag',
        bodyKey: 'app.help.chapters.traits.sections.traitBuilds.body',
        tipKey: 'app.help.chapters.traits.sections.traitBuilds.tip',
        jumpLabelKey: 'app.help.chapters.traits.sections.traitBuilds.jumpLabel',
        jumpTarget: { tab: 'insight' },
      },
    ],
  },
  {
    id: 'craft',
    titleKey: 'app.help.chapters.craft.title',
    badgeKey: 'app.help.chapters.craft.badge',
    summaryKey: 'app.help.chapters.craft.summary',
    sections: [
      {
        id: 'craft-consumables',
        chapterId: 'craft',
        titleKey: 'app.help.chapters.craft.sections.consumables.title',
        tagKey: 'app.help.chapters.craft.sections.consumables.tag',
        bodyKey: 'app.help.chapters.craft.sections.consumables.body',
        tipKey: 'app.help.chapters.craft.sections.consumables.tip',
        jumpLabelKey: 'app.help.chapters.craft.sections.consumables.jumpLabel',
        jumpTarget: { tab: 'combat' },
      },
      {
        id: 'craft-liquefaction',
        chapterId: 'craft',
        titleKey: 'app.help.chapters.craft.sections.liquefaction.title',
        tagKey: 'app.help.chapters.craft.sections.liquefaction.tag',
        bodyKey: 'app.help.chapters.craft.sections.liquefaction.body',
        tipKey: 'app.help.chapters.craft.sections.liquefaction.tip',
        jumpLabelKey: 'app.help.chapters.craft.sections.liquefaction.jumpLabel',
        jumpTarget: { tab: 'crafting', craftingMode: 'enhancement', enhancementSubTab: 'liquefy' },
      },
      {
        id: 'craft-foundation',
        chapterId: 'craft',
        titleKey: 'app.help.chapters.craft.sections.foundation.title',
        tagKey: 'app.help.chapters.craft.sections.foundation.tag',
        bodyKey: 'app.help.chapters.craft.sections.foundation.body',
        tipKey: 'app.help.chapters.craft.sections.foundation.tip',
        jumpLabelKey: 'app.help.chapters.craft.sections.foundation.jumpLabel',
        jumpTarget: { tab: 'exploration' },
      },
    ],
  },
  {
    id: 'world',
    titleKey: 'app.help.chapters.world.title',
    badgeKey: 'app.help.chapters.world.badge',
    summaryKey: 'app.help.chapters.world.summary',
    sections: [
      {
        id: 'world-map-pvp',
        chapterId: 'world',
        titleKey: 'app.help.chapters.world.sections.mapAndPvp.title',
        tagKey: 'app.help.chapters.world.sections.mapAndPvp.tag',
        bodyKey: 'app.help.chapters.world.sections.mapAndPvp.body',
        tipKey: 'app.help.chapters.world.sections.mapAndPvp.tip',
        jumpLabelKey: 'app.help.chapters.world.sections.mapAndPvp.jumpLabel',
        jumpTarget: { tab: 'map', mapSubTab: 'map' },
      },
      {
        id: 'world-buildings',
        chapterId: 'world',
        titleKey: 'app.help.chapters.world.sections.buildings.title',
        tagKey: 'app.help.chapters.world.sections.buildings.tag',
        bodyKey: 'app.help.chapters.world.sections.buildings.body',
        tipKey: 'app.help.chapters.world.sections.buildings.tip',
        jumpLabelKey: 'app.help.chapters.world.sections.buildings.jumpLabel',
        jumpTarget: { tab: 'map', mapSubTab: 'buildings' },
      },
      {
        id: 'world-party',
        chapterId: 'world',
        titleKey: 'app.help.chapters.world.sections.party.title',
        tagKey: 'app.help.chapters.world.sections.party.tag',
        bodyKey: 'app.help.chapters.world.sections.party.body',
        tipKey: 'app.help.chapters.world.sections.party.tip',
        jumpLabelKey: 'app.help.chapters.world.sections.party.jumpLabel',
        jumpTarget: { tab: 'social', socialSubTab: 'party' },
      },
    ],
  },
]

/** 帮助全手册总篇数 */
export const HELP_TOTAL_SECTIONS: number = HELP_CHAPTERS.reduce((sum, chapter) => sum + chapter.sections.length, 0)

/** 将含 `{{kind:id|label}}` 富文本标记的模板还原为可读纯文本，避免内部前缀 ID 污染关键词检索。 */
export function stripRichMarkup(template: string): string {
  return parseLogTemplate(template)
    .map(segment => (segment.type === 'text' ? segment.value : segment.label))
    .join('')
}

/** 按关键词过滤帮助章节与下属小节；支持空格分词 AND 匹配（大小写不敏感）。 */
export function filterHelpChapters(
  chapters: readonly HelpChapterDef[],
  rawQuery: string,
  t: TFunction,
): readonly HelpChapterDef[] {
  const tokens = rawQuery
    .trim()
    .toLowerCase()
    .split(/\s+/)
    .filter(Boolean)
  if (tokens.length === 0) return chapters

  const result: HelpChapterDef[] = []
  for (const chapter of chapters) {
    const chapterText = `${t(chapter.titleKey)} ${t(chapter.summaryKey)}`.toLowerCase()
    const matchedSections = chapter.sections.filter(section => {
      const haystack = [
        chapterText,
        t(section.titleKey),
        t(section.tagKey),
        stripRichMarkup(t(section.bodyKey)),
        stripRichMarkup(t(section.tipKey)),
      ]
        .join('\n')
        .toLowerCase()
      return tokens.every(token => haystack.includes(token))
    })
    if (matchedSections.length > 0) {
      result.push({ ...chapter, sections: matchedSections })
    }
  }
  return result
}
