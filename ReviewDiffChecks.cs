namespace Corrector;

internal static class ReviewDiffChecks
{
    internal static void Run(Action<string, bool> check)
    {
        void Expect(string name, string original, string corrected, string removed, string added)
        {
            var (before, after) = ReviewDiff.Compare(original, corrected);
            check(name, Join(before) == original && Join(after) == corrected &&
                Join(before.Where(s => s.Changed)) == removed && Join(after.Where(s => s.Changed)) == added &&
                Join(before.Where(s => !s.Changed)) == Join(after.Where(s => !s.Changed)) &&
                Alternates(before) && Alternates(after));
        }

        Expect("review unchanged", "Тот же текст", "Тот же текст", "", "");
        Expect("review empty", "", "", "", "");
        Expect("review whole insertion", "", "Текст", "", "Текст");
        Expect("review whole deletion", "Текст", "", "Текст", "");
        Expect("review punctuation only", "Привет мир", "Привет, мир!", "", ",!");
        Expect("review lexical edits keep context", "Он прешёл и сел за стл.", "Он пришёл и сел за стол.", "прешёлстл", "пришёлстол");
        Expect("review repeats keep aligned", "да, да и да", "да и да", ", да", "");
        Expect("review line endings preserved", "раз\r\nдва", "раз\nдва", "\r\n", "\n");
        Expect("review form feed apart from horizontal spaces", "а \f б", "а \u2028 б", "\f", "\u2028");
        Expect("review tab and space", "в\tдоме", "в  доме", "\t", "  ");
        Expect("review keeps entire combining sequence", "е\u0301шь", "ешь", "е\u0301шь", "ешь");
        Expect("review joined emoji", "вот 👨‍👩‍👧‍👦 тут 👍🏽", "вот 👩‍👧 тут 👍🏻", "👨‍👩‍👧‍👦👍🏽", "👩‍👧👍🏻");

        string context = string.Concat(Enumerable.Repeat("много слов ", 1_200));
        Expect("review distant edits in a draft", "начло " + context + "кнец", "начало " + context + "конец",
            "начлокнец", "началоконец");

        string large = string.Concat(Enumerable.Repeat("строка\r\n", 40_000));
        Expect("review huge common context", large + "ошпка " + large, large + "ошибка " + large, "ошпка", "ошибка");
        string rewritten = string.Concat(Enumerable.Repeat("новая\n", 45_000));
        var (left, right) = ReviewDiff.Compare(large + "хвост", rewritten + "хвост");
        check("review bounded runs on huge rewrite", Join(left) == large + "хвост" && Join(right) == rewritten + "хвост" &&
            left.Count == 2 && right.Count == 2 && left[^1] == new ReviewSpan("хвост", false) && left[^1] == right[^1]);
    }

    static string Join(IEnumerable<ReviewSpan> spans) => string.Concat(spans.Select(s => s.Text));
    static bool Alternates(IReadOnlyList<ReviewSpan> spans) =>
        spans.All(s => s.Text.Length > 0) && !spans.Zip(spans.Skip(1)).Any(pair => pair.First.Changed == pair.Second.Changed);
}
