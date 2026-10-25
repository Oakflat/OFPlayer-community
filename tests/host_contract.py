"""Exercise the real native executable against temporary fixtures, never user data."""
import argparse
from contextlib import closing
import hashlib
import json
from pathlib import Path
import queue
import sqlite3
import subprocess
import tempfile
import threading
import time
import wave

class Host:
    def __init__(self, executable, root):
        self.process = subprocess.Popen([str(executable), '--data-root', str(root)], stdin=subprocess.PIPE, stdout=subprocess.PIPE, stderr=subprocess.PIPE, encoding='utf-8', creationflags=getattr(subprocess, 'CREATE_NO_WINDOW', 0))
        self.messages = queue.Queue()
        self.errors = []
        self.next_id = 0
        def read():
            for line in self.process.stdout:
                self.messages.put(json.loads(line))
        def errors():
            self.errors.extend(self.process.stderr.read().splitlines())
        threading.Thread(target=read, daemon=True).start()
        threading.Thread(target=errors, daemon=True).start()
    def send(self, command, args=None, version=1):
        self.next_id += 1
        self.process.stdin.write(json.dumps({'protocolVersion':version,'id':self.next_id,'command':command,'args':args or {}})+'\n')
        self.process.stdin.flush()
        return self.next_id
    def response(self, request_id, timeout=30):
        end = time.monotonic() + timeout
        while time.monotonic() < end:
            message = self.messages.get(timeout=max(.1, end-time.monotonic()))
            if message.get('id') == request_id:
                return message
        raise AssertionError('No response: '+str(self.errors))
    def call(self, command, args=None):
        result = self.response(self.send(command,args))
        assert result['ok'], result
        return result['data']
    def close(self):
        if self.process.poll() is None:
            self.call('host_shutdown')
            self.process.stdin.close()
            self.process.wait(timeout=10)
        assert self.process.returncode == 0, self.errors

def main():
    parser = argparse.ArgumentParser()
    parser.add_argument('--host',type=Path,required=True)
    parser.add_argument('--output',type=Path)
    args = parser.parse_args()
    executable = args.host.resolve()
    passed = []
    with tempfile.TemporaryDirectory(prefix='ofplayer-community-contract-') as temp:
        base=Path(temp); source=base/'legacy'; target=base/'community'
        assert base.resolve().parent == Path(tempfile.gettempdir()).resolve() and base.name.startswith('ofplayer-community-contract-')
        media=base/'media';media.mkdir()
        for name in ['first.wav','missing.wav']:
            with wave.open(str(media/name),'wb') as wav:
                wav.setnchannels(1);wav.setsampwidth(2);wav.setframerate(8000);wav.writeframes(b'\x00\x00'*8000)
        host=Host(executable,source)
        try:
            handshake=host.call('host_handshake')
            assert handshake['edition']=='community'
            assert all(cap.startswith('local.') for cap in handshake['capabilities'])
            passed.append('Community handshake exposes only local capabilities')
            catalog=host.call('catalog.snapshot');library=catalog['libraries'][0]['id']
            for i in range(4):host.call('library.create',{'name':f'Local {i}'})
            for i in range(12):host.call('playlist.create',{'libraryId':library,'name':f'Playlist {i}'})
            catalog=host.call('catalog.snapshot')
            assert len(catalog['libraries'])==5 and len(catalog['playlists'])>=12
            passed.append('Local libraries/playlists have no commercial quota')
            rows=host.call('import.files',{'libraryId':library,'files':[{'sourcePath':str(p),'fileName':p.name} for p in media.glob('*.wav')]})
            assert len(rows)==2, rows
            ids={Path(row['source']['path']).name:row['id'] for row in rows}
            first=ids['first.wav'];missing=ids['missing.wav']
            host.call('session.queue',{'trackIds':[first,missing]})
            before=host.call('session.snapshot')['session']
            invalid_queue=host.response(host.send('session.select',{'trackId':first,'queueTrackIds':[missing],'autoplay':False}))
            assert not invalid_queue['ok'], invalid_queue
            assert host.call('session.snapshot')['session']==before
            passed.append('Invalid queue is rejected before loading or changing the current track')
            (media/'missing.wav').unlink()
            failed=host.response(host.send('session.select',{'trackId':missing,'autoplay':False}))
            assert not failed['ok']
            after=host.call('session.snapshot')['session']
            assert before['currentTrackId']==after['currentTrackId']==first
            assert before['queueTrackIds']==after['queueTrackIds']
            passed.append('Missing file selection leaves persisted current track and queue unchanged')
            playlist=next(p['id'] for p in catalog['playlists'] if p['libraryId']==library and p['name']=='Playlist 0')
            host.call('playlist.add',{'playlistId':playlist,'trackId':missing})
            host.call('track.delete',{'trackIds':[missing]})
            assert host.call('session.snapshot')['session']['currentTrackId']==first
            assert not any(r['trackId']==missing for r in host.call('catalog.snapshot')['playlistTrackRelations'])
            assert (media/'first.wav').is_file()
            passed.append('Removing another missing track preserves selection and cleans playlist relations')
            for command in ['together_control_start','mobile_handoff_start_host','external_library_test','connect_start','video_dlna_scan','assistant_tool_call','license_identity','cloud_upload']:
                rejected=host.response(host.send(command))
                assert not rejected['ok'] and rejected['error']['code']=='unknown-command', rejected
            passed.append('Together/streaming/interop/DLNA/account/assistant RPCs rejected')
            rejected=host.response(host.send('import.files',{'libraryId':library,'files':[{'sourcePath':'https://example.invalid/song.mp3'}]}))
            assert not rejected['ok']
            passed.append('Remote media URL rejected before import')
            import_id=host.send('import.files',{'libraryId':library,'files':[{'sourcePath':str(media/'first.wav')}] * 300})
            ping_id=host.send('session.snapshot')
            responses=[]
            while len(responses)<2:
                message=host.messages.get(timeout=30)
                if message.get('id') in (import_id,ping_id):responses.append(message)
            assert responses[0]['id']==ping_id and all(r['ok'] for r in responses),responses
            passed.append('Playback/session command responds while background import is pending')
            host.call('preferences.patch',{'theme':'dark','volume':.35,'accountToken':'PRIVATE-FIXTURE-TOKEN'})
            bad_version=host.response(host.send('catalog.snapshot',version=2))
            assert not bad_version['ok']
            passed.append('Incompatible protocol fails closed')
        finally:
            host.close()
        passed.append('Graceful host shutdown exits with no retained process')
        source_db=source/'ofplayer-desktop.sqlite3'
        artwork=source/'track-artwork'/'fixture.png';artwork.parent.mkdir(exist_ok=True);artwork.write_bytes(b'local-artwork-fixture')
        with closing(sqlite3.connect(source_db)) as db:
            db.execute('PRAGMA journal_mode=WAL');db.execute('PRAGMA wal_autocheckpoint=0')
            db.execute('CREATE TABLE private_account_test(secret TEXT)')
            db.execute('INSERT INTO private_account_test VALUES (?)',('PRIVATE-FIXTURE-TOKEN',))
            db.execute('INSERT INTO app_state(key,value_json,updated_at) VALUES (?,?,?)',('externalLibraries.connections',json.dumps({'password':'PRIVATE-FIXTURE-TOKEN'}),1))
            db.execute("UPDATE app_state SET value_json=json_set(value_json,'$.accountToken','PRIVATE-FIXTURE-TOKEN') WHERE key='session'")
            db.execute('INSERT OR REPLACE INTO track_artwork(track_id,artwork_text,artwork_path,mime_type,content_hash,byte_length,updated_at) VALUES (?,?,?,?,?,?,?)',(first,'',str(artwork),'image/png','fixture',len(artwork.read_bytes()),1))
            payload=json.loads(db.execute('SELECT payload_json FROM tracks WHERE id=?',(first,)).fetchone()[0]);payload['favorite']=True
            db.execute('UPDATE tracks SET payload_json=? WHERE id=?',(json.dumps(payload),first));db.commit()
            assert Path(str(source_db)+'-wal').is_file()
            before_hash=hashlib.sha256(source_db.read_bytes()).hexdigest()
            result=subprocess.run([str(executable),'--migrate-tauri',str(source),str(target)],capture_output=True,text=True,encoding='utf-8',creationflags=getattr(subprocess,'CREATE_NO_WINDOW',0),timeout=30)
            assert result.returncode==0,result.stderr
            receipt=json.loads(result.stdout);assert receipt['migrated'] and receipt['sourcePreserved'] and not receipt['credentialsMigrated']
            assert hashlib.sha256(source_db.read_bytes()).hexdigest()==before_hash
            assert db.execute('SELECT secret FROM private_account_test').fetchone()[0]=='PRIVATE-FIXTURE-TOKEN'
            passed.append('Read-only SQLite backup includes committed WAL and leaves source intact')
        with closing(sqlite3.connect(target/'ofplayer-desktop.sqlite3')) as db:
            assert json.loads(db.execute('SELECT payload_json FROM tracks WHERE id=?',(first,)).fetchone()[0])['favorite']
            assert db.execute('SELECT COUNT(*) FROM playlists').fetchone()[0]>=12
            prefs=json.loads(db.execute("SELECT value_json FROM app_state WHERE key='preferences'").fetchone()[0])
            assert prefs['theme']=='dark' and prefs['volume']==.35 and 'accountToken' not in prefs
            assert db.execute("SELECT COUNT(*) FROM sqlite_master WHERE name='private_account_test'").fetchone()[0]==0
            assert db.execute("SELECT COUNT(*) FROM app_state WHERE key='externalLibraries.connections'").fetchone()[0]==0
            assert db.execute('PRAGMA integrity_check').fetchone()[0]=='ok'
            artwork_path=Path(db.execute('SELECT artwork_path FROM track_artwork WHERE track_id=?',(first,)).fetchone()[0])
            assert artwork_path.resolve().is_relative_to(target.resolve()) and artwork_path.read_bytes()==b'local-artwork-fixture'
            assert 'accountToken' not in json.loads(db.execute("SELECT value_json FROM app_state WHERE key='session'").fetchone()[0])
            passed.append('Migration relocates stored artwork and strips non-session fields')
        assert b'PRIVATE-FIXTURE-TOKEN' not in (target/'ofplayer-desktop.sqlite3').read_bytes()
        passed.append('Migrated catalog retains local state and removes credential rows and freelist bytes')
        target_hash=hashlib.sha256((target/'ofplayer-desktop.sqlite3').read_bytes()).hexdigest()
        repeat=subprocess.run([str(executable),'--migrate-tauri',str(source),str(target)],capture_output=True,text=True,encoding='utf-8',creationflags=getattr(subprocess,'CREATE_NO_WINDOW',0),timeout=30)
        assert repeat.returncode==0 and json.loads(repeat.stdout)['reason']=='existing-target-preserved'
        assert hashlib.sha256((target/'ofplayer-desktop.sqlite3').read_bytes()).hexdigest()==target_hash
        passed.append('Existing Community target is never overwritten')
    result={'passed':len(passed),'failed':0,'checks':passed,'scope':'temporary local fixtures; no physical playback or user data','hostSHA256':hashlib.sha256(executable.read_bytes()).hexdigest()}
    if args.output:
        args.output.parent.mkdir(parents=True,exist_ok=True);args.output.write_text(json.dumps(result,indent=2),encoding='utf-8')
    print(json.dumps(result,indent=2))
if __name__=='__main__':main()
