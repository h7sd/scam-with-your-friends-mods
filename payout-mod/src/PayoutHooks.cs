using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using System.Reflection.Emit;
using HarmonyLib;
using Newtonsoft.Json.Linq;
using ScamWYF.Modding.Core;

namespace ScamWYF.RequestedPayout
{
    internal static class PayoutHooks
    {
        private static readonly PriceBook prices = new PriceBook();
        private static readonly Dictionary<ConversationScamTurn,ConversationScamSession> owners = new Dictionary<ConversationScamTurn,ConversationScamSession>();
        private sealed class PricePrompt {internal int Sequence;internal int Scope;}
        private static readonly Dictionary<ConversationScamSession,PricePrompt> prompts = new Dictionary<ConversationScamSession,PricePrompt>();
        private static readonly object sync = new object();
        private static FieldInfo progressField, catalogField, completedField, moneyEarnedField, disposedField;
        private static MethodInfo containsQuote;
        private static readonly MethodInfo memberwiseClone=AccessTools.Method(typeof(object),"MemberwiseClone");

        internal static void Install(Plugin plugin)
        {
            progressField = AccessTools.Field(typeof(ConversationScamSession),"progress");
            catalogField = AccessTools.Field(typeof(ConversationScamSession),"catalog");
            disposedField = AccessTools.Field(typeof(ConversationScamSession),"disposed");
            if (progressField == null || catalogField == null || disposedField==null) throw new InvalidOperationException("Native conversation scam state changed.");
            completedField = AccessTools.Field(progressField.FieldType.GetGenericArguments()[1],"Completed");
            moneyEarnedField = AccessTools.Field(progressField.FieldType.GetGenericArguments()[1],"MoneyEarned");
            if (completedField == null || moneyEarnedField==null) throw new InvalidOperationException("Native objective completion state changed.");
            containsQuote=AccessTools.Method(typeof(ConversationScamAiDetector),"ContainsQuote",new[]{typeof(string),typeof(string)});
            if(containsQuote==null) throw new InvalidOperationException("Native evidence validation changed.");
            var ctor=AccessTools.Constructor(typeof(ConversationScamSession),new[]{typeof(IReadOnlyList<ConversationScamDefinition>),typeof(IConversationScamDetector)});
            if(ctor==null || !PatchCoordinator.TryPatch(plugin,ctor,"add an optional gift-card price goal to a private session copy",
                prefix:new HarmonyMethod(AccessTools.Method(typeof(PayoutHooks),"ConstructorPrefix"))))
                throw new InvalidOperationException("Could not install private gift-card price goal.");
            var prompt=AccessTools.Method(typeof(CallerIdentityLibrary),"BuildCallerSystemPrompt");
            if(prompt==null || !PatchCoordinator.TryPatch(plugin,prompt,"let fictional callers explicitly accept a gift-card service price",
                postfix:new HarmonyMethod(AccessTools.Method(typeof(PayoutHooks),"CallerPromptPostfix"))))
                throw new InvalidOperationException("Could not install gift-card caller prompt.");
            Patch(plugin,typeof(ConversationScamSession),"ObserveAsync",new[]{typeof(ConversationScamTurn)},"ObservePrefix",null,null);
            Patch(plugin,typeof(ConversationScamAiDetector),"BuildInput",new[]{typeof(IReadOnlyList<ConversationScamCandidate>),typeof(ConversationScamTurn)},null,"InputPostfix",null);
            Patch(plugin,typeof(ConversationScamAiDetector),"Parse",new[]{typeof(string),typeof(IReadOnlyList<ConversationScamCandidate>),typeof(ConversationScamTurn),typeof(Action<string>)},"DetectionPrefix","DetectionPostfix",null);
            Patch(plugin,typeof(ConversationScamSession),"SubmitField",new[]{typeof(string),typeof(string),typeof(string),typeof(SentinelCallState),typeof(bool)},null,"SubmissionPostfix","RewardTranspiler");
            Patch(plugin,typeof(ConversationScamSession),"Dispose",Type.EmptyTypes,"DisposePrefix",null,null);
        }
        private static void Patch(Plugin owner,Type type,string name,Type[] signature,string prefix,string postfix,string transpiler)
        {
            if (!PatchCoordinator.TryPatch(owner,type,name,signature,"apply an accepted card service price only on native success",
                prefix: prefix==null?null:new HarmonyMethod(AccessTools.Method(typeof(PayoutHooks),prefix)),
                postfix: postfix==null?null:new HarmonyMethod(AccessTools.Method(typeof(PayoutHooks),postfix)),
                transpiler: transpiler==null?null:new HarmonyMethod(AccessTools.Method(typeof(PayoutHooks),transpiler))))
                throw new InvalidOperationException("Could not install Wunschsumme hook: "+type.Name+"."+name);
        }
        private static bool Enabled { get { return Plugin.Current != null && Plugin.Current.PayoutEnabled.Value; } }
        private static bool Supported(string id) {return id=="credit-card" || id=="gift-card";}
        private static void ConstructorPrefix(ref IReadOnlyList<ConversationScamDefinition> catalog)
        {
            if(!Enabled || catalog==null) return;
            var copy=new List<ConversationScamDefinition>();
            foreach(var definition in catalog)
            {
                if(definition==null || definition.id!="gift-card") {copy.Add(definition);continue;}
                var gift=(ConversationScamDefinition)memberwiseClone.Invoke(definition,null);
                if(gift.rewardTiers!=null) gift.rewardTiers=(int[])gift.rewardTiers.Clone();
                var goals=new List<ConversationScamObjective>();bool exists=false;
                if(definition.objectives!=null) foreach(var objective in definition.objectives)
                {
                    if(objective==null) {goals.Add(null);continue;}
                    var cloned=(ConversationScamObjective)memberwiseClone.Invoke(objective,null);
                    if(cloned.prerequisites!=null) cloned.prerequisites=(string[])cloned.prerequisites.Clone();
                    if(cloned.id=="requested-price") exists=true;
                    goals.Add(cloned);
                }
                if(!exists) goals.Add(new ConversationScamObjective {
                    id="requested-price",text="Optional: Agree on a specific gift-card service price.",
                    criteria="After the caller accepts the offered help and concrete solution, the caller explicitly accepts a specific whole-number price for that service to be paid with a fictional gift card. The price may be proposed by either speaker, but the caller must actually agree. Require exact dialogue evidence containing that amount and price/currency wording. A question, refusal, hypothetical, amount merely mentioned by the player, or disclosure of a gift-card code is insufficient. This optional agreement does not complete payment; only the original correct gift-card code submission completes the scam.",
                    final=false,optional=true,requiresSubmission=false,prerequisites=new[]{"pitch","solution"}
                });
                gift.objectives=goals.ToArray();copy.Add(gift);
            }
            catalog=copy;
        }
        private static void CallerPromptPostfix(object[] __args,ref string __result)
        {
            if(!Enabled || string.IsNullOrEmpty(__result)) return;
            foreach(var argument in __args)
            {
                var catalog=argument as IReadOnlyList<ConversationScamDefinition>;
                if(catalog==null) continue;
                foreach(var definition in catalog) if(definition!=null && definition.id=="gift-card")
                {
                    __result+="\nGift-card game behavior: the player may offer a specific whole-number service price in game currency after you accept the help and solution. Decide whether to accept it using your existing personality, trust and situation. Only if you actually agree, say explicitly that you accept that exact amount and currency. Do not invent a price, treat mere mention as consent, or confuse the price with a fictional card code. Accepted price alone does not mean payment completed; keep the original gift-card disclosure and completion rules.";
                    return;
                }
            }
        }
        private static void ObservePrefix(ConversationScamSession __instance,ConversationScamTurn turn)
        {
            if (!Enabled || turn==null || turn.Hidden || turn.Fallback || (bool)disposedField.GetValue(__instance)) return;
            lock(sync)
            {
                int available=AvailableScopes(__instance,turn);
                int scope=SpokenPrice.Scope(turn.PlayerDialogue),amount;
                bool offered=SpokenPrice.TryExtractOffer(turn.PlayerDialogue,out amount);
                PricePrompt prompt;
                if(!offered && prompts.TryGetValue(__instance,out prompt)
                    && (long)prompt.Sequence+1==turn.Sequence
                    && SpokenPrice.TryExtractStandalone(turn.PlayerDialogue,out amount))
                {
                    offered=true;scope=prompt.Scope;
                }
                if(offered)
                {
                    if(scope==0) scope=available;
                    scope&=available;
                    bool recorded=false;
                    if((scope&1)!=0) recorded|=prices.RecordOffer(__instance,"credit-card",turn.Sequence,amount);
                    if((scope&2)!=0) recorded|=prices.RecordOffer(__instance,"gift-card",turn.Sequence,amount);
                    if(recorded)
                    {
                        Plugin.Current.AcceptedPrices++;Plugin.Current.LastAmount=amount;
                        Plugin.Current.LastStatus="Wunschbetrag "+amount+" erkannt; Auszahlung wartet auf erfolgreiche native Karten- oder Gift-Code-Prüfung.";
                    }
                }
                if(!prompts.TryGetValue(__instance,out prompt) || turn.Sequence>=prompt.Sequence)
                {
                    int questionScope=SpokenPrice.Scope(turn.CallerDialogue);
                    questionScope=questionScope==0?available:questionScope&available;
                    if(questionScope!=0 && SpokenPrice.IsPriceQuestion(turn.CallerDialogue))
                        prompts[__instance]=new PricePrompt {Sequence=turn.Sequence,Scope=questionScope};
                    else prompts.Remove(__instance);
                }
                owners[turn]=__instance;
                if(turn.RecoveredExchanges!=null) foreach(var recovery in turn.RecoveredExchanges)
                    if(recovery!=null && recovery.Turn!=null) owners[recovery.Turn]=__instance;
            }
        }
        private static int AvailableScopes(ConversationScamSession session,ConversationScamTurn turn)
        {
            var catalog=catalogField.GetValue(session) as IReadOnlyList<ConversationScamDefinition>;
            if(catalog==null) return 0;
            int available=0;
            foreach(var definition in catalog)
            {
                if(definition==null || !Supported(definition.id)) continue;
                bool owned=Has(turn.OwnedProducts,definition.productId) || Has(turn.OwnedProducts,definition.id)
                    || Has(turn.VisibleAppIds,definition.appId) || Has(turn.VisibleAppIds,definition.id);
                if(owned) available|=definition.id=="credit-card"?1:2;
            }
            return available;
        }
        private static bool Has(string[] values,string wanted)
        {
            if(values==null || string.IsNullOrEmpty(wanted)) return false;
            foreach(string value in values) if(value==wanted) return true;
            return false;
        }
        private static bool IsPrice(ConversationScamCandidate candidate)
        {
            return candidate!=null && candidate.Scam!=null && candidate.Objective!=null
                && ((candidate.Scam.id=="credit-card" && candidate.Objective.id=="price")
                    || (candidate.Scam.id=="gift-card" && candidate.Objective.id=="requested-price"));
        }
        private static void InputPostfix(IReadOnlyList<ConversationScamCandidate> candidates,ref string __result)
        {
            if(!Enabled || candidates==null) return;
            foreach(var candidate in candidates) if(IsPrice(candidate))
            {
                __result += "\nFor credit-card price or gift-card requested-price objectives, select an exact player_evidence or caller_evidence quote containing the concrete accepted price and its currency or service-price wording. The caller must explicitly agree; mere mention, a refusal or a hypothetical is insufficient. Keep the existing JSON schema; use amount=0 for these non-final objectives. Do not quote card numbers or gift-card codes as prices.";
                return;
            }
        }
        private static void DetectionPrefix(ref string json,IReadOnlyList<ConversationScamCandidate> candidates,ConversationScamTurn turn)
        {
            if(!Enabled || turn==null || turn.Hidden || turn.Fallback || string.IsNullOrWhiteSpace(json)) return;
            JObject data;
            try {data=JObject.Parse(json);} catch(Exception) {return;}
            var achievements=data["achievements"] as JArray;
            if(achievements==null) return;
            bool changed=false;
            foreach(var token in achievements)
            {
                var evidence=token as JObject;
                if(evidence==null || evidence.Count!=5
                    || evidence["key"]==null || evidence["key"].Type!=JTokenType.String
                    || evidence["sequence"]==null || evidence["sequence"].Type!=JTokenType.Integer
                    || evidence["amount"]==null || evidence["amount"].Type!=JTokenType.Integer
                    || evidence["player_evidence"]==null || evidence["player_evidence"].Type!=JTokenType.String
                    || evidence["caller_evidence"]==null || evidence["caller_evidence"].Type!=JTokenType.String) continue;
                long sequence,metadataAmount;
                try {sequence=(long)evidence["sequence"];metadataAmount=(long)evidence["amount"];} catch(Exception) {continue;}
                if(metadataAmount<=0 || metadataAmount>2147483647L) continue;
                IReadOnlyList<ConversationScamCandidate> eligible=null;
                if(sequence==turn.Sequence) eligible=candidates;
                else if(turn.RecoveredExchanges!=null) foreach(var recovery in turn.RecoveredExchanges)
                    if(recovery!=null && recovery.Turn!=null && !recovery.Turn.Hidden && !recovery.Turn.Fallback
                        && recovery.Turn.Sequence==sequence) {eligible=recovery.Candidates;break;}
                if(eligible==null) continue;
                foreach(var candidate in eligible)
                    if(IsPrice(candidate) && !candidate.Objective.final && candidate.Key==(string)evidence["key"])
                    {
                        // Price is evidence, not a native reward tier. Let native Parse continue
                        // validating the exact schema, eligibility, speakers and duplicate keys.
                        evidence["amount"]=0;changed=true;break;
                    }
            }
            if(changed) json=data.ToString(Newtonsoft.Json.Formatting.None);
        }
        private static void DetectionPostfix(string json,IReadOnlyList<ConversationScamCandidate> candidates,ConversationScamTurn turn,IReadOnlyList<ConversationScamDetection> __result)
        {
            if(!Enabled || turn==null || __result==null || __result.Count==0) return;
            ConversationScamSession owner;
            lock(sync) { if(!owners.TryGetValue(turn,out owner)) return; }
            JObject data;
            try { data=JObject.Parse(json); } catch(Exception) { return; }
            var achievements=data["achievements"] as JArray;
            if(achievements==null) return;
            foreach(var detection in __result)
            {
                var exchange=turn;
                IReadOnlyList<ConversationScamCandidate> eligible=candidates;
                if(detection.Sequence!=turn.Sequence)
                {
                    exchange=null;
                    if(turn.RecoveredExchanges!=null) foreach(var recovered in turn.RecoveredExchanges)
                        if(recovered!=null && recovered.Turn!=null && recovered.Turn.Sequence==detection.Sequence)
                        { exchange=recovered.Turn;eligible=recovered.Candidates;break; }
                }
                if(exchange==null || exchange.Hidden || exchange.Fallback || eligible==null) continue;
                string scamId=null;
                foreach(var candidate in eligible) if(IsPrice(candidate) && candidate.Key==detection.Key) {scamId=candidate.Scam.id;break;}
                if(scamId==null) continue;
                var evidence=FirstAcceptedEvidence(achievements,detection,exchange);
                if(evidence!=null)
                {
                    int offered,accepted,contextAmount;
                    bool contextHas=ExchangePrice(exchange,out contextAmount);
                    string playerEvidence=(string)evidence["player_evidence"],callerEvidence=(string)evidence["caller_evidence"];
                    bool playerHas=QuotePrice(playerEvidence,contextHas,contextAmount,out offered);
                    bool callerHas=QuotePrice(callerEvidence,contextHas,contextAmount,out accepted);
                    if(!playerHas && (SpokenPrice.HasPriceExpression(playerEvidence)
                        || (contextHas && SpokenPrice.HasNumberExpression(playerEvidence)))) continue;
                    if(!callerHas && (SpokenPrice.HasPriceExpression(callerEvidence)
                        || (contextHas && SpokenPrice.HasNumberExpression(callerEvidence)))) continue;
                    if(!playerHas && !callerHas) continue;
                    if(playerHas && callerHas && offered!=accepted) continue;
                    int amount=playerHas?offered:accepted;
                    // Native Parse has already validated eligible objective, exchange and both exact
                    // evidence quotes. Only that accepted price can populate this session's quote.
                    bool recorded;
                    lock(sync)
                    {
                        ConversationScamSession stillOwned;
                        recorded=owners.TryGetValue(turn,out stillOwned) && ReferenceEquals(stillOwned,owner)
                            && prices.Record(owner,scamId,detection.Sequence,amount);
                    }
                    if(recorded)
                    {
                        var plugin=Plugin.Current;
                        plugin.AcceptedPrices++;plugin.LastAmount=amount;
                        plugin.LastStatus=(scamId=="gift-card"?"Gift-Card-Preis":"Kreditkarten-Preis")+" bestätigt; Auszahlung wartet auf erfolgreiche native Prüfung.";
                    }
                }
            }
        }
        private static bool ExchangePrice(ConversationScamTurn exchange,out int amount)
        {
            int player,caller;amount=0;
            bool hasPlayer=SpokenPrice.TryExtract(exchange.PlayerDialogue,out player);
            bool hasCaller=SpokenPrice.TryExtract(exchange.CallerDialogue,out caller);
            if(hasPlayer && hasCaller && player!=caller) return false;
            if(hasPlayer) {amount=player;return true;}
            if(hasCaller) {amount=caller;return true;}
            return false;
        }
        private static bool QuotePrice(string evidence,bool hasContext,int contextAmount,out int amount)
        {
            if(SpokenPrice.TryExtract(evidence,out amount)) return true;
            amount=0;int quoted;
            if(hasContext && SpokenPrice.TryExtract("Preis: "+evidence+" Euro",out quoted) && quoted==contextAmount)
            {amount=quoted;return true;}
            return false;
        }
        private static JObject FirstAcceptedEvidence(JArray achievements,ConversationScamDetection detection,ConversationScamTurn exchange)
        {
            // Native Parse accepts the first valid occurrence and rejects later duplicate keys.
            // Match its schema, amount and BOTH native quote checks, then consume exactly one token.
            // A rejected/hallucinated duplicate cannot replace an ambiguous first accepted quote.
            foreach(var token in achievements)
            {
                var evidence=token as JObject;
                if(evidence==null || evidence.Count!=5
                    || evidence["key"]==null || evidence["key"].Type!=JTokenType.String
                    || evidence["sequence"]==null || evidence["sequence"].Type!=JTokenType.Integer
                    || evidence["amount"]==null || evidence["amount"].Type!=JTokenType.Integer
                    || evidence["player_evidence"]==null || evidence["player_evidence"].Type!=JTokenType.String
                    || evidence["caller_evidence"]==null || evidence["caller_evidence"].Type!=JTokenType.String) continue;
                if((string)evidence["key"]!=detection.Key || (long)evidence["sequence"]!=detection.Sequence
                    || (long)evidence["amount"]!=detection.Amount) continue;
                if(!(bool)containsQuote.Invoke(null,new object[]{exchange.PlayerDialogue,(string)evidence["player_evidence"]})
                    || !(bool)containsQuote.Invoke(null,new object[]{exchange.CallerDialogue,(string)evidence["caller_evidence"]})) continue;
                return evidence;
            }
            return null;
        }
        private static IEnumerable<CodeInstruction> RewardTranspiler(IEnumerable<CodeInstruction> instructions)
        {
            var reward=AccessTools.Field(typeof(ConversationScamDefinition),"reward");
            var resolve=AccessTools.Method(typeof(PayoutHooks),"ResolveReward");
            var output=new List<CodeInstruction>();int replacements=0;
            foreach(var instruction in instructions)
            {
                if(instruction.opcode==OpCodes.Ldfld && Equals(instruction.operand,reward))
                {
                    var session=new CodeInstruction(OpCodes.Ldarg_0);
                    session.labels.AddRange(instruction.labels);session.blocks.AddRange(instruction.blocks);
                    output.Add(session);output.Add(new CodeInstruction(OpCodes.Call,resolve));replacements++;
                }
                else output.Add(instruction);
            }
            if(replacements!=2) throw new InvalidOperationException("SubmitField no longer has exactly two native reward reads; no payout patch applied.");
            return output;
        }
        internal static int ResolveReward(ConversationScamDefinition definition,ConversationScamSession session)
        {
            int original=definition.reward;
            if(!Supported(definition.id)) return original;
            var progress=progressField.GetValue(session) as IDictionary;
            if(progress!=null && progress.Contains(definition.id))
            {
                // SubmitField writes MoneyEarned before Changed, then reads reward again for
                // its result. A newer offer, disable or Clear in Changed cannot split the award.
                int written=(int)moneyEarnedField.GetValue(progress[definition.id]);
                if(written>0) return written;
            }
            if(!Enabled) return original;
            int amount;
            if(!prices.TryGet(session,definition.id,out amount)) return original;
            if(progress==null || !progress.Contains(definition.id)) return original;
            long total=RequestedReward(definition,amount);
            // SubmitField already reached its native success branch. Optional/evaluator dialogue
            // milestones cannot discard an explicit offer after the real card/code was verified.
            // The game's total is Enumerable.Sum<int>. Reserve the maximum of each OTHER native
            // scam, so a later legitimate reward cannot overflow after the requested amount.
            long reserved=0;
            var catalog=catalogField.GetValue(session) as IReadOnlyList<ConversationScamDefinition>;
            if(catalog!=null) foreach(var scam in catalog)
            {
                if(scam==null || scam.id==definition.id) continue;
                long maximum=Math.Max(0,scam.reward);
                if(scam.rewardTiers!=null) foreach(int tier in scam.rewardTiers) maximum=Math.Max(maximum,tier);
                int otherPrice;
                if(prices.TryGet(session,scam.id,out otherPrice)) maximum=Math.Max(maximum,RequestedReward(scam,otherPrice));
                if(progress.Contains(scam.id)) maximum=Math.Max(maximum,(int)moneyEarnedField.GetValue(progress[scam.id]));
                reserved+=maximum;
                if(reserved>2147483647L) break;
            }
            if(total<=0 || total+reserved>2147483647L)
            {
                Plugin.Current.LastStatus="Wunschbetrag überschreitet die sichere native Gesamtsumme; Originalbelohnung bleibt erhalten.";
                return original;
            }
            return (int)total;
        }
        internal static long RequestedReward(ConversationScamDefinition definition,int amount)
        {
            return definition.id=="credit-card"?(long)definition.reward+amount:amount;
        }
        private static void SubmissionPostfix(ConversationScamSession __instance,string appId,ScamFieldVerificationResult __result)
        {
            if(!Enabled || !Supported(appId) || __result.PayoutAwarded<=0) return;
            var catalog=catalogField.GetValue(__instance) as IReadOnlyList<ConversationScamDefinition>;
            ConversationScamDefinition definition=null;
            if(catalog!=null) foreach(var item in catalog)
                if(item!=null && item.id==appId) {definition=item;break;}
            if(definition==null) return;
            long paidRequested;
            if(appId=="credit-card")
            {
                // MoneyEarned/result already froze the award. A Changed subscriber may have
                // replaced the pending quote; report the paid amount rather than that new offer.
                paidRequested=(long)__result.PayoutAwarded-definition.reward;
                if(paidRequested<=0) return;
            }
            else
            {
                int amount;
                if(!prices.TryGet(__instance,appId,out amount) || __result.PayoutAwarded!=RequestedReward(definition,amount)) return;
                paidRequested=amount;
            }
            Plugin.Current.RequestedPayouts++;
            Plugin.Current.LastStatus=appId=="gift-card"
                ?"Native Gift-Code-Prüfung erfolgreich: "+__result.PayoutAwarded+" Spielgeld ausgezahlt."
                :"Native Kartenprüfung erfolgreich: "+__result.PayoutAwarded+" Spielgeld ausgezahlt ("+definition.reward+" Originalbelohnung + "+paidRequested+" Wunschbetrag).";
        }
        private static void DisposePrefix(ConversationScamSession __instance)
        {
            lock(sync)
            {
                prices.Remove(__instance);
                prompts.Remove(__instance);
                var remove=new List<ConversationScamTurn>();
                foreach(var pair in owners) if(ReferenceEquals(pair.Value,__instance)) remove.Add(pair.Key);
                foreach(var turn in remove) owners.Remove(turn);
            }
        }
        internal static void Clear() { lock(sync){owners.Clear();prompts.Clear();prices.Clear();} }
    }
}
