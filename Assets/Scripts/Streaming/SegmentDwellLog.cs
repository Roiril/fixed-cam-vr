#nullable enable
using System;
using System.Collections.Generic;

namespace FixedCamVr.Streaming
{
    /// <summary>
    /// 区間 (lap, camera) の**実測滞在時間**を測る純ロジック（UnityEngine 非依存・時刻は注入）。
    ///
    /// 目的は「オーサリングした演出が体験者の歩速で発火しないこと」を作者に見せること
    /// （計画 2026-07-25_shot-timeline-foundation.md §4 / §8 論点 5）。
    /// リボン UI は区間ブロックに実測平均を出し、<c>進入 +20s</c> の演出が
    /// 平均滞在を超えていれば警告する。読む先が無かったのでここで作る。
    ///
    /// 計時の入力はショーの時計（<see cref="CueScheduler.CameraEntered"/> = ZoneCommitted 由来）だけ。
    /// 画面の切替（手動 A・Web 固定・インサート）では動かない＝「体験者がその区間に居た時間」になる。
    /// </summary>
    public sealed class SegmentDwellLog
    {
        /// <summary>確定した 1 区間の滞在。heartbeat で卓へ送る単位。</summary>
        public struct Sample
        {
            public int lap;
            public int camera;
            public float sec;
        }

        /// <summary>送信待ちの上限。卓が居ない現場でも無制限に溜めない（古い方から捨てる）。</summary>
        public const int DefaultCapacity = 64;

        /// <summary>これ未満は雑音として捨てる（境界 jitter の取りこぼし対策。dwell 既定 0.5s より十分小さく）。</summary>
        public const float MinSampleSec = 0.05f;

        private readonly int _capacity;
        private readonly List<Sample> _pending = new List<Sample>();
        private bool _has;
        private int _lap;
        private int _camera;
        private float _since;

        public SegmentDwellLog(int capacity = DefaultCapacity)
        {
            _capacity = capacity > 0 ? capacity : DefaultCapacity;
        }

        public int PendingCount => _pending.Count;
        public bool HasCurrent => _has;
        public int CurrentLap => _lap;
        public int CurrentCamera => _camera;

        /// <summary>
        /// 区間へ進入した。別区間へ移ったときだけ直前の滞在を確定する。
        /// 同一キーの再通知（seed と最初の commit が重なる等）は計時を継続する。
        /// </summary>
        public void Enter(int lap, int camera, float now)
        {
            if (_has)
            {
                if (lap == _lap && camera == _camera) return;   // 同じ区間 = 継続
                Complete(now);
            }
            _has = true;
            _lap = lap;
            _camera = camera;
            _since = now;
        }

        /// <summary>ラン開始 / 体験者交代。計時中の部分区間は「滞在」として成立しないので捨てる。</summary>
        public void Reset()
        {
            _has = false;
        }

        /// <summary>送信待ちを取り出して空にする（送れなかったら <see cref="PutBack"/> で戻す）。</summary>
        public Sample[] TakePending()
        {
            if (_pending.Count == 0) return Array.Empty<Sample>();
            var arr = _pending.ToArray();
            _pending.Clear();
            return arr;
        }

        /// <summary>送信失敗分を先頭へ戻す（新しいサンプルより古いので前に積む）。容量超過は古い方から捨てる。</summary>
        public void PutBack(Sample[]? samples)
        {
            if (samples == null || samples.Length == 0) return;
            _pending.InsertRange(0, samples);
            TrimToCapacity();
        }

        private void Complete(float now)
        {
            float sec = now - _since;
            if (!(sec >= MinSampleSec)) return;   // NaN もここで落ちる
            _pending.Add(new Sample { lap = _lap, camera = _camera, sec = sec });
            TrimToCapacity();
        }

        private void TrimToCapacity()
        {
            int over = _pending.Count - _capacity;
            if (over > 0) _pending.RemoveRange(0, over);
        }
    }
}
