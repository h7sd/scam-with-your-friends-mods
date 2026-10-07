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
        private static readonly object sync = new object();
        private static FieldInfo progressField, catalogField, completedField, moneyEarnedField;
        private static MethodInfo containsQuote;
        private static readonly MethodInfo memberwiseClone=AccessTools.Method(typeof(object),"MemberwiseClone");

        internal static void Install(Plugin plugin)
        {
            progressField = AccessTools.Field(typeof(ConversationScamSession),"progress");
            catalogField = AccessTools.Field(typeof(ConversationScamSession),"catalog");
            if (progressField == null || catalogField == null) throw new InvalidOperationException("Native conversation scam state changed.");
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
            Patch(plugin,typeof(ConversationScamAiDetector),"Parse",new[]{typeof(string),typeof(IReadOnlyList<ConversationScamCandidate>),typeof(ConversationScamTurn),typeof(Action<string>)},null,"DetectionPostfix",null);
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
            if (!Enabled || turn==null || turn.Hidden || turn.Fallback) return;
            lock(sync)
            {
                owners[turn]=__instance;
                if(turn.RecoveredExchanges!=null) foreach(var recovery in turn.RecoveredExchanges)
                    if(recovery!=null && recovery.Turn!=null) owners[recovery.Turn]=__instance;
            }
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
                    int offered,accepted;
                    bool playerHas=SpokenPrice.TryExtract((string)evidence["player_evidence"],out offered);
                    bool callerHas=SpokenPrice.TryExtract((string)evidence["caller_evidence"],out accepted);
                    if(!playerHas && SpokenPrice.HasPriceExpression((string)evidence["player_evidence"])) continue;
                    if(!callerHas && SpokenPrice.HasPriceExpression((string)evidence["caller_evidence"])) continue;
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
            if(!Enabled || !Supported(definition.id)) return original;
            int amount;
            if(!prices.TryGet(session,definition.id,out amount)) return original;
            var progress=progressField.GetValue(session) as IDictionary;
            if(progress==null || !progress.Contains(definition.id)) return original;
            var completed=completedField.GetValue(progress[definition.id]) as HashSet<string>;
            if(completed==null) return original;
            if(definition.id=="credit-card" && (!completed.Contains("price") || !completed.Contains("service"))) return original;
            if(definition.id=="gift-card" && (!completed.Contains("requested-price") || !completed.Contains("pitch") || !completed.Contains("solution"))) return original;
            // The game's total is Enumerable.Sum<int>. Reserve the maximum of each OTHER native
            // scam, so a later legitimate reward cannot overflow after the requested amount.
            long reserved=0;
            var catalog=catalogField.GetValue(session) as IReadOnlyList<ConversationScamDefinition>;
            if(catalog!=null) foreach(var scam in catalog)
            {
                if(scam==null || scam.id==definition.id) continue;
                int maximum=Math.Max(0,scam.reward);
                if(scam.rewardTiers!=null) foreach(int tier in scam.rewardTiers) maximum=Math.Max(maximum,tier);
                int otherPrice;
                if(prices.TryGet(session,scam.id,out otherPrice)) maximum=Math.Max(maximum,otherPrice);
                if(progress.Contains(scam.id)) maximum=Math.Max(maximum,(int)moneyEarnedField.GetValue(progress[scam.id]));
                reserved+=maximum;
            }
            if((long)amount+reserved>2147483647L)
            {
                Plugin.Current.LastStatus="Wunschbetrag überschreitet die sichere native Gesamtsumme; Originalbelohnung bleibt erhalten.";
                return original;
            }
            return amount;
        }
        private static void SubmissionPostfix(ConversationScamSession __instance,string appId,ScamFieldVerificationResult __result)
        {
            if(!Enabled || !Supported(appId) || __result.PayoutAwarded<=0) return;
            int amount;
            if(prices.TryGet(__instance,appId,out amount) && __result.PayoutAwarded==amount)
            {
                Plugin.Current.RequestedPayouts++;
                Plugin.Current.LastStatus=(appId=="gift-card"?"Native Gift-Code-Prüfung":"Native Kartenprüfung")+" erfolgreich: "+amount+" Spielgeld ausgezahlt.";
            }
        }
        private static void DisposePrefix(ConversationScamSession __instance)
        {
            lock(sync)
            {
                prices.Remove(__instance);
                var remove=new List<ConversationScamTurn>();
                foreach(var pair in owners) if(ReferenceEquals(pair.Value,__instance)) remove.Add(pair.Key);
                foreach(var turn in remove) owners.Remove(turn);
            }
        }
        internal static void Clear() { lock(sync){owners.Clear();prices.Clear();} }
    }
}
