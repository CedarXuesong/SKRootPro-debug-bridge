import getpass,sys,struct,json,time,urllib.request,urllib.error,subprocess,os,re,threading
from pathlib import Path
from types import SimpleNamespace
root=Path(__file__).resolve().parents[2]
sys.path.insert(0,str(root/'skpdb_probe'));import pc_client as pc
import argparse, shutil
def main():
    parser=argparse.ArgumentParser(description='Opt-in real-device WebUI integration test')
    parser.add_argument('--host',required=True)
    parser.add_argument('--port',type=int,default=39876)
    parser.add_argument('--knock-port',type=int,default=39877)
    parser.add_argument('--node',default=shutil.which('node'))
    parser.add_argument('--exclusive-heartbeat',action='store_true',help='Require last-page shutdown; close other WebUI pages first')
    args=parser.parse_args()
    key=getpass.getpass('KEY_STDIN (never saved): ').strip().encode()
    channel=pc.connect(SimpleNamespace(host=args.host,port=args.port,knock_port=args.knock_port,key=key,timeout=12))
    info=pc.request(channel,b'B\x01',b'B').decode();assert 'version=' in info
    start=pc.request(channel,b'B\x03',b'B').decode();port=int(re.search(r'webui_port=(\d+)',start)[1]);base=f'http://{args.host}:{port}'
    print('WebUI URL '+base,flush=True)
    opener=urllib.request.build_opener(urllib.request.ProxyHandler({}))
    def req(path,body=None,headers=None):
        request=urllib.request.Request(base+path,data=None if body is None else body.encode(),headers=headers or {})
        try:
            with opener.open(request,timeout=18) as response:return response.status,response.read().decode(),response.headers
        except urllib.error.HTTPError as e:return e.code,e.read().decode(),e.headers
    assert req('/api/info')[0]==403 and req('/api/exec','id')[0]==403
    status,html,headers=req('/');assert status==200 and 'type="password"' not in html and 'login-form' not in html
    csrf=re.search(r'name="skpdb-csrf" content="([0-9a-f]{64})"',html)[1]
    auth={'X-SKPDB-CSRF':csrf,'Content-Type':'text/plain'}
    assert req('/api/auth','',auth)[0]==404
    assert req('/api/info',headers={'X-SKPDB-CSRF':'invalid'})[0]==403
    stop=threading.Event()
    def heartbeat():
        direct=urllib.request.build_opener(urllib.request.ProxyHandler({}))
        while not stop.wait(.7):
            try:
                request=urllib.request.Request(base+'/api/alive',data=b'',headers=auth)
                with direct.open(request,timeout=3) as response:response.read()
            except OSError:pass
    thread=threading.Thread(target=heartbeat,daemon=True);thread.start()
    assert req('/api/alive','')[0]==403
    assert req('/api/alive','',dict(auth,Origin='http://example.invalid'))[0]==403
    assert req('/api/alive','',auth)[0]==200
    status,text,headers=req('/api/info',headers=auth);assert status==200 and 'fast_token=' not in text and 'admin_socket=' not in text
    assert headers['Cache-Control']=='no-store' and 'frame-ancestors' in headers['Content-Security-Policy']
    assert req('/api/config','service_port=0',auth)[0]==400
    assert req('/api/list?path=%00',headers=auth)[0]==400 and req('/api/file?path=relative',headers=auth)[0]==400
    status,diag,_=req('/api/diagnostics','',auth);assert status==200 and 'touch.check=ready' in diag and 'memory.kernel=ok' in diag
    assert 'probe_address=' not in diag and 'scratch_address=' not in diag
    folder='/data/local/tmp/skpdb-webui-check-'+str(time.time_ns())
    command='mkdir -p '+folder+"; printf '%s' '<img src=x onerror=alert(1)> safe text' > "+folder+'/webui-test.txt'
    b=pc.request(channel,b'E'+command.encode(),b'E');assert struct.unpack_from('!I',b)[0]==0
    node=Path(args.node)
    env=dict(os.environ,NODE_PATH=str(node.parent.parent/'node_modules'))
    data={'url':base,'folder':folder}
    result=subprocess.run([str(node),str(root/'skpdb_probe/tests/webui_browser.cjs')],input=json.dumps(data),cwd=root,env=env,capture_output=True,text=True,encoding='utf-8',timeout=90,creationflags=subprocess.CREATE_NO_WINDOW)
    if result.returncode:print(result.stderr,flush=True);raise RuntimeError('Browser tests failed')
    browser=json.loads(result.stdout.strip().splitlines()[-1]);assert browser['passed']
    print('Browser and HTTP security checks passed',flush=True)
    # Closing the last foreground page must close the HTTP loader, while the bridge remains responsive.
    stop.set();thread.join(timeout=4);time.sleep(5)
    try:req('/');closed=False
    except OSError:closed=True
    if args.exclusive_heartbeat: assert closed,'WebUI still has a foreground heartbeat or did not close the port'
    assert pc.request(channel,b'K',b'K')==b'PONG'
    pc.request(channel,b'E'+('rm -f '+folder+'/webui-test.txt; rmdir '+folder).encode(),b'E')
    report={'passed':True,'url':base,'browser':browser,'security':['no second key prompt','missing page header rejected','invalid page header rejected','cross-origin rejected','sensitive info redacted','input bounds checked'],'heartbeatClosedPort':closed,'heartbeatNote':None if closed else 'Another foreground page may be active; last-page shutdown not confirmed','primarySurvived':True}
    (root/'.scratch/webui-0.12.11-tests.json').write_text(json.dumps(report,ensure_ascii=False,indent=2),encoding='utf-8')
    print(json.dumps({'passed':True,'screenshots':[browser['desktop'],browser['mobile']],'heartbeatClosedPort':closed}),flush=True)
    channel.close();key=b''

if __name__ == "__main__":
    main()
