import sys, subprocess, re, os
P=os.path.dirname(os.path.abspath(__file__))
for f in sys.argv[1:]:
    src=open(os.path.join(P,f),encoding='utf-8').read()
    pins=re.findall(r'^// EXPECT: (\w+) = (.*)$',src,re.M)
    out=subprocess.run(['bash',os.path.join(P,'br.sh'),'run',f],capture_output=True,text=True).stdout
    vals=dict(re.findall(r'^(\w+) = (.*)$',out,re.M))
    ex=re.search(r'\[exit (\d+)\]',out).group(1)
    bad=[(n,v,vals.get(n)) for n,v in pins if vals.get(n)!=v.strip()]
    print(f"{f}: exit {ex}, pins {len(pins)-len(bad)}/{len(pins)} matched" + ("" if not bad else f"  MISMATCH {bad}"))
