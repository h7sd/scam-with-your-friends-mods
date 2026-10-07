using System;
using BepInEx;
using BepInEx.Configuration;
using ScamWYF.Modding.Core;
using ScamWYF.Modding.Core.Ui;
using UnityEngine.UIElements;

namespace ScamWYF.RequestedPayout
{
    [BepInPlugin(PluginGuid, "Wunschsumme", "1.1.0")]
    public sealed class Plugin : ScamMod
    {
        public const string PluginGuid = "com.community.scamwyf.requestedpayout";
        internal static Plugin Current;
        internal ConfigEntry<bool> PayoutEnabled;
        internal int AcceptedPrices;
        internal int RequestedPayouts;
        internal int LastAmount;
        internal string LastStatus = "Warte auf einen im Kreditkarten- oder Gift-Card-Scam ausdrücklich akzeptierten Preis.";

        protected override void OnModLoad()
        {
            Current = this;
            PayoutEnabled = Config.Bind("General", "Enabled", true,
                "Use an explicitly accepted whole-number credit-card or gift-card service price as the native success payout. No money is awarded for merely saying an amount.");
            WatchConfig();
            PayoutHooks.Install(this);
            ModMenu.AddPage(this,"Wunschsumme",BuildPage,-10);
            ModLog.LogInfo("Wunschsumme: native Preisbestätigung und serverseitige Kreditkarten-/Gift-Card-Auszahlung aktiv.");
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
            Widgets.Heading(page,"Wunschsumme: Kreditkarte und Gift Card");
            Widgets.FieldRow(page,"Status",PayoutEnabled.Value ? "Aktiv (Host / Server)" : "Deaktiviert");
            Widgets.Paragraph(page,"Erst wenn der Anrufer den Servicepreis akzeptiert und die fiktive Karte bzw. der Gift-Code erfolgreich in der Spiel-App geprüft wird, zahlt das Spiel den vereinbarten Betrag. Gift Cards haben dafür ein zusätzliches optionales Preisziel nach der Hilfs- und Lösungszusage. Ohne Preisvereinbarung bleibt die Originalbelohnung. Beträge werden pro Anruf und Scam getrennt gespeichert.");
            Widgets.FieldRow(page,"Bestätigte Preise",AcceptedPrices.ToString());
            Widgets.FieldRow(page,"Auszahlungen",RequestedPayouts.ToString());
            if (LastAmount > 0) Widgets.FieldRow(page,"Letzter akzeptierter Betrag",LastAmount.ToString());
            Widgets.Note(page,LastStatus);
            Widgets.Note(page,"Nur positive ganze Spielgeldeinheiten. Mehrdeutige, negative und Dezimalbeträge behalten die Originalbelohnung. Ein sehr großer Betrag muss Platz für die anderen nativen Anrufbelohnungen lassen. Kartenwerte und Dialoge werden von dieser Mod nicht protokolliert.");
            ConfigEditor.Build(page);
        }
    }
}
