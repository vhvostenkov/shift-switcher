#!/usr/bin/env python3
"""Explicit CGEvent QA in owned fixtures only. Leaves one tested candidate running."""
import argparse
import hashlib
import json
import os
import signal
import subprocess
import time
from pathlib import Path

root=Path(__file__).resolve().parent.parent
parser=argparse.ArgumentParser()
parser.add_argument('--target',choices=['TextEdit','Safari','Chrome','Notes'],default='TextEdit')
parser.add_argument('--diagnostics',action='store_true')
args=parser.parse_args()
target={'Safari':'com.apple.Safari','Chrome':'com.google.Chrome','Notes':'com.apple.Notes'}.get(args.target)
app=root/'build/Shift Switcher Candidate.app/Contents/MacOS/Shift Switcher'
if not app.is_file():raise SystemExit('Build the candidate first')
owned=[]
for line in subprocess.check_output(['ps','-axo','pid=,args='],text=True).splitlines():
    parts=line.strip().split(None,1)
    if len(parts)==2 and (parts[1]==str(app) or parts[1].startswith(str(app)+' ')):
        owned.append(int(parts[0]));os.kill(owned[-1],signal.SIGTERM)
for pid in owned:
    for _ in range(50):
        try:os.kill(pid,0)
        except ProcessLookupError:break
        time.sleep(.1)
# LaunchServices can briefly retain the old bundle after its PID exits.
time.sleep(1)
report=root/'build'/('compatibility-'+target+'.json' if target else 'native-acceptance.json')
logpath=root/'build'/('qa-'+args.target+'.log')
command=[str(app),'--compatibility-acceptance='+target if target else '--native-acceptance']
env=dict(os.environ)
if args.diagnostics:env['SHIFT_SWITCHER_DIAGNOSTICS']='1'
started=time.time()
with logpath.open('w') as log:
    process=subprocess.Popen(command,stdin=subprocess.DEVNULL,stdout=log,stderr=subprocess.STDOUT,env=env)
    for _ in range(90):
        if report.exists() and report.stat().st_mtime>=started:
            data=json.loads(report.read_text())
            data['binary_sha256']=hashlib.sha256(app.read_bytes()).hexdigest()
            data['source_commit']=subprocess.check_output(['git','rev-parse','HEAD'],cwd=root,text=True).strip()
            data['source_dirty']=bool(subprocess.check_output(['git','status','--porcelain','--','Sources','Tests','build.sh'],cwd=root,text=True).strip())
            report.write_text(json.dumps(data,ensure_ascii=False,indent=2)+'\n')
            print(json.dumps({'target':args.target,'passed':sum(r['passed'] for r in data['results']),'total':len(data['results']),'report':str(report),'binary_sha256':data['binary_sha256']},ensure_ascii=False))
            raise SystemExit(0 if all(r['passed'] for r in data['results']) else 1)
        if process.poll() is not None:raise SystemExit('Candidate exited before producing a fresh report; previous results are not accepted')
        time.sleep(.5)
raise SystemExit('No fresh report within 45 seconds')
