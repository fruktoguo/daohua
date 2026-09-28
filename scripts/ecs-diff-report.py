#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""ECS 双内核台账差分报告（按操作序号聚合 + 语义归一化分类）。

用法：python3 scripts/ecs-diff-report.py <artifacts/diff 目录>

比较 ledger-current-*.jsonl（基线）与 ledger-optimized-*.jsonl（优化版）。
所有行都带 "s" 操作序号，直接按序号聚合比对，不做行级 SequenceMatcher
（避免 diff 块边界把同一操作拆碎造成误归属）。

每个不一致操作归入以下类别之一：

  合理差异（无序容器/实现细节，非业务语义）：
  - order-only        ：回调多重集一致（忽略 cb 序号 i）、成员集一致，
                        仅迭代顺序 / bs.o 顺序哈希 / sys_members 顺序哈希不同
  - id-drift          ：顺序差异的级联——回调内 new YuoEntity/AddChild 依迭代序
                        消费实体 id，导致同一逻辑实体两侧分到不同 id；st/dump
                        摘要 n 相同但 h 不同，dump 差异仅为实体 id 互换

  优化版疑似 bug：
  - ghost-component   ：优化版对 component.Entity 已释放（ID=-1）的孤儿组件
                        继续派发系统回调；基线从未出现 e=-1 回调
  - callback-set      ：归一化后回调多重集仍不一致且不属于上述模式

  基线疑似 bug / 生命周期语义差异：
  - lifecycle-destroy ：基线对未实际移除/销毁的组件或实体误触发 IDestroy
                        （实体随后继续存活并再次触发）；优化版只在真实销毁时派发

  其余：
  - query             ：q 行差异（非顺序哈希、非 id 漂移可解释）
  - state             ：st/dump 差异且 n 不同（真实结构分叉，需人工）
  - exception         ：ex/crash 行不对称（两侧相同异常属于一致行为）
  - truncation        ：一侧操作缺失
  - capability        ：SetActiveEntities 等基线不具备的 API 差异
  - mixed             ：其它组合

meta 行只标注台账来源，不参与比对。
stderr 日志归一化 vendor 树路径与行号后逐字节比对。
"""

import json
import os
import re
import sys
from collections import Counter, defaultdict
from difflib import SequenceMatcher


def parse(line):
    try:
        return json.loads(line)
    except Exception:
        return {}


def line_type(line):
    return parse(line).get("t", "?")


def line_seq(line):
    s = parse(line).get("s", -1)
    return s if isinstance(s, int) else -1


def load(path):
    # utf-8-sig 吃掉 BOM；meta 行是来源标注不参与比对
    with open(path, "r", encoding="utf-8-sig") as f:
        return [l for l in f.read().splitlines() if line_type(l) != "meta"]


def cb_norm(line):
    """cb 行归一化：去掉序号 i（迭代序实现细节），保留 sys/tag/e/c/v。"""
    d = parse(line)
    for k in ("t", "s", "i"):
        d.pop(k, None)
    return json.dumps(d, sort_keys=True)


def cb_norm_noe(line):
    """cb 行深度归一化：再去掉实体 id e，识别 id 漂移。"""
    d = parse(line)
    for k in ("t", "s", "i", "e"):
        d.pop(k, None)
    return json.dumps(d, sort_keys=True)


def q_norm(line):
    """q 行归一化：成员表 {hex} 段是原始迭代序哈希，归一化掉。"""
    return re.sub(r"\{[0-9a-f]+\}", "{H}", line)


def q_norm_noid(line):
    """q 行深度归一化：再把所有数字抹为 #，只剩结构差异。"""
    return re.sub(r"\d+", "#", q_norm(line))


def strip_ids(line):
    return re.sub(r"\d+", "#", line)


def bs_member_map(line):
    """bs 行 -> {sys|tag: (sorted ids, n)}；无法解析返回 None。"""
    v = parse(line).get("v")
    if not isinstance(v, dict):
        return None
    return {k: (tuple(x.get("ids", [])), x.get("n")) for k, x in v.items()}


def bs_member_map_noid(line):
    """bs 成员深度归一化：ids 抹成计数占位。"""
    v = parse(line).get("v")
    if not isinstance(v, dict):
        return None
    return {k: x.get("n") for k, x in v.items()}


def classify_op(a_lines, b_lines):
    """对同一操作序号下两侧台账行做分类。"""
    if not a_lines or not b_lines:
        return "truncation"

    # SetActiveEntities 为优化版独有 API：基线条件编译跳过该调用，
    # 优化版台账多出 active_applied q 行且派发集合按活动子集裁剪。
    joined = " ".join(a_lines + b_lines)
    if '"k":"active_applied"' in joined or '"k":"set_active' in joined or 'set_active' in joined:
        return "capability"

    def rows(lines, t):
        return [l for l in lines if line_type(l) == t]

    a_ex = rows(a_lines, "ex") + rows(a_lines, "crash")
    b_ex = rows(b_lines, "ex") + rows(b_lines, "crash")
    if a_ex != b_ex:
        return "exception"
    if a_ex:
        return "exception-shared"  # 两侧同异常——一致行为，不算差异

    a_cb = rows(a_lines, "cb")
    b_cb = rows(b_lines, "cb")
    a_st = rows(a_lines, "st") + rows(a_lines, "dump")
    b_st = rows(b_lines, "st") + rows(b_lines, "dump")
    a_q = [q_norm(l) for l in rows(a_lines, "q") + rows(a_lines, "syslist")]
    b_q = [q_norm(l) for l in rows(b_lines, "q") + rows(b_lines, "syslist")]

    acb = Counter(cb_norm(l) for l in a_cb)
    bcb = Counter(cb_norm(l) for l in b_cb)
    cb_equal = acb == bcb
    st_equal = a_st == b_st
    q_equal = a_q == b_q

    if cb_equal:
        if st_equal and q_equal:
            return "order-only"
        if not st_equal:
            # 状态摘要分叉：实体数一致 => id 漂移级联；n 不同 => 真结构分叉
            same_n = all(
                parse(x).get("n") == parse(y).get("n")
                for x, y in zip(
                    [l for l in a_st if line_type(l) == "st"],
                    [l for l in b_st if line_type(l) == "st"],
                )
            ) and len(a_st) == len(b_st)
            return "id-drift" if same_n else "state"
        return "query" if not q_equal else "order-only"

    # 回调集合不同：细分
    only_a = list((acb - bcb).elements())
    only_b = list((bcb - acb).elements())

    # 1) 仅实体 id 不同（内容/系统/组件/负载一致）=> id 漂移级联
    if Counter(cb_norm_noe(l) for l in a_cb) == Counter(cb_norm_noe(l) for l in b_cb):
        return "id-drift"

    # 2) 优化版幽灵组件：b 侧多出 e=-1 回调（component.Entity 已释放）
    b_neg1 = [x for x in only_b if json.loads(x).get("e") == -1]
    a_neg1 = [x for x in only_a if json.loads(x).get("e") == -1]
    if b_neg1 and not a_neg1:
        return "ghost-component"

    # 3) 生命周期时序：a 侧多出 IDestroy 而 b 侧对应同实体 IUpdate
    #    （基线在回调内 Destroy/RemoveComponent 已派发回调但对象继续存活）
    def key_of(norm):
        d = json.loads(norm)
        return (d.get("sys"), d.get("e"), d.get("c"), d.get("v"))

    a_destroy = {key_of(x) for x in only_a if json.loads(x).get("tag") == "IDestroy"}
    b_update_keys = {
        key_of(x)
        for x in only_b
        if json.loads(x).get("tag") in ("IUpdate", "IFixedUpdate", "ILateUpdate")
    }
    if a_destroy and a_destroy <= b_update_keys:
        return "lifecycle-destroy"

    # 4) 深度归一化抹掉数字后一致 => 也是 id 漂移（q/dump 文本里的 id）
    if Counter(strip_ids(l) for l in a_cb) == Counter(strip_ids(l) for l in b_cb):
        return "id-drift"

    # 5) bs 成员集一致但回调集不同
    a_bs = [bs_member_map(l) for l in rows(a_lines, "bs")]
    b_bs = [bs_member_map(l) for l in rows(b_lines, "bs")]
    if a_bs and b_bs and a_bs == b_bs:
        return "callback-order"
    if a_bs and b_bs and bs_member_map_noid(rows(a_lines, "bs")[0]) == bs_member_map_noid(rows(b_lines, "bs")[0]):
        return "id-drift"

    return "callback-set"


def group_by_seq(lines):
    d = defaultdict(list)
    for l in lines:
        d[line_seq(l)].append(l)
    return d


def op_head(lines):
    for l in lines:
        if line_type(l) == "op":
            return l
    return lines[0] if lines else "?"


def diff_pair(a_path, b_path, name, out, stats, first_state):
    a = load(a_path)
    b = load(b_path)
    if a == b:
        out.append(f"[equal] {name}: {len(a)} 行完全一致")
        stats["equal_files"] += 1
        return

    ga, gb = group_by_seq(a), group_by_seq(b)
    op_diffs = []
    for s in sorted(set(ga) | set(gb)):
        if s < 0:
            continue
        al, bl = ga.get(s, []), gb.get(s, [])
        if al == bl:
            continue
        cls = classify_op(al, bl)
        stats[cls] += 1
        op_diffs.append((s, cls, al, bl))
        if cls in ("id-drift", "state") and name not in first_state:
            first_state[name] = s

    counts = dict(Counter(c for _, c, _, _ in op_diffs))
    out.append(
        f"[DIFF ] {name}: current={len(a)} 行 optimized={len(b)} 行，"
        f"差异操作 {len(op_diffs)} 个 -> {counts}"
    )
    if name in first_state:
        out.append(f"         首个状态/id 漂移点 op_seq={first_state[name]}")

    shown = 0
    for s, cls, al, bl in op_diffs:
        if cls in ("order-only", "exception-shared"):
            continue
        if shown >= 30:
            out.append(f"  ... 其余 {len(op_diffs) - shown} 个差异省略")
            break
        shown += 1
        out.append(f"  - [{cls}] op_seq={s} 行数 a={len(al)} b={len(bl)}")
        out.append(f"    op: {op_head(al)[:200]}")
        shown_a = [x for x in al if x not in bl][:4]
        shown_b = [x for x in bl if x not in al][:4]
        for x in shown_a:
            out.append(f"    -a {x[:230]}")
        for x in shown_b:
            out.append(f"    -b {x[:230]}")


def norm_stderr(text):
    text = text.replace("vendor/YuoCore-optimized", "vendor/YuoCore")
    text = re.sub(r":\s*line\s*\d+", ": line N", text)
    text = re.sub(r"[^\s]*/daohua/", "~/", text)
    return text


def main():
    outdir = sys.argv[1] if len(sys.argv) > 1 else "artifacts/diff"
    out = []
    stats = Counter()
    first_state = {}
    seeds = sorted(
        set(
            int(m.group(1))
            for f in os.listdir(outdir)
            for m in [re.match(r"ledger-current-seed(\d+)\.jsonl$", f)]
            if m
        )
    )
    for s in seeds:
        diff_pair(
            os.path.join(outdir, f"ledger-current-seed{s}.jsonl"),
            os.path.join(outdir, f"ledger-optimized-seed{s}.jsonl"),
            f"seed{s}",
            out,
            stats,
            first_state,
        )
    a = os.path.join(outdir, "ledger-current-directed.jsonl")
    b = os.path.join(outdir, "ledger-optimized-directed.jsonl")
    if os.path.exists(a) and os.path.exists(b):
        diff_pair(a, b, "directed", out, stats, first_state)

    out.append("")
    out.append("=== stderr 比对（路径归一化） ===")
    stderr_diffs = 0
    for s in seeds + ["directed"]:
        suffix = f"seed{s}" if isinstance(s, int) else "directed"
        fa = os.path.join(outdir, f"stderr-current-{suffix}.log")
        fb = os.path.join(outdir, f"stderr-optimized-{suffix}.log")
        if not (os.path.exists(fa) and os.path.exists(fb)):
            continue
        ta, tb = norm_stderr(open(fa).read()), norm_stderr(open(fb).read())
        if ta or tb:
            if ta == tb:
                out.append(f"[stderr-same ] {suffix}: 两侧各 {ta.count(chr(10))} 行（内容一致）")
            else:
                stderr_diffs += 1
                out.append(f"[stderr-DIFF ] {suffix}: current {ta.count(chr(10))} 行 / optimized {tb.count(chr(10))} 行")
                for x in ta.splitlines()[:3]:
                    out.append(f"    -a {x[:200]}")
                for x in tb.splitlines()[:3]:
                    out.append(f"    -b {x[:200]}")

    out.insert(0, f"差异分类汇总={dict(stats)}；stderr 不一致文件对={stderr_diffs}")
    print("\n".join(out))


if __name__ == "__main__":
    main()
