"""Build fixed-weight UI fonts from the OFL-licensed Noto Sans KR source.
Run after installing fonttools into artifacts/font-tools.
"""
from pathlib import Path
import sys
root = Path(__file__).resolve().parents[1]
sys.path.insert(0, str(root / 'artifacts/font-tools'))
from fontTools.ttLib import TTFont
from fontTools.varLib.instancer import instantiateVariableFont
source = root / 'artifacts/font-source/NotoSansKR.ttf'
output = root / 'SwiftCopy.App/Assets/Fonts'
license_text = (output / 'OFL.txt').read_text(encoding='utf-8')
for weight, style in [(500, 'Medium'), (600, 'SemiBold')]:
    print(f'Building fixed {style} font...', flush=True)
    font = TTFont(source)
    font = instantiateVariableFont(font, {'wght': weight}, inplace=True)
    names = {1: 'SwiftCopy Sans', 2: style, 3: f'SwiftCopySans-{style}-1.0',
             4: f'SwiftCopy Sans {style}', 6: f'SwiftCopySans-{style}',
             13: license_text, 16: 'SwiftCopy Sans', 17: style}
    for record in list(font['name'].names):
        if record.nameID in names:
            font['name'].setName(names[record.nameID], record.nameID,
                                 record.platformID, record.platEncID, record.langID)
    for name_id, value in names.items():
        font['name'].setName(value, name_id, 3, 1, 0x409)
    path = output / f'SwiftCopySans-{style}.ttf'
    font.save(path)
    print(f'Saved {path.name} ({path.stat().st_size:,} bytes)', flush=True)
