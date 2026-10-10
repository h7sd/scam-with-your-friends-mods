using System;
using BepInEx;
using BepInEx.Configuration;
using ScamWYF.Modding.Core;
using ScamWYF.Modding.Core.Ui;
using UnityEngine.UIElements;

namespace ScamWYF.RequestedPayout
{
    [BepInPlugin(PluginGuid, "Requested Payout", "1.1.3")]
    public sealed class Plugin : ScamMod
    {
        public const string PluginGuid = "com.community.scamwyf.requestedpayout";
        internal static Plugin Current;
        internal ConfigEntry<bool> PayoutEnabled;
        internal int AcceptedPrices;
        internal int RequestedPayouts;
        internal int LastAmount;
        internal string LastStatus = "Waiting for a clearly stated credit-card or gift-card service price.";

        protected override void OnModLoad()
        {
            Current = this;
            PayoutEnabled = Config.Bind("General", "Enabled", true,
                "Remember the latest clearly stated whole-number player service price. Credit cards pay the original reward plus that requested amount; gift cards pay the requested amount instead of the original reward. Payment still requires successful card/code verification in the game.");
            WatchConfig();
            PayoutHooks.Install(this);
            ModMenu.AddPage(this,"Requested Payout",BuildPage,-10);
            ModLog.LogInfo("Requested Payout: tracking requested amounts and applying credit-card/gift-card payouts on the server.");
        }
        protected override void OnModUnload()
        {
            PayoutHooks.Clear();
            if (Current == this) Current = null;
        }
        protected override void OnConfigReloaded()
        {
            if (!PayoutEnabled.Value) PayoutHooks.Clear();
            ModMenu.Refresh();
        }
        private void BuildPage(VisualElement page)
        {
            Widgets.Heading(page,"Requested Payout: Credit Card and Gift Card");
            Widgets.FieldRow(page,"Status",PayoutEnabled.Value ? "Enabled (Host / Server)" : "Disabled");
            Widgets.Paragraph(page,"The latest clearly stated whole-number service price is saved as the requested amount. After the fictional credit card is successfully verified, the game pays the original reward plus the requested amount: a 400 base reward and a 20,000 request pay 20,400. For gift cards, the requested amount replaces the original reward. Additional AI dialogue objectives are not required. If no amount is detected, the original reward applies. Credit cards, gift cards, and separate calls are tracked independently.");
            Widgets.FieldRow(page,"Amounts detected",AcceptedPrices.ToString());
            Widgets.FieldRow(page,"Payouts",RequestedPayouts.ToString());
            if (LastAmount > 0) Widgets.FieldRow(page,"Last requested amount",LastAmount.ToString());
            Widgets.Note(page,LastStatus);
            Widgets.Note(page,"Only positive whole units of in-game currency are accepted. Ambiguous, negative, and decimal amounts are ignored. Very large amounts must leave room for the call's other rewards. This mod does not log card values or dialogue.");
            ConfigEditor.Build(page);
        }
    }
}
