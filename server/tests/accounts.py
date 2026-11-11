"""Real HTTP tests: account migration, isolation, immutable review events, cookie/CSRF and revocation."""
from concurrent.futures import ThreadPoolExecutor
from contextlib import closing
from pathlib import Path
import hashlib
import json
import os
import sqlite3
import subprocess
import tempfile
import urllib.error
import urllib.request
import uuid
from integration import service, command, DOTNET, DLL, ROOT


def origin_checks(temp):
    """No-referrer + same-origin POST legitimately serializes Origin as null (Fetch 3.2)."""
    with service(temp / 'origin.db') as oldcall:
        url = next(cell.cell_contents for cell in oldcall.__closure__ if isinstance(cell.cell_contents,str) and cell.cell_contents.startswith('http://'))
        def call(path, body=None, origin=None, site=None, cookie=None, proof=None, referer=None):
            headers = {'Content-Type':'application/json'} if body is not None else {}
            if origin is not None: headers['Origin'] = origin
            if site is not None: headers['Sec-Fetch-Site'] = site
            if cookie: headers['Cookie'] = cookie
            if proof: headers['X-Tuci-Origin-Proof'] = proof
            if referer: headers['Referer'] = referer
            data = None if body is None else json.dumps(body).encode()
            request = urllib.request.Request(url+path,data=data,headers=headers)
            try:
                with urllib.request.urlopen(request,timeout=20) as response:
                    return response.status,json.loads(response.read()),response.headers
            except urllib.error.HTTPError as error:
                return error.code,error.read().decode(),error.headers
        credentials = dict(username='origin-fixture',password='fixture password 123',deviceName='Origin test',browser=True,
                           inviteCode=command(temp / 'origin.db','invite-web')['code'])
        status,_,_ = call('/v1/account/register',credentials)
        assert status == 200
        status,_,headers = call('/v1/account/login',credentials,origin='null',site='same-origin')
        assert status == 200, ('same-origin no-referrer login',status)
        cookie = headers['Set-Cookie'].split(';')[0]
        proof_path = '/v1/account/origin-proof'
        status, reply, headers = call(proof_path)
        assert status == 200 and 0 < reply['expiresIn'] <= 120
        assert headers['Cache-Control'] == 'no-store'
        assert headers['Cross-Origin-Resource-Policy'] == 'same-origin'
        proof = reply['proof']
        proof_cookie, *attributes = headers['Set-Cookie'].split('; ')
        assert proof_cookie == '__Host-tuci-origin-proof=' + proof
        assert {'secure', 'httponly', 'samesite=strict', 'path=/', 'max-age=120'} <= {value.lower() for value in attributes}
        assert not any(value.lower().startswith('domain=') for value in attributes)
        assert call(proof_path, cookie=proof_cookie)[1]['proof'] == proof

        # Fetch Metadata is a forbidden header: other origins cannot claim same-origin in JS.
        # An opaque origin by itself, Referer, User-Agent or a caller-controlled custom
        # header must never be treated as proof of origin.
        status, _, headers = call('/v1/account/login', credentials, origin='null', referer='https://101.133.134.128/tuci/')
        assert status == 403 and headers['X-Tuci-Origin-Error'] == 'proof-required'
        fake = 'A' * 96
        assert call('/v1/account/login', credentials, origin='null', proof=fake,
                    cookie='__Host-tuci-origin-proof='+fake)[0] == 403
        assert call('/v1/account/login', credentials, origin='null', proof=fake, cookie=proof_cookie)[0] == 403
        assert call('/v1/account/login', credentials, origin='null', proof=proof)[0] == 403
        for site in ['cross-site', 'same-site', 'none', 'same-origin, cross-site']:
            status, _, headers = call('/v1/account/login', credentials, origin='null', site=site, cookie=proof_cookie, proof=proof)
            assert status == 403 and headers['X-Tuci-Origin-Error'] is None, site
            assert call(proof_path, site=site)[0] == 403, site
        status, _, headers = call('/v1/account/login', credentials, site='same-site', cookie=proof_cookie, proof=proof)
        assert status == 403 and headers['X-Tuci-Origin-Error'] is None
        for origin,site in [('https://evil.example','same-origin'), ('https://101.133.134.128.evil.example','same-origin'),
                            ('https://101.133.134.128','cross-site')]:
            status, _, headers = call('/v1/account/login', credentials, origin=origin, site=site, cookie=proof_cookie, proof=proof)
            assert status == 403 and headers['X-Tuci-Origin-Error'] is None, (origin,site)
            assert call('/v1/account/logout', {}, origin=origin, site=site, cookie=cookie+'; '+proof_cookie, proof=proof)[0] == 403
            assert call(proof_path, origin=origin, site=site)[0] == 403
        assert call('/v1/account/login',credentials,origin='https://101.133.134.128',site='same-origin')[0] == 200
        status, _, headers = call('/v1/account/login', credentials, origin='null', cookie=proof_cookie, proof=proof)
        assert status == 200
        legacy_cookie = headers['Set-Cookie'].split(';')[0]
        assert call('/v1/account/logout', {}, origin='null', cookie=legacy_cookie+'; '+proof_cookie, proof=proof)[0] == 200
        assert call('/v1/account/logout',{},origin='null',site='same-origin',cookie=cookie)[0] == 200
    print('PASS: no-referrer login with Fetch Metadata or browser proof, hostile origins rejected')


def run(temp):
    database = temp / 'accounts.db'
    legacy = command(database, 'invite')
    with service(database, trust_local_proxy=True) as oldcall:
        olddevice = oldcall('/v1/redeem', {'code': legacy['code'], 'deviceName':'Old PC'})[1]
        oldphone = oldcall('/v1/redeem', {'code': command(database,'invite',legacy['vaultId'])['code'], 'deviceName':'Old phone'})[1]
        # Get the isolated host from the helper closure to exercise actual cookie headers.
        url = next(cell.cell_contents for cell in oldcall.__closure__ if isinstance(cell.cell_contents,str) and cell.cell_contents.startswith('http://'))
        def call(path, body=None, token=None, cookie=None, origin=None, ip='192.0.2.40', expected_vault=None):
            headers={'Content-Type':'application/json','X-Forwarded-For':ip}
            if token: headers['Authorization']='Bearer '+token
            if cookie:
                headers['Cookie']=cookie
                headers['X-Tuci-Vault']=expected_vault if expected_vault is not None else alice['vaultId']
            if origin: headers['Origin']=origin
            req=urllib.request.Request(url+path,data=None if body is None else json.dumps(body).encode(),headers=headers)
            try:
                with urllib.request.urlopen(req,timeout=20) as r: return r.status,json.loads(r.read()),r.headers
            except urllib.error.HTTPError as e: return e.code,e.read().decode(),e.headers
        def ok(path,body=None,token=None,**kwargs):
            status,data,headers=call(path,body,token,**kwargs)
            assert status==200,(path,status,data)
            return data,headers
        request={'username':'测试Alice','password':'test password 123','deviceName':'Windows','browser':False}
        alice,_=ok('/v1/account/register',request,olddevice['token'])
        assert alice['vaultId']==legacy['vaultId']
        assert call('/v1/changes',token=oldphone['token'])[0]==401
        assert call('/v1/account/register',dict(request,username='other'),alice['token'])[0]==409
        # A brand-new account must spend an operator-issued invite; claiming a vault
        # is authorised by its own device token instead.
        assert call('/v1/account/register',dict(request,username='nobody'))[0]==400
        assert call('/v1/account/register',dict(request,username='nobody',inviteCode='AAAA-BBBB-CCCC'))[0]==401
        assert call('/v1/account/register',dict(request,username='测试ALICE',
                    inviteCode=command(database,'invite-web')['code']))[0]==409
        bob,_=ok('/v1/account/register',dict(request,username='bob',inviteCode=command(database,'invite-web')['code']))
        operator,_=ok('/v1/account/register',dict(request,username='trinhrae',inviteCode=command(database,'invite-web')['code']))
        issued,_=ok('/v1/invites',{},operator['token'])
        assert call('/v1/invites',{},alice['token'])[0]==403
        assert call('/v1/invites',{},bob['token'])[0]==403
        assert bob['vaultId']!=alice['vaultId'] and bob['userId']!=alice['userId']
        assert call('/v1/account/login',dict(request,password='incorrect password'))[0]==401
        browser,h=ok('/v1/account/login',dict(request,browser=True,deviceName='iPhone Safari'))
        assert 'token' not in browser and browser['userId']==alice['userId']
        assert browser['canInvite'] is False
        cookie=h.get('Set-Cookie')
        assert all(x in cookie for x in ['secure','httponly','samesite=strict','path=/tuci-sync/']),cookie
        cookie=cookie.split(';')[0]
        assert ok('/v1/account/me',cookie=cookie)[0]['username']==request['username']
        assert call('/v1/account/login',request,origin='https://evil.example')[0]==403
        assert call('/v1/account/logout',{},cookie=cookie,origin='https://evil.example')[0]==403
        assert call('/v1/state/changes')[0]==401
        assert call('/v1/state/changes',cookie=cookie,expected_vault=bob['vaultId'])[0]==403
        assert call('/v1/state/changes',cookie=cookie,expected_vault='')[0]==400
        note=str(uuid.uuid4())
        op=dict(operationId=str(uuid.uuid4()),noteId=note,baseVersion=0,path='名词/example.md',markdown='# Example',deleted=False)
        ok('/v1/operations',op,alice['token'])
        assert ok('/v1/changes',token=bob['token'])[0]['changes']==[]
        exposure=str(uuid.uuid4()); key='event/'+exposure+'/shown'
        shown=dict(noteId=note,at='2026-11-07T12:00:00Z',day='2026-11-07',weight=2.1,weightLevel=.5)
        state=dict(operationId=str(uuid.uuid4()),key=key,value=shown,deleted=False)
        with ThreadPoolExecutor(2) as pool:
            receipts=list(pool.map(lambda _:ok('/v1/state/operations',state,alice['token'])[0],range(2)))
        assert receipts[0]==receipts[1] and receipts[0]['seq']==1
        assert call('/v1/state/operations',dict(state,value=dict(shown,weight=99)),alice['token'])[0]==409
        # The immutable exposure identity, rather than only the transport operation, prevents double counting.
        again,_=ok('/v1/state/operations',dict(state,operationId=str(uuid.uuid4()),value=dict(shown,weight=99)),cookie=cookie)
        assert again==receipts[0]
        completion=dict(operationId=str(uuid.uuid4()),key='event/'+exposure+'/completed',value=dict(noteId=note,at='2026-11-07T12:01:00Z',day='2026-11-07',remembered=True))
        ok('/v1/state/operations',completion,cookie=cookie)
        result,_=ok('/v1/state/changes?after=0',token=alice['token'])
        assert result['cursor']==2 and len(result['changes'])==2 and result['changes'][0]['value']['weight']==2.1
        assert ok('/v1/state/changes?after=0',token=bob['token'])[0]['changes']==[]
        assert call('/v1/state/operations',state,bob['token'])[0]==409
        assert call('/v1/state/operations',dict(state,deleted=True,operationId=str(uuid.uuid4())),alice['token'])[0]==400
        for value in [dict(shown,weight='bad'),dict(shown,weight=0),dict(shown,at='November 7 2026'),dict(shown,at=123),dict(shown,apiKey='secret'),dict(shown,noteId='missing')]:
            assert call('/v1/state/operations',dict(state,value=value,operationId=str(uuid.uuid4())),alice['token'])[0] in [400,409]
        assert call('/v1/state/operations',dict(state,key='settings/apiKey'),alice['token'])[0]==400
        baseline=dict(noteId=note,day='2026-11-07',remembered=1,skipped=0,shown=2,lastShown=shown['at'])
        legacy_key='legacy/test-device/'+note+'/2026-11-07'
        for bad in [dict(baseline,remembered=.5),dict(baseline,skipped='1'),dict(baseline,lastShown='yesterday')]:
            assert call('/v1/state/operations',dict(operationId=str(uuid.uuid4()),key=legacy_key,value=bad),alice['token'])[0]==400
        assert call('/v1/state/operations',dict(operationId=str(uuid.uuid4()),key='note-reading/'+note,value=dict(noteId=note,progress=.5,anchor=123)),alice['token'])[0]==400
        # More than one page: deterministic seq and independent note/state cursors.
        for i in range(130):
            ok('/v1/state/operations',dict(operationId=str(uuid.uuid4()),key='reading/'+hashlib.sha256(f'https://example.com/{i}'.encode()).hexdigest(),value=dict(url=f'https://example.com/{i}',progress=.5,anchor='p2')),alice['token'])
        first=ok('/v1/state/changes?after=0',token=alice['token'])[0]
        assert first['hasMore'] and len(first['changes'])==128
        second=ok('/v1/state/changes?after='+str(first['cursor']),token=alice['token'])[0]
        assert not second['hasMore'] and second['cursor']==132
        assert call('/v1/state/changes?after=99999',token=alice['token'])[0]==409
        # Re-login keeps account/vault identity, but logout revokes only that device.
        ok('/v1/account/logout',{},cookie=cookie)
        assert call('/v1/account/me',cookie=cookie)[0]==401
        assert ok('/v1/account/me',token=alice['token'])[0]['userId']==alice['userId']
        second_device,_=ok('/v1/account/login',request,ip='192.0.2.41')
        assert call('/v1/account/password',dict(currentPassword='wrong password',newPassword='a new password'),alice['token'],ip='192.0.2.41')[0]==401
        ok('/v1/account/password',dict(currentPassword=request['password'],newPassword='a new password'),alice['token'],ip='192.0.2.41')
        assert call('/v1/account/me',token=second_device['token'])[0]==401
        assert call('/v1/account/login',request,ip='192.0.2.41')[0]==401
        relogin,_=ok('/v1/account/login',dict(request,password='a new password'),ip='192.0.2.41')
        assert relogin['vaultId']==alice['vaultId']
        # Account-level throttling survives changing source IPs.
        for i in range(10):
            status=call('/v1/account/login',dict(request,password='wrong password'),ip='192.0.3.'+str(i+1))[0]
            assert status==401,status
        assert call('/v1/account/login',dict(request,password='a new password'),ip='192.0.3.50')[0]==429
        backup=temp/'accounts-backup.db'; command(database,'backup',str(backup))
    with closing(sqlite3.connect(backup)) as con:
        assert con.execute('PRAGMA integrity_check').fetchone()[0]=='ok'
        assert con.execute('PRAGMA user_version').fetchone()[0]==2
        password=con.execute('SELECT password FROM accounts WHERE id=?',(alice['userId'],)).fetchone()[0]
        assert password.startswith('pbkdf2-sha256$600000$') and 'a new password' not in password
        assert con.execute('SELECT COUNT(*) FROM state_changes WHERE vault=?',(alice['vaultId'],)).fetchone()[0]==132
    with service(backup) as restored:
        status,result=restored('/v1/state/changes?after=130',token=relogin['token'])
        assert status==200 and result['cursor']==132
    print('PASS: legacy claim, two-account isolation, HttpOnly cookie and CSRF, review dedupe, paging, revocation, password, lockout, backup/restore')


if __name__=='__main__':
    subprocess.run([DOTNET,'build',str(ROOT/'Tuci.Sync.csproj'),'-c','Release','--no-restore'],check=True)
    with tempfile.TemporaryDirectory() as directory:
        origin_checks(Path(directory))
        run(Path(directory))
