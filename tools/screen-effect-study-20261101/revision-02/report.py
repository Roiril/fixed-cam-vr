"""Build only revision-02. Missing evidence is an error; no visual substitution."""
import argparse
import base64
import hashlib
import html
import json
import mimetypes
import os
from pathlib import Path


def sha(path):
    return hashlib.sha256(Path(path).read_bytes()).hexdigest()


def main():
    p = argparse.ArgumentParser(__doc__)
    for name in ('output', 'still-dir', 'still-image', 'still-view', 'still-preview', 'still-base', 'spatial-dir', 'spatial-variant-dir', 'spatial-preview', 'spatial-verification', 'reference-image', 'reference-video',
                 'new-video', 'old-still', 'old-video', 'comparison-manifest', 'feedback', 'house-css'):
        p.add_argument('--' + name, required=True, type=Path)
    p.add_argument('--audit', action='append', default=[], type=Path)
    p.add_argument('--still-native', action='append', required=True, type=Path,
                   help='Explicit selected native crop/comparison PNG; repeat for each image')
    p.add_argument('--still-rgba-hash-key', required=True)
    p.add_argument('--still-opaque-hash-key', required=True)
    p.add_argument('--previous-still-preview', type=Path)
    p.add_argument('--spatial-image', action='append', required=True, type=Path)
    args = p.parse_args()
    out = args.output.resolve()
    if out.name != 'revision-02':
        p.error('Output must be the separate revision-02 directory')
    out.mkdir(parents=True, exist_ok=True)
    used = {}

    def require(path):
        path = Path(path).resolve()
        if not path.is_file():
            raise FileNotFoundError(f'Required evidence missing: {path}')
        used[str(path)] = {'sha256': sha(path), 'bytes': path.stat().st_size}
        return path

    def read_json(path):
        return json.loads(require(path).read_text(encoding='utf-8-sig'))

    def data(path):
        path = require(path)
        mime = mimetypes.guess_type(path.name)[0] or 'application/octet-stream'
        return 'data:' + mime + ';base64,' + base64.b64encode(path.read_bytes()).decode('ascii')

    def link(path, label):
        path = require(path)
        return '<a href="' + html.escape(os.path.relpath(path, out).replace('\\', '/'), quote=True) + '">' + html.escape(label) + '</a>'

    def image(path, alt, cls=''):
        return f'<div class="pixel-box {cls}"><img loading="eager" src="{data(path)}" alt="{html.escape(alt, quote=True)}"></div>'

    def figure(path, caption):
        return '<figure>' + image(path, caption) + '<figcaption>' + html.escape(caption) + '</figcaption></figure>'

    still = args.still_dir
    spatial = args.spatial_dir
    variant = args.spatial_variant_dir
    facts = read_json(still / 'manifest.json')
    if facts.get(args.still_rgba_hash_key) != sha(require(args.still_image)):
        raise ValueError('Selected authored still no longer matches its manifest')
    if facts.get(args.still_opaque_hash_key) != sha(require(args.still_view)):
        raise ValueError('Selected opaque still no longer matches its manifest')
    still_verification = read_json(still / 'verification.json')
    comparison = read_json(args.comparison_manifest)
    # Contract deliberately explicit: the motion worker supplies matching-time pairs.
    entries = comparison.get('entries')
    if not entries or not all('reference' in e and 'revision' in e for e in entries):
        raise ValueError('Comparison manifest requires entries[{time,reference:{path,actualTime},revision:{path,actualTime}}]')
    times = [float(e['time']) for e in entries]
    if not any(3 <= t <= 3.3 for t in times) or not any(4.7 <= t <= 5 for t in times):
        raise ValueError('Native reference/new evidence must cover the 3–5 second transition')
    pair_html = []
    for e in entries:
        if not 3 <= float(e['time']) <= 5:
            continue
        pair = []
        for key, label in [('reference', '原動画'), ('revision', '今回')]:
            item = e[key]
            path = Path(item['path'])
            if not path.is_absolute():
                path = args.comparison_manifest.parent / path
            if 'sha256' in item and sha(require(path)) != item['sha256']:
                raise ValueError('Evidence image no longer matches comparison manifest: ' + str(path))
            pair.append(figure(path, f"{label}：指定 {e['time']} 秒 / 実PTS {item['actualTime']} 秒"))
        pair_html.append('<div class="media-grid">' + ''.join(pair) + '</div>')
    spatial_facts = read_json(args.spatial_verification)
    require(variant / 'manifest.json')
    require(spatial / 'source-manifest.json')
    require(spatial / 'projection-verification.json')
    # Camera parameters come from the selected renderer package, never the live simulator.
    assets_text = require(spatial / 'assets.js').read_text(encoding='utf-8-sig')
    assets = json.JSONDecoder().raw_decode(assets_text.split('=', 1)[1].lstrip())[0]
    camera = assets['camera']
    css = require(args.house_css).read_text(encoding='utf-8-sig')
    feedback = require(args.feedback).read_text(encoding='utf-8-sig')
    audits = [read_json(a) for a in args.audit]
    audit_html = '<p>今回の独立評価は未取得です。主観点は空欄です。過去の83点・85点は今回の承認でも新評価でもありません。</p>'
    if audits:
        audit_html = '<p>独立評価の原データを以下に示します。画素の実測とは別の評価です。主観点は対象ファイルごとに記録しています。ユーザーの目視確認へ移るため、85点を目指す追加反復と新しい動画採点は終了しました。</p>'
        evaluated_primary = set()
        for path, audit in zip(args.audit, audits):
            if not audit.get('selected_candidate_path') or not audit.get('candidate_sha256'):
                raise ValueError('Audit must name selected_candidate_path and candidate_sha256: ' + str(path))
            target = require(Path(audit['selected_candidate_path']))
            if sha(target) != audit['candidate_sha256']:
                raise ValueError('Audit target changed since evaluation: ' + str(target))
            is_primary = target in (args.still_view.resolve(), args.new_video.resolve())
            if is_primary:
                evaluated_primary.add(target)
            label = '今回の主選択への評価' if is_primary else '比較候補への評価（今回の主選択の点数ではありません）'
            score = audit.get('overall_similarity')
            score_text = '未取得' if score is None else f'{score}/100 — ' + ('85点未達' if score < 85 else '85点以上')
            audit_html += '<h3>' + html.escape(path.stem) + '</h3><p>' + label + '。' + score_text + '</p><p>評価対象：<code>' + html.escape(str(target)) + '</code><br>SHA-256：<code>' + audit['candidate_sha256'] + '</code></p><pre>' + html.escape(json.dumps(audit, ensure_ascii=False, indent=2)) + '</pre>'
        for target, name in [(args.still_view.resolve(), '主選択の静止画'), (args.new_video.resolve(), '主選択の動画')]:
            if target not in evaluated_primary:
                audit_html += '<p>' + name + 'の独立評価は未取得です。主観点は空欄です。</p>'
    still_uris = {'new': data(args.still_view), 'old': data(args.old_still), 'base': data(args.still_base)}
    still_captions = {'new': '今回：' + str(args.still_view.parent.name) + '/' + args.still_view.name, 'old': '旧版：' + args.old_still.name, 'base': '加工前：' + args.still_base.name}
    native = ''.join(figure(path, '選択した原寸比較：' + path.name) for path in args.still_native)
    spatial_gallery = ''.join(figure(path, '選択した空間比較：' + path.name) for path in args.spatial_image)
    diag = ' / '.join(link(variant / (name + '-3.2.png'), label) for name, label in [('surface','面ID'),('depth','深度'),('wire','分割面'),('holes','未撮影領域'),('weight','境界の変位抑制')])
    diag += ' / ' + link(spatial / 'camera-overlay-3.2.png', 'カメラ位置')
    spatial_links = link(args.spatial_preview, '空間変形の操作用プレビュー') + ' / ' + link(args.spatial_verification, '選択版の登録・変形・欠落の数値検証') + ' / ' + link(spatial / 'projection-verification.json', 'カメラ投影の照合')
    previous_link = '<p class="note">' + link(args.previous_still_preview, '直前の07：中央の縦端を細くする前') + '</p>' if args.previous_still_preview else ''
    camera_values = f"位置 ({camera['x']}, {camera['y']}, {camera['z']}) m。yaw {camera['yawDeg']}° / pitch {camera['pitchDeg']}° / roll {camera['rollDeg']}°。焦点距離 ({camera['fxPx']}, {camera['fyPx']}) px。主点 ({camera['cxPx']}, {camera['cyPx']}) px。元写真 {camera['srcW']}×{camera['srcH']}。method={camera.get('method')}。"
    coverage = facts.get('coverage', {})
    coverage_rows = ''.join('<tr><th>' + html.escape(str(name)) + '</th><td><pre>' + html.escape(json.dumps(values, ensure_ascii=False, indent=2)) + '</pre></td></tr>' for name, values in coverage.items())
    measurements = '<div class="tblwrap"><table><thead><tr><th>選択範囲</th><th>manifestの実測</th></tr></thead><tbody>' + coverage_rows + '</tbody></table></div>' if coverage_rows else '<p>選択範囲ごとのcoverageはmanifestに記載がありません。</p>'
    measurements += '<details><summary>選択候補の実測と保存則検査</summary><h3>manifest</h3><pre>' + html.escape(json.dumps(facts, ensure_ascii=False, indent=2)) + '</pre><h3>verification</h3><pre>' + html.escape(json.dumps(still_verification, ensure_ascii=False, indent=2)) + '</pre></details>'
    for path in (args.reference_video, args.new_video, args.old_video, args.still_image):
        require(path)
    source_summary = html.escape(json.dumps(used, ensure_ascii=False, indent=2))
    document = f'''<!doctype html><html lang="ja"><meta charset="utf-8"><meta name="viewport" content="width=device-width,initial-scale=1"><title>輪郭・黒い靄・空間変形の修正版</title><style>{css}
    button{{font:inherit;color:var(--ink);background:var(--surface);border:1px solid var(--line);padding:var(--sp-2) var(--sp-3);cursor:pointer;border-radius:0}}button[aria-pressed="true"]{{outline:2px solid var(--ink);outline-offset:-2px}}.controls{{display:flex;flex-wrap:wrap;gap:var(--sp-2);margin:var(--sp-4) 0}}.media-grid{{display:grid;grid-template-columns:repeat(2,minmax(0,1fr));gap:var(--sp-4)}}.spatial-grid{{display:grid;grid-template-columns:repeat(3,minmax(0,1fr));gap:var(--sp-4)}}figure{{min-width:0;margin:var(--sp-4) 0}}.pixel-box{{max-width:100%;overflow:auto;background:var(--surface)}}.pixel-box img{{display:block;width:100%;height:auto;max-width:100%}}.pixel-box.native-pixels img{{width:auto;max-width:none}}video{{display:block;width:100%;height:auto;background:var(--surface)}}pre{{white-space:pre-wrap;overflow-wrap:anywhere;font-size:var(--fs-sm)}}code{{overflow-wrap:anywhere}}.wrap.compact{{padding-top:var(--sp-4);padding-bottom:var(--sp-4)}}.status{{font-variant-numeric:tabular-nums;font-size:var(--fs-sm);color:var(--mute)}}@media(max-width:40rem){{.media-grid,.spatial-grid{{grid-template-columns:1fr}}}}
    </style><div class="wrap"><h1>輪郭・黒い靄・空間変形の現時点版</h1><p class="meta">2026-11-01 ／ revision-02 ／ 静止画08・動画05</p><p class="lead">ユーザーの目視確認用に、静止画08と動画05を並べました。85点に達した完成版とはしていません。本編への採用と実機での確認は未判定です。</p><p>静止画は全体の明暗と細線の保護を調整し、広い縦境界を選んで加工しました。動画は黒い靄の質感と3〜5秒の退場を修正し、世界座標の局所変形に参照で測った画面全体の運動を重ねました。</p><div class="controls"><button id="theme-light">明るい表示</button><button id="theme-dark">暗い表示</button><button id="pixel-zoom" aria-pressed="false">画像を原寸で見る</button></div><h2>明暗を調整し、広い縦境界を加工する</h2><p>原参照は輪郭と見やすさの比較に用います。色相とキャラクターは対象から除外します。今回はB専用の手動範囲を使います。全体の明暗調整と細線の保護を行い、広い縦境界だけに空間処理を加える試作です。自動認識や任意画像に使う汎用フィルタではありません。</p></div>
    <div class="wide media-grid">{figure(args.reference_image,'原参照スクリーンショット：色相・キャラクターは比較対象外')}<figure><div class="controls"><button data-still="new" aria-pressed="true">今回</button><button data-still="old" aria-pressed="false">旧版</button><button data-still="base" aria-pressed="false">加工前</button></div><div class="pixel-box"><img id="still-selected" src="{still_uris['new']}" alt="今回のB画像"></div><figcaption id="still-caption">{still_captions['new']}</figcaption></figure></div>
    <div class="wide">{native}</div><div class="wrap compact">{measurements}<p>08は07の中央の縦端を18pxから12pxへ細くし、511行中259行でぼかし成分を0にしました。他の板面・コード・床・縦2本は07と同じRGBAです。線の断面計測は07の結果を引き継ぐものとしてmanifestに帰属を残しています。黒い衣服の弱い線は未修正です。</p><p>この実測は選択した候補だけに対応します。別候補のRGB一致や線幅の結果を転用していません。保存則の検査は見た目の採否を示しません。</p><p>{link(args.still_preview,'静止画の全検証')} / {link(still / 'manifest.json','画素検証の原データ')}</p>{previous_link}<h2>原動画と今回を同じ時刻で再生する</h2><p>3〜5秒は旧い上側の窓を閉じながら、右下・下辺・中央下から別の窓を開きます。非対称な別々の場で制御します。黒い靄は密度場と移流による生成です。原動画のRGB素材は転写していません。</p><p>靄の細かい侵食と終盤の明るい下側の残り方は原参照と厳密には一致していません。赤・白・箔・文字の形は以前の固定素材を使っています。新しい動画の独立点は未取得です。</p></div>
    <div class="wide" id="motion-section"><div class="controls"><button id="start-both">0秒から再生</button><button id="loop-both">3〜5秒を反復</button><button id="ending-both">11秒から再生</button><button id="pause-both">停止</button></div><p class="status" id="play-status" role="status">停止中</p><div class="media-grid"><figure><video id="reference-video" controls preload="auto" playsinline src="{data(args.reference_video)}"></video><figcaption>原動画。音声はこの動画から再生します。</figcaption></figure><figure><video id="revision-video" controls preload="auto" playsinline muted src="{data(args.new_video)}"></video><figcaption>今回の修正版。比較用にミュートしています。</figcaption></figure></div><p>{link(args.new_video.parent / 'preview.html','今回の操作用プレビュー')}</p><details><summary>旧版Bとの比較</summary><p>{link(args.old_video,'旧 round-07 B の動画')}</p></details><h3>3〜5秒の原寸フレーム</h3><p>指定時刻とデコードした原PTSを各画像に記載しています。原寸ボタンで画素数を保った表示へ切り替わります。</p>{''.join(pair_html)}</div>
    <div class="wrap"><h2>世界座標の局所変形に、参照の全体運動を重ねる</h2><p>元写真を手動キャリブレーションした面へ投影します。世界座標で局所変形させ、固定カメラへ投影した後、参照で計測した共通の2D相似運動を重ねます。最後に1.35倍の表示範囲と固定したガラス表現を通します。全体のみ・局所のみ・合成・変形なしの4条件は同じ表示処理です。真の3Dカメラ運動を復元したものではありません。</p><p>面・仮物体の輪郭・外周から12px以内で局所変位の振幅を0へ戻し、未撮影領域の裂けを抑えます。輪郭は共通の全体運動に従い、内部を局所変形させる制約です。元画素の貼り戻しや穴埋めではありません。</p><p>棚と金属フレームの輪郭・奥行は仮のカードです。実測した3D復元ではありません。元UVの連続したレイと面の深度で可視領域を判定し、壁の横点線を改善しました。合成版の表示範囲に残る穴は3.2/6.5/8.5/10.5秒で141/200/161/182画素です。変形なしでも境界の数値誤差による1画素が残ります。フレーム縁の細い裂けも残ります。</p><p>既存の全CRTと画素一致する再現ではありません。同じB画像を2状態に使います。未撮影領域を鏡像・端の引き伸ばし・補間で創作していません。</p><p>カメラBの手動値：{camera_values}</p></div><div class="wide spatial-grid">{spatial_gallery}</div><div class="wrap compact"><p>{spatial_links}</p><p>{diag}</p><p>診断画像のマゼンタは未撮影領域です。幾何の不一致と画作りの採否は別に確認する必要があります。</p>
    <h2>独立評価と実測を分けて残す</h2>{audit_html}<details><summary>今回の赤入れ原文</summary><pre>{html.escape(feedback)}</pre></details><h2>今回使った入力を固定する</h2><p>画像・動画はこのHTMLに埋め込みました。操作用プレビューと数値検証は隣接する保存ファイルへリンクしています。入力ハッシュ・出力ハッシュは <a href="report-manifest.json">report-manifest.json</a> に残します。</p><details><summary>入力SHA-256一覧</summary><pre>{source_summary}</pre></details></div><script>
    const stillSources={json.dumps(still_uris)},stillCaptions={json.dumps(still_captions,ensure_ascii=False)};
    document.querySelectorAll('[data-still]').forEach(b=>b.onclick=()=>{{document.querySelectorAll('[data-still]').forEach(v=>v.setAttribute('aria-pressed',String(v===b)));document.getElementById('still-selected').src=stillSources[b.dataset.still];document.getElementById('still-caption').textContent=stillCaptions[b.dataset.still];}});
    document.getElementById('theme-light').onclick=()=>document.documentElement.dataset.theme='light';document.getElementById('theme-dark').onclick=()=>document.documentElement.dataset.theme='dark';
    document.getElementById('pixel-zoom').onclick=function(){{const enabled=this.getAttribute('aria-pressed')!=='true';this.setAttribute('aria-pressed',String(enabled));document.querySelectorAll('.pixel-box').forEach(box=>box.classList.toggle('native-pixels',enabled));}};
    const ref=document.getElementById('reference-video'),rev=document.getElementById('revision-video'),videos=[ref,rev],status=document.getElementById('play-status');let loop=false,active=false,seeking=false,loopCount=0,generation=0;
    function statusText(){{status.textContent=(active?(loop?'3〜5秒を反復中':'再生中'):'停止中')+' / 原 '+ref.currentTime.toFixed(2)+'秒 / 今回 '+rev.currentTime.toFixed(2)+'秒';}}
    async function seekBoth(t){{seeking=true;await Promise.all(videos.map(v=>new Promise((resolve,reject)=>{{if(Math.abs(v.currentTime-t)<.002){{resolve();return;}}const timeout=setTimeout(()=>reject(Error('seek timeout')),5000);v.addEventListener('seeked',()=>{{clearTimeout(timeout);resolve();}},{{once:true}});v.currentTime=t;}})));seeking=false;statusText();}}
    async function playAt(t,isLoop){{const operation=++generation;active=false;loop=isLoop;videos.forEach(v=>v.pause());await seekBoth(t);if(operation!==generation)return;active=true;await Promise.all(videos.map(v=>v.play()));statusText();}}
    function stopBoth(){{generation++;active=false;loop=false;videos.forEach(v=>v.pause());statusText();}}
    document.getElementById('start-both').onclick=()=>playAt(0,false);document.getElementById('loop-both').onclick=()=>playAt(3,true);document.getElementById('ending-both').onclick=()=>playAt(11,false);document.getElementById('pause-both').onclick=stopBoth;
    videos.forEach(v=>{{v.addEventListener('pause',()=>{{if(active&&!seeking&&!v.ended)stopBoth();}});v.addEventListener('play',()=>{{if(!active&&!seeking)playAt(v.currentTime,false);}});}});
    async function tick(){{if(active&&!seeking){{if(loop&&ref.currentTime>=5){{videos.forEach(v=>v.pause());await seekBoth(3);loopCount++;if(active)await Promise.all(videos.map(v=>v.play()));}}else if(Math.abs(ref.currentTime-rev.currentTime)>.065)rev.currentTime=ref.currentTime;statusText();}}requestAnimationFrame(tick);}}requestAnimationFrame(tick);
    ref.addEventListener('ended',()=>{{if(!loop){{active=false;rev.pause();statusText();}}}});ref.addEventListener('seeked',()=>{{if(!seeking&&active&&Math.abs(ref.currentTime-rev.currentTime)>.065)rev.currentTime=ref.currentTime;}});
    window.reportQA={{state:()=>({{active,loop,seeking,loopCount,times:videos.map(v=>v.currentTime),paused:videos.map(v=>v.paused)}}),seekBoth}};
    </script></html>'''
    document = document.replace(source_summary, html.escape(json.dumps(used, ensure_ascii=False, indent=2)), 1)
    # A forward link is recorded only after the output exists.
    report_path = out / 'report.html'
    tmp = out / 'report.html.tmp'
    tmp.write_text(document, encoding='utf-8', newline='\n')
    os.replace(tmp, report_path)
    manifest = {'schema': 1, 'report': str(report_path), 'report_sha256': sha(report_path),
                'evaluation_target_hash': {'still_RGBA': sha(args.still_image), 'still_opaque': sha(args.still_view), 'motion_MP4': sha(args.new_video)},
                'selected_paths': {'still_RGBA': str(args.still_image.resolve()), 'still_opaque': str(args.still_view.resolve()), 'motion_MP4': str(args.new_video.resolve())},
                'audits': [str(a) for a in args.audit], 'used_source_hashes': used,
                'builder_sha256': sha(Path(__file__)), 'subjective_score': None if not audits else 'See independent raw audits; no aggregate invented'}
    (out / 'report-manifest.json.tmp').write_text(json.dumps(manifest, ensure_ascii=False, indent=2), encoding='utf-8')
    os.replace(out / 'report-manifest.json.tmp', out / 'report-manifest.json')
    print(json.dumps({'report': str(report_path), 'sha256': manifest['report_sha256'], 'inputs': len(used)}, ensure_ascii=True))


if __name__ == '__main__':
    main()
