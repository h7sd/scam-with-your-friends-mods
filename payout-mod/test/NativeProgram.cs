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
    private const string ActualCardTag = "FICTIONAL_CREDIT_CARD_NUMBER";
    private const string ActualCardValue = "4111111111111111";

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
        TestExplicitOfferPayouts();
        TestNativeModelPriceNormalization();
        TestImmediatePriceAnswers();
        TestQuoteContextFallback();
        TestRewardSnapshotDuringChanged();
        Console.WriteLine("Payout native tests: " + passed + " passed, " + failed + " failed");
        return failed == 0 ? 0 : 1;
    }

    private static bool SyntheticCardDefinition(string tag, ref SentinelDefinition __result)
    {
        if (tag != CardTag && tag != GiftTag && tag != ActualCardTag) return true;
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
        Check(result.PayoutAwarded == 9000 && noAcceptedPrice.MoneyEarned == 9000, "confirmed amount is paid without optional native price milestone");

        var noService = Session(out card, serviceComplete: false);
        Prices().Record(noService, 1, 9000);
        result = noService.SubmitField("credit-card", CardTag, CardValue, Caller(), true);
        Check(result.PayoutAwarded == 9000 && noService.MoneyEarned == 9000, "confirmed amount is paid without optional native service milestone");

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
                && Prices().TryGet(session, "gift-card", out amount) && amount == 7500,
                "native missing " + missing + " excludes optional AI goal while explicit player offer stays remembered");
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

    private static ConversationScamSession ActualCatalogSession()
    {
        // Public catalog metadata from the installed game, not the earlier simplified fixture:
        // actual product IDs, reward 400/200, number/code final goals and native prerequisites.
        var card = new ConversationScamDefinition {
            id = "credit-card", appId = "credit-card", productId = "scam-credit-card", reward = 400,
            objectives = new[] {
                new ConversationScamObjective { id = "service" },
                new ConversationScamObjective { id = "price", prerequisites = new[] { "service" } },
                new ConversationScamObjective { id = "number", final = true, requiresSubmission = true,
                    requiredSentinel = ActualCardTag, prerequisites = new[] { "service", "price" } },
            },
        };
        var gift = new ConversationScamDefinition {
            id = "gift-card", appId = "gift-card", productId = "scam-gift-card", reward = 200,
            objectives = new[] {
                new ConversationScamObjective { id = "pitch" },
                new ConversationScamObjective { id = "solution", prerequisites = new[] { "pitch" } },
                new ConversationScamObjective { id = "code", final = true, requiresSubmission = true,
                    requiredSentinel = GiftTag, prerequisites = new[] { "pitch", "solution" } },
            },
        };
        return new ConversationScamSession(new[] { card, gift }, new FixtureDetector());
    }

    private static ConversationScamTurn ActualOfferTurn(string dialogue, int sequence, bool hidden = false, bool fallback = false)
    {
        return new ConversationScamTurn {
            Sequence = sequence, PlayerDialogue = dialogue, CallerDialogue = "Ja.", RecentDialogue = "",
            OwnedProducts = new[] { "scam-credit-card", "scam-gift-card" },
            VisibleAppIds = new[] { "credit-card", "gift-card" },
            CallerSentinels = new Dictionary<string,string>(), TrustBefore = 50, TrustAfter = 50,
            Hidden = hidden, Fallback = fallback,
        };
    }

    private static SentinelCallState ActualCaller()
    {
        var caller = new SentinelCallState();
        var infos = (IDictionary)AccessTools.Field(typeof(SentinelCallState), "infosByTag").GetValue(caller);
        infos.Add(ActualCardTag, new SentinelInfo { Tag = ActualCardTag, RawValue = ActualCardValue, IsRevealed = true });
        infos.Add(GiftTag, new SentinelInfo { Tag = GiftTag, RawValue = GiftValue, IsRevealed = true });
        return caller;
    }

    private static ScamFieldVerificationResult ActualSubmit(ConversationScamSession session, string scam, string value = null)
    {
        bool gift = scam == "gift-card";
        return session.SubmitField(scam, gift ? GiftTag : ActualCardTag, value ?? (gift ? GiftValue : ActualCardValue), ActualCaller(), true);
    }

    private static void TestExplicitOfferPayouts()
    {
        foreach (string offer in new[] { "Der Service kostet 20000 Euro.", "Der Service kostet 20.000 Euro.",
            "Der Service kostet zwanzigtausend Euro." })
        {
            foreach (string scam in new[] { "credit-card", "gift-card" })
            {
                PayoutHooks.Clear();
                var session = ActualCatalogSession();
                int awardCalls = 0, changedMoney = -1;
                var turn = ActualOfferTurn(offer, 1);
                turn.AwardReward = (definition, payout) => { awardCalls++; return true; };
                session.Changed += () => changedMoney = session.MoneyEarned;
                session.ObserveAsync(turn).GetAwaiter().GetResult();
                Check(Completed(session, "credit-card").Count == 0 && Completed(session, "gift-card").Count == 0,
                    scam + " actual catalog price offer needs no service/price/pitch/solution milestones");
                Check(session.MoneyEarned == 0 && awardCalls == 0, scam + " explicit offer alone awards no money");
                int quote;
                Check(Prices().TryGet(session, "credit-card", out quote) && quote == 20000
                    && Prices().TryGet(session, "gift-card", out quote) && quote == 20000,
                    scam + " generic service offer is remembered for both scams in this call");
                var failure = ActualSubmit(session, scam, "wrong-code");
                Check(failure.PayoutAwarded == 0 && session.MoneyEarned == 0, scam + " wrong code after 20000 offer pays nothing");
                var result = ActualSubmit(session, scam);
                Check(result.PayoutAwarded == 20000 && session.MoneyEarned == 20000 && changedMoney == 20000,
                    scam + " actual native verified code pays stated 20000 with brief caller yes: " + offer);
                result = ActualSubmit(session, scam);
                Check(result.PayoutAwarded == 0 && session.MoneyEarned == 20000,
                    scam + " retry after explicit 20000 payout is idempotent");
                session.Dispose();
            }
        }

        PayoutHooks.Clear();
        var scoped = ActualCatalogSession();
        scoped.ObserveAsync(ActualOfferTurn("Der Kreditkarten-Service kostet 20000 Euro.", 1)).GetAwaiter().GetResult();
        int amount;
        Check(Prices().TryGet(scoped, "credit-card", out amount) && amount == 20000
            && !Prices().TryGet(scoped, "gift-card", out amount), "explicit credit-card offer cannot price gift scam");
        var giftOriginal = ActualSubmit(scoped, "gift-card");
        Check(giftOriginal.PayoutAwarded == 200, "unpriced gift scam keeps actual original reward200");
        scoped.ObserveAsync(ActualOfferTurn("Der Kreditkarten-Service kostet 25000 Euro.", 3)).GetAwaiter().GetResult();
        scoped.ObserveAsync(ActualOfferTurn("Der Kreditkarten-Service kostet 10000 Euro.", 2)).GetAwaiter().GetResult();
        Check(Prices().TryGet(scoped, "credit-card", out amount) && amount == 25000,
            "newest observed credit offer wins over older out-of-order turn");
        Prices().Record(scoped, "credit-card", 1, 5000);
        var latest = ActualSubmit(scoped, "credit-card");
        Check(latest.PayoutAwarded == 25000 && scoped.MoneyEarned == 25200,
            "late AI-confirmed earlier amount cannot override latest explicit 25000 offer");
        scoped.Dispose();

        var giftScoped = ActualCatalogSession();
        giftScoped.ObserveAsync(ActualOfferTurn("Der Geschenkkarten-Service kostet 7500 Euro.", 1)).GetAwaiter().GetResult();
        Check(Prices().TryGet(giftScoped, "gift-card", out amount) && amount == 7500
            && !Prices().TryGet(giftScoped, "credit-card", out amount), "explicit gift offer cannot price credit scam");
        var creditOriginal = ActualSubmit(giftScoped, "credit-card");
        Check(creditOriginal.PayoutAwarded == 400, "unpriced credit scam keeps actual original reward400");
        giftScoped.Dispose();

        var first = ActualCatalogSession();
        first.ObserveAsync(ActualOfferTurn("Der Service kostet 20000 Euro.", 1)).GetAwaiter().GetResult();
        var other = ActualCatalogSession();
        Check(!Prices().TryGet(other, "credit-card", out amount) && !Prices().TryGet(other, "gift-card", out amount),
            "generic offer cannot leak into another call");
        other.Dispose();
        first.Dispose();
        first.ObserveAsync(ActualOfferTurn("Der Service kostet 30000 Euro.", 2)).GetAwaiter().GetResult();
        Check(!Prices().TryGet(first, "credit-card", out amount) && !Prices().TryGet(first, "gift-card", out amount),
            "late ObserveAsync after Dispose cannot resurrect pending offers");

        foreach (bool fallback in new[] { false, true })
        {
            var excluded = ActualCatalogSession();
            excluded.ObserveAsync(ActualOfferTurn("Der Service kostet 20000 Euro.", 1, hidden: !fallback, fallback: fallback)).GetAwaiter().GetResult();
            Check(!Prices().TryGet(excluded, "credit-card", out amount) && !Prices().TryGet(excluded, "gift-card", out amount),
                (fallback ? "fallback" : "hidden") + " turn cannot supply an explicit payout offer");
            excluded.Dispose();
        }
    }

    private static void TestNativeModelPriceNormalization()
    {
        foreach (string scam in new[] { "credit-card", "gift-card" })
        {
            PayoutHooks.Clear();
            var session = ActualCatalogSession();
            ConversationScamDefinition definition = null;
            foreach (var entry in (IReadOnlyList<ConversationScamDefinition>)AccessTools.Field(typeof(ConversationScamSession), "catalog").GetValue(session))
                if (entry.id == scam) definition = entry;
            string priceId = scam == "credit-card" ? "price" : "requested-price";
            ConversationScamObjective objective = null;
            foreach (var entry in definition.objectives) if (entry.id == priceId) objective = entry;
            var candidate = new ConversationScamCandidate { Scam = definition, Objective = objective };
            var turn = ActualOfferTurn("Ich benötige Hilfe.", 1);
            turn.CallerDialogue = "Ja, der Servicepreis beträgt 20000 Euro.";
            AccessTools.Method(typeof(PayoutHooks), "ObservePrefix").Invoke(null, new object[] { session, turn });
            var evidence = Evidence(candidate.Key, turn.PlayerDialogue, turn.CallerDialogue);
            evidence["amount"] = 20000;
            string json = new JObject { ["achievements"] = new JArray(evidence) }.ToString();
            var parsed = (IReadOnlyList<ConversationScamDetection>)AccessTools.Method(typeof(ConversationScamAiDetector), "Parse")
                .Invoke(null, new object[] { json, new[] { candidate }, turn, null });
            Check(parsed.Count == 1 && parsed[0].Amount == 0,
                scam + " model price amount20000 is normalized to native non-final metadata0");
            int amount;
            Check(Prices().TryGet(session, scam, out amount) && amount == 20000,
                scam + " model metadata normalization preserves evidence price20000");
            var nonPrice = new ConversationScamCandidate { Scam = definition, Objective = definition.objectives[0] };
            evidence = Evidence(nonPrice.Key, turn.PlayerDialogue, turn.CallerDialogue);
            evidence["amount"] = 20000;
            json = new JObject { ["achievements"] = new JArray(evidence) }.ToString();
            parsed = (IReadOnlyList<ConversationScamDetection>)AccessTools.Method(typeof(ConversationScamAiDetector), "Parse")
                .Invoke(null, new object[] { json, new[] { nonPrice }, turn, null });
            Check(parsed.Count == 0, scam + " non-price objective retains native amount cap");
            session.Dispose();
        }
    }

    private static void TestImmediatePriceAnswers()
    {
        foreach (string answer in new[] { "20000", "20.000", "zwanzigtausend" })
        {
            var session = ActualCatalogSession();
            var question = ActualOfferTurn("Hallo.", 1);
            question.CallerDialogue = "Was kostet Ihr Service?";
            session.ObserveAsync(question).GetAwaiter().GetResult();
            session.ObserveAsync(ActualOfferTurn(answer, 2)).GetAwaiter().GetResult();
            int amount;
            Check(Prices().TryGet(session, "credit-card", out amount) && amount == 20000
                && Prices().TryGet(session, "gift-card", out amount) && amount == 20000,
                "immediate standalone answer to a caller price question records20000: " + answer);
            Check(ActualSubmit(session, "credit-card").PayoutAwarded == 20000,
                "immediate price answer reaches native verified credit payout: " + answer);
            session.Dispose();
        }
        var scoped = ActualCatalogSession();
        var scopedQuestion = ActualOfferTurn("Hallo.", 1);
        scopedQuestion.CallerDialogue = "Wie hoch ist der Kreditkarten-Preis?";
        scoped.ObserveAsync(scopedQuestion).GetAwaiter().GetResult();
        scoped.ObserveAsync(ActualOfferTurn("20000", 2)).GetAwaiter().GetResult();
        int stored;
        Check(Prices().TryGet(scoped, "credit-card", out stored) && stored == 20000
            && !Prices().TryGet(scoped, "gift-card", out stored), "bare answer preserves caller-question credit scope");
        scoped.Dispose();

        var stale = ActualCatalogSession();
        var priorQuestion = ActualOfferTurn("Hallo.", 1);
        priorQuestion.CallerDialogue = "Was kostet Ihr Service?";
        stale.ObserveAsync(priorQuestion).GetAwaiter().GetResult();
        stale.ObserveAsync(ActualOfferTurn("Einen Moment.", 2)).GetAwaiter().GetResult();
        stale.ObserveAsync(ActualOfferTurn("20000", 3)).GetAwaiter().GetResult();
        Check(!Prices().TryGet(stale, "credit-card", out stored) && !Prices().TryGet(stale, "gift-card", out stored),
            "non-immediate number does not reuse earlier price-question context");
        stale.Dispose();

        foreach (string callerQuestion in new[] { "Wie lautet Ihre Kartennummer?", "Welche PIN haben Sie?",
            "Wie lautet der Giftcode?", "Was kostet der Service, und welche PIN haben Sie?" })
        {
            var credential = ActualCatalogSession();
            var question = ActualOfferTurn("Hallo.", 1);
            question.CallerDialogue = callerQuestion;
            credential.ObserveAsync(question).GetAwaiter().GetResult();
            credential.ObserveAsync(ActualOfferTurn("20000", 2)).GetAwaiter().GetResult();
            Check(!Prices().TryGet(credential, "credit-card", out stored) && !Prices().TryGet(credential, "gift-card", out stored),
                "card/PIN/code question cannot supply price context: " + callerQuestion);
            credential.Dispose();
        }

        foreach (string unsafeOffer in new[] { "Was kostet 20000 Euro?", "Soll ich 20000 Euro zahlen?",
            "Die PIN lautet 20000 Euro.", "Der Giftcode lautet 20000 Euro.", "Meine Kartennummer ist 4111111111111111." })
        {
            var session = ActualCatalogSession();
            session.ObserveAsync(ActualOfferTurn(unsafeOffer, 1)).GetAwaiter().GetResult();
            Check(!Prices().TryGet(session, "credit-card", out stored) && !Prices().TryGet(session, "gift-card", out stored),
                "inquiry or credential is not an explicit price offer: " + unsafeOffer);
            session.Dispose();
        }
    }

    private static void TestQuoteContextFallback()
    {
        foreach (bool fullContext in new[] { true, false })
        {
            PayoutHooks.Clear();
            ConversationScamDefinition card;
            var session = Session(out card, priceComplete: false, serviceComplete: false);
            var candidate = new ConversationScamCandidate { Scam = card, Objective = card.objectives[1] };
            var turn = new ConversationScamTurn {
                Sequence = 1, PlayerDialogue = fullContext ? "Der Service kostet 20000 Euro." : "20000",
                CallerDialogue = "Ja, 20000",
            };
            AccessTools.Method(typeof(PayoutHooks), "ObservePrefix").Invoke(null, new object[] { session, turn });
            string json = new JObject { ["achievements"] = new JArray(Evidence(candidate.Key, "20000", turn.CallerDialogue)) }.ToString();
            var parsed = (IReadOnlyList<ConversationScamDetection>)AccessTools.Method(typeof(ConversationScamAiDetector), "Parse")
                .Invoke(null, new object[] { json, new[] { candidate }, turn, null });
            int amount;
            bool found = Prices().TryGet(session, "credit-card", out amount);
            Check(parsed.Count == 1 && (fullContext ? found && amount == 20000 : !found),
                fullContext ? "short exact price quotes recover20000 from full exchange currency context"
                    : "two bare quotes without full price context do not fabricate a payout amount");
            session.Dispose();
        }

        var crossTurn = ActualCatalogSession();
        crossTurn.ObserveAsync(ActualOfferTurn("Der Service kostet 20.000 Euro.", 1)).GetAwaiter().GetResult();
        var briefAcceptance = ActualOfferTurn("Die Hilfe läuft jetzt.", 2);
        briefAcceptance.CallerDialogue = "Ja, 20.000.";
        crossTurn.ObserveAsync(briefAcceptance).GetAwaiter().GetResult();
        Check(ActualSubmit(crossTurn, "credit-card").PayoutAwarded == 20000,
            "full player offer stays remembered across later bare caller acceptance");
        crossTurn.Dispose();
    }

    private static void TestRewardSnapshotDuringChanged()
    {
        var session = ActualCatalogSession();
        session.ObserveAsync(ActualOfferTurn("Der Service kostet 20000 Euro.", 1)).GetAwaiter().GetResult();
        session.Changed += () => Prices().RecordOffer(session, "credit-card", 2, 30000);
        var result = ActualSubmit(session, "credit-card");
        Check(result.PayoutAwarded == 20000 && session.MoneyEarned == 20000,
            "price change during native Changed event cannot split progress20000 from payout result30000");
        session.Dispose();
        foreach (bool clear in new[] { false, true })
        {
            session = ActualCatalogSession();
            session.ObserveAsync(ActualOfferTurn("Der Service kostet 20000 Euro.", 1)).GetAwaiter().GetResult();
            session.Changed += () => {
                if (clear) PayoutHooks.Clear();
                else Plugin.Current.PayoutEnabled.Value = false;
            };
            result = ActualSubmit(session, "credit-card");
            Check(result.PayoutAwarded == 20000 && session.MoneyEarned == 20000,
                (clear ? "clearing quote book" : "disabling mod") + " during Changed preserves already-written native payout amount");
            Plugin.Current.PayoutEnabled.Value = true;
            session.Dispose();
        }
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
