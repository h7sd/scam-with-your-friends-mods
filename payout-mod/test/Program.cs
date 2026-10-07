using System;
using System.Collections.Generic;
using System.Reflection;
using ScamWYF.RequestedPayout;

internal static class Program
{
    private static int passed, failed;

    private static void Main()
    {
        var accepted = new Dictionary<string, int>
        {
            { "Ja, ich akzeptiere den Preis von 5000 Euro.", 5000 },
            { "Ich bezahle 5.000 Euro.", 5000 },
            { "Ich zahle fünftausend Euro.", 5000 },
            { "Ja, FÜNFTAUSEND EURO passt.", 5000 },
            { "Fünf tausend Euro sind in Ordnung.", 5000 },
            { "Der vereinbarte Preis beträgt 5000.", 5000 },
            { "€5000", 5000 },
            { "5000 €", 5000 },
            { "5000 EUR", 5000 },
            { "Der Preis ist 5 000 Euro.", 5000 },
            { "Der Preis ist 5\u00a0000 Euro.", 5000 },
            { "5.000.000 Euro", 5000000 },
            { "I agree to pay 5,000 dollars.", 5000 },
            { "The price is five thousand dollars.", 5000 },
            { "I agree to pay 5000 euros.", 5000 },
            { "The agreed price is 5000.", 5000 },
            { "ein Euro", 1 },
            { "eintausend Euro", 1000 },
            { "fünfhundert Euro", 500 },
            { "fünfundzwanzig Euro", 25 },
            { "fünfzig Euro", 50 },
            { "twenty five euros", 25 },
            { "zweitausendfünfhundert Euro", 2500 },
            { "fünftausend Euro, also 5000 Euro", 5000 },
        };
        foreach (var sample in accepted) Parse(sample.Key, true, sample.Value);

        var rejected = new[]
        {
            null, "", "   ", "Ja, ich bin einverstanden.", "5000", "Meine Kartennummer ist 5000.",
            "Meine Kreditkartennummer ist 4111111111111111.",
            "Meine Kreditkartennummer ist 4111 1111 1111 1111.",
            "Meine Kreditkartennummer ist 1234567890.",
            "Der Preis ist 4111111111111111 Euro.",
            "Der Preis ist 5.000,50 Euro.", "5000,50 Euro", "5000.50 dollars", "1,50 Euro",
            "1.000,000 Euro", "1,000.000 dollars", "5k Euro", "10k dollars",
            "five six euros", "fünf sechs Euro", "five thousand and six thousand dollars",
            "fünftausend Euro und fünfzig Cent", "fünf Euro fünfzig", "50 Cent", "0 Euro", "null Euro",
            "-5000 Euro", "−5000 Euro", "-€5000", "minus fünftausend Euro", "5000 Euro oder 6000 Euro", "2147483648 Euro",
            "2.147.483.648 Euro", "Der Preis beträgt 99999999999999999999999999 Euro.",
            "Meine Telefonnummer lautet 030 1234567.", "Die PIN ist 1234.", "Euro", "5000 Prozent",
        };
        foreach (var sample in rejected) Parse(sample, false, 0);

        TestBook();
        TestScamPartition();
        Console.WriteLine("Payout source tests: " + passed + " passed, " + failed + " failed");
        Environment.ExitCode = failed == 0 ? 0 : 1;
    }

    private static void Parse(string evidence, bool expected, int expectedAmount)
    {
        int amount;
        bool actual = SpokenPrice.TryExtract(evidence, out amount);
        Check(actual == expected && (!expected || amount == expectedAmount),
            "parse " + (evidence ?? "<null>") + ": expected " + (expected ? expectedAmount.ToString() : "rejected")
                + ", got " + (actual ? amount.ToString() : "rejected"));
    }

    private static void TestBook()
    {
        // Use the production class, supporting either an instance or static API.
        Type bookType = typeof(PriceBook);
        MethodInfo record = Method(bookType, "Record"), lookup = Method(bookType, "TryGet"),
            remove = Method(bookType, "Remove"), clear = Method(bookType, "Clear");
        object book = record.IsStatic ? null : Activator.CreateInstance(bookType, true);
        var callA = new EqualSession();
        var callB = new EqualSession();
        clear.Invoke(book, new object[0]);
        Check(!TryGet(lookup, book, callA, 0), "new call has no negotiated price");
        record.Invoke(book, new object[] { callA, 1, 5000 });
        Check(TryGet(lookup, book, callA, 5000), "call A stores 5000");
        Check(!TryGet(lookup, book, callB, 0), "reference-distinct call B does not inherit call A despite Equals");
        record.Invoke(book, new object[] { callB, 1, 7500 });
        Check(TryGet(lookup, book, callA, 5000) && TryGet(lookup, book, callB, 7500), "simultaneous calls preserve separate prices");
        record.Invoke(book, new object[] { callA, 1, 9000 });
        Check(TryGet(lookup, book, callA, 5000), "duplicate sequence is idempotent");
        record.Invoke(book, new object[] { callA, 0, 1000 });
        Check(TryGet(lookup, book, callA, 5000), "stale observation cannot overwrite current price");
        record.Invoke(book, new object[] { callA, 2, 6000 });
        Check(TryGet(lookup, book, callA, 5000), "first accepted price stays fixed even after newer observation");
        record.Invoke(book, new object[] { callA, 3, 0 });
        Check(TryGet(lookup, book, callA, 5000), "zero price does not overwrite accepted amount");
        record.Invoke(book, new object[] { callA, 4, -1000 });
        Check(TryGet(lookup, book, callA, 5000), "negative price does not overwrite accepted amount");
        remove.Invoke(book, new object[] { callA });
        Check(!TryGet(lookup, book, callA, 0), "aborted/disposed call removes price");
        Check(TryGet(lookup, book, callB, 7500), "removing one call preserves the other");
        clear.Invoke(book, new object[0]);
        Check(!TryGet(lookup, book, callA, 0) && !TryGet(lookup, book, callB, 0), "shutdown clears all prices");
    }

    private static MethodInfo Method(Type type, string name)
    {
        Type[] legacySignature = name == "Record" ? new[] { typeof(object), typeof(int), typeof(int) }
            : name == "TryGet" ? new[] { typeof(object), typeof(int).MakeByRefType() }
            : name == "Remove" ? new[] { typeof(object) } : Type.EmptyTypes;
        return type.GetMethod(name, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.Instance,
            null, legacySignature, null)
            ?? throw new MissingMethodException(type.FullName, name);
    }

    private static void TestScamPartition()
    {
        Type type = typeof(PriceBook);
        BindingFlags flags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance;
        MethodInfo record = type.GetMethod("Record", flags, null,
            new[] { typeof(object), typeof(string), typeof(int), typeof(int) }, null);
        MethodInfo lookup = type.GetMethod("TryGet", flags, null,
            new[] { typeof(object), typeof(string), typeof(int).MakeByRefType() }, null);
        Check(record != null && lookup != null, "price book exposes separate per-scam quote API");
        if (record == null || lookup == null) return;
        object book = Activator.CreateInstance(type, true);
        var session = new EqualSession();
        var otherSession = new EqualSession();
        record.Invoke(book, new object[] { session, "credit-card", 1, 5000 });
        record.Invoke(book, new object[] { session, "gift-card", 2, 7500 });
        Check(ScamGet(lookup, book, session, "credit-card", 5000), "same call credit-card price is 5000");
        Check(ScamGet(lookup, book, session, "gift-card", 7500), "same call gift-card price is 7500");
        Check(!ScamGet(lookup, book, otherSession, "gift-card", 0), "other call cannot read gift-card price");
        record.Invoke(book, new object[] { session, "gift-card", 3, 9000 });
        record.Invoke(book, new object[] { session, "credit-card", 4, 2000 });
        Check(ScamGet(lookup, book, session, "gift-card", 7500)
            && ScamGet(lookup, book, session, "credit-card", 5000), "first accepted price stays fixed independently for each scam");
        Method(type, "Remove").Invoke(book, new object[] { session });
        Check(!ScamGet(lookup, book, session, "gift-card", 0)
            && !ScamGet(lookup, book, session, "credit-card", 0), "call disposal removes both scam quotes");
    }

    private static bool ScamGet(MethodInfo lookup, object book, object session, string scam, int expected)
    {
        object[] args = new object[] { session, scam, 0 };
        bool found = (bool)lookup.Invoke(book, args);
        return expected == 0 ? found : found && (int)args[2] == expected;
    }

    private static bool TryGet(MethodInfo lookup, object book, object session, int expected)
    {
        var args = new object[] { session, 0 };
        bool found = (bool)lookup.Invoke(book, args);
        return expected == 0 ? found : found && (int)args[1] == expected;
    }

    private static void Check(bool condition, string message)
    {
        if (condition) { passed++; return; }
        failed++;
        Console.WriteLine("FAIL: " + message);
    }

    private sealed class EqualSession
    {
        public override bool Equals(object other) { return other is EqualSession; }
        public override int GetHashCode() { return 0; }
    }
}
