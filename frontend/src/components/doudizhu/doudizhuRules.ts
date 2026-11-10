/** 客户端仅为选牌与提示判型，出牌和结算仍由服务端裁决。 */
export type PatternKind = 'single' | 'pair' | 'triple' | 'tripleSingle' | 'triplePair' | 'straight' | 'pairRun' | 'plane' | 'planeSingle' | 'planePair' | 'fourSingle' | 'fourPair' | 'bomb' | 'rocket'
export interface Pattern { kind: PatternKind; primary: number; count: number; length: number }
export interface Move { cards: number[]; pattern: Pattern }
export const rank = (card: number): number => card < 52 ? Math.floor(card / 4) + 3 : card - 36
export const sortCards = (cards: readonly number[]): number[] => [...cards].sort((a, b) => rank(b) - rank(a) || a - b)
  function groups(cards: readonly number[]): Map<number, number[]> {
    const result = new Map<number, number[]>();
    for (const card of cards) { const r = rank(card); if (!result.has(r)) result.set(r, []); result.get(r)!.push(card); }
    return new Map([...result].sort(([a], [b]) => a - b));
  }
  function pattern(kind: PatternKind, primary: number, count: number, length = 1): Pattern { return { kind, primary, count, length }; }
  /** 牌型与比较独立于表现，规则覆盖普通牌、连续牌、飞机及炸弹。 */
  export function evaluate(cards: readonly number[]): Pattern | null {
    const n = cards.length;
    if (n < 1 || n > 20 || new Set(cards).size !== n) return null;
    const g = groups(cards), rs = [...g.keys()], counts = rs.map(r => g.get(r)!.length);
    const high = rs[rs.length - 1];
    if (n === 1) return pattern('single', high, n);
    if (n === 2 && g.size === 1) return pattern('pair', high, n);
    if (n === 2 && g.has(16) && g.has(17)) return pattern('rocket', 17, n);
    if (n === 3 && g.size === 1) return pattern('triple', high, n);
    if (n === 4 && g.size === 1) return pattern('bomb', high, n);
    const triple = rs.find(r => g.get(r)!.length === 3);
    if (n === 4 && g.size === 2 && triple !== undefined) return pattern('tripleSingle', triple, n);
    if (n === 5 && g.size === 2 && triple !== undefined && counts.includes(2)) return pattern('triplePair', triple, n);
    const consecutive = rs.every((r, i) => !i || r === rs[i - 1] + 1) && high <= 14;
    if (n >= 5 && counts.every(c => c === 1) && consecutive) return pattern('straight', high, n, n);
    if (n >= 6 && counts.every(c => c === 2) && consecutive) return pattern('pairRun', high, n, n / 2);
    for (let size = 2; size <= 6; size++) {
      if (![size * 3, size * 4, size * 5].includes(n)) continue;
      for (let start = 3; start + size - 1 <= 14; start++) {
        const core = Array.from({ length: size }, (_, i) => start + i);
        if (!core.every(r => (g.get(r)?.length || 0) >= 3)) continue;
        const remainder = rs.map(r => [r, g.get(r)!.length - (core.includes(r) ? 3 : 0)]).filter(([, c]) => c > 0);
        if (!remainder.length && n === size * 3) return pattern('plane', start + size - 1, n, size);
        if (n === size * 4 && remainder.length === size && remainder.every(([, c]) => c === 1)) return pattern('planeSingle', start + size - 1, n, size);
        if (n === size * 5 && remainder.length === size && remainder.every(([, c]) => c === 2)) return pattern('planePair', start + size - 1, n, size);
      }
    }
    const four = rs.find(r => g.get(r)!.length === 4);
    if (four !== undefined) {
      const wings = rs.filter(r => r !== four);
      if (n === 6 && wings.every(r => g.get(r)!.length <= 2)) return pattern('fourSingle', four, n);
      if (n === 8 && wings.length === 2 && wings.every(r => g.get(r)!.length === 2)) return pattern('fourPair', four, n);
    }
    return null;
  }
  export function beats(candidate: Pattern, target: Pattern | null): boolean {
    if (!target) return true;
    if (candidate.kind === 'rocket') return target.kind !== 'rocket';
    if (target.kind === 'rocket') return false;
    if (candidate.kind === 'bomb') return target.kind !== 'bomb' || candidate.primary > target.primary;
    if (target.kind === 'bomb') return false;
    return candidate.kind === target.kind && candidate.count === target.count && candidate.length === target.length && candidate.primary > target.primary;
  }
  function combinations(values: readonly number[], count: number, visit: (cards: number[]) => void): void {
    const picked: number[] = [];
    function walk(start: number): void {
      if (picked.length === count) { visit([...picked]); return; }
      for (let i = start; i <= values.length - (count - picked.length); i++) { picked.push(values[i]); walk(i + 1); picked.pop(); }
    }
    walk(0);
  }
  /** 按牌型生成候选，避免对最多二十张手牌做指数级全子集扫描。 */
  export function legalMoves(hand: readonly number[], target: Pattern | null = null): Move[] {
    const g = groups(hand), rs = [...g.keys()], result: Move[] = [], seen = new Set<string>();
    const wanted = (kind: PatternKind) => !target || target.kind === kind;
    function add(cards: readonly number[]): void {
      const key = [...cards].sort((a, b) => a - b).join(',');
      if (seen.has(key)) return;
      const p = evaluate(cards);
      if (!p || !beats(p, target)) return;
      seen.add(key); result.push({ cards: sortCards(cards), pattern: p });
    }
    for (const r of rs) {
      const cards = g.get(r)!;
      if (wanted('single')) add(cards.slice(0, 1));
      if (cards.length >= 2 && wanted('pair')) add(cards.slice(0, 2));
      if (cards.length >= 3) {
        const core = cards.slice(0, 3);
        if (wanted('triple')) add(core);
        for (const other of rs.filter(other => other !== r)) {
          if (wanted('tripleSingle')) add([...core, g.get(other)![0]]);
          if (wanted('triplePair') && g.get(other)!.length >= 2) add([...core, ...g.get(other)!.slice(0, 2)]);
        }
      }
      if (cards.length === 4) {
        add(cards);
        const others = rs.filter(other => other !== r);
        if (wanted('fourSingle')) {
          const pool = others.flatMap(other => g.get(other)!.slice(0, 2));
          combinations(pool, 2, wings => add([...cards, ...wings]));
        }
        if (wanted('fourPair')) combinations(others.filter(other => g.get(other)!.length >= 2), 2, wings => add([...cards, ...wings.flatMap(other => g.get(other)!.slice(0, 2))]));
      }
    }
    if (g.has(16) && g.has(17)) add([52, 53]);
    for (const [copies, min, kind] of [[1, 5, 'straight'], [2, 3, 'pairRun'], [3, 2, 'plane']] as const) {
      if (copies < 3 && !wanted(kind)) continue;
      if (copies === 3 && !(['plane', 'planeSingle', 'planePair'] as const).some(wanted)) continue;
      for (let start = 3; start <= 14; start++) {
        const coreRanks: number[] = [], coreCards: number[] = [];
        for (let end = start; end <= 14 && (g.get(end)?.length || 0) >= copies; end++) {
          coreRanks.push(end); coreCards.push(...g.get(end)!.slice(0, copies));
          const length = coreRanks.length;
          if (length < min) continue;
          if (wanted(kind)) add(coreCards);
          if (copies !== 3) continue;
          const remainder = new Map(rs.map(r => [r, g.get(r)!.slice(coreRanks.includes(r) ? 3 : 0)]));
          const otherRanks = rs.filter(r => remainder.get(r)!.length > 0);
          if (wanted('planeSingle') && length * 4 <= hand.length) combinations(otherRanks, length, wings => add([...coreCards, ...wings.map(r => remainder.get(r)![0])]));
          if (wanted('planePair') && length * 5 <= hand.length) combinations(otherRanks.filter(r => remainder.get(r)!.length >= 2), length, wings => add([...coreCards, ...wings.flatMap(r => remainder.get(r)!.slice(0, 2))]));
        }
      }
    }
    return result;
  }
