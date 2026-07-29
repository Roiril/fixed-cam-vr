// 🕹 ショーシミュレーション（実機なしでショーを検証する卓 UI）。
//   計画: .claude/plans/2026-07-25_show-simulator.md 段 S4
//
// フロアマップのドットをドラッグすると、その位置を毎 tick（20ms）供給してショーが**実時間で進む**。
// 判定は scenario-engine.js（Unity 純ロジックの JS ミラー）を通す。ミラーが Unity と同じ答えを出すことは
// golden fixture（scenario_walk.trace.json）で機械固定してある — この UI は「独自解釈」を一切持たない。
//
// ⚠ **これは「ロジックの検証」であって「体感の検証」ではない**。VR のスケール感・dip の体感時間・
//   MJPEG の遅延・位置合わせ・Quest の性能は卓では分からない（パネル下部に常時明示）。
//   卓で通っても実機確認は要る。

import {
  actorProxyGeometry, actorBodyGeometry, drawActorProxy, proxyIssueText, resolveProxyCalib, DEFAULT_HEIGHT_M,
} from './actor-proxy.js';
import { camColor, escapeHtml, createMediaCache, FX_DEFAULT } from './common.js';
import { createCompositeView } from './composite-view.js';
import { durationOf, onDurationResolved } from './media-duration.js';
import { createShowRunner, parseScenario, sampleAt, DEFAULT_TICK_MS } from './scenario-engine.js';
import { buildScenarioConfig, serializeScenario } from './show-scenario.js';
import { recordConfig, recStepIssue } from './record-model.js';

const LS_KEY = 'mawarimi.scenarios.v1';
const MAX_EVENTS = 300;
const SPEEDS = [1, 4, 16];

export function createShowSim(container, deps) {
  // deps: { getState: () => showJson, setDot: (x, z) => void }

  container.innerHTML = `
    <div class="ss-wrap">
      <div class="ss-left">
        <div class="ss-bar">
          <button class="ss-play accent">▶ 再生</button>
          <button class="ss-stop">⏹ 先頭へ</button>
          <span class="ss-clock">00:00.000</span>
          <span class="ss-speeds"></span>
          <span class="spacer"></span>
          <button class="ss-rec">● 記録</button>
          <span class="ss-recinfo"></span>
        </div>

        <div class="ss-note ss-howto">歩かせ方: 左のフロアマップを <b>🚶 歩かせる</b> にして、
          マップの上をマウスで押したままドラッグ（押した場所に体験者が立ちます）。</div>

        <div class="ss-screen">
          <div class="ss-screen-view">
            <canvas class="ss-canvas" width="480" height="360"></canvas>
            <!-- CG 人形の**輪郭だけ**を重ねる層。合成器は WebGL なので 2D を上に載せる。
                 卓は人形を実描画しない（設計 §2.1）ので、ここに出るのは足元・身長・向きの線。 -->
            <canvas class="ss-proxy" width="480" height="360"></canvas>
            <div class="ss-screen-badge"></div>
          </div>
          <div class="ss-screen-side">
            <div class="ss-screen-chip"><i></i><b class="ss-screen-cam">—</b></div>
            <div class="ss-screen-detail">停止中</div>
            <div class="ss-screen-note">映しているのは卓の合成器（Quest と同じ式）。ライブ映像はカメラが
              繋がっている時だけ出る。素材（事前映像・静止画）とマスク・画像加工は繋がっていなくても出る。</div>
          </div>
        </div>

        <div class="ss-readout">
          <span class="ss-ro"><i>周回</i><b class="ss-lap">—</b></span>
          <span class="ss-ro"><i>区間</i><b class="ss-seg">—</b></span>
          <span class="ss-ro"><i>確定ゾーン</i><b class="ss-zone">—</b></span>
          <span class="ss-ro"><i>位置</i><b class="ss-pos">—</b></span>
          <span class="ss-ro"><i>武装</i><b class="ss-armed">—</b></span>
        </div>

        <div class="ss-log-title">イベントログ<span class="ss-hint">時刻はシミュレーション内の経過</span></div>
        <div class="ss-log"></div>
      </div>

      <div class="ss-right">
        <div class="ss-note ss-status"></div>

        <div class="ss-block">
          <div class="ss-block-title">記録したシナリオ</div>
          <div class="ss-row">
            <input class="ss-name" type="text" placeholder="名前（例: 速く歩く）">
            <button class="ss-save">💾 保存</button>
          </div>
          <div class="ss-row">
            <select class="ss-list"></select>
            <button class="ss-replay">▶ 再実行</button>
          </div>
          <label class="ss-chk"><input class="ss-usesaved" type="checkbox"> 保存時の設定で再生（既定は今の show.json）</label>
          <div class="ss-savestate"></div>
        </div>

        <div class="ss-block ss-warn-block" style="display:none">
          <div class="ss-block-title">⚠ 設定の警告</div>
          <div class="ss-warnings"></div>
        </div>

        <div class="ss-block ss-limits">
          <div class="ss-block-title">これは実機の代わりにならない</div>
          <ul>
            <li>VR のスケール感・立体視、首追従の気持ちよさ</li>
            <li>dip の黒の体感時間、フェードの繋がり</li>
            <li>MJPEG の実レイテンシ・stall・砂嵐、Wi-Fi 由来の挙動</li>
            <li>位置合わせ（点タッチ登録）・OS recenter・コントローラ触覚</li>
            <li>Quest の性能（fps・発熱）</li>
          </ul>
          <div class="ss-hint">検証できるのは「この歩き方をしたら何が起きるか」だけ。<b>卓で通っても実機確認は要る。</b></div>
        </div>
      </div>
    </div>`;

  const q = (s) => container.querySelector(s);
  const playBtn = q('.ss-play'), stopBtn = q('.ss-stop'), recBtn = q('.ss-rec');
  const clockEl = q('.ss-clock'), speedsEl = q('.ss-speeds'), recInfoEl = q('.ss-recinfo');
  const chipEl = q('.ss-screen-chip i'), chipCamEl = q('.ss-screen-cam'), detailEl = q('.ss-screen-detail');
  const badgeEl = q('.ss-screen-badge'), howtoEl = q('.ss-howto');
  const lapEl = q('.ss-lap'), segEl = q('.ss-seg'), zoneEl = q('.ss-zone'), posEl = q('.ss-pos'), armedEl = q('.ss-armed');
  const logEl = q('.ss-log'), statusEl = q('.ss-status');
  const nameI = q('.ss-name'), saveBtn = q('.ss-save'), listSel = q('.ss-list'), replayBtn = q('.ss-replay');
  const useSavedChk = q('.ss-usesaved'), saveStateEl = q('.ss-savestate');
  const warnBlock = q('.ss-warn-block'), warnEl = q('.ss-warnings');

  // ---- 状態 -------------------------------------------------------------------
  let state = null;              // show.json
  let cfg = null, meta = null;   // buildScenarioConfig の結果
  let runner = null;
  let enabled = false;           // フロアマップの「シミュレーション」チェック
  let playing = false;
  let speed = 1;
  let simMs = 0;
  let acc = 0, lastWall = 0;
  let pos = { x: 0, z: -0.7 };
  let fedPos = { x: pos.x, z: pos.z };   // 直近の tick へ実際に供給した位置（tick 間の補間の始点）
  // 体の向きの近似。実機の follow は**体験者の頭 yaw**を使うが、卓は頭の向きを持っていないので
  // 歩いた方向で代用する（止まっている間は最後の向きを保つ）。厳密には違うので UI で必ず注記する。
  let walkYawDeg = 0;
  let hasWalkYaw = false;
  let events = [];
  let recording = false, recSamples = [], recHoldAt = null;
  let replay = null;             // { samples, endMs, name }
  let staleConfig = false;       // 実行中に show.json が変わった
  let runFinished = false;       // 走り切った（run.totalLaps）。実機は暗転して終わるので卓も止める
  let notice = '';               // 一時メッセージ（再実行おわり 等）。renderStatus が毎回描き直す
  let scenarios = [];            // { name, url? , local? }

  // ---- 設定の組み立て ---------------------------------------------------------
  function rebuild(force) {
    if (!state) return;
    if (playing && !force) { staleConfig = true; renderStatus(); return; }
    // 「素材の終わりまで」の尺は素材の実尺で解く（media-duration が測れた分だけ）。
    const built = buildScenarioConfig(state, { getDuration: durationOf });
    cfg = built.cfg;
    meta = built.meta;
    runner = createShowRunner(cfg);
    // 時計を 0 へ戻すので、前の走行のログは残さない（時刻が対応しなくなるため）。
    // 位置の補間始点も今の立ち位置へ揃える（先頭 tick で古い位置から一気に歩いたことにしない）。
    simMs = 0; acc = 0; fedPos = { x: pos.x, z: pos.z }; hasWalkYaw = false; walkYawDeg = 0;
    events = []; logEl.innerHTML = ''; renderLogEmpty();
    staleConfig = false;
    runFinished = false;
    renderWarnings();
    renderAll();
  }

  function camLabel(i) {
    if (!Number.isInteger(i) || i < 0) return '—';
    const c = meta && meta.cameras[i];
    return c ? `カメラ ${c.id}` : `#${i}`;
  }

  // ---- 描画 -------------------------------------------------------------------
  function renderSpeeds() {
    speedsEl.innerHTML = '';
    for (const s of SPEEDS) {
      const b = document.createElement('button');
      b.className = 'ss-speed' + (s === speed ? ' on' : '');
      b.textContent = `×${s}`;
      b.onclick = () => { speed = s; renderSpeeds(); };
      speedsEl.appendChild(b);
    }
  }

  const fmtClock = (ms) => {
    const t = Math.max(0, Math.round(ms));
    const m = Math.floor(t / 60000), s = Math.floor((t % 60000) / 1000), r = t % 1000;
    return `${String(m).padStart(2, '0')}:${String(s).padStart(2, '0')}.${String(r).padStart(3, '0')}`;
  };

  const baseName = (u) => String(u || '').split('/').pop();

  // 「いま画面に映っているもの」の**唯一の解決点**。文字（チップ・説明・⚠ バッジ）も
  //   実画（合成器へ渡すライブ映像）もここだけを見る。
  //   演出が走っていればそのカットの内容が live より優先される
  //   （実機では TakeRunner が CameraSwitchDirector を占有し、live カットでは表示カメラごと変わる）。
  //   ⚠ 2 箇所で別々に解決していた頃、実画だけがゾーンのカメラのままで
  //     「文字は カメラ A・映像は カメラ C」になっていた（2026-07-26 修正）。
  // この卓は CG 人形を**描かない**（実描画の正は Unity。glTF + スキニング + IK + 影を二重実装すると
  // 一致を検証する手段が無くなる）。だからといって黙って落とすと「著作した演出が画から消えたのに
  // 誰も気づかない」— この現場が何度も潰してきた罪と同型になる。**必ず文字で言う。**
  function resolveScreen() {
    const d = resolveScreenInner();
    const st = d.shot && d.shot.step;
    if (st && st.cg) {
      const where = st.cgMode === 'fixed' ? '決めた位置' : '体験者の位置';
      // 輪郭は重ねるが、それは**見た目ではない**。文字の明示は残す（輪郭を「実際の絵」と
      // 読まれると、著作者が Unity での最終確認を飛ばす）。
      d.detail += `　👤 CG 人形「${st.cg}」が${where}に立ちます（輪郭だけ・実際の見た目は Unity で確認）`;
    }
    return d;
  }

  // ---- CG 人形の輪郭プロキシ（画に重ねる）--------------------------------------
  //   ribbon の 📍 画面で置く と**同じ幾何・同じ描画関数**を使う（別々に描くと、どちらが
  //   正しいのか分からなくなる）。ここは読み取り専用 — シミュレータから立ち位置は変えられない。
  const proxyCanvas = q('.ss-proxy');
  const proxyCtx = proxyCanvas.getContext('2d');

  /** 合成器の contain-fit（composite-view の preScale）と同じ写像。映像実寸 → キャンバス画素。 */
  function frameToCanvas(imgW, imgH) {
    const W = proxyCanvas.width, H = proxyCanvas.height;
    const fa = W / H, a = (imgW > 0 && imgH > 0) ? imgW / imgH : fa;
    const [sx, sy] = a > fa ? [1, fa / a] : [a / fa, 1];
    return (p) => ({
      x: W / 2 + (p.u / imgW - 0.5) * sx * W,
      y: H / 2 + (p.v / imgH - 0.5) * sy * H,
    });
  }

  /**
   * 人形の輪郭を画へ重ねる。**出ない時はその理由を返す**（返り値はバッジに出る）。
   * 黙って落とすのは「著作した演出が黙って消える」と同型の罪。
   */
  function drawProxyOverlay(d) {
    proxyCtx.clearRect(0, 0, proxyCanvas.width, proxyCanvas.height);
    const st = d.shot && d.shot.step;
    if (!st || !st.cg || !state || !meta) return '';
    // 素材（事前映像・静止画）のカットには構図を決めるカメラが無い。素材はいつどこで撮ったか
    // 分からない画なので、どのカメラの較正を当てても人形のパースが合わない。
    // **実機（TakeRunner）も素材カットでは人形を出さない**ので、卓もそう言う。
    if (d.cam < 0) {
      return `👤 人形「${st.cg}」が置かれていますが、素材のカットなので**実機でも出ません**`
        + '（素材の構図と人形のパースが合わないため）。人形を出すならライブか録画のカットへ';
    }
    const cam = (state.cameras || [])[d.cam];
    if (!cam) return '';

    // 実寸はライブ映像が真（来ていなければ較正の焼き込み値で代用する）。
    const camObj = meta.cameras[d.cam];
    const img = (camObj && deps.getLiveImg) ? deps.getLiveImg(camObj.id) : null;
    const imgW = (img && img.naturalWidth) || (cam.calib && cam.calib.srcW) || 640;
    const imgH = (img && img.naturalHeight) || (cam.calib && cam.calib.srcH) || 480;

    const res = resolveProxyCalib(cam, imgW, imgH);
    // 姿勢が無いカメラでは実機も人形を出さない。卓だけ出すと「卓では出たのに実機で出ない」になる。
    if (!res.calib) return `👤 ${res.note}`;
    const actor = (state.actors || []).find((a) => a.id === st.cg) || null;
    if (!actor) return `👤 人形「${st.cg}」は 🎭 CG 人形に定義がありません（実機でも出ません）`;
    const heightM = actor.heightM > 0 ? actor.heightM : DEFAULT_HEIGHT_M;

    // follow は体験者の足元（＝いまドラッグしている点）。向きは卓が持っていないので矢印を出さない
    // （適当な向きを描くと、それが著作値だと誤読される）。
    const fixed = st.cgMode === 'fixed';
    // follow は体験者の足元（＝いま歩かせている点）。向きは実機だと頭の yaw だが卓は持っていないので
    // 歩いた方向で代用する（下の注記でそう言う）。止まっている間は最後に向いた方を保つ。
    const place = fixed
      ? (st.hasPlacement && st.placement ? st.placement : placementFromActor(actor))
      : { x: pos.x, z: pos.z, yawDeg: hasWalkYaw ? walkYawDeg : 0 };
    let g = actorProxyGeometry(res.calib, place, heightM, { srcW: imgW, srcH: imgH });
    // follow の矢印は「著作した向き」ではないので、掴めるハンドルの見た目にはしない。
    if (!fixed) g = { ...g, arrow: null };
    const body = actorBodyGeometry(res.calib, place, heightM, { srcW: imgW, srcH: imgH });

    if (g.visible) {
      drawActorProxy(proxyCtx, g, {
        toPx: frameToCanvas(imgW, imgH),
        scale: proxyCanvas.width / 640,
        dim: res.source !== 'calib',
        body,
        label: (actor.name || actor.id) + (res.source === 'pose' ? '（概算）' : ''),
      });
    }
    const issue = proxyIssueText(g, res.source);
    if (issue) return `👤 ${actor.name || actor.id}: ${issue}`;
    if (!fixed) {
      return `👤 ${actor.name || actor.id}: 体験者の位置に追従中`
        + (hasWalkYaw ? '（体の向きは歩いた方向で代用。実機は頭の向き）' : '（歩かせると体の向きが付きます）');
    }
    return '';
  }
  function placementFromActor(a) {
    return a ? { x: a.fixedX || 0, z: a.fixedZ || 0, yawDeg: a.fixedYawDeg || 0 } : { x: 0, z: 0, yawDeg: 0 };
  }

  function resolveScreenInner() {
    if (!runner) return { cam: -1, shot: null, detail: '—' };
    const shot = currentShot();
    if (shot) {
      const { take: t, step: st } = shot;
      const nth = `カット ${runner.activeStepIndex + 1}/${t.steps.length}`;
      const overlay = st.cueId ? ` + cue ${st.cueId}` : '';
      if (st.source === 'live') {
        // カットが指すカメラが正（未指定のときだけ「いま映っているもの」に従う）。
        const cam = st.camera >= 0 ? st.camera : runner.shownCamera;
        return { cam, shot, detail: `🎬 ${t.id}（${nth}: ${camLabel(cam)} のライブ${overlay}）` };
      }
      if (st.source === 'inherit') {
        return { cam: runner.shownCamera, shot, detail: `🎬 ${t.id}（${nth}: ライブに重ね${overlay}）` };
      }
      if (st.source === 'rec') {
        // 端末内録画は「その周・そのカメラで録った映像」。卓には録画が無いので live で代用して見せる。
        const cam = st.camera >= 0 ? st.camera : runner.shownCamera;
        const lap = st.recLap > 0 ? `${st.recLap}周目 ` : '';
        return { cam, shot, detail: `🎬 ${t.id}（${nth}: ${lap}${camLabel(cam)} の録画${overlay}・卓ではライブで代用）` };
      }
      const label = st.source === 'clip' ? '動画' : '静止画';
      return { cam: -1, shot, detail: `🎬 ${t.id}（${nth}: ${label} ${baseName(st.assetUrl) || st.cueId || '素材未設定'} を全面）` };
    }
    return { cam: runner.shownCamera, shot: null, detail: enabled ? 'ライブ映像' : 'ライブ映像（停止中）' };
  }

  // ==========================================================================
  //  画面プレビュー（実画）— カメラ列・cue エディタと同じ合成器を provider で駆動する。
  //    実機の ScreenComposite と同じ式（live に素材をマスク合成 → post）。
  //    映せないもの: カメラが繋がっていなければライブは黒（素材と post は出る）。
  // ==========================================================================
  const media = createMediaCache();
  let lastShotKey = '';

  /** いま演出が持っているカット（無ければ null = ライブそのまま）。 */
  function currentShot() {
    if (!runner || !meta || !runner.takeActive) return null;
    const t = meta.takes[runner.activeTakeIndex];
    const st = t && t.steps[runner.activeStepIndex];
    if (!t || !st) return null;
    return { take: t, step: st, key: `${t.id}#${runner.activeStepIndex}` };
  }

  /** 画像加工の解決（実機と同じ カット > 区間 > カメラ > 全体）。 */
  function resolvePost(shot, camIdx) {
    if (shot && shot.step.hasPost && shot.step.post) return shot.step.post;
    const seg = runner && runner.segment;
    const segPost = seg && meta ? meta.segmentPosts[`${seg.lap}:${seg.camera}`] : null;
    if (segPost) return segPost;
    const cam = (meta && camIdx >= 0) ? meta.cameras[camIdx] : null;
    if (cam && cam.post) return cam.post;
    return (meta && meta.globalPost) || FX_DEFAULT;
  }

  // カットが変わった瞬間に頭出し（trimStart）して再生。早送り中は再生速度も合わせる。
  function syncVideo(el, shot) {
    if (shot.key !== lastShotKey) {
      lastShotKey = shot.key;
      try { el.currentTime = Math.max(0, shot.step.trimStartSec || 0); } catch { /* metadata 前 */ }
    }
    el.playbackRate = Math.max(0.0625, Math.min(16, speed));
    if (el.paused) { try { el.play().catch(() => {}); } catch { /* noop */ } }
  }

  function sampleScreen() {
    // 文字表示と同じ解決を使う（カットが live なら**そのカットのカメラ**を映す）。
    const scr = resolveScreen();
    const camIdx = scr.cam;
    const camObj = (meta && camIdx >= 0) ? meta.cameras[camIdx] : null;
    const liveImg = (camObj && deps.getLiveImg) ? deps.getLiveImg(camObj.id) : null;
    const shot = scr.shot;
    if (!shot) lastShotKey = '';

    let overlayEl = null, overlayReady = false, overlayW = 0, overlayH = 0, maskEl = null;
    let strength = 1, fadeSec = 0.3;
    const url = shot && shot.step.playUrl;
    if (url) {
      const rec = media.get(url);
      if (rec && rec.el) {
        overlayEl = rec.el;
        overlayReady = rec.ready;
        overlayW = rec.isVideo ? (rec.el.videoWidth || 0) : (rec.el.naturalWidth || 0);
        overlayH = rec.isVideo ? (rec.el.videoHeight || 0) : (rec.el.naturalHeight || 0);
        if (rec.isVideo && rec.ready) syncVideo(rec.el, shot);
      }
      // マスク無し = 全面差し替え（composite-view の既定と同じ）。
      maskEl = shot.step.maskUrl ? media.getImage(shot.step.maskUrl) : null;
      strength = shot.step.strength >= 0 ? shot.step.strength : 1;
      fadeSec = shot.step.fadeInSec >= 0 ? shot.step.fadeInSec : 0.3;
    }
    return {
      liveImg, overlayEl, overlayReady, overlayW, overlayH, maskEl,
      overlayOn: !!overlayEl, fadeSec, strength, feather: 0,
      post: resolvePost(shot, camIdx),
      // 尺の管理は runner（Unity と同じ判定）が持つ。合成器には終端を判定させない。
      trimEnd: 0, onVideoEnd: null,
    };
  }

  createCompositeView(q('.ss-canvas'), { sample: sampleScreen });

  let lastScreenKey = '';
  function renderScreen() {
    const d = resolveScreen();
    // 輪郭は毎 tick 描き直す（follow は体験者の足元に付いて動くので、DOM の差分では間引けない）。
    const proxy = drawProxyOverlay(d);
    const issue = [screenIssue(d), proxy].filter(Boolean).join(' / ');
    // 停止中も毎 tick 呼ばれるので、内容が変わった時だけ DOM を書く。
    const key = `${d.cam}|${d.detail}|${issue}`;
    if (key === lastScreenKey) return;
    lastScreenKey = key;
    if (d.cam >= 0) {
      chipEl.style.background = camColor(d.cam);
      chipEl.style.visibility = 'visible';
      chipCamEl.textContent = camLabel(d.cam);
    } else {
      chipEl.style.visibility = 'hidden';
      chipCamEl.textContent = runner && runner.takeActive ? '素材' : '—';
    }
    detailEl.textContent = d.detail;
    badgeEl.textContent = issue;
  }

  /**
   * 「録画」カットが実機で本当に録れているか（show.json record との照合）。
   * 判定は record-model が単一の正（卓のパネル・本番前チェック・リボンと同じ文言になる）。
   */
  function recCoverageIssue(step) {
    return recStepIssue(recordConfig(state), step, (meta && meta.cameras ? meta.cameras.length : 0));
  }

  // 画面が黒いとき、それが「壊れている」のか「配信が来ていない」のかを画面の上で言う。
  //   卓は現場でカメラ 3 台のうち 1 台だけ繋がっている、という状態が普通に起きる。
  //   引数は resolveScreen() の結果（文字・実画と同じ解決を共有する）。
  function screenIssue(d) {
    if (!runner || !meta) return '';
    const shot = d.shot;
    if (shot && shot.step.playUrl) return '';                 // 素材が映っている
    // ライブを映すカット（live / inherit / 卓ではライブ代用の rec）は、素材が無くて当たり前。
    const liveLike = !shot || shot.step.source === 'live' || shot.step.source === 'inherit'
      || shot.step.source === 'rec';
    if (!liveLike) return '⚠ このカットは素材が未設定です（実機では飛ばされます）';
    if (shot && shot.step.source === 'rec') {
      // 「録れない設定の録画」を黙ってライブで代用すると、卓では成立しているのに実機では
      // カットごと消える（著作者に発見手段が無い）。設定と照合して必ず言う。
      const miss = recCoverageIssue(shot.step);
      if (miss) return `⚠ ${miss}`;
      return `${camLabel(d.cam)} の録画を映すカットです（卓には端末内録画が無いのでライブで代用表示）`;
    }
    if (d.cam < 0) return '';
    const camObj = meta.cameras[d.cam];
    const img = (camObj && deps.getLiveImg) ? deps.getLiveImg(camObj.id) : null;
    if (img && img.naturalWidth) return '';
    return `${camLabel(d.cam)} のライブ映像が来ていません（卓にカメラが繋がっていないだけで、`
      + '演出の判定・素材・画像加工はこのまま検証できます）';
  }

  function renderReadout() {
    if (!runner) return;
    lapEl.textContent = `${runner.lap} 周目`;
    const seg = runner.segment;
    segEl.textContent = seg ? `L${seg.lap}・${camLabel(seg.camera)}` : '—';
    const pend = runner.pendingZone;
    zoneEl.textContent = camLabel(runner.zoneCamera) + (pend >= 0 ? `（${camLabel(pend)} へ滞在待ち）` : '');
    posEl.textContent = `(${pos.x.toFixed(2)}, ${pos.z.toFixed(2)})`;
    armedEl.textContent = `${runner.armedCount} 本`;
    clockEl.textContent = fmtClock(simMs);
  }

  function renderStatus() {
    const bits = [];
    if (notice) bits.push(notice);
    if (!state) bits.push('show.json 待ち');
    else if (!meta || meta.zoneSource !== 'grid') bits.push('⚠ ゾーンを展開できません（フロアマップを確認）');
    else if (!enabled) bits.push('左のフロアマップを 🚶 歩かせる にすると動きます');
    else if (replay) bits.push(`▶ 再実行中: ${replay.name}`);
    else if (playing) bits.push(recording ? '● 記録中（マップをドラッグ）' : '実行中（マップをドラッグ）');
    else bits.push('一時停止中');
    if (runFinished) bits.push(`🏁 体験おわり（${totalLaps()} 周を走り切りました。⏹ 先頭へ でやり直す）`);
    if (staleConfig) bits.push('⚠ show.json が更新されました（⏹ 先頭へ で反映）');
    statusEl.textContent = bits.join(' / ');
    statusEl.className = 'ss-note ss-status' + (bits.some((b) => b.startsWith('⚠')) ? ' warn' : '');
    // 歩かせ方の説明は「まだ動かしていない人」だけに要る。走り出したら畳んで画面を空ける。
    howtoEl.style.display = (enabled && playing) ? 'none' : '';
  }

  function renderWarnings() {
    const ws = (meta && meta.warnings) || [];
    warnBlock.style.display = ws.length ? '' : 'none';
    warnEl.innerHTML = ws.map((w) => `<div class="ss-warning">${escapeHtml(w)}</div>`).join('');
  }

  function renderAll() { renderScreen(); renderReadout(); renderStatus(); }

  // ---- イベントログ -----------------------------------------------------------
  const EVENT_STYLE = {
    screen: 'ev-screen', zone: 'ev-zone', lap: 'ev-lap',
    seg: 'ev-seg', take: 'ev-take', step: 'ev-step', end: 'ev-end',
    drop: 'ev-drop', finish: 'ev-lap',
  };

  function describeEvent(e) {
    switch (e.kind) {
      case 'screen': return `画面 → ${camLabel(e.a)}`;
      case 'zone': return `ゾーン確定 ${camLabel(e.a)}`;
      case 'lap': return `周回 → ${e.a} 周目`;
      // 実機はここで暗転して終わる（次の体験者を待つ）。卓も同じところで止める。
      case 'finish': return `🏁 体験おわり（${e.a} 周を走り切りました）`;
      case 'seg': return `区間 L${e.a}・${camLabel(e.b)} へ進入`;
      case 'take': return `演出開始 ${e.id}`;
      case 'step': {
        const t = meta && meta.takes.find((x) => x.id === e.id);
        const st = t && t.steps[e.a];
        const what = st
          ? (st.source === 'live' ? `${camLabel(st.camera >= 0 ? st.camera : e.b)} のライブ`
            : st.source === 'inherit' ? `ライブに重ね${st.cueId ? ` (${st.cueId})` : ''}`
              : `${st.source === 'clip' ? '動画' : '静止画'} ${baseName(st.assetUrl) || st.cueId || '素材なし'}`)
          : (e.b >= 0 ? camLabel(e.b) : '');
        return `カット ${e.a + 1} ${e.id}${what ? `: ${what}` : ''}`;
      }
      // 「出ないまま区間が終わった」= 著作した山場が体験者に届かなかったということ。必ず見せる。
      case 'drop': return `✖ 出ませんでした ${e.id}（${e.a === 0
        ? '別の演出 / ライブ卓が画面を使用中のまま区間が終わった'
        : '同じ区間の別の演出が先に選ばれた'}）`;
      default: return e.kind;
    }
  }

  // 空のログは「ただの黒い箱」に見えるので、何をすれば埋まるかを書いておく。
  function renderLogEmpty() {
    if (logEl.childElementCount) return;
    logEl.innerHTML = '<div class="ss-log-empty">（まだイベントなし）<br>'
      + '左のフロアマップを 🚶 歩かせる にしてドラッグすると、'
      + 'ゾーン確定・周回・区間・演出の発火がここに時刻つきで並びます。</div>';
  }

  function pushEvents(list) {
    if (!list.length) return;
    const empty = logEl.querySelector('.ss-log-empty');
    if (empty) empty.remove();
    for (const e of list) {
      events.push(e);
      const row = document.createElement('div');
      row.className = `ss-ev ${EVENT_STYLE[e.kind] || ''}`;
      const t = document.createElement('i');
      t.textContent = fmtClock(e.t);
      const b = document.createElement('span');
      b.textContent = describeEvent(e);
      row.append(t, b);
      logEl.appendChild(row);
    }
    while (logEl.childElementCount > MAX_EVENTS) logEl.removeChild(logEl.firstChild);
    events = events.slice(-MAX_EVENTS);
    logEl.scrollTop = logEl.scrollHeight;
  }

  // ---- 記録 -------------------------------------------------------------------
  function recordSample(tMs, p) {
    const last = recSamples[recSamples.length - 1];
    if (!last) { recSamples.push({ tMs, x: p.x, z: p.z }); recHoldAt = null; return; }
    if (last.x === p.x && last.z === p.z) { recHoldAt = tMs; return; }
    // 立ち止まりを保存する: 動く直前の時刻に「同じ位置」の点を打ってから新しい点を打つ
    //   （これが無いと再生時に長い直線補間になり「立ち止まった」が消える）。
    if (recHoldAt !== null && recHoldAt > last.tMs) recSamples.push({ tMs: recHoldAt, x: last.x, z: last.z });
    recHoldAt = null;
    recSamples.push({ tMs, x: p.x, z: p.z });
  }

  function flushRecord() {
    const last = recSamples[recSamples.length - 1];
    if (last && (last.tMs < simMs)) recSamples.push({ tMs: simMs, x: pos.x, z: pos.z });
  }

  function renderRecInfo() {
    recBtn.classList.toggle('on', recording);
    recBtn.textContent = recording ? '■ 記録停止' : '● 記録';
    recInfoEl.textContent = recSamples.length ? `記録 ${recSamples.length} 点 / ${fmtClock(recSamples[recSamples.length - 1].tMs)}` : '';
  }

  // ---- 時計 -------------------------------------------------------------------
  // ⚠ ドラッグ位置は「このフレームで歩いた先」であって「瞬間移動した先」ではない。
  //   1 フレーム分の tick 全部に同じ位置を配ると、体験者は **テレポートして立ち止まる** 動き方になる。
  //   通過ラインの判定は「前 tick の位置 → 今 tick の位置」の線分交差なので、
  //   1 tick の移動が 1m（LINE_MAX_STEP_M）を超えるとテレポート扱いで**横断が数えられない**。
  //   ×4 / ×16 の早送りでは 1 フレームで sim 時間が 0.5 秒進むため普通のドラッグでも簡単に超える。
  //   → 前回供給した位置から今の位置まで tick 数で等速に分配する（replay の sampleAt と同じ扱い）。
  function advance(dtMs) {
    acc += dtMs;
    const total = Math.min(Math.floor(acc / DEFAULT_TICK_MS), 4000); // 早送り中の tick 数上限（暴走防止）
    const from = { x: fedPos.x, z: fedPos.z };
    const to = { x: pos.x, z: pos.z };
    for (let i = 1; i <= total; i++) {
      acc -= DEFAULT_TICK_MS;
      simMs += DEFAULT_TICK_MS;
      let p;
      if (replay) {
        p = sampleAt(replay.samples, simMs);
        pos = p;
        if (deps.setDot) deps.setDot(p.x, p.z);
      } else {
        const u = i / total;
        p = { x: from.x + (to.x - from.x) * u, z: from.z + (to.z - from.z) * u };
      }
      const dx = p.x - fedPos.x, dz = p.z - fedPos.z;
      if (Math.hypot(dx, dz) > 0.01) {   // 1cm 未満の揺れでは向きを変えない
        walkYawDeg = Math.atan2(dx, dz) * 180 / Math.PI;
        hasWalkYaw = true;
      }
      fedPos = p;
      pushEvents(runner.step(simMs, p.x, p.z));
      if (recording) recordSample(simMs, p);
      if (replay && simMs >= replay.endMs) { finishReplay(); break; }
      if (checkRunFinished()) break;
    }
  }

  // 体験の終端（show.json run.totalLaps）。実機は走り切ると暗転して終わるので、卓でも同じところで止める。
  // 止めないと「3 周で終わる設定なのに 4 周目の演出が卓では動いて見える」という嘘になり、
  // 逆に**3 周目の最後に置いた離脱時の演出が出るかどうか**を卓で確かめられない。
  //
  // 走行中の演出は見せ切る（実機の ShowRunLogic と同じ規則）— ここを合わせないと
  // 「卓では山場が出たのに実機では出ない」の逆パターンを作る。
  function checkRunFinished() {
    if (runFinished || !runner) return false;
    const total = totalLaps();
    if (total <= 0 || runner.lap <= total) return false;
    if (runner.takeActive) return false;   // 見せ切ってから畳む
    runFinished = true;
    pause();
    pushEvents([{ kind: 'finish', tMs: simMs, a: totalLaps() }]);
    renderAll();
    return true;
  }

  function totalLaps() {
    const r = (state && state.run) || {};
    const n = parseInt(r.totalLaps, 10);
    return Number.isFinite(n) && n > 0 ? n : 3;   // 既定は実機と同じ 3 周
  }

  // rAF はタブ非表示で止まる（裏タブで凍る）ので壁時計駆動。
  setInterval(() => {
    if (!runner) return;
    if (!playing) {
      // 停止中も画面表示だけは追う。カメラの MJPEG は起動直後まだ 1 枚も来ていないことがあり、
      // 一度だけ描いた「ライブ映像が来ていません」が繋がった後も残っていた（2026-07-26 修正）。
      renderScreen();
      return;
    }
    const now = performance.now();
    const dt = Math.min(now - lastWall, 250) * speed;   // タブ復帰の巨大 dt は捨てる
    lastWall = now;
    advance(dt);
    renderAll();
    if (recording) renderRecInfo();
  }, 33);

  function play() {
    if (!runner) rebuild(true);
    if (!runner) return;
    // 走り切った後の ▶ は先頭からやり直す（そのまま再開しても即また終わるだけで、
    // 「押したのに動かない」に見える）。
    if (runFinished) { resetRun(); return; }
    notice = '';
    playing = true;
    lastWall = performance.now();
    playBtn.textContent = '⏸ 一時停止';
    playBtn.classList.remove('accent');
    renderStatus();
  }
  function pause() {
    playing = false;
    playBtn.textContent = '▶ 再生';
    playBtn.classList.add('accent');
    renderStatus();
  }
  function resetRun() {
    pause();
    replay = null;
    notice = '';
    if (recording) { recording = false; flushRecord(); renderRecInfo(); }
    logEl.innerHTML = ''; renderLogEmpty();
    rebuild(true);
  }
  function finishReplay() {
    const name = replay ? replay.name : '';
    replay = null;
    pause();
    notice = `▶ 再実行おわり: ${name}`;   // pause → renderStatus の後に立てる（次の描画で残る）
    renderStatus();
  }

  playBtn.onclick = () => (playing ? pause() : play());
  stopBtn.onclick = () => resetRun();
  recBtn.onclick = () => {
    if (recording) {
      recording = false;
      flushRecord();
    } else {
      recording = true;
      recSamples = [];
      recHoldAt = null;
      recordSample(simMs, pos);
      if (!playing) play();
    }
    renderRecInfo();
    renderStatus();
  };

  // ---- シナリオの保存 / 一覧 / 再実行 -------------------------------------------
  function localAll() {
    try { return JSON.parse(localStorage.getItem(LS_KEY) || '{}'); } catch { return {}; }
  }
  function localSave(name, obj) {
    const all = localAll();
    all[name] = obj;
    localStorage.setItem(LS_KEY, JSON.stringify(all));
  }

  async function refreshScenarios() {
    let items = [];
    let server = false;
    try {
      const r = await fetch('/scenarios/list');
      if (r.ok) {
        const j = await r.json();
        if (Array.isArray(j.items)) { items = j.items.map((it) => ({ name: it.name, url: it.url })); server = true; }
      }
    } catch { /* サーバ未対応 / 未再起動 */ }
    const locals = Object.keys(localAll()).map((name) => ({ name, local: true }));
    scenarios = items.concat(locals.filter((l) => !items.some((s) => s.name === l.name)));
    listSel.innerHTML = '';
    if (!scenarios.length) {
      const o = document.createElement('option');
      o.textContent = '（保存したシナリオなし）'; o.value = '';
      listSel.appendChild(o);
    }
    scenarios.forEach((s, i) => {
      const o = document.createElement('option');
      o.value = String(i);
      o.textContent = s.local ? `${s.name}（この端末）` : s.name;
      listSel.appendChild(o);
    });
    saveStateEl.dataset.server = server ? '1' : '0';
    return server;
  }

  saveBtn.onclick = async () => {
    if (recording) { recording = false; flushRecord(); renderRecInfo(); }
    if (recSamples.length < 2) { saveStateEl.textContent = '✕ 記録がありません（● 記録 してからドットを動かす）'; return; }
    const name = (nameI.value || '').trim() || `walk_${new Date().toISOString().slice(0, 19).replace(/[:T-]/g, '')}`;
    const obj = serializeScenario(cfg, recSamples, { name });
    let where = '';
    try {
      const r = await fetch('/scenarios/save', { method: 'POST', body: JSON.stringify({ name, scenario: obj }) });
      const j = r.ok ? await r.json() : null;
      if (j && j.ok) where = `server:${j.path || j.name}`;
    } catch { /* fallthrough */ }
    if (!where) {
      localSave(name, obj);
      where = 'local';
    }
    saveStateEl.textContent = where === 'local'
      ? `💾 ${name}（この端末に保存 — 卓サーバに保存 API が無い / 未再起動のため）`
      : `💾 ${name} → tools/web-compositor/scenarios/`;
    nameI.value = '';
    await refreshScenarios();
  };

  replayBtn.onclick = async () => {
    const s = scenarios[parseInt(listSel.value, 10)];
    if (!s) return;
    let obj = null;
    if (s.local) obj = localAll()[s.name];
    else {
      try { obj = await (await fetch(s.url)).json(); } catch { obj = null; }
    }
    if (!obj || !Array.isArray(obj.samples) || !obj.samples.length) {
      saveStateEl.textContent = '✕ シナリオを読めませんでした';
      return;
    }
    const parsed = parseScenario(obj);
    // 既定は「いまの show.json の設定」で再実行する（設定を直しながら同じ歩きを試すため）。
    // 保存時の設定で再現したいときはチェックを入れる。
    const useSaved = useSavedChk.checked;
    if (!useSaved) rebuild(true);
    else { cfg = parsed.cfg; meta = buildScenarioConfig(state || {}).meta; runner = createShowRunner(cfg); }
    logEl.innerHTML = ''; renderLogEmpty();
    events = [];
    simMs = parsed.samples[0].tMs;
    acc = 0;
    replay = {
      samples: parsed.samples,
      endMs: parsed.samples[parsed.samples.length - 1].tMs,
      name: s.name + (useSaved ? '（保存時の設定）' : '（今の設定）'),
    };
    pos = { x: parsed.samples[0].x, z: parsed.samples[0].z };
    fedPos = { x: pos.x, z: pos.z };
    if (deps.setDot) deps.setDot(pos.x, pos.z);
    play();
  };

  // ---- 外部 API ---------------------------------------------------------------
  /** show.json が更新されたら呼ぶ。 */
  function onState(s) {
    state = s;
    rebuild(false);
    renderStatus();
  }

  /** フロアマップのシミュレーションドットから呼ばれる（有効/無効・位置）。 */
  function onSimDot(e) {
    if (typeof e.enabled === 'boolean') {
      enabled = e.enabled;
      if (!enabled) pause();
    }
    if (Number.isFinite(e.x) && Number.isFinite(e.z) && !replay) {
      const moved = !pos || Math.abs(pos.x - e.x) > 1e-6 || Math.abs(pos.z - e.z) > 1e-6;
      pos = { x: e.x, z: e.z };
      // **歩かせたら勝手に走り出す**。ドラッグそのものが「体験者が歩いた」という入力なので、
      // その後に ▶ 再生 を押させるのは隠れた前提でしかない（押し忘れると「動かない」に見える）。
      if (moved && enabled && !playing) play();
    }
    renderAll();
  }

  // 素材の実尺が測れたら尺を解き直す（実行中なら「設定が変わった」扱い＝⏹ で反映）。
  onDurationResolved(() => rebuild(false));

  renderSpeeds();
  renderRecInfo();
  renderAll();
  refreshScenarios();

  return { onState, onSimDot, isRunning: () => playing };
}
