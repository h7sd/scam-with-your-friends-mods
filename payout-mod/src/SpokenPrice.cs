using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;

namespace ScamWYF.RequestedPayout
{
    internal static class SpokenPrice
    {
        private static readonly Regex Context = new Regex(@"(?i)(?:€|\$|\b(?:euros?|dollars?|eur|usd|preis|price|kostet|kosten|costs?|pay|zahlen|zahle|zahlst|bezahle|bezahlen)\b)");
        private static readonly Regex Credential = new Regex(@"(?i)\b(?:karten(?:nummer|code)|kreditkarten(?:nummer|code)|geschenkkartencode|gutscheincode|card\s*(?:number|code)|gift\s*(?:card\s*)?code|pin|cvv|cvc|iban|konto(?:nummer)?|account\s*number|phone\s*number|telefonnummer|expiry|expiration|ablaufdatum|g[uü]ltigkeit)\b");
        private static readonly Regex PriceQuestion = new Regex(@"(?i)(?:\b(?:preis|price)\b|(?:was|wie\s*viel|wieviel)\b.{0,35}\b(?:kostet|kosten|zahlen|bezahlen)\b|\bhow\s*much\b.{0,35}\b(?:cost|pay|service|euros?|dollars?)\b)");
        private static readonly Regex Unsafe = new Regex(@"(?i)(?:\b(?:minus|negative|negativ|cent|cents|komma|point|decimal|pence)\b|(?:^|\s|€|\$)[-−]\s*[€$]?\s*\d|\d(?:[\s-]?\d){11,18}|\b\d[\d.,]*\s*[km]\b)");
        private static readonly Regex Tokens = new Regex(@"[a-z]+|\d[\d.,]*");
        private static readonly HashSet<string> EnglishNumbers = new HashSet<string>(new[] {
            "zero","one","two","three","four","five","six","seven","eight","nine","ten","eleven","twelve","thirteen",
            "fourteen","fifteen","sixteen","seventeen","eighteen","nineteen","twenty","thirty","forty","fifty","sixty","seventy","eighty","ninety"
        });
        private static readonly Dictionary<string, int> Small = new Dictionary<string, int> {
            {"null",0},{"zero",0},{"ein",1},{"eins",1},{"eine",1},{"einen",1},{"one",1},
            {"zwei",2},{"two",2},{"drei",3},{"three",3},{"vier",4},{"four",4},{"funf",5},{"fuenf",5},{"five",5},
            {"sechs",6},{"six",6},{"sieben",7},{"seven",7},{"acht",8},{"eight",8},{"neun",9},{"nine",9},
            {"zehn",10},{"ten",10},{"elf",11},{"eleven",11},{"zwolf",12},{"zwoelf",12},{"twelve",12},
            {"dreizehn",13},{"thirteen",13},{"vierzehn",14},{"fourteen",14},{"funfzehn",15},{"fuenfzehn",15},{"fifteen",15},
            {"sechzehn",16},{"sixteen",16},{"siebzehn",17},{"seventeen",17},{"achtzehn",18},{"eighteen",18},{"neunzehn",19},{"nineteen",19},
            {"zwanzig",20},{"twenty",20},{"dreissig",30},{"thirty",30},{"vierzig",40},{"forty",40},{"funfzig",50},{"fuenfzig",50},{"fifty",50},
            {"sechzig",60},{"sixty",60},{"siebzig",70},{"seventy",70},{"achtzig",80},{"eighty",80},{"neunzig",90},{"ninety",90}
        };

        internal static bool TryExtract(string evidence, out int amount)
        {
            amount = 0;
            if (string.IsNullOrWhiteSpace(evidence) || evidence.Length > 10000 || !Context.IsMatch(evidence) || Unsafe.IsMatch(evidence)) return false;
            string text = evidence.ToLowerInvariant().Replace('ä','a').Replace('ö','o').Replace('ü','u').Replace("ß","ss");
            text = Regex.Replace(text, @"\b[1-9]\d{0,2}(?:[ \u00a0]\d{3})+\b", match => match.Value.Replace(" ","").Replace("\u00a0",""));
            var matches = Tokens.Matches(text);
            var amounts = new HashSet<long>();
            for (int i = 0; i < matches.Count; i++)
            {
                string token = matches[i].Value;
                if (char.IsDigit(token[0]))
                {
                    token = token.TrimEnd('.',',');
                    if(token.IndexOf('.')>=0 && token.IndexOf(',')>=0) return false;
                    long value;
                    // Only plain integers or unambiguous groups of three digits. Decimal fractions
                    // are not rounded and cannot accidentally become a much larger game payout.
                    if (!Regex.IsMatch(token, @"^(?:\d+|[1-9]\d{0,2}(?:[.,]\d{3})+)$")
                        || !long.TryParse(token.Replace(".","").Replace(",",""), out value)) return false;
                    amounts.Add(value);
                    continue;
                }
                if (!IsNumberWord(token)) continue;
                var words = new List<string>();
                while (i < matches.Count && (IsNumberWord(matches[i].Value) || (words.Count > 0 && (matches[i].Value == "and" || matches[i].Value == "und"))))
                {
                    words.Add(matches[i].Value); i++;
                }
                int after = i; i--;
                // German indefinite articles and English "one service" are not price amounts.
                if (words.Count == 1 && (token == "ein" || token == "eine" || token == "einen" || token == "one")
                    && (after >= matches.Count || !IsCurrency(matches[after].Value))) continue;
                long number;
                if (!TryWords(words, out number)) return false;
                amounts.Add(number);
            }
            if (amounts.Count != 1) return false;
            foreach (long value in amounts)
            {
                if (value <= 0 || value > 2147483647L) return false;
                amount = (int)value;
            }
            return amount > 0;
        }

        private static bool IsCurrency(string value) { return value == "euro" || value == "euros" || value == "dollar" || value == "dollars" || value == "eur" || value == "usd"; }
        internal static bool HasPriceExpression(string evidence)
        {
            if(string.IsNullOrEmpty(evidence) || !Context.IsMatch(evidence)) return false;
            if(Regex.IsMatch(evidence,@"\d")) return true;
            string normalized=evidence.ToLowerInvariant().Replace('ä','a').Replace('ö','o').Replace('ü','u').Replace("ß","ss");
            foreach(Match match in Tokens.Matches(normalized)) if(IsNumberWord(match.Value)) return true;
            return false;
        }
        internal static bool TryExtractOffer(string dialogue,out int amount)
        {
            amount=0;
            if(string.IsNullOrWhiteSpace(dialogue)) return false;
            if(Regex.IsMatch(dialogue,@"(?i)(?:^\s*(?:was|wie\s*viel|wieviel|how\s*much|what)\b.{0,45}\b(?:kostet|kosten|costs?|price|preis)\b|\b(?:soll(?:te)?\s+ich|should\s+i|meinst\s+du|do\s+you\s+think)\b|^\s*(?:kostet|costs?)\s+(?:das|es|it|that)\b)")) return false;
            if(Regex.IsMatch(dialogue,@"(?i)(?:\b(?:kostet|kosten|costs?)\s+(?:nicht|not|kein(?:e|en)?)\b|\b(?:preis|price)\s+(?:(?:ist|betr[aä]gt|is)\s+)?(?:nicht|not|kein(?:e|en)?)\b|\b(?:nicht|not)\s+\d|\b(?:vielleicht|maybe|perhaps|hypothetisch|hypothetical)\b|\b(?:wenn|falls|if|w[uü]rde|could|would)\b.{0,100}\b(?:kostet|kosten|costs?)\b)")) return false;
            // A request for card details may follow a real service price. Currency alone must
            // not make a card/PIN/code value into a price, and multiple amounts stay ambiguous.
            if(Credential.IsMatch(dialogue)
                && !Regex.IsMatch(dialogue,@"(?i)\b(?:preis|price|kostet|kosten|costs?)\b")) return false;
            return TryExtract(dialogue,out amount);
        }
        internal static bool TryExtractStandalone(string dialogue,out int amount)
        {
            amount=0;
            if(string.IsNullOrWhiteSpace(dialogue) || dialogue.Length>200 || Credential.IsMatch(dialogue)) return false;
            string normalized=dialogue.ToLowerInvariant().Replace('ä','a').Replace('ö','o').Replace('ü','u').Replace("ß","ss");
            if(!Regex.IsMatch(normalized,@"^[a-z0-9.,\s]+[.!?]?$")) return false;
            var matches=Tokens.Matches(normalized);
            if(matches.Count==0) return false;
            foreach(Match match in matches)
                if(!char.IsDigit(match.Value[0]) && !IsNumberWord(match.Value)
                    && match.Value!="and" && match.Value!="und") return false;
            return TryExtract("Preis: "+dialogue+" Euro",out amount);
        }
        internal static bool IsPriceQuestion(string dialogue)
        {
            return !string.IsNullOrWhiteSpace(dialogue) && dialogue.Length<=5000
                && !Credential.IsMatch(dialogue) && PriceQuestion.IsMatch(dialogue)
                && (dialogue.IndexOf('?')>=0 || Regex.IsMatch(dialogue,@"(?i)\b(?:was|wie|wieviel|what|how|welcher|welchen|nenn|nenne|tell)\b"));
        }
        internal static int Scope(string dialogue)
        {
            if(string.IsNullOrEmpty(dialogue)) return 0;
            int scope=0;
            if(Regex.IsMatch(dialogue,@"(?i)\b(?:credit[\s-]?cards?|kreditkart(?:e|en)|bankkart(?:e|en))\b")) scope|=1;
            if(Regex.IsMatch(dialogue,@"(?i)\b(?:gift[\s-]?cards?|geschenkkart(?:e|en)|gutschein(?:e|en)?)\b")) scope|=2;
            return scope;
        }
        internal static bool HasNumberExpression(string dialogue)
        {
            if(string.IsNullOrWhiteSpace(dialogue)) return false;
            string normalized=dialogue.ToLowerInvariant().Replace('ä','a').Replace('ö','o').Replace('ü','u').Replace("ß","ss");
            var matches=Tokens.Matches(normalized);
            for(int i=0;i<matches.Count;i++)
            {
                string value=matches[i].Value;
                if((value=="ein" || value=="eine" || value=="einen" || value=="one")
                    && (i+1>=matches.Count || !IsCurrency(matches[i+1].Value))) continue;
                if(char.IsDigit(value[0]) || IsNumberWord(value)) return true;
            }
            return false;
        }
        private static bool IsNumberWord(string value)
        {
            long parsed;
            return Small.ContainsKey(value) || value == "hundred" || value == "thousand" || value == "million" || value == "millions" || value == "billion" || value == "billions" || TryGerman(value, out parsed);
        }

        private static bool TryWords(List<string> words, out long result)
        {
            result = 0;
            bool english = false;
            foreach (string word in words) if (word == "hundred" || word == "thousand" || word == "billion" || word == "and") english = true;
            if (!english)
            {
                string joined = "";
                foreach (string word in words) joined += word;
                if (TryGerman(joined, out result)) return true;
            }
            long current = 0;
            long previousScale = 2147483648L;
            int previousSmall = 0;
            bool lastWasSmall = false;
            foreach (string word in words)
            {
                if (word == "and") { if(lastWasSmall) return false; continue; }
                int small;
                if (Small.TryGetValue(word, out small))
                {
                    if(!EnglishNumbers.Contains(word)) return false;
                    if(lastWasSmall && !(previousSmall>=20 && previousSmall<=90 && previousSmall%10==0 && small>0 && small<10)) return false;
                    current += small;previousSmall=small;lastWasSmall=true;
                }
                else if (word == "hundred") { current = Math.Max(1, current) * 100;lastWasSmall=false; }
                else if (word == "thousand" || word == "million" || word == "millions" || word == "billion" || word == "billions")
                {
                    long scale = word == "thousand" ? 1000 : word.StartsWith("million") ? 1000000 : 1000000000;
                    if(scale>=previousScale) return false;
                    previousScale=scale;
                    result += Math.Max(1, current) * scale; current = 0;lastWasSmall=false;
                }
                else return false;
                if (current > 2147483647L || result > 2147483647L) return false;
            }
            result += current;
            return result >= 0 && result <= 2147483647L;
        }

        private static bool TryGerman(string word, out long result)
        {
            result = 0;
            if (word.Length == 0) return true;
            int small;
            if (Small.TryGetValue(word, out small)) { result = small; return true; }
            foreach (string scaleWord in new[] { "milliarden", "milliarde", "millionen", "million", "tausend", "hundert" })
            {
                int position = word.IndexOf(scaleWord, StringComparison.Ordinal);
                if (position < 0) continue;
                if(position==0 && scaleWord.StartsWith("million") && word.Length>scaleWord.Length) return false;
                if(position==0 && scaleWord.StartsWith("milliard") && word.Length>scaleWord.Length) return false;
                long left, right;
                if (!TryGerman(word.Substring(0,position), out left) || !TryGerman(word.Substring(position+scaleWord.Length), out right)) return false;
                long scale = scaleWord.StartsWith("milliard") ? 1000000000 : scaleWord.StartsWith("million") ? 1000000 : scaleWord == "tausend" ? 1000 : 100;
                result = Math.Max(1,left) * scale + right;
                return result <= 2147483647L;
            }
            int conjunction = word.IndexOf("und", StringComparison.Ordinal);
            if (conjunction > 0)
            {
                long units, tens;
                if (TryGerman(word.Substring(0,conjunction), out units) && TryGerman(word.Substring(conjunction+3), out tens)
                    && units > 0 && units < 10 && tens >= 20 && tens <= 90 && tens % 10 == 0)
                { result = units + tens; return true; }
            }
            return false;
        }
    }
}
