// 廻リ視の体験タイムライン（企画書 = docs/proposal/proposal.pdf の構成をそのまま形にしたもの）。
//
//   ここが**体験の設計そのもの**。卓（ブラウザ）でクリックして作るのと同じデータを、
//   読める形で 1 ファイルに書いてある。直すときはここを直して `node authoring/apply.mjs --apply`。
//
//   企画書との対応:
//     導入        … 区間 1 を歩いて固定視点に慣れる（run.introEnabled）。演出も録画も走らない
//     1 周目      … 差し替えなし + 行動を録画。死角の壁に血の手形（視点が切り替わった瞬間に気づく）
//     2 周目      … 人形 → 怪物。歩行（通過ライン）に同期させ、前後に乱れを挿す
//     3 周目      … 1 周目の録画を背景に、体験者に追従する CG 人形を重ねる（反転）
//     全体        … 導入を含め 3 分以内 / 各周およそ 30 秒（= 1 区間およそ 10 秒）
//
//   設計の判断（design-critic の指摘を容れたもの）:
//   - 2 周目の怪物は**山を 1 つだけ**にする。マスクは静的で、台車を確実に置ける区間でしか成立しない
//   - 3 周目の録画カットは**秒指定**にする。「素材の終わりまで」だと尺が滞在に従属して
//     「各周およそ 30 秒」を守れず、遅れが累積して最後の区間の演出が消える
//   - 3 周目の 2・3 区間目は `wait:"chain"`。前の演出が終わるまで待てるようにする（保険）
//   - 締めは `at:"exit"` に置かず、最後の演出の**最後のカット**にする（離脱時は画面が塞がっていると出ない）
//   - 1 周目の手形は**本来は物理**（壁に実物）が正しい。企画書の「差し替え演出は行わず」と一致し、
//     2・3 周目でも壁に残る。ここで組んでいるのは会場を汚せない時の代替（かつ、いま検証できる唯一の形）

import { FX_DEFAULT } from '../common.js';
import { TAKE, newStep, newTake, newSeg, takeId, defaultBgm, serializeTimelineV3 } from '../timeline-model.js';

// ---- 素材 ---------------------------------------------------------------------
//   `gen_*` は **2026-07-29 に現地の生映像を種にして Codex で作った実素材**（卓の 🪄 と同じ作り方）。
//   種フレームは同日 18:51 の captures/camcam[ABC]_*.jpg で、構図・照明はそのまま。
//   マスクは種と生成の差分から起こしてある（make-diff-mask.py）。
const A = { NINGYO: '/captures/gen_monsterA_01.png', CLIP: '/testassets/test_clip_A.mp4' };
const B = { HAND: '/captures/gen_handB_01.png' };
const C = {
  CLOSE: '/captures/gen_closeC_01.png',
  // 締めは**日本人形そのもの**が床に立っている 1 枚（卓の 🪄 ボタン経由で作ったもの）。
  // 直前まで「過去の自分」を見ていた画面に、追ってきた本体が置き去りのように立っている。
  FINALE: '/captures/gen_camC_doll_20260729_191238.png',
};

/**
 * この体験が使う cue（素材 + マスク + フェード）。**この配列が正**で、apply が show.json へ上書きする。
 * cue は「重ねる素材」の定義だけを持ち、**尺は持たない**（尺はカット側が単一の正）。
 */
export const REQUIRED_CUES = [
  {
    id: 'cue_hand_B',
    name: '血の手形（壁・1周目）',
    camera: 'B',
    maskUrl: '/masks/cue_hand_B.png',   // 種との差分（左のパーテーション面だけ）
    sourceUrl: B.HAND,
    strength: 1,
    loop: true,
    // フェードは 0 に近づける。滲み出てくると「差し替わった」と気づかれる。
    // 企画書の手形は「切り替わった瞬間、すでにそこに在った」もの。
    fadeIn: 0,
    fadeOut: 0.35,
  },
  {
    id: 'cue_ningyo_A',
    name: '人形が覗いている（2周目）',
    camera: 'A',
    maskUrl: '/masks/cue_ningyo_A.png', // 種との差分（カーテンの隙間の人影だけ）
    sourceUrl: A.NINGYO,
    strength: 1,
    loop: true,
    fadeIn: 0.35,
    fadeOut: 0.4,
  },
];

/** カメラの概算姿勢（CG 人形を出すのに要る）。**現場で 🎯 較正したら上書きされる前提の当て推量**。 */
export const CAMERA_POSES = {
  // ゾーンの塗り（layout.grid）から「そのゾーンを見る位置」を置いた。高さは三脚を想定して 1.35m、
  // 画角は既に著作されているカメラ D と同じ 77.4°（同型のスマホ前提）。
  A: { x: -1.05, z: 1.05, y: 1.35, yawDeg: 133, pitchDeg: -14, hfovDeg: 77.4 },   // 北西から南東の区間を見る
  B: { x: -0.68, z: -1.05, y: 1.35, yawDeg: 0, pitchDeg: -20, hfovDeg: 77.4 },    // 南から西の回廊を見上げる
  C: { x: 1.05, z: 1.05, y: 1.35, yawDeg: -110, pitchDeg: -20, hfovDeg: 77.4 },   // 北東から北の回廊を見る
};

/**
 * 手形を**壁に実物で置ける**なら true にする（会場を汚せる場合）。
 * そのとき 1 周目は演出ゼロ＝企画書「差し替え演出は行わず」に完全に一致し、
 * しかも手形は 2 周目・3 周目でも壁に残る（映像の演出では作れない持続）。
 */
export const PHYSICAL_HANDPRINT = false;

/** 3 周目だけ、記録映像らしい質感へ寄せる（区間 post。live のカットにも同じだけ掛かる）。 */
const PAST_LOOK = {
  ...FX_DEFAULT,
  exposure: -0.85, contrast: 1.32, saturation: 0.22, temperature: -0.24, tint: 0.18,
  lift: 0.09, vignette: 0.52, grain: 0.1, scanline: 0.28, scanlineCount: 240, aberration: 0.12,
};

// ---- 小さな組み立て子 ----------------------------------------------------------
const seg = (lap, camera, over = {}) => ({ ...newSeg(lap, camera), ...over });

/** 「いまの画面のまま、その上に素材を重ねる」カット。 */
const overlay = (cueId, durSec, over = {}) => newStep({
  source: TAKE.SRC_INHERIT, cueId, durKind: TAKE.DUR_SEC, durSec, ...over,
});
/** 指定カメラの生映像へ戻すカット。 */
const live = (camera, durSec, over = {}) => newStep({
  source: TAKE.SRC_LIVE, camera, durKind: TAKE.DUR_SEC, durSec, ...over,
});
/** 全面を事前映像 / 静止画で覆うカット（マスクを使わない差し替え）。 */
const clip = (assetUrl, durSec, over = {}) => newStep({
  source: TAKE.SRC_CLIP, assetUrl, durKind: TAKE.DUR_SEC, durSec, ...over,
});
const still = (assetUrl, durSec, over = {}) => newStep({
  source: TAKE.SRC_STILL, assetUrl, durKind: TAKE.DUR_SEC, durSec, ...over,
});
/** この体験で録った区間映像を背景にするカット（+ CG 人形）。 */
const rec = (camera, recLap, durSec, over = {}) => newStep({
  source: TAKE.SRC_REC, camera, recLap, durKind: TAKE.DUR_SEC, durSec, ...over,
});

const glitchCut = (ms = 240) => ({ transition: TAKE.TRANS_GLITCH, transitionMs: ms });

/**
 * 体験の本体。返り値は show.json の `timeline`（serialize 済み）。
 *   cameraIndex … カメラ id（'A'/'B'/'C'）→ index。塗りとコース順は現場設定を尊重する。
 */
export function buildTimeline(cameraIndex, prevRev = 0) {
  const A_i = cameraIndex('A');
  const B_i = cameraIndex('B');
  const C_i = cameraIndex('C');
  const segments = [];

  // ===== 1 周目 =============================================================
  //  差し替えはしない。録画だけ回り、死角の壁に手形が「もう在る」。
  //  発火は進入 +0 秒 = カメラが切り替わる暗転に相乗りする（企画書 2.3 の
  //  「差し替えを映像切替のタイミングに同期させ、知覚的検出を抑える」）。
  if (!PHYSICAL_HANDPRINT) segments.push(seg(1, B_i, {
    takes: [newTake(takeId(1, B_i, 0), {
      name: '壁の血の手形',
      at: TAKE.AT_ENTER,
      offsetSec: 0,
      ifMissed: TAKE.MISSED_FIRE_ON_EXIT,
      steps: [
        // 瞬時に差し替える（fadeIn 0）。「切り替わったら、もうそうなっていた」を作るため。
        overlay('cue_hand_B', 5, { transition: TAKE.TRANS_CUT, fadeInSec: 0 }),
        // 手形は瞬時に消える。見間違いだったかもしれない、という引っかかりを残す。
        live(B_i, 0.4, { transition: TAKE.TRANS_CUT }),
      ],
    })],
  }));

  // ===== 2 周目 =============================================================
  //  山は 1 つ。人形が怪物へ変わり、画面いっぱいに迫って襲う。
  //  発火は通過ライン（= 歩行に同期）。前後に乱れを挿して継ぎ目を隠す。
  //  演出のあいだだけ音を止める（終われば自動で戻る）。
  segments.push(seg(2, A_i, {
    takes: [newTake(takeId(2, A_i, 0), {
      name: '人形が怪物に変わる',
      at: TAKE.AT_LINE,
      lineId: 'line_2',
      ifMissed: TAKE.MISSED_FIRE_ON_EXIT,
      hasBgm: true,
      bgm: { ...defaultBgm(), action: 'stop', fadeOutSec: 0.3 },
      steps: [
        // ① 乱れの中で、カーテンの隙間に人影だけを差し替えで足す（マスク）。まだ遠い。
        overlay('cue_ningyo_A', 2.6, glitchCut(260)),
        // ② 乱れて実写へ戻る。居たのかどうか確かめられないまま歩き続けることになる。
        live(A_i, 0.5, glitchCut(300)),
      ],
    })],
  }));

  //  襲いかかる。画面を埋める全面の差し替え（マスクを使わない）。
  //  ここだけ音を止めない — 直前まで鳴っていた曲が続いたまま顔が来る方が逃げ場が無い。
  segments.push(seg(2, C_i, {
    takes: [newTake(takeId(2, C_i, 0), {
      name: '襲いかかる',
      at: TAKE.AT_ENTER,
      offsetSec: 3.5,
      ifMissed: TAKE.MISSED_SKIP,      // 速く歩いた人には出さない（無理に出すと間が悪い）
      steps: [
        // ① 乱れの中で画面いっぱいに迫る（生成した実写調の 1 枚）。
        still(C.CLOSE, 1.6, { ...glitchCut(200), glitch: 0.95, glitchSec: 0.4 }),
        // ② 乱れて実写へ戻る。
        live(C_i, 0.5, glitchCut(300)),
      ],
    })],
  }));

  // ===== 3 周目 =============================================================
  //  背景は 1 周目の録画。その上に体験者へ追従する CG 人形が立つ。
  //  画面の中を歩いている自分は過去なので、いまの動きとは合わない。
  //  音は 3 周目の頭で落とす（以降の区間は指示が無いので carry-forward で無音のまま）。
  const pastTake = (lap, cam, index, extraSteps = [], over = {}) => newTake(takeId(lap, cam, index), {
    name: '過去の自分',
    at: TAKE.AT_ENTER,
    offsetSec: 0.5,
    ifMissed: TAKE.MISSED_FIRE_ON_EXIT,
    steps: [
      // 乱れの中で「いま」から「1 周目の録画」へ入れ替える。人形は体験者の頭に追従する（分身）。
      rec(cam, 1, 6, { ...glitchCut(320), cg: 'doll', cgMode: TAKE.CG_FOLLOW }),
      ...extraSteps,
      live(cam, 0.5, glitchCut(260)),
    ],
    ...over,
  });

  segments.push(seg(3, A_i, {
    hasPost: true, post: { ...PAST_LOOK },
    hasBgm: true, bgm: { ...defaultBgm(), action: 'stop', fadeOutSec: 2 },
    takes: [pastTake(3, A_i, 0)],
  }));
  segments.push(seg(3, B_i, {
    hasPost: true, post: { ...PAST_LOOK },
    // 前の区間の演出が長引いていたら、区間を出ても終わるまで待つ（捨てずに出す）。
    takes: [pastTake(3, B_i, 0, [], { wait: TAKE.WAIT_CHAIN })],
  }));
  segments.push(seg(3, C_i, {
    hasPost: true, post: { ...PAST_LOOK },
    takes: [pastTake(3, C_i, 0, [
      // 締め。人形がこちらを見て終わる。**離脱時に置かない**（画面が塞がっていると出ないため）、
      // 最後の演出の最後から 2 番目のカットとして必ず順番に出す。
      still(C.FINALE, 2.4, { ...glitchCut(300), glitch: 0.7, glitchSec: 0.5 }),
    ], { wait: TAKE.WAIT_CHAIN })],
  }));

  return serializeTimelineV3({ rev: (prevRev || 0) + 1, segments });
}

/**
 * ゾーンが切り替わる継ぎ目に薄く乱れを重ねる（企画書 2.3「乱れを一時的に重畳でき、
 * 差し替えの継ぎ目の隠蔽や、体験者の注意・移動の誘導に用いる」）。
 * ここが 0 でないと、2 周目の差し替えだけが乱れる＝乱れ自体が合図になってしまう。
 */
export const SWITCH_GLITCH = 0.25;

/** 体験の骨格（導入 → 3 周 → 終了）。企画書「導入を含め 3 分以内 / 各周およそ 30 秒」。 */
export const RUN = {
  totalLaps: 3,
  introEnabled: true,
  introMinSec: 20,
  introAutoAdvance: true,
  targetSec: 180,
  hardLimitSec: 300,
  endFadeSec: 1.5,
};

/**
 * 端末内録画。3 周目が使うのは**1 周目だけ**。
 * 2 周目まで録るとラン全体の容量（maxTotalMB）を食い合い、1 周目の録画が先頭で打ち切られうる。
 */
export const RECORD = {
  enabled: true, laps: [1], maxSegmentSec: 60, maxTotalMB: 200, fpsCap: 15,
};

export { A, B, C };
