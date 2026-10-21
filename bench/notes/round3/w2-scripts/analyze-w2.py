"""Round-3 wave-2 A/B tables: r3w1 (wave-1 tip binary) vs r3w2 (wave-2 tip) vs knob arms r3w2prefer / r3w2pull0 / r3w2antig1 / r3w2legs0.
Pooled samples across sessions of a mirror; analyst bootstrap CI on the ratio of medians."""
import glob, json, os, random, statistics
S = r'C:\Users\cdupu\AppData\Local\Temp\claude\C--Users-cdupu--claude-spares-spare-dUbXep\dbd3f5e5-01ed-41dd-bc92-4bc0ec85152b\scratchpad\round3\gates\ab'
MIRRORS = ['r3w1', 'r3w2', 'r3w2prefer', 'r3w2pull0', 'r3w2antig1', 'r3w2legs0', 'r3w2retain', 'r3w2g35']
LABEL = {'r3w1': 'wave-1 tip 9da403a8 binary + wave-2 bench', 'r3w2': 'wave-2 tip, defaults',
         'r3w2prefer': 'BLADE_FOLD_ROW_SLOTS=prefer (old precedence: row slots over the scatter)',
         'r3w2pull0': 'BLADE_ORBIT_PULL=0 (serial ordered scatter, no row-owned pull)',
         'r3w2antig1': 'BLADE_TILE_TUNE=anti_g=1 (old default in every regime)',
         'r3w2legs0': 'BLADE_EPILOGUE_LEGS=0 (separate joined input-sum pass)',
         'r3w2retain': 'BLADE_POOL_RETAIN=1 (A4 opt-in retention arm, 64 MB / 96 MB per thread)',
         'r3w2g35': 'BLADE_ORBIT_PULL=0 + BLADE_FOLD_ROW_SLOTS=prefer (the G35 row slots at rank 3: pull off AND the old precedence)'}
CELLS = [
    ('pairkernel', 'mt8-repro-native', ['r3w1', 'r3w2', 'r3w2prefer', 'r3w2pull0'], 'W2-0a precedence (rank 2: pull falls through)'),
    ('pairkernel', 'serial-repro-native', ['r3w1', 'r3w2', 'r3w2prefer', 'r3w2pull0'], 'control (must agree)'),
    ('comoment3', 'mt8-repro-native', ['r3w1', 'r3w2', 'r3w2prefer', 'r3w2pull0', 'r3w2g35'], 'W2-3 pull vs scatter (pull0) vs row slots (g35 = pull0 + prefer; prefer alone still pulls)'),
    ('comoment3', 'serial-repro-native', ['r3w1', 'r3w2', 'r3w2prefer', 'r3w2pull0'], 'control (must agree)'),
    ('antisym', 'serial-licensed-native', ['r3w1', 'r3w2', 'r3w2antig1'], 'W2-0b anti_g per regime (serial: default 1 = antig1)'),
    ('antisym', 'mt8-licensed-native', ['r3w1', 'r3w2', 'r3w2antig1'], 'W2-0b anti_g per regime (mt8: default auto vs 1)'),
    ('saxpy', 'serial-repro-native', ['r3w1', 'r3w2', 'r3w2legs0'], 'W2-4 legs (target cell)'),
    ('saxpy', 'serial-licensed-native', ['r3w1', 'r3w2', 'r3w2legs0'], 'W2-4 legs (target cell)'),
    ('saxpy', 'mt8-repro-native', ['r3w1', 'r3w2', 'r3w2legs0'], 'W2-4 decline control (threaded producer)'),
    ('saxpy', 'mt8-licensed-native', ['r3w1', 'r3w2', 'r3w2legs0'], 'W2-4 decline control (threaded producer)'),
    ('haloadjoint', 'serial-licensed-native', ['r3w1', 'r3w2'], 'new kernel N02'),
    ('haloadjoint', 'mt8-licensed-native', ['r3w1', 'r3w2'], 'new kernel N02'),
    ('haloadjoint', 'serial-repro-native', ['r3w1', 'r3w2'], 'new kernel N02'),
    ('haloadjoint', 'mt8-repro-native', ['r3w1', 'r3w2'], 'new kernel N02'),
    ('stencil', 'mt8-repro-native', ['r3w1', 'r3w2', 'r3w2retain'], 'control (row slots unaffected); A4 retention arm'),
    ('stencil', 'mt8-licensed-native', ['r3w1', 'r3w2', 'r3w2retain'], 'A4 retention arm (24 MB lap)'),
    ('recur', 'mt8-licensed-native', ['r3w1', 'r3w2', 'r3w2retain'], 'A4 retention arm (48 MB trajectory)'),
    ('recur', 'mt8-repro-native', ['r3w1', 'r3w2', 'r3w2retain'], 'A4 retention arm (48 MB trajectory)'),
    ('gram', 'serial-licensed-native', ['r3w1', 'r3w2'], 'control'),
]
PAIRS = [('r3w2', 'r3w1'), ('r3w2prefer', 'r3w2'), ('r3w2pull0', 'r3w2'), ('r3w2antig1', 'r3w2'), ('r3w2legs0', 'r3w2'), ('r3w2retain', 'r3w2'), ('r3w2g35', 'r3w2'), ('r3w2g35', 'r3w2pull0')]
ARMS = ['blade-llvm', 'blade-cpp', 'gfortran-natural', 'gfortran-expert', 'ifx-natural', 'ifx-expert']
rng = random.Random(20261021)


def load(m):
    out = {}
    for f in sorted(glob.glob(r'C:\bt_r2ab\%s\bench\results\*\session-*\session.json' % m)):
        d = json.load(open(f, encoding='utf8'))
        a = d['argv']
        if 'bench' not in a:
            continue
        k = a[a.index('--kernels') + 1]
        out.setdefault(k, []).append((os.path.dirname(f), d))
    return out


def samples(arm):
    return [s['time_s'] for s in arm['samples'] if s['kind'] == 'round' and s['ok'] and not s.get('excursion_dropped')]


def med(x):
    return statistics.median(x)


def boot(a, b, n=4000):
    rs = []
    for _ in range(n):
        ra = [a[rng.randrange(len(a))] for _ in a]
        rb = [b[rng.randrange(len(b))] for _ in b]
        rs.append(med(rb) / med(ra))
    rs.sort()
    return rs[int(.025 * n)], rs[int(.975 * n) - 1]


def ms(x):
    return '--' if x is None else '%.4g' % (x * 1e3)


def fr(r):
    return '--' if r is None else '%.3f [%.3f, %.3f] %s' % (r['ratio'], r['ci95'][0], r['ci95'][1], r['verdict'])


def blocks(sess, k, reg):
    """every block of (kernel, regime) across sessions, oldest first"""
    out = []
    for p, d in sess.get(k, []):
        for b in d['blocks']:
            if b['regime'] == reg:
                out.append((p, d, b))
    return out


def pooled(bs, arm):
    """pooled timed samples of an arm across blocks; None if any block lacks it or it is not ok"""
    xs = []
    for p, d, b in bs:
        a = b['arms'].get(arm)
        if a is None or a['status'] != 'ok':
            return None
        xs += samples(a)
    return xs or None


data = {m: load(m) for m in MIRRORS}
T_ratio, T_arms, T_exp, T_blk, T_sess, SLOW, MISSING = [], [], [], [], [], [], []
T_peak = []
for k, reg, ms_, item in CELLS:
    B = {m: blocks(data[m], k, reg) for m in ms_}
    for m in ms_:
        if not B[m]:
            MISSING.append('| %s | %s | %s | not completed |' % (k, reg, m))
    ref = 'blade-cpp' if reg.startswith('mt8') else 'blade-llvm'
    for m in ms_:
        for p, d, b in B[m]:
            cls = 'indicative' if b['suppressed'] else 'live'
            co = b['controls']
            T_blk.append('| %s | %s | %s | %s | %s | %.3f | %.3f | %s | %d | %s |' % (
                k, reg, m, b['rounds']['n'], cls, co['compute']['median'] * 1e3, co['memory']['median'] * 1e3,
                '; '.join(b['flags']) or '-', len(b.get('excursions') or []), os.path.basename(p)))
            row = '| %s | %s | %s | %s |' % (k, reg, m, cls)
            for arm in ARMS:
                a = b['arms'].get(arm)
                row += ' n/a |' if a is None else (' %s%s |' % (a['status'], (': ' + a['reason'][:50]) if a.get('reason') else '') if a['status'] != 'ok' else ' %s |' % ms(med(samples(a))))
            T_arms.append(row)
            prow = '| %s | %s | %s | %s |' % (k, reg, m, cls)
            for arm in ARMS:
                a = b['arms'].get(arm)
                ws = [s.get('peak_working_set') for s in a.get('samples', []) if s.get('peak_working_set')] if a else []
                prow += ' %s |' % ('%.1f' % (max(ws) / 2**20) if ws else 'n/a')
            T_peak.append(prow)
            T_exp.append('| %s | %s | %s | %s | %s | %s | %s |' % (k, reg, m, ref, fr(a and None) if False else fr(b['arms'].get('gfortran-expert', {}).get('ratio') if b['arms'].get('gfortran-expert', {}).get('status') == 'ok' else None), fr(b['arms'].get('ifx-expert', {}).get('ratio') if b['arms'].get('ifx-expert', {}).get('status') == 'ok' else None), cls))
    for Bm, Am in PAIRS:
        if Bm not in ms_ or Am not in ms_ or not B[Bm] or not B[Am]:
            continue
        for arm in ARMS:
            sa = pooled(B[Am], arm); sb = pooled(B[Bm], arm)
            if sa is None or sb is None:
                if arm.startswith('blade'):
                    T_ratio.append('| %s | %s | %s | %s/%s | %s | -- | -- | (arm not ok in a block) |' % (k, reg, item, Bm, Am, arm))
                continue
            ma = med(sa); mb = med(sb); r = mb / ma
            lo, hi = boot(sa, sb)
            cls = 'live' if not any(b['suppressed'] for _, _, b in B[Am] + B[Bm]) else 'ind'
            T_ratio.append('| %s | %s | %s | %s/%s | %s | %s -> %s | %.3f [%.3f, %.3f] | %s %d/%d |' % (
                k, reg, item, Bm, Am, arm, ms(ma), ms(mb), r, lo, hi, cls, len(sa), len(sb)))
            if arm.startswith('blade') and lo > 1 and (Bm, Am) == ('r3w2', 'r3w1'):
                SLOW.append((k, reg, arm, ma, mb, r, lo, hi, cls))
for m in MIRRORS:
    for k, lst in data[m].items():
        for p, d in lst:
            T_sess.append('| %s | %s | %s | %s | %s | %.0f | %s | %s |' % (k, m, p, d['started'], d['aborted'], d['duration_s'], d['failures'] or 'none', '; '.join(d['flags']) or 'none'))

o = ['# Round-3 wave-2 harness A/B tables (generated by analyze-w2.py)\n', 'Mirrors: ' + '; '.join('%s = %s' % (m, LABEL[m]) for m in MIRRORS) + '\n',
     '## 1. Pairwise medians: B/A < 1 = B faster (samples pooled across sessions of a mirror). CI = analyst unpaired bootstrap.\n',
     '| kernel | regime | item | B/A | arm | A -> B median (ms) | B/A [95% CI] | class n_A/n_B |\n|---|---|---|---|---|---|---|---|'] + T_ratio
o += ['\n## 2. Arm medians per block (ms)\n', '| kernel | regime | mirror | block | ' + ' | '.join(ARMS) + ' |\n|---|---|---|---|' + '---|' * len(ARMS)] + T_arms
o += ['\n## 3. Expert ratios (expert median / Blade ref median; > 1 Blade faster); harness ratio [CI] verdict\n',
      '| kernel | regime | mirror | ref | gfortran-expert | ifx-expert | block |\n|---|---|---|---|---|---|---|'] + T_exp
o += ['\n## 4. Blocks\n', '| kernel | regime | mirror | rounds | class | compute ctl | memory ctl | flags | excursions | bundle |\n|---|---|---|---|---|---|---|---|---|---|'] + T_blk
o += ['\n## 5. r3w2 slower than r3w1 beyond the CI (Blade arms)\n']
o += (['| kernel | regime | arm | old ms | new ms | new/old [CI] | class |\n|---|---|---|---|---|---|---|'] + ['| %s | %s | %s | %s | %s | %.3f [%.3f, %.3f] | %s |' % (k, reg, arm, ms(ma), ms(mb), r, lo, hi, c) for k, reg, arm, ma, mb, r, lo, hi, c in SLOW]) if SLOW else ['none']
o += ['\n## 6. Missing cells\n'] + ((['| kernel | regime | mirror | status |\n|---|---|---|---|'] + MISSING) if MISSING else ['none'])
o += ['\n## 7. Sessions\n', '| kernel | mirror | bundle | started | aborted | duration (s) | failures | flags |\n|---|---|---|---|---|---|---|---|'] + T_sess
o += ['\n## 8. Peak working set per block (max over samples, MB): the A4 retention arm r3w2retain beside r3w2\n', '| kernel | regime | mirror | class | ' + ' | '.join(ARMS) + ' |\n|---|---|---|---|' + '---|' * len(ARMS)] + T_peak
open(os.path.join(S, 'tables-w2.md'), 'w', encoding='utf8').write('\n'.join(o))
print('ratio rows', len(T_ratio), 'arm rows', len(T_arms), 'slow', len(SLOW), 'missing', len(MISSING))
