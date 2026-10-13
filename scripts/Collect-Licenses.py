"""Collect installed NuGet/runtime license notices for the release packages."""
import argparse, json, shutil, xml.etree.ElementTree as ET
from pathlib import Path

parser = argparse.ArgumentParser()
parser.add_argument('--root', type=Path, required=True)
parser.add_argument('--output', type=Path, required=True)
args = parser.parse_args()
args.output.mkdir(parents=True, exist_ok=True)
packages, folders = {}, set()
for project in ('PS3HddTool.Migrate', 'PS3HddTool.Avalonia'):
    assets = json.loads((args.root / project / 'obj/project.assets.json').read_text())
    folders.update(Path(p) for p in assets['packageFolders'])
    for key, info in assets['libraries'].items():
        if info.get('type') == 'package':
            packages[key] = info['path']
    for framework in assets['project'].get('frameworks', {}).values():
        for dependency in framework.get('downloadDependencies', []):
            if dependency['name'].lower() == 'microsoft.netcore.app.runtime.win-x64':
                version = dependency['version'].strip('[]').split(',')[0].strip()
                packages['Microsoft.NETCore.App.Runtime.win-x64/' + version] = dependency['name'].lower() + '/' + version

inventory = []
for key, relative in sorted(packages.items()):
    directory = next((f / relative for f in folders if (f / relative).is_dir()), None)
    if directory is None:
        raise RuntimeError('Package not found: ' + key)
    meta = {}
    nuspecs = list(directory.glob('*.nuspec'))
    if nuspecs:
        for element in ET.parse(nuspecs[0]).getroot().iter():
            name = element.tag.split('}')[-1]
            if name in ('authors', 'license', 'licenseUrl', 'projectUrl'):
                meta[name] = element.text or ''
                if name == 'license':
                    meta['licenseType'] = element.attrib.get('type')
            if name == 'repository':
                meta['repository'] = element.attrib.get('url', '')
    license_files = []
    for file in directory.rglob('*'):
        if not file.is_file():
            continue
        name = file.name.lower()
        explicitly_named = meta.get('licenseType') == 'file' and str(file.relative_to(directory)).replace('\\', '/') == meta.get('license')
        if explicitly_named or any(s in name for s in ('license', 'licence', 'notice', 'copying', 'copyright')):
            if file.suffix.lower() not in ('.txt', '.md', '.html', '.htm', ''):
                continue
            target = args.output / key / file.relative_to(directory)
            target.parent.mkdir(parents=True, exist_ok=True)
            shutil.copyfile(file, target)
            license_files.append(str(target.relative_to(args.output)).replace('\\', '/'))
    name = key.split('/')[0]
    fallback = []
    if name.startswith('Avalonia') and name != 'Avalonia.BuildServices':
        fallback.append('Avalonia-LICENSE.md')
    if name == 'Avalonia.Fonts.Inter':
        fallback.append('Inter-OFL.txt')
    if name == 'MicroCom.Runtime':
        fallback.append('MicroCom-LICENSE.txt')
    if name == 'Tmds.DBus.Protocol':
        fallback.append('Tmds-DBus-LICENSE.txt')
    for filename in fallback:
        target = args.output / key / filename
        target.parent.mkdir(parents=True, exist_ok=True)
        shutil.copyfile(args.root / 'licenses' / filename, target)
        license_files.append(str(target.relative_to(args.output)).replace('\\', '/'))
    build_only = name == 'Avalonia.BuildServices'
    if not license_files and not build_only:
        raise RuntimeError('Missing release license text: ' + key)
    inventory.append({'package': key, **meta, 'licenseFiles': license_files,
                      'buildTimeOnly': build_only, 'redistributed': not build_only})
shutil.copyfile(args.root / 'licenses/sources.json', args.output / 'canonical-license-sources.json')
(args.output / 'inventory.json').write_text(json.dumps(inventory, indent=2) + '\n')
print(json.dumps({'packages': len(inventory), 'packagesWithoutEmbeddedLicense': [p['package'] for p in inventory if not p['licenseFiles']]}))
