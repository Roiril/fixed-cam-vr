import {FX_DEFAULT} from './common.js';

// 素材一覧の選択行を、現在の著作で再生する 1 カットへ解決する。
// マスク行でも同じカットの映像とマスクを組にする。
export function resolveLibraryPreview(show, effect, item) {
  const match = /^step:(\d+):(assetUrl|sourceUrl|maskUrl)$/.exec(item?.key || '');
  if (!show || !effect || !match) return null;
  const index = Number(match[1]);
  const segment = (show.timeline?.segments || []).find(s =>
    (s.takes || []).some(t => t.id === effect.id));
  const take = (segment?.takes || []).find(t => t.id === effect.id);
  const step = take?.steps?.[index];
  if (!step) return null;
  const cue = (show.cues || []).find(c => c.id === step.cueId) || {};
  const assets = effect.assets || [];
  const prefix = `step:${index}:`;
  const source = assets.find(a => a.key === prefix + 'assetUrl' && a.current?.exists)
    || assets.find(a => a.key === prefix + 'sourceUrl' && a.current?.exists);
  const mask = assets.find(a => a.key === prefix + 'maskUrl');
  const cameras = show.cameras || [];
  const camera = (Number.isInteger(step.camera) && step.camera >= 0 ? cameras[step.camera] : null)
    || cameras.find(c => c.id === cue.camera)
    || (Number.isInteger(segment.camera) ? cameras[segment.camera] : null);
  const live = step.source === 'inherit' || step.source === 'live';
  const post = step.hasPost && step.post ? step.post
    : segment.hasPost && segment.post ? segment.post
      : camera?.post ? camera.post
        : show.post;
  return {
    live, camera,
    source: source?.current || null,
    kind: source?.kind || '',
    mask: mask?.current || null,
    maskRequired: !!(step.maskUrl || cue.maskUrl),
    strength: step.strength >= 0 ? step.strength : (cue.strength ?? 1),
    fadeSec: step.fadeInSec >= 0 ? step.fadeInSec : (cue.fadeIn ?? 0.3),
    trimStart: step.trimStartSec >= 0 ? step.trimStartSec : (cue.trimStart ?? 0),
    trimEnd: step.trimEndSec >= 0 ? step.trimEndSec : (cue.trimEnd ?? 0),
    post: {...FX_DEFAULT, ...(post || {})},
  };
}
