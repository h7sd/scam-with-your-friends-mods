using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using HarmonyLib;
using Newtonsoft.Json.Linq;
using ScamWYF.RequestedPayout;

internal static class NativeProgram
{
    private static string gameDirectory, jsonAssembly;
    private static int passed, failed;
    private const string CardTag = "payout-test-card";
    private const string CardValue = "fictional-test-card-value";
    private const string GiftTag = "FICTIONAL_GIFT_CARD_CODE";
    private const string GiftValue = "fictional-gift-test-code";

    private static int Main(string[] args)
    {
        gameDirectory = args[0];
        jsonAssembly = args[1];
        AppDomain.CurrentDomain.AssemblyResolve += ResolveGameAssembly;
        Assembly.LoadFrom(jsonAssembly);
        try { return Run(); }
        catch (Exception ex) { Console.WriteLine("NATIVE HARNESS ERROR: " + ex); return 2; }
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
        Plugin.Current = new Plugin();
        PayoutHooks.Install(Plugin.Current);

        // The fixture uses a made-up tag. Suppress only its asset catalog lookup;
        // native reveal/RDP/permission/submission matching and payout guards execute.
        new Harmony("scamwyf.requestedpayout.independent-native-fixture").Patch(
            AccessTools.Method(typeof(SentinelValueFactory), "GetDefinition"),
            prefix: new HarmonyMethod(typeof(NativeProgram), "SyntheticCardDefinition"));

        TestNativeSubmission();
        TestQuoteEvidence();
        TestGiftCardSubmission();
        TestGiftPriceObservation();
        Console.WriteLine("Payout native tests: " + passed + " passed, " + failed + " failed");
        return failed == 0 ? 0 : 1;
    }

    private static bool SyntheticCardDefinition(string tag, ref SentinelDefinition __result)
    {
        if (tag != CardTag && tag != GiftTag) return true;
        __result = null;
        return false;
    }

    private static ConversationScamSession Session(out ConversationScamDefinition card,
        int otherReward = 100, bool priceComplete = true, bool serviceComplete = true)
    {
        card = CreditDefinition();
        var other = new ConversationScamDefinition { id = "other", appId = "other", reward = otherReward, objectives = new ConversationScamObjective[0] };
        var session = new ConversationScamSession(new[] { card, other }, new FixtureDetector());
        var completed = Completed(session, card.id);
        if (priceComplete) completed.Add("price");
        if (serviceComplete) completed.Add("service");
        return session;
    }

    private static ConversationScamDefinition CreditDefinition()
    {
        return new ConversationScamDefinition {
            id = "credit-card", appId = "credit-card", productId = "credit-card", reward = 200,
            objectives = new[] {
                new ConversationScamObjective { id = "service" },
                new ConversationScamObjective { id = "price" },
                new ConversationScamObjective { id = "card", final = true, requiresSubmission = true, requiredSentinel = CardTag },
            },
        };
    }

    private static HashSet<string> Completed(ConversationScamSession session, string id)
    {
        IDictionary progress = (IDictionary)AccessTools.Field(typeof(ConversationScamSession), "progress").GetValue(session);
        object state = progress[id];
        return (HashSet<string>)AccessTools.Field(state.GetType(), "Completed").GetValue(state);
    }

    private static SentinelCallState Caller(bool revealed = true, bool rdp = false)
    {
        var caller = new SentinelCallState();
        var info = new SentinelInfo { Tag = CardTag, RawValue = CardValue, IsRevealed = revealed, IsRdpLoot = rdp };
        var infos = (IDictionary)AccessTools.Field(typeof(SentinelCallState), "infosByTag").GetValue(caller);
        infos.Add(CardTag, info);
        return caller;
    }

    private static PriceBook Prices()
    {
        return (PriceBook)AccessTools.Field(typeof(PayoutHooks), "prices").GetValue(null);
    }

    private static void TestNativeSubmission()
    {
        PayoutHooks.Clear();
        ConversationScamDefinition card;
        var session = Session(out card);
        Prices().Record(session, 1, 5000);
        int moneyAtChanged = -1;
        session.Changed += () => moneyAtChanged = session.MoneyEarned;
        var result = session.SubmitField("credit-card", CardTag, CardValue, Caller(), true);
        Check(result.PayoutAwarded == 5000 && session.MoneyEarned == 5000, "native success result and Progress.MoneyEarned both use 5000");
        Check(moneyAtChanged == 5000, "native Changed subscribers see requested amount before result returns");
        Check(card.reward == 200, "shared native catalog reward remains unchanged");
        result = session.SubmitField("credit-card", CardTag, CardValue, Caller(), true);
        Check(result.PayoutAwarded == 0 && session.MoneyEarned == 5000, "repeat SubmitField pays nothing and keeps existing progress");

        var otherCall = Session(out card);
        Prices().Record(otherCall, 1, 7500);
        result = otherCall.SubmitField("credit-card", CardTag, CardValue, Caller(), true);
        Check(result.PayoutAwarded == 7500 && otherCall.MoneyEarned == 7500 && session.MoneyEarned == 5000, "native parallel sessions keep separate reward and progress");

        TestFailure("card mismatch", CardTag, "wrong-card", Caller(), true);
        TestFailure("unauthorized caller", CardTag, CardValue, Caller(), false);
        TestFailure("unrevealed card", CardTag, CardValue, Caller(false), true);
        TestFailure("RDP card excluded", CardTag, CardValue, Caller(true, true), true);
        TestFailure("wrong field tag", "wrong-tag", CardValue, Caller(), true);
        TestFailure("missing caller", CardTag, CardValue, null, true);

        var aborted = Session(out card);
        Prices().Record(aborted, 1, 6000);
        aborted.Dispose();
        int amount;
        Check(!Prices().TryGet(aborted, out amount), "native Dispose removes price entry");
        result = aborted.SubmitField("credit-card", CardTag, CardValue, Caller(), true);
        Check(result.PayoutAwarded == 0 && aborted.MoneyEarned == 0, "disposed call cannot pay even with valid submitted card");

        var noAcceptedPrice = Session(out card, priceComplete: false);
        Prices().Record(noAcceptedPrice, 1, 9000);
        result = noAcceptedPrice.SubmitField("credit-card", CardTag, CardValue, Caller(), true);
        Check(result.PayoutAwarded == 200 && noAcceptedPrice.MoneyEarned == 200, "without native explicit price acceptance only original reward is used");

        var noService = Session(out card, serviceComplete: false);
        Prices().Record(noService, 1, 9000);
        result = noService.SubmitField("credit-card", CardTag, CardValue, Caller(), true);
        Check(result.PayoutAwarded == 200 && noService.MoneyEarned == 200, "without native service objective only original reward is used");

        var disabled = Session(out card);
        Prices().Record(disabled, 1, 9000);
        Plugin.Current.PayoutEnabled.Value = false;
        result = disabled.SubmitField("credit-card", CardTag, CardValue, Caller(), true);
        Check(result.PayoutAwarded == 200 && disabled.MoneyEarned == 200, "disabled mod preserves original payout");
        Plugin.Current.PayoutEnabled.Value = true;

        var nearLimit = Session(out card, otherReward: 100);
        Prices().Record(nearLimit, 1, int.MaxValue);
        result = nearLimit.SubmitField("credit-card", CardTag, CardValue, Caller(), true);
        Check(result.PayoutAwarded == 200 && nearLimit.MoneyEarned == 200, "unsafe native int total retains original reward");

        var safeLimit = Session(out card, otherReward: 100);
        Prices().Record(safeLimit, 1, int.MaxValue - 100);
        result = safeLimit.SubmitField("credit-card", CardTag, CardValue, Caller(), true);
        Check(result.PayoutAwarded == int.MaxValue - 100 && safeLimit.MoneyEarned == int.MaxValue - 100, "safe maximum reserves remaining native rewards");
    }

    private static void TestFailure(string name, string tag, string value, SentinelCallState caller, bool authorized)
    {
        ConversationScamDefinition card;
        var session = Session(out card);
        Prices().Record(session, 1, 5000);
        var result = session.SubmitField("credit-card", tag, value, caller, authorized);
        Check(result.PayoutAwarded == 0 && session.MoneyEarned == 0, name + " produces no payout or progress money");
    }

    private static ConversationScamSession GiftSession(out ConversationScamDefinition card,
        out ConversationScamDefinition gift, out ConversationScamDefinition inputGift,
        bool agreed = true, int[] tiers = null, IConversationScamDetector detector = null)
    {
        card = CreditDefinition();
        inputGift = new ConversationScamDefinition {
            id = "gift-card", appId = "gift-card", productId = "gift-card", reward = 200, rewardTiers = tiers,
            objectives = new[] {
                new ConversationScamObjective { id = "pitch" },
                new ConversationScamObjective { id = "solution", prerequisites = new[] { "pitch" } },
                new ConversationScamObjective { id = "code", final = true, requiresSubmission = true,
                    requiredSentinel = GiftTag, prerequisites = new[] { "pitch", "solution" } },
                new ConversationScamObjective { id = "bonus", optional = true },
            },
        };
        var session = new ConversationScamSession(new[] { card, inputGift }, detector ?? new FixtureDetector());
        gift = null;
        foreach (var definition in (IReadOnlyList<ConversationScamDefinition>)AccessTools.Field(typeof(ConversationScamSession), "catalog").GetValue(session))
            if (definition.id == "gift-card") gift = definition;
        Completed(session, "credit-card").Add("service");
        Completed(session, "credit-card").Add("price");
        Completed(session, "gift-card").Add("pitch");
        Completed(session, "gift-card").Add("solution");
        if (agreed) Completed(session, "gift-card").Add("requested-price");
        return session;
    }

    private static SentinelCallState GiftCaller(bool revealed = true, bool rdp = false)
    {
        var caller = new SentinelCallState();
        var infos = (IDictionary)AccessTools.Field(typeof(SentinelCallState), "infosByTag").GetValue(caller);
        infos.Add(GiftTag, new SentinelInfo { Tag = GiftTag, RawValue = GiftValue, IsRevealed = revealed, IsRdpLoot = rdp });
        infos.Add(CardTag, new SentinelInfo { Tag = CardTag, RawValue = CardValue, IsRevealed = revealed, IsRdpLoot = rdp });
        return caller;
    }

    private static void ScamRecord(ConversationScamSession session, string scam, int sequence, int amount)
    {
        MethodInfo record = typeof(PriceBook).GetMethod("Record", BindingFlags.Instance | BindingFlags.NonPublic,
            null, new[] { typeof(object), typeof(string), typeof(int), typeof(int) }, null);
        if (record == null) throw new MissingMethodException("PriceBook.Record per-scam API");
        record.Invoke(Prices(), new object[] { session, scam, sequence, amount });
    }

    private static void TestGiftCardSubmission()
    {
        PayoutHooks.Clear();
        ConversationScamDefinition card, gift, inputGift;
        var session = GiftSession(out card, out gift, out inputGift);
        Check(!ReferenceEquals(gift, inputGift), "gift catalog definition is cloned for this session");
        Check(inputGift.objectives.Length == 4, "shared catalog receives no new gift-price objective");
        ConversationScamObjective price = null, code = null;
        foreach (var objective in gift.objectives) {
            if (objective.id == "requested-price") price = objective;
            if (objective.id == "code") code = objective;
        }
        Check(price != null && price.optional && !price.final && !price.requiresSubmission,
            "gift requested-price objective stays optional and non-final");
        Check(price != null && price.prerequisites.Length == 2 && Array.IndexOf(price.prerequisites, "pitch") >= 0
            && Array.IndexOf(price.prerequisites, "solution") >= 0, "gift price requires native pitch and solution");
        Check(code.prerequisites.Length == 2 && Array.IndexOf(code.prerequisites, "requested-price") < 0,
            "native code completion does not require optional requested-price");
        Check(!ReferenceEquals(code, inputGift.objectives[2]) && !ReferenceEquals(code.prerequisites, inputGift.objectives[2].prerequisites),
            "session clone owns its code objective and prerequisite array");

        ScamRecord(session, "credit-card", 1, 5000);
        ScamRecord(session, "gift-card", 2, 7500);
        ScamRecord(session, "gift-card", 3, 9000);
        ScamRecord(session, "credit-card", 4, 2000);
        int observedMoney = -1;
        session.Changed += () => observedMoney = session.MoneyEarned;
        var result = session.SubmitField("gift-card", GiftTag, "wrong-code", GiftCaller(), true);
        Check(result.PayoutAwarded == 0 && session.MoneyEarned == 0, "wrong gift code pays nothing");
        result = session.SubmitField("gift-card", GiftTag, GiftValue, GiftCaller(), true);
        Check(result.PayoutAwarded == 7500 && session.MoneyEarned == 7500 && observedMoney == 7500,
            "valid gift code after failed attempt pays its first agreed 7500 consistently");
        result = session.SubmitField("gift-card", GiftTag, GiftValue, GiftCaller(), true);
        Check(result.PayoutAwarded == 0 && session.MoneyEarned == 7500, "same gift code is idempotent");
        result = session.SubmitField("credit-card", CardTag, CardValue, GiftCaller(), true);
        Check(result.PayoutAwarded == 5000 && session.MoneyEarned == 12500 && observedMoney == 12500,
            "same call credit5000 and gift7500 use separate agreed amounts");
        Check(card.reward == 200 && gift.reward == 200 && inputGift.reward == 200,
            "requested payouts leave both original catalog rewards unchanged");

        TestGiftFailure("unrevealed gift code", GiftTag, GiftValue, GiftCaller(false), true);
        TestGiftFailure("RDP gift code", GiftTag, GiftValue, GiftCaller(true, true), true);
        TestGiftFailure("wrong gift field", "wrong-tag", GiftValue, GiftCaller(), true);
        TestGiftFailure("unauthorized gift code", GiftTag, GiftValue, GiftCaller(), false);
        TestGiftFailure("missing gift caller", GiftTag, GiftValue, null, true);

        var noPrice = GiftSession(out card, out gift, out inputGift, agreed: false);
        ScamRecord(noPrice, "gift-card", 1, 7500);
        result = noPrice.SubmitField("gift-card", GiftTag, GiftValue, GiftCaller(), true);
        Check(result.PayoutAwarded == 200 && noPrice.MoneyEarned == 200,
            "gift code without optional price confirmation keeps native reward");
        var tiered = GiftSession(out card, out gift, out inputGift, tiers: new[] { 200, 400, 800 });
        ScamRecord(tiered, "gift-card", 1, 7500);
        result = tiered.SubmitField("gift-card", GiftTag, GiftValue, GiftCaller(), true);
        Check(result.PayoutAwarded == 7500 && tiered.MoneyEarned == 7500 && gift.rewardTiers[2] == 800 && inputGift.rewardTiers[2] == 800,
            "gift reward tiers remain intact while accepted price controls native verified payout");
        Check(!ReferenceEquals(gift.rewardTiers, inputGift.rewardTiers), "gift session owns a separate reward-tier array");
        var parallel = GiftSession(out card, out gift, out inputGift);
        ScamRecord(parallel, "gift-card", 1, 6000);
        result = parallel.SubmitField("gift-card", GiftTag, GiftValue, GiftCaller(), true);
        Check(result.PayoutAwarded == 6000 && parallel.MoneyEarned == 6000 && session.MoneyEarned == 12500,
            "separate call gift6000 cannot overwrite first call credit5000 and gift7500");
        var disabledGift = GiftSession(out card, out gift, out inputGift);
        ScamRecord(disabledGift, "gift-card", 1, 7500);
        Plugin.Current.PayoutEnabled.Value = false;
        result = disabledGift.SubmitField("gift-card", GiftTag, GiftValue, GiftCaller(), true);
        Check(result.PayoutAwarded == 200 && disabledGift.MoneyEarned == 200,
            "disabled mod preserves gift native reward");
        Plugin.Current.PayoutEnabled.Value = true;
        var disposedGift = GiftSession(out card, out gift, out inputGift);
        ScamRecord(disposedGift, "credit-card", 1, 5000);
        ScamRecord(disposedGift, "gift-card", 2, 7500);
        disposedGift.Dispose();
        int disposedAmount;
        Check(!Prices().TryGet(disposedGift, "credit-card", out disposedAmount)
            && !Prices().TryGet(disposedGift, "gift-card", out disposedAmount), "native call disposal removes both scam prices");
        result = disposedGift.SubmitField("gift-card", GiftTag, GiftValue, GiftCaller(), true);
        Check(result.PayoutAwarded == 0 && disposedGift.MoneyEarned == 0, "disposed call cannot pay valid gift code");
        TestCombinedLimit(false);
        TestCombinedLimit(true);
    }

    private static void TestGiftFailure(string name, string tag, string value, SentinelCallState caller, bool authorized)
    {
        ConversationScamDefinition card, gift, inputGift;
        var session = GiftSession(out card, out gift, out inputGift);
        ScamRecord(session, "gift-card", 1, 7500);
        var result = session.SubmitField("gift-card", tag, value, caller, authorized);
        Check(result.PayoutAwarded == 0 && session.MoneyEarned == 0, name + " pays nothing");
    }

    private static void TestCombinedLimit(bool giftFirst)
    {
        ConversationScamDefinition card, gift, inputGift;
        var session = GiftSession(out card, out gift, out inputGift);
        string first = giftFirst ? "gift-card" : "credit-card", second = giftFirst ? "credit-card" : "gift-card";
        ScamRecord(session, first, 1, 1500000000);
        var firstResult = session.SubmitField(first, giftFirst ? GiftTag : CardTag, giftFirst ? GiftValue : CardValue, GiftCaller(), true);
        Check(firstResult.PayoutAwarded == 1500000000, first + " first safe large requested amount is paid");
        ScamRecord(session, second, 2, 1500000000);
        var secondResult = session.SubmitField(second, giftFirst ? CardTag : GiftTag, giftFirst ? CardValue : GiftValue, GiftCaller(), true);
        Check(secondResult.PayoutAwarded == 200 && session.MoneyEarned == 1500000200,
            first + " then " + second + " reserve already-paid custom reward and avoid native int overflow");
    }

    private static void TestGiftPriceObservation()
    {
        PayoutHooks.Clear();
        ConversationScamDefinition card, gift, inputGift;
        var detector = new GiftPriceDetector();
        var session = GiftSession(out card, out gift, out inputGift, agreed: false, detector: detector);
        int awardCalls = 0;
        var turn = GiftPriceTurn(1);
        turn.AwardReward = (definition, payout) => { awardCalls++; return true; };
        session.ObserveAsync(turn).GetAwaiter().GetResult();
        Check(detector.SawPrice, "real native ObserveAsync exposes optional gift price after pitch and solution");
        Check(Completed(session, "gift-card").Contains("requested-price"),
            "real native ObserveAsync completes the optional gift price objective");
        Check(awardCalls == 0 && session.MoneyEarned == 0, "gift price agreement does not pay before code verification");
        int amount;
        Check(Prices().TryGet(session, "gift-card", out amount) && amount == 7500,
            "real native detector parse stores accepted gift price 7500");
        var result = session.SubmitField("gift-card", GiftTag, GiftValue, GiftCaller(), true);
        Check(result.PayoutAwarded == 7500 && session.MoneyEarned == 7500,
            "native observation followed by valid gift code pays accepted 7500");
        session.Dispose();

        foreach (string missing in new[] { "pitch", "solution" })
        {
            detector = new GiftPriceDetector();
            session = GiftSession(out card, out gift, out inputGift, agreed: false, detector: detector);
            Completed(session, "gift-card").Remove(missing);
            session.ObserveAsync(GiftPriceTurn(1)).GetAwaiter().GetResult();
            Check(!detector.SawPrice && !Completed(session, "gift-card").Contains("requested-price")
                && !Prices().TryGet(session, "gift-card", out amount),
                "native missing " + missing + " excludes optional price and cannot record agreement");
            session.Dispose();
        }
    }

    private static ConversationScamTurn GiftPriceTurn(int sequence)
    {
        return new ConversationScamTurn {
            Sequence = sequence,
            PlayerDialogue = "Der Preis beträgt 7500 Euro.",
            CallerDialogue = "Ja, ich akzeptiere 7500 Euro.",
            RecentDialogue = "Wir haben uns auf die Hilfe und die Lösung geeinigt.",
            OwnedProducts = new[] { "credit-card", "gift-card" },
            VisibleAppIds = new[] { "credit-card", "gift-card" },
            CallerSentinels = new Dictionary<string,string>(),
            TrustBefore = 50, TrustAfter = 60,
        };
    }

    private static void TestQuoteEvidence()
    {
        TestQuotes("exact valid native evidence", false, false, "Der Preis beträgt 5000 Euro.", "Ja, ich bezahle 5000 Euro.", false, 5000);
        TestQuotes("unrelated fabricated quote before valid quote", false, false, "Der Preis beträgt 5000 Euro.", "Ja, ich bezahle 5000 Euro.", true, 5000);
        TestQuotes("hidden exchange ignored", true, false, "Der Preis beträgt 5000 Euro.", "Ja, ich bezahle 5000 Euro.", false, 0);
        TestQuotes("fallback exchange ignored", false, true, "Der Preis beträgt 5000 Euro.", "Ja, ich bezahle 5000 Euro.", false, 0);
        TestQuotes("decimal offer and whole caller amount rejected", false, false, "Der Preis beträgt 5000,50 Euro.", "Ja, ich bezahle 5000 Euro.", false, 0);
        TestQuotes("whole offer and decimal caller amount rejected", false, false, "Der Preis beträgt 5000 Euro.", "Ja, ich bezahle 5000,50 Euro.", false, 0);
        TestDuplicate("valid price followed by fabricated duplicate", "5000 Euro", "Ja, 5000 Euro", "999999 Euro", "Ja, 999999 Euro", false, 5000);
        TestDuplicate("first ambiguous price cannot be replaced by later duplicate", "5000,50 Euro", "Ja, 5000,50 Euro", "5000 Euro", "Ja, 5000 Euro", true, 0);
    }

    private static void TestQuotes(string name, bool hidden, bool fallback, string player, string caller, bool insertInvalid, int expected)
    {
        PayoutHooks.Clear();
        ConversationScamDefinition card;
        var session = Session(out card);
        var turn = new ConversationScamTurn { Sequence = 1, PlayerDialogue = player, CallerDialogue = caller, Hidden = hidden, Fallback = fallback };
        var candidate = new ConversationScamCandidate { Scam = card, Objective = card.objectives[1] };
        var candidates = new[] { candidate };
        AccessTools.Method(typeof(PayoutHooks), "ObservePrefix").Invoke(null, new object[] { session, turn });
        var achievements = new JArray();
        if (insertInvalid) achievements.Add(Evidence(candidate.Key, "I offer 999999 Euro.", "I accept 999999 Euro."));
        achievements.Add(Evidence(candidate.Key, player, caller));
        var raw = new JObject { ["achievements"] = achievements }.ToString();
        MethodInfo parse = AccessTools.Method(typeof(ConversationScamAiDetector), "Parse");
        try
        {
            if (insertInvalid)
            {
                // An earlier native-accepted result cannot authorize a fabricated raw duplicate.
                string validRaw = new JObject { ["achievements"] = new JArray(Evidence(candidate.Key, player, caller)) }.ToString();
                var nativeValid = (IReadOnlyList<ConversationScamDetection>)parse.Invoke(null, new object[] { validRaw, candidates, turn, null });
                PayoutHooks.Clear();
                AccessTools.Method(typeof(PayoutHooks), "ObservePrefix").Invoke(null, new object[] { session, turn });
                AccessTools.Method(typeof(PayoutHooks), "DetectionPostfix").Invoke(null, new object[] { raw, candidates, turn, nativeValid });
            }
            else parse.Invoke(null, new object[] { raw, candidates, turn, null });
        }
        catch (TargetInvocationException ex)
        {
            Check(ex.InnerException is FormatException, name + ": malformed native evaluation fails closed");
        }
        int amount;
        bool found = Prices().TryGet(session, out amount);
        Check(expected == 0 ? !found : found && amount == expected, name + ": expected " + expected + ", got " + (found ? amount.ToString() : "none"));
        session.Dispose();
        Check(((IDictionary)AccessTools.Field(typeof(PayoutHooks), "owners").GetValue(null)).Count == 0, name + ": Dispose clears turn owners");
        var lateDetection = new[] { new ConversationScamDetection { Key = candidate.Key, Sequence = turn.Sequence, Amount = 0 } };
        AccessTools.Method(typeof(PayoutHooks), "DetectionPostfix").Invoke(null, new object[] { raw, candidates, turn, lateDetection });
        Check(!Prices().TryGet(session, out amount), name + ": late detection cannot restore disposed price");
    }

    private static void TestDuplicate(string name, string firstPlayer, string firstCaller, string secondPlayer,
        string secondCaller, bool bothQuotedInDialogue, int expected)
    {
        PayoutHooks.Clear();
        ConversationScamDefinition card;
        var session = Session(out card);
        var candidate = new ConversationScamCandidate { Scam = card, Objective = card.objectives[1] };
        var candidates = new[] { candidate };
        var turn = new ConversationScamTurn {
            Sequence = 1,
            PlayerDialogue = firstPlayer + (bothQuotedInDialogue ? "; " + secondPlayer : ""),
            CallerDialogue = firstCaller + (bothQuotedInDialogue ? "; " + secondCaller : ""),
        };
        var nativeRaw = new JObject { ["achievements"] = new JArray(Evidence(candidate.Key, firstPlayer, firstCaller)) }.ToString();
        var duplicateRaw = new JObject { ["achievements"] = new JArray(
            Evidence(candidate.Key, firstPlayer, firstCaller), Evidence(candidate.Key, secondPlayer, secondCaller)) }.ToString();
        var nativeResult = (IReadOnlyList<ConversationScamDetection>)AccessTools.Method(typeof(ConversationScamAiDetector), "Parse")
            .Invoke(null, new object[] { nativeRaw, candidates, turn, null });
        AccessTools.Method(typeof(PayoutHooks), "ObservePrefix").Invoke(null, new object[] { session, turn });
        AccessTools.Method(typeof(PayoutHooks), "DetectionPostfix").Invoke(null, new object[] { duplicateRaw, candidates, turn, nativeResult });
        int amount;
        bool found = Prices().TryGet(session, out amount);
        Check(expected == 0 ? !found : found && amount == expected,
            name + ": expected " + expected + ", got " + (found ? amount.ToString() : "none"));
        session.Dispose();
    }

    private static JObject Evidence(string key, string player, string caller)
    {
        return new JObject { ["key"] = key, ["sequence"] = 1, ["amount"] = 0, ["player_evidence"] = player, ["caller_evidence"] = caller };
    }

    private static void Check(bool condition, string message)
    {
        if (condition) { passed++; return; }
        failed++;
        Console.WriteLine("FAIL: " + message);
    }

    private sealed class FixtureDetector : IConversationScamDetector
    {
        public Task<IReadOnlyList<ConversationScamDetection>> DetectAsync(IReadOnlyList<ConversationScamCandidate> candidates,
            ConversationScamTurn turn, CancellationToken cancellation)
        {
            return Task.FromResult<IReadOnlyList<ConversationScamDetection>>(new ConversationScamDetection[0]);
        }
        public void Dispose() { }
    }

    private sealed class GiftPriceDetector : IConversationScamDetector
    {
        internal bool SawPrice;
        public Task<IReadOnlyList<ConversationScamDetection>> DetectAsync(IReadOnlyList<ConversationScamCandidate> candidates,
            ConversationScamTurn turn, CancellationToken cancellation)
        {
            foreach (var candidate in candidates)
            {
                if (candidate.Key != "gift-card.requested-price") continue;
                SawPrice = true;
                var evidence = Evidence(candidate.Key, turn.PlayerDialogue, turn.CallerDialogue);
                evidence["sequence"] = turn.Sequence;
                string json = new JObject { ["achievements"] = new JArray(evidence) }.ToString();
                var accepted = (IReadOnlyList<ConversationScamDetection>)AccessTools.Method(typeof(ConversationScamAiDetector), "Parse")
                    .Invoke(null, new object[] { json, candidates, turn, null });
                return Task.FromResult(accepted);
            }
            return Task.FromResult<IReadOnlyList<ConversationScamDetection>>(new ConversationScamDetection[0]);
        }
        public void Dispose() { }
    }
}
