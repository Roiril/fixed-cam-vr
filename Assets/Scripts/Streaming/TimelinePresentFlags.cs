#nullable enable
namespace FixedCamVr.Streaming
{
    /// <summary>
    /// timeline の present-flag を「宣言flag ∧ 対象オブジェクト存在」で確定する純ロジック。
    /// 旧実装は object!=null の純代入で、Web が flag=false でも既定オブジェクトを常に送るため
    /// 全 true に化けた（B1）。宣言 bool を gate に残すことで再発しない。live/焼き込み/キャッシュ一律に呼ぶ。
    /// object 存在は「宣言 true なのに object 欠落」時の null-deref 防止の付帯条件にすぎない。
    /// </summary>
    public static class TimelinePresentFlags
    {
        public static void Reconcile(ShowTimelineDef? t)
        {
            if (t?.segments == null) return;
            foreach (var seg in t.segments)
            {
                if (seg == null) continue;
                seg.hasPost = seg.hasPost && seg.post != null;
                seg.hasInsert = seg.hasInsert && seg.insert != null;
                // BGM は「幻のオブジェクト = action:continue」で無害だが、契約を揃えて宣言 bool を正にする。
                seg.hasBgm = seg.hasBgm && seg.bgm != null;
                if (seg.insert != null) seg.insert.hasPost = seg.insert.hasPost && seg.insert.post != null;
                if (seg.cues != null)
                    foreach (var c in seg.cues)
                        if (c != null) c.hasOverride = c.hasOverride && c.@override != null;
                // v3 のカット（step）の post も同じ規約で確定させる。使用時（TakeRunner）にも AND を掛けて
                // いるが、present-flag の確定点は Reconcile 1 箇所という不変条件をここで保つ。
                if (seg.takes != null)
                    foreach (var take in seg.takes)
                    {
                        if (take == null) continue;
                        // 演出の BGM も同じ規約（宣言 bool ∧ 入れ子存在）。幻の bgm で音が飛ばない。
                        take.hasBgm = take.hasBgm && take.bgm != null;
                        if (take.steps == null) continue;
                        foreach (var step in take.steps)
                        {
                            if (step == null) continue;
                            step.hasPost = step.hasPost && step.post != null;
                            // CG 人形の立ち位置も同じ規約。幽霊の placement で人形が原点に立たない。
                            step.hasPlacement = step.hasPlacement && step.placement != null;
                        }
                    }
            }
        }
    }
}
