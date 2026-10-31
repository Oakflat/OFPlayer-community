"""Check the actual standalone dependency graph and production network boundary."""
import json
from pathlib import Path
import re
import subprocess
import xml.etree.ElementTree as ET

root=Path(__file__).resolve().parents[1]
meta=json.loads(subprocess.check_output(['cargo','metadata','--locked','--format-version','1','--filter-platform','x86_64-pc-windows-msvc'],cwd=root,text=True,encoding='utf-8'))
blocked={'tauri','tauri-build','ofplayer-hub','ofplayer-native-host','ureq','webrtc','quinn','mdns-sd','tokio-tungstenite','axum'}
packages=[]
for package in meta['packages']:
    assert package['name'] not in blocked, 'Excluded dependency: '+package['name']
    assert not package['name'].startswith(('ofplayer-hub-', 'ofplayer-cloudflare-')), 'Commercial transport dependency'
    if package['source'] is None:
        assert Path(package['manifest_path']).resolve().is_relative_to(root), 'External source dependency'
    else:
        assert package['source'].startswith('registry+https://github.com/rust-lang/crates.io-index'), 'Non-public package source'
    packages.append({'name':package['name'],'version':package['version'],'license':package.get('license')})
for project in (root/'src-winui').glob('*/*.csproj'):
    for reference in ET.parse(project).getroot().iter('ProjectReference'):
        path=project.parent/reference.attrib['Include'].replace('\\','/')
        assert path.resolve().is_relative_to(root) and path.is_file(), 'External project reference'
for source in (root/'src-winui').rglob('*.cs'):
    if 'OFPlayer.Community.Tests' in source.parts:continue
    text=source.read_text(encoding='utf-8-sig')
    if re.search(r'\b(HttpClient|HttpRequestMessage|WebSocket|TcpClient|TcpListener|UdpClient)\b',text):
        assert source.name=='DailyActivity.cs', 'Unexpected network client: '+str(source)
    assert not re.search(r'\b(OpenUriAsync|CreateFromUri|NetworkSourceFactory)\b',text), 'Remote video source exposed'
assert not (root/'src-tauri/Cargo.toml').exists(), 'Legacy Tauri remains an active build'
assert not (root/'package.json').exists(), 'Legacy frontend remains an active build'
for excluded in ('BackendAccountSessionService.cs','ConnectSessionService.cs','CloudLibraryProfile.cs','MainViewModel.Together.cs','MainViewModel.Hub.cs','MainViewModel.Licensing.cs'):
    assert not list((root/'src-winui').rglob(excluded)), 'Commercial application service restored: '+excluded
assert (root/'web/immersive.html').is_file() and (root/'web/lyric-capsule.html').is_file()
print(f'Community boundary passed: {len(packages)} package records; standalone projects; third-party providers and minimal DAU; no commercial services')
