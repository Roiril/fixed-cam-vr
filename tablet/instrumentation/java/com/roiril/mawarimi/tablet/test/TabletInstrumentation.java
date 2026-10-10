package com.roiril.mawarimi.tablet.test;

import android.app.Activity;
import android.app.Instrumentation;
import android.content.ComponentName;
import android.content.Context;
import android.graphics.Bitmap;
import android.media.AudioManager;
import android.os.Bundle;
import android.os.ParcelFileDescriptor;
import android.os.SystemClock;
import android.util.Log;
import android.view.View;
import android.view.ViewGroup;
import android.webkit.ValueCallback;
import android.webkit.WebView;
import android.webkit.WebViewClient;
import android.webkit.WebResourceRequest;
import android.webkit.WebResourceResponse;

import org.json.JSONArray;
import org.json.JSONObject;
import org.json.JSONTokener;

import java.io.ByteArrayOutputStream;
import java.io.ByteArrayInputStream;
import java.io.IOException;
import java.io.InputStream;
import java.nio.charset.StandardCharsets;
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
    private static final String LOCAL_URL = "https://appassets.androidplatform.net/index.html";
    private static final String STAFF_FINISH_URL = LOCAL_URL + "?staff=1";
    private static final String TRANSPORT_SCRIPT = "<script src=\"./tablet-transport.js\"></script>";
    private static final Pattern FIRST_NUMBER = Pattern.compile("\\d+");
    private final Bundle results = new Bundle();
    private boolean requireQuest;
    private boolean mutateQuest;
    private int checks;
    private boolean playback;
    private boolean briefing;
    private boolean equipment;
    private boolean staffFlow;
    private boolean staffFinish;
    private boolean visitorFocus;
    private final AtomicReference<String> staffFinishHtml = new AtomicReference<>();

    @Override
    public void onCreate(Bundle arguments) {
        super.onCreate(arguments);
        requireQuest = arguments != null && "true".equalsIgnoreCase(arguments.getString("requireQuest"));
        mutateQuest = arguments != null && "true".equalsIgnoreCase(arguments.getString("mutateQuest"));
        playback = arguments != null && "true".equalsIgnoreCase(arguments.getString("playback"));
        briefing = arguments != null && "true".equalsIgnoreCase(arguments.getString("briefing"));
        equipment = arguments != null && "true".equalsIgnoreCase(arguments.getString("equipment"));
        staffFlow = arguments != null && "true".equalsIgnoreCase(arguments.getString("staffFlow"));
        staffFinish = arguments != null && "true".equalsIgnoreCase(arguments.getString("staffFinish"));
        visitorFocus = arguments != null && "true".equalsIgnoreCase(arguments.getString("visitorFocus"));
        start();
    }

    @Override
    public void onStart() {
        int resultCode = Activity.RESULT_OK;
        try {
            if (visitorFocus || staffFinish) runStaffFinishTests(); else if (staffFlow) runStaffFlowTests(); else if (equipment) runEquipmentTests(); else if (briefing) runBriefingTests(); else if (playback) runPlaybackTests(); else runFunctionalTests();
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
        // A newly created WebView can still report complete for its initial about:blank page.
        waitFor("staff page ready", 15000, () -> evaluateBoolean(view,
            "location.href==='" + LOCAL_URL + "'&&document.readyState==='complete'"
                + "&&typeof TabletTransport!=='undefined'&&TabletTransport.native===true"
                + "&&!!document.getElementById('staff')"));
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
            verifyNativeMediaVolume(activity, view);
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
            openVisitorControls(view);
            int position = readSceneCounter(view);
            // The production UI rejects manual advances less than 150ms apart.
            for (; position < 12; position++) {
                int next = position + 1;
                if (evaluateBoolean(view, "document.getElementById('briefingControls').hidden")) openVisitorControls(view);
                evaluate(view, "document.getElementById('briefingNext').click()");
                waitFor("staff preview sentence " + next, 5000, () -> readSceneCounter(view) == next);
                SystemClock.sleep(200);
            }
            if (evaluateBoolean(view, "document.getElementById('briefingControls').hidden")) openVisitorControls(view);
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
            Log.i(TAG, "staffFlow equipment=" + equipmentLayout);
            saveScreenshot("doctor-staff-flow-equipment.png");
            check("equipment preview fits viewport", equipmentLayout.optBoolean("fits"));
            check("equipment preview has visitor instructions",
                equipmentLayout.optString("text").contains("左手にコントローラー")
                    && equipmentLayout.optString("text").contains("Meta Quest")
                    && equipmentLayout.optString("text").contains("ヘッドフォン"));
            checkButtonBounds(view, "briefingReplay", true);
            checkButtonBounds(view, "briefingSettings", true);

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

    private void verifyNativeMediaVolume(Activity activity, WebView view) throws Exception {
        AudioManager audioManager = (AudioManager) activity.getSystemService(Context.AUDIO_SERVICE);
        check("AudioManager is available", audioManager != null);
        int current = audioManager.getStreamVolume(AudioManager.STREAM_MUSIC);
        int max = audioManager.getStreamMaxVolume(AudioManager.STREAM_MUSIC);
        boolean muted = audioManager.isStreamMute(AudioManager.STREAM_MUSIC);
        JSONObject bridge = evaluateObject(view, "JSON.stringify(TabletTransport.getMediaVolume())");
        check("native media current matches AudioManager", bridge.optInt("current", -1) == current);
        check("native media max matches AudioManager", bridge.optInt("max", -1) == max);
        check("native media muted matches AudioManager", bridge.optBoolean("muted") == muted);
        String expected = "音量 " + current + " / " + max;
        waitFor("native media volume rendered", 5000, () -> expected.equals(evaluateString(view,
            "document.getElementById('devAudioValue').textContent.trim()")));
        String displayed = evaluateString(view, "document.getElementById('devAudioValue').textContent.trim()");
        String guidance = evaluateString(view, "document.getElementById('devAudioFix').textContent.trim()");
        if (current == 0 || muted) {
            check("silent media volume shows guidance", guidance.equals("本体の音量ボタンで上げる")
                && evaluateBoolean(view, "!document.getElementById('devAudioFix').hidden"));
        }
        Log.i(TAG, "staffFlow mediaVolume current=" + current + " max=" + max + " muted=" + muted
            + " displayed=" + displayed + " guidance=" + guidance);
    }

    private void runStaffFinishTests() throws Exception {
        Activity activity = launchFromShell();
        WebView view = waitForWebView(activity, 10000);
        waitFor("production page before fixture", 15000, () -> evaluateBoolean(view,
            "location.href==='" + LOCAL_URL + "'&&document.readyState==='complete'"
                + "&&typeof TabletTransport!=='undefined'&&TabletTransport.native===true"
                + "&&!!document.getElementById('staff')"));
        String source = readAsset(activity, "web/index.html");
        String savedVisitorStorage = visitorFocus ? evaluateString(view,
            "JSON.stringify({session:{...sessionStorage},local:{...localStorage}})") : null;
        check("production transport script occurs once", source.indexOf(TRANSPORT_SCRIPT) >= 0
            && source.indexOf(TRANSPORT_SCRIPT) == source.lastIndexOf(TRANSPORT_SCRIPT));
        int[] originalSize = new int[2];
        WebViewClient[] originalClient = new WebViewClient[1];
        runOnMainSync(() -> {
            originalClient[0] = view.getWebViewClient();
            // Keep the application's origin/asset policy and CSP. Only replace the HTML body
            // for this test URL. loadDataWithBaseURL can become chrome-error with blocked networking.
            view.setWebViewClient(new WebViewClient() {
                @Override public boolean shouldOverrideUrlLoading(WebView target, WebResourceRequest request) {
                    return originalClient[0].shouldOverrideUrlLoading(target, request);
                }
                @Override public WebResourceResponse shouldInterceptRequest(WebView target, WebResourceRequest request) {
                    WebResourceResponse response = originalClient[0].shouldInterceptRequest(target, request);
                    if (request.isForMainFrame() && STAFF_FINISH_URL.equals(request.getUrl().toString())
                        && response != null && response.getStatusCode() == 200 && staffFinishHtml.get() != null) {
                        try { response.getData().close(); }
                        catch (IOException error) { Log.w(TAG, "fixture source close", error); }
                        byte[] bytes = staffFinishHtml.get().getBytes(StandardCharsets.UTF_8);
                        response.setData(new ByteArrayInputStream(bytes));
                        java.util.Map<String, String> headers = new java.util.HashMap<>(response.getResponseHeaders());
                        headers.put("Content-Length", String.valueOf(bytes.length));
                        response.setResponseHeaders(headers);
                    }
                    return response;
                }
                @Override public void onPageFinished(WebView target, String url) {
                    originalClient[0].onPageFinished(target, url);
                }
            });
            ViewGroup.LayoutParams params = view.getLayoutParams();
            originalSize[0] = params.width;
            originalSize[1] = params.height;
        });
        try {
            if (visitorFocus) {
                runVisitorFocusTests(activity, view, source, originalSize[0]);
                return;
            }
            for (String scenario : new String[] {"setup", "handover", "playing", "outro", "finished", "offline"}) {
                runStaffFinishScenario(activity, view, source, scenario, false, originalSize[0]);
                if ("handover".equals(scenario)) runStaffFinishReceipts(view);
            }
            for (String scenario : new String[] {"setup", "finished"}) {
                runStaffFinishScenario(activity, view, source, scenario, true, originalSize[0]);
            }
        } finally {
            if (savedVisitorStorage != null) {
                evaluate(view, "(s=>{sessionStorage.clear();localStorage.clear();"
                    + "Object.entries(s.session).forEach(([k,v])=>sessionStorage.setItem(k,v));"
                    + "Object.entries(s.local).forEach(([k,v])=>localStorage.setItem(k,v));})("
                    + savedVisitorStorage + ")");
            }
            runOnMainSync(() -> {
                view.setWebViewClient(originalClient[0]);
                staffFinishHtml.set(null);
                ViewGroup.LayoutParams params = view.getLayoutParams();
                params.width = originalSize[0];
                params.height = originalSize[1];
                view.setLayoutParams(params);
                view.requestLayout();
                view.loadUrl(LOCAL_URL);
            });
            Log.i(TAG, "staffFinish restored production URL=" + LOCAL_URL + " width=" + originalSize[0]);
        }
    }

    private String visitorFocusTransport() throws Exception {
        JSONObject status = staffFinishStatus("handover");
        status.put("portalSessionId", "visitor-focus-" + SystemClock.elapsedRealtime());
        return "(()=>{'use strict';sessionStorage.clear();const status=" + status + ";let tick=100;"
            + "globalThis.__visitorFocusStatus=status;globalThis.__visitorFocusOffline=false;"
            + "const response=body=>Promise.resolve({ok:true,status:200,json:async()=>JSON.parse(JSON.stringify(body))});"
            + "globalThis.TabletTransport=Object.freeze({native:false,getQuest:()=>'beta',setQuest:()=>{},"
            + "getMediaVolume:()=>({current:0,max:15,muted:true}),request:(path,options={})=>{"
            + "if(String(path).startsWith('./asset/'))return fetch(path,options);"
            + "if(globalThis.__visitorFocusOffline)return Promise.reject(new TypeError('fixture offline'));"
            + "if(path==='./status'){status.questTick=++tick;return response(status);}"
            + "const b=JSON.parse(options.body||'{}');if(path==='./set'){"
            + "status.received++;status.appliedSeq=status.received;status.applyCount++;"
            + "status.lang=b.lang;status.relief=b.relief;"
            + "status.pending={seq:status.received,lang:b.lang,relief:b.relief};"
            + "status.lastRequest={...status.pending,tabletSessionId:b.tabletSessionId};"
            + "status.staffSetup.stage='explanation';status.staffSetup.reason='explanation';"
            + "return response({ok:true,seq:status.received});}"
            + "if(path==='./tablet/pulse'){if(b.seq>0){status.briefing={seq:b.seq,revision:b.briefingRevision,"
            + "tabletSessionId:b.tabletSessionId,completed:!!b.briefingCompleted,staffConfirmed:!!b.staffConfirmed};"
            + "status.staffSetup.stage=b.staffConfirmed?'handedOff':b.briefingCompleted?'ready':'explanation';"
            + "status.staffSetup.reason=b.staffConfirmed?'':b.briefingCompleted?'staff':'explanation';}"
            + "return response({ok:true});}return Promise.reject(new Error('fixture request rejected: '+path));}});})();";
    }

    private void runVisitorFocusTests(Activity activity, WebView view, String source, int originalWidth) throws Exception {
        for (String lang : new String[] {"ja", "en", "fr"}) {
            loadVisitorFocus(activity, view, source, lang, false, originalWidth);
            verifyVisitorFocus(view, lang + "-wide", false);
            if ("ja".equals(lang)) verifyVisitorFocusInteractions(view);
        }
        loadVisitorFocus(activity, view, source, "fr", true, originalWidth);
        verifyVisitorFocus(view, "fr-narrow", true);
        openVisitorControls(view);
        saveScreenshot("doctor-visitor-focus-controls-narrow.png");
        verifyVisitorControlsLayout(view);
    }

    private void loadVisitorFocus(Activity activity, WebView view, String source, String lang,
                                  boolean narrow, int originalWidth) throws Exception {
        String html = source.replace(TRANSPORT_SCRIPT, "<script>" + visitorFocusTransport() + "</script>");
        int width = narrow ? Math.round(375 * activity.getResources().getDisplayMetrics().density) : originalWidth;
        runOnMainSync(() -> {
            ViewGroup.LayoutParams params = view.getLayoutParams();
            params.width = width;
            view.setLayoutParams(params);
            view.requestLayout();
            staffFinishHtml.set(html);
            view.loadUrl(STAFF_FINISH_URL);
        });
        waitFor("visitor fixture ready", 15000, () -> evaluateBoolean(view,
            "typeof __visitorFocusStatus!=='undefined'&&document.readyState==='complete'"
                + "&&document.getElementById('staff').open&&!document.getElementById('staffVisitorBtn').hidden"
                + "&&!document.getElementById('staffBriefingBtn').disabled"));
        evaluate(view, "document.getElementById('staffVisitorBtn').click();document.getElementById('titleStart').click()");
        waitFor("visitor settings", 5000, () -> evaluateBoolean(view,
            "!document.getElementById('settingsView').hidden"));
        evaluate(view, "(()=>{const e=document.querySelector('input[name=lang][value=" + lang + "]');"
            + "e.checked=true;e.dispatchEvent(new Event('change',{bubbles:true}));"
            + "document.getElementById('settingsNextBtn').click();document.getElementById('sendBtn').click();})()");
        waitFor("visitor confirmation", 5000, () -> evaluateBoolean(view,
            "!document.getElementById('applyBtn').hidden"));
        evaluate(view, "document.getElementById('applyBtn').click()");
        waitFor("visitor settings applied", 8000, () -> evaluateBoolean(view,
            "!document.getElementById('resultContinueBtn').hidden"));
        evaluate(view, "document.getElementById('resultContinueBtn').click()");
        waitFor("visitor focused briefing", 10000, () -> evaluateBoolean(view,
            "!document.getElementById('briefingView').hidden&&!document.getElementById('staff').open"
                + "&&document.getElementById('subtitleTyped').textContent.length>0"
                + "&&document.getElementById('briefingControls').hidden"));
        if ("ja".equals(lang)) waitFor("doctor frame playing", 10000, () -> evaluateBoolean(view,
            "!document.getElementById('doctorVideo').hidden&&document.getElementById('doctorVideo').currentTime>0.1"));
        SystemClock.sleep(400);
    }

    private JSONObject visitorFocusLayout(WebView view) throws Exception {
        return evaluateObject(view, "JSON.stringify((()=>{"
            + "const shown=e=>{if(!e)return false;const r=e.getBoundingClientRect(),s=getComputedStyle(e);"
            + "return !e.hidden&&r.width>0&&r.height>0&&s.display!=='none'&&s.visibility!=='hidden';};"
            + "const rect=e=>{const r=e.getBoundingClientRect();return {x:r.x,y:r.y,w:r.width,h:r.height,right:r.right,bottom:r.bottom};};"
            + "const noise=['.brand','#journey','#connectionText','#staffToggle','.briefing-copy','.briefing-visual',"
            + "'#briefingProgress','#briefingControls'];"
            + "const video=document.getElementById('doctorVideo'),still=document.getElementById('doctorImage'),"
            + "caption=document.getElementById('subtitle');"
            + "return {width:innerWidth,height:innerHeight,scrollWidth:document.documentElement.scrollWidth,"
            + "scrollHeight:document.documentElement.scrollHeight,noise:noise.filter(s=>shown(document.querySelector(s))),"
            + "captionVisible:shown(caption),caption:rect(caption),text:document.getElementById('subtitleTyped').textContent,"
            + "doctorVisible:shown(video)||shown(still),videoVisible:shown(video),doctor:rect(shown(video)?video:still),"
            + "frames:video.webkitDecodedFrameCount||0,time:video.currentTime,paused:video.paused,"
            + "counter:document.getElementById('sceneCounter').textContent};})())");
    }

    private void verifyVisitorFocus(WebView view, String name, boolean narrow) throws Exception {
        JSONObject layout = visitorFocusLayout(view);
        Log.i(TAG, "visitorFocus scenario=" + name + " layout=" + layout);
        saveScreenshot("doctor-visitor-focus-" + name + ".png");
        check(name + " only doctor and subtitle", layout.getJSONArray("noise").length() == 0);
        check(name + " subtitle rendered", layout.getBoolean("captionVisible") && !layout.getString("text").isEmpty());
        check(name + " doctor rendered", layout.getBoolean("doctorVisible"));
        check(name + " no horizontal overflow", layout.getInt("scrollWidth") <= layout.getInt("width") + 1);
        check(name + " no vertical page overflow", layout.getInt("scrollHeight") <= layout.getInt("height") + 1);
        if (narrow) check(name + " uses 375 CSS px", Math.abs(layout.getInt("width") - 375) <= 2);
        JSONObject caption = layout.getJSONObject("caption"), doctor = layout.getJSONObject("doctor");
        check(name + " subtitle stays on screen", caption.getDouble("x") >= 0 && caption.getDouble("y") >= 0
            && caption.getDouble("right") <= layout.getInt("width") + 1 && caption.getDouble("bottom") <= layout.getInt("height") + 1);
        check(name + " doctor and subtitle do not overlap", doctor.getDouble("bottom") <= caption.getDouble("y") + 1);
    }

    private void openVisitorControls(WebView view) throws Exception {
        evaluate(view, "(()=>{const e=document.getElementById('subtitle'),r=e.getBoundingClientRect();"
            + "for(const type of ['pointerdown','pointerup'])e.dispatchEvent(new PointerEvent(type,"
            + "{bubbles:true,isPrimary:true,pointerId:21,button:0,clientX:r.x+r.width/2,clientY:r.y+r.height/2}));})()");
        waitFor("visitor controls visible", 3000, () -> evaluateBoolean(view,
            "!document.getElementById('briefingControls').hidden"));
    }

    private void holdVisitorButton(WebView view, String id, int duration) throws Exception {
        evaluate(view, "(()=>{const e=document.getElementById('" + id + "'),r=e.getBoundingClientRect();"
            + "e.dispatchEvent(new PointerEvent('pointerdown',{bubbles:true,isPrimary:true,pointerId:22,button:0,"
            + "clientX:r.x+r.width/2,clientY:r.y+r.height/2}));})()");
        SystemClock.sleep(duration);
        evaluate(view, "document.getElementById('" + id + "').dispatchEvent(new PointerEvent('pointerup',"
            + "{bubbles:true,isPrimary:true,pointerId:22,button:0}))");
    }

    private void ensureStaffScreen(WebView view) throws Exception {
        if (evaluateBoolean(view, "document.getElementById('staff').open")) return;
        boolean speaking = evaluateBoolean(view,
            "!document.getElementById('briefingView').hidden&&document.getElementById('equipmentGuide').hidden");
        if (speaking && evaluateBoolean(view, "document.getElementById('briefingControls').hidden")) openVisitorControls(view);
        holdVisitorButton(view, speaking ? "briefingStaff" : "staffToggle", 1650);
        waitFor("staff screen opened deliberately", 3000, () -> evaluateBoolean(view, "document.getElementById('staff').open"));
    }

    private void verifyVisitorControlsLayout(WebView view) throws Exception {
        JSONObject result = evaluateObject(view, "JSON.stringify((()=>{const e=document.getElementById('briefingControls'),"
            + "r=e.getBoundingClientRect();return {width:innerWidth,height:innerHeight,x:r.x,y:r.y,right:r.right,bottom:r.bottom,"
            + "buttons:[...e.querySelectorAll('button')].filter(b=>!b.hidden&&b.getBoundingClientRect().height>0)"
            + ".map(b=>{const r=b.getBoundingClientRect();return {id:b.id,height:r.height,onScreen:r.left>=0&&r.top>=0"
            + "&&r.right<=innerWidth+1&&r.bottom<=innerHeight+1};})};})())");
        Log.i(TAG, "visitorFocus controls=" + result);
        check("controls panel inside viewport", result.getDouble("x") >= 0 && result.getDouble("y") >= 0
            && result.getDouble("right") <= result.getInt("width") + 1 && result.getDouble("bottom") <= result.getInt("height") + 1);
        JSONArray buttons = result.getJSONArray("buttons");
        check("controls contain actions", buttons.length() >= 5);
        for (int i = 0; i < buttons.length(); i++) {
            JSONObject b = buttons.getJSONObject(i);
            check("control touch target " + b.getString("id"), b.getDouble("height") >= 44);
            check("control on screen " + b.getString("id"), b.getBoolean("onScreen"));
        }
    }

    private void verifyVisitorFocusInteractions(WebView view) throws Exception {
        String before = evaluateString(view, "document.getElementById('sceneCounter').textContent");
        double clockBefore = evaluateDouble(view, "document.getElementById('doctorVideo').currentTime");
        double framesBefore = evaluateDouble(view, "document.getElementById('doctorVideo').webkitDecodedFrameCount");
        SystemClock.sleep(500);
        check("doctor decodes additional real frames", evaluateDouble(view,
            "document.getElementById('doctorVideo').webkitDecodedFrameCount") > framesBefore);
        openVisitorControls(view);
        check("tap did not skip the sentence", before.equals(evaluateString(view,
            "document.getElementById('sceneCounter').textContent")));
        double stopped = evaluateDouble(view, "document.getElementById('doctorVideo').currentTime");
        SystemClock.sleep(600);
        check("controls pause media", evaluateBoolean(view, "document.getElementById('doctorVideo').paused")
            && Math.abs(evaluateDouble(view, "document.getElementById('doctorVideo').currentTime") - stopped) < 0.08);
        saveScreenshot("doctor-visitor-focus-controls-wide.png");
        verifyVisitorControlsLayout(view);
        evaluate(view, "document.getElementById('briefingResume').click()");
        waitFor("visitor playback resumes", 4000, () -> evaluateBoolean(view,
            "document.getElementById('briefingControls').hidden&&!document.getElementById('doctorVideo').paused"));
        check("resume preserves sentence playback position", evaluateDouble(view,
            "document.getElementById('doctorVideo').currentTime") >= stopped - 0.12 && stopped > clockBefore);
        waitFor("visitor automatic sentence advance", 30000, () -> !before.equals(evaluateString(view,
            "document.getElementById('sceneCounter').textContent")));
        openVisitorControls(view);
        evaluate(view, "document.getElementById('briefingStaff').click()");
        check("short staff tap does not expose staff data", !evaluateBoolean(view, "document.getElementById('staff').open"));
        holdVisitorButton(view, "briefingStaff", 200);
        SystemClock.sleep(1500);
        check("released staff hold is cancelled", !evaluateBoolean(view, "document.getElementById('staff').open"));
        holdVisitorButton(view, "briefingStaff", 1650);
        waitFor("intentional staff entry", 2000, () -> evaluateBoolean(view, "document.getElementById('staff').open"));
        evaluate(view, "document.getElementById('staffDoneBtn').click()");
        waitFor("staff returns to paused controls", 2000, () -> evaluateBoolean(view,
            "!document.getElementById('staff').open&&!document.getElementById('briefingControls').hidden"));
        check("staff return does not silently resume", evaluateBoolean(view, "document.getElementById('doctorVideo').paused"));
        // Real local media and the production controls remain in use. Only Quest responses are fixtures.
        for (int i = 0; i < 14; i++) {
            if (evaluateBoolean(view, "!document.getElementById('equipmentGuide').hidden")) break;
            if (evaluateBoolean(view, "document.getElementById('briefingControls').hidden")) openVisitorControls(view);
            evaluate(view, "document.getElementById('briefingNext').click()");
            SystemClock.sleep(700);
        }
        waitFor("visitor handback", 4000, () -> evaluateBoolean(view,
            "!document.getElementById('equipmentGuide').hidden"));
        check("handback does not automatically expose staff data", !evaluateBoolean(view, "document.getElementById('staff').open"));
        check("handback asks to return tablet", evaluateString(view,
            "document.getElementById('briefingTitle').textContent").contains("スタッフ"));
        saveScreenshot("doctor-visitor-focus-handback-wide.png");
        holdVisitorButton(view, "staffToggle", 1650);
        waitFor("staff confirms returned tablet", 3000, () -> evaluateBoolean(view,
            "document.getElementById('staff').open&&!document.getElementById('staffPrepareBtn').disabled"));
        holdVisitorButton(view, "staffPrepareBtn", 1350);
        waitFor("acknowledged wear briefing", 7000, () -> evaluateBoolean(view,
            "!document.getElementById('staff').open&&document.getElementById('equipmentGuide').hidden"
                + "&&document.getElementById('doctorVideo').src.includes('wear-ja')"));
        waitFor("wear playback starts after acknowledged staff confirmation", 4000, () -> evaluateBoolean(view,
            "document.getElementById('briefingControls').hidden&&!document.getElementById('doctorVideo').paused"));
        SystemClock.sleep(500);
        verifyVisitorFocus(view, "wear-wide", false);
        evaluate(view, "globalThis.__tabletLifecycle(true)");
        check("background freezes wear media", evaluateBoolean(view, "document.getElementById('doctorVideo').paused"));
        evaluate(view, "globalThis.__tabletLifecycle(false)");
        check("foreground provides explicit resume", evaluateBoolean(view,
            "!document.getElementById('briefingControls').hidden&&document.getElementById('doctorVideo').paused"));
        evaluate(view, "document.getElementById('briefingResume').click();globalThis.__visitorFocusOffline=true");
        waitFor("offline briefing recovery shown", 6000, () -> evaluateBoolean(view,
            "!document.getElementById('briefingControls').hidden&&document.getElementById('doctorVideo').paused"));
        saveScreenshot("doctor-visitor-focus-offline-wide.png");
        evaluate(view, "globalThis.__visitorFocusOffline=false");
    }

    private void runStaffFinishScenario(Activity activity, WebView view, String source, String scenario,
                                        boolean narrow, int originalWidth) throws Exception {
        int current = "setup".equals(scenario) ? 0 : 8;
        int max = 15;
        boolean muted = false;
        String html = source.replace(TRANSPORT_SCRIPT,
            "<script>" + staffFinishTransport(scenario, current, max, muted) + "</script>");
        int width = narrow ? Math.round(375 * activity.getResources().getDisplayMetrics().density) : originalWidth;
        runOnMainSync(() -> {
            ViewGroup.LayoutParams params = view.getLayoutParams();
            params.width = width;
            view.setLayoutParams(params);
            view.requestLayout();
            staffFinishHtml.set(html);
            view.loadUrl(STAFF_FINISH_URL);
        });
        SystemClock.sleep(700);
        Log.i(TAG, "staffFinish loaded=" + evaluate(view,
            "JSON.stringify({url:location.href,ready:document.readyState,transport:typeof TabletTransport,"
                + "state:document.getElementById('staff')?.dataset.state,"
                + "title:document.getElementById('staffNowTitle')?.textContent})"));
        String expectedState = expectedStaffState(scenario);
        String expectedVolume = "音量 " + current + " / " + max;
        waitFor("staffFinish " + scenario + " " + (narrow ? "narrow" : "wide"), 20000, () -> evaluateBoolean(view,
            "document.readyState==='complete'&&document.getElementById('staff').open"
                + "&&document.getElementById('staff').dataset.state==='" + expectedState + "'"
                + "&&document.getElementById('staffNowTitle').textContent.trim()==='" + expectedStaffTitle(scenario) + "'"
                + "&&document.getElementById('devAudioValue').textContent.trim()==='" + expectedVolume + "'"));
        evaluate(view, "document.getElementById('staffTrouble').open=false;document.getElementById('staff').scrollTop=0");
        String actionId = expectedStaffActionId(scenario);
        if (narrow && !actionId.isEmpty()) {
            evaluate(view, "document.getElementById('" + actionId + "').scrollIntoView({block:'center',inline:'nearest'})");
            SystemClock.sleep(250);
        }
        JSONObject layout = staffFinishLayout(view);
        String size = narrow ? "narrow" : "wide";
        Log.i(TAG, "staffFinish scenario=" + scenario + " size=" + size + " layout=" + layout);
        saveScreenshot("doctor-staff-finish-" + scenario + "-" + size + ".png");
        check("staffFinish " + scenario + " document has no horizontal overflow",
            layout.optDouble("documentOverflowX") <= 1);
        check("staffFinish " + scenario + " staff has no horizontal overflow",
            layout.optDouble("staffOverflowX") <= 1);
        if (narrow) {
            check("staffFinish " + scenario + " uses 375 CSS px width",
                Math.abs(layout.optDouble("viewportWidth") - 375) <= 2);
        } else {
            check("staffFinish " + scenario + " uses tablet landscape viewport",
                layout.optDouble("viewportWidth") >= 1000 && layout.optDouble("viewportHeight") >= 600);
            check("staffFinish " + scenario + " fits without staff scroll",
                layout.optDouble("staffScrollHeight") <= layout.optDouble("viewportHeight") + 1);
        }
        check("staffFinish " + scenario + " title rendered",
            expectedStaffTitle(scenario).equals(layout.optString("title")));
        check("staffFinish " + scenario + " media volume rendered",
            expectedVolume.equals(layout.optString("audio")));
        check("staffFinish " + scenario + " renders device rows", layout.optInt("deviceCount") >= 7);
        String readiness = layout.optString("readiness");
        check("staffFinish " + scenario + " readiness is measured", readiness.matches("[0-6] / 6"));
        if ("setup".equals(scenario)) {
            check("staffFinish setup reports incomplete readiness", "4 / 6".equals(readiness));
            check("staffFinish setup shows camera failure", layout.optString("cameraB").equals("映像が届いていません"));
            check("staffFinish setup shows recovery", layout.optString("recovery").startsWith("対処："));
            check("staffFinish setup shows silent volume guidance",
                layout.optString("audioFix").equals("本体の音量ボタンで上げる"));
        } else if ("offline".equals(scenario)) {
            check("staffFinish offline reports zero readiness", "0 / 6".equals(readiness));
            check("staffFinish offline shows recovery", layout.optString("recovery").startsWith("対処："));
        } else {
            check("staffFinish " + scenario + " reports complete readiness", "6 / 6".equals(readiness));
            check("staffFinish " + scenario + " has no volume guidance", layout.optString("audioFix").isEmpty());
        }
        if ("playing".equals(scenario)) {
            check("staffFinish playing shows lap and time", layout.optString("meta").equals("2 周目 · 1:24"));
        }
        if ("finished".equals(scenario)) {
            String items = layout.optString("items");
            check("staffFinish finished shows collection checklist", items.contains("ヘッドセット")
                && items.contains("ヘッドフォン") && items.contains("左コントローラー") && items.contains("清拭"));
        }
        JSONArray buttons = layout.getJSONArray("buttons");
        check("staffFinish " + scenario + " has expected main action count",
            buttons.length() == (actionId.isEmpty() ? 0 : 1));
        if (!actionId.isEmpty()) {
            JSONObject button = buttons.getJSONObject(0);
            check("staffFinish " + scenario + " main action id", actionId.equals(button.optString("id")));
            check("staffFinish " + scenario + " main action text",
                expectedStaffActionText(scenario).equals(button.optString("text")));
            check("staffFinish " + scenario + " main action is at least 44px", button.optDouble("height") >= 44);
            check("staffFinish " + scenario + " main action is on screen", button.optBoolean("onScreen"));
        }
        check("staffFinish " + scenario + " uses production colors",
            !layout.optString("shellBackground").isEmpty()
                && !"rgba(0, 0, 0, 0)".equals(layout.optString("shellBackground")));
    }

    private JSONObject staffFinishLayout(WebView view) throws Exception {
        return evaluateObject(view,
            "JSON.stringify((()=>{var d=document.documentElement,s=document.getElementById('staff'),"
                + "sh=s.querySelector('.staff-shell');var shown=e=>{var r=e.getBoundingClientRect(),c=getComputedStyle(e);"
                + "return !e.hidden&&c.display!=='none'&&c.visibility==='visible'&&r.width>0&&r.height>0;};"
                + "var buttons=[...document.querySelectorAll('.staff-now-actions button')].filter(shown).map(e=>{"
                + "var r=e.getBoundingClientRect();return {id:e.id,text:e.textContent.trim(),height:r.height,"
                + "onScreen:r.left>=-1&&r.top>=-1&&r.right<=innerWidth+1&&r.bottom<=innerHeight+1};});"
                + "return {viewportWidth:innerWidth,viewportHeight:innerHeight,"
                + "documentOverflowX:Math.max(0,d.scrollWidth-d.clientWidth),"
                + "staffOverflowX:Math.max(0,s.scrollWidth-s.clientWidth),staffScrollHeight:s.scrollHeight,"
                + "title:document.getElementById('staffNowTitle').textContent.trim(),"
                + "meta:document.getElementById('staffNowMeta').textContent.trim(),"
                + "items:document.getElementById('staffNowList').textContent.trim(),"
                + "readiness:document.getElementById('staffReadinessCount').textContent.trim(),"
                + "audio:document.getElementById('devAudioValue').textContent.trim(),"
                + "audioFix:document.getElementById('devAudioFix').textContent.trim(),"
                + "cameraB:document.getElementById('devCameraBValue').textContent.trim(),"
                + "receipt:document.getElementById('staffReceipt').textContent.trim(),"
                + "recovery:document.getElementById('staffRecovery').textContent.trim(),"
                + "deviceCount:[...document.querySelectorAll('.staff-device')].filter(shown).length,buttons:buttons,"
                + "shellBackground:getComputedStyle(s).backgroundColor};})())");
    }

    private String staffFinishTransport(String scenario, int current, int max, boolean muted) throws Exception {
        JSONObject status = staffFinishStatus(scenario);
        boolean offline = "offline".equals(scenario);
        return "(()=>{'use strict';const status=" + status + ";let tick=status.questTick;"
            + "globalThis.__staffFinishStatus=status;"
            + "const response=(code,body)=>Promise.resolve({ok:code>=200&&code<300,status:code,"
            + "json:async()=>JSON.parse(JSON.stringify(body)),text:async()=>JSON.stringify(body)});"
            + "globalThis.TabletTransport=Object.freeze({native:false,getQuest:()=>'',setQuest:()=>{},"
            + "getMediaVolume:()=>({current:" + current + ",max:" + max + ",muted:" + muted + "}),"
            + "request:(path,options={})=>{if(String(path).startsWith('./asset/'))return fetch(path,options);"
            + (offline
                ? "return Promise.reject(new TypeError('fixture offline'));"
                : "if(path==='./status'||path==='/status'){status.questTick=++tick;return response(200,status);}"
                    + "if(path==='./tablet/pulse'||path==='/tablet/pulse'){const body=JSON.parse(options.body||'{}');"
                    + "if(body.staffReset){status.portalSessionId='staff-finish-reset';status.staffResetId=body.resetRequestId;"
                    + "status.visitorGeneration++;status.staffSetup.stage='settings';status.staffSetup.reason='settings';}"
                    + "return response(200,{ok:true});}"
                    + "return Promise.reject(new Error('fixture request rejected'));"
            )
            + "}});})();";
    }

    private void runStaffFinishReceipts(WebView view) throws Exception {
        evaluate(view, "__staffFinishStatus.staffSetup.cameras[1].state='trouble';"
            + "__staffFinishStatus.staffSetup.cameras[1].problem='nostream'");
        waitFor("receipt failure observed", 6000, () -> evaluateBoolean(view,
            "document.getElementById('devCameraB').dataset.tone==='trouble'"));
        evaluate(view, "__staffFinishStatus.staffSetup.cameras[1].state='ok';"
            + "__staffFinishStatus.staffSetup.cameras[1].problem=''");
        verifyReceipt(view, "接続と機器の状態が戻りました", "recovered");
        evaluate(view, "__staffFinishStatus.staffSetup.stage='reset';__staffFinishStatus.staffSetup.reason='reset'");
        waitFor("initial preparation action", 6000, () -> evaluateBoolean(view,
            "!document.getElementById('staffResetBtn').hidden&&!document.getElementById('staffResetBtn').disabled"));
        evaluate(view, "document.getElementById('staffResetBtn').click()");
        verifyReceipt(view, "次の体験者の準備ができました", "prepared");
    }

    private void verifyReceipt(WebView view, String text, String name) throws Exception {
        waitFor("receipt " + name, 6000, () -> evaluateBoolean(view,
            "document.getElementById('staffReceipt').classList.contains('is-visible')"
                + "&&document.getElementById('staffReceipt').textContent==='" + text + "'"));
        SystemClock.sleep(250);
        check("receipt " + name + " is fully on screen", evaluateBoolean(view,
            "(()=>{const e=document.getElementById('staffReceipt'),r=e.getBoundingClientRect();"
                + "return r.left>=0&&r.top>=0&&r.right<=innerWidth&&r.bottom<=innerHeight"
                + "&&getComputedStyle(e).opacity==='1';})()"));
        saveScreenshot("doctor-staff-finish-" + name + "-wide.png");
    }

    private JSONObject staffFinishStatus(String scenario) throws Exception {
        JSONObject setup = staffSetup("settings", "settings");
        String phase = "INTRO";
        if ("setup".equals(scenario)) {
            setup = staffSetup("setup", "camera");
            setup.put("positionConfirmed", false).put("position", "needed");
            setup.getJSONArray("cameras").getJSONObject(1).put("state", "trouble").put("problem", "nostream");
        } else if ("playing".equals(scenario)) {
            phase = "RUN";
            setup = staffSetup("playing", "");
            setup.put("run", staffRun("RUN", 2, 84, "off", ""));
        } else if ("outro".equals(scenario)) {
            phase = "END";
            setup = staffSetup("ended", "");
            setup.put("run", staffRun("END", 3, 168, "playing", "released"));
        } else if ("finished".equals(scenario)) {
            phase = "END";
            setup = staffSetup("ended", "");
            setup.put("run", staffRun("END", 3, 176, "done", "released"));
        }
        return new JSONObject()
            .put("ok", true).put("lang", "ja").put("relief", false).put("phase", phase)
            .put("titleStage", "Wait").put("appliedSeq", 0).put("applyCount", 0).put("received", 0)
            .put("pending", JSONObject.NULL).put("questTick", 100).put("portalSessionId", "staff-finish-stable")
            .put("lastRequest", JSONObject.NULL).put("visitorGeneration", 1).put("staffResetId", "")
            .put("staffResetRejectedId", "").put("briefing", JSONObject.NULL).put("staffSetup", setup);
    }

    private JSONObject staffSetup(String stage, String reason) throws Exception {
        JSONArray cameras = new JSONArray();
        for (String id : new String[] {"A", "B", "C"}) {
            cameras.put(new JSONObject().put("id", id).put("state", "ok").put("problem", ""));
        }
        return new JSONObject().put("stage", stage).put("reason", reason).put("positionConfirmed", true)
            .put("resetProgress", 0).put("position", "confirmed").put("cameras", cameras)
            .put("tablet", "ok").put("content", true)
            .put("run", staffRun("INTRO", 0, 0, "off", ""));
    }

    private JSONObject staffRun(String phase, int lap, int sec, String outro, String ending) throws Exception {
        return new JSONObject().put("phase", phase).put("lap", lap).put("laps", 3).put("sec", sec)
            .put("outro", outro).put("ending", ending);
    }

    private String expectedStaffState(String scenario) {
        return "handover".equals(scenario) ? "handover" : scenario;
    }

    private String expectedStaffTitle(String scenario) {
        if ("setup".equals(scenario)) return "ヘッドセットで最初の準備";
        if ("handover".equals(scenario)) return "タブレットを来場者に渡す";
        if ("playing".equals(scenario)) return "体験中";
        if ("outro".equals(scenario)) return "終わりの演出中です";
        if ("finished".equals(scenario)) return "体験が終わりました";
        if ("offline".equals(scenario)) return "クエストに接続できません";
        throw new AssertionError("Unknown staffFinish scenario: " + scenario);
    }

    private String expectedStaffActionId(String scenario) {
        if ("handover".equals(scenario)) return "staffVisitorBtn";
        if ("finished".equals(scenario)) return "staffCollectBtn";
        if ("offline".equals(scenario)) return "staffRetryBtn";
        return "";
    }

    private String expectedStaffActionText(String scenario) {
        if ("handover".equals(scenario)) return "来場者の画面にする";
        if ("finished".equals(scenario)) return "長押しで次の準備へ進む";
        if ("offline".equals(scenario)) return "もう一度確認する";
        return "";
    }

    private String readAsset(Activity activity, String path) throws IOException {
        try (InputStream input = activity.getAssets().open(path);
             ByteArrayOutputStream output = new ByteArrayOutputStream()) {
            byte[] buffer = new byte[8192];
            for (int count; (count = input.read(buffer)) >= 0;) output.write(buffer, 0, count);
            return new String(output.toByteArray(), StandardCharsets.UTF_8);
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
                + "if(!document.getElementById('briefingView').hidden&&!b.hidden)b.click();return true;})()");
            ensureStaffScreen(view);
            evaluate(view, "(function(){document.getElementById('staffTrouble').open=true;var q=document.getElementById('quest"
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

    private boolean screenshotProbeChecked;

    private boolean hasRenderedColors(Bitmap bitmap) {
        Set<Integer> colors = new HashSet<>();
        int[] row = new int[bitmap.getWidth()];
        for (int y = 0; y < bitmap.getHeight(); y++) {
            bitmap.getPixels(row, 0, row.length, 0, y, row.length, 1);
            for (int color : row) {
                colors.add(color);
                if (colors.size() >= 4) return true;
            }
        }
        return false;
    }

    private void saveScreenshot(String filename) throws Exception {
        boolean visitorFocusName = filename.matches("doctor-visitor-focus-[a-z-]+\\.png");
        boolean staffFinishName = filename.matches(
            "doctor-staff-finish-(setup|handover|playing|outro|finished|offline|recovered|prepared)-(wide|narrow)\\.png")
            && !(filename.endsWith("handover-narrow.png") || filename.endsWith("playing-narrow.png")
                || filename.endsWith("outro-narrow.png") || filename.endsWith("offline-narrow.png"));
        if (!visitorFocusName && !staffFinishName && !"doctor-staff-flow-staff.png".equals(filename)
            && !"doctor-staff-flow-equipment.png".equals(filename)
            && !"doctor-staff-flow-title.png".equals(filename)) {
            throw new AssertionError("Unexpected staffFlow screenshot name: " + filename);
        }
        if (!screenshotProbeChecked) {
            Bitmap probe = Bitmap.createBitmap(64, 64, Bitmap.Config.ARGB_8888);
            probe.eraseColor(android.graphics.Color.BLACK);
            check("screenshot detector rejects a blank image", !hasRenderedColors(probe));
            probe.setPixel(3, 3, android.graphics.Color.WHITE);
            probe.setPixel(5, 5, android.graphics.Color.RED);
            probe.setPixel(7, 7, android.graphics.Color.BLUE);
            check("screenshot detector finds sparse rendered pixels", hasRenderedColors(probe));
            probe.recycle();
            screenshotProbeChecked = true;
        }
        Bitmap screenshot = getUiAutomation().takeScreenshot();
        check("screenshot captured " + filename, screenshot != null);
        String destination = "/sdcard/" + filename;
        runShell("screencap -p " + destination);
        boolean rendered = hasRenderedColors(screenshot);
        screenshot.recycle();
        check("screenshot has rendered colors " + filename, rendered);
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

        ensureStaffScreen(resumedWebView);
        waitFor("staff briefing ready", 10000, () -> evaluateBoolean(resumedWebView,
            "!document.getElementById('staffBriefingBtn').disabled"));
        String staffButton = evaluateString(resumedWebView,
            "document.getElementById('staffBriefingBtn').textContent.trim()");
        evaluate(resumedWebView, "(function(){document.getElementById('staffBriefingBtn').click();return true;})()");
        waitFor("offline briefing preview", 5000, () -> evaluateBoolean(resumedWebView,
            "!document.getElementById('briefingView').hidden&&document.getElementById('subtitleTyped').textContent.length>0"));
        openVisitorControls(resumedWebView);
        String briefingTitle = evaluateString(resumedWebView,
            "document.getElementById('briefingTitle').textContent.trim()");
        String briefingMode = evaluateString(resumedWebView,
            "document.getElementById('briefingPanelStatus').textContent.trim()");
        check("staff preview control text", staffButton.contains("説明"));
        check("staff preview visible offline", !briefingTitle.isEmpty());
        check("staff preview mode text", briefingMode.contains("スタッフ確認"));
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
        ensureStaffScreen(view);
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
                evaluate(view, "document.getElementById('briefingResume').click()");
            }
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
        ensureStaffScreen(view);
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
        if (evaluateBoolean(view, "document.getElementById('briefingControls').hidden")) openVisitorControls(view);
        int position = readSceneCounter(view);
        for (; position < 12; position++) {
            int next = position + 1;
            if (evaluateBoolean(view, "document.getElementById('briefingControls').hidden")) openVisitorControls(view);
            evaluate(view, "document.getElementById('briefingNext').click()");
            waitFor("next sentence " + next, 5000, () -> readSceneCounter(view) == next);
            SystemClock.sleep(200);
        }
        if (evaluateBoolean(view, "document.getElementById('briefingControls').hidden")) openVisitorControls(view);
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
