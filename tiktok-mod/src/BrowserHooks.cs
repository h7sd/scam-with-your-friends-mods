using System;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using Kolkata.UI;
using ScamWYF.Modding.Core;
using ScamWYF.Modding.Core.Ui;
using UnityEngine;
using UnityEngine.UIElements;
using VoltstroStudios.UnityWebBrowser.Input;

namespace ScamWYF.TikTokPocket
{
    internal static class BrowserHooks
    {
        private sealed class Session
        {
            internal object Builder;
            internal Component OriginalHost;
            internal PocketBrowserHost Host;
        }
        private static readonly List<Session> sessions=new List<Session>();
        private static FieldInfo builderHost,controllerHost,activeApp,inputHandler;
        private static MethodInfo monitorActive;
        internal static void Install(Plugin plugin)
        {
            var builder=NativeApi.Type("KolkataOsContentBuilder");var runtime=NativeApi.Type("KolkataOsAppRuntime");
            var host=NativeApi.Type("KolkataOsBrowserHost");
            builderHost=NativeApi.Field(builder,"m_browserHost");controllerHost=NativeApi.Field(typeof(KolkataOsController),"m_browserHost");
            activeApp=NativeApi.Field(typeof(KolkataOsController),"m_activeApp");inputHandler=NativeApi.Field(host,"inputHandler");
            monitorActive=AccessTools.PropertyGetter(typeof(KolkataOsSingleton),"IsMonitorSessionActive");
            if(monitorActive==null) throw new InvalidOperationException("The game computer interface changed.");
            Patch(plugin,AccessTools.Method(builder,"BuildBrowserContent",new[]{runtime}),"BuildPrefix");
            Patch(plugin,AccessTools.Method(host,"ApplyBrowserAppState",new[]{runtime}),"StatePrefix");
            Patch(plugin,AccessTools.Method(host,"IsInputActive",Type.EmptyTypes),"InputPrefix");
            Patch(plugin,AccessTools.Method(builder,"Dispose",Type.EmptyTypes),"BuilderDisposePrefix");
            Patch(plugin,AccessTools.Method(typeof(KolkataOsController),"OnDisable",Type.EmptyTypes),"ControllerDisablePrefix");
        }
        private static void Patch(Plugin plugin,MethodBase method,string prefix)
        {
            if(method==null || !PatchCoordinator.TryPatch(plugin,method,"display TikTok Pocket in its separate local video browser",prefix:new HarmonyMethod(AccessTools.Method(typeof(BrowserHooks),prefix))))
                throw new InvalidOperationException("Could not connect TikTok Pocket to the game computer.");
        }
        private static bool BuildPrefix(object __instance,[HarmonyArgument(0)] object app)
        {
            if(!NativeApi.Own(app)) return true;
            var content=NativeApi.Get(app,"WindowContent") as VisualElement;
            if(Plugin.Current==null || content==null) return false;
            foreach(var existing in sessions.ToArray()) if(ReferenceEquals(existing.Builder,__instance)) Remove(existing);
            PocketBrowserHost created=null;
            try
            {
                var original=builderHost.GetValue(__instance) as Component;
                KolkataOsController owner=null;
                foreach(var candidate in UnityEngine.Object.FindObjectsOfType<KolkataOsController>())
                    if(ReferenceEquals(controllerHost.GetValue(candidate),original)) {owner=candidate;break;}
                var handler=original==null?null:inputHandler.GetValue(original) as WebBrowserInputHandler;
                if(owner==null || handler==null) throw new InvalidOperationException();
                var gameObject=new GameObject("TikTok Pocket Video Browser");gameObject.hideFlags=HideFlags.HideInHierarchy;
                gameObject.transform.SetParent(owner.transform,false);created=gameObject.AddComponent<PocketBrowserHost>();
                created.Initialize(owner,app,handler,content);
                sessions.Add(new Session{Builder=__instance,OriginalHost=original,Host=created});
            }
            catch
            {
                if(created!=null) {created.DisposeBrowser();UnityEngine.Object.Destroy(created.gameObject);}
                content.Clear();content.Add(new Label("TikTok Pocket could not prepare its separate video browser. Reinstall the mod and restart the game."));
                Plugin.Current.SetStatus("TikTok Pocket could not prepare its separate video browser.");
            }
            return false;
        }
        // The stock CEF host must never receive a TikTok app/profile, including startup failures.
        private static bool StatePrefix(object __instance,[HarmonyArgument(0)] object app) {return !NativeApi.Own(app);}
        private static bool InputPrefix(object __instance,ref bool __result)
        {
            foreach(var session in sessions) if(ReferenceEquals(session.OriginalHost,__instance) && session.Host!=null
                && NativeApi.Own(activeApp.GetValue(session.Host.Controller))) {__result=false;return false;}
            return true;
        }
        internal static bool Visible(PocketBrowserHost host)
        {
            if(host==null || host.Controller==null || Plugin.Current==null) return false;
            var desktop=KolkataOsSingleton.Instance;
            return desktop!=null && desktop.Controller==host.Controller && (bool)monitorActive.Invoke(desktop,null)
                && NativeApi.Get(host.App,"State").ToString()=="OpenVisible";
        }
        internal static bool InputAllowed(PocketBrowserHost host)
        {
            return Visible(host) && Application.isFocused && !ModMenu.IsOpen && !FeedbackUi.IsOpen
                && ReferenceEquals(activeApp.GetValue(host.Controller),host.App);
        }
        internal static void CheckVideoCompatibility()
        {
            foreach(var session in sessions) if(session.Host!=null && Visible(session.Host)) {session.Host.Probe();return;}
            if(Plugin.Current!=null) Plugin.Current.SetStatus("Open TikTok Pocket on your computer before checking video compatibility.");
        }
        private static void BuilderDisposePrefix(object __instance)
        {foreach(var session in sessions.ToArray()) if(ReferenceEquals(session.Builder,__instance)) Remove(session);}
        private static void ControllerDisablePrefix(KolkataOsController __instance)
        {foreach(var session in sessions.ToArray()) if(session.Host!=null && session.Host.Controller==__instance) Remove(session);}
        private static void Remove(Session session)
        {
            sessions.Remove(session);if(session.Host==null) return;
            session.Host.DisposeBrowser();UnityEngine.Object.Destroy(session.Host.gameObject);
        }
        internal static void ResetSessions() {foreach(var session in sessions) if(session.Host!=null) session.Host.ResetProfile();}
        internal static void Clear() {foreach(var session in sessions.ToArray()) Remove(session);}
    }
}
