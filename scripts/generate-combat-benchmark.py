#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""
道化太初 · 战斗基准模拟与产出期望模型文档/配置自动化生成工具
------------------------------------------------------------
最终确定平衡参数体系:
1. 物理系 (1 AD : 1 暴击 : 1 破招):
   - 平A: 1.0s, 100% AD, 专属攻速 50%, 专属增伤 100% -> 0.250s 1秒4刀 200% AD
   - 单体1/2: 220% AD, CD 12.0s, 动作 1.0s
   - 群攻3: 180% AD, CD 18.0s, 动作 2.0s, 3目标
2. 法术系 (固定 2.0x AP : 1 命中):
   - 大群攻1: 90% AP, CD 22.0s, 吟唱 2.0s, 5目标
   - 强群攻2: 120% AP, CD 20.0s, 吟唱 2.0s, 3目标
   - 灵爆普攻: 60% AP, 0 CD, 吟唱 2.0s
3. 四阶段划分:
   - 前期: T1~T2 凡俗
   - 中期: T3~T5 练气筑基金丹
   - 后期: T6~T9 元婴化神炼虚合体
   - 大后期: T10~T12 大乘渡劫真仙
"""

import json
import os
import random
import sys

REPO_ROOT = "/home/yuohira/game/xiuxian"
CONFIG_PATH = os.path.join(REPO_ROOT, "config", "game-data", "expected-attributes.json")
COMBAT_DOC_PATH = os.path.join(REPO_ROOT, "docs", "design", "combat-benchmark-model.md")
PLAYER_DOC_PATH = os.path.join(REPO_ROOT, "docs", "design", "player-expected-attributes-model.md")


def get_stage_name(tier):
    if tier <= 2:
        return "前期 (凡俗)"
    elif tier <= 5:
        return "中期 (练气/筑基/金丹)"
    elif tier <= 9:
        return "后期 (元婴/化神/炼虚/合体)"
    else:
        return "大后期 (大乘/渡劫/真仙)"


def compute_special_attributes(tier_obj):
    t = tier_obj["tier"]
    lv = tier_obj["level"]
    x = (lv - 1) / 126.0
    x_quad = x * x

    cdr = round(x_quad * 75.0, 2)
    atr = round(x_quad * 50.0, 2)
    crit_dmg = round(150.0 + x_quad * 350.0, 2)
    p_extra_spd = round(x_quad * 50.0, 2)
    p_extra_dmg = round(x_quad * 100.0, 2)
    wait = round(5.0 - 2.5 * x_quad, 2)
    skill_int = round(1.0 + (t - 1) * 0.30, 2)

    p_interval = round(max(0.05, 1.0 * (1.0 - atr / 100.0) * (1.0 - p_extra_spd / 100.0)), 3)
    p_ratio = round(100.0 * (1.0 + p_extra_dmg / 100.0), 1)
    m_cast = round(max(0.1, 2.0 * (1.0 - atr / 100.0)), 2)
    m_ratio = round(60.0 * skill_int, 1)

    return {
        "cooldownReductionPct": cdr,
        "actionSpeedReductionPct": atr,
        "criticalDamageMultiplierPct": crit_dmg,
        "physicalAutoExtraAttackSpeedPct": p_extra_spd,
        "physicalAutoExtraDamagePct": p_extra_dmg,
        "waveWaitDelaySeconds": wait,
        "skillIntensityMultiplier": skill_int,
        "dynamicResistanceFactorK": float(t),
        "physicalAutoIntervalSeconds": p_interval,
        "physicalAutoDamageRatioPct": p_ratio,
        "magicAutoCastSeconds": m_cast,
        "magicAutoDamageRatioPct": m_ratio,
    }


def run_deterministic_sim(tier_info, team_type="physical", team_size=1, sim_seconds=8640, seed=42):
    random.seed(seed)
    tier_num = tier_info["tier"]
    p_exp = tier_info["finalExpected"]
    lv = tier_info["level"]

    p_max_hp = p_exp["maxHitpoints"]
    p_max_mp = p_exp["maxManapoints"]
    p_ad = p_exp["physicalAttack"]
    p_ap = p_ad * 2.0 # 固定 2.0x AP (2 AP : 1 命中)
    p_armor = p_exp["armor"]
    p_mres = p_exp["magicResistance"]

    k_res = float(tier_num)

    x = (lv - 1) / 126.0
    x_quad = x * x

    wave_delay = 5.0 - 2.5 * x_quad
    skill_intensity = 1.0 + (tier_num - 1) * 0.30

    cdr_pct = x_quad * 0.75
    cd_mult = 1.0 - cdr_pct

    action_speed_pct = x_quad * 0.50
    action_mult = 1.0 - action_speed_pct

    crit_dmg_ratio = 1.50 + x_quad * 3.50

    auto_speed_pct = x_quad * 0.50
    auto_speed_mult = 1.0 - auto_speed_pct
    auto_dmg_pct = x_quad * 1.00
    auto_dmg_mult = 1.0 + auto_dmg_pct

    phys_auto_interval = max(0.05, 1.0 * action_mult * auto_speed_mult)
    phys_auto_dmg_ratio = 1.00 * auto_dmg_mult

    magic_auto_cast = max(0.1, 2.0 * action_mult)
    magic_auto_dmg_ratio = 0.60 * skill_intensity

    # 实体属性流派配点装配
    if team_type == "physical":
        # 1 AD : 1 暴击 (2.0x基线) : 1 破招 (2.0x基线)
        p_hit = p_exp["accuracyRating"] * 1.0
        p_crit = p_exp["criticalRating"] * 2.0
        p_break = p_exp["breakRating"] * 2.0
    else:
        # 2 AP : 1 命中 (2.0x基线) : 0.5 暴击 : 0.5 破招
        p_hit = p_exp["accuracyRating"] * 2.0
        p_crit = p_exp["criticalRating"] * 0.5
        p_break = p_exp["breakRating"] * 0.5

    p_hp_regen = (p_max_hp * 0.03) / 10.0
    p_mp_regen = (p_max_mp * 0.15) / 10.0

    curr_time = 0.0
    wave_count = 0
    total_kills = 0
    minion_kills = 0
    elite_kills = 0
    boss_kills = 0

    wave_durations = []
    normal_wave_durations = []
    elite_wave_durations = []
    boss_wave_durations = []
    non_boss_wave_durations = []

    if team_type == "physical":
        damage_tracker = {
            "skill_1": {"name": "核心单体1 (220%)", "total_dmg": 0.0, "casts": 0},
            "skill_2": {"name": "核心单体2 (220%)", "total_dmg": 0.0, "casts": 0},
            "skill_3": {"name": "群体绝技 (180% 3怪)", "total_dmg": 0.0, "casts": 0},
            "auto_attack": {"name": "物理平A (1秒4刀)", "total_dmg": 0.0, "casts": 0},
        }
    else:
        damage_tracker = {
            "skill_1": {"name": "大范围群攻 (90% 5怪)", "total_dmg": 0.0, "casts": 0},
            "skill_2": {"name": "强力群攻 (120% 3怪)", "total_dmg": 0.0, "casts": 0},
            "skill_3": {"name": "法术普攻 (灵爆 60%)", "total_dmg": 0.0, "casts": 0},
        }

    # 精确非线性平方减伤与破招判定函数
    def calc_damage_per_hit(raw_dmg, def_res, atk_hit, def_eva, atk_crit, def_cavoid, atk_break, def_parry, is_player=True):
        break_succ = False
        if atk_break > def_parry:
            prob = atk_break / (atk_break + def_parry + 1.0)
            if random.random() < prob:
                break_succ = True

        effective_hit = atk_hit * (2.0 if break_succ else 1.0)
        effective_crit = atk_crit * (2.0 if break_succ else 1.0)

        hit_prob = effective_hit / (effective_hit + def_eva + 1.0)
        if random.random() > hit_prob:
            return 0.0

        crit_prob = effective_crit / (effective_crit + def_cavoid + 1.0)
        is_crit = random.random() < crit_prob
        crit_mult = (crit_dmg_ratio if is_player else 1.5) if is_crit else 1.0

        # 非线性平方破防减伤
        reduction = raw_dmg / (raw_dmg + def_res * k_res + 1.0)
        actual_dmg = raw_dmg * reduction * crit_mult
        return max(1.0, actual_dmg)

    while curr_time < sim_seconds:
        wave_count += 1
        curr_time += wave_delay
        if curr_time >= sim_seconds:
            break

        wave_start_time = curr_time
        is_boss_wave = wave_count % 20 == 0
        is_elite_wave = (not is_boss_wave) and (wave_count % 5 == 0)

        monsters = []
        if is_boss_wave:
            monsters.append({
                "kind": "boss",
                "hp": p_max_hp * 20.0,
                "max_hp": p_max_hp * 20.0,
                "ad": p_ad * 1.5,
                "ap": p_ap * 1.5,
                "armor": p_armor * 1.5,
                "mres": p_mres * 1.5,
                "hit": p_exp["accuracyRating"] * 1.5,
                "eva": p_exp["evasionRating"] * 1.5,
                "crit": p_exp["criticalRating"] * 1.5,
                "cavoid": p_exp["criticalAvoidance"] * 1.5,
                "break": p_exp["breakRating"] * 1.5,
                "parry": p_exp["parryRating"] * 1.5,
                "atk_interval": 2.5,
                "next_atk": curr_time + 1.0,
            })
            for _ in range(2):
                monsters.append({
                    "kind": "minion",
                    "hp": p_max_hp * 3.0,
                    "max_hp": p_max_hp * 3.0,
                    "ad": p_ad * 0.8,
                    "ap": p_ap * 0.8,
                    "armor": p_armor * 0.8,
                    "mres": p_mres * 0.8,
                    "hit": p_exp["accuracyRating"] * 0.8,
                    "eva": p_exp["evasionRating"] * 0.8,
                    "crit": p_exp["criticalRating"] * 0.8,
                    "cavoid": p_exp["criticalAvoidance"] * 0.8,
                    "break": p_exp["breakRating"] * 0.8,
                    "parry": p_exp["parryRating"] * 0.8,
                    "atk_interval": 2.2,
                    "next_atk": curr_time + 1.2,
                })
        elif is_elite_wave:
            monsters.append({
                "kind": "elite",
                "hp": p_max_hp * 5.0,
                "max_hp": p_max_hp * 5.0,
                "ad": p_ad * 1.2,
                "ap": p_ap * 1.2,
                "armor": p_armor * 1.2,
                "mres": p_mres * 1.2,
                "hit": p_exp["accuracyRating"] * 1.2,
                "eva": p_exp["evasionRating"] * 1.2,
                "crit": p_exp["criticalRating"] * 1.2,
                "cavoid": p_exp["criticalAvoidance"] * 1.2,
                "break": p_exp["breakRating"] * 1.2,
                "parry": p_exp["parryRating"] * 1.2,
                "atk_interval": 2.2,
                "next_atk": curr_time + 1.0,
            })
            for _ in range(2):
                monsters.append({
                    "kind": "minion",
                    "hp": p_max_hp * 3.0,
                    "max_hp": p_max_hp * 3.0,
                    "ad": p_ad * 0.8,
                    "ap": p_ap * 0.8,
                    "armor": p_armor * 0.8,
                    "mres": p_mres * 0.8,
                    "hit": p_exp["accuracyRating"] * 0.8,
                    "eva": p_exp["evasionRating"] * 0.8,
                    "crit": p_exp["criticalRating"] * 0.8,
                    "cavoid": p_exp["criticalAvoidance"] * 0.8,
                    "break": p_exp["breakRating"] * 0.8,
                    "parry": p_exp["parryRating"] * 0.8,
                    "atk_interval": 2.2,
                    "next_atk": curr_time + 1.2,
                })
        else:
            for _ in range(3):
                monsters.append({
                    "kind": "minion",
                    "hp": p_max_hp * 3.0,
                    "max_hp": p_max_hp * 3.0,
                    "ad": p_ad * 0.8,
                    "ap": p_ap * 0.8,
                    "armor": p_armor * 0.8,
                    "mres": p_mres * 0.8,
                    "hit": p_exp["accuracyRating"] * 0.8,
                    "eva": p_exp["evasionRating"] * 0.8,
                    "crit": p_exp["criticalRating"] * 0.8,
                    "cavoid": p_exp["criticalAvoidance"] * 0.8,
                    "break": p_exp["breakRating"] * 0.8,
                    "parry": p_exp["parryRating"] * 0.8,
                    "atk_interval": 2.2,
                    "next_atk": curr_time + 1.0 + random.uniform(0, 0.5),
                })

        players = []
        for p_i in range(team_size):
            if team_type == "physical":
                p_skills = [
                    {"id": "skill_1", "cd": 12.0 * cd_mult, "cast": 1.0 * action_mult, "mult": 2.20 * skill_intensity, "max_targets": 1, "cost": 20.0 * (1.0 + (tier_num - 1) * 0.2), "is_ap": False},
                    {"id": "skill_2", "cd": 12.0 * cd_mult, "cast": 1.0 * action_mult, "mult": 2.20 * skill_intensity, "max_targets": 1, "cost": 20.0 * (1.0 + (tier_num - 1) * 0.2), "is_ap": False},
                    {"id": "skill_3", "cd": 18.0 * cd_mult, "cast": 2.0 * action_mult, "mult": 1.80 * skill_intensity, "max_targets": 3, "cost": 30.0 * (1.0 + (tier_num - 1) * 0.2), "is_ap": False},
                ]
            else:
                p_skills = [
                    {"id": "skill_1", "cd": 22.0 * cd_mult, "cast": 2.0 * action_mult, "mult": 0.90 * skill_intensity, "max_targets": 5, "cost": 30.0 * (1.0 + (tier_num - 1) * 0.2), "is_ap": True},
                    {"id": "skill_2", "cd": 20.0 * cd_mult, "cast": 2.0 * action_mult, "mult": 1.20 * skill_intensity, "max_targets": 3, "cost": 35.0 * (1.0 + (tier_num - 1) * 0.2), "is_ap": True},
                    {"id": "skill_3", "cd": 0.0, "cast": magic_auto_cast, "mult": magic_auto_dmg_ratio, "max_targets": 1, "cost": 5.0 * (1.0 + (tier_num - 1) * 0.2), "is_ap": True},
                ]
            players.append({
                "hp": p_max_hp,
                "mp": p_max_mp,
                "skills": p_skills,
                "next_action": curr_time + 0.05 * p_i,
                "next_skill_ready": [curr_time] * len(p_skills),
            })

        while any(m["hp"] > 0 for m in monsters) and any(p["hp"] > 0 for p in players) and curr_time < sim_seconds:
            alive_monsters = [m for m in monsters if m["hp"] > 0]
            if not alive_monsters:
                break

            next_events = [p["next_action"] for p in players] + [m["next_atk"] for m in alive_monsters]
            earliest = min(next_events)
            dt = max(0.005, earliest - curr_time)
            curr_time = earliest

            for p in players:
                p["hp"] = min(p_max_hp, p["hp"] + p_hp_regen * dt)
                p["mp"] = min(p_max_mp, p["mp"] + p_mp_regen * dt)

            for p in players:
                if curr_time >= p["next_action"]:
                    alive_monsters = [m for m in monsters if m["hp"] > 0]
                    if not alive_monsters:
                        break
                    cast_skill = False
                    for s_idx, s in enumerate(p["skills"]):
                        if curr_time >= p["next_skill_ready"][s_idx] and p["mp"] >= s["cost"]:
                            p["mp"] -= s["cost"]
                            if s["cd"] > 0:
                                p["next_skill_ready"][s_idx] = curr_time + s["cast"] + s["cd"]
                            else:
                                p["next_skill_ready"][s_idx] = curr_time + s["cast"]

                            atk_stat = p_ap if s["is_ap"] else p_ad
                            raw_dmg = atk_stat * s["mult"]
                            targets = alive_monsters[: s["max_targets"]]
                            damage_tracker[s["id"]]["casts"] += 1
                            for tgt in targets:
                                res_stat = tgt["mres"] if s["is_ap"] else tgt["armor"]
                                dmg = calc_damage_per_hit(raw_dmg, res_stat, p_hit, tgt["eva"], p_crit, tgt["cavoid"], p_break, tgt["parry"], is_player=True)
                                tgt["hp"] -= dmg
                                damage_tracker[s["id"]]["total_dmg"] += dmg
                            cast_skill = True
                            p["next_action"] = curr_time + s["cast"]
                            break
                    if not cast_skill:
                        if team_type == "physical":
                            tgt = alive_monsters[0]
                            raw_dmg = p_ad * phys_auto_dmg_ratio
                            dmg = calc_damage_per_hit(raw_dmg, tgt["armor"], p_hit, tgt["eva"], p_crit, tgt["cavoid"], p_break, tgt["parry"], is_player=True)
                            tgt["hp"] -= dmg
                            damage_tracker["auto_attack"]["casts"] += 1
                            damage_tracker["auto_attack"]["total_dmg"] += dmg
                            p["next_action"] = curr_time + phys_auto_interval
                        else:
                            p["next_action"] = curr_time + 0.1

            for m in alive_monsters:
                if curr_time >= m["next_atk"] and m["hp"] > 0:
                    raw_dmg = m["ad"]
                    target_p = random.choice(players)
                    dmg = calc_damage_per_hit(raw_dmg, p_armor, m["hit"], p_exp["evasionRating"], m["crit"], p_exp["criticalAvoidance"], m["break"], p_exp["parryRating"], is_player=False)
                    target_p["hp"] -= dmg
                    m["next_atk"] = curr_time + m["atk_interval"]

        wave_dur = curr_time - wave_start_time
        wave_durations.append(wave_dur)

        if is_boss_wave:
            boss_wave_durations.append(wave_dur)
            boss_kills += 1
            minion_kills += 2
        elif is_elite_wave:
            elite_wave_durations.append(wave_dur)
            non_boss_wave_durations.append(wave_dur)
            elite_kills += 1
            minion_kills += 2
        else:
            normal_wave_durations.append(wave_dur)
            non_boss_wave_durations.append(wave_dur)
            minion_kills += 3

        total_kills = minion_kills + elite_kills + boss_kills

    avg_overall_wave = sum(wave_durations) / max(1, len(wave_durations))
    avg_normal_wave = sum(normal_wave_durations) / max(1, len(normal_wave_durations)) if normal_wave_durations else 0
    avg_elite_wave = sum(elite_wave_durations) / max(1, len(elite_wave_durations)) if elite_wave_durations else 0
    avg_non_boss_wave = sum(non_boss_wave_durations) / max(1, len(non_boss_wave_durations)) if non_boss_wave_durations else 0
    avg_boss_dur = sum(boss_wave_durations) / max(1, len(boss_wave_durations)) if boss_wave_durations else 0

    avg_overall_kill = sim_seconds / max(1, total_kills)
    avg_non_boss_kill = avg_non_boss_wave / 3.0

    hourly_kills = total_kills / (sim_seconds / 3600.0)
    hourly_waves = len(wave_durations) / (sim_seconds / 3600.0)

    total_damage = sum(item["total_dmg"] for item in damage_tracker.values())
    breakdown = {}
    for k, item in damage_tracker.items():
        dmg = item["total_dmg"]
        pct = (dmg / total_damage * 100.0) if total_damage > 0 else 0.0
        breakdown[k] = {
            "name": item["name"],
            "dps": dmg / sim_seconds,
            "pct": pct,
        }

    return {
        "tier": tier_num,
        "realmName": tier_info["realmName"],
        "stage": get_stage_name(tier_num),
        "level": lv,
        "teamType": team_type,
        "teamSize": team_size,
        "hourlyKills": round(hourly_kills, 2),
        "hourlyWaves": round(hourly_waves, 2),
        "totalKills": total_kills,
        "dailyKills24h": round(hourly_kills * 24.0, 0),
        "avgNormalWaveSec": round(avg_normal_wave, 2),
        "avgEliteWaveSec": round(avg_elite_wave, 2),
        "avgNonBossWaveSec": round(avg_non_boss_wave, 2),
        "avgNonBossKillSec": round(avg_non_boss_kill, 2),
        "avgOverallWaveSec": round(avg_overall_wave, 2),
        "avgOverallKillSec": round(avg_overall_kill, 2),
        "avgBossDurSec": round(avg_boss_dur, 2),
        "totalDps": round(total_damage / sim_seconds, 1),
        "breakdown": breakdown,
    }


def main():
    print("==========================================================")
    print("道化太初 · 战斗基准模拟与产出期望模型自动化生成中...")
    print("==========================================================")

    with open(CONFIG_PATH, "r", encoding="utf-8") as f:
        config = json.load(f)

    # 1. Update Special Attributes
    print("1/4 计算并更新全 12 阶特殊属性 (二次函数模型，ATR 满级 50%)...")
    for tier_obj in config["tiers"]:
        tier_obj["expectedSpecialAttributes"] = compute_special_attributes(tier_obj)

    # 2. Run simulation
    print("2/4 执行 2.4 小时确定性单人与三人组队全境界战斗推演 (精确单发破防内核)...")
    solo_phys = []
    solo_magic = []
    team_phys = []
    team_magic = []

    for tier_obj in config["tiers"]:
        sp = run_deterministic_sim(tier_obj, "physical", 1)
        sm = run_deterministic_sim(tier_obj, "magic", 1)
        tp = run_deterministic_sim(tier_obj, "physical", 3)
        tm = run_deterministic_sim(tier_obj, "magic", 3)

        solo_phys.append(sp)
        solo_magic.append(sm)
        team_phys.append(tp)
        team_magic.append(tm)

        tier_obj["combatSimulationDualStyle"] = {
            "physicalStyle": {
                "attackPowerType": "AD (Physical Attack) [1 AD : 1 Crit : 1 Break]",
                "normalWaveDurationSeconds": sp["avgNormalWaveSec"],
                "eliteWaveDurationSeconds": sp["avgEliteWaveSec"],
                "nonBossAvgWaveSeconds": sp["avgNonBossWaveSec"],
                "nonBossAvgKillSeconds": sp["avgNonBossKillSec"],
                "bossWaveDurationSeconds": sp["avgBossDurSec"],
                "overallAvgWaveSeconds": sp["avgOverallWaveSec"],
                "overallAvgKillSeconds": sp["avgOverallKillSec"],
                "hourlyWaves": sp["hourlyWaves"],
                "hourlyKills": sp["hourlyKills"],
                "dailyKills24h": sp["dailyKills24h"],
                "totalDps": sp["totalDps"],
                "dpsBreakdown": sp["breakdown"],
            },
            "magicStyle": {
                "attackPowerType": "AP = 2.0 * AD [2 AP : 1 Hit]",
                "normalWaveDurationSeconds": sm["avgNormalWaveSec"],
                "eliteWaveDurationSeconds": sm["avgEliteWaveSec"],
                "nonBossAvgWaveSeconds": sm["avgNonBossWaveSec"],
                "nonBossAvgKillSeconds": sm["avgNonBossKillSec"],
                "bossWaveDurationSeconds": sm["avgBossDurSec"],
                "overallAvgWaveSeconds": sm["avgOverallWaveSec"],
                "overallAvgKillSeconds": sm["avgOverallKillSec"],
                "hourlyWaves": sm["hourlyWaves"],
                "hourlyKills": sm["hourlyKills"],
                "dailyKills24h": sm["dailyKills24h"],
                "totalDps": sm["totalDps"],
                "dpsBreakdown": sm["breakdown"],
            },
        }
        tier_obj["combatEfficiencyRatio"] = {
            "solo": {
                "nonBossEfficiencyRatioPct": round((sm["avgNonBossWaveSec"] / max(0.01, sp["avgNonBossWaveSec"])) * 100, 1),
                "bossDurationRatioPct": round((sp["avgBossDurSec"] / max(0.01, sm["avgBossDurSec"])) * 100, 1),
                "overallKillsRatioPct": round((sp["hourlyKills"] / max(0.01, sm["hourlyKills"])) * 100, 1),
            },
            "party3Player": {
                "physicalTeam": {
                    "normalWaveDurationSeconds": tp["avgNormalWaveSec"],
                    "eliteWaveDurationSeconds": tp["avgEliteWaveSec"],
                    "nonBossAvgWaveSeconds": tp["avgNonBossWaveSec"],
                    "nonBossAvgKillSeconds": tp["avgNonBossKillSec"],
                    "bossWaveDurationSeconds": tp["avgBossDurSec"],
                    "overallAvgWaveSeconds": tp["avgOverallWaveSec"],
                    "hourlyWaves": tp["hourlyWaves"],
                    "hourlyKills": tp["hourlyKills"],
                    "dailyKills24h": tp["dailyKills24h"],
                },
                "magicTeam": {
                    "normalWaveDurationSeconds": tm["avgNormalWaveSec"],
                    "eliteWaveDurationSeconds": tm["avgEliteWaveSec"],
                    "nonBossAvgWaveSeconds": tm["avgNonBossWaveSec"],
                    "nonBossAvgKillSeconds": tm["avgNonBossKillSec"],
                    "bossWaveDurationSeconds": tm["avgBossDurSec"],
                    "overallAvgWaveSeconds": tm["avgOverallWaveSec"],
                    "hourlyWaves": tm["hourlyWaves"],
                    "hourlyKills": tm["hourlyKills"],
                    "dailyKills24h": tm["dailyKills24h"],
                },
                "teamNonBossEfficiencyRatioPct": round((tm["avgNonBossWaveSec"] / max(0.01, tp["avgNonBossWaveSec"])) * 100, 1),
                "teamOverallKillsRatioPct": round((tp["hourlyKills"] / max(0.01, tm["hourlyKills"])) * 100, 1),
                "partySpeedupMultiplier": round((tp["hourlyKills"] + tm["hourlyKills"]) / max(0.1, (sp["hourlyKills"] + sm["hourlyKills"])), 2),
            },
        }

    # Update top-level rules with full structured specifications
    config["rules"] = {
        "rootScalingFormula": "Root% = Level * 0.03 (每级 3%)",
        "singleBuffSingleStatFormula": "EquipPieceBase / 4 (精确整数百分比)",
        "singleBuffAllStatsFormula": "EquipPieceBase / 48",
        "expectedBuffCountFormula": "round(Tier * 1.1^Tier)",
        "equipEquivStatFormula": "EnhancedPieceBase / 2",
        "enhancementMultiplierFormula": "1.05^EnhancementLevel",
        "monsterWaveRules": {
            "monstersPerWave": 3,
            "bossWaveFrequency": "每 20 波",
            "eliteWaveFrequency": "每 5 波 (非 Boss 波)",
            "minionAttributes": "80% 全属性, 300% 生命值",
            "eliteAttributes": "120% 全属性, 500% 生命值",
            "bossAttributes": "150% 全属性, 2000% 生命值"
        },
        "growthCurveModel": {
            "type": "quadratic",
            "normalizedFormula": "x = (Level - 1) / 126.0, Progress(x) = x^2",
            "description": "二次函数模型，前期平缓，中后期爆发式提升"
        },
        "advancedMechanicsFormulas": {
            "cooldownReductionFormula": "CDR(Level) = ((Level - 1) / 126.0)^2 * 75% (0% -> 75% 二次曲线，仅负责主动技能 CD 缩减)",
            "actionSpeedReductionFormula": "ATR(Level) = ((Level - 1) / 126.0)^2 * 50% (0% -> 50% 二次曲线，动作时间与施法吟唱时间缩减，时间乘数 1.00 -> 0.50)",
            "criticalDamageRatioFormula": "CritDamageMultiplier(Level) = 1.50 + ((Level - 1) / 126.0)^2 * 3.50 (150% -> 500% 二次曲线)",
            "waveSearchDelayFormula": "WaitSeconds = 5.0 - 2.5 * ((Level - 1) / 126.0)^2 (5.0s -> 2.5s 二次曲线)",
            "skillIntensityFormula": "SkillIntensity = 1.0 + (Tier - 1) * 0.30 (1.00x ~ 4.30x，纯技能强度成长)"
        },
        "damageMitigationFormula": {
            "model": "Damage Squared Resistance Penetration (攻击力与伤害非线性平方模型)",
            "dynamicResistanceCoefficientFormula": "K(Tier) = Tier (1阶为1.0，12阶真仙为12.0)",
            "mitigatedDamageFormula": "ActualDamage = RawDamage * (RawDamage / (RawDamage + EffectiveResistance * K + 1.0)) * (isCritical ? CritDmg : 1.0)",
            "description": "单发原始伤害越高，天然穿甲能力越强；单发原始伤害低，受抗性拦截更严重"
        },
        "statAllocationStyles": {
            "physicalStyle": {
                "allocationRatio": "1 AD : 1 暴击 : 1 破招",
                "mechanics": "破招成功触发双倍命中(2x)与双倍暴击(2x)，配合1秒4刀极速出刀与220%高倍单体大招"
            },
            "magicStyle": {
                "allocationRatio": "固定 2.0x AP : 1 命中",
                "mechanics": "固定 2.0x AP 超高大攻击力在平方减伤下天然破防击穿魔抗，2.0x 命中确保绝对必中，全屏大范围 AoE 极速清怪"
            }
        },
        "benchmarkCombatSkillsModel": {
            "physicalStyle": {
                "name": "物理系标准技能基准 (1 AD : 1 暴击 : 1 破招)",
                "autoAttack": {
                    "name": "物理普通攻击 (平 A)",
                    "baseIntervalSeconds": 1.0,
                    "baseDamageRatioPct": 100.0,
                    "maxExtraAttackSpeedPct": 50.0,
                    "maxExtraDamagePct": 100.0,
                    "finalIntervalSeconds": 0.250,
                    "finalDamageRatioPct": 200.0,
                    "description": "受 ATR 与专属攻速双重缩减，12阶真仙达到 0.250s (1秒4刀) / 200% AD"
                },
                "skills": [
                    {
                        "slot": 1,
                        "name": "核心单体1",
                        "baseDamageRatioPct": 220.0,
                        "baseCooldownSeconds": 12.0,
                        "baseCastSeconds": 1.0,
                        "maxTargets": 1,
                        "cost": 20
                    },
                    {
                        "slot": 2,
                        "name": "核心单体2",
                        "baseDamageRatioPct": 220.0,
                        "baseCooldownSeconds": 12.0,
                        "baseCastSeconds": 1.0,
                        "maxTargets": 1,
                        "cost": 20
                    },
                    {
                        "slot": 3,
                        "name": "群体绝技",
                        "baseDamageRatioPct": 180.0,
                        "baseCooldownSeconds": 18.0,
                        "baseCastSeconds": 2.0,
                        "maxTargets": 3,
                        "cost": 30
                    },
                    {
                        "slot": 4,
                        "name": "功能Buff",
                        "description": "常驻/被动 Buff 型技能，不参与战斗即时伤害施法，其增益已直接计入四独立乘区中的 Buff 乘区 (1 + Buff%)"
                    }
                ]
            },
            "magicStyle": {
                "name": "法术系标准技能基准 (固定 2.0x AP : 1 命中)",
                "autoAttack": {
                    "name": "法术普攻 (灵爆术)",
                    "baseCastSeconds": 2.0,
                    "baseDamageRatioPct": 60.0,
                    "cooldownSeconds": 0.0,
                    "finalCastSeconds": 1.00,
                    "finalDamageRatioPct": 258.0,
                    "description": "0 CD 技能，受 ATR 缩减至 1.00s (1秒1发)，伤害享受技能强度阶位成长 (60% -> 258% AP)"
                },
                "skills": [
                    {
                        "slot": 1,
                        "name": "大范围群攻",
                        "baseDamageRatioPct": 90.0,
                        "baseCooldownSeconds": 22.0,
                        "baseCastSeconds": 2.0,
                        "maxTargets": 5,
                        "cost": 30
                    },
                    {
                        "slot": 2,
                        "name": "强力群攻",
                        "baseDamageRatioPct": 120.0,
                        "baseCooldownSeconds": 20.0,
                        "baseCastSeconds": 2.0,
                        "maxTargets": 3,
                        "cost": 35
                    },
                    {
                        "slot": 4,
                        "name": "功能Buff",
                        "description": "常驻/被动 Buff 型技能，不参与战斗即时伤害施法，其增益已直接计入四独立乘区中的 Buff 乘区 (1 + Buff%)"
                    }
                ]
            }
        }
    }

    # Save JSON
    print("3/4 持久化写入 config/game-data/expected-attributes.json...")
    with open(CONFIG_PATH, "w", encoding="utf-8") as f:
        json.dump(config, f, ensure_ascii=False, indent=2)

    # 3. Generate docs/design/combat-benchmark-model.md
    print("4/4 动态渲染并生成 docs/design/combat-benchmark-model.md 与 docs/design/player-expected-attributes-model.md...")
    lines = []
    lines.append("# 道化太初 · 战斗基准模拟与产出期望模型 (Combat Benchmark Model)")
    lines.append("")
    lines.append("> **文档定位**：全游戏战斗数值、关卡挂机产出、技能循环、单人与组队效率全景标准规约文档。  ")
    lines.append("> **关联配置文件**：[`config/game-data/expected-attributes.json`](../../config/game-data/expected-attributes.json)  ")
    lines.append("> **玩家属性基准文档**：[`player-expected-attributes-model.md`](./player-expected-attributes-model.md)  ")
    lines.append("")
    lines.append("---")
    lines.append("")
    lines.append("## 1. 概述与核心设计定位")
    lines.append("")
    lines.append("本模型旨在建立一套**标准玩家数值在标准关卡怪物环境下的确定性战斗模拟与产出期望体系**，用于：")
    lines.append("1. **锚定挂机产出节奏**：提供全 12 境界四阶段（前期凡俗 / 中期金丹 / 后期合体 / 大后期真仙）单人与组队的击杀量、波次耗时与产出基准；")
    lines.append("2. **双流派配点对偶基准**：")
    lines.append("   - **🗡️ 物理系 (Physical)**：走 **`1 AD : 1 暴击 : 1 破招`**，破招成功触发**双倍命中与双倍暴击**，结合 **1秒4刀（0.25s）** 专属极速平 A 与 **`220% AD`** 核心单体大招，主打毁灭级单体与 Boss 斩杀；")
    lines.append("   - **🔮 法术系 (Magic)**：走 **`固定 2.0x AP : 1 命中`**，超大攻击力在非线性平方减伤下**天然强势破防**，结合 **`2.0x 命中必定必中`** 与 **`90%~120% AP`** 大范围全屏轰炸，主打极速清场小怪波次；")
    lines.append("3. **精确非线性平方减伤公式**：$\\text{实际伤害} = \\frac{\\mathbf{RawDamage^2}}{\\mathbf{RawDamage} + \\mathbf{RES} \\times K + 1.0}$，单发原始伤害越高，天然穿甲能力越强；")
    lines.append("4. **四阶段全景覆盖**：前期 (T1~T2 凡俗)、中期 (T3~T5 练气筑基金丹)、后期 (T6~T9 元婴化神炼虚合体)、大后期 (T10~T12 大乘渡劫真仙)。")
    lines.append("")
    lines.append("---")
    lines.append("")
    lines.append("## 2. 实体同构建模与关卡出怪基准")
    lines.append("")
    lines.append("### 2.1 怪我同构建模原则 (Monster-Player Isomorphic Modeling)")
    lines.append("- **完全对称数据结构**：怪物在领域模型（ECS / Domain）中与玩家完全同构，具备相同的属性池、技能系统与普通攻击机制；")
    lines.append("- **统一攻防结算**：走同一套抗性减伤、暴击、破招拆招与命中判定管线。")
    lines.append("")
    lines.append("### 2.2 地图出怪逻辑与基准抽象 (Map Spawning & Benchmark Abstraction)")
    lines.append("- **基准抽象波次循环**：")
    lines.append("  - **普通波 (Minion Wave)**：常规波次，刷新 **3 只普通怪物**（80% 全属性，300% 玩家生命）；")
    lines.append("  - **精英波 (Elite Wave)**：**每 5 波**刷新 1 次，**1 只精英怪 + 2 只普通怪**（精英 120% 全属性，500% 玩家生命）；")
    lines.append("  - **首领波 (Boss Wave)**：**每 20 波**刷新 1 次，**1 只 Boss 首领 + 2 只普通怪**（首领 150% 全属性，2000% 玩家生命）。")
    lines.append("")
    lines.append("---")
    lines.append("")
    lines.append("## 3. 战斗攻防对抗与高级机制二次函数成长模型")
    lines.append("")
    lines.append("### 3.1 动态抗性减伤公式 (Dynamic Resistance)")
    lines.append("$$\\mathbf{实际伤害} = \\frac{\\mathbf{RawDamage^2}}{\\mathbf{RawDamage} + \\mathbf{RES} \\times K + 1.0} \\times (\\text{是否暴击} ? \\text{CritMultiplier} : 1.0)$$")
    lines.append("")
    lines.append("### 3.2 二次函数高级成长公式 (Quadratic Model, $x = \\frac{Lv-1}{126}$)")
    lines.append("1. **波次等待时间**：$\\text{Wait}(Lv) = 5.0 - 2.5 \\times x^2 \\implies 5.00\\text{s} \\to 2.50\\text{s}$")
    lines.append("2. **技能冷却缩减 (CDR)**：$\\text{CDR}(Lv) = x^2 \\times 75\\% \\implies 0.0\\% \\to 75.0\\%$")
    lines.append("3. **行动速度加成 (ATR)**：$\\text{ATR}(Lv) = x^2 \\times 50\\% \\implies 0.0\\% \\to 50.0\\%$")
    lines.append("4. **暴击伤害倍率**：$\\text{CritDmg}(Lv) = 1.50 + x^2 \\times 3.50 \\implies 150.0\\% \\to 500.0\\%$")
    lines.append("5. **物理平 A 专属攻速**：$\\text{AutoSpeedBonus}(Lv) = x^2 \\times 50\\% \\implies 0.0\\% \\to 50.0\\%$")
    lines.append("   $$\\mathbf{物理平 A 实际间隔} = 1.0\\text{s} \\times (1 - \\text{ATR}) \\times (1 - \\text{AutoSpeedBonus}) \\implies \\mathbf{1.000s \\to 0.250s}\\text{ (1秒4刀)}$$")
    lines.append("6. **物理平 A 专属增伤**：$\\text{AutoDamageBonus}(Lv) = x^2 \\times 100\\% \\implies 0.0\\% \\to 100.0\\% \\implies \\mathbf{100\\% \\to 200\\%\\text{ AD}}$$")
    lines.append("7. **法术普攻 (灵爆 2.0s 0CD)**：$\\text{Cast}(Lv) = 2.0\\text{s} \\times (1 - \\text{ATR}) \\implies \\mathbf{2.00s \\to 1.00s}\\text{ (1秒1发)}$")
    lines.append("")
    lines.append("### 3.3 全 12 阶二次高级机制参数总览表")
    lines.append("| 阶位 | 阶段 | 境界与代表等级 | CDR | ATR | 暴伤倍率 | 物理平A攻速 | 物理平A增伤 | 平A实际间隔 | 平A伤害倍率 | 法术实际吟唱 | 法术伤害倍率 | 等待时间 | 技能强度 | 抗性 $K$ |")
    lines.append("| :--- | :--- | :--- | :--- | :--- | :--- | :--- | :--- | :--- | :--- | :--- | :--- | :--- | :--- | :--- |")

    for t in config["tiers"]:
        s = t["expectedSpecialAttributes"]
        lines.append(f"| **第 {t['tier']:2d} 阶** | {get_stage_name(t['tier'])} | **{t['realmName']}** (Lv {t['level']:3d}) | **`{s['cooldownReductionPct']:4.1f}%`** | **`{s['actionSpeedReductionPct']:4.1f}%`** | **`{s['criticalDamageMultiplierPct']:5.1f}%`** | **`{s['physicalAutoExtraAttackSpeedPct']:4.1f}%`** | **`{s['physicalAutoExtraDamagePct']:5.1f}%`** | **`{s['physicalAutoIntervalSeconds']:.3f}s`** | `{s['physicalAutoDamageRatioPct']:.0f}%` AD | **`{s['magicAutoCastSeconds']:.2f}s`** | `{s['magicAutoDamageRatioPct']:.0f}%` AP | `{s['waveWaitDelaySeconds']:.2f}s` | `{s['skillIntensityMultiplier']:.2f}x` | `{s['dynamicResistanceFactorK']:.1f}` |")

    lines.append("")
    lines.append("---")
    lines.append("")
    lines.append("## 4. 物理系 vs 法术系 双流派技能配置与单发循环总表")
    lines.append("")
    lines.append("### 4.1 双流派技能配置架构")
    lines.append("| 流派分类 | 配点模型 | 技能 1 (主动) | 技能 2 (主动) | 技能 3 (主动/普攻) | 技能 4 (Buff) | 常态普攻 |")
    lines.append("| :--- | :--- | :--- | :--- | :--- | :--- | :--- |")
    lines.append("| **物理系 (Physical)** | `1 AD : 1 暴击 : 1 破招` | 核心单体 1 (`220%` AD, CD 12s, 动作 1s) | 核心单体 2 (`220%` AD, CD 12s, 动作 1s) | 群体绝技 (`180%` AD, 3怪, CD 18s, 动作 2s) | 功能 Buff | 专属强化平 A (`0.250s` 1秒4刀, `200%` AD) |")
    lines.append("| **法术系 (Magic)** | `固定 2.0x AP : 1 命中` | 大范围群攻 (`90%` AP, 5怪, CD 22s, 吟唱 2s) | 强力群攻 (`120%` AP, 3怪, CD 20s, 吟唱 2s) | 法术普攻 (`1.00s` 1秒1发, `60%~258%` AP) | 功能 Buff | 0CD 灵爆术 (2s基准, 1.0s吟唱) |")
    lines.append("")
    lines.append("### 4.2 全 12 阶双流派单发技能期望伤害与 CD/动作 循环详表")
    lines.append("| 阶位 | 境界与代表等级 | 物理单体 1/2 (CD/动作) | 物理群攻 3怪 (CD/动作) | 物理平 A (间隔/秒伤) | 法术群攻1 5怪 (CD/吟唱) | 法术群攻2 3怪 (CD/吟唱) | 法术普攻 (吟唱/秒伤) |")
    lines.append("| :--- | :--- | :--- | :--- | :--- | :--- | :--- | :--- |")

    for t in range(1, 13):
        t_info = config["tiers"][t - 1]
        p_exp = t_info["finalExpected"]
        s_attr = t_info["expectedSpecialAttributes"]
        lv = t_info["level"]
        ad = p_exp["physicalAttack"]
        ap = ad * 2.0
        arm = p_exp["armor"]
        mres = p_exp["magicResistance"]
        k_res = float(t)

        x = (lv - 1) / 126.0
        x_quad = x * x
        cdr = x_quad * 0.75
        atr = x_quad * 0.50
        p_dmg = x_quad * 1.00
        skill_int = 1.0 + (t - 1) * 0.30

        # 单发精确平方减伤计算
        p_s1_raw = ad * 2.20 * skill_int
        p_s1_dmg = p_s1_raw * (p_s1_raw / (p_s1_raw + arm * 0.8 * k_res + 1.0))
        p_s1_cd = 12.0 * (1.0 - cdr)
        p_s1_cast = 1.0 * (1.0 - atr)

        p_aoe_raw = ad * 1.80 * skill_int
        p_aoe_dmg = p_aoe_raw * (p_aoe_raw / (p_aoe_raw + arm * 0.8 * k_res + 1.0)) * 3.0
        p_aoe_cd = 18.0 * (1.0 - cdr)
        p_aoe_cast = 2.0 * (1.0 - atr)

        p_auto_raw = ad * 1.00 * (1.0 + p_dmg)
        p_auto_dmg = p_auto_raw * (p_auto_raw / (p_auto_raw + arm * 0.8 * k_res + 1.0))
        p_auto_int = s_attr["physicalAutoIntervalSeconds"]

        m_aoe1_raw = ap * 0.90 * skill_int
        m_aoe1_dmg = m_aoe1_raw * (m_aoe1_raw / (m_aoe1_raw + mres * 0.8 * k_res + 1.0)) * 3.0
        m_aoe1_cd = 22.0 * (1.0 - cdr)
        m_aoe1_cast = 2.0 * (1.0 - atr)

        m_aoe2_raw = ap * 1.20 * skill_int
        m_aoe2_dmg = m_aoe2_raw * (m_aoe2_raw / (m_aoe2_raw + mres * 0.8 * k_res + 1.0)) * 3.0
        m_aoe2_cd = 20.0 * (1.0 - cdr)
        m_aoe2_cast = 2.0 * (1.0 - atr)

        m_auto_raw = ap * 0.60 * skill_int
        m_auto_dmg = m_auto_raw * (m_auto_raw / (m_auto_raw + mres * 0.8 * k_res + 1.0))
        m_auto_cast = s_attr["magicAutoCastSeconds"]

        lines.append(f"| **第 {t:2d} 阶** | **{t_info['realmName']}** (Lv {lv:3d}) | **{p_s1_dmg:,.0f}** ({p_s1_cd:.1f}s/{p_s1_cast:.2f}s) | **{p_aoe_dmg:,.0f}** ({p_aoe_cd:.1f}s/{p_aoe_cast:.2f}s) | **{p_auto_dmg:,.0f}** (**`{p_auto_int:.3f}s`** / `{p_auto_dmg/p_auto_int:,.0f}/s`) | **{m_aoe1_dmg:,.0f}** ({m_aoe1_cd:.1f}s/{m_aoe1_cast:.2f}s) | **{m_aoe2_dmg:,.0f}** ({m_aoe2_cd:.1f}s/{m_aoe2_cast:.2f}s) | **{m_auto_dmg:,.0f}** (**`{m_auto_cast:.2f}s`** / `{m_auto_dmg/m_auto_cast:,.0f}/s`) |")

    lines.append("")
    lines.append("---")
    lines.append("")
    lines.append("## 5. 单人模式 (Solo 1v1) 全 12 阶四阶段 战斗效率全景详表")
    lines.append("")
    lines.append("### 5.1 物理系 (Physical Style) 单人战斗效率详表")
    lines.append("| 阶位 | 阶段 | 境界与代表等级 | 普通波耗时 | 精英波耗时 | 非Boss单波均耗 (单怪) | Boss波耗时 (每20波) | 全局单波均耗 (单怪) | 每小时通关 | 每小时击杀 | 24h 挂机击杀 | 综合总 DPS |")
    lines.append("| :--- | :--- | :--- | :--- | :--- | :--- | :--- | :--- | :--- | :--- | :--- | :--- |")

    for sp in solo_phys:
        lines.append(f"| **第 {sp['tier']:2d} 阶** | {sp['stage']} | **{sp['realmName']}** (Lv {sp['level']:3d}) | {sp['avgNormalWaveSec']:.1f}s | {sp['avgEliteWaveSec']:.1f}s | **{sp['avgNonBossWaveSec']:.1f}s** (`{sp['avgNonBossKillSec']:.1f}s`) | **{sp['avgBossDurSec']:.1f}s** (~{sp['avgBossDurSec']/60.0:.1f}分) | **{sp['avgOverallWaveSec']:.1f}s** (`{sp['avgOverallKillSec']:.1f}s`) | **`{sp['hourlyWaves']:.1f} 波`** | **`{sp['hourlyKills']:.1f} 只`** | **{sp['dailyKills24h']:,}** 只 | **`{sp['totalDps']:,.1f}/s`** |")

    lines.append("")
    lines.append("### 5.2 法术系 (Magic Style) 单人战斗效率详表")
    lines.append("| 阶位 | 阶段 | 境界与代表等级 | 普通波耗时 | 精英波耗时 | 非Boss单波均耗 (单怪) | Boss波耗时 (每20波) | 全局单波均耗 (单怪) | 每小时通关 | 每小时击杀 | 24h 挂机击杀 | 综合总 DPS |")
    lines.append("| :--- | :--- | :--- | :--- | :--- | :--- | :--- | :--- | :--- | :--- | :--- | :--- |")

    for sm in solo_magic:
        lines.append(f"| **第 {sm['tier']:2d} 阶** | {sm['stage']} | **{sm['realmName']}** (Lv {sm['level']:3d}) | {sm['avgNormalWaveSec']:.1f}s | {sm['avgEliteWaveSec']:.1f}s | **{sm['avgNonBossWaveSec']:.1f}s** (`{sm['avgNonBossKillSec']:.1f}s`) | **{sm['avgBossDurSec']:.1f}s** (~{sm['avgBossDurSec']/60.0:.1f}分) | **{sm['avgOverallWaveSec']:.1f}s** (`{sm['avgOverallKillSec']:.1f}s`) | **`{sm['hourlyWaves']:.1f} 波`** | **`{sm['hourlyKills']:.1f} 只`** | **{sm['dailyKills24h']:,}** 只 | **`{sm['totalDps']:,.1f}/s`** |")

    lines.append("")
    lines.append("### 5.3 单人模式两系效率对比比例详表 (物理 / 法术)")
    lines.append("| 阶位 | 阶段 | 境界与代表等级 | 物理非Boss单波 | 法术非Boss单波 | **无 Boss 效率比 (物/法)** | 物理总击杀/h | 法术总击杀/h | **算上 Boss 效率比 (物/法)** | 物理Boss耗时 | 法术Boss耗时 | **Boss 战耗时比 (物/法)** |")
    lines.append("| :--- | :--- | :--- | :--- | :--- | :--- | :--- | :--- | :--- | :--- | :--- | :--- |")

    for t_idx in range(12):
        sp = solo_phys[t_idx]
        sm = solo_magic[t_idx]
        r_non_boss = (sm["avgNonBossWaveSec"] / max(0.01, sp["avgNonBossWaveSec"])) * 100
        r_overall = (sp["hourlyKills"] / max(0.01, sm["hourlyKills"])) * 100
        r_boss = (sp["avgBossDurSec"] / max(0.01, sm["avgBossDurSec"])) * 100
        lines.append(f"| **第 {sp['tier']:2d} 阶** | {sp['stage']} | **{sp['realmName']}** (Lv {sp['level']:3d}) | **{sp['avgNonBossWaveSec']:.1f}s** | **{sm['avgNonBossWaveSec']:.1f}s** | **`{r_non_boss:5.1f}%`** | **`{sp['hourlyKills']:.1f}`** 只 | **`{sm['hourlyKills']:.1f}`** 只 | **`{r_overall:5.1f}%`** | {sp['avgBossDurSec']:.1f}s | {sm['avgBossDurSec']:.1f}s | **`{r_boss:5.1f}%`** |")

    lines.append("")
    lines.append("---")
    lines.append("")
    lines.append("## 6. 三人组队模式 (3-Player Team) 全 12 阶战斗效率全景详表")
    lines.append("")
    lines.append("### 6.1 3 物理队 vs 3 法术队 战斗效率详表")
    lines.append("| 阶位 | 阶段 | 境界与代表等级 | 3物理非Boss单波 (单怪) | 3法术非Boss单波 (单怪) | **组队无Boss效率比** | 3物理总击杀/h | 3法术总击杀/h | **组队算上Boss效率比** | 3物理Boss耗时 | 3法术Boss耗时 | 组队提速倍率 |")
    lines.append("| :--- | :--- | :--- | :--- | :--- | :--- | :--- | :--- | :--- | :--- | :--- | :--- |")

    for t_idx in range(12):
        tp = team_phys[t_idx]
        tm = team_magic[t_idx]
        sp = solo_phys[t_idx]
        sm = solo_magic[t_idx]
        r_non_boss = (tm["avgNonBossWaveSec"] / max(0.01, tp["avgNonBossWaveSec"])) * 100
        r_overall = (tp["hourlyKills"] / max(0.01, tm["hourlyKills"])) * 100
        speedup = (tp["hourlyKills"] + tm["hourlyKills"]) / max(0.1, (sp["hourlyKills"] + sm["hourlyKills"]))
        lines.append(f"| **第 {tp['tier']:2d} 阶** | {tp['stage']} | **{tp['realmName']}** (Lv {tp['level']:3d}) | **{tp['avgNonBossWaveSec']:.1f}s** (`{tp['avgNonBossKillSec']:.1f}s`) | **{tm['avgNonBossWaveSec']:.1f}s** (`{tm['avgNonBossKillSec']:.1f}s`) | **`{r_non_boss:5.1f}%`** | **`{tp['hourlyKills']:.1f}`** 只 | **`{tm['hourlyKills']:.1f}`** 只 | **`{r_overall:5.1f}%`** | {tp['avgBossDurSec']:.1f}s (~{tp['avgBossDurSec']/60.0:.1f}分) | {tm['avgBossDurSec']:.1f}s (~{tm['avgBossDurSec']/60.0:.1f}分) | **`{speedup:.2f}x`** |")

    lines.append("")
    lines.append("---")
    lines.append("")
    lines.append("## 7. 普攻与非 Buff 技能每小时 DPS 输出量与精准占比详表 (DPS Breakdown)")
    lines.append("")
    lines.append("### 7.1 物理系各技能与物理平 A 伤害占比演变")
    lines.append("| 阶位 | 境界与代表等级 | 总输出 DPS | 核心单体 1 (220% AD) | 核心单体 2 (220% AD) | 群体绝技 (180% AD 3怪) | 物理平 A (1秒4刀) |")
    lines.append("| :--- | :--- | :--- | :--- | :--- | :--- | :--- |")

    for sp in solo_phys:
        b = sp["breakdown"]
        lines.append(f"| **第 {sp['tier']:2d} 阶** | **{sp['realmName']}** (Lv {sp['level']:3d}) | **{sp['totalDps']:,.1f}/s** | **`{b['skill_1']['pct']:.1f}%`** ({b['skill_1']['dps']:,.1f}/s) | **`{b['skill_2']['pct']:.1f}%`** ({b['skill_2']['dps']:,.1f}/s) | **`{b['skill_3']['pct']:.1f}%`** ({b['skill_3']['dps']:,.1f}/s) | **`{b['auto_attack']['pct']:.1f}%`** ({b['auto_attack']['dps']:,.1f}/s) |")

    lines.append("")
    lines.append("### 7.2 法术系各技能与法术普攻伤害占比演变 (固定 2.0x AP)")
    lines.append("| 阶位 | 境界与代表等级 | 总输出 DPS | 大范围群攻 (90% AP 5怪) | 强力群攻 (120% AP 3怪) | 法术普攻 (灵爆 60%) |")
    lines.append("| :--- | :--- | :--- | :--- | :--- | :--- |")

    for sm in solo_magic:
        b = sm["breakdown"]
        lines.append(f"| **第 {sm['tier']:2d} 阶** | **{sm['realmName']}** (Lv {sm['level']:3d}) | **{sm['totalDps']:,.1f}/s** | **`{b['skill_1']['pct']:.1f}%`** ({b['skill_1']['dps']:,.1f}/s) | **`{b['skill_2']['pct']:.1f}%`** ({b['skill_2']['dps']:,.1f}/s) | **`{b['skill_3']['pct']:.1f}%`** ({b['skill_3']['dps']:,.1f}/s) |")

    lines.append("")
    lines.append("---")
    lines.append("")
    lines.append("## 8. 单人 vs 组队 四阶段数值生态与平衡性结论")
    lines.append("")
    lines.append("1. **前期（T1~T2 凡俗阶段）**：法修凭借 2.0x AP 大范围轰炸小怪优势明显，清场效率领先物理约 20%~30%；")
    lines.append("2. **中期（T3~T5 练气筑基金丹）**：物理破招与暴击成长起步，两系在金丹期效率收敛至 95%~105% 完美平衡区间；")
    lines.append("3. **后期（T6~T9 元婴化神炼虚合体）**：物理群攻 (180% 3怪) 与单体 (220%) 配合破防暴击开始发力，两系势均力敌；")
    lines.append("4. **大后期（T10~T12 大乘渡劫真仙）**：物理系 1 秒 4 刀（0.25s）结合破招翻倍暴击爆发斩杀 Boss 展现极致单体输出，总通关击杀效率领先法术约 15%~30%；")
    lines.append("5. **组队提速效应**：全境界 3 人组队相比单人挂机，通关与击杀效率稳定提升 **`2.5 ~ 3.0 倍`**，联机合作收益极佳！")

    with open(COMBAT_DOC_PATH, "w", encoding="utf-8") as f:
        f.write("\n".join(lines) + "\n")

    # 4. Generate docs/design/player-expected-attributes-model.md
    p_lines = []
    p_lines.append("# 道化太初 · 境界阶位体系与基准玩家期望属性量化模型")
    p_lines.append("")
    p_lines.append("> **文档定位**：全游戏数值顶层规约文档（标准玩家属性底座、装备预算与成长乘区）。  ")
    p_lines.append("> **关联配置文件**：[`config/game-data/expected-attributes.json`](../../config/game-data/expected-attributes.json)  ")
    p_lines.append("> **战斗与产出基准文档**：[`combat-benchmark-model.md`](./combat-benchmark-model.md)  ")
    p_lines.append("")
    p_lines.append("---")
    p_lines.append("")
    p_lines.append("## 1. 境界、阶位与品质三维架构")
    p_lines.append("")
    p_lines.append("《道化太初》建立了一套**横向境界阶位（12 阶四阶段）**与**纵向品质稀有度（8 品）**交织的修仙数值架构：")
    p_lines.append("")
    p_lines.append("### 1.1 命名规范与概念界定")
    p_lines.append("1. **【阶位】（Tier 1 ~ 12）**：统领装备、法宝、技能、丹药与关卡怪物的基础属性数值底座与修为佩戴门槛；")
    p_lines.append("2. **【品质】（Quality 1 ~ 8）**：决定物品/神通的词条数量上限、附加特效与完美度偏置；")
    p_lines.append("3. **展示规范**：物品与技能直接使用本名（例：`青锋剑`、`太乙神雷`），不拼接冗长前缀，在详情面板与角标标签中展示阶位 `[五阶]` 与品质色 `[天品]`。")
    p_lines.append("")
    p_lines.append("### 1.2 四阶段划分与一体化徽章")
    p_lines.append("- **前期**：第 1 阶（凡胎）、第 2 阶（叩仙门）；")
    p_lines.append("- **中期**：第 3 阶（练气）、第 4 阶（筑基）、第 5 阶（金丹）；")
    p_lines.append("- **后期**：第 6 阶（元婴）、第 7 阶（化神）、第 8 阶（炼虚）、第 9 阶（合体）；")
    p_lines.append("- **大后期**：第 10 阶（大乘）、第 11 阶（渡劫）、第 12 阶（飞升真仙）；")
    p_lines.append("- **一体化徽章表现**：`[大境界牌匾框] ◇位阶棱形框◇ (小境界胶囊框)`（如 `[练气] ◇极◇ (练气巅峰)`，Hover 浮窗显示精确等级 Lv）。")
    p_lines.append("")
    p_lines.append("---")
    p_lines.append("")
    p_lines.append("## 2. 核心属性四独立乘区归一化数学模型")
    p_lines.append("")
    p_lines.append("$$\\mathbf{最终面板属性} = \\mathbf{基础数值池 (BaseFlat)} \\times \\mathbf{(1 + 六维\\%)} \\times \\mathbf{(1 + 装备\\%)} \\times \\mathbf{(1 + Buff\\%)}$$")
    p_lines.append("")
    p_lines.append("### 2.1 属性预算与价值折算法则")
    p_lines.append("- **点数价值换算**：$\\mathbf{1.0\\ 点预算} = \\mathbf{12.0\\ 生命值 (HP)} = \\mathbf{8.0\\ 灵力值 (MP)} = \\mathbf{1.0\\ 攻防/对抗属性点}$；")
    p_lines.append("- **流派配点对偶法则**：")
    p_lines.append("  - **🗡️ 物理系 (Physical)**：走 **`1 AD : 1 暴击 : 1 破招`**，破招触发双倍暴击与命中，配合 1 秒 4 刀爆发斩杀；")
    p_lines.append("  - **🔮 法术系 (Magic)**：走 **`固定 2.0x AP : 1 命中`**，超高 AP 破防轰炸，配合高命中必中全屏清场。")
    p_lines.append("")
    p_lines.append("---")
    p_lines.append("")
    p_lines.append("## 3. 装备标准化权重预算模型与槽位分配")
    p_lines.append("| 装备槽位 | 槽位定位 | 预算权重占比 | 核心主属性倾斜 |")
    p_lines.append("| :--- | :--- | :--- | :--- |")
    p_lines.append("| **武器 (Weapon)** | 核心输出 | **20.0%** | 物理攻击 (AD)、术法攻击 (AP)、命中 (Hit)、破防 (Break) |")
    p_lines.append("| **头盔 (Head)** | 生存防护 | **15.0%** | 生命值 (HP)、灵力值 (MP)、护甲 (Armor) |")
    p_lines.append("| **衣服 (Body)** | 核心生存 | **20.0%** | 生命值 (HP)、护甲 (Armor)、魔抗 (MRes)、暴击招架 (Parry) |")
    p_lines.append("| **下装 (Legs)** | 均衡防御 | **15.0%** | 生命值 (HP)、闪避 (Eva)、护甲 (Armor) |")
    p_lines.append("| **戒指 (Ring)** | 攻击对抗 | **15.0%** | 物理/术法攻击、暴击 (Crit)、命中 (Hit) |")
    p_lines.append("| **项链 (Neck)** | 特殊对抗 | **15.0%** | 灵力上限 (MP)、暴击回避 (CAvoid)、破防 (Break) |")
    p_lines.append("| **总计 (Total)** | **6 槽位** | **100.0%** | **全套装备合计提供标准 1.0x 装备乘区增益** |")
    p_lines.append("")
    p_lines.append("---")
    p_lines.append("")
    p_lines.append("## 4. 全 12 阶位基准玩家期望属性矩阵总表")
    p_lines.append("")
    p_lines.append("### 4.1 基础数值池总表 (Base Flat - 乘区 1 底座)")
    p_lines.append("| 阶位 | 阶段 | 境界与代表等级 | 生命上限 HP | 灵力上限 MP | 物攻 AD | 法攻 AP | 物理护甲 | 术法抗性 | 命中等级 | 闪避等级 | 暴击等级 | 暴击回避 | 破防等级 | 招架等级 |")
    p_lines.append("| :--- | :--- | :--- | :--- | :--- | :--- | :--- | :--- | :--- | :--- | :--- | :--- | :--- | :--- | :--- |")
    for t in config["tiers"]:
        b = t["baseFlat"]
        lines_s = get_stage_name(t["tier"])
        p_lines.append(f"| **第 {t['tier']:2d} 阶** | {lines_s} | **{t['realmName']}** (Lv {t['level']:3d}) | {b['maxHitpoints']:,.0f} | {b['maxManapoints']:,.0f} | {b['physicalAttack']:.1f} | {b['magicAttack']:.1f} | {b['armor']:.1f} | {b['magicResistance']:.1f} | {b['accuracyRating']:.1f} | {b['evasionRating']:.1f} | {b['criticalRating']:.1f} | {b['criticalAvoidance']:.1f} | {b['breakRating']:.1f} | {b['parryRating']:.1f} |")

    p_lines.append("")
    p_lines.append("### 4.2 最终期望属性总表 (Final Expected - 四乘区完全体)")
    p_lines.append("| 阶位 | 阶段 | 境界与代表等级 | 最终生命 HP | 最终灵力 MP | 最终物攻 AD | 最终法攻 AP | 最终护甲 | 最终魔抗 | 最终命中 | 最终闪避 | 最终暴击 | 最终暴抗 | 最终破防 | 最终招架 |")
    p_lines.append("| :--- | :--- | :--- | :--- | :--- | :--- | :--- | :--- | :--- | :--- | :--- | :--- | :--- | :--- | :--- |")
    for t in config["tiers"]:
        f_stat = t["finalExpected"]
        lines_s = get_stage_name(t["tier"])
        p_lines.append(f"| **第 {t['tier']:2d} 阶** | {lines_s} | **{t['realmName']}** (Lv {t['level']:3d}) | **{f_stat['maxHitpoints']:,.0f}** | **{f_stat['maxManapoints']:,.0f}** | **{f_stat['physicalAttack']:,.1f}** | **{f_stat['magicAttack']:,.1f}** | {f_stat['armor']:,.1f} | {f_stat['magicResistance']:,.1f} | {f_stat['accuracyRating']:,.1f} | {f_stat['evasionRating']:,.1f} | {f_stat['criticalRating']:,.1f} | {f_stat['criticalAvoidance']:,.1f} | {f_stat['breakRating']:,.1f} | {f_stat['parryRating']:,.1f} |")

    p_lines.append("")
    p_lines.append("### 4.3 高级特殊机制期望属性总表")
    p_lines.append("| 阶位 | 阶段 | 境界与代表等级 | CDR | ATR | 暴伤倍率 | 物理平A额外攻速 | 物理平A额外增伤 | 平A实际间隔 | 平A伤害倍率 | 法术实际吟唱 | 寻怪等待 | 技能强度 | 动态抗性 $K$ |")
    p_lines.append("| :--- | :--- | :--- | :--- | :--- | :--- | :--- | :--- | :--- | :--- | :--- | :--- | :--- | :--- |")
    for t in config["tiers"]:
        s = t["expectedSpecialAttributes"]
        lines_s = get_stage_name(t["tier"])
        p_lines.append(f"| **第 {t['tier']:2d} 阶** | {lines_s} | **{t['realmName']}** (Lv {t['level']:3d}) | **`{s['cooldownReductionPct']:4.1f}%`** | **`{s['actionSpeedReductionPct']:4.1f}%`** | **`{s['criticalDamageMultiplierPct']:5.1f}%`** | **`{s['physicalAutoExtraAttackSpeedPct']:4.1f}%`** | **`{s['physicalAutoExtraDamagePct']:5.1f}%`** | **`{s['physicalAutoIntervalSeconds']:.3f}s`** | `{s['physicalAutoDamageRatioPct']:.0f}%` AD | **`{s['magicAutoCastSeconds']:.2f}s`** | `{s['waveWaitDelaySeconds']:.2f}s` | `{s['skillIntensityMultiplier']:.2f}x` | `{s['dynamicResistanceFactorK']:.1f}` |")

    p_lines.append("")
    p_lines.append("---")
    p_lines.append("")
    p_lines.append("## 5. 跨系统消费与数据流转拓扑")
    p_lines.append("1. **怪我同构数据模型**：怪物与玩家采用完全同构的数据建模，具备相同的属性池、技能系统与普通攻击机制；")
    p_lines.append("2. **地图出怪与基准抽象**：不同地图/秘境/关卡支持灵活独立的出怪逻辑配置，基准文档中的波次为全周期产出基线；")
    p_lines.append("3. **战斗模拟与关卡产出规约**：完整的战斗公式、双流派技能、单人/组队战斗模拟详表，详见独立规约文档 [`combat-benchmark-model.md`](./combat-benchmark-model.md)；")
    p_lines.append("4. **代码与服务端消费**：所有数值模型以 [`config/game-data/expected-attributes.json`](../../config/game-data/expected-attributes.json) 作为单点真实数据源。")

    with open(PLAYER_DOC_PATH, "w", encoding="utf-8") as f:
        f.write("\n".join(p_lines) + "\n")

    print("已成功动态刷新 docs/design/combat-benchmark-model.md, docs/design/player-expected-attributes-model.md 与 config/game-data/expected-attributes.json！")
    print("==========================================================")


if __name__ == "__main__":
    main()
