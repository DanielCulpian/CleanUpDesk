"""Portable structural checks; these do not replace compilation or C# tests."""
from pathlib import Path
import re
import xml.etree.ElementTree as ET
import sys

sys.stdout.reconfigure(encoding='utf-8')

root = Path(__file__).resolve().parent.parent
def source(path):
    """Exclude generated and local-only paths from source checks."""
    return not any(part in {'bin', 'obj', '.tools', 'artifacts'} for part in path.relative_to(root).parts)

files = [p for p in [*root.rglob('*.xaml'), *root.rglob('*.csproj'), root / 'Directory.Build.props', root / 'CleanUpDesk.slnx', root / 'src/CleanUpDesk.App/app.manifest'] if source(p)]
for path in files:
    ET.parse(path)
print(f'PASS XML: {len(files)} well-formed files')
xaml = root / 'src/CleanUpDesk.App/MainWindow.xaml'
code = '\n'.join(p.read_text(encoding='utf-8-sig') for p in (root / 'src/CleanUpDesk.App').glob('*.cs') if 'partial class MainWindow' in p.read_text(encoding='utf-8-sig'))
events = {'Click', 'Closing', 'KeyDown', 'PreviewKeyDown', 'MouseDoubleClick', 'Sorting', 'SelectionChanged', 'SizeChanged'}
handlers = set()
for node in ET.parse(xaml).iter():
    for key, value in node.attrib.items():
        if key in events:
            handlers.add(value)
            assert re.search(r'\b' + re.escape(value) + r'\s*\(', code), f'Event without a handler: {value}'
print(f'PASS events: {len(handlers)} handlers present')
for project in (p for p in root.rglob('*.csproj') if source(p)):
    for item in ET.parse(project).iter('ProjectReference'):
        assert (project.parent / item.attrib['Include']).exists()
print('PASS local project references')
native = (root / 'src/CleanUpDesk.Core/NativeFiles.cs').read_text()
assert 'Directory.Delete(path, true)' not in native
assert 'if ((flags & 0x80) != 0)' in native
print('PASS structural guards: no opaque recursive deletion; nonrecyclable deletion veto')
for path in (p for p in (root / 'src').rglob('*') if source(p) and p.suffix in {'.cs', '.xaml'}):
    content = path.read_text(encoding='utf-8-sig', errors='strict')
    assert not re.search(r'\u00c3[\u0080-\u00bf]|\u00c2[\u0080-\u00bf]|\u00e2[\u0080-\u00bf\u2000-\u20ff]|[\u0080-\u009f\ufffd]', content), f'Damaged text encoding: {path}'
content = xaml.read_text(encoding='utf-8-sig')
for expected in ['Escribe un nombre', 'Últimos análisis', 'Tamaño lógico', 'Abrir carpeta']:
    assert expected in content, f'Missing or damaged Spanish UI text: {expected}'
assert 'Value="{Binding UsedPercent, Mode=OneWay}"' in content
for node in ET.parse(xaml).iter('{http://schemas.microsoft.com/winfx/2006/xaml/presentation}Run'):
    text = node.attrib.get('Text', '')
    if text.startswith('{Binding '):
        assert 'Mode=OneWay' in text, f'Run.Text requires explicit OneWay: {text}'
print('PASS UTF-8: Spanish characters and symbols; read-only UsedPercent binding')
print('These checks do not verify compilation, bindings, COM, or Windows behavior.')
