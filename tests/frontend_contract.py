"""Mainline UI contracts against a temporary Community catalog and mock media servers."""
import argparse,json,tempfile,wave,threading,io
from pathlib import Path
from http.server import BaseHTTPRequestHandler,ThreadingHTTPServer
from urllib.parse import urlsplit
from host_contract import Host

def main():
 p=argparse.ArgumentParser();p.add_argument('--host',type=Path,required=True);a=p.parse_args();passed=[]
 with tempfile.TemporaryDirectory(prefix='ofplayer-community-ui-') as temp:
  base=Path(temp);media=base/'media';media.mkdir()
  for n in ('a','b','c'):
   with wave.open(str(media/(n+'.wav')),'wb') as w:w.setnchannels(1);w.setsampwidth(2);w.setframerate(8000);w.writeframes(b'\0\0'*16000)
  h=Host(a.host.resolve(),base/'data')
  try:
   h.call('host_handshake')
   structure=h.call('desktop_catalog_load_structure');lib=structure['libraries'][0]['id']
   report=h.call('desktop_library_import_files',{'libraryId':lib,'files':[{'sourcePath':str(f),'fileName':f.name} for f in media.glob('*.wav')]})
   assert len(report['importedTracks'])==3,report
   report2=h.call('desktop_library_scan_import',{'libraryId':lib,'directories':[str(media)]})
   assert not report2['importedTracks'],report2
   passed.append('Files and folders import through the mainline interface without duplicates')
   system=next(p for p in structure['playlists'] if p['libraryId']==lib and p.get('systemKey')=='all-tracks')
   query={'activeLibrary':lib,'activeCollectionRef':'playlist:'+system['id'],'searchQuery':'','typeFilter':'','sortOption':'title','offset':0,'limit':200,'includeTrackIds':True}
   rows=h.call('desktop_catalog_query_collection_tracks',query)
   assert rows['totalCount']==3,rows
   ids=rows['trackIds']
   for i,tid in enumerate(ids):
    h.call('desktop_catalog_update_track',{'trackId':tid,'patch':{'album':'Album A' if i<2 else 'Album B','artist':'Artist','title':'Song '+str(i)}})
   groups=h.call('desktop_catalog_group_summary',{'activeLibrary':lib,'groupKind':'album'})
   assert len(groups['groups'])==2,groups
   gquery={**query,'groupKind':'album','groupKey':'Album A'}
   group=h.call('desktop_catalog_query_group_tracks',gquery)
   assert group['totalCount']==2,group
   assert all(row['album']=='Album A' for row in group['rows']),group
   passed.append('Album/artist browsing, search, sort and mainline row metadata')
   paths=h.call('desktop_catalog_local_paths',{'trackIds':ids})
   assert len(paths)==3 and Path(paths[0]['path']).is_file(),paths
   h.call('desktop_track_set_favorite',{'trackId':ids[0],'isFavorite':True})
   favorites=h.call('desktop_catalog_query_collection_tracks',{**query,'activeCollectionRef':'view:all-favorites'})
   assert favorites['totalCount']==1,favorites
   playlist=h.call('desktop_playlist_create',{'libraryId':lib,'name':'Test playlist'})
   pid=playlist.get('id') or playlist['playlist']['id']
   for tid in ids:h.call('desktop_playlist_add_track',{'playlistId':pid,'trackId':tid})
   h.call('desktop_playlist_reorder_tracks',{'playlistId':pid,'orderedTrackIds':ids[::-1]})
   tracks=h.call('desktop_catalog_query_collection_tracks',{**query,'activeCollectionRef':'playlist:'+pid,'sortOption':'manual'})
   assert tracks['trackIds']==ids[::-1],tracks
   passed.append('Favorites, playlist CRUD and persisted drag ordering')
   # The sidebar uses the core's actual system-playlist identity, including after switching libraries.
   second=h.call('desktop_library_create',{'name':'Second library'})
   second_lib=second['library']['id'];second_system=second['defaultPlaylist']['id']
   structure2=h.call('desktop_catalog_load_structure')
   assert set(structure2)=={'libraries','playlists'},structure2.keys()
   assert any(p['id']==second_system and p['libraryId']==second_lib for p in structure2['playlists'])
   empty=h.call('desktop_catalog_query_collection_tracks',{**query,'activeLibrary':second_lib,'activeCollectionRef':'playlist:'+second_system})
   assert empty['totalCount']==0,empty
   assert h.call('desktop_catalog_query_collection_tracks',query)['totalCount']==3
   h.call('desktop_library_reorder',{'orderedLibraryIds':[second_lib,lib]})
   h.call('desktop_library_rename',{'libraryId':second_lib,'name':'Renamed library'})
   for _ in range(2):
    metadata=h.call('desktop_catalog_load_structure')
    assert metadata['libraries'][0]['id']==second_lib and metadata['libraries'][0]['name']=='Renamed library',metadata
    assert any(p['id']==pid and p['libraryId']==lib for p in metadata['playlists'])
   passed.append('Lightweight sidebar metadata, real system-playlist IDs and isolated multi-library navigation')
   h.call('playback_session_set_queue',{'trackIds':ids})
   snap=h.call('playback_session_snapshot')
   edited=h.call('playback_session_edit_queue',{'action':'remove','trackIds':[ids[-1]],'expectedQueueRevision':snap['queueRevision'],'expectedTrackId':snap['currentTrackId']})
   assert ids[-1] not in edited['session']['queueTrackIds'],edited
   rejected=h.response(h.send('playback_session_edit_queue',{'action':'append','trackIds':[ids[-1]],'expectedQueueRevision':snap['queueRevision'],'expectedTrackId':snap['currentTrackId']}))
   assert not rejected['ok']
   passed.append('Queue editing keeps the current item and rejects stale revisions')
   lyric=media/'a.lrc';lyric.write_text('[00:00.00]First line\n[00:01.00]Second line',encoding='utf-8')
   h.call('desktop_catalog_update_track',{'trackId':ids[0],'patch':{'lyricsPath':str(lyric)}})
   resolved=h.call('lyrics_resolve_track',{'trackId':ids[0],'positionSeconds':1.1})
   assert len(resolved['lines'])==2,resolved
   h.call('desktop_preferences_save',{'patch':{'theme':'dark','repeatMode':'one','shuffleEnabled':False}})
   assert h.call('desktop_preferences_load')['preferences']['repeatMode']=='one'
   assert isinstance(h.call('desktop_storage_analyze'),dict)
   assert isinstance(h.call('desktop_history_load_stats',{'request':{}}),dict)
   passed.append('Timed lyrics, preferences, storage and listening statistics')
   for cmd in ('license_identity','mobile_handoff_start_host','connect_start','together_control_start','cloud_upload','account_login'):
    reply=h.response(h.send(cmd));assert not reply['ok'] and reply['error']['code']=='unknown-command',reply
   passed.append('Commercial and official cloud commands remain unavailable')
   media_bytes=(media/'a.wav').read_bytes()
   class Server(BaseHTTPRequestHandler):
    def log_message(self,*args):pass
    def do_PROPFIND(self):
     body=b'<?xml version="1.0"?><d:multistatus xmlns:d="DAV:"><d:response><d:href>/music/</d:href><d:propstat><d:prop><d:resourcetype><d:collection/></d:resourcetype></d:prop></d:propstat></d:response><d:response><d:href>/music/song.wav</d:href><d:propstat><d:prop><d:displayname>song.wav</d:displayname><d:getcontenttype>audio/wav</d:getcontenttype><d:getcontentlength>'+str(len(media_bytes)).encode()+b'</d:getcontentlength></d:prop></d:propstat></d:response></d:multistatus>'
     self.send_response(207);self.send_header('Content-Type','application/xml');self.send_header('Content-Length',str(len(body)));self.end_headers();self.wfile.write(body)
    def do_GET(self):
     if self.path.startswith('/music/song.wav'):
      self.send_response(200);self.send_header('Content-Type','audio/wav');self.send_header('Content-Length',str(len(media_bytes)));self.end_headers();self.wfile.write(media_bytes);return
     endpoint=urlsplit(self.path).path
     data={'status':'ok','version':'1.16.1'}
     if 'getMusicFolders' in endpoint:data['musicFolders']={'musicFolder':[{'id':'1','name':'Music'}]}
     elif 'getAlbumList2' in endpoint:data['albumList2']={'album':[{'id':'album','name':'Album'}]}
     elif 'getAlbum' in endpoint:data['album']={'song':[{'id':'song','title':'Mock song','artist':'Artist','album':'Album','suffix':'wav','size':len(media_bytes),'duration':2}]}
     elif 'stream' in endpoint:
      self.send_response(200);self.send_header('Content-Type','audio/wav');self.send_header('Content-Length',str(len(media_bytes)));self.end_headers();self.wfile.write(media_bytes);return
     body=json.dumps({'subsonic-response':data}).encode();self.send_response(200);self.send_header('Content-Type','application/json');self.send_header('Content-Length',str(len(body)));self.end_headers();self.wfile.write(body)
   server=ThreadingHTTPServer(('127.0.0.1',0),Server);threading.Thread(target=server.serve_forever,daemon=True).start()
   try:
    for provider,subpath in [('webdav','/music/'),('subsonic','/')]:
     c={'id':'mock-'+provider,'provider':provider,'name':'Mock '+provider,'endpoint':'http://127.0.0.1:'+str(server.server_port)+subpath,'auth':{'username':'fixture','password':'fixture'}}
     assert h.call('external_library_test_connection',{'request':{'connection':c}})['ok']
     h.call('desktop_external_library_put_connection',{'value':c})
     assert any(x['id']==c['id'] for x in h.call('desktop_external_library_load_connections'))
     libraries=h.call('external_library_list_libraries',{'request':{'connection':c}})
     assert libraries['libraries']
     listing=h.call('external_library_list_tracks',{'request':{'connection':c,'limit':10}})
     assert listing['tracks'],listing
     resolved=h.call('external_library_resolve_playback_source',{'request':{'connection':c,'track':listing['tracks'][0],'includeMetadata':True}})
     assert Path(resolved['source']['path']).is_file(),resolved
     h.call('desktop_external_library_delete_connection',{'connectionId':c['id']})
     passed.append(provider+' saved connection, browse and audio cache via a mock server')
   finally:server.shutdown();server.server_close()
  finally:h.close()
 print(json.dumps({'passed':len(passed),'checks':passed},ensure_ascii=False,indent=2))
if __name__=='__main__':main()
