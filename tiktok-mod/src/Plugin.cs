using System;
using System.IO;
using BepInEx;
using BepInEx.Configuration;
using ScamWYF.Modding.Core;
using ScamWYF.Modding.Core.Ui;
using UnityEngine.UIElements;

namespace ScamWYF.TikTokPocket
{
    [BepInPlugin(PluginGuid,"TikTok Pocket","1.0.0")]
    public sealed class Plugin : ScamMod
    {
        public const string PluginGuid="com.community.scamwyf.tiktokpocket";
        internal const string AppId="tiktok-pocket",ProductId="tiktok-pocket-license";
        internal static Plugin Current;
        internal ConfigEntry<int> Price;
        internal ConfigEntry<bool> RememberSession;
        internal int ShopPrice;
        internal string Status="Buy TikTok Pocket in Scamazon's Games & Software shop.";
        internal string IconPath {get {return Path.Combine(Path.GetDirectoryName(Info.Location),"TikTokPocket","tiktok-pocket.png");}}
        internal string ProfilePath {get {return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),"ScamWYF-Mods","TikTokPocket",RememberSession.Value?"Profile":"Guest");}}
        protected override void OnModLoad()
        {
            Current=this;
            Price=Config.Bind("Shop","Price",1000,new ConfigDescription("Price in game currency. Changes apply after restarting the game; host and clients should use the same price.",new AcceptableValueRange<int>(1,2147483647)));
            ShopPrice=Price.Value;
            RememberSession=Config.Bind("Browser","RememberSession",true,"Keep TikTok's browser profile locally. Turn off for private browsing. TikTok may still require you to sign in again.");
            try {BrowserHooks.Install(this);CatalogHooks.Install(this);}
            catch {BrowserHooks.Clear();Current=null;throw;}
            WatchConfig();
            ModMenu.AddPage(this,"TikTok Pocket",BuildPage,-5);
            ModLog.LogInfo("TikTok Pocket: shop app and a separate local browser profile are ready.");
        }
        protected override void OnModUnload()
        {
            if(Current==this) Current=null;
            BrowserHooks.Clear();CatalogHooks.Clear();
        }
        protected override void OnConfigReloaded()
        {
            BrowserHooks.ResetSessions();ModMenu.Refresh();
        }
        internal void SetStatus(string message) {Status=message;}
        internal void ReportCompatibility(string message) {Status=message;ModLog.LogInfo(message);}
        private void BuildPage(VisualElement page)
        {
            Widgets.Heading(page,"TikTok Pocket");
            Widgets.Paragraph(page,"Buy the app in Scamazon, then browse the official TikTok website in a phone-shaped window while playing. TikTok handles sign-in in the browser; its profile is stored on this PC.");
            Widgets.FieldRow(page,"Shop price",ShopPrice.ToString()+" in-game currency");
            Widgets.Note(page,Status);
            var open=new Button(CatalogHooks.OpenApp){text="Open TikTok Pocket"};page.Add(open);
            var compatibility=new Button(BrowserHooks.CheckVideoCompatibility){text="Check video compatibility"};page.Add(compatibility);
            var guest=new Toggle("Private browsing (do not remember sign-in)"){value=!RememberSession.Value};
            guest.RegisterValueChangedCallback(evt=>{RememberSession.Value=!evt.newValue;BrowserHooks.ResetSessions();});page.Add(guest);
            Widgets.Note(page,"Browser sessions stay on this PC. Private browsing keeps sign-in temporary. TikTok may ask you to sign in again. Website availability and video playback depend on TikTok and the browser shipped with the game.");
            ConfigEditor.Build(page);
        }
    }
}
