using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using Kolkata.UI;
using ScamWYF.Modding.Core;

namespace ScamWYF.TikTokPocket
{
    internal static class CatalogHooks
    {
        private static FieldInfo definitions,apps,access;
        private static MethodInfo open,isUnlocked,currentDay,removeRuntime,monitorActive;
        private static Type productType;
        private static readonly List<KolkataOsController> controllers=new List<KolkataOsController>();
        private static readonly List<object> manifests=new List<object>();
        internal static void Install(Plugin plugin)
        {
            definitions=NativeApi.Field(typeof(KolkataOsController),"m_appDefinitions");
            apps=NativeApi.Field(typeof(KolkataOsController),"m_appsById");
            access=NativeApi.Field(typeof(KolkataOsController),"m_applicationAccess");
            open=AccessTools.Method(typeof(KolkataOsController),"OpenAppById",new[]{typeof(string)});
            removeRuntime=AccessTools.Method(typeof(KolkataOsController),"RemoveApplicationRuntime",new[]{NativeApi.Type("KolkataOsAppRuntime")});
            monitorActive=AccessTools.PropertyGetter(typeof(KolkataOsSingleton),"IsMonitorSessionActive");
            var manifestType=NativeApi.Type("KolkataOsApplicationStoreManifest");
            var accessType=NativeApi.Type("KolkataOsApplicationAccess");
            productType=NativeApi.Type("KolkataOsApplicationProductDefinition");
            isUnlocked=AccessTools.Method(accessType,"IsAppUnlocked",new[]{NativeApi.Type("KolkataOsAppDefinition"),typeof(int)});
            currentDay=AccessTools.PropertyGetter(NativeApi.Type("ScamazonUnlockSchedule"),"CurrentDay");
            if(open==null || isUnlocked==null || currentDay==null || removeRuntime==null || monitorActive==null) throw new InvalidOperationException("The game app catalog changed.");
            Patch(plugin,AccessTools.Method(typeof(KolkataOsController),"BuildDesktop"),"DesktopPrefix");
            Patch(plugin,AccessTools.Method(accessType,"Configure",new[]{manifestType}),"ManifestPrefix");
            Patch(plugin,AccessTools.Method(NativeApi.Type("KolkataOsApplicationStoreCatalog"),"SetManifest",new[]{manifestType}),"ManifestPrefix");
        }
        private static void Patch(Plugin plugin,MethodBase method,string prefix)
        {
            if(method==null || !PatchCoordinator.TryPatch(plugin,method,"register the purchased TikTok Pocket app",
                prefix:new HarmonyMethod(AccessTools.Method(typeof(CatalogHooks),prefix))))
                throw new InvalidOperationException("Could not register the TikTok Pocket app.");
        }
        private static void ManifestPrefix([HarmonyArgument(0)] object manifest)
        {
            if(Plugin.Current==null || manifest==null) return;
            if(!manifests.Contains(manifest)) manifests.Add(manifest);
            var products=NativeApi.Get(manifest,"products") as Array;
            var items=new List<object>();
            if(products!=null) foreach(var item in products) {items.Add(item);if(item!=null && (string)NativeApi.Get(item,"id")==Plugin.ProductId) return;}
            var product=NativeApi.New("KolkataOsApplicationProductDefinition");
            NativeApi.Set(product,"id",Plugin.ProductId);NativeApi.Set(product,"appId",Plugin.AppId);
            NativeApi.Set(product,"title","TikTok Pocket");NativeApi.Set(product,"description","The official TikTok website in a phone-shaped browser. Optional sign-in stays in a local browser profile.");
            NativeApi.Set(product,"iconPath",Plugin.Current.IconPath);NativeApi.Set(product,"iconText","TT");
            NativeApi.Set(product,"price",Plugin.Current.ShopPrice);NativeApi.Set(product,"sellPrice",0);NativeApi.Set(product,"ownership","permanent");NativeApi.Set(product,"sellable",false);
            NativeApi.Set(product,"category","functional");NativeApi.Set(product,"appCategory","fun");NativeApi.Set(product,"accessMode","purchase");NativeApi.Set(product,"unlockDay",1);
            NativeApi.Set(product,"accentColor","#FE2C55");NativeApi.Set(product,"scanDurationMultiplier",1f);NativeApi.Set(product,"removalDurationMultiplier",1f);
            items.Add(product);
            var merged=Array.CreateInstance(productType,items.Count);
            for(int i=0;i<items.Count;i++) merged.SetValue(items[i],i);
            NativeApi.Set(manifest,"products",merged);
        }
        private static void DesktopPrefix(KolkataOsController __instance)
        {
            if(Plugin.Current==null) return;
            if(!controllers.Contains(__instance)) controllers.Add(__instance);
            var list=definitions.GetValue(__instance) as IList;
            if(list==null) return;
            foreach(var existing in list) if(existing!=null && (string)NativeApi.Get(existing,"id")==Plugin.AppId) return;
            var app=NativeApi.New("KolkataOsAppDefinition");
            NativeApi.Set(app,"id",Plugin.AppId);NativeApi.Set(app,"title","TikTok Pocket");NativeApi.Set(app,"contentType","browser");NativeApi.Set(app,"iconPath",Plugin.Current.IconPath);
            NativeApi.Set(app,"pinned",false);NativeApi.Set(app,"showOnDesktop",true);NativeApi.Set(app,"desktopOrder",186);NativeApi.Set(app,"desktopAnchor","top-left");NativeApi.Set(app,"initialState","closed");
            NativeApi.Set(app,"windowLeft",40f);NativeApi.Set(app,"windowTop",40f);NativeApi.Set(app,"windowWidth",390f);NativeApi.Set(app,"windowHeight",680f);NativeApi.Set(app,"minWindowWidth",320f);NativeApi.Set(app,"minWindowHeight",480f);NativeApi.Set(app,"accentColor","#FE2C55");
            var appAccess=NativeApi.New("KolkataOsAppAccessDefinition");NativeApi.Set(appAccess,"mode","catalog");NativeApi.Set(appAccess,"productId",Plugin.ProductId);NativeApi.Set(appAccess,"unlockDay",1);NativeApi.Set(app,"access",appAccess);
            var browser=NativeApi.New("KolkataOsBrowserContent");NativeApi.Set(browser,"homeUrl","https://www.tiktok.com/");NativeApi.Set(browser,"targetAppId",Plugin.AppId);NativeApi.Set(browser,"defaultShortcuts",Array.CreateInstance(NativeApi.Type("KolkataOsBrowserShortcutDefinition"),0));
            var content=NativeApi.New("KolkataOsAppContent");NativeApi.Set(content,"browser",browser);NativeApi.Set(app,"content",content);list.Add(app);
        }
        internal static void OpenApp()
        {
            var desktop=KolkataOsSingleton.Instance;
            var controller=desktop==null || !(bool)monitorActive.Invoke(desktop,null)?null:desktop.Controller;
            if(controller!=null && controller.isActiveAndEnabled)
            {
                var runtime=apps.GetValue(controller) as IDictionary;
                var licensing=access.GetValue(controller);
                if(runtime==null || !runtime.Contains(Plugin.AppId) || licensing==null
                    || !(bool)isUnlocked.Invoke(licensing,new[]{NativeApi.Get(runtime[Plugin.AppId],"Definition"),currentDay.Invoke(null,null)}))
                {Plugin.Current.SetStatus("Buy TikTok Pocket in Scamazon first.");return;}
                open.Invoke(controller,new object[]{Plugin.AppId});return;
            }
            if(Plugin.Current!=null) Plugin.Current.SetStatus("Open the in-game computer first.");
        }
        internal static void Clear()
        {
            foreach(var controller in controllers) if(controller!=null && definitions!=null)
            {
                // Use the game's removal path to unregister the live icon, window and cached
                // runtime before our browser hooks are removed. A stale app must never reopen
                // inside the stock browser after the mod has been disabled.
                var runtime=apps==null?null:apps.GetValue(controller) as IDictionary;
                if(runtime!=null && runtime.Contains(Plugin.AppId) && removeRuntime!=null)
                    removeRuntime.Invoke(controller,new[]{runtime[Plugin.AppId]});
                var list=definitions.GetValue(controller) as IList;
                if(list!=null) for(int i=list.Count-1;i>=0;i--) if(list[i]!=null && (string)NativeApi.Get(list[i],"id")==Plugin.AppId) list.RemoveAt(i);
            }
            foreach(var manifest in manifests)
            {
                var items=new List<object>();
                var products=NativeApi.Get(manifest,"products") as Array;
                if(products!=null) foreach(var item in products) if(item==null || (string)NativeApi.Get(item,"id")!=Plugin.ProductId) items.Add(item);
                var remaining=Array.CreateInstance(productType,items.Count);for(int i=0;i<items.Count;i++) remaining.SetValue(items[i],i);NativeApi.Set(manifest,"products",remaining);
            }
            controllers.Clear();manifests.Clear();
        }
    }
}
