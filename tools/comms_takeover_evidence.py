"""Read applied takeover values without treating a delivered notice as rendered evidence."""
from __future__ import annotations


def analyze_takeover(events):
    samples = [e for e in events if e.get('ev') == 'commsTakeover']
    if not samples:
        return []
    output = []
    holds = [e for e in samples if e.get('phase') == 'LieHold']
    for e in holds:
        try:
            lie, ink, missing = float(e['lie']), float(e['glyph']), int(e['cx'])
        except (KeyError, ValueError, TypeError):
            output.append(('WARN', '改変後の報告の描画値が足りない'))
            continue
        if lie < 0.99 or ink < 0.99 or missing != 0:
            output.append(('FAIL', '改変後の否定文が欠けているか、表示されていない'))
        else:
            output.append(('OK', '改変後の否定文は欠落0で表示された'))
    phases = {e.get('phase') for e in samples}
    if 'Truth' in phases and 'Erase' in phases and holds:
        output.append(('OK', '検出文から消去を経て否定文へ進んだ'))
    elif 'Truth' in phases:
        output.append(('WARN', '検出文の改変は途中までの記録。中断またはログの範囲を確認する'))
    return output
