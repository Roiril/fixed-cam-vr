# 追いつきから乗っ取りまで：動画生成用サウンドプロンプト

2026-09-21。入力処理と演出の変更を終えた後に作成。累積表示と人形のみ1秒残留を検証したQuest 3の録画「20260921_141433」に合わせて更新。音源の生成と実装はまだ行っていない。

## 作り方

以下を1本ずつ生成する。各9秒。動画から音だけ取り出して使う。3本を一つの完成音に混ぜず、主画面・エージェントスクリーン・周囲の空間で発音位置と音量を分ける。生成時には左右移動や立体音響を焼き込まない。

0秒は人形視点が自然に終わる瞬間。警告は0・1・2・3・4秒で追加され、その場に残る。侵食は4.60〜6.20秒。完成文と警告を6.20〜7.55秒保持する。7.55秒で警告と空間エラーを消す。人形の面だけを8.55秒まで静止して残す。この1秒に新たな音を加えない。既存の心音や環境音を一緒に止める指定ではない。

生成モデルには秒単位の構成を伝える。0.01秒単位の位置は生成後の編集目標とする。合わない場合は短い音を切り出して配置する。声や台詞は効果音に混ぜない。既存のスイの音声がある場合も別の音源として扱う。

## 1. 主画面：装置が遮断を試みて失敗する

乾いた接点音と短い電気的なざらつき。1秒ごとの文字の追加を支える。4秒の失敗だけは接点が噛み合わず崩れる。解決したような音は出さない。

```text
Create a 9-second video with synchronized sound, intended as an isolated sound-effects source for a VR installation. Visuals: a locked-off close-up of a plain dark electronic enclosure in a black room, no readable text, no people, no camera movement. The audio is the deliverable.

Sound concept: a monitoring device attempts to isolate an unauthorized connection, but its switching mechanism fails. Intimate, dry electromechanical contact sounds with a short grainy electrical rasp. Restrained urgency, not a cinematic jump scare.

At 0.0 seconds: one firm, compact relay contact with a brief rough electrical edge. At 1.0 and 2.0 seconds: separate short contact events from the same device, subtly less stable each time. At 3.0 seconds: a deliberate closing contact, as if attempting to disconnect. At 4.0 seconds: that contact fails to seat, producing a short broken chatter that collapses within about 0.45 seconds. Do not make the failure louder than a startling impact. After 4.6 seconds, this track is silent. Keep the remaining tail silent through 9.0 seconds.

Single centered source, mono-compatible, close and dry, no baked-in panning or room reverb. Preserve clear midrange detail that remains audible without deep bass. No music, melody, voice, whisper, laughter, alarm siren, pure-tone UI beep, success chime, cinematic boom, jump-scare sting, piercing sustained high frequency, or heavy sub-bass. No background room tone. Leave clean silence between the short events.
```

## 2. エージェントスクリーン：表示が侵食される

薄い面が細かく震える音。単なる通信切れではなく、表示を保とうとする装置が内側から書き換えられる感触にする。2.57〜4.60秒と6.20〜8.55秒は静かにする。読む時間に別の音で注意を引かない。

```text
Create a 9-second video with synchronized sound, intended as an isolated sound-effects source. Visuals: a locked-off close-up of a thin black electronic display against darkness. It starts still, trembles subtly late in the shot, then becomes motionless. No faces, no readable text, no camera movement. The audio is the deliverable.

Sound concept: the display is being overwritten from within while trying to remain functional. Use a small dry vibrating surface, fine brittle electrical crackle, and a strained granular friction texture. It should feel physically close and unnervingly controlled, not like a monster vocalization or a loud explosion.

From 0.0 to 2.0 seconds: silence. At about 2.0 seconds: one very soft, short electrical contact indicating that the display has opened; decay completely before 2.45 seconds. From 2.45 to 4.60 seconds: silence. From 4.60 to 6.20 seconds: a restrained fine tremor and granular electrical strain. Within that interval, make four distinct brief disruptions near 4.62, 5.02, 5.42, and 5.82 seconds. Each disruption lasts roughly 0.12 seconds, with clearly quieter gaps. The disruptions sound like a surface briefly losing alignment, not four gunshots or four musical beats. Settle completely at 6.20 seconds. Hold silence until 8.55 seconds, including the final second when only this display remains. At 8.55 seconds, allow only a tiny dry contact cut, finished by 8.60 seconds. Silent tail to 9.0 seconds.

One centered, mono-compatible source. No stereo sweep, binaural processing, echo, or room reverb. Keep the defining texture in the audible midrange, without relying on sub-bass. No speech, synthesized syllables, breathing, whispers, laughter, music, melody, warning beeps, siren, harsh sustained squeal, horror sting, or large impact. Do not add a triumphant or reassuring ending.
```

## 3. 周囲の空間：侵入した状態が残る

周囲の記号に合わせる薄い干渉音。主画面やスイより目立たせない。2秒以降に下げる。失敗時に膨らませず、侵食中の細かな音を隠さない。

```text
Create a 9-second video with synchronized sound, intended as an isolated ambient sound-effects source for later spatial placement. Visuals: an empty dark room, static camera, faint red light, no objects moving, no text or people. The audio is the deliverable.

Sound concept: a subtle foreign electrical interference has entered the room. A thin, irregular, non-tonal granular rustle with restrained electrical roughness. No identifiable animal, person, footsteps, or moving object. Tension comes from the persistent unfamiliar texture, not from loudness or dramatic hits.

At 0.0 seconds, the interference appears promptly but without an impact. Maintain a quiet unstable texture through 2.0 seconds. Reduce it smoothly between 2.0 and 2.45 seconds to a much fainter level, so the listener can attend to a small display in front of them. Keep it faint and steady from 2.45 until 7.55 seconds. No swell at 4.0 seconds and no new event during 4.60 to 6.20 seconds. Cut the texture off abruptly at 7.55 seconds without a reverb tail. The remainder through 9.0 seconds is silent.

Deliver a single centered mono-compatible texture with no baked-in directional travel, binaural motion, delay, or room reverberation. Spatial placement will be done separately in the VR engine. Audible midrange detail, gently controlled upper frequencies, no dependence on deep sub-bass. No music, drone with a musical pitch, melody, rhythm, voices, whispers, laughter, breath, heartbeat, siren, UI beep, footsteps, cinematic riser, jump scare, or sudden volume spike. Do not add other environmental sounds.
```

## 編集と試聴の判定

主画面の音を先に決める。エージェントスクリーンの侵食音を次に合わせる。周囲の音は最後に小さく足す。3本とも同じ音量にしない。既存のBGMと心音は差し替えない。

スイの文を読む間に音へ目を奪われないこと。4回の乱れが映像より先に聞こえないこと。赤い否定文の静止中に大きな新規音が鳴らないこと。7.55秒で空間の干渉音が止まること。人形だけが残る1秒間は静かなままにすること。人形が消える8.55秒の接点音も8.60秒までに終わること。これを実機で合わせる。

数値は実装時の同期目標。生成だけで時刻が保証されるわけではない。現在の日本語・英語・フランス語は同じ8.55秒。将来文が長くなって保持時間が延びる場合は、静かな保持区間を延ばし、4回の侵食音と終了音を映像に再同期する。
