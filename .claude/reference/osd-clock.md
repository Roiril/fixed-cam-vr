# OSD の時計で破ると壊れること

`rules/screen-post.md` から移した。

- OSD の v を反転しない／矩形の内外を分岐にしない／版は 1 セルずつ描く 〔出典: S:1366-1386〕
- OSD のセル並びは `make-osd-font.py` と対 — `OsdClockLogicTests.Glyphs_MatchTheBakingScript` 〔出典: S:1388-1391〕
- OSD はシーンに焼かないと出ない（`menu scene`）。観測 `osd=-`
