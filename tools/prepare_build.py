"""Prepare an isolated Microsoft .NET SDK and upstream Fluent icon resources."""
import hashlib
import json
from pathlib import Path
import urllib.request
import xml.etree.ElementTree as ET
import zipfile

ROOT = Path(__file__).resolve().parents[1]
SDK = ROOT.parent / '.tools' / 'dotnet10'
ASSETS = ROOT / 'src' / 'LLG.Helper' / 'Assets'

def read(url):
    with urllib.request.urlopen(url, timeout=90) as response:
        return response.read()

def prepare_sdk():
    if (SDK / 'dotnet.exe').exists():
        print('Isolated SDK already present', flush=True)
        return
    metadata = json.loads(read('https://dotnetcli.blob.core.windows.net/dotnet/release-metadata/10.0/releases.json'))
    sdk = next(r['sdk'] for r in metadata['releases'] if r['sdk']['version'] == metadata['latest-sdk'])
    artifact = next(f for f in sdk['files'] if f['rid'] == 'win-x64' and f['url'].endswith('.zip'))
    SDK.mkdir(parents=True, exist_ok=True)
    archive = SDK.parent / ('dotnet-sdk-' + sdk['version'] + '-win-x64.zip')
    print('Downloading official .NET SDK ' + sdk['version'], flush=True)
    if not archive.exists():
        with urllib.request.urlopen(artifact['url'], timeout=90) as response, archive.open('wb') as output:
            while chunk := response.read(1024 * 1024):
                output.write(chunk)
    with archive.open('rb') as source:
        actual = hashlib.file_digest(source, 'sha512').hexdigest()
    if actual.lower() != artifact['hash'].lower():
        raise RuntimeError('SDK SHA-512 mismatch; archive not extracted')
    with zipfile.ZipFile(archive) as bundle:
        for entry in bundle.infolist():
            if not (SDK / entry.filename).resolve().is_relative_to(SDK.resolve()):
                raise RuntimeError('Archive path outside SDK directory')
        bundle.extractall(SDK)
    (SDK / 'source.json').write_text(json.dumps({'version': sdk['version'], 'url': artifact['url'], 'sha512': actual}, indent=2))
    print('SDK ready; SHA-512 verified', flush=True)

def prepare_icons():
    ASSETS.mkdir(parents=True, exist_ok=True)
    specs = [('StatusSuccess', 'Checkmark Circle', 'checkmark_circle', 'filled', '#0861ED'),
             ('StatusClock', 'Clock', 'clock', 'regular', '#626975'),
             ('StatusInfo', 'Info', 'info', 'regular', '#626975'),
             ('StatusError', 'Error Circle', 'error_circle', 'regular', '#B42318')]
    resources = ['<ResourceDictionary xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation" xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml">']
    manifest = []
    for key, folder, name, style, color in specs:
        url = 'https://raw.githubusercontent.com/microsoft/fluentui-system-icons/main/assets/' + urllib.parse.quote(folder) + '/SVG/ic_fluent_' + name + '_24_' + style + '.svg'
        svg = read(url)
        (ASSETS / (key + '.svg')).write_bytes(svg)
        paths = [e.attrib['d'] for e in ET.fromstring(svg).iter() if e.tag.endswith('path')]
        resources.append(f'<DrawingImage x:Key="{key}"><DrawingImage.Drawing><DrawingGroup>')
        for path in paths:
            resources.append(f'<GeometryDrawing Brush="{color}" Geometry="F1 {path}" />')
        resources.append('</DrawingGroup></DrawingImage.Drawing></DrawingImage>')
        manifest.append({'key': key, 'url': url, 'sha256': hashlib.sha256(svg).hexdigest()})
    resources.append('</ResourceDictionary>')
    (ASSETS / 'Icons.xaml').write_text('\n'.join(resources), encoding='utf-8')
    (ASSETS / 'Fluent-LICENSE.txt').write_bytes(read('https://raw.githubusercontent.com/microsoft/fluentui-system-icons/main/LICENSE'))
    (ASSETS / 'icons-source.json').write_text(json.dumps(manifest, indent=2), encoding='utf-8')
    print('Official Fluent icon resources prepared', flush=True)

if __name__ == '__main__':
    prepare_icons()
    prepare_sdk()
