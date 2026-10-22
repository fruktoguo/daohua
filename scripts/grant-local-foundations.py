#!/usr/bin/env python3
"""
本地开发专用：一键为本地 PostgreSQL 数据库中所有角色注入 1 亿底蕴。
用法：在服务端停止时执行，或在开发重置后执行。
"""
import subprocess, json, brotli, hashlib, sys

def grant_all(amount=100_000_000.0):
    cmd = ["docker", "exec", "daohua-local-db", "psql", "-U", "daohua", "-d", "daohua", "-t", "-A", "-c",
           "SELECT player_id, codec, encode(payload, 'hex') FROM game.player_state_sections WHERE section_kind='progression';"]
    try:
        out = subprocess.check_output(cmd).decode('utf-8').strip().splitlines()
    except Exception as e:
        print(f"读取数据库失败: {e}", file=sys.stderr)
        return

    count = 0
    for line in out:
        if not line.strip(): continue
        parts = line.split('|')
        if len(parts) != 3: continue
        pid, codec, hex_payload = parts
        raw_bytes = bytes.fromhex(hex_payload)
        decompressed = brotli.decompress(raw_bytes) if codec == 'br-json' else raw_bytes
        doc = json.loads(decompressed.decode('utf-8'))
        doc['foundation'] = max(float(doc.get('foundation', 0)), amount)
        new_raw = json.dumps(doc, separators=(',', ':'), ensure_ascii=False).encode('utf-8')
        new_checksum = hashlib.sha256(new_raw).hexdigest()
        new_compressed = brotli.compress(new_raw)
        sql = f"""UPDATE game.player_state_sections 
                 SET payload = decode('{new_compressed.hex()}', 'hex'), 
                     raw_bytes = {len(new_raw)}, 
                     stored_bytes = {len(new_compressed)}, 
                     checksum = '{new_checksum}', 
                     codec = 'br-json', 
                     updated_at = NOW() 
                 WHERE player_id = '{pid}' AND section_kind = 'progression';"""
        subprocess.check_call(["docker", "exec", "daohua-local-db", "psql", "-U", "daohua", "-d", "daohua", "-c", sql],
                              stdout=subprocess.DEVNULL)
        count += 1
    print(f"成功为本地全部 {count} 个角色注入 {amount:,.0f} 底蕴！")

if __name__ == '__main__':
    grant_all()
