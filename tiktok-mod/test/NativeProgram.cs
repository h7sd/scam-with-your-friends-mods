using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Reflection.Emit;
using System.Runtime.CompilerServices;
using System.Runtime.Serialization;
using HarmonyLib;
using Kolkata.UI;
using ScamWYF.TikTokPocket;
using UnityEngine;
using VoltstroStudios.UnityWebBrowser.Core;
using VoltstroStudios.UnityWebBrowser.Logging;
using VoltstroStudios.UnityWebBrowser.Shared;

internal static class NativeProgram
{
    private static string gameDirectory, jsonAssembly, taskDirectory;
    private static int passed, failed;
    private static Exception profileFailure;
    private static bool profileFailureReported;
    private static readonly Dictionary<string,int> preferences = new Dictionary<string,int>();

    private static int Main(string[] args)
    {
        gameDirectory = args[0]; jsonAssembly = args[1]; taskDirectory = args[2];
        AppDomain.CurrentDomain.AssemblyResolve += ResolveGameAssembly;
        Assembly.LoadFrom(jsonAssembly);
        try { return Run(); }
        catch (Exception ex) { Console.WriteLine("TIKTOK NATIVE HARNESS ERROR: " + ex); return 2; }
    }
    private static Assembly ResolveGameAssembly(object sender, ResolveEventArgs args)
    {
        string file = new AssemblyName(args.Name).Name + ".dll";
        if (file == "Newtonsoft.Json.dll") return Assembly.LoadFrom(jsonAssembly);
        foreach (string folder in new[] { "Scam With Your Friends_Data/Managed", "BepInEx/core" })
        {
            string path = Path.Combine(gameDirectory, folder, file);
            if (File.Exists(path)) return Assembly.LoadFrom(path);
        }
        return null;
    }
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static int Run()
    {
        Plugin.Current = new Plugin {
            IconPath = Path.Combine(taskDirectory, "tiktok-mod", "assets", "tiktok-pocket.png"),
            ProfilePath = Path.Combine(taskDirectory, "build", "tiktok-tests", "profiles", Guid.NewGuid().ToString("N")),
        };
        CatalogHooks.Install(Plugin.Current);
        TestNativeCatalogAndAccess();
        TestProfileInitialization();
        Console.WriteLine("TikTok native tests: " + passed + " passed, " + failed + " failed");
        return failed == 0 ? 0 : 1;
    }
    private static void TestNativeCatalogAndAccess()
    {
        object manifest = NativeApi.New("KolkataOsApplicationStoreManifest");
        object oldProduct = NativeApi.New("KolkataOsApplicationProductDefinition");
        NativeApi.Set(oldProduct, "id", "existing-fixture-product");
        NativeApi.Set(oldProduct, "appId", "existing-fixture-app");
        NativeApi.Set(oldProduct, "title", "Existing Product");
        NativeApi.Set(oldProduct, "price", 42);
        NativeApi.Set(oldProduct, "sellable", true);
        NativeApi.Set(oldProduct, "accessMode", "purchase");
        NativeApi.Set(oldProduct, "ownership", "permanent");
        Array products = Array.CreateInstance(oldProduct.GetType(), 1);
        products.SetValue(oldProduct, 0); NativeApi.Set(manifest, "products", products);
        var inject = AccessTools.Method(typeof(CatalogHooks), "ManifestPrefix");
        inject.Invoke(null, new[] { manifest });
        inject.Invoke(null, new[] { manifest });
        var injected = (Array)NativeApi.Get(manifest, "products");
        Check(injected.Length == 2, "manifest registration is idempotent and adds exactly one product");
        Check(ReferenceEquals(injected.GetValue(0), oldProduct) && (int)NativeApi.Get(oldProduct,"price") == 42
            && (bool)NativeApi.Get(oldProduct,"sellable"), "existing product identity, price and sellability are unchanged");
        object product = injected.GetValue(1);
        Check((string)NativeApi.Get(product,"id") == Plugin.ProductId && (string)NativeApi.Get(product,"appId") == Plugin.AppId,
            "native product links its unique license to the TikTok app");
        Check((int)NativeApi.Get(product,"price") == 1000, "native shop price is1000");
        Check(!(bool)NativeApi.Get(product,"sellable") && (int)NativeApi.Get(product,"sellPrice") == 0,
            "TikTok product cannot be sold for money");
        Check((string)NativeApi.Get(product,"ownership") == "permanent" && (string)NativeApi.Get(product,"accessMode") == "purchase",
            "TikTok product uses the native permanent purchase license");
        Check((string)NativeApi.Get(product,"iconPath") == Plugin.Current.IconPath && File.Exists(Plugin.Current.IconPath),
            "native product uses the supplied separate TikTok icon");

        // A managed storage-only controller fixture invokes the real registration prefix.
        // No GameObject, renderer or Unity scene is created.
        var controller = (KolkataOsController)FormatterServices.GetUninitializedObject(typeof(KolkataOsController));
        FieldInfo appDefinitions = NativeApi.Field(typeof(KolkataOsController),"m_appDefinitions");
        var appList = (IList)Activator.CreateInstance(appDefinitions.FieldType);
        object regular = NativeApi.New("KolkataOsAppDefinition");
        NativeApi.Set(regular,"id","browser"); NativeApi.Set(regular,"title","Existing Browser");
        NativeApi.Set(regular,"windowWidth",800f); NativeApi.Set(regular,"windowHeight",600f);
        object regularContent = NativeApi.New("KolkataOsAppContent"), regularBrowser = NativeApi.New("KolkataOsBrowserContent");
        NativeApi.Set(regularBrowser,"homeUrl","https://fixture.invalid/");
        NativeApi.Set(regularContent,"browser",regularBrowser); NativeApi.Set(regular,"content",regularContent);
        appList.Add(regular); appDefinitions.SetValue(controller,appList);
        var desktop = AccessTools.Method(typeof(CatalogHooks),"DesktopPrefix");
        desktop.Invoke(null,new object[]{controller});
        // Uninitialized Unity objects have no engine identity. Clear only fixture tracking
        // before replay; the same native definition list exercises the app duplicate guard.
        ((IList)AccessTools.Field(typeof(CatalogHooks),"controllers").GetValue(null)).Clear();
        desktop.Invoke(null,new object[]{controller});
        Check(appList.Count == 2, "desktop registration adds one distinct TikTok app idempotently");
        Check(ReferenceEquals(appList[0],regular) && (string)NativeApi.Get(regular,"title") == "Existing Browser"
            && (float)NativeApi.Get(regular,"windowWidth") == 800f && (float)NativeApi.Get(regular,"windowHeight") == 600f
            && ReferenceEquals(NativeApi.Get(regular,"content"),regularContent)
            && (string)NativeApi.Get(regularBrowser,"homeUrl") == "https://fixture.invalid/",
            "regular browser definition, dimensions, content and homepage stay unchanged");
        object app = appList[1];
        Check((string)NativeApi.Get(app,"id") == Plugin.AppId && (string)NativeApi.Get(app,"contentType") == "browser",
            "TikTok is its own browser app rather than replacing the regular browser");
        Check((float)NativeApi.Get(app,"windowWidth") == 390f && (float)NativeApi.Get(app,"windowHeight") == 680f,
            "native TikTok app has phone portrait dimensions390x680");
        Check((string)NativeApi.Get(app,"iconPath") == Plugin.Current.IconPath && (bool)NativeApi.Get(app,"showOnDesktop"),
            "TikTok desktop definition has its own supplied icon");
        object appAccess = NativeApi.Get(app,"access");
        Check((string)NativeApi.Get(appAccess,"mode") == "catalog" && (string)NativeApi.Get(appAccess,"productId") == Plugin.ProductId,
            "desktop app access uses the purchase catalog license");
        object browser = NativeApi.Get(NativeApi.Get(app,"content"),"browser");
        Check((string)NativeApi.Get(browser,"homeUrl") == "https://www.tiktok.com/"
            && (string)NativeApi.Get(browser,"targetAppId") == Plugin.AppId,
            "native browser content points to official TikTok and its own app");
        Type runtimeType=NativeApi.Type("KolkataOsAppRuntime");
        object regularRuntime=FormatterServices.GetUninitializedObject(runtimeType);
        object pocketRuntime=FormatterServices.GetUninitializedObject(runtimeType);
        NativeApi.Set(regularRuntime,"Definition",regular);NativeApi.Set(pocketRuntime,"Definition",app);
        Check(!NativeApi.Own(regularRuntime) && NativeApi.Own(pocketRuntime),
            "actual native runtime identities scope TikTok-only changes to its own app");
        Check((bool)AccessTools.Method(typeof(BrowserHooks),"StatePrefix").Invoke(null,new[]{new object(),regularRuntime}),
            "regular browser app state keeps its native path");
        Check(!(bool)AccessTools.Method(typeof(BrowserHooks),"StatePrefix").Invoke(null,new[]{new object(),pocketRuntime}),
            "TikTok without an isolated session cannot fall back to the regular browser host");

        // Native methods use an in-memory persistence adapter in this test process.
        // Actual PlayerPrefs or save files are never read or written.
        var environmental = new Harmony("scamwyf.tiktokpocket.independent-native-environment");
        Type accessType = NativeApi.Type("KolkataOsApplicationAccess");
        object access = Activator.CreateInstance(accessType,true);
        AccessTools.Method(accessType,"Configure").Invoke(access,new[]{manifest});
        Check(((ICollection<string>)AccessTools.Field(accessType,"m_runPurchases").GetValue(access)).Count == 0,
            "actual native Configure creates no run entitlements");
        Check(preferences.Count == 0, "manifest and native Configure do not write test ownership preferences");
        Check(((Array)AccessTools.Field(accessType,"m_products").GetValue(access)).Length == 2,
            "actual native Configure includes TikTok alongside the existing product");
        try
        {
        environmental.Patch(AccessTools.Method(accessType,"IsOwned"),
            transpiler:new HarmonyMethod(typeof(NativeProgram),"NoDiskSave"));
        environmental.Patch(AccessTools.Method(NativeApi.Type("KolkataOsApplicationStoreCatalog"),"IsAvailable"),
            transpiler:new HarmonyMethod(typeof(NativeProgram),"NoDiskSave"));
        environmental.Patch(AccessTools.Method(accessType,"ApplyServerTransaction"),
            transpiler:new HarmonyMethod(typeof(NativeProgram),"NoDiskSave"));
        var unlock = AccessTools.Method(accessType,"IsAppUnlocked");
        var owned = AccessTools.Method(accessType,"IsOwned");
        Check(!(bool)owned.Invoke(access,new object[]{Plugin.ProductId})
            && !(bool)unlock.Invoke(access,new object[]{app,1}), "catalog injection and Configure do not auto-grant TikTok access");
        AccessTools.Method(accessType,"ApplyServerTransaction").Invoke(access,
            new object[]{Plugin.ProductId,false,true,true,"Fixture rejected purchase"});
        Check(!(bool)unlock.Invoke(access,new object[]{app,1}), "native rejected purchase callback leaves TikTok locked");
        AccessTools.Method(accessType,"ApplyServerTransaction").Invoke(access,
            new object[]{Plugin.ProductId,true,true,true,"Fixture successful purchase"});
        Check((bool)owned.Invoke(access,new object[]{Plugin.ProductId})
            && (bool)unlock.Invoke(access,new object[]{app,1}), "native successful permanent purchase callback unlocks TikTok");
        Check(!(bool)owned.Invoke(access,new object[]{"existing-fixture-product"}), "TikTok purchase callback does not grant another product");
        }
        catch(Exception ex)
        {
            Exception cause = ex;
            while(cause.InnerException != null) cause = cause.InnerException;
            if(!(cause is System.Security.SecurityException) && !(cause is TypeLoadException)) throw;
            Console.WriteLine("SKIP: native purchase/access execution needs Unity engine calls unavailable in Windows CLR4 (" + cause.GetType().Name + "). No real PlayerPrefs were accessed.");
        }
    }
    private static void TestProfileInitialization()
    {
        try
        {
            // Copy the production prefix, adapting only Unity object's engine-backed null
            // comparison to a managed storage-holder comparison. Never invoke browser Init.
            Harmony.ReversePatch(AccessTools.Method(typeof(BrowserHooks),"InitPrefix"),
                new HarmonyMethod(typeof(NativeProgram),"ProfileInitCopy"),
                AccessTools.Method(typeof(NativeProgram),"ManagedHostComparison"),null);
            Type hostType = NativeApi.Type("KolkataOsBrowserHost");
            object host = FormatterServices.GetUninitializedObject(hostType);
            var ownedClient = (WebBrowserClient)FormatterServices.GetUninitializedObject(typeof(WebBrowserClient));
            var foreignClient = (WebBrowserClient)FormatterServices.GetUninitializedObject(typeof(WebBrowserClient));
            GC.SuppressFinalize(ownedClient); GC.SuppressFinalize(foreignClient);
            FieldInfo browserClient = NativeApi.Field(hostType,"browserClient");
            FieldInfo profile = AccessTools.Field(typeof(WebBrowserClient),"cachePath");
            browserClient.SetValue(host,ownedClient);
            AccessTools.Field(typeof(BrowserHooks),"client").SetValue(null,browserClient);
            AccessTools.Field(typeof(BrowserHooks),"cachePath").SetValue(null,profile);
            AccessTools.Field(typeof(BrowserHooks),"fail").SetValue(null,AccessTools.Method(typeof(NativeProgram),"ReportProfileFailure"));
            Type sessionType = typeof(BrowserHooks).GetNestedType("Session",BindingFlags.NonPublic);
            object session = Activator.CreateInstance(sessionType,true);
            AccessTools.Field(sessionType,"Host").SetValue(session,host);
            var sessions = (IList)AccessTools.Field(typeof(BrowserHooks),"sessions").GetValue(null);
            sessions.Add(session);
            ownedClient.enableAudioForwarding=true; ownedClient.remoteDebugging=true;
            ownedClient.remoteDebuggingPort=9222; ownedClient.ignoreSslErrors=true;
            foreignClient.remoteDebugging=true; foreignClient.ignoreSslErrors=true;
            foreignClient.enableAudioForwarding=true;
            Plugin.Current.RememberSession.Value=true;
            bool allowed=ProfileInitCopy(ownedClient);
            if(!allowed && profileFailure==null) throw new InvalidOperationException("Owned profile failed before the instrumented native logger call.");
            if(allowed) Check(true, "production profile prefix accepts its exact owned browser client");
            else
            {
                Exception cause=profileFailure;
                while(cause.InnerException!=null) cause=cause.InnerException;
                if(!(cause is System.Security.SecurityException) && !(cause is TypeLoadException)) throw profileFailure;
                Check(profileFailureReported,"production prefix rejects Init and reports preparation failure when native initialization is unavailable");
                Console.WriteLine("SKIP: owned client logger/native initialization requires Unity profiler calls ("+cause.GetType().Name+"). Earlier privacy settings are checked; no browser process was started.");
            }
            Check(((FileInfo)profile.GetValue(ownedClient)).FullName == Path.GetFullPath(Plugin.Current.ProfilePath)
                && Directory.Exists(Plugin.Current.ProfilePath), "owned client profile is assigned before Init in a test-only isolated directory");
            Check(!ownedClient.incognitoMode && ownedClient.localStorage, "remembered session uses persistent local storage mode");
            Check(!ownedClient.enableAudioForwarding, "owned TikTok audio is not forwarded to multiplayer");
            Check(!ownedClient.remoteDebugging && ownedClient.remoteDebuggingPort==0
                && ownedClient.remoteDebuggingAllowedOrigins.Length==0, "owned profile disables remote debugging and allowed origins");
            Check(!ownedClient.ignoreSslErrors && ownedClient.ignoreSslErrorsDomains.Length==0,
                "owned profile enforces TLS validation");
            if(allowed) Check(ownedClient.logSeverity==LogSeverity.Fatal && ownedClient.processLogHandler==null,
                "owned profile suppresses process logging metadata");
            Check(ProfileInitCopy(foreignClient) && profile.GetValue(foreignClient)==null && foreignClient.remoteDebugging
                && foreignClient.ignoreSslErrors && foreignClient.enableAudioForwarding,
                "foreign regular browser client profile and settings are untouched");
            Plugin.Current.RememberSession.Value=false;
            ProfileInitCopy(ownedClient);
            Check(ownedClient.incognitoMode,
                "private browsing is assigned to the exact owned client before initialization");
            sessions.Clear();
        }
        catch(Exception ex)
        {
            Exception cause=ex;
            while(cause.InnerException!=null) cause=cause.InnerException;
            if(!(cause is System.Security.SecurityException) && !(cause is TypeLoadException)) throw;
            Console.WriteLine("SKIP: exact-owned browser profile execution is unavailable outside Unity ("+cause.GetType().Name+": "+cause.Message+"). No browser process or account/profile was opened.");
        }
    }
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static bool ProfileInitCopy(WebBrowserClient client) {throw new NotImplementedException("Reverse patch stand-in");}
    private static void ReportProfileFailure(string message,bool stop) {profileFailureReported=true;}
    private static void SetLoggerMeasured(WebBrowserClient client,IWebBrowserLogger logger)
    {
        try {client.Logger=logger;}
        catch(Exception ex) {profileFailure=ex;throw;}
    }
    private static bool ManagedNotEqual(UnityEngine.Object left,UnityEngine.Object right) {return !ReferenceEquals(left,right);}
    private static IEnumerable<CodeInstruction> ManagedHostComparison(IEnumerable<CodeInstruction> instructions)
    {
        MethodInfo comparison=AccessTools.Method(typeof(UnityEngine.Object),"op_Inequality");
        MethodInfo logger=AccessTools.PropertySetter(typeof(WebBrowserClient),"Logger");
        foreach(var instruction in instructions)
        {
            if(instruction.opcode==OpCodes.Call && Equals(instruction.operand,comparison))
                yield return new CodeInstruction(OpCodes.Call,AccessTools.Method(typeof(NativeProgram),"ManagedNotEqual"));
            else if((instruction.opcode==OpCodes.Call || instruction.opcode==OpCodes.Callvirt) && Equals(instruction.operand,logger))
                yield return new CodeInstruction(OpCodes.Call,AccessTools.Method(typeof(NativeProgram),"SetLoggerMeasured"));
            else yield return instruction;
        }
    }
    private static int PrefsGet(string key, int defaultValue)
    {
        int value; return preferences.TryGetValue(key,out value) ? value : defaultValue;
    }
    private static void PrefsSet(string key, int value) {preferences[key]=value;}
    private static IEnumerable<CodeInstruction> NoDiskSave(IEnumerable<CodeInstruction> instructions)
    {
        MethodInfo save = AccessTools.Method(typeof(PlayerPrefs),"Save");
        MethodInfo get = AccessTools.Method(typeof(PlayerPrefs),"GetInt",new[]{typeof(string),typeof(int)});
        MethodInfo set = AccessTools.Method(typeof(PlayerPrefs),"SetInt",new[]{typeof(string),typeof(int)});
        foreach(var instruction in instructions) {
            if(instruction.opcode == OpCodes.Call && Equals(instruction.operand,save)) yield return new CodeInstruction(OpCodes.Nop);
            else if(instruction.opcode == OpCodes.Call && Equals(instruction.operand,get)) yield return new CodeInstruction(OpCodes.Call,AccessTools.Method(typeof(NativeProgram),"PrefsGet"));
            else if(instruction.opcode == OpCodes.Call && Equals(instruction.operand,set)) yield return new CodeInstruction(OpCodes.Call,AccessTools.Method(typeof(NativeProgram),"PrefsSet"));
            else yield return instruction;
        }
    }
    private static void Check(bool condition,string message)
    {
        if(condition){passed++;return;} failed++;Console.WriteLine("FAIL: "+message);
    }
}
