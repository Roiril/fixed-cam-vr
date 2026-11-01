1. P3の過剰伸びは手動の仮深度へ推定Tzを作用させたことによる。Tz/Tx/Ty/Rzの代理推定とqflat/qworldの混合を全廃する。
2. pinned worldFを固定cameraで再投影したclip座標へ、計測済みの同じ2D相似Sを重ねる。Sを除いた残差は元localFと同じ程度に戻る。
3. 全4条件は同じ1.35viewport cropと固定displayを使う。元カメラ・形状・UV・field振幅・12px拘束は変えず、穴埋めも行わない。
