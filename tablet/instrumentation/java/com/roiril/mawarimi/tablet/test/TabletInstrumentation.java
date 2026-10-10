package com.roiril.mawarimi.tablet.test;

import android.app.Activity;
import android.app.Instrumentation;
import android.content.ComponentName;
import android.graphics.Bitmap;
import android.os.Bundle;
import android.os.ParcelFileDescriptor;
import android.os.SystemClock;
import android.util.Log;
import android.view.View;
import android.view.ViewGroup;
import android.webkit.ValueCallback;
import android.webkit.WebView;

import org.json.JSONArray;
import org.json.JSONObject;
import org.json.JSONTokener;

import java.io.ByteArrayOutputStream;
import java.io.IOException;
import java.io.InputStream;
import java.security.MessageDigest;
import java.util.HashSet;
import java.util.Set;
import java.util.concurrent.CountDownLatch;
import java.util.concurrent.TimeUnit;
import java.util.concurrent.atomic.AtomicReference;
import java.util.regex.Matcher;
import java.util.regex.Pattern;

public final class TabletInstrumentation extends Instrumentation {
    private static final String TAG = "DoctorTabletTest";
    private static final ComponentName ACTIVITY = new ComponentName(
        "com.roiril.mawarimi.tablet", "com.roiril.mawarimi.tablet.MainActivity");
    private static final Pattern FIRST_NUMBER = Pattern.compile("\\d+");
    private final Bundle results = new Bundle();
    private boolean requireQuest;
    private boolean mutateQuest;
    private int checks;
    private boolean playback;
    private boolean briefing;
    private boolean equipment;
    private boolean staffFlow;

    @Override
    public void onCreate(Bundle arguments) {
        super.onCreate(arguments);
        requireQuest = arguments != null && "true".equalsIgnoreCase(arguments.getString("requireQuest"));
        mutateQuest = arguments != null && "true".equalsIgnoreCase(arguments.getString("mutateQuest"));
        playback = arguments != null && "true".equalsIgnoreCase(arguments.getString("playback"));
        briefing = arguments != null && "true".equalsIgnoreCase(arguments.getString("briefing"));
        equipment = arguments != null && "true".equalsIgnoreCase(arguments.getString("equipment"));
        staffFlow = arguments != null && "true".equalsIgnoreCase(arguments.getString("staffFlow"));
        start();
    }

    @Override
    public void onStart() {
        int resultCode = Activity.RESULT_OK;
        try {
            if (staffFlow) runStaffFlowTests(); else if (equipment) runEquipmentTests(); else if (briefing) runBriefingTests(); else if (playback) runPlaybackTests(); else runFunctionalTests();
            results.putString("summary", "PASS checks=" + checks);
            Log.i(TAG, "PASS checks=" + checks);
        } catch (Throwable error) {
            resultCode = Activity.RESULT_CANCELED;
            results.putString("summary", "FAIL " + error);
            Log.e(TAG, "FAIL", error);
        } finally {
            finish(resultCode, results);
        }
    }

    private void runStaffFlowTests() throws Exception {
        Activity activity = launchFromShell();
        WebView view = waitForWebView(activity, 10000);
        String originalQuest = "";
        waitFor("page ready", 15000, () -> "complete".equals(evaluateString(view, "document.readyState")));
        try {
            originalQuest = evaluateString(view, "TabletTransport.getQuest()");
            check("original Quest selection is restorable",
                "alpha".equals(originalQuest) || "beta".equals(originalQuest));
            Log.i(TAG, "staffFlow originalQuest=" + originalQuest);

            waitFor("staff home", 5000, () -> evaluateBoolean(view,
                "document.getElementById('staff').open"));
            evaluate(view, "(function(){var d=document.getElementById('staffTrouble');d.open=true;"
                + "var q=document.getElementById('questBeta');q.checked=true;"
                + "q.dispatchEvent(new Event('change',{bubbles:true}));"
                + "document.getElementById('staffCheckBtn').click();return true;})()");
            waitFor("Quest beta connection", 20000, () -> evaluateBoolean(view,
                "document.getElementById('questBeta').checked"
                    + "&&document.getElementById('staffCheckMessage').textContent.includes('通信先を保存しました')"
                    + "&&document.getElementById('staffTargetName').textContent.includes('クエスト β')"));
            check("Quest beta selected", "beta".equals(evaluateString(view, "TabletTransport.getQuest()")));
            check("visitor entry remains disabled", evaluateBoolean(view,
                "document.getElementById('titleStart').disabled"));
            JSONObject staffState = evaluateObject(view,
                "JSON.stringify({viewport:{width:innerWidth,height:innerHeight},"
                    + "target:document.getElementById('staffTargetName').textContent.trim(),"
                    + "state:document.getElementById('staff').dataset.state,"
                    + "title:document.getElementById('staffNowTitle').textContent.trim(),"
                    + "message:document.getElementById('staffCheckMessage').textContent.trim(),"
                    + "quest:document.getElementById('devQuestValue').textContent.trim(),"
                    + "titleDisabled:document.getElementById('titleStart').disabled})");
            check("staff home has current action", !staffState.optString("title").isEmpty());
            Log.i(TAG, "staffFlow state=" + staffState);
            checkButtonBounds(view, "staffCheckBtn", true);
            verifyStaffAlertSound(view);
            evaluate(view, "document.getElementById('staff').scrollTop=0");
            SystemClock.sleep(300);
            saveScreenshot("doctor-staff-flow-staff.png");

            evaluate(view, "document.getElementById('staffBriefingBtn').click()");
            waitFor("staff briefing preview", 5000, () -> evaluateBoolean(view,
                "!document.getElementById('briefingView').hidden"
                    + "&&!document.getElementById('briefingMode').hidden"));
            int position = readSceneCounter(view);
            // The production UI rejects manual advances less than 150ms apart.
            for (; position < 12; position++) {
                int next = position + 1;
                evaluate(view, "document.getElementById('briefingNext').click()");
                waitFor("staff preview sentence " + next, 5000, () -> readSceneCounter(view) == next);
                SystemClock.sleep(200);
            }
            check("staff preview final action", evaluateBoolean(view,
                "document.getElementById('briefingNext').textContent==='装着の案内へ'"
                    + "&&!document.getElementById('briefingNext').hidden"));
            evaluate(view, "document.getElementById('briefingNext').click()");
            waitFor("staff equipment preview", 5000, () -> evaluateBoolean(view,
                "!document.getElementById('equipmentGuide').hidden"
                    + "&&!document.getElementById('equipmentSteps').hidden"));
            JSONObject equipmentLayout = evaluateObject(view,
                "JSON.stringify((()=>{var d=document.documentElement;var g=document.getElementById('equipmentGuide');"
                    + "var r=g.getBoundingClientRect();return {viewport:{width:innerWidth,height:innerHeight},"
                    + "document:{width:d.scrollWidth,height:d.scrollHeight},guide:{left:r.left,top:r.top,right:r.right,bottom:r.bottom},"
                    + "fits:d.scrollWidth<=innerWidth+1&&d.scrollHeight<=innerHeight+1,text:g.textContent.trim()};})())");
            check("equipment preview fits viewport", equipmentLayout.optBoolean("fits"));
            check("equipment preview has visitor instructions",
                equipmentLayout.optString("text").contains("左手にコントローラー")
                    && equipmentLayout.optString("text").contains("Meta Quest")
                    && equipmentLayout.optString("text").contains("ヘッドフォン"));
            checkButtonBounds(view, "briefingReplay", true);
            checkButtonBounds(view, "briefingSettings", true);
            Log.i(TAG, "staffFlow equipment=" + equipmentLayout);
            saveScreenshot("doctor-staff-flow-equipment.png");

            evaluate(view, "document.getElementById('briefingSettings').click()");
            waitFor("staff home after preview", 5000, () -> evaluateBoolean(view,
                "document.getElementById('staff').open"
                    + "&&document.getElementById('staffNowTitle').textContent.trim().length>0"));
            JSONObject titleLayout = evaluateObject(view,
                "JSON.stringify({viewport:{width:innerWidth,height:innerHeight},"
                    + "width:document.documentElement.scrollWidth,height:document.documentElement.scrollHeight,"
                    + "fits:document.documentElement.scrollWidth<=innerWidth+1"
                    + "&&document.documentElement.scrollHeight<=innerHeight+1,"
                    + "label:document.getElementById('staffNowTitle').textContent.trim(),"
                    + "state:document.getElementById('staff').dataset.state})");
            check("staff home fits viewport", titleLayout.optBoolean("fits"));
            Log.i(TAG, "staffFlow return=" + titleLayout);
            saveScreenshot("doctor-staff-flow-title.png");
        } finally {
            if ("alpha".equals(originalQuest) || "beta".equals(originalQuest)) {
                restoreQuestSelection(view, originalQuest);
            } else {
                try { evaluate(view, "document.getElementById('staff').close()"); }
                catch (Throwable cleanupError) { Log.w(TAG, "staffFlow close failed", cleanupError); }
            }
        }
    }

    private void verifyStaffAlertSound(WebView view) throws Exception {
        evaluate(view, "(function(){var Native=window.AudioContext||window.webkitAudioContext;"
            + "if(!Native)throw new Error('AudioContext is unavailable');"
            + "var proof=window.__staffAlertAudioProof={starts:[],stops:[],frequencies:[],maxEnergy:0,zeroSamples:0,nonzeroSamples:0};"
            + "function Wrapped(){var ctx=new Native();var analyser=ctx.createAnalyser();analyser.fftSize=256;"
            + "analyser.connect(ctx.destination);var createGain=ctx.createGain.bind(ctx);ctx.createGain=function(){"
            + "var gain=createGain();var connect=gain.connect.bind(gain);gain.connect=function(target){"
            + "return connect(target===ctx.destination?analyser:target);};return gain;};"
            + "var createOscillator=ctx.createOscillator.bind(ctx);ctx.createOscillator=function(){var oscillator=createOscillator();"
            + "var start=oscillator.start.bind(oscillator),stop=oscillator.stop.bind(oscillator);"
            + "var setFrequency=oscillator.frequency.setValueAtTime.bind(oscillator.frequency);"
            + "oscillator.frequency.setValueAtTime=function(value,when){proof.frequencies.push(value);return setFrequency(value,when);};"
            + "oscillator.start=function(when){proof.starts.push(when);return start(when);};"
            + "oscillator.stop=function(when){proof.stops.push(when);return stop(when);};return oscillator;};"
            + "var data=new Uint8Array(analyser.fftSize);function sample(){analyser.getByteTimeDomainData(data);"
            + "var energy=0;for(var i=0;i<data.length;i++)energy+=Math.abs(data[i]-128);"
            + "proof.maxEnergy=Math.max(proof.maxEnergy,energy);if(energy===0)proof.zeroSamples++;else proof.nonzeroSamples++;}"
            + "sample();proof.timer=setInterval(sample,10);"
            + "return ctx;}Wrapped.prototype=Native.prototype;window.AudioContext=Wrapped;window.webkitAudioContext=Wrapped;return true;})()");
        check("staff alert has no signal before actual tap", evaluateBoolean(view,
            "window.__staffAlertAudioProof.maxEnergy===0"
                + "&&window.__staffAlertAudioProof.starts.length===0"));
        JSONObject bounds = checkButtonBounds(view, "staffAlertSoundBtn", true);
        double ratio = evaluateDouble(view, "window.devicePixelRatio");
        int x = (int)Math.round((bounds.optDouble("left") + bounds.optDouble("right")) * 0.5 * ratio);
        int y = (int)Math.round((bounds.optDouble("top") + bounds.optDouble("bottom")) * 0.5 * ratio);
        runShell("input tap " + x + " " + y);
        waitFor("staff alert generated waveform", 3000, () -> evaluateBoolean(view,
            "window.__staffAlertAudioProof.starts.length===2"
                + "&&window.__staffAlertAudioProof.stops.length===2"
                + "&&window.__staffAlertAudioProof.zeroSamples>0"
                + "&&window.__staffAlertAudioProof.nonzeroSamples>0"
                + "&&window.__staffAlertAudioProof.maxEnergy>0"));
        JSONObject proof = evaluateObject(view,
            "JSON.stringify((()=>{var p=window.__staffAlertAudioProof;clearInterval(p.timer);return {"
                + "starts:p.starts,stops:p.stops,frequencies:p.frequencies,maxEnergy:p.maxEnergy,"
                + "zeroSamples:p.zeroSamples,nonzeroSamples:p.nonzeroSamples,"
                + "startGapMs:(p.starts[1]-p.starts[0])*1000,scheduledMs:(p.stops[1]-p.starts[0])*1000,"
                + "message:document.getElementById('staffAlertSoundHelp').textContent.trim()};})())");
        Log.i(TAG, "staffAlert generatedSignal=" + proof
            + " audibility=requires-human-listening inputTap=" + x + "," + y);
        JSONArray frequencies = proof.getJSONArray("frequencies");
        check("staff alert has two starts", proof.getJSONArray("starts").length() == 2);
        check("staff alert frequencies are distinct from briefing media",
            Math.abs(frequencies.getDouble(0) - 294) < 1 && Math.abs(frequencies.getDouble(1) - 349) < 1);
        check("staff alert tone gap is scheduled", proof.optDouble("startGapMs") >= 140
            && proof.optDouble("startGapMs") <= 160);
        check("staff alert duration is short", proof.optDouble("scheduledMs") >= 280
            && proof.optDouble("scheduledMs") <= 300);
        check("staff alert analyser has zero control and nonzero signal",
            proof.optInt("zeroSamples") > 0 && proof.optInt("nonzeroSamples") > 0
                && proof.optInt("maxEnergy") > 0);
        check("staff alert reports successful preview",
            proof.optString("message").equals("この音で機器の異常を知らせます。音量はタブレット本体のボタンで調整します。"));
    }

    private void restoreQuestSelection(WebView view, String quest) throws Exception {
        Throwable normalRestoreError = null;
        try {
            evaluate(view, "(function(){var b=document.getElementById('briefingSettings');"
                + "if(!document.getElementById('briefingView').hidden&&!b.hidden)b.click();"
                + "var d=document.getElementById('staff');if(!d.open)document.getElementById('staffToggle').click();"
                + "document.getElementById('staffTrouble').open=true;var q=document.getElementById('quest"
                + ("alpha".equals(quest) ? "Alpha" : "Beta") + "');q.checked=true;"
                + "q.dispatchEvent(new Event('change',{bubbles:true}));"
                + "document.getElementById('staffCheckBtn').click();return true;})()");
            waitFor("restore Quest " + quest, 20000, () -> evaluateBoolean(view,
                "TabletTransport.getQuest()==='" + quest + "'"
                    + "&&document.getElementById('staffCheckMessage').textContent.includes('通信先を保存しました')"));
        } catch (Throwable error) {
            normalRestoreError = error;
            Log.w(TAG, "normal Quest restore failed; using persisted transport fallback", error);
            evaluate(view, "TabletTransport.setQuest('" + quest + "');location.reload()");
            waitFor("reload restored Quest " + quest, 15000, () -> evaluateBoolean(view,
                "document.readyState==='complete'&&TabletTransport.getQuest()==='" + quest + "'"));
        } finally {
            try { evaluate(view, "document.getElementById('staff').close()"); }
            catch (Throwable closeError) { Log.w(TAG, "staffFlow close failed", closeError); }
        }
        check("Quest selection restored", quest.equals(evaluateString(view, "TabletTransport.getQuest()")));
        Log.i(TAG, "staffFlow restoredQuest=" + quest + " fallback=" + (normalRestoreError != null));
    }

    private JSONObject checkButtonBounds(WebView view, String id, boolean require44Px) throws Exception {
        evaluate(view, "document.getElementById('" + id + "').scrollIntoView({block:'center',inline:'center'})");
        SystemClock.sleep(200);
        JSONObject bounds = evaluateObject(view,
            "JSON.stringify((()=>{var e=document.getElementById('" + id + "');var r=e.getBoundingClientRect();"
                + "var s=getComputedStyle(e);return {id:e.id,left:r.left,top:r.top,right:r.right,bottom:r.bottom,"
                + "width:r.width,height:r.height,viewportWidth:innerWidth,viewportHeight:innerHeight,"
                + "visible:s.display!=='none'&&s.visibility==='visible'&&r.width>0&&r.height>0};})())");
        boolean onScreen = bounds.optBoolean("visible")
            && bounds.optDouble("left") >= -1 && bounds.optDouble("top") >= -1
            && bounds.optDouble("right") <= bounds.optDouble("viewportWidth") + 1
            && bounds.optDouble("bottom") <= bounds.optDouble("viewportHeight") + 1;
        check(id + " is accessible in viewport", onScreen);
        if (require44Px) check(id + " is at least 44px high", bounds.optDouble("height") >= 44);
        Log.i(TAG, "buttonBounds " + bounds);
        return bounds;
    }

    private void saveScreenshot(String filename) throws Exception {
        if (!"doctor-staff-flow-staff.png".equals(filename)
            && !"doctor-staff-flow-equipment.png".equals(filename)
            && !"doctor-staff-flow-title.png".equals(filename)) {
            throw new AssertionError("Unexpected staffFlow screenshot name: " + filename);
        }
        Bitmap screenshot = getUiAutomation().takeScreenshot();
        check("screenshot captured " + filename, screenshot != null);
        screenshot.recycle();
        String destination = "/sdcard/" + filename;
        runShell("screencap -p " + destination);
        String byteOutput = runShell("wc -c " + destination);
        long bytes = firstNumber(byteOutput);
        check("screenshot saved " + destination, bytes > 0);
        Log.i(TAG, "screenshot " + destination + " bytes=" + bytes);
    }

    private String runShell(String command) throws Exception {
        try (ParcelFileDescriptor descriptor = getUiAutomation().executeShellCommand(command);
             InputStream input = new ParcelFileDescriptor.AutoCloseInputStream(descriptor);
             ByteArrayOutputStream output = new ByteArrayOutputStream()) {
            byte[] buffer = new byte[4096];
            for (int count; (count = input.read(buffer)) >= 0;) output.write(buffer, 0, count);
            return output.toString("UTF-8");
        }
    }

    private int readSceneCounter(WebView view) throws Exception {
        return Math.toIntExact(firstNumber(evaluateString(view,
            "document.getElementById('sceneCounter').textContent")));
    }

    private long firstNumber(String value) {
        Matcher matcher = FIRST_NUMBER.matcher(value == null ? "" : value);
        if (!matcher.find()) throw new AssertionError("Expected a number in: " + value);
        return Long.parseLong(matcher.group());
    }

    private void runPlaybackTests() throws Exception {
        Activity activity = launchFromShell();
        WebView view = waitForWebView(activity, 10000);
        waitFor("page ready", 15000, () -> "complete".equals(evaluateString(view, "document.readyState")));
        String[] files = {"introduction-ja-v1.mp4", "wear-ja-v1.mp4", "subject-ja-v1.mp4", "report-ja-v1.mp4"};
        for (String file : files) {
            for (int start : new int[]{0, 1048576, 1199370}) {
                byte[] expected = new byte[100000];
                int count = 0;
                try (InputStream input = getTargetContext().getAssets().open("web/asset/" + file)) {
                    long left = start;
                    while (left > 0) {
                        int n = input.read(expected, 0, (int)Math.min(left, expected.length));
                        if (n < 0) break;
                        left -= n;
                    }
                    while (count < expected.length) {
                        int n = input.read(expected, count, expected.length - count);
                        if (n < 0) break;
                        count += n;
                    }
                }
                if (count == 0) continue;
                MessageDigest digest = MessageDigest.getInstance("SHA-256");
                digest.update(expected, 0, count);
                StringBuilder golden = new StringBuilder();
                for (byte b : digest.digest()) golden.append(String.format("%02x", b & 255));
                evaluate(view, "window.__rangeDone=false;fetch('./asset/" + file
                    + "',{headers:{Range:'bytes=" + start + "-" + (start + count - 1)
                    + "'}}).then(r=>r.arrayBuffer()).then(async b=>{window.__rangeLength=b.byteLength;"
                    + "window.__rangeHash=Array.from(new Uint8Array(await crypto.subtle.digest('SHA-256',b)))"
                    + ".map(x=>x.toString(16).padStart(2,'0')).join('');window.__rangeDone=true;});");
                waitFor("range fetch", 15000, () -> evaluateBoolean(view, "window.__rangeDone"));
                String actual = evaluateString(view, "window.__rangeHash");
                Log.i(TAG, "range " + file + " start=" + start + " bytes="
                    + evaluateDouble(view, "window.__rangeLength") + " expected=" + golden + " actual=" + actual);
                check("range bytes " + file + ":" + start, golden.toString().equals(actual));
            }
            evaluate(view, "(function(){var v=document.createElement('video');v.muted=false;v.playsInline=true;"
                + "v.style.cssText='position:fixed;inset:0;width:100%;height:85%;z-index:999999';document.body.appendChild(v);"
                + "window.__playVideo=v;window.__playDone=false;window.__playError='';v.onended=()=>{window.__playDone=true;};"
                + "v.onerror=()=>{window.__playError=String(v.error&&v.error.code)+':'+(v.error&&v.error.message);window.__playDone=true;};"
                + "v.src='./asset/" + file + "';v.play().catch(e=>{window.__playError=String(e);window.__playDone=true;});})()");
            waitFor("unmuted full video", 40000, () -> evaluateBoolean(view, "window.__playDone"));
            String error = evaluateString(view, "window.__playError");
            Log.i(TAG, "fullPlayback " + file + " error=" + error + " clock="
                + evaluateDouble(view, "window.__playVideo.currentTime") + " duration="
                + evaluateDouble(view, "window.__playVideo.duration"));
            check("unmuted full playback " + file, error.isEmpty());
            evaluate(view, "window.__playVideo.removeAttribute('src');window.__playVideo.load();window.__playVideo.remove();");
        }
    }

    private void runFunctionalTests() throws Exception {
        Activity activity = launchFromShell();
        final WebView initialWebView = waitForWebView(activity, 10000);
        waitFor("page ready", 15000, () -> "complete".equals(evaluateString(initialWebView, "document.readyState")));
        check("secure local origin", evaluateString(initialWebView, "location.origin").equals("https://appassets.androidplatform.net"));

        evaluate(initialWebView, INSTALL_TEST_HARNESS);
        waitFor("local asset probe", 20000, () -> evaluateBoolean(initialWebView,
            "Boolean(window.__doctorTest&&window.__doctorTest.assetsDone)"));
        JSONObject assets = evaluateObject(initialWebView, "JSON.stringify(window.__doctorTest.assets)");
        check("briefing JSON loaded", assets.optBoolean("json"));
        check("three local images decoded", assets.optInt("images") == 3);
        Log.i(TAG, "assets " + assets);

        evaluate(initialWebView, "window.__doctorTest.runMedia()");
        waitFor("five video probes", 120000, () -> evaluateBoolean(initialWebView,
            "Boolean(window.__doctorTest&&window.__doctorTest.mediaDone)"));
        JSONArray media = evaluateArray(initialWebView, "JSON.stringify(window.__doctorTest.media)");
        check("video probe count", media.length() == 5);
        for (int index = 0; index < media.length(); index++) {
            JSONObject item = media.getJSONObject(index);
            check("video loaded " + item.optString("name"), item.optBoolean("loaded"));
            check("video seeked " + item.optString("name"), item.optDouble("seeked", -1) > 0);
            check("video frame advanced " + item.optString("name"), item.optBoolean("frame"));
            Log.i(TAG, "media " + item);
        }

        evaluate(initialWebView, "window.__doctorTest.startBackgroundVideo()");
        waitFor("background video playing", 20000, () -> evaluateBoolean(initialWebView,
            "Boolean(window.__doctorTest&&window.__doctorTest.backgroundReady)"));
        double before = evaluateDouble(initialWebView, "window.__doctorTest.backgroundVideo.currentTime");
        long backgroundStartedAt = SystemClock.elapsedRealtime();
        pressHome();
        SystemClock.sleep(2200);
        resumeExisting(activity, initialWebView);
        final WebView resumedWebView = initialWebView;
        SystemClock.sleep(800);
        double afterResume = evaluateDouble(resumedWebView, "window.__doctorTest.backgroundVideo.currentTime");
        long backgroundElapsedMs = SystemClock.elapsedRealtime() - backgroundStartedAt;
        boolean paused = evaluateBoolean(resumedWebView, "window.__doctorTest.backgroundVideo.paused");
        SystemClock.sleep(900);
        double afterWait = evaluateDouble(resumedWebView, "window.__doctorTest.backgroundVideo.currentTime");
        check("background media paused", paused);
        check("background media did not advance", Math.abs(afterResume - before) < 0.8);
        check("background media remains stopped", Math.abs(afterWait - afterResume) < 0.12);
        Log.i(TAG, "background before=" + before + " resumed=" + afterResume + " afterWait=" + afterWait
            + " paused=" + paused + " backgroundElapsedMs=" + backgroundElapsedMs
            + " maxBackgroundAdvanceSec=0.8 (less than the 2.2s HOME wait; allows HOME dispatch latency)");
        evaluate(resumedWebView, "(function(){var v=window.__doctorTest.backgroundVideo;"
            + "if(v){v.pause();v.removeAttribute('src');v.load();v.remove();}return true;})()");

        evaluate(resumedWebView, "window.__doctorTest.runBridge()");
        waitFor("bridge probe", 25000, () -> evaluateBoolean(resumedWebView,
            "Boolean(window.__doctorTest&&window.__doctorTest.bridgeDone)"));
        JSONObject bridge = evaluateObject(resumedWebView, "JSON.stringify(window.__doctorTest.bridge)");
        boolean online = bridge.optBoolean("get") && bridge.optBoolean("post");
        if (requireQuest) check("Quest GET and POST", online);
        check("offline preview remains ready", "complete".equals(evaluateString(resumedWebView, "document.readyState")));
        Log.i(TAG, "bridge requireQuest=" + requireQuest + " result=" + bridge);

        if (mutateQuest) {
            check("mutation requires Quest", requireQuest);
            evaluate(resumedWebView, "window.__doctorTest.runMutation()");
            waitFor("safe set and restore", 30000, () -> evaluateBoolean(resumedWebView,
                "Boolean(window.__doctorTest&&window.__doctorTest.mutationDone)"));
            JSONObject mutation = evaluateObject(resumedWebView, "JSON.stringify(window.__doctorTest.mutation)");
            check("Quest setting restored or safely skipped",
                mutation.optBoolean("restored") || mutation.has("skipped"));
            Log.i(TAG, "mutation " + mutation);
        }

        evaluate(resumedWebView, "(function(){document.getElementById('staffToggle').click();return true;})()");
        waitFor("staff briefing ready", 10000, () -> evaluateBoolean(resumedWebView,
            "!document.getElementById('staffBriefingBtn').disabled"));
        String staffButton = evaluateString(resumedWebView,
            "document.getElementById('staffBriefingBtn').textContent.trim()");
        evaluate(resumedWebView, "(function(){document.getElementById('staffBriefingBtn').click();return true;})()");
        waitFor("offline briefing preview", 5000, () -> evaluateBoolean(resumedWebView,
            "(function(){var v=document.getElementById('briefingView');"
                + "var m=document.getElementById('briefingMode');var r=v.getBoundingClientRect();"
                + "var s=getComputedStyle(v);return !v.hidden&&v.getAttribute('aria-hidden')!=='true'"
                + "&&r.width>0&&r.height>0&&s.display!=='none'&&s.visibility==='visible'"
                + "&&!m.hidden&&m.textContent.trim()==='スタッフ確認モードです。クエストへ設定は送りません。';})()"));
        String briefingTitle = evaluateString(resumedWebView,
            "document.getElementById('briefingTitle').textContent.trim()");
        String briefingMode = evaluateString(resumedWebView,
            "document.getElementById('briefingMode').textContent.trim()");
        check("staff preview control text", staffButton.contains("説明"));
        check("staff preview visible offline", !briefingTitle.isEmpty());
        check("staff preview mode text", briefingMode.equals(
            "スタッフ確認モードです。クエストへ設定は送りません。"));
        Log.i(TAG, "offlinePreview button=" + staffButton + " title=" + briefingTitle
            + " mode=" + briefingMode);
        evaluate(resumedWebView, "(function(){document.getElementById('briefingSettings').click();"
            + "delete window.__doctorTest;return true;})()");
    }

    private Activity launchFromShell() throws Exception {
        ActivityMonitor monitor = addMonitor(ACTIVITY.getClassName(), null, false);
        try (ParcelFileDescriptor command = getUiAutomation().executeShellCommand(
            "am start -n com.roiril.mawarimi.tablet/.MainActivity")) {
            // HyperOS can block startActivitySync from the background test process.
        }
        Activity activity = monitor.waitForActivityWithTimeout(15000);
        removeMonitor(monitor);
        if (activity == null) throw new AssertionError("Shell Activity launch timed out");
        return activity;
    }

    private void runBriefingTests() throws Exception {
        Activity activity = launchFromShell();
        WebView view = waitForWebView(activity, 10000);
        waitFor("page ready", 15000, () -> "complete".equals(evaluateString(view, "document.readyState")));
        evaluate(view, "document.getElementById('staffToggle').click()");
        waitFor("briefing ready", 10000, () -> evaluateBoolean(view,
            "!document.getElementById('staffBriefingBtn').disabled"));
        evaluate(view, "document.getElementById('staffBriefingBtn').click()");
        waitFor("briefing visible", 5000, () -> evaluateBoolean(view,
            "!document.getElementById('briefingView').hidden"));
        for (int round = 1; round <= 2; round++) {
            if (round > 1) {
                evaluate(view, "document.getElementById('briefingReplay').click()");
                SystemClock.sleep(1200);
                double before = evaluateDouble(view, "document.getElementById('doctorVideo').currentTime");
                pressHome();
                SystemClock.sleep(1800);
                try (ParcelFileDescriptor command = getUiAutomation().executeShellCommand(
                    "am start --activity-reorder-to-front --activity-single-top -n com.roiril.mawarimi.tablet/.MainActivity")) {}
                waitFor("resume focus", 5000, () -> evaluateBoolean(view, "!document.hidden"));
                double resumed = evaluateDouble(view, "document.getElementById('doctorVideo').currentTime");
                check("briefing stopped in background", Math.abs(resumed - before) < 0.8);
                evaluate(view, "document.getElementById('mediaRetry').click()");
            }
            evaluate(view, "document.getElementById('briefingToggle').click()");
            Set<String> rendered = new HashSet<>();
            long deadline = SystemClock.elapsedRealtime() + 95000;
            boolean ended = false;
            while (SystemClock.elapsedRealtime() < deadline) {
                JSONObject sample = evaluateObject(view,
                    "JSON.stringify((()=>{var v=document.getElementById('doctorVideo');var r=v.getBoundingClientRect();"
                    + "return {cue:document.getElementById('sceneCounter').textContent,clock:v.currentTime,"
                    + "frames:v.getVideoPlaybackQuality().totalVideoFrames,audio:v.webkitAudioDecodedByteCount||0,"
                    + "visible:!v.hidden&&r.width>0&&r.height>0,paused:v.paused,muted:v.muted,"
                    + "failed:!document.getElementById('mediaNotice').hidden&&document.getElementById('mediaMessage').textContent.includes('再生できません'),"
                    + "ended:document.getElementById('briefingNext').disabled};})())");
                if (sample.optBoolean("failed")) throw new AssertionError("Briefing media failure " + sample);
                if (sample.optBoolean("visible") && !sample.optBoolean("paused") && !sample.optBoolean("muted")
                    && sample.optInt("frames") > 0 && sample.optLong("audio") > 0 && sample.optDouble("clock") > 0.1
                    && rendered.add(sample.optString("cue"))) {
                    Log.i(TAG, "briefing round=" + round + " rendered=" + sample);
                }
                if (sample.optBoolean("ended")) { ended = true; break; }
                SystemClock.sleep(120);
            }
            check("briefing completed round " + round, ended);
            check("all twelve subtitles have video and decoded audio round " + round, rendered.size() == 12);
            Log.i(TAG, "briefing round=" + round + " renderedCues=" + rendered.size() + "/12");
        }
        evaluate(view, "document.getElementById('briefingSettings').click()");
    }

    private void runEquipmentTests() throws Exception {
        Activity activity = launchFromShell();
        WebView view = waitForWebView(activity, 10000);
        waitFor("page ready", 15000, () -> "complete".equals(evaluateString(view, "document.readyState")));
        evaluate(view, "document.getElementById('staffToggle').click()");
        waitFor("briefing ready", 10000, () -> evaluateBoolean(view,
            "!document.getElementById('staffBriefingBtn').disabled"));
        evaluate(view, "document.getElementById('staffBriefingBtn').click()");
        SystemClock.sleep(1200);
        waitFor("doctor visible", 10000, () -> evaluateBoolean(view,
            "!document.getElementById('doctorVideo').hidden"));
        JSONObject touch = evaluateObject(view, "JSON.stringify((()=>{var v=document.getElementById('doctorVideo');"
            + "var s=getComputedStyle(v);return {tap:s.webkitTapHighlightColor,selection:s.userSelect,drag:s.webkitUserDrag};})())");
        check("doctor has no tap highlight", "rgba(0, 0, 0, 0)".equals(touch.optString("tap")));
        check("doctor cannot be selected", "none".equals(touch.optString("selection")));
        Log.i(TAG, "doctorTouch " + touch);
        try (ParcelFileDescriptor command = getUiAutomation().executeShellCommand("input swipe 960 450 960 450 700");
             InputStream output = new ParcelFileDescriptor.AutoCloseInputStream(command)) {
            while (output.read() != -1) { /* Wait until pointerup has been dispatched. */ }
        }
        SystemClock.sleep(1000);
        check("long press does not select text", evaluateBoolean(view, "String(window.getSelection())===''"));
        int position = readSceneCounter(view);
        for (; position < 12; position++) {
            int next = position + 1;
            evaluate(view, "document.getElementById('briefingNext').click()");
            waitFor("next sentence " + next, 5000, () -> readSceneCounter(view) == next);
            SystemClock.sleep(200);
        }
        check("final sentence offers equipment", evaluateBoolean(view,
            "document.getElementById('briefingReplay').hidden&&!document.getElementById('briefingNext').hidden"
            + "&&document.getElementById('briefingNext').textContent==='装着の案内へ'"));
        evaluate(view, "document.getElementById('briefingNext').click()");
        waitFor("equipment visible", 5000, () -> evaluateBoolean(view, "!document.getElementById('equipmentGuide').hidden"));
        JSONObject layout = evaluateObject(view, "JSON.stringify((()=>{var g=document.getElementById('equipmentGuide');"
            + "var b=document.getElementById('briefingReplay');var r=g.getBoundingClientRect();var q=b.getBoundingClientRect();"
            + "return {text:g.textContent,title:document.getElementById('briefingTitle').textContent,"
            + "viewport:innerWidth+'x'+innerHeight,width:document.documentElement.scrollWidth,height:document.documentElement.scrollHeight,"
            + "fits:document.documentElement.scrollWidth<=innerWidth+1&&document.documentElement.scrollHeight<=innerHeight+1,"
            + "below:q.top>=r.bottom,buttonVisible:!b.hidden&&q.bottom<=innerHeight,"
            + "doctorHidden:getComputedStyle(document.querySelector('.portrait')).display==='none',"
            + "subtitleHidden:document.getElementById('subtitle').hidden,paused:document.getElementById('doctorVideo').paused};})())");
        check("equipment text", layout.optString("text").contains("左手にコントローラー")
            && layout.optString("text").contains("ヘッドフォン") && layout.optString("text").contains("クエスト内に表示"));
        check("equipment fits tablet", layout.optBoolean("fits"));
        check("replay below equipment", layout.optBoolean("below") && layout.optBoolean("buttonVisible"));
        check("doctor stopped and hidden", layout.optBoolean("doctorHidden") && layout.optBoolean("paused") && layout.optBoolean("subtitleHidden"));
        Log.i(TAG, "equipmentLayout " + layout);
        SystemClock.sleep(500);
        try (ParcelFileDescriptor command = getUiAutomation().executeShellCommand("screencap -p /sdcard/doctor-equipment.png");
             InputStream output = new ParcelFileDescriptor.AutoCloseInputStream(command)) {
            while (output.read() != -1) { /* Capture this screen before starting replay. */ }
        }
        evaluate(view, "document.getElementById('briefingReplay').click()");
        waitFor("replay first sentence", 5000, () -> readSceneCounter(view) == 1
            && evaluateBoolean(view,
                "document.getElementById('equipmentGuide').hidden&&!document.getElementById('subtitle').hidden"));
        waitFor("replay media advancing", 10000, () -> evaluateBoolean(view,
            "!document.getElementById('doctorVideo').hidden&&document.getElementById('doctorVideo').currentTime>0.3"));
        check("replay restores doctor", evaluateBoolean(view, "!document.querySelector('.stage').classList.contains('is-equipment')"));
        evaluate(view, "document.getElementById('briefingSettings').click()");
    }

    private void resumeExisting(Activity activity, WebView webView) throws Exception {
        if (activity.isFinishing() || activity.isDestroyed()) {
            throw new AssertionError("Activity was destroyed while backgrounded");
        }
        try (ParcelFileDescriptor command = getUiAutomation().executeShellCommand(
            "am start --activity-reorder-to-front --activity-single-top -n com.roiril.mawarimi.tablet/.MainActivity")) {}
        waitFor("existing Activity resumed", 10000, () -> {
            AtomicReference<Boolean> resumed = new AtomicReference<>(false);
            runOnMainSync(() -> resumed.set(!activity.isFinishing() && !activity.isDestroyed()
                && activity.hasWindowFocus() && webView.isAttachedToWindow() && webView.isShown()));
            return resumed.get();
        });
        AtomicReference<WebView> current = new AtomicReference<>();
        runOnMainSync(() -> current.set(findWebView(activity.getWindow().getDecorView())));
        if (current.get() != webView) {
            throw new AssertionError("Activity or WebView was recreated while backgrounded");
        }
        Log.i(TAG, "existing Activity resumed with retained WebView");
    }

    private void pressHome() throws IOException {
        ParcelFileDescriptor command = getUiAutomation().executeShellCommand("input keyevent KEYCODE_HOME");
        command.close();
    }

    private WebView waitForWebView(Activity activity, long timeoutMs) throws Exception {
        AtomicReference<WebView> found = new AtomicReference<>();
        waitFor("WebView", timeoutMs, () -> {
            runOnMainSync(() -> found.set(findWebView(activity.getWindow().getDecorView())));
            return found.get() != null;
        });
        return found.get();
    }

    private static WebView findWebView(View view) {
        if (view instanceof WebView) return (WebView) view;
        if (!(view instanceof ViewGroup)) return null;
        ViewGroup group = (ViewGroup) view;
        for (int index = 0; index < group.getChildCount(); index++) {
            WebView found = findWebView(group.getChildAt(index));
            if (found != null) return found;
        }
        return null;
    }

    private String evaluate(WebView view, String script) throws Exception {
        CountDownLatch latch = new CountDownLatch(1);
        AtomicReference<String> value = new AtomicReference<>();
        runOnMainSync(() -> view.evaluateJavascript(script, result -> {
            value.set(result);
            latch.countDown();
        }));
        if (!latch.await(10, TimeUnit.SECONDS)) throw new AssertionError("JavaScript evaluation timed out");
        return value.get();
    }

    private Object evaluateValue(WebView view, String script) throws Exception {
        String encoded = evaluate(view, script);
        return encoded == null ? null : new JSONTokener(encoded).nextValue();
    }

    private String evaluateString(WebView view, String script) throws Exception {
        Object value = evaluateValue(view, script);
        return value == null || value == JSONObject.NULL ? "" : String.valueOf(value);
    }

    private boolean evaluateBoolean(WebView view, String script) throws Exception {
        return Boolean.TRUE.equals(evaluateValue(view, script));
    }

    private double evaluateDouble(WebView view, String script) throws Exception {
        Object value = evaluateValue(view, script);
        if (!(value instanceof Number)) throw new AssertionError("Expected number from " + script + ": " + value);
        return ((Number) value).doubleValue();
    }

    private JSONObject evaluateObject(WebView view, String script) throws Exception {
        return new JSONObject(evaluateString(view, script));
    }

    private JSONArray evaluateArray(WebView view, String script) throws Exception {
        return new JSONArray(evaluateString(view, script));
    }

    private void waitFor(String name, long timeoutMs, CheckedCondition condition) throws Exception {
        long deadline = SystemClock.elapsedRealtime() + timeoutMs;
        Throwable lastError = null;
        while (SystemClock.elapsedRealtime() < deadline) {
            try {
                if (condition.test()) return;
            } catch (Throwable error) {
                lastError = error;
            }
            SystemClock.sleep(100);
        }
        AssertionError timeout = new AssertionError(name + " timed out");
        if (lastError != null) timeout.initCause(lastError);
        throw timeout;
    }

    private void check(String name, boolean condition) {
        checks++;
        if (!condition) throw new AssertionError(name);
        Log.i(TAG, "PASS " + name);
    }

    private interface CheckedCondition {
        boolean test() throws Exception;
    }

    private static final String INSTALL_TEST_HARNESS =
        "(function(){var T=window.__doctorTest={assetsDone:false,mediaDone:false,bridgeDone:false};"
        + "Promise.all([fetch('./asset/briefing-v1.json').then(function(r){return r.ok;}),"
        + "...['briefing-device-v1.png','briefing-survey-v1.png','doctor.jpg'].map(function(n){"
        + "return new Promise(function(ok){var i=new Image();i.onload=function(){ok(i.naturalWidth>0);};"
        + "i.onerror=function(){ok(false);};i.src='./asset/'+n;});})]).then(function(v){"
        + "T.assets={json:v[0],images:v.slice(1).filter(Boolean).length};T.assetsDone=true;});"
        + "T.runMedia=async function(){var files=['introduction-ja-v1.mp4','wear-ja-v1.mp4',"
        + "'subject-ja-v1.mp4','report-ja-v1.mp4','kabe-one-lap-doll-v1.mp4'];T.media=[];"
        + "for(var n of files){var v=document.createElement('video');v.muted=true;v.playsInline=true;"
        + "v.preload='auto';v.style.cssText='position:fixed;left:0;top:0;width:64px;height:36px;z-index:2147483647;pointer-events:none';"
        + "document.body.appendChild(v);var once=function(e,t){"
        + "return new Promise(function(ok,bad){var x=setTimeout(function(){bad(new Error(e+' timeout'));},t);"
        + "v.addEventListener(e,function(){clearTimeout(x);ok();},{once:true});v.addEventListener('error',"
        + "function(){clearTimeout(x);bad(new Error('media error'));},{once:true});});};try{v.src='./asset/'+n;"
        + "v.load();await once('loadeddata',20000);var target=Math.min(0.35,Math.max(0.05,v.duration/3));"
        + "v.currentTime=target;await once('seeked',10000);var seeked=v.currentTime;"
        + "var quality=v.getVideoPlaybackQuality?v.getVideoPlaybackQuality():null;var framesBefore=quality?quality.totalVideoFrames:-1;await v.play();"
        + "var frame=false;if(v.requestVideoFrameCallback){await new Promise(function(ok){var x=setTimeout(ok,3000);"
        + "v.requestVideoFrameCallback(function(){frame=true;clearTimeout(x);ok();});});}else{"
        + "await new Promise(function(ok){setTimeout(ok,700);});frame=v.currentTime>seeked;}"
        + "quality=v.getVideoPlaybackQuality?v.getVideoPlaybackQuality():null;var framesAfter=quality?quality.totalVideoFrames:-1;"
        + "frame=frame||(framesAfter>framesBefore);T.media.push({name:n,loaded:v.readyState>=2,duration:v.duration,"
        + "seeked:seeked,current:v.currentTime,framesBefore:framesBefore,framesAfter:framesAfter,frame:frame});"
        + "}catch(e){T.media.push({name:n,loaded:false,error:String(e)});}v.pause();v.removeAttribute('src');v.load();v.remove();}"
        + "T.mediaDone=true;};T.startBackgroundVideo=async function(){var v=document.createElement('video');"
        + "v.muted=true;v.playsInline=true;v.src='./asset/introduction-ja-v1.mp4';"
        + "v.style.cssText='position:fixed;left:0;top:0;width:64px;height:36px;z-index:2147483647;pointer-events:none';"
        + "document.body.appendChild(v);T.backgroundVideo=v;await v.play();await new Promise(function(ok){"
        + "if(v.requestVideoFrameCallback)v.requestVideoFrameCallback(function(){ok();});else setTimeout(ok,700);});"
        + "T.backgroundReady=true;};T.runBridge=async function(){var r={get:false,post:false};try{"
        + "var g=await TabletTransport.request('/status',{},'alpha');r.get=g.ok;"
        + "if(g.ok){var p=await TabletTransport.request('/tablet/pulse',{method:'POST',body:JSON.stringify({"
        + "tabletSessionId:'instrumentation-test'})},'alpha');r.post=p.ok;}}catch(e){r.error=String(e);}"
        + "T.bridge=r;T.bridgeDone=true;};T.runMutation=async function(){var out={};var id='instrumentation-'+Date.now();"
        + "var original=null;try{var first=await TabletTransport.request('/status',{},'alpha');var s=await first.json();"
        + "if(!first.ok){out.skipped='status';}else if(s.pending){out.skipped='existingPending';}"
        + "else if(s.phase!=='INTRO'||s.titleStage!=='Wait'){out.skipped='notWait';}else{"
        + "original={lang:s.lang,relief:s.relief,portalSessionId:s.portalSessionId};"
        + "var set=await TabletTransport.request('/set',{method:'POST',body:JSON.stringify({lang:original.lang,"
        + "relief:original.relief,tabletSessionId:id,portalSessionId:original.portalSessionId})},'alpha');"
        + "out.set=set.ok;await new Promise(function(ok){setTimeout(ok,400);});}}catch(e){out.error=String(e);}"
        + "finally{if(original){try{var middle=await (await TabletTransport.request('/status',{},'alpha')).json();"
        + "if(middle.pending&&middle.lastRequest&&middle.lastRequest.tabletSessionId===id){"
        + "out.clear=(await TabletTransport.request('/clear',{method:'POST',body:JSON.stringify({tabletSessionId:id,"
        + "portalSessionId:original.portalSessionId})},'alpha')).ok;await new Promise(function(ok){setTimeout(ok,400);});}"
        + "var final=await (await TabletTransport.request('/status',{},'alpha')).json();"
        + "out.restored=final.lang===original.lang&&final.relief===original.relief&&!final.pending;"
        + "}catch(cleanupError){out.cleanupError=String(cleanupError);}}}"
        + "T.mutation=out;T.mutationDone=true;};return true;})()";
}
