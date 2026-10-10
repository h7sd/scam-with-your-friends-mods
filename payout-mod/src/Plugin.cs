using System;
using BepInEx;
using BepInEx.Configuration;
using ScamWYF.Modding.Core;
using ScamWYF.Modding.Core.Ui;
using UnityEngine.UIElements;

namespace ScamWYF.RequestedPayout
{
    [BepInPlugin(PluginGuid, "Wunschsumme", "1.1.2")]
    public sealed class Plugin : ScamMod
    {
        public const string PluginGuid = "com.community.scamwyf.requestedpayout";
        internal static Plugin Current;
        internal ConfigEntry<bool> PayoutEnabled;
        internal int AcceptedPrices;
        internal int RequestedPayouts;
        internal int LastAmount;
        internal string LastStatus = "Warte auf einen klar genannten Wunschbetrag für Kreditkarte oder Gift Card.";

        protected override void OnModLoad()
        {
            Current = this;
            PayoutEnabled = Config.Bind("General", "Enabled", true,
                "Remember the latest clearly stated whole-number player service price. Credit cards pay the original reward plus that requested amount; gift cards pay the requested amount instead of the original reward. Payment still requires successful native card/code verification.");
            WatchConfig();
            PayoutHooks.Install(this);
            ModMenu.AddPage(this,"Wunschsumme",BuildPage,-10);
            ModLog.LogInfo("Wunschsumme: Wunschbetrag-Erkennung und serverseitige Kreditkarten-/Gift-Card-Auszahlung aktiv.");
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
            Widgets.Paragraph(page,"Der zuletzt klar von dir genannte ganze Servicepreis wird als Wunschbetrag gespeichert. Nach erfolgreicher Prüfung der fiktiven Kreditkarte zahlt das Spiel Originalbelohnung plus Wunschbetrag: bei 400 Originalbelohnung und 20.000 Wunschbetrag also 20.400. Bei Gift Cards ersetzt der Wunschbetrag weiterhin die Originalbelohnung. Zusätzliche KI-Gesprächsziele sind dafür nicht nötig. Ohne erkannten Betrag bleibt die Originalbelohnung. Kreditkarte und Gift Card sowie verschiedene Anrufe bleiben getrennt.");
            Widgets.FieldRow(page,"Erkannte Wunschbeträge",AcceptedPrices.ToString());
            Widgets.FieldRow(page,"Auszahlungen",RequestedPayouts.ToString());
            if (LastAmount > 0) Widgets.FieldRow(page,"Letzter Wunschbetrag",LastAmount.ToString());
            Widgets.Note(page,LastStatus);
            Widgets.Note(page,"Nur positive ganze Spielgeldeinheiten. Mehrdeutige, negative und Dezimalbeträge werden ignoriert. Ein sehr großer Betrag muss Platz für die anderen nativen Anrufbelohnungen lassen. Kartenwerte und Dialoge werden von dieser Mod nicht protokolliert.");
            ConfigEditor.Build(page);
        }
    }
}
