"""Wave-3 harness A/B tables: w2 (the wave-2 binary, ORIGINAL sources = OLD) vs w3 (wave-3 tip) vs knob arms w3a / w3g.

Reads every `bench` session.json under C:\\bt_r2ab\\<mirror>\\bench\\results; the LAST session of a
(kernel, regime) per mirror wins. Writes tables.md beside this script. Analyst CIs are unpaired
percentile bootstraps (4000 resamples over timed round samples, excursion-dropped excluded) of
median(B)/median(A) across two separate sessions -- NOT a harness output.
"""
import glob, json, os, random, statistics, sys

S = os.path.dirname(os.path.abspath(__file__))
MIRRORS = ['w2', 'w3', 'w3a', 'w3g']
LABEL = {'w2': 'w2 (wave-2 binary 1cf90428, ORIGINAL sources = OLD)', 'w3': 'w3 (wave-3 tip, defaults)',
         'w3a': 'w3a (BLADE_REC_STEP_OMP=0 on the new recur source)', 'w3g': 'w3g (BLADE_TILE_TUNE=anti_g=0)'}
# (kernel, regime, mirrors, item)
CELLS = [
    ('recur', 'mt8-licensed-native', ['w2', 'w3', 'w3a'], 'E-G5 (new arm; w2 = no Blade arm)'),
    ('recur', 'mt8-repro-native', ['w2', 'w3', 'w3a'], 'E-G5 strict post-pass (new arm)'),
    ('recur', 'serial-licensed-native', ['w2', 'w3', 'w3a'], 'control (serial emission; w3a = per-step copies)'),
    ('recur', 'serial-repro-native', ['w2', 'w3', 'w3a'], 'control'),
    ('stencil', 'mt8-licensed-native', ['w2', 'w3'], 'item 2: read-back licence (Path B)'),
    ('stencil', 'mt8-repro-native', ['w2', 'w3'], 'item 2: repro (serial read-back, G48)'),
    ('stencil', 'serial-licensed-native', ['w2', 'w3'], 'control'),
    ('antisym', 'serial-licensed-native', ['w3', 'w3g'], 'item 4: anti_g 1 vs auto (3 sessions each)'),
    ('antisym', 'mt8-licensed-native', ['w3', 'w3g'], 'item 4: anti_g 1 vs auto, mt8 (3 sessions each)'),
    ('gram', 'serial-licensed-native', ['w2', 'w3'], 'control'),
]
PAIRS = [('w3', 'w2'), ('w3a', 'w3'), ('w3g', 'w3')]   # (B, A): ratio = median(B)/median(A)
ARMS = ['blade-llvm', 'blade-cpp', 'gfortran-natural', 'gfortran-expert', 'ifx-natural', 'ifx-expert']
rng = random.Random(20261020)


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
    return statistics.median(x) if x else float('nan')


def boot(a, b, n=4000):
    if not a or not b:
        return float('nan'), float('nan')
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


def block(sess, k, reg):
    for p, d in sorted(sess.get(k, []), key=lambda pd: (not pd[1].get('aborted'), pd[0]))[::-1]:
        for b in d['blocks']:
            if b['regime'] == reg:
                return p, d, b
    return None, None, None


def er(b, arm, key='ratio'):
    a = b['arms'].get(arm)
    return None if a is None or a.get('status') != 'ok' else a.get(key)


data = {m: load(m) for m in MIRRORS}
T_ratio, T_arms, T_exp, T_blk, T_sess, SLOW, MISSING = [], [], [], [], [], [], []
for k, reg, ms_, item in CELLS:
    blocks = {m: block(data[m], k, reg) for m in ms_}
    for m in ms_:
        if blocks[m][2] is None:
            MISSING.append('| %s | %s | %s | not completed |' % (k, reg, m))
    ref = 'blade-cpp' if reg.startswith('mt8') else 'blade-llvm'
    # per-mirror arm medians + expert ratios
    for m in ms_:
        p, d, b = blocks[m]
        if b is None:
            continue
        cls = 'aborted' if d.get('aborted') else ('indicative' if b.get('suppressed', True) else 'live')
        co = b.get('controls') or {'compute': {'median': float('nan')}, 'memory': {'median': float('nan')}}
        T_blk.append('| %s | %s | %s | %s | %s | %.3f | %.3f | %s | %d |' % (
            k, reg, m, (b.get('rounds') or {}).get('n', 0), cls, co['compute']['median'] * 1e3, co['memory']['median'] * 1e3,
            '; '.join(b.get('flags') or []) or '-', len(b.get('excursions') or [])))
        row = '| %s | %s | %s | %s |' % (k, reg, m, cls)
        for arm in ARMS:
            a = b['arms'].get(arm)
            if a is None:
                row += ' n/a |'
            elif a.get('status') != 'ok':
                row += ' %s%s |' % (a['status'], (': ' + a['reason'][:60]) if a.get('reason') else '')
            else:
                row += ' %s |' % ms(med(samples(a)))
        T_arms.append(row)
        T_exp.append('| %s | %s | %s | %s | %s | %s | %s |' % (
            k, reg, m, ref, fr(er(b, 'gfortran-expert')), fr(er(b, 'ifx-expert')), cls))
    # pairwise ratios
    for B, A in PAIRS:
        if B not in ms_ or A not in ms_:
            continue
        pa, da, ba = blocks[A]
        pb, db, bb = blocks[B]
        if ba is None or bb is None:
            continue
        for arm in ARMS:
            aa = ba['arms'].get(arm); ab = bb['arms'].get(arm)
            if aa is None or ab is None or aa.get('status') != 'ok' or ab.get('status') != 'ok':
                if arm.startswith('blade') and (aa or ab):
                    T_ratio.append('| %s | %s | %s | %s/%s | %s | %s | | (%s / %s) |' % (
                        k, reg, item, B, A, arm, '--', aa.get('status') if aa else 'n/a', ab.get('status') if ab else 'n/a'))
                continue
            sa = samples(aa); sb = samples(ab)
            ma = med(sa); mb = med(sb); r = mb / ma
            lo, hi = boot(sa, sb)
            cls = 'live' if not (ba.get('suppressed', True) or bb.get('suppressed', True)) else 'ind'
            T_ratio.append('| %s | %s | %s | %s/%s | %s | %s -> %s | %.3f [%.3f, %.3f] | %s %d/%d |' % (
                k, reg, item, B, A, arm, ms(ma), ms(mb), r, lo, hi, cls, len(sa), len(sb)))
            if arm.startswith('blade') and lo > 1 and (B, A) == ('w3', 'w2'):
                SLOW.append((k, reg, arm, ma, mb, r, lo, hi, cls))
for m in MIRRORS:
    for k, lst in data[m].items():
        for p, d in lst:
            T_sess.append('| %s | %s | %s | %s | %s | %.0f | %s | %s |' % (
                k, m, p, d.get('started'), d.get('aborted'), d.get('duration_s', 0), d.get('failures') or 'none', '; '.join(d.get('flags') or []) or 'none'))

o = ['# Wave-2 harness A/B tables (generated by analyze.py)\n']
o.append('Mirrors: ' + '; '.join('%s = %s' % (m, LABEL[m]) for m in MIRRORS) + '\n')
o.append('## 1. Pairwise medians: B/A < 1 = B faster. Class live = neither block drift-suppressed; ind = at least one indicative. CI = analyst unpaired bootstrap (not a harness output).\n')
o.append('| kernel | regime | item | B/A | arm | A -> B median (ms) | B/A [95% CI] | class n_A/n_B |\n|---|---|---|---|---|---|---|---|')
o += T_ratio
o.append('\n## 2. Arm medians per mirror (ms)\n')
o.append('| kernel | regime | mirror | block | ' + ' | '.join(ARMS) + ' |\n|---|---|---|---|' + '---|' * len(ARMS))
o += T_arms
o.append('\n## 3. Expert ratios (expert median / Blade ref median; > 1 Blade faster); harness ratio [harness CI] verdict\n')
o.append('| kernel | regime | mirror | ref | gfortran-expert | ifx-expert | block |\n|---|---|---|---|---|---|---|')
o += T_exp
o.append('\n## 4. Blocks: rounds, class, control medians (ms), flags, excursions dropped\n')
o.append('| kernel | regime | mirror | rounds | class | compute ctl | memory ctl | flags | excursions |\n|---|---|---|---|---|---|---|---|---|')
o += T_blk
o.append('\n## 5. w3 slower than w2 beyond the CI (Blade arms; lower bound of w3/w2 > 1)\n')
if SLOW:
    o.append('| kernel | regime | arm | w2 ms | w3 ms | w3/w2 [CI] | class |\n|---|---|---|---|---|---|---|')
    for k, reg, arm, ma, mb, r, lo, hi, c in SLOW:
        o.append('| %s | %s | %s | %s | %s | %.3f [%.3f, %.3f] | %s |' % (k, reg, arm, ms(ma), ms(mb), r, lo, hi, c))
else:
    o.append('none')
o.append('\n## 6. Missing cells\n')
o += (['| kernel | regime | mirror | status |\n|---|---|---|---|'] + MISSING) if MISSING else ['none']
o.append('\n## 7. Sessions\n')
o.append('| kernel | mirror | bundle | started | aborted | duration (s) | failures | flags |\n|---|---|---|---|---|---|---|---|')
o += T_sess
open(os.path.join(S, 'tables.md'), 'w', encoding='utf8').write('\n'.join(o))
print('ratio rows', len(T_ratio), 'arm rows', len(T_arms), 'slow', len(SLOW), 'missing', len(MISSING))
