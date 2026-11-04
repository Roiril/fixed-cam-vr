from pathlib import Path
H=Path(__file__).resolve().parent
def edit(name,changes):
    p=H/name;s=p.read_text(encoding='utf-8')
    for old,new in changes:
        assert old in s,(name,old);s=s.replace(old,new)
    q=p.with_suffix(p.suffix+'.tmp');q.write_text(s,encoding='utf-8');q.replace(p)
edit('preview.template.html',[
 ('修正版2','修正版3'),
 ('<option value="local">空間が曲がる</option>','<option value="combined">全体の動きと空間の曲がり</option><option value="local">空間が曲がる</option>'),
 ('壁と床が曲がる動きに、密度の違う黒い靄を重ねています。','大きな傾きと前後の動きに、壁と床が曲がる動きと黒い靄を重ねています。'),
 ('表示は独立近似。','表示は独立近似。同じ1.35倍の表示範囲で動きを比べます。'),
 ('仮の奥行き</option>','仮の奥行き</option>')])
edit('motion-revision.js',[
 ("面の境界と外周から12pxの範囲は動きを抑えています。仮の物体の輪郭は固定されます。確認表示では靄と文字を外します。","面の境界と外周から12pxは内部の曲がりを抑え、全体の傾きと前後動に合わせます。仮の奥行きで動きを補う近似で、実空間を復元したものではありません。確認表示では靄と文字を外します。"),
 ("version:'revision-02'","version:'revision-02-pass-03'")])
edit('package.py',[
 ("['motion-revision.js','effects.js','preview.template.html']","['smoke-fields.js','effects.js']"),
 ("'pass':'02'","'pass':'03'"),
 ("'onlyEffectChange':'smoke-fields.js; compositor, display, scene input and frozen foreground byte-identical to pass01'","'onlyEffectChange':'spatial renderer combined; smoke-fields/effects byte-identical to pass02; sameB two-state compositor and timings retained'")])
edit('capture.mjs',[("{mode:'local',strength:1,diagnostic:'beauty',display:true}","{mode:'combined',strength:1,diagnostic:'beauty',display:true}")])
edit('compare.py',[
 ("['global','local','none']","['combined','global','local','none']"),
 ("(1170*3,570)","(1170*4,570)")])
print('Pass03 controls and four-way comparisons configured; spatial bundle awaits frozen module.')
