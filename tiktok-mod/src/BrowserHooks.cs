using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using HarmonyLib;
using Kolkata.UI;
using ScamWYF.Modding.Core;
using ScamWYF.Modding.Core.Ui;
using UnityEngine;
using UnityEngine.UIElements;
using UnityEngine.InputSystem;
using VoltstroStudios.UnityWebBrowser.Core;
using VoltstroStudios.UnityWebBrowser.Input;
using VoltstroStudios.UnityWebBrowser.Logging;
using VoltstroStudios.UnityWebBrowser.Shared;

namespace ScamWYF.TikTokPocket
{
    internal static class BrowserHooks
    {
        private sealed class Session
        {
            internal KolkataOsController Controller;
            internal object OriginalBuilder,Builder,App;
            internal Component OriginalHost,Host;
            internal WebBrowserInputHandler Input;
            internal readonly List<InputAction> Actions=new List<InputAction>();
            internal bool Remember;
            internal string ProbeName;
        }
        private sealed class QuietLogger : IWebBrowserLogger
        {
            public void Debug(object message) {}
            public void Warn(object message) {}
            public void Error(object message) {}
        }
        private static readonly List<Session> sessions=new List<Session>();
        private static FieldInfo builderHost,controllerHost,activeApp,inputHandler,boundWorkspace,client,cachePath,initializationState,browserImage,currentUrl;
        private static MethodInfo build,dispose,configureInput,bindWorkspace,applyState,fail,stopInput;
        private static Type hostType;
        private static readonly MethodInfo clone=AccessTools.Method(typeof(object),"MemberwiseClone");
        internal static void Install(Plugin plugin)
        {
            var builderType=NativeApi.Type("KolkataOsContentBuilder");
            var runtimeType=NativeApi.Type("KolkataOsAppRuntime");
            hostType=NativeApi.Type("KolkataOsBrowserHost");
            builderHost=Required(builderType,"m_browserHost");
            controllerHost=Required(typeof(KolkataOsController),"m_browserHost");
            activeApp=Required(typeof(KolkataOsController),"m_activeApp");
            inputHandler=Required(hostType,"inputHandler");
            boundWorkspace=Required(hostType,"boundWorkspace");
            client=Required(hostType,"browserClient");
            cachePath=Required(typeof(WebBrowserClient),"cachePath");
            initializationState=Required(hostType,"browserInitializationState");
            browserImage=Required(hostType,"browserImage");
            currentUrl=Required(hostType,"currentUrl");
            build=AccessTools.Method(builderType,"BuildBrowserContent",new[]{runtimeType});
            dispose=AccessTools.Method(hostType,"DisposeBrowserClient",Type.EmptyTypes);
            stopInput=AccessTools.Method(hostType,"StopInput",Type.EmptyTypes);
            configureInput=AccessTools.Method(hostType,"ConfigureInputHandler",new[]{typeof(WebBrowserInputHandler)});
            bindWorkspace=AccessTools.Method(hostType,"BindWorkspace",new[]{typeof(ScammerWorkspace)});
            applyState=AccessTools.Method(hostType,"ApplyBrowserAppState",new[]{runtimeType});
            fail=AccessTools.Method(hostType,"FailBrowserInitialization",new[]{typeof(string),typeof(bool)});
            if(build==null || dispose==null || stopInput==null || configureInput==null || bindWorkspace==null || applyState==null || fail==null) throw new InvalidOperationException("The game's browser renderer changed.");
            Patch(plugin,build,"BuildPrefix",null);
            Patch(plugin,applyState,"StatePrefix",null);
            Patch(plugin,AccessTools.Method(hostType,"IsInputActive",Type.EmptyTypes),"InputPrefix",null);
            Patch(plugin,AccessTools.Method(hostType,"ReportReviewBrowserHistoryEntry",new[]{typeof(string)}),"HistoryPrefix",null);
            Patch(plugin,AccessTools.Method(hostType,"SaveCurrentPageShortcut",new[]{typeof(Action)}),"ShortcutPrefix",null);
            Patch(plugin,AccessTools.Method(typeof(WebBrowserClient),"Init",Type.EmptyTypes),"InitPrefix",null);
            Patch(plugin,AccessTools.Method(typeof(WebBrowserClient),"InvokeJsMethod"),"ProbePrefix",null);
            Patch(plugin,AccessTools.Method(builderType,"Dispose",Type.EmptyTypes),"BuilderDisposePrefix",null);
            Patch(plugin,AccessTools.Method(typeof(KolkataOsController),"OnDisable",Type.EmptyTypes),"ControllerDisablePrefix",null);
        }
        private static FieldInfo Required(Type type,string name)
        {
            var field=AccessTools.Field(type,name);
            if(field==null) throw new InvalidOperationException("The game's browser profile interface changed.");
            return field;
        }
        private static void Patch(Plugin plugin,MethodBase method,string prefix,string postfix)
        {
            if(method==null || !PatchCoordinator.TryPatch(plugin,method,"render TikTok Pocket with a separate local browser profile",
                prefix:prefix==null?null:new HarmonyMethod(AccessTools.Method(typeof(BrowserHooks),prefix)),
                postfix:postfix==null?null:new HarmonyMethod(AccessTools.Method(typeof(BrowserHooks),postfix))))
                throw new InvalidOperationException("Could not connect TikTok Pocket to the game's browser.");
        }
        private static bool Own(object app) {return NativeApi.Own(app);}
        private static bool BuildPrefix(object __instance,[HarmonyArgument(0)] object app)
        {
            if(Plugin.Current==null || !Own(app)) return true;
            foreach(var existing in sessions) if(ReferenceEquals(existing.Builder,__instance)) return true;
            Session session=null;
            foreach(var existing in sessions) if(ReferenceEquals(existing.OriginalBuilder,__instance)) {session=existing;break;}
            GameObject created=null;
            try
            {
                if(session==null)
                {
                    var original=builderHost.GetValue(__instance) as Component;
                    if(original==null) throw new InvalidOperationException();
                    KolkataOsController owner=null;
                    foreach(var candidate in UnityEngine.Object.FindObjectsOfType<KolkataOsController>())
                        if(ReferenceEquals(controllerHost.GetValue(candidate),original)) {owner=candidate;break;}
                    if(owner==null) throw new InvalidOperationException();
                    var handler=inputHandler.GetValue(original) as WebBrowserInputHandler;
                    if(handler==null) throw new InvalidOperationException();
                    var gameObject=new GameObject("TikTok Pocket Browser");created=gameObject;
                    gameObject.hideFlags=HideFlags.HideInHierarchy;
                    gameObject.transform.SetParent(owner.transform,false);
                    var host=gameObject.AddComponent(hostType);
                    session=new Session {Controller=owner,OriginalBuilder=__instance,OriginalHost=original,Host=host,App=app,Remember=Plugin.Current.RememberSession.Value};
                    session.Input=UnityEngine.Object.Instantiate(handler);
                    // InputActions are serialized managed objects; Unity cloning alone is not
                    // sufficient to guarantee that the two handlers own different action state.
                    for(Type inputType=handler.GetType();inputType!=null;inputType=inputType.BaseType)
                        foreach(var field in inputType.GetFields(BindingFlags.Public|BindingFlags.NonPublic|BindingFlags.Instance|BindingFlags.DeclaredOnly))
                            if(field.FieldType==typeof(InputAction))
                            {
                                var originalAction=field.GetValue(handler) as InputAction;
                                var ownedAction=originalAction==null?null:originalAction.Clone();
                                field.SetValue(session.Input,ownedAction);
                                if(ownedAction!=null) session.Actions.Add(ownedAction);
                            }
                    configureInput.Invoke(host,new object[]{session.Input});
                    // This browser is local media. It must not send website audio or history
                    // through the game's workspace/multiplayer call bridge.
                    bindWorkspace.Invoke(host,new object[]{null});
                    var builder=clone.Invoke(__instance,null);
                    builderHost.SetValue(builder,host);
                    // The stock controls target the workspace audio stream. TikTok plays locally
                    // through CEF and uses its own site's volume controls instead.
                    NativeApi.Set(builder,"m_getBrowserVolume",new Func<float>(()=>1f));
                    NativeApi.Set(builder,"m_setBrowserVolume",new Action<float>(value=>{}));
                    NativeApi.Set(builder,"m_browserVolumeSlider",null);
                    NativeApi.Set(builder,"m_browserVolumeButton",null);
                    session.Builder=builder;
                    sessions.Add(session);
                }
                session.App=app;
                build.Invoke(session.Builder,new object[]{app});
                var builtContent=NativeApi.Get(app,"WindowContent") as VisualElement;
                if(builtContent!=null)
                {
                    var toolbar=builtContent.Q<VisualElement>(className:"kolkata-browser-toolbar");
                    if(toolbar!=null) toolbar.style.display=DisplayStyle.None;
                    var statusbar=builtContent.Q<VisualElement>(className:"kolkata-browser-statusbar");
                    if(statusbar!=null) statusbar.style.display=DisplayStyle.None;
                    var controls=new VisualElement();controls.AddToClassList("tiktok-pocket-controls");
                    controls.style.flexDirection=FlexDirection.Row;
                    var current=session;
                    controls.Add(new Button(()=>Navigate(current,false)){text="Back"});
                    controls.Add(new Button(()=>Navigate(current,true)){text="Refresh"});
                    builtContent.Insert(0,controls);
                }
            }
            catch
            {
                if(session!=null) Remove(session);
                else if(created!=null) UnityEngine.Object.Destroy(created);
                var content=NativeApi.Get(app,"WindowContent") as VisualElement;
                if(content!=null) {content.Clear();content.Add(new Label("TikTok Pocket could not start its separate browser. The regular browser remains available."));}
                Plugin.Current.SetStatus("TikTok Pocket could not start its separate browser. Check that the game includes its browser engine.");
            }
            return false;
        }
        private static bool StatePrefix(object __instance,[HarmonyArgument(0)] object app)
        {
            if(!Own(app)) return true;
            foreach(var session in sessions)
            {
                if(ReferenceEquals(session.Host,__instance)) return true;
                if(ReferenceEquals(session.OriginalHost,__instance)) {applyState.Invoke(session.Host,new[]{app});return false;}
            }
            // The content builder creates the isolated host. Never initialize this app in the
            // regular browser while its content has not been built yet.
            return false;
        }
        private static bool InitPrefix(WebBrowserClient __instance)
        {
            foreach(var session in sessions) if(session.Host!=null && ReferenceEquals(client.GetValue(session.Host),__instance))
            {
                try
                {
                    if(Plugin.Current==null) return false;
                    string path=Plugin.Current.ProfilePath;
                    Directory.CreateDirectory(path);
                    cachePath.SetValue(__instance,new FileInfo(path));
                    __instance.cache=true;__instance.localStorage=true;
                    __instance.incognitoMode=!Plugin.Current.RememberSession.Value;
                    __instance.enableAudioForwarding=false;
                    __instance.remoteDebugging=false;__instance.remoteDebuggingPort=0;
                    __instance.remoteDebuggingAllowedOrigins=new string[0];
                    __instance.ignoreSslErrors=false;__instance.ignoreSslErrorsDomains=new string[0];
                    __instance.Logger=new QuietLogger();__instance.logSeverity=LogSeverity.Fatal;
                    __instance.processLogHandler=null;
                    session.Remember=Plugin.Current.RememberSession.Value;
                    Plugin.Current.SetStatus("TikTok Pocket uses a separate browser profile on this PC. Sign-in is optional and handled by TikTok.");
                    return true;
                }
                catch
                {
                    string message="The local TikTok browser profile could not be prepared. TikTok was not started in the regular browser profile.";
                    if(Plugin.Current!=null) Plugin.Current.SetStatus(message);
                    fail.Invoke(session.Host,new object[]{message,true});
                    return false;
                }
            }
            return true;
        }
        private static bool InputPrefix(object __instance,ref bool __result)
        {
            foreach(var session in sessions)
            {
                bool ownHost=ReferenceEquals(session.Host,__instance);
                if(!ownHost && !ReferenceEquals(session.OriginalHost,__instance)) continue;
                var active=session.Controller==null?null:activeApp.GetValue(session.Controller);
                if((ownHost && !Own(active)) || (!ownHost && Own(active))) {__result=false;return false;}
            }
            return true;
        }
        private static void Navigate(Session session,bool refresh)
        {
            var browser=session.Host==null?null:client.GetValue(session.Host) as WebBrowserClient;
            if(browser==null || browser.HasDisposed || !browser.IsConnected || !browser.ReadySignalReceived) return;
            if(refresh) browser.Refresh();else browser.GoBack();
        }
        internal static void CheckVideoCompatibility()
        {
            var desktop=KolkataOsSingleton.Instance;
            var controller=desktop==null?null:desktop.Controller;
            foreach(var session in sessions) if(session.Controller==controller && session.Host!=null)
            {
                var browser=client.GetValue(session.Host) as WebBrowserClient;
                if(browser==null || browser.HasDisposed || !browser.IsConnected || !browser.ReadySignalReceived) break;
                // A one-shot callback exposes no .NET actions to the page. Only bounded codec
                // booleans and the browser's numeric media error are accepted, never page data.
                session.ProbeName="tiktokPocketMedia_"+Guid.NewGuid().ToString("N");
                string script="(function(){var p=document.createElement('video'),v=document.querySelector('video');function c(t){return !!p.canPlayType(t);}uwb.ExecuteJsMethod('"+session.ProbeName+"',c('video/mp4; codecs=\"avc1.42E01E\"'),c('audio/mp4; codecs=\"mp4a.40.2\"'),c('video/webm; codecs=\"vp9,opus\"'),c('video/mp4; codecs=\"av01.0.05M.08\"'),!!v,v&&v.error?v.error.code:0);})();";
                try {browser.ExecuteJs(script);Plugin.Current.SetStatus("Checking video compatibility. No URLs, account data or video addresses are collected.");}
                catch {session.ProbeName=null;Plugin.Current.SetStatus("The video compatibility check could not run. Open TikTok Pocket and wait for the page to load.");}
                return;
            }
            if(Plugin.Current!=null) Plugin.Current.SetStatus("Open TikTok Pocket on your computer and wait for the page to load before checking video compatibility.");
        }
        private static bool ProbePrefix(WebBrowserClient __instance,[HarmonyArgument(0)] object message)
        {
            foreach(var session in sessions) if(session.Host!=null && ReferenceEquals(client.GetValue(session.Host),__instance)
                && session.ProbeName!=null && (string)JsProperty(message,"MethodName")==session.ProbeName)
            {
                session.ProbeName=null;
                var args=JsProperty(message,"Arguments") as Array;
                if(args==null || args.Length!=6) return false;
                var values=new object[6];
                for(int i=0;i<6;i++)
                {
                    var arg=args.GetValue(i);if(arg==null) return false;
                    values[i]=JsProperty(arg,"Value");
                    var nativeType=JsProperty(arg,"Type");string type=nativeType==null?null:nativeType.ToString();
                    if(i<5?(type!="Bool" || !(values[i] is bool)):(type!="Int" || !(values[i] is int))) return false;
                }
                int error=(int)values[5];if(error<0 || error>4) return false;
                if(Plugin.Current!=null)
                {
                    string result="Video compatibility: H.264="+Support((bool)values[0])+", AAC="+Support((bool)values[1])
                        +", WebM="+Support((bool)values[2])+", AV1="+Support((bool)values[3])
                        +"; video element="+((bool)values[4]?"present":"absent")+"; media error="+error+" (0 means no browser error reported).";
                    Plugin.Current.ReportCompatibility(result);ModMenu.Refresh();
                }
                return false;
            }
            return true;
        }
        private static object JsProperty(object target,string name)
        {
            return target==null?null:AccessTools.Property(target.GetType(),name).GetValue(target,null);
        }
        private static string Support(bool supported) {return supported?"supported":"unavailable";}
        private static bool HistoryPrefix(object __instance)
        {
            foreach(var session in sessions) if(ReferenceEquals(session.Host,__instance)) return false;
            return true;
        }
        private static bool ShortcutPrefix(object __instance,ref bool __result)
        {
            foreach(var session in sessions) if(ReferenceEquals(session.Host,__instance))
            {__result=false;if(Plugin.Current!=null) Plugin.Current.SetStatus("Browser shortcuts are disabled for TikTok Pocket. Use TikTok's own saved content.");return false;}
            return true;
        }
        private static void BuilderDisposePrefix(object __instance)
        {
            foreach(var session in sessions.ToArray()) if(ReferenceEquals(session.OriginalBuilder,__instance)) Remove(session);
        }
        private static void ControllerDisablePrefix(KolkataOsController __instance)
        {
            foreach(var session in sessions.ToArray()) if(ReferenceEquals(session.Controller,__instance)) Remove(session);
        }
        private static void Remove(Session session)
        {
            sessions.Remove(session);
            if(session.Host!=null)
            {
                stopInput.Invoke(session.Host,null);
                dispose.Invoke(session.Host,null);
                UnityEngine.Object.Destroy(session.Host.gameObject);
            }
            foreach(var action in session.Actions) action.Dispose();session.Actions.Clear();
            if(session.Input!=null) UnityEngine.Object.Destroy(session.Input);
        }
        internal static void ResetSessions()
        {
            if(Plugin.Current==null) return;
            foreach(var session in sessions) if(session.Host!=null && session.Remember!=Plugin.Current.RememberSession.Value)
            {
                stopInput.Invoke(session.Host,null);dispose.Invoke(session.Host,null);
                initializationState.SetValue(session.Host,Enum.ToObject(initializationState.FieldType,0));
                currentUrl.SetValue(session.Host,"https://www.tiktok.com/");
                var image=browserImage.GetValue(session.Host) as Image;if(image!=null) image.image=null;
                session.Remember=Plugin.Current.RememberSession.Value;
                applyState.Invoke(session.Host,new[]{session.App});
            }
        }
        internal static void Clear() {foreach(var session in sessions.ToArray()) Remove(session);}
    }
}
